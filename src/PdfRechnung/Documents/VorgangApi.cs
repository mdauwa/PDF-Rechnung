using Microsoft.Extensions.Options;
using PdfRechnung.Services;

namespace PdfRechnung.Documents;

public sealed record MailUpdate(List<string>? To, List<string>? Cc, List<string>? Bcc, string? Subject, string? Body, string? FileName, bool? Enabled);
public sealed record ActionRequest(string Action, bool Force = false, string? Comment = null);
public sealed record BulkSendRequest(List<string> Ids);

/// <summary>Portal-API für die Vorgänge: Liste, Detail, PDF-Vorschau, Versanddaten, Aktionen, Upload.</summary>
public static class VorgangApi
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/vorgaenge");

        g.MapGet("", (string? status, string? q, string? from, string? to, DocumentStore store) =>
        {
            var list = store.All().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var set = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
                list = list.Where(v => set.Contains(v.Status));
            }
            if (DateOnly.TryParse(from, out var f)) list = list.Where(v => DateOnly.FromDateTime(v.CreatedAt.LocalDateTime) >= f);
            if (DateOnly.TryParse(to, out var t)) list = list.Where(v => DateOnly.FromDateTime(v.CreatedAt.LocalDateTime) <= t);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var s = q.Trim();
                bool Has(string? x) => x?.Contains(s, StringComparison.CurrentCultureIgnoreCase) == true;
                list = list.Where(v => Has(v.VoucherId) || Has(v.BuyerName) || Has(v.BuyerId) || Has(v.Mail.Subject)
                    || v.Mail.To.Any(Has) || Has(v.OriginalFileName) || Has(v.PrintUser) || Has(v.Id));
            }
            var all = store.All();
            var today = DateOnly.FromDateTime(DateTime.Today);
            return Results.Ok(new
            {
                items = list.Take(2000).Select(v => v.Summary()),
                counts = VorgangStatus.All.ToDictionary(s => s, s => all.Count(v => v.Status == s)),
                sentToday = all.Count(v => v.SentAt is { } sa && DateOnly.FromDateTime(sa.LocalDateTime) == today)
            });
        });

        g.MapGet("/{id}", (string id, DocumentStore store, IConfiguration cfg) =>
        {
            var v = store.Get(id);
            if (v is null) return Results.NotFound(new { error = "Vorgang nicht gefunden." });
            var mail = cfg.GetSection("Mail").Get<MailOptions>() ?? new MailOptions();
            return Results.Ok(new
            {
                vorgang = v,
                hasSendPdf = store.FilePath(id, DocumentStore.SendFile) is not null,
                mailEnabled = mail.Enabled,
                autoSend = mail.Enabled && mail.AutoSend,
                autoSendAt = mail.Enabled && mail.AutoSend && v.SendAttempts == 0 && (v.Status == VorgangStatus.Ready || (mail.AutoSendWithWarnings && v.Status == VorgangStatus.Warning))
                    ? v.CreatedAt.AddMinutes(Math.Max(0, mail.AutoSendDelayMinutes)) : (DateTimeOffset?)null
            });
        });

        g.MapGet("/{id}/pdf", (string id, bool? download, string? variant, DocumentStore store) =>
        {
            var v = store.Get(id);
            // Standard: das Versand-PDF (ohne Steuerzeichen, mit Briefpapier); "original" = Ausdruck wie eingegangen
            var path = variant == "original" ? store.FilePath(id, DocumentStore.PdfFile)
                : store.FilePath(id, DocumentStore.SendFile) ?? store.FilePath(id, DocumentStore.PdfFile);
            if (v is null || path is null) return Results.NotFound();
            var name = MailTemplate.SafeFileName(v.Mail.FileName) + ".pdf";
            return download == true
                ? Results.File(path, "application/pdf", name)
                : Results.File(path, "application/pdf", enableRangeProcessing: true);
        });

        g.MapGet("/{id}/text", (string id, DocumentStore store) =>
        {
            var path = store.FilePath(id, DocumentStore.TextFile);
            return path is null ? Results.NotFound() : Results.Text(File.ReadAllText(path), "text/plain; charset=utf-8");
        });

        g.MapGet("/{id}/mailvorschau", (string id, DocumentStore store, SendService send, IOptionsMonitor<MailOptions> mail) =>
        {
            var v = store.Get(id);
            if (v is null) return Results.NotFound();
            var o = mail.CurrentValue;
            var r = send.Render(v);
            var from = string.IsNullOrWhiteSpace(v.Mail.FromAddress) ? o.FromAddress : v.Mail.FromAddress;
            var name = v.Mail.FromName ?? (string.IsNullOrWhiteSpace(v.Mail.FromAddress) ? o.FromName : null);
            return Results.Ok(new { html = MailPreview.Inline(r), text = r.Text, subject = v.Mail.Subject, from, fromName = name, template = v.Mail.Template, reason = v.Mail.TemplateReason });
        });

        g.MapPut("/{id}/mail", (string id, MailUpdate req, DocumentStore store, DocumentProcessor proc, PortalAccess access, HttpContext ctx) =>
        {
            var a = access.For(ctx);
            if (!a.CanSend) return Forbidden();
            var bad = (req.To ?? new()).Concat(req.Cc ?? new()).Concat(req.Bcc ?? new()).Where(x => !DocumentChecker.IsMail(x)).ToList();
            if (bad.Count > 0) return Results.BadRequest(new { error = "Ungültige E-Mail-Adresse: " + string.Join(", ", bad) });
            var v = store.Update(id, v =>
            {
                if (v.Status is VorgangStatus.Sent or VorgangStatus.Sending or VorgangStatus.Discarded) return false;
                v.Mail.To = Clean(req.To); v.Mail.Cc = Clean(req.Cc); v.Mail.Bcc = Clean(req.Bcc);
                if (req.Subject is not null) v.Mail.Subject = req.Subject.Trim();
                if (req.Body is not null) v.Mail.Body = req.Body.Replace("\r\n", "\n");
                if (req.FileName is not null) v.Mail.FileName = MailTemplate.SafeFileName(req.FileName);
                if (req.Enabled is not null) v.Mail.Enabled = req.Enabled.Value;
                v.Mail.Edited = true;
                if (v.Status == VorgangStatus.SendError) v.Status = VorgangStatus.Warning;
                proc.Revalidate(v);
                v.History.Add(new HistoryEntry(DateTimeOffset.Now, a.Name, "versanddaten", $"An: {string.Join(", ", v.Mail.To)}"));
                return true;
            });
            return v is null ? Results.NotFound() : Results.Ok(new { vorgang = v });
        });

        g.MapPost("/{id}/aktion", async (string id, ActionRequest req, DocumentStore store, DocumentProcessor proc, SendService send,
            PortalAccess access, HttpContext ctx, CancellationToken ct) =>
        {
            var a = access.For(ctx);
            if (!a.CanSend) return Forbidden();
            switch (req.Action)
            {
                case "senden":
                {
                    var (ok, msg) = await send.SendAsync(id, a.Name, req.Force, ct);
                    return ok ? Results.Ok(new { message = msg, vorgang = store.Get(id) }) : Results.BadRequest(new { error = msg, vorgang = store.Get(id) });
                }
                case "pruefen":
                {
                    var v = proc.Recheck(id, a.Name);
                    return v is null ? Results.NotFound() : Results.Ok(new { message = "Neu geprüft.", vorgang = v });
                }
                case "verwerfen":
                {
                    var v = store.Update(id, v =>
                    {
                        if (v.Status is VorgangStatus.Sent or VorgangStatus.Sending) return false;
                        v.Status = VorgangStatus.Discarded;
                        v.History.Add(new HistoryEntry(DateTimeOffset.Now, a.Name, "verworfen", req.Comment));
                        return true;
                    });
                    return v is null ? Results.NotFound() : Results.Ok(new { message = "Verworfen.", vorgang = v });
                }
                case "wiederherstellen":
                {
                    var v = store.Update(id, v =>
                    {
                        if (v.Status != VorgangStatus.Discarded) return false;
                        v.Status = VorgangStatus.Warning;
                        proc.Revalidate(v);
                        v.History.Add(new HistoryEntry(DateTimeOffset.Now, a.Name, "wiederhergestellt"));
                        return true;
                    });
                    return v is null ? Results.NotFound() : Results.Ok(new { message = "Wiederhergestellt.", vorgang = v });
                }
                case "erneut":
                {
                    // Versendeten Vorgang erneut senden: Status zurücksetzen, dann senden
                    var v = store.Update(id, v =>
                    {
                        if (v.Status != VorgangStatus.Sent) return false;
                        v.Status = VorgangStatus.Warning;
                        proc.Revalidate(v);
                        v.History.Add(new HistoryEntry(DateTimeOffset.Now, a.Name, "erneut", "erneuter Versand"));
                        return true;
                    });
                    if (v is null) return Results.NotFound();
                    var (ok, msg) = await send.SendAsync(id, a.Name, req.Force, ct);
                    return ok ? Results.Ok(new { message = msg, vorgang = store.Get(id) }) : Results.BadRequest(new { error = msg, vorgang = store.Get(id) });
                }
                case "loeschen":
                {
                    if (!a.IsAdmin) return Forbidden();
                    return store.Delete(id) ? Results.Ok(new { message = "Gelöscht." }) : Results.NotFound();
                }
                default: return Results.BadRequest(new { error = "Unbekannte Aktion." });
            }
        });

        g.MapPost("/senden", async (BulkSendRequest req, SendService send, PortalAccess access, HttpContext ctx, CancellationToken ct) =>
        {
            var a = access.For(ctx);
            if (!a.CanSend) return Forbidden();
            var results = new List<object>();
            foreach (var id in req.Ids.Distinct())
            {
                var (ok, msg) = await send.SendAsync(id, a.Name, force: false, ct);
                results.Add(new { id, ok, message = msg });
            }
            return Results.Ok(new { results });
        });

        g.MapPost("/upload", async (HttpRequest request, DocumentProcessor proc, PortalAccess access, HttpContext ctx) =>
        {
            var a = access.For(ctx);
            if (!a.CanSend) return Forbidden();
            var form = await request.ReadFormAsync();
            var created = new List<object>();
            foreach (var f in form.Files)
            {
                if (f.Length == 0) continue;
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms);
                var bytes = ms.ToArray();
                if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
                    return Results.BadRequest(new { error = $"{f.FileName} ist keine PDF-Datei." });
                var v = proc.Process(bytes, "Upload", Path.GetFileName(f.FileName), a.Name);
                created.Add(v.Summary());
            }
            return created.Count == 0 ? Results.BadRequest(new { error = "Keine Datei ausgewählt." }) : Results.Ok(new { created });
        }).DisableAntiforgery();
    }

    private static IResult Forbidden() => Results.Json(new { error = "Keine Berechtigung zum Versenden. Bitte an einen Administrator wenden." }, statusCode: 403);

    private static List<string> Clean(List<string>? l) =>
        (l ?? new()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
