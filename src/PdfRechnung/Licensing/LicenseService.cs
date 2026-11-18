using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfRechnung.Settings;

namespace PdfRechnung.Licensing;

/// <summary>Was das Produkt darf. Grundfunktion = Druck annehmen ist immer erlaubt; Versand, ERP-Rückmeldung usw. hängen an <see cref="Base"/>.</summary>
public sealed record LicenseGate(bool Base, bool EInvoice, bool Exchange, string Mode, string Reason)
{
    /// <summary>Ohne Lizenzdienst (Tests, Werkzeuge): alles erlaubt.</summary>
    public static readonly LicenseGate Unrestricted = new(true, true, true, "ohne", "");
}

/// <summary>Zugriff auf den aktuellen Lizenzstand ohne Abhängigkeit (für Prüfungen tief in den Diensten).</summary>
public static class License
{
    private static volatile LicenseGate _current = LicenseGate.Unrestricted;
    public static LicenseGate Current => _current;
    internal static void Set(LicenseGate g) => _current = g;
    /// <summary>Nur für Tests.</summary>
    public static void ResetForTests(LicenseGate? g = null) => _current = g ?? LicenseGate.Unrestricted;

    public const string ModuleEInvoice = "bp1000md01";
    public const string ModuleExchange = "bw1000md02";

    public static string BaseMessage => $"Belegpost ist nicht lizenziert ({Current.Reason}) – Drucke werden angenommen, aber nicht versendet. Einstellungen → Lizenz.";
    public static string EInvoiceMessage => "Das Modul eRechnung ist nicht lizenziert – keine ZUGFeRD/XRechnung. Einstellungen → Lizenz.";
    public static string ExchangeMessage => "Das Modul Datenaustausch (Filialverbund, FTP/SFTP) ist nicht lizenziert. Einstellungen → Lizenz.";
}

/// <summary>
/// Online-Lizenzierung über die Lizenz-API v2 (license.erpaustria.com).
/// - Produkt-ID und Registrierungstoken sind fest eingebaut (Token beim Bauen aus Licensing\registration.token, nicht im Git).
/// - Pro Installation einmalig: clientId (GUID) und Installationsgeheimnis (32 Zufallsbytes, hex), verschlüsselt im Datenordner.
/// - Registrierung, dann alle 6 h Lizenzabruf (bei Fehler alle 30 min erneut).
/// - Testzeit 30 Tage ab erstem Start, solange die Installation noch nie freigeschaltet war.
/// - Ist der Server nicht erreichbar, gilt der zuletzt bestätigte Stand 14 Tage weiter.
/// </summary>
public sealed class LicenseService : BackgroundService
{
    public const string ProductId = "80d368a7-3aec-491f-b31d-073c9d1043e0";
    public const string ProductCode = "bp1000";
    public const string DefaultServer = "https://license.erpaustria.com:8889/api/v2";
    public static readonly TimeSpan TrialPeriod = TimeSpan.FromDays(30);
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    private readonly SettingsStore _settings;
    private readonly ILogger<LicenseService> _log;
    private readonly HttpClient _http;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private LicenseState _state;
    private readonly Func<DateTimeOffset> _now;

    private readonly bool _global;

    /// <param name="publishGlobally">false = nur <see cref="Gate"/> setzen, nicht <see cref="License.Current"/> (Tests).</param>
    public LicenseService(SettingsStore settings, ILogger<LicenseService> log, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null,
        bool publishGlobally = true)
    {
        _settings = settings; _log = log; _global = publishGlobally;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Belegpost", typeof(LicenseService).Assembly.GetName().Version?.ToString() ?? "1"));
        _file = Path.Combine(settings.DataDirectory, "lizenz.dat");
        _state = Load();
        Publish();
    }

    /// <summary>Server-Adresse (für Tests überschreibbar; im Betrieb fest).</summary>
    public string Server { get; init; } = DefaultServer;

    /// <summary>Eingebautes Registrierungstoken (beim Bauen eingebettet; berechtigt nur zur Registrierung, nicht zur Freischaltung).</summary>
    public static string? RegistrationToken { get; set; } = ReadEmbeddedToken();

    private static string? ReadEmbeddedToken()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Licensing.registration.token");
        if (s is null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        var t = r.ReadToEnd().Trim();
        return t.Length == 0 ? null : t;
    }

