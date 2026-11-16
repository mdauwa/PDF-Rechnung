using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfRechnung.Documents;

/// <summary>
/// Signatur – global (Einstellungen → Regeln &amp; Signaturen) oder je Benutzer („Meine Mail-Regeln“).
/// Zuordnung über Briefpapier und/oder Absender; ohne Zuordnung und mit „Standard“ gilt sie, wenn nichts anderes passt.
/// </summary>
public sealed class SignatureDef
{
    public string Name { get; set; } = "";
    /// <summary>Text mit Platzhaltern {…}, **fett**, *kursiv*, Links.</summary>
    public string Text { get; set; } = "";
    public List<string> Stationeries { get; set; } = new();
    public List<string> Senders { get; set; } = new();
    /// <summary>Standardsignatur dieser Ebene (Benutzer bzw. global).</summary>
    public bool Default { get; set; }
}

/// <summary>Versandweg eines Belegs.</summary>
public static class MailRoute
{
    /// <summary>"" = wie eingestellt (Autoversand laut „E-Mail &amp; Versand“).</summary>
    public const string Standard = "", Direct = "direkt", Approval = "freigabe", Outlook = "outlook", Gotomaxx = "gotomaxx", None = "keine";
    public static readonly string[] All = { Standard, Direct, Approval, Outlook, Gotomaxx, None };
    public static string Text(string r) => r switch
    {
        Direct => "direkt versenden (automatisch)",
        Approval => "nach Freigabe im Portal",
        Outlook => "über Outlook des Benutzers",
        Gotomaxx => "an gotomaxx PDFMAILER",
        None => "keine Mail",
        _ => "wie eingestellt"
    };
    public static string? Normalize(string? r)
    {
        var t = (r ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "" => null,
            "direkt" or "auto" or "sofort" => Direct,
            "freigabe" or "portal" or "manuell" => Approval,
            "outlook" => Outlook,
            "gotomaxx" or "pdfmailer" => Gotomaxx,
            "keine" or "0" or "aus" => None,
            _ => null
        };
    }
}

/// <summary>
/// Eine Regel: Bedingungen (alle müssen passen; leer = egal; mehrere Werte = einer davon; * und ? als Platzhalter)
/// und Aktionen (leer = nicht ändern). Steuerzeichen im Druck übersteuern Regeln immer.
/// </summary>
public sealed class MailRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    // ---- Bedingungen ----
    /// <summary>Belegart, z. B. „Rechnung“, „Angebot“ (aus den Rechnungsdaten oder dem ersten Wort des Dateinamens/Betreffs).</summary>
    public List<string> DocumentTypes { get; set; } = new();
    public List<string> Stationeries { get; set; } = new();
    public List<string> Senders { get; set; } = new();
    /// <summary>Empfänger, z. B. „*@kunde.at“.</summary>
    public List<string> Recipients { get; set; } = new();
    /// <summary>Kundennummer oder -name.</summary>
    public List<string> Customers { get; set; } = new();
    /// <summary>Quelle: Drucker, MAPI, Eingang, Upload.</summary>
    public List<string> Sources { get; set; } = new();
    /// <summary>E-Rechnungsformat (@@xt@@), z. B. „XRECHNUNG*“.</summary>
    public List<string> Formats { get; set; } = new();
    /// <summary>Betreff oder Dateiname enthält (Platzhalter erlaubt).</summary>
    public string TextContains { get; set; } = "";
    /// <summary>Nur globale Regeln: gilt für diese Portal-Benutzer (IDs).</summary>
    public List<string> Users { get; set; } = new();

    // ---- Aktionen ----
    public string Template { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ReplyTo { get; set; } = "";
    public string Signature { get; set; } = "";
    /// <summary>Versandweg (<see cref="MailRoute"/>), "" = nicht ändern.</summary>
    public string Route { get; set; } = "";
    /// <summary>Bei Versandweg gotomaxx: Drucker ("" = Standarddrucker).</summary>
    public string GotomaxxPrinter { get; set; } = "";
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    /// <summary>Danach keine weiteren Regeln prüfen.</summary>
    public bool Stop { get; set; }
}

/// <summary>Regeln und Signaturen einer Ebene (global bzw. ein Benutzer).</summary>
public class MailRuleSet
{
    public List<SignatureDef> Signatures { get; set; } = new();
    public List<MailRule> Rules { get; set; } = new();
}

/// <summary>Globale Regeln und Signaturen (Einstellungen).</summary>
public sealed class MailRuleOptions : MailRuleSet { }

/// <summary>Was über einen Beleg bekannt ist, wenn die Regeln geprüft werden.</summary>
public sealed record MailRuleContext(
    string? DocumentType, string? Stationery, string? Sender, IReadOnlyList<string> Recipients,
    string? CustomerId, string? CustomerName, string? Source, string? Format, string Text, string? UserId);

