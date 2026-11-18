using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PdfRechnung.Data;
using PdfRechnung.Erp;

namespace PdfRechnung.Documents;

/// <summary>Einstellungen: gotomaxx-Protokoll einlesen (Einstellungen → Drucker & Belege → gotomaxx).</summary>
public sealed class GotomaxxLogOptions
{
    public bool Enabled { get; set; }
    /// <summary>Ordner der gotomaxx-Protokolle (usage_*.dat). Leer = C:\ProgramData\gotomaxx\pdfmailer6.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Überwachte Drucker (gotomaxx-Profile). Leer = alle.</summary>
    public List<string> Profiles { get; set; } = new();
    public bool Watches(string profile) => Profiles.Count == 0 || Profiles.Any(p => string.Equals(p.Trim(), profile, StringComparison.OrdinalIgnoreCase));
    /// <summary>Wie oft gelesen wird (Sekunden).</summary>
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>Beim ersten Lesen so viele Tage zurück übernehmen.</summary>
    public int ImportDays { get; set; } = 30;
    /// <summary>Ergebnis ins ERP zurückmelden (über die ERP-Rückmeldung).</summary>
    public bool ErpFeedback { get; set; } = true;
    /// <summary>Direkt aus dem ERP gedruckte Belege im ERP suchen: Belegindex aus Dokumentname erraten und mit BELEG.GET prüfen.</summary>
    public bool LookupErp { get; set; } = true;
    /// <summary>Belegart und Belegnummer aus dem Dokumentnamen (Gruppen „art“ und „nr“, optional „idx“ = Indexanfang wie „0NA“).</summary>
    public string TitlePattern { get; set; } = DefaultTitlePattern;
    /// <summary>Anfang des Belegindex, wenn er nicht im Dokumentnamen steht (die ersten beiden Stellen), Standard „0N“.</summary>
    public string IndexPrefix { get; set; } = "0N";
    /// <summary>Wort im Dokumentnamen → Belegart-Kennbuchstabe im Index, z. B. „Rechnung=R“.</summary>
    public List<GotomaxxTypeMapping> TypeMap { get; set; } = DefaultTypeMap();
    /// <summary>Stellen der Belegnummer im Index: kürzere Nummern rechtsbündig mit Leerzeichen, z. B. „0NR 1234567“.</summary>
    public int NumberWidth { get; set; } = 8;
    /// <summary>Lässt sich die Belegart nicht erraten: in dieser Reihenfolge suchen.</summary>
    public string SearchOrder { get; set; } = "R, L, A, N, b";

    public static List<GotomaxxTypeMapping> DefaultTypeMap() => new()
    {
        new() { Text = "Rechnung", Code = "R" }, new() { Text = "Lieferschein", Code = "L" }, new() { Text = "Auftrag", Code = "A" },
        new() { Text = "Angebot", Code = "N" }, new() { Text = "Bestellung", Code = "b" }, new() { Text = "Gutschrift", Code = "G" }
    };
    /// <summary>Fehler dem Benutzer melden, der gedruckt hat (Melder und Portal).</summary>
    public bool NotifyUsers { get; set; } = true;
    /// <summary>Zusätzlich per E-Mail an den Benutzer (E-Mail-Adresse aus der Benutzerverwaltung).</summary>
    public bool NotifyUserMail { get; set; }
    /// <summary>Mandant für die Suche und Rückmeldung (leer = allgemeine ERP-Verbindung). Mandanten des druckenden Benutzers gehen vor.</summary>
    public string Mandant { get; set; } = "";

    public const string DefaultTitlePattern = @"^(?<art>.+?)\s*(?:\([^)]*\))?\s+-\s+(?<nr>[A-Za-z0-9/._-]+)";
    public string FolderOrDefault => string.IsNullOrWhiteSpace(Folder) ? @"C:\ProgramData\gotomaxx\pdfmailer6" : Folder.Trim();
}

public sealed class GotomaxxTypeMapping
{
    public string Text { get; set; } = "";
    public string Code { get; set; } = "";
}

/// <summary>Ein Eintrag aus dem gotomaxx-Protokoll mit Zuordnung.</summary>
public sealed record GotomaxxLogEntry(long Nr, string File, long EntryId, string Profile, string? User, string? Computer, string? Source,
    DateTimeOffset At, int State, string? Actions, string? Error, string? DocType, string? DocNumber, string? VorgangId, string? ErpIndex,
    string? ErpStatus, string? ErpMessage, long? ErpJob, string? Owner = null, string? Mandant = null)
{
    /// <summary>offen, versendet, fehler</summary>
    public string StateText => State switch { 1 => "versendet", 2 or 3 => "fehler", 0 => "offen", _ => "unbekannt" };
}

