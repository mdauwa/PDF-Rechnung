using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PdfRechnung.Edi;

namespace PdfRechnung.Connect;

/// <summary>Knoten der Connect-Beschreibung (aus der Excel-Liste, tools/connect_schema.py).</summary>
public sealed class ConnectNode
{
    public string Name { get; set; } = "";
    /// <summary>zwingend, erforderlich, optional</summary>
    public string Req { get; set; } = "optional";
    public string? Type { get; set; }
    public int? Len { get; set; }
    public string? Desc { get; set; }
    public string? Sample { get; set; }
    public bool Repeat { get; set; }
    public List<string>? Attrs { get; set; }
    public List<ConnectNode>? Children { get; set; }
    public bool IsBlock => Children is not null;
}

public sealed class ConnectType
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>eingang = Webshop → ERP, ausgang = ERP → Webshop</summary>
    public string Direction { get; set; } = "";
    public List<string> Names { get; set; } = new();
    public ConnectNode? Root { get; set; }
    public string? Block { get; set; }
    public List<string>? Fields { get; set; }
}

public sealed class ConnectSchema
{
    public string Version { get; set; } = "";
    public List<ConnectType> Types { get; set; } = new();
    public List<ConnectType> Extra { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Aliases { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Misspelled { get; set; } = new();

    private static readonly Lazy<ConnectSchema> _s = new(() =>
    {
        using var st = typeof(ConnectSchema).Assembly.GetManifestResourceStream("ErpSuiteConnect.json")!;
        return JsonSerializer.Deserialize<ConnectSchema>(st, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    });
    public static ConnectSchema Current => _s.Value;

    /// <summary>Dateiname → Typ, wie FWebshop (GetFileType) ihn erkennt; spezielle Namen vor allgemeinen (ORDERSTATUS vor ORDER).</summary>
    private static readonly (string Part, string Key)[] NameOrder =
    {
        ("ORDERSTATUS", "ORDERSTATUS"), ("ARTIKELSTAMM", "ARTIKELSTAMM"), ("ARTIKEL_", "ARTIKELSTAMM"), ("LAGERBESTAND", "LAGERBESTAND"),
        ("ADRESSEN", "ADRESSEN"), ("KATEGORIE", "KATEGORIE"), ("GUTSCHEIN", "GUTSCHEINE"), ("CATEGORIES", "CATEGORIES"),
        ("PRODUCTREGISTRATION", "PRODUCTREGISTRATION"), ("PRODUCTS", "PRODUCTS"), ("USERS", "USERS"), ("REPAIRREQUEST", "REPAIRREQUEST"),
        ("CHARGEN", "CHARGEN"), ("ORDER", "ORDER")
    };

    public ConnectType? ByName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var n = Path.GetFileName(fileName).ToUpperInvariant();
        foreach (var (part, key) in NameOrder)
            if (n.Contains(part)) return Get(key);
        return null;
    }

    public ConnectType? Get(string key) => Types.Concat(Extra).FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Prüft XML-Dateien von ERP Suite Connect (Webshop-Schnittstelle) gegen die Schnittstellenbeschreibung
/// und gegen das, was das Programm (FWebshop) tatsächlich liest: Aufbau, Pflichtfelder je Block, Datentyp und Länge,
/// bekannte Schreibvarianten (Aliase), Tippfehler in Feldnamen und typische Werte.
/// </summary>
public static class ConnectValidator
{
    /// <summary>Ist das eine Connect-Datei? (Wurzel ROOT bzw. Dateiname wie ORDER_…, Artikelstamm_…)</summary>
    public static ConnectType? Detect(string? fileName, XDocument? doc)
    {
        var s = ConnectSchema.Current;
        var byName = s.ByName(fileName);
        if (doc?.Root is null) return byName;
        if (!doc.Root.Name.LocalName.Equals("ROOT", StringComparison.OrdinalIgnoreCase) || doc.Root.Name.NamespaceName.Length > 0) return null;
        if (byName is not null) return byName;
        // nach dem ersten Block raten
        var first = doc.Root.Elements().FirstOrDefault()?.Name.LocalName.ToUpperInvariant();
        return first switch
        {
            "ORDER" => doc.Root.Elements().First().Elements().Any(e => e.Name.LocalName.Equals("ARTIKEL", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("ADRESS", StringComparison.OrdinalIgnoreCase)) ? s.Get("ORDER") : s.Get("ORDERSTATUS"),
            "ADRESS" => s.Get("USERS"),
            "KATEGORIE" => s.Get("KATEGORIE"),
            "GUTSCHEIN" => s.Get("GUTSCHEINE"),
            "CHARGE" => s.Get("CHARGEN"),
            "REPAIRREQUEST" => s.Get("REPAIRREQUEST"),
            "ARTIKEL" => doc.Root.Descendants().Any(e => e.Name.LocalName.Equals("Lagerbestand", StringComparison.OrdinalIgnoreCase)) && doc.Root.Elements().First().Elements().Count() <= 5
                ? s.Get("LAGERBESTAND") : s.Get("ARTIKELSTAMM"),
            _ => null
        };
    }

    public static EdiReport Validate(byte[] data, string? fileName, EdiReport? into = null)
    {
        var r = into ?? new EdiReport { FileName = fileName, Size = data.Length };
        r.Format = "ERP Suite Connect";
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(data);
            doc = XDocument.Load(ms, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            r.Title = "ERP Suite Connect (XML nicht lesbar)";
            r.AddAt(EdiReport.Fehler, $"Kein gültiges XML: {ex.Message}", ex.LineNumber);
            r.Checks.Add("Wohlgeformtheit");
            return r;
        }
        r.Content ??= System.Text.Encoding.UTF8.GetString(data).TrimStart('﻿');
        var s = ConnectSchema.Current;
        var type = Detect(fileName, doc);
        r.Checks.Add("Wohlgeformtheit");
        if (type is null)
        {
            r.Title = "ERP Suite Connect (Nachrichtentyp unbekannt)";
            r.Error("Nachrichtentyp nicht erkannt – Dateiname sollte mit ORDER_, USERS_, PRODUCTS_, Orderstatus_, Artikelstamm_, Lagerbestand_, Adressen_, Kategorie_ oder Gutscheine_ beginnen.");
            return r;
        }
        r.Title = $"ERP Suite Connect {type.Key} – {type.Title}";
        r.Checks.Add($"Schnittstellenbeschreibung V{s.Version}");
        r.Checks.Add("Feldnamen wie im Programm (FWebshop) gelesen");

        // Dateiname: das Programm erkennt den Typ am Namen
        var byName = s.ByName(fileName);
        if (fileName is not null && byName is null)
            r.Warn($"Am Dateinamen „{fileName}“ erkennt ERP Suite Connect den Nachrichtentyp nicht – der Name muss „{type.Names[0].TrimEnd('_')}“ enthalten, z. B. {type.Names[0].TrimEnd('_')}_12345.xml.");
        else if (byName is not null && byName.Key != type.Key)
            r.Warn($"Dateiname deutet auf {byName.Key}, der Inhalt sieht nach {type.Key} aus.");
        if (!fileName?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ?? false)
            r.Warn("Die Datei sollte die Endung .xml haben.");

        var root = doc.Root!;
        if (!root.Name.LocalName.Equals("ROOT", StringComparison.OrdinalIgnoreCase))
            r.Error($"Wurzelelement ist <{root.Name.LocalName}>, erwartet wird <ROOT>.", null);
        if (doc.Declaration is { } decl && decl.Encoding is { Length: > 0 } enc && !enc.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            r.Info($"Zeichensatz laut XML-Kopf: {enc}.");

        if (type.Root is null)
        {
            // Typ nur aus dem Programm bekannt (CATEGORIES, REPAIRREQUEST, …): Felder grob prüfen
            r.Info($"{type.Key} steht nicht in der Schnittstellenbeschreibung – geprüft wird nur, was das Programm liest.");
            if (type.Block is not null)
            {
                var blocks = root.Descendants().Where(e => e.Name.LocalName.Equals(type.Block, StringComparison.OrdinalIgnoreCase)).ToList();
                if (blocks.Count == 0) r.Error($"Kein <{type.Block}>-Block gefunden.");
                var known = new HashSet<string>(type.Fields ?? new(), StringComparer.OrdinalIgnoreCase);
                foreach (var e in blocks.SelectMany(b => b.Elements()).Where(e => !known.Contains(e.Name.LocalName)).Select(e => e.Name.LocalName).Distinct())
                    r.Info($"<{e}> in <{type.Block}> liest das Programm nicht.");
            }
            return r;
        }

        var ctx = new Ctx(r, s, type);
        ctx.Block(root, type.Root, "ROOT");
        ctx.Flush();
        foreach (var (name, count) in ctx.Unknown)
            r.Info($"<{name}> steht nicht in der Beschreibung ({count}×) – wird nur übernommen, wenn es als freies Feld eingerichtet ist.");
        Plausibility(root, type, r);
        return r;
    }

    private sealed class Ctx
    {
        private readonly EdiReport _r;
        private readonly ConnectSchema _s;
        private readonly ConnectType _t;
        public readonly Dictionary<string, int> Unknown = new(StringComparer.OrdinalIgnoreCase);

        public Ctx(EdiReport r, ConnectSchema s, ConnectType t) { _r = r; _s = s; _t = t; }

        private static int? Line(XObject o) => o is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : null;

        public void Block(XElement el, ConnectNode def, string path)
        {
            var children = def.Children ?? new();
            var blockKey = def.Name.ToUpperInvariant();
            var aliases = _s.Aliases.TryGetValue(blockKey, out var a) ? a : new();
            if (blockKey == "ORDER" && _t.Key != "ORDER") aliases = new();
            var blockAliases = _s.Aliases.TryGetValue("blocks", out var b) ? b : new();
            var misspelled = _s.Misspelled.TryGetValue(_t.Key, out var m) ? m : new();
            var seen = new Dictionary<ConnectNode, List<XElement>>();

            foreach (var e in el.Elements())
            {
                var n = e.Name.LocalName;
                var node = children.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (node is null && blockAliases.TryGetValue(n.ToLowerInvariant(), out var canon))
                    node = children.FirstOrDefault(c => c.Name.Equals(canon, StringComparison.OrdinalIgnoreCase));
                if (node is null && aliases.TryGetValue(n.ToLowerInvariant(), out var target))
                {
                    node = target.Length > 0 ? children.FirstOrDefault(c => c.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) : null;
                    if (node is null) { Leaf(e, null, path); continue; } // vom Programm gelesen, nicht beschrieben
                }
                // In der Beschreibung falsch geschrieben, im Programm richtig (oder umgekehrt)
                var wrong = misspelled.FirstOrDefault(x => x.Key.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (wrong.Key is not null)
                    Say(EdiReport.Warnung, $"<{n}> liest das Programm nicht – es erwartet <{wrong.Value}>. (Die Beschreibung schreibt den Namen anders als das Programm.)", e);
                if (node is null)
                {
                    if (e.HasElements) Say(EdiReport.Hinweis, $"Block <{n}> in {path} steht nicht in der Beschreibung.", e);
                    else Unknown[n] = Unknown.GetValueOrDefault(n) + 1;
                    continue;
                }
                (seen.TryGetValue(node, out var list) ? list : seen[node] = new()).Add(e);
                if (node.IsBlock) Block(e, node, path + "/" + node.Name);
                else Leaf(e, node, path);
            }

            foreach (var c in children)
            {
                if (seen.ContainsKey(c) || c.Req == "optional") continue;
                // Aliase des Programms zählen mit (z. B. <Street> statt <Strasse>)
                var alias = aliases.Where(x => x.Value.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToList();
                if (alias.Count > 0 && el.Elements().Any(e => alias.Contains(e.Name.LocalName.ToLowerInvariant()))) continue;
                if (c.IsBlock)
                {
                    if (c.Req == "zwingend") Say(EdiReport.Fehler, $"Pflichtblock <{c.Name}> fehlt in {path}.", el);
                    else Say(EdiReport.Warnung, $"Block <{c.Name}> fehlt in {path} (für eine ordentliche Verarbeitung nötig).", el);
                }
                else if (c.Req == "zwingend") Say(EdiReport.Fehler, $"Pflichtfeld <{c.Name}> fehlt in {path}{Desc(c)}.", el);
                else Say(EdiReport.Warnung, $"Feld <{c.Name}> fehlt in {path}{Desc(c)} – für eine ordentliche Verarbeitung erforderlich.", el);
            }
        }

        private static string Desc(ConnectNode c) => string.IsNullOrEmpty(c.Desc) ? "" : $" ({Short(c.Desc)})";
        private static string Short(string s) => s.Length > 60 ? s[..57] + "…" : s;

        private void Leaf(XElement e, ConnectNode? def, string path)
        {
            var n = e.Name.LocalName;
            var v = e.Value.Trim();
            if (def is null) return;
            if (e.HasElements) { Say(EdiReport.Fehler, $"<{n}> in {path} ist ein Feld, enthält aber Unterelemente.", e); return; }
            if (v.Length == 0)
            {
                if (def.Req == "zwingend") Say(EdiReport.Fehler, $"Pflichtfeld <{n}> in {path} ist leer{Desc(def)}.", e);
                else if (def.Req == "erforderlich") Say(EdiReport.Warnung, $"<{n}> in {path} ist leer – für eine ordentliche Verarbeitung erforderlich.", e);
                return;
            }
            var type = (def.Type ?? "").ToUpperInvariant();
            var where = $"<{n}> in {path}";
            switch (type)
            {
                case "INTEGER":
                    if (!Regex.IsMatch(v, @"^-?\d+$")) Say(EdiReport.Fehler, $"{where} muss eine ganze Zahl sein: „{Cut(v)}“.", e);
                    break;
                case "R" or "R0" or "R2" or "DOUBLE":
                    var num = Num(v);
                    if (num is null) Say(EdiReport.Fehler, $"{where} muss eine Zahl sein: „{Cut(v)}“.", e);
                    else if (type == "R0" && num % 1 != 0) Say(EdiReport.Warnung, $"{where} sollte eine ganze Zahl sein: „{v}“.", e);
                    else if (type == "R2" && Decimals(v) > 2) Say(EdiReport.Warnung, $"{where} hat mehr als 2 Nachkommastellen: „{v}“.", e);
                    if (def.Len is > 0 && v.Count(char.IsAsciiDigit) > def.Len) Say(EdiReport.Warnung, $"{where} hat mehr als {def.Len} Stellen: „{v}“.", e);
                    return;
                case "ANJ1":
                    if (v is not ("J" or "N")) Say(EdiReport.Warnung, $"{where} erwartet J oder N: „{Cut(v)}“.", e);
                    return;
                case "BLOB" or "L":
                    return;
            }
            var isDate = def.Name.Contains("Datum", StringComparison.OrdinalIgnoreCase) || n.Contains("Date", StringComparison.OrdinalIgnoreCase);
            if (def.Len is > 0 && v.Length > def.Len && !isDate)
                Say(EdiReport.Warnung, $"{where} ist {v.Length} Zeichen lang, vorgesehen sind {def.Len} – wird ggf. abgeschnitten: „{Cut(v)}“.", e);
            if (isDate && Date(v) is null)
                Say(EdiReport.Warnung, $"{where}: Datum „{v}“ nicht lesbar – erwartet TT.MM.JJJJ.", e);
        }

        private readonly Dictionary<string, (string Level, int? Line, int Count)> _msgs = new();
        private readonly List<string> _order = new();

        private void Say(string level, string text, XElement e)
        {
            if (_msgs.TryGetValue(text, out var x)) { _msgs[text] = (x.Level, x.Line, x.Count + 1); return; }
            _msgs[text] = (level, Line(e), 1);
            _order.Add(text);
        }

        /// <summary>Gleiche Meldungen (z. B. je Artikel) einmal mit Anzahl ausgeben.</summary>
        public void Flush()
        {
            foreach (var t in _order)
            {
                var (level, line, count) = _msgs[t];
                _r.AddAt(level, count > 1 ? $"{t} ({count}×)" : t, line);
            }
        }
    }

    // ------------------------------------------------------------------ Plausibilität (aus dem Programm)

    private static void Plausibility(XElement root, ConnectType t, EdiReport r)
    {
        int? L(XObject o) => o is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : null;
        XElement? F(XElement p, params string[] names) => p.Elements().FirstOrDefault(e => names.Any(n => e.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase)));
        IEnumerable<XElement> All(XElement p, params string[] names) => p.Elements().Where(e => names.Any(n => e.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase)));

        foreach (var x in root.Descendants().Where(e => e.Name.LocalName.Equals("Status", StringComparison.OrdinalIgnoreCase) && !e.HasElements && e.Value.Trim().Length > 0))
            if (x.Value.Trim().ToUpperInvariant() is not ("I" or "U" or "D"))
                r.AddAt(EdiReport.Warnung, $"<Status> „{x.Value.Trim()}“ – vorgesehen sind I (neu), U (ändern), D (löschen).", L(x));
        foreach (var x in root.Descendants().Where(e => e.Name.LocalName is "Land" or "Country" or "land" or "country" && !e.HasElements))
        {
            var v = x.Value.Trim();
            if (v.Length > 0 && !(v.Length is 2 or 3 && v.All(char.IsAsciiLetter)))
                r.AddAt(EdiReport.Warnung, $"<{x.Name.LocalName}> „{v}“ ist kein Ländercode (z. B. AT, DE).", L(x));
        }
        foreach (var x in root.Descendants().Where(e => e.Name.LocalName.Equals("Email", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Equals("Mail", StringComparison.OrdinalIgnoreCase)))
        {
            var v = x.Value.Trim();
            if (v.Length > 0 && !Regex.IsMatch(v, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                r.AddAt(EdiReport.Warnung, $"<{x.Name.LocalName}> „{v}“ ist keine gültige E-Mail-Adresse – die Kundensuche läuft über die E-Mail.", L(x));
        }
        if (t.Key != "ORDER") return;

        foreach (var order in All(root, "ORDER"))
        {
            if (F(order, "Steuerberechnung") is { } sb && sb.Value.Trim().ToUpperInvariant() is not ("BRUTTO" or "NETTO" or ""))
                r.AddAt(EdiReport.Warnung, $"<Steuerberechnung> „{sb.Value.Trim()}“ – das Programm kennt nur Brutto, alles andere gilt als Netto.", L(sb));
            if (F(order, "Steuerberechnungsart") is { } sa && sa.Value.Trim() is not ("3" or "4" or "5" or ""))
                r.AddAt(EdiReport.Warnung, $"<Steuerberechnungsart> „{sa.Value.Trim()}“ – vorgesehen sind 3, 4 oder 5.", L(sa));
            if (F(order, "FiburelevanteEinstellungenEinlesen") is { } fi && fi.Value.Trim().ToUpperInvariant() is not ("TRUE" or "FALSE" or ""))
                r.AddAt(EdiReport.Warnung, $"<FiburelevanteEinstellungenEinlesen> „{fi.Value.Trim()}“ – erwartet True oder False.", L(fi));
            if (F(order, "Datum", "OrderDate") is { } d && d.Value.Trim().Length > 0 && Date(d.Value.Trim()) is null)
                r.AddAt(EdiReport.Warnung, $"Bestelldatum „{d.Value.Trim()}“ nicht lesbar – erwartet TT.MM.JJJJ.", L(d));
            var adr = F(order, "ADRESS", "ADDRESS", "ORDERINGPARTY", "INVOICERECIPIENT");
            if (adr is not null)
            {
                var shop = F(adr, "ShopID")?.Value.Trim();
                var nr = F(adr, "Adressnummer", "Addressnummer")?.Value.Trim();
                var mail = F(adr, "Email", "Mail")?.Value.Trim();
                if (string.IsNullOrEmpty(shop))
                    r.AddAt(EdiReport.Warnung, "Bestellung ohne ShopID – wird nur verarbeitet, wenn „Bestellungen ohne ShopID“ eingeschaltet ist und eine Bestellnummer vorliegt.", L(adr));
                if (string.IsNullOrEmpty(nr) && string.IsNullOrEmpty(mail))
                    r.AddAt(EdiReport.Warnung, "Weder Adressnummer noch E-Mail – der Kunde kann im ERP nicht gefunden werden, es wird ein neuer Kunde angelegt.", L(adr));
                if (string.IsNullOrEmpty(F(adr, "Firma", "Companyname", "Firma1", "Name")?.Value.Trim()) && string.IsNullOrEmpty(F(adr, "Nachname")?.Value.Trim()))
                    r.AddAt(EdiReport.Warnung, "Adresse ohne Firma und ohne Nachname.", L(adr));
            }
            var lines = All(order, "ARTIKEL", "PRODUCT", "LISTLINEITEM").ToList();
            if (lines.Count == 0) r.AddAt(EdiReport.Warnung, "Bestellung ohne Artikel.", L(order));
            foreach (var a in lines)
            {
                var menge = Num(F(a, "Menge")?.Value);
                if (menge is 0) r.AddAt(EdiReport.Warnung, "Artikel mit Menge 0.", L(a));
                var n = Num(F(a, "Netto")?.Value); var b = Num(F(a, "Brutto")?.Value); var st = Num(F(a, "Steuersatz")?.Value);
                if (n is { } nn && b is { } bb && st is { } ss && nn != 0 && Math.Abs(nn * (1 + ss / 100m) - bb) > 0.02m * Math.Max(1, Math.Abs(bb)))
                    r.AddAt(EdiReport.Hinweis, $"Artikel {F(a, "Artikelnummer")?.Value.Trim()}: Netto {nn} + {ss} % ergibt {Math.Round(nn * (1 + ss / 100m), 2)}, Brutto ist {bb}.", L(a));
            }
        }
    }

    // ------------------------------------------------------------------ Hilfen

    public static decimal? Num(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Count(c => c is '.' or ',') > 1) return null;
        return decimal.TryParse(s.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int Decimals(string s) { var i = s.LastIndexOfAny(new[] { '.', ',' }); return i < 0 ? 0 : s.Length - i - 1; }

    private static DateOnly? Date(string s) =>
        DateOnly.TryParseExact(s.Trim(), new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd.MM.yy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? DateOnly.FromDateTime(dt) : null;

    private static string Cut(string s) => s.Length > 40 ? s[..37] + "…" : s;
}
