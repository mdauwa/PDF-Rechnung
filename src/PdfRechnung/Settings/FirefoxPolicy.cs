using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace PdfRechnung.Settings;

/// <summary>
/// Windows-Anmeldung in Firefox: Firefox meldet sich nur bei eingetragenen Adressen automatisch mit dem Windows-Konto an
/// (Richtlinie „Authentication“: SPNEGO = Kerberos, NTLM). Belegpost trägt die Portal-Adressen beim Start und nach dem
/// Speichern der Einstellungen selbst ein – nur bei Anmeldeart „windows“ und wenn nicht abgeschaltet.
///
/// Wo: Gibt es schon Firefox-Richtlinien in der Registry (z. B. per GPO), liest Firefox nur diese – dann dort
/// (HKLM\SOFTWARE\Policies\Mozilla\Firefox). Sonst in <c>distribution\policies.json</c> jeder gefundenen Firefox-Installation.
/// Vorhandene Einträge bleiben erhalten; eigene Einträge merkt sich Belegpost (firefox-richtlinie.json) und entfernt
/// sie wieder, wenn sich die Adressen ändern oder die Anmeldeart wechselt.
/// </summary>
public sealed class FirefoxPolicy
{
    public sealed record Status(DateTimeOffset At, bool Active, string Mode, List<string> Installs, List<string> Targets, List<string> Origins, string? Error, bool Changed);

    private sealed class State
    {
        public List<string> Origins { get; set; } = new();
        public bool NonFqdn { get; set; }
        public bool EnterpriseRoots { get; set; }
    }

    private readonly SettingsStore _settings;
    private readonly IOptionsMonitor<AuthOptions> _auth;
    private readonly IOptionsMonitor<ServerOptions> _server;
    private readonly IOptionsMonitor<PortalOptions> _portal;
    private readonly ILogger<FirefoxPolicy> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Status? Last { get; private set; }

    public FirefoxPolicy(SettingsStore settings, IOptionsMonitor<AuthOptions> auth, IOptionsMonitor<ServerOptions> server,
        IOptionsMonitor<PortalOptions> portal, ILogger<FirefoxPolicy> log)
    {
        _settings = settings; _auth = auth; _server = server; _portal = portal; _log = log;
    }

    private string StateFile => Path.Combine(_settings.DataDirectory, "firefox-richtlinie.json");

