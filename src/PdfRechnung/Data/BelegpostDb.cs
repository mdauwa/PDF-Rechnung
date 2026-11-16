using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PdfRechnung.Data;

/// <summary>
/// Eingebettete SQLite-Datenbank (daten\belegpost.db): Vorgänge, Zeitstrahl, Benutzer, Melder-Geräte,
/// Warteschlangen (Melder-Aufträge, ERP-Rückmeldung), Status-Links und Audit.
/// PDFs und Anhänge bleiben als Dateien im Vorgangsordner.
/// WAL-Modus: Lesen und Schreiben gleichzeitig, eine Datei, keine Installation.
/// </summary>
public sealed class BelegpostDb
{
    public const string FileName = "belegpost.db";
    private static readonly ConcurrentDictionary<string, BelegpostDb> Instances = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _connectionString;

    private BelegpostDb(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        Path = System.IO.Path.Combine(dataDirectory, FileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Migrate(c);
    }

    /// <summary>Eine Instanz je Datenverzeichnis (Dienst, Tests, Kommandozeile teilen sich so die Migration).</summary>
    public static BelegpostDb For(string dataDirectory) =>
        Instances.GetOrAdd(System.IO.Path.GetFullPath(dataDirectory), d => new BelegpostDb(d));

    public string Path { get; }
    public string DataDirectory { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;");
        return c;
    }

    public static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = Cmd(c, sql, p);
        cmd.ExecuteNonQuery();
    }

    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = Cmd(c, sql, p);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    // ---------------- Schema ----------------

    /// <summary>Nummerierte Schema-Schritte; die erreichte Nummer steht in PRAGMA user_version.</summary>
    private static readonly string[] Steps =
    {
        // 1: Grundschema
        """
        CREATE TABLE vorgang(
            id TEXT PRIMARY KEY,
            erstellt INTEGER NOT NULL,
            status TEXT NOT NULL,
            besitzer TEXT,
            belndx TEXT,
            belegart TEXT,
            belegnr TEXT,
            gesendet INTEGER,
            ordner TEXT NOT NULL,
            suche TEXT NOT NULL DEFAULT '',
            daten TEXT NOT NULL);
        CREATE INDEX ix_vorgang_besitzer ON vorgang(besitzer, status, erstellt);
        CREATE INDEX ix_vorgang_status ON vorgang(status, erstellt);
        CREATE INDEX ix_vorgang_erstellt ON vorgang(erstellt);
        CREATE INDEX ix_vorgang_belndx ON vorgang(belndx);
        CREATE INDEX ix_vorgang_gesendet ON vorgang(gesendet);

        CREATE TABLE vorgang_ereignis(
            nr INTEGER PRIMARY KEY AUTOINCREMENT,
            vorgang_id TEXT NOT NULL,
            zeit INTEGER NOT NULL,
            code TEXT NOT NULL,
            kanal TEXT,
            von TEXT,
            text TEXT);
        CREATE INDEX ix_ereignis_vorgang ON vorgang_ereignis(vorgang_id, zeit);

        CREATE TABLE benutzer(id TEXT PRIMARY KEY, daten TEXT NOT NULL);
        CREATE TABLE geraet(id TEXT PRIMARY KEY, benutzer_id TEXT NOT NULL, token_hash TEXT NOT NULL, daten TEXT NOT NULL);
        CREATE INDEX ix_geraet_token ON geraet(token_hash);
        CREATE TABLE wert(schluessel TEXT PRIMARY KEY, daten TEXT NOT NULL);

        CREATE TABLE melder_auftrag(
            id TEXT PRIMARY KEY,
            benutzer_id TEXT NOT NULL,
            geraet_id TEXT,
            art TEXT NOT NULL,
            vorgang_id TEXT,
            daten TEXT,
            erstellt INTEGER NOT NULL,
            zugestellt INTEGER,
            erledigt INTEGER);
        CREATE INDEX ix_melder_auftrag ON melder_auftrag(benutzer_id, erledigt);

        CREATE TABLE erp_auftrag(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            belndx TEXT NOT NULL,
            vorgang_id TEXT NOT NULL,
            code TEXT NOT NULL,
            ereignis_zeit INTEGER NOT NULL,
            status TEXT NOT NULL,
            versuche INTEGER NOT NULL DEFAULT 0,
            naechster_versuch INTEGER NOT NULL,
            erstellt INTEGER NOT NULL,
            erledigt INTEGER,
            meldung TEXT,
            felder TEXT);
        CREATE INDEX ix_erp_auftrag ON erp_auftrag(status, naechster_versuch);
        CREATE INDEX ix_erp_auftrag_belndx ON erp_auftrag(belndx, status);

        CREATE TABLE status_link(
            token TEXT PRIMARY KEY,
            schluessel TEXT NOT NULL UNIQUE,
            erstellt INTEGER NOT NULL,
            letzter_abruf INTEGER,
            widerrufen INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE audit(
            nr INTEGER PRIMARY KEY AUTOINCREMENT,
            zeit INTEGER NOT NULL,
            von TEXT,
            aktion TEXT NOT NULL,
            ziel TEXT,
            text TEXT);
        CREATE INDEX ix_audit_zeit ON audit(zeit);
        """,
        // 2: Eingang (abgerufene und per AS2 empfangene Dateien)
        """
        CREATE TABLE eingang(
            nr INTEGER PRIMARY KEY AUTOINCREMENT,
            zeit INTEGER NOT NULL,
            kanal TEXT NOT NULL,
            quelle TEXT,
            kunde_id TEXT,
            kunde TEXT,
            datei TEXT NOT NULL,
            groesse INTEGER NOT NULL,
            ziel TEXT NOT NULL COLLATE NOCASE,
            kopie TEXT,
            vorgang_id TEXT,
            gelesen INTEGER);
        CREATE INDEX ix_eingang_zeit ON eingang(zeit);
        CREATE INDEX ix_eingang_ziel ON eingang(ziel);
        """,
        // 3: Prüfergebnis je Eingangsdatei (EDIFACT / E-Rechnung)
        """
        ALTER TABLE eingang ADD COLUMN pruefung TEXT;
        ALTER TABLE eingang ADD COLUMN pruefung_text TEXT;
        """
    };

