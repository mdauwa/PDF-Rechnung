using Microsoft.Extensions.Options;
using PdfRechnung.Services;

namespace PdfRechnung.Documents;

public sealed record MailUpdate(List<string>? To, List<string>? Cc, List<string>? Bcc, string? Subject, string? Body, string? FileName, bool? Enabled);
public sealed record ActionRequest(string Action, bool Force = false, string? Comment = null);
public sealed record LineCorrectionRequest(int Index, string? Name, string? Description, string? SellerId, string? UnitCode, string? VatCategory);
public sealed record InvoiceCorrectionRequest(Dictionary<string, string?>? Fields, List<LineCorrectionRequest>? Lines);
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

        g.MapGet("", (string? status, string? q, string? from, string? to, string? mine, int? offset, int? limit, DocumentStore store, PortalAccess access, HttpContext ctx, Delivery.InboundLog book) =>
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
                Offset = Math.Max(0, offset ?? 0),
                Limit = Math.Clamp(limit ?? 100, 1, 500)
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
                total = store.Count(filter), offset = filter.Offset, limit = filter.Limit,
                counts = store.CountByStatus(vis),
                mine = mineOpen,
                sentToday = store.CountSentBetween(vis, today, today.AddDays(1)),
                inbound = acc.SeeInbound ? book.Unread() : 0
            });
        });

        // Admin: Kennungen aller Treffer des Filters (für „alle Treffer löschen“)
        g.MapGet("/ids", (string? status, string? q, string? from, string? to, string? mine, DocumentStore store, PortalAccess access, HttpContext ctx) =>
        {
            var acc = access.For(ctx);
            if (!acc.IsAdmin) return Results.Json(new { error = "Nur Administratoren." }, statusCode: 403);
            var filter = Visibility(acc) with
            {
                Owner = mine is "1" or "true" ? acc.UserId ?? "-" : null,
                Statuses = string.IsNullOrWhiteSpace(status) ? null
                    : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                From = DateOnly.TryParse(from, out var f) ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue)) : null,
                Before = DateOnly.TryParse(to, out var t) ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
                Text = string.IsNullOrWhiteSpace(q) ? null : q
            };
            return Results.Ok(new { ids = store.QueryIds(filter) });
        });

        // Admin: mehrere Vorgänge endgültig löschen (Datenbank und Ablageordner); „in Versand“ wird übersprungen
        g.MapPost("/loeschen", (BulkSendRequest req, DocumentStore store, PortalAccess access, HttpContext ctx, PdfRechnung.Data.BelegpostDb db) =>
        {
            var a = access.For(ctx);
            if (!a.IsAdmin) return Results.Json(new { error = "Nur Administratoren dürfen Vorgänge löschen." }, statusCode: 403);
            int deleted = 0, skipped = 0;
            foreach (var id in req.Ids.Distinct().Take(5000))
            {
                var v = store.Get(id);
                if (v is null) continue;
                if (v.Status == VorgangStatus.Sending) { skipped++; continue; }
                if (store.Delete(id)) deleted++;
            }
            if (deleted > 0) db.Audit(a.Name, "Vorgänge gelöscht", null, $"{deleted} gelöscht{(skipped > 0 ? $", {skipped} in Versand übersprungen" : "")}");
            return Results.Ok(new { deleted, skipped });
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
                hasXml = store.FilePath(id, DocumentStore.XmlFile) is not null,
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

        // Validator für die E-Rechnung dieses Vorgangs: bei ZUGFeRD das Versand-PDF (prüft auch die Einbettung), sonst die XML
        g.MapGet("/{id}/pruefung", (string id, DocumentStore store) =>
        {
            var v = store.Get(id);
            if (v is null) return Results.NotFound(new { error = "Vorgang nicht gefunden." });
            var xml = store.FilePath(id, DocumentStore.XmlFile);
            if (xml is null) return Results.NotFound(new { error = "Für diesen Vorgang hat Belegpost keine E-Rechnung erzeugt." });
            var zugferd = InvoiceFormat.Find(v.FormatCode ?? v.Invoice.FormatCode)?.Kind == "zugferd";
            var pdf = zugferd ? store.FilePath(id, DocumentStore.SendFile) : null;
            var path = pdf ?? xml;
            var name = (string.IsNullOrWhiteSpace(v.Mail.FileName) ? v.VoucherId ?? id : v.Mail.FileName) + Path.GetExtension(path);
            var rep = Edi.EdiValidation.Validate(File.ReadAllBytes(path), name);
            rep.ViewUrl = $"api/vorgaenge/{Uri.EscapeDataString(id)}/ansicht";
            return Results.Ok(rep);
        });

        g.MapGet("/{id}/ansicht", (string id, DocumentStore store, HttpContext ctx) =>
        {
            var xml = store.Get(id) is null ? null : store.FilePath(id, DocumentStore.XmlFile);
            return xml is null ? Results.NotFound() : Edi.InvoiceView.Result(ctx, File.ReadAllBytes(xml));
        });

        // ---- Rechnungsdaten-Editor: Stammdaten korrigieren, Beträge bleiben wie gedruckt ----
        g.MapGet("/{id}/rechnungsdaten", (string id, DocumentStore store, DocumentProcessor proc, PortalAccess access, HttpContext ctx) =>
        {
            var v = store.Get(id);
            if (v is null) return Results.NotFound();
            var orig = proc.OriginalCodes(id) ?? new ParsedCodes();
            var c = v.Corrections ?? new InvoiceCorrections();
            var rows = orig.Table("ITEM_DETAILS").ToList();
            var a = access.For(ctx);
            return Results.Ok(new
            {
                id, voucherId = v.VoucherId, documentType = v.DocumentType, format = v.FormatName, eInvoice = v.EInvoiceProfile,
                editable = a.CanSend && CorrectionsAllowed(v) && v.FormatCode is not null,
                why = v.FormatCode is null ? "Für diesen Beleg sind keine Rechnungsdaten (@@xt@@/@@xi@@) gedruckt."
                    : !CorrectionsAllowed(v) ? "Der Beleg ist versendet, an gotomaxx übergeben oder verworfen." : null,
                changedBy = c.ChangedBy, changedAt = c.ChangedAt,
                fields = InvoiceCorrections.Editable.Select(f => new
                {
                    key = f.Key, label = f.Label, group = f.Group, type = f.Type, help = f.Help,
                    printed = orig.Field(f.Key),
                    value = c.Fields.TryGetValue(f.Key, out var cv) ? cv : orig.Field(f.Key),
                    corrected = c.Fields.ContainsKey(f.Key)
                }),
                lines = rows.Select((r, i) =>
                {
                    var l = i < v.Invoice.Lines.Count ? v.Invoice.Lines[i] : null;
                    c.Lines.TryGetValue(i, out var lc);
                    return new
                    {
                        index = i, lineId = r.Get("ITEM_LINE_ID"), name = lc?.Name ?? r.Get("ITEM_NAME"), description = lc?.Description ?? r.Get("ITEM_DESCRIPTION"),
                        sellerId = lc?.SellerId ?? r.Get("ITEM_SELLER_ID"), quantity = l?.Quantity, baseQuantity = l?.BaseQuantity, unitText = r.Get("ITEM_UNIT"),
                        unitCode = lc?.UnitCode ?? l?.UnitCode, price = l?.Price, vatPercent = l?.VatPercent,
                        vatCategory = lc?.VatCategory ?? r.Get("ITEM_VAT_CATEGORY") ?? "S", total = l?.Total,
                        corrected = lc is { IsEmpty: false }
                    };
                }),
                totals = new { net = v.Invoice.NetAmount, tax = v.Invoice.TaxAmount, gross = v.Invoice.GrossAmount, printedGross = v.Invoice.PrintedGrossAmount, currency = v.Invoice.Fields.GetValueOrDefault("CURRENCY")?.Trim() ?? "EUR" },
                messages = v.Messages
            });
        });

        g.MapPut("/{id}/rechnungsdaten", (string id, InvoiceCorrectionRequest req, DocumentStore store, DocumentProcessor proc, PortalAccess access, HttpContext ctx) =>
        {
            if (!PdfRechnung.Licensing.License.Current.EInvoice) return Results.Json(new { error = PdfRechnung.Licensing.License.EInvoiceMessage }, statusCode: 402);
            var a = access.For(ctx);
            if (!a.CanSend) return Forbidden();
            var v0 = store.Get(id);
            if (v0 is null) return Results.NotFound();
            if (!CorrectionsAllowed(v0)) return Results.BadRequest(new { error = "Der Beleg ist versendet, übergeben oder verworfen – keine Korrektur mehr möglich." });
            var orig = proc.OriginalCodes(id) ?? new ParsedCodes();
            var rows = orig.Table("ITEM_DETAILS").ToList();
            var printedInv = proc.InvoiceFrom(orig);
            var c = new InvoiceCorrections { ChangedBy = a.Name, ChangedAt = DateTimeOffset.Now };
            var changed = new List<string>();
            foreach (var (key, raw) in req.Fields ?? new())
            {
                var def = InvoiceCorrections.Editable.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (def is null || raw is null) continue;
                var val = raw.Trim();
                if (val == (orig.Field(def.Key) ?? "")) continue; // wie gedruckt → keine Korrektur
                c.Fields[def.Key] = val;
                changed.Add($"{def.Group} {def.Label}");
            }
            var cats = new[] { "Z", "E", "AE", "K", "G", "O" };
            foreach (var l in req.Lines ?? new())
            {
                if (l.Index < 0 || l.Index >= rows.Count) continue;
                var r = rows[l.Index];
                var lc = new LineCorrection();
                string? Diff(string? value, string key) => value is null || value.Trim() == (r.Get(key) ?? "") ? null : value.Trim();
                lc.Name = Diff(l.Name, "ITEM_NAME");
                lc.Description = Diff(l.Description, "ITEM_DESCRIPTION");
                lc.SellerId = Diff(l.SellerId, "ITEM_SELLER_ID");
                if (!string.IsNullOrWhiteSpace(l.UnitCode))
                {
                    var u = l.UnitCode.Trim().ToUpperInvariant();
                    if (!InvoiceData.IsUneceCode(u)) return Results.BadRequest(new { error = $"Position {r.Get("ITEM_LINE_ID")}: „{l.UnitCode}“ ist kein UN/ECE-Einheitencode." });
                    if (u != (printedInv.Lines.ElementAtOrDefault(l.Index)?.UnitCode ?? "")) lc.UnitCode = u;
                }
                if (!string.IsNullOrWhiteSpace(l.VatCategory))
                {
                    var cat = l.VatCategory.Trim().ToUpperInvariant();
                    var printedCat = (r.Get("ITEM_VAT_CATEGORY") ?? "S").ToUpperInvariant();
                    if (cat != printedCat)
                    {
                        if ((ControlCodeParser.Number(r.Get("ITEM_VAT_PERCENT")) ?? 0) != 0 || !cats.Contains(cat))
                            return Results.BadRequest(new { error = $"Position {r.Get("ITEM_LINE_ID")}: Die Steuerkategorie kann nur bei 0 % geändert werden (Z, E, AE, K, G, O)." });
                        lc.VatCategory = cat;
                    }
                }
                if (!lc.IsEmpty) { c.Lines[l.Index] = lc; changed.Add($"Position {r.Get("ITEM_LINE_ID") ?? (l.Index + 1).ToString()}"); }
            }
            store.Update(id, v =>
            {
                v.Corrections = c;
                v.History.Add(new HistoryEntry(DateTimeOffset.Now, a.Name, "korrektur",
                    changed.Count == 0 ? "Korrekturen der Rechnungsdaten entfernt (wie gedruckt)" : "Rechnungsdaten korrigiert: " + string.Join(", ", changed)));
                return true;
            });
            var after = proc.Recheck(id, a.Name);
            return Results.Ok(new { message = changed.Count == 0 ? "Keine Abweichung zum Druck – Korrekturen entfernt." : $"{changed.Count} Korrektur(en) gespeichert, E-Rechnung neu erzeugt und geprüft.", vorgang = after });
        });

        g.MapGet("/{id}/xml", (string id, DocumentStore store) =>
        {
            var v = store.Get(id);
            var path = v is null ? null : store.FilePath(id, DocumentStore.XmlFile);
            if (path is null) return Results.NotFound();
            var name = MailTemplate.SafeFileName(string.IsNullOrWhiteSpace(v!.Mail.FileName) ? v.VoucherId ?? id : v.Mail.FileName) + ".xml";
            return Results.File(path, "application/xml", name);
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
                // Sammelversand nur für versendbare Vorgänge (die Auswahl kann beim Admin auch andere enthalten – z. B. zum Löschen)
                if (vv.Status is not (VorgangStatus.Ready or VorgangStatus.Warning or VorgangStatus.SendError)) { results.Add(new { id, ok = false, message = $"{vv.VoucherId ?? id}: nicht versendbar ({vv.Status})." }); continue; }
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

    private static bool CorrectionsAllowed(Vorgang v) =>
        v.Status is not (VorgangStatus.Sent or VorgangStatus.Sending or VorgangStatus.Gotomaxx or VorgangStatus.Discarded);

    private static IResult Forbidden() => Results.Json(new { error = "Keine Berechtigung zum Versenden. Bitte an einen Administrator wenden." }, statusCode: 403);

    private static List<string> Clean(List<string>? l) =>
        (l ?? new()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