    /// <summary>Adressen des Portals, wie Benutzer sie aufrufen: Servername, voller Name, weitere Namen, öffentliche Adresse.</summary>
    public static List<string> PortalOrigins(ServerOptions s, PortalOptions p, string machine, string? domain)
    {
        var hosts = new List<string>();
        void Host(string? h) { h = h?.Trim().TrimEnd('.').ToLowerInvariant(); if (!string.IsNullOrEmpty(h) && !hosts.Contains(h)) hosts.Add(h); }
        Host(machine);
        if (!string.IsNullOrWhiteSpace(domain)) Host($"{machine}.{domain}");
        foreach (var h in s.AdditionalHostNames ?? Array.Empty<string>()) Host(h);
        var list = new List<string>();
        void Add(string o) { if (!list.Contains(o, StringComparer.OrdinalIgnoreCase)) list.Add(o); }
        foreach (var h in hosts) Add($"https://{h}:{s.HttpsPort}");
        if (s.EnableHttp && !s.RedirectHttpToHttps) foreach (var h in hosts) Add($"http://{h}:{s.HttpPort}");
        if (Uri.TryCreate(p.PublicUrl?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            Add(u.IsDefaultPort ? $"{u.Scheme}://{u.Host.ToLowerInvariant()}" : $"{u.Scheme}://{u.Host.ToLowerInvariant()}:{u.Port}");
        return list;
    }

    /// <summary>Gibt es Adressen ohne Punkt (z. B. https://server:5444)? Dann braucht Firefox AllowNonFQDN.</summary>
    public static bool HasNonFqdn(IEnumerable<string> origins) =>
        origins.Any(o => Uri.TryCreate(o, UriKind.Absolute, out var u) && !u.Host.Contains('.'));

    /// <summary>
    /// policies.json zusammenführen: eigene Adressen in SPNEGO/NTLM ergänzen, früher eigene, nicht mehr gültige entfernen.
    /// Alles andere bleibt unverändert. Liefert den neuen Text (oder den alten, wenn sich nichts ändert).
    /// </summary>
    public static string MergeJson(string? existing, IReadOnlyCollection<string> add, IReadOnlyCollection<string> removeOwn, bool nonFqdn, bool enterpriseRoots, out bool changed)
    {
        JsonObject root;
        try { root = (string.IsNullOrWhiteSpace(existing) ? null : JsonNode.Parse(existing) as JsonObject) ?? new JsonObject(); }
        catch (JsonException) { throw new InvalidOperationException("Die vorhandene policies.json ist kein gültiges JSON – bitte von Hand prüfen."); }
        var before = root.ToJsonString();
        var policies = root["policies"] as JsonObject ?? (JsonObject)(root["policies"] = new JsonObject());
        var auth = policies["Authentication"] as JsonObject;
        if (auth is null && add.Count == 0) { changed = false; return existing ?? ""; }
        auth ??= (JsonObject)(policies["Authentication"] = new JsonObject());
        foreach (var key in new[] { "SPNEGO", "NTLM" })
        {
            var arr = auth[key] as JsonArray ?? new JsonArray();
            var values = arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
            values.RemoveAll(v => removeOwn.Contains(v, StringComparer.OrdinalIgnoreCase) && !add.Contains(v, StringComparer.OrdinalIgnoreCase));
            foreach (var a in add) if (!values.Contains(a, StringComparer.OrdinalIgnoreCase)) values.Add(a);
            if (values.Count == 0) auth.Remove(key);
            else auth[key] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        }
        if (nonFqdn && add.Count > 0)
        {
            var nf = auth["AllowNonFQDN"] as JsonObject ?? (JsonObject)(auth["AllowNonFQDN"] = new JsonObject());
            nf["SPNEGO"] = true; nf["NTLM"] = true;
        }
        if (auth.Count == 0) policies.Remove("Authentication");
        if (enterpriseRoots && add.Count > 0)
        {
            var c = policies["Certificates"] as JsonObject ?? (JsonObject)(policies["Certificates"] = new JsonObject());
            if (c["ImportEnterpriseRoots"] is null) c["ImportEnterpriseRoots"] = true;
        }
        var after = root.ToJsonString();
        changed = after != before;
        return changed ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : existing ?? "";
    }

    /// <summary>Im Hintergrund anwenden (Start des Dienstes, gespeicherte Einstellungen).</summary>
    public void ApplyInBackground() => _ = Task.Run(() =>
    {
        try { Apply(); } catch (Exception ex) { _log.LogWarning(ex, "Firefox-Richtlinie"); }
    });

    public Status Apply()
    {
        _gate.Wait();
        try
        {
            var a = _auth.CurrentValue;
            var active = a.Mode == "windows" && a.FirefoxPolicy;
            var origins = active ? PortalOrigins(_server.CurrentValue, _portal.CurrentValue, Environment.MachineName, Domain()) : new List<string>();
            var status = OperatingSystem.IsWindows() ? ApplyWindows(active, origins) : new Status(DateTimeOffset.Now, active, "–", new(), new(), origins, "Nur unter Windows.", false);
            Last = status;
            if (status.Changed) _log.LogInformation("Firefox-Richtlinie {Mode}: {Targets} – {Origins}", status.Mode, string.Join(", ", status.Targets), string.Join(", ", origins));
            if (status.Error is not null) _log.LogWarning("Firefox-Richtlinie: {Error}", status.Error);
            return status;
        }
        finally { _gate.Release(); }
    }

    private static string? Domain()
    {
        try { var d = IPGlobalProperties.GetIPGlobalProperties().DomainName; return string.IsNullOrWhiteSpace(d) ? null : d; } catch { return null; }
    }

    private State LoadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new() : new(); } catch { return new(); }
    }

