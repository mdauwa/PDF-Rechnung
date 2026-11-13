using System.Globalization;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace PdfRechnung.Documents;

/// <summary>
/// Text eines PDFs zeilenweise je Seite (für die Steuerzeichen). Die Steuerzeichen können überall auf der Seite stehen,
/// auch neben anderem Text, in winziger Schrift oder gedreht. Deshalb werden die Zeilen aus den einzelnen Buchstaben
/// gebildet: gleiche Grundlinie, gleiche Schriftgröße und Richtung = eine Zeile, sortiert in Schreibrichtung.
/// </summary>
public static class PdfText
{
    public static (int Pages, List<(int Page, string Line)> Lines) Extract(byte[] pdf)
    {
        var lines = new List<(int, string)>();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
            foreach (var (text, _) in BuildLines(page.Letters.Where(l => !string.IsNullOrEmpty(l.Value)).ToList()))
                lines.Add((page.Number, text));
        return (doc.NumberOfPages, lines);
    }

    /// <summary>
    /// Umrisse des gedruckten Inhalts je Seite (PDF-Koordinaten, Ursprung unten links) ohne die Steuerzeichen –
    /// für die Prüfung, ob der Inhalt in die Ränder des Briefpapiers ragt.
    /// </summary>
    public static List<List<(double X1, double Y1, double X2, double Y2)>> ContentBoxes(byte[] pdf)
    {
        var result = new List<List<(double, double, double, double)>>();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            var boxes = new List<(double, double, double, double)>();
            foreach (var (text, letters) in BuildLines(page.Letters.Where(l => !string.IsNullOrEmpty(l.Value)).ToList()))
            {
                var isCode = new bool[text.Length];
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"@@[A-Za-z<]{2}.*?@@"))
                    for (var i = m.Index; i < m.Index + m.Length; i++) isCode[i] = true;
                for (var i = 0; i < letters.Count; i++)
                {
                    var l = letters[i];
                    if (l is null || isCode[i] || string.IsNullOrWhiteSpace(l.Value)) continue;
                    var r = l.GlyphRectangle;
                    if (r.Width <= 0 && r.Height <= 0) continue;
                    boxes.Add((r.Left, r.Bottom, r.Right, r.Top));
                }
            }
            foreach (var img in page.GetImages())
            {
                var r = img.Bounds;
                boxes.Add((r.Left, r.Bottom, r.Right, r.Top));
            }
            result.Add(boxes);
        }
        return result;
    }

    /// <summary>Zeilen aus Buchstaben: Text und je Zeichen der zugehörige Buchstabe (null = eingefügtes Leerzeichen).</summary>
    private static IEnumerable<(string Text, List<UglyToad.PdfPig.Content.Letter?> Letters)> BuildLines(List<UglyToad.PdfPig.Content.Letter> letters)
    {
        static int Angle(UglyToad.PdfPig.Content.Letter l)
        {
            var dx = l.EndBaseLine.X - l.StartBaseLine.X; var dy = l.EndBaseLine.Y - l.StartBaseLine.Y;
            return dx == 0 && dy == 0 ? 0 : (int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI);
        }
        foreach (var dir in letters.GroupBy(Angle))
        {
            var rad = dir.Key * Math.PI / 180;
            var (cos, sin) = (Math.Cos(rad), Math.Sin(rad));
            double U(UglyToad.PdfPig.Content.Letter l) => l.StartBaseLine.X * cos + l.StartBaseLine.Y * sin;
            double V(UglyToad.PdfPig.Content.Letter l) => -l.StartBaseLine.X * sin + l.StartBaseLine.Y * cos;
            var groups = dir.GroupBy(l => (V: Math.Round(V(l) * 4) / 4, Size: Math.Round(l.PointSize * 10) / 10))
                            .OrderByDescending(g => g.Key.V);
            foreach (var g in groups)
            {
                var sb = new System.Text.StringBuilder();
                var map = new List<UglyToad.PdfPig.Content.Letter?>();
                UglyToad.PdfPig.Content.Letter? prev = null;
                foreach (var l in g.OrderBy(U))
                {
                    if (prev is not null)
                    {
                        var prevEnd = prev.EndBaseLine.X * cos + prev.EndBaseLine.Y * sin;
                        // Lücke breiter als ein Drittel der Schriftgröße = Leerzeichen (falls nicht schon gedruckt)
                        if (U(l) - prevEnd > l.PointSize * 0.3 && sb.Length > 0 && sb[^1] != ' ' && l.Value != " ") { sb.Append(' '); map.Add(null); }
                    }
                    sb.Append(l.Value);
                    for (var k = 0; k < l.Value.Length; k++) map.Add(l);
                    prev = l;
                }
                yield return (sb.ToString(), map);
            }
        }
    }
}

