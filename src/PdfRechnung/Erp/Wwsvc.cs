using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfRechnung.Erp;

// Übernommen aus ERP-Austria-MCP-Server (Erp/WwsServiceClient.vb, Erp/ErpServicePassManager.vb) und auf
// die Belegpost-Rückmeldung beschränkt: nur BELEG.GET und BELEG.PUT. Belegpost verwendet eine eigene
// SecuredApp und einen eigenen Servicepass – nie den Pass eines anderen Dienstes.

/// <summary>Gespeicherter Servicepass (verschlüsselt in daten\erp\pass-&lt;binding&gt;.bin).</summary>
public sealed class ErpServicePass
{
    public string Binding { get; set; } = "";
    public string PassId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    /// <summary>Missing, Ready, PendingApproval, RegistrationOutcomeUnknown.</summary>
    public string State { get; set; } = "Missing";
    public int RequestId { get; set; }
    public DateTimeOffset? LastValidatedUtc { get; set; }
}

public sealed class ErpException : Exception
{
    public ErpException(string code, int? httpStatus = null, int? wwsStatus = null, int? apiCode = null)
        : base(Describe(code)) { Code = code; HttpStatus = httpStatus; WwsStatus = wwsStatus; ApiCode = apiCode; }

    public string Code { get; }
    public int? HttpStatus { get; }
    public int? WwsStatus { get; }
    public int? ApiCode { get; }

    /// <summary>Kann ein späterer Versuch helfen? (Netz, Zeitüberschreitung, Freigabe ausstehend)</summary>
    public bool Transient => Code is "Unavailable" or "ConnectionFailure" or "DnsFailure" or "RequestTimedOut" or "PendingApproval"
        or "TlsFailure" or "SessionLoginRequired" or "InvalidResponse" || HttpStatus is >= 500;

    public static string Describe(string code) => code switch
    {
        "Disabled" => "ERP-Anbindung ist ausgeschaltet.",
        "NotConfigured" => "ERP-Anbindung ist nicht vollständig eingerichtet.",
        "PendingApproval" => "Servicepass wartet auf Freigabe im ERP.",
        "RegistrationDisabled" => "Es ist noch kein Servicepass vorhanden und die Registrierung ist nicht erlaubt.",
        "RegistrationOutcomeUnknown" => "Registrierung des Servicepasses mit unklarem Ausgang – bitte in den Einstellungen bestätigen.",
        "InvalidStoredPass" => "Gespeicherter Servicepass ist ungültig.",
        "BindingMismatch" => "Gespeicherter Servicepass gehört zu einer anderen ERP-Einstellung.",
        "PassValidationRejected" => "ERP hat den Servicepass abgelehnt.",
        "SessionLoginRequired" => "Anmeldung des technischen ERP-Benutzers fehlgeschlagen.",
        "RequestSequenceExhausted" => "Anfragenummer erschöpft – Servicepass neu registrieren.",
        "RequestTimedOut" => "Zeitüberschreitung bei der ERP-Anfrage.",
        "DnsFailure" => "ERP-Server nicht gefunden (DNS).",
        "TlsFailure" => "HTTPS-Verbindung zum ERP fehlgeschlagen (Zertifikat prüfen).",
        "ConnectionFailure" => "ERP nicht erreichbar.",
        "Unavailable" => "ERP nicht erreichbar.",
        "ResponseTooLarge" => "Antwort des ERP zu groß.",
        "InvalidResponse" => "Unerwartete Antwort des ERP.",
        "QueryRejected" => "ERP hat die Anfrage abgelehnt.",
        "WriteRejected" => "ERP hat das Schreiben abgelehnt.",
        "DocumentNotFound" => "Beleg mit diesem Belegindex nicht im ERP gefunden.",
        _ => "ERP-Anfrage nicht abgeschlossen (" + code + ")."
    };
}

public sealed class WwsResponse
{
    public int Status { get; set; }
    public int ResultStatus { get; set; }
    public int? ErrorCode { get; set; }
    public JsonElement Body { get; set; }
    public string? SessionToken { get; set; }
    public bool Succeeded => Status == 200 && ResultStatus == 200;
}

