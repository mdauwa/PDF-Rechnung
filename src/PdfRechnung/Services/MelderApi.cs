using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

public sealed record PairRequest(string? Name);
public sealed record MelderSettingsRequest(bool Outlook);

/// <summary>
/// „Belegpost Melder“: kleines Programm im Infobereich des Benutzer-PCs. Es wird im Portal gekoppelt
/// (Zugangsschlüssel je PC) und öffnet bei einem neuen Beleg dieses Benutzers sofort die Ansicht im Browser.
/// Dazu dieselben Ereignisse live für offene Portal-Tabs.
/// </summary>
public static class MelderApi
{
    public const string ExeName = "Belegpost-Melder.exe";
    public const string Scheme = "belegpost-melder";

    public static void Map(WebApplication app)
    {
        // ---------- Portal (Cookie-Anmeldung) ----------
        var g = app.MapGroup("/api/melder");

        g.MapGet("/geraete", (PortalAccess access, HttpContext ctx, UserStore users) =>
        {
            var a = access.For(ctx);
            return Results.Ok(new
            {
                devices = a.UserId is null ? new List<MelderDevice>() : users.Devices(a.UserId),
                canPair = a.UserId is not null,
                download = File.Exists(ExePath) ? "melder/" + ExeName : null,
                server = $"{ctx.Request.Scheme}://{ctx.Request.Host}"
            });
        });

        g.MapPost("/koppeln", (PairRequest req, PortalAccess access, HttpContext ctx, UserStore users, CertificateProvider certs,
            IOptionsSnapshot<PortalOptions> portal) =>
        {
            var a = access.For(ctx);
            if (a.UserId is null)
                return Results.BadRequest(new { error = "Der Melder wird an einen Portal-Benutzer gekoppelt – bitte mit dem eigenen Konto anmelden (nicht als Server-Administrator)." });
            var (device, token) = users.AddDevice(a.UserId, req.Name ?? "PC");
            var code = PairingCode(new PairingData($"{ctx.Request.Scheme}://{ctx.Request.Host}", token, Thumbprint(certs), portal.Value.Title, a.Name));
            return Results.Ok(new { code, link = $"{Scheme}:koppeln?c={code}", device = device.Id });
        });

        g.MapDelete("/geraete/{id}", (string id, PortalAccess access, HttpContext ctx, UserStore users) =>
        {
            var a = access.For(ctx);
            var ok = users.RemoveDevice(id, a.IsAdmin ? null : a.UserId ?? "-");
            return ok ? Results.Ok(new { removed = true }) : Results.NotFound();
        });

        g.MapPost("/test", (PortalAccess access, HttpContext ctx, EventHub hub, DocumentStore store) =>
        {
            var a = access.For(ctx);
            if (a.UserId is null) return Results.BadRequest(new { error = "Nur mit einem Portal-Benutzer möglich." });
            var latest = store.All().FirstOrDefault(v => v.OwnerUserId == a.UserId) ?? store.All().FirstOrDefault(a.CanSee);
            if (latest is null) return Results.BadRequest(new { error = "Es gibt noch keinen Vorgang zum Anzeigen." });
            hub.Publish(a.UserId, "test", latest.Id, "Test: " + (latest.VoucherId ?? latest.OriginalFileName ?? latest.Id), latest.Status, "Testmeldung aus dem Portal");
            return Results.Ok(new { listeners = hub.Subscribers(a.UserId) });
        });

        // Live-Ereignisse für offene Portal-Tabs
        app.MapGet("/api/ereignisse", async (PortalAccess access, HttpContext ctx, EventHub hub) =>
        {
            var a = access.For(ctx);
            if (a.UserId is null) { ctx.Response.StatusCode = 204; return; }
            using var sub = hub.Subscribe(a.UserId, LastId(ctx));
            await EventHub.Stream(ctx, sub, ctx.RequestAborted);
        });

        // ---------- Melder (Zugangsschlüssel statt Cookie; liegt bewusst außerhalb von /api) ----------
        app.MapGet("/melder/api/hallo", (HttpContext ctx, UserStore users, IOptionsSnapshot<PortalOptions> portal) =>
        {
            var found = users.FindDevice(Bearer(ctx));
            if (found is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            users.TouchDevice(f.Device.Id);
            return Results.Ok(new { user = f.User.Name, device = f.Device.Name, title = portal.Value.Title, outlook = f.User.SendViaOutlook });
        });

        app.MapGet("/melder/api/ereignisse", async (HttpContext ctx, UserStore users, EventHub hub) =>
        {
            var found = users.FindDevice(Bearer(ctx));
            if (found is not { } f)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Melder nicht (mehr) gekoppelt." });
                return;
            }
            users.TouchDevice(f.Device.Id);
            using var sub = hub.Subscribe(f.User.Id, LastId(ctx));
            await EventHub.Stream(ctx, sub, ctx.RequestAborted);
        });

        // Einstellungen des Melders (z. B. „neue Belege in Outlook öffnen“ → kein Autoversand für diesen Benutzer)
        app.MapPost("/melder/api/einstellungen", (MelderSettingsRequest req, HttpContext ctx, UserStore users) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            users.SetDeviceOutlook(f.Device.Id, req.Outlook && f.User.SendViaOutlook);
            return Results.Ok(new { saved = true, outlookAllowed = f.User.SendViaOutlook });
        });