/// <summary>Platzhalter für Betreff, Mailtext und Dateinamen.</summary>
public static class MailTemplate
{
    public static readonly string[] Placeholders = { "{Belegart}", "{Belegnummer}", "{Belegdatum}", "{Kunde}", "{Kundennummer}", "{Betrag}", "{Waehrung}" };

    public static string Apply(string template, Vorgang v)
    {
        var amount = v.GrossAmount is { } g ? g.ToString("N2", CultureInfo.GetCultureInfo("de-AT")) : "";
        return (template ?? "")
            .Replace("{Belegart}", v.DocumentType).Replace("{Belegnummer}", v.VoucherId ?? "")
            .Replace("{Belegdatum}", v.VoucherDate ?? "").Replace("{Kunde}", v.BuyerName ?? "")
            .Replace("{Kundennummer}", v.BuyerId ?? "").Replace("{Betrag}", amount).Replace("{Waehrung}", v.Currency)
            .Replace("\r\n", "\n");
    }

    /// <summary>Dateiname ohne unzulässige Zeichen.</summary>
    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (s.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Length == 0 ? "Beleg" : s.Length > 120 ? s[..120] : s;
    }
}

/// <summary>Macht aus einem gedruckten PDF einen Vorgang: Steuerzeichen lesen, Rechnungsdaten aufbereiten, prüfen, ablegen.</summary>
public sealed class DocumentProcessor
{
    private readonly DocumentStore _store;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly IOptionsMonitor<InvoiceOptions> _invoice;
    private readonly ILogger<DocumentProcessor> _log;

    public DocumentProcessor(DocumentStore store, IOptionsMonitor<MailOptions> mail, IOptionsMonitor<InvoiceOptions> invoice, ILogger<DocumentProcessor> log,
        IOptionsMonitor<InboxOptions>? inbox = null, IOptionsMonitor<StationeryOptions>? stationery = null, Settings.SettingsStore? settings = null,
        IOptionsMonitor<MailTemplateOptions>? templates = null, Settings.UserStore? users = null, Services.EventHub? events = null)
    {
        _users = users; _events = events;
        _store = store; _mail = mail; _invoice = invoice; _log = log; _inbox = inbox; _stationery = stationery; _templates = templates;
        _stationeryDir = PdfComposer.StationeryDirectory(settings?.DataDirectory ?? Path.GetDirectoryName(store.Root)!);
    }

    private readonly IOptionsMonitor<StationeryOptions>? _stationery;
    private readonly IOptionsMonitor<MailTemplateOptions>? _templates;
    private readonly Settings.UserStore? _users;
    private readonly Services.EventHub? _events;
    public MailTemplateOptions Templates => _templates?.CurrentValue ?? new();
    private readonly string _stationeryDir;

