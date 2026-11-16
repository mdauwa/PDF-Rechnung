using PdfRechnung.Settings;

namespace PdfRechnung.Delivery;

/// <summary>Einstellungen → Kunden (Versandwege und Regeln je Kunde) und Abruf (nur Administratoren).</summary>
public static class DeliveryApi
{
    public sealed record TestRequest(string? CustomerId, string? ChannelId, string? Type);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
            AdminApi.IsAdmin(ctx.HttpContext) ? await next(ctx) : Results.Json(new { error = "Keine Berechtigung." }, statusCode: 403));

        g.MapGet("/kunden", (CustomerStore store) => Results.Ok(new
        {
            items = store.All().Select(Redacted).OrderBy(c => c.Number, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name),
            types = ChannelType.All.Select(t => new { key = t, text = ChannelType.Text(t) }),
            contents = ChannelContent.All.Select(c => new { key = c, text = ChannelContent.Text(c) })
        }));

        g.MapPut("/kunden", (Customer c, CustomerStore store, SettingsStore settings) =>
        {
            c.Number = c.Number.Trim(); c.Name = c.Name.Trim(); c.Mandant = c.Mandant.Trim();
            c.Emails = c.Emails.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (c.Number.Length == 0 && c.Emails.Count == 0) return Results.BadRequest(new { error = "Bitte eine Kundennummer oder mindestens eine E-Mail-Adresse angeben." });
            if (c.Number.Length > 0 && store.All().Any(x => x.Id != c.Id && CustomerStore.SameNumber(x.Number, c.Number)
                    && (string.IsNullOrWhiteSpace(x.Mandant) || string.IsNullOrWhiteSpace(c.Mandant) || MandantOptions.Same(x.Mandant, c.Mandant))))
                return Results.BadRequest(new { error = $"Kundennummer {c.Number} ist schon angelegt." });
            foreach (var ch in c.Channels)
            {
                if (!ChannelType.All.Contains(ch.Settings.Type)) return Results.BadRequest(new { error = $"Unbekannter Versandweg „{ch.Settings.Type}“." });
                if (ch.Settings.Content.Length > 0 && !ChannelContent.All.Contains(ch.Settings.Content)) return Results.BadRequest(new { error = $"Unbekannter Inhalt „{ch.Settings.Content}“." });
            }
            var prev = string.IsNullOrEmpty(c.Id) ? null : store.Get(c.Id);
            foreach (var ch in c.Channels)
                ch.Settings.TakeSecrets(prev?.Channels.FirstOrDefault(x => x.Id == ch.Id && !string.IsNullOrEmpty(ch.Id))?.Settings, x => settings.Secrets.Protect(x));
            return Results.Ok(new { customer = Redacted(store.Save(c)) });
        });

        g.MapDelete("/kunden/{id}", (string id, CustomerStore store) => store.Delete(id) ? Results.Ok(new { ok = true }) : Results.NotFound());

        // Verbindung testen (gespeicherte Einstellung): Versandweg eines Kunden oder global
        g.MapPost("/versandwege/test", async (TestRequest req, CustomerStore store, DeliveryService delivery, Microsoft.Extensions.Options.IOptionsMonitor<DeliveryOptions> opt, CancellationToken ct) =>
        {
            ChannelSettings? own = null;
            if (!string.IsNullOrEmpty(req.CustomerId))
                own = store.Get(req.CustomerId)?.Channels.FirstOrDefault(c => c.Id == req.ChannelId)?.Settings;
            else if (!string.IsNullOrEmpty(req.Type))
                own = new ChannelSettings { Type = req.Type };
            if (own is null) return Results.BadRequest(new { error = "Versandweg nicht gefunden – bitte zuerst speichern." });
            try { return Results.Ok(new { ok = true, message = await delivery.TestAsync(own, ct) }); }
            catch (Exception ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
        });

        g.MapGet("/abruf", (InboundService inbound) => Results.Ok(new
        {
            jobs = inbound.Jobs().Select(j =>
            {
                var (ni, no) = inbound.NextRuns(j);
                return new
                {
                    j.Key, j.Label, type = ChannelType.Text(j.Settings.Type), target = inbound.Target(j.Settings), interval = j.Settings.InboundInterval,
                    inboundPlan = j.Settings.InboundEnabled == "ja" ? ScheduleEngine.Describe(j.Settings.InboundSchedule) : null,
                    outboxPlan = string.IsNullOrWhiteSpace(j.Settings.OutboxFolder) ? null : ScheduleEngine.Describe(j.Settings.OutboxSchedule),
                    nextInbound = ni, nextOutbox = no
                };
            }),
            runs = inbound.Runs()
        }));

        g.MapPost("/abruf/jetzt", async (string key, InboundService inbound, CancellationToken ct) =>
        {
            var job = inbound.Jobs().FirstOrDefault(j => j.Key == key);
            if (job is null) return Results.NotFound(new { error = "Abruf nicht gefunden – bitte zuerst speichern." });
            var r = await inbound.RunAsync(job, ct);
            return Results.Ok(new { ok = r.Ok, message = $"{r.Label}: {r.Message}" });
        });
    }

    private static Customer Redacted(Customer c)
    {
        foreach (var ch in c.Channels) ch.Settings.Redact();
        return c;
    }
}