public static class GotomaxxErpStatus
{
    /// <summary>über den Vorgang (Belegpost hat an gotomaxx übergeben) – die Rückmeldung läuft über den Vorgang</summary>
    public const string Vorgang = "vorgang";
    public const string Queued = "eingereiht", NotAssignable = "nicht zuordenbar", NotFound = "nicht gefunden", Off = "aus", Pending = "offen", LookupError = "suchfehler";
    /// <summary>Beim ersten Lesen älter als „ImportDays“: nur angezeigt, nicht ans ERP gemeldet</summary>
    public const string Old = "nicht gemeldet (alt)";
}

/// <summary>Ein Ereignis aus dem gotomaxx-Detailtext (Speichern, Drucken, E-Mail …).</summary>
public sealed record GotomaxxEvent(string Name, string? Info, string? Error);

/// <summary>
/// Liest die gotomaxx-PDFMAILER-Protokolle (SQLite, Tabelle Entry) laufend ein, ordnet die Einträge Belegen zu und meldet
/// das Ergebnis (versendet bzw. Fehler) über die ERP-Rückmeldung zurück. Die gotomaxx-Dateien werden nie verändert –
/// gelesen wird aus einer Kopie.
/// </summary>
public sealed class GotomaxxLogService : BackgroundService
{
    private readonly BelegpostDb _db;
    private readonly DocumentStore _store;
    private readonly IOptionsMonitor<GotomaxxLogOptions> _opt;
    private readonly ErpFeedbackService _erp;
    private readonly ILogger<GotomaxxLogService> _log;
    private readonly Settings.UserStore? _users;
    private readonly IOptionsMonitor<MandantOptions>? _mandanten;
    private readonly Services.EventHub? _hub;
    private readonly IOptionsMonitor<MailOptions>? _mail;
    private readonly Settings.SettingsStore _settings;
    private readonly string _temp;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public (DateTimeOffset? At, string? Error, int Files) State { get; private set; }

