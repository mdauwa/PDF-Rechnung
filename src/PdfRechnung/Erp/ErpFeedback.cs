using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PdfRechnung.Data;
using PdfRechnung.Documents;
using PdfRechnung.Services;
using PdfRechnung.Settings;

namespace PdfRechnung.Erp;

/// <summary>Werte, die Belegpost für die Rückgabefelder zur Verfügung stellt.</summary>
public static class ErpValues
{
    public sealed record Def(string Key, string Label, string Example, int MinLength = 0, bool NeverTruncate = false, bool Date = false, bool Time = false);

    public static readonly Def[] All =
    {
        new("statuscode", "Statuscode (3-stellig)", "110", 3, NeverTruncate: true),
        new("statustext", "Statustext", "versendet über Outlook"),
        new("datum", "Datum des Status", "15.11.2026", Date: true),
        new("uhrzeit", "Uhrzeit des Status", "10:34", Time: true),
        new("zeitstempel", "Zeitstempel des Status", "15.11.2026 10:34", Date: true),
        new("versanddatum", "Versanddatum (erster Versand)", "15.11.2026", Date: true),
        new("versandzeit", "Versandzeit (erster Versand)", "10:34", Time: true),
        new("kanal", "Versandkanal", "Outlook"),
        new("empfaenger", "Empfänger (An)", "buchhaltung@kunde.at"),
        new("anzahl", "Anzahl Versände", "2"),
        new("bediener", "Bedienernummer (3-stellig)", "007", 3, NeverTruncate: true),
        new("benutzer", "Benutzername (Portal)", "Max Muster"),
        new("vorgang", "Vorgangsnummer", "20261115-103112-a1b2c3"),
        new("statuslink", "Status-Link", "https://belegpost.firma.at:5444/s/…"),
        new("betreff", "Mail-Betreff", "Rechnung 4711"),
        new("fest", "Fester Text", ""),
        new("vorlage", "Freie Vorlage", "{Statustext} am {Datum}")
    };

    public static Def? Find(string? key) => All.FirstOrDefault(d => d.Key == key);

    /// <summary>Länge aus dem Feldnamen: Zahl nach dem letzten „_“ (BEL_3_8 → 8).</summary>
    public static int? LengthFromName(string field)
    {
        var m = Regex.Match(field.Trim(), @"_(\d{1,4})$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : null;
    }

    public static int? EffectiveLength(ErpFieldMapping m) => m.Length > 0 ? m.Length : LengthFromName(m.Field);

    /// <summary>„TT.MM.JJJJ“ (deutsch) oder .NET-Format → .NET-Format.</summary>
    public static string NetFormat(string? f, string fallback)
    {
        if (string.IsNullOrWhiteSpace(f)) return fallback;
        return f.Replace("JJJJ", "yyyy").Replace("JJ", "yy").Replace("TT", "dd").Replace("hh", "HH").Replace("SS", "ss");
    }

    /// <summary>Prüft die Zuordnungstabelle; Fehler verhindern das Speichern, Warnungen werden angezeigt.</summary>
    public static (List<string> Errors, List<string> Warnings) Validate(ErpOptions o)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in o.Fields)
        {
            var name = f.Field.Trim();
            if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]{0,63}$")) { errors.Add($"ERP-Feld „{name}“: ungültiger Feldname."); continue; }
            if (!name.StartsWith("BEL_", StringComparison.OrdinalIgnoreCase)) errors.Add($"ERP-Feld „{name}“: nur Belegkopf-Felder (BEL_…) sind erlaubt.");
            if (name.Equals("BELNDX", StringComparison.OrdinalIgnoreCase)) errors.Add("BELNDX kann nicht beschrieben werden.");
            if (!seen.Add(name)) errors.Add($"ERP-Feld „{name}“ ist doppelt zugeordnet.");
            var def = Find(f.Value);
            if (def is null) { errors.Add($"ERP-Feld „{name}“: unbekannter Wert „{f.Value}“."); continue; }
            var len = EffectiveLength(f);
            if (len is null) { errors.Add($"ERP-Feld „{name}“: Länge nicht erkennbar – bitte die Länge angeben (Feldname BEL_x_<Länge>)."); continue; }
            if (def.MinLength > 0 && len < def.MinLength) errors.Add($"ERP-Feld „{name}“ ({len} Zeichen) ist zu kurz für „{def.Label}“ ({def.MinLength} Zeichen).");
            var example = Example(def, f);
            if (example.Length > len && !def.NeverTruncate) warnings.Add($"ERP-Feld „{name}“ ({len} Zeichen): „{def.Label}“ kann länger sein (z. B. „{example}“ = {example.Length}) und wird gekürzt.");
        }
        if (o.Fields.Count(f => f.Value == "statuscode") > 1) errors.Add("Genau ein Feld darf den Statuscode bekommen.");
        foreach (var c in o.Codes)
            if (c.ErpValue.Length > 0 && !(c.ErpValue.Length == 3 && c.ErpValue.All(char.IsAsciiDigit)))
                errors.Add($"Statuscode {c.Code}: ERP-Wert „{c.ErpValue}“ muss 3-stellig numerisch sein.");
        return (errors, warnings);
    }

    private static string Example(Def d, ErpFieldMapping f) => d.Key switch
    {
        "fest" or "vorlage" => f.Format,
        _ when d.Date && d.Key == "zeitstempel" => new DateTime(2026, 12, 31, 23, 59, 0).ToString(NetFormat(f.Format, "dd.MM.yyyy HH:mm"), CultureInfo.GetCultureInfo("de-AT")),
        _ when d.Date => new DateTime(2026, 12, 31).ToString(NetFormat(f.Format, "dd.MM.yyyy"), CultureInfo.GetCultureInfo("de-AT")),
        _ when d.Time => new DateTime(2026, 12, 31, 23, 59, 59).ToString(NetFormat(f.Format, "HH:mm"), CultureInfo.GetCultureInfo("de-AT")),
        _ => d.Example
    };
}