    /// <summary>Versand-PDF erzeugen: Steuerzeichen entfernen, Briefpapier unterlegen. Fehler werden zu Meldungen.</summary>
    private byte[]? Compose(Vorgang v, byte[] original, ParsedCodes p)
    {
        try
        {
            var r = PdfComposer.Compose(original, p, _stationery?.CurrentValue ?? new StationeryOptions(), _stationeryDir);
            v.Stationery = r.StationeryName;
            v.RemovedCodes = r.RemovedCodes;
            v.LayoutMessages = r.Messages;
            return r.Pdf;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Versand-PDF für {Id} konnte nicht erzeugt werden", v.Id);
            v.Stationery = null; v.RemovedCodes = 0;
            v.LayoutMessages = new() { new CheckMessage(DocumentChecker.Error, "Das Versand-PDF konnte nicht erzeugt werden: " + ex.Message) };
            return null;
        }
    }

    private readonly IOptionsMonitor<InboxOptions>? _inbox;

    /// <summary>Pfade aus den Steuerzeichen über die Pfadzuordnung (Einstellungen → Drucker &amp; Eingang) umsetzen.</summary>
    public static string MapPath(string path, IEnumerable<string> mappings)
    {
        foreach (var m in mappings)
        {
            var i = m.IndexOf('=');
            if (i <= 0) continue;
            var from = m[..i].Trim(); var to = m[(i + 1)..].Trim();
            if (from.Length > 0 && path.StartsWith(from, StringComparison.OrdinalIgnoreCase)) return to + path[from.Length..];
        }
        return path;
    }

    private ParsedCodes Map(ParsedCodes p)
    {
        var maps = _inbox?.CurrentValue.PathMappings ?? new List<string>();
        if (maps.Count == 0) return p;
        if (p.BodyFile is not null) p.BodyFile = MapPath(p.BodyFile, maps);
        for (var i = 0; i < p.Attachments.Count; i++) p.Attachments[i] = MapPath(p.Attachments[i], maps);
        for (var i = 0; i < p.AttachmentFolders.Count; i++) p.AttachmentFolders[i] = MapPath(p.AttachmentFolders[i], maps);
        for (var i = 0; i < p.AppendPdfs.Count; i++) p.AppendPdfs[i] = MapPath(p.AppendPdfs[i], maps);
        for (var i = 0; i < p.EmbedFiles.Count; i++) p.EmbedFiles[i] = MapPath(p.EmbedFiles[i], maps);
        return p;
    }