        // Mail eines Vorgangs für Outlook: Empfänger, Betreff, HTML-Text, Logo (inline) und alle Anlagen
        app.MapGet("/melder/api/vorgang/{id}/mail", async (string id, HttpContext ctx, UserStore users, DocumentStore store, SendService send,
            IOptionsMonitor<MailOptions> mailOpt, CancellationToken ct) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            var v = store.Get(id);
            if (v is null || !PortalAccess.ForUser(f.User).CanSee(v)) return Results.NotFound(new { error = "Vorgang nicht gefunden." });
            if (!f.User.SendViaOutlook) return Results.Json(new { error = "Senden über Outlook ist für dich nicht freigeschaltet (Einstellungen → Benutzer)." }, statusCode: 403);
            if (!v.Mail.Enabled) return Results.Json(new { error = "Für diesen Beleg ist kein E-Mail-Versand vorgesehen (@@ml0@@)." }, statusCode: 409);
            var (mail, err) = await send.BuildMail(v, ct);
            if (mail is null) return Results.Json(new { error = err }, statusCode: 409);
            var o = mailOpt.CurrentValue;
            return Results.Ok(new
            {
                id = v.Id, to = mail.To, cc = mail.Cc, bcc = mail.Bcc, subject = mail.Subject,
                html = mail.HtmlBody, text = mail.Body,
                from = string.IsNullOrWhiteSpace(v.Mail.FromAddress) ? o.FromAddress : v.Mail.FromAddress,
                replyTo = mail.Reply(o),
                status = v.Status,
                attachments = mail.Attachments.Select(a => new { name = a.Name, contentType = a.ContentType, data = Convert.ToBase64String(a.Data), contentId = (string?)null })
                    .Concat(mail.Images.Select(i => new { name = i.Name, contentType = i.ContentType, data = Convert.ToBase64String(i.Data), contentId = (string?)i.ContentId }))
            });
        });

        // Dieselbe Mail als .eml-Entwurf (X-Unsent) – für PCs ohne klassisches Outlook
        app.MapGet("/melder/api/vorgang/{id}/eml", async (string id, HttpContext ctx, UserStore users, DocumentStore store, SendService send,
            IOptionsMonitor<MailOptions> mailOpt, CancellationToken ct) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            var v = store.Get(id);
            if (v is null || !PortalAccess.ForUser(f.User).CanSee(v)) return Results.NotFound(new { error = "Vorgang nicht gefunden." });
            if (!f.User.SendViaOutlook) return Results.Json(new { error = "Senden über Outlook ist für dich nicht freigeschaltet (Einstellungen → Benutzer)." }, statusCode: 403);
            var (mail, err) = await send.BuildMail(v, ct);
            if (mail is null) return Results.Json(new { error = err }, statusCode: 409);
            var msg = MailSender.BuildMime(mailOpt.CurrentValue, mail, includeCopyTo: false);
            msg.Headers.Add("X-Unsent", "1");
            if (msg.From.Count == 0) msg.Headers.Remove(MimeKit.HeaderId.From); // Outlook nimmt dann das eigene Konto
            using var ms = new MemoryStream();
            await msg.WriteToAsync(ms, ct);
            return Results.File(ms.ToArray(), "message/rfc822", MailTemplate.SafeFileName(v.Mail.FileName) + ".eml");
        });

        // Melder meldet: Entwurf ist in Outlook offen → Vorgang „An Outlook übergeben“, kein Autoversand mehr
        app.MapPost("/melder/api/vorgang/{id}/outlook", (string id, HttpContext ctx, UserStore users, DocumentStore store, SendService send) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            var v = store.Get(id);
            if (v is null || !PortalAccess.ForUser(f.User).CanSee(v) || !f.User.SendViaOutlook) return Results.NotFound();
            send.MarkOutlook(id, $"{f.User.Name} (Outlook, {f.Device.Name})");
            return Results.Ok(new { ok = true });
        });

        // Portal: „In Outlook öffnen“ → der eigene Melder öffnet den Entwurf
        app.MapPost("/api/vorgaenge/{id}/outlook", (string id, PortalAccess access, HttpContext ctx, DocumentStore store, EventHub hub) =>
        {
            var a = access.For(ctx);
            var v = store.Get(id);
            if (v is null || !a.CanSee(v)) return Results.NotFound();
            if (a.UserId is null) return Results.BadRequest(new { error = "Nur mit einem Portal-Benutzer möglich (Melder ist an den Benutzer gekoppelt)." });
            if (!a.Outlook || !a.CanSend) return Results.Json(new { error = "Senden über Outlook ist für dich nicht freigeschaltet – bitte an einen Administrator wenden." }, statusCode: 403);
            if (!v.Mail.Enabled) return Results.BadRequest(new { error = "Für diesen Beleg ist kein E-Mail-Versand vorgesehen." });
            if (v.Status == VorgangStatus.Error) return Results.BadRequest(new { error = "Der Vorgang hat Fehler – bitte zuerst beheben (z. B. Empfänger, Anlagen)." });
            if (hub.Subscribers(a.UserId) == 0) return Results.BadRequest(new { error = "Kein Melder verbunden – bitte den Belegpost Melder auf diesem PC starten." });
            var title = string.Join(" ", new[] { v.DocumentType, v.VoucherId }.Where(x => !string.IsNullOrWhiteSpace(x)));
            hub.Publish(a.UserId, "outlook", v.Id, title.Length > 0 ? title : v.OriginalFileName ?? v.Id, v.Status, null, v.Mail.Enabled);
            return Results.Ok(new { message = "An den Melder geschickt – Outlook öffnet sich gleich." });
        });

        // Download des Melders (liegt neben Belegpost.exe)
        app.MapGet("/melder/" + ExeName, () =>
            File.Exists(ExePath) ? Results.File(ExePath, "application/octet-stream", ExeName) : Results.NotFound());
    }

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, ExeName);

    private static string? Bearer(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    private static long? LastId(HttpContext ctx)
    {
        var s = ctx.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrEmpty(s)) s = ctx.Request.Query["after"].ToString();
        return long.TryParse(s, out var v) ? v : null;
    }

    private static string? Thumbprint(CertificateProvider certs) =>
        certs.Current is { } c ? Convert.ToHexString(SHA256.HashData(c.RawData)) : null;

    public sealed record PairingData(string S, string T, string? F, string? N, string? U);

    /// <summary>Kopplungscode: Base64url(JSON mit Server-Adresse, Zugangsschlüssel, Zertifikats-Fingerabdruck).</summary>
    public static string PairingCode(PairingData d) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { s = d.S, t = d.T, f = d.F, n = d.N, u = d.U })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
