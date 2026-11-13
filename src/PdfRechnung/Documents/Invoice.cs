namespace PdfRechnung.Documents;

/// <summary>E-Rechnungsformat, wie es das ERP per <c>@@xt@@</c> anfordert.</summary>
public sealed record InvoiceFormat(string Code, string Name, string Kind, bool Supported)
{
    /// <summary>Alle Formate, die die ERP-Vorlage (SEBELK) anfordern kann.</summary>
    public static readonly IReadOnlyList<InvoiceFormat> All = new[]
    {
        new InvoiceFormat("gotomaxx.invoice.zugferd2.comfort", "ZUGFeRD 2 Comfort (EN 16931)", "zugferd", false),
        new InvoiceFormat("gotomaxx.invoice.zugferd2.extended", "ZUGFeRD 2 Extended", "zugferd", false),
        new InvoiceFormat("gotomaxx.invoice.zugferd.basic", "ZUGFeRD 1 Basic", "zugferd", false),
        new InvoiceFormat("gotomaxx.invoice.zugferd.comfort", "ZUGFeRD 1 Comfort", "zugferd", false),
        new InvoiceFormat("gotomaxx.invoice.zugferd.extended", "ZUGFeRD 1 Extended", "zugferd", false),
        new InvoiceFormat("gotomaxx.invoice.xrechnung.ubl", "XRechnung (UBL)", "xrechnung", false),
        new InvoiceFormat("gotomaxx.invoice.xrechnung2.cii", "XRechnung 2 (CII)", "xrechnung", false),
        new InvoiceFormat("gotomaxx.invoice.xrechnung3.cii", "XRechnung 3 (CII)", "xrechnung", false),
        new InvoiceFormat("gotomaxx.invoice.peppol.bis3.ubl", "Peppol BIS Billing 3", "peppol", false),
    };

