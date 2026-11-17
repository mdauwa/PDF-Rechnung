using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfRechnung.Documents;

/// <summary>Ein Bild der globalen Bildverwaltung: Name für die Variable <c>{Bild:Name}</c>, Datei in „mailbilder“, Breite in Pixel (optional).</summary>
public sealed record MailImageEntry(string Name, string File, int? Width = null);

/// <summary>
/// Globale Bilder (Logo, Zertifikate, Banner …), die in Mailtexten und Signaturen über <c>{Bild:Name}</c> bzw.
/// <c>{Bild:Name|Breite}</c> eingebunden werden. Wird ein Bild hier ausgetauscht, gilt das sofort für alle Texte.
/// Die Liste liegt als <c>katalog.json</c> im Ordner „mailbilder“; die Bilddateien selbst sind dieselben wie bei Editor-Bildern.
/// </summary>
public static partial class MailImages
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [GeneratedRegex(@"^[\p{L}0-9_\-]{1,40}$")]
    private static partial Regex NamePattern();

    /// <summary>Variable im Text: {Bild:Name} oder {Bild:Name|Breite}.</summary>
    [GeneratedRegex(@"\{Bild:(?<name>[\p{L}0-9_\-]{1,40})(?:\|(?<width>\d{1,4}))?\}", RegexOptions.IgnoreCase)]
    public static partial Regex Variable();

    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    private static string CatalogPath(string dataDirectory) => Path.Combine(MailHtml.ImageDirectory(dataDirectory), "katalog.json");

    public static List<MailImageEntry> Load(string? dataDirectory)
    {
        if (dataDirectory is null) return new();
        var path = CatalogPath(dataDirectory);
        lock (Gate)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<List<MailImageEntry>>(File.ReadAllText(path), Json) ?? new() : new(); }
            catch { return new(); }
        }
    }

    private static void Save(string dataDirectory, List<MailImageEntry> items)
    {
        var path = CatalogPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(), Json));
        File.Move(tmp, path, true);
    }

    public static MailImageEntry? Find(string? dataDirectory, string name) =>
        Load(dataDirectory).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Bild unter einem Namen ablegen (ersetzt ein vorhandenes gleichen Namens; Breite bleibt, wenn keine angegeben).</summary>
    public static MailImageEntry Put(string dataDirectory, string name, byte[] bytes, int? width)
    {
        if (!ValidName(name)) throw new ArgumentException("Name: nur Buchstaben, Ziffern, _ und - (höchstens 40 Zeichen), keine Leerzeichen.");
        if (bytes.Length > MailHtml.MaxImageBytes) throw new ArgumentException("Das Bild ist größer als 2 MB.");
        var ext = MailHtml.DetectImage(bytes) ?? throw new ArgumentException("Nur PNG-, JPG- oder GIF-Bilder sind möglich.");
        var file = MailHtml.SaveImage(bytes, ext, dataDirectory);
        lock (Gate)
        {
            var items = Load(dataDirectory);
            var old = items.FindIndex(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            var entry = new MailImageEntry(name, file, NormalizeWidth(width) ?? (old >= 0 ? items[old].Width : null));
            if (old >= 0) items[old] = entry; else items.Add(entry);
            Save(dataDirectory, items);
            return entry;
        }
    }

    /// <summary>Umbenennen und/oder Breite ändern. Liefert null, wenn das Bild nicht existiert.</summary>
    public static MailImageEntry? Update(string dataDirectory, string name, string newName, int? width)
    {
        if (!ValidName(newName)) throw new ArgumentException("Name: nur Buchstaben, Ziffern, _ und - (höchstens 40 Zeichen), keine Leerzeichen.");
        lock (Gate)
        {
            var items = Load(dataDirectory);
            var i = items.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            if (!string.Equals(name, newName, StringComparison.OrdinalIgnoreCase)
                && items.Any(x => string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Ein Bild „{newName}“ gibt es schon.");
            items[i] = items[i] with { Name = newName, Width = NormalizeWidth(width) };
            Save(dataDirectory, items);
            return items[i];
        }
    }

    /// <summary>Nur den Eintrag entfernen – die Bilddatei bleibt (sie kann noch direkt in Texten verwendet sein).</summary>
    public static bool Remove(string dataDirectory, string name)
    {
        lock (Gate)
        {
            var items = Load(dataDirectory);
            var n = items.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save(dataDirectory, items);
            return n > 0;
        }
    }

    private static int? NormalizeWidth(int? w) => w is > 0 ? Math.Min(w.Value, 1200) : null;

    /// <summary>
    /// {Bild:Name} im (bereinigten) HTML durch ein Bild ersetzen, das auf die Datei in „mailbilder“ verweist;
    /// <see cref="MailHtml.EmbedImages"/> macht daraus anschließend einen cid:-Anhang. Unbekannte Namen werden entfernt
    /// und in <paramref name="unknown"/> gemeldet.
    /// </summary>
    public static string Insert(string html, string? dataDirectory, ICollection<string>? unknown = null)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("{Bild:", StringComparison.OrdinalIgnoreCase)) return html;
        var items = Load(dataDirectory);
        return Variable().Replace(html, m =>
        {
            var name = m.Groups["name"].Value;
            var e = items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e is null) { if (unknown is not null && !unknown.Contains(name)) unknown.Add(name); return ""; }
            var width = m.Groups["width"].Success && int.TryParse(m.Groups["width"].Value, out var w) && w > 0 ? Math.Min(w, 1200) : e.Width;
            var size = width is { } px ? $" width=\"{px}\"" : "";
            var maxW = width is { } px2 ? $"width:{px2}px;" : "";
            return $"<img src=\"{MailHtml.UrlPrefix}{e.File}\" alt=\"{System.Net.WebUtility.HtmlEncode(e.Name)}\"{size} style=\"border:0;{maxW}max-width:100%;height:auto;\">";
        });
    }

    /// <summary>Für die Textfassung: Bildvariablen entfernen.</summary>
    public static string Strip(string? text) => string.IsNullOrEmpty(text) ? text ?? "" : Variable().Replace(text, "");

    /// <summary>Breite und Höhe eines PNG-, GIF- oder JPG-Bildes (für die Anzeige in der Verwaltung).</summary>
    public static (int W, int H)? Size(byte[] b)
    {
        try
        {
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
                return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (b.Length > 10 && b[0] == (byte)'G')
                return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    var len = (b[i + 2] << 8) | b[i + 3];
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                        return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    i += 2 + len;
                }
            }
        }
        catch { /* unbekannt */ }
        return null;
    }
}
