using PdfRechnung.Services;

namespace PdfRechnung.Documents;

/// <summary>Menügruppe „Gotomaxx“: Protokoll je Drucker (gotomaxx-Profil) mit Belegdaten und ERP-Rückmeldung.</summary>
public static class GotomaxxLogApi
{
    /// <summary>null = alle sichtbar; sonst die Benutzer, deren Drucke der Angemeldete sieht.</summary>
    public static IReadOnlyCollection<string>? Owners(HttpContext ctx)
    {
        var a = ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx);
        return a.IsAdmin || a.SeeAll || a.SeeOutbound ? null : (a.Visible?.ToList() ?? new List<string>());
    }

    /// <summary>Eintrag für die Liste (Feldnamen dürfen sich auch in camelCase nicht doppeln).</summary>
    public static object Item(GotomaxxLogEntry e, Erp.ErpFeedbackService? erp)
    {
        var job = e.ErpJob is { } id && erp is not null ? erp.JobState(id) : null;
        return new
        {
            e.Nr, e.At, e.Profile, e.User, e.Computer, e.Source, code = e.State, state = e.StateText, e.Actions, e.Error,
            e.DocType, e.DocNumber, e.VorgangId, e.ErpIndex, e.ErpStatus, e.ErpMessage, e.Mandant,
            erpJob = job is null ? null : new { status = job.Value.Status, message = job.Value.Message }
        };
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/gotomaxx-protokoll").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<PortalAccess>().For(ctx.HttpContext);
            // Ausgang-Recht bzw. „sieht alle Belege“: alles; sonst nur die eigenen (und freigegebenen) Drucke
            return a.Authenticated && (a.SeeOutbound || a.UserId is not null) ? await next(ctx) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
        });

        g.MapGet("/drucker", (GotomaxxLogService log) => Results.Ok(new
        {
            printers = log.Profiles(),
            state = new { at = log.State.At, error = log.State.Error, files = log.State.Files }
        }));

        g.MapGet("", (string? drucker, string? q, string? status, string? from, string? to, GotomaxxLogService log, Erp.ErpFeedbackService erp, HttpContext ctx) =>
        {
            var items = log.List(drucker, q, status,
                DateOnly.TryParse(from, out var f) ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue)) : null,
                DateOnly.TryParse(to, out var t) ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null, 1000,
                owners: Owners(ctx));
            var a = ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx);
            return Results.Ok(new
            {
                items = items.Select(e => Item(e, erp)),
                canChange = a.CanSend
            });
        });

        // Zuordnung und ERP-Rückmeldung erneut versuchen
        g.MapPost("/{nr:long}/erneut", async (long nr, GotomaxxLogService log, HttpContext ctx, CancellationToken ct) =>
        {
            var a = ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx);
            if (!a.CanSend) return Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
            if (log.Get(nr) is not { } e || (Owners(ctx) is { } own && (e.Owner is null || !own.Contains(e.Owner)))) return Results.NotFound();
            log.Reset(nr);
            await log.ScanAsync(ct);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/admin/gotomaxx-protokoll/profile", (HttpContext ctx, GotomaxxLogService log) =>
            Settings.AdminApi.IsAdmin(ctx) ? Results.Ok(log.ProfileInfos().Select(x => new { name = x.Name, total = x.Total, last = x.Last })) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403));

        app.MapPost("/api/admin/gotomaxx-protokoll/lesen", async (HttpContext ctx, GotomaxxLogService log, CancellationToken ct) =>
        {
            if (!Settings.AdminApi.IsAdmin(ctx)) return Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
            var n = await log.ScanAsync(ct);
            return Results.Ok(new { ok = log.State.Error is null, message = log.State.Error ?? $"{n} Einträge gelesen aus {log.State.Files} Protokolldatei(en)." });
        });
    }
}
