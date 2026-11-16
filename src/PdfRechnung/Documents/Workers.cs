using Microsoft.Extensions.Options;
using PdfRechnung.Settings;

namespace PdfRechnung.Documents;

/// <summary>Versendet einen Vorgang per E-Mail (manuell aus dem Portal oder durch den Autoversand).</summary>
public sealed class SendService
{
    private readonly DocumentStore _store;
    private readonly SettingsStore _settings;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly IOptionsMonitor<StorageOptions> _storage;
    private readonly ILogger<SendService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IOptionsMonitor<MailTemplateOptions> _templates;
    private readonly IOptionsMonitor<MandantOptions>? _mandanten;
    private readonly IServiceProvider? _sp;

    public SendService(DocumentStore store, SettingsStore settings, IOptionsMonitor<MailOptions> mail, IOptionsMonitor<StorageOptions> storage,
        IOptionsMonitor<MailTemplateOptions> templates, ILogger<SendService> log, IOptionsMonitor<MandantOptions>? mandanten = null,
        IServiceProvider? sp = null)
    {
        _mandanten = mandanten; _sp = sp;
        _store = store; _settings = settings; _mail = mail; _storage = storage; _templates = templates; _log = log;
    }

    /// <summary>Mail eines Vorgangs gestalten (für Versand und Vorschau im Portal).</summary>
    public static RenderedMail Render(Vorgang v, MailTemplateOptions templates, string dataDirectory)
    {
        if (v.Mail.Plain) return new RenderedMail("", v.Mail.Body, new List<InlineImage>()); // normale E-Mail: Text wie übergeben
        var design = v.Mail.Template is null ? null
            : templates.Items.FirstOrDefault(x => string.Equals(x.Name, v.Mail.Template, StringComparison.OrdinalIgnoreCase));
        var values = v.Mail.Variables.Count > 0 ? v.Mail.Variables : Placeholders.Build(v, null, MailTemplate.SafeFileName(v.Mail.FileName) + ".pdf");
        return MailDesigner.Render(design, v.Mail.Body, values, MailDesigner.Directory(dataDirectory), MailFacts.From(v), v.Mail.SignatureText);
    }

    public RenderedMail Render(Vorgang v) => Render(v, _templates.CurrentValue, _settings.DataDirectory);

    /// <summary>
    /// Versendet den Vorgang. force = auch bei Fehlern (nur manuell, mit Bestätigung).
    /// Hat der Kunde eigene Versandwege (Kunden-Verwaltung), werden diese ausgeführt – sonst per E-Mail wie eingestellt.
    /// </summary>
    public async Task<(bool Ok, string Message)> SendAsync(string id, string by, bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var o = _mail.CurrentValue;
            var v = _store.Get(id);
            if (v is null) return (false, "Vorgang nicht gefunden.");
            if (v.Status == VorgangStatus.Discarded) return (false, "Der Vorgang ist verworfen.");
            if (v.Status == VorgangStatus.Error && !force) return (false, "Der Vorgang hat Fehler. Bitte zuerst beheben oder „Trotzdem senden“ wählen.");

            // Versandwege des Kunden (neu planen, solange noch nichts erfolgreich war; „erneut senden“ = alle noch einmal)
            var delivery = _sp?.GetService<PdfRechnung.Delivery.DeliveryService>();
            if (delivery is not null)
            {
                var resend = v.Status == VorgangStatus.Sent;
                if (resend || v.Deliveries.All(d => d.Status != "ok"))
                {
                    var plan = delivery.Plan(v);
                    if (resend) v.Deliveries.Clear();
                    PdfRechnung.Delivery.DeliveryPlanner.Apply(v, plan);
                    if (plan.NoDelivery) return (false, $"Für {plan.Customer!.Label} ist laut {plan.Rule} kein Versand vorgesehen.");
                    _store.Update(id, x => { x.CustomerId = v.CustomerId; x.CustomerName = v.CustomerName; x.CustomerReason = v.CustomerReason; x.Deliveries = v.Deliveries; return true; });
                }
                if (v.Deliveries.Count > 0) return await SendDeliveriesAsync(v, delivery, by, ct);
            }

            if (!o.Enabled) return (false, "Der E-Mail-Versand ist nicht eingerichtet (Einstellungen → E-Mail).");
            if (v.Mail.To.Count == 0) return (false, "Kein Empfänger eingetragen.");

            var (mail, buildError) = await BuildMail(v, ct);
            if (mail is null) return (false, buildError!);

            _store.Update(id, x => { x.Status = VorgangStatus.Sending; x.SendAttempts++; return true; });
            try
            {
                await MailSender.SendAsync(o, new MailSecrets(_settings.Secrets.Unprotect(o.Password), _settings.Secrets.Unprotect(o.GraphClientSecret)), mail, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Versand von {Id} fehlgeschlagen", id);
                _store.Update(id, x =>
                {
                    x.Status = VorgangStatus.SendError; x.SendError = ex.Message; x.SendChannel = "Mail";
                    x.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "versandfehler", ex.Message));
                    return true;
                });
                return (false, "Versand fehlgeschlagen: " + ex.Message);
            }

