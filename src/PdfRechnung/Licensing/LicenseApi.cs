using PdfRechnung.Services;

namespace PdfRechnung.Licensing;

public static class LicenseApi
{
    public static void Map(WebApplication app)
    {
        // Kurzstand für den Hinweisbalken (jeder Angemeldete)
        app.MapGet("/api/lizenz", (HttpContext ctx, PortalAccess access) =>
        {
            if (!access.For(ctx).Authenticated) return Results.Unauthorized();
            var g = License.Current;
            return Results.Ok(new { mode = g.Mode, reason = g.Reason, @base = g.Base, eRechnung = g.EInvoice, datenaustausch = g.Exchange });
        });

        var g = app.MapGroup("/api/admin/lizenz").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<PortalAccess>().For(ctx.HttpContext).IsAdmin
                ? await next(ctx) : (object?)Results.Json(new { error = "Nur für Administratoren." }, statusCode: 403));
        g.MapGet("", (LicenseService svc) => Results.Ok(svc.Status()));
        g.MapPost("/pruefen", async (LicenseService svc, CancellationToken ct) =>
        {
            await svc.RefreshAsync(ct);
            return Results.Ok(svc.Status());
        });
    }
}
