using Microsoft.Extensions.Options;
using PdfRechnung.Exchange;
using PdfRechnung.Monitoring;
using PdfRechnung.Services;

namespace PdfRechnung.Api;

/// <summary>
/// REST-API v1 für Überwachung und Konnektoren. Anmeldung mit API-Schlüssel (Header „X-Api-Key“ oder „Authorization: Bearer …“);
/// ein angemeldeter Portal-Administrator darf den Status ebenfalls abfragen.
/// </summary>
public static class ApiV1
{
    public sealed record AckRequest(bool Ok, string? Message, int NotImported = 0);
    public sealed record BeatRequest(string? Version, string? Machine, string? Info);

    private static ApiKey? Key(HttpContext ctx) => ctx.RequestServices.GetRequiredService<ApiKeyStore>().Authenticate(ctx);

    private static IResult Unauthorized() => Results.Json(new { error = "API-Schlüssel fehlt oder ist ungültig." }, statusCode: 401);
    private static IResult Forbidden(string what) => Results.Json(new { error = $"Der API-Schlüssel ist für „{what}“ nicht freigeschaltet." }, statusCode: 403);

    public static void Map(WebApplication app)
    {
        // ---------- Lebendcheck ohne Anmeldung ----------
        app.MapGet("/api/v1/ping", () => Results.Ok(new { ok = true, zeit = DateTimeOffset.Now }));

        // ---------- Status ----------
        var st = app.MapGroup("/api/v1/status").AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var a = http.RequestServices.GetRequiredService<PortalAccess>().For(http);
            if (a.Authenticated && a.IsAdmin) return await next(ctx);
            var k = Key(http);
            if (k is null) return Unauthorized();
            return k.Has(ApiKey.ScopeStatus) ? await next(ctx) : Forbidden("Status");
        });

        // Gesamtstatus: HTTP 200 bei ok/warnung, 503 bei fehler (für Monitoring-Systeme); ?immer200=1 liefert immer 200.
        st.MapGet("", (HealthService health, string? immer200, string? details) =>
        {
            var r = health.Report();
            var body = Body(r.Status, r, details is "0" or "false" ? null : r.Checks);
            return Results.Json(body, statusCode: r.Status == HealthLevel.Error && immer200 is not ("1" or "true") ? 503 : 200);
        });

        // Selektiv: Versandweg (Schlüssel/Name), Kommunikationspartner, Standort-Kürzel oder Bereich (versandwege, ausgang, vorgaenge, eingang, connect, filialverbund)
        st.MapGet("/{was}", (string was, HealthService health, string? immer200) =>
        {
            var r = health.Report();
            var checks = r.Checks.Where(c => string.Equals(c.Area, was, StringComparison.OrdinalIgnoreCase)).ToList();
            if (checks.Count == 0) checks = health.For(r, was);
            if (checks.Count == 0) return Results.Json(new { status = "unbekannt", error = $"„{was}“ nicht gefunden.", bekannt = Known(r) }, statusCode: 404);
            var status = HealthLevel.Worst(checks.Select(c => c.Status));
            return Results.Json(Body(status, r, checks), statusCode: status == HealthLevel.Error && immer200 is not ("1" or "true") ? 503 : 200);
        });

        // Liste der abfragbaren Versandwege/Standorte
        app.MapGet("/api/v1/status-liste", (HttpContext http, HealthService health) =>
        {
            var a = http.RequestServices.GetRequiredService<PortalAccess>().For(http);
            if (!(a.Authenticated && a.IsAdmin) && Key(http) is not { } k) return Unauthorized();
            return Results.Ok(Known(health.Report()));
        });

        // ---------- Datenaustausch (Konnektoren, Filial-Client) ----------
        var ex = app.MapGroup("/api/v1/austausch").AddEndpointFilter(async (ctx, next) =>
        {
            var k = Key(ctx.HttpContext);
            if (k is null) return Unauthorized();
            if (!k.Has(ApiKey.ScopeExchange)) return Forbidden("Datenaustausch");
            var o = ctx.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<FilialverbundOptions>>().CurrentValue;
            var s = o.Find(k.Standort);
            if (!o.Enabled) return Results.Json(new { error = "Der Filialverbund ist nicht eingeschaltet." }, statusCode: 409);
            if (s is null || !s.Active) return Results.Json(new { error = $"Standort „{k.Standort}“ ist nicht angelegt oder nicht aktiv." }, statusCode: 403);
            ctx.HttpContext.Items["standort"] = s;
            return await next(ctx);
        });

        static Standort Me(HttpContext h) => (Standort)h.Items["standort"]!;

        // Konfiguration des Clients (im Portal gepflegt)
        ex.MapGet("/konfiguration", (HttpContext h, Settings.SettingsStore store) =>
        {
            var s = Me(h); var c = s.Client;
            return Results.Ok(new
            {
                standort = s.Code, name = s.Name,
                erpPfad = c.ErpPath, importProgramm = c.ImportProgram, schnittstelle = c.Interface, erpBenutzer = c.ErpUser,
                erpKennwort = string.IsNullOrEmpty(c.ErpPassword) ? null : store.Secrets.Unprotect(c.ErpPassword),
                mandant = c.Mandant, belegzeitraum = c.Period, importMuster = c.ImportPattern, importPruefen = c.CheckImport,
                sonderdateien = c.SpecialFiles, exporte = c.Exports,
                aufgaben = c.Tasks.Where(t => t.Enabled).Select(t => new
                {
                    id = t.Id, name = t.Name, zeitplan = t.Schedule, zeitplanText = Delivery.ScheduleEngine.Describe(t.Schedule),
                    import = t.Import, importMuster = string.IsNullOrWhiteSpace(t.ImportPattern) ? c.ImportPattern : t.ImportPattern,
                    schnittstelle = string.IsNullOrWhiteSpace(t.Interface) ? c.Interface : t.Interface,
                    sonderdateien = t.SpecialFiles, export = t.Export, erpBeenden = t.CloseErp, workflows = t.Workflows.Where(w => w.Enabled)
                }),
                abfrageSekunden = Math.Clamp(c.PollSeconds, 15, 3600)
            });
        });

        // Lebenszeichen
        ex.MapPost("/lebenszeichen", (HttpContext h, BeatRequest? req, ExchangeStore store) =>
        {
            store.Beat(Me(h).Code, req?.Version, req?.Machine, req?.Info, h.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true, zeit = DateTimeOffset.Now, offen = store.Pending(Me(h).Code, 1000).Count });
        });

        // Bereitliegende Dateien
        ex.MapGet("/dateien", (HttpContext h, ExchangeStore store, Microsoft.Extensions.Options.IOptionsMonitor<FilialverbundOptions> opt) =>
        {
            var s = Me(h);
            store.Beat(s.Code, null, null, null, h.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(store.Pending(s.Code).Select(e => new
            {
                nr = e.Nr, datei = e.FileName, groesse = e.Size, zeit = e.At, status = e.Status, versuche = e.Attempts,
                art = Kind(s.Client, e.FileName)
            }));
        });

        ex.MapGet("/dateien/{nr:long}", (HttpContext h, long nr, ExchangeStore store) =>
        {
            var e = store.Get(nr);
            if (e is null || e.Direction != ExchangeStatus.Out || !string.Equals(e.Standort, Me(h).Code, StringComparison.OrdinalIgnoreCase)) return Results.NotFound(new { error = "Datei nicht gefunden." });
            if (!e.Exists) return Results.NotFound(new { error = "Die Datei ist am Server nicht mehr vorhanden." });
            if (e.Status == ExchangeStatus.Waiting) store.SetStatus(nr, ExchangeStatus.Fetched, null, countAttempt: true);
            else store.SetStatus(nr, e.Status, null, countAttempt: true);
            return Results.File(e.Stored, "application/octet-stream", e.FileName);
        });

        // Quittung: importiert bzw. ersetzt (ok) oder Fehler mit Meldung
        ex.MapPost("/dateien/{nr:long}/quittung", (HttpContext h, long nr, AckRequest req, ExchangeStore store) =>
        {
            var e = store.Get(nr);
            if (e is null || e.Direction != ExchangeStatus.Out || !string.Equals(e.Standort, Me(h).Code, StringComparison.OrdinalIgnoreCase)) return Results.NotFound(new { error = "Datei nicht gefunden." });
            var msg = (req.Message ?? "").Trim();
            if (req.NotImported > 0) msg = $"{req.NotImported} Satz/Sätze nicht übernommen. {msg}".Trim();
            store.SetStatus(nr, req.Ok ? ExchangeStatus.Imported : ExchangeStatus.Failed, msg.Length == 0 ? null : msg[..Math.Min(msg.Length, 2000)]);
            return Results.Ok(new { ok = true });
        });

        // Rückläufer hochladen (multipart „datei“, optional „an“ = weitere Standorte/Gruppen)
        ex.MapPost("/dateien", async (HttpContext h, FilialverbundService fv) =>
        {
            if (!h.Request.HasFormContentType) return Results.BadRequest(new { error = "Bitte als multipart/form-data mit Feld „datei“ senden." });
            var form = await h.Request.ReadFormAsync();
            var file = form.Files.GetFile("datei") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Keine Datei." });
            if (file.Length > 200L * 1024 * 1024) return Results.BadRequest(new { error = "Datei zu groß (max. 200 MB)." });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var (nr, path, fwd) = fv.Receive(Me(h).Code, file.FileName, ms.ToArray(), form["an"].FirstOrDefault());
            return Results.Ok(new { ok = true, nr, weitergeleitet = fwd });
        }).DisableAntiforgery();
    }

    /// <summary>import | sonderdatei – wie der Client die Datei behandeln soll.</summary>
    public static string Kind(ClientSettings c, string fileName) =>
        c.SpecialFiles.Any(r => !string.IsNullOrWhiteSpace(r.Pattern) && Documents.MailRuleEngine.Like(fileName, r.Pattern.Trim())) ? "sonderdatei" : "import";

    private static object Body(string status, HealthReport r, IEnumerable<HealthCheck>? checks) => new
    {
        status,
        ok = status != HealthLevel.Error,
        zeit = r.At,
        version = r.Version,
        rechner = r.Machine,
        laufzeitMinuten = (long)r.Uptime.TotalMinutes,
        pruefungen = checks?.Select(c => new
        {
            schluessel = c.Key, bereich = c.Area, titel = c.Title, status = c.Status, meldung = c.Message, seit = c.Since,
            versandweg = c.Channel, partner = c.Partner, anzahl = c.Count
        })
    };

    private static object Known(HealthReport r) => r.Checks.Where(c => c.Channel is not null)
        .Select(c => new { schluessel = c.Channel, titel = c.Title, partner = c.Partner, bereich = c.Area }).DistinctBy(x => (x.schluessel, x.partner)).ToList();
}