            var now = DateTimeOffset.Now;
            _store.Update(id, x =>
            {
                x.Status = VorgangStatus.Sent; x.SentAt = now; x.SentBy = by; x.SendError = null; x.SendChannel = "Mail";
                x.History.Add(new HistoryEntry(now, by, "versendet", $"an {string.Join(", ", x.Mail.To)}{(x.Mail.Cc.Count > 0 ? $" (CC {string.Join(", ", x.Mail.Cc)})" : "")}"));
                return true;
            });
            Export(id, v);
            _log.LogInformation("Vorgang {Id} versendet an {To} durch {By}", id, string.Join(", ", v.Mail.To), by);
            return (true, $"Versendet an {string.Join(", ", v.Mail.To)}.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Alle noch offenen bzw. fehlgeschlagenen Versandwege des Kunden ausführen.</summary>
    private async Task<(bool Ok, string Message)> SendDeliveriesAsync(Vorgang v, PdfRechnung.Delivery.DeliveryService delivery, string by, CancellationToken ct)
    {
        var customer = v.CustomerId is null ? null : delivery.Customers.Get(v.CustomerId);
        if (customer is null) return (false, "Der Kunde dieses Belegs ist nicht mehr angelegt – bitte „Neu prüfen“.");
        _store.Update(v.Id, x => { x.Status = VorgangStatus.Sending; x.SendAttempts++; return true; });
        var o = _mail.CurrentValue;
        var results = new List<string>();
        foreach (var d in v.Deliveries.Where(d => d.Status != "ok"))
        {
            var ch = customer.Channels.FirstOrDefault(c => c.Id == d.ChannelId);
            d.Attempts++; d.At = DateTimeOffset.Now;
            try
            {
                if (ch is null) throw new InvalidOperationException("Versandweg beim Kunden nicht mehr vorhanden.");
                if (ch.Settings.Type == PdfRechnung.Delivery.ChannelType.Mail)
                {
                    var s = delivery.Effective(ch.Settings);
                    if (!o.Enabled) throw new InvalidOperationException("Der E-Mail-Versand ist nicht eingerichtet (Einstellungen → E-Mail).");
                    var template = v.Mail.Template;
                    if (!string.IsNullOrWhiteSpace(s.Template)) v.Mail.Template = s.Template;
                    var (mail, err) = await BuildMail(v, ct);
                    v.Mail.Template = template;
                    if (mail is null) throw new InvalidOperationException(err);
                    if (s.To.Count > 0) { mail.To.Clear(); mail.To.AddRange(s.To); }
                    if (s.Cc.Count > 0) { mail.Cc.Clear(); mail.Cc.AddRange(s.Cc); }
                    if (s.Bcc.Count > 0) { mail.Bcc.Clear(); mail.Bcc.AddRange(s.Bcc); }
                    if (mail.To.Count == 0) throw new InvalidOperationException("Kein Empfänger – weder im Beleg noch beim Kunden.");
                    // Inhalt „PDF“ = wie im Beleg (PDF + Anlagen); sonst genau die gewählten Dateien
                    if (!string.IsNullOrEmpty(s.Content) && s.Content != PdfRechnung.Delivery.ChannelContent.Pdf)
                    {
                        var files = await delivery.FilesAsync(v, s, ct);
                        mail.Attachments.Clear();
                        foreach (var f in files) mail.Attachments.Add((f.Name, f.Data, f.ContentType));
                    }
                    await MailSender.SendAsync(o, new MailSecrets(_settings.Secrets.Unprotect(o.Password), _settings.Secrets.Unprotect(o.GraphClientSecret)), mail, ct);
                    d.Target = string.Join(", ", mail.To);
                }
                else d.Target = await delivery.DeliverAsync(v, ch, ct);
                d.Status = "ok"; d.Message = null;
                results.Add($"{d.Label}: ok");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Versandweg {Channel} für {Id} fehlgeschlagen", d.Label, v.Id);
                d.Status = "fehler"; d.Message = ex.Message;
                results.Add($"{d.Label}: {ex.Message}");
            }
        }
        var now = DateTimeOffset.Now;
        var allOk = v.Deliveries.All(d => d.Status == "ok");
        _store.Update(v.Id, x =>
        {
            x.Deliveries = v.Deliveries;
            x.SendChannel = string.Join(", ", v.Deliveries.Select(d => d.Label));
            foreach (var d in v.Deliveries.Where(d => d.At >= now.AddMinutes(-10)))
                x.History.Add(new HistoryEntry(d.At ?? now, by, d.Status == "ok" ? "versendet" : "versandfehler",
                    d.Status == "ok" ? $"{d.Label} → {d.Target}" : $"{d.Label}: {d.Message}"));
            if (allOk) { x.Status = VorgangStatus.Sent; x.SentAt = now; x.SentBy = by; x.SendError = null; }
            else { x.Status = VorgangStatus.SendError; x.SendError = string.Join("; ", v.Deliveries.Where(d => d.Status != "ok").Select(d => $"{d.Label}: {d.Message}")); }
            return true;
        });
        if (allOk) Export(v.Id, v);
        return allOk ? (true, "Versendet – " + string.Join(", ", v.Deliveries.Select(d => d.Label)) + ".")
            : (false, "Nicht alle Versandwege erfolgreich: " + string.Join("; ", results));
    }

    public async Task<(OutgoingMail? Mail, string? Error)> BuildMail(Vorgang v, CancellationToken ct)
    {
        var rendered = Render(v);
        var mail = new OutgoingMail
        {
            Subject = v.Mail.Subject, Body = rendered.Text, HtmlBody = string.IsNullOrEmpty(rendered.Html) ? null : rendered.Html,
            FromAddress = v.Mail.FromAddress, FromName = v.Mail.FromName, ReplyTo = v.Mail.ReplyTo
        };
        mail.Images.AddRange(rendered.Images);
        mail.To.AddRange(v.Mail.To); mail.Cc.AddRange(v.Mail.Cc); mail.Bcc.AddRange(v.Mail.Bcc);
        if (!v.Mail.NoDocument)
        {
            var pdf = _store.FilePath(v.Id, DocumentStore.SendFile);
            if (pdf is null) return (null, "Das Versand-PDF fehlt – bitte „Neu prüfen“.");
            mail.Attachments.Add((MailTemplate.SafeFileName(v.Mail.FileName) + ".pdf", await File.ReadAllBytesAsync(pdf, ct), "application/pdf"));
        }
        foreach (var a in v.Mail.Attachments)
        {
            if (!File.Exists(a)) return (null, $"Anlage nicht gefunden: {a}");
            mail.Attachments.Add((Path.GetFileName(a), await File.ReadAllBytesAsync(a, ct), MimeType(a)));
        }
        return (mail, null);
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".txt" => "text/plain",
        ".xml" => "application/xml", ".zip" => "application/zip", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", _ => "application/octet-stream"
    };

    /// <summary>Vom Melder in Outlook geöffnet: nicht mehr automatisch versenden.</summary>
    public void MarkOutlook(string id, string by) => _store.Update(id, x =>
    {
        if (x.Status is VorgangStatus.Sent or VorgangStatus.Sending) return false;
        x.Status = VorgangStatus.Outlook; x.SentBy = by; x.OutlookHandedAt = DateTimeOffset.Now;
        x.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "outlook", "Als E-Mail-Entwurf in Outlook geöffnet – Versand durch den Benutzer"));
        return true;
    });

    /// <summary>Kopie in den Exportordner (Archiv/DMS), falls eingestellt.</summary>
    private void Export(string id, Vorgang v)
    {
        var folder = _mandanten?.CurrentValue.Find(v.Mandant) is { ExportFolder.Length: > 0 } m ? m.ExportFolder.Trim() : _storage.CurrentValue.ExportFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            var pdf = _store.FilePath(id, DocumentStore.SendFile);
            if (pdf is not null)
            {
                var name = MailTemplate.SafeFileName(v.Mail.FileName);
                var target = Path.Combine(folder, name + ".pdf");
                if (File.Exists(target)) target = Path.Combine(folder, $"{name}_{v.Id}.pdf");
                File.Copy(pdf, target);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Export von {Id} nach {Folder} fehlgeschlagen", id, folder); }
    }
}

