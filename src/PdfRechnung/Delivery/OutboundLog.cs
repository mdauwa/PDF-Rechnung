using PdfRechnung.Data;
using PdfRechnung.Services;

namespace PdfRechnung.Delivery;

/// <summary>Ein Eintrag im Ausgang: eine aus einem Ausgangsordner gesendete oder wegen Prüffehlern zurückgehaltene Datei.</summary>
public sealed record OutboundEntry(long Nr, DateTimeOffset At, string Channel, string ChannelText, string? Source, string? CustomerId, string? Customer,
    string FileName, long Size, string Status, string Stored, bool Exists, string? Check, string? CheckText, string? ConnectType);

/// <summary>
/// Ausgangsbuch: jede Datei, die der Dienst aus dem Ausgangsordner eines Versandwegs sendet (Status „gesendet“) oder wegen
/// Prüffehlern in den Unterordner „fehlerhaft“ legt (Status „fehlerhaft“). Die Datei selbst bleibt im Archiv- bzw. Fehlerordner.
/// </summary>
public sealed class OutboundLog
{
    public const string Sent = "gesendet", Held = "fehlerhaft";
    private readonly BelegpostDb _db;

    public OutboundLog(BelegpostDb db) => _db = db;

    /// <summary>Datei vermerken (Fehler hier dürfen den Versand nie stören).</summary>
    public long Add(string channel, string? source, string? customerId, string? customer, string fileName, byte[] data, string status, string storedPath,
        Edi.EdiReport? report = null)
    {
        try
        {
            if (report is null && Edi.EdiValidation.CanValidate(data) && !Edi.EdiValidation.IsPdf(data))
                try { report = Edi.EdiValidation.Validate(data, fileName); } catch (Exception) { }
            string? connect = null;
            try { if (InboundLog.ConnectEnabled()) connect = Connect.ConnectFiles.TypeOf(fileName, data); } catch (Exception) { }
            using var c = _db.Open();
            BelegpostDb.Exec(c, "INSERT INTO ausgang(zeit, kanal, quelle, kunde_id, kunde, datei, groesse, status, ablage, pruefung, pruefung_text, connect_typ) " +
                "VALUES(@z,@k,@q,@ki,@ku,@d,@g,@s,@a,@p,@pt,@c)",
                ("@z", BelegpostDb.Ms(DateTimeOffset.Now)), ("@k", channel), ("@q", source), ("@ki", customerId), ("@ku", customer),
                ("@d", fileName), ("@g", (long)data.Length), ("@s", status), ("@a", InboundLog.Normalize(storedPath)),
                ("@p", report?.Status), ("@pt", report?.Summary), ("@c", connect));
            return Convert.ToInt64(BelegpostDb.Scalar(c, "SELECT last_insert_rowid()"));
        }
        catch (Exception) { return 0; }
    }

    /// <param name="area">null = Ausgang (ohne ERP-Suite-Connect-Dateien), "connect" = nur ERP Suite Connect.</param>
    public List<OutboundEntry> List(string? text, DateTimeOffset? from, DateTimeOffset? before, string? channel, string? status, int limit = 1000, string? area = null)
    {
        using var c = _db.Open();
        var where = new List<string>();
        var p = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(text)) { where.Add("(datei LIKE @q OR kunde LIKE @q OR quelle LIKE @q)"); p.Add(("@q", "%" + text.Trim() + "%")); }
        if (from is not null) { where.Add("zeit >= @f"); p.Add(("@f", BelegpostDb.Ms(from.Value))); }
        if (before is not null) { where.Add("zeit < @b"); p.Add(("@b", BelegpostDb.Ms(before.Value))); }
        if (!string.IsNullOrWhiteSpace(channel)) { where.Add("kanal = @k"); p.Add(("@k", channel)); }
        if (status is Sent or Held) { where.Add("status = @s"); p.Add(("@s", status)); }
        where.Add(area == "connect" ? "connect_typ IS NOT NULL" : "connect_typ IS NULL");
        p.Add(("@l", Math.Clamp(limit, 1, 5000)));
        using var cmd = BelegpostDb.Cmd(c, Select + " WHERE " + string.Join(" AND ", where) + " ORDER BY nr DESC LIMIT @l", p.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<OutboundEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public OutboundEntry? Get(long nr)
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, Select + " WHERE nr=@n", ("@n", nr));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    private const string Select = "SELECT nr, zeit, kanal, quelle, kunde_id, kunde, datei, groesse, status, ablage, pruefung, pruefung_text, connect_typ FROM ausgang";

    private static OutboundEntry Read(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var channel = r.GetString(2);
        var stored = r.GetString(9);
        return new OutboundEntry(r.GetInt64(0), BelegpostDb.At(r.GetInt64(1)), channel, ChannelType.Text(channel),
            r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6), r.GetInt64(7), r.GetString(8), stored, File.Exists(stored),
            r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12));
    }
}

/// <summary>Vorgänge → Ausgang: aus Ausgangsordnern gesendete Dateien ansehen (Benutzer mit Recht „Ausgang sehen“).</summary>
public static class OutboundApi
{
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".xml", ".edi", ".txt", ".csv", ".json", ".x12", ".edifact", ".inh", ".dat", ".log" };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/ausgang").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<PortalAccess>().For(ctx.HttpContext);
            return a.SeeOutbound ? await next(ctx) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
        });

        g.MapGet("", (string? q, string? from, string? to, string? kanal, string? status, OutboundLog book) => Results.Ok(new
        {
            items = book.List(q,
                DateOnly.TryParse(from, out var f) ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue)) : null,
                DateOnly.TryParse(to, out var t) ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
                kanal, status),
            channels = ChannelType.All.Where(x => x != ChannelType.Gotomaxx).Select(x => new { key = x, text = ChannelType.Text(x) })
        }));

        g.MapGet("/{nr:long}/datei", (long nr, string? ansehen, OutboundLog book) =>
        {
            var e = book.Get(nr);
            if (e is null) return Results.NotFound(new { error = "Eintrag nicht gefunden." });
            if (!e.Exists) return Results.NotFound(new { error = "Die Datei liegt nicht mehr im Archiv- bzw. Fehlerordner." });
            var ext = Path.GetExtension(e.FileName);
            var view = ansehen is "1" or "true";
            if (view && ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return Results.File(e.Stored, "application/pdf", enableRangeProcessing: true);
            if (view && TextTypes.Contains(ext)) return Results.File(e.Stored, "text/plain; charset=utf-8");
            return Results.File(e.Stored, MimeTypes.For(e.FileName), e.FileName);
        });

        g.MapGet("/{nr:long}/pruefung", (long nr, OutboundLog book) =>
        {
            var e = book.Get(nr);
            if (e is null || !e.Exists) return Results.NotFound(new { error = "Datei nicht mehr vorhanden." });
            return Results.Ok(Edi.EdiValidation.Validate(File.ReadAllBytes(e.Stored), e.FileName));
        });
    }
}
