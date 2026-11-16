using PdfRechnung.Data;

namespace PdfRechnung.Delivery;

/// <summary>Ein Eintrag im Eingang: eine abgerufene oder per AS2 empfangene Datei.</summary>
public sealed record InboundEntry(long Nr, DateTimeOffset At, string Channel, string ChannelText, string? Source, string? CustomerId, string? Customer,
    string FileName, long Size, string Target, bool HasCopy, bool InTarget, string? VorgangId, bool Read,
    string? Check = null, string? CheckText = null);

/// <summary>
/// Eingangsbuch: jede abgerufene Datei wird mit Herkunft (Versandweg, Kunde) in der Datenbank vermerkt und als Kopie unter
/// daten\eingang-archiv\JJJJ-MM abgelegt – auch wenn der Eingangsordner die Datei danach weiterverarbeitet (PDF → Vorgang).
/// </summary>
public sealed class InboundLog
{
    private readonly BelegpostDb _db;
    private readonly string _archive;

    public InboundLog(BelegpostDb db, string dataDirectory)
    {
        _db = db;
        _archive = Path.Combine(dataDirectory, "eingang-archiv");
    }

    /// <summary>PDFs nur prüfen, wenn sie eine eingebettete Datei haben (sonst ist es ein normales PDF, keine E-Rechnung).</summary>
    private static bool IsZugferdCandidate(byte[] data)
    {
        var s = System.Text.Encoding.Latin1.GetString(data);
        return s.Contains("/EmbeddedFiles") || s.Contains("factur-x", StringComparison.OrdinalIgnoreCase) || s.Contains("zugferd", StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>Datei vermerken (Fehler hier dürfen den Abruf nie stören).</summary>
    public long Add(string channel, string? source, string? customerId, string? customer, string fullPath, byte[] data)
    {
        try
        {
            using var c = _db.Open();
            BelegpostDb.Exec(c, "INSERT INTO eingang(zeit, kanal, quelle, kunde_id, kunde, datei, groesse, ziel) VALUES(@z,@k,@q,@ki,@ku,@d,@g,@t)",
                ("@z", BelegpostDb.Ms(DateTimeOffset.Now)), ("@k", channel), ("@q", source), ("@ki", customerId), ("@ku", customer),
                ("@d", Path.GetFileName(fullPath)), ("@g", (long)data.Length), ("@t", Normalize(fullPath)));
            var nr = Convert.ToInt64(BelegpostDb.Scalar(c, "SELECT last_insert_rowid()"));
            try
            {
                if (Edi.EdiValidation.CanValidate(data) && !(Edi.EdiValidation.IsPdf(data) && !IsZugferdCandidate(data)))
                {
                    var rep = Edi.EdiValidation.Validate(data, Path.GetFileName(fullPath));
                    BelegpostDb.Exec(c, "UPDATE eingang SET pruefung=@p, pruefung_text=@t WHERE nr=@n", ("@p", rep.Status), ("@t", rep.Summary), ("@n", nr));
                }
            }
            catch (Exception) { }
            try
            {
                var dir = Path.Combine(_archive, DateTime.Now.ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                var copy = Path.Combine(dir, $"{nr:D6}_{Path.GetFileName(fullPath)}");
                File.WriteAllBytes(copy, data);
                BelegpostDb.Exec(c, "UPDATE eingang SET kopie=@c WHERE nr=@n", ("@c", copy), ("@n", nr));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return nr;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Der Eingangsordner hat aus der Datei einen Vorgang gemacht.</summary>
    public void LinkVorgang(string fullPath, string vorgangId)
    {
        try
        {
            using var c = _db.Open();
            BelegpostDb.Exec(c, "UPDATE eingang SET vorgang_id=@v WHERE nr=(SELECT MAX(nr) FROM eingang WHERE ziel=@t COLLATE NOCASE AND vorgang_id IS NULL)",
                ("@v", vorgangId), ("@t", Normalize(fullPath)));
        }
        catch (Exception) { }
    }

    public List<InboundEntry> List(string? text, DateTimeOffset? from, DateTimeOffset? before, string? channel, bool unreadOnly, int limit = 1000)
    {
        using var c = _db.Open();
        var where = new List<string>();
        var p = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(text)) { where.Add("(datei LIKE @q OR kunde LIKE @q OR quelle LIKE @q)"); p.Add(("@q", "%" + text.Trim() + "%")); }
        if (from is not null) { where.Add("zeit >= @f"); p.Add(("@f", BelegpostDb.Ms(from.Value))); }
        if (before is not null) { where.Add("zeit < @b"); p.Add(("@b", BelegpostDb.Ms(before.Value))); }
        if (!string.IsNullOrWhiteSpace(channel)) { where.Add("kanal = @k"); p.Add(("@k", channel)); }
        if (unreadOnly) where.Add("gelesen IS NULL");
        p.Add(("@l", Math.Clamp(limit, 1, 5000)));
        using var cmd = BelegpostDb.Cmd(c, "SELECT nr, zeit, kanal, quelle, kunde_id, kunde, datei, groesse, ziel, kopie, vorgang_id, gelesen, pruefung, pruefung_text FROM eingang"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY nr DESC LIMIT @l", p.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<InboundEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public InboundEntry? Get(long nr) => Raw(nr)?.Entry;

    /// <summary>Datei zum Herunterladen: Kopie im Archiv, sonst die Datei im Zielordner (falls noch da).</summary>
    public (InboundEntry Entry, string? Path)? Download(long nr)
    {
        var x = Raw(nr);
        if (x is null) return null;
        var path = x.Value.Copy is { } k && File.Exists(k) ? k : File.Exists(x.Value.Entry.Target) ? x.Value.Entry.Target : null;
        return (x.Value.Entry, path);
    }

    private (InboundEntry Entry, string? Copy)? Raw(long nr)
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, "SELECT nr, zeit, kanal, quelle, kunde_id, kunde, datei, groesse, ziel, kopie, vorgang_id, gelesen, pruefung, pruefung_text FROM eingang WHERE nr=@n", ("@n", nr));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (Read(r), r.IsDBNull(9) ? null : r.GetString(9)) : null;
    }

    public int MarkRead(IEnumerable<long>? nrs)
    {
        using var c = _db.Open();
        var now = BelegpostDb.Ms(DateTimeOffset.Now);
        if (nrs is null) { using var all = BelegpostDb.Cmd(c, "UPDATE eingang SET gelesen=@z WHERE gelesen IS NULL", ("@z", now)); return all.ExecuteNonQuery(); }
        var n = 0;
        foreach (var nr in nrs) { using var cmd = BelegpostDb.Cmd(c, "UPDATE eingang SET gelesen=@z WHERE nr=@n AND gelesen IS NULL", ("@z", now), ("@n", nr)); n += cmd.ExecuteNonQuery(); }
        return n;
    }

    public int Unread()
    {
        try { using var c = _db.Open(); return Convert.ToInt32(BelegpostDb.Scalar(c, "SELECT COUNT(*) FROM eingang WHERE gelesen IS NULL")); }
        catch (Exception) { return 0; }
    }

    private static InboundEntry Read(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var channel = r.GetString(2);
        var target = r.GetString(8);
        return new InboundEntry(r.GetInt64(0), BelegpostDb.At(r.GetInt64(1)), channel, ChannelType.Text(channel),
            r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6), r.GetInt64(7), target, !r.IsDBNull(9), File.Exists(target), r.IsDBNull(10) ? null : r.GetString(10), !r.IsDBNull(11),
            r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13));
    }
}