    private static void Migrate(SqliteConnection c)
    {
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"));
        for (var i = version; i < Steps.Length; i++)
        {
            using var tx = c.BeginTransaction();
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Steps[i];
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version={i + 1};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------------- Audit ----------------

    public void Audit(string? by, string action, string? target = null, string? text = null)
    {
        try
        {
            using var c = Open();
            Exec(c, "INSERT INTO audit(zeit, von, aktion, ziel, text) VALUES(@z,@v,@a,@t,@x)",
                ("@z", Ms(DateTimeOffset.Now)), ("@v", by), ("@a", action), ("@t", target), ("@x", text));
        }
        catch (SqliteException) { /* Audit darf den Ablauf nie stören */ }
    }

    public List<object> AuditLog(int limit)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT zeit, von, aktion, ziel, text FROM audit ORDER BY nr DESC LIMIT @l", ("@l", Math.Clamp(limit, 1, 2000)));
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
            list.Add(new { at = At(r.GetInt64(0)), by = r.IsDBNull(1) ? null : r.GetString(1), action = r.GetString(2),
                target = r.IsDBNull(3) ? null : r.GetString(3), text = r.IsDBNull(4) ? null : r.GetString(4) });
        return list;
    }

    // ---------------- Wartung ----------------

    /// <summary>Online-Sicherung (Datenbank bleibt in Betrieb).</summary>
    public string Backup(string directory, int keep)
    {
        Directory.CreateDirectory(directory);
        var target = System.IO.Path.Combine(directory, $"belegpost-{DateTime.Now:yyyyMMdd}.db");
        var tmp = target + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var src = Open())
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
        {
            dst.Open();
            src.BackupDatabase(dst);
        }
        File.Move(tmp, target, overwrite: true);
        foreach (var old in Directory.GetFiles(directory, "belegpost-*.db").OrderByDescending(f => f).Skip(Math.Max(1, keep)))
            try { File.Delete(old); } catch (IOException) { }
        return target;
    }

    public string IntegrityCheck()
    {
        using var c = Open();
        return Convert.ToString(Scalar(c, "PRAGMA quick_check;")) ?? "?";
    }

    public void Vacuum()
    {
        using var c = Open();
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
    }
}

/// <summary>Tägliche Sicherung, Prüfung beim Start und wöchentliches VACUUM.</summary>
public sealed class DatabaseMaintenanceWorker : BackgroundService
{
    private readonly BelegpostDb _db;
    private readonly ILogger<DatabaseMaintenanceWorker> _log;
    private readonly IConfiguration _cfg;

    public DatabaseMaintenanceWorker(BelegpostDb db, IConfiguration cfg, ILogger<DatabaseMaintenanceWorker> log)
    {
        _db = db; _cfg = cfg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var check = _db.IntegrityCheck();
            if (check != "ok") _log.LogError("Datenbankprüfung meldet: {Ergebnis}", check);
        }
        catch (Exception ex) { _log.LogError(ex, "Datenbankprüfung fehlgeschlagen"); }

        var lastVacuum = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
            try
            {
                var dir = Path.Combine(_db.DataDirectory, "sicherung");
                var keep = _cfg.GetValue("Storage:DatabaseBackups", 14);
                var today = Path.Combine(dir, $"belegpost-{DateTime.Now:yyyyMMdd}.db");
                if (keep > 0 && !File.Exists(today))
                    _log.LogInformation("Datenbank gesichert: {Datei}", _db.Backup(dir, keep));
                if (DateTime.Now - lastVacuum > TimeSpan.FromDays(7) && DateTime.Now.Hour is >= 1 and <= 4)
                {
                    _db.Vacuum();
                    lastVacuum = DateTime.Now;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Datenbankwartung"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
