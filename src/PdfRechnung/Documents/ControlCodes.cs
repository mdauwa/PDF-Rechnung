using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfRechnung.Documents;

/// <summary>Ein gedrucktes Steuerzeichen (gotomaxx-Syntax <c>@@XXWert@@</c>).</summary>
public sealed record ControlCode(int Page, string Code, string Value);

/// <summary>Eine Tabellenzeile aus <c>@@xr table=NAME|FELD=Wert|…@@</c> (ggf. ergänzt um <c>@@&lt;&lt;|FELD=Wert@@</c>).</summary>
public sealed class TableRow
{
    public string Table { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => Values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>Ergebnis der Auswertung aller Steuerzeichen eines Druckauftrags.</summary>
public sealed class ParsedCodes
{
    public List<ControlCode> Codes { get; } = new();

    // ---- E-Rechnung ----
    /// <summary>Wert von <c>@@xt@@</c>, z. B. gotomaxx.invoice.zugferd2.comfort. null = keine E-Rechnung angefordert.</summary>
    public string? InvoiceFormat { get; set; }
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TableRow> Rows { get; } = new();
    public bool PeppolSend { get; set; }

    // ---- Mail ----
    /// <summary><c>@@ml@@</c>: null = nicht angegeben, true/false = Versand an/aus.</summary>
    public bool? MailEnabled { get; set; }
    public List<string> To { get; } = new();
    public List<string> Cc { get; } = new();
    public List<string> Bcc { get; } = new();
    public string? Subject { get; set; }
    public List<string> BodyLines { get; } = new();
    public string? BodyFile { get; set; }
    public List<string> Attachments { get; } = new();
    public List<string> AttachmentFolders { get; } = new();
    public bool NoAttachment { get; set; }

    // ---- Eigene Erweiterungen (Belegpost) ----
    /// <summary><c>@@pv Name=Wert@@</c>: eigene Platzhalter für Betreff, Mailtext und Vorlage, z. B. {Anrede}.</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary><c>@@pt Vorlagenname@@</c>: Mail-Vorlage ausdrücklich wählen.</summary>
    public string? MailTemplate { get; set; }
    /// <summary><c>@@pf adresse@firma.at@@</c>: Absenderadresse dieses Belegs.</summary>
    public string? Sender { get; set; }
    /// <summary>@@pu benutzer@@ – wer gedruckt hat (Windows- oder ERP-Benutzer); überschreibt den Benutzer des Druckauftrags.</summary>
    public string? PrintUser { get; set; }
    /// <summary>@@pb007@@ – ERP-Bedienernummer (3-stellig, numerisch). Ordnet den Beleg dem Benutzer mit dieser Nummer zu.</summary>
    public string? OperatorNumber { get; set; }
    /// <summary>@@pb…@@ mit ungültigem Wert (nicht numerisch).</summary>
    public string? InvalidOperatorNumber { get; set; }

    // ---- Bearbeiten / Allgemein ----
    public string? Stationery { get; set; }
    public bool StationerySet { get; set; }
    public string? FileName { get; set; }
    /// <summary><c>@@mf@@</c> aus der ERP-Vorlage: Dateiname des Mail-Anhangs (Vorrang hat <c>@@fn@@</c>).</summary>
    public string? MailFileName { get; set; }
    public Dictionary<string, string> PdfInfo { get; } = new();
    public List<string> AppendPdfs { get; } = new();
    public List<string> EmbedFiles { get; } = new();
    public string? PdfA { get; set; }
    public List<int> SplitAfterPages { get; } = new();
    public List<int> RemovePages { get; } = new();
    public List<string> IncludeFiles { get; } = new();

    /// <summary>Steuerzeichen, die (noch) nicht ausgewertet werden.</summary>
    public List<ControlCode> Unknown { get; } = new();
    /// <summary>Bewusst ignoriert, weil der Server den Versand übernimmt (z. B. Mailprogramm öffnen).</summary>
    public List<ControlCode> Ignored { get; } = new();

    public string? Field(string key) => Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    public IEnumerable<TableRow> Table(string name) => Rows.Where(r => r.Table.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Liest gotomaxx-kompatible Steuerzeichen aus dem Text eines Druckauftrags.
/// Aufbau: <c>@@</c> + zwei Zeichen Befehl + Wert + <c>@@</c>, nie über einen Zeilenumbruch.
/// </summary>
public static partial class ControlCodeParser
{
    [GeneratedRegex(@"@@(?<code>[A-Za-z<]{2})(?<value>.*?)@@", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    /// <summary>Alle Steuerzeichen einer Textzeile.</summary>
    public static IEnumerable<ControlCode> Find(string line, int page)
    {
        foreach (Match m in CodeRegex().Matches(line))
            yield return new ControlCode(page, m.Groups["code"].Value.ToLowerInvariant(), m.Groups["value"].Value);
    }

    /// <summary>Wertet die Zeilen (je Seite) aus.</summary>
    public static ParsedCodes Parse(IEnumerable<(int Page, string Line)> lines)
    {
        var r = new ParsedCodes();
        foreach (var (page, line) in lines)
            foreach (var c in Find(line, page))
            {
                r.Codes.Add(c);
                Apply(r, c);
            }
        return r;
    }

    public static ParsedCodes Parse(string text) =>
        Parse(text.Split('\n').Select(l => (1, l.TrimEnd('\r'))));

    private static void Apply(ParsedCodes r, ControlCode c)
    {
        var v = c.Value;
        var t = v.Trim();
        switch (c.Code)
        {
            // ---------- E-Rechnung ----------
            case "xt": r.InvoiceFormat = t.Length > 0 ? t : null; break;
            case "xi":
            {
                var i = t.IndexOf('=');
                if (i > 0) r.Fields[t[..i].Trim()] = t[(i + 1)..].Trim();
                else r.Unknown.Add(c);
                break;
            }
            case "xr":
            {
                var row = ParseRow(t);
                if (row is null) r.Unknown.Add(c); else r.Rows.Add(row);
                break;
            }
            case "<<":
            {
                // Fortsetzung der vorigen Tabellenzeile (so ist die ERP-Vorlage aufgebaut: "<<|FELD=Wert")
                var last = r.Rows.LastOrDefault();
                if (last is null) { r.Unknown.Add(c); break; }
                foreach (var (k, val) in Pairs(t.TrimStart('|'))) last.Values[k] = val;
                break;
            }
            case "pe": r.PeppolSend = t == "1"; break;

            // ---------- Eigene Erweiterungen ----------
            case "pv":
            {
                var i = t.IndexOf('=');
                if (i > 0) r.Variables[t[..i].Trim().Trim('{', '}')] = t[(i + 1)..].Trim();
                else r.Unknown.Add(c);
                break;
            }
            case "pt": r.MailTemplate = t.Length > 0 ? t : null; break;
            case "pf": r.Sender = t.Length > 0 ? t : null; break;
            case "pu": r.PrintUser = t.Length > 0 ? t : null; break;
            case "pb":
                if (Settings.UserStore.NormalizeOperator(t) is { } no) r.OperatorNumber = no;
                else r.InvalidOperatorNumber = t;
                break;

            // ---------- Mail ----------
            case "ml": r.MailEnabled = t != "0"; break;
            case "to" or "aa": r.To.AddRange(Addresses(t)); break;
            case "cc": r.Cc.AddRange(Addresses(t)); break;
            case "bc": r.Bcc.AddRange(Addresses(t)); break;
            case "ed": r.Subject = t; break;
            case "sa": r.BodyLines.Add(v.TrimEnd()); break;
            case "mb": r.BodyFile = t; break;
            case "ma": if (t.Length > 0) r.Attachments.Add(t); break;
            case "mc": if (t.Length > 0) r.AttachmentFolders.Add(t); break;
            case "md": r.NoAttachment = true; break;
            case "mf": r.MailFileName = t.Length > 0 ? t : null; break;
            case "mo" or "ms" or "mp": r.Ignored.Add(c); break;

            // ---------- Bearbeiten ----------
            case "bp": r.StationerySet = true; r.Stationery = t.Length > 0 ? t : null; break;
            case "di":
                if (t.Length > 1 && "task".Contains(t[0])) r.PdfInfo[t[..1]] = t[1..];
                else r.Unknown.Add(c);
                break;
            case "af": if (t.Length > 0) r.AppendPdfs.Add(t); break;
            case "ef": if (t.Length > 0) r.EmbedFiles.Add(t); break;
            case "rp" when t.Equals("c", StringComparison.OrdinalIgnoreCase): r.RemovePages.Add(c.Page); break;
            case "ip": r.Ignored.Add(c); break; // Grafik/QR-Code: folgt mit dem Druckdienst

            // ---------- Allgemein ----------
            case "fn": r.FileName = t.Length > 0 ? t : null; break;
            case "ca": r.PdfA = t; break;
            case "nd": r.SplitAfterPages.Add(c.Page); break;
            case "cf": if (t.Length > 0) r.IncludeFiles.Add(t); break;
            case "of" or "od" or "jc" or "je": r.Ignored.Add(c); break;

            default: r.Unknown.Add(c); break;
        }
    }

    private static TableRow? ParseRow(string t)
    {
        // table=ITEM_DETAILS|ITEM_LINE_ID=1|ITEM_NAME=…
        var parts = t.Split('|');
        if (parts.Length == 0 || !parts[0].Trim().StartsWith("table=", StringComparison.OrdinalIgnoreCase)) return null;
        var row = new TableRow { Table = parts[0].Trim()[6..].Trim() };
        foreach (var (k, v) in Pairs(string.Join('|', parts.Skip(1)))) row.Values[k] = v;
        return row;
    }

    private static IEnumerable<(string Key, string Value)> Pairs(string s)
    {
        foreach (var p in s.Split('|'))
        {
            var i = p.IndexOf('=');
            if (i > 0) yield return (p[..i].Trim(), p[(i + 1)..].Trim());
        }
    }

    private static IEnumerable<string> Addresses(string s) =>
        s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(a => a.Length > 0);

    // ---------------- Werte ----------------

    /// <summary>Zahl aus dem ERP: "1234.56", "1234,56", "1.234,56", "1,234.56", "-12,00", "12,00-".</summary>
    public static decimal? Number(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace(" ", "").Replace(" ", "").Replace("€", "");
        var neg = false;
        if (s.EndsWith('-')) { neg = true; s = s[..^1]; }
        var lastComma = s.LastIndexOf(','); var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
            s = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (lastComma >= 0)
            s = s.Count(ch => ch == ',') > 1 ? s.Replace(",", "") : s.Replace(',', '.');
        else if (s.Count(ch => ch == '.') > 1)
            s = s.Replace(".", "");
        if (!decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)) return null;
        return neg ? -d : d;
    }

    /// <summary>Datum aus dem ERP: TT.MM.JJJJ (auch TT.MM.JJ oder JJJJ-MM-TT).</summary>
    public static DateOnly? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "yyyy-MM-dd", "yyyyMMdd" };
        return DateOnly.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