/// <summary>Einstellungen für eine Verbindung (entschlüsselt, nur im Speicher).</summary>
public sealed record WwsSettings(string Endpoint, string Vendor, string Application, string SecureAppId, int Revision,
    string Username, string Password, bool AllowRegistration)
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    public static WwsSettings From(ErpOptions o, string? password) =>
        new(o.Endpoint.Trim(), o.Vendor.Trim(), o.Application.Trim(), o.SecureAppId.Trim(), o.Revision, o.Username.Trim(), password ?? "", o.AllowRegistration);

    public List<string> Validate()
    {
        var e = new List<string>();
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var u) || u.Scheme != "https" || u.UserInfo != "" || u.Query != "" || u.Fragment != "" || !Endpoint.EndsWith('/'))
            e.Add("ERP: HTTPS-Basisadresse mit abschließendem / und ohne Zugangsdaten angeben (z. B. https://erp.firma.at/WWSVC/).");
        if (!IsIdentifier(Vendor)) e.Add("ERP: Hersteller-Hash prüfen (1–128 Buchstaben/Ziffern, - oder _).");
        if (!IsIdentifier(Application)) e.Add("ERP: Anwendungs-Hash prüfen (1–128 Buchstaben/Ziffern, - oder _).");
        if (!(SecureAppId.Length is >= 1 and <= 25 && SecureAppId.All(char.IsAsciiDigit))) e.Add("ERP: SecuredApp-Zugriffs-ID prüfen (1–25 Ziffern).");
        if (Revision < 0) e.Add("ERP: Revision muss 0 oder größer sein.");
        if (Username.Length > 120 || Password.Length > 512 || Username.Any(char.IsControl) || Password.Any(char.IsControl) || (Username.Length == 0) != (Password.Length == 0))
            e.Add("ERP: technischen Benutzer samt Kennwort vollständig angeben (oder beides leer).");
        return e;
    }

    /// <summary>Bindung des Servicepasses an genau diese ERP-Einstellung.</summary>
    public string Binding() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        { "belegpost", new Uri(Endpoint).AbsoluteUri, Vendor, Application, SecureAppId, Username })));

    public static bool IsIdentifier(string? v) => v is { Length: >= 1 and <= 128 } && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>HTTP-Ebene von WWSVC (REGISTER, VALIDATE, CONNECT, EXECJSON).</summary>
public sealed class WwsServiceClient
{
    public static readonly string[] AllowedFunctions = { "BELEG.GET", "BELEG.PUT" };
    private const int MaxResponse = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly WwsSettings _o;
    private readonly Func<DateTimeOffset> _clock;