    [SupportedOSPlatform("windows")]
    private Status ApplyWindows(bool active, List<string> origins)
    {
        var old = LoadState();
        var installs = FindInstalls();
        var targets = new List<string>();
        var changed = false;
        string mode;
        string? error = null;
        var nonFqdn = HasNonFqdn(origins);
        try
        {
            if (RegistryPoliciesExist())
            {
                mode = "Registry";
                changed = ApplyRegistry(origins, old.Origins, nonFqdn);
                targets.Add(@"HKLM\SOFTWARE\Policies\Mozilla\Firefox");
            }
            else
            {
                mode = "policies.json";
                foreach (var dir in installs)
                {
                    var file = Path.Combine(dir, "distribution", "policies.json");
                    var existing = File.Exists(file) ? File.ReadAllText(file) : null;
                    if (existing is null && origins.Count == 0) continue;
                    var text = MergeJson(existing, origins, old.Origins, nonFqdn, enterpriseRoots: true, out var ch);
                    if (ch)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, text);
                        changed = true;
                    }
                    targets.Add(file);
                }
            }
            if (installs.Count == 0 && active) error = "Firefox ist auf diesem Server nicht installiert – nichts zu tun.";
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new State { Origins = origins, NonFqdn = nonFqdn, EnterpriseRoots = true }));
        }
        catch (UnauthorizedAccessException) { mode = "–"; error = "Keine Berechtigung – das geht nur, wenn Belegpost als Dienst läuft."; }
        catch (Exception ex) { mode = "–"; error = ex.Message; }
        return new Status(DateTimeOffset.Now, active, mode, installs, targets, origins, installs.Count == 0 && !active ? null : error, changed);
    }

    /// <summary>Firefox-Programmordner (normal, ESR, 32 Bit) aus der Registry und den Standardpfaden.</summary>
    [SupportedOSPlatform("windows")]
    public static List<string> FindInstalls()
    {
        var dirs = new List<string>();
        void Dir(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            d = d.Trim().Trim('"').TrimEnd('\\');
            if (File.Exists(Path.Combine(d, "firefox.exe")) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var product in new[] { "Mozilla Firefox", "Mozilla Firefox ESR" })
                {
                    using var p = hklm.OpenSubKey(@"SOFTWARE\Mozilla\" + product);
                    foreach (var ver in p?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var main = p!.OpenSubKey(ver + @"\Main");
                        Dir(main?.GetValue("Install Directory") as string);
                    }
                }
            }
            catch { }
        }
        Dir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox"));
        Dir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox"));
        return dirs;
    }

    private const string PolicyKey = @"SOFTWARE\Policies\Mozilla\Firefox";

    [SupportedOSPlatform("windows")]
    private static bool RegistryPoliciesExist()
    {
        using var k = Registry.LocalMachine.OpenSubKey(PolicyKey);
        return k is not null && (k.ValueCount > 0 || k.SubKeyCount > 0);
    }

    /// <summary>Registry-Form: Authentication\SPNEGO\1 = Adresse, Authentication\NTLM\1 = Adresse (Listen nummeriert).</summary>
    [SupportedOSPlatform("windows")]
    private static bool ApplyRegistry(List<string> add, List<string> removeOwn, bool nonFqdn)
    {
        var changed = false;
        using var auth = Registry.LocalMachine.CreateSubKey(PolicyKey + @"\Authentication");
        foreach (var name in new[] { "SPNEGO", "NTLM" })
        {
            using var k = auth.CreateSubKey(name);
            var values = k.GetValueNames().Select(n => (Name: n, Value: k.GetValue(n) as string ?? "")).ToList();
            foreach (var (n, v) in values)
                if (removeOwn.Contains(v, StringComparer.OrdinalIgnoreCase) && !add.Contains(v, StringComparer.OrdinalIgnoreCase)) { k.DeleteValue(n, false); changed = true; }
            var present = k.GetValueNames().Select(n => k.GetValue(n) as string ?? "").ToList();
            var next = k.GetValueNames().Select(n => int.TryParse(n, out var i) ? i : 0).DefaultIfEmpty(0).Max() + 1;
            foreach (var a in add)
                if (!present.Contains(a, StringComparer.OrdinalIgnoreCase)) { k.SetValue((next++).ToString(), a, RegistryValueKind.String); changed = true; }
        }
        if (nonFqdn && add.Count > 0)
        {
            using var nf = auth.CreateSubKey("AllowNonFQDN");
            foreach (var n in new[] { "SPNEGO", "NTLM" })
                if (nf.GetValue(n) is not int i || i != 1) { nf.SetValue(n, 1, RegistryValueKind.DWord); changed = true; }
        }
        if (add.Count > 0)
        {
            using var c = Registry.LocalMachine.CreateSubKey(PolicyKey + @"\Certificates");
            if (c.GetValue("ImportEnterpriseRoots") is null) { c.SetValue("ImportEnterpriseRoots", 1, RegistryValueKind.DWord); changed = true; }
        }
        return changed;
    }
}
