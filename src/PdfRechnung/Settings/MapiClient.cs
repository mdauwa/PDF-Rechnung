using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PdfRechnung.Settings;

/// <summary>
/// Installation des Simple-MAPI-Mailclients „Belegpost“ (BelegpostMapi.dll) am ERP-/RDP-Server.
/// 64-Bit-DLL nach System32, 32-Bit-DLL nach SysWOW64; registriert wird ein gemeinsamer Pfad unter System32 –
/// die Dateisystem-Umleitung lädt in 32-Bit-Prozessen (ERP 32 Bit) automatisch die Datei aus SysWOW64.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MapiClient
{
    public const string ClientName = "Belegpost";
    public const string DllName = "BelegpostMapi.dll";
    public const string SourceX64 = "BelegpostMapi-x64.dll", SourceX86 = "BelegpostMapi-x86.dll";
    private const string ClientsKey = @"SOFTWARE\Clients\Mail";
    private const string BackupKey = @"SOFTWARE\Belegpost";

    public static string System64Path => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", DllName);
    public static string System32Path => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", DllName);
    /// <summary>In der Registrierung eingetragener Pfad (für beide Bitbreiten gleich).</summary>
    public static string RegisteredPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", DllName);

    /// <param name="Users">Angemeldete bzw. geladene Windows-Benutzer dieses Servers und ihr eigener Standard (HKCU hat Vorrang vor HKLM).</param>
    /// <param name="PerUserLogon">Active-Setup-Eintrag aktiv: jeder Benutzer bekommt Belegpost bei seiner nächsten Anmeldung als eigenen Standard.</param>
    public sealed record MapiStatus(bool Available, bool Installed, bool DefaultForAll, string? CurrentDefault, bool X64, bool X86, string? Problem,
        IReadOnlyList<MapiUserDefault>? Users = null, bool PerUserLogon = false);

    /// <param name="Value">Eigener Standard des Benutzers (HKCU); leer = er nutzt den Standard für alle.</param>
    public sealed record MapiUserDefault(string Sid, string Account, string? Value, bool Belegpost);

    public static MapiStatus Status()
    {
        var src = Path.GetDirectoryName(ServiceManager.ExecutablePath)!;
        var available = File.Exists(Path.Combine(src, SourceX64)) && File.Exists(Path.Combine(src, SourceX86));
        string? current = null;
        var installed = false;
        try
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = b.OpenSubKey(ClientsKey);
            current = k?.GetValue(null) as string;
            using var c = b.OpenSubKey(ClientsKey + "\\" + ClientName);
            installed = c is not null;
        }
        catch (Exception ex) { return new MapiStatus(available, false, false, null, false, false, ex.Message); }
        var x64 = File.Exists(System64Path);
        var x86 = !Environment.Is64BitOperatingSystem || File.Exists(System32Path);
        var forAll = string.Equals(current, ClientName, StringComparison.OrdinalIgnoreCase);
        var users = new List<MapiUserDefault>();
        foreach (var sid in LoadedUserSids())
        {
            string? value = null;
            try { using var k = Registry.Users.OpenSubKey(sid + "\\" + ClientsKey); value = k?.GetValue(null) as string; } catch { }
            var effective = string.IsNullOrEmpty(value) ? forAll : string.Equals(value, ClientName, StringComparison.OrdinalIgnoreCase);
            users.Add(new MapiUserDefault(sid, AccountName(sid), value, effective));
        }
        return new MapiStatus(available, installed && x64 && x86, forAll, current, x64, x86, null,
            users.OrderBy(u => u.Account, StringComparer.OrdinalIgnoreCase).ToList(), ActiveSetupMode() == "setzen");
    }

    /// <summary>DLLs kopieren und „Belegpost“ als Mailclient registrieren (ändert noch nicht den Standard).</summary>
    public static void Install(Action<string> log)
    {
        var src = Path.GetDirectoryName(ServiceManager.ExecutablePath)!;
        var x64 = Path.Combine(src, SourceX64);
        var x86 = Path.Combine(src, SourceX86);
        if (!File.Exists(x64) || !File.Exists(x86)) throw new FileNotFoundException($"{SourceX64}/{SourceX86} fehlen neben Belegpost.exe.");
        CopyDll(x64, Environment.Is64BitOperatingSystem ? System64Path : System64Path, log);
        if (Environment.Is64BitOperatingSystem) CopyDll(x86, System32Path, log);

        foreach (var view in Views())
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = b.CreateSubKey(ClientsKey + "\\" + ClientName, writable: true);
            k.SetValue(null, ClientName);
            k.SetValue("DLLPath", RegisteredPath);
            k.SetValue("DLLPathEx", RegisteredPath);
            k.SetValue("SupportsMAPI", 1, RegistryValueKind.DWord);
            using var p = k.CreateSubKey("Protocols\\mailto", writable: true);
            p.SetValue(null, "URL:MailTo-Protokoll");
        }
        log("Mailclient „Belegpost“ registriert (64 und 32 Bit).");
    }

    /// <summary>Belegpost für alle Benutzer des Servers als Standard-Mailprogramm setzen (vorheriger Wert wird gemerkt).</summary>
    public static void SetDefaultForAll(Action<string> log)
    {
        if (!Status().Installed) Install(log);
        foreach (var view in Views())
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = b.CreateSubKey(ClientsKey, writable: true);
            var old = k.GetValue(null) as string;
            if (!string.Equals(old, ClientName, StringComparison.OrdinalIgnoreCase))
            {
                using var bk = b.CreateSubKey(BackupKey, writable: true);
                bk.SetValue("MapiVorher", old ?? "");
            }
            k.SetValue(null, ClientName);
        }
        log("Belegpost ist jetzt das Standard-Mailprogramm für MAPI auf diesem Server.");
        SetForUsers(log);
    }

    /// <summary>
    /// Belegpost auch als eigenen Standard jedes Windows-Benutzers setzen (HKCU\Software\Clients\Mail hat Vorrang vor dem
    /// Standard für alle – Outlook trägt sich dort oft selbst ein). Sofort für alle angemeldeten Benutzer, für alle anderen
    /// bei ihrer nächsten Anmeldung (Active Setup). Der bisherige Wert jedes angemeldeten Benutzers wird gemerkt.
    /// </summary>
    public static void SetForUsers(Action<string> log)
    {
        var n = 0;
        foreach (var sid in LoadedUserSids())
        {
            try
            {
                using var k = Registry.Users.CreateSubKey(sid + "\\" + ClientsKey, writable: true);
                var old = k.GetValue(null) as string ?? "";
                if (string.Equals(old, ClientName, StringComparison.OrdinalIgnoreCase)) continue;
                using (var bk = Registry.Users.CreateSubKey(sid + "\\" + BackupKey, writable: true)) bk.SetValue("MapiVorher", old);
                k.SetValue(null, ClientName);
                n++;
                log($"Standard für {AccountName(sid)}: {(old.Length == 0 ? "(keiner)" : old)} → {ClientName}");
            }
            catch (Exception ex) { log($"{AccountName(sid)}: {ex.Message}"); }
        }
        if (n == 0) log("Alle angemeldeten Benutzer haben Belegpost bereits als Standard.");
        SetActiveSetup("setzen", @"%SystemRoot%\System32\reg.exe add ""HKCU\Software\Clients\Mail"" /ve /d ""Belegpost"" /f", log);
        log("Alle anderen Benutzer bekommen Belegpost bei ihrer nächsten Anmeldung am Server als Standard.");
    }

    /// <summary>Vorheriges Standard-Mailprogramm wiederherstellen.</summary>
    public static void RestoreDefault(Action<string> log)
    {
        foreach (var view in Views())
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var bk = b.OpenSubKey(BackupKey);
            var old = bk?.GetValue("MapiVorher") as string ?? "";
            using var k = b.CreateSubKey(ClientsKey, writable: true);
            if (old.Length == 0) k.DeleteValue("", throwOnMissingValue: false);
            else k.SetValue(null, old);
        }
        log("Vorheriges Standard-Mailprogramm wiederhergestellt.");

        // Benutzer: gemerkten Wert zurück; wer Belegpost ohne gemerkten Wert hat, nutzt wieder den Standard für alle
        foreach (var sid in LoadedUserSids())
        {
            try
            {
                using var k = Registry.Users.OpenSubKey(sid + "\\" + ClientsKey, writable: true);
                if (k is null || !string.Equals(k.GetValue(null) as string, ClientName, StringComparison.OrdinalIgnoreCase)) continue;
                using var bk = Registry.Users.OpenSubKey(sid + "\\" + BackupKey, writable: true);
                var old = bk?.GetValue("MapiVorher") as string ?? "";
                if (old.Length == 0) k.DeleteValue("", throwOnMissingValue: false); else k.SetValue(null, old);
                bk?.DeleteValue("MapiVorher", throwOnMissingValue: false);
                log($"Standard für {AccountName(sid)} wiederhergestellt ({(old.Length == 0 ? "Standard für alle" : old)}).");
            }
            catch (Exception ex) { log($"{AccountName(sid)}: {ex.Message}"); }
        }
        // Nicht angemeldete Benutzer: bei der nächsten Anmeldung Belegpost wieder entfernen (nur wenn noch eingetragen)
        SetActiveSetup("entfernen",
            @"%SystemRoot%\System32\cmd.exe /c %SystemRoot%\System32\reg.exe query ""HKCU\Software\Clients\Mail"" /ve | %SystemRoot%\System32\find.exe /i ""Belegpost"" >nul && %SystemRoot%\System32\reg.exe delete ""HKCU\Software\Clients\Mail"" /ve /f", log);
    }

    // ---------- Benutzer dieses Servers ----------

    private const string ActiveSetupKey = @"SOFTWARE\Microsoft\Active Setup\Installed Components\BelegpostMapi";

    /// <summary>Geladene Profile echter Benutzer (HKEY_USERS\S-1-5-21-…, ohne _Classes).</summary>
    private static IEnumerable<string> LoadedUserSids()
    {
        string[] names;
        try { names = Registry.Users.GetSubKeyNames(); } catch { yield break; }
        foreach (var n in names)
            if ((n.StartsWith("S-1-5-21-", StringComparison.Ordinal) || n.StartsWith("S-1-12-1-", StringComparison.Ordinal))
                && !n.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                yield return n;
    }

    private static string AccountName(string sid)
    {
        try { return new System.Security.Principal.SecurityIdentifier(sid).Translate(typeof(System.Security.Principal.NTAccount)).Value; }
        catch { return sid; }
    }

    /// <summary>Active Setup: führt <paramref name="command"/> bei der nächsten Anmeldung jedes Benutzers einmal aus (neue Version = erneut).</summary>
    private static void SetActiveSetup(string mode, string command, Action<string> log)
    {
        using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
        using var k = b.CreateSubKey(ActiveSetupKey, writable: true);
        k.SetValue(null, "Belegpost – Standard-Mailprogramm (MAPI)");
        k.SetValue("StubPath", command, RegistryValueKind.ExpandString);
        k.SetValue("IsInstalled", 1, RegistryValueKind.DWord);
        // Version muss steigen, damit Benutzer, die den Eintrag schon ausgeführt haben, ihn erneut ausführen
        k.SetValue("Version", "1,0," + DateTime.UtcNow.ToString("yyMMdd") + "," + DateTime.UtcNow.ToString("HHmm"));
        k.SetValue("BelegpostModus", mode);
    }

    private static string? ActiveSetupMode()
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
            using var k = b.OpenSubKey(ActiveSetupKey);
            return k?.GetValue("BelegpostModus") as string;
        }
        catch { return null; }
    }

    /// <summary>Befehl, um Belegpost nur für einzelne Benutzer als Standard zu setzen (z. B. per Anmeldeskript/GPO).</summary>
    public const string PerUserCommand = @"reg add ""HKCU\Software\Clients\Mail"" /ve /d ""Belegpost"" /f";

    /// <summary>Beim Dienststart: installierte DLLs aktualisieren, wenn neben der Exe neuere liegen.</summary>
    public static void RefreshIfInstalled(Action<string> log)
    {
        if (!Status().Installed) return;
        var src = Path.GetDirectoryName(ServiceManager.ExecutablePath)!;
        var x64 = Path.Combine(src, SourceX64);
        var x86 = Path.Combine(src, SourceX86);
        if (File.Exists(x64) && !ServiceManager.SameFile(x64, System64Path)) CopyDll(x64, System64Path, log);
        if (Environment.Is64BitOperatingSystem && File.Exists(x86) && !ServiceManager.SameFile(x86, System32Path)) CopyDll(x86, System32Path, log);
    }

    private static IEnumerable<RegistryView> Views() =>
        Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Default };

    private static void CopyDll(string from, string to, Action<string> log)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
            log($"{Path.GetFileName(from)} → {to}");
        }
        catch (IOException)
        {
            // DLL ist gerade in einem ERP-Prozess geladen: daneben ablegen und beim nächsten Neustart ersetzen
            var pending = to + ".neu";
            File.Copy(from, pending, overwrite: true);
            if (!MoveFileEx(pending, to, MoveFileFlags.ReplaceExisting | MoveFileFlags.DelayUntilReboot))
                throw new IOException($"{to} ist in Verwendung und konnte nicht ersetzt werden.");
            log($"{to} ist in Verwendung – wird beim nächsten Neustart des Servers ersetzt.");
        }
    }

    [Flags]
    private enum MoveFileFlags { ReplaceExisting = 1, DelayUntilReboot = 4 }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? newName, MoveFileFlags flags);
}
