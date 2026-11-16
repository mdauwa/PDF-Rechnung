using Microsoft.Extensions.Options;
using PdfRechnung.Services;
using PdfRechnung.Settings;

namespace PdfRechnung.Documents;

/// <summary>
/// Eigene Signaturen und Mail-Regeln eines Benutzers („Meine Mail-Regeln“).
/// Jeder angemeldete Portal-Benutzer bearbeitet seine eigenen; Administratoren zusätzlich die aller Benutzer (?user=…).
/// </summary>
public static class MailRulesApi
{
    public sealed record SaveRequest(MailRuleSet? Set);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/meine-regeln", (HttpContext ctx, PortalAccess access, UserStore users, MailRuleStore store,
            IOptionsSnapshot<MailTemplateOptions> templates, IOptionsSnapshot<StationeryOptions> stationery, IOptionsSnapshot<MailRuleOptions> global,
            IOptionsSnapshot<GotomaxxOptions> gotomaxx, IOptionsSnapshot<MailOptions> mail, GotomaxxService gm) =>
        {
            var (user, error) = Target(ctx, access, users);
            if (user is null) return error!;
            return Results.Ok(new
            {
                userId = user.Id,
                name = user.Name,
                set = store.Get(user.Id),
                choices = new
                {
                    templates = templates.Value.Items.Select(t => t.Name).Where(n => n.Length > 0),
                    stationeries = stationery.Value.Items.Select(s => s.Name).Where(n => n.Length > 0),
                    globalSignatures = global.Value.Signatures.Select(s => s.Name).Where(n => n.Length > 0),
                    routes = MailRoute.All.Select(r => new { key = r, text = MailRoute.Text(r) }),
                    gotomaxx = gotomaxx.Value.Enabled,
                    printers = gotomaxx.Value.Enabled ? gm.Printers() : Array.Empty<string>(),
                    outlook = user.SendViaOutlook,
                    defaultSender = mail.Value.FromAddress
                }
            });
        });

        app.MapPut("/api/meine-regeln", (HttpContext ctx, SaveRequest req, PortalAccess access, UserStore users, MailRuleStore store) =>
        {
            var (user, error) = Target(ctx, access, users);
            if (user is null) return error!;
            var set = req.Set ?? new MailRuleSet();
            foreach (var r in set.Rules) r.Users.Clear(); // „Benutzer“ gibt es nur global
            var errors = MailRuleStore.Validate(set, global: false);
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors) });
            store.Save(user.Id, set);
            return Results.Ok(new { ok = true, message = "Signaturen und Regeln gespeichert – sie gelten für neue Belege." });
        });
    }

    /// <summary>Eigener Benutzer oder – für Administratoren – der per ?user= gewählte.</summary>
    private static (PortalUser? User, IResult? Error) Target(HttpContext ctx, PortalAccess access, UserStore users)
    {
        var a = access.For(ctx);
        if (!a.Authenticated) return (null, Results.Unauthorized());
        var wanted = ctx.Request.Query["user"].ToString();
        if (!string.IsNullOrEmpty(wanted) && wanted != a.UserId)
        {
            if (!a.IsAdmin) return (null, Results.Json(new { error = "Nur Administratoren dürfen die Regeln anderer Benutzer bearbeiten." }, statusCode: 403));
            return users.FindById(wanted) is { } other ? (other, null) : (null, Results.NotFound(new { error = "Benutzer nicht gefunden." }));
        }
        if (a.UserId is null || users.FindById(a.UserId) is not { } self)
            return (null, Results.BadRequest(new { error = "Eigene Regeln gibt es nur für Portal-Benutzer (nicht für den Server-Administrator)." }));
        return (self, null);
    }
}
