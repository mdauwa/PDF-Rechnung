using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BelegpostMelder;

/// <summary>Einstellungen des Melders je Windows-Benutzer: %APPDATA%\Belegpost\melder.json (Schlüssel per DPAPI geschützt).</summary>
public sealed class MelderConfig
{
    public string? Server { get; set; }
    /// <summary>Zugangsschlüssel, mit DPAPI (CurrentUser) verschlüsselt, Base64.</summary>
    public string? TokenProtected { get; set; }
    /// <summary>SHA-256 des Server-Zertifikats (für selbst signierte Zertifikate).</summary>
    public string? Thumbprint { get; set; }
    public string? PortalTitle { get; set; }
    public string? UserName { get; set; }
    /// <summary>Älterer Schalter („automatisch öffnen“) – wird in <see cref="OnNewDocument"/> übernommen.</summary>
    public bool AutoOpen { get; set; } = true;
    /// <summary>Was passiert bei einem neuen Beleg: "portal" (Ansicht im Browser), "outlook" (Mail-Entwurf in Outlook), "hinweis" (nur Meldung).</summary>
    public string? OnNewDocument { get; set; }
    public string Action => OnNewDocument is "outlook" && !OutlookAllowed ? "portal"
        : OnNewDocument is "portal" or "outlook" or "hinweis" ? OnNewDocument : AutoOpen ? "portal" : "hinweis";
    public long? LastSeq { get; set; }
    public string? LastDocumentPath { get; set; }
    public string? LastDocumentId { get; set; }
    /// <summary>Darf dieser Benutzer über Outlook senden (Freigabe im Portal)?</summary>
    public bool OutlookAllowed { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Belegpost");
    public static string FilePath => Path.Combine(Folder, "melder.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool Paired => !string.IsNullOrEmpty(Server) && !string.IsNullOrEmpty(TokenProtected);

    public string? Token
    {
        get
        {
            if (string.IsNullOrEmpty(TokenProtected)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
        set => TokenProtected = value is null ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
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
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>Inhalt des Kopplungscodes aus dem Portal.</summary>
public sealed record PairingData(string Server, string Token, string? Thumbprint, string? Title, string? User)
{
    /// <summary>Code aus dem Portal oder Link „belegpost-melder:koppeln?c=…“ lesen.</summary>
    public static PairingData? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        var i = s.IndexOf("c=", StringComparison.OrdinalIgnoreCase);
        if (s.StartsWith(Program.UrlScheme + ":", StringComparison.OrdinalIgnoreCase) && i >= 0) s = s[(i + 2)..];
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