/// <summary>Daten für die Berechnung der Rückgabewerte.</summary>
public sealed record ErpValueContext(Vorgang V, string Code, DateTimeOffset At, string? Channel, string? StatusLink);

/// <summary>Berechnet die BELEG.PUT-Felder aus der Zuordnungstabelle.</summary>
public static class ErpFieldBuilder
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-AT");

    public static string ErpCode(ErpOptions o, string code) =>
        o.Codes.FirstOrDefault(c => c.Code == code) is { ErpValue.Length: 3 } c ? c.ErpValue : code;

    public static bool ShouldWrite(ErpOptions o, string code) =>
        StatusCode.IsStatus(code) && (o.Codes.FirstOrDefault(c => c.Code == code)?.Write ?? DefaultWrite(code));

    /// <summary>Standard: „erfasst“ und „freigegeben“ werden nicht geschrieben.</summary>
    public static bool DefaultWrite(string code) => code is not (StatusCode.Erfasst or StatusCode.Freigegeben);

    public static Dictionary<string, string> Build(ErpOptions o, ErpValueContext ctx, Func<string, string?>? currentErpValue = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in o.Fields)
        {
            var field = m.Field.Trim();
            if (m.OnlyCodes.Count > 0 && !m.OnlyCodes.Contains(ctx.Code)) continue;
            if (m.OnlyIfEmpty && currentErpValue?.Invoke(field) is { Length: > 0 } existing && !string.IsNullOrWhiteSpace(existing)) continue;
            var value = Value(o, m, ctx);
            if (ErpValues.EffectiveLength(m) is { } len && value.Length > len) value = value[..len];
            result[field] = value;
        }
        return result;
    }

    public static string Value(ErpOptions o, ErpFieldMapping m, ErpValueContext ctx)
    {
        var v = ctx.V;
        string Date(DateTimeOffset? t, string def) => t is { } x ? x.ToLocalTime().ToString(ErpValues.NetFormat(m.Format, def), De) : "";
        return m.Value switch
        {
            "statuscode" => ErpCode(o, ctx.Code),
            "statustext" => StatusCode.Text(ctx.Code),
            "datum" => Date(ctx.At, "dd.MM.yyyy"),
            "uhrzeit" => Date(ctx.At, "HH:mm"),
            "zeitstempel" => Date(ctx.At, "dd.MM.yyyy HH:mm"),
            "versanddatum" => Date(v.SentAt ?? v.OutlookConfirmedAt, "dd.MM.yyyy"),
            "versandzeit" => Date(v.SentAt ?? v.OutlookConfirmedAt, "HH:mm"),
            "kanal" => ctx.Channel ?? v.SendChannel ?? "",
            "empfaenger" => string.Join(", ", v.Mail.To),
            "anzahl" => v.SendCount.ToString(CultureInfo.InvariantCulture),
            "bediener" => v.OperatorNumber ?? "",
            "benutzer" => v.OwnerName ?? v.SentBy ?? "",
            "vorgang" => v.Id,
            "statuslink" => ctx.StatusLink ?? "",
            "betreff" => v.Mail.Subject,
            "fest" => m.Format,
            "vorlage" => Template(m.Format, o, ctx),
            _ => ""
        };
    }

    private static string Template(string template, ErpOptions o, ErpValueContext ctx)
    {
        var v = ctx.V;
        string D(DateTimeOffset? t, string f) => t is { } x ? x.ToLocalTime().ToString(f, De) : "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Statuscode"] = ErpCode(o, ctx.Code), ["Statustext"] = StatusCode.Text(ctx.Code),
            ["Datum"] = D(ctx.At, "dd.MM.yyyy"), ["Zeit"] = D(ctx.At, "HH:mm"), ["Uhrzeit"] = D(ctx.At, "HH:mm"),
            ["Versanddatum"] = D(v.SentAt ?? v.OutlookConfirmedAt, "dd.MM.yyyy"), ["Versandzeit"] = D(v.SentAt ?? v.OutlookConfirmedAt, "HH:mm"),
            ["Kanal"] = ctx.Channel ?? v.SendChannel ?? "", ["Empfaenger"] = string.Join(", ", v.Mail.To), ["Empfänger"] = string.Join(", ", v.Mail.To),
            ["Bediener"] = v.OperatorNumber ?? "", ["Benutzer"] = v.OwnerName ?? v.SentBy ?? "", ["Vorgang"] = v.Id,
            ["StatusLink"] = ctx.StatusLink ?? "", ["Betreff"] = v.Mail.Subject, ["Belegnummer"] = v.VoucherId ?? "",
            ["Anzahl"] = v.SendCount.ToString(CultureInfo.InvariantCulture)
        };
        return Regex.Replace(template ?? "", @"\{([A-Za-zÄÖÜäöü]+)\}", mm => values.TryGetValue(mm.Groups[1].Value, out var x) ? x : mm.Value);
    }
}

