using Microsoft.Extensions.Options;
using PdfRechnung.Delivery;
using PdfRechnung.Documents;
using PdfRechnung.Exchange;

namespace PdfRechnung.Monitoring;

/// <summary>Benachrichtigung bei Problemen, auf die niemand reagiert (Einstellungen → Benachrichtigung).</summary>
public sealed class AlertOptions
{
    public bool Enabled { get; set; }
    /// <summary>Empfänger, mehrere mit Komma oder Strichpunkt.</summary>
    public string Recipients { get; set; } = "";
    /// <summary>Erst melden, wenn ein Problem so lange besteht (Minuten).</summary>
    public int DelayMinutes { get; set; } = 30;
    /// <summary>Solange das Problem besteht, erneut melden nach (Minuten, 0 = nur einmal).</summary>
    public int RepeatMinutes { get; set; } = 240;
    /// <summary>Mail „wieder in Ordnung“, wenn ein gemeldetes Problem behoben ist.</summary>
    public bool SendRecovery { get; set; } = true;
    /// <summary>Nur an diesen Tagen und in diesem Zeitfenster senden (leer = immer); außerhalb wird gesammelt und danach gemeldet.</summary>
    public string Days { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    // ---- Was wird überwacht ----
    public bool Channels { get; set; } = true;
    public bool Outbox { get; set; } = true;
    public bool Documents { get; set; } = true;
    /// <summary>Vorgänge mit Fehler/Versandfehler gelten nach so vielen Minuten ohne Bearbeitung als Problem.</summary>
    public int DocumentMinutes { get; set; } = 60;
    /// <summary>Auch Vorgänge mit Warnungen (warten auf Freigabe) melden.</summary>
    public bool DocumentWarnings { get; set; }
    public bool Inbound { get; set; } = true;
    public bool Connect { get; set; } = true;
    /// <summary>ERP Suite Connect: Eingangsdateien gelten nach so vielen Minuten ohne Verarbeitung als hängengeblieben.</summary>
    public int ConnectMinutes { get; set; } = 60;
    public bool Filialverbund { get; set; } = true;
}

public static class HealthLevel
{
    public const string Ok = "ok", Warning = "warnung", Error = "fehler";
    public static int Rank(string s) => s switch { Error => 2, Warning => 1, _ => 0 };
    public static string Worst(IEnumerable<string> s) => s.OrderByDescending(Rank).FirstOrDefault() ?? Ok;
}

/// <summary>Eine Prüfung. Key ist stabil (für Benachrichtigung und selektive Abfrage), Since = seit wann das Problem besteht.</summary>
public sealed record HealthCheck(string Key, string Area, string Title, string Status, string Message, DateTimeOffset? Since = null,
    string? Channel = null, string? Partner = null, int Count = 0, bool Alert = true);

public sealed record HealthReport(string Status, DateTimeOffset At, string Version, string Machine, TimeSpan Uptime, List<HealthCheck> Checks);

/// <summary>Ermittelt den Zustand des Dienstes: Versandwege, Ausgang, Vorgänge, Eingang, ERP Suite Connect, Filialverbund.</summary>
public sealed class HealthService
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Now;
    private readonly InboundService _inbound;
    private readonly OutboundLog _outbound;
    private readonly InboundLog _inLog;
    private readonly DocumentStore _docs;
    private readonly ExchangeStore _exchange;
    private readonly FilialverbundService _filial;
    private readonly IOptionsMonitor<AlertOptions> _alerts;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly IOptionsMonitor<Connect.ConnectOptions> _connect;
    private readonly IOptionsMonitor<FilialverbundOptions> _fv;

    public HealthService(InboundService inbound, OutboundLog outbound, InboundLog inLog, DocumentStore docs, ExchangeStore exchange, FilialverbundService filial,
        IOptionsMonitor<AlertOptions> alerts, IOptionsMonitor<MailOptions> mail, IOptionsMonitor<Connect.ConnectOptions> connect, IOptionsMonitor<FilialverbundOptions> fv)
    {
        _inbound = inbound; _outbound = outbound; _inLog = inLog; _docs = docs; _exchange = exchange; _filial = filial;
        _alerts = alerts; _mail = mail; _connect = connect; _fv = fv;
    }

