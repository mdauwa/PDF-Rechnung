using System.Globalization;
using System.Text;

namespace PdfRechnung.Documents;

/// <summary>
/// Korrigiert typische Zeichenfehler in Texten aus dem ERP, bevor sie in Mails landen:
///   • UTF-8 als Windows-1252/Latin-1 gelesen („Ã¼“ statt „ü“, „â‚¬“ statt „€“),
///   • zerlegte Umlaute („u“ + Trema) → zusammengesetzt (NFC),
///   • Ersatzzeichen „�“ in typischen deutschen Wörtern,
///   • Ligaturen aus PDFs (ﬁ, ﬂ), weiche Trennstriche, Steuerzeichen, geschützte Leerzeichen.
/// Liefert den korrigierten Text und eine kurze Beschreibung jeder Korrektur.
/// </summary>
public static class TextFixer
{
    static TextFixer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Windows1252 => Encoding.GetEncoding(1252);

    // Typische Doppelkodierungen (UTF-8-Bytes als Windows-1252 dargestellt)
    private static readonly string[] MojibakeMarkers = { "Ã¤", "Ã¶", "Ã¼", "Ã„", "Ã–", "Ãœ", "ÃŸ", "Ã©", "Ã¨", "Ã¡", "Ã ", "â‚¬", "â€“", "â€”", "â€ž", "â€œ", "â€\u009d", "â€™", "â€˜", "Â°", "Â§", "Â " };

    // Ersatzzeichen in häufigen Wörtern (wenn die Quelle verloren ist)
    private static readonly (string Wrong, string Right)[] ReplacementWords =
    {
        ("Gr��e", "Grüße"), ("Gr�e", "Grüße"), ("f�r", "für"), ("Stra�e", "Straße"), ("M�rz", "März"),
        ("�ber", "über"), ("geb�hr", "gebühr"), ("R�ck", "Rück"), ("r�ck", "rück"), ("m�glich", "möglich"),
        ("K�ln", "Köln"), ("�sterreich", "Österreich"), ("j�hr", "jähr"), ("Gesch�ft", "Geschäft"), ("gem��", "gemäß"),
        ("Empf�nger", "Empfänger"), ("Gr��", "Grüß"), ("Anh�ng", "Anhäng"), ("zur�ck", "zurück"), ("Erl�uter", "Erläuter")
    };

    public static (string Text, List<string> Changes) Fix(string? input)
    {
        var changes = new List<string>();
        if (string.IsNullOrEmpty(input)) return (input ?? "", changes);
        var s = input;

        // 1. Doppelt kodiertes UTF-8 zurückdrehen (ggf. mehrfach)
        for (var round = 0; round < 2 && MojibakeMarkers.Any(s.Contains); round++)
        {
            var repaired = TryRedecode(s);
            if (repaired is null || repaired == s) break;
            changes.Add("falsch kodierte Umlaute/Sonderzeichen korrigiert (UTF-8 als ANSI gelesen)");
            s = repaired;
        }

        // 2. Zerlegte Zeichen zusammensetzen
        var nfc = s.Normalize(NormalizationForm.FormC);
        if (nfc != s) { changes.Add("zerlegte Umlaute zusammengesetzt"); s = nfc; }

        // 3. Ersatzzeichen in bekannten Wörtern
        if (s.Contains('�'))
        {
            foreach (var (w, r) in ReplacementWords)
                if (s.Contains(w)) { s = s.Replace(w, r); changes.Add($"„{w.Replace('�', '?')}“ → „{r}“"); }
            if (s.Contains('�')) changes.Add("nicht lesbare Zeichen (�) im Text – bitte prüfen");
        }

        // 4. Typografische Störenfriede aus PDFs
        var sb = new StringBuilder(s.Length);
        var cleaned = false;
        foreach (var c in s)
        {
            switch (c)
            {
                case 'ﬁ': sb.Append("fi"); cleaned = true; break;
                case 'ﬂ': sb.Append("fl"); cleaned = true; break;
                case 'ﬀ': sb.Append("ff"); cleaned = true; break;
                case '­': cleaned = true; break;                       // weicher Trennstrich
                case ' ' or ' ' or ' ': sb.Append(' '); cleaned = true; break;
                case '​' or '‌' or '‍' or '﻿': cleaned = true; break;
                case '\r': break;
                case '\n' or '\t': sb.Append(c); break;
                default:
                    if (char.IsControl(c)) { cleaned = true; break; }
                    sb.Append(c); break;
            }
        }
        if (cleaned) changes.Add("unsichtbare Steuer-/Sonderzeichen entfernt");
        return (sb.ToString(), changes.Distinct().ToList());
    }

    public static string FixText(string? input) => Fix(input).Text;

    private static string? TryRedecode(string s)
    {
        try
        {
            // Nur umkodieren, wenn alle Zeichen in Windows-1252 darstellbar sind (sonst ist es kein reines Mojibake)
            var enc1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var bytes = enc1252.GetBytes(s);
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return utf8.GetString(bytes);
        }
        catch
        {
            // Teilweise kaputt: nur die bekannten Sequenzen ersetzen
            var map = new Dictionary<string, string>
            {
                ["Ã¤"] = "ä", ["Ã¶"] = "ö", ["Ã¼"] = "ü", ["Ã„"] = "Ä", ["Ã–"] = "Ö", ["Ãœ"] = "Ü", ["ÃŸ"] = "ß",
                ["Ã©"] = "é", ["Ã¨"] = "è", ["Ã¡"] = "á", ["â‚¬"] = "€", ["â€“"] = "–", ["â€”"] = "—", ["â€ž"] = "„",
                ["â€œ"] = "“", ["â€™"] = "’", ["â€˜"] = "‘", ["Â°"] = "°", ["Â§"] = "§"
            };
            var r = s;
            foreach (var (k, v) in map) r = r.Replace(k, v);
            return r;
        }
    }

    /// <summary>
    /// Textdatei lesen (z. B. @@mb@@): UTF-8 mit/ohne BOM, sonst Windows-1252 (ANSI) –
    /// damit Umlaute aus Dateien des ERP nicht als „�“ ankommen.
    /// </summary>
    public static string ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Windows1252.GetString(bytes); }
    }
}