    public WwsServiceClient(HttpClient http, WwsSettings options, Func<DateTimeOffset>? clock = null)
    {
        _http = http; _o = options; _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<WwsResponse> SendAsync(string operation, ErpServicePass pass, string? session, CancellationToken ct,
        string? functionName = null, IReadOnlyDictionary<string, string>? parameters = null, int maxLines = 26)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WwsSettings.RequestTimeout);
        try { return await SendCoreAsync(operation, pass, session, timeout.Token, functionName, parameters, maxLines); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ErpException("RequestTimedOut"); }
    }

    private async Task<WwsResponse> SendCoreAsync(string operation, ErpServicePass pass, string? session, CancellationToken ct,
        string? functionName, IReadOnlyDictionary<string, string>? parameters, int maxLines)
    {
        if (maxLines is < 1 or > 100) throw new ArgumentException("maxLines");
        string path;
        var method = HttpMethod.Get;
        switch (operation)
        {
            case "REGISTER":
                // Pfadparameter enthalten Zugangsdaten – diese URL wird nie protokolliert.
                path = "WWSERVICE/REGISTER/" + string.Join("/", new[] { _o.Vendor, _o.Application, _o.SecureAppId,
                    _o.Revision.ToString(CultureInfo.InvariantCulture), _o.Username, _o.Password }.Select(Uri.EscapeDataString));
                break;
            case "VALIDATE":
                path = "WWSERVICE/VALIDATE/" + Uri.EscapeDataString(pass.PassId);
                break;
            case "CONNECT":
                path = "WWSERVICE/CONNECT/" + Uri.EscapeDataString(pass.PassId) + "/" + Uri.EscapeDataString(_o.Username) + "/" + Uri.EscapeDataString(_o.Password);
                break;
            case "EXECJSON":
                if (functionName is null || !AllowedFunctions.Contains(functionName, StringComparer.Ordinal)) throw new ArgumentException("ERP-Funktion nicht freigegeben.");
                path = "EXECJSON"; method = HttpMethod.Put;
                break;
            default:
                throw new ArgumentException("ERP-Operation nicht freigegeben.");
        }

        using var request = new HttpRequestMessage(method, new Uri(new Uri(_o.Endpoint), path));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (operation != "REGISTER")
        {
            var timestamp = _clock().UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
            var hash = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(pass.AppSecret + timestamp))).ToLowerInvariant();
            request.Headers.Add("WWSVC-TS", timestamp);
            request.Headers.Add("WWSVC-HASH", hash);
            request.Headers.Add("WWSVC-REQID", pass.RequestId.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("WWSVC-EXECUTE-MODE", "SYNCHRON");
            if (!string.IsNullOrEmpty(session)) request.Headers.Add("WWSVC-SESSION-TOKEN", session);
            if (operation == "EXECJSON")
            {
                request.Headers.Add("WWSVC-ACCEPT-RESULT-MAX-LINES", maxLines.ToString(CultureInfo.InvariantCulture));
                var info = new Dictionary<string, object>
                {
                    ["SERVICEPASS"] = pass.PassId, ["APPHASH"] = hash, ["TIMESTAMP"] = timestamp, ["REQUESTID"] = pass.RequestId,
                    ["EXECUTE_MODE"] = "SYNCHRON", ["GET_RESULT_TYPE"] = "JSON", ["GET_RESULT_MAX_LINES"] = maxLines
                };
                if (!string.IsNullOrEmpty(session)) info["SESSION_TOKEN"] = session;
                var args = (parameters ?? new Dictionary<string, string>()).Select(p => new { PNAME = p.Key, PTYPE = "S", PCONTENT = p.Value }).ToList();
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    WWSVC_PASSINFO = info,
                    WWSVC_FUNCTION = new { FUNCTIONNAME = functionName, REVISION = 0, PARAMETER = args }
                }), Encoding.UTF8, "application/json");
            }
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.Content.Headers.ContentLength.GetValueOrDefault() > MaxResponse) throw new ErpException("ResponseTooLarge");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > MaxResponse) throw new ErpException("ResponseTooLarge");
                buffer.Write(chunk, 0, n);
            }
            var bytes = buffer.ToArray();
            // BELEG.PUT liefert bei Erfolg u. U. keinen Inhalt
            if (bytes.Length == 0 || bytes.All(b => b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
            {
                if (response.IsSuccessStatusCode && operation == "EXECJSON" && functionName == "BELEG.PUT")
                    return new WwsResponse { Status = 200, ResultStatus = 200, Body = JsonDocument.Parse("{}").RootElement.Clone() };
                throw new ErpException("InvalidResponse", (int)response.StatusCode);
            }
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("COMRESULT", out var com) || com.ValueKind != JsonValueKind.Object
                || !com.TryGetProperty("STATUS", out var st) || st.ValueKind != JsonValueKind.Number || !st.TryGetInt32(out var status))
                throw new ErpException("InvalidResponse", (int)response.StatusCode);
            var answer = new WwsResponse { Status = (int)response.StatusCode, ResultStatus = status, Body = body.Clone() };
            if (com.TryGetProperty("ERRORCODE", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)) answer.ErrorCode = number;
            if (response.Headers.TryGetValues("WWSVC-SESSION-TOKEN", out var tokens)) answer.SessionToken = tokens.SingleOrDefault();
            return answer;
        }
        catch (HttpRequestException ex)
        {
            throw ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => new ErpException("DnsFailure"),
                HttpRequestError.SecureConnectionError => new ErpException("TlsFailure"),
                HttpRequestError.ConnectionError => new ErpException("ConnectionFailure"),
                _ => new ErpException("Unavailable")
            };
        }
        catch (JsonException) { throw new ErpException("InvalidResponse"); }
        catch (IOException) { throw new ErpException("Unavailable"); }
    }
}

/// <summary>
/// Servicepass-Verwaltung wie im MCP-Server: REGISTER nur wenn erlaubt (Absicht vorher gespeichert, nie blind doppelt),
/// VALIDATE vor jeder Operation (202 = Freigabe ausstehend, 404/50200 = neu registrieren), CONNECT mit einer
/// Wiederholung bei 401, Anfragenummer vor jedem Aufruf gespeichert. Ein Gate: nie parallel.
/// </summary>
public sealed class ErpServicePassManager
{
    private readonly WwsSettings _o;
    private readonly WwsServiceClient _client;
    private readonly Func<string, ErpServicePass?> _load;
    private readonly Action<string, ErpServicePass> _save;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _session;