    public HealthReport Report()
    {
        var a = _alerts.CurrentValue;
        var list = new List<HealthCheck>();
        void Safe(string area, Action f)
        {
            try { f(); }
            catch (Exception ex) { list.Add(new HealthCheck("pruefung-" + area, area, "Prüfung " + area, HealthLevel.Warning, "Prüfung nicht möglich: " + ex.Message, DateTimeOffset.Now, Alert: false)); }
        }
        list.Add(new HealthCheck("dienst", "dienst", "Dienst", HealthLevel.Ok, $"läuft seit {Started:dd.MM.yyyy HH:mm}", Alert: false));
        Safe("versandwege", () => Channels(list, a));
        Safe("ausgang", () => Outbox(list, a));
        Safe("vorgaenge", () => Documents(list, a));
        Safe("eingang", () => Inbound(list, a));
        Safe("connect", () => ConnectCheck(list, a));
        Safe("filialverbund", () => Filialverbund(list, a));
        var status = HealthLevel.Worst(list.Select(c => c.Status));
        return new HealthReport(status, DateTimeOffset.Now, typeof(HealthService).Assembly.GetName().Version?.ToString(3) ?? "",
            Environment.MachineName, DateTimeOffset.Now - Started, list);
    }

    /// <summary>Prüfungen eines Versandwegs bzw. Kommunikationspartners/Standorts (Key, Name, Kunde, Typ oder Standort-Kürzel).</summary>
    public List<HealthCheck> For(HealthReport r, string what)
    {
        what = what.Trim();
        bool Eq(string? x) => x is not null && string.Equals(x, what, StringComparison.OrdinalIgnoreCase);
        bool Has(string? x) => x is not null && x.Contains(what, StringComparison.OrdinalIgnoreCase);
        var exact = r.Checks.Where(c => Eq(c.Channel) || Eq(c.Partner) || Eq(c.Key)).ToList();
        return exact.Count > 0 ? exact : r.Checks.Where(c => c.Channel is not null && (Has(c.Channel) || Has(c.Title) || Has(c.Partner))).ToList();
    }

    private void Channels(List<HealthCheck> list, AlertOptions a)
    {
        foreach (var job in _inbound.Jobs())
        {
            var run = _inbound.LastRun(job.Key);
            var fail = _inbound.FailingSince(job.Key);
            var (nextIn, nextOut) = _inbound.NextRuns(job);
            var status = fail is not null ? HealthLevel.Error : HealthLevel.Ok;
            var msg = run is null ? "noch nicht gelaufen" : $"{(run.Ok ? "zuletzt in Ordnung" : "zuletzt fehlerhaft")} ({run.At:dd.MM. HH:mm}): {run.Message}";
            if (fail is not null) msg = $"Übertragung schlägt seit {fail:dd.MM. HH:mm} fehl: {run?.Message}";
            var next = new[] { nextIn, nextOut }.Where(x => x is not null).Min();
            if (next is not null && fail is null) msg += $" · nächster Lauf {next:dd.MM. HH:mm}";
            list.Add(new HealthCheck("versandweg-" + job.Key, "versandwege", job.Label, status, msg, fail, job.Key, job.Customer ?? job.Settings.Type, Alert: a.Channels));
        }
    }

    private void Outbox(List<HealthCheck> list, AlertOptions a)
    {
        var held = _outbound.List(null, DateTimeOffset.Now.AddDays(-30), null, null, OutboundLog.Held, 500)
            .Concat(_outbound.List(null, DateTimeOffset.Now.AddDays(-30), null, null, OutboundLog.Held, 500, "connect"))
            .Where(e => e.Exists).ToList();
        foreach (var g in held.GroupBy(e => e.Source ?? e.ChannelText))
        {
            var oldest = g.Min(e => e.At);
            list.Add(new HealthCheck("ausgang-" + Key(g.Key), "ausgang", "Ausgang " + g.Key, HealthLevel.Error,
                $"{g.Count()} Datei(en) wegen Prüffehlern zurückgehalten (Ordner „fehlerhaft“): {string.Join(", ", g.Take(3).Select(e => e.FileName))}{(g.Count() > 3 ? " …" : "")}",
                oldest, g.Key, g.First().Customer, g.Count(), a.Outbox));
        }
        if (held.Count == 0) list.Add(new HealthCheck("ausgang", "ausgang", "Ausgang", HealthLevel.Ok, "keine zurückgehaltenen Dateien", Alert: false));
    }

