using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

public sealed record PairRequest(string? Name);

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
            return Results.Ok(new { user = f.User.Name, device = f.Device.Name, title = portal.Value.Title });
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
