using System.Collections.Concurrent;
using System.Xml.Linq;
using PdfRechnung.Edi;

namespace PdfRechnung.Connect;

/// <summary>Einstellungen für ERP Suite Connect (Webshop-Schnittstelle). Die Verarbeitung selbst macht ERP Suite Connect.</summary>
public sealed class ConnectOptions
{
    /// <summary>Menüpunkt „ERP Suite Connect“ anzeigen; abgerufene Connect-Dateien (außer Bestellungen) nur dort zeigen.</summary>
    public bool Enabled { get; set; }
    /// <summary>Eingang: Ordner, aus dem ERP Suite Connect die Dateien vom Webshop liest (ORDER_, USERS_, PRODUCTS_ …).</summary>
    public string InFolder { get; set; } = "";
    /// <summary>Verarbeitete Eingangsdateien (Archiv von ERP Suite Connect). Optional.</summary>
    public string InArchive { get; set; } = "";
    /// <summary>Ausgang: Ordner, in den das ERP die Dateien für den Webshop schreibt (Artikelstamm_, Lagerbestand_, Orderstatus_ …).</summary>
    public string OutFolder { get; set; } = "";
    /// <summary>Gesendete Ausgangsdateien. Optional.</summary>
    public string OutArchive { get; set; } = "";
}

public sealed record ConnectFile(string Name, long Size, DateTimeOffset Changed, string? Type, string? TypeTitle, string? Check, string? CheckText);

/// <summary>Dateien in den Connect-Ordnern auflisten und prüfen (Prüfergebnis je Datei zwischengespeichert).</summary>
public static class ConnectFiles
{
    private static readonly ConcurrentDictionary<string, (string Status, string Summary)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Connect-Nachrichtentyp einer Datei (null = keine Connect-Datei).</summary>
    public static string? TypeOf(string name, byte[] data)
    {
        if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !XmlInvoiceValidator.Looks(data)) return null;
        try
        {
            using var ms = new MemoryStream(data);
            var doc = XDocument.Load(ms);
            return ConnectValidator.Detect(name, doc)?.Key;
        }
        catch { return null; }
    }

    public static string? Folder(ConnectOptions o, string area, bool archive) => (area, archive) switch
    {
        ("eingang", false) => o.InFolder,
        ("eingang", true) => o.InArchive,
        ("ausgang", false) => o.OutFolder,
        ("ausgang", true) => o.OutArchive,
        _ => null
    } is { Length: > 0 } f ? f.Trim() : null;

    public static List<ConnectFile> List(string folder, int max = 300)
    {
        if (!Directory.Exists(folder)) return new();
        var s = ConnectSchema.Current;
        return new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(max)
            .Select(f =>
            {
                var t = s.ByName(f.Name);
                var (status, summary) = Checked(f);
                return new ConnectFile(f.Name, f.Length, f.LastWriteTime, t?.Key, t?.Title, status, summary);
            }).ToList();
    }

    private static (string? Status, string? Summary) Checked(FileInfo f)
    {
        var key = $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
        if (Cache.TryGetValue(key, out var c)) return c;
        if (f.Length > 20 * 1024 * 1024) return (null, null);
        try
        {
            var data = File.ReadAllBytes(f.FullName);
            if (!EdiValidation.CanValidate(data)) return (null, null);
            var r = EdiValidation.Validate(data, f.Name);
            if (Cache.Count > 5000) Cache.Clear();
            return Cache[key] = (r.Status, r.Summary);
        }
        catch (IOException) { return (null, "Datei in Benutzung"); }
    }

    /// <summary>Datei im Ordner sicher auflösen (nur der Dateiname, keine Pfade).</summary>
    public static string? Resolve(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains("..")) return null;
        var p = Path.Combine(folder, name);
        return File.Exists(p) ? p : null;
    }
}
