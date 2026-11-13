using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using PdfRechnung;
using PdfRechnung.Documents;
using PdfRechnung.Services;
using PdfRechnung.Settings;

// ======================================================================
//  Belegpost – Druckdienst und Versand-Portal für Belege und E-Rechnungen
//  Start-Varianten (wie Firmenportal):
//   • als Windows-Dienst            → normaler Betrieb (alle Netzwerkkarten)
//   • per Doppelklick auf die EXE   → Einrichtung im Browser (nur localhost)
//   • --install / --uninstall       → Dienst einrichten/entfernen (von der Oberfläche mit UAC aufgerufen)
// ======================================================================

var isService = WindowsServiceHelpers.IsWindowsService();
var store = new SettingsStore(SettingsStore.DefaultDataDirectory());

if (CommandLine.Handles(args)) return CommandLine.Run(args, store);

if (int.TryParse(CommandLine.Value(args, "--wait-pid"), out var waitPid))
    ServiceManager.WaitForProcessExit(waitPid, TimeSpan.FromSeconds(30));

if (!isService && OperatingSystem.IsWindows() && ServiceManager.IsInstalled())
{
    var installed = ServiceManager.ServiceExecutable();
    if (installed is not null && !ServiceManager.SameFile(installed, ServiceManager.ExecutablePath))
    {
        Console.WriteLine($"Neue Programmversion – aktualisiere den Dienst ({installed}) …");
        return ServiceManager.RunElevated("--install --open-browser") ? 0 : 1223;
    }
}

if (!isService && OperatingSystem.IsWindows() && ServiceManager.IsInstalled() && ServiceManager.IsRunning())
{
    var port = RuntimeControl.ReadShortcutPort() ?? TryLoad(store)?.Server.HttpsPort ?? 5444;
    Console.WriteLine($"Der Dienst läuft bereits. Öffne https://localhost:{port}/");
    ServiceManager.OpenBrowser($"https://localhost:{port}/");
    return 0;
}

AppSettings settings;
try
{
    store.EnsureCreated();
    settings = store.Load();
}
catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && !isService)
{
    Console.WriteLine("Keine Berechtigung für die Einstellungen – starte mit Administratorrechten neu …");
    return ServiceManager.RunElevated(string.Join(' ', args)) ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : default
});
builder.Configuration.AddJsonFile(store.SettingsPath, optional: true, reloadOnChange: true);
builder.Host.UseWindowsService(o => o.ServiceName = ServiceManager.ServiceName);
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddSingleton(store);
var userStore = new UserStore(store.DataDirectory);
builder.Services.AddSingleton(userStore);
builder.Services.AddSingleton<PortalAccess>();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection("Portal"));
builder.Services.Configure<InboxOptions>(builder.Configuration.GetSection("Inbox"));
builder.Services.Configure<MailOptions>(builder.Configuration.GetSection("Mail"));
builder.Services.Configure<InvoiceOptions>(builder.Configuration.GetSection("Invoice"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<StationeryOptions>(builder.Configuration.GetSection("Stationery"));
builder.Services.Configure<MailTemplateOptions>(builder.Configuration.GetSection("MailTemplates"));
builder.Services.AddSingleton(sp => new RuntimeControl(sp.GetRequiredService<IHostApplicationLifetime>(), isService));

// ---- Vorgänge ----
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<DocumentProcessor>();
builder.Services.AddSingleton<SendService>();
builder.Services.AddHostedService<InboxWatcher>();
builder.Services.AddSingleton<AutoSendWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoSendWorker>());
builder.Services.AddHostedService<RetentionWorker>();

// ---- HTTPS: eigenes PFX oder automatisch erzeugtes, selbst signiertes Zertifikat ----
var certs = new CertificateProvider(store, machineKeys: isService && OperatingSystem.IsWindows());
using (var bootLog = LoggerFactory.Create(b => b.AddConsole()))
    certs.Load(settings.Server, bootLog.CreateLogger("Zertifikat"));
builder.Services.AddSingleton(certs);

var server = settings.Server;
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // PDF-Upload
    // Dienst: eingestellte Adressen (leer = alle). Einrichtung per Doppelklick: localhost – und zusätzlich
    // die ausdrücklich eingestellten Adressen, damit man das Portal auch so im Netz testen kann.
    var bindWarnings = new List<string>();
    var binds = NetworkBinding.Resolve(server.BindAddresses, out bindWarnings);
    if (bindWarnings.Count > 0)
        using (var bootLog = LoggerFactory.Create(b => b.AddConsole()))
            foreach (var w in bindWarnings) bootLog.CreateLogger("Netzwerk").LogWarning("{Warnung}", w);
    void Listen(int port, Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> cfg)
    {
        if (binds is null) { if (isService) k.ListenAnyIP(port, cfg); else k.ListenLocalhost(port, cfg); return; }
        foreach (var ip in binds) k.Listen(ip, port, cfg);
    }
    Listen(server.HttpsPort, o => o.UseHttps(certs.Current!));
    if (server.EnableHttp) Listen(server.HttpPort, _ => { });
});
if (server.EnableHttp && server.RedirectHttpToHttps)
    builder.Services.AddHttpsRedirection(o => o.HttpsPort = server.HttpsPort);

