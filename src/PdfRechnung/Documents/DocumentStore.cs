using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PdfRechnung.Data;
using PdfRechnung.Settings;

namespace PdfRechnung.Documents;

/// <summary>Filter für die Vorgangsliste (Sichtbarkeit wird immer mitgegeben).</summary>
public sealed record VorgangFilter
{
    /// <summary>true = alle Belege; sonst nur die der Besitzer in <see cref="Owners"/>.</summary>
    public bool SeeAll { get; init; }
    public IReadOnlyCollection<string>? Owners { get; init; }
    /// <summary>Nur Belege dieses Besitzers („Meine Belege“).</summary>
    public string? Owner { get; init; }
    public IReadOnlyCollection<string>? Statuses { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Before { get; init; }
    public string? Text { get; init; }
    public int Limit { get; init; } = 1000;
    public int Offset { get; init; }
}

/// <summary>
/// Ablage der Vorgänge: Metadaten und Zeitstrahl in der SQLite-Datenbank, Dateien (beleg.pdf, versand.pdf, text.txt)
/// je Vorgang im Ordner vorgaenge\JJJJ\MM\&lt;Id&gt;. Ältere meta.json werden beim Start einmalig übernommen.
/// </summary>
public sealed class DocumentStore
{
    public const string PdfFile = "beleg.pdf", SendFile = "versand.pdf", TextFile = "text.txt", MetaFile = "meta.json", XmlFile = "rechnung.xml";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Serialisiert Schreibzugriffe je Vorgang (Lesen läuft parallel über WAL).</summary>
    private readonly object _lock = new();
    private readonly ILogger<DocumentStore> _log;

    public DocumentStore(SettingsStore settings, ILogger<DocumentStore> log)
    {
        _log = log;
        Db = BelegpostDb.For(settings.DataDirectory);
        Root = Path.Combine(settings.DataDirectory, "vorgaenge");
        Directory.CreateDirectory(Root);
        ImportLegacy();
        RecoverInterrupted();
    }

    public BelegpostDb Db { get; }
    public string Root { get; }
    public event Action<Vorgang>? Changed;
    /// <summary>Neuer Eintrag im Zeitstrahl (z. B. für die ERP-Rückmeldung).</summary>
    public event Action<VorgangEvent, Vorgang>? StatusChanged;

    public static string NewId(DateTimeOffset at) => $"{at:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    // ---------------- Übernahme alter meta.json ----------------

    private void ImportLegacy()
    {
        var files = Directory.EnumerateFiles(Root, MetaFile, SearchOption.AllDirectories).ToList();
        if (files.Count == 0) return;
        int ok = 0, bad = 0;
        using var c = Db.Open();
        foreach (var meta in files)
        {
            try
            {
                var v = JsonSerializer.Deserialize<Vorgang>(File.ReadAllText(meta), Json);
                if (v is null || string.IsNullOrEmpty(v.Id)) { bad++; continue; }
                v.StatusCode ??= StatusCode.FromVorgang(v, alreadySent: false);
                if (v.Status == VorgangStatus.Sent && v.SendCount == 0) v.SendCount = 1;
                using var tx = c.BeginTransaction();
                if (Exists(c, v.Id)) { tx.Rollback(); }
                else
                {
                    Write(c, v, Path.GetDirectoryName(meta)!, insert: true);
                    // Zeitstrahl aus den bekannten Zeitpunkten rekonstruieren
                    AddEventRow(c, new VorgangEvent(v.Id, v.CreatedAt, StatusCode.Erfasst, null, "Übernahme", null));
                    if (v.StatusCode is { } code && code != StatusCode.Erfasst)
                        AddEventRow(c, new VorgangEvent(v.Id, v.SentAt ?? v.History.LastOrDefault()?.At ?? v.CreatedAt, code,
                            v.Status == VorgangStatus.Sent ? "Mail" : null, v.SentBy ?? "Übernahme", null));
                    tx.Commit();
                }
                File.Move(meta, meta + ".migriert", overwrite: true);
                ok++;
            }
            catch (Exception ex) { bad++; _log.LogWarning(ex, "Vorgang {Meta} konnte nicht übernommen werden", meta); }
        }
        _log.LogInformation("{Ok} Vorgänge in die Datenbank übernommen ({Bad} fehlerhaft)", ok, bad);
        Db.Audit("System", "Übernahme Vorgänge", null, $"{ok} übernommen, {bad} fehlerhaft");
    }