/// <summary>Ein Auftrag der ERP-Rückmeldung (Tabelle erp_auftrag).</summary>
public sealed record ErpJob(long Id, string ErpIndex, string VorgangId, string Code, DateTimeOffset EventAt, string Status, int Attempts,
    DateTimeOffset NextAttempt, DateTimeOffset Created, DateTimeOffset? Done, string? Message, string? Fields);

public static class ErpJobStatus
{
    public const string Open = "offen", Done = "erledigt", Failed = "fehler", Superseded = "ersetzt", Discarded = "verworfen";
}

/// <summary>
/// ERP-Rückmeldung: Statuswechsel landen sofort (in Millisekunden) in der Warteschlange; ein Hintergrunddienst
/// schreibt sie nacheinander per BELEG.PUT ins ERP. Ist das ERP nicht erreichbar, wird später erneut versucht –
/// der Bedienablauf wird nie verzögert. Pro Belegindex zählt der neueste Status.
/// </summary>
public sealed class ErpFeedbackService : BackgroundService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly BelegpostDb _db;
    private readonly DocumentStore _store;
    private readonly SettingsStore _settings;
    private readonly IOptionsMonitor<ErpOptions> _opt;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ErpFeedbackService> _log;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _managerLock = new();
    private (string Key, ErpServicePassManager Manager)? _manager;

    public ErpFeedbackService(BelegpostDb db, DocumentStore store, SettingsStore settings, IOptionsMonitor<ErpOptions> opt, IServiceProvider sp,
        ILogger<ErpFeedbackService> log)
    {
        _db = db; _store = store; _settings = settings; _opt = opt; _sp = sp; _log = log;
        _store.StatusChanged += OnStatusChanged;
    }

    /// <summary>Letzter Zustand der Verbindung für die Oberfläche.</summary>
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }

    private string PassDirectory => Path.Combine(_settings.DataDirectory, "erp");

    // ---------------- Einreihen ----------------

    private void OnStatusChanged(VorgangEvent e, Vorgang v)
    {
        try
        {
            var o = _opt.CurrentValue;
            if (!o.Enabled || v.ErpIndex is null || !ErpFieldBuilder.ShouldWrite(o, e.Code) || o.Fields.Count == 0) return;
            Enqueue(v.ErpIndex, v.Id, e.Code, e.At);
        }
        catch (Exception ex) { _log.LogWarning(ex, "ERP-Rückmeldung konnte nicht eingereiht werden ({Id})", v.Id); }
    }

    public long Enqueue(string belndx, string vorgangId, string code, DateTimeOffset at)
    {
        long id;
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            var newer = BelegpostDb.Scalar(c, "SELECT COUNT(*) FROM erp_auftrag WHERE belndx=@b AND ereignis_zeit > @z AND status IN ('offen','erledigt')",
                ("@b", belndx), ("@z", BelegpostDb.Ms(at))) is long n && n > 0;
            BelegpostDb.Exec(c, "UPDATE erp_auftrag SET status='ersetzt', erledigt=@now WHERE belndx=@b AND status='offen' AND ereignis_zeit <= @z",
                ("@b", belndx), ("@z", BelegpostDb.Ms(at)), ("@now", BelegpostDb.Ms(DateTimeOffset.Now)));
            BelegpostDb.Exec(c, "INSERT INTO erp_auftrag(belndx, vorgang_id, code, ereignis_zeit, status, naechster_versuch, erstellt) VALUES(@b,@v,@c,@z,@s,@n,@n)",
                ("@b", belndx), ("@v", vorgangId), ("@c", code), ("@z", BelegpostDb.Ms(at)),
                ("@s", newer ? ErpJobStatus.Superseded : ErpJobStatus.Open), ("@n", BelegpostDb.Ms(DateTimeOffset.Now)));
            id = (long)BelegpostDb.Scalar(c, "SELECT last_insert_rowid()")!;
            tx.Commit();
        }
        _wake.Release();
        return id;
    }

    // ---------------- Abarbeiten ----------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var worked = false;
            try { worked = await RunOnceAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "ERP-Rückmeldung"); }
            if (worked) continue;
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Bearbeitet den nächsten fälligen Auftrag. true = es wurde etwas getan.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var o = _opt.CurrentValue;
        if (!o.Enabled) return false;
        ErpJob? job;
        using (var c = _db.Open())
            job = ReadJobs(c, "WHERE status='offen' AND naechster_versuch <= @n ORDER BY naechster_versuch, id LIMIT 1", ("@n", BelegpostDb.Ms(DateTimeOffset.Now))).FirstOrDefault();
        if (job is null) return false;
        await ProcessAsync(job, o, ct);
        return true;
    }

    private async Task ProcessAsync(ErpJob job, ErpOptions o, CancellationToken ct)
    {
        var v = _store.Get(job.VorgangId);
        if (v is null) { Finish(job.Id, ErpJobStatus.Discarded, "Vorgang gelöscht.", null); return; }
        try
        {
            var manager = Manager(o);
            JsonElement? current = null;
            if (o.CheckBeforeWrite)
            {
                current = await manager.ReadDocumentAsync(job.ErpIndex, ct);
                if (current is null) throw new ErpException("DocumentNotFound");
            }
            var ev = _store.Events(v.Id).LastOrDefault(e => e.Code == job.Code);
            var link = StatusLinkUrl(v);
            var fields = ErpFieldBuilder.Build(o, new ErpValueContext(v, job.Code, job.EventAt, ev?.Channel, link),
                f => current is { } cur && cur.TryGetProperty(f, out var x) ? x.ToString() : null);
            if (current is { } existing && fields.All(f => existing.TryGetProperty(f.Key, out var x) && x.ToString() == f.Value))
            {
                Finish(job.Id, ErpJobStatus.Done, "Im ERP bereits aktuell.", fields);
            }
            else if (fields.Count > 0)
            {
                await manager.WriteDocumentAsync(job.ErpIndex, fields, ct);
                Finish(job.Id, ErpJobStatus.Done, null, fields);
            }
            else Finish(job.Id, ErpJobStatus.Done, "Keine Felder für diesen Status.", fields);
            _store.AddNote(v.Id, StatusCode.ErpBestaetigt, "ERP", "ERP-Rückmeldung", ErpFieldBuilder.ErpCode(o, job.Code));
            LastSuccess = DateTimeOffset.Now;
            LastError = null;
            _db.Audit("ERP-Rückmeldung", "BELEG.PUT", job.ErpIndex, string.Join(", ", fields.Select(f => $"{f.Key}={f.Value}")));
        }
        catch (ErpException ex)
        {
            LastError = ex.Message;
            var age = DateTimeOffset.Now - job.Created;
            switch (ex.Code)
            {
                case "DocumentNotFound" or "WriteRejected" or "QueryRejected" when !ex.Transient:
                    Finish(job.Id, ErpJobStatus.Failed, ex.Message, null);
                    break;
                case "PendingApproval" or "RegistrationDisabled" or "RegistrationOutcomeUnknown" or "BindingMismatch" or "InvalidStoredPass"
                    or "PassValidationRejected" or "NotConfigured":
                    // Einrichtung fehlt: Warteschlange pausiert, Aufträge bleiben offen
                    Retry(job, ex.Message, TimeSpan.FromMinutes(10), giveUp: false);
                    break;
                default:
                    Retry(job, ex.Message, Backoff(job.Attempts), giveUp: age > TimeSpan.FromDays(Math.Max(1, o.GiveUpDays)));
                    break;
            }
            _log.LogInformation("ERP-Rückmeldung {Belndx}: {Msg}", job.ErpIndex, ex.Message);
        }
    }

    public static TimeSpan Backoff(int attempts) => attempts switch
    {
        0 => TimeSpan.FromSeconds(30),
        1 => TimeSpan.FromMinutes(2),
        2 => TimeSpan.FromMinutes(10),
        3 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(1)
    };

    private void Retry(ErpJob job, string message, TimeSpan wait, bool giveUp)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "UPDATE erp_auftrag SET versuche=versuche+1, naechster_versuch=@n, meldung=@m, status=@s, erledigt=@e WHERE id=@id",
            ("@n", BelegpostDb.Ms(DateTimeOffset.Now + wait)), ("@m", message), ("@s", giveUp ? ErpJobStatus.Failed : ErpJobStatus.Open),
            ("@e", giveUp ? BelegpostDb.Ms(DateTimeOffset.Now) : null), ("@id", job.Id));
    }

    private void Finish(long id, string status, string? message, Dictionary<string, string>? fields)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "UPDATE erp_auftrag SET status=@s, erledigt=@e, meldung=@m, felder=@f, versuche=versuche+1 WHERE id=@id",
            ("@s", status), ("@e", BelegpostDb.Ms(DateTimeOffset.Now)), ("@m", message),
            ("@f", fields is null ? null : JsonSerializer.Serialize(fields)), ("@id", id));
    }

    private string? StatusLinkUrl(Vorgang v)
    {
        var o = _opt.CurrentValue;
        if (!o.Fields.Any(f => f.Value == "statuslink" || f.Format.Contains("{StatusLink}", StringComparison.OrdinalIgnoreCase))) return null;
        return _sp.GetService<StatusLinkService>()?.UrlFor(v);
    }

    // ---------------- Verbindung ----------------

    public ErpServicePassManager Manager(ErpOptions o)
    {
        if (!o.IsConfigured) throw new ErpException("NotConfigured");
        var s = WwsSettings.From(o, _settings.Secrets.Unprotect(o.Password));
        if (s.Validate() is { Count: > 0 }) throw new ErpException("NotConfigured");
        var key = JsonSerializer.Serialize(s);
        lock (_managerLock)
        {
            if (_manager is { } m && m.Key == key) return m.Manager;
            var mgr = new ErpServicePassManager(s, new WwsServiceClient(Http, s), LoadPass, SavePass);
            _manager = (key, mgr);
            return mgr;
        }
    }

    private string PassFile(string binding) => Path.Combine(PassDirectory, "pass-" + binding[..Math.Min(32, binding.Length)].ToLowerInvariant() + ".bin");

    private ErpServicePass? LoadPass(string binding)
    {
        var f = PassFile(binding);
        if (!File.Exists(f)) return null;
        var plain = _settings.Secrets.UnprotectBytes(File.ReadAllBytes(f));
        return JsonSerializer.Deserialize<ErpServicePass>(plain, Json);
    }

    private void SavePass(string binding, ErpServicePass pass)
    {
        Directory.CreateDirectory(PassDirectory);
        var f = PassFile(binding);
        var tmp = f + ".tmp";
        File.WriteAllBytes(tmp, _settings.Secrets.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(pass, Json)));
        File.Move(tmp, f, overwrite: true);
    }

    // ---------------- Abfragen für die Oberfläche ----------------

    public List<ErpJob> Jobs(string? status, string? vorgangId, int limit = 200)
    {
        var w = new List<string>();
        var p = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(status)) { w.Add("status=@s"); p.Add(("@s", status)); }
        if (!string.IsNullOrEmpty(vorgangId)) { w.Add("vorgang_id=@v"); p.Add(("@v", vorgangId)); }
        p.Add(("@l", Math.Clamp(limit, 1, 2000)));
        using var c = _db.Open();
        return ReadJobs(c, (w.Count > 0 ? "WHERE " + string.Join(" AND ", w) : "") + " ORDER BY id DESC LIMIT @l", p.ToArray());
    }

    public Dictionary<string, long> Counts()
    {
        using var c = _db.Open();
        using var cmd = BelegpostDb.Cmd(c, "SELECT status, COUNT(*) FROM erp_auftrag GROUP BY status");
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, long>();
        while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
        return d;
    }

    public bool Retry(long id)
    {
        using var c = _db.Open();
        var n = c.CreateCommand();
        n.CommandText = "UPDATE erp_auftrag SET status='offen', naechster_versuch=@n, erstellt=@n, erledigt=NULL WHERE id=@id AND status IN ('fehler','offen','verworfen')";
        n.Parameters.AddWithValue("@n", BelegpostDb.Ms(DateTimeOffset.Now));
        n.Parameters.AddWithValue("@id", id);
        var ok = n.ExecuteNonQuery() > 0;
        if (ok) _wake.Release();
        return ok;
    }

    public bool Discard(long id)
    {
        using var c = _db.Open();
        var n = c.CreateCommand();
        n.CommandText = "UPDATE erp_auftrag SET status='verworfen', erledigt=@n WHERE id=@id AND status IN ('fehler','offen')";
        n.Parameters.AddWithValue("@n", BelegpostDb.Ms(DateTimeOffset.Now));
        n.Parameters.AddWithValue("@id", id);
        return n.ExecuteNonQuery() > 0;
    }

    /// <summary>Alle offenen Aufträge sofort versuchen (z. B. nach Freigabe im ERP).</summary>
    public void RetryNow()
    {
        using (var c = _db.Open())
            BelegpostDb.Exec(c, "UPDATE erp_auftrag SET naechster_versuch=@n WHERE status='offen'", ("@n", BelegpostDb.Ms(DateTimeOffset.Now)));
        _wake.Release();
    }

    private static List<ErpJob> ReadJobs(Microsoft.Data.Sqlite.SqliteConnection c, string tail, params (string, object?)[] p)
    {
        using var cmd = BelegpostDb.Cmd(c, "SELECT id, belndx, vorgang_id, code, ereignis_zeit, status, versuche, naechster_versuch, erstellt, erledigt, meldung, felder FROM erp_auftrag " + tail, p);
        using var r = cmd.ExecuteReader();
        var list = new List<ErpJob>();
        while (r.Read())
            list.Add(new ErpJob(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), BelegpostDb.At(r.GetInt64(4)), r.GetString(5), r.GetInt32(6),
                BelegpostDb.At(r.GetInt64(7)), BelegpostDb.At(r.GetInt64(8)), r.IsDBNull(9) ? null : BelegpostDb.At(r.GetInt64(9)),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11)));
        return list;
    }
}
