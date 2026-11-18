using System.Collections.Concurrent;

namespace PdfRechnung.Documents;

/// <summary>
/// Verschiebt Vorgänge in den Ordner, in den sie gehören (eingestellter Belegordner → Mandant → JJJJ → MM).
/// - auf Knopfdruck alle vorhandenen Vorgänge (im Hintergrund, in Blöcken, abbrechbar, Fortschritt abrufbar)
/// - einzelne Vorgänge automatisch, wenn sich ihr Mandant ändert
/// Kopiert wird vor dem Umstellen in der Datenbank; der alte Ordner wird erst danach gelöscht.
/// </summary>
public sealed class DocumentRelocator : BackgroundService
{
    private readonly DocumentStore _store;
    private readonly ILogger<DocumentRelocator> _log;
    private readonly ConcurrentQueue<string> _single = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private readonly List<string> _leftovers = new();

    public DocumentRelocator(DocumentStore store, ILogger<DocumentRelocator> log)
    {
        _store = store; _log = log;
        _store.MandantChanged += id => { _single.Enqueue(id); _wake.Release(); };
    }

    public sealed record Progress(bool Running, long Checked, long Moved, long Skipped, long Missing, long Failed, string? LastError,
        DateTimeOffset? Started, DateTimeOffset? Finished, int Leftovers, bool Cancelled);

    private long _checked, _moved, _skipped, _missing, _failed;
    private string? _lastError;
    private DateTimeOffset? _started, _finished;
    private bool _running, _cancelled;

    public Progress State()
    {
        lock (_gate) return new(_running, _checked, _moved, _skipped, _missing, _failed, _lastError, _started, _finished, _leftovers.Count, _cancelled);
    }

    /// <summary>Alle Vorgänge prüfen und bei Bedarf verschieben. false = läuft schon.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_running) return false;
            _running = true; _cancelled = false;
            _checked = _moved = _skipped = _missing = _failed = 0; _lastError = null;
            _started = DateTimeOffset.Now; _finished = null;
            _runCts = new CancellationTokenSource();
        }
        var ct = _runCts.Token;
        _ = Task.Run(() => RunAll(ct));
        return true;
    }

    public void Stop() { lock (_gate) { _runCts?.Cancel(); } }

    private async Task RunAll(CancellationToken ct)
    {
        try
        {
            string? after = null;
            while (!ct.IsCancellationRequested)
            {
                var block = _store.IdsAfter(after, 200);
                if (block.Count == 0) break;
                foreach (var (id, _) in block)
                {
                    if (ct.IsCancellationRequested) break;
                    One(id);
                }
                after = block[^1].Id;
                await Task.Delay(20, CancellationToken.None); // Platte/Netz nicht dauerhaft auslasten
            }
            RetryLeftovers();
        }
        catch (Exception ex) { lock (_gate) _lastError = ex.Message; _log.LogWarning(ex, "Verschieben der Belege abgebrochen"); }
        finally
        {
            lock (_gate) { _running = false; _cancelled = ct.IsCancellationRequested; _finished = DateTimeOffset.Now; }
            _log.LogInformation("Belege verschieben: {Checked} geprüft, {Moved} verschoben, {Failed} Fehler", _checked, _moved, _failed);
        }
    }

    private void One(string id)
    {
        try
        {
            var r = _store.Relocate(id, out var leftover, out var oldDir);
            lock (_gate)
            {
                _checked++;
                switch (r)
                {
                    case DocumentStore.RelocateResult.Moved: _moved++; break;
                    case DocumentStore.RelocateResult.Skipped: _skipped++; break;
                    case DocumentStore.RelocateResult.Missing: _missing++; break;
                }
                if (leftover is not null) _leftovers.Add(leftover);
            }
            if (r == DocumentStore.RelocateResult.Moved && leftover is null && oldDir is not null) _store.RemoveEmptyParents(oldDir);
        }
        catch (Exception ex)
        {
            lock (_gate) { _checked++; _failed++; _lastError = $"{id}: {ex.Message}"; }
            _log.LogWarning(ex, "Vorgang {Id} konnte nicht verschoben werden", id);
        }
    }

    /// <summary>Alte Ordner, die beim ersten Versuch nicht gelöscht werden konnten (Datei war offen), erneut löschen.</summary>
    private void RetryLeftovers()
    {
        List<string> list;
        lock (_gate) { list = _leftovers.ToList(); _leftovers.Clear(); }
        foreach (var d in list)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); _store.RemoveEmptyParents(d); }
            catch { lock (_gate) _leftovers.Add(d); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(10), stoppingToken); } catch (OperationCanceledException) { return; }
            // kurz warten: oft folgen mehrere Änderungen am selben Vorgang
            try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { return; }
            var seen = new HashSet<string>();
            while (_single.TryDequeue(out var id))
                if (seen.Add(id)) One(id);
            if (!State().Running) RetryLeftovers();
        }
    }
}