    private void Documents(List<HealthCheck> list, AlertOptions a)
    {
        var limit = DateTimeOffset.Now.AddMinutes(-Math.Max(1, a.DocumentMinutes));
        void Add(string key, string title, string[] statuses, string level, bool alert)
        {
            var items = _docs.Query(new VorgangFilter { SeeAll = true, Statuses = statuses, Before = limit, Limit = 1000 });
            if (items.Count == 0) { list.Add(new HealthCheck(key, "vorgaenge", title, HealthLevel.Ok, "keine offenen", Alert: false)); return; }
            var oldest = items.Min(v => v.CreatedAt);
            list.Add(new HealthCheck(key, "vorgaenge", title, level,
                $"{items.Count} Vorgang/Vorgänge seit mehr als {a.DocumentMinutes} Minuten nicht bearbeitet, ältester vom {oldest:dd.MM. HH:mm}: " +
                string.Join(", ", items.Take(3).Select(Label)) + (items.Count > 3 ? " …" : ""),
                oldest.AddMinutes(a.DocumentMinutes), Count: items.Count, Alert: alert));
        }
        Add("vorgaenge-fehler", "Vorgänge mit Fehlern", new[] { VorgangStatus.Error }, HealthLevel.Error, a.Documents);
        Add("vorgaenge-versandfehler", "Vorgänge mit Versandfehler", new[] { VorgangStatus.SendError }, HealthLevel.Error, a.Documents);
        Add("vorgaenge-haengen", "Vorgänge hängen im Versand", new[] { VorgangStatus.Sending }, HealthLevel.Error, a.Documents);
        Add("vorgaenge-warnung", "Vorgänge mit Warnungen", new[] { VorgangStatus.Warning }, HealthLevel.Warning, a.Documents && a.DocumentWarnings);
    }

    private void Inbound(List<HealthCheck> list, AlertOptions a)
    {
        var bad = _inLog.List(null, DateTimeOffset.Now.AddDays(-30), null, null, true, 500).Where(e => e.Check == Edi.EdiReport.Fehler).ToList();
        if (bad.Count == 0) { list.Add(new HealthCheck("eingang-pruefung", "eingang", "Eingang", HealthLevel.Ok, "keine ungelesenen fehlerhaften Dateien", Alert: false)); return; }
        list.Add(new HealthCheck("eingang-pruefung", "eingang", "Eingang: fehlerhafte Dateien", HealthLevel.Error,
            $"{bad.Count} empfangene Datei(en) mit Prüffehlern, noch nicht angesehen: {string.Join(", ", bad.Take(3).Select(e => e.FileName))}{(bad.Count > 3 ? " …" : "")}",
            bad.Min(e => e.At), Count: bad.Count, Alert: a.Inbound));
    }

