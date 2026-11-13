using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace PdfRechnung.Documents;

public sealed record ComposeResult(byte[] Pdf, int RemovedCodes, string? StationeryName, List<CheckMessage> Messages);

/// <summary>
/// Erzeugt aus dem Ausdruck das Versand-PDF:
///   1. Steuerzeichen aus dem Seiteninhalt löschen (die Textbefehle selbst – nicht nur überdecken),
///   2. Briefpapier unterlegen (erste Seite / Folgeseiten, Versatz, Skalierung),
///   3. optional den Inhalt in den Druckbereich einpassen, sonst Ränder prüfen,
///   4. PDF-Infos aus @@di@@ setzen.
/// </summary>
public static partial class PdfComposer
{
    public const double MmToPt = 72.0 / 25.4;

    public static string StationeryDirectory(string dataDirectory) => Path.Combine(dataDirectory, "briefpapier");

    /// <summary>Welches Briefpapier gilt? @@bpName@@ hat Vorrang, @@bp@@ ohne Namen = keines, sonst das Standard-Briefpapier.</summary>
    public static (Stationery? Item, string? Error) Select(ParsedCodes p, StationeryOptions o)
    {
        string? name = p.StationerySet ? p.Stationery : (string.IsNullOrWhiteSpace(o.Default) ? null : o.Default);
        if (name is null) return (null, null);
        var item = o.Items.FirstOrDefault(s => s.Name == name);
        if (item is not null) return (item, null);
        var similar = o.Items.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return (null, similar is not null
            ? $"Briefpapier „{name}“ nicht gefunden – meinten Sie „{similar.Name}“? (Groß-/Kleinschreibung beachten)"
            : $"Briefpapier „{name}“ ist nicht angelegt (Einstellungen → Briefpapier).");
    }

    public static ComposeResult Compose(byte[] original, ParsedCodes p, StationeryOptions stationery, string stationeryDir)
    {
        var messages = new List<CheckMessage>();
        var (item, error) = Select(p, stationery);
        if (error is not null) messages.Add(new CheckMessage(DocumentChecker.Error, error));

        using var input = new MemoryStream(original);
        var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var removed = 0;
        var contentBoxes = PdfText.ContentBoxes(original);

        for (var i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            var seq = ContentReader.ReadContent(page);
            var count = RemoveControlCodes(seq, page);
            removed += count;

            var st = item is null ? null : i == 0 || item.FollowingMode != "none" ? item : null;
            double w = page.Width.Point, h = page.Height.Point;
            var (top, bottom, left, right) = st is null ? (0d, 0d, 0d, 0d) : Margins(st, i);

            // Inhalt in den Druckbereich einpassen (verkleinern + verschieben)
            var fit = st is not null && st.FitContent && (top + bottom + left + right) > 0;
            if (fit)
            {
                var scale = Math.Min((w - left - right) / w, (h - top - bottom) / h);
                var tx = left + ((w - left - right) - w * scale) / 2;
                var ty = bottom + ((h - top - bottom) - h * scale) / 2;
                seq = Wrap(seq, scale, tx, ty);
            }
            if (count > 0 || fit) page.Contents.ReplaceContent(seq);

            if (st is not null)
            {
                var file = i > 0 && st.FollowingMode == "file" && !string.IsNullOrWhiteSpace(st.FollowingPageFile) ? st.FollowingPageFile : st.FirstPageFile;
                var path = Path.Combine(stationeryDir, file);
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(path))
                {
                    if (i == 0) messages.Add(new CheckMessage(DocumentChecker.Error, $"Die PDF-Datei für das Briefpapier „{st.Name}“ fehlt."));
                }
                else Underlay(page, path, st);

                // Ränder prüfen (nur wenn nicht eingepasst wird)
                if (!fit && i < contentBoxes.Count)
                {
                    var hit = Overlaps(contentBoxes[i], w, h, top, bottom, left, right);
                    if (hit is not null)
                        messages.Add(new CheckMessage(DocumentChecker.Warning,
                            $"Seite {i + 1}: Der Inhalt ragt in den {hit} des Briefpapiers „{st.Name}“. In den Einstellungen „Inhalt in den Druckbereich einpassen“ aktivieren oder das Formular anpassen."));
                }
            }
        }

