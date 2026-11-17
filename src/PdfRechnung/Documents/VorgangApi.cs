using Microsoft.Extensions.Options;
using PdfRechnung.Services;

namespace PdfRechnung.Documents;

public sealed record MailUpdate(List<string>? To, List<string>? Cc, List<string>? Bcc, string? Subject, string? Body, string? FileName, bool? Enabled);
public sealed record ActionRequest(string Action, bool Force = false, string? Comment = null);
public sealed record BulkSendRequest(List<string> Ids);

/// <summary>Portal-API für die Vorgänge: Liste, Detail, PDF-Vorschau, Versanddaten, Aktionen, Upload.</summary>
public static class VorgangApi
{
    /// <summary>Sichtbarer Bereich eines Benutzers als Datenbankfilter.</summary>
    public static VorgangFilter Visibility(AccessInfo a) => new()
    {
        SeeAll = a.Authenticated && a.SeeAll,
        Owners = a.Authenticated && a.Visible is not null ? a.Visible.ToList() : Array.Empty<string>()
    };

    public sealed record Anlage(int Nr, string Name, long Size, string Path);

    /// <summary>
    /// Herunterladbare Anlagen: nur Dateien, die im Ordner des Vorgangs liegen (z. B. Anhänge einer normalen E-Mail) –
    /// keine beliebigen Pfade aus Steuerzeichen.
    /// </summary>
    public static List<Anlage> Anlagen(DocumentStore store, Vorgang v)
    {
        var dir = Path.GetFullPath(store.DirectoryFor(v)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var list = new List<Anlage>();
        for (var i = 0; i < v.Mail.Attachments.Count; i++)
        {
            try
            {
                var full = Path.GetFullPath(v.Mail.Attachments[i]);
                if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                    list.Add(new Anlage(i, Path.GetFileName(full), new FileInfo(full).Length, full));
            }
            catch (Exception) { }
        }
        return list;
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/vorgaenge");
        // Sichtbarkeit: Vorgänge anderer Benutzer gibt es für Benutzer mit eingeschränkter Sicht nicht (404)
        g.AddEndpointFilter(async (ctx, next) =>
        {
            if (ctx.HttpContext.Request.RouteValues.TryGetValue("id", out var idObj) && idObj is string id)
            {
                var sp = ctx.HttpContext.RequestServices;
                var a = sp.GetRequiredService<PortalAccess>().For(ctx.HttpContext);
                var v = sp.GetRequiredService<DocumentStore>().Get(id);
                if (v is not null && !a.CanSee(v)) return Results.NotFound();
            }
            return await next(ctx);
        });

        g.MapGet("", (string? status, string? q, string? from, string? to, string? mine, DocumentStore store, PortalAccess access, HttpContext ctx, Delivery.InboundLog book) =>
        {
            var acc = access.For(ctx);
            var me = acc.UserId;
            var vis = Visibility(acc);
            var filter = vis with
            {
                Owner = mine is "1" or "true" ? me ?? "-" : null,
                Statuses = string.IsNullOrWhiteSpace(status) ? null
                    : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                From = DateOnly.TryParse(from, out var f) ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue)) : null,
                Before = DateOnly.TryParse(to, out var t) ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
                Text = string.IsNullOrWhiteSpace(q) ? null : q,
                Limit = 2000
            };
            var today = new DateTimeOffset(DateTime.Today);
            var mineOpen = me is null ? 0 : store.Count(vis with
            {
                Owner = me,
                Statuses = new[] { VorgangStatus.Error, VorgangStatus.Warning, VorgangStatus.Ready, VorgangStatus.SendError, VorgangStatus.Sending }
            });
            return Results.Ok(new
            {
                items = store.Query(filter).Select(v => v.Summary()),
                counts = store.CountByStatus(vis),
                mine = mineOpen,
                sentToday = store.CountSentBetween(vis, today, today.AddDays(1)),
                inbound = acc.SeeInbound ? book.Unread() : 0
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
                // Anlagen, die im Vorgang liegen (z. B. normale E-Mail): Name und Größe zum Herunterladen
                anlagen = Anlagen(store, v).Select(x => new { nr = x.Nr, name = x.Name, size = x.Size }),
                mandant = cfg.GetSection("Mandanten").Get<MandantOptions>()?.Find(v.Mandant) is { } md
                    ? new { number = md.Number, name = md.Name, configured = true } : v.Mandant is null ? null : new { number = v.Mandant, name = "", configured = false },
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

        g.MapGet("/{id}/anlage/{nr:int}", (string id, int nr, DocumentStore store) =>
        {
            var v = store.Get(id);
            var a = v is null ? null : Anlagen(store, v).FirstOrDefault(x => x.Nr == nr);
            return a is null ? Results.NotFound() : Results.File(a.Path, "application/octet-stream", a.Name);
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
                case "gotomaxx":
                {
                    var (ok, msg) = await ctx.RequestServices.GetRequiredService<GotomaxxService>().HandOverNowAsync(id, a.Name, ct);
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

        g.MapPost("/senden", async (BulkSendRequest req, SendService send, DocumentStore store, PortalAccess access, HttpContext ctx, CancellationToken ct) =>
        {
            var a = access.For(ctx);
            if (!a.CanSend) return Forbidden();
            var results = new List<object>();
            foreach (var id in req.Ids.Distinct())
            {
                if (store.Get(id) is not { } vv || !a.CanSee(vv)) { results.Add(new { id, ok = false, message = "Nicht gefunden." }); continue; }
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
                var v = proc.Process(bytes, "Upload", Path.GetFileName(f.FileName), a.Name, null, a.UserId);
                created.Add(v.Summary());
            }
            return created.Count == 0 ? Results.BadRequest(new { error = "Keine Datei ausgewählt." }) : Results.Ok(new { created });
        }).DisableAntiforgery();
    }

    private static IResult Forbidden() => Results.Json(new { error = "Keine Berechtigung zum Versenden. Bitte an einen Administrator wenden." }, statusCode: 403);

    private static List<string> Clean(List<string>? l) =>
        (l ?? new()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
