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
    public const string ModuleExchange = "bp1000md02";

    public static string BaseMessage => $"Belegpost ist nicht lizenziert ({Current.Reason}) – Drucke werden angenommen, aber nicht versendet. Einstellungen → Lizenz.";
    public static string EInvoiceMessage => "Das Modul eRechnung ist nicht lizenziert – keine ZUGFeRD/XRechnung. Einstellungen → Lizenz.";
    public static string ExchangeMessage => "Das Modul Datenaustausch (Filialverbund, FTP/SFTP) ist nicht lizenziert. Einstellungen → Lizenz.";
}

/// <summary>
/// Online-Lizenzierung über die Lizenz-API v2 (license.erpaustria.com).
/// - Produkt-ID und Registrierungstoken sind fest eingebaut (Token beim Bauen aus Licensing\registration.token, nicht im Git).
/// - Pro Installation einmalig: clientId (GUID) und Installationsgeheimnis (32 Zufallsbytes, hex), verschlüsselt im Datenordner.
/// - Server: zuerst Port 443; antwortet dort nichts oder ein anderer Dienst, Port 8889; sonst Fehler.
/// - Abfrage: in den ersten 30 Tagen täglich, danach wöchentlich (bei Störung stündlich erneut). Jede Abfrage meldet die Programmversion.
/// - Testzeit 30 Tage ab erstem Start, solange die Installation noch nie freigeschaltet war (nicht bei Sperre/Aboende).
/// - Ist der Server nicht erreichbar, gilt der zuletzt bestätigte Stand 14 Tage weiter.
/// </summary>
public sealed class LicenseService : BackgroundService
{
    public const string ProductId = "80d368a7-3aec-491f-b31d-073c9d1043e0";
    public const string ProductCode = "bp1000";
    public static readonly string[] DefaultServers = { "https://license.erpaustria.com/api/v2", "https://license.erpaustria.com:8889/api/v2" };
    public static readonly TimeSpan TrialPeriod = TimeSpan.FromDays(30);
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    /// <summary>Gemeldete Programmversion (X-Product-Version) – muss im Versionskatalog des Lizenzservers stehen.</summary>
    public static string ProductVersion { get; } = (typeof(LicenseService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "0.0.0");

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
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(15) })
        { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Belegpost", ProductVersion));
        _file = Path.Combine(settings.DataDirectory, "lizenz.dat");
        _state = Load();
        Publish();
    }

    /// <summary>Server-Adressen in Reihenfolge (für Tests überschreibbar; im Betrieb 443, dann 8889).</summary>
    public string[] Servers { get; init; } = DefaultServers;

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
        public string? Server { get; set; }
        /// <summary>Nutzungslizenz gültig (licenseValid).</summary>
        public bool Active { get; set; }
        public bool EverActive { get; set; }
        public string? Status { get; set; }
        public string? LicenseModel { get; set; }
        public bool Blocked { get; set; }
        public string? BlockReason { get; set; }
        public bool UpdatesAllowed { get; set; }
        public DateTimeOffset? ValidUntil { get; set; }
        public DateTimeOffset? UpdatesAllowedUntil { get; set; }
        public string? ReportedVersion { get; set; }
        public bool? VersionAllowed { get; set; }
        public string? VersionStatus { get; set; }
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

    /// <summary>Status des Servers in Worten.</summary>
    public static string StatusText(string? status, string? blockReason = null) => status switch
    {
        "valid" => "gültig",
        "blocked" => "gesperrt" + (string.IsNullOrWhiteSpace(blockReason) ? "" : $" ({BlockText(blockReason)})"),
        "product_disabled" => "Produkt beim Hersteller deaktiviert",
        "disabled" => "Installation deaktiviert",
        "unassigned" => "noch keinem Kunden zugeordnet",
        "customer_missing" => "Kundennummer beim Hersteller nicht mehr vorhanden",
        "subscription_ended" => "Abo abgelaufen",
        "version_not_allowed" => "diese Programmversion ist nicht freigegeben",
        null or "" => "noch nicht abgefragt",
        _ => status
    };

    private static string BlockText(string r) => r switch { "non_payment" => "Zahlung offen", _ => r };

    public static string VersionText(string? v) => v switch
    {
        "allowed" => "freigegeben",
        "unknown_version" => "beim Hersteller nicht bekannt",
        "version_disabled" => "vom Hersteller gesperrt",
        "not_yet_released" => "noch nicht veröffentlicht",
        "released_after_entitlement" => "nach Ende der Updateberechtigung erschienen",
        "registration_date_unknown" => "Registrierungsdatum beim Hersteller unbekannt",
        "not_reported" or null => "noch nicht gemeldet",
        _ => v
    };

    /// <summary>Freigaben aus dem gespeicherten Stand berechnen.</summary>
    public static LicenseGate Evaluate(LicenseState s, DateTimeOffset now)
    {
        var confirmed = s.LastSuccess is { } ok && now - ok <= OfflineGrace;
        if (s.Active && confirmed)
            return new LicenseGate(true, s.Modules.Contains(License.ModuleEInvoice, StringComparer.OrdinalIgnoreCase),
                s.Modules.Contains(License.ModuleExchange, StringComparer.OrdinalIgnoreCase), "lizenziert",
                (s.LicenseModel == "purchase" ? "Kauflizenz" : "Abo") + (s.ValidUntil is { } u ? $" bis {u.ToLocalTime():dd.MM.yyyy}" : "")
                + (s.CustomerNumber is { } k ? $" · Kunde {k}" : ""));
        if (s.Active && !confirmed)
            return new LicenseGate(false, false, false, "abgelaufen", $"Lizenzserver seit über {OfflineGrace.TotalDays:0} Tagen nicht erreichbar");
        // ausdrücklich gesperrt oder Abo beendet: sofort, auch in der Testzeit
        if (s.Blocked || s.Status is "blocked" or "subscription_ended" or "product_disabled")
            return new LicenseGate(false, false, false, s.Status == "subscription_ended" ? "abgelaufen" : "gesperrt", StatusText(s.Status, s.BlockReason));
        // noch nie freigeschaltet → Testzeit
        var trialEnd = s.FirstStart + TrialPeriod;
        if (!s.EverActive && now < trialEnd)
            return new LicenseGate(true, true, true, "test", $"Testzeit bis {trialEnd.ToLocalTime():dd.MM.yyyy}");
        return new LicenseGate(false, false, false, "ungueltig",
            s.Status is { Length: > 0 } st && st != "valid" ? StatusText(st, s.BlockReason)
            : s.EverActive ? "Lizenz nicht mehr gültig" : "Testzeit abgelaufen, noch nicht freigeschaltet");
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

    /// <summary>Abfrageabstand: in den ersten 30 Tagen täglich, danach wöchentlich.</summary>
    public TimeSpan CheckInterval
    {
        get { lock (_gate) return _now() - _state.FirstStart < TrialPeriod ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7); }
    }

    public object Status()
    {
        LicenseState s; lock (_gate) s = _state;
        var g = Gate;
        return new
        {
            mode = g.Mode, reason = g.Reason, @base = g.Base, eRechnung = g.EInvoice, datenaustausch = g.Exchange,
            productCode = ProductCode, productId = ProductId, productVersion = ProductVersion, server = s.Server ?? string.Join(" / ", Servers),
            tokenEmbedded = RegistrationToken is not null,
            s.LicenseCode, s.InstallationId, s.ClientId, s.CustomerNumber, s.Active, modules = s.ModuleNames, moduleCodes = s.Modules,
            status = s.Status, statusText = StatusText(s.Status, s.BlockReason), s.LicenseModel, s.Blocked, s.BlockReason,
            s.UpdatesAllowed, validUntil = s.ValidUntil, updatesAllowedUntil = s.UpdatesAllowedUntil,
            s.ReportedVersion, s.VersionAllowed, s.VersionStatus, versionText = VersionText(s.VersionStatus),
            s.LastSuccess, s.LastAttempt, s.LastError, s.FirstStart,
            nextCheck = (s.LastSuccess is { } ls ? ls + CheckInterval : (DateTimeOffset?)null),
            checkEvery = CheckInterval.TotalDays >= 7 ? "wöchentlich" : "täglich",
            trialEnd = s.EverActive ? (DateTimeOffset?)null : s.FirstStart + TrialPeriod,
            graceEnd = s.LastSuccess is { } ok ? ok + OfflineGrace : (DateTimeOffset?)null
        };
    }

    private volatile bool _force;
    public void CheckNow() { _force = true; _wake.Release(); }

    // ---------------- Server ----------------

    private sealed class Detail
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("productId")] public string? ProductId { get; set; }
        [JsonPropertyName("licenseCode")] public string? LicenseCode { get; set; }
        [JsonPropertyName("customerNumber")] public string? CustomerNumber { get; set; }
        [JsonPropertyName("licenseModel")] public string? LicenseModel { get; set; }
        [JsonPropertyName("licenseValid")] public bool? LicenseValid { get; set; }
        [JsonPropertyName("active")] public bool Active { get; set; }
        [JsonPropertyName("updatesAllowed")] public bool UpdatesAllowed { get; set; }
        [JsonPropertyName("validUntilUtc")] public DateTimeOffset? ValidUntil { get; set; }
        [JsonPropertyName("updatesAllowedUntilUtc")] public DateTimeOffset? UpdatesAllowedUntil { get; set; }
        [JsonPropertyName("blocked")] public bool Blocked { get; set; }
        [JsonPropertyName("blockReason")] public string? BlockReason { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reportedVersion")] public string? ReportedVersion { get; set; }
        [JsonPropertyName("versionAllowed")] public bool? VersionAllowed { get; set; }
        [JsonPropertyName("versionStatus")] public string? VersionStatus { get; set; }
        [JsonPropertyName("revision")] public int Revision { get; set; }
        [JsonPropertyName("modules")] public List<ModuleItem>? Modules { get; set; }
    }

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
                if (s.InstallationId is null)
                {
                    if (RegistrationToken is null) throw new InvalidOperationException("In dieser Programmversion ist kein Registrierungstoken eingebaut.");
                    var reg = await CallAsync(s, b =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, b + "/registrations")
                        { Content = JsonContent.Create(new { productId = ProductId, clientId = s.ClientId }) };
                        req.Headers.Add("X-Registration-Token", RegistrationToken);
                        req.Headers.Add("X-Installation-Token", s.InstallationToken);
                        return req;
                    }, ct);
                    s.InstallationId = reg.Id;
                    s.LicenseCode = reg.LicenseCode ?? s.LicenseCode;
                    _log.LogInformation("Belegpost beim Lizenzserver registriert: {Code}", reg.LicenseCode);
                }
                // die eine Lizenzabfrage (meldet die Programmversion)
                var d = await CallAsync(s, b =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, $"{b}/installations/{Uri.EscapeDataString(s.InstallationId!)}/license");
                    req.Headers.Add("X-Installation-Token", s.InstallationToken);
                    req.Headers.Add("X-Product-Version", ProductVersion);
                    return req;
                }, ct);
                Apply(s, d);
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
                s.LastError = ex.Message;
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

    private static void Apply(LicenseState s, Detail d)
    {
        s.LicenseCode = d.LicenseCode ?? s.LicenseCode;
        s.CustomerNumber = d.CustomerNumber ?? s.CustomerNumber;
        s.Active = d.LicenseValid ?? d.Active;
        if (s.Active) s.EverActive = true;
        s.Status = d.Status ?? (s.Active ? "valid" : null);
        s.LicenseModel = d.LicenseModel;
        s.Blocked = d.Blocked;
        s.BlockReason = string.IsNullOrWhiteSpace(d.BlockReason) ? null : d.BlockReason;
        s.UpdatesAllowed = d.UpdatesAllowed;
        s.ValidUntil = d.ValidUntil;
        s.UpdatesAllowedUntil = d.UpdatesAllowedUntil;
        s.ReportedVersion = d.ReportedVersion;
        s.VersionAllowed = d.VersionAllowed;
        s.VersionStatus = d.VersionStatus;
        s.Revision = d.Revision;
        var mods = (d.Modules ?? new()).Where(m => m.Enabled && !string.IsNullOrEmpty(m.Code)).ToList();
        s.Modules = mods.Select(m => m.Code!).ToList();
        s.ModuleNames = mods.Select(m => m.Name ?? m.Code!).ToList();
    }

    /// <summary>
    /// Anfrage an den Lizenzserver: zuerst Port 443, dann 8889. Ein Server zählt nur, wenn die Lizenz-API antwortet
    /// (JSON mit Lizenzdaten bzw. einem ihrer Fehlercodes). Keine Verbindung oder ein anderer Dienst → nächster; zuletzt Fehler.
    /// </summary>
    private async Task<Detail> CallAsync(LicenseState s, Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        var problems = new List<string>();
        foreach (var server in Servers)
        {
            var b = server.TrimEnd('/');
            var port = new Uri(b).Port;
            HttpResponseMessage res;
            using var req = build(b);
            try { res = await _http.SendAsync(req, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                problems.Add($"Port {port}: keine Verbindung ({(ex is TaskCanceledException ? "Zeitüberschreitung" : ex.GetBaseException().Message)})");
                continue;
            }
            using (res)
            {
                JsonElement json;
                try
                {
                    var media = res.Content.Headers.ContentType?.MediaType ?? "";
                    if (!media.Contains("json", StringComparison.OrdinalIgnoreCase)) throw new JsonException();
                    json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if (json.ValueKind != JsonValueKind.Object) throw new JsonException();
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    problems.Add($"Port {port}: anderer Dienst (HTTP {(int)res.StatusCode}, keine Lizenz-API)");
                    continue;
                }
                if (json.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                {
                    var code = err.GetString();
                    if (code is "route_not_found" or "method_not_allowed") { problems.Add($"Port {port}: anderer Dienst ({code})"); continue; }
                    s.Server = b;
                    throw new LicenseApiException(code, (int)res.StatusCode);
                }
                if (!res.IsSuccessStatusCode || !json.TryGetProperty("licenseCode", out _))
                {
                    problems.Add($"Port {port}: anderer Dienst (HTTP {(int)res.StatusCode})");
                    continue;
                }
                s.Server = b;
                return json.Deserialize<Detail>()!;
            }
        }
        throw new InvalidOperationException("Lizenzserver nicht erreichbar – " + string.Join("; ", problems));
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
            "product_version_required" or "invalid_product_version" => $"Programmversion {ProductVersion} wird vom Lizenzserver nicht angenommen",
            "api_v2_disabled" or "service_unavailable" or "schema_not_initialized" or "schema_version_unsupported" or "license_policy_missing"
                => "Lizenzserver vorübergehend nicht verfügbar",
            _ => $"Lizenzserver antwortet mit {status}{(code is null ? "" : " (" + code + ")")}"
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            // fällig? (letzte erfolgreiche Abfrage + täglich/wöchentlich)
            DateTimeOffset? last; lock (_gate) last = _state.LastSuccess;
            var due = _force || last is null || _now() - last.Value >= CheckInterval;
            _force = false;
            var ok = !due || await RefreshAsync(ct);
            Publish();
            var wait = !ok ? RetryInterval
                : last is null ? CheckInterval
                : TimeSpan.FromTicks(Math.Clamp((CheckInterval - (_now() - (_state.LastSuccess ?? _now()))).Ticks, TimeSpan.FromMinutes(5).Ticks, CheckInterval.Ticks));
            // spätestens stündlich neu bewerten (Testzeit-/Offline-Ende ohne Abfrage)
            if (wait > TimeSpan.FromHours(1)) wait = TimeSpan.FromHours(1);
            try { await _wake.WaitAsync(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
