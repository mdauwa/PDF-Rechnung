using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;

namespace PdfRechnung.Settings;

public sealed record ServiceStatus(
    bool IsWindows, bool RunningAsService, bool Installed, string? State, bool Elevated,
    string Executable, string DataDirectory, string Version, string MachineName);

/// <summary>
/// Installation und Steuerung des Windows-Dienstes – ohne PowerShell, über sc.exe und netsh.
/// Aufruf aus der Oberfläche; fehlen Administratorrechte, startet sich das Programm selbst mit UAC-Abfrage neu
/// (Befehlszeile: Belegpost.exe --install | --uninstall).
/// </summary>
public static class ServiceManager
{
    /// <summary>Interner Dienstname (sc.exe, Registry).</summary>
    public const string ServiceName = "Belegpost";
    /// <summary>Anzeigename in der Diensteverwaltung.</summary>
    public const string DisplayName = "Belegpost";
    private const string FirewallRule = "Belegpost";
    /// <summary>Name vor der Umbenennung – wird bei der Installation entfernt.</summary>
    private const string LegacyServiceName = "PdfRechnung", LegacyFirewallRule = "PDF Rechnung";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Belegpost.exe");

    /// <summary>
    /// Lokaler Programmordner des Dienstes. Der Dienst läuft als SYSTEM und sieht keine verbundenen Netzlaufwerke
    /// (z. B. Y:) – daher wird das Programm bei der Installation hierher kopiert.
    /// </summary>
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Belegpost");