// ---- Anmeldung: Portal-Cookie; Benutzer über Microsoft Entra ID, lokaler Admin per Kennwort (nur am Server) ----
var entra = settings.Auth.Entra;
AuthApi.EntraConfiguredAtStartup = entra.IsConfigured;
var authBuilder = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = PortalAccess.Scheme;
    o.DefaultChallengeScheme = entra.IsConfigured ? PortalAccess.EntraScheme : PortalAccess.Scheme;
}).AddCookie(PortalAccess.Scheme, o =>
{
    o.Cookie.Name = "belegpost.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    // Eingebettet im Firmenportal (iframe, andere Herkunft) braucht der Browser SameSite=None
    o.Cookie.SameSite = settings.Auth.Firmenportal.Enabled ? SameSiteMode.None : SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromHours(10);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
if (entra.IsConfigured)
{
    authBuilder.AddOpenIdConnect(PortalAccess.EntraScheme, o =>
    {
        o.Authority = string.IsNullOrWhiteSpace(entra.Authority)
            ? $"https://login.microsoftonline.com/{entra.TenantId.Trim()}/v2.0" : entra.Authority.Trim();
        o.ClientId = entra.ClientId.Trim();
        o.ClientSecret = store.Secrets.Unprotect(entra.ClientSecret);
        o.RequireHttpsMetadata = !o.Authority.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
        o.ResponseType = "code";
        o.UsePkce = true;
        o.Scope.Clear();
        foreach (var sc in new[] { "openid", "profile", "email" }) o.Scope.Add(sc);
        o.CallbackPath = "/signin-oidc";
        o.SignInScheme = PortalAccess.Scheme;
        o.MapInboundClaims = false;
        o.SaveTokens = false;
        o.TokenValidationParameters.NameClaimType = "name";
        o.Events.OnTokenValidated = ctx =>
        {
            var p = ctx.Principal!;
            var names = new[] { "preferred_username", "email", "upn", "unique_name" }.Select(t => p.FindFirst(t)?.Value).ToList();
            var login = names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
            var user = users.FindByLogin(names);
            if (user is null || !user.Active)
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Anmeldung")
                    .LogWarning("Entra-Anmeldung ohne Freigabe: {Login}", login);
                ctx.Response.Redirect("/#/anmeldung?fehler=nicht-freigeschaltet&konto=" + Uri.EscapeDataString(login));
                ctx.HandleResponse();
                return Task.CompletedTask;
            }
            ctx.Principal = PortalAccess.Principal("entra", user.Name, user.Id, login);
            users.TouchLogin(user.Id);
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/#/anmeldung?fehler=" + Uri.EscapeDataString(ctx.Failure?.Message ?? "Anmeldung fehlgeschlagen"));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddAuthorization();

var app = builder.Build();
store.Saved += () => ((IConfigurationRoot)app.Configuration).Reload();

if (server.EnableHttp && server.RedirectHttpToHttps) app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Einbettung nur im eigenen Portal und – falls eingerichtet – im Firmenportal
app.Use(async (ctx, next) =>
{
    var fp = ctx.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue.Firmenportal;
    var origin = fp.Enabled ? HandoverLogin.Origin(fp.Url) : null;
    ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'" + (origin is null ? "" : " " + origin);
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

// Schnittstellen nur mit Anmeldung; verändernde Aufrufe nur mit eigenem Header (CSRF-Schutz bei Cookie-Anmeldung)
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/health") && !path.StartsWithSegments("/api/auth")
        && !path.StartsWithSegments("/api/portal"))
    {
        if (!ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx).Authenticated)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Bitte anmelden." });
            return;
        }
    }
    if (path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && !ctx.Request.Headers.ContainsKey(AdminApi.CsrfHeader))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Ungültige Anfrage." });
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------- API ----------------

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.Now }));

app.MapGet("/api/portal", (IOptionsSnapshot<PortalOptions> portal, HttpContext ctx, RuntimeControl rt, CertificateProvider c, PortalAccess access,
    IOptionsSnapshot<MailOptions> mail) =>
{
    var a = access.For(ctx);
    return Results.Ok(new
    {
        title = portal.Value.Title,
        authenticated = a.Authenticated,
        user = a.Authenticated ? a.Name : null,
        login = a.Login,
        isAdmin = a.IsAdmin,
        isLocalAdmin = a.IsLocalAdmin,
        canSend = a.CanSend,
        userId = a.UserId,
        setupMode = rt.SetupMode,
        mailEnabled = a.Authenticated && mail.Value.Enabled,
        autoSend = a.Authenticated && mail.Value.Enabled && mail.Value.AutoSend,
        certificateWarning = a.Authenticated ? c.Info?.Warning : null,
        version = typeof(Program).Assembly.GetName().Version?.ToString(3)
    });
});

VorgangApi.Map(app);
MelderApi.Map(app);
AuthApi.Map(app);
AdminApi.Map(app);

// ---------------- Start ----------------

if (isService && OperatingSystem.IsWindows())
{
    try { ServiceManager.UpdateFirewall(server, m => app.Logger.LogInformation("{Msg}", m)); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Firewall-Regel konnte nicht gesetzt werden"); }
    try { ServiceManager.EnsureDisplayName(m => app.Logger.LogInformation("{Msg}", m)); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Anzeigename des Dienstes konnte nicht gesetzt werden"); }
    RuntimeControl.WriteShortcut(server);
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"https://localhost:{server.HttpsPort}/";
    app.Logger.LogInformation("Belegpost gestartet ({Mode}) – {Url} – Zertifikat: {Cert}",
        isService ? "Dienst" : "Einrichtung", url, certs.Info?.Source);
    if (!isService && Environment.GetEnvironmentVariable("BELEGPOST_NO_BROWSER") is null && Environment.GetEnvironmentVariable("PDFRECHNUNG_NO_BROWSER") is null)
    {
        Console.WriteLine();
        Console.WriteLine("  Einrichtung läuft – im Browser: " + url + "#/einstellungen");
        Console.WriteLine("  (Hinweis: Beim selbst signierten Zertifikat einmalig die Browser-Warnung bestätigen.)");
        Console.WriteLine("  Dieses Fenster schließen, um die Einrichtung zu beenden.");
        Console.WriteLine();
        ServiceManager.OpenBrowser(url + "#/einstellungen");
    }
});

app.Run();
return Environment.ExitCode;

static AppSettings? TryLoad(SettingsStore s)
{
    try { return s.Load(); } catch { return null; }
}

public partial class Program;
