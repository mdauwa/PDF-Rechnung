using Microsoft.Extensions.Options;

namespace PdfRechnung.Exchange;

/// <summary>
/// Verteilt die Dateien aus dem Ausgangsordner der Zentrale an die Standorte und legt Rückläufer der Standorte ab.
/// Unterordner „F01“ bzw. „Gruppe“ → an diesen Standort bzw. alle Mitglieder; Dateien direkt im Ordner → nach Verteilregeln.
/// </summary>
public sealed class FilialverbundService : BackgroundService
{
    private readonly IOptionsMonitor<FilialverbundOptions> _opt;
    private readonly ExchangeStore _store;
    private readonly string _dataDirectory;
    private readonly ILogger<FilialverbundService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Letzter Lauf: Zeit, Fehler (null = in Ordnung), Dateien ohne Empfänger.</summary>
    public (DateTimeOffset? At, string? Error, DateTimeOffset? ErrorSince, List<string> Unassigned) State { get; private set; } = (null, null, null, new());

    public FilialverbundService(IOptionsMonitor<FilialverbundOptions> opt, ExchangeStore store, Settings.SettingsStore settings, ILogger<FilialverbundService> log)
    {
        _opt = opt; _store = store; _dataDirectory = settings.DataDirectory; _log = log;
    }

    public string InFolder(FilialverbundOptions o) =>
        string.IsNullOrWhiteSpace(o.InFolder) ? Path.Combine(_dataDirectory, "filialverbund", "eingang") : o.InFolder.Trim();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (_opt.CurrentValue.Enabled && PdfRechnung.Licensing.License.Current.Exchange) await DistributeAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Einen Verteillauf ausführen. Liefert die Anzahl verteilter Dateien.</summary>
    public async Task<int> DistributeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var o = _opt.CurrentValue;
        var unassigned = new List<string>();
        var count = 0;
        try
        {
            var root = o.OutFolder.Trim();
            if (root.Length == 0) { SetState(null, unassigned); return 0; }
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Ausgangsordner Filialverbund nicht gefunden: " + root);
            var archive = string.IsNullOrWhiteSpace(o.OutArchive) ? Path.Combine(root, "verteilt") : o.OutArchive.Trim();
            var patterns = Delivery.DeliveryService.Patterns(string.IsNullOrWhiteSpace(o.Pattern) ? "*.dta" : o.Pattern);

            // Unterordner je Standort/Gruppe
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (SamePath(dir, archive) || name.Equals("verteilt", StringComparison.OrdinalIgnoreCase)) continue;
                var targets = o.Resolve(new[] { name });
                foreach (var f in Candidates(dir, patterns))
                {
                    if (targets.Count == 0) { unassigned.Add($"{name}\\{Path.GetFileName(f)} (kein Standort bzw. keine Gruppe „{name}“)"); continue; }
                    if (Distribute(f, targets, $"Ordner {name}", archive)) count++;
                }
            }
            // Dateien direkt im Ordner: Verteilregeln
            foreach (var f in Candidates(root, patterns))
            {
                var name = Path.GetFileName(f);
                var rules = o.Regeln.Where(r => !string.IsNullOrWhiteSpace(r.Pattern) && Documents.MailRuleEngine.Like(name, r.Pattern.Trim())).ToList();
                var targets = o.Resolve(rules.SelectMany(r => FilialverbundOptions.SplitList(r.Targets)));
                if (targets.Count == 0) { unassigned.Add($"{name} (keine passende Verteilregel)"); continue; }
                if (Distribute(f, targets, "Regel " + string.Join(", ", rules.Select(r => r.Pattern)), archive)) count++;
            }
            SetState(null, unassigned);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Filialverbund: Verteilung");
            SetState(ex.Message, unassigned);
        }
        finally { _gate.Release(); }
        return count;
    }

    private void SetState(string? error, List<string> unassigned) =>
        State = (DateTimeOffset.Now, error, error is null ? null : State.ErrorSince ?? DateTimeOffset.Now, unassigned);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Fertig geschriebene Dateien (nicht .part, seit 5 s unverändert), älteste zuerst.</summary>
    private static IEnumerable<string> Candidates(string dir, List<string> patterns) =>
        Directory.GetFiles(dir)
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Where(f => patterns.Count == 0 || patterns.Any(p => Documents.MailRuleEngine.Like(Path.GetFileName(f), p)))
            .Where(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromSeconds(5))
            .OrderBy(File.GetLastWriteTimeUtc);

    private bool Distribute(string path, List<Standort> targets, string source, string archive)
    {
        byte[] data;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); // noch in Benutzung → später
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (IOException) { return false; }
        var name = Path.GetFileName(path);
        var stored = _store.Keep(name, data, ExchangeStatus.Out);
        foreach (var t in targets) _store.Add(ExchangeStatus.Out, t.Code, name, data.Length, ExchangeStatus.Waiting, stored, source);
        var dir = Path.Combine(archive, DateTime.Now.ToString("yyyy-MM"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, name);
        if (File.Exists(dest)) dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(name)}");
        File.Move(path, dest);
        _log.LogInformation("Filialverbund: {File} an {Targets} verteilt", name, string.Join(", ", targets.Select(t => t.Code)));
        return true;
    }

    /// <summary>Rückläufer eines Standorts annehmen: ablegen und ggf. an andere Standorte weitergeben.</summary>
    public (long Nr, string Path, List<string> Forwarded) Receive(string standort, string fileName, byte[] data, string? to)
    {
        var o = _opt.CurrentValue;
        fileName = Path.GetFileName(fileName);
        if (fileName.Length == 0) throw new ArgumentException("Dateiname fehlt.");
        var stored = _store.Keep(fileName, data, ExchangeStatus.In);
        var dir = InFolder(o);
        if (o.InSubfolders) dir = Path.Combine(dir, Documents.MailTemplate.SafeFileName(standort));
        Directory.CreateDirectory(dir);
        var safe = Documents.MailTemplate.SafeFileName(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName);
        var dest = Path.Combine(dir, safe);
        for (var i = 1; File.Exists(dest); i++) dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(safe)}_{i}{Path.GetExtension(safe)}");
        File.WriteAllBytes(dest + ".part", data);
        File.Move(dest + ".part", dest);
        var nr = _store.Add(ExchangeStatus.In, standort, fileName, data.Length, ExchangeStatus.Stored, stored, "Client " + standort, "abgelegt in " + dest);
        var forward = o.Resolve(FilialverbundOptions.SplitList(string.IsNullOrWhiteSpace(to) ? o.Find(standort)?.ForwardTo : to))
            .Where(s => !string.Equals(s.Code, standort, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var t in forward) _store.Add(ExchangeStatus.Out, t.Code, fileName, data.Length, ExchangeStatus.Waiting, stored, "Rückläufer von " + standort);
        return (nr, dest, forward.Select(f => f.Code).ToList());
    }
}
