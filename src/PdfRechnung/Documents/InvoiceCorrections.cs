namespace PdfRechnung.Documents;

/// <summary>
/// Korrekturen an den gedruckten Rechnungsdaten (Portal → „Rechnungsdaten bearbeiten“).
/// Erlaubt sind Stammdaten und technische Angaben – Mengen, Preise, Steuersätze und Summen bleiben wie gedruckt,
/// damit E-Rechnung und Sichtkopie (PDF) übereinstimmen. Die Korrekturen liegen getrennt von den gedruckten Werten,
/// überstehen „Neu prüfen“ und werden vor jeder Prüfung auf die Steuerzeichen angewendet.
/// </summary>
public sealed class InvoiceCorrections
{
    /// <summary>Kopffeld → Wert. Leerer Wert = Feld entfernen.</summary>
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Positionskorrekturen je Position (Index in der Reihenfolge des Drucks, ab 0).</summary>
    public Dictionary<int, LineCorrection> Lines { get; set; } = new();
    public string? ChangedBy { get; set; }
    public DateTimeOffset? ChangedAt { get; set; }

    public bool IsEmpty => Fields.Count == 0 && Lines.Values.All(l => l.IsEmpty);

    public sealed record FieldDef(string Key, string Label, string Group, string Type = "text", string? Help = null);

    /// <summary>Bearbeitbare Kopffelder (alles andere ist gesperrt).</summary>
    public static readonly IReadOnlyList<FieldDef> Editable = new FieldDef[]
    {
        new("DOCUMENT_TYPE", "Belegart", "Beleg", "documentType"),
        new("ROUTING_ID", "Leitweg-ID / Käuferreferenz (BT-10)", "Beleg", Help: "Bei XRechnung Pflicht, für Behörden die Leitweg-ID."),
        new("BUYER_ORDER_ID", "Bestellnummer des Käufers (BT-13)", "Beleg"),
        new("DELIVERY_DATE", "Liefer-/Leistungsdatum", "Beleg", "date"),
        new("INVOICE_NOTE", "Notiz (BT-22)", "Beleg", "textarea"),

        new("SELLER_NAME", "Name", "Verkäufer"),
        new("SELLER_ADDRESS_LINE", "Straße", "Verkäufer"),
        new("SELLER_ADDRESS_ZIP", "PLZ", "Verkäufer"),
        new("SELLER_ADDRESS_CITY", "Ort", "Verkäufer"),
        new("SELLER_ADDRESS_COUNTRY", "Land (ISO, z. B. AT)", "Verkäufer", "country"),
        new("SELLER_TAX_ID", "USt-IdNr.", "Verkäufer"),
        new("SELLER_TAX_REG_ID", "Steuernummer", "Verkäufer"),
        new("SELLER_CONTACT_NAME", "Ansprechpartner", "Verkäufer"),
        new("SELLER_CONTACT_PHONE", "Telefon", "Verkäufer"),
        new("SELLER_CONTACT_EMAIL", "E-Mail", "Verkäufer", "email"),
        new("SELLER_ENDPOINT_ID", "Elektronische Adresse (BT-34)", "Verkäufer", Help: "Leer = E-Mail des Ansprechpartners."),
        new("SELLER_ENDPOINT_TYPE", "Art der elektronischen Adresse", "Verkäufer", "endpointType"),

        new("BUYER_ID", "Kundennummer", "Käufer"),
        new("BUYER_NAME", "Name", "Käufer"),
        new("BUYER_ADDRESS_LINE", "Straße", "Käufer"),
        new("BUYER_ADDRESS_ZIP", "PLZ", "Käufer"),
        new("BUYER_ADDRESS_CITY", "Ort", "Käufer"),
        new("BUYER_ADDRESS_COUNTRY", "Land (ISO, z. B. AT)", "Käufer", "country"),
        new("BUYER_TAX_ID", "USt-IdNr.", "Käufer"),
        new("BUYER_CONTACT_EMAIL", "E-Mail", "Käufer", "email"),
        new("BUYER_ENDPOINT_ID", "Elektronische Adresse (BT-49)", "Käufer", Help: "Leer = E-Mail des Käufers bzw. Mailempfänger. Für Peppol die Peppol-ID."),
        new("BUYER_ENDPOINT_TYPE", "Art der elektronischen Adresse", "Käufer", "endpointType"),

        new("SHIPPING_ADDRESS_NAME", "Name", "Lieferadresse"),
        new("SHIPPING_ADDRESS_LINE", "Straße", "Lieferadresse"),
        new("SHIPPING_ADDRESS_ZIP", "PLZ", "Lieferadresse"),
        new("SHIPPING_ADDRESS_CITY", "Ort", "Lieferadresse"),
        new("SHIPPING_ADDRESS_COUNTRY", "Land (ISO)", "Lieferadresse", "country"),

        new("PAYMENT_MEANS_TYPE_CODE", "Zahlungsart", "Zahlung", "paymentMeans"),
        new("SELLER_IBAN", "IBAN (Zahlungsempfänger)", "Zahlung"),
        new("SELLER_BIC", "BIC", "Zahlung"),
        new("PAYMENT_TERMS_DUE_DATE", "Fälligkeitsdatum", "Zahlung", "date"),
        new("PAYMENT_TERMS_DESCRIPTION", "Zahlungsbedingung (Text)", "Zahlung"),
        new("PAYMENT_DIRECTDEBIT_MANDATE_ID", "Mandatsreferenz (Lastschrift)", "Zahlung"),
        new("PAYMENT_DIRECTDEBIT_REFERENCE_ID", "Gläubiger-ID (Lastschrift)", "Zahlung"),
        new("PAYMENT_DIRECTDEBIT_IBAN", "IBAN des Käufers (Lastschrift)", "Zahlung"),

        new("VAT_EXEPT_REASON_TEXT", "Befreiungsgrund bei 0 % (BT-120)", "Steuer", "textarea",
            "Nur bei Steuerkategorie Z, E, AE, K, G. Leer = Standardtext je Kategorie."),
    };

