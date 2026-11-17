using System.Text.Json.Serialization;

namespace PdfRechnung.Edi;

public sealed record EdiIssue(string Level, string Text, int? Segment = null, int? Line = null, string? Tag = null, string? Position = null, string? Rule = null);

public sealed class EdiMessageInfo
{
    public string Ref { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string Version { get; set; } = "";
    public string? Agency { get; set; }
    public string? Association { get; set; }
    public string? Directory { get; set; }
    public int FirstSegment { get; set; }
    public int Segments { get; set; }
}

/// <summary>Prüfbericht für eine Datei (EDIFACT, E-Rechnungs-XML oder ZUGFeRD-PDF).</summary>
public sealed class EdiReport
{
    public const string Fehler = "fehler", Warnung = "warnung", Hinweis = "info";

    /// <summary>EDIFACT, CII, UBL, ZUGFeRD-PDF, XML, unbekannt</summary>
    public string Format { get; set; } = "";
    /// <summary>Lesbare Bezeichnung, z. B. „EDIFACT D.96A INVOIC (EANCOM)“ oder „XRechnung 3.0 (CII)“.</summary>
    public string? Title { get; set; }
    public string? FileName { get; set; }
    public long Size { get; set; }
    public string? Syntax { get; set; }
    public string? Separators { get; set; }
    public string? Sender { get; set; }
    public string? Recipient { get; set; }
    public string? ControlRef { get; set; }
    public List<EdiMessageInfo> Messages { get; set; } = new();
    public List<EdiIssue> Issues { get; set; } = new();
    /// <summary>Welche Prüfungen gelaufen sind (z. B. „Syntax“, „Schema (XSD)“, „Regeln EN 16931“).</summary>
    public List<string> Checks { get; set; } = new();
    /// <summary>E-Rechnung: angewandtes Prüfszenario, z. B. „XRechnung 3.0 – UBL Invoice“ (null = kein Szenario gefunden bzw. keine E-Rechnung).</summary>
    public string? Scenario { get; set; }
    /// <summary>E-Rechnung: Empfehlung „annehmen“ oder „ablehnen“ (wie die KoSIT-Prüfung: Schema- oder Regelfehler bzw. kein Szenario → ablehnen).</summary>
    public string? Acceptance { get; set; }
    /// <summary>Stand der Prüfartefakte (für den Bericht).</summary>
    public string? RuleSet { get; set; }
    /// <summary>Geprüfter Inhalt zur Anzeige (Segmente bzw. XML), gekürzt.</summary>
    public string? Content { get; set; }

    [JsonIgnore] public List<EdiSegment> Segments { get; set; } = new();
    [JsonIgnore] internal HashSet<string> CodeHints { get; } = new();

    public int Errors => Issues.Count(i => i.Level == Fehler);
    public int Warnings => Issues.Count(i => i.Level == Warnung);
    public bool Valid => Errors == 0;
    /// <summary>ok, warnung, fehler</summary>
    public string Status => Errors > 0 ? Fehler : Warnings > 0 ? Warnung : "ok";

    public string Summary =>
        $"{Title ?? Format}: {(Errors == 0 && Warnings == 0 ? "keine Fehler" : $"{Errors} Fehler, {Warnings} Warnung(en)")}";

    public void Add(string level, string text, EdiSegment? s = null, string? pos = null) =>
        Issues.Add(new EdiIssue(level, text, s?.No, s?.Line, s?.Tag, pos));
    public void Error(string text, EdiSegment? s = null, string? pos = null) => Add(Fehler, text, s, pos);
    public void Warn(string text, EdiSegment? s = null, string? pos = null) => Add(Warnung, text, s, pos);
    public void Info(string text, EdiSegment? s = null, string? pos = null) => Add(Hinweis, text, s, pos);
    public void AddAt(string level, string text, int? line, string? pos = null, string? rule = null) => Issues.Add(new EdiIssue(level, text, null, line, null, pos, rule));

    internal EdiReport Finish()
    {
        if (Format == "EDIFACT")
        {
            var types = Messages.Select(m => m.Type).Distinct().ToList();
            var dirs = Messages.Select(m => m.Directory ?? m.Version).Distinct().ToList();
            Title = $"EDIFACT {string.Join(", ", dirs)} {string.Join(", ", types)}".Trim();
            if (Messages.Count > 1) Title += $" ({Messages.Count} Nachrichten)";
            Checks.Add("Syntax und Trennzeichen");
            Checks.Add("Umschlag (UNB/UNZ, UNH/UNT, Zähler, Referenzen)");
            if (Messages.Any(m => m.Directory is not null)) Checks.Add("Struktur, Pflichtangaben, Datentyp/Länge gegen das UN/EDIFACT-Verzeichnis");
            Content = string.Join("\n", Segments.Take(5000).Select(s => s.Raw));
        }
        return this;
    }
}
