using System.Text;

namespace PdfRechnung.Edi;

/// <summary>Ein Segment einer EDIFACT-Datei.</summary>
public sealed class EdiSegment
{
    public int No { get; init; }
    public int Line { get; init; }
    public string Tag { get; init; } = "";
    /// <summary>Datenelemente, je Element die Komponenten (ein einfaches Element = eine Komponente).</summary>
    public List<List<string>> Elements { get; init; } = new();
    public string Raw { get; init; } = "";
    public string El(int i, int c = 0) => i < Elements.Count && c < Elements[i].Count ? Elements[i][c] : "";
}

/// <summary>
/// Prüft EDIFACT-/EANCOM-Dateien: Syntax (UNA, Trennzeichen, Freigabezeichen, Zeichensatz), Umschlag
/// (UNB/UNZ, UNG/UNE, UNH/UNT mit Zählern und Referenzen) und Struktur gegen das UN/EDIFACT-Verzeichnis
/// (Segmentgruppen, Reihenfolge, Wiederholungen, Pflichtangaben, Datentyp und Länge, Codes).
/// </summary>
public static class EdifactValidator
{
    private const int MaxIssues = 300;

    public static bool Looks(ReadOnlySpan<byte> data)
    {
        var s = Encoding.Latin1.GetString(data[..Math.Min(data.Length, 64)]).TrimStart('﻿', 'ï', '»', '¿', ' ', '\r', '\n', '\t');
        return s.StartsWith("UNA") || s.StartsWith("UNB") || s.StartsWith("UNH");
    }

    public static EdiReport Validate(byte[] data, string? fileName = null)
    {
        var r = new EdiReport { Format = "EDIFACT", FileName = fileName, Size = data.Length };
        var (text, utf8) = Decode(data);
        var segs = Parse(text, r, out var sep);
        r.Segments = segs;
        r.Separators = sep.ToString();
        if (segs.Count == 0) { r.Error("Keine Segmente gefunden."); return r.Finish(); }

        var i = 0;
        var interchanges = 0;
        while (i < segs.Count && r.Issues.Count < MaxIssues)
        {
            if (segs[i].Tag != "UNB")
            {
                r.Error(interchanges == 0 ? "Die Datei beginnt nicht mit UNB (Nutzdatenkopf)." : $"Nach UNZ folgt {segs[i].Tag} statt eines neuen UNB.", segs[i]);
                // weiter mit dem nächsten UNB oder UNH, damit noch etwas geprüft wird
                var next = segs.FindIndex(i + 1, s => s.Tag is "UNB" or "UNH");
                if (interchanges == 0 && segs[i].Tag == "UNH") { Interchange(segs, ref i, null, r, utf8); interchanges++; continue; }
                if (next < 0) break;
                i = next;
                continue;
            }
            Interchange(segs, ref i, segs[i], r, utf8);
            interchanges++;
        }
        if (r.Issues.Count >= MaxIssues) r.Warn($"Mehr als {MaxIssues} Meldungen – Prüfung abgebrochen.");
        return r.Finish();
    }

    // ------------------------------------------------------------------ Lesen

    public readonly record struct Separators(char Component, char Data, char Decimal, char Release, char Segment)
    {
        public static readonly Separators Default = new(':', '+', '.', '?', '\'');
        public override string ToString() => $"UNA{Component}{Data}{Decimal}{Release} {Segment}";
    }

