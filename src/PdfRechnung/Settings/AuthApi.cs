using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PdfRechnung.Services;

namespace PdfRechnung.Settings;

public sealed record PasswordRequest(string? Password, string? Current = null);
public sealed record SaveUsersRequest(List<PortalUser> Users);

/// <summary>
/// Anmeldung: lokaler Administrator (Kennwort, nur direkt am Server; wird beim ersten Start festgelegt) und
/// Benutzer über Microsoft Entra ID. Dazu die Benutzerverwaltung.
/// </summary>
public static class AuthApi
{
    public const int MinPasswordLength = 10;
    private static int _failedLocal;
    private static DateTimeOffset _lockedUntil;

    /// <summary>Wird beim Start gesetzt: Entra ist nur aktiv, wenn es beim Start konfiguriert war (Änderung → Neustart).</summary>
    public static bool EntraConfiguredAtStartup { get; set; }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/auth/status", (HttpContext ctx, PortalAccess access, UserStore users, IOptionsMonitor<AuthOptions> auth) =>
        {
            var a = access.For(ctx);
            return Results.Ok(new
            {
                authenticated = a.Authenticated,
                user = a.Authenticated ? new { name = a.Name, login = a.Login, isAdmin = a.IsAdmin, isLocalAdmin = a.IsLocalAdmin, canSend = a.CanSend } : null,
                isLocal = PortalAccess.IsLocalRequest(ctx),
                needsSetup = !users.HasLocalAdminPassword,
                entraEnabled = auth.CurrentValue.Entra.IsConfigured && EntraConfiguredAtStartup
            });
        });

        app.MapPost("/api/auth/setup", async (PasswordRequest req, HttpContext ctx, UserStore users) =>
        {
            if (!PortalAccess.IsLocalRequest(ctx)) return Results.Json(new { error = "Das Admin-Kennwort kann nur direkt am Server festgelegt werden." }, statusCode: 403);
            if (users.HasLocalAdminPassword) return Results.Json(new { error = "Das Admin-Kennwort ist bereits festgelegt." }, statusCode: 409);
            var err = CheckPassword(req.Password);
            if (err is not null) return Results.BadRequest(new { error = err });
            users.SetLocalAdminPassword(req.Password!);
            await ctx.SignInAsync(PortalAccess.Scheme, PortalAccess.Principal("local", "Administrator (Server)"));
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/auth/local", async (PasswordRequest req, HttpContext ctx, UserStore users, ILoggerFactory lf) =>
        {
            if (!PortalAccess.IsLocalRequest(ctx)) return Results.Json(new { error = "Die Anmeldung mit dem Admin-Kennwort ist nur direkt am Server möglich." }, statusCode: 403);
            if (DateTimeOffset.Now < _lockedUntil)
                return Results.Json(new { error = $"Zu viele Fehlversuche – bitte bis {_lockedUntil:HH:mm:ss} warten." }, statusCode: 429);
            if (string.IsNullOrEmpty(req.Password) || !users.VerifyLocalAdminPassword(req.Password))
            {
                await Task.Delay(800);
                if (Interlocked.Increment(ref _failedLocal) % 5 == 0) _lockedUntil = DateTimeOffset.Now.AddMinutes(1);
                lf.CreateLogger("Anmeldung").LogWarning("Fehlgeschlagene Anmeldung des lokalen Administrators");
                return Results.Json(new { error = "Kennwort falsch." }, statusCode: 401);
            }
            Interlocked.Exchange(ref _failedLocal, 0);
            await ctx.SignInAsync(PortalAccess.Scheme, PortalAccess.Principal("local", "Administrator (Server)"));
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/auth/login", (string? returnUrl, HttpContext ctx) =>
        {
            if (!EntraConfiguredAtStartup) return Results.Redirect("/#/anmeldung?fehler=" + Uri.EscapeDataString("Die Anmeldung mit Microsoft ist nicht eingerichtet."));
            var target = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, new[] { PortalAccess.EntraScheme });
        });

        app.MapGet("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(PortalAccess.Scheme);
            return Results.Redirect("/#/anmeldung?abgemeldet=1");
        });
    }

    public static string? CheckPassword(string? pw) =>
        string.IsNullOrEmpty(pw) || pw.Length < MinPasswordLength ? $"Das Kennwort muss mindestens {MinPasswordLength} Zeichen lang sein." : null;

    /// <summary>Benutzerverwaltung (Gruppe /api/admin – nur Administratoren).</summary>
    public static void MapUsers(RouteGroupBuilder g)
    {
        g.MapGet("/users", (UserStore users) =>
        {
            var d = users.Snapshot();
            return Results.Ok(new { users = d.Users.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase), localAdminSet = users.HasLocalAdminPassword });
        });

        g.MapPut("/users", (SaveUsersRequest req, UserStore users, HttpContext ctx, PortalAccess access) =>
        {
            var errors = new List<string>();
            var list = req.Users ?? new();
            foreach (var u in list)
            {
                u.Name = (u.Name ?? "").Trim();
                u.Email = string.IsNullOrWhiteSpace(u.Email) ? null : u.Email.Trim();
                u.Logins = u.Logins.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (u.Name.Length == 0) errors.Add("Jeder Benutzer braucht einen Namen.");
                if (u.Email is null && u.Logins.Count == 0) errors.Add($"„{u.Name}“: bitte E-Mail-Adresse (Microsoft-Anmeldename) eintragen.");
            }
            var dupes = list.SelectMany(u => u.AllLogins()).GroupBy(l => l, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
            if (dupes.Count > 0) errors.Add("Anmeldename mehrfach vergeben: " + string.Join(", ", dupes));
            var me = access.For(ctx);
            if (me.UserId is not null && list.FirstOrDefault(u => u.Id == me.UserId) is { } self && (!self.IsAdmin || !self.Active))
                errors.Add("Du kannst dir das Administrator-Recht nicht selbst entziehen.");
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("\n", errors.Distinct()) });
            users.Replace(list);
            return Results.Ok(new { saved = true });
        });

        g.MapPost("/local-password", (PasswordRequest req, HttpContext ctx, PortalAccess access, UserStore users) =>
        {
            if (!access.For(ctx).IsLocalAdmin) return Results.Json(new { error = "Nur der lokale Administrator kann sein Kennwort ändern." }, statusCode: 403);
            if (string.IsNullOrEmpty(req.Current) || !users.VerifyLocalAdminPassword(req.Current)) return Results.BadRequest(new { error = "Das bisherige Kennwort ist falsch." });
            var err = CheckPassword(req.Password);
            if (err is not null) return Results.BadRequest(new { error = err });
            users.SetLocalAdminPassword(req.Password!);
            return Results.Ok(new { ok = true });
        });
    }
}