/// <summary>
/// Überwacht den Eingangsordner. Jede fertig geschriebene PDF-Datei wird zu einem Vorgang verarbeitet und danach gelöscht;
/// nicht lesbare Dateien landen im Unterordner „fehler“.
/// </summary>
public sealed class InboxWatcher : BackgroundService
{
    private readonly IOptionsMonitor<InboxOptions> _opt;
    private readonly IOptionsMonitor<MandantOptions>? _mandanten;
    private readonly SettingsStore _settings;
    private readonly DocumentProcessor _processor;
    private readonly ILogger<InboxWatcher> _log;
    private readonly Dictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);

    public InboxWatcher(IOptionsMonitor<InboxOptions> opt, SettingsStore settings, DocumentProcessor processor, ILogger<InboxWatcher> log,
        IOptionsMonitor<MandantOptions>? mandanten = null)
    {
        _mandanten = mandanten;
        _opt = opt; _settings = settings; _processor = processor; _log = log;
    }

    public static string ResolveFolder(InboxOptions o, SettingsStore s) =>
        string.IsNullOrWhiteSpace(o.Folder) ? Path.Combine(s.DataDirectory, "eingang") : o.Folder.Trim();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (_opt.CurrentValue.Enabled) Scan(); }
            catch (Exception ex) { _log.LogWarning(ex, "Eingangsordner konnte nicht gelesen werden"); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { }
        }
    }

    private void Scan()
    {
        Scan(ResolveFolder(_opt.CurrentValue, _settings), null);
        // Eingangsordner der Mandanten: PDFs von dort gehören zum jeweiligen Mandanten
        foreach (var m in _mandanten?.CurrentValue.Items ?? new())
            if (!string.IsNullOrWhiteSpace(m.InboxFolder) && MandantOptions.Normalize(m.Number) is not null)
                try { Scan(m.InboxFolder.Trim(), m); }
                catch (Exception ex) { _log.LogWarning(ex, "Eingangsordner von Mandant {Mandant} konnte nicht gelesen werden", m.Number); }
    }

    private void Scan(string folder, Mandant? mandant)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf"))
        {
            var info = new FileInfo(file);
            // erst verarbeiten, wenn die Größe zwischen zwei Durchläufen gleich bleibt (Datei fertig geschrieben)
            if (!_seen.TryGetValue(file, out var size) || size != info.Length || info.Length == 0) { _seen[file] = info.Length; continue; }
            byte[] data;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                data = ms.ToArray();
            }
            catch (IOException) { continue; } // noch in Benutzung
            _seen.Remove(file);
            try
            {
                _processor.Process(data, "Eingang", info.Name,
                    originMandant: mandant?.Number, originMandantReason: mandant is null ? null : $"Eingangsordner des Mandanten {mandant.Label}");
                File.Delete(file);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Datei {File} konnte nicht verarbeitet werden", file);
                var err = Path.Combine(folder, "fehler");
                Directory.CreateDirectory(err);
                File.Move(file, Path.Combine(err, $"{DateTime.Now:yyyyMMdd-HHmmss}_{info.Name}"), overwrite: true);
            }
        }
    }
}

