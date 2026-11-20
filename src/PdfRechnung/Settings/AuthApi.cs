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
    /// <summary>Windows-Anmeldung (Negotiate) beim Start eingerichtet (Anmeldeart „windows“, Änderung → Neustart).</summary>
    public static bool WindowsConfiguredAtStartup { get; set; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTimeOffset Until)> _adFails = new(StringComparer.OrdinalIgnoreCase);

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
                entraEnabled = auth.CurrentValue.Mode == "microsoft" && auth.CurrentValue.Entra.IsConfigured && EntraConfiguredAtStartup,
                mode = auth.CurrentValue.Mode,
                windowsEnabled = auth.CurrentValue.Mode == "windows" && WindowsConfiguredAtStartup,
                adEnabled = auth.CurrentValue.Mode is "windows" or "ad"
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

        // Windows-Anmeldung: der Browser meldet das angemeldete Windows-Konto (Kerberos/NTLM). Administratoren nie automatisch.
        app.MapGet("/auth/windows", async (string? ziel, string? wdh, HttpContext ctx, UserStore users, IOptionsMonitor<AuthOptions> auth, ILoggerFactory lf) =>
        {
            var target = !string.IsNullOrEmpty(ziel) && ziel.StartsWith('/') && !ziel.StartsWith("//") ? ziel : "/";
            if (!WindowsConfiguredAtStartup) return Results.Redirect("/#/anmeldung?manuell=1&fehler=" + Uri.EscapeDataString("Die Windows-Anmeldung ist nicht eingerichtet."));
            var res = await ctx.AuthenticateAsync(PortalAccess.WindowsScheme);
            if (!res.Succeeded || res.Principal?.Identity?.Name is not { Length: > 0 } account)
            {
                // Der Browser hat Anmeldedaten geschickt, der Abschluss klappte aber nicht (NTLM über eine neue Verbindung,
                // z. B. Firefox nach der Kennwortabfrage): einmal still neu versuchen – der Browser sendet die Daten erneut.
                if (string.IsNullOrEmpty(wdh) && ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } h
                    && (h.StartsWith("Negotiate", StringComparison.OrdinalIgnoreCase) || h.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase)))
                    return Results.Redirect("/auth/windows?wdh=1&ziel=" + Uri.EscapeDataString(target));
                // Browser soll das Windows-Konto senden; klappt das nicht (kein Intranet/keine Domäne), sieht der Benutzer diese Seite
                await ctx.ChallengeAsync(PortalAccess.WindowsScheme);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync("<!doctype html><meta charset=utf-8><title>Anmeldung</title><body style=\"font-family:Segoe UI,sans-serif;margin:40px\">"
                    + "<h2>Windows-Anmeldung nicht möglich</h2><p>Der Browser hat kein Windows-Konto übermittelt.</p>"
                    + "<p><a href=\"/#/anmeldung?manuell=1\">Mit Benutzername und Kennwort anmelden</a></p>"
                    + (ctx.Request.Headers.UserAgent.ToString().Contains("Firefox", StringComparison.OrdinalIgnoreCase)
                        ? "<p style=\"color:#555\">Firefox sendet das Windows-Konto nur an freigegebene Adressen. Der Administrator trägt <b>"
                          + System.Net.WebUtility.HtmlEncode(ctx.Request.Host.Host) + "</b> per Richtlinie (Authentication → SPNEGO/NTLM, AllowNonFQDN) "
                          + "oder unter about:config in <code>network.negotiate-auth.trusted-uris</code> ein.</p>" : "")
                    + "</body>");
                return Results.Empty;
            }
            var log = lf.CreateLogger("Anmeldung");
            var user = users.FindByPrintName(account) ?? AutoCreate(account, users, auth.CurrentValue, log);
            if (user is null || !user.Active)
            {
                log.LogWarning("Windows-Anmeldung ohne Freigabe: {Login}", account);
                return Results.Redirect("/#/anmeldung?manuell=1&fehler=nicht-freigeschaltet&konto=" + Uri.EscapeDataString(account));
            }
            if (user.IsAdmin)
                return Results.Redirect("/#/anmeldung?manuell=1&admin=1&konto=" + Uri.EscapeDataString(account));
            await ctx.SignInAsync(PortalAccess.Scheme, PortalAccess.Principal("windows", user.Name, user.Id, account));
            users.TouchLogin(user.Id);
            log.LogInformation("Windows-Anmeldung: {Login} → {User}", account, user.Name);
            return Results.Redirect(target);
        });

        // Anmeldung mit Windows-Benutzername und Kennwort, geprüft am Active Directory (auch für Administratoren)
        app.MapPost("/api/auth/ad", async (AdLoginRequest req, HttpContext ctx, UserStore users, IOptionsMonitor<AuthOptions> auth, ILoggerFactory lf) =>
        {
            var o = auth.CurrentValue;
            if (o.Mode is not ("windows" or "ad")) return Results.Json(new { error = "Die Anmeldung am Active Directory ist nicht eingeschaltet." }, statusCode: 403);
            var name = (req.User ?? "").Trim();
            if (name.Length == 0 || string.IsNullOrEmpty(req.Password)) return Results.BadRequest(new { error = "Bitte Benutzername und Kennwort eingeben." });
            var key = AdLogin.Split(name, o.Ad.Domain).Account;
            if (_adFails.TryGetValue(key, out var f) && f.Until > DateTimeOffset.Now)
                return Results.Json(new { error = $"Zu viele Fehlversuche – bitte bis {f.Until:HH:mm:ss} warten." }, statusCode: 429);
            var log = lf.CreateLogger("Anmeldung");
            bool ok; string account;
            try { ok = AdLogin.Validate(name, req.Password!, o.Ad.Domain, out account); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "AD-Anmeldung für {Login} nicht prüfbar", key);
                return Results.Json(new { error = "Das Active Directory ist nicht erreichbar: " + ex.Message }, statusCode: 503);
            }
            if (!ok)
            {
                await Task.Delay(800);
                var n = (_adFails.TryGetValue(key, out var old) && old.Until > DateTimeOffset.Now.AddMinutes(-10) ? old.Count : 0) + 1;
                _adFails[key] = (n, n % 5 == 0 ? DateTimeOffset.Now.AddMinutes(1) : DateTimeOffset.Now);
                log.LogWarning("Fehlgeschlagene AD-Anmeldung: {Login}", key);
                return Results.Json(new { error = "Benutzername oder Kennwort falsch." }, statusCode: 401);
            }
            _adFails.TryRemove(key, out _);
            var user = users.FindByPrintName(account) ?? AutoCreate(account, users, o, log);
            if (user is null || !user.Active)
            {
                log.LogWarning("AD-Anmeldung ohne Freigabe: {Login}", account);
                return Results.Json(new { error = $"Das Konto {account} ist im Portal nicht freigeschaltet. Bitte an einen Administrator wenden." }, statusCode: 403);
            }
            await ctx.SignInAsync(PortalAccess.Scheme, PortalAccess.Principal("ad", user.Name, user.Id, account));
            users.TouchLogin(user.Id);
            return Results.Ok(new { ok = true });
        });

        // Anmeldung aus dem Firmenportal übernehmen (signiertes Übergabe-Token)
        app.MapGet("/auth/firmenportal", async (string? token, string? ziel, HttpContext ctx, UserStore users, SettingsStore store,
            Microsoft.Extensions.Options.IOptionsMonitor<AuthOptions> auth, ILoggerFactory lf) =>
        {
            var fp = auth.CurrentValue.Firmenportal;
            var target = !string.IsNullOrEmpty(ziel) && ziel.StartsWith('/') && !ziel.StartsWith("//") ? ziel : "/";
            string Err(string e) => "/#/anmeldung?fehler=" + Uri.EscapeDataString(e);
            var secret = store.Secrets.Unprotect(fp.Secret);
            if (!fp.Enabled || string.IsNullOrEmpty(secret)) return Results.Redirect(Err("Die Anmeldung über das Firmenportal ist nicht eingerichtet."));
            var r = HandoverLogin.Validate(token, secret);
            var log = lf.CreateLogger("Anmeldung");
            if (!r.Ok) { log.LogWarning("Firmenportal-Übergabe abgelehnt: {Fehler}", r.Error); return Results.Redirect(Err(r.Error!)); }
            var user = users.FindByLogin(r.Names);
            if (user is null && fp.CreateUsers && r.Names.FirstOrDefault(n => n.Contains('@')) is { } mail)
            {
                user = new PortalUser { Id = Guid.NewGuid().ToString("N")[..12], Name = r.DisplayName ?? mail, Email = mail, CanSend = true, Visibility = "eigene" };
                var created = user;
                users.Update(d => d.Users.Add(created));
                log.LogInformation("Benutzer {Mail} beim ersten Aufruf aus dem Firmenportal angelegt", mail);
            }
            if (user is null || !user.Active)
            {
                log.LogWarning("Firmenportal-Übergabe ohne Freigabe: {Login}", r.Names[0]);
                return Results.Redirect("/#/anmeldung?fehler=nicht-freigeschaltet&konto=" + Uri.EscapeDataString(r.Names[0]));
            }
            await ctx.SignInAsync(PortalAccess.Scheme, PortalAccess.Principal("firmenportal", user.Name, user.Id, r.Names[0]));
            users.TouchLogin(user.Id);
            return Results.Redirect(target);
        });

        app.MapGet("/auth/login", (string? returnUrl, HttpContext ctx) =>
        {
            if (!EntraConfiguredAtStartup) return Results.Redirect("/#/anmeldung?fehler=" + Uri.EscapeDataString("Die Anmeldung mit Microsoft ist nicht eingerichtet."));
            var target = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, new[] { PortalAccess.EntraScheme });
        });

        app.MapGet("/auth/logout", async (string? admin, HttpContext ctx) =>
        {
            await ctx.SignOutAsync(PortalAccess.Scheme);
            // „Als Administrator anmelden“: abmelden und die Anmeldung mit Kennwort zeigen (keine automatische Windows-Anmeldung)
            return Results.Redirect(admin == "1" ? "/#/anmeldung?manuell=1&admin=1" : "/#/anmeldung?abgemeldet=1");
        });
    }

    public sealed record AdLoginRequest(string? User, string? Password);

    /// <summary>Unbekanntes Windows-Konto anlegen, wenn eingeschaltet – Daten aus dem AD, sonst aus der lokalen Benutzerverwaltung.</summary>
    private static PortalUser? AutoCreate(string account, UserStore users, AuthOptions o, ILogger log)
    {
        if (!o.CreateUsers) return null;
        var info = UserDirectory.Lookup(account, o.Ad.Domain);
        var (u, created) = users.Provision(account, info.Name, info.Email, new[] { info.Upn });
        log.LogInformation(created ? "Benutzer {Name} ({Account}) bei der ersten Anmeldung angelegt – Daten aus {Source}"
            : "Windows-Konto {Account} dem vorhandenen Benutzer {Name} zugeordnet ({Source})", u.Name, account, info.Source);
        return u;
    }

    /// <summary>Für die Microsoft-Anmeldung: unbekannten Benutzer aus den Angaben der Anmeldung anlegen.</summary>
    public static PortalUser? AutoCreateEntra(string? name, IEnumerable<string?> logins, UserStore users, AuthOptions o, ILogger log)
    {
        if (!o.CreateUsers) return null;
        var list = logins.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).ToList();
        var mail = list.FirstOrDefault(l => l.Contains('@'));
        if (mail is null) return null;
        var (u, created) = users.Provision(null, name ?? mail, mail, list);
        if (created) log.LogInformation("Benutzer {Name} ({Mail}) bei der ersten Microsoft-Anmeldung angelegt", u.Name, mail);
        return u;
    }

    public static string? CheckPassword(string? pw) =>
        string.IsNullOrEmpty(pw) || pw.Length < MinPasswordLength ? $"Das Kennwort muss mindestens {MinPasswordLength} Zeichen lang sein." : null;

    /// <summary>Benutzerverwaltung (Gruppe /api/admin – nur Administratoren).</summary>
    public static void MapUsers(RouteGroupBuilder g)
    {
        g.MapGet("/users", (UserStore users) =>
        {
            var d = users.Snapshot();
            return Results.Ok(new
            {
                users = d.Users.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase),
                localAdminSet = users.HasLocalAdminPassword,
                melder = d.Devices.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.Select(y => new { y.Name, y.LastSeen }).ToList())
            });
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
                u.PrintNames = (u.PrintNames ?? new()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var nos = (u.OperatorNumbers ?? new()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
                foreach (var n in nos.Where(n => UserStore.NormalizeOperator(n) is null))
                    errors.Add($"„{u.Name}“: ERP-Bediener-Nr. „{n}“ – bitte 3 Ziffern, z. B. 007.");
                u.OperatorNumbers = nos.Select(n => UserStore.NormalizeOperator(n) ?? n).Distinct().ToList();
                u.Mandanten = (u.Mandanten ?? new()).Select(MandantOptions.Normalize).Where(m => m is not null).Select(m => m!)
                    .DistinctBy(MandantOptions.Key, StringComparer.OrdinalIgnoreCase).ToList();
                u.Visibility = u.Visibility is "auswahl" or "alle" ? u.Visibility : "eigene";
                u.OutlookTarget = u.OutlookTarget is "melder" or "sitzung" ? u.OutlookTarget : "";
                u.MailClient = MailClients.All.Contains(u.MailClient) ? u.MailClient : "";
                u.AlsoSees = (u.AlsoSees ?? new()).Where(id => id != u.Id && list.Any(o => o.Id == id)).Distinct().ToList();
                u.SendFromAddress = (u.SendFromAddress ?? "").Trim(); u.SendFromName = (u.SendFromName ?? "").Trim(); u.ReplyTo = (u.ReplyTo ?? "").Trim();
                if (u.SendFromAddress.Length > 0 && !Documents.DocumentChecker.IsMail(u.SendFromAddress)) errors.Add($"„{u.Name}“: Absenderadresse „{u.SendFromAddress}“ ist keine gültige E-Mail-Adresse.");
                if (u.ReplyTo.Length > 0 && !Documents.DocumentChecker.IsMail(u.ReplyTo)) errors.Add($"„{u.Name}“: Antwortadresse „{u.ReplyTo}“ ist keine gültige E-Mail-Adresse.");
                // je ERP-Nummer: nur Nummern des Benutzers, leere Einträge weg
                u.OperatorSenders = (u.OperatorSenders ?? new())
                    .Select(x => new OperatorSender { Operator = UserStore.NormalizeOperator(x.Operator) ?? "", FromAddress = (x.FromAddress ?? "").Trim(), FromName = (x.FromName ?? "").Trim(), ReplyTo = (x.ReplyTo ?? "").Trim() })
                    .Where(x => x.Operator.Length > 0 && u.OperatorNumbers.Contains(x.Operator) && (x.FromAddress + x.FromName + x.ReplyTo).Length > 0)
                    .GroupBy(x => x.Operator).Select(g => g.First()).ToList();
                foreach (var x in u.OperatorSenders)
                {
                    if (x.FromAddress.Length > 0 && !Documents.DocumentChecker.IsMail(x.FromAddress)) errors.Add($"„{u.Name}“, ERP-Nr. {x.Operator}: Absenderadresse „{x.FromAddress}“ ist ungültig.");
                    if (x.ReplyTo.Length > 0 && !Documents.DocumentChecker.IsMail(x.ReplyTo)) errors.Add($"„{u.Name}“, ERP-Nr. {x.Operator}: Antwortadresse „{x.ReplyTo}“ ist ungültig.");
                }
            }
            var dupNo = list.SelectMany(u => u.OperatorNumbers.Select(n => (No: UserStore.NormalizeOperator(n), u.Name))).Where(x => x.No is not null)
                .GroupBy(x => x.No).Where(x => x.Select(y => y.Name).Distinct().Count() > 1)
                .Select(x => $"{x.Key} ({string.Join(", ", x.Select(y => y.Name).Distinct())})").ToList();
            if (dupNo.Count > 0) errors.Add("Bedienernummer mehrfach vergeben: " + string.Join("; ", dupNo));
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
