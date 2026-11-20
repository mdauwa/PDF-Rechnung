using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

/// <summary>
/// Outlook im Web: Belegpost legt die fertige Mail (CI-Text, Logo, PDF, Anlagen) über Microsoft 365 als Entwurf im Postfach
/// des Benutzers an und liefert den Link, den Portal, Melder (Windows, Mac, Linux) oder die Sitzung am Server im Browser öffnen.
/// Der Benutzer sendet selbst. Im Hintergrund prüft der Dienst alle 2 Minuten „Gesendete Elemente“ und meldet dann 110.
/// Voraussetzung: Microsoft 365 ist eingerichtet und die App-Registrierung hat die Anwendungsberechtigung Mail.ReadWrite.
/// </summary>
public sealed class OutlookWebService : BackgroundService
{
    private readonly DocumentStore _store;
    private readonly SettingsStore _settings;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly SendService _send;
    private readonly ILogger<OutlookWebService> _log;

    public OutlookWebService(DocumentStore store, SettingsStore settings, IOptionsMonitor<MailOptions> mail, SendService send, ILogger<OutlookWebService> log)
    {
        _store = store; _settings = settings; _mail = mail; _send = send; _log = log;
    }

    public sealed record Result(bool Ok, string Message, string? Url);

    public async Task<Result> OpenAsync(Vorgang v, PortalUser user, string by, CancellationToken ct)
    {
        var mailbox = (user.Email ?? "").Trim();
        if (mailbox.Length == 0 || !mailbox.Contains('@'))
            return new Result(false, $"Für {user.Name} ist keine E-Mail-Adresse hinterlegt – sie bestimmt das Postfach für Outlook im Web (Einstellungen → Benutzer).", null);
        if (!v.Mail.Enabled) return new Result(false, "Für diesen Beleg ist kein E-Mail-Versand vorgesehen (@@ml0@@).", null);
        var (mail, err) = await _send.BuildMail(v, ct);
        if (mail is null) return new Result(false, err ?? "Die Mail konnte nicht erstellt werden.", null);
        var o = _mail.CurrentValue;
        try
        {
            var draft = await GraphMailSender.CreateDraftAsync(o, _settings.Secrets.Unprotect(o.GraphClientSecret), mailbox, mail,
                string.IsNullOrWhiteSpace(v.Mail.FromAddress) ? null : v.Mail.FromAddress, ct);
            _store.Update(v.Id, x =>
            {
                x.OutlookWebMailbox = mailbox;
                x.OutlookWebMessageId = draft.InternetMessageId;
                x.OutlookWebDraftAt = DateTimeOffset.Now;
                x.OutlookConfirmedAt = null;
                return true;
            });
            _send.MarkOutlook(v.Id, by);
            return new Result(true, "Entwurf in Outlook im Web angelegt.", draft.WebLink);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Outlook im Web: Entwurf für {Id} nicht angelegt", v.Id);
            _store.AddEvent(v.Id, StatusCode.OutlookFehler, "Outlook im Web", by, ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
            return new Result(false, ex.Message, null);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CheckSentAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogInformation(ex, "Outlook im Web: Versandprüfung"); }
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Offene Outlook-Web-Entwürfe (bis 14 Tage) in „Gesendete Elemente“ suchen.</summary>
    public async Task CheckSentAsync(CancellationToken ct)
    {
        var open = _store.Query(new VorgangFilter { SeeAll = true, Statuses = new[] { VorgangStatus.Outlook }, Limit = 500 })
            .Where(v => v.OutlookWebMessageId is not null && v.OutlookWebMailbox is not null && v.OutlookConfirmedAt is null
                && v.OutlookWebDraftAt > DateTimeOffset.Now.AddDays(-14))
            .ToList();
        if (open.Count == 0) return;
        var o = _mail.CurrentValue;
        var secret = _settings.Secrets.Unprotect(o.GraphClientSecret);
        foreach (var v in open)
        {
            var at = await GraphMailSender.FindSentAsync(o, secret, v.OutlookWebMailbox!, v.OutlookWebMessageId!, ct);
            if (at is not null) _send.ConfirmOutlookSent(v.Id, v.SentBy ?? $"{v.OwnerName ?? "Benutzer"} (Outlook im Web)", at);
        }
    }
}
