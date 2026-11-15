using Microsoft.Extensions.Options;
using PdfRechnung.Data;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

/// <summary>Ein Auftrag an den Melder eines Benutzers (z. B. „diese MAPI-Mail in Outlook öffnen“).</summary>
public sealed record MelderTask(string Id, string UserId, string? DeviceId, string Kind, string? VorgangId, DateTimeOffset Created, DateTimeOffset? Delivered, DateTimeOffset? Done);

/// <summary>
/// Persistente Aufträge an Melder (Tabelle melder_auftrag). Ist kein Melder online, wartet der Auftrag, bis sich
/// ein Melder des Benutzers verbindet. Ein Gerät übernimmt einen Auftrag atomar – so öffnet nie zwei PCs denselben Entwurf.
/// </summary>
public sealed class MelderTasks
{
    public const string KindOutlook = "outlook";
    private readonly BelegpostDb _db;
    private readonly EventHub _hub;

    public MelderTasks(BelegpostDb db, EventHub hub) { _db = db; _hub = hub; }

    public string Create(string userId, string? deviceId, string kind, string? vorgangId, string title)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        using (var c = _db.Open())
            BelegpostDb.Exec(c, "INSERT INTO melder_auftrag(id, benutzer_id, geraet_id, art, vorgang_id, erstellt) VALUES(@i,@u,@g,@a,@v,@e)",
                ("@i", id), ("@u", userId), ("@g", deviceId), ("@a", kind), ("@v", vorgangId), ("@e", BelegpostDb.Ms(DateTimeOffset.Now)));
        _hub.Publish(userId, "auftrag", vorgangId ?? "", title, kind, deviceId);
        return id;
    }

    /// <summary>Offene Aufträge für dieses Gerät (an das Gerät oder an „irgendeinen Melder“ des Benutzers).</summary>
    public List<MelderTask> Pending(string userId, string deviceId)
    {
        using var c = _db.Open();
        return Read(c, "WHERE benutzer_id=@u AND erledigt IS NULL AND (geraet_id IS NULL OR geraet_id=@g) AND (zugestellt IS NULL OR zugestellt < @alt) ORDER BY erstellt",
            ("@u", userId), ("@g", deviceId), ("@alt", BelegpostDb.Ms(DateTimeOffset.Now.AddMinutes(-10))));
    }

    /// <summary>Gerät übernimmt den Auftrag (nur einmal; nach 10 Minuten ohne Erledigung erneut vergebbar).</summary>
    public MelderTask? Claim(string id, string userId, string deviceId)
    {
        using var c = _db.Open();
        var n = BelegpostDb.Cmd(c, "UPDATE melder_auftrag SET zugestellt=@n, geraet_id=@g WHERE id=@i AND benutzer_id=@u AND erledigt IS NULL "
            + "AND (geraet_id IS NULL OR geraet_id=@g) AND (zugestellt IS NULL OR zugestellt < @alt)",
            ("@n", BelegpostDb.Ms(DateTimeOffset.Now)), ("@g", deviceId), ("@i", id), ("@u", userId), ("@alt", BelegpostDb.Ms(DateTimeOffset.Now.AddMinutes(-10))));
        if (n.ExecuteNonQuery() == 0) return null;
        return Read(c, "WHERE id=@i", ("@i", id)).FirstOrDefault();
    }

    public void Complete(string id, string userId)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "UPDATE melder_auftrag SET erledigt=@n WHERE id=@i AND benutzer_id=@u", ("@n", BelegpostDb.Ms(DateTimeOffset.Now)), ("@i", id), ("@u", userId));
    }

    /// <summary>Aufträge, die zu lange niemand abgeholt hat (Beleg bleibt im Portal).</summary>
    public List<MelderTask> Expire(TimeSpan age)
    {
        using var c = _db.Open();
        var list = Read(c, "WHERE erledigt IS NULL AND erstellt < @t", ("@t", BelegpostDb.Ms(DateTimeOffset.Now - age)));
        foreach (var t in list) BelegpostDb.Exec(c, "UPDATE melder_auftrag SET erledigt=@n, daten='abgelaufen' WHERE id=@i", ("@n", BelegpostDb.Ms(DateTimeOffset.Now)), ("@i", t.Id));
        return list;
    }

    private static List<MelderTask> Read(Microsoft.Data.Sqlite.SqliteConnection c, string tail, params (string, object?)[] p)
    {
        using var cmd = BelegpostDb.Cmd(c, "SELECT id, benutzer_id, geraet_id, art, vorgang_id, erstellt, zugestellt, erledigt FROM melder_auftrag " + tail, p);
        using var r = cmd.ExecuteReader();
        var list = new List<MelderTask>();
        while (r.Read())
            list.Add(new MelderTask(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                BelegpostDb.At(r.GetInt64(5)), r.IsDBNull(6) ? null : BelegpostDb.At(r.GetInt64(6)), r.IsDBNull(7) ? null : BelegpostDb.At(r.GetInt64(7))));
        return list;
    }
}

/// <summary>
/// Überwacht Fristen: nicht abgeholte Outlook-Aufträge (Beleg bleibt im Portal, Status 820) und
/// Outlook-Entwürfe ohne Versandbestätigung (Status 830; eine spätere Bestätigung setzt 110).
/// </summary>
public sealed class OutlookWatchWorker : BackgroundService
{
    private readonly MelderTasks _tasks;
    private readonly DocumentStore _store;
    private readonly EventHub _hub;
    private readonly IOptionsMonitor<MapiOptions> _opt;
    private readonly ILogger<OutlookWatchWorker> _log;

    public OutlookWatchWorker(MelderTasks tasks, DocumentStore store, EventHub hub, IOptionsMonitor<MapiOptions> opt, ILogger<OutlookWatchWorker> log)
    {
        _tasks = tasks; _store = store; _hub = hub; _opt = opt; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
            try { RunOnce(); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Outlook-Überwachung"); }
        }
    }

    public void RunOnce()
    {
        var o = _opt.CurrentValue;
        foreach (var t in _tasks.Expire(TimeSpan.FromHours(Math.Max(1, o.DeliveryTimeoutHours))))
        {
            if (t.VorgangId is null) continue;
            var v = _store.AddEvent(t.VorgangId, StatusCode.OutlookFehler, "Outlook", "Melder", "Kein Melder hat den Entwurf abgeholt – Beleg im Portal.");
            if (v is not null) _hub.Publish(t.UserId, "neu", v.Id, $"{v.DocumentType} {v.VoucherId}".Trim(), v.Status, "Outlook-Entwurf nicht zugestellt – bitte im Portal versenden.", v.Mail.Enabled);
        }
        var limit = DateTimeOffset.Now.AddHours(-Math.Max(1, o.OutlookConfirmHours));
        foreach (var v in _store.Query(new VorgangFilter { SeeAll = true, Statuses = new[] { VorgangStatus.Outlook }, Limit = 5000 }))
            if (v.StatusCode == StatusCode.AnOutlook && (v.OutlookHandedAt ?? v.CreatedAt) < limit)
                _store.AddEvent(v.Id, StatusCode.OutlookUnbestaetigt, "Outlook", "Überwachung", "Keine Versandbestätigung aus Outlook.");
    }
}
