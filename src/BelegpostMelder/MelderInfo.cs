namespace BelegpostMelder;

/// <summary>Version, Betriebssystem und Fähigkeiten dieses Melders (werden dem Server bei jeder Verbindung gemeldet).</summary>
public static class MelderInfo
{
    public static string Version => Application.ProductVersion.Split('+')[0];
    public const string Platform = "windows";
    /// <summary>Auftragsarten bzw. Mailprogramme, die dieser Melder kann.</summary>
    public static readonly string[] Capabilities = { "outlook", "outlookweb", "standard", "speichern", "drucken" };
}
