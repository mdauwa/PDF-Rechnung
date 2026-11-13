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

    public SendService(DocumentStore store, SettingsStore settings, IOptionsMonitor<MailOptions> mail, IOptionsMonitor<StorageOptions> storage,
        IOptionsMonitor<MailTemplateOptions> templates, ILogger<SendService> log)
    {
        _store = store; _settings = settings; _mail = mail; _storage = storage; _templates = templates; _log = log;
    }

    /// <summary>Mail eines Vorgangs gestalten (für Versand und Vorschau im Portal).</summary>
    public static RenderedMail Render(Vorgang v, MailTemplateOptions templates, string dataDirectory)
    {
        var design = v.Mail.Template is null ? null
            : templates.Items.FirstOrDefault(x => string.Equals(x.Name, v.Mail.Template, StringComparison.OrdinalIgnoreCase));
        var values = v.Mail.Variables.Count > 0 ? v.Mail.Variables : Placeholders.Build(v, null, MailTemplate.SafeFileName(v.Mail.FileName) + ".pdf");
        return MailDesigner.Render(design, v.Mail.Body, values, MailDesigner.Directory(dataDirectory), MailFacts.From(v));
    }

    public RenderedMail Render(Vorgang v) => Render(v, _templates.CurrentValue, _settings.DataDirectory);

    /// <summary>Versendet den Vorgang. force = auch bei Fehlern (nur manuell, mit Bestätigung).</summary>
    public async Task<(bool Ok, string Message)> SendAsync(string id, string by, bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var o = _mail.CurrentValue;
            var v = _store.Get(id);
            if (v is null) return (false, "Vorgang nicht gefunden.");
            if (!o.Enabled) return (false, "Der E-Mail-Versand ist nicht eingerichtet (Einstellungen → E-Mail).");
            if (v.Status == VorgangStatus.Discarded) return (false, "Der Vorgang ist verworfen.");
            if (v.Status == VorgangStatus.Error && !force) return (false, "Der Vorgang hat Fehler. Bitte zuerst beheben oder „Trotzdem senden“ wählen.");
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
                    x.Status = VorgangStatus.SendError; x.SendError = ex.Message;
                    x.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "versandfehler", ex.Message));
                    return true;
                });
                return (false, "Versand fehlgeschlagen: " + ex.Message);
            }

            var now = DateTimeOffset.Now;
            _store.Update(id, x =>
            {
                x.Status = VorgangStatus.Sent; x.SentAt = now; x.SentBy = by; x.SendError = null;
                x.History.Add(new HistoryEntry(now, by, "versendet", $"an {string.Join(", ", x.Mail.To)}{(x.Mail.Cc.Count > 0 ? $" (CC {string.Join(", ", x.Mail.Cc)})" : "")}"));
                return true;
            });
            Export(id, v);
            _log.LogInformation("Vorgang {Id} versendet an {To} durch {By}", id, string.Join(", ", v.Mail.To), by);
            return (true, $"Versendet an {string.Join(", ", v.Mail.To)}.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Fertige Mail eines Vorgangs (HTML, Logo, Beleg und Anlagen) – für den Versand und für Outlook über den Melder.</summary>
    public async Task<(OutgoingMail? Mail, string? Error)> BuildMail(Vorgang v, CancellationToken ct)
    {
        var rendered = Render(v);
        var mail = new OutgoingMail
        {
            Subject = v.Mail.Subject, Body = rendered.Text, HtmlBody = rendered.Html,
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
        x.Status = VorgangStatus.Outlook; x.SentBy = by;
        x.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "outlook", "Als E-Mail-Entwurf in Outlook geöffnet – Versand durch den Benutzer"));
        return true;
    });

    /// <summary>Kopie in den Exportordner (Archiv/DMS), falls eingestellt.</summary>
    private void Export(string id, Vorgang v)
    {
        var folder = _storage.CurrentValue.ExportFolder;
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
    private readonly SettingsStore _settings;
    private readonly DocumentProcessor _processor;
    private readonly ILogger<InboxWatcher> _log;
    private readonly Dictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);

    public InboxWatcher(IOptionsMonitor<InboxOptions> opt, SettingsStore settings, DocumentProcessor processor, ILogger<InboxWatcher> log)
    {
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
        var folder = ResolveFolder(_opt.CurrentValue, _settings);
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
                _processor.Process(data, "Eingang", info.Name);
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
        if (!o.Enabled || !o.AutoSend) return;
        var due = DateTimeOffset.Now.AddMinutes(-Math.Max(0, o.AutoSendDelayMinutes));
        var candidates = _store.All().Where(v =>
            (v.Status == VorgangStatus.Ready || (o.AutoSendWithWarnings && v.Status == VorgangStatus.Warning))
            && v.SendAttempts == 0 && v.CreatedAt <= due && v.Mail.To.Count > 0
            && !(_users?.UsesOutlook(v.OwnerUserId) ?? false)).ToList(); // Benutzer versendet selbst über Outlook
        foreach (var v in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var (ok, msg) = await _send.SendAsync(v.Id, "Autoversand", force: false, ct);
            if (!ok) _log.LogWarning("Autoversand {Id}: {Msg}", v.Id, msg);
        }
    }
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
                var old = _store.All().Where(v => v.CreatedAt < limit && VorgangStatus.IsFinal(v.Status)).ToList();
                foreach (var v in old) _store.Delete(v.Id);
                if (old.Count > 0) _log.LogInformation("{Count} Vorgänge älter als {Days} Tage gelöscht", old.Count, days);
            }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { }
        }
    }
}