/// <summary>Autoversand: versendet korrekte Vorgänge nach der eingestellten Wartezeit. Fehlerhafte Vorgänge nie.</summary>
public sealed class AutoSendWorker : BackgroundService
{
    private readonly IOptionsMonitor<MailOptions> _opt;
    private readonly DocumentStore _store;
    private readonly SendService _send;
    private readonly ILogger<AutoSendWorker> _log;
    private readonly Settings.UserStore? _users;

    public AutoSendWorker(IOptionsMonitor<MailOptions> opt, DocumentStore store, SendService send, ILogger<AutoSendWorker> log, Settings.UserStore? users = null)
    {
        _opt = opt; _store = store; _send = send; _log = log; _users = users;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnce(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Autoversand"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { }
        }
    }

    public async Task RunOnce(CancellationToken ct)
    {
        var o = _opt.CurrentValue;
        if (!o.Enabled) return;
        var due = DateTimeOffset.Now.AddMinutes(-Math.Max(0, o.AutoSendDelayMinutes));
        var statuses = o.AutoSendWithWarnings ? new[] { VorgangStatus.Ready, VorgangStatus.Warning } : new[] { VorgangStatus.Ready };
        var candidates = _store.Query(new VorgangFilter { SeeAll = true, Statuses = statuses, Before = due.AddSeconds(1), Limit = 500 }).Where(v =>
            (v.Status == VorgangStatus.Ready || (o.AutoSendWithWarnings && v.Status == VorgangStatus.Warning))
            && v.SendAttempts == 0 && v.CreatedAt <= due && (v.Mail.To.Count > 0 || v.Deliveries.Count > 0) && !v.Mail.Plain // normale E-Mails nie automatisch
            && IsAutomatic(v, o)).ToList();
        foreach (var v in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var (ok, msg) = await _send.SendAsync(v.Id, "Autoversand", force: false, ct);
            if (!ok) _log.LogWarning("Autoversand {Id}: {Msg}", v.Id, msg);
        }
    }