    public ErpServicePassManager(WwsSettings options, WwsServiceClient client, Func<string, ErpServicePass?> load, Action<string, ErpServicePass> save,
        Func<DateTimeOffset>? clock = null)
    {
        _o = options; _client = client; _load = load; _save = save; _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public WwsSettings Settings => _o;

    /// <summary>Pass prüfen (ggf. registrieren) und Sitzung aufbauen.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await ReadyAsync(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>Einen Beleg per BELNDX lesen (erste Zeile) – null, wenn es ihn nicht gibt.</summary>
    public async Task<JsonElement?> ReadDocumentAsync(string belndx, CancellationToken ct)
    {
        var r = await ExecAsync("BELEG.GET", new Dictionary<string, string> { ["BELNDX"] = belndx }, ct, maxLines: 1);
        if (!r.Succeeded) throw new ErpException(r.Status == 401 ? "SessionLoginRequired" : "QueryRejected", r.Status, r.ResultStatus, r.ErrorCode);
        if (r.Body.TryGetProperty("BELEGLISTE", out var list) && list.ValueKind == JsonValueKind.Object
            && list.TryGetProperty("BELEG", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            return items[0].Clone();
        return null;
    }

    /// <summary>Belege suchen (BELEG.GET mit beliebigen Suchparametern, z. B. Belegart und Belegnummer).</summary>
    public async Task<List<JsonElement>> FindDocumentsAsync(IReadOnlyDictionary<string, string> search, CancellationToken ct, int maxLines = 5)
    {
        var r = await ExecAsync("BELEG.GET", new Dictionary<string, string>(search), ct, maxLines: maxLines);
        if (!r.Succeeded) throw new ErpException(r.Status == 401 ? "SessionLoginRequired" : "QueryRejected", r.Status, r.ResultStatus, r.ErrorCode);
        var list = new List<JsonElement>();
        if (r.Body.TryGetProperty("BELEGLISTE", out var bl) && bl.ValueKind == JsonValueKind.Object
            && bl.TryGetProperty("BELEG", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray()) list.Add(it.Clone());
        return list;
    }

    /// <summary>Felder im Belegkopf setzen (BELEG.PUT). Absolute Werte – eine Wiederholung ist unkritisch.</summary>
    public async Task WriteDocumentAsync(string belndx, IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal) { ["BELNDX"] = belndx };
        foreach (var (k, v) in fields) p[k] = v;
        p["KEINE_VERBUCHUNG"] = "1";
        p["BERECHNE_SUMMEN"] = "0";
        var r = await ExecAsync("BELEG.PUT", p, ct);
        if (!r.Succeeded) throw new ErpException(r.Status == 401 ? "SessionLoginRequired" : "WriteRejected", r.Status, r.ResultStatus, r.ErrorCode);
    }

    private async Task<WwsResponse> ExecAsync(string function, Dictionary<string, string> parameters, CancellationToken ct, int maxLines = 26)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var pass = await ReadyAsync(ct);
            var r = await SendAsync("EXECJSON", pass, ct, function, parameters, maxLines);
            if (r.Status == 401 && r.ResultStatus == 401 && _o.Username.Length > 0)
            {
                _session = null;
                await ConnectAsync(pass, ct);
                r = await SendAsync("EXECJSON", pass, ct, function, parameters, maxLines);
            }
            return r;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Nach „Registrierung mit unklarem Ausgang“: Admin bestätigt, dass neu registriert werden darf.</summary>
    public void ResetUnknownRegistration()
    {
        var binding = _o.Binding();
        var pass = _load(binding);
        if (pass is { State: "RegistrationOutcomeUnknown" })
            _save(binding, new ErpServicePass { Binding = binding });
    }

    public ErpServicePass? StoredPass() => _load(_o.Binding());

    private async Task<ErpServicePass> ReadyAsync(CancellationToken ct)
    {
        var binding = _o.Binding();
        var pass = _load(binding) ?? new ErpServicePass { Binding = binding };
        if (pass.Binding != binding) throw new ErpException("BindingMismatch");
        if (pass.State == "RegistrationOutcomeUnknown") throw new ErpException(pass.State);
        if (pass.State == "Missing")
        {
            if (!string.IsNullOrEmpty(pass.PassId) || !string.IsNullOrEmpty(pass.AppSecret) || pass.RequestId != 0) throw new ErpException("InvalidStoredPass");
            await RegisterAsync(pass, ct);
        }
        else
        {
            if (!WwsSettings.IsIdentifier(pass.PassId) || !WwsSettings.IsIdentifier(pass.AppSecret) || pass.RequestId < 0
                || pass.State is not ("Ready" or "PendingApproval")) throw new ErpException("InvalidStoredPass");
            var validation = await SendAsync("VALIDATE", pass, ct);
            if (validation.Status == 401 && validation.ResultStatus == 401 && _o.Username.Length > 0)
            {
                _session = null;
                await ConnectAsync(pass, ct);
                validation = await SendAsync("VALIDATE", pass, ct);
            }
            if (validation.Status == 202 && validation.ResultStatus == 202)
            {
                pass.State = "PendingApproval"; _save(binding, pass);
                throw new ErpException("PendingApproval");
            }
            if (validation.Status == 404 && validation.ResultStatus == 404 && validation.ErrorCode == 50200)
                await RegisterAsync(pass, ct); // nur der dokumentierte Code „Pass fehlt“ erlaubt das Ersetzen
            else if (!validation.Succeeded)
                throw new ErpException("PassValidationRejected", validation.Status, validation.ResultStatus, validation.ErrorCode);
            else
            {
                pass.State = "Ready"; pass.LastValidatedUtc = _clock(); _save(binding, pass);
            }
        }
        if (pass.State == "PendingApproval") throw new ErpException("PendingApproval");
        if (_o.Username.Length > 0 && string.IsNullOrEmpty(_session)) await ConnectAsync(pass, ct);
        return pass;
    }

    private async Task RegisterAsync(ErpServicePass pass, CancellationToken ct)
    {
        if (!_o.AllowRegistration) throw new ErpException("RegistrationDisabled");
        ct.ThrowIfCancellationRequested();
        // Absicht vorher speichern: übersteht verlorene Antworten und Abstürze. Nie blind doppelt registrieren.
        pass.State = "RegistrationOutcomeUnknown"; _save(pass.Binding, pass);
        _session = null;
        var r = await _client.SendAsync("REGISTER", pass, null, ct);
        if ((r.Status != 200 && r.Status != 202) || r.ResultStatus != r.Status) throw new ErpException("RegistrationOutcomeUnknown");
        if (!r.Body.TryGetProperty("SERVICEPASS", out var data) || data.ValueKind != JsonValueKind.Object) throw new ErpException("RegistrationOutcomeUnknown");
        var id = Text(data, "PASSID");
        var secret = Text(data, "APPID");
        if (!WwsSettings.IsIdentifier(id) || !WwsSettings.IsIdentifier(secret)) throw new ErpException("RegistrationOutcomeUnknown");
        pass.PassId = id!; pass.AppSecret = secret!; pass.RequestId = 0; pass.LastValidatedUtc = null;
        pass.State = r.Status == 202 ? "PendingApproval" : "Ready";
        _save(pass.Binding, pass);
    }

    private async Task ConnectAsync(ErpServicePass pass, CancellationToken ct)
    {
        var r = await SendAsync("CONNECT", pass, ct);
        if (!r.Succeeded) throw new ErpException("SessionLoginRequired", r.Status, r.ResultStatus, r.ErrorCode);
        var value = r.SessionToken;
        if (r.Body.TryGetProperty("SESSIONTOKEN", out var data) && data.ValueKind == JsonValueKind.Object) value = Text(data, "WWSVC_SESSION_TOKEN");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ErpException("SessionLoginRequired");
        _session = value;
    }

    private Task<WwsResponse> SendAsync(string operation, ErpServicePass pass, CancellationToken ct, string? function = null,
        IReadOnlyDictionary<string, string>? parameters = null, int maxLines = 26)
    {
        if (pass.RequestId == int.MaxValue) throw new ErpException("RequestSequenceExhausted");
        pass.RequestId++;
        _save(pass.Binding, pass); // vor dem Senden reservieren; nie eine Anfragenummer wiederverwenden
        return _client.SendAsync(operation, pass, _session, ct, function, parameters, maxLines);
    }

    private static string? Text(JsonElement data, string key) =>
        data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
