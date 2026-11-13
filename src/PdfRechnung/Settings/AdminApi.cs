using Microsoft.Extensions.Options;
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
                placeholders = Placeholders.BuiltIn.Select(x => "{" + x + "}").ToArray()
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
                    Subject = "Testmail Belegpost",
                    Body = $"Diese Testmail wurde am {DateTime.Now:dd.MM.yyyy HH:mm} vom Dienst Belegpost auf {Environment.MachineName} gesendet."
                }, ct);
                return Results.Ok(new { ok = true, message = $"Testmail an {req.To.Trim()} gesendet." });
            }
            catch (Exception ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        // ---------- Briefpapier ----------
        // PDF hochladen (wird erst mit „Speichern“ einem Briefpapier zugeordnet)
        g.MapPost("/stationery/file", async (HttpRequest request, SettingsStore store) =>
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is not { Length: > 0 }) return Results.BadRequest(new { error = "Bitte eine PDF-Datei auswählen." });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var data = ms.ToArray();
            int pages; double width, height;
            try
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(data);
                pages = pdf.NumberOfPages;
                var first = pdf.GetPage(1);
                width = first.Width / PdfComposer.MmToPt; height = first.Height / PdfComposer.MmToPt;
            }
            catch (Exception ex) { return Results.BadRequest(new { error = "Keine gültige PDF-Datei: " + ex.Message }); }
            var dir = PdfComposer.StationeryDirectory(store.DataDirectory);
            Directory.CreateDirectory(dir);
            var baseName = new string(Path.GetFileNameWithoutExtension(file.FileName).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            var name = $"{(baseName.Length == 0 ? "briefpapier" : baseName[..Math.Min(40, baseName.Length)])}_{Guid.NewGuid().ToString("N")[..6]}.pdf";
            await File.WriteAllBytesAsync(Path.Combine(dir, name), data);
            var hint = Math.Abs(width - 210) > 2 || Math.Abs(height - 297) > 2 ? $"Achtung: Das PDF ist {width:0} × {height:0} mm, nicht A4 – es wird auf die Seitengröße skaliert." : null;
            return Results.Ok(new { file = name, pages, width = Math.Round(width), height = Math.Round(height), hint });
        }).DisableAntiforgery();

        g.MapGet("/stationery/file/{name}", (string name, SettingsStore store) =>
        {
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) return Results.BadRequest();
            var path = Path.Combine(PdfComposer.StationeryDirectory(store.DataDirectory), name);
            return File.Exists(path) ? Results.File(path, "application/pdf") : Results.NotFound();
        });

        // Vorschau mit den (auch noch nicht gespeicherten) Werten aus der Oberfläche
        g.MapPost("/stationery/preview", (Stationery st, SettingsStore store) =>
            Results.File(PdfComposer.Preview(st, PdfComposer.StationeryDirectory(store.DataDirectory)), "application/pdf"));

        // ---------- Mail-Vorlagen ----------
        g.MapPost("/mailtemplates/logo", async (HttpRequest request, SettingsStore store) =>
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is not { Length: > 0 }) return Results.BadRequest(new { error = "Bitte eine Bilddatei auswählen." });
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif")) return Results.BadRequest(new { error = "Bitte PNG, JPG oder GIF verwenden (SVG zeigen viele Mailprogramme nicht an)." });
            if (file.Length > 1024 * 1024) return Results.BadRequest(new { error = "Das Logo ist größer als 1 MB – bitte verkleinern (es wird in jede Mail eingebettet)." });
            var dir = MailDesigner.Directory(store.DataDirectory);
            Directory.CreateDirectory(dir);
            var baseName = new string(Path.GetFileNameWithoutExtension(file.FileName).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            var name = $"{(baseName.Length == 0 ? "logo" : baseName[..Math.Min(40, baseName.Length)])}_{Guid.NewGuid().ToString("N")[..6]}{ext}";
            await using (var fs = File.Create(Path.Combine(dir, name))) await file.CopyToAsync(fs);
            return Results.Ok(new { file = name });
        }).DisableAntiforgery();

        g.MapGet("/mailtemplates/logo/{name}", (string name, SettingsStore store) =>
        {
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) return Results.BadRequest();
            var path = Path.Combine(MailDesigner.Directory(store.DataDirectory), name);
            return File.Exists(path) ? Results.File(path, MailDesigner.ImageType(path)) : Results.NotFound();
        });

        // Vorschau mit Beispieldaten (auch für noch nicht gespeicherte Vorlagen)
        g.MapPost("/mailtemplates/preview", (MailDesign d, SettingsStore store, IOptionsMonitor<MailOptions> mail) =>
        {
            var o = mail.CurrentValue;
            var sample = new PdfRechnung.Documents.Vorgang
            {
                DocumentType = "Rechnung", VoucherId = "RE-2026-0815", VoucherDate = DateTime.Today.ToString("dd.MM.yyyy"),
                BuyerName = "Muster GmbH", BuyerId = "10042", GrossAmount = 1234.56m, Currency = "EUR"
            };
            sample.Mail.FileName = "Rechnung RE-2026-0815";
            var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Anrede"] = "Sehr geehrte Frau Muster", ["Ansprechpartner"] = "Max Beispiel" };
            var values = Placeholders.Build(sample, custom, "Rechnung RE-2026-0815.pdf", string.IsNullOrWhiteSpace(d.FromName) ? o.FromName : d.FromName);
            var missing = new List<string>();
            var subject = Placeholders.Apply(string.IsNullOrWhiteSpace(d.Subject) ? o.DefaultSubject : d.Subject, values, missing);
            var body = Placeholders.Apply(string.IsNullOrWhiteSpace(d.Body) ? o.DefaultBody : d.Body, values, missing);
            foreach (var t in new[] { d.Signature, d.Footer, d.HeaderText, d.CustomHtml }) Placeholders.Apply(t, values, missing);
            var r = MailDesigner.Render(d, body, values, MailDesigner.Directory(store.DataDirectory), MailFacts.From(sample));
            return Results.Ok(new { subject, html = MailPreview.Inline(r), text = r.Text, missing = missing.Except(new[] { "Inhalt", "Logo", "Signatur", "Fusszeile", "Fußzeile", "Kopfzeile", "Belegbox", "Farbe" }, StringComparer.OrdinalIgnoreCase) });
        });

        // ---------- Zertifikat ----------
        g.MapGet("/certificate/download", (CertificateProvider certs) =>
            certs.Current is null ? Results.NotFound()
            : Results.File(certs.Current.RawData, "application/pkix-cert", $"Belegpost-{Environment.MachineName}.cer"));

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
        foreach (var st in s.Stationery.Items)
        {
            if (string.IsNullOrWhiteSpace(st.Name)) e.Add("Jedes Briefpapier braucht einen Namen (wie im Steuerzeichen @@bpName@@).");
            else if (st.Name != st.Name.Trim()) e.Add($"Briefpapier „{st.Name}“: Der Name darf nicht mit Leerzeichen beginnen oder enden.");
            if (string.IsNullOrWhiteSpace(st.FirstPageFile)) e.Add($"Briefpapier „{st.Name}“: Bitte ein PDF für die erste Seite hochladen.");
            if (st.FollowingMode == "file" && string.IsNullOrWhiteSpace(st.FollowingPageFile)) e.Add($"Briefpapier „{st.Name}“: Bitte ein PDF für die Folgeseiten hochladen.");
            if (st.FollowingMode is not ("same" or "file" or "none")) e.Add($"Briefpapier „{st.Name}“: Ungültige Einstellung für Folgeseiten.");
            if (new[] { st.MarginTop, st.MarginBottom, st.MarginLeft, st.MarginRight, st.FollowingMarginTop, st.FollowingMarginBottom }.Any(x => x < 0 || x > 150))
                e.Add($"Briefpapier „{st.Name}“: Ränder müssen zwischen 0 und 150 mm liegen.");
            if (st.Scale is < 10 or > 200) e.Add($"Briefpapier „{st.Name}“: Die Skalierung muss zwischen 10 und 200 % liegen.");
        }
        if (s.Stationery.Items.GroupBy(x => x.Name).Any(g => g.Count() > 1)) e.Add("Briefpapier-Namen müssen eindeutig sein.");
        foreach (var t in s.MailTemplates.Items)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) e.Add("Jede Mail-Vorlage braucht einen Namen (wie im Steuerzeichen @@ptName@@).");
            if (t.Kind is not ("simple" or "html")) e.Add($"Mail-Vorlage „{t.Name}“: Unbekannte Art.");
            if (t.Kind == "html" && !t.CustomHtml.Contains("{Inhalt}", StringComparison.OrdinalIgnoreCase)) e.Add($"Mail-Vorlage „{t.Name}“: Das eigene HTML muss den Platzhalter {{Inhalt}} enthalten.");
            foreach (var c in new[] { t.PrimaryColor, t.BackgroundColor, t.TextColor })
                if (!string.IsNullOrWhiteSpace(c) && !MailDesigner.IsColor(c)) e.Add($"Mail-Vorlage „{t.Name}“: Farbe „{c}“ bitte als #RRGGBB angeben.");
            foreach (var a in new[] { t.FromAddress, t.ReplyTo }.Concat(t.Senders))
                if (!string.IsNullOrWhiteSpace(a) && !DocumentChecker.IsMail(a.Trim())) e.Add($"Mail-Vorlage „{t.Name}“: Ungültige Adresse „{a}“.");
            foreach (var st in t.Stationeries)
                if (!string.IsNullOrWhiteSpace(st) && s.Stationery.Items.All(x => !string.Equals(x.Name, st.Trim(), StringComparison.OrdinalIgnoreCase)))
                    e.Add($"Mail-Vorlage „{t.Name}“: Briefpapier „{st}“ gibt es nicht.");
        }
        if (s.MailTemplates.Items.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) e.Add("Namen der Mail-Vorlagen müssen eindeutig sein.");
        if (!string.IsNullOrEmpty(s.MailTemplates.Default) && s.MailTemplates.Items.All(x => x.Name != s.MailTemplates.Default))
            e.Add($"Die Standard-Mailvorlage „{s.MailTemplates.Default}“ gibt es nicht.");
        if (!string.IsNullOrEmpty(s.Stationery.Default) && s.Stationery.Items.All(x => x.Name != s.Stationery.Default))
            e.Add($"Das Standard-Briefpapier „{s.Stationery.Default}“ gibt es nicht.");
        return e.Distinct().ToList();
    }
}
