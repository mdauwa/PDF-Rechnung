using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;

namespace PdfRechnung.Documents;

/// <summary>
/// HTML in Mailtext, Signatur und Fußzeile: bereinigen (auch nach Einfügen aus Outlook/Word), Bilder ablegen und einbetten,
/// einheitlich gestalten und eine Textfassung erzeugen.
/// Erlaubt sind nur Auszeichnungen, die Mailprogramme zuverlässig darstellen (Absätze, Schrift, Farben, Links, Bilder, Listen, Tabellen).
/// Alles andere – zweites &lt;html&gt;/&lt;head&gt;/&lt;body&gt;, &lt;style&gt;, Skripte, Office-Tags (o:p), Klassen, mso-Stile – wird entfernt.
/// </summary>
public static partial class MailHtml
{
    /// <summary>Ablage für Bilder in Vorlagen und Signaturen; im Editor erreichbar unter <see cref="UrlPrefix"/>.</summary>
    public static string ImageDirectory(string dataDirectory) => Path.Combine(dataDirectory, "mailbilder");
    public const string UrlPrefix = "api/mailbilder/";
    public const int MaxImageBytes = 2 * 1024 * 1024;

    [GeneratedRegex(@"<\s*/?\s*(p|div|br|span|table|tr|td|img|b|strong|i|em|u|a|font|ul|ol|li|h[1-6]|hr|blockquote|html|body)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();

    /// <summary>Enthält der Text HTML (aus dem Editor) oder ist es einfacher Text mit **fett** usw.?</summary>
    public static bool IsHtml(string? s) => !string.IsNullOrEmpty(s) && TagPattern().IsMatch(s);

    private static readonly string[] Tags =
    {
        "p", "br", "div", "span", "b", "strong", "i", "em", "u", "s", "a", "img", "ul", "ol", "li",
        "table", "thead", "tbody", "tr", "td", "th", "h1", "h2", "h3", "h4", "hr", "blockquote", "sup", "sub", "small"
    };
    private static readonly string[] Attributes =
    {
        "href", "src", "alt", "title", "width", "height", "align", "valign", "colspan", "rowspan", "style",
        "cellpadding", "cellspacing", "border", "target", "bgcolor"
    };
    private static readonly string[] Css =
    {
        "color", "background-color", "font-family", "font-size", "font-weight", "font-style", "text-decoration", "text-align",
        "vertical-align", "line-height", "letter-spacing", "margin", "margin-top", "margin-bottom", "margin-left", "margin-right",
        "padding", "padding-top", "padding-bottom", "padding-left", "padding-right", "border", "border-top", "border-bottom",
        "border-left", "border-right", "border-collapse", "width", "height", "max-width", "display", "white-space"
    };

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear(); foreach (var t in Tags) s.AllowedTags.Add(t);
        s.AllowedAttributes.Clear(); foreach (var a in Attributes) s.AllowedAttributes.Add(a);
        s.AllowedCssProperties.Clear(); foreach (var c in Css) s.AllowedCssProperties.Add(c);
        s.AllowedSchemes.Clear(); foreach (var x in new[] { "http", "https", "mailto", "tel", "cid", "data" }) s.AllowedSchemes.Add(x);
        s.AllowedAtRules.Clear();
        s.AllowedClasses.Clear();
        s.AllowDataAttributes = false;
        s.KeepChildNodes = true; // unbekannte Tags (o:p, font, center …) auspacken, Inhalt behalten
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement el) return;
            // data: nur für Bilder
            if (el.GetAttribute("href") is { } href && href.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)) el.RemoveAttribute("href");
            if (el.LocalName == "img" && el.GetAttribute("src") is not { Length: > 0 }) { el.Remove(); return; }
            if (el.LocalName == "img" && el.GetAttribute("src") is { } src)
            {
                var t = src.Trim();
                var ok = t.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase) || t.Contains("/" + UrlPrefix, StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(t, @"^data:image/(png|jpe?g|gif);base64,", RegexOptions.IgnoreCase);
                if (!ok) el.Remove(); // file:///…clip_image001.png aus Outlook usw. – nicht erreichbar
            }
            if (el.LocalName == "a" && el.HasAttribute("href")) el.SetAttribute("target", "_blank");
        };
        return s;
    }

    [ThreadStatic] private static HtmlSanitizer? _sanitizer;
    private static HtmlSanitizer Sanitizer => _sanitizer ??= CreateSanitizer();

    /// <summary>
    /// HTML bereinigen. Nimmt nur den Inhalt eines eingefügten Dokuments (kein zweites &lt;html&gt;/&lt;head&gt;/&lt;body&gt;),
    /// wandelt &lt;font&gt; in Stile um, entfernt Office-Reste und leere Elemente.
    /// </summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html.Replace("\r\n", "\n");
        // Kommentare inkl. bedingter Office-Kommentare <!--[if gte mso 9]> … <![endif]-->
        s = Regex.Replace(s, @"<!--\[if[\s\S]*?<!\[endif\]-->", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<!--[\s\S]*?-->", "");
        // Nur den Body eines vollständigen Dokuments
        var body = Regex.Match(s, @"<body[^>]*>([\s\S]*)</body>", RegexOptions.IgnoreCase);
        if (body.Success) s = body.Groups[1].Value;
        s = Regex.Replace(s, @"<(head|style|script|title|xml|noscript|iframe|object)\b[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(meta|link|base)\b[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</?(html|body)\b[^>]*>", "", RegexOptions.IgnoreCase);
        s = FontToSpan(s);
        s = Sanitizer.Sanitize(s);
        s = Regex.Replace(s, @"\s*mso-[a-z\-]+\s*:[^;""]*;?", "", RegexOptions.IgnoreCase);
        // Farben als #rrggbb – Outlook (Word-Engine) kennt kein rgba()
        s = Regex.Replace(s, @"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)", m =>
            "#" + string.Concat(Enumerable.Range(1, 3).Select(k => Math.Clamp(int.Parse(m.Groups[k].Value), 0, 255).ToString("x2"))));
        s = Regex.Replace(s, @"(style=""[^""]*"")", m => Regex.Replace(m.Value, @":\s+", ":").Replace("; ", ";"));
        s = Regex.Replace(s, @"\sstyle=""\s*""", "");
        // leere Spans/Divs entfernen, mehr als zwei Leerzeilen zusammenfassen
        for (var i = 0; i < 3; i++) s = Regex.Replace(s, @"<span>\s*</span>|<span>([^<]*)</span>", m => m.Groups[1].Value);
        s = Regex.Replace(s, @"(<p>(\s|&nbsp;|<br>)*</p>\s*){2,}", "<p>&nbsp;</p>");
        return s.Trim();
    }

    private static string FontToSpan(string s) =>
        Regex.Replace(s, @"<font\b([^>]*)>", m =>
        {
            var a = m.Groups[1].Value;
            var styles = new List<string>();
            if (Regex.Match(a, @"color\s*=\s*[""']?([#\w]+)", RegexOptions.IgnoreCase) is { Success: true } c) styles.Add("color:" + c.Groups[1].Value);
            if (Regex.Match(a, @"face\s*=\s*[""']([^""']+)", RegexOptions.IgnoreCase) is { Success: true } f) styles.Add("font-family:" + f.Groups[1].Value);
            if (Regex.Match(a, @"size\s*=\s*[""']?([1-7])", RegexOptions.IgnoreCase) is { Success: true } z)
                styles.Add("font-size:" + (z.Groups[1].Value switch { "1" => "10px", "2" => "13px", "3" => "15px", "4" => "18px", "5" => "24px", "6" => "32px", _ => "40px" }));
            return styles.Count > 0 ? $"<span style=\"{string.Join(";", styles)}\">" : "<span>";
        }, RegexOptions.IgnoreCase).Replace("</font>", "</span>", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Zum Speichern (Vorlagen, Signaturen): HTML bereinigen und eingefügte data:-Bilder als Dateien ablegen
    /// (Verweis <c>api/mailbilder/…</c>). Text ohne HTML bleibt unverändert.
    /// </summary>
    public static string ForStorage(string? value, string dataDirectory)
    {
        if (!IsHtml(value)) return value ?? "";
        var clean = Sanitize(value);
        clean = Regex.Replace(clean, @"src=""(data:image/(png|jpe?g|gif);base64,([A-Za-z0-9+/=\s]+))""", m =>
        {
            try
            {
                var bytes = Convert.FromBase64String(m.Groups[3].Value);
                if (bytes.Length > MaxImageBytes) return "src=\"\"";
                var ext = m.Groups[2].Value.ToLowerInvariant() == "jpeg" ? "jpg" : m.Groups[2].Value.ToLowerInvariant();
                return $"src=\"{UrlPrefix}{SaveImage(bytes, ext, dataDirectory)}\"";
            }
            catch { return "src=\"\""; }
        }, RegexOptions.IgnoreCase);
        return Regex.Replace(clean, @"<img[^>]*src=""""[^>]*>", "");
    }

    /// <summary>Bild ablegen (Name = Prüfsumme, gleiche Bilder nur einmal). Liefert den Dateinamen.</summary>
    public static string SaveImage(byte[] bytes, string ext, string dataDirectory)
    {
        var dir = ImageDirectory(dataDirectory);
        Directory.CreateDirectory(dir);
        var name = Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant() + "." + ext;
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return name;
    }

    /// <summary>Bildtyp anhand der ersten Bytes (png, jpg, gif) – null, wenn kein unterstütztes Bild.</summary>
    public static string? DetectImage(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "jpg"
        : b.Length > 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' ? "gif" : null;

    /// <summary>
    /// Für die Mail: Text oder HTML → bereinigtes HTML mit einheitlichen Abständen und Linkfarbe;
    /// Platzhalterwerte werden in HTML sicher maskiert.
    /// </summary>
    public static string ToMailHtml(string? content, IReadOnlyDictionary<string, string> values, string linkColor)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        if (!IsHtml(content)) return MailDesigner.Format(Placeholders.Apply(content, values), linkColor);
        var escaped = values.ToDictionary(kv => kv.Key, kv => WebUtility.HtmlEncode(kv.Value), StringComparer.OrdinalIgnoreCase);
        var html = Sanitize(Placeholders.Apply(content, escaped));
        // einheitlich: Absätze mit festem Abstand, Links in der Hauptfarbe, Bilder ohne Rahmen
        // Absätze wie in Outlook ohne Abstand – eine Leerzeile ist ein leerer Absatz
        html = Regex.Replace(html, @"<p(?=[\s>])(?![^>]*style=)", "<p style=\"margin:0;\"");
        html = Regex.Replace(html, @"<p style=""(?![^""]*margin)", "<p style=\"margin:0;");
        html = Regex.Replace(html, @"<a (?![^>]*style=)", $"<a style=\"color:{linkColor};\" ");
        html = Regex.Replace(html, @"<img (?![^>]*style=)", "<img style=\"border:0;max-width:100%;height:auto;\" ");
        html = Regex.Replace(html, @"<(ul|ol)(?=[\s>])(?![^>]*style=)", "<$1 style=\"margin:0 0 10px 0;padding-left:22px;\"");
        return html;
    }

    /// <summary>
    /// Bilder einbetten: Verweise auf <c>api/mailbilder/…</c> und data:-Bilder werden zu cid:-Anhängen,
    /// damit sie in jedem Mailprogramm ohne Nachladen erscheinen.
    /// </summary>
    public static string EmbedImages(string html, string? dataDirectory, List<InlineImage> images)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var n = images.Count;
        html = Regex.Replace(html, @"src=""(?:[^""]*/)?" + Regex.Escape(UrlPrefix) + @"([A-Za-z0-9_\-]+\.(png|jpg|jpeg|gif))""", m =>
        {
            if (dataDirectory is null) return m.Value;
            var file = Path.GetFileName(m.Groups[1].Value);
            var path = Path.Combine(ImageDirectory(dataDirectory), file);
            if (!File.Exists(path)) return "src=\"\"";
            var cid = $"bild{file.Split('.')[0][..8]}@belegpost";
            if (!images.Any(i => i.ContentId == cid))
                images.Add(new InlineImage(cid, file, File.ReadAllBytes(path), MailDesigner.ImageType(path)));
            return $"src=\"cid:{cid}\"";
        }, RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"src=""data:image/(png|jpe?g|gif);base64,([A-Za-z0-9+/=\s]+)""", m =>
        {
            try
            {
                var bytes = Convert.FromBase64String(m.Groups[2].Value);
                var cid = $"bild{++n}@belegpost";
                var ext = m.Groups[1].Value.ToLowerInvariant();
                images.Add(new InlineImage(cid, "bild" + n + "." + ext, bytes, "image/" + (ext == "jpg" ? "jpeg" : ext)));
                return $"src=\"cid:{cid}\"";
            }
            catch { return "src=\"\""; }
        }, RegexOptions.IgnoreCase);
        return Regex.Replace(html, @"<img[^>]*src=""""[^>]*>", "");
    }

    /// <summary>Textfassung (text/plain) aus HTML: Absätze und Zeilenumbrüche bleiben, Links als „Text (Adresse)“.</summary>
    public static string ToText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        if (!IsHtml(content)) return content.Replace("**", "");
        var s = content.Replace("\r\n", "\n").Replace("\n", " ");
        s = Regex.Replace(s, @"<a\b[^>]*href=""(?:mailto:|tel:)?([^""]+)""[^>]*>([\s\S]*?)</a>", m =>
        {
            var text = Regex.Replace(m.Groups[2].Value, "<[^>]+>", "").Trim();
            var url = m.Groups[1].Value;
            return text.Length == 0 || WebUtility.HtmlDecode(text) == url ? url : $"{text} ({url})";
        }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|h[1-6]|li|tr|table|blockquote)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "- ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</t[dh]>", "  ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<img[^>]*alt=""([^""]+)""[^>]*>", "[$1]", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return string.Join("\n", s.Split('\n').Select(l => l.Trim())).Trim();
    }
}
