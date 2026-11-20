using System.Text;
using System.Text.Json;

namespace BelegpostMelder;

/// <summary>
/// Einstellungen des Melders je Benutzer: ~/.config/belegpost/melder.json. Die Datei ist nur für den Benutzer lesbar (0600);
/// unter Linux gibt es kein DPAPI, der Zugangsschlüssel liegt deshalb nur durch die Dateirechte geschützt.
/// </summary>
public sealed class MelderConfig
{
    public string? Server { get; set; }
    public string? TokenStored { get; set; }
    public string? Thumbprint { get; set; }
    public string? PortalTitle { get; set; }
    public string? UserName { get; set; }
    /// <summary>"portal", "outlook" (E-Mail öffnen) oder "hinweis".</summary>
    public string? OnNewDocument { get; set; } = "portal";
    public string Action => OnNewDocument is "outlook" && !OutlookAllowed ? "portal" : OnNewDocument is "portal" or "outlook" or "hinweis" ? OnNewDocument : "portal";
    public long? LastSeq { get; set; }
    public string? LastDocumentPath { get; set; }
    public string? LastDocumentId { get; set; }
    public bool OutlookAllowed { get; set; }
    public string MapiMode { get; set; } = "";
    public string? MapiEffectiveText { get; set; }
    public string? MapiInherited { get; set; }
    public string? MapiNote { get; set; }
    public string MailClient { get; set; } = "outlookweb";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "belegpost");
    public static string FilePath => Path.Combine(Folder, "melder.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool Paired => !string.IsNullOrEmpty(Server) && !string.IsNullOrEmpty(TokenStored);

    public string? Token
    {
        get => string.IsNullOrEmpty(TokenStored) ? null : Encoding.UTF8.GetString(Convert.FromBase64String(TokenStored));
        set => TokenStored = value is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    public static MelderConfig Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<MelderConfig>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>Inhalt des Kopplungscodes aus dem Portal (gleiches Format wie beim Windows- und Mac-Melder).</summary>
public sealed record PairingData(string Server, string Token, string? Thumbprint, string? Title, string? User)
{
    public static PairingData? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        var i = s.IndexOf("c=", StringComparison.OrdinalIgnoreCase);
        if (s.StartsWith("belegpost-melder:", StringComparison.OrdinalIgnoreCase) && i >= 0) s = s[(i + 2)..];
        s = Uri.UnescapeDataString(s).Trim().TrimEnd('/');
        try
        {
            var b64 = s.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            var r = doc.RootElement;
            string? Get(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var server = Get("s"); var token = Get("t");
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(token)) return null;
            return new PairingData(server.TrimEnd('/'), token, Get("f"), Get("n"), Get("u"));
        }
        catch { return null; }
    }
}
