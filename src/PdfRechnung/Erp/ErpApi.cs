using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Services;
using PdfRechnung.Settings;

namespace PdfRechnung.Erp;

public sealed record ErpPreviewRequest(string? VorgangId, string? ErpIndex, string? Code, ErpOptions? Erp);
public sealed record ErpTestWriteRequest(string ErpIndex, string? VorgangId, string? Code, bool Confirm);

/// <summary>Einstellungen → ERP-Anbindung (nur Administratoren) und Zeitstrahl/Status-Link am Vorgang.</summary>
public static class ErpApi
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/erp").AddEndpointFilter(async (ctx, next) =>
            AdminApi.IsAdmin(ctx.HttpContext) ? await next(ctx) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403));

        g.MapGet("/status", (string? mandant, ErpFeedbackService erp) =>
        {
            var o = erp.OptionsFor(mandant);
            ErpServicePass? pass = null;
            string? problem = null;
            try { if (o.IsConfigured) pass = erp.Manager(o).StoredPass(); }
            catch (Exception ex) { problem = ex is ErpException ee ? ee.Message : "Gespeicherter Servicepass kann nicht gelesen werden."; }
            return Results.Ok(new
            {
                enabled = o.Enabled,
                configured = o.IsConfigured,
                pass = pass is null ? "keiner" : pass.State,
                passText = pass?.State switch
                {
                    null => "Noch kein Servicepass – wird beim ersten Zugriff registriert" + (o.AllowRegistration ? "." : " (Registrierung erlauben!)."),
                    "Ready" => "Servicepass bereit",
                    "PendingApproval" => "Servicepass wartet auf Freigabe im ERP",
                    "RegistrationOutcomeUnknown" => "Registrierung mit unklarem Ausgang – im ERP prüfen, dann bestätigen",
                    "Missing" => "Kein Servicepass",
                    var s => s
                },
                lastValidated = pass?.LastValidatedUtc,
                problem,
                lastError = erp.LastError,
                lastSuccess = erp.LastSuccess,
                counts = erp.Counts()
            });
        });

        g.MapPost("/pruefen", async (string? mandant, ErpFeedbackService erp, CancellationToken ct) =>
        {
            try
            {
                await erp.Manager(erp.OptionsFor(mandant)).CheckAsync(ct);
                erp.RetryNow();
                return Results.Ok(new { ok = true, message = "Verbindung zum ERP in Ordnung, Servicepass gültig." });
            }
            catch (ErpException ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        g.MapPost("/registrierung-bestaetigen", (string? mandant, ErpFeedbackService erp, PortalAccess access, HttpContext ctx) =>
        {
            try
            {
                erp.Manager(erp.OptionsFor(mandant)).ResetUnknownRegistration();
                ctx.RequestServices.GetRequiredService<PdfRechnung.Data.BelegpostDb>().Audit(access.For(ctx).Name, "ERP-Registrierung zurückgesetzt");
                return Results.Ok(new { ok = true });
            }
            catch (ErpException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapGet("/auftraege", (string? status, string? q, string? mandant, int? offset, int? limit, ErpFeedbackService erp) =>
        {
            var (items, total) = erp.JobsPage(status, q, mandant, offset ?? 0, limit ?? 50);
            return Results.Ok(new { items = items.Select(View), total, offset = offset ?? 0, limit = limit ?? 50, counts = erp.Counts() });
        });

        g.MapPost("/auftraege/{id:long}/wiederholen", (long id, ErpFeedbackService erp) =>
            erp.Retry(id) ? Results.Ok(new { ok = true }) : Results.NotFound());

        g.MapPost("/auftraege/{id:long}/verwerfen", (long id, ErpFeedbackService erp) =>
            erp.Discard(id) ? Results.Ok(new { ok = true }) : Results.NotFound());

        g.MapPost("/wiederholen", (ErpFeedbackService erp) => { erp.RetryNow(); return Results.Ok(new { ok = true }); });

        // Vorschau: welche Felder würden geschrieben (mit den Einstellungen aus der Maske, noch ungespeichert)
        g.MapPost("/vorschau", (ErpPreviewRequest req, DocumentStore store, IOptionsMonitor<ErpOptions> opt, StatusLinkService links) =>
        {
            var o = req.Erp ?? opt.CurrentValue;
            var v = !string.IsNullOrWhiteSpace(req.VorgangId) ? store.Get(req.VorgangId.Trim())
                : !string.IsNullOrWhiteSpace(req.ErpIndex) ? store.ByErpIndex(req.ErpIndex.Trim()).FirstOrDefault()
                : store.Query(new VorgangFilter { SeeAll = true, Limit = 1 }).FirstOrDefault();
            if (v is null) return Results.BadRequest(new { error = "Kein passender Vorgang gefunden." });
            var code = string.IsNullOrWhiteSpace(req.Code) ? v.StatusCode ?? StatusCode.Erfasst : req.Code;
            var fields = ErpFieldBuilder.Build(o, new ErpValueContext(v, code, DateTimeOffset.Now, v.SendChannel, links.UrlFor(v)));
            var (errors, warnings) = ErpValues.Validate(o);
            var p = new Dictionary<string, string> { ["BELNDX"] = v.ErpIndex ?? "(kein @@pi@@ im Beleg)" };
            foreach (var f in fields) p[f.Key] = f.Value;
            p["KEINE_VERBUCHUNG"] = "1";
            p["BERECHNE_SUMMEN"] = "0";
            return Results.Ok(new
            {
                vorgang = v.Id, beleg = $"{v.DocumentType} {v.VoucherId}".Trim(), code, codeText = StatusCode.Text(code),
                write = ErpFieldBuilder.ShouldWrite(o, code), parameters = p, errors, warnings
            });
        });

        g.MapPost("/testschreiben", async (ErpTestWriteRequest req, DocumentStore store, IOptionsMonitor<ErpOptions> opt, ErpFeedbackService erp,
            StatusLinkService links, PortalAccess access, HttpContext ctx, CancellationToken ct) =>
        {
            if (!req.Confirm) return Results.BadRequest(new { error = "Bitte das Testschreiben bestätigen." });
            var belndx = ControlCodeParser.NormalizeErpIndex(req.ErpIndex);
            if (belndx is null) return Results.BadRequest(new { error = "Der Belegindex hat 11 Zeichen: Zeitraum, Archiv (A/N), Belegart und 8-stellige Belegnummer, z. B. 0NR41261010." });
            var v = (req.VorgangId is { Length: > 0 } id ? store.Get(id) : null) ?? store.ByErpIndex(belndx).FirstOrDefault()
                ?? store.Query(new VorgangFilter { SeeAll = true, Limit = 1 }).FirstOrDefault();
            if (v is null) return Results.BadRequest(new { error = "Es gibt noch keinen Vorgang als Datenquelle." });
            var o = erp.OptionsFor(v); // Verbindung des Mandanten dieses Vorgangs
            var code = string.IsNullOrWhiteSpace(req.Code) ? v.StatusCode ?? StatusCode.Erfasst : req.Code;
            var fields = ErpFieldBuilder.Build(o, new ErpValueContext(v, code, DateTimeOffset.Now, v.SendChannel, links.UrlFor(v)));
            if (fields.Count == 0) return Results.BadRequest(new { error = "Keine Felder zugeordnet." });
            try
            {
                var mgr = erp.Manager(o);
                if (await mgr.ReadDocumentAsync(belndx, ct) is null) return Results.Ok(new { ok = false, message = ErpException.Describe("DocumentNotFound") });
                await mgr.WriteDocumentAsync(belndx, fields, ct);
                ctx.RequestServices.GetRequiredService<PdfRechnung.Data.BelegpostDb>()
                    .Audit(access.For(ctx).Name, "ERP-Testschreiben", belndx, string.Join(", ", fields.Select(f => $"{f.Key}={f.Value}")));
                return Results.Ok(new { ok = true, message = $"In Beleg {belndx} geschrieben: " + string.Join(", ", fields.Select(f => $"{f.Key}={f.Value}")) });
            }
            catch (ErpException ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        g.MapGet("/audit", (PdfRechnung.Data.BelegpostDb db) => Results.Ok(new { items = db.AuditLog(300) }));

        // ---------- Zeitstrahl, Status-Link und ERP-Rückmeldung am Vorgang ----------
        app.MapGet("/api/vorgaenge/{id}/verlauf", (string id, DocumentStore store, PortalAccess access, HttpContext ctx, StatusLinkService links,
            IOptionsMonitor<StatusLinkOptions> lo, IOptionsMonitor<ErpOptions> eo, ErpFeedbackService erp) =>
        {
            var a = access.For(ctx);
            var v = store.Get(id);
            if (v is null || !a.CanSee(v)) return Results.NotFound();
            return Results.Ok(new
            {
                events = store.Events(id).Select(e => new { at = e.At, code = e.Code, text = e.CodeText, channel = e.Channel, by = e.By, note = e.Text }),
                statusCode = v.StatusCode,
                statusText = StatusCode.Text(v.StatusCode),
                erpIndex = v.ErpIndex,
                statusLink = lo.CurrentValue.Enabled ? links.UrlFor(v) : null,
                erpEnabled = eo.CurrentValue.Enabled,
                erp = erp.Jobs(null, id, 20).Select(View)
            });
        });

        app.MapPost("/api/vorgaenge/{id}/statuslink/neu", (string id, DocumentStore store, PortalAccess access, HttpContext ctx, StatusLinkService links) =>
        {
            var a = access.For(ctx);
            var v = store.Get(id);
            if (v is null || !a.CanSee(v)) return Results.NotFound();
            if (!a.IsAdmin) return Results.Json(new { error = "Nur Administratoren können den Link neu erzeugen." }, statusCode: 403);
            return Results.Ok(new { statusLink = links.BaseUrl() + "/s/" + links.Renew(v) });
        });

        app.MapPost("/api/vorgaenge/{id}/erp/senden", (string id, DocumentStore store, PortalAccess access, HttpContext ctx, ErpFeedbackService erp,
            IOptionsMonitor<ErpOptions> eo) =>
        {
            var a = access.For(ctx);
            var v = store.Get(id);
            if (v is null || !a.CanSee(v)) return Results.NotFound();
            if (!a.IsAdmin) return Results.Json(new { error = "Nur Administratoren." }, statusCode: 403);
            if (!eo.CurrentValue.Enabled || v.ErpIndex is null) return Results.BadRequest(new { error = "ERP-Rückmeldung ist aus oder der Beleg hat keinen Belegindex (@@pi@@)." });
            erp.Enqueue(v.ErpIndex, v.Id, v.StatusCode ?? StatusCode.Erfasst, DateTimeOffset.Now, v.Mandant);
            return Results.Ok(new { ok = true });
        });
    }

    private static object View(ErpJob j) => new
    {
        j.Id, erpIndex = j.ErpIndex, vorgangId = j.VorgangId, j.Code, codeText = StatusCode.Text(j.Code), eventAt = j.EventAt, j.Status,
        attempts = j.Attempts, nextAttempt = j.NextAttempt, created = j.Created, done = j.Done, message = j.Message,
        fields = j.Fields is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(j.Fields),
        mandant = j.Mandant, secureApp = j.SecureApp
    };
}
