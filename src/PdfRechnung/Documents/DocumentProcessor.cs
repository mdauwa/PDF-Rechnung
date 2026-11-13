using System.Globalization;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PdfRechnung.Documents;

/// <summary>Text eines PDFs zeilenweise je Seite (für die Steuerzeichen).</summary>
public static class PdfText
{
    public static (int Pages, List<(int Page, string Line)> Lines) Extract(byte[] pdf)
    {
        var lines = new List<(int, string)>();
        using var doc = PdfDocument.Open(pdf);
        foreach (var page in doc.GetPages())
        {
            string text;
            try { text = ContentOrderTextExtractor.GetText(page); }
            catch { text = page.Text; }
            foreach (var l in text.Split('\n'))
                lines.Add((page.Number, l.TrimEnd('\r')));
            // Fallback: Steuerzeichen, die der Layout-Extraktor zerlegt hat, im Rohtext der Seite suchen
            if (!text.Contains("@@") && page.Text.Contains("@@"))
                lines.Add((page.Number, page.Text));
        }
        return (doc.NumberOfPages, lines);
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

    public DocumentProcessor(DocumentStore store, IOptionsMonitor<MailOptions> mail, IOptionsMonitor<InvoiceOptions> invoice, ILogger<DocumentProcessor> log)
    {
        _store = store; _mail = mail; _invoice = invoice; _log = log;
    }

    public Vorgang Process(byte[] pdf, string source, string? fileName, string? user = null, string? machine = null)
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
            p = ControlCodeParser.Parse(lines);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDF {File} konnte nicht gelesen werden", fileName);
            text = "";
            p = new ParsedCodes();
            v.Messages.Add(new CheckMessage(DocumentChecker.Error, "Das PDF konnte nicht gelesen werden: " + ex.Message));
        }

        Fill(v, p, overwriteMail: true);
        if (v.Messages.Count == 0) v.Messages = DocumentChecker.Check(v, p, CurrentSettings());
        v.UpdateStatus();
        v.History.Add(new HistoryEntry(now, user ?? source, "eingang",
            $"{source}{(fileName is null ? "" : $": {fileName}")} · {v.Pages} Seite(n) · {v.Codes.Count} Steuerzeichen"));
        _store.Create(v, pdf, text);
        _log.LogInformation("Vorgang {Id} angelegt: {Type} {No} → {Status}", v.Id, v.DocumentType, v.VoucherId, v.Status);
        return v;
    }

    /// <summary>Prüfung wiederholen (z. B. nach geänderten Einstellungen oder Versanddaten).</summary>
    public Vorgang? Recheck(string id, string by)
    {
        var path = _store.FilePath(id, DocumentStore.PdfFile);
        if (path is null) return null;
        ParsedCodes p;
        try { p = ControlCodeParser.Parse(PdfText.Extract(File.ReadAllBytes(path)).Lines); }
        catch { p = new ParsedCodes(); }
        return _store.Update(id, v =>
        {
            Fill(v, p, overwriteMail: !v.Mail.Edited);
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
        try { p = path is null ? new ParsedCodes() : ControlCodeParser.Parse(PdfText.Extract(File.ReadAllBytes(path)).Lines); }
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
        v.Stationery = p.Stationery;

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
        m.Subject = !string.IsNullOrWhiteSpace(p.Subject) ? p.Subject! : MailTemplate.Apply(o.DefaultSubject, v);
        var body = p.BodyLines.Count > 0 ? string.Join("\n", p.BodyLines)
            : p.BodyFile is not null && File.Exists(p.BodyFile) ? File.ReadAllText(p.BodyFile)
            : MailTemplate.Apply(o.DefaultBody, v);
        m.Body = body.Replace("\r\n", "\n");
        m.Attachments = p.Attachments.ToList();
        foreach (var folder in p.AttachmentFolders)
            if (Directory.Exists(folder)) m.Attachments.AddRange(Directory.EnumerateFiles(folder).Take(20));
        m.FileName = MailTemplate.SafeFileName(p.FileName ?? MailTemplate.Apply(o.DefaultFileName, v));
        v.Mail = m;
    }
}