    public static InvoiceFormat? Find(string? code) =>
        code is null ? null : All.FirstOrDefault(f => f.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class InvoiceLine
{
    public string? LineId { get; set; }
    public string? ArticleNo { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public string? UnitText { get; set; }
    public string? UnitCode { get; set; }
    public decimal? BaseQuantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? Allowance { get; set; }
    public string? AllowanceReason { get; set; }
    public decimal? VatPercent { get; set; }
    public string? VatCategory { get; set; }
    public decimal? Total { get; set; }
}

public sealed class InvoiceCharge
{
    /// <summary>true = Zuschlag (CHARGE), false = Abschlag (ALLOWANCE).</summary>
    public bool IsCharge { get; set; }
    public decimal? Amount { get; set; }
    public string? Reason { get; set; }
    public decimal? VatPercent { get; set; }
    public string? VatCategory { get; set; }
}

public sealed class VatBreakdown
{
    public string Category { get; set; } = "S";
    public decimal Percent { get; set; }
    public decimal Basis { get; set; }
    public decimal Tax { get; set; }
}

/// <summary>Aufbereitete Rechnungsdaten aus den Steuerzeichen inkl. nachgerechneter Summen.</summary>
public sealed class InvoiceData
{
    public string? FormatCode { get; set; }
    public string? FormatName { get; set; }
    public string? DocumentTypeCode { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<InvoiceLine> Lines { get; set; } = new();
    public List<InvoiceCharge> Charges { get; set; } = new();
    public List<Dictionary<string, string>> PaymentTerms { get; set; } = new();
    public List<VatBreakdown> Vat { get; set; } = new();

    public decimal LineTotal { get; set; }
    public decimal ChargeTotal { get; set; }
    public decimal AllowanceTotal { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal? PrintedGrossAmount { get; set; }
    public decimal? PrepaidAmount { get; set; }
    public decimal? DuePayableAmount { get; set; }

    public static string DocumentTypeName(string? code) => code switch
    {
        "380" => "Rechnung",
        "381" => "Gutschrift",
        "384" => "Korrekturrechnung",
        "326" => "Teilrechnung",
        "389" => "Gutschrift (Selbstfakturierung)",
        "875" or "876" or "877" => "Schlussrechnung",
        null or "" => "Dokument",
        _ => $"Beleg {code}"
    };

    /// <summary>Baut die Rechnungsdaten aus den Steuerzeichen. Einheiten werden über die Einstellung „Einheiten“ in UN/ECE-Codes übersetzt.</summary>
    public static InvoiceData From(ParsedCodes p, IReadOnlyDictionary<string, string> units)
    {
        var d = new InvoiceData
        {
            FormatCode = p.InvoiceFormat,
            FormatName = InvoiceFormat.Find(p.InvoiceFormat)?.Name ?? p.InvoiceFormat,
            DocumentTypeCode = p.Field("DOCUMENT_TYPE") ?? (p.InvoiceFormat is null ? null : "380"),
            Fields = new Dictionary<string, string>(p.Fields, StringComparer.OrdinalIgnoreCase)
        };
        var n = ControlCodeParser.Number;

        foreach (var r in p.Table("ITEM_DETAILS"))
        {
            var unit = r.Get("ITEM_UNIT");
            string? code = null;
            if (unit is not null)
                code = units.TryGetValue(unit, out var c) ? c : IsUneceCode(unit) ? unit.ToUpperInvariant() : null;
            d.Lines.Add(new InvoiceLine
            {
                LineId = r.Get("ITEM_LINE_ID"),
                ArticleNo = r.Get("ITEM_SELLER_ID"),
                Name = r.Get("ITEM_NAME"),
                Description = r.Get("ITEM_DESCRIPTION"),
                Quantity = n(r.Get("ITEM_QUANTITY")),
                UnitText = unit,
                UnitCode = code,
                BaseQuantity = n(r.Get("ITEM_PRICE_BASE_QUANTITY")),
                Price = n(r.Get("ITEM_GROSS_PRICE")) ?? n(r.Get("ITEM_NET_PRICE")),
                Allowance = n(r.Get("ITEM_ALLOWANCE_AMOUNT")),
                AllowanceReason = r.Get("ITEM_ALLOWANCE_REASON"),
                VatPercent = n(r.Get("ITEM_VAT_PERCENT")),
                VatCategory = r.Get("ITEM_VAT_CATEGORY") ?? "S",
                Total = n(r.Get("ITEM_TOTAL_AMOUNT"))
            });
        }

        foreach (var r in p.Table("ALLOWANCE_CHARGE"))
            d.Charges.Add(new InvoiceCharge
            {
                IsCharge = !string.Equals(r.Get("ALLOWANCE_CHARGE_TYPE"), "ALLOWANCE", StringComparison.OrdinalIgnoreCase),
                Amount = n(r.Get("ALLOWANCE_CHARGE_AMOUNT")),
                Reason = r.Get("ALLOWANCE_CHARGE_REASON"),
                VatPercent = n(r.Get("ALLOWANCE_CHARGE_VAT_PERCENT")),
                VatCategory = r.Get("ALLOWANCE_CHARGE_VAT_CATEGORY") ?? "S"
            });

        foreach (var r in p.Table("PAYMENT_TERMS_TABLE"))
            d.PaymentTerms.Add(new Dictionary<string, string>(r.Values));

        d.Calculate();
        d.PrintedGrossAmount = n(p.Field("GROSS_AMOUNT"));
        d.PrepaidAmount = n(p.Field("PREPAID_AMOUNT"));
        d.DuePayableAmount = n(p.Field("DUE_PAYABLE_AMOUNT"));
        return d;
    }

    /// <summary>Summen und Steueraufschlüsselung (BT-106 … BT-119) nachrechnen.</summary>
    public void Calculate()
    {
        LineTotal = Round(Lines.Sum(l => l.Total ?? 0));
        ChargeTotal = Round(Charges.Where(c => c.IsCharge).Sum(c => c.Amount ?? 0));
        AllowanceTotal = Round(Charges.Where(c => !c.IsCharge).Sum(c => c.Amount ?? 0));
        NetAmount = LineTotal + ChargeTotal - AllowanceTotal;

        var groups = new Dictionary<(string, decimal), decimal>();
        void Add(string? cat, decimal? pct, decimal amount)
        {
            var key = ((cat ?? "S").ToUpperInvariant(), pct ?? 0);
            groups[key] = groups.GetValueOrDefault(key) + amount;
        }
        foreach (var l in Lines) Add(l.VatCategory, l.VatPercent, l.Total ?? 0);
        foreach (var c in Charges) Add(c.VatCategory, c.VatPercent, (c.IsCharge ? 1 : -1) * (c.Amount ?? 0));
        Vat = groups.OrderByDescending(g => g.Key.Item2).Select(g => new VatBreakdown
        {
            Category = g.Key.Item1, Percent = g.Key.Item2, Basis = Round(g.Value), Tax = Round(g.Value * g.Key.Item2 / 100m)
        }).ToList();
        TaxAmount = Vat.Sum(v => v.Tax);
        GrossAmount = NetAmount + TaxAmount;
    }

    public static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    /// <summary>Sieht aus wie ein UN/ECE-Code (z. B. H87, C62, HUR, XPK).</summary>
    public static bool IsUneceCode(string s) =>
        s.Length is 2 or 3 && s.All(char.IsLetterOrDigit) && s.Any(char.IsLetter) && s.ToUpperInvariant() == s;
}