    public GotomaxxLogService(BelegpostDb db, DocumentStore store, IOptionsMonitor<GotomaxxLogOptions> opt, ErpFeedbackService erp,
        Settings.SettingsStore settings, ILogger<GotomaxxLogService> log, Settings.UserStore? users = null, Services.EventHub? hub = null,
        IOptionsMonitor<MailOptions>? mail = null, IOptionsMonitor<MandantOptions>? mandanten = null)
    {
        _mandanten = mandanten;
        _db = db; _store = store; _opt = opt; _erp = erp; _log = log; _users = users; _hub = hub; _mail = mail; _settings = settings;
        _temp = Path.Combine(settings.DataDirectory, "temp", "gotomaxx");
        ErpFeedbackService.ExternalVorgang = External;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var o = _opt.CurrentValue;
            if (o.Enabled) await ScanAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(o.IntervalSeconds, 15, 3600)), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Alle Protokolldateien lesen und neue Einträge zuordnen.</summary>
    public async Task<int> ScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var added = 0;
        try
        {
            var o = _opt.CurrentValue;
            var dir = o.FolderOrDefault;
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Ordner der gotomaxx-Protokolle nicht gefunden: " + dir);
            var files = Directory.GetFiles(dir, "usage_*.dat");
            foreach (var f in files) added += ReadFile(f, o);
            AssignOwners();
            await AssignAsync(o, ct);
            await NotifyAsync(o, ct);
            State = (DateTimeOffset.Now, null, files.Length);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "gotomaxx-Protokoll");
            State = (DateTimeOffset.Now, ex.Message, State.Files);
        }
        finally { _gate.Release(); }
        return added;
    }

    // ---------------- Lesen ----------------

    private int ReadFile(string path, GotomaxxLogOptions o)
    {
        var name = Path.GetFileName(path);
        Directory.CreateDirectory(_temp);
        var copy = Path.Combine(_temp, name);
        // Kopie lesen: gotomaxx schreibt weiter, und wir verändern seine Datei nie (WAL-Datei gehört dazu)
        File.Copy(path, copy, true);
        var wal = path + "-wal";
        if (File.Exists(wal)) File.Copy(wal, copy + "-wal", true); else if (File.Exists(copy + "-wal")) File.Delete(copy + "-wal");
        if (File.Exists(copy + "-shm")) File.Delete(copy + "-shm");

        long last; List<long> open;
        var marks = Marks();
        using (var c = _db.Open())
        {
            var stored = BelegpostDb.Scalar(c, "SELECT MAX(eintrag_id) FROM gotomaxx_protokoll WHERE datei=@d", ("@d", name)) is long l ? l : -1;
            last = Math.Max(stored, marks.TryGetValue(name, out var m) ? m : -1);
            open = new();
            using var cmd = BelegpostDb.Cmd(c, "SELECT eintrag_id FROM gotomaxx_protokoll WHERE datei=@d AND status=0 AND zeit > @z", ("@d", name), ("@z", BelegpostDb.Ms(DateTimeOffset.Now.AddDays(-2))));
            using var r = cmd.ExecuteReader();
            while (r.Read()) open.Add(r.GetInt64(0));
        }
        var rows = new List<(long Id, string Profile, string? User, string? Computer, string? Source, long Ts, int State, string? Detail)>();
        // Schreibrecht nötig, damit SQLite die WAL-Datei der Kopie einlesen kann – die Kopie gehört uns.
        using (var g = new SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            g.Open();
            // Erstes Lesen: die ganze Geschichte (die neuesten 20000) zur Anzeige; ans ERP gemeldet wird nur, was jünger als ImportDays ist
            var where = last < 0 ? "1=1" : "entryId > @l" + (open.Count > 0 ? $" OR entryId IN ({string.Join(",", open)})" : "");
            using var cmd = g.CreateCommand();
            cmd.CommandText = last < 0
                ? "SELECT * FROM (SELECT entryId, profileName, user, computer, source, timestamp, state, detail FROM Entry ORDER BY entryId DESC LIMIT 20000) ORDER BY entryId"
                : $"SELECT entryId, profileName, user, computer, source, timestamp, state, detail FROM Entry WHERE {where} ORDER BY entryId LIMIT 20000";
            cmd.Parameters.AddWithValue("@l", last);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.IsDBNull(1) ? "(ohne Profil)" : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? 0 : r.GetInt64(5), r.IsDBNull(6) ? -1 : (int)r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        if (rows.Count == 0) return 0;
        // Bis hierher gelesen – auch Einträge nicht überwachter Drucker nicht erneut lesen
        marks[name] = Math.Max(last, rows.Max(x => x.Id));
        SaveMarks(marks);
        rows = rows.Where(x => o.Watches(x.Profile)).ToList();
        if (rows.Count == 0) return 0;
        var oldBefore = last < 0 ? DateTimeOffset.Now.AddDays(-Math.Max(1, o.ImportDays)).ToUnixTimeSeconds() : long.MinValue;
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            foreach (var x in rows)
            {
                var old = x.Ts < oldBefore;
                var events = ParseEvents(x.Detail);
                var (art, nr) = ParseTitle(x.Source, o);
                BelegpostDb.Exec(c, """
                    INSERT INTO gotomaxx_protokoll(datei, eintrag_id, profil, benutzer, rechner, quelle, zeit, status, aktionen, fehler, belegart, belegnr, besitzer, erp_status, erp_meldung, gemeldet)
                    VALUES(@d,@e,@p,@u,@r,@q,@z,@s,@a,@f,@ba,@bn,@o,@es,@em,@gm)
                    ON CONFLICT(datei, eintrag_id) DO UPDATE SET status=@s, aktionen=@a, fehler=@f
                    """,
                    ("@d", name), ("@e", x.Id), ("@p", x.Profile), ("@u", x.User), ("@r", x.Computer), ("@q", x.Source),
                    ("@z", BelegpostDb.Ms(DateTimeOffset.FromUnixTimeSeconds(x.Ts))), ("@s", x.State),
                    ("@a", Actions(events)), ("@f", FirstError(events)), ("@ba", art), ("@bn", nr), ("@o", Owner(x.User)),
                    ("@es", old ? GotomaxxErpStatus.Old : null), ("@em", old ? $"Beim ersten Lesen älter als {o.ImportDays} Tage" : null), ("@gm", old ? 0 : null));
            }
            tx.Commit();
        }
        return rows.Count;
    }

    private string MarksFile => Path.Combine(Path.GetDirectoryName(_temp)!, "..", "gotomaxx-stand.json");
    private Dictionary<string, long> Marks()
    {
        try { return File.Exists(MarksFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(MarksFile)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }
    private void SaveMarks(Dictionary<string, long> m)
    {
        try { File.WriteAllText(MarksFile, System.Text.Json.JsonSerializer.Serialize(m)); } catch (Exception) { }
    }

    /// <summary>Alle gotomaxx-Profile in den Protokolldateien (für die Auswahl in den Einstellungen).</summary>
    public List<string> AvailableProfiles() => ProfileInfos().Select(x => x.Name).ToList();

    public sealed record ProfileInfo(string Name, long Total, DateTimeOffset? Last);

    /// <summary>Drucker in den Protokolldateien mit Anzahl und letztem Eintrag.</summary>
    public List<ProfileInfo> ProfileInfos()
    {
        var o = _opt.CurrentValue;
        var map = new Dictionary<string, (long Total, long Last)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(o.FolderOrDefault)) return new();
            Directory.CreateDirectory(_temp);
            foreach (var path in Directory.GetFiles(o.FolderOrDefault, "usage_*.dat"))
            {
                var copy = Path.Combine(_temp, "liste_" + Path.GetFileName(path));
                File.Copy(path, copy, true);
                if (File.Exists(path + "-wal")) File.Copy(path + "-wal", copy + "-wal", true);
                using var g = new SqliteConnection($"Data Source={copy};Pooling=False");
                g.Open();
                using var cmd = g.CreateCommand();
                cmd.CommandText = "SELECT profileName, COUNT(*), MAX(timestamp) FROM Entry WHERE profileName IS NOT NULL GROUP BY profileName";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var name = r.GetString(0); var total = r.GetInt64(1); var last = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                    map[name] = map.TryGetValue(name, out var v) ? (v.Total + total, Math.Max(v.Last, last)) : (total, last);
                }
            }
        }
        catch (Exception ex) { _log.LogInformation(ex, "gotomaxx-Profile lesen"); }
        foreach (var p in ProfileNames()) if (!map.ContainsKey(p)) map[p] = (0, 0);
        return map.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ProfileInfo(x.Key, x.Value.Total, x.Value.Last > 0 ? DateTimeOffset.FromUnixTimeSeconds(x.Value.Last).ToLocalTime() : null)).ToList();
    }

    /// <summary>Portal-Benutzer zum Windows-Benutzer im gotomaxx-Protokoll (Druck-Benutzer bzw. Anmeldename, ohne Domäne).</summary>
    private string? Owner(string? windowsUser)
    {
        if (_users is null || string.IsNullOrWhiteSpace(windowsUser)) return null;
        // Windows-Benutzer kommt mit oder ohne Domäne („FIRMA\m.muster“ / „m.muster“); FindByPrintName vergleicht auch ohne Domäne
        // und mit dem Teil vor dem @ der E-Mail-Adresse bzw. Anmeldung.
        var full = windowsUser.Trim();
        var u = _users.FindByPrintName(full) ?? _users.FindByLogin(new[] { full, Settings.UserStore.Short(full) });
        return u?.Id;
    }

    /// <summary>Einträge ohne Portal-Benutzer nachträglich zuordnen (z. B. Benutzer erst später angelegt oder Druck-Benutzer ergänzt).</summary>
    private void AssignOwners()
    {
        if (_users is null) return;
        using var c = _db.Open();
        var names = new List<string>();
        using (var cmd = BelegpostDb.Cmd(c, "SELECT DISTINCT benutzer FROM gotomaxx_protokoll WHERE besitzer IS NULL AND benutzer IS NOT NULL"))
        using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        foreach (var n in names)
            if (Owner(n) is { } id)
                BelegpostDb.Exec(c, "UPDATE gotomaxx_protokoll SET besitzer=@o WHERE besitzer IS NULL AND benutzer=@u", ("@o", id), ("@u", n));
    }

    /// <summary>Fehler dem druckenden Benutzer melden (Melder/Portal, optional Mail) – je Eintrag einmal.</summary>
    private async Task NotifyAsync(GotomaxxLogOptions o, CancellationToken ct)
    {
        List<GotomaxxLogEntry> todo;
        using (var c = _db.Open())
        {
            // Ältere Einträge (Erstimport) nicht nachmelden
            BelegpostDb.Exec(c, "UPDATE gotomaxx_protokoll SET gemeldet=0 WHERE gemeldet IS NULL AND (status NOT IN (2,3) OR besitzer IS NULL OR zeit < @z)",
                ("@z", BelegpostDb.Ms(DateTimeOffset.Now.AddHours(-24))));
            using var cmd = BelegpostDb.Cmd(c, Select + " WHERE gemeldet IS NULL AND status IN (2,3) AND besitzer IS NOT NULL ORDER BY zeit LIMIT 200");
            using var r = cmd.ExecuteReader();
            todo = new();
            while (r.Read()) todo.Add(Read(r));
        }
        foreach (var e in todo)
        {
            var mark = 0;
            if (o.NotifyUsers && _hub is not null)
            {
                _hub.Publish(e.Owner!, "hinweis", "#/gotomaxx?d=" + Uri.EscapeDataString(e.Profile), $"Nicht versendet: {e.Source}",
                    VorgangStatus.SendError, e.Error ?? "gotomaxx meldet einen Fehler.", mail: false);
                mark = 1;
            }
            if (o.NotifyUserMail && _users?.FindById(e.Owner) is { Email: { Length: > 0 } to } && _mail?.CurrentValue is { } m)
            {
                try
                {
                    var mail = new OutgoingMail
                    {
                        Subject = $"Nicht versendet: {e.Source}",
                        Body = $"Der Beleg „{e.Source}“ wurde von gotomaxx ({e.Profile}) am {e.At:dd.MM.yyyy HH:mm} nicht versendet.\n\nFehler: {e.Error ?? "unbekannt"}\nVerarbeitung: {e.Actions}\n\nBitte prüfen und erneut senden. Übersicht im Portal unter Gotomaxx → {e.Profile}."
                    };
                    mail.To.Add(to);
                    await MailSender.SendAsync(m, new MailSecrets(_settings.Secrets.Unprotect(m.Password), _settings.Secrets.Unprotect(m.GraphClientSecret)), mail, ct);
                    mark = 1;
                }
                catch (Exception ex) { _log.LogWarning(ex, "gotomaxx-Hinweis an {To}", to); }
            }
            using var c = _db.Open();
            BelegpostDb.Exec(c, "UPDATE gotomaxx_protokoll SET gemeldet=@g WHERE nr=@n", ("@g", mark == 1 ? BelegpostDb.Ms(DateTimeOffset.Now) : 0), ("@n", e.Nr));
        }
    }

    /// <summary>Detailtext „events { entry { name … info … error … } … }“ in Ereignisse zerlegen.</summary>
    public static List<GotomaxxEvent> ParseEvents(string? detail)
    {
        var list = new List<GotomaxxEvent>();
        if (string.IsNullOrWhiteSpace(detail)) return list;
        foreach (Match m in Regex.Matches(detail, @"entry\s*\{(.*?)\}", RegexOptions.Singleline))
        {
            string? Val(string key)
            {
                var v = Regex.Match(m.Groups[1].Value, @"^\s*" + key + @"(?:[ \t]+(.*))?$", RegexOptions.Multiline);
                if (!v.Success) return null;
                return Clean(v.Groups[1].Success ? v.Groups[1].Value : "");
            }
            var name = Val("name") ?? "";
            var error = Val("error");
            list.Add(new GotomaxxEvent(name, Val("info"), error));
        }
        return list;
    }

    private static string Clean(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        s = s.Replace("\\'", "'").Trim();
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') s = s[1..^1];
        return s.Replace(@"\\", @"\");
    }

    private static string Actions(List<GotomaxxEvent> ev) =>
        string.Join(" · ", ev.Select(e => $"{e.Name}{(e.Error is not null ? " ✗" : " ✓")}{(string.IsNullOrWhiteSpace(e.Info) ? "" : ": " + e.Info)}"));

    /// <summary>Erster Fehler: „Fehlertext: 'Info' (Funktion)“ – leerer Fehlertext bei gotomaxx heißt „Fehler“.</summary>
    private static string? FirstError(List<GotomaxxEvent> ev) =>
        ev.FirstOrDefault(e => e.Error is not null) is { } e
            ? $"{(string.IsNullOrWhiteSpace(e.Error) ? "Fehler" : e.Error)} bei „{e.Name}“{(string.IsNullOrWhiteSpace(e.Info) ? "" : $": {e.Info}")}"
            : null;

    /// <summary>Belegart und Belegnummer aus dem Dokumentnamen (ohne Pfad und Endung).</summary>
    public static (string? Art, string? Nr) ParseTitle(string? source, GotomaxxLogOptions o)
    {
        if (string.IsNullOrWhiteSpace(source)) return (null, null);
        var t = source.Trim();
        if (t.Contains('\\') || t.Contains('/')) t = Path.GetFileNameWithoutExtension(t.Replace('/', '\\').Split('\\')[^1]);
        try
        {
            var m = Regex.Match(t, string.IsNullOrWhiteSpace(o.TitlePattern) ? GotomaxxLogOptions.DefaultTitlePattern : o.TitlePattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!m.Success || !m.Groups["nr"].Success) return (null, null);
            var art = m.Groups["art"].Success ? m.Groups["art"].Value.Trim() : null;
            return (art, m.Groups["nr"].Value.Trim());
        }
        catch (Exception) { return (null, null); }
    }

    // ---------------- Zuordnen und zurückmelden ----------------

    private async Task AssignAsync(GotomaxxLogOptions o, CancellationToken ct)
    {
        var todo = List(null, null, null, null, null, 500, onlyUnassigned: true);
        if (todo.Count == 0) return;
        // Über Belegpost an gotomaxx übergebene Vorgänge (letzte 14 Tage)
        var candidates = _store.Query(new VorgangFilter
        {
            SeeAll = true, From = DateTimeOffset.Now.AddDays(-14), Limit = 5000,
            Statuses = new[] { VorgangStatus.Gotomaxx, VorgangStatus.Sending, VorgangStatus.SendError, VorgangStatus.Sent }
        }).Where(v => v.GotomaxxPrinter is not null || v.SendChannel == GotomaxxService.Channel).ToList();

        foreach (var e in todo.OrderBy(x => x.At))
        {
            if (ct.IsCancellationRequested) break;
            if (e.State is not (1 or 2 or 3)) continue; // noch in Arbeit
            var ok = e.State == 1;
            var v = MatchVorgang(e, candidates);
            if (v is not null)
            {
                var updated = _store.Update(v.Id, x =>
                {
                    if (ok && x.Status == VorgangStatus.Sent) return false;
                    x.Status = ok ? VorgangStatus.Sent : VorgangStatus.SendError;
                    x.SendChannel = GotomaxxService.Channel;
                    x.SendError = ok ? null : e.Error ?? "gotomaxx meldet einen Fehler.";
                    if (ok) { x.SentAt = e.At; x.SentBy = $"gotomaxx ({e.Profile})"; }
                    x.History.Add(new HistoryEntry(e.At, "gotomaxx-Protokoll", "gotomaxx", ok ? $"von gotomaxx verarbeitet: {e.Actions}" : $"gotomaxx-Fehler: {e.Error}"));
                    return true;
                });
                SetErp(e.Nr, GotomaxxErpStatus.Vorgang, v.ErpIndex is null ? "Vorgang ohne Belegindex – keine ERP-Rückmeldung" : "Rückmeldung über den Vorgang", v.Id, v.ErpIndex, null);
                continue;
            }
            if (!o.ErpFeedback) { SetErp(e.Nr, GotomaxxErpStatus.Off, "ERP-Rückmeldung für das gotomaxx-Protokoll ist aus.", null, null, null); continue; }
            if (!o.LookupErp || e.DocNumber is null) { SetErp(e.Nr, GotomaxxErpStatus.NotAssignable, e.DocNumber is null ? "Belegnummer im Dokumentnamen nicht erkannt." : "Suche im ERP ist aus.", null, null, null); continue; }
            var tried = Candidates(o, e);
            // Mandanten: die des druckenden Benutzers gehen vor, sonst die globale Einstellung
            var mandanten = MandantenFor(e, o);
            var setups = mandanten.Select(m => (Mandant: m, Opt: _erp.OptionsFor(m))).Where(x => x.Opt.Enabled).ToList();
            if (setups.Count == 0)
            {
                SetErp(e.Nr, GotomaxxErpStatus.Off, mandanten.Any(m => m is not null)
                    ? $"ERP-Rückmeldung für Mandant {string.Join(", ", mandanten.Where(m => m is not null))} ist nicht eingerichtet." : "ERP-Rückmeldung ist nicht eingerichtet.", null, null, null);
                continue;
            }
            try
            {
                // Belegindex erraten und der Reihe nach im ERP prüfen (BELEG.GET mit BELNDX) – je Mandant, der erste Treffer zählt
                var idx = new List<string>();
                ErpOptions erpOpt = setups[0].Opt; string? mandant = setups[0].Mandant;
                foreach (var (m, opt) in setups)
                {
                    var manager = _erp.Manager(opt);
                    foreach (var cand in tried)
                    {
                        try { if (await manager.ReadDocumentAsync(cand, ct) is not null) { idx.Add(cand); break; } }
                        catch (ErpException x) when (x.Code is "QueryRejected" or "DocumentNotFound" && !x.Transient) { } // diesen Index gibt es nicht
                    }
                    if (idx.Count > 0) { erpOpt = opt; mandant = m; break; }
                }
                if (idx.Count == 0)
                {
                    var where = setups.Any(x => x.Mandant is not null) ? $" in Mandant {string.Join(", ", setups.Select(x => x.Mandant ?? "allgemein"))}" : "";
                    SetErp(e.Nr, GotomaxxErpStatus.NotFound, $"Im ERP{where} nicht gefunden (versucht: {string.Join(", ", tried)}).", null, null, null);
                    continue;
                }
                SetMandant(e.Nr, mandant);
                var code = ok ? StatusCode.Versendet : StatusCode.GotomaxxFehler;
                if (!ErpFieldBuilder.ShouldWrite(erpOpt, code) || erpOpt.Fields.Count == 0)
                {
                    SetErp(e.Nr, GotomaxxErpStatus.Off, "Für diesen Status ist keine Rückmeldung eingestellt.", null, idx[0], null);
                    continue;
                }
                var job = _erp.Enqueue(idx[0], ErpFeedbackService.ExternalPrefix + e.Nr, code, e.At, mandant);
                SetErp(e.Nr, GotomaxxErpStatus.Queued, null, null, idx[0], job);
            }
            catch (ErpException ex)
            {
                // Verbindung/Einrichtung: später erneut versuchen (max. 20 Versuche)
                using var c = _db.Open();
                BelegpostDb.Exec(c, "UPDATE gotomaxx_protokoll SET erp_versuche=erp_versuche+1, erp_meldung=@m, erp_status=CASE WHEN erp_versuche>=19 THEN @s ELSE NULL END WHERE nr=@n",
                    ("@m", ex.Message), ("@s", GotomaxxErpStatus.LookupError), ("@n", e.Nr));
            }
        }
    }

    /// <summary>
    /// Mögliche Belegindizes: steht der Indexanfang im Dokumentnamen (z. B. „(0NA)“), nur dieser. Sonst Präfix (Standard „0N“) +
    /// Belegart aus einem Wort im Dokumentnamen (Rechnung, Lieferschein, Auftrag …); lässt sie sich nicht erraten, in der
    /// eingestellten Reihenfolge (Rechnung, Lieferschein, Auftrag, Angebot, Bestellung).
    /// </summary>
    public static List<string> Candidates(GotomaxxLogOptions o, GotomaxxLogEntry e)
    {
        var raw = (e.DocNumber ?? "").Trim();
        // Belegnummer rechtsbündig: kürzere Nummern mit Leerzeichen auffüllen („0NR 1234567“)
        var nr = o.NumberWidth > 0 && raw.Length < o.NumberWidth ? raw.PadLeft(o.NumberWidth) : raw;
        var list = new List<string>();
        var text = e.Source ?? "";
        var idx = Regex.Match(text, @"\((?<idx>[0-9A-Za-z]{2}[A-Za-z])\)\s*(?:\.pdf)?\s*$");
        if (idx.Success) { list.Add(idx.Groups["idx"].Value + nr); return list; }
        var prefix = string.IsNullOrWhiteSpace(o.IndexPrefix) ? "0N" : o.IndexPrefix.Trim();
        var map = (o.TypeMap.Count > 0 ? o.TypeMap : GotomaxxLogOptions.DefaultTypeMap()).Select(SplitMapping)
            .Where(m => m.Text.Length > 0 && m.Code.Length > 0).ToList();
        // Gesucht wird nach dem Wort („Angebot“), der Kennbuchstabe („N“) kommt an die 3. Stelle des Index
        var hay = text + " " + (e.DocType ?? "");
        var guessed = map.Where(m => hay.Contains(m.Text, StringComparison.OrdinalIgnoreCase)).Select(m => m.Code).Distinct().ToList();
        var order = guessed.Count > 0 ? guessed
            : (o.SearchOrder ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (order.Count == 0) order = new() { "R", "L", "A", "N", "b" };
        foreach (var art in order) { var c = prefix + art + nr; if (!list.Contains(c)) list.Add(c); }
        return list;
    }

    /// <summary>„Angebot (N)“ im Suchwort: Klammer abtrennen, Buchstabe in der Klammer gilt als Kennbuchstabe.</summary>
    private static (string Text, string Code) SplitMapping(GotomaxxTypeMapping m)
    {
        var text = (m.Text ?? "").Trim(); var code = (m.Code ?? "").Trim();
        var k = Regex.Match(text, @"^(?<t>.*?)\s*\((?<c>[A-Za-z])\)$");
        if (k.Success) { text = k.Groups["t"].Value.Trim(); if (code.Length == 0) code = k.Groups["c"].Value; }
        var kc = Regex.Match(code, @"^\(?(?<c>[A-Za-z])\)?$");
        if (kc.Success) code = kc.Groups["c"].Value;
        return (text, code);
    }

    /// <summary>Vorgang, den Belegpost an gotomaxx übergeben hat: gleicher Dokumentname, vor dem Protokolleintrag übergeben.</summary>
    private static Vorgang? MatchVorgang(GotomaxxLogEntry e, List<Vorgang> candidates)
    {
        var src = Norm(e.Source);
        if (src.Length == 0) return null;
        return candidates
            .Where(v => v.CreatedAt <= e.At.AddMinutes(5))
            .Where(v => new[] { v.Mail.FileName, v.OriginalFileName, v.Id }.Any(n => Norm(n) is { Length: > 0 } nn && nn == src))
            .OrderByDescending(v => v.CreatedAt).FirstOrDefault();
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim().Replace('/', '\\').Split('\\')[^1];
        if (t.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
        return t.Trim().ToLowerInvariant();
    }

    private void SetErp(long nr, string status, string? message, string? vorgangId, string? belndx, long? job)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "UPDATE gotomaxx_protokoll SET erp_status=@s, erp_meldung=@m, vorgang_id=COALESCE(@v, vorgang_id), belndx=COALESCE(@b, belndx), erp_auftrag=COALESCE(@j, erp_auftrag) WHERE nr=@n",
            ("@s", status), ("@m", message), ("@v", vorgangId), ("@b", belndx), ("@j", job), ("@n", nr));
    }

    /// <summary>Eintrag erneut zuordnen (z. B. nach Änderung der Einstellungen).</summary>
    public void Reset(long nr)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "UPDATE gotomaxx_protokoll SET erp_status=NULL, erp_meldung=NULL, erp_versuche=0, erp_auftrag=NULL WHERE nr=@n", ("@n", nr));
    }

    /// <summary>
    /// Mandanten für Suche und Rückmeldung: die dem druckenden Benutzer zugewiesenen (nur eingerichtete), sonst die globale
    /// Einstellung (null = allgemeine ERP-Verbindung).
    /// </summary>
    public List<string?> MandantenFor(GotomaxxLogEntry e, GotomaxxLogOptions o)
    {
        var known = _mandanten?.CurrentValue;
        var own = (_users?.FindById(e.Owner)?.Mandanten ?? new())
            .Where(m => known is null || known.Find(m) is not null).Select(m => (string?)m).ToList();
        if (own.Count > 0) return own;
        return new() { string.IsNullOrWhiteSpace(o.Mandant) ? null : o.Mandant.Trim() };
    }

    private void SetMandant(long nr, string? mandant)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "UPDATE gotomaxx_protokoll SET mandant=@m WHERE nr=@n", ("@m", mandant), ("@n", nr));
    }

    /// <summary>Vorübergehender Vorgang für die ERP-Rückmeldung eines direkt gedruckten Belegs.</summary>
    private Vorgang? External(string id)
    {
        if (!long.TryParse(id[ErpFeedbackService.ExternalPrefix.Length..], out var nr) || Get(nr) is not { } e) return null;
        var o = _opt.CurrentValue;
        return new Vorgang
        {
            Id = id, CreatedAt = e.At, ErpIndex = e.ErpIndex, DocumentType = e.DocType ?? "Beleg", VoucherId = e.DocNumber,
            OriginalFileName = e.Source, Status = e.State == 1 ? VorgangStatus.Sent : VorgangStatus.SendError, SendChannel = GotomaxxService.Channel,
            SentAt = e.State == 1 ? e.At : null, SentBy = $"gotomaxx ({e.Profile})", SendError = e.Error, PrintUser = e.User,
            Mandant = e.Mandant ?? MandantenFor(e, o).FirstOrDefault(), GotomaxxPrinter = e.Profile
        };
    }

    // ---------------- Abfragen ----------------

    private const string Select = "SELECT nr, datei, eintrag_id, profil, benutzer, rechner, quelle, zeit, status, aktionen, fehler, belegart, belegnr, vorgang_id, belndx, erp_status, erp_meldung, erp_auftrag, besitzer, mandant FROM gotomaxx_protokoll";

    public GotomaxxLogEntry? Get(long nr)
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, Select + " WHERE nr=@n", ("@n", nr));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <param name="owners">null = alle; sonst nur Einträge dieser Benutzer (Sichtbarkeit).</param>
    public List<GotomaxxLogEntry> List(string? profile, string? text, string? state, DateTimeOffset? from, DateTimeOffset? before, int limit = 1000, bool onlyUnassigned = false,
        IReadOnlyCollection<string>? owners = null)
    {
        using var c = _db.Open();
        var w = new List<string>(); var p = new List<(string, object?)>();
        if (owners is not null)
        {
            var ids = owners.Select((o, i) => (o, i)).ToList();
            w.Add(ids.Count == 0 ? "0" : "besitzer IN (" + string.Join(",", ids.Select(x => "@o" + x.i)) + ")");
            foreach (var (o, i) in ids) p.Add(("@o" + i, o));
        }
        if (!string.IsNullOrWhiteSpace(profile)) { w.Add("profil=@p"); p.Add(("@p", profile)); }
        if (!string.IsNullOrWhiteSpace(text)) { w.Add("(quelle LIKE @q OR benutzer LIKE @q OR fehler LIKE @q OR belegnr LIKE @q OR aktionen LIKE @q OR belndx LIKE @q OR rechner LIKE @q)"); p.Add(("@q", "%" + text.Trim() + "%")); }
        if (state == "versendet") w.Add("status=1");
        else if (state == "fehler") w.Add("status IN (2,3)");
        else if (state == "offen") w.Add("status NOT IN (1,2,3)");
        else if (state == "erp-offen") w.Add("(erp_status IS NULL OR erp_status IN ('eingereiht','suchfehler','nicht gefunden'))");
        if (from is not null) { w.Add("zeit >= @f"); p.Add(("@f", BelegpostDb.Ms(from.Value))); }
        if (before is not null) { w.Add("zeit < @b"); p.Add(("@b", BelegpostDb.Ms(before.Value))); }
        if (onlyUnassigned) w.Add("erp_status IS NULL AND status IN (1,2,3)");
        p.Add(("@l", Math.Clamp(limit, 1, 5000)));
        using var cmd = BelegpostDb.Cmd(c, Select + (w.Count > 0 ? " WHERE " + string.Join(" AND ", w) : "") + " ORDER BY zeit DESC, nr DESC LIMIT @l", p.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<GotomaxxLogEntry>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Drucker (gotomaxx-Profile) mit Zählern.</summary>
    public List<object> Profiles()
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, "SELECT profil, COUNT(*), SUM(status=1), SUM(status IN (2,3)), MAX(zeit) FROM gotomaxx_protokoll GROUP BY profil ORDER BY profil COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read()) list.Add(new { profile = r.GetString(0), total = r.GetInt64(1), sent = r.GetInt64(2), errors = r.GetInt64(3), last = BelegpostDb.At(r.GetInt64(4)) });
        return list;
    }

    public List<string> ProfileNames()
    {
        try
        {
            using var c = _db.Open();
            using var cmd = BelegpostDb.Cmd(c, "SELECT DISTINCT profil FROM gotomaxx_protokoll ORDER BY profil COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            var o = _opt.CurrentValue;
            while (r.Read()) if (o.Watches(r.GetString(0))) list.Add(r.GetString(0));
            return list;
        }
        catch (Exception) { return new(); }
    }

    private static GotomaxxLogEntry Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), N(r, 4), N(r, 5), N(r, 6), BelegpostDb.At(r.GetInt64(7)), r.GetInt32(8),
        N(r, 9), N(r, 10), N(r, 11), N(r, 12), N(r, 13), N(r, 14), N(r, 15), N(r, 16), r.IsDBNull(17) ? null : r.GetInt64(17), N(r, 18), N(r, 19));

    private static string? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
