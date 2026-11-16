using System.Text;

namespace PdfRechnung.Edi;

/// <summary>Prüfbericht als Text (für den Ordner „fehlerhaft“ und zum Kopieren).</summary>
public static class EdiReportText
{
    public static string Format(EdiReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Prüfbericht Belegpost – {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Datei:   {r.FileName} ({r.Size:N0} Bytes)");
        sb.AppendLine($"Format:  {r.Title ?? r.Format}");
        if (r.Sender is not null) sb.AppendLine($"Absender: {r.Sender}  Empfänger: {r.Recipient}  Referenz: {r.ControlRef}  Zeichensatz: {r.Syntax}");
        foreach (var m in r.Messages) sb.AppendLine($"Nachricht {m.Ref}: {m.Type} {m.Version} {m.Association} – {m.Segments} Segmente{(m.Directory is null ? "" : $", Verzeichnis {m.Directory}")}");
        sb.AppendLine($"Geprüft: {string.Join(" · ", r.Checks)}");
        sb.AppendLine($"Ergebnis: {r.Errors} Fehler, {r.Warnings} Warnung(en)");
        sb.AppendLine();
        foreach (var i in r.Issues)
        {
            var where = string.Join(" ", new[] { i.Segment is { } s ? $"Segment {s}" : null, i.Line is { } l ? $"Zeile {l}" : null, i.Tag, i.Position }.Where(x => !string.IsNullOrEmpty(x)));
            sb.AppendLine($"{i.Level.ToUpperInvariant(),-8} {(where.Length > 0 ? $"[{where}] " : "")}{i.Text}");
        }
        return sb.ToString();
    }
}