    /// <summary>Pfad der EXE, die als Dienst eingetragen ist (aus der Registry), oder null.</summary>
    [SupportedOSPlatform("windows")]
    public static string? ServiceExecutable()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        var image = key?.GetValue("ImagePath") as string;
        if (string.IsNullOrWhiteSpace(image)) return null;
        image = Environment.ExpandEnvironmentVariables(image.Trim());
        return image.StartsWith('"') ? image[1..image.IndexOf('"', 1)] : image.Split(' ')[0];
    }

    /// <summary>true, wenn beide Dateien existieren und inhaltlich gleich sind.</summary>
    public static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
            return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
        }
        catch { return false; }
    }

    /// <summary>
    /// Kopiert das Programm (EXE, wwwroot, sql, Konfiguration) in den lokalen Programmordner und liefert den Pfad der
    /// dortigen EXE. Läuft das Programm bereits von dort, wird nichts kopiert. Ein laufender Dienst wird vorher gestoppt.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string Deploy(Action<string> log)
    {
        var src = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var dst = Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.Combine(dst, "Belegpost.exe");
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return ExecutablePath;

        if (IsInstalled() && IsRunning())
        {
            log("Beende laufenden Dienst für die Aktualisierung …");
            Sc($"stop {ServiceName}", log, ignoreErrors: true);
            try
            {
                using var sc = new ServiceController(ServiceName);
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
            catch (System.ServiceProcess.TimeoutException) { log("Dienst reagiert nicht – versuche trotzdem zu kopieren."); }
        }

        log($"Kopiere Programm nach {dst} …");
        Directory.CreateDirectory(dst);
        CopyWithRetry(ExecutablePath, target);
        foreach (var f in Directory.EnumerateFiles(src).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".config", StringComparison.OrdinalIgnoreCase)))
            CopyWithRetry(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var dir in new[] { "wwwroot", "sql" })
        {
            var from = Path.Combine(src, dir);
            if (!Directory.Exists(from)) continue;
            var to = Path.Combine(dst, dir);
            if (Directory.Exists(to)) Directory.Delete(to, recursive: true);   // veraltete Dateien entfernen
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(from, f);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, rel))!);
                File.Copy(f, Path.Combine(to, rel), overwrite: true);
            }
        }
        log("Programmdateien kopiert.");
        return target;
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var i = 0; ; i++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(1000); }   // Datei kurz nach dem Dienststopp noch gesperrt
        }
    }

    public static ServiceStatus GetStatus(bool runningAsService, string dataDir)
    {
        string? state = null;
        var installed = false;
        var elevated = false;
        if (OperatingSystem.IsWindows())
        {
            elevated = IsElevated();
            try
            {
                using var sc = ServiceController.GetServices().FirstOrDefault(s =>
                    s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));
                if (sc is not null) { installed = true; state = sc.Status.ToString(); }
            }
            catch { /* ohne Rechte */ }
        }
        return new ServiceStatus(OperatingSystem.IsWindows(), runningAsService, installed, state, elevated,
            ExecutablePath, dataDir, typeof(ServiceManager).Assembly.GetName().Version?.ToString(3) ?? "",
            Environment.MachineName);
    }

    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    public static bool IsInstalled()
    {
        try { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    public static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    /// <summary>
    /// Startet Belegpost.exe mit Administratorrechten (UAC-Abfrage). Liefert false, wenn der Benutzer abbricht.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool RunElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExecutablePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // vom Benutzer abgebrochen
        {
            return false;
        }
    }

    /// <summary>Legt den Dienst an (bzw. aktualisiert ihn), setzt Wiederherstellung, Firewall und Ordnerrechte.</summary>
    [SupportedOSPlatform("windows")]
    public static void Install(SettingsStore store, ServerOptions server, Action<string> log, string? executable = null)
    {
        var exe = executable ?? ExecutablePath;
        var bin = $"\"{exe}\"";
        RemoveLegacy(log);
        if (IsInstalled())
        {
            log("Dienst ist bereits vorhanden – Pfad wird aktualisiert.");
            Sc($"config {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= delayed-auto DisplayName= \"{DisplayName}\"", log);
        }
        else
        {
            Sc($"create {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= delayed-auto DisplayName= \"{DisplayName}\"", log);
        }
        Sc($"description {ServiceName} \"Belegpost – Druckdienst und Versand-Portal für Belege und E-Rechnungen\"", log);
        // Bei Absturz oder Neustart-Anforderung (Exitcode ≠ 0) automatisch neu starten
        Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000", log);
        Sc($"failureflag {ServiceName} 1", log);

        UpdateFirewall(server, log, exe);
        try
        {
            store.RestrictAccess();
            log("Zugriff auf das Datenverzeichnis auf Administratoren und SYSTEM beschränkt.");
        }
        catch (Exception ex) { log("Ordnerrechte konnten nicht gesetzt werden: " + ex.Message); }

        Sc($"start {ServiceName}", log, ignoreErrors: true);
    }

    /// <summary>Setzt den Anzeigenamen, falls ein älterer Stand noch einen anderen eingetragen hat (beim Dienststart).</summary>
    [SupportedOSPlatform("windows")]
    public static void EnsureDisplayName(Action<string> log)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        if (key is null || key.GetValue("DisplayName") as string == DisplayName) return;
        Sc($"config {ServiceName} DisplayName= \"{DisplayName}\"", log);
        log($"Anzeigename des Dienstes auf „{DisplayName}“ gesetzt.");
    }

    /// <summary>Dienst und Firewall-Regel unter dem alten Namen „PdfRechnung“ entfernen (Umbenennung in Belegpost).</summary>
    [SupportedOSPlatform("windows")]
    private static void RemoveLegacy(Action<string> log)
    {
        bool exists;
        try { exists = ServiceController.GetServices().Any(s => s.ServiceName.Equals(LegacyServiceName, StringComparison.OrdinalIgnoreCase)); }
        catch { exists = false; }
        if (!exists) return;
        log($"Alten Dienst „{LegacyServiceName}“ gefunden – er wird durch „{ServiceName}“ ersetzt.");
        Sc($"stop {LegacyServiceName}", log, ignoreErrors: true);
        try
        {
            using var sc = new ServiceController(LegacyServiceName);
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch { }
        Sc($"delete {LegacyServiceName}", log, ignoreErrors: true);
        Run("netsh", $"advfirewall firewall delete rule name=\"{LegacyFirewallRule}\"", log, ignoreErrors: true);
        var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), LegacyServiceName);
        try { if (Directory.Exists(oldDir)) { Directory.Delete(oldDir, true); log($"Alten Programmordner {oldDir} entfernt."); } }
        catch (Exception ex) { log($"Alter Programmordner {oldDir} konnte nicht entfernt werden ({ex.Message}) – bitte später von Hand löschen."); }
    }

    [SupportedOSPlatform("windows")]
    public static void Uninstall(Action<string> log)
    {
        // Reihenfolge wichtig, falls der Dienst sich selbst entfernt: erst löschen/Firewall, dann stoppen.
        Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", log, ignoreErrors: true);
        Sc($"delete {ServiceName}", log, ignoreErrors: true);
        Sc($"stop {ServiceName}", log, ignoreErrors: true);
    }

    /// <summary>Firewall-Regel für die konfigurierten Ports (Domänen- und privates Netz) – wird bei jedem Dienststart abgeglichen.</summary>
    [SupportedOSPlatform("windows")]
    public static void UpdateFirewall(ServerOptions server, Action<string> log, string? executable = null)
    {
        var ports = server.EnableHttp ? $"{server.HttpsPort},{server.HttpPort}" : $"{server.HttpsPort}";
        Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", _ => { }, ignoreErrors: true);
        Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP " +
                     $"localport={ports} profile=domain,private program=\"{executable ?? ExecutablePath}\"", log, ignoreErrors: true);
    }

    /// <summary>Wartet, bis ein Prozess beendet ist (z. B. die Einrichtungs-Instanz, die den Port noch belegt).</summary>
    public static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(timeout);
        }
        catch (ArgumentException) { /* bereits beendet */ }
    }

    public static void OpenBrowser(string url)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* egal */ }
    }

    private static void Sc(string args, Action<string> log, bool ignoreErrors = false) =>
        Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), args, log, ignoreErrors);

    private static void Run(string file, string args, Action<string> log, bool ignoreErrors)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} konnte nicht gestartet werden.");
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        var name = Path.GetFileNameWithoutExtension(file);
        if (p.ExitCode != 0 && !ignoreErrors)
            throw new InvalidOperationException($"{name} {args} → Fehler {p.ExitCode}: {output}");
        log($"{name} {args.Split(' ')[0]}: {(p.ExitCode == 0 ? "OK" : output)}");
    }
}
