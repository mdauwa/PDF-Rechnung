using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfRechnung.Documents;

/// <summary>
/// Platzhalter in Betreff, Mailtext, Signatur und Fußzeile.
///   {Name}             → Wert aus @@pvName=Wert@@ oder eingebaut (Belegnummer, Kunde …)
///   {Name|Ersatztext}  → Ersatztext, wenn kein Wert übergeben wurde (z. B. {Anrede|Sehr geehrte Damen und Herren})
/// Platzhalter ohne Wert und ohne Ersatztext werden leer ersetzt und als Warnung gemeldet.
/// </summary>
public static partial class Placeholders
{
    [GeneratedRegex(@"\{(?<name>[\p{L}0-9_\-]{1,40})(?:\|(?<fallback>[^{}]*))?\}")]
    private static partial Regex Pattern();

    /// <summary>Eingebaute Platzhalter (immer verfügbar).</summary>
    public static readonly string[] BuiltIn = { "Belegart", "Belegnummer", "Belegdatum", "Kunde", "Kundennummer", "Betrag", "Waehrung", "Dateiname", "Absender", "Datum" };

    public static Dictionary<string, string> Build(Vorgang v, IReadOnlyDictionary<string, string>? custom, string? fileName = null, string? sender = null)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Belegart"] = v.DocumentType ?? "",
            ["Belegnummer"] = v.VoucherId ?? "",
            ["Belegdatum"] = v.VoucherDate ?? "",
            ["Kunde"] = v.BuyerName ?? "",
            ["Kundennummer"] = v.BuyerId ?? "",
            ["Betrag"] = v.GrossAmount is { } g ? g.ToString("N2", CultureInfo.GetCultureInfo("de-AT")) : "",
            ["Waehrung"] = v.Currency ?? "",
            ["Dateiname"] = fileName ?? "",
            ["Absender"] = sender ?? "",
            ["Datum"] = DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-AT"))
        };
        d["Währung"] = d["Waehrung"];
        if (custom is not null)
            foreach (var (k, val) in custom) d[k] = val;
        return d;
    }

    /// <summary>Ersetzt alle Platzhalter. Nicht gefundene Namen landen in <paramref name="missing"/>.</summary>
    public static string Apply(string? template, IReadOnlyDictionary<string, string> values, ICollection<string>? missing = null)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return Pattern().Replace(template.Replace("\r\n", "\n"), m =>
        {
            var name = m.Groups["name"].Value;
            if (values.TryGetValue(name, out var val) && !string.IsNullOrWhiteSpace(val)) return val;
            if (m.Groups["fallback"].Success) return m.Groups["fallback"].Value;
            if (values.ContainsKey(name)) return ""; // bekannt, aber leer
            if (missing is not null && !missing.Contains(name)) missing.Add(name);
            return "";
        });
    }

    /// <summary>Alle verwendeten Platzhalternamen (für die Vorschau in den Einstellungen).</summary>
    public static IEnumerable<string> Names(string? text) =>
        string.IsNullOrEmpty(text) ? Enumerable.Empty<string>() : Pattern().Matches(text).Select(m => m.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Fertig gestaltete Mail: HTML, Textfassung und eingebettete Bilder (Logo).</summary>
public sealed record RenderedMail(string Html, string Text, List<InlineImage> Images);
public sealed record InlineImage(string ContentId, string Name, byte[] Data, string ContentType);

/// <summary>
/// Baut aus Mailtext + Vorlage eine HTML-Mail im CI des Kunden.
/// Der Aufbau ist bewusst tabellenbasiert mit Inline-Styles – so sieht die Mail auch in Outlook (Word-Engine) richtig aus.
/// Im Mailtext sind einfache Auszeichnungen möglich: **fett**, *kursiv*, Links (https://…, mailto) werden klickbar,
/// Leerzeile = neuer Absatz, Zeilen mit „- “ werden zur Aufzählung.
/// </summary>
public static partial class MailDesigner
{
    public static string Directory(string dataDirectory) => Path.Combine(dataDirectory, "mailvorlagen");

    /// <summary>
    /// Vorlage wählen: @@pt@@ → Vorlage, die dem Briefpapier zugeordnet ist → Vorlage des Absenders → Standard.
    /// </summary>
    public static (MailDesign? Design, string? Reason, string? Warning) Select(MailTemplateOptions o, string? requested, string? stationery, string? sender)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var r = requested.Trim();
            var hit = o.Items.FirstOrDefault(x => string.Equals(x.Name, r, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return (hit, $"Steuerzeichen @@pt{r}@@", null);
            var fallback = Select(o, null, stationery, sender);
            return (fallback.Design, fallback.Reason, $"Mail-Vorlage „{r}“ (@@pt@@) gibt es nicht – {(fallback.Design is null ? "einfache Mail" : $"„{fallback.Design.Name}“")} wird verwendet.");
        }
        if (!string.IsNullOrWhiteSpace(stationery))
        {
            var hit = o.Items.FirstOrDefault(x => x.Stationeries.Any(s => string.Equals(s.Trim(), stationery, StringComparison.OrdinalIgnoreCase)));
            if (hit is not null) return (hit, $"Briefpapier „{stationery}“", null);
        }
        if (!string.IsNullOrWhiteSpace(sender))
        {
            var hit = o.Items.FirstOrDefault(x => x.Senders.Any(s => string.Equals(s.Trim(), sender.Trim(), StringComparison.OrdinalIgnoreCase))
                                               || string.Equals(x.FromAddress.Trim(), sender.Trim(), StringComparison.OrdinalIgnoreCase) && x.FromAddress.Length > 0);
            if (hit is not null) return (hit, $"Absender {sender}", null);
        }
        if (!string.IsNullOrWhiteSpace(o.Default))
        {
            var hit = o.Items.FirstOrDefault(x => string.Equals(x.Name, o.Default, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return (hit, "Standardvorlage", null);
        }
        return (null, null, null);
    }

    public static RenderedMail Render(MailDesign? d, string body, IReadOnlyDictionary<string, string> values, string? logoDirectory, MailFacts? facts = null)
    {
        body = (body ?? "").Replace("\r\n", "\n").Trim('\n');
        if (d is null) return new RenderedMail(Simple(body), body, new());

        var images = new List<InlineImage>();
        string logoHtml = "";
        if (!string.IsNullOrWhiteSpace(d.LogoFile) && logoDirectory is not null)
        {
            var path = Path.Combine(logoDirectory, Path.GetFileName(d.LogoFile));
            if (File.Exists(path))
            {
                var cid = "logo@belegpost";
                images.Add(new InlineImage(cid, Path.GetFileName(path), File.ReadAllBytes(path), ImageType(path)));
                var w = Math.Clamp(d.LogoWidth, 40, 560);
                logoHtml = $"<img src=\"cid:{cid}\" width=\"{w}\" alt=\"{Html(values.GetValueOrDefault("Absender") ?? "Logo")}\" style=\"display:block;border:0;width:{w}px;max-width:100%;height:auto\">";
            }
        }

        var signature = Placeholders.Apply(d.Signature, values);
        var footer = Placeholders.Apply(d.Footer, values);
        var header = Placeholders.Apply(d.HeaderText, values);
        var font = CssValue(d.FontFamily, "Segoe UI, Arial, Helvetica, sans-serif");
        var primary = Color(d.PrimaryColor, "#1f5fbf");
        var bg = Color(d.BackgroundColor, "#f4f5f7");
        var fg = Color(d.TextColor, "#1c2330");
        var bodyHtml = Format(body, primary);
        var sigHtml = signature.Length > 0 ? Format(signature, primary) : "";
        var footHtml = footer.Length > 0 ? Format(footer, primary) : "";
        var boxHtml = d.ShowDocumentBox && facts is not null ? Box(facts, primary, fg) : "";
        var text = string.Join("\n\n", new[] { header, body, signature, StripMarkup(footer) }.Where(s => s.Length > 0));

        if (d.Kind == "html" && !string.IsNullOrWhiteSpace(d.CustomHtml))
        {
            var htmlValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in values) htmlValues[k] = Html(v);
            htmlValues["Inhalt"] = bodyHtml; htmlValues["Logo"] = logoHtml; htmlValues["Signatur"] = sigHtml;
            htmlValues["Fusszeile"] = footHtml; htmlValues["Fußzeile"] = footHtml; htmlValues["Kopfzeile"] = Html(header);
            htmlValues["Belegbox"] = boxHtml; htmlValues["Farbe"] = primary;
            return new RenderedMail(Placeholders.Apply(d.CustomHtml, htmlValues), text, images);
        }

        var align = d.LogoAlign is "center" or "right" ? d.LogoAlign : "left";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<meta name=\"color-scheme\" content=\"light\"><title>").Append(Html(header)).Append("</title></head>");
        sb.Append($"<body style=\"margin:0;padding:0;background:{bg};\">");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:{bg};\"><tr><td align=\"center\" style=\"padding:24px 12px;\">");
        sb.Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:600px;max-width:100%;background:#ffffff;border-radius:6px;\">");
        if (logoHtml.Length > 0)
            sb.Append($"<tr><td align=\"{align}\" style=\"padding:24px 32px 8px 32px;\">{logoHtml.Replace("display:block;", align == "left" ? "display:block;" : "display:inline-block;")}</td></tr>");
        if (header.Length > 0)
            sb.Append($"<tr><td style=\"padding:{(logoHtml.Length > 0 ? 16 : 0)}px 0 0 0;\"><div style=\"background:{primary};color:#ffffff;font-family:{font};font-size:18px;font-weight:600;padding:14px 32px;\">{Html(header)}</div></td></tr>");
        else if (logoHtml.Length > 0)
            sb.Append($"<tr><td style=\"padding:8px 32px 0 32px;\"><div style=\"border-top:3px solid {primary};font-size:0;line-height:0;\">&nbsp;</div></td></tr>");
        else
            sb.Append($"<tr><td style=\"font-size:0;line-height:0;border-top:4px solid {primary};border-radius:6px 6px 0 0;\">&nbsp;</td></tr>");
        sb.Append($"<tr><td style=\"padding:24px 32px 8px 32px;font-family:{font};font-size:15px;line-height:1.55;color:{fg};\">{bodyHtml}</td></tr>");
        if (boxHtml.Length > 0) sb.Append($"<tr><td style=\"padding:8px 32px 8px 32px;font-family:{font};\">{boxHtml}</td></tr>");
        if (sigHtml.Length > 0) sb.Append($"<tr><td style=\"padding:8px 32px 24px 32px;font-family:{font};font-size:15px;line-height:1.55;color:{fg};\">{sigHtml}</td></tr>");
        else sb.Append("<tr><td style=\"padding:0 0 16px 0;\"></td></tr>");
        sb.Append("</table>");
        if (footHtml.Length > 0)
            sb.Append($"<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:600px;max-width:100%;\"><tr><td style=\"padding:16px 32px;font-family:{font};font-size:12px;line-height:1.5;color:#6b7280;\">{footHtml}</td></tr></table>");
        sb.Append("</td></tr></table></body></html>");
        return new RenderedMail(sb.ToString(), text, images);
    }

    /// <summary>Einfache Mail ohne Vorlage (nur Text, leicht formatiert).</summary>
    private static string Simple(string body) =>
        $"<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI, Arial, sans-serif;font-size:15px;line-height:1.5;color:#1c2330;\">{Format(body, "#1f5fbf")}</body></html>";

    private static string Box(MailFacts f, string primary, string fg)
    {
        var rows = new List<(string, string)>();
        if (!string.IsNullOrEmpty(f.Number)) rows.Add((f.Type ?? "Beleg", f.Number));
        if (!string.IsNullOrEmpty(f.Date)) rows.Add(("Datum", f.Date));
        if (!string.IsNullOrEmpty(f.Amount)) rows.Add(("Betrag", f.Amount));
        if (!string.IsNullOrEmpty(f.Attachment)) rows.Add(("Anhang", f.Attachment));
        if (rows.Count == 0) return "";
        var sb = new StringBuilder($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-left:4px solid {primary};background:#f7f8fa;\">");
        foreach (var (k, v) in rows)
            sb.Append($"<tr><td style=\"padding:6px 14px;font-size:13px;color:#6b7280;width:110px;\">{Html(k)}</td><td style=\"padding:6px 14px;font-size:14px;color:{fg};font-weight:600;\">{Html(v)}</td></tr>");
        return sb.Append("</table>").ToString();
    }

    [GeneratedRegex(@"(https?://[^\s<]+[^\s<.,;:!?)\]]|www\.[^\s<]+[^\s<.,;:!?)\]]|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})")]
    private static partial Regex LinkPattern();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])")] private static partial Regex Italic();

    /// <summary>Text → HTML: Absätze, Zeilenumbrüche, Aufzählungen, **fett**, *kursiv*, Links.</summary>
    public static string Format(string text, string linkColor)
    {
        var sb = new StringBuilder();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n").Trim('\n'), @"\n[ \t]*\n"))
        {
            var lines = block.Split('\n');
            // Aufeinanderfolgende Zeilen mit „- “ werden zur Aufzählung, der Rest bleibt Absatz
            var para = new List<string>();
            var list = new List<string>();
            void FlushPara()
            {
                if (para.Count == 0) return;
                sb.Append("<p style=\"margin:0 0 14px 0;\">").Append(string.Join("<br>", para.Select(l => Inline(l, linkColor)))).Append("</p>");
                para.Clear();
            }
            void FlushList()
            {
                if (list.Count == 0) return;
                sb.Append("<ul style=\"margin:0 0 14px 0;padding-left:22px;\">");
                foreach (var l in list) sb.Append("<li>").Append(Inline(l, linkColor)).Append("</li>");
                sb.Append("</ul>");
                list.Clear();
            }
            foreach (var l in lines)
            {
                var t = l.TrimStart();
                if (t.StartsWith("- ") || t.StartsWith("• ")) { FlushPara(); list.Add(t[2..]); }
                else { FlushList(); para.Add(l); }
            }
            FlushPara(); FlushList();
        }
        return sb.ToString();
    }

    private static string Inline(string line, string linkColor)
    {
        var s = Html(line);
        s = LinkPattern().Replace(s, m =>
        {
            var t = m.Value;
            var href = t.Contains('@') && !t.StartsWith("http") ? "mailto:" + t : t.StartsWith("www.") ? "https://" + t : t;
            return $"<a href=\"{href}\" style=\"color:{linkColor};\">{t}</a>";
        });
        s = Bold().Replace(s, "<strong>$1</strong>");
        s = Italic().Replace(s, "<em>$1</em>");
        return s;
    }

    private static string StripMarkup(string s) => s.Replace("**", "");

    /// <summary>HTML-Sonderzeichen maskieren; Umlaute bleiben lesbar (die Mail ist UTF-8).</summary>
    public static string Html(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")] private static partial Regex HexColor();
    public static bool IsColor(string? c) => c is not null && HexColor().IsMatch(c.Trim());
    private static string Color(string? c, string fallback) => IsColor(c) ? c!.Trim() : fallback;
    private static string CssValue(string? v, string fallback) =>
        string.IsNullOrWhiteSpace(v) || v.IndexOfAny(new[] { '<', '>', '"', ';', '{', '}' }) >= 0 ? fallback : v.Trim();

    public static string ImageType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => "application/octet-stream"
    };
}

/// <summary>Daten für den Infokasten der Mail.</summary>
public sealed record MailFacts(string? Type, string? Number, string? Date, string? Amount, string? Attachment)
{
    public static MailFacts From(Vorgang v) => new(
        v.DocumentType, v.VoucherId, v.VoucherDate,
        v.GrossAmount is { } g ? $"{g.ToString("N2", CultureInfo.GetCultureInfo("de-AT"))} {v.Currency}" : null,
        v.Mail.NoDocument ? null : MailTemplate.SafeFileName(v.Mail.FileName) + ".pdf");
}

/// <summary>Für die Vorschau im Browser: cid:-Bilder als data:-URLs einsetzen.</summary>
public static class MailPreview
{
    public static string Inline(RenderedMail r)
    {
        var html = r.Html;
        foreach (var i in r.Images) html = html.Replace("cid:" + i.ContentId, $"data:{i.ContentType};base64,{Convert.ToBase64String(i.Data)}");
        return html;
    }
}
