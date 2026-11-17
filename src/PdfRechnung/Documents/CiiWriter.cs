using System.Globalization;
using System.Text;
using System.Xml;

namespace PdfRechnung.Documents;

/// <summary>
/// Erzeugt die E-Rechnung als UN/CEFACT Cross Industry Invoice (CII, D16B/D22B) aus den gedruckten Rechnungsdaten:
/// ZUGFeRD 2.x / Factur-X Profil EN 16931 („Comfort“) und XRechnung 3 (CII).
/// Feldzuordnung siehe „Steuerzeichen zu ZUGFeRD-XML“ (Projekt-Doku).
/// </summary>
public static class CiiWriter
{
    public const string ProfileEn16931 = "urn:cen.eu:en16931:2017";
    public const string ProfileXRechnung3 = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";

    private const string Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private const string Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private const string Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    private const string Qdt = "urn:un:unece:uncefact:data:standard:QualifiedDataType:100";

    /// <summary>Standardtexte für den Befreiungsgrund (BT-120) je Steuerkategorie.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExemptionTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["E"] = "Steuerbefreit",
        ["AE"] = "Übergang der Steuerschuld auf den Leistungsempfänger (Reverse Charge)",
        ["K"] = "Innergemeinschaftliche Lieferung, steuerfrei",
        ["G"] = "Ausfuhrlieferung, steuerfrei",
        ["O"] = "Nicht steuerbar",
    };

    /// <summary>Welche Formate Belegpost selbst erzeugt (Kind = zugferd: XML im PDF, xrechnung: XML als eigene Datei).</summary>
    public static string? ProfileFor(string? formatCode) => formatCode?.Trim().ToLowerInvariant() switch
    {
        "gotomaxx.invoice.zugferd2.comfort" => ProfileEn16931,
        "gotomaxx.invoice.xrechnung2.cii" or "gotomaxx.invoice.xrechnung3.cii" => ProfileXRechnung3,
        _ => null
    };

    public sealed record Options(string Profile, string? BuyerEmailFallback = null);

    public static byte[] Write(InvoiceData d, Options o)
    {
        string? F(string key) => d.Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var xr = o.Profile == ProfileXRechnung3;
        var cur = F("CURRENCY") ?? "EUR";

        var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("rsm", "CrossIndustryInvoice", Rsm);
            w.WriteAttributeString("xmlns", "qdt", null, Qdt);
            w.WriteAttributeString("xmlns", "ram", null, Ram);
            w.WriteAttributeString("xmlns", "udt", null, Udt);

            // ---- Kontext und Dokument ----
            w.WriteStartElement("rsm", "ExchangedDocumentContext", Rsm);
            w.WriteStartElement("ram", "GuidelineSpecifiedDocumentContextParameter", Ram);
            El(w, "ID", o.Profile);
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("rsm", "ExchangedDocument", Rsm);
            El(w, "ID", F("VOUCHER_ID") ?? "");
            El(w, "TypeCode", d.DocumentTypeCode ?? F("DOCUMENT_TYPE") ?? "380");
            Date(w, "IssueDateTime", F("VOUCHER_DATE"));
            if (F("INVOICE_NOTE") is { } note) { w.WriteStartElement("ram", "IncludedNote", Ram); El(w, "Content", note); w.WriteEndElement(); }
            w.WriteEndElement();

            w.WriteStartElement("rsm", "SupplyChainTradeTransaction", Rsm);

            // ---- Positionen ----
            var lineNo = 0;
            foreach (var l in d.Lines)
            {
                lineNo++;
                w.WriteStartElement("ram", "IncludedSupplyChainTradeLineItem", Ram);
                w.WriteStartElement("ram", "AssociatedDocumentLineDocument", Ram);
                El(w, "LineID", LineId(l.LineId, lineNo));
                w.WriteEndElement();

                w.WriteStartElement("ram", "SpecifiedTradeProduct", Ram);
                if (Clean(l.ArticleNo) is { } art) El(w, "SellerAssignedID", art);
                El(w, "Name", Clean(l.Name) ?? Clean(l.Description) ?? $"Position {lineNo}");
                if (Clean(l.Description) is { } desc && Clean(l.Name) is not null) El(w, "Description", desc);
                w.WriteEndElement();

                var (qty, price) = QuantityAndPrice(l);
                var bq = l.BaseQuantity is > 0 ? l.BaseQuantity.Value : 1m;
                var unit = l.UnitCode ?? "H87";
                w.WriteStartElement("ram", "SpecifiedLineTradeAgreement", Ram);
                w.WriteStartElement("ram", "NetPriceProductTradePrice", Ram);
                El(w, "ChargeAmount", Num(price, 4));
                if (bq != 1m) { w.WriteStartElement("ram", "BasisQuantity", Ram); w.WriteAttributeString("unitCode", unit); w.WriteString(Num(bq, 4)); w.WriteEndElement(); }
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("ram", "SpecifiedLineTradeDelivery", Ram);
                w.WriteStartElement("ram", "BilledQuantity", Ram); w.WriteAttributeString("unitCode", unit); w.WriteString(Num(qty, 4)); w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("ram", "SpecifiedLineTradeSettlement", Ram);
                Tax(w, Cat(l.VatCategory), l.VatPercent ?? 0, null, null, null);
                if (l.Allowance is { } al && al != 0)
                {
                    w.WriteStartElement("ram", "SpecifiedTradeAllowanceCharge", Ram);
                    Indicator(w, al < 0);
                    El(w, "ActualAmount", Num(Math.Abs(al)));
                    El(w, "Reason", Clean(l.AllowanceReason) ?? "Rabatt");
                    w.WriteEndElement();
                }
                w.WriteStartElement("ram", "SpecifiedTradeSettlementLineMonetarySummation", Ram);
                El(w, "LineTotalAmount", Num(l.Total ?? 0));
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteEndElement();
            }

            // ---- Kopf: Vereinbarung (Verkäufer, Käufer) ----
            w.WriteStartElement("ram", "ApplicableHeaderTradeAgreement", Ram);
            var buyerRef = F("ROUTING_ID") ?? (xr ? F("BUYER_ID") ?? F("VOUCHER_ID") : null);
            if (buyerRef is not null) El(w, "BuyerReference", buyerRef);

            w.WriteStartElement("ram", "SellerTradeParty", Ram);
            El(w, "Name", F("SELLER_NAME") ?? "");
            if (F("SELLER_CONTACT_NAME") is not null || F("SELLER_CONTACT_PHONE") is not null || F("SELLER_CONTACT_EMAIL") is not null)
            {
                w.WriteStartElement("ram", "DefinedTradeContact", Ram);
                if (F("SELLER_CONTACT_NAME") is { } cn) El(w, "PersonName", cn);
                if (F("SELLER_CONTACT_PHONE") is { } ph) { w.WriteStartElement("ram", "TelephoneUniversalCommunication", Ram); El(w, "CompleteNumber", ph); w.WriteEndElement(); }
                if (F("SELLER_CONTACT_EMAIL") is { } em) { w.WriteStartElement("ram", "EmailURIUniversalCommunication", Ram); El(w, "URIID", em); w.WriteEndElement(); }
                w.WriteEndElement();
            }
            Address(w, F("SELLER_ADDRESS_ZIP"), F("SELLER_ADDRESS_LINE"), F("SELLER_ADDRESS_CITY"), F("SELLER_ADDRESS_COUNTRY"));
            Endpoint(w, F("SELLER_ENDPOINT_ID") ?? F("SELLER_CONTACT_EMAIL"), F("SELLER_ENDPOINT_TYPE"));
            if (F("SELLER_TAX_ID") is { } vat) TaxReg(w, "VA", vat);
            if (F("SELLER_TAX_REG_ID") is { } fc) TaxReg(w, "FC", fc);
            w.WriteEndElement();

            w.WriteStartElement("ram", "BuyerTradeParty", Ram);
            if (F("BUYER_ID") is { } bid) El(w, "ID", bid);
            El(w, "Name", F("BUYER_NAME") ?? "");
            Address(w, F("BUYER_ADDRESS_ZIP"), F("BUYER_ADDRESS_LINE"), F("BUYER_ADDRESS_CITY"), F("BUYER_ADDRESS_COUNTRY"));
            Endpoint(w, F("BUYER_ENDPOINT_ID") ?? F("BUYER_CONTACT_EMAIL") ?? Clean(o.BuyerEmailFallback), F("BUYER_ENDPOINT_TYPE"));
            if (F("BUYER_TAX_ID") is { } bvat) TaxReg(w, "VA", bvat);
            w.WriteEndElement();
            if (F("BUYER_ORDER_ID") is { } order)
            {
                w.WriteStartElement("ram", "BuyerOrderReferencedDocument", Ram);
                El(w, "IssuerAssignedID", order);
                w.WriteEndElement();
            }
            w.WriteEndElement();

            // ---- Lieferung ----
            w.WriteStartElement("ram", "ApplicableHeaderTradeDelivery", Ram);
            if (F("SHIPPING_ADDRESS_NAME") is { } sn)
            {
                w.WriteStartElement("ram", "ShipToTradeParty", Ram);
                El(w, "Name", sn);
                Address(w, F("SHIPPING_ADDRESS_ZIP"), F("SHIPPING_ADDRESS_LINE"), F("SHIPPING_ADDRESS_CITY"), F("SHIPPING_ADDRESS_COUNTRY"));
                w.WriteEndElement();
            }
            if (ParseDate(F("DELIVERY_DATE")) is not null)
            {
                w.WriteStartElement("ram", "ActualDeliverySupplyChainEvent", Ram);
                Date(w, "OccurrenceDateTime", F("DELIVERY_DATE"));
                w.WriteEndElement();
            }
            w.WriteEndElement();

            // ---- Abrechnung ----
            w.WriteStartElement("ram", "ApplicableHeaderTradeSettlement", Ram);
            var means = F("PAYMENT_MEANS_TYPE_CODE") ?? "58";
            if (means == "59" && F("PAYMENT_DIRECTDEBIT_REFERENCE_ID") is { } cred) El(w, "CreditorReferenceID", cred);
            El(w, "PaymentReference", F("VOUCHER_ID") ?? "");
            El(w, "InvoiceCurrencyCode", cur);

            w.WriteStartElement("ram", "SpecifiedTradeSettlementPaymentMeans", Ram);
            El(w, "TypeCode", means);
            if (means == "59")
            {
                if (F("PAYMENT_DIRECTDEBIT_IBAN") is { } diban) { w.WriteStartElement("ram", "PayerPartyDebtorFinancialAccount", Ram); El(w, "IBANID", Iban(diban)); w.WriteEndElement(); }
            }
            else if (F("SELLER_IBAN") is { } iban)
            {
                w.WriteStartElement("ram", "PayeePartyCreditorFinancialAccount", Ram); El(w, "IBANID", Iban(iban)); w.WriteEndElement();
                if (F("SELLER_BIC") is { } bic) { w.WriteStartElement("ram", "PayeeSpecifiedCreditorFinancialInstitution", Ram); El(w, "BICID", bic); w.WriteEndElement(); }
            }
            w.WriteEndElement();

            foreach (var v in d.Vat)
                Tax(w, Cat(v.Category), v.Percent, v.Tax, v.Basis, ExemptionFor(Cat(v.Category)) is { } ex ? F("VAT_EXEPT_REASON_TEXT") ?? ex : null);

            foreach (var c in d.Charges)
            {
                w.WriteStartElement("ram", "SpecifiedTradeAllowanceCharge", Ram);
                Indicator(w, c.IsCharge);
                El(w, "ActualAmount", Num(c.Amount ?? 0));
                El(w, "Reason", Clean(c.Reason) ?? (c.IsCharge ? "Zuschlag" : "Abschlag"));
                w.WriteStartElement("ram", "CategoryTradeTax", Ram);
                El(w, "TypeCode", "VAT");
                El(w, "CategoryCode", Cat(c.VatCategory));
                El(w, "RateApplicablePercent", Num(c.VatPercent ?? 0));
                w.WriteEndElement();
                w.WriteEndElement();
            }

            var terms = PaymentTermsText(d, F("PAYMENT_TERMS_DESCRIPTION"), xr);
            var due = ParseDate(F("PAYMENT_TERMS_DUE_DATE"));
            var mandate = means == "59" ? F("PAYMENT_DIRECTDEBIT_MANDATE_ID") : null;
            if (terms is not null || due is not null || mandate is not null)
            {
                w.WriteStartElement("ram", "SpecifiedTradePaymentTerms", Ram);
                if (terms is not null) El(w, "Description", terms);
                if (due is not null) Date(w, "DueDateDateTime", F("PAYMENT_TERMS_DUE_DATE"));
                if (mandate is not null) El(w, "DirectDebitMandateID", mandate);
                w.WriteEndElement();
            }

            var prepaid = d.PrepaidAmount ?? 0;
            w.WriteStartElement("ram", "SpecifiedTradeSettlementHeaderMonetarySummation", Ram);
            El(w, "LineTotalAmount", Num(d.LineTotal));
            if (d.ChargeTotal != 0) El(w, "ChargeTotalAmount", Num(d.ChargeTotal));
            if (d.AllowanceTotal != 0) El(w, "AllowanceTotalAmount", Num(d.AllowanceTotal));
            El(w, "TaxBasisTotalAmount", Num(d.NetAmount));
            w.WriteStartElement("ram", "TaxTotalAmount", Ram); w.WriteAttributeString("currencyID", cur); w.WriteString(Num(d.TaxAmount)); w.WriteEndElement();
            El(w, "GrandTotalAmount", Num(d.GrossAmount));
            if (prepaid != 0) El(w, "TotalPrepaidAmount", Num(prepaid));
            El(w, "DuePayableAmount", Num(d.GrossAmount - prepaid));
            w.WriteEndElement();

            w.WriteEndElement(); // Settlement
            w.WriteEndElement(); // Transaction
            w.WriteEndElement(); // Invoice
            w.WriteEndDocument();
        }
        return ms.ToArray();
    }

    // ---------- Hilfen ----------

    private static void El(XmlWriter w, string name, string value) => w.WriteElementString("ram", name, Ram, value);

    private static void Indicator(XmlWriter w, bool charge)
    {
        w.WriteStartElement("ram", "ChargeIndicator", Ram);
        w.WriteElementString("udt", "Indicator", Udt, charge ? "true" : "false");
        w.WriteEndElement();
    }

    private static void Date(XmlWriter w, string name, string? value)
    {
        w.WriteStartElement("ram", name, Ram);
        w.WriteStartElement("udt", "DateTimeString", Udt);
        w.WriteAttributeString("format", "102");
        w.WriteString((ParseDate(value) ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Address(XmlWriter w, string? zip, string? line, string? city, string? country)
    {
        w.WriteStartElement("ram", "PostalTradeAddress", Ram);
        if (zip is not null) El(w, "PostcodeCode", zip);
        if (line is not null) El(w, "LineOne", line);
        if (city is not null) El(w, "CityName", city);
        El(w, "CountryID", (country ?? "AT").ToUpperInvariant());
        w.WriteEndElement();
    }

    private static void Endpoint(XmlWriter w, string? id, string? type)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        w.WriteStartElement("ram", "URIUniversalCommunication", Ram);
        w.WriteStartElement("ram", "URIID", Ram);
        w.WriteAttributeString("schemeID", string.IsNullOrWhiteSpace(type) ? (id.Contains('@') ? "EM" : "0088") : type.Trim());
        w.WriteString(id.Trim());
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void TaxReg(XmlWriter w, string scheme, string id)
    {
        w.WriteStartElement("ram", "SpecifiedTaxRegistration", Ram);
        w.WriteStartElement("ram", "ID", Ram);
        w.WriteAttributeString("schemeID", scheme);
        w.WriteString(id.Replace(" ", ""));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Tax(XmlWriter w, string cat, decimal pct, decimal? tax, decimal? basis, string? exemption)
    {
        w.WriteStartElement("ram", "ApplicableTradeTax", Ram);
        if (tax is not null) El(w, "CalculatedAmount", Num(tax.Value));
        El(w, "TypeCode", "VAT");
        if (exemption is not null) El(w, "ExemptionReason", exemption);
        if (basis is not null) El(w, "BasisAmount", Num(basis.Value));
        El(w, "CategoryCode", cat);
        if (cat != "O") El(w, "RateApplicablePercent", Num(pct));
        w.WriteEndElement();
    }

    private static string? ExemptionFor(string cat) => ExemptionTexts.TryGetValue(cat, out var t) ? t : null;

    private static string Cat(string? c) => string.IsNullOrWhiteSpace(c) ? "S" : c.Trim().ToUpperInvariant();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Iban(string s) => s.Replace(" ", "").ToUpperInvariant();

    private static string LineId(string? id, int no)
    {
        var t = Clean(id);
        if (t is null) return no.ToString(CultureInfo.InvariantCulture);
        // ERP druckt „  1.00“ – als Positionsnummer genügt „1“
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n == Math.Truncate(n)
            ? ((long)n).ToString(CultureInfo.InvariantCulture) : t;
    }

    /// <summary>EN 16931 verlangt einen positiven Nettopreis (BR-27): ein negativer Preis wird zu negativer Menge.</summary>
    private static (decimal Qty, decimal Price) QuantityAndPrice(InvoiceLine l)
    {
        var q = l.Quantity ?? 1; var p = l.Price ?? 0;
        if (p < 0) { p = -p; q = -q; }
        if (l.Price is null && q != 0)
        {
            var bq = l.BaseQuantity is > 0 ? l.BaseQuantity.Value : 1m;
            p = Math.Abs(((l.Total ?? 0) + (l.Allowance ?? 0)) / q * bq);
        }
        return (q, p);
    }

    private static string? PaymentTermsText(InvoiceData d, string? description, bool xrechnung)
    {
        var parts = new List<string>();
        if (description is not null) parts.Add(description);
        foreach (var t in d.PaymentTerms)
        {
            string? G(string k) => t.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            var days = ControlCodeParser.Number(G("PAYMENT_TERMS_TABLE_DAYS"));
            var pct = ControlCodeParser.Number(G("PAYMENT_TERMS_TABLE_PERCENT"));
            if (days is null || pct is null) continue;
            var basis = ControlCodeParser.Number(G("PAYMENT_TERMS_TABLE_BASEAMOUNT"));
            // XRechnung-Skontozeile (Dokumentation XRechnung, Kapitel Skonto)
            parts.Add(xrechnung
                ? $"#SKONTO#TAGE={days:0}#PROZENT={Num(pct.Value)}#{(basis is null ? "" : $"BASISBETRAG={Num(basis.Value)}#")}"
                : $"{Num(pct.Value).Replace('.', ',')} % Skonto bei Zahlung innerhalb von {days:0} Tagen");
        }
        if (parts.Count == 0) return null;
        // Skontozeilen in XRechnung müssen je in einer eigenen Zeile stehen und mit Zeilenumbruch enden
        return xrechnung ? string.Join("\n", parts) + (parts.Any(x => x.StartsWith("#SKONTO")) ? "\n" : "") : string.Join(" – ", parts);
    }

    private static string Num(decimal d, int decimals = 2)
    {
        var r = Math.Round(d, decimals, MidpointRounding.AwayFromZero);
        var s = r.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
        return decimals == 2 ? r.ToString("0.00", CultureInfo.InvariantCulture) : s;
    }

    public static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        foreach (var f in new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "yyyyMMdd", "dd.MM.yy" })
            if (DateOnly.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return null;
    }
}
