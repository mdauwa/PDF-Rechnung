using System.Net.Mail;

namespace PdfRechnung.Documents;

/// <summary>Prüft einen Vorgang: Versanddaten, Pflichtfelder der E-Rechnung, Summen, Einheiten, Format.</summary>
public static class DocumentChecker
{
    public const string Error = "fehler", Warning = "warnung", Info = "info";

    public static List<CheckMessage> Check(Vorgang v, ParsedCodes p, AppSettings settings)
    {
        var m = new List<CheckMessage>();
        void E(string text, string? field = null) => m.Add(new CheckMessage(Error, text, field));
        void W(string text, string? field = null) => m.Add(new CheckMessage(Warning, text, field));
        void I(string text, string? field = null) => m.Add(new CheckMessage(Info, text, field));

        // ---------- Versand ----------
        if (v.Mail.Enabled)
        {
            var viaCustomer = v.Deliveries.Count > 0;
            var customerMail = v.Deliveries.Any(d => d.Type == PdfRechnung.Delivery.ChannelType.Mail);
            if (v.Mail.To.Count == 0 && !viaCustomer) E("Kein E-Mail-Empfänger (@@to@@) – bitte im Versand eintragen.", "to");
            if (viaCustomer) I($"Versand über die Versandwege von {v.CustomerName}: {string.Join(", ", v.Deliveries.Select(d => d.Label))}.");
            foreach (var a in v.Mail.To.Concat(v.Mail.Cc).Concat(v.Mail.Bcc))
                if (!IsMail(a)) E($"Ungültige E-Mail-Adresse: {a}", "to");
            if (string.IsNullOrWhiteSpace(v.Mail.Subject)) W("Kein Betreff.", "subject");
            if (!settings.Mail.Enabled && (!viaCustomer || customerMail)) W("Der E-Mail-Versand ist in den Einstellungen noch nicht eingerichtet.");
        }
        else I("Für diesen Beleg ist kein Versand vorgesehen (@@ml0@@).");

        foreach (var att in v.Mail.Attachments)
            if (!File.Exists(att)) W($"Anlage nicht gefunden (am Server): {att}");
        if (p.BodyFile is not null && !File.Exists(p.BodyFile)) W($"Textdatei für den Mailtext nicht gefunden (am Server): {p.BodyFile}");
        foreach (var f in v.Mail.TextFixes)
            if (f.StartsWith('!')) W(f[1..]); else I("Text automatisch korrigiert – " + f);
        if (v.Mail.MissingPlaceholders.Count > 0)
            W($"Platzhalter ohne Wert: {string.Join(", ", v.Mail.MissingPlaceholders.Select(x => "{" + x + "}"))} – mit @@pv{v.Mail.MissingPlaceholders[0]}=…@@ übergeben oder einen Ersatztext angeben: {{{v.Mail.MissingPlaceholders[0]}|…}}.", "body");
        if (v.Mail.Template is not null) I($"Mail-Vorlage „{v.Mail.Template}“ ({v.Mail.TemplateReason}).");
        if (!string.IsNullOrWhiteSpace(v.Mail.FromAddress) && !IsMail(v.Mail.FromAddress)) E($"Ungültige Absenderadresse: {v.Mail.FromAddress}", "from");
        if (p.InvalidMandant is { } badM) W($"Mandant „{badM}“ (@@pm@@) ist ungültig – erwartet wird die Mandantennummer, z. B. @@pm10000@@.");
        if (p.InvalidErpIndex is { } badIdx) W($"Belegindex „{badIdx}“ (@@pi@@) ist ungültig – erwartet werden 11 Zeichen: Zeitraum, Archiv (A/N), Belegart und 8-stellige Belegnummer, z. B. @@pi0NR41261010@@. Der Status wird nicht ins ERP zurückgeschrieben.");
        if (p.InvalidOperatorNumber is { } bad) W($"Bedienernummer „{bad}“ (@@pb@@) ist ungültig – erwartet werden 3 Ziffern, z. B. @@pb007@@.");
        else if (p.OperatorNumber is { } no && v.OwnerUserId is null) W($"Bedienernummer {no} ist keinem Benutzer zugeordnet (Einstellungen → Benutzer → ERP-Bediener-Nr.).");
        else if (p.OperatorNumber is null && string.IsNullOrWhiteSpace(p.PrintUser) && v.ProcessOperator is { } pno && v.OwnerUserId is null)
            W($"ERP-Benutzer {pno} (aus dem druckenden Prozess) ist keinem Benutzer zugeordnet (Einstellungen → Benutzer → ERP-Bediener-Nr.).");
        m.AddRange(v.LayoutMessages);
        if (v.Stationery is not null) I($"Briefpapier „{v.Stationery}“ unterlegt{(p.StationeryFromPrinter is { } pn ? $" (Vorgabe des Druckers „{pn}“)" : "")}.");
        if (p.SplitAfterPages.Count > 0) W("Der Druckauftrag enthält mehrere Belege (@@nd@@) – das Aufteilen folgt mit dem Druckdienst.");
        if (p.RemovePages.Count > 0) I($"Seite(n) {string.Join(", ", p.RemovePages.Distinct())} sollen entfernt werden (@@rpc@@) – folgt mit dem Druckdienst.");
        if (p.AppendPdfs.Count > 0 || p.EmbedFiles.Count > 0) I("PDFs anhängen/Dateien einbetten (@@af@@/@@ef@@) folgt mit dem Druckdienst.");
        var undocumented = p.Undocumented.Where(c => c.Value.Trim().Length > 0).Select(c => $"@@{c.Code}{c.Value}@@").Distinct().ToList();
        if (undocumented.Count > 0) I($"Ohne Wirkung in Belegpost (bei gotomaxx nicht dokumentiert): {string.Join(" ", undocumented)}");
        foreach (var u in p.Unknown.Take(10)) W($"Unbekanntes Steuerzeichen auf Seite {u.Page}: @@{u.Code}{u.Value}@@");
        if (p.Unknown.Count > 10) W($"… und {p.Unknown.Count - 10} weitere unbekannte Steuerzeichen.");

        // ---------- E-Rechnung ----------
        var inv = v.Invoice;
        if (p.InvoiceFormat is null)
        {
            if (p.Fields.Count > 0 || p.Rows.Count > 0) W("Rechnungsdaten (@@xi@@/@@xr@@) ohne Formatangabe (@@xt@@) – es wird keine E-Rechnung erzeugt.");
            else if (v.Codes.Count == 0) W("Im Dokument wurden keine Steuerzeichen gefunden.");
            return m;
        }

        var fmt = InvoiceFormat.Find(p.InvoiceFormat);
        if (fmt is null) E($"Unbekanntes E-Rechnungsformat „{p.InvoiceFormat}“.");
        else if (!fmt.Supported) E($"{fmt.Name} wird noch nicht unterstützt – die E-Rechnung kann noch nicht erzeugt werden.");

        var profile = ERechnungProfile.For(p.InvoiceFormat);
        if (profile is not null) CheckFields(m, p, inv, profile, settings);
        if (p.PeppolSend) I("Peppol-Versand angefordert (@@pe1@@) – folgt in einem späteren Schritt.");

        var pm = p.Field("PAYMENT_MEANS_TYPE_CODE") ?? "30";
        // ---------- Positionen ----------
        if (inv.Lines.Count == 0) E("Keine Positionen (@@xr table=ITEM_DETAILS…@@).");
        foreach (var l in inv.Lines)
        {
            var id = l.LineId ?? "?";
            if (string.IsNullOrWhiteSpace(l.Name)) E($"Position {id}: Bezeichnung fehlt.");
            if (l.Quantity is null) E($"Position {id}: Menge fehlt oder ist nicht lesbar.");
            if (l.Total is null) E($"Position {id}: Positionsbetrag fehlt oder ist nicht lesbar.");
            if (l.Price is null) W($"Position {id}: Einzelpreis fehlt.");
            else if (l.Price < 0) E($"Position {id}: Einzelpreis ist negativ – EN 16931 erwartet einen positiven Preis (negative Menge verwenden).");
            if (l.VatPercent is null) E($"Position {id}: Steuersatz fehlt.");
            if (l.UnitText is null) W($"Position {id}: Mengeneinheit fehlt – es wird „Stück“ (H87) angenommen.");
            else if (l.UnitCode is null) W($"Position {id}: Mengeneinheit „{l.UnitText}“ ist keinem UN/ECE-Code zugeordnet (Einstellungen → E-Rechnung → Einheiten).");
            if (l.Quantity is { } q && l.Price is { } pr && l.Total is { } t)
            {
                var bq = l.BaseQuantity is > 0 ? l.BaseQuantity.Value : 1;
                var calc = InvoiceData.Round(q * pr / bq - (l.Allowance ?? 0));
                if (Math.Abs(calc - t) > settings.Invoice.SumTolerance)
                    W($"Position {id}: Menge × Preis{(l.Allowance is not null ? " − Rabatt" : "")} = {calc:N2}, gedruckt {t:N2}.");
            }
        }
        foreach (var vat in inv.Vat.Where(x => x.Percent == 0 && x.Category is "Z" or "E" or "AE" or "K" or "G"))
            if (p.Field("VAT_EXEPT_REASON_TEXT") is null)
                W($"Steuerkategorie {vat.Category} (0 %) ohne Befreiungsgrund (VAT_EXEPT_REASON_TEXT) – wird aus den Einstellungen ergänzt.");

        // ---------- Summen ----------
        var tol = settings.Invoice.SumTolerance;
        if (inv.PrintedGrossAmount is { } g)
        {
            if (Math.Abs(g - inv.GrossAmount) > tol)
                E($"Summenprüfung: gedruckter Bruttobetrag {g:N2}, nachgerechnet {inv.GrossAmount:N2} (Netto {inv.NetAmount:N2} + Steuer {inv.TaxAmount:N2}).", "GROSS_AMOUNT");
        }
        else if (inv.Lines.Count > 0)
            I($"Kein Bruttobetrag gedruckt (GROSS_AMOUNT) – nachgerechnet: {inv.GrossAmount:N2}.");
        if (inv.DuePayableAmount is { } due)
        {
            var expected = inv.GrossAmount - (inv.PrepaidAmount ?? 0);
            if (Math.Abs(due - expected) > tol) W($"Offener Betrag {due:N2} passt nicht zu Brutto − bereits bezahlt ({expected:N2}).", "DUE_PAYABLE_AMOUNT");
        }
        if (inv.DocumentTypeCode == "381" && inv.GrossAmount < 0)
            W("Gutschrift mit negativen Beträgen – EN 16931 erwartet bei Gutschriften (381) positive Beträge.");
        return m;
    }

