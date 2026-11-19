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
                // Mehrere Zeilen an derselben Stelle übereinander (das ERP druckt z. B. alle Steuerzeichen auf dieselbe Position):
                // in Reihenfolge des Inhaltsstroms lesen und eine neue Zeile beginnen, sobald ein Buchstabe auf einem schon belegten Platz landet.
                var runs = new List<List<UglyToad.PdfPig.Content.Letter>>();
                var run = new List<UglyToad.PdfPig.Content.Letter>();
                var used = new List<(double From, double To)>();
                foreach (var l in g) // Reihenfolge wie im PDF
                {
                    var from = U(l); var to = l.EndBaseLine.X * cos + l.EndBaseLine.Y * sin;
                    var width = Math.Max(to - from, l.PointSize * 0.2);
                    if (!string.IsNullOrWhiteSpace(l.Value) && used.Any(u => Math.Min(u.To, from + width) - Math.Max(u.From, from) > width * 0.6))
                    {
                        runs.Add(run); run = new(); used.Clear();
                    }
                    run.Add(l);
                    if (!string.IsNullOrWhiteSpace(l.Value)) used.Add((from, from + width));
                }
                runs.Add(run);
                foreach (var r in runs.Where(r => r.Count > 0))
                {
                var sb = new System.Text.StringBuilder();
                var map = new List<UglyToad.PdfPig.Content.Letter?>();
                UglyToad.PdfPig.Content.Letter? prev = null;
                foreach (var l in r.OrderBy(U))
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
        IOptionsMonitor<MailTemplateOptions>? templates = null, Settings.UserStore? users = null, Services.EventHub? events = null,
        GotomaxxService? gotomaxx = null, IOptionsMonitor<MailRuleOptions>? rules = null, MailRuleStore? ruleStore = null,
        Services.MelderTasks? melderTasks = null, IOptionsMonitor<MandantOptions>? mandanten = null, PdfRechnung.Delivery.CustomerStore? customers = null)
    {
        _mandanten = mandanten; _customers = customers;
        _users = users; _events = events; _gotomaxx = gotomaxx; _rules = rules; _ruleStore = ruleStore; _melderTasks = melderTasks;
        _store = store; _mail = mail; _invoice = invoice; _log = log; _inbox = inbox; _stationery = stationery; _templates = templates;
        _stationeryDir = PdfComposer.StationeryDirectory(settings?.DataDirectory ?? Path.GetDirectoryName(store.Root)!);
    }

    private readonly IOptionsMonitor<StationeryOptions>? _stationery;
    private readonly IOptionsMonitor<MailTemplateOptions>? _templates;
    private readonly Settings.UserStore? _users;
    private readonly Services.EventHub? _events;
    private readonly GotomaxxService? _gotomaxx;
    private readonly IOptionsMonitor<MailRuleOptions>? _rules;
    private readonly MailRuleStore? _ruleStore;
    private readonly Services.MelderTasks? _melderTasks;

    /// <summary>
    /// Öffnen in der Windows-Sitzung am Server statt über den Melder (RDS). (Vorgang, outlook) → true = übernommen.
    /// Wird beim Start gesetzt (<see cref="Services.SessionLauncher"/>).
    /// </summary>
    public Func<Vorgang, bool, bool>? SessionOpen { get; set; }
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
            return EInvoice(v, r.Pdf);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Versand-PDF für {Id} konnte nicht erzeugt werden", v.Id);
            v.Stationery = null; v.RemovedCodes = 0;
            v.LayoutMessages = new() { new CheckMessage(DocumentChecker.Error, "Das Versand-PDF konnte nicht erzeugt werden: " + ex.Message) };
            return null;
        }
    }

    /// <summary>
    /// E-Rechnung erzeugen, wenn Belegpost das angeforderte Format kann: CII-XML aus den Rechnungsdaten, mit den
    /// KoSIT-/EN-16931-Regeln geprüft. ZUGFeRD: XML ins Versand-PDF eingebettet; XRechnung: XML als eigene Datei.
    /// Nicht bei Weiterleitung an gotomaxx (dort erzeugt gotomaxx die E-Rechnung).
    /// </summary>
    private byte[] EInvoice(Vorgang v, byte[] pdf)
    {
        v.EInvoiceProfile = null; v.PendingEInvoice = null;
        var fmt = InvoiceFormat.Find(v.FormatCode ?? v.Invoice.FormatCode);
        var profile = CiiWriter.ProfileFor(fmt?.Code);
        if (fmt is null || profile is null || v.Mail.Route == MailRoute.Gotomaxx) return pdf;
        if (!PdfRechnung.Licensing.License.Current.EInvoice)
        {
            v.LayoutMessages.Add(new CheckMessage(DocumentChecker.Warning, $"{fmt.Name} wird nicht erzeugt: " + PdfRechnung.Licensing.License.EInvoiceMessage));
            return pdf;
        }
        try
        {
            var xml = CiiWriter.Write(v.Invoice, new CiiWriter.Options(profile, v.Mail.To.FirstOrDefault()));
            var rep = Edi.EdiValidation.Validate(xml, DocumentStore.XmlFile);
            foreach (var i in rep.Issues.Where(i => i.Level is Edi.EdiReport.Fehler or Edi.EdiReport.Warnung))
                v.LayoutMessages.Add(new CheckMessage(i.Level == Edi.EdiReport.Fehler ? DocumentChecker.Error : DocumentChecker.Warning,
                    $"E-Rechnung: {i.Text}"));
            var errors = rep.Issues.Count(i => i.Level == Edi.EdiReport.Fehler);
            v.LayoutMessages.Add(new CheckMessage(DocumentChecker.Info, errors == 0
                ? $"{fmt.Name} erzeugt und geprüft ({rep.Scenario ?? "EN 16931"}): gültig."
                : $"{fmt.Name} erzeugt, die Prüfung meldet {errors} Fehler."));
            v.EInvoiceProfile = fmt.Name;
            v.PendingEInvoice = xml;
            if (fmt.Kind == "zugferd")
            {
                var title = string.Join(" ", new[] { InvoiceData.DocumentTypeName(v.Invoice.DocumentTypeCode), v.VoucherId }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return FacturX.Embed(pdf, xml, "EN 16931", title);
            }
            return pdf;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "E-Rechnung für {Id} konnte nicht erzeugt werden", v.Id);
            v.LayoutMessages.Add(new CheckMessage(DocumentChecker.Error, $"{fmt.Name} konnte nicht erzeugt werden: {ex.Message}"));
            return pdf;
        }
    }

    private readonly IOptionsMonitor<InboxOptions>? _inbox;
    private readonly IOptionsMonitor<MandantOptions>? _mandanten;
    private readonly PdfRechnung.Delivery.CustomerStore? _customers;

    /// <summary>Kunde und seine Versandwege ermitteln (für Prüfung und Anzeige).</summary>
    private void PlanDelivery(Vorgang v)
    {
        if (_customers is null) return;
        var plan = PdfRechnung.Delivery.DeliveryPlanner.Plan(_customers, v);
        PdfRechnung.Delivery.DeliveryPlanner.Apply(v, plan);
        if (plan.HasChannels && v.Mail.Enabled)
            v.Mail.RouteEffective = $"Versandwege von {plan.Customer!.Label}: {string.Join(", ", plan.Channels.Select(c => c.Label))}"
                + (v.Mail.RouteEffective is { Length: > 0 } r && !r.StartsWith("Versandwege von", StringComparison.Ordinal) ? " · " + r : "");
        if (plan.NoDelivery && v.Mail.Enabled)
        {
            v.Mail.Enabled = false;
            v.Mail.RouteEffective = $"kein Versand ({plan.Customer!.Label}, {plan.Rule})";
        }
    }

    /// <summary>Eingestellter Mandant eines Vorgangs (null = keiner bzw. nicht eingerichtet).</summary>
    public Mandant? MandantOf(Vorgang v) => _mandanten?.CurrentValue.Find(v.Mandant);

    /// <summary>Briefpapier des zusätzlichen Druckers, wenn der Druck kein @@bp@@ enthält.</summary>
    private ParsedCodes ApplyPrinter(ParsedCodes p, string? printerId)
    {
        if (p.StationerySet || _inbox?.CurrentValue.FindPrinter(printerId) is not { Stationery.Length: > 0 } pr) return p;
        p.StationerySet = true;
        p.Stationery = pr.Stationery.Trim() == "-" ? null : pr.Stationery.Trim();
        p.StationeryFromPrinter = pr.Name;
        return p;
    }

    private const string OwnerMandantReason = "Mandant des Benutzers";

    /// <summary>Mandant setzen: @@pm@@ im Beleg → Herkunft (Prozess /M:, Drucker, Eingangsordner).</summary>
    private void SetMandant(Vorgang v, ParsedCodes p)
    {
        if (v.MandantManual) return;
        var originReason = v.OriginMandantReason
            ?? (v.MandantReason is { } r && !r.StartsWith(OwnerMandantReason, StringComparison.Ordinal) ? r : null); // ältere Vorgänge
        v.OriginMandantReason ??= v.OriginMandant is not null ? originReason : null;
        v.Mandant = p.Mandant ?? v.OriginMandant;
        v.MandantReason = p.Mandant is not null ? "Steuerzeichen @@pm@@" : v.OriginMandant is not null ? originReason ?? "Herkunft" : null;
    }

    /// <summary>
    /// Ist aus Beleg und Herkunft kein eingerichteter Mandant bekannt, gilt der Mandant, dem der Besitzer (Portal-Benutzer)
    /// zugewiesen ist (bei mehreren der erste). Sonst bleibt es bei der allgemeinen ERP-Verbindung.
    /// </summary>
    private void ApplyOwnerMandant(Vorgang v)
    {
        if (v.MandantManual) return;
        var known = _mandanten?.CurrentValue;
        if (known is null || known.Find(v.Mandant) is not null) return;
        var owner = v.OwnerUserId is null ? null : _users?.FindById(v.OwnerUserId);
        var own = (owner?.Mandanten ?? new()).Select(m => known.Find(m)).Where(m => m is not null).ToList();
        if (own.Count == 0) return;
        var was = v.Mandant;
        v.Mandant = own[0]!.Number;
        v.MandantReason = $"{OwnerMandantReason} {owner!.Name}" + (own.Count > 1 ? $" (erster von {own.Count})" : "")
            + (was is not null ? $" – Mandant {was} aus dem Beleg ist nicht eingerichtet" : "");
    }

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

    private ParsedCodes Map(ParsedCodes p, string? originMandant = null)
    {
        var mandant = _mandanten?.CurrentValue.Find(p.Mandant ?? originMandant);
        var maps = (mandant?.PathMappings ?? new List<string>()).Concat(_inbox?.CurrentValue.PathMappings ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (maps.Count == 0) return p;
        if (p.BodyFile is not null) p.BodyFile = MapPath(p.BodyFile, maps);
        for (var i = 0; i < p.Attachments.Count; i++) p.Attachments[i] = MapPath(p.Attachments[i], maps);
        for (var i = 0; i < p.AttachmentFolders.Count; i++) p.AttachmentFolders[i] = MapPath(p.AttachmentFolders[i], maps);
        for (var i = 0; i < p.AppendPdfs.Count; i++) p.AppendPdfs[i] = MapPath(p.AppendPdfs[i], maps);
        for (var i = 0; i < p.EmbedFiles.Count; i++) p.EmbedFiles[i] = MapPath(p.EmbedFiles[i], maps);
        return p;
    }

    public Vorgang Process(byte[] pdf, string source, string? fileName, string? user = null, string? machine = null, string? ownerUserId = null,
        Action<Vorgang>? beforeSave = null, bool publish = true, string? processOperator = null, string? mapiSubject = null, List<string>? mapiTo = null,
        string? originMandant = null, string? originMandantReason = null, string? printerId = null)
    {
        var now = DateTimeOffset.Now;
        var v = new Vorgang { Id = DocumentStore.NewId(now), CreatedAt = now, Source = source, OriginalFileName = fileName, PrintUser = user, PrintMachine = machine,
            ProcessOperator = processOperator, MapiSubject = string.IsNullOrWhiteSpace(mapiSubject) ? null : mapiSubject.Trim(), MapiTo = mapiTo ?? new(),
            OriginMandant = MandantOptions.Normalize(originMandant), MandantReason = originMandantReason, OriginMandantReason = originMandantReason, PrinterId = printerId };
        string text;
        ParsedCodes p;
        try
        {
            var (pages, lines) = PdfText.Extract(pdf);
            v.Pages = pages;
            text = string.Join("\n", lines.Select(l => $"[{l.Page}] {l.Line}"));
            p = Map(ApplyPrinter(ControlCodeParser.Parse(lines), v.PrinterId), v.OriginMandant);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDF {File} konnte nicht gelesen werden", fileName);
            text = "";
            p = new ParsedCodes();
            v.Messages.Add(new CheckMessage(DocumentChecker.Error, "Das PDF konnte nicht gelesen werden: " + ex.Message));
        }

        SetMandant(v, p);
        AssignOwner(v, p, ownerUserId);
        ApplyOwnerMandant(v);
        v.SaveRequest = p.SaveFolder is null or "0" ? null : p.SaveFolder;
        v.PrintRequest = p.PrintPrinter is null or "0" ? null : p.PrintPrinter;
        v.SaveAfterSendRequest = p.SaveAfterSend is null or "0" ? null : p.SaveAfterSend;

        Fill(v, p, overwriteMail: true);
        var sendPdf = v.Messages.Count == 0 ? Compose(v, pdf, p) : null;
        var readOk = v.Messages.Count == 0;
        PlanDelivery(v);
        if (readOk) v.Messages = DocumentChecker.Check(v, p, CurrentSettings());
        v.UpdateStatus();
        v.History.Add(new HistoryEntry(now, user ?? source, "eingang",
            $"{source}{(fileName is null ? "" : $": {fileName}")} · {v.Pages} Seite(n) · {v.Codes.Count} Steuerzeichen"));
        if (beforeSave is not null)
        {
            beforeSave(v);
            if (v.Mail.Plain) v.Messages = v.Messages.Where(m => m.Field == "mapi").ToList();
            PlanDelivery(v);
            if (!v.Mail.Plain && readOk) v.Messages = DocumentChecker.Check(v, p, CurrentSettings()).Concat(v.Messages.Where(m => m.Field == "mapi")).ToList();
            v.UpdateStatus();
        }
        MelderJobsCheck(v);
        var forward = readOk && !v.Mail.Plain && Forward(v, p, user ?? source);
        var outlook = !forward && readOk && OutlookRoute(v);
        _store.Create(v, pdf, text, sendPdf);
        if (forward) _gotomaxx?.Enqueue(v.Id);
        if (outlook && v.Source == "Drucker" && SessionOpen?.Invoke(v, true) == true) { /* direkt in der Sitzung am Server */ }
        else if (outlook && _melderTasks is not null)
        {
            // Melder am PC bzw. in der RDS-Sitzung, an dem gedruckt wurde (sonst irgendein Melder des Benutzers)
            _melderTasks.Create(v.OwnerUserId!, _users?.DeviceOn(v.OwnerUserId!, v.PrintMachine)?.Id, Services.MelderTasks.KindOutlook, v.Id, TaskTitle(v));
        }
        CreateMelderJobs(v);
        _log.LogInformation("Vorgang {Id} angelegt: {Type} {No} → {Status}", v.Id, v.DocumentType, v.VoucherId, v.Status);
        if (publish && v.OwnerUserId is not null && _events is not null)
        {
            var title = string.Join(" ", new[] { v.DocumentType, v.VoucherId }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (title.Length == 0) title = v.OriginalFileName ?? "Neuer Beleg";
            if (!string.IsNullOrWhiteSpace(v.BuyerName)) title += " – " + v.BuyerName;
            var first = v.Messages.FirstOrDefault(m => m.Level == DocumentChecker.Error)?.Text ?? v.Messages.FirstOrDefault(m => m.Level == DocumentChecker.Warning)?.Text;
            _events.Publish(v.OwnerUserId, "neu", v.Id, title, v.Status, first, v.Mail.MelderMayOpenOutlook);
            // RDS ohne Melder: neuen Druck direkt im Browser der Sitzung zeigen (Belegpost-Drucker, nicht MAPI)
            if (!outlook && v.Source == "Drucker") SessionOpen?.Invoke(v, false);
        }
        return v;
    }

    private static string TaskTitle(Vorgang v)
    {
        var title = string.Join(" ", new[] { v.DocumentType, v.VoucherId }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return title.Length > 0 ? title : v.OriginalFileName ?? v.Id;
    }

    /// <summary>@@pa@@ / @@pp@@: Hinweise, wenn Speichern bzw. Drucken über den Melder nicht möglich ist.</summary>
    private void MelderJobsCheck(Vorgang v)
    {
        if (v.SaveRequest is null && v.PrintRequest is null && v.SaveAfterSendRequest is null) return;
        var what = v.PrintRequest is null ? "Speichern" : v.SaveRequest is null && v.SaveAfterSendRequest is null ? "Drucken" : "Speichern und Drucken";
        string? problem = v.OwnerUserId is null ? "der Beleg ist keinem Portal-Benutzer zugeordnet"
            : _users is not null && _users.Devices(v.OwnerUserId).Count == 0 ? $"für {v.OwnerName ?? "den Benutzer"} ist kein Belegpost Melder gekoppelt" : null;
        if (problem is not null)
            v.Messages.Add(new CheckMessage(DocumentChecker.Info, $"{what} über den Melder (@@pa@@/@@pn@@/@@pp@@) nicht möglich: {problem}.", "melder"));
    }

    /// <summary>@@pa@@ / @@pp@@: Aufträge an den Melder – laufen zusätzlich zu Mail, Outlook oder gotomaxx.</summary>
    private void CreateMelderJobs(Vorgang v)
    {
        if (v.SaveAfterSendRequest is not null && v.OwnerUserId is not null)
            _store.Update(v.Id, x => { x.History.Add(new HistoryEntry(DateTimeOffset.Now, "Steuerzeichen", "melder",
                "Speichern über den Melder nach dem Senden angefordert" + (x.SaveAfterSendRequest!.Length > 0 ? $" (Ordner „{x.SaveAfterSendRequest}“)" : ""))); return true; });
        if (_melderTasks is null || v.OwnerUserId is null || (v.SaveRequest is null && v.PrintRequest is null)) return;
        var device = _users?.DeviceOn(v.OwnerUserId, v.PrintMachine)?.Id;
        var title = TaskTitle(v);
        if (v.SaveRequest is not null) _melderTasks.Create(v.OwnerUserId, device, Services.MelderTasks.KindSave, v.Id, title, v.SaveRequest);
        if (v.PrintRequest is not null) _melderTasks.Create(v.OwnerUserId, device, Services.MelderTasks.KindPrint, v.Id, title, v.PrintRequest);
        _store.Update(v.Id, x =>
        {
            if (x.SaveRequest is not null) x.History.Add(new HistoryEntry(DateTimeOffset.Now, "Steuerzeichen", "melder",
                "Speichern über den Melder angefordert" + (x.SaveRequest.Length > 0 ? $" (Ordner „{x.SaveRequest}“)" : "")));
            if (x.PrintRequest is not null) x.History.Add(new HistoryEntry(DateTimeOffset.Now, "Steuerzeichen", "melder",
                "Drucken über den Melder angefordert" + (x.PrintRequest.Length > 0 ? $" (Drucker „{x.PrintRequest}“)" : " (Standarddrucker)")));
            return true;
        });
    }

    /// <summary>
    /// Weiterleitung an gotomaxx (@@pg@@ oder Einstellung „immer“): Die Belegpost-Prüfung entfällt, weil gotomaxx
    /// den Beleg selbst verarbeitet und versendet. Liefert true, wenn der Vorgang zur Übergabe vorgemerkt wurde.
    /// </summary>
    private bool Forward(Vorgang v, ParsedCodes p, string by)
    {
        var o = _gotomaxx?.Options ?? new GotomaxxOptions();
        var target = GotomaxxCodes.Target(p, o);
        var why = target is null ? null : target.Length > 0 ? $"Weiterleitung an den gotomaxx-Drucker „{target}“ (@@pg@@)"
            : p.GotomaxxSet ? "Weiterleitung an den gotomaxx-Standarddrucker (@@pg@@)" : "Weiterleitung an gotomaxx (Einstellung „jeden Beleg“)";
        // Versandweg gotomaxx aus Regel oder @@pw@@ (außer @@pg0@@)
        if (target is null && v.Mail.Route == MailRoute.Gotomaxx && !(p.GotomaxxSet && p.GotomaxxPrinter == "0"))
        {
            if (o.Enabled) { target = v.Mail.RoutePrinter ?? ""; why = $"Weiterleitung an gotomaxx ({v.Mail.RouteReason})"; }
            else
            {
                v.Messages.Add(new CheckMessage(DocumentChecker.Error, $"Versandweg gotomaxx ({v.Mail.RouteReason}), aber die Weiterleitung an gotomaxx ist nicht eingerichtet (Einstellungen → Drucker & Belege → gotomaxx)."));
                v.UpdateStatus();
                return false;
            }
        }
        if (target is "" && MandantOf(v) is { GotomaxxPrinter.Length: > 0 } gm) target = gm.GotomaxxPrinter.Trim(); // Standarddrucker des Mandanten
        if (target is { } waitFor && o.ApproveFirst)
        {
            // Erst prüfen und auf Freigabe warten – „An gotomaxx übergeben“ im Portal druckt dann auf diesen Drucker
            v.GotomaxxPrinter = waitFor;
            v.Mail.Route = MailRoute.Gotomaxx;
            v.Mail.RouteReason = why!.Replace("Weiterleitung an ", "");
            v.Mail.RouteEffective = $"{MailRoute.Text(MailRoute.Gotomaxx)} nach Freigabe ({v.Mail.RouteReason})";
            v.Messages = DocumentChecker.Check(v, p, CurrentSettings()).Concat(v.Messages.Where(m => m.Field == "mapi")).ToList();
            v.Messages.Insert(0, new CheckMessage(DocumentChecker.Info,
                $"Wartet auf Freigabe – danach Übergabe an den gotomaxx-Drucker „{(waitFor.Length > 0 ? waitFor : o.Printer)}“ ({why})."));
            v.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "gotomaxx", $"{why} – wartet auf Freigabe im Portal"));
            v.UpdateStatus();
            return false;
        }
        if (target is { } printer)
        {
            v.Messages = v.Messages.Where(m => m.Field == "mapi").ToList();
            GotomaxxService.Mark(v, printer, by, why!);
            return true;
        }
        if (p.GotomaxxSet && p.GotomaxxPrinter != "0" && !o.Enabled)
        {
            v.Messages.Add(new CheckMessage(DocumentChecker.Error, "@@pg@@: Die Weiterleitung an gotomaxx ist angefordert, aber nicht eingerichtet (Einstellungen → Drucker & Belege → gotomaxx)."));
            v.UpdateStatus();
        }
        return false;
    }

    /// <summary>Versandweg Outlook (Regel/@@pw@@): Entwurf über den Melder des Benutzers – prüft, ob das geht.</summary>
    private bool OutlookRoute(Vorgang v)
    {
        if (v.Mail.Route != MailRoute.Outlook || v.Mail.Plain || !v.Mail.Enabled || v.Source == "MAPI") return false;
        var owner = v.OwnerUserId is null ? null : _users?.FindById(v.OwnerUserId);
        string? problem = owner is null ? "der Beleg ist keinem Portal-Benutzer zugeordnet"
            : !owner.SendViaOutlook ? $"Senden über Outlook ist für {owner.Name} nicht freigeschaltet"
            : v.Status == VorgangStatus.Error ? "der Beleg hat Fehler" : null;
        if (problem is not null)
        {
            v.Messages.Add(new CheckMessage(DocumentChecker.Warning, $"Versandweg Outlook ({v.Mail.RouteReason}) nicht möglich: {problem} – der Beleg wartet im Portal auf Freigabe."));
            v.UpdateStatus();
            return false;
        }
        v.History.Add(new HistoryEntry(DateTimeOffset.Now, "Regeln", "outlook", $"Wird in Outlook bei {owner!.Name} geöffnet ({v.Mail.RouteReason})"));
        return true;
    }

    /// <summary>
    /// Wem gehört der Beleg? Reihenfolge: @@pb@@ (Bedienernummer) → @@pu@@ (Name) → Benutzernummer des druckenden
    /// Prozesses (z. B. wwa.exe der WebWARE) → Benutzer des Druckauftrags → beim Upload der hochladende Benutzer.
    /// </summary>
    private void AssignOwner(Vorgang v, ParsedCodes p, string? fallbackUserId)
    {
        if (!string.IsNullOrWhiteSpace(p.PrintUser)) v.PrintUser = p.PrintUser.Trim();
        // Steuerzeichen haben Vorrang; sonst die Benutzernummer aus dem druckenden Prozess
        v.OperatorNumber = p.OperatorNumber ?? (string.IsNullOrWhiteSpace(p.PrintUser) ? v.ProcessOperator : null);
        v.ErpIndex = p.ErpIndex ?? (p.Field("ERP_VOUCHER_INDEX") is { } xi ? ControlCodeParser.NormalizeErpIndex(xi) : null) ?? v.ErpIndex;
        var owner = (p.OperatorNumber is not null ? _users?.FindByOperator(p.OperatorNumber) : null)
            ?? (!string.IsNullOrWhiteSpace(p.PrintUser) ? _users?.FindByPrintName(p.PrintUser) : null)
            ?? (p.OperatorNumber is null && string.IsNullOrWhiteSpace(p.PrintUser) && v.ProcessOperator is not null ? _users?.FindByOperator(v.ProcessOperator) : null)
            ?? (fallbackUserId is not null ? _users?.FindById(fallbackUserId) : null)
            ?? _users?.FindByPrintName(v.PrintUser);
        v.OwnerUserId = owner?.Id ?? fallbackUserId;
        v.OwnerName = owner?.Name;
        v.OwnerEmail = owner?.Email;
    }

    /// <summary>Prüfung wiederholen (z. B. nach geänderten Einstellungen oder Versanddaten).</summary>
    /// <summary>Rechnungsdaten aus Steuerzeichen mit den eingestellten Einheiten.</summary>
    public InvoiceData InvoiceFrom(ParsedCodes p) => InvoiceData.From(p, _invoice.CurrentValue.Units);

    /// <summary>Steuerzeichen wie gedruckt (ohne Korrekturen) – für den Rechnungsdaten-Editor.</summary>
    public ParsedCodes? OriginalCodes(string id)
    {
        var path = _store.FilePath(id, DocumentStore.PdfFile);
        if (path is null) return null;
        var stored = _store.Get(id);
        try { return Map(ApplyPrinter(ControlCodeParser.Parse(PdfText.Extract(File.ReadAllBytes(path)).Lines), stored?.PrinterId), stored?.OriginMandant); }
        catch { return new ParsedCodes(); }
    }

    public Vorgang? Recheck(string id, string by)
    {
        var path = _store.FilePath(id, DocumentStore.PdfFile);
        if (path is null) return null;
        if (_store.Get(id) is { Mail.Plain: true } plain) return plain; // normale E-Mail: nichts zu prüfen
        ParsedCodes p;
        var original = File.ReadAllBytes(path);
        var stored = _store.Get(id);
        try { p = Map(ApplyPrinter(ControlCodeParser.Parse(PdfText.Extract(original).Lines), stored?.PrinterId), stored?.OriginMandant); }
        catch { p = new ParsedCodes(); }
        return _store.Update(id, v =>
        {
            (v.Corrections ??= new()).Apply(p); // Korrekturen aus dem Portal
            // Zuordnung neu (z. B. Bedienernummer inzwischen einem Benutzer zugewiesen); Upload-Besitzer bleibt als Rückfall
            SetMandant(v, p);
            AssignOwner(v, p, v.Source == "Upload" ? v.OwnerUserId : null);
            ApplyOwnerMandant(v);
            Fill(v, p, overwriteMail: !v.Mail.Edited);
            _store.WriteFile(id, DocumentStore.SendFile, Compose(v, original, p));
            _store.WriteFile(id, DocumentStore.XmlFile, v.PendingEInvoice);
            if (v.Deliveries.All(d => d.Status != "ok")) { v.Deliveries.Clear(); PlanDelivery(v); }
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
        if (v.Mail.Plain) { v.Messages = v.Messages.Where(m => m.Field == "mapi").ToList(); v.UpdateStatus(); return; }
        var path = _store.FilePath(v.Id, DocumentStore.PdfFile);
        ParsedCodes p;
        try { p = path is null ? new ParsedCodes() : Map(ApplyPrinter(ControlCodeParser.Parse(PdfText.Extract(File.ReadAllBytes(path)).Lines), v.PrinterId), v.OriginMandant); }
        catch { p = new ParsedCodes(); }
        v.Messages = DocumentChecker.Check(v, p, CurrentSettings());
        v.UpdateStatus();
    }

    /// <summary>gotomaxx-SMTP-Profil (@@sp@@) als Absender, wenn es eine Mailadresse ist.</summary>
    private static string? SmtpSender(ParsedCodes p) =>
        p.SmtpProfile?.Trim() is { Length: > 0 } sp && System.Net.Mail.MailAddress.TryCreate(sp, out _) && sp.Contains('@') ? sp : null;

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

        // Vorlage, Absender, Signatur, Versandweg: Steuerzeichen → eigene Regeln → globale Regeln → Zuordnung → Standard
        var stationery = _stationery is null ? v.Stationery : PdfComposer.Select(p, _stationery.CurrentValue).Item?.Name ?? v.Stationery;
        var userRules = _ruleStore?.Get(v.OwnerUserId);
        var globalRules = _rules?.CurrentValue;
        var ctx = new MailRuleContext(
            MailRuleEngine.GuessDocumentType(string.IsNullOrEmpty(inv.DocumentTypeCode) ? null : v.DocumentType, p.MailFileName, p.FileName, p.Subject, v.OriginalFileName),
            stationery, p.Sender?.Trim() ?? SmtpSender(p) ?? o.FromAddress, v.MapiTo.Count > 0 ? v.MapiTo : m.To, v.BuyerId, v.BuyerName, v.Source, p.InvoiceFormat,
            string.Join(" ", new[] { v.MapiSubject, p.Subject, p.MailFileName, p.FileName, v.OriginalFileName }.Where(x => !string.IsNullOrWhiteSpace(x))),
            v.OwnerUserId);
        var rr = MailRuleEngine.Evaluate(ctx, userRules, globalRules);
        m.RuleNotes = new List<string>();
        m.AppliedRules = rr.Applied.ToList();

        // Absender für den Serverversand: @@pf@@ → Regel → Einstellung des Benutzers → Vorlage → Standard
        var owner = v.OwnerUserId is null ? null : _users?.FindById(v.OwnerUserId);
        var own = owner?.SenderFor(v.OperatorNumber) ?? default;
        var sender = p.Sender?.Trim();
        if (string.IsNullOrEmpty(sender) && SmtpSender(p) is { } sp) { sender = sp; m.RuleNotes.Add($"Absender {sp} aus @@sp@@ (gotomaxx-SMTP-Profil)"); }
        if (string.IsNullOrEmpty(sender) && rr.FromAddress is { } rf) { sender = rf.Text; m.RuleNotes.Add($"Absender {rf.Text} aus {rf.Reason}"); }
        if (string.IsNullOrEmpty(sender) && own.From is { } of) { sender = of; m.RuleNotes.Add($"Absender {of} ({own.Source})"); }
        var (design, reason, warn) = !string.IsNullOrWhiteSpace(p.MailTemplate) || rr.Template is null
            ? MailDesigner.Select(Templates, p.MailTemplate, stationery, sender ?? o.FromAddress)
            : MailDesigner.Select(Templates, rr.Template.Text, stationery, sender ?? o.FromAddress, rr.Template.Reason);
        m.Template = design?.Name;
        m.TemplateReason = reason;
        if (warn is not null) fixes.Add("!" + warn);
        m.FromAddress = !string.IsNullOrWhiteSpace(sender) ? sender : !string.IsNullOrWhiteSpace(design?.FromAddress) ? design!.FromAddress.Trim() : null;
        m.FromName = rr.FromName?.Text ?? own.Name ?? (!string.IsNullOrWhiteSpace(design?.FromName) ? design!.FromName.Trim() : null);
        m.ReplyTo = rr.ReplyTo?.Text ?? own.ReplyTo ?? (!string.IsNullOrWhiteSpace(design?.ReplyTo) ? design!.ReplyTo.Trim() : null);
        if (rr.ReplyTo is null && own.ReplyTo is { } ownReply) m.RuleNotes.Add($"Antwort an {ownReply} ({own.Source})");
        if (rr.FromName is { } fnr) m.RuleNotes.Add($"Absendername aus {fnr.Reason}");
        if (rr.ReplyTo is { } rtr) m.RuleNotes.Add($"Antwort an {rtr.Text} aus {rtr.Reason}");
        // CC/BCC aus Regeln nur, wenn keine gedruckt wurden
        if (m.Cc.Count == 0 && rr.Cc.Count > 0) { m.Cc = rr.Cc.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); m.RuleNotes.Add($"CC aus {rr.CcReason}"); }
        if (m.Bcc.Count == 0 && rr.Bcc.Count > 0) { m.Bcc = rr.Bcc.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); m.RuleNotes.Add($"BCC aus {rr.BccReason}"); }

        // Signatur: @@ps@@ → Regel → Benutzer (Briefpapier, Absender, Standard) → global → Vorlage
        if (p.SignatureSet && p.Signature is null) { m.SignatureName = null; m.SignatureText = ""; m.SignatureReason = "Steuerzeichen @@ps@@ (keine Signatur)"; }
        else
        {
            var (req, reqWhy) = p.Signature is { } ps ? (ps, $"Steuerzeichen @@ps{ps}@@") : rr.Signature is { } rs ? (rs.Text, rs.Reason) : ((string?)null, (string?)null);
            var (sig, sigWhy, sigWarn) = MailRuleEngine.SelectSignature(req, reqWhy, stationery, sender ?? o.FromAddress, userRules, globalRules);
            m.SignatureName = sig?.Name; m.SignatureText = sig?.Text; m.SignatureReason = sigWhy;
            if (sigWarn is not null) fixes.Add("!" + sigWarn);
        }

        // Versandweg: @@pw@@ → Regel
        if (p.Route is { } pr) { m.Route = pr; m.RouteReason = $"Steuerzeichen @@pw{pr}@@"; }
        else if (rr.Route is { } rro) { m.Route = rro.Text; m.RouteReason = rro.Reason; m.RoutePrinter = rr.GotomaxxPrinter?.Text; }
        if (p.InvalidRoute is { } ir) fixes.Add($"!Versandweg „{ir}“ (@@pw@@) ist unbekannt – erlaubt sind direkt, wennok, freigabe, outlook, gotomaxx, keine.");
        if (m.Route == MailRoute.None && p.MailEnabled is null) m.Enabled = false;
        m.RouteEffective = m.Route.Length > 0 ? $"{MailRoute.Text(m.Route)} ({m.RouteReason})"
            : !m.Enabled ? "keine Mail (@@ml0@@ bzw. Einstellung)"
            : owner?.SendViaOutlook == true ? $"{owner.Name} sendet selbst über Outlook – kein Autoversand"
            : o.AutoSend ? $"Autoversand nach {o.AutoSendDelayMinutes} Min. (Einstellung „E-Mail & Versand“)"
            : "nach Freigabe im Portal (Autoversand ist aus)";

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
        var bodyTemplate = Clean(body, "Mailtext");
        m.Body = (MailHtml.IsHtml(bodyTemplate)
            ? MailHtml.Sanitize(Placeholders.Apply(bodyTemplate, values.ToDictionary(kv => kv.Key, kv => System.Net.WebUtility.HtmlEncode(kv.Value), StringComparer.OrdinalIgnoreCase), missing))
            : Placeholders.Apply(bodyTemplate, values, missing)).Replace("\r\n", "\n");
        // Platzhalter in Signatur/Fußzeile der Vorlage prüfen
        if (design is not null)
        {
            Placeholders.Apply(m.SignatureText ?? design.Signature, values, missing);
            Placeholders.Apply(design.Footer, values, missing);
            Placeholders.Apply(design.HeaderText, values, missing);
        }
        else if (!string.IsNullOrEmpty(m.SignatureText)) Placeholders.Apply(m.SignatureText, values, missing);
        m.Variables = values;
        m.TextFixes = fixes.Distinct().ToList();
        m.MissingPlaceholders = missing;

        m.Attachments = p.Attachments.ToList();
        foreach (var folder in p.AttachmentFolders)
            if (Directory.Exists(folder)) m.Attachments.AddRange(Directory.EnumerateFiles(folder).Take(20));
        v.Mail = m;
    }
}
