using PdfRechnung.Exchange;
using PdfRechnung.Monitoring;
using PdfRechnung.Services;
using PdfRechnung.Settings;

namespace PdfRechnung.Api;

/// <summary>Portal-Seiten für API-Schlüssel, Benachrichtigung, Status und Filialverbund.</summary>
public static class AdminMonitoringApi
{
    public sealed record KeyRequest(string? Name, List<string>? Scopes, string? Standort, bool Active = true);
    public sealed record StatusChange(List<long> Nrs, string Aktion);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
            AdminApi.IsAdmin(ctx.HttpContext) ? await next(ctx) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403));

        // ---------- API-Schlüssel ----------
        g.MapGet("/apikeys", (ApiKeyStore keys) => Results.Ok(keys.All().Select(k => new
        {
            k.Id, k.Name, k.Prefix, k.Scopes, k.Standort, k.Active, k.Created, k.LastUsed, k.LastAddress
        })));
        g.MapPost("/apikeys", (KeyRequest r, ApiKeyStore keys, BelegpostDbAudit audit, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(r.Name)) return Results.BadRequest(new { error = "Bitte einen Namen eintragen." });
            var scopes = r.Scopes ?? new();
            if (scopes.Count == 0) return Results.BadRequest(new { error = "Bitte mindestens ein Recht wählen." });
            if (scopes.Contains(ApiKey.ScopeExchange) && string.IsNullOrWhiteSpace(r.Standort)) return Results.BadRequest(new { error = "Für den Datenaustausch bitte den Standort wählen." });
            var (k, secret) = keys.Create(r.Name, scopes, r.Standort ?? "");
            audit.Write(ctx, "API-Schlüssel angelegt", k.Name);
            return Results.Ok(new { key = k.Id, secret });
        });
        g.MapPut("/apikeys/{id}", (string id, KeyRequest r, ApiKeyStore keys) =>
            keys.Update(id, r.Name ?? "", r.Scopes ?? new(), r.Standort ?? "", r.Active) ? Results.Ok(new { ok = true }) : Results.NotFound());
        g.MapDelete("/apikeys/{id}", (string id, ApiKeyStore keys, BelegpostDbAudit audit, HttpContext ctx) =>
        {
            if (!keys.Delete(id)) return Results.NotFound();
            audit.Write(ctx, "API-Schlüssel gelöscht", id);
            return Results.Ok(new { ok = true });
        });

        // ---------- Status und Benachrichtigung ----------
        g.MapGet("/status", (HealthService health, AlertService alerts) =>
        {
            var r = health.Report();
            return Results.Ok(new
            {
                r.Status, r.At, r.Version, r.Machine, uptimeMinutes = (long)r.Uptime.TotalMinutes, r.Checks,
                open = alerts.Open(), last = new { at = alerts.Last.At, text = alerts.Last.Text, error = alerts.Last.Error }
            });
        });
        g.MapPost("/benachrichtigung/test", async (AlertService alerts, CancellationToken ct) =>
        {
            try { return Results.Ok(new { ok = true, message = await alerts.TestAsync(ct) }); }
            catch (Exception ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        // ---------- Filialverbund ----------
        g.MapPost("/filialverbund/verteilen", async (FilialverbundService fv, CancellationToken ct) =>
        {
            var n = await fv.DistributeAsync(ct);
            var st = fv.State;
            return Results.Ok(new { ok = st.Error is null, message = st.Error ?? $"{n} Datei(en) verteilt" + (st.Unassigned.Count > 0 ? $", {st.Unassigned.Count} ohne Empfänger" : "") + ".", unassigned = st.Unassigned });
        });
        g.MapGet("/filialverbund/clients", (ExchangeStore store, Microsoft.Extensions.Options.IOptionsMonitor<FilialverbundOptions> opt) =>
        {
            var beats = store.Beats();
            return Results.Ok(opt.CurrentValue.Standorte.Select(s => new
            {
                s.Code, s.Name, s.Active,
                beat = beats.TryGetValue(s.Code, out var b) ? b : null,
                pending = store.Pending(s.Code, 1000).Count
            }));
        });

        // ---------- Datenaustausch ansehen (Benutzer mit Recht Eingang oder Ausgang) ----------
        var x = app.MapGroup("/api/austausch").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<PortalAccess>().For(ctx.HttpContext);
            return a.SeeInbound || a.SeeOutbound ? await next(ctx) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
        });
        x.MapGet("", (string? q, string? standort, string? richtung, string? status, string? from, string? to, ExchangeStore store, HttpContext ctx,
            Microsoft.Extensions.Options.IOptionsMonitor<FilialverbundOptions> opt) =>
        {
            var a = ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx);
            var dir = richtung is "aus" or "ein" ? richtung : !a.SeeOutbound ? ExchangeStatus.In : !a.SeeInbound ? ExchangeStatus.Out : null;
            if (dir == ExchangeStatus.Out && !a.SeeOutbound || dir == ExchangeStatus.In && !a.SeeInbound) return Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
            var items = store.List(q, standort, dir, status,
                DateOnly.TryParse(from, out var f) ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue)) : null,
                DateOnly.TryParse(to, out var t) ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null);
            return Results.Ok(new
            {
                items = items.Select(e => new { e.Nr, e.At, e.Direction, e.Standort, e.FileName, e.Size, e.Status, e.StatusAt, e.Message, e.Source, e.Exists, e.Attempts }),
                standorte = opt.CurrentValue.Standorte.Select(s => new { s.Code, s.Name }),
                enabled = opt.CurrentValue.Enabled,
                canChange = a.CanSend
            });
        });
        x.MapGet("/{nr:long}/datei", (long nr, string? ansehen, ExchangeStore store, HttpContext ctx) =>
        {
            var a = ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx);
            var e = store.Get(nr);
            if (e is null || (e.Direction == ExchangeStatus.Out ? !a.SeeOutbound : !a.SeeInbound)) return Results.NotFound(new { error = "Eintrag nicht gefunden." });
            if (!e.Exists) return Results.NotFound(new { error = "Die Datei ist nicht mehr vorhanden." });
            return ansehen is "1" ? Results.File(e.Stored, "text/plain; charset=windows-1252") : Results.File(e.Stored, "application/octet-stream", e.FileName);
        });
        // Erledigt (Fehler manuell bereinigt) bzw. neu bereitstellen (Client holt die Datei erneut)
        x.MapPost("/status", (StatusChange r, ExchangeStore store, HttpContext ctx, BelegpostDbAudit audit) =>
        {
            var a = ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx);
            if (!a.CanSend) return Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403);
            var (status, msg) = r.Aktion switch
            {
                "erledigt" => (ExchangeStatus.Done, $"als erledigt markiert von {a.Name}"),
                "neu" => (ExchangeStatus.Waiting, $"neu bereitgestellt von {a.Name}"),
                _ => ((string?)null, (string?)null)
            };
            if (status is null) return Results.BadRequest(new { error = "Unbekannte Aktion." });
            var n = 0;
            foreach (var nr in r.Nrs)
                if (store.Get(nr) is { Direction: ExchangeStatus.Out } && store.SetStatus(nr, status, msg)) n++;
            audit.Write(ctx, "Datenaustausch " + r.Aktion, string.Join(",", r.Nrs));
            return Results.Ok(new { changed = n });
        });
    }
}

/// <summary>Kleiner Helfer: Eintrag im Prüfprotokoll mit dem angemeldeten Namen.</summary>
public sealed class BelegpostDbAudit
{
    private readonly Data.BelegpostDb _db;
    public BelegpostDbAudit(Data.BelegpostDb db) => _db = db;
    public void Write(HttpContext ctx, string action, string? target)
    {
        try { _db.Audit(ctx.RequestServices.GetRequiredService<PortalAccess>().For(ctx).Name, action, target, null); } catch (Exception) { }
    }
}