/// <summary>Ergebnis: je Aktion der Wert und woher er kommt.</summary>
public sealed class MailRuleResult
{
    public sealed record Value(string Text, string Reason);
    public Value? Template, FromAddress, FromName, ReplyTo, Signature, Route, GotomaxxPrinter;
    public List<string> Cc { get; } = new();
    public List<string> Bcc { get; } = new();
    public string? CcReason, BccReason;
    /// <summary>Angewendete Regeln (für die Anzeige im Vorgang).</summary>
    public List<string> Applied { get; } = new();
}

public static class MailRuleEngine
{
    /// <summary>
    /// Regeln prüfen: zuerst die des Benutzers, dann die globalen – jeweils von oben nach unten.
    /// Je Aktion gewinnt die erste Regel, die sie setzt (eigene Regeln also vor globalen). CC/BCC kommen von der ersten Regel, die welche hat.
    /// </summary>
    public static MailRuleResult Evaluate(MailRuleContext ctx, MailRuleSet? user, MailRuleSet? global)
    {
        var r = new MailRuleResult();
        foreach (var (set, level) in new[] { (user, "eigene Regel"), (global, "Regel") })
        {
            if (set is null) continue;
            foreach (var rule in set.Rules)
            {
                if (!rule.Enabled || !Matches(rule, ctx, level == "Regel")) continue;
                var why = $"{level} „{rule.Name}“";
                var used = false;
                void Set(ref MailRuleResult.Value? slot, string? value)
                {
                    if (slot is null && !string.IsNullOrWhiteSpace(value)) { slot = new(value.Trim(), why); used = true; }
                }
                Set(ref r.Template, rule.Template);
                Set(ref r.FromAddress, rule.FromAddress);
                Set(ref r.FromName, rule.FromName);
                Set(ref r.ReplyTo, rule.ReplyTo);
                Set(ref r.Signature, rule.Signature);
                if (r.Route is null && MailRoute.Normalize(rule.Route) is { } route)
                {
                    r.Route = new(route, why); used = true;
                    if (route == MailRoute.Gotomaxx) r.GotomaxxPrinter = new(rule.GotomaxxPrinter.Trim(), why);
                }
                if (r.CcReason is null && rule.Cc.Any(x => !string.IsNullOrWhiteSpace(x))) { r.Cc.AddRange(Clean(rule.Cc)); r.CcReason = why; used = true; }
                if (r.BccReason is null && rule.Bcc.Any(x => !string.IsNullOrWhiteSpace(x))) { r.Bcc.AddRange(Clean(rule.Bcc)); r.BccReason = why; used = true; }
                if (used || rule.Stop) r.Applied.Add(why);
                if (rule.Stop) return r;
            }
        }
        return r;
    }

    public static bool Matches(MailRule rule, MailRuleContext c, bool global)
    {
        if (global && rule.Users.Any(u => !string.IsNullOrWhiteSpace(u)) && !rule.Users.Any(u => string.Equals(u.Trim(), c.UserId, StringComparison.OrdinalIgnoreCase))) return false;
        if (!Any(rule.DocumentTypes, c.DocumentType)) return false;
        if (!Any(rule.Stationeries, c.Stationery)) return false;
        if (!Any(rule.Senders, c.Sender)) return false;
        if (!AnyOf(rule.Recipients, c.Recipients)) return false;
        if (!AnyOf(rule.Customers, new[] { c.CustomerId, c.CustomerName })) return false;
        if (!Any(rule.Sources, c.Source)) return false;
        if (!Any(rule.Formats, c.Format)) return false;
        if (!string.IsNullOrWhiteSpace(rule.TextContains) && !Like(c.Text, "*" + rule.TextContains.Trim().Trim('*') + "*")) return false;
        return true;
    }