    /// <summary>Kennzahlen, die Belegpost selbst nachrechnet, wenn sie nicht gedruckt sind.</summary>
    private static readonly HashSet<string> Computed = new(StringComparer.OrdinalIgnoreCase)
        { "LINE_TOTAL_AMOUNT", "NET_AMOUNT", "TAX_AMOUNT", "GROSS_AMOUNT", "DUE_PAYABLE_AMOUNT", "CHARGE_AMOUNT", "ALLOWANCE_AMOUNT" };

    /// <summary>Kopf- und Tabellenfelder gegen die gotomaxx-Liste prüfen (Pflicht je Format, Werteformat, unbekannte Felder).</summary>
    private static void CheckFields(List<CheckMessage> m, ParsedCodes p, InvoiceData inv, ERechnungProfile profile, AppSettings settings)
    {
        void E(string text, string? field = null) => m.Add(new CheckMessage(Error, text, field));
        void W(string text, string? field = null) => m.Add(new CheckMessage(Warning, text, field));
        void I(string text, string? field = null) => m.Add(new CheckMessage(Info, text, field));
        var fmt = profile.Name;
        var pm = p.Field("PAYMENT_MEANS_TYPE_CODE") ?? "30";
        var transfer = pm is "30" or "58" or "31" or "42";

        // ---------- Pflichtfelder im Kopf ----------
        var computed = new List<string>();
        var defaults = new List<string>();
        foreach (var f in ERechnungCatalog.Header.Where(profile.Requires))
        {
            if (p.Field(f.Name) is not null) continue;
            switch (f.Name)
            {
                case "SELLER_TAX_ID" or "SELLER_TAX_REG_ID":
                    continue; // entweder–oder, siehe unten
                case "SELLER_IBAN" or "SELLER_BIC":
                    if (!transfer || inv.DocumentTypeCode == "381") continue;
                    if (f.Name == "SELLER_BIC") { W($"BIC des Verkäufers fehlt (SELLER_BIC) – laut gotomaxx Pflicht bei {fmt}.", f.Name); continue; }
                    E($"Pflichtfeld fehlt: IBAN des Verkäufers (SELLER_IBAN) – Zahlungsart {pm} ist eine Überweisung.", f.Name);
                    continue;
                case "CURRENCY":
                    W("Keine Währung (CURRENCY) – es wird EUR angenommen.", f.Name);
                    continue;
            }
            if (Computed.Contains(f.Name)) { computed.Add(f.Name); continue; }
            if (f.Default is not null) { defaults.Add($"{f.Name}={f.Default}"); continue; }
            E($"Pflichtfeld fehlt ({fmt}): {f.Label} – @@xi {f.Name}=…@@", f.Name);
        }
        if (p.Field("SELLER_TAX_ID") is null && p.Field("SELLER_TAX_REG_ID") is null)
            E("Pflichtfeld fehlt: USt-IdNr. oder Steuernummer des Verkäufers (SELLER_TAX_ID / SELLER_TAX_REG_ID)", "SELLER_TAX_ID");
        if (defaults.Count > 0) I($"Nicht gedruckt, Vorbelegung laut gotomaxx: {string.Join(", ", defaults)}.");
        if (computed.Count > 0) I($"Nicht gedruckt, wird nachgerechnet: {string.Join(", ", computed)}.");

        // ---------- bedingte Pflichtfelder ----------
        var payee = p.Fields.Keys.Where(k => k.StartsWith("PAYEE_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (payee.Count > 0 && p.Field("PAYEE_NAME") is null) E("Abweichender Zahlungsempfänger ohne Namen (PAYEE_NAME, BR-17).", "PAYEE_NAME");
        if (payee.Any(k => k.StartsWith("PAYEE_ADDRESS", StringComparison.OrdinalIgnoreCase)) && p.Field("PAYEE_ADDRESS_COUNTRY") is null)
            E("Adresse des Zahlungsempfängers ohne Land (PAYEE_ADDRESS_COUNTRY).", "PAYEE_ADDRESS_COUNTRY");
        if (p.Field("PROJECT_REFERENCE_ID") is not null && p.Field("PROJECT_REFERENCE_NAME") is null)
            E("Projektnummer ohne Projektname (PROJECT_REFERENCE_NAME, Pflicht zu BT-11).", "PROJECT_REFERENCE_NAME");
        if (p.Field("DELIVERY_DATE") is null && p.Field("INVOICING_PERIOD_START_DATE") is null && !inv.Lines.Any(l => l.HasDate))
            W("Weder Lieferdatum (DELIVERY_DATE) noch Leistungszeitraum (INVOICING_PERIOD_…) – umsatzsteuerlich (§ 14 UStG) nötig.", "DELIVERY_DATE");

        // ---------- Werte und unbekannte Felder ----------
        var unknown = new List<string>();
        var notInFormat = new List<string>();
        foreach (var (name, value) in p.Fields)
        {
            var f = ERechnungCatalog.Field(name);
            if (f is null)
            {
                if (ERechnungCatalog.All.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    W($"{name} gehört in eine Tabelle (@@xr table=…@@), nicht in @@xi.", name);
                else unknown.Add(name);
                continue;
            }
            if (!profile.Has(f)) notInFormat.Add(name);
            CheckValue(f, value, null, E, W);
        }
        if (unknown.Count > 0) W($"Unbekannte Felder (Tippfehler?): {string.Join(", ", unknown.Select(u => $"@@xi {u}=…@@"))}");
        if (notInFormat.Count > 0) I($"In {fmt} nicht vorgesehen, wird nicht übertragen: {string.Join(", ", notInFormat)}.");

        // ---------- Tabellen ----------
        var unknownTables = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableNotInFormat = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableUnknownFields = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, idx) in p.Rows.Select((r, i) => (r, i)))
        {
            if (!ERechnungCatalog.IsTable(row.Table)) { unknownTables.Add(row.Table); continue; }
            var no = p.Rows.Take(idx).Count(r => r.Table.Equals(row.Table, StringComparison.OrdinalIgnoreCase)) + 1;
            var where = row.Get("ITEM_LINE_ID") is { } lid ? $"{row.Table} Position {lid}" : $"{row.Table} Zeile {no}";
            foreach (var (name, value) in row.Values)
            {
                var f = ERechnungCatalog.Field(name, row.Table);
                if (f is null) { tableUnknownFields.Add($"{row.Table}.{name}"); continue; }
                if (!profile.Has(f)) tableNotInFormat.Add($"{row.Table}.{name}");
                CheckValue(f, value, where, E, W);
            }
            if (row.Table.Equals("ITEM_DETAILS", StringComparison.OrdinalIgnoreCase) || row.Table.Equals("VAT", StringComparison.OrdinalIgnoreCase)) continue; // eigene Prüfung
            foreach (var f in ERechnungCatalog.TableFields(row.Table).Where(profile.Requires))
                if (row.Get(f.Name) is null) E($"{where}: Pflichtfeld fehlt ({fmt}): {f.Label} ({f.Name}).");
        }
        if (unknownTables.Count > 0) W($"Unbekannte Tabelle(n): {string.Join(", ", unknownTables)} (@@xr table=…@@).");
        if (tableUnknownFields.Count > 0) W($"Unbekannte Tabellenfelder (Tippfehler?): {string.Join(", ", tableUnknownFields)}.");
        if (tableNotInFormat.Count > 0) I($"In {fmt} nicht vorgesehen, wird nicht übertragen: {string.Join(", ", tableNotInFormat)}.");

        // ---------- gedruckte Steueraufschlüsselung (table=VAT) ----------
        var tol = settings.Invoice.SumTolerance;
        foreach (var r in p.Table("VAT"))
        {
            var pct = ControlCodeParser.Number(r.Get("VAT_PERCENT"));
            var cat = (r.Get("VAT_CATEGORY") ?? "S").ToUpperInvariant();
            var calc = inv.Vat.FirstOrDefault(x => x.Category == cat && x.Percent == (pct ?? 0));
            if (calc is null) { W($"Steuersatz {pct:0.##} % ({cat}) ist gedruckt (table=VAT), kommt in den Positionen aber nicht vor."); continue; }
            if (ControlCodeParser.Number(r.Get("BASIS_AMOUNT")) is { } b && Math.Abs(b - calc.Basis) > tol)
                W($"Steuersatz {pct:0.##} %: gedruckte Bemessungsgrundlage {b:N2}, nachgerechnet {calc.Basis:N2}.");
            if (ControlCodeParser.Number(r.Get("VAT_AMOUNT")) is { } t && Math.Abs(t - calc.Tax) > tol)
                W($"Steuersatz {pct:0.##} %: gedruckter Steuerbetrag {t:N2}, nachgerechnet {calc.Tax:N2}.");
        }
    }

    private static void CheckValue(ERechnungField f, string value, string? where, Action<string, string?> E, Action<string, string?> W)
    {
        var v = value.Trim();
        if (v.Length == 0) return;
        var at = where is null ? "" : where + ": ";
        switch (f.Type)
        {
            case "datum" when ControlCodeParser.Date(v) is null:
                E($"{at}{f.Text} ({f.Name}) ist kein Datum: „{v}“ – erwartet TT.MM.JJJJ.", f.Name); break;
            case "betrag" or "prozent" or "menge" when ControlCodeParser.Number(v) is null:
                E($"{at}{f.Text} ({f.Name}) ist keine Zahl: „{v}“.", f.Name); break;
        }
        if (f.Codelist?.StartsWith("ISO-3166", StringComparison.OrdinalIgnoreCase) == true && !(v.Length == 2 && v.All(char.IsAsciiLetterUpper)))
            E($"{at}{f.Text} ({f.Name}) „{v}“ ist kein Ländercode (ISO 3166 Alpha-2, z. B. AT, DE).", f.Name);
        if (f.Codelist?.StartsWith("ISO 4217", StringComparison.OrdinalIgnoreCase) == true && !(v.Length == 3 && v.All(char.IsAsciiLetterUpper)))
            E($"{at}{f.Text} ({f.Name}) „{v}“ ist kein Währungscode (ISO 4217, z. B. EUR).", f.Name);
    }

    public static bool IsMail(string s)
    {
        try { return MailAddress.TryCreate(s, out var a) && a.Address.Contains('.') && a.Address == s.Trim(); }
        catch { return false; }
    }
}