    private static (string Text, bool Utf8) Decode(byte[] data)
    {
        var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        try
        {
            var t = new UTF8Encoding(false, true).GetString(data, start, data.Length - start);
            return (t, t.Any(c => c > 127));
        }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(data, start, data.Length - start), false); }
    }

    public static List<EdiSegment> Parse(string text, EdiReport r, out Separators sep)
    {
        sep = Separators.Default;
        var p = 0;
        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
        if (text.AsSpan(p).StartsWith("UNA"))
        {
            if (text.Length < p + 9) { r.Error("UNA ist unvollständig."); return new(); }
            var a = text.Substring(p + 3, 6);
            sep = new Separators(a[0], a[1], a[2], a[3], a[5]);
            if (a.Distinct().Count() < 5 && a[3] != ' ')
                r.Error($"UNA: Trennzeichen sind nicht eindeutig ({sep}).");
            if (a[2] is not ('.' or ',')) r.Warn($"UNA: Dezimalzeichen „{a[2]}“ – erwartet . oder ,");
            p += 9;
        }

        var list = new List<EdiSegment>();
        var line = 1 + text.Take(p).Count(c => c == '\n');
        var raw = new StringBuilder();
        var cur = new StringBuilder();
        var comps = new List<string>();
        var els = new List<List<string>>();
        var segLine = line;
        var breaksInside = false;
        var hasRelease = sep.Release != ' ';

        void EndComp() { comps.Add(cur.ToString()); cur.Clear(); }
        void EndEl() { EndComp(); els.Add(comps); comps = new(); }

        for (; p < text.Length; p++)
        {
            var c = text[p];
            if (c is '\r' or '\n')
            {
                if (c == '\n') line++;
                if (raw.Length > 0) breaksInside = true;
                continue;
            }
            if (raw.Length == 0 && cur.Length == 0 && els.Count == 0 && comps.Count == 0) segLine = line;
            if (hasRelease && c == sep.Release)
            {
                raw.Append(c);
                if (p + 1 < text.Length) { p++; cur.Append(text[p]); raw.Append(text[p]); }
                else r.Error("Freigabezeichen am Dateiende.");
                continue;
            }
            if (c == sep.Segment)
            {
                EndEl();
                var tag = els[0][0];
                els[0] = els[0].Count > 1 ? els[0] : els[0];
                list.Add(new EdiSegment { No = list.Count + 1, Line = segLine, Tag = tag, Elements = els.Skip(1).ToList(), Raw = raw.ToString() + c });
                els = new(); raw.Clear();
                continue;
            }
            raw.Append(c);
            if (c == sep.Data) EndEl();
            else if (c == sep.Component) EndComp();
            else cur.Append(c);
        }
        if (raw.ToString().Trim().Length > 0) r.Error($"Die Datei endet ohne Segmentendezeichen ({sep.Segment}) – letzter Rest: „{Short(raw.ToString())}“.");
        if (breaksInside) r.Info("Zeilenumbrüche innerhalb von Segmenten wurden ignoriert.");
        foreach (var s in list)
            if (s.Tag.Length != 3 || !s.Tag.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9'))
                r.Error($"Ungültige Segmentkennung „{Short(s.Tag)}“.", s);
        return list;
    }

    // ------------------------------------------------------------------ Umschlag

    private static void Interchange(List<EdiSegment> segs, ref int i, EdiSegment? unb, EdiReport r, bool utf8)
    {
        var syntax = "UNOA";
        var version = 3;
        string? ref0020 = null;
        if (unb is not null)
        {
            syntax = unb.El(0, 0);
            int.TryParse(unb.El(0, 1), out version);
            r.Syntax ??= $"{syntax}:{unb.El(0, 1)}";
            r.Sender ??= Join(unb.El(1, 0), unb.El(1, 1));
            r.Recipient ??= Join(unb.El(2, 0), unb.El(2, 1));
            ref0020 = unb.El(4);
            r.ControlRef ??= ref0020;
            if (!System.Text.RegularExpressions.Regex.IsMatch(syntax, "^UNO[A-Z]$")) r.Error($"UNB: Syntaxkennung „{syntax}“ ungültig (erwartet UNOA, UNOB, UNOC …).", unb, "S001/0001");
            if (version is < 1 or > 4) r.Error($"UNB: Syntaxversion „{unb.El(0, 1)}“ ungültig (1–4).", unb, "S001/0002");
            if (unb.El(1, 0).Length == 0) r.Error("UNB: Absender fehlt.", unb, "S002/0004");
            if (unb.El(2, 0).Length == 0) r.Error("UNB: Empfänger fehlt.", unb, "S003/0010");
            CheckLen(r, unb, "S002/0004", unb.El(1, 0), 35);
            CheckLen(r, unb, "S003/0010", unb.El(2, 0), 35);
            var date = unb.El(3, 0);
            var time = unb.El(3, 1);
            if (!ValidDate(date)) r.Error($"UNB: Datum „{date}“ ungültig (JJMMTT{(version >= 4 ? " oder JJJJMMTT" : "")}).", unb, "S004/0017");
            else if (date.Length == 8 && version < 4) r.Warn("UNB: 8-stelliges Datum ist erst ab Syntaxversion 4 vorgesehen.", unb, "S004/0017");
            if (!ValidTime(time)) r.Error($"UNB: Uhrzeit „{time}“ ungültig (SSMM).", unb, "S004/0019");
            if (string.IsNullOrEmpty(ref0020)) r.Error("UNB: Datenaustauschreferenz fehlt.", unb, "0020");
            else CheckLen(r, unb, "0020", ref0020, 14);
            if (unb.Elements.Count > 11) r.Error("UNB: zu viele Datenelemente.", unb);
            if (utf8 && syntax is "UNOA" or "UNOB" or "UNOC" or "UNOD" or "UNOE" or "UNOF")
                r.Warn($"Die Datei ist UTF-8-kodiert (Umlaute als Mehrbyte-Zeichen), UNB nennt aber {syntax}. Der Empfänger liest Umlaute dann falsch – {syntax} braucht ISO-8859, UTF-8 wäre UNOW (Syntax 4).", unb, "S001/0001");
            CheckCharset(segs, i, syntax, r);
            i++;
        }

        var messages = 0;
        var groups = 0;
        var refs = new HashSet<string>();
        while (i < segs.Count && segs[i].Tag != "UNB" && r.Issues.Count < MaxIssues)
        {
            var s = segs[i];
            switch (s.Tag)
            {
                case "UNZ":
                    if (unb is null) r.Error("UNZ ohne UNB.", s);
                    else
                    {
                        var expected = groups > 0 ? groups : messages;
                        if (!int.TryParse(s.El(0), out var n)) r.Error($"UNZ: Anzahl „{s.El(0)}“ ist keine Zahl.", s, "0036");
                        else if (n != expected) r.Error($"UNZ: Anzahl {n}, enthalten sind aber {expected} {(groups > 0 ? "Gruppe(n)" : "Nachricht(en)")}.", s, "0036");
                        if (s.El(1) != ref0020) r.Error($"UNZ: Referenz „{s.El(1)}“ passt nicht zu UNB „{ref0020}“.", s, "0020");
                    }
                    i++;
                    return;
                case "UNG":
                    groups++;
                    Group(segs, ref i, r, refs, ref messages);
                    continue;
                case "UNH":
                    Message(segs, ref i, r, refs);
                    messages++;
                    continue;
                case "UNA":
                    r.Error("UNA darf nur am Dateianfang stehen.", s); i++; continue;
                default:
                    r.Error($"Segment {s.Tag} außerhalb einer Nachricht (zwischen UNT und UNH).", s);
                    i++;
                    continue;
            }
        }
        if (unb is not null) r.Error("UNZ (Nutzdatenende) fehlt.", segs[Math.Min(i, segs.Count) - 1]);
    }

    private static void Group(List<EdiSegment> segs, ref int i, EdiReport r, HashSet<string> refs, ref int messages)
    {
        var ung = segs[i++];
        var count = 0;
        while (i < segs.Count && segs[i].Tag == "UNH") { Message(segs, ref i, r, refs); count++; messages++; }
        if (i < segs.Count && segs[i].Tag == "UNE")
        {
            var une = segs[i++];
            if (!int.TryParse(une.El(0), out var n) || n != count) r.Error($"UNE: Anzahl „{une.El(0)}“, enthalten sind {count} Nachricht(en).", une, "0060");
            if (une.El(1) != ung.El(4)) r.Error($"UNE: Referenz „{une.El(1)}“ passt nicht zu UNG „{ung.El(4)}“.", une, "0048");
        }
        else r.Error("UNE (Gruppenende) fehlt.", ung);
    }

    private static void Message(List<EdiSegment> segs, ref int i, EdiReport r, HashSet<string> refs)
    {
        var unh = segs[i];
        var start = i;
        var end = segs.FindIndex(i + 1, s => s.Tag is "UNT" or "UNH" or "UNZ" or "UNB" or "UNE");
        var hasUnt = end >= 0 && segs[end].Tag == "UNT";
        var last = end < 0 ? segs.Count - 1 : hasUnt ? end : end - 1;
        i = last + 1;

        var refNo = unh.El(0);
        var type = unh.El(1, 0);
        var info = new EdiMessageInfo
        {
            Ref = refNo, Type = type, Version = Join(unh.El(1, 1), unh.El(1, 2)), Agency = unh.El(1, 3), Association = unh.El(1, 4),
            FirstSegment = unh.No, Segments = last - start + 1
        };
        r.Messages.Add(info);
        if (refNo.Length == 0) r.Error("UNH: Nachrichtenreferenz fehlt.", unh, "0062");
        else if (!refs.Add(refNo)) r.Warn($"UNH: Nachrichtenreferenz {refNo} kommt im Datenaustausch mehrfach vor.", unh, "0062");
        if (type.Length == 0) r.Error("UNH: Nachrichtentyp fehlt.", unh, "S009/0065");
        if (unh.El(1, 1).Length == 0 || unh.El(1, 2).Length == 0) r.Error("UNH: Version/Release fehlt (z. B. D:96A).", unh, "S009");
        if (unh.El(1, 3).Length == 0) r.Error("UNH: verwaltende Organisation fehlt (UN).", unh, "S009/0051");

        if (!hasUnt) r.Error($"Nachricht {refNo} ({type}): UNT (Nachrichtenende) fehlt.", unh);
        else
        {
            var unt = segs[last];
            var n = last - start + 1;
            if (!int.TryParse(unt.El(0), out var cnt)) r.Error($"UNT: Segmentanzahl „{unt.El(0)}“ ist keine Zahl.", unt, "0074");
            else if (cnt != n) r.Error($"UNT: Segmentanzahl {cnt}, die Nachricht hat aber {n} Segmente (UNH bis UNT).", unt, "0074");
            if (unt.El(1) != refNo) r.Error($"UNT: Referenz „{unt.El(1)}“ passt nicht zu UNH „{refNo}“.", unt, "0062");
        }

        var (dir, wanted) = EdifactDirectory.For(type, unh.El(1, 1), unh.El(1, 2), unh.El(1, 4));
        if (dir is null)
        {
            r.Warn($"Nachricht {refNo}: Verzeichnis {wanted} ist nicht hinterlegt (vorhanden: D.96A, D.01B, D.12A, D.20B, EANCOM 2002) – nur Syntax und Umschlag geprüft.", unh);
            return;
        }
        info.Directory = dir.Name + (unh.El(1, 4).StartsWith("EAN", StringComparison.OrdinalIgnoreCase) ? $" (EANCOM {unh.El(1, 4)})" : "");
        if (!dir.Messages.TryGetValue(type, out var def))
        {
            r.Warn($"Nachricht {refNo}: Nachrichtentyp {type} ist im Verzeichnis {dir.Name} nicht enthalten – nur Syntax und Umschlag geprüft.", unh);
            return;
        }
        info.Name = def.Name;
        var body = segs.GetRange(start, last - start + 1);
        new StructureMatcher(body, r, dir, $"{type} {refNo}").Run(def.Structure);
        foreach (var s in body)
            if (s.Tag is not ("UNH" or "UNT")) Elements(s, dir, r);
    }

    // ------------------------------------------------------------------ Struktur

    private sealed class StructureMatcher
    {
        private readonly List<EdiSegment> _s;
        private readonly EdiReport _r;
        private readonly EdifactDirectory _dir;
        private readonly string _msg;
        private readonly HashSet<string> _said = new();
        private int _pos;

        public StructureMatcher(List<EdiSegment> s, EdiReport r, EdifactDirectory dir, string msg) { _s = s; _r = r; _dir = dir; _msg = msg; }

        public void Run(List<EdiNode> top)
        {
            var at = Seq(top, 0, "");
            var guard = 0;
            while (_pos < _s.Count && guard++ < 200 && _r.Issues.Count < MaxIssues)
            {
                var s = _s[_pos];
                var where = Expected(top, at);
                Say("fehler", _dir.Segments.ContainsKey(s.Tag)
                    ? $"{_msg}: Segment {s.Tag} ist an dieser Stelle nicht erlaubt{where}."
                    : $"{_msg}: Segment {s.Tag} gibt es im Verzeichnis {_dir.Name} nicht.", s);
                _pos++;
                at = Seq(top, at, "");
            }
        }

        private string Expected(List<EdiNode> top, int at)
        {
            var prev = _pos > 0 ? _s[_pos - 1].Tag : null;
            return prev is null ? "" : $" (nach {prev})";
        }

        /// <summary>Knotenliste ab Index abarbeiten; liefert den Index des letzten Knotens, bei dem etwas passte.</summary>
        private int Seq(List<EdiNode> nodes, int from, string path)
        {
            var last = from;
            for (var k = from; k < nodes.Count; k++)
            {
                var n = nodes[k];
                var count = 0;
                while (_pos < _s.Count && _s[_pos].Tag == n.Trigger)
                {
                    count++;
                    if (count == n.Max + 1)
                        Say("fehler", n.IsGroup
                            ? $"{_msg}: Segmentgruppe {Label(n, path)} wiederholt sich öfter als erlaubt ({n.Max}×)."
                            : $"{_msg}: Segment {n.Id}{In(path)} wiederholt sich öfter als erlaubt ({n.Max}×).", _s[_pos]);
                    if (n.IsGroup)
                    {
                        _pos++;
                        Seq(n.Children!, 1, path.Length == 0 ? n.Id : path + "/" + n.Id);
                    }
                    else _pos++;
                    last = k;
                }
                if (count == 0 && n.Mandatory)
                {
                    var here = _pos < _s.Count ? _s[_pos] : _s[^1];
                    Say("fehler", n.IsGroup
                        ? $"{_msg}: Pflicht-Segmentgruppe {Label(n, path)} fehlt."
                        : $"{_msg}: Pflichtsegment {n.Id}{In(path)} fehlt{(_pos < _s.Count ? $" (vor {here.Tag})" : "")}.", here);
                }
            }
            return last;
        }

        private static string Label(EdiNode n, string path) => $"{(path.Length > 0 ? path + "/" : "")}{n.Id} (beginnt mit {n.Trigger})";
        private static string In(string path) => path.Length > 0 ? $" in {path}" : "";

        private void Say(string level, string text, EdiSegment s)
        {
            if (_said.Add(text + "#" + s.No)) _r.Add(level, text, s);
        }
    }

    // ------------------------------------------------------------------ Datenelemente

    private static readonly HashSet<string> CodeSeen = new();

    private static void Elements(EdiSegment s, EdifactDirectory dir, EdiReport r)
    {
        if (!dir.Segments.TryGetValue(s.Tag, out var def)) return; // als Strukturfehler gemeldet
        if (s.Elements.Count > def.Parts.Count)
            r.Error($"{s.Tag}: {s.Elements.Count} Datenelemente, erlaubt sind {def.Parts.Count}.", s, $"{def.Parts.Count + 1}");
        if (s.Elements.Count > 0 && s.Elements[^1].All(c => c.Length == 0))
            r.Warn($"{s.Tag}: überflüssiges Trennzeichen am Segmentende.", s);
        for (var e = 0; e < def.Parts.Count; e++)
        {
            var part = def.Parts[e];
            var vals = e < s.Elements.Count ? s.Elements[e] : new List<string>();
            var empty = vals.All(v => v.Length == 0);
            var pos = (e + 1).ToString("00");
            if (dir.Composites.TryGetValue(part.Code, out var comp))
            {
                if (empty) { if (part.Mandatory) r.Error($"{s.Tag}: Pflicht-Datenelement {part.Code} ({comp.Name}) fehlt.", s, pos); continue; }
                if (vals.Count > comp.Parts.Count) r.Error($"{s.Tag}: {part.Code} hat {vals.Count} Komponenten, erlaubt sind {comp.Parts.Count}.", s, pos);
                for (var c = 0; c < comp.Parts.Count; c++)
                {
                    var cp = comp.Parts[c];
                    var v = c < vals.Count ? vals[c] : "";
                    var cpos = $"{pos}.{c + 1}";
                    if (v.Length == 0) { if (cp.Mandatory) r.Error($"{s.Tag}: in {part.Code} fehlt die Pflichtkomponente {cp.Code} ({ElName(dir, cp.Code)}).", s, cpos); continue; }
                    Value(s, dir, r, cp.Code, v, cpos);
                }
            }
            else
            {
                if (empty) { if (part.Mandatory) r.Error($"{s.Tag}: Pflicht-Datenelement {part.Code} ({ElName(dir, part.Code)}) fehlt.", s, pos); continue; }
                if (vals.Count > 1) r.Error($"{s.Tag}: {part.Code} ist ein einfaches Datenelement, enthält aber Komponenten-Trennzeichen.", s, pos);
                Value(s, dir, r, part.Code, vals[0], pos);
            }
        }
    }

    private static string ElName(EdifactDirectory dir, string code) => dir.Elements.TryGetValue(code, out var d) ? d.Name : code;

    private static void Value(EdiSegment s, EdifactDirectory dir, EdiReport r, string code, string v, string pos)
    {
        if (!dir.Elements.TryGetValue(code, out var d)) return;
        var len = v.Length;
        if (d.Type == "n")
        {
            var digits = v.TrimStart('-');
            if (!digits.All(ch => char.IsAsciiDigit(ch) || ch is '.' or ','))
                r.Error($"{s.Tag}: {code} ({d.Name}) muss numerisch sein: „{Short(v)}“.", s, pos);
            len = digits.Count(char.IsAsciiDigit);
        }
        else if (d.Type == "a" && v.Any(char.IsAsciiDigit))
            r.Error($"{s.Tag}: {code} ({d.Name}) darf keine Ziffern enthalten: „{Short(v)}“.", s, pos);
        if (d.Max > 0 && len > d.Max) r.Error($"{s.Tag}: {code} ({d.Name}) ist {len} Zeichen lang, erlaubt sind {d.Max}: „{Short(v)}“.", s, pos);
        else if (d.Min > 1 && len < d.Min) r.Error($"{s.Tag}: {code} ({d.Name}) muss mindestens {d.Min} Zeichen lang sein: „{Short(v)}“.", s, pos);
        if (dir.Codes.TryGetValue(code, out var codes) && codes.Count > 0 && !codes.Contains(v) && r.CodeHints.Add($"{code}={v}"))
            r.Info($"{s.Tag}: Code „{v}“ für {code} ({d.Name}) steht nicht in der UN-Codeliste {dir.Name} (bei EANCOM- oder Partnercodes normal).", s, pos);
    }

    // ------------------------------------------------------------------ Hilfen

    private static void CheckCharset(List<EdiSegment> segs, int from, string syntax, EdiReport r)
    {
        Func<char, bool>? ok = syntax switch
        {
            "UNOA" => c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' || " .,-()/='+:?!\"%&*;<>".Contains(c),
            "UNOB" => c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || " .,-()/='+:?!\"%&*;<>@[]{}\\|~#$^_`".Contains(c),
            "UNOC" or "UNOD" or "UNOE" or "UNOF" or "UNOG" or "UNOH" or "UNOI" or "UNOJ" or "UNOK" => c => c <= 'ÿ',
            _ => null
        };
        if (ok is null) return;
        var bad = 0;
        for (var i = from; i < segs.Count && segs[i].Tag != "UNZ"; i++)
        {
            var s = segs[i];
            var ch = s.Raw.FirstOrDefault(c => !ok(c));
            if (ch == default) continue;
            if (bad++ < 5)
                r.Error($"Zeichen „{ch}“ ist im Zeichensatz {syntax} nicht erlaubt{(syntax == "UNOA" && char.IsLower(ch) ? " (UNOA kennt keine Kleinbuchstaben – UNOB/UNOC verwenden)" : "")}.", s);
        }
        if (bad > 5) r.Error($"… insgesamt {bad} Segmente mit Zeichen außerhalb von {syntax}.");
    }

    private static void CheckLen(EdiReport r, EdiSegment s, string pos, string v, int max)
    {
        if (v.Length > max) r.Error($"{s.Tag}: Wert „{Short(v)}“ ist länger als {max} Zeichen.", s, pos);
    }

    private static bool ValidDate(string d) =>
        (d.Length == 6 && DateOnly.TryParseExact(d, "yyMMdd", out _)) || (d.Length == 8 && DateOnly.TryParseExact(d, "yyyyMMdd", out _));

    private static bool ValidTime(string t) => t.Length == 4 && TimeOnly.TryParseExact(t, "HHmm", out _);

    private static string Join(string a, string b) => b.Length > 0 ? $"{a}:{b}" : a;

    public static string Short(string s) => s.Length > 60 ? s[..57] + "…" : s;
}
