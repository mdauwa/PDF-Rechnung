using PdfRechnung.Services;

namespace PdfRechnung.Delivery;

/// <summary>Vorgänge → Eingang: abgerufene Dateien ansehen und herunterladen (Benutzer, die alle Vorgänge sehen).</summary>
public static class InboundApi
{
    public sealed record ReadRequest(List<long>? Nrs, bool All = false);

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".xml", ".edi", ".txt", ".csv", ".json", ".x12", ".edifact", ".inh", ".dat", ".log" };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/eingang").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<PortalAccess>().For(ctx.HttpContext);
            return a.Authenticated && (a.SeeAll || a.IsAdmin) ? await next(ctx) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
        });

        g.MapGet("", (string? q, string? from, string? to, string? kanal, string? ungelesen, string? anzahl, InboundLog book) =>
        {
            if (anzahl is "1" or "true") return (IResult)Results.Ok(new { unread = book.Unread() });
            var items = book.List(q,
                DateOnly.TryParse(from, out var f) ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue)) : null,
                DateOnly.TryParse(to, out var t) ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
                kanal, ungelesen is "1" or "true");
            return Results.Ok(new
            {
                items,
                unread = book.Unread(),
                channels = ChannelType.All.Where(x => x != ChannelType.Gotomaxx).Select(x => new { key = x, text = ChannelType.Text(x) })
            });
        });

        g.MapGet("/{nr:long}/datei", (long nr, string? ansehen, InboundLog book) =>
        {
            var x = book.Download(nr);
            if (x is null) return Results.NotFound(new { error = "Eintrag nicht gefunden." });
            if (x.Value.Path is null) return Results.NotFound(new { error = "Die Datei liegt weder im Archiv noch im Zielordner." });
            book.MarkRead(new[] { nr });
            var name = x.Value.Entry.FileName;
            var ext = Path.GetExtension(name);
            var view = ansehen is "1" or "true";
            if (view && ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return Results.File(x.Value.Path, "application/pdf", enableRangeProcessing: true);
            if (view && TextTypes.Contains(ext))
                return Results.File(x.Value.Path, "text/plain; charset=utf-8");
            return Results.File(x.Value.Path, MimeTypes.For(name), name);
        });

        g.MapGet("/{nr:long}/pruefung", (long nr, InboundLog book) =>
        {
            var x = book.Download(nr);
            if (x?.Path is null) return Results.NotFound(new { error = "Datei nicht mehr vorhanden." });
            return Results.Ok(Edi.EdiValidation.Validate(File.ReadAllBytes(x.Value.Path), x.Value.Entry.FileName));
        });

        g.MapPost("/gelesen", (ReadRequest req, InboundLog book) =>
            Results.Ok(new { changed = book.MarkRead(req.All ? null : req.Nrs ?? new()), unread = book.Unread() }));
    }
}
