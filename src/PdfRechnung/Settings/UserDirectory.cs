using System.DirectoryServices;
using System.Runtime.Versioning;

namespace PdfRechnung.Settings;

/// <summary>
/// Angaben zu einem Windows-Konto für das automatische Anlegen von Benutzern: zuerst aus dem Active Directory
/// (Anzeigename, E-Mail, UPN), sonst aus der lokalen Benutzerverwaltung des Servers (vollständiger Name).
/// Der Dienst fragt als Computerkonto (SYSTEM) – das darf in einer Domäne standardmäßig lesen.
/// </summary>
public static class UserDirectory
{
    public sealed record Info(string Account, string Name, string? Email, string? Upn, string Source);

    /// <summary>Konto „DOMÄNE\name“ (oder „name“). Fällt nie aus – im schlechtesten Fall bleibt der Kontoname als Name.</summary>
    public static Info Lookup(string account, string configuredDomain = "")
    {
        var shortName = UserStore.Short(account);
        var domain = account.Contains('\\') ? account[..account.IndexOf('\\')] : configuredDomain;
        if (OperatingSystem.IsWindows())
        {
            var isLocal = string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase) || domain is "." or "";
            if (!isLocal || !string.IsNullOrWhiteSpace(configuredDomain))
                try { if (FromAd(account, shortName, string.IsNullOrWhiteSpace(configuredDomain) ? null : configuredDomain.Trim()) is { } ad) return ad; }
                catch { /* AD nicht erreichbar → lokal */ }
            try { if (FromLocal(account, shortName) is { } local) return local; }
            catch { /* kein lokales Konto */ }
        }
        return new Info(account, shortName, null, null, "Anmeldename");
    }

    [SupportedOSPlatform("windows")]
    private static Info? FromAd(string account, string shortName, string? domain)
    {
        using var root = domain is null ? new DirectoryEntry() : new DirectoryEntry("LDAP://" + domain);
        using var search = new DirectorySearcher(root)
        {
            Filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={Escape(shortName)}))",
            ClientTimeout = TimeSpan.FromSeconds(5), ServerTimeLimit = TimeSpan.FromSeconds(5), SizeLimit = 1
        };
        foreach (var p in new[] { "displayName", "givenName", "sn", "mail", "userPrincipalName" }) search.PropertiesToLoad.Add(p);
        var r = search.FindOne();
        if (r is null) return null;
        string? P(string n) => r.Properties[n] is { Count: > 0 } v ? v[0]?.ToString()?.Trim() : null;
        var name = P("displayName") is { Length: > 0 } dn ? dn
            : string.Join(" ", new[] { P("givenName"), P("sn") }.Where(x => !string.IsNullOrEmpty(x))) is { Length: > 0 } gn ? gn : shortName;
        return new Info(account, name, P("mail"), P("userPrincipalName"), "Active Directory");
    }

    [SupportedOSPlatform("windows")]
    private static Info? FromLocal(string account, string shortName)
    {
        using var e = new DirectoryEntry($"WinNT://{Environment.MachineName}/{shortName},user");
        var full = e.Properties["FullName"]?.Value?.ToString()?.Trim();
        return new Info(account, string.IsNullOrEmpty(full) ? shortName : full, null, null, "lokale Benutzer des Servers");
    }

    /// <summary>LDAP-Filterwert maskieren (RFC 4515).</summary>
    public static string Escape(string v) =>
        string.Concat(v.Select(c => c switch { '\\' => "\\5c", '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\0' => "\\00", _ => c.ToString() }));
}