    // ---------------- Zustand ----------------

    public sealed class LicenseState
    {
        public string ClientId { get; set; } = Guid.NewGuid().ToString();
        public string InstallationToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        public string? InstallationId { get; set; }
        public string? LicenseCode { get; set; }
        public DateTimeOffset FirstStart { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastSuccess { get; set; }
        public DateTimeOffset? LastAttempt { get; set; }
        public string? LastError { get; set; }
        public bool Active { get; set; }
        public bool EverActive { get; set; }
        public bool Enabled { get; set; }
        public string? CustomerNumber { get; set; }
        public List<string> Modules { get; set; } = new();
        public List<string> ModuleNames { get; set; } = new();
        public int Revision { get; set; }
    }

    private LicenseState Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var json = Encoding.UTF8.GetString(_settings.Secrets.UnprotectBytes(File.ReadAllBytes(_file)));
                var s = JsonSerializer.Deserialize<LicenseState>(json);
                if (s is not null) return s;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Lizenzdatei {File} nicht lesbar – neue Installation wird registriert", _file); }
        var fresh = new LicenseState { FirstStart = _now() };
        Save(fresh);
        return fresh;
    }

    private void Save(LicenseState s)
    {
        try
        {
            var bytes = _settings.Secrets.ProtectBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s)));
            var tmp = _file + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Lizenzdatei konnte nicht gespeichert werden"); }
    }

    /// <summary>Freigaben aus dem gespeicherten Stand berechnen.</summary>
    public static LicenseGate Evaluate(LicenseState s, DateTimeOffset now)
    {
        var confirmed = s.LastSuccess is { } ok && now - ok <= OfflineGrace;
        if (s.Active && confirmed)
            return new LicenseGate(true, s.Modules.Contains(License.ModuleEInvoice, StringComparer.OrdinalIgnoreCase),
                s.Modules.Contains(License.ModuleExchange, StringComparer.OrdinalIgnoreCase), "lizenziert",
                s.CustomerNumber is { } k ? $"Kunde {k}" : "");
        if (s.Active && !confirmed)
            return new LicenseGate(false, false, false, "abgelaufen", $"Lizenzserver seit über {OfflineGrace.TotalDays:0} Tagen nicht erreichbar");
        // noch nie freigeschaltet → Testzeit
        var trialEnd = s.FirstStart + TrialPeriod;
        if (!s.EverActive && now < trialEnd)
            return new LicenseGate(true, true, true, "test", $"Testzeit bis {trialEnd.ToLocalTime():dd.MM.yyyy}");
        return new LicenseGate(false, false, false, "ungueltig",
            s.EverActive ? "Lizenz gesperrt oder Kundenzuordnung entfernt" : "Testzeit abgelaufen, noch nicht freigeschaltet");
    }

    /// <summary>Aktueller Stand dieser Instanz.</summary>
    public LicenseGate Gate { get; private set; } = LicenseGate.Unrestricted;

    private void Publish()
    {
        LicenseGate g;
        lock (_gate) g = Evaluate(_state, _now());
        Gate = g;
        if (_global) License.Set(g);
    }

    public object Status()
    {
        LicenseState s; lock (_gate) s = _state;
        var g = Gate;
        return new
        {
            mode = g.Mode, reason = g.Reason, @base = g.Base, eRechnung = g.EInvoice, datenaustausch = g.Exchange,
            productCode = ProductCode, productId = ProductId, server = Server, tokenEmbedded = RegistrationToken is not null,
            s.LicenseCode, s.InstallationId, s.ClientId, s.CustomerNumber, s.Active, s.Enabled, modules = s.ModuleNames,
            moduleCodes = s.Modules, s.LastSuccess, s.LastAttempt, s.LastError, s.FirstStart,
            trialEnd = s.EverActive ? (DateTimeOffset?)null : s.FirstStart + TrialPeriod,
            graceEnd = s.LastSuccess is { } ok ? ok + OfflineGrace : (DateTimeOffset?)null
        };
    }

    public void CheckNow() => _wake.Release();

    // ---------------- Server ----------------

    private sealed record Detail(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("licenseCode")] string? LicenseCode,
        [property: JsonPropertyName("customerNumber")] string? CustomerNumber,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("revision")] int Revision,
        [property: JsonPropertyName("modules")] List<ModuleItem>? Modules);

    private sealed record ModuleItem([property: JsonPropertyName("Code")] string? Code, [property: JsonPropertyName("Name")] string? Name, [property: JsonPropertyName("Enabled")] bool Enabled);

    /// <summary>Registrieren (falls nötig) und Lizenz abrufen. true = Antwort erhalten.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await _run.WaitAsync(ct);
        try
        {
            LicenseState s; lock (_gate) s = _state;
            s.LastAttempt = _now();
            try
            {
                Detail d;
                if (s.InstallationId is null)
                {
                    if (RegistrationToken is null) throw new InvalidOperationException("In dieser Programmversion ist kein Registrierungstoken eingebaut.");
                    using var req = new HttpRequestMessage(HttpMethod.Post, Server.TrimEnd('/') + "/registrations")
                    { Content = JsonContent.Create(new { productId = ProductId, clientId = s.ClientId }) };
                    req.Headers.Add("X-Registration-Token", RegistrationToken);
                    req.Headers.Add("X-Installation-Token", s.InstallationToken);
                    d = await SendAsync(req, ct);
                    s.InstallationId = d.Id;
                    _log.LogInformation("Belegpost beim Lizenzserver registriert: {Code}", d.LicenseCode);
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{Server.TrimEnd('/')}/installations/{Uri.EscapeDataString(s.InstallationId)}/license");
                    req.Headers.Add("X-Installation-Token", s.InstallationToken);
                    d = await SendAsync(req, ct);
                }
                s.LicenseCode = d.LicenseCode ?? s.LicenseCode;
                s.CustomerNumber = d.CustomerNumber;
                s.Enabled = d.Enabled;
                s.Active = d.Active;
                if (d.Active) s.EverActive = true;
                s.Revision = d.Revision;
                var mods = (d.Modules ?? new()).Where(m => m.Enabled && !string.IsNullOrEmpty(m.Code)).ToList();
                s.Modules = mods.Select(m => m.Code!).ToList();
                s.ModuleNames = mods.Select(m => m.Name ?? m.Code!).ToList();
                s.LastSuccess = _now();
                s.LastError = null;
                return true;
            }
            catch (LicenseApiException ex) when (ex.Code is "invalid_installation_token" or "record_not_found")
            {
                // Installationsgeheimnis ungültig (z. B. beim Hersteller neu erzeugt) – nichts selbst freischalten
                s.Active = false;
                s.LastError = ex.Message + " – bitte beim Hersteller melden (Lizenzcode angeben).";
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                s.LastError = ex is LicenseApiException ? ex.Message : "Lizenzserver nicht erreichbar: " + ex.Message;
                _log.LogWarning("Lizenzabruf fehlgeschlagen: {Msg}", s.LastError);
                return false;
            }
            finally
            {
                lock (_gate) _state = s;
                Save(s);
                Publish();
            }
        }
        finally { _run.Release(); }
    }

    private async Task<Detail> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            string? code = null;
            try { code = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString(); } catch { /* keine JSON-Antwort */ }
            throw new LicenseApiException(code, (int)res.StatusCode);
        }
        return await res.Content.ReadFromJsonAsync<Detail>(ct) ?? throw new LicenseApiException("empty_response", (int)res.StatusCode);
    }

    public sealed class LicenseApiException(string? code, int status) : Exception(Describe(code, status))
    {
        public string? Code { get; } = code;
        private static string Describe(string? code, int status) => code switch
        {
            "invalid_registration_token" => "Registrierungstoken vom Lizenzserver abgelehnt",
            "invalid_installation_token" => "Installationsschlüssel vom Lizenzserver abgelehnt",
            "installation_already_registered" => "Diese Installation ist bereits mit einem anderen Schlüssel registriert",
            "record_not_found" => "Installation beim Lizenzserver nicht gefunden",
            "api_v2_disabled" or "service_unavailable" or "schema_not_initialized" => "Lizenzserver vorübergehend nicht verfügbar",
            _ => $"Lizenzserver antwortet mit {status}{(code is null ? "" : " (" + code + ")")}"
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var ok = await RefreshAsync(ct);
            Publish();
            try { await _wake.WaitAsync(ok ? CheckInterval : RetryInterval, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
