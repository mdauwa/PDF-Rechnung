using PdfRechnung.Data;

namespace PdfRechnung.Exchange;

/// <summary>Lebenszeichen eines Konnektors (Filial-Client).</summary>
public sealed record ConnectorBeat(string Code, DateTimeOffset LastSeen, string? Version, string? Machine, string? Info, string? Address);

/// <summary>
/// Datenaustausch-Buch: jede Datei für einen Standort (richtung „aus“) bzw. von einem Standort (richtung „ein“) mit Status.
/// Die Dateien liegen unter daten\austausch\JJJJ-MM; eine an mehrere Standorte verteilte Datei liegt nur einmal dort.
/// </summary>
public sealed class ExchangeStore
{
    private readonly BelegpostDb _db;
    public string Root { get; }

    public ExchangeStore(BelegpostDb db, string dataDirectory)
    {
        _db = db;
        Root = Path.Combine(dataDirectory, "austausch");
    }

    /// <summary>Datei im Austauschordner ablegen (eindeutiger Name) und den Pfad liefern.</summary>
    public string Keep(string fileName, byte[] data, string direction)
    {
        var dir = Path.Combine(Root, direction, DateTime.Now.ToString("yyyy-MM"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.Now:ddHHmmssfff}_{Guid.NewGuid().ToString("N")[..6]}_{Documents.MailTemplate.SafeFileName(Path.GetFileNameWithoutExtension(fileName))}{Path.GetExtension(fileName)}");
        File.WriteAllBytes(path, data);
        return path;
    }

    public long Add(string direction, string standort, string fileName, long size, string status, string stored, string? source, string? message = null)
    {
        using var c = _db.Open();
        var now = BelegpostDb.Ms(DateTimeOffset.Now);
        BelegpostDb.Exec(c, "INSERT INTO austausch(zeit, richtung, standort, datei, groesse, status, status_zeit, meldung, quelle, ablage) VALUES(@z,@r,@s,@d,@g,@st,@z,@m,@q,@a)",
            ("@z", now), ("@r", direction), ("@s", standort), ("@d", fileName), ("@g", size), ("@st", status), ("@m", message), ("@q", source), ("@a", stored));
        return Convert.ToInt64(BelegpostDb.Scalar(c, "SELECT last_insert_rowid()"));
    }

    public bool SetStatus(long nr, string status, string? message, bool countAttempt = false)
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, "UPDATE austausch SET status=@s, status_zeit=@z, meldung=COALESCE(@m, meldung)" + (countAttempt ? ", versuche=versuche+1" : "") + " WHERE nr=@n",
            ("@s", status), ("@z", BelegpostDb.Ms(DateTimeOffset.Now)), ("@m", message), ("@n", nr));
        return cmd.ExecuteNonQuery() > 0;
    }

    public ExchangeEntry? Get(long nr)
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, Select + " WHERE nr=@n", ("@n", nr));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Für den Standort bereitliegende Dateien (wartet oder abgeholt, aber noch nicht quittiert), älteste zuerst.</summary>
    public List<ExchangeEntry> Pending(string standort, int limit = 200)
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, Select + " WHERE richtung='aus' AND standort=@s AND status IN ('wartet','abgeholt') ORDER BY nr LIMIT @l",
            ("@s", standort), ("@l", Math.Clamp(limit, 1, 1000)));
        using var r = cmd.ExecuteReader();
        var list = new List<ExchangeEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public List<ExchangeEntry> List(string? text, string? standort, string? direction, string? status, DateTimeOffset? from, DateTimeOffset? before, int limit = 1000)
    {
        using var c = _db.Open();
        var where = new List<string>();
        var p = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(text)) { where.Add("(datei LIKE @q OR meldung LIKE @q OR quelle LIKE @q)"); p.Add(("@q", "%" + text.Trim() + "%")); }
        if (!string.IsNullOrWhiteSpace(standort)) { where.Add("standort = @s"); p.Add(("@s", standort)); }
        if (direction is ExchangeStatus.In or ExchangeStatus.Out) { where.Add("richtung = @r"); p.Add(("@r", direction)); }
        if (status == "offen") where.Add("status IN ('wartet','abgeholt','fehler')");
        else if (!string.IsNullOrWhiteSpace(status)) { where.Add("status = @st"); p.Add(("@st", status)); }
        if (from is not null) { where.Add("zeit >= @f"); p.Add(("@f", BelegpostDb.Ms(from.Value))); }
        if (before is not null) { where.Add("zeit < @b"); p.Add(("@b", BelegpostDb.Ms(before.Value))); }
        p.Add(("@l", Math.Clamp(limit, 1, 5000)));
        using var cmd = BelegpostDb.Cmd(c, Select + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY nr DESC LIMIT @l", p.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<ExchangeEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Offene Probleme für die Überwachung: Importfehler (nicht erledigt) und lange nicht abgeholte/quittierte Dateien.</summary>
    public List<ExchangeEntry> Problems(TimeSpan pendingAfter)
    {
        using var c = _db.Open();
        var limit = BelegpostDb.Ms(DateTimeOffset.Now - pendingAfter);
        using var cmd = BelegpostDb.Cmd(c, Select + " WHERE richtung='aus' AND (status='fehler' OR (status IN ('wartet','abgeholt') AND zeit < @l)) ORDER BY nr LIMIT 500", ("@l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<ExchangeEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public void Beat(string code, string? version, string? machine, string? info, string? address)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "INSERT INTO konnektor(code, zuletzt, version, rechner, info, adresse) VALUES(@c,@z,@v,@r,@i,@a) " +
            "ON CONFLICT(code) DO UPDATE SET zuletzt=@z, version=COALESCE(@v, version), rechner=COALESCE(@r, rechner), info=COALESCE(@i, info), adresse=@a",
            ("@c", code), ("@z", BelegpostDb.Ms(DateTimeOffset.Now)), ("@v", version), ("@r", machine), ("@i", info), ("@a", address));
    }

    public Dictionary<string, ConnectorBeat> Beats()
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, "SELECT code, zuletzt, version, rechner, info, adresse FROM konnektor");
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, ConnectorBeat>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
            d[r.GetString(0)] = new ConnectorBeat(r.GetString(0), BelegpostDb.At(r.GetInt64(1)), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
        return d;
    }

    private const string Select = "SELECT nr, zeit, richtung, standort, datei, groesse, status, status_zeit, meldung, quelle, ablage, versuche FROM austausch";

    private static ExchangeEntry Read(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var stored = r.GetString(10);
        return new ExchangeEntry(r.GetInt64(0), BelegpostDb.At(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetString(6),
            r.IsDBNull(7) ? null : BelegpostDb.At(r.GetInt64(7)), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
            stored, File.Exists(stored), r.GetInt32(11));
    }
}
