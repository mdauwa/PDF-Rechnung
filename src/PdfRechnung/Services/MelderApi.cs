using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

public sealed record PairRequest(string? Name);
public sealed record MelderSettingsRequest(bool? Outlook, string? MapiMode = null);
public sealed record MapiModeRequest(string? Mode);
public sealed record SentConfirmation(DateTimeOffset? At);
public sealed record OutlookFailure(string? Message);

/// <summary>
/// „Belegpost Melder“: kleines Programm im Infobereich des Benutzer-PCs. Es wird im Portal gekoppelt
/// (Zugangsschlüssel je PC) und öffnet bei einem neuen Beleg dieses Benutzers sofort die Ansicht im Browser.
/// Dazu dieselben Ereignisse live für offene Portal-Tabs.
/// </summary>
public static class MelderApi
{
    public const string ExeName = "Belegpost-Melder.exe";
    public const string MacZipName = "Belegpost-Melder-macOS.zip";
    public const string Scheme = "belegpost-melder";

    public static void Map(WebApplication app)
    {
        // ---------- Portal (Cookie-Anmeldung) ----------
        var g = app.MapGroup("/api/melder");

        g.MapGet("/geraete", (PortalAccess access, HttpContext ctx, UserStore users, IOptionsMonitor<MapiOptions> mapi) =>
        {
            var a = access.For(ctx);
            var userDefault = a.UserId is null ? "" : users.FindById(a.UserId)?.MapiMode ?? "";
            return Results.Ok(new
            {
                mapiModes = MapiMode.All.Select(m => new { key = m, text = MapiMode.Text(m) }),
                mapiDefaultText = MapiMode.Text(userDefault.Length > 0 ? userDefault : mapi.CurrentValue.DefaultMode),
                devices = a.UserId is null ? new List<MelderDevice>() : users.Devices(a.UserId),
                canPair = a.UserId is not null,
                download = File.Exists(ExePath) ? "melder/" + ExeName : null,
                downloadMac = File.Exists(MacZipPath) ? "melder/" + MacZipName : null,
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

        // MAPI-Einstellung eines PCs: Benutzer für eigene Geräte, Admin für alle („überrollen“ – der Benutzer kann danach wieder ändern)
        g.MapPost("/geraete/{id}/mapi", (string id, MapiModeRequest req, PortalAccess access, HttpContext ctx, UserStore users, EventHub hub,
            PdfRechnung.Data.BelegpostDb db) =>
        {
            var a = access.For(ctx);
            var d = users.Device(id);
            if (d is null || (!a.IsAdmin && d.UserId != a.UserId)) return Results.NotFound();
            var mode = req.Mode ?? "";
            if (mode.Length > 0 && !MapiMode.All.Contains(mode)) return Results.BadRequest(new { error = "Unbekannte Einstellung." });
            var admin = a.IsAdmin && d.UserId != a.UserId;
            users.SetDeviceMapi(id, mode, admin ? "admin" : "benutzer", a.Name);
            db.Audit(a.Name, admin ? "MAPI-Einstellung überrollt" : "MAPI-Einstellung", d.Name, MapiMode.Text(mode));
            hub.Publish(d.UserId, "einstellung", "", "Einstellung geändert", mode, admin ? $"Vom Administrator gesetzt: {MapiMode.Text(mode)}" : null);
            return Results.Ok(new { saved = true });
        });

        // Admin: Vorgabe eines Benutzers setzen und optional auf alle seine Geräte überrollen
        g.MapPost("/benutzer/{userId}/mapi", (string userId, MapiModeRequest req, bool? alleGeraete, PortalAccess access, HttpContext ctx, UserStore users,
            EventHub hub, PdfRechnung.Data.BelegpostDb db) =>
        {
            var a = access.For(ctx);
            if (!a.IsAdmin) return Results.Json(new { error = "Nur Administratoren." }, statusCode: 403);
            var mode = req.Mode ?? "";
            if (mode.Length > 0 && !MapiMode.All.Contains(mode)) return Results.BadRequest(new { error = "Unbekannte Einstellung." });
            if (users.FindById(userId) is null) return Results.NotFound();
            users.Update(x => { var u = x.Users.FirstOrDefault(v => v.Id == userId); if (u is not null) u.MapiMode = mode; });
            var n = 0;
            if (alleGeraete == true)
                foreach (var d in users.Devices(userId)) { users.SetDeviceMapi(d.Id, mode, "admin", a.Name); n++; }
            db.Audit(a.Name, alleGeraete == true ? "MAPI-Vorgabe überrollt" : "MAPI-Vorgabe", userId, MapiMode.Text(mode));
            hub.Publish(userId, "einstellung", "", "Einstellung geändert", mode, alleGeraete == true ? $"Vom Administrator gesetzt: {MapiMode.Text(mode)}" : null);
            return Results.Ok(new { saved = true, devices = n });
        });

        // Admin: alle Geräte (für die Benutzerverwaltung)
        g.MapGet("/alle-geraete", (PortalAccess access, HttpContext ctx, UserStore users, IOptionsMonitor<MapiOptions> mapi) =>
        {
            var a = access.For(ctx);
            if (!a.IsAdmin) return Results.Json(new { error = "Nur Administratoren." }, statusCode: 403);
            return Results.Ok(new { devices = users.Devices(null), defaultMode = mapi.CurrentValue.DefaultMode });
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
            var latest = store.Query(new VorgangFilter { SeeAll = true, Owner = a.UserId, Limit = 1 }).FirstOrDefault()
                ?? store.Query(VorgangApi.Visibility(a) with { Limit = 1 }).FirstOrDefault();
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
            users.TouchDevice(f.Device.Id, ctx.Request.Query["pc"].ToString());
            return Results.Ok(new { user = f.User.Name, device = f.Device.Name, title = portal.Value.Title, outlook = f.User.SendViaOutlook,
                mapi = MapiInfo(users, f.User, users.Device(f.Device.Id) ?? f.Device, ctx) });
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
            if (req.Outlook is { } o) users.SetDeviceOutlook(f.Device.Id, o && f.User.SendViaOutlook);
            if (req.MapiMode is { } m)
            {
                if (m.Length > 0 && !MapiMode.All.Contains(m)) return Results.BadRequest(new { error = "Unbekannte Einstellung." });
                users.SetDeviceMapi(f.Device.Id, m, "benutzer", f.User.Name);
                ctx.RequestServices.GetRequiredService<PdfRechnung.Data.BelegpostDb>().Audit(f.User.Name, "MAPI-Einstellung (Melder)", f.Device.Name, MapiMode.Text(m));
            }
            return Results.Ok(new { saved = true, outlookAllowed = f.User.SendViaOutlook, mapi = MapiInfo(users, f.User, users.Device(f.Device.Id) ?? f.Device, ctx) });
        });

        // Aufträge an diesen Melder (z. B. MAPI-Mail in Outlook öffnen) – auch nach Offline-Zeit
        app.MapGet("/melder/api/auftraege", (HttpContext ctx, UserStore users, MelderTasks tasks) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            return Results.Ok(new { items = tasks.Pending(f.User.Id, f.Device.Id).Select(t => new { t.Id, kind = t.Kind, vorgangId = t.VorgangId, created = t.Created }) });
        });

        app.MapPost("/melder/api/auftraege/{id}/uebernehmen", (string id, HttpContext ctx, UserStore users, MelderTasks tasks) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            return tasks.Claim(id, f.User.Id, f.Device.Id) is { } t
                ? Results.Ok(new { t.Id, kind = t.Kind, vorgangId = t.VorgangId })
                : Results.Conflict(new { error = "Auftrag wurde bereits übernommen." });
        });

        app.MapPost("/melder/api/auftraege/{id}/erledigt", (string id, HttpContext ctx, UserStore users, MelderTasks tasks) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            tasks.Complete(id, f.User.Id);
            return Results.Ok(new { ok = true });
        });

        // Outlook-Versandbestätigung: das Element ist in „Gesendete Elemente“ aufgetaucht → Status 110
        app.MapPost("/melder/api/vorgang/{id}/gesendet", (string id, SentConfirmation req, HttpContext ctx, UserStore users, DocumentStore store) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            var v = store.Get(id);
            if (v is null || !PortalAccess.ForUser(f.User).CanSee(v)) return Results.NotFound();
            if (v.OutlookConfirmedAt is not null) return Results.Ok(new { ok = true, already = true });
            var at = req.At is { } a && a <= DateTimeOffset.Now.AddMinutes(5) && a > v.CreatedAt.AddMinutes(-5) ? a : DateTimeOffset.Now;
            var by = $"{f.User.Name} (Outlook, {f.Device.Name})";
            store.Update(id, x =>
            {
                x.OutlookConfirmedAt = at;
                x.SendChannel = "Outlook";
                x.SentBy = by;
                x.SentAt ??= at;
                x.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "outlookGesendet", "Versand aus Outlook bestätigt"));
                x.Status = VorgangStatus.Sent; // Statuswechsel erzeugt 110 (bzw. 120 bei erneutem Versand)
                return true;
            });
            if (v.Status == VorgangStatus.Sent) store.AddEvent(id, StatusCode.VersendetOutlook, "Outlook", by, null, at); // war schon versendet
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/melder/api/vorgang/{id}/outlookfehler", (string id, OutlookFailure req, HttpContext ctx, UserStore users, DocumentStore store) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            var v = store.Get(id);
            if (v is null || !PortalAccess.ForUser(f.User).CanSee(v)) return Results.NotFound();
            var msg = (req.Message ?? "").Trim();
            store.AddEvent(id, StatusCode.OutlookFehler, "Outlook", $"{f.User.Name} ({f.Device.Name})", msg.Length > 300 ? msg[..300] : msg);
            return Results.Ok(new { ok = true });
        });

        // Mail eines Vorgangs für Outlook: Empfänger, Betreff, HTML-Text, Logo (inline) und alle Anlagen
        app.MapGet("/melder/api/vorgang/{id}/mail", async (string id, HttpContext ctx, UserStore users, DocumentStore store, SendService send,
            IOptionsMonitor<MailOptions> mailOpt, CancellationToken ct) =>
        {
            if (users.FindDevice(Bearer(ctx)) is not { } f) return Results.Json(new { error = "Melder nicht (mehr) gekoppelt." }, statusCode: 401);
            var v = store.Get(id);
            if (v is null || !PortalAccess.ForUser(f.User).CanSee(v)) return Results.NotFound(new { error = "Vorgang nicht gefunden." });
            if (!f.User.SendViaOutlook && !v.Mail.Plain) return Results.Json(new { error = "Senden über Outlook ist für dich nicht freigeschaltet (Einstellungen → Benutzer)." }, statusCode: 403);
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
            if (!f.User.SendViaOutlook && !v.Mail.Plain) return Results.Json(new { error = "Senden über Outlook ist für dich nicht freigeschaltet (Einstellungen → Benutzer)." }, statusCode: 403);
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
            if (v is null || !PortalAccess.ForUser(f.User).CanSee(v) || !(f.User.SendViaOutlook || v.Mail.Plain)) return Results.NotFound();
            send.MarkOutlook(id, $"{f.User.Name} (Outlook, {f.Device.Name})");
            return Results.Ok(new { ok = true });
        });

        // Portal: „In Outlook öffnen“ → der eigene Melder öffnet den Entwurf
        app.MapPost("/api/vorgaenge/{id}/outlook", async (string id, PortalAccess access, HttpContext ctx, DocumentStore store, EventHub hub,
            SessionLauncher launcher, UserStore users, CancellationToken ct) =>
        {
            var a = access.For(ctx);
            var v = store.Get(id);
            if (v is null || !a.CanSee(v)) return Results.NotFound();
            if (a.UserId is null) return Results.BadRequest(new { error = "Nur mit einem Portal-Benutzer möglich (Melder ist an den Benutzer gekoppelt)." });
            // normale E-Mails öffnen sich immer im eigenen Outlook (wie ein Mailclient) – dafür braucht es keine Outlook-Freigabe
            if (!a.CanSend || (!a.Outlook && !v.Mail.Plain)) return Results.Json(new { error = "Senden über Outlook ist für dich nicht freigeschaltet – bitte an einen Administrator wenden." }, statusCode: 403);
            if (!v.Mail.Enabled) return Results.BadRequest(new { error = "Für diesen Beleg ist kein E-Mail-Versand vorgesehen." });
            if (v.Status == VorgangStatus.Error) return Results.BadRequest(new { error = "Der Vorgang hat Fehler – bitte zuerst beheben (z. B. Empfänger, Anlagen)." });
            // RDS mit Outlook am Server: Belege vom Belegpost-Drucker direkt in der Sitzung öffnen (kein Melder nötig)
            if (!v.Mail.Plain && users.FindById(a.UserId) is { } me && launcher.UsesSession(me))
            {
                var r = await launcher.OpenOutlookAsync(v, me, a.Name, ct);
                return r.Ok ? Results.Ok(new { message = r.Message }) : Results.BadRequest(new { error = r.Message });
            }
            if (hub.Subscribers(a.UserId) == 0) return Results.BadRequest(new { error = "Kein Melder verbunden – bitte den Belegpost Melder auf diesem PC starten." });
            var title = string.Join(" ", new[] { v.DocumentType, v.VoucherId }.Where(x => !string.IsNullOrWhiteSpace(x)));
            hub.Publish(a.UserId, "outlook", v.Id, title.Length > 0 ? title : v.OriginalFileName ?? v.Id, v.Status, null, v.Mail.Enabled);
            return Results.Ok(new { message = "An den Melder geschickt – Outlook öffnet sich gleich." });
        });

        app.MapGet("/melder/" + MacZipName, () =>
            File.Exists(MacZipPath) ? Results.File(MacZipPath, "application/zip", MacZipName) : Results.NotFound());

        // Download des Melders (liegt neben Belegpost.exe)
        app.MapGet("/melder/" + ExeName, () =>
            File.Exists(ExePath) ? Results.File(ExePath, "application/octet-stream", ExeName) : Results.NotFound());
    }

    private static object MapiInfo(UserStore users, PortalUser u, MelderDevice d, HttpContext ctx)
    {
        var global = ctx.RequestServices.GetRequiredService<IOptionsMonitor<MapiOptions>>().CurrentValue.DefaultMode;
        var effective = users.EffectiveMapiMode(u.Id, d.Id, global);
        return new
        {
            mode = d.MapiMode, effective, effectiveText = MapiMode.Text(effective),
            inherited = d.MapiMode.Length == 0 ? (u.MapiMode.Length > 0 ? "Vorgabe des Administrators" : "Standard") : null,
            source = d.MapiSource, changedBy = d.MapiChangedBy, changedAt = d.MapiChangedAt,
            note = d.MapiSource == "admin" && d.MapiChangedAt is { } at ? $"Vom Administrator am {at:dd.MM.yyyy} gesetzt" : null,
            outlookAllowed = u.SendViaOutlook,
            options = MapiMode.All.Select(m => new { key = m, text = MapiMode.Text(m) })
        };
    }

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, ExeName);
    public static string MacZipPath => Path.Combine(AppContext.BaseDirectory, MacZipName);

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
