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
            if (v.Mail.To.Count == 0) E("Kein E-Mail-Empfänger (@@to@@) – bitte im Versand eintragen.", "to");
            foreach (var a in v.Mail.To.Concat(v.Mail.Cc).Concat(v.Mail.Bcc))
                if (!IsMail(a)) E($"Ungültige E-Mail-Adresse: {a}", "to");
            if (string.IsNullOrWhiteSpace(v.Mail.Subject)) W("Kein Betreff.", "subject");
            if (!settings.Mail.Enabled) W("Der E-Mail-Versand ist in den Einstellungen noch nicht eingerichtet.");
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
        if (p.InvalidOperatorNumber is { } bad) W($"Bedienernummer „{bad}“ (@@pb@@) ist ungültig – erwartet werden 3 Ziffern, z. B. @@pb007@@.");
        else if (p.OperatorNumber is { } no && v.OwnerUserId is null) W($"Bedienernummer {no} ist keinem Benutzer zugeordnet (Einstellungen → Benutzer → ERP-Bediener-Nr.).");
        m.AddRange(v.LayoutMessages);
        if (v.Stationery is not null) I($"Briefpapier „{v.Stationery}“ unterlegt.");
        if (p.SplitAfterPages.Count > 0) W("Der Druckauftrag enthält mehrere Belege (@@nd@@) – das Aufteilen folgt mit dem Druckdienst.");
        if (p.RemovePages.Count > 0) I($"Seite(n) {string.Join(", ", p.RemovePages.Distinct())} sollen entfernt werden (@@rpc@@) – folgt mit dem Druckdienst.");
        if (p.AppendPdfs.Count > 0 || p.EmbedFiles.Count > 0) I("PDFs anhängen/Dateien einbetten (@@af@@/@@ef@@) folgt mit dem Druckdienst.");
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

        void Req(string field, string label)
        {
            if (p.Field(field) is null) E($"Pflichtfeld fehlt: {label} ({field})", field);
        }
        Req("VOUCHER_ID", "Rechnungsnummer");
        Req("VOUCHER_DATE", "Rechnungsdatum");
        Req("SELLER_NAME", "Verkäufer Name");
        Req("SELLER_ADDRESS_ZIP", "Verkäufer PLZ");
        Req("SELLER_ADDRESS_CITY", "Verkäufer Ort");
        Req("SELLER_ADDRESS_COUNTRY", "Verkäufer Land");
        Req("BUYER_NAME", "Käufer Name");
        Req("BUYER_ADDRESS_COUNTRY", "Käufer Land");
        if (p.Field("SELLER_TAX_ID") is null && p.Field("SELLER_TAX_REG_ID") is null)
            E("Pflichtfeld fehlt: USt-IdNr. oder Steuernummer des Verkäufers (SELLER_TAX_ID / SELLER_TAX_REG_ID)", "SELLER_TAX_ID");
        if (p.Field("CURRENCY") is null) W("Keine Währung (CURRENCY) – es wird EUR angenommen.", "CURRENCY");
        if (p.Field("VOUCHER_DATE") is { } vd && ControlCodeParser.Date(vd) is null) E($"Rechnungsdatum nicht lesbar: {vd}", "VOUCHER_DATE");
        if (p.Field("DELIVERY_DATE") is { } dd && ControlCodeParser.Date(dd) is null) W($"Lieferdatum nicht lesbar: {dd}", "DELIVERY_DATE");

        if (fmt?.Kind is "xrechnung" or "peppol")
        {
            if (p.Field("ROUTING_ID") is null && fmt.Kind == "xrechnung") E("XRechnung: Leitweg-ID / Käuferreferenz fehlt (ROUTING_ID).", "ROUTING_ID");
            if (p.Field("SELLER_CONTACT_EMAIL") is null) E("XRechnung: E-Mail des Verkäufers fehlt (SELLER_CONTACT_EMAIL).", "SELLER_CONTACT_EMAIL");
            if (p.Field("SELLER_CONTACT_PHONE") is null) E("XRechnung: Telefon des Verkäufers fehlt (SELLER_CONTACT_PHONE).", "SELLER_CONTACT_PHONE");
            if (p.Field("SELLER_ENDPOINT_ID") is null) W("Elektronische Adresse des Verkäufers fehlt (SELLER_ENDPOINT_ID).", "SELLER_ENDPOINT_ID");
            if (p.Field("BUYER_ENDPOINT_ID") is null) E("Elektronische Adresse des Käufers fehlt (BUYER_ENDPOINT_ID).", "BUYER_ENDPOINT_ID");
        }
        if (p.PeppolSend) I("Peppol-Versand angefordert (@@pe1@@) – folgt in einem späteren Schritt.");

        var pm = p.Field("PAYMENT_MEANS_TYPE_CODE");
        if ((pm is null or "58" or "30") && p.Field("SELLER_IBAN") is null && inv.DocumentTypeCode != "381")
            W("Keine IBAN des Verkäufers (SELLER_IBAN).", "SELLER_IBAN");
        if (pm == "59" && p.Field("PAYMENT_DIRECTDEBIT_MANDATE_ID") is null) E("SEPA-Lastschrift ohne Mandatsreferenz (PAYMENT_DIRECTDEBIT_MANDATE_ID).");

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

    public static bool IsMail(string s)
    {
        try { return MailAddress.TryCreate(s, out var a) && a.Address.Contains('.') && a.Address == s.Trim(); }
        catch { return false; }
    }
}
