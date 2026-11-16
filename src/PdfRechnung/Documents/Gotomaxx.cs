using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace PdfRechnung.Documents;

/// <summary>
/// Weiterleitung an einen gotomaxx-PDFMAILER-Drucker: Der Beleg wird als echter Windows-Druckauftrag an den
/// PDFMAILER-Drucker geschickt, damit gotomaxx ihn wie einen ERP-Ausdruck verarbeitet.
/// Die Belegpost-eigenen Steuerzeichen (nur für das Portal) werden entfernt, alle anderen unverändert weitergegeben.
/// </summary>
public static class GotomaxxCodes
{
    /// <summary>Steuerzeichen, die nur Belegpost/das Portal betreffen und gotomaxx nicht kennt.</summary>
    public static readonly IReadOnlySet<string> PortalOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pb", // ERP-Bedienernummer
        "pi", // ERP-Belegindex
        "pu", // wer hat gedruckt
        "pv", // eigene Platzhalter
        "pt", // Mail-Vorlage
        "pf", // Absender
        "pg", // Weiterleitung an gotomaxx (dieses Steuerzeichen)
        "ps", // Signatur
        "pm", // Mandant
        "pw"  // Versandweg
    };

    /// <summary>Belegpost-Erweiterung in <c>@@xi@@</c>: <c>@@xi ERP_VOUCHER_INDEX=…@@</c>.</summary>
    public const string ErpVoucherIndexField = "ERP_VOUCHER_INDEX";

    public static bool IsPortalOnly(ControlCode c)
    {
        if (PortalOnly.Contains(c.Code)) return true;
        if (c.Code.Equals("xi", StringComparison.OrdinalIgnoreCase))
        {
            var t = c.Value.Trim();
            var i = t.IndexOf('=');
            return i > 0 && t[..i].Trim().Equals(ErpVoucherIndexField, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Steuerzeichen für gotomaxx in der gedruckten Reihenfolge (ohne Portal-Steuerzeichen).</summary>
    public static List<ControlCode> Forward(IEnumerable<ControlCode> codes) => codes.Where(c => !IsPortalOnly(c)).ToList();

    /// <summary>Gedruckte Form eines Steuerzeichens: <c>@@xxWert@@</c> (Wert wie gelesen, Pfade ohne Pfadzuordnung).</summary>
    public static string Line(ControlCode c) => "@@" + c.Code + c.Value + "@@";

    /// <summary>
    /// Soll der Beleg an gotomaxx? null = nein, "" = Standarddrucker aus den Einstellungen, sonst Druckername.
    /// <c>@@pg@@</c> = Standarddrucker, <c>@@pgName@@</c> = dieser Drucker, <c>@@pg0@@</c> = nicht weiterleiten (auch wenn „immer“ eingestellt ist).
    /// </summary>
    public static string? Target(ParsedCodes p, GotomaxxOptions o)
    {
        if (!o.Enabled) return null;
        if (p.GotomaxxSet) return p.GotomaxxPrinter == "0" ? null : p.GotomaxxPrinter ?? "";
        return o.Always ? "" : null;
    }

    /// <summary>Druckauftrag aus einem Vorgang (Steuerzeichen je Seite).</summary>
    public static GotomaxxJob Job(Vorgang v, string printer, GotomaxxOptions o) => new()
    {
        Printer = printer,
        DocumentName = !string.IsNullOrWhiteSpace(v.Mail.FileName) ? v.Mail.FileName : v.OriginalFileName ?? v.Id,
        FontSize = o.CodeFontSize is > 0 and <= 24 ? o.CodeFontSize : 2,
        Visible = o.CodesVisible,
        Codes = Forward(v.Codes).Select(c => new GotomaxxJobCode(Math.Max(1, c.Page), Line(c))).ToList()
    };
}

public sealed record GotomaxxJobCode(int Page, string Text);

/// <summary>Ein Druckauftrag an gotomaxx (wird als auftrag.json auch an die Benutzersitzung übergeben).</summary>
public sealed class GotomaxxJob
{
    public string Printer { get; set; } = "";
    public string DocumentName { get; set; } = "";
    /// <summary>Schriftgröße der neu gedruckten Steuerzeichen in Punkt.</summary>
    public double FontSize { get; set; } = 2;
    /// <summary>Steuerzeichen schwarz statt weiß drucken (zum Testen).</summary>
    public bool Visible { get; set; }
    public List<GotomaxxJobCode> Codes { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>Druckt ein PDF auf einen Windows-Drucker (direkt oder in der Sitzung des Benutzers).</summary>
public interface IGotomaxxPrinter
{
    /// <summary>Installierte Drucker am Server.</summary>
    IReadOnlyList<string> Printers();

    /// <param name="windowsUsers">Mögliche Windows-Anmeldenamen des Benutzers (für den Druck in seiner Sitzung).</param>
    /// <param name="clientName">RDP-Client des Druckauftrags (bevorzugte Sitzung, wenn der Benutzer mehrfach angemeldet ist).</param>
    /// <returns>Beschreibung, wie gedruckt wurde (für den Verlauf).</returns>
    Task<string> PrintAsync(GotomaxxJob job, byte[] pdf, IReadOnlyCollection<string> windowsUsers, string? clientName, GotomaxxOptions o, CancellationToken ct);
}

/// <summary>Andere Betriebssysteme (Entwicklung/Tests): Drucken ist nicht möglich.</summary>
public sealed class UnsupportedGotomaxxPrinter : IGotomaxxPrinter
{
    public IReadOnlyList<string> Printers() => Array.Empty<string>();
    public Task<string> PrintAsync(GotomaxxJob job, byte[] pdf, IReadOnlyCollection<string> windowsUsers, string? clientName, GotomaxxOptions o, CancellationToken ct) =>
        throw new PlatformNotSupportedException("Drucken ist nur unter Windows möglich.");
}

/// <summary>Übergibt Vorgänge an gotomaxx – nacheinander, im Hintergrund (Eingang) oder sofort (Knopf im Portal).</summary>
public sealed class GotomaxxService : BackgroundService
{
    public const string Channel = "gotomaxx";

    private readonly DocumentStore _store;
    private readonly IOptionsMonitor<GotomaxxOptions> _opt;
    private readonly IGotomaxxPrinter _printer;
    private readonly Settings.UserStore? _users;
    private readonly ILogger<GotomaxxService> _log;
    private readonly Channel<string> _queue = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GotomaxxService(DocumentStore store, IOptionsMonitor<GotomaxxOptions> opt, IGotomaxxPrinter printer, ILogger<GotomaxxService> log, Settings.UserStore? users = null)
    {
        _store = store; _opt = opt; _printer = printer; _log = log; _users = users;
    }

    public GotomaxxOptions Options => _opt.CurrentValue;
    public IReadOnlyList<string> Printers() { try { return _printer.Printers(); } catch { return Array.Empty<string>(); } }

    public void Enqueue(string id) => _queue.Writer.TryWrite(id);

    /// <summary>Vorgang zur Übergabe vormerken (Status „wird gesendet“, Kanal gotomaxx).</summary>
    public static void Mark(Vorgang v, string printer, string by, string text)
    {
        v.GotomaxxPrinter = printer;
        v.Status = VorgangStatus.Sending;
        v.SendChannel = Channel;
        v.SendError = null;
        v.History.Add(new HistoryEntry(DateTimeOffset.Now, by, "gotomaxx", text));
    }

    /// <summary>Aus dem Portal: übergeben und auf das Ergebnis warten.</summary>
    public async Task<(bool Ok, string Message)> HandOverNowAsync(string id, string by, CancellationToken ct)
    {
        var o = Options;
        if (!o.Enabled) return (false, "Die Weiterleitung an gotomaxx ist nicht eingerichtet (Einstellungen → Drucker & Eingang).");
        var v = _store.Update(id, x =>
        {
            if (x.Status is VorgangStatus.Sending or VorgangStatus.Discarded) return false;
            Mark(x, x.GotomaxxPrinter ?? "", by, "Übergabe an gotomaxx aus dem Portal");
            return true;
        });
        if (v is null) return (false, "Vorgang nicht gefunden.");
        if (v.SendChannel != Channel || v.Status != VorgangStatus.Sending) return (false, "Der Vorgang wird gerade versendet oder ist verworfen.");
        return await HandleAsync(id, by, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Nach einem Neustart: unterbrochene Übergaben fortsetzen
        try
        {
            foreach (var v in _store.Query(new VorgangFilter { SeeAll = true, Statuses = new[] { VorgangStatus.Sending }, Limit = 1000 }))
                if (v.SendChannel == Channel) Enqueue(v.Id);
        }
        catch (Exception ex) { _log.LogWarning(ex, "gotomaxx: offene Übergaben konnten nicht gelesen werden"); }

        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
        {
            try { await HandleAsync(id, "Weiterleitung", ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "gotomaxx: Übergabe {Id}", id); }
        }
    }

    private async Task<(bool Ok, string Message)> HandleAsync(string id, string by, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var v = _store.Get(id);
            if (v is null) return (false, "Vorgang nicht gefunden.");
            if (v.Status != VorgangStatus.Sending || v.SendChannel != Channel) return (false, "Der Vorgang ist nicht zur Übergabe an gotomaxx vorgemerkt.");
            var o = Options;
            var printer = string.IsNullOrWhiteSpace(v.GotomaxxPrinter) ? o.Printer.Trim() : v.GotomaxxPrinter.Trim();
            string? error = null, how = null;
            if (!o.Enabled) error = "Die Weiterleitung an gotomaxx ist ausgeschaltet.";
            else if (printer.Length == 0) error = "Kein gotomaxx-Drucker eingestellt (Einstellungen → Drucker & Eingang).";
            else
            {
                try
                {
                    var path = _store.FilePath(id, DocumentStore.PdfFile) ?? throw new FileNotFoundException("Das Original-PDF fehlt.");
                    var pdf = PdfComposer.StripCodes(await File.ReadAllBytesAsync(path, ct));
                    var job = GotomaxxCodes.Job(v, printer, o);
                    how = await _printer.PrintAsync(job, pdf, WindowsUsers(v), v.PrintMachine, o, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "gotomaxx: Druck von {Id} auf {Printer} fehlgeschlagen", id, printer);
                    error = ex.Message;
                }
            }

            var now = DateTimeOffset.Now;
            _store.Update(id, x =>
            {
                if (error is null)
                {
                    x.Status = VorgangStatus.Gotomaxx; x.SentAt = now; x.SentBy = by; x.SendError = null;
                    x.History.Add(new HistoryEntry(now, by, "gotomaxx", $"An „{printer}“ gedruckt{(how is null ? "" : " – " + how)}"));
                }
                else
                {
                    x.Status = VorgangStatus.SendError; x.SendError = error; x.SendAttempts++;
                    x.History.Add(new HistoryEntry(now, by, "versandfehler", "gotomaxx: " + error));
                }
                x.SendChannel = Channel;
                return true;
            });
            if (error is null) _log.LogInformation("Vorgang {Id} an gotomaxx-Drucker {Printer} übergeben ({How})", id, printer, how);
            return error is null ? (true, $"An „{printer}“ übergeben.") : (false, "Übergabe an gotomaxx fehlgeschlagen: " + error);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Versandweg „gotomaxx-Drucker“ eines Kunden: drucken, ohne den Status des Vorgangs zu ändern.</summary>
    public async Task<string> PrintForDeliveryAsync(Vorgang v, string? printer, CancellationToken ct)
    {
        var o = Options;
        var target = string.IsNullOrWhiteSpace(printer) ? o.Printer.Trim() : printer.Trim();
        if (target.Length == 0) throw new InvalidOperationException("Kein gotomaxx-Drucker eingestellt.");
        await _gate.WaitAsync(ct);
        try
        {
            var path = _store.FilePath(v.Id, DocumentStore.PdfFile) ?? throw new FileNotFoundException("Das Original-PDF fehlt.");
            var pdf = PdfComposer.StripCodes(await File.ReadAllBytesAsync(path, ct));
            var how = await _printer.PrintAsync(GotomaxxCodes.Job(v, target, o), pdf, WindowsUsers(v), v.PrintMachine, o, ct);
            return $"gotomaxx-Drucker „{target}“ ({how})";
        }
        finally { _gate.Release(); }
    }

    /// <summary>Windows-Anmeldenamen für die Sitzungssuche: Benutzer des Druckauftrags/MAPI und die Druck-Namen des Portal-Benutzers.</summary>
    private List<string> WindowsUsers(Vorgang v)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(v.PrintUser)) names.Add(v.PrintUser.Trim());
        if (v.OwnerUserId is not null && _users?.FindById(v.OwnerUserId) is { } u)
            names.AddRange(u.PrintNames.Concat(u.Logins).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