    /// <summary>
    /// Versandweg (Regel bzw. @@pw@@): „direkt“ = immer automatisch, „wennok“ = automatisch nur ohne Warnungen und Fehler,
    /// „freigabe“/„outlook“ = nie;
    /// sonst wie eingestellt – außer der Benutzer versendet selbst über Outlook.
    /// </summary>
    private bool IsAutomatic(Vorgang v, MailOptions o) => v.Mail.Route switch
    {
        MailRoute.Direct => true,
        MailRoute.AutoIfOk => v.Status == VorgangStatus.Ready,
        MailRoute.Standard => o.AutoSend && !(_users?.UsesOutlook(v.OwnerUserId) ?? false),
        _ => false
    };
}

/// <summary>Löscht abgeschlossene Vorgänge (versendet, verworfen, ohne Versand) nach der Aufbewahrungsdauer.</summary>
public sealed class RetentionWorker : BackgroundService
{
    private readonly IOptionsMonitor<StorageOptions> _opt;
    private readonly DocumentStore _store;
    private readonly ILogger<RetentionWorker> _log;

    public RetentionWorker(IOptionsMonitor<StorageOptions> opt, DocumentStore store, ILogger<RetentionWorker> log)
    {
        _opt = opt; _store = store; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var days = _opt.CurrentValue.RetentionDays;
            if (days > 0)
            {
                var limit = DateTimeOffset.Now.AddDays(-days);
                var old = _store.FinalBefore(limit);
                foreach (var id in old) _store.Delete(id);
                if (old.Count > 0) _log.LogInformation("{Count} Vorgänge älter als {Days} Tage gelöscht", old.Count, days);
            }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { }
        }
    }
}
