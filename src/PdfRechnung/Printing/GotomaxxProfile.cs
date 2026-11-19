using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PdfRechnung.Documents;

namespace PdfRechnung.Printing;

/// <summary>
/// Trennzeichen der Steuerzeichen beim Weiterleiten an gotomaxx. gotomaxx liest im Normalfall <c>@@…@@</c>; im Profil eines
/// PDFMAILER-Druckers kann ein anderes stehen (<c>pdfmailer { commandDelimiter $$ }</c>). Die Profile liegen je Benutzer unter
/// <c>%APPDATA%\gotomaxx\pdfmailer6\profiles\&lt;Profil-ID&gt;.cfg</c>; die Profil-ID gehört zum Drucker und ist für alle Benutzer gleich.
/// Fehlt die Datei, gelten die gotomaxx-Standardwerte, also <c>@@</c>.
/// </summary>
public static partial class GotomaxxProfile
{
    public const string Auto = "auto", Default = "@@";

    /// <summary>Gültige Einstellung: „auto“ oder 1–5 Zeichen ohne Leerzeichen.</summary>
    public static bool IsValidSetting(string? s)
    {
        var t = (s ?? "").Trim();
        return t.Length == 0 || t.Equals(Auto, StringComparison.OrdinalIgnoreCase) || (t.Length <= 5 && !t.Any(char.IsWhiteSpace));
    }

    /// <summary>Trennzeichen aus dem Text einer gotomaxx-Einstellungsdatei; null = nicht gesetzt (gotomaxx-Standard @@).</summary>
    public static string? DelimiterFromConfig(string? cfg)
    {
        if (string.IsNullOrEmpty(cfg)) return null;
        var m = DelimiterRegex().Match(cfg);
        if (!m.Success) return null;
        var v = (m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value).Trim();
        return v.Length == 0 ? null : v;
    }

    [GeneratedRegex(@"^[ \t]*commandDelimiter[ \t]+(?:""(?<q>[^""\r\n]*)""|(?<v>[^\s]+))[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DelimiterRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    /// <summary>Gedruckte Steuerzeichenzeile (<c>@@xxWert@@</c>) mit anderem Trennzeichen.</summary>
    public static string WithDelimiter(string line, string delimiter)
    {
        if (string.IsNullOrEmpty(delimiter) || delimiter == Default) return line;
        if (line.Length >= 4 && line.StartsWith(Default, StringComparison.Ordinal) && line.EndsWith(Default, StringComparison.Ordinal))
            return delimiter + line[2..^2] + delimiter;
        return line;
    }

    /// <summary>
    /// Profil-IDs zu einem Druckernamen aus den gotomaxx-Protokollen (<c>usage_&lt;ID&gt;.dat</c>, Spalte profileName).
    /// Gelesen wird eine Kopie; die Dateien von gotomaxx bleiben unverändert.
    /// </summary>
    public static List<string> IdsFromUsage(string folder, string printer, string tempDir)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(printer) || !Directory.Exists(folder)) return ids;
        Directory.CreateDirectory(tempDir);
        foreach (var path in Directory.GetFiles(folder, "usage_*.dat"))
        {
            var id = GuidRegex().Match(Path.GetFileName(path));
            if (!id.Success) continue;
            var copy = Path.Combine(tempDir, "profil_" + Path.GetFileName(path));
            try
            {
                File.Copy(path, copy, true);
                if (File.Exists(path + "-wal")) File.Copy(path + "-wal", copy + "-wal", true);
                else if (File.Exists(copy + "-wal")) File.Delete(copy + "-wal");
                using var c = new SqliteConnection($"Data Source={copy};Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM Entry WHERE profileName = @p COLLATE NOCASE LIMIT 1";
                cmd.Parameters.AddWithValue("@p", printer.Trim());
                if (cmd.ExecuteScalar() is not null) ids.Add(id.Value.ToLowerInvariant());
            }
            catch { /* Datei gesperrt oder anderes Format – nächste */ }
        }
        return ids;
    }

    /// <summary>
    /// Trennzeichen für den Druck in dieser Windows-Sitzung. <paramref name="setting"/>: fester Wert oder „auto“ (aus dem gotomaxx-Profil
    /// des Benutzers). Liefert das Zeichen und eine Beschreibung für den Verlauf.
    /// </summary>
    public static (string Delimiter, string Info) Resolve(string? setting, string printer, IEnumerable<string>? knownIds)
    {
        var s = (setting ?? "").Trim();
        if (s.Length == 0) return (Default, "");
        if (!s.Equals(Auto, StringComparison.OrdinalIgnoreCase)) return (s, s == Default ? "" : $"Trennzeichen {s}");

        var ids = (knownIds ?? Enumerable.Empty<string>()).Select(x => x.ToLowerInvariant()).ToList();
        if (OperatingSystem.IsWindows())
            foreach (var id in RegistryIds(printer)) if (!ids.Contains(id)) ids.Add(id);
        if (ids.Count == 0) return (Default, "Trennzeichen @@ (gotomaxx-Profil des Druckers nicht gefunden)");
        foreach (var id in ids)
            foreach (var file in ProfileFiles(id))
            {
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }
                var d = DelimiterFromConfig(text) ?? Default;
                return (d, $"Trennzeichen {d} aus dem gotomaxx-Profil {id}");
            }
        return (Default, "Trennzeichen @@ (kein eigenes gotomaxx-Profil – Standard)");
    }

    /// <summary>Profildateien dieser ID im Benutzerprofil (pdfmailer6, pdfmailer7 …), neueste Version zuerst.</summary>
    public static IEnumerable<string> ProfileFiles(string id)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gotomaxx");
        if (!Directory.Exists(root)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root, "pdfmailer*"); } catch { yield break; }
        foreach (var dir in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var f = Path.Combine(dir, "profiles", id + ".cfg");
            if (File.Exists(f)) yield return f;
        }
    }

    /// <summary>GUIDs in den Registry-Daten des Druckers und seines Anschlusses (dort hinterlegt gotomaxx seine Profil-ID).</summary>
    [SupportedOSPlatform("windows")]
    public static List<string> RegistryIds(string printer)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(printer)) return found;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers\" + printer.Trim());
            if (key is null) return found;
            Collect(key, found, 0);
            if (key.GetValue("Port") is string port && port.Length > 0)
            {
                using var monitors = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Monitors");
                foreach (var m in monitors?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var p = monitors!.OpenSubKey(m + @"\Ports\" + port.TrimEnd(':'));
                    if (p is not null) Collect(p, found, 1);
                }
            }
        }
        catch { /* keine Leserechte – dann ohne */ }
        return found;
    }

    [SupportedOSPlatform("windows")]
    private static void Collect(Microsoft.Win32.RegistryKey key, List<string> found, int depth)
    {
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name);
            var text = value switch
            {
                string s => s,
                string[] a => string.Join("\n", a),
                byte[] b => Encoding.Unicode.GetString(b) + "\n" + Encoding.ASCII.GetString(b),
                _ => null
            };
            if (text is null) continue;
            foreach (Match m in GuidRegex().Matches(text))
            {
                var id = m.Value.ToLowerInvariant();
                if (!found.Contains(id)) found.Add(id);
            }
        }
        if (depth >= 2) return;
        foreach (var sub in key.GetSubKeyNames())
        {
            try { using var k = key.OpenSubKey(sub); if (k is not null) Collect(k, found, depth + 1); } catch { }
        }
    }
}
