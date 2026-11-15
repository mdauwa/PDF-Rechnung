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

    public sealed record MapiStatus(bool Available, bool Installed, bool DefaultForAll, string? CurrentDefault, bool X64, bool X86, string? Problem);

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
        return new MapiStatus(available, installed && x64 && x86, string.Equals(current, ClientName, StringComparison.OrdinalIgnoreCase), current, x64, x86, null);
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