        foreach (var (k, v) in p.PdfInfo)
            switch (k)
            {
                case "t": doc.Info.Title = v; break;
                case "a": doc.Info.Author = v; break;
                case "s": doc.Info.Subject = v; break;
                case "k": doc.Info.Keywords = v; break;
            }
        doc.Info.Creator = "PDF Rechnung";

        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return new ComposeResult(output.ToArray(), removed, item?.Name, messages);
    }

    /// <summary>Ränder in Punkt für die Seite (Folgeseiten ggf. mit eigenen Werten für oben/unten).</summary>
    public static (double Top, double Bottom, double Left, double Right) Margins(Stationery s, int pageIndex)
    {
        var following = pageIndex > 0 && s.FollowingOwnMargins;
        return ((following ? s.FollowingMarginTop : s.MarginTop) * MmToPt, (following ? s.FollowingMarginBottom : s.MarginBottom) * MmToPt,
            s.MarginLeft * MmToPt, s.MarginRight * MmToPt);
    }

    /// <summary>Briefpapier unter den vorhandenen Inhalt legen.</summary>
    internal static void Underlay(PdfPage page, string path, Stationery st)
    {
        using var form = XPdfForm.FromFile(path);
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
        var scale = (st.Scale <= 0 ? 100 : st.Scale) / 100.0;
        double w = page.Width.Point, h = page.Height.Point;
        // XGraphics: Ursprung oben links, y nach unten
        var dx = st.OffsetX * MmToPt;
        var dy = -st.OffsetY * MmToPt; // positiver Versatz = nach oben
        gfx.DrawImage(form, dx + w * (1 - scale) / 2, dy + h * (1 - scale) / 2, w * scale, h * scale);
    }

    /// <summary>
    /// Vorschau für die Einstellungen: leere A4-Seiten (erste Seite + Folgeseite) mit Briefpapier,
    /// der Druckbereich ist rot gestrichelt, der Bereich außerhalb leicht rot hinterlegt.
    /// </summary>
    public static byte[] Preview(Stationery st, string stationeryDir)
    {
        var doc = new PdfDocument();
        doc.Info.Title = $"Briefpapier {st.Name} – Vorschau";
        for (var i = 0; i < 2; i++)
        {
            var page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            double w = page.Width.Point, h = page.Height.Point;
            var showStationery = i == 0 || st.FollowingMode != "none";
            var file = i > 0 && st.FollowingMode == "file" && !string.IsNullOrWhiteSpace(st.FollowingPageFile) ? st.FollowingPageFile : st.FirstPageFile;
            var path = Path.Combine(stationeryDir, file ?? "");
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                var (top, bottom, left, right) = showStationery ? Margins(st, i) : (0d, 0d, 0d, 0d);
                var shade = new XSolidBrush(XColor.FromArgb(40, 209, 69, 59));
                if (top > 0) gfx.DrawRectangle(shade, 0, 0, w, top);
                if (bottom > 0) gfx.DrawRectangle(shade, 0, h - bottom, w, bottom);
                if (left > 0) gfx.DrawRectangle(shade, 0, top, left, h - top - bottom);
                if (right > 0) gfx.DrawRectangle(shade, w - right, top, right, h - top - bottom);
                var pen = new XPen(XColor.FromArgb(209, 69, 59), 1.2) { DashStyle = XDashStyle.Dash };
                gfx.DrawRectangle(pen, left, top, w - left - right, h - top - bottom);
                // Platzhalter für Textzeilen im Druckbereich
                var line = new XSolidBrush(XColor.FromArgb(205, 213, 225));
                for (var y = top + 30; y < h - bottom - 20 && y < top + 30 + 14 * 22; y += 22)
                    gfx.DrawRectangle(line, left + 20, y, Math.Max(10, (w - left - right - 40) * (y % 3 == 0 ? 0.6 : 0.85)), 6);
            }
            if (showStationery && File.Exists(path)) Underlay(page, path, st);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static CSequence Wrap(CSequence seq, double scale, double tx, double ty)
    {
        var inv = CultureInfo.InvariantCulture;
        var prefix = ContentReader.ReadContent(Encoding.ASCII.GetBytes(
            $"q {scale.ToString("0.#####", inv)} 0 0 {scale.ToString("0.#####", inv)} {tx.ToString("0.###", inv)} {ty.ToString("0.###", inv)} cm\n"));
        var suffix = ContentReader.ReadContent(Encoding.ASCII.GetBytes("Q\n"));
        var result = new CSequence();
        result.Add(prefix); result.Add(seq); result.Add(suffix);
        return result;
    }

    private static string? Overlaps(List<(double X1, double Y1, double X2, double Y2)> boxes, double w, double h, double top, double bottom, double left, double right)
    {
        const double tol = 1.5;
        foreach (var b in boxes)
        {
            if (top > 0 && b.Y2 > h - top + tol) return "Kopfbereich (Rand oben)";
            if (bottom > 0 && b.Y1 < bottom - tol) return "Fußbereich (Rand unten)";
            if (left > 0 && b.X1 < left - tol) return "linken Rand";
            if (right > 0 && b.X2 > w - right + tol) return "rechten Rand";
        }
        return null;
    }

    // ---------------- Steuerzeichen entfernen ----------------

    /// <summary>
    /// Entfernt alle Textbefehle (Tj, TJ, ', "), deren Text zu einem Steuerzeichen @@…@@ gehört.
    /// Ein Steuerzeichen kann auf mehrere Textbefehle verteilt sein; die Zuordnung läuft über die
    /// Reihenfolge im Inhalt (@@ öffnet, das nächste @@ schließt).
    /// </summary>
    public static int RemoveControlCodes(CSequence seq, PdfPage page)
    {
        var fonts = new Dictionary<string, FontDecoder>();
        FontDecoder? current = null;
        var shows = new List<(CSequence Parent, COperator Op, string Text)>();
        Collect(seq, page, fonts, ref current, shows);

        var text = new StringBuilder();
        var spans = new List<(int Start, int End)>();
        foreach (var s in shows) { spans.Add((text.Length, text.Length + s.Text.Length)); text.Append(s.Text); }
        var all = text.ToString();
        var remove = new bool[shows.Count];
        foreach (Match m in CodeRegex().Matches(all))
            for (var k = 0; k < shows.Count; k++)
                if (spans[k].Start < m.Index + m.Length && spans[k].End > m.Index) remove[k] = true;

        var n = 0;
        for (var k = 0; k < shows.Count; k++)
            if (remove[k] && shows[k].Parent.Remove(shows[k].Op)) n++;
        return n;
    }

    [GeneratedRegex(@"@@[A-Za-z<]{2}[^\n]{0,400}?@@", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    private static void Collect(CSequence seq, PdfPage page, Dictionary<string, FontDecoder> fonts, ref FontDecoder? current,
        List<(CSequence, COperator, string)> shows)
    {
        foreach (var obj in seq)
        {
            if (obj is CSequence nested) { Collect(nested, page, fonts, ref current, shows); continue; }
            if (obj is not COperator op) continue;
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.Tf when op.Operands.Count > 0 && op.Operands[0] is CName fn:
                    if (!fonts.TryGetValue(fn.Name, out current)) fonts[fn.Name] = current = FontDecoder.For(page, fn.Name);
                    break;
                case OpCodeName.Tj or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble:
                {
                    var str = op.Operands.OfType<CString>().LastOrDefault();
                    if (str is not null) shows.Add((seq, op, (current ?? FontDecoder.Latin1).Decode(str.Value)));
                    break;
                }
                case OpCodeName.TJ when op.Operands.Count > 0 && op.Operands[0] is CArray arr:
                {
                    var sb = new StringBuilder();
                    foreach (var item in arr)
                        if (item is CString s) sb.Append((current ?? FontDecoder.Latin1).Decode(s.Value));
                    shows.Add((seq, op, sb.ToString()));
                    break;
                }
            }
        }
    }

    /// <summary>Übersetzt Zeichencodes eines Fonts in Unicode (ToUnicode-CMap, sonst WinAnsi/Latin-1).</summary>
    private sealed class FontDecoder
    {
        public static readonly FontDecoder Latin1 = new(1, new());
        private readonly int _bytes;
        private readonly Dictionary<int, string> _map;
        private FontDecoder(int bytes, Dictionary<int, string> map) { _bytes = bytes; _map = map; }

        public string Decode(string raw)
        {
            // PDFsharp liefert Strings als Zeichenfolge der Bytes (0–255)
            var sb = new StringBuilder();
            for (var i = 0; i + _bytes - 1 < raw.Length; i += _bytes)
            {
                var code = _bytes == 2 ? (raw[i] & 0xFF) << 8 | (raw[i + 1] & 0xFF) : raw[i] & 0xFF;
                sb.Append(_map.TryGetValue(code, out var u) ? u : _bytes == 1 ? ((char)code).ToString() : "�");
            }
            return sb.ToString();
        }

        public static FontDecoder For(PdfPage page, string name)
        {
            try
            {
                var font = page.Resources.Elements.GetDictionary("/Font")?.Elements.GetDictionary(name);
                if (font is null) return Latin1;
                var twoBytes = font.Elements.GetName("/Subtype") == "/Type0";
                var map = new Dictionary<int, string>();
                if (font.Elements.GetReference("/ToUnicode")?.Value is PdfDictionary cmap && cmap.Stream is not null)
                    ParseCMap(Encoding.Latin1.GetString(cmap.Stream.UnfilteredValue), map);
                return new FontDecoder(twoBytes ? 2 : 1, map);
            }
            catch { return Latin1; }
        }

        private static void ParseCMap(string cmap, Dictionary<int, string> map)
        {
            static string Hex(string h)
            {
                var sb = new StringBuilder();
                for (var i = 0; i + 3 < h.Length; i += 4) sb.Append((char)Convert.ToInt32(h.Substring(i, 4), 16));
                return sb.ToString();
            }
            foreach (Match block in Regex.Matches(cmap, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                    map[Convert.ToInt32(m.Groups[1].Value, 16)] = Hex(m.Groups[2].Value.PadLeft(4, '0'));
            foreach (Match block in Regex.Matches(cmap, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
            {
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    int lo = Convert.ToInt32(m.Groups[1].Value, 16), hi = Convert.ToInt32(m.Groups[2].Value, 16), dst = Convert.ToInt32(m.Groups[3].Value, 16);
                    for (var c = lo; c <= hi && c - lo < 65536; c++) map[c] = ((char)(dst + c - lo)).ToString();
                }
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*\[(.*?)\]", RegexOptions.Singleline))
                {
                    int lo = Convert.ToInt32(m.Groups[1].Value, 16);
                    var targets = Regex.Matches(m.Groups[3].Value, @"<([0-9A-Fa-f]+)>").Select(t => Hex(t.Groups[1].Value.PadLeft(4, '0'))).ToList();
                    for (var k = 0; k < targets.Count; k++) map[lo + k] = targets[k];
                }
            }
        }
    }
}
