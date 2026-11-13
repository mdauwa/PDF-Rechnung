using System.Text.Json;
using PdfRechnung.Documents;

namespace PdfRechnung.Settings;

public sealed class SaveSettingsRequest
{
    public AppSettings Settings { get; set; } = new();
    /// <summary>Neuer Entra-Clientschlüssel; leer = unverändert.</summary>
    public string? EntraSecret { get; set; }
    public bool ClearEntraSecret { get; set; }
    /// <summary>Neues SMTP-Kennwort; leer = unverändert.</summary>
    public string? MailPassword { get; set; }
    public bool ClearMailPassword { get; set; }
    /// <summary>Neuer Clientschlüssel für Microsoft Graph (Mailversand); leer = unverändert.</summary>
    public string? GraphSecret { get; set; }
    public bool ClearGraphSecret { get; set; }
}

public sealed record MailTestRequest(MailOptions? Mail, string? Password, string? To, string? GraphSecret = null);

/// <summary>Einstellungs-API. Zugriff: lokaler Administrator (Kennwort, direkt am Server) oder Portal-Administrator.</summary>
public static class AdminApi
{
    public const string CsrfHeader = "X-Requested-With";

    public static bool IsAdmin(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<PdfRechnung.Services.PortalAccess>().For(ctx).IsAdmin;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!IsAdmin(http))
                return Results.Json(new { error = "Keine Berechtigung. Einstellungen sind nur für Administratoren erreichbar." }, statusCode: 403);
            if (!HttpMethods.IsGet(http.Request.Method) && !http.Request.Headers.ContainsKey(CsrfHeader))
                return Results.Json(new { error = "Ungültige Anfrage." }, statusCode: 400);
            return await next(ctx);
        });

        AuthApi.MapUsers(g);

        // ---------- Einstellungen ----------
        g.MapGet("/settings", (SettingsStore store, CertificateProvider certs, RuntimeControl rt, DocumentStore docs) =>
        {
            var s = store.Load();
            var hasEntraSecret = !string.IsNullOrEmpty(s.Auth.Entra.ClientSecret);
            var hasMailPassword = !string.IsNullOrEmpty(s.Mail.Password);
            var hasGraphSecret = !string.IsNullOrEmpty(s.Mail.GraphClientSecret);
            s.Mail.GraphClientSecret = null;
            s.Server.CertificatePassword = null;
            s.Auth.Entra.ClientSecret = null;
            s.Mail.Password = null;
            return Results.Ok(new
            {
                settings = s,
                hasEntraSecret,
                hasMailPassword,
                hasGraphSecret,
                hasEntraLogin = !string.IsNullOrWhiteSpace(s.Auth.Entra.TenantId) && !string.IsNullOrWhiteSpace(s.Auth.Entra.ClientId),
                entraRedirectUris = CertificateProvider.HostNames(s.Server).Select(h => $"https://{h}:{s.Server.HttpsPort}/signin-oidc"),
                certificate = certs.Info,
                hostNames = CertificateProvider.HostNames(s.Server),
                dataDirectory = store.DataDirectory,
                inboxFolder = InboxWatcher.ResolveFolder(s.Inbox, store),
                documentsFolder = docs.Root,
                setupMode = rt.SetupMode,
                placeholders = MailTemplate.Placeholders
            });
        });

        g.MapPut("/settings", (SaveSettingsRequest req, SettingsStore store) =>
        {
            var old = store.Load();
            var s = req.Settings;
            var errors = Validate(s);
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("\n", errors) });

            s.Auth.Entra.ClientSecret = req.ClearEntraSecret ? null
                : !string.IsNullOrEmpty(req.EntraSecret) ? store.Secrets.Protect(req.EntraSecret.Trim())
                : old.Auth.Entra.ClientSecret;
            s.Mail.Password = req.ClearMailPassword ? null
                : !string.IsNullOrEmpty(req.MailPassword) ? store.Secrets.Protect(req.MailPassword)
                : old.Mail.Password;
            s.Mail.GraphClientSecret = req.ClearGraphSecret ? null
                : !string.IsNullOrEmpty(req.GraphSecret) ? store.Secrets.Protect(req.GraphSecret.Trim())
                : old.Mail.GraphClientSecret;
            s.Server.CertificatePath = old.Server.CertificatePath;         // nur über Zertifikat-Endpunkte änderbar
            s.Server.CertificatePassword = old.Server.CertificatePassword;
            s.Invoice.Units = new Dictionary<string, string>(
                s.Invoice.Units.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim().ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);

            store.Save(s);
            var restart = Json(old.Server) != Json(s.Server) || Json(old.Auth) != Json(s.Auth);
            return Results.Ok(new { saved = true, restartRequired = restart });
        });

        // ---------- E-Mail testen ----------
        g.MapPost("/mail-test", async (MailTestRequest req, SettingsStore store, CancellationToken ct) =>
        {
            var saved = store.Load().Mail;
            var o = req.Mail ?? saved;
            var pw = !string.IsNullOrEmpty(req.Password) ? req.Password : store.Secrets.Unprotect(saved.Password);
            var graph = !string.IsNullOrEmpty(req.GraphSecret) ? req.GraphSecret.Trim() : store.Secrets.Unprotect(saved.GraphClientSecret);
            if (string.IsNullOrWhiteSpace(req.To)) return Results.BadRequest(new { error = "Bitte eine Empfängeradresse für die Testmail eintragen." });
            try
            {
                await MailSender.SendAsync(o, new MailSecrets(pw, graph), new OutgoingMail
                {
                    To = { req.To.Trim() },
                    Subject = "Testmail PDF Rechnung",
                    Body = $"Diese Testmail wurde am {DateTime.Now:dd.MM.yyyy HH:mm} vom Dienst PDF Rechnung auf {Environment.MachineName} gesendet."
                }, ct);
                return Results.Ok(new { ok = true, message = $"Testmail an {req.To.Trim()} gesendet." });
            }
            catch (Exception ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        // ---------- Zertifikat ----------
        g.MapGet("/certificate/download", (CertificateProvider certs) =>
            certs.Current is null ? Results.NotFound()
            : Results.File(certs.Current.RawData, "application/pkix-cert", $"PdfRechnung-{Environment.MachineName}.cer"));

        g.MapPost("/certificate", async (HttpRequest request, SettingsStore store, CertificateProvider certs) =>
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var password = form["password"].ToString();
            var path = form["path"].ToString().Trim();
            byte[] data;
            if (file is { Length: > 0 })
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                data = ms.ToArray();
            }
            else if (!string.IsNullOrEmpty(path))
            {
                if (!File.Exists(path)) return Results.BadRequest(new { error = $"Datei nicht gefunden: {path}" });
                data = await File.ReadAllBytesAsync(path);
            }
            else return Results.BadRequest(new { error = "Bitte eine PFX-Datei auswählen oder einen Pfad angeben." });

            CertificateInfo info;
            try { info = certs.Inspect(data, string.IsNullOrEmpty(password) ? null : password); }
            catch (Exception ex) { return Results.BadRequest(new { error = "Zertifikat ungültig oder Passwort falsch: " + ex.Message }); }

            var s = store.Load();
            if (file is { Length: > 0 })
            {
                Directory.CreateDirectory(store.CertificateDirectory);
                await File.WriteAllBytesAsync(Path.Combine(store.CertificateDirectory, "server.pfx"), data);
                s.Server.CertificatePath = "server.pfx";
            }
            else s.Server.CertificatePath = path;
            s.Server.CertificatePassword = store.Secrets.Protect(string.IsNullOrEmpty(password) ? null : password);
            store.Save(s);
            return Results.Ok(new { info, restartRequired = true });
        });

        g.MapPost("/certificate/selfsigned", (SettingsStore store, CertificateProvider certs) =>
        {
            var s = store.Load();
            s.Server.CertificatePath = "";
            s.Server.CertificatePassword = null;
            store.Save(s);
            certs.DeleteSelfSigned();
            return Results.Ok(new { restartRequired = true });
        });

        // ---------- Dienst ----------
        g.MapGet("/service", (SettingsStore store, RuntimeControl rt) =>
        {
            var log = Path.Combine(store.DataDirectory, "install.log");
            return Results.Ok(new
            {
                status = ServiceManager.GetStatus(rt.IsService, store.DataDirectory),
                setupMode = rt.SetupMode,
                installLog = File.Exists(log) ? File.ReadAllText(log) : null
            });
        });

        g.MapPost("/service/restart", (RuntimeControl rt) =>
        {
            rt.Restart();
            return Results.Ok(new { message = "Neustart läuft …" });
        });

        g.MapPost("/service/install", (RuntimeControl rt) =>
        {
            if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "Nur unter Windows möglich." });
            if (rt.IsService) return Results.BadRequest(new { error = "Läuft bereits als Dienst." });
            var args = $"--install --wait-pid {Environment.ProcessId} --open-browser";
            var started = ServiceManager.IsElevated() ? StartPlain(args) : ServiceManager.RunElevated(args);
            if (!started) return Results.BadRequest(new { error = "Die Administrator-Abfrage wurde abgebrochen." });
            rt.StopSoon();
            return Results.Ok(new { message = "Dienst wird installiert. Der Browser öffnet sich danach automatisch neu." });
        });

        g.MapPost("/service/uninstall", (RuntimeControl rt) =>
        {
            if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "Nur unter Windows möglich." });
            if (rt.IsService)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800);
                    if (OperatingSystem.IsWindows()) ServiceManager.Uninstall(_ => { });
                });
                return Results.Ok(new { message = "Dienst wird entfernt und beendet." });
            }
            if (ServiceManager.IsElevated()) { ServiceManager.Uninstall(_ => { }); return Results.Ok(new { message = "Dienst entfernt." }); }
            return ServiceManager.RunElevated("--uninstall")
                ? Results.Ok(new { message = "Dienst wird entfernt (Administrator-Abfrage bestätigt)." })
                : Results.BadRequest(new { error = "Die Administrator-Abfrage wurde abgebrochen." });
        });
    }

    private static bool StartPlain(string args)
    {
        RuntimeControl.StartSelf(args);
        return true;
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static List<string> Validate(AppSettings s)
    {
        var e = new List<string>();
        bool PortOk(int p) => p is > 0 and <= 65535;
        if (!PortOk(s.Server.HttpsPort)) e.Add("HTTPS-Port ist ungültig.");
        if (s.Server.EnableHttp && !PortOk(s.Server.HttpPort)) e.Add("HTTP-Port ist ungültig.");
        if (s.Server.EnableHttp && s.Server.HttpPort == s.Server.HttpsPort) e.Add("HTTP- und HTTPS-Port müssen verschieden sein.");
        if (!MailSender.Transports.Contains(s.Mail.Transport)) e.Add("Unbekannte Versandart.");
        if (s.Mail.Enabled)
        {
            if (string.IsNullOrWhiteSpace(s.Mail.FromAddress)) e.Add("Bitte die Absenderadresse eintragen.");
            if (s.Mail.Transport == "Graph")
            {
                if (string.IsNullOrWhiteSpace(s.Mail.GraphTenantId)) e.Add("Bitte die Mandanten-ID für Microsoft 365 eintragen.");
                if (string.IsNullOrWhiteSpace(s.Mail.GraphClientId)) e.Add("Bitte die Client-ID für Microsoft 365 eintragen.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(s.Mail.Host)) e.Add("Bitte den SMTP-Server eintragen.");
                if (!PortOk(s.Mail.Port)) e.Add("SMTP-Port ist ungültig.");
            }
        }
        if (!MailSender.SecurityModes.Contains(s.Mail.Security)) e.Add("Unbekannte Verschlüsselung für SMTP.");
        if (s.Mail.AutoSendDelayMinutes < 0) e.Add("Die Wartezeit für den Autoversand darf nicht negativ sein.");
        if (s.Storage.RetentionDays < 0) e.Add("Die Aufbewahrungsdauer darf nicht negativ sein.");
        if (s.Invoice.SumTolerance < 0) e.Add("Die Toleranz der Summenprüfung darf nicht negativ sein.");
        return e.Distinct().ToList();
    }
}