    public static bool IsEditable(string key) => Editable.Any(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Korrekturen auf die gelesenen Steuerzeichen anwenden (vor Prüfung und Erzeugung der E-Rechnung).</summary>
    public void Apply(ParsedCodes p)
    {
        foreach (var (k, val) in Fields)
        {
            if (!IsEditable(k)) continue;
            if (string.IsNullOrWhiteSpace(val)) p.Fields.Remove(k); else p.Fields[k] = val.Trim();
        }
        var items = p.Table("ITEM_DETAILS").ToList();
        foreach (var (i, c) in Lines)
        {
            if (i < 0 || i >= items.Count) continue;
            var r = items[i];
            if (c.Name is not null) r.Values["ITEM_NAME"] = c.Name;
            if (c.Description is not null) r.Values["ITEM_DESCRIPTION"] = c.Description;
            if (c.SellerId is not null) r.Values["ITEM_SELLER_ID"] = c.SellerId;
            if (!string.IsNullOrWhiteSpace(c.UnitCode)) r.Values["ITEM_UNIT"] = c.UnitCode.Trim().ToUpperInvariant();
            // Steuerkategorie nur bei 0 % (z. B. Z → AE), damit sich keine Beträge ändern
            if (!string.IsNullOrWhiteSpace(c.VatCategory) && (ControlCodeParser.Number(r.Get("ITEM_VAT_PERCENT")) ?? 0) == 0)
                r.Values["ITEM_VAT_CATEGORY"] = c.VatCategory.Trim().ToUpperInvariant();
        }
    }
}

public sealed class LineCorrection
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SellerId { get; set; }
    /// <summary>UN/ECE-Einheitencode (Rec. 20), z. B. H87, HUR, C62.</summary>
    public string? UnitCode { get; set; }
    /// <summary>Steuerkategorie – nur bei 0 % änderbar (Z, E, AE, K, G, O).</summary>
    public string? VatCategory { get; set; }
    public bool IsEmpty => Name is null && Description is null && SellerId is null && string.IsNullOrWhiteSpace(UnitCode) && string.IsNullOrWhiteSpace(VatCategory);
}