    private void ConnectCheck(List<HealthCheck> list, AlertOptions a)
    {
        var c = _connect.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.InFolder)) return;
        if (!Directory.Exists(c.InFolder))
        {
            list.Add(new HealthCheck("connect-eingang", "connect", "ERP Suite Connect Eingang", HealthLevel.Error, "Ordner nicht gefunden: " + c.InFolder, DateTimeOffset.Now, Alert: a.Connect));
            return;
        }
        var limit = DateTime.Now.AddMinutes(-Math.Max(1, a.ConnectMinutes));
        var old = Directory.GetFiles(c.InFolder).Select(f => new FileInfo(f)).Where(f => f.LastWriteTime < limit).OrderBy(f => f.LastWriteTime).ToList();
        list.Add(old.Count == 0
            ? new HealthCheck("connect-eingang", "connect", "ERP Suite Connect Eingang", HealthLevel.Ok, "keine hängengebliebenen Dateien", Alert: false)
            : new HealthCheck("connect-eingang", "connect", "ERP Suite Connect Eingang", HealthLevel.Error,
                $"{old.Count} Datei(en) seit mehr als {a.ConnectMinutes} Minuten nicht verarbeitet (läuft ERP Suite Connect?): {string.Join(", ", old.Take(3).Select(f => f.Name))}",
                new DateTimeOffset(old[0].LastWriteTime).AddMinutes(a.ConnectMinutes), Count: old.Count, Alert: a.Connect));
    }

    private void Filialverbund(List<HealthCheck> list, AlertOptions a)
    {
        var o = _fv.CurrentValue;
        if (!o.Enabled) return;
        var st = _filial.State;
        list.Add(st.Error is not null
            ? new HealthCheck("filialverbund-verteilung", "filialverbund", "Filialverbund Verteilung", HealthLevel.Error, st.Error, st.ErrorSince, "filialverbund", Alert: a.Filialverbund)
            : st.Unassigned.Count > 0
                ? new HealthCheck("filialverbund-verteilung", "filialverbund", "Filialverbund Verteilung", HealthLevel.Warning,
                    $"{st.Unassigned.Count} Datei(en) ohne Empfänger: {string.Join("; ", st.Unassigned.Take(3))}", st.At, "filialverbund", Count: st.Unassigned.Count, Alert: a.Filialverbund)
                : new HealthCheck("filialverbund-verteilung", "filialverbund", "Filialverbund Verteilung", HealthLevel.Ok,
                    st.At is null ? "noch nicht gelaufen" : $"zuletzt {st.At:dd.MM. HH:mm}", Channel: "filialverbund", Alert: false));
        var beats = _exchange.Beats();
        var problems = _exchange.Problems(TimeSpan.FromMinutes(Math.Max(5, o.PendingMinutes))).GroupBy(e => e.Standort, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var s in o.Standorte.Where(s => s.Active))
        {
            var title = $"Standort {s.Code}{(string.IsNullOrWhiteSpace(s.Name) ? "" : " – " + s.Name)}";
            beats.TryGetValue(s.Code, out var beat);
            var offline = beat is null || DateTimeOffset.Now - beat.LastSeen > TimeSpan.FromMinutes(Math.Max(5, o.OfflineMinutes));
            list.Add(new HealthCheck("standort-" + Key(s.Code), "filialverbund", title + ": Client",
                offline ? HealthLevel.Error : HealthLevel.Ok,
                beat is null ? "Client hat sich noch nie gemeldet" : offline ? $"Client meldet sich seit {beat.LastSeen:dd.MM. HH:mm} nicht mehr" : $"zuletzt gemeldet {beat.LastSeen:dd.MM. HH:mm}{(beat.Machine is null ? "" : " von " + beat.Machine)}",
                offline ? (beat?.LastSeen ?? null) : null, "filialverbund", s.Code, Alert: a.Filialverbund && beat is not null));
            if (problems.TryGetValue(s.Code, out var p))
            {
                var failed = p.Where(e => e.Status == ExchangeStatus.Failed).ToList();
                var pending = p.Where(e => e.Status != ExchangeStatus.Failed).ToList();
                if (failed.Count > 0)
                    list.Add(new HealthCheck("standort-" + Key(s.Code) + "-import", "filialverbund", title + ": Importfehler", HealthLevel.Error,
                        $"{failed.Count} Datei(en) mit Importfehler: {string.Join("; ", failed.Take(3).Select(e => $"{e.FileName} ({e.Message})"))}",
                        failed.Min(e => e.StatusAt ?? e.At), "filialverbund", s.Code, failed.Count, a.Filialverbund));
                if (pending.Count > 0)
                    list.Add(new HealthCheck("standort-" + Key(s.Code) + "-offen", "filialverbund", title + ": nicht abgeholt", HealthLevel.Error,
                        $"{pending.Count} Datei(en) seit mehr als {o.PendingMinutes} Minuten nicht abgeholt bzw. nicht quittiert: {string.Join(", ", pending.Take(3).Select(e => e.FileName))}",
                        pending.Min(e => e.At).AddMinutes(o.PendingMinutes), "filialverbund", s.Code, pending.Count, a.Filialverbund));
            }
        }
    }

    private static string Label(Vorgang v) =>
        string.Join(" ", new[] { v.DocumentType, v.VoucherId, v.BuyerName }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } t ? t : v.OriginalFileName ?? v.Id;

    private static string Key(string s) => new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
}
