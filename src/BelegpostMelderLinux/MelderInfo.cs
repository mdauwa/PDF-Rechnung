using System.Reflection;

namespace BelegpostMelder;

/// <summary>Version, Betriebssystem und Fähigkeiten dieses Melders (werden dem Server bei jeder Verbindung gemeldet).</summary>
public static class MelderInfo
{
    public static string Version =>
        (typeof(MelderInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0").Split('+')[0];
    public const string Platform = "linux";
    /// <summary>Kein klassisches Outlook unter Linux: Outlook im Web, Standard-Mailprogramm (xdg-email), Speichern, Drucken (CUPS).</summary>
    public static readonly string[] Capabilities = { "outlookweb", "standard", "speichern", "drucken" };
}
