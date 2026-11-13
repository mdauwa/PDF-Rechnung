namespace PdfRechnung.Settings;

/// <summary>Standardwerte für eine neue settings.json und Ergänzungen beim Start älterer Installationen.</summary>
public static class DefaultSettings
{
    /// <summary>Häufige Mengeneinheiten → UN/ECE-Code (Rec. 20/21). In der Oberfläche erweiterbar.</summary>
    public static readonly IReadOnlyDictionary<string, string> Units = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Stk"] = "H87", ["St"] = "H87", ["Stück"] = "H87", ["Stck"] = "H87", ["ST"] = "H87",
        ["Std"] = "HUR", ["h"] = "HUR", ["Stunde"] = "HUR", ["Stunden"] = "HUR",
        ["Min"] = "MIN", ["Tag"] = "DAY", ["Tage"] = "DAY", ["Monat"] = "MON", ["Mon"] = "MON", ["Jahr"] = "ANN",
        ["kg"] = "KGM", ["g"] = "GRM", ["t"] = "TNE",
        ["m"] = "MTR", ["lfm"] = "MTR", ["m2"] = "MTK", ["m²"] = "MTK", ["m3"] = "MTQ", ["m³"] = "MTQ", ["km"] = "KMT",
        ["l"] = "LTR", ["Ltr"] = "LTR",
        ["Pkg"] = "XPK", ["Pck"] = "XPK", ["Paket"] = "XPK", ["Karton"] = "XCT", ["Ktn"] = "XCT", ["Pal"] = "XPX", ["Palette"] = "XPX",
        ["Satz"] = "SET", ["Set"] = "SET", ["Paar"] = "PR", ["pauschal"] = "LS", ["Pausch"] = "LS", ["psch"] = "LS",
        ["Lizenz"] = "C62", ["Lz"] = "C62", ["Einheit"] = "C62", ["x"] = "C62"
    };

    public static AppSettings Create()
    {
        var s = new AppSettings();
        foreach (var kv in Units) s.Invoice.Units[kv.Key] = kv.Value;
        return s;
    }

    /// <summary>Ergänzt fehlende Werte. true = geändert (wird dann gespeichert).</summary>
    public static bool Migrate(AppSettings s)
    {
        var changed = false;
        if (s.Invoice.Units.Count == 0)
        {
            foreach (var kv in Units) s.Invoice.Units[kv.Key] = kv.Value;
            changed = true;
        }
        return changed;
    }
}
