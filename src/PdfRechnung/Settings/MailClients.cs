namespace PdfRechnung.Settings;

/// <summary>
/// Mailprogramm, in dem „In Outlook senden“ (Portal, Belegpost-Drucker, MAPI-Weg Outlook) die fertige Mail als Entwurf öffnet.
/// </summary>
public static class MailClients
{
    /// <summary>Outlook als Programm (Windows: klassisches Outlook über COM, Mac: Outlook für Mac).</summary>
    public const string Outlook = "outlook";
    /// <summary>Outlook im Web: Belegpost legt den Entwurf über Microsoft 365 im Postfach des Benutzers an und öffnet ihn im Browser.</summary>
    public const string OutlookWeb = "outlookweb";
    /// <summary>Standard-Mailprogramm des Rechners (Windows: Simple MAPI, macOS: Mail-Entwurf des Systems, Linux: xdg-email).</summary>
    public const string Standard = "standard";

    public static readonly string[] All = { Outlook, OutlookWeb, Standard };

    public static bool IsValid(string? s) => string.IsNullOrEmpty(s) || All.Contains(s);

    /// <summary>Wirksames Mailprogramm: Benutzer, sonst globale Vorgabe, sonst Outlook.</summary>
    public static string Effective(PortalUser? u, string? global)
    {
        if (u is not null && All.Contains(u.MailClient)) return u.MailClient;
        return All.Contains(global ?? "") ? global! : Outlook;
    }

    public static string Text(string c) => c switch
    {
        OutlookWeb => "Outlook im Web",
        Standard => "Standard-Mailprogramm",
        _ => "Outlook"
    };
}
