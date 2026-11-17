using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;

namespace PdfRechnung.Monitoring;

/// <summary>
/// Benachrichtigung per Mail, wenn ein Problem länger als die eingestellte Wartezeit besteht und niemand reagiert.
/// Solange es besteht, wird nach der Wiederholzeit erneut gemeldet; ist es behoben, kommt (optional) „wieder in Ordnung“.
/// </summary>
public sealed class AlertService : BackgroundService
{
    private sealed class Entry
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTimeOffset Since { get; set; }
        public DateTimeOffset? Notified { get; set; }
    }

    private readonly HealthService _health;
    private readonly IOptionsMonitor<AlertOptions> _opt;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly Settings.SettingsStore _settings;
    private readonly ILogger<AlertService> _log;
    private readonly string _file;
    private readonly object _lock = new();
    private Dictionary<string, Entry> _state;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Letzte versendete Benachrichtigung bzw. letzter Fehler beim Versand (für die Einstellungsseite).</summary>
    public (DateTimeOffset? At, string? Text, string? Error) Last { get; private set; }

    public AlertService(HealthService health, IOptionsMonitor<AlertOptions> opt, IOptionsMonitor<MailOptions> mail, Settings.SettingsStore settings, ILogger<AlertService> log)
    {
        _health = health; _opt = opt; _mail = mail; _settings = settings; _log = log;
        _file = Path.Combine(settings.DataDirectory, "benachrichtigung.json");
        try { _state = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file), Json) ?? new() : new(); }
        catch (Exception) { _state = new(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (_opt.CurrentValue.Enabled) await CheckAsync(DateTimeOffset.Now, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Benachrichtigung"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Ein Durchlauf: fällige Probleme und behobene melden.</summary>
    public async Task CheckAsync(DateTimeOffset now, CancellationToken ct)
    {
        var o = _opt.CurrentValue;
        var report = _health.Report();
        var problems = report.Checks.Where(c => c.Alert && c.Status != HealthLevel.Ok).ToList();
        List<HealthCheck> due; List<(string Key, Entry E)> recovered;
        lock (_lock)
        {
            foreach (var p in problems)
            {
                var since = p.Since ?? now;
                if (!_state.TryGetValue(p.Key, out var e)) _state[p.Key] = e = new Entry { Since = since };
                e.Title = p.Title; e.Message = p.Message;
                if (since < e.Since) e.Since = since;
            }
            recovered = _state.Where(kv => problems.All(p => p.Key != kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var r in recovered) _state.Remove(r.Key);
            var delay = TimeSpan.FromMinutes(Math.Max(0, o.DelayMinutes));
            var repeat = o.RepeatMinutes > 0 ? TimeSpan.FromMinutes(o.RepeatMinutes) : (TimeSpan?)null;
            due = problems.Where(p =>
            {
                var e = _state[p.Key];
                if (now - e.Since < delay) return false;
                return e.Notified is null || (repeat is not null && now - e.Notified >= repeat);
            }).ToList();
            Save();
        }
        recovered = recovered.Where(r => r.E.Notified is not null).ToList();
        if (!Delivery.ScheduleEngine.Allowed(o.Days, o.From, o.To, now.LocalDateTime)) return;
        if (due.Count == 0 && (recovered.Count == 0 || !o.SendRecovery)) return;
        var others = problems.Where(p => due.All(d => d.Key != p.Key)).ToList();
        var (subject, text, html) = Compose(report, due, others, o.SendRecovery ? recovered.Select(r => (r.E.Title, r.E.Since)).ToList() : new());
        try
        {
            await SendAsync(o, subject, text, html, ct);
            lock (_lock) { foreach (var d in due) if (_state.TryGetValue(d.Key, out var e)) e.Notified = now; Save(); }
            Last = (now, subject, null);
            _log.LogInformation("Benachrichtigung gesendet: {Subject}", subject);
        }
        catch (Exception ex)
        {
            Last = (now, subject, ex.Message);
            _log.LogWarning(ex, "Benachrichtigung konnte nicht gesendet werden");
        }
    }

    /// <summary>Testmail mit dem aktuellen Zustand.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var o = _opt.CurrentValue;
        var report = _health.Report();
        var problems = report.Checks.Where(c => c.Status != HealthLevel.Ok).ToList();
        var (subject, text, html) = Compose(report, new(), problems, new(), test: true);
        await SendAsync(o, subject, text, html, ct);
        return $"Testmail an {o.Recipients.Trim()} gesendet.";
    }

    /// <summary>Aktuell bekannte Probleme mit Beginn und letzter Meldung (für die Einstellungsseite).</summary>
    public List<object> Open()
    {
        lock (_lock) return _state.Select(kv => (object)new { key = kv.Key, title = kv.Value.Title, message = kv.Value.Message, since = kv.Value.Since, notified = kv.Value.Notified }).ToList();
    }

    private async Task SendAsync(AlertOptions o, string subject, string text, string html, CancellationToken ct)
    {
        var to = (o.Recipients ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (to.Count == 0) throw new InvalidOperationException("Keine Empfänger für die Benachrichtigung eingetragen.");
        var m = _mail.CurrentValue;
        var mail = new OutgoingMail { Subject = subject, Body = text, HtmlBody = html };
        mail.To.AddRange(to);
        await MailSender.SendAsync(m, new MailSecrets(_settings.Secrets.Unprotect(m.Password), _settings.Secrets.Unprotect(m.GraphClientSecret)), mail, ct);
    }

    internal static (string Subject, string Text, string Html) Compose(HealthReport r, List<HealthCheck> due, List<HealthCheck> others,
        List<(string Title, DateTimeOffset Since)> recovered, bool test = false)
    {
        var subject = test ? $"Belegpost {r.Machine}: Testmail Benachrichtigung"
            : due.Count > 0 ? $"Belegpost {r.Machine}: {due.Count} Problem{(due.Count == 1 ? "" : "e")} ohne Reaktion"
            : $"Belegpost {r.Machine}: wieder in Ordnung";
        var t = new StringBuilder();
        var h = new StringBuilder("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1b2430\">");
        void Section(string title, IEnumerable<HealthCheck> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            t.AppendLine(title).AppendLine(new string('-', title.Length));
            h.Append($"<h3 style=\"margin:18px 0 6px\">{WebUtility.HtmlEncode(title)}</h3><table style=\"border-collapse:collapse\">");
            foreach (var c in list)
            {
                var since = c.Since is { } s ? $"seit {s:dd.MM.yyyy HH:mm}" : "";
                t.AppendLine($"* {c.Title} {since}").AppendLine($"  {c.Message}");
                var color = c.Status == HealthLevel.Error ? "#b42318" : "#b54708";
                h.Append($"<tr><td style=\"padding:4px 10px 4px 0;vertical-align:top;color:{color};font-weight:600;white-space:nowrap\">{WebUtility.HtmlEncode(c.Title)}</td>" +
                         $"<td style=\"padding:4px 0\">{WebUtility.HtmlEncode(c.Message)}<br><span style=\"color:#667085\">{since}</span></td></tr>");
            }
            t.AppendLine();
            h.Append("</table>");
        }
        if (test) { t.AppendLine("Testmail der Benachrichtigung – so sehen Meldungen aus.").AppendLine(); h.Append("<p>Testmail der Benachrichtigung – so sehen Meldungen aus.</p>"); }
        Section("Probleme ohne Reaktion", due);
        Section(test ? "Aktueller Zustand" : "Weitere offene Punkte", others);
        if (recovered.Count > 0)
        {
            t.AppendLine("Wieder in Ordnung").AppendLine("-----------------");
            h.Append("<h3 style=\"margin:18px 0 6px\">Wieder in Ordnung</h3><ul>");
            foreach (var (title, since) in recovered) { t.AppendLine($"* {title} (Problem seit {since:dd.MM.yyyy HH:mm})"); h.Append($"<li>{WebUtility.HtmlEncode(title)} <span style=\"color:#667085\">(Problem seit {since:dd.MM.yyyy HH:mm})</span></li>"); }
            h.Append("</ul>");
            t.AppendLine();
        }
        if (test && others.Count == 0) { t.AppendLine("Zurzeit ist alles in Ordnung."); h.Append("<p>Zurzeit ist alles in Ordnung.</p>"); }
        var foot = $"Dienst Belegpost {r.Version} auf {r.Machine}, Stand {r.At:dd.MM.yyyy HH:mm}. Einstellungen → Benachrichtigung.";
        t.AppendLine(foot);
        h.Append($"<p style=\"color:#667085;font-size:12px;margin-top:20px\">{WebUtility.HtmlEncode(foot)}</p></div>");
        return (subject, t.ToString(), h.ToString());
    }

    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            File.Move(tmp, _file, true);
        }
        catch (Exception) { }
    }
}
