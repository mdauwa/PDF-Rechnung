using Microsoft.Extensions.Options;
using PdfRechnung.Delivery;
using PdfRechnung.Edi;
using PdfRechnung.Services;

namespace PdfRechnung.Connect;

/// <summary>ERP Suite Connect → Eingang / Ausgang: Ordner ansehen, Dateien prüfen und herunterladen.</summary>
public static class ConnectApi
{
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { ".xml", ".txt", ".csv", ".json", ".edi", ".dta", ".log" };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/connect").AddEndpointFilter(async (ctx, next) =>
        {
            var sp = ctx.HttpContext.RequestServices;
            var a = sp.GetRequiredService<PortalAccess>().For(ctx.HttpContext);
            var area = ctx.HttpContext.Request.RouteValues["area"] as string;
            if (!(area == "ausgang" ? a.SeeOutbound : a.SeeInbound)) return Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
            if (!sp.GetRequiredService<IOptionsMonitor<ConnectOptions>>().CurrentValue.Enabled) return Results.Json(new { error = "ERP Suite Connect ist nicht eingeschaltet." }, statusCode: 404);
            return await next(ctx);
        });

        g.MapGet("/{area}", (string area, string? archiv, IOptionsMonitor<ConnectOptions> opt, InboundLog book, OutboundLog outBook) =>
        {
            if (area is not ("eingang" or "ausgang")) return Results.NotFound();
            var o = opt.CurrentValue;
            var folder = ConnectFiles.Folder(o, area, archiv is "1" or "true");
            var exists = folder is not null && Directory.Exists(folder);
            return Results.Ok(new
            {
                area,
                folder,
                exists,
                hasArchive = ConnectFiles.Folder(o, area, true) is not null,
                version = ConnectSchema.Current.Version,
                files = exists ? ConnectFiles.List(folder!) : new(),
                received = area == "eingang" && archiv is not ("1" or "true") ? book.List(null, null, null, null, false, 300, "connect") : null,
                sent = area == "ausgang" && archiv is not ("1" or "true") ? outBook.List(null, null, null, null, null, 300, "connect") : null
            });
        });

        g.MapGet("/{area}/datei", (string area, string name, string? archiv, string? ansehen, IOptionsMonitor<ConnectOptions> opt) =>
        {
            var folder = ConnectFiles.Folder(opt.CurrentValue, area, archiv is "1" or "true");
            var path = folder is null ? null : ConnectFiles.Resolve(folder, name);
            if (path is null) return Results.NotFound(new { error = "Datei nicht gefunden." });
            if (ansehen is "1" && TextTypes.Contains(Path.GetExtension(name))) return Results.File(path, "text/plain; charset=utf-8");
            return Results.File(path, MimeTypes.For(name), name);
        });

        g.MapGet("/{area}/pruefung", (string area, string name, string? archiv, IOptionsMonitor<ConnectOptions> opt) =>
        {
            var folder = ConnectFiles.Folder(opt.CurrentValue, area, archiv is "1" or "true");
            var path = folder is null ? null : ConnectFiles.Resolve(folder, name);
            if (path is null) return Results.NotFound(new { error = "Datei nicht gefunden." });
            return Results.Ok(EdiValidation.Validate(File.ReadAllBytes(path), name));
        });
    }
}