    private static IEnumerable<string> Clean(IEnumerable<string> list) => list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim());

    private static bool Any(List<string> patterns, string? value)
    {
        var p = Clean(patterns).ToList();
        return p.Count == 0 || (!string.IsNullOrWhiteSpace(value) && p.Any(x => Like(value.Trim(), x)));
    }

    private static bool AnyOf(List<string> patterns, IEnumerable<string?> values)
    {
        var p = Clean(patterns).ToList();
        return p.Count == 0 || values.Any(v => !string.IsNullOrWhiteSpace(v) && p.Any(x => Like(v!.Trim(), x)));
    }

    /// <summary>Vergleich ohne Groß-/Kleinschreibung mit * und ?.</summary>
    public static bool Like(string value, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Signatur wählen: angefordert (@@ps@@ oder Regel) → Benutzer: Briefpapier, Absender, Standard → global: Briefpapier, Absender, Standard.
    /// Liefert null, wenn keine passt (dann gilt die Signatur der Mail-Vorlage).
    /// </summary>
    public static (SignatureDef? Signature, string? Reason, string? Warning) SelectSignature(
        string? requested, string? requestReason, string? stationery, string? sender, MailRuleSet? user, MailRuleSet? global)
    {
        var levels = new[] { (Set: user, Name: "eigene Signatur"), (Set: global, Name: "Signatur") };
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var name = requested.Trim();
            foreach (var (set, _) in levels)
                if (set?.Signatures.FirstOrDefault(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    return (hit, requestReason, null);
            var fallback = SelectSignature(null, null, stationery, sender, user, global);
            return (fallback.Signature, fallback.Reason, $"Signatur „{name}“ ({requestReason}) gibt es nicht – {(fallback.Signature is null ? "Signatur der Mail-Vorlage" : $"„{fallback.Signature.Name}“")} wird verwendet.");
        }
        foreach (var (set, level) in levels)
        {
            if (set is null) continue;
            if (!string.IsNullOrWhiteSpace(stationery) && set.Signatures.FirstOrDefault(s => s.Stationeries.Any(x => string.Equals(x.Trim(), stationery.Trim(), StringComparison.OrdinalIgnoreCase))) is { } byStationery)
                return (byStationery, $"{level} für Briefpapier „{stationery}“", null);
            if (!string.IsNullOrWhiteSpace(sender) && set.Signatures.FirstOrDefault(s => s.Senders.Any(x => Like(sender.Trim(), x.Trim()))) is { } bySender)
                return (bySender, $"{level} für Absender {sender}", null);
            if (set.Signatures.FirstOrDefault(s => s.Default) is { } def)
                return (def, level == "eigene Signatur" ? "eigene Standardsignatur" : "Standardsignatur", null);
        }
        return (null, null, null);
    }

    /// <summary>Belegart: aus den Rechnungsdaten, sonst erstes Wort von Dateiname bzw. Betreff („Angebot_N1126…“ → „Angebot“).</summary>
    public static string? GuessDocumentType(string? documentType, params string?[] texts)
    {
        if (!string.IsNullOrWhiteSpace(documentType)) return documentType.Trim();
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var m = Regex.Match(t, @"^\s*([\p{L}][\p{L}\-]+)");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }
}

/// <summary>Regeln und Signaturen je Benutzer (Datei <c>daten\regeln\&lt;id&gt;.json</c>), getrennt von der Benutzerliste.</summary>
public sealed class MailRuleStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dir;
    private readonly object _lock = new();

    public MailRuleStore(Settings.SettingsStore settings) => _dir = Path.Combine(settings.DataDirectory, "regeln");

    private string File(string userId) => Path.Combine(_dir, string.Concat(userId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')) + ".json");

    public MailRuleSet Get(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return new MailRuleSet();
        lock (_lock)
        {
            try
            {
                var f = File(userId);
                return System.IO.File.Exists(f) ? JsonSerializer.Deserialize<MailRuleSet>(System.IO.File.ReadAllText(f), Json) ?? new() : new();
            }
            catch { return new MailRuleSet(); }
        }
    }

    public void Save(string userId, MailRuleSet set)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            var f = File(userId);
            var tmp = f + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(set, Json));
            System.IO.File.Move(tmp, f, true);
        }
    }

    /// <summary>Prüft Regeln/Signaturen; liefert Fehlermeldungen.</summary>
    public static List<string> Validate(MailRuleSet s, bool global)
    {
        var e = new List<string>();
        foreach (var g in s.Signatures.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            e.Add($"Die Signatur „{g.Key}“ gibt es mehrfach.");
        if (s.Signatures.Any(x => string.IsNullOrWhiteSpace(x.Name))) e.Add("Jede Signatur braucht einen Namen.");
        if (s.Signatures.Count(x => x.Default) > 1) e.Add("Nur eine Signatur kann Standard sein.");
        foreach (var r in s.Rules)
        {
            var n = string.IsNullOrWhiteSpace(r.Name) ? "(ohne Namen)" : r.Name;
            if (string.IsNullOrWhiteSpace(r.Name)) e.Add("Jede Regel braucht einen Namen.");
            if (!string.IsNullOrWhiteSpace(r.Route) && MailRoute.Normalize(r.Route) is null) e.Add($"Regel {n}: unbekannter Versandweg „{r.Route}“.");
            if (!global && r.Users.Any(u => !string.IsNullOrWhiteSpace(u))) e.Add($"Regel {n}: „Benutzer“ gibt es nur bei globalen Regeln.");
            foreach (var a in r.Cc.Concat(r.Bcc).Append(r.FromAddress).Append(r.ReplyTo).Where(a => !string.IsNullOrWhiteSpace(a)))
                if (!DocumentChecker.IsMail(a.Trim())) e.Add($"Regel {n}: „{a}“ ist keine gültige E-Mail-Adresse.");
        }
        return e;
    }
}