    public Vorgang Process(byte[] pdf, string source, string? fileName, string? user = null, string? machine = null, string? ownerUserId = null)
    {
        var now = DateTimeOffset.Now;
        var v = new Vorgang { Id = DocumentStore.NewId(now), CreatedAt = now, Source = source, OriginalFileName = fileName, PrintUser = user, PrintMachine = machine };
        string text;
        ParsedCodes p;
        try
        {
            var (pages, lines) = PdfText.Extract(pdf);
            v.Pages = pages;
            text = string.Join("\n", lines.Select(l => $"[{l.Page}] {l.Line}"));
            p = Map(ControlCodeParser.Parse(lines));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDF {File} konnte nicht gelesen werden", fileName);
            text = "";
            p = new ParsedCodes();
            v.Messages.Add(new CheckMessage(DocumentChecker.Error, "Das PDF konnte nicht gelesen werden: " + ex.Message));
        }

        // Wem gehört der Beleg? @@pu@@ vor Druckauftrag; beim Upload der hochladende Benutzer
        if (!string.IsNullOrWhiteSpace(p.PrintUser)) v.PrintUser = p.PrintUser.Trim();
        var owner = ownerUserId is not null ? _users?.FindById(ownerUserId) : _users?.FindByPrintName(v.PrintUser);
        v.OwnerUserId = owner?.Id ?? ownerUserId;
        v.OwnerName = owner?.Name;

        Fill(v, p, overwriteMail: true);
        var sendPdf = v.Messages.Count == 0 ? Compose(v, pdf, p) : null;
        if (v.Messages.Count == 0) v.Messages = DocumentChecker.Check(v, p, CurrentSettings());
        v.UpdateStatus();
        v.History.Add(new HistoryEntry(now, user ?? source, "eingang",
            $"{source}{(fileName is null ? "" : $": {fileName}")} · {v.Pages} Seite(n) · {v.Codes.Count} Steuerzeichen"));
        _store.Create(v, pdf, text, sendPdf);
        _log.LogInformation("Vorgang {Id} angelegt: {Type} {No} → {Status}", v.Id, v.DocumentType, v.VoucherId, v.Status);
        if (v.OwnerUserId is not null && _events is not null)
        {
            var title = string.Join(" ", new[] { v.DocumentType, v.VoucherId }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (title.Length == 0) title = v.OriginalFileName ?? "Neuer Beleg";
            if (!string.IsNullOrWhiteSpace(v.BuyerName)) title += " – " + v.BuyerName;
            var first = v.Messages.FirstOrDefault(m => m.Level == DocumentChecker.Error)?.Text ?? v.Messages.FirstOrDefault(m => m.Level == DocumentChecker.Warning)?.Text;
            _events.Publish(v.OwnerUserId, "neu", v.Id, title, v.Status, first);
        }
        return v;
    }

    /// <summary>Prüfung wiederholen (z. B. nach geänderten Einstellungen oder Versanddaten).</summary>
    public Vorgang? Recheck(string id, string by)
    {
        var path = _store.FilePath(id, DocumentStore.PdfFile);
        if (path is null) return null;
        ParsedCodes p;
        var original = File.ReadAllBytes(path);
        try { p = Map(ControlCodeParser.Parse(PdfText.Extract(original).Lines)); }
        catch { p = new ParsedCodes(); }
        return _store.Update(id, v =>
        {
            Fill(v, p, overwriteMail: !v.Mail.Edited);
            _store.WriteFile(id, DocumentStore.SendFile, Compose(v, original, p));
            v.Messages = DocumentChecker.Check(v, p, CurrentSettings());
            if (v.Status is VorgangStatus.SendError) v.Status = VorgangStatus.Warning;
            v.UpdateStatus();
            v.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "pruefen", $"{v.ErrorCount} Fehler, {v.WarningCount} Warnungen"));
            return true;
        });
    }

    /// <summary>Nur die Prüfung (ohne PDF neu zu lesen), z. B. nach Änderung der Versanddaten.</summary>
    public void Revalidate(Vorgang v)
    {
        var path = _store.FilePath(v.Id, DocumentStore.PdfFile);
        ParsedCodes p;
        try { p = path is null ? new ParsedCodes() : Map(ControlCodeParser.Parse(PdfText.Extract(File.ReadAllBytes(path)).Lines)); }
        catch { p = new ParsedCodes(); }
        v.Messages = DocumentChecker.Check(v, p, CurrentSettings());
        v.UpdateStatus();
    }

    private AppSettings CurrentSettings() => new() { Mail = _mail.CurrentValue, Invoice = _invoice.CurrentValue };

    private void Fill(Vorgang v, ParsedCodes p, bool overwriteMail)
    {
        var inv = InvoiceData.From(p, _invoice.CurrentValue.Units);
        v.Invoice = inv;
        v.Codes = p.Codes;
        v.FormatCode = p.InvoiceFormat;
        v.FormatName = inv.FormatName;
        v.DocumentType = InvoiceData.DocumentTypeName(inv.DocumentTypeCode);
        v.VoucherId = p.Field("VOUCHER_ID") ?? p.PdfInfo.GetValueOrDefault("t");
        v.VoucherDate = p.Field("VOUCHER_DATE");
        v.BuyerId = p.Field("BUYER_ID");
        v.BuyerName = p.Field("BUYER_NAME");
        v.Currency = p.Field("CURRENCY") ?? "EUR";
        v.GrossAmount = inv.PrintedGrossAmount ?? (inv.Lines.Count > 0 ? inv.GrossAmount : null);

        if (!overwriteMail) return;
        var o = _mail.CurrentValue;
        var m = new MailData
        {
            Enabled = p.MailEnabled ?? true,
            To = p.To.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Cc = p.Cc.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Bcc = p.Bcc.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            NoDocument = p.NoAttachment
        };
        // Wenn kein Empfänger gedruckt wurde: E-Mail des Käufers aus den Rechnungsdaten (Kassenbelege)
        if (m.To.Count == 0 && p.Field("BUYER_CONTACT_EMAIL") is { } buyerMail && DocumentChecker.IsMail(buyerMail)) m.To.Add(buyerMail);

        // Texte aus dem ERP säubern (Umlaute, Sonderzeichen)
        var fixes = new List<string>();
        string Clean(string? text, string where)
        {
            var (t, c) = TextFixer.Fix(text);
            foreach (var x in c) fixes.Add($"{where}: {x}");
            return t;
        }
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, val) in p.Variables) custom[k] = Clean(val, $"Platzhalter {{{k}}}");

        // Vorlage und Absender
        var stationery = _stationery is null ? v.Stationery : PdfComposer.Select(p, _stationery.CurrentValue).Item?.Name ?? v.Stationery;
        var sender = p.Sender?.Trim();
        var (design, reason, warn) = MailDesigner.Select(Templates, p.MailTemplate, stationery, sender ?? o.FromAddress);
        m.Template = design?.Name;
        m.TemplateReason = reason;
        if (warn is not null) fixes.Add("!" + warn);
        m.FromAddress = !string.IsNullOrWhiteSpace(sender) ? sender : !string.IsNullOrWhiteSpace(design?.FromAddress) ? design!.FromAddress.Trim() : null;
        m.FromName = !string.IsNullOrWhiteSpace(design?.FromName) ? design!.FromName.Trim() : null;
        m.ReplyTo = !string.IsNullOrWhiteSpace(design?.ReplyTo) ? design!.ReplyTo.Trim() : null;

        var fileTemplate = p.FileName ?? p.MailFileName ?? o.DefaultFileName;
        var baseValues = Placeholders.Build(v, custom, null, m.FromName ?? o.FromName);
        var missing = new List<string>();
        m.FileName = MailTemplate.SafeFileName(Placeholders.Apply(Clean(fileTemplate, "Dateiname"), baseValues, missing));
        var values = Placeholders.Build(v, custom, m.FileName + ".pdf", m.FromName ?? o.FromName);

        var subject = !string.IsNullOrWhiteSpace(p.Subject) ? p.Subject!
            : !string.IsNullOrWhiteSpace(design?.Subject) ? design!.Subject : o.DefaultSubject;
        m.Subject = Placeholders.Apply(Clean(subject, "Betreff"), values, missing).Replace("\n", " ").Trim();
        string body;
        if (p.BodyLines.Count > 0) body = string.Join("\n", p.BodyLines);
        else if (p.BodyFile is not null && File.Exists(p.BodyFile))
        {
            try { body = TextFixer.ReadTextFile(p.BodyFile); }
            catch (Exception ex) { body = o.DefaultBody; fixes.Add($"!Textdatei {p.BodyFile} konnte nicht gelesen werden: {ex.Message}"); }
        }
        else body = !string.IsNullOrWhiteSpace(design?.Body) ? design!.Body : o.DefaultBody;
        m.Body = Placeholders.Apply(Clean(body, "Mailtext"), values, missing).Replace("\r\n", "\n");
        // Platzhalter in Signatur/Fußzeile der Vorlage prüfen
        if (design is not null)
        {
            Placeholders.Apply(design.Signature, values, missing);
            Placeholders.Apply(design.Footer, values, missing);
            Placeholders.Apply(design.HeaderText, values, missing);
        }
        m.Variables = values;
        m.TextFixes = fixes.Distinct().ToList();
        m.MissingPlaceholders = missing;

        m.Attachments = p.Attachments.ToList();
        foreach (var folder in p.AttachmentFolders)
            if (Directory.Exists(folder)) m.Attachments.AddRange(Directory.EnumerateFiles(folder).Take(20));
        v.Mail = m;
    }
}
