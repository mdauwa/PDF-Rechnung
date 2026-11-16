using System.Text.Json;

namespace PdfRechnung.Documents;

/// <summary>Ein E-Rechnungsfeld der gotomaxx-Schnittstelle (Name, BT-Nummer, Formate, Pflicht). Erzeugt mit tools/erechnung_katalog.py; Bezeichnungen sind eigene Texte.</summary>
public sealed class ERechnungField
{
    public string Name { get; set; } = "";
    /// <summary>null = Kopffeld (@@xi), sonst Tabelle (@@xr table=…).</summary>
    public string? Table { get; set; }
    public string? Group { get; set; }
    public string? Bt { get; set; }
    public string Text { get; set; } = "";
    /// <summary>text, betrag, datum, prozent, menge, code</summary>
    public string Type { get; set; } = "text";
    /// <summary>comfort, extended, xrechnung, xrechnung-cii, peppol</summary>
    public List<string> Formats { get; set; } = new();
    public List<string> Required { get; set; } = new();
    public bool Conditional { get; set; }
    public string? Codelist { get; set; }
    /// <summary>Nur in UBL-Formaten (XRechnung UBL, Peppol).</summary>
    public bool UblOnly { get; set; }
    public string? Default { get; set; }
    public string? Since { get; set; }

    public string Label => Bt is null ? Text : $"{Text} ({Bt})";
}

/// <summary>Profil, das sich aus @@xt@@ ergibt: welche Felder übertragen werden und welche Pflicht sind.</summary>
public sealed record ERechnungProfile(string Key, bool Cii, string Name)
{
    public static ERechnungProfile? For(string? formatCode)
    {
        var c = (formatCode ?? "").Trim().ToLowerInvariant();
        var name = InvoiceFormat.Find(c)?.Name ?? c;
        if (c.Contains("peppol")) return new("peppol", false, name);
        if (c.Contains("xrechnung")) return new("xrechnung", c.EndsWith(".cii"), name);
        if (c.Contains("zugferd")) return new(c.EndsWith(".extended") ? "extended" : "comfort", true, name);
        return null;
    }

    public bool Has(ERechnungField f)
    {
        if (Key == "xrechnung" && !Cii && f.Formats.Count == 1 && f.Formats[0] == "xrechnung-cii") return false;
        if (Cii && f.UblOnly) return false;
        return f.Formats.Contains(Key) || (Key == "xrechnung" && Cii && f.Formats.Contains("xrechnung-cii"));
    }

    public bool Requires(ERechnungField f) => f.Required.Contains(Key);
}

public static class ERechnungCatalog
{
    private static readonly Lazy<List<ERechnungField>> _all = new(() =>
    {
        using var s = typeof(ERechnungCatalog).Assembly.GetManifestResourceStream("ERechnungFelder.json")!;
        return JsonSerializer.Deserialize<List<ERechnungField>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    });

    public static IReadOnlyList<ERechnungField> All => _all.Value;

    public static IEnumerable<ERechnungField> Header => All.Where(f => f.Table is null);

    public static ERechnungField? Field(string name, string? table = null) =>
        All.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Table, table, StringComparison.OrdinalIgnoreCase));

    public static bool IsTable(string table) => All.Any(f => string.Equals(f.Table, table, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<ERechnungField> TableFields(string table) =>
        All.Where(f => string.Equals(f.Table, table, StringComparison.OrdinalIgnoreCase));
}
