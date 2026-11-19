using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PdfRechnung.Settings;

/// <summary>
/// Kennwortprüfung gegen das Active Directory (bzw. die lokale Benutzerdatenbank des Servers) über LogonUser
/// (Netzwerkanmeldung – es wird keine Sitzung geöffnet, kein Profil geladen). Der Server muss Mitglied der Domäne sein.
/// </summary>
public static class AdLogin
{
    /// <summary>Domäne des Servers (DNS), leer wenn keine.</summary>
    public static string ServerDomain()
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().DomainName ?? ""; } catch { return ""; }
    }

    /// <summary>„DOMÄNE\name“, „name@domäne“ oder „name“ (+ eingestellte Domäne) → (Benutzer, Domäne) für LogonUser.</summary>
    public static (string User, string? Domain, string Account) Split(string input, string configuredDomain)
    {
        var n = input.Trim();
        var bs = n.IndexOf('\\');
        if (bs > 0) return (n[(bs + 1)..], n[..bs], n);
        if (n.Contains('@')) return (n, null, n); // UPN: Domäne null
        var d = string.IsNullOrWhiteSpace(configuredDomain) ? ServerDomain() : configuredDomain.Trim();
        return (n, d.Length == 0 ? "." : d, d.Length == 0 ? n : $"{d}\\{n}");
    }

    /// <summary>true = Kennwort stimmt. Wirft bei technischen Fehlern (z. B. Domäne nicht erreichbar).</summary>
    public static bool Validate(string input, string password, string configuredDomain, out string account)
    {
        var (user, domain, acc) = Split(input, configuredDomain);
        account = acc;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Die Anmeldung am Active Directory geht nur unter Windows.");
        if (LogonUser(user, domain, password, 3 /* LOGON32_LOGON_NETWORK */, 0 /* DEFAULT */, out var token))
        {
            CloseHandle(token);
            return true;
        }
        var err = Marshal.GetLastWin32Error();
        // 1326 falsches Kennwort/Benutzer, 1327 Kontoeinschränkung, 1330 Kennwort abgelaufen, 1331 deaktiviert, 1909 gesperrt
        if (err is 1326 or 1327 or 1328 or 1329 or 1330 or 1331 or 1909 or 1385) return false;
        throw new Win32Exception(err);
    }

    public static string Reason(int code) => code switch
    {
        1330 => "Das Windows-Kennwort ist abgelaufen.",
        1331 => "Das Windows-Konto ist deaktiviert.",
        1909 => "Das Windows-Konto ist gesperrt.",
        _ => "Benutzername oder Kennwort falsch."
    };

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, string password, int type, int provider, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
