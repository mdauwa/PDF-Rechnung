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

        app.MapPut("/api/meine-regeln", (HttpContext ctx, SaveRequest req, PortalAccess access, UserStore users, MailRuleStore store, SettingsStore settings) =>
        {
            var (user, error) = Target(ctx, access, users);
            if (user is null) return error!;
            var set = req.Set ?? new MailRuleSet();
            foreach (var r in set.Rules) r.Users.Clear(); // „Benutzer“ gibt es nur global
            var errors = MailRuleStore.Validate(set, global: false);
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors) });
            foreach (var sig in set.Signatures) sig.Text = MailHtml.ForStorage(sig.Text, settings.DataDirectory);
            store.Save(user.Id, set);
            return Results.Ok(new { ok = true, message = "Signaturen und Regeln gespeichert – sie gelten für neue Belege." });
        });
    }

    /// <summary>
    /// Bilder für Vorlagen und Signaturen (Editor): hochladen (PNG/JPG/GIF, höchstens 2 MB) und anzeigen.
    /// Jeder angemeldete Benutzer darf – Signaturen pflegen auch Nicht-Admins.
    /// </summary>
    public static void MapImages(WebApplication app)
    {
        app.MapPost("/api/mailbilder", async (HttpContext ctx, PortalAccess access, SettingsStore settings) =>
        {
            if (!access.For(ctx).Authenticated) return Results.Unauthorized();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length > MailHtml.MaxImageBytes) return Results.BadRequest(new { error = "Das Bild ist größer als 2 MB." });
            var bytes = ms.ToArray();
            var ext = MailHtml.DetectImage(bytes);
            if (ext is null) return Results.BadRequest(new { error = "Nur PNG-, JPG- oder GIF-Bilder sind möglich." });
            return Results.Ok(new { url = MailHtml.UrlPrefix + MailHtml.SaveImage(bytes, ext, settings.DataDirectory) });
        });
        app.MapGet("/api/mailbilder/{file}", (string file, HttpContext ctx, PortalAccess access, SettingsStore settings) =>
        {
            if (!access.For(ctx).Authenticated) return Results.Unauthorized();
            var name = Path.GetFileName(file);
            var path = Path.Combine(MailHtml.ImageDirectory(settings.DataDirectory), name);
            return File.Exists(path) ? Results.File(path, MailDesigner.ImageType(path)) : Results.NotFound();
        });

        // ---------- Bildverwaltung: globale Bilder für {Bild:Name} ----------
        // Lesen darf jeder Angemeldete (Auswahl im Editor), ändern nur Administratoren.
        app.MapGet("/api/mailbilder-katalog", (HttpContext ctx, PortalAccess access, SettingsStore settings) =>
        {
            if (!access.For(ctx).Authenticated) return Results.Unauthorized();
            var dir = MailHtml.ImageDirectory(settings.DataDirectory);
            return Results.Ok(MailImages.Load(settings.DataDirectory).Select(e =>
            {
                var path = Path.Combine(dir, e.File);
                var size = File.Exists(path) ? MailImages.Size(File.ReadAllBytes(path)) : null;
                return new
                {
                    name = e.Name, file = e.File, width = e.Width, url = MailHtml.UrlPrefix + e.File, variable = "{Bild:" + e.Name + "}",
                    pixelWidth = size?.W, pixelHeight = size?.H, bytes = File.Exists(path) ? new FileInfo(path).Length : 0, missing = !File.Exists(path)
                };
            }));
        });
        app.MapPost("/api/mailbilder-katalog", async (HttpContext ctx, PortalAccess access, SettingsStore settings) =>
        {
            if (!access.For(ctx).IsAdmin) return Results.Json(new { error = "Nur Administratoren dürfen Bilder verwalten." }, statusCode: 403);
            var name = ctx.Request.Query["name"].ToString().Trim();
            int? width = int.TryParse(ctx.Request.Query["width"], out var w) ? w : null;
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length > MailHtml.MaxImageBytes) return Results.BadRequest(new { error = "Das Bild ist größer als 2 MB." });
            try { var e = MailImages.Put(settings.DataDirectory, name, ms.ToArray(), width); return Results.Ok(new { e.Name, e.File, e.Width }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPut("/api/mailbilder-katalog/{name}", (string name, MailImageUpdate body, HttpContext ctx, PortalAccess access, SettingsStore settings) =>
        {
            if (!access.For(ctx).IsAdmin) return Results.Json(new { error = "Nur Administratoren dürfen Bilder verwalten." }, statusCode: 403);
            try
            {
                var e = MailImages.Update(settings.DataDirectory, name, (body.Name ?? name).Trim(), body.Width);
                return e is null ? Results.NotFound(new { error = "Bild nicht gefunden." }) : Results.Ok(new { e.Name, e.File, e.Width });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapDelete("/api/mailbilder-katalog/{name}", (string name, HttpContext ctx, PortalAccess access, SettingsStore settings) =>
        {
            if (!access.For(ctx).IsAdmin) return Results.Json(new { error = "Nur Administratoren dürfen Bilder verwalten." }, statusCode: 403);
            return MailImages.Remove(settings.DataDirectory, name) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Bild nicht gefunden." });
        });
    }

    public sealed record MailImageUpdate(string? Name, int? Width);

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