    private void RecoverInterrupted()
    {
        // Ein beim Beenden laufender Versand gilt als nicht versendet
        foreach (var v in Query(new VorgangFilter { SeeAll = true, Statuses = new[] { VorgangStatus.Sending }, Limit = 100_000 }))
            Update(v.Id, x =>
            {
                x.Status = VorgangStatus.SendError;
                x.SendError = "Versand wurde unterbrochen (Dienst beendet).";
                return true;
            });
    }

    // ---------------- Lesen ----------------

    /// <summary>Kopie eines Vorgangs (Änderungen nur über <see cref="Update"/>).</summary>
    public Vorgang? Get(string id)
    {
        using var c = Db.Open();
        return Read(c, id)?.V;
    }

    public string? Directory_(string id)
    {
        using var c = Db.Open();
        return BelegpostDb.Scalar(c, "SELECT ordner FROM vorgang WHERE id=@id", ("@id", id)) as string;
    }

    public string? FilePath(string id, string file)
    {
        var dir = Directory_(id);
        if (dir is null) return null;
        var p = Path.Combine(dir, file);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Alle Vorgänge (neueste zuerst). Für Listen besser <see cref="Query"/> verwenden.</summary>
    public List<Vorgang> All() => Query(new VorgangFilter { SeeAll = true, Limit = int.MaxValue });

    public List<Vorgang> Query(VorgangFilter f)
    {
        var (where, p) = Where(f);
        using var c = Db.Open();
        using var cmd = BelegpostDb.Cmd(c, $"SELECT daten FROM vorgang {where} ORDER BY erstellt DESC LIMIT @lim OFFSET @off",
            p.Append(("@lim", (object?)Math.Max(0, f.Limit))).Append(("@off", Math.Max(0, f.Offset))).ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<Vorgang>();
        while (r.Read())
            if (JsonSerializer.Deserialize<Vorgang>(r.GetString(0), Json) is { } v) list.Add(v);
        return list;
    }

    public int Count(VorgangFilter f)
    {
        var (where, p) = Where(f);
        using var c = Db.Open();
        return Convert.ToInt32(BelegpostDb.Scalar(c, $"SELECT COUNT(*) FROM vorgang {where}", p.ToArray()));
    }

    /// <summary>Anzahl je Portal-Status im sichtbaren Bereich.</summary>
    public Dictionary<string, int> CountByStatus(VorgangFilter visibility)
    {
        var (where, p) = Where(visibility with { Statuses = null, Text = null, From = null, Before = null, Owner = null });
        using var c = Db.Open();
        using var cmd = BelegpostDb.Cmd(c, $"SELECT status, COUNT(*) FROM vorgang {where} GROUP BY status", p.ToArray());
        using var r = cmd.ExecuteReader();
        var d = VorgangStatus.All.ToDictionary(s => s, _ => 0);
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    /// <summary>Am angegebenen Tag versendete Vorgänge im sichtbaren Bereich.</summary>
    public int CountSentBetween(VorgangFilter visibility, DateTimeOffset from, DateTimeOffset before)
    {
        var (where, p) = Where(visibility with { Statuses = null, Text = null, From = null, Before = null, Owner = null });
        where += (where.Length == 0 ? "WHERE " : " AND ") + "gesendet >= @sf AND gesendet < @sb";
        using var c = Db.Open();
        return Convert.ToInt32(BelegpostDb.Scalar(c, $"SELECT COUNT(*) FROM vorgang {where}",
            p.Append(("@sf", BelegpostDb.Ms(from))).Append(("@sb", BelegpostDb.Ms(before))).ToArray()));
    }

    /// <summary>Vorgänge zu einem ERP-Belegindex (neueste zuerst).</summary>
    public List<Vorgang> ByErpIndex(string belndx)
    {
        using var c = Db.Open();
        using var cmd = BelegpostDb.Cmd(c, "SELECT daten FROM vorgang WHERE belndx=@b ORDER BY erstellt DESC LIMIT 50", ("@b", belndx));
        using var r = cmd.ExecuteReader();
        var list = new List<Vorgang>();
        while (r.Read())
            if (JsonSerializer.Deserialize<Vorgang>(r.GetString(0), Json) is { } v) list.Add(v);
        return list;
    }

    /// <summary>IDs abgeschlossener Vorgänge vor einem Zeitpunkt (Aufbewahrung).</summary>
    public List<string> FinalBefore(DateTimeOffset limit)
    {
        using var c = Db.Open();
        using var cmd = BelegpostDb.Cmd(c, "SELECT id FROM vorgang WHERE erstellt < @l AND status IN (@a,@b,@c)",
            ("@l", BelegpostDb.Ms(limit)), ("@a", VorgangStatus.Sent), ("@b", VorgangStatus.Discarded), ("@c", VorgangStatus.NoMail));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static (string Where, List<(string, object?)> P) Where(VorgangFilter f)
    {
        var w = new List<string>();
        var p = new List<(string, object?)>();
        if (!f.SeeAll)
        {
            var owners = (f.Owners ?? Array.Empty<string>()).ToList();
            if (owners.Count == 0) w.Add("0");
            else
            {
                w.Add("besitzer IN (" + string.Join(",", owners.Select((_, i) => "@o" + i)) + ")");
                for (var i = 0; i < owners.Count; i++) p.Add(("@o" + i, owners[i]));
            }
        }
        if (f.Owner is not null) { w.Add("besitzer = @me"); p.Add(("@me", f.Owner)); }
        if (f.Statuses is { Count: > 0 } st)
        {
            var l = st.ToList();
            w.Add("status IN (" + string.Join(",", l.Select((_, i) => "@s" + i)) + ")");
            for (var i = 0; i < l.Count; i++) p.Add(("@s" + i, l[i]));
        }
        if (f.From is { } from) { w.Add("erstellt >= @from"); p.Add(("@from", BelegpostDb.Ms(from))); }
        if (f.Before is { } before) { w.Add("erstellt < @before"); p.Add(("@before", BelegpostDb.Ms(before))); }
        if (!string.IsNullOrWhiteSpace(f.Text)) { w.Add("instr(suche, @q) > 0"); p.Add(("@q", f.Text.Trim().ToLowerInvariant())); }
        return (w.Count == 0 ? "" : "WHERE " + string.Join(" AND ", w), p);
    }

    // ---------------- Zeitstrahl ----------------

    public List<VorgangEvent> Events(string vorgangId)
    {
        using var c = Db.Open();
        return ReadEvents(c, "WHERE vorgang_id=@id", ("@id", vorgangId));
    }

    /// <summary>Zeitstrahl aller Vorgänge eines ERP-Belegindex.</summary>
    public List<VorgangEvent> EventsForErpIndex(string belndx)
    {
        using var c = Db.Open();
        return ReadEvents(c, "WHERE vorgang_id IN (SELECT id FROM vorgang WHERE belndx=@b)", ("@b", belndx));
    }

    private static List<VorgangEvent> ReadEvents(SqliteConnection c, string where, params (string, object?)[] p)
    {
        using var cmd = BelegpostDb.Cmd(c, $"SELECT vorgang_id, zeit, code, kanal, von, text FROM vorgang_ereignis {where} ORDER BY zeit, nr", p);
        using var r = cmd.ExecuteReader();
        var list = new List<VorgangEvent>();
        while (r.Read())
            list.Add(new VorgangEvent(r.GetString(0), BelegpostDb.At(r.GetInt64(1)), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>
    /// Eigenes Ereignis eintragen (z. B. „versendet über Outlook“ vom Melder). Setzt den Statuscode des Vorgangs,
    /// außer das Ereignis ist älter als das letzte bekannte.
    /// </summary>
    public Vorgang? AddEvent(string id, string code, string? channel, string? by, string? text = null, DateTimeOffset? at = null)
    {
        VorgangEvent ev;
        Vorgang v;
        lock (_lock)
        {
            using var c = Db.Open();
            using var tx = c.BeginTransaction();
            var row = Read(c, id);
            if (row is null) return null;
            v = row.Value.V;
            ev = new VorgangEvent(id, at ?? DateTimeOffset.Now, code, channel, by, text);
            AddEventRow(c, ev);
            var last = BelegpostDb.Scalar(c, "SELECT MAX(zeit) FROM vorgang_ereignis WHERE vorgang_id=@id", ("@id", id)) is long ms ? ms : 0;
            if (BelegpostDb.Ms(ev.At) >= last)
            {
                v.StatusCode = code;
                Write(c, v, row.Value.Dir, insert: false);
            }
            tx.Commit();
        }
        Changed?.Invoke(v);
        StatusChanged?.Invoke(ev, v);
        return v;
    }

    /// <summary>Hinweis im Zeitstrahl ohne Statusänderung (z. B. „Status im ERP bestätigt“).</summary>
    public void AddNote(string id, string code, string? channel, string? by, string? text = null)
    {
        using var c = Db.Open();
        AddEventRow(c, new VorgangEvent(id, DateTimeOffset.Now, code, channel, by, text));
    }

    private static void AddEventRow(SqliteConnection c, VorgangEvent e) =>
        BelegpostDb.Exec(c, "INSERT INTO vorgang_ereignis(vorgang_id, zeit, code, kanal, von, text) VALUES(@v,@z,@c,@k,@b,@t)",
            ("@v", e.VorgangId), ("@z", BelegpostDb.Ms(e.At)), ("@c", e.Code), ("@k", e.Channel), ("@b", e.By), ("@t", e.Text));

    // ---------------- Schreiben ----------------

    /// <summary>Legt einen neuen Vorgang mit seinen Dateien an.</summary>
    /// <summary>Ordner, in dem ein neuer Vorgang angelegt wird.</summary>
    public string DirectoryFor(Vorgang v) => Path.Combine(Root, v.CreatedAt.ToString("yyyy"), v.CreatedAt.ToString("MM"), v.Id);

    public void Create(Vorgang v, byte[] pdf, string text, byte[]? sendPdf = null)
    {
        var dir = DirectoryFor(v);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, PdfFile), pdf);
        if (sendPdf is not null) File.WriteAllBytes(Path.Combine(dir, SendFile), sendPdf);
        if (v.PendingEInvoice is not null) File.WriteAllBytes(Path.Combine(dir, XmlFile), v.PendingEInvoice);
        File.WriteAllText(Path.Combine(dir, TextFile), text);
        var events = new List<VorgangEvent> { new(v.Id, v.CreatedAt, StatusCode.Erfasst, v.Source, v.OwnerName ?? v.PrintUser, null) };
        if (StatusCode.FromVorgang(v, false) is { } code)
            events.Add(new VorgangEvent(v.Id, v.CreatedAt.AddMilliseconds(1), code, null, null, null));
        v.StatusCode = events[^1].Code;
        lock (_lock)
        {
            using var c = Db.Open();
            using var tx = c.BeginTransaction();
            Write(c, v, dir, insert: true);
            foreach (var e in events) AddEventRow(c, e);
            tx.Commit();
        }
        Changed?.Invoke(v);
        foreach (var e in events) StatusChanged?.Invoke(e, v);
    }

    /// <summary>Datei eines Vorgangs schreiben (z. B. neu erzeugtes Versand-PDF); null = löschen.</summary>
    public void WriteFile(string id, string file, byte[]? data)
    {
        var dir = Directory_(id);
        if (dir is null) return;
        var path = Path.Combine(dir, file);
        if (data is null) { if (File.Exists(path)) File.Delete(path); return; }
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Ändert einen Vorgang unter Sperre und speichert ihn. Ändert sich dabei der Portal-Status,
    /// wird automatisch ein Zeitstrahl-Eintrag mit dem passenden Statuscode geschrieben.
    /// </summary>
    public Vorgang? Update(string id, Func<Vorgang, bool> change)
    {
        Vorgang v;
        VorgangEvent? ev = null;
        lock (_lock)
        {
            using var c = Db.Open();
            using var tx = c.BeginTransaction();
            var row = Read(c, id);
            if (row is null) return null;
            v = row.Value.V;
            var oldStatus = v.Status;
            var oldSendCount = v.SendCount;
            if (!change(v)) return v;
            if (v.Status == VorgangStatus.Sent && oldStatus != VorgangStatus.Sent) v.SendCount = oldSendCount + 1;
            if (v.Status != oldStatus && StatusCode.FromVorgang(v, v.SendCount > 1) is { } code && code != v.StatusCode)
            {
                var channel = v.Status == VorgangStatus.Sent ? v.SendChannel ?? "Mail" : v.Status == VorgangStatus.Outlook ? "Outlook"
                    : v.Status == VorgangStatus.Gotomaxx || (v.Status == VorgangStatus.SendError && v.SendChannel == GotomaxxService.Channel) ? GotomaxxService.Channel : null;
                ev = new VorgangEvent(id, DateTimeOffset.Now, code, channel, v.History.LastOrDefault()?.By, null);
                v.StatusCode = code;
                AddEventRow(c, ev);
            }
            Write(c, v, row.Value.Dir, insert: false);
            tx.Commit();
        }
        Changed?.Invoke(v);
        if (ev is not null) StatusChanged?.Invoke(ev, v);
        return v;
    }

    public bool Delete(string id)
    {
        string? dir;
        lock (_lock)
        {
            using var c = Db.Open();
            using var tx = c.BeginTransaction();
            dir = BelegpostDb.Scalar(c, "SELECT ordner FROM vorgang WHERE id=@id", ("@id", id)) as string;
            if (dir is null) return false;
            BelegpostDb.Exec(c, "DELETE FROM vorgang WHERE id=@id", ("@id", id));
            BelegpostDb.Exec(c, "DELETE FROM vorgang_ereignis WHERE vorgang_id=@id", ("@id", id));
            tx.Commit();
        }
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) { _log.LogWarning(ex, "Ordner {Dir} konnte nicht gelöscht werden", dir); }
        return true;
    }

    // ---------------- Zeilen ----------------

    private static bool Exists(SqliteConnection c, string id) =>
        BelegpostDb.Scalar(c, "SELECT 1 FROM vorgang WHERE id=@id", ("@id", id)) is not null;

    private static (Vorgang V, string Dir)? Read(SqliteConnection c, string id)
    {
        using var cmd = BelegpostDb.Cmd(c, "SELECT daten, ordner FROM vorgang WHERE id=@id", ("@id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var v = JsonSerializer.Deserialize<Vorgang>(r.GetString(0), Json);
        return v is null ? null : (v, r.GetString(1));
    }

    private static void Write(SqliteConnection c, Vorgang v, string dir, bool insert)
    {
        var sql = insert
            ? "INSERT INTO vorgang(id, erstellt, status, besitzer, belndx, belegart, belegnr, gesendet, ordner, suche, daten) VALUES(@id,@e,@s,@b,@x,@a,@n,@g,@o,@q,@d)"
            : "UPDATE vorgang SET erstellt=@e, status=@s, besitzer=@b, belndx=@x, belegart=@a, belegnr=@n, gesendet=@g, ordner=@o, suche=@q, daten=@d WHERE id=@id";
        BelegpostDb.Exec(c, sql,
            ("@id", v.Id), ("@e", BelegpostDb.Ms(v.CreatedAt)), ("@s", v.Status), ("@b", v.OwnerUserId), ("@x", v.ErpIndex),
            ("@a", v.DocumentType), ("@n", v.VoucherId), ("@g", v.SentAt is { } s ? BelegpostDb.Ms(s) : null), ("@o", dir),
            ("@q", SearchText(v)), ("@d", JsonSerializer.Serialize(v, Json)));
    }

    private static string SearchText(Vorgang v) => string.Join("\n", new[]
    {
        v.Id, v.VoucherId, v.BuyerName, v.BuyerId, v.Mail.Subject, v.OriginalFileName, v.PrintUser, v.OwnerName, v.ErpIndex
    }.Concat(v.Mail.To).Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
}
