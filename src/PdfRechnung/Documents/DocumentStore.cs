using System.Text.Json;
using System.Text.Json.Serialization;
using PdfRechnung.Settings;

namespace PdfRechnung.Documents;

/// <summary>
/// Ablage der Vorgänge: je Vorgang ein Ordner vorgaenge\JJJJ\MM\&lt;Id&gt; mit meta.json, beleg.pdf und text.txt.
/// Alle Metadaten werden beim Start eingelesen und im Speicher gehalten.
/// </summary>
public sealed class DocumentStore
{
    public const string PdfFile = "beleg.pdf", TextFile = "text.txt", MetaFile = "meta.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, (Vorgang V, string Dir)> _items = new();
    private readonly ILogger<DocumentStore> _log;

    public DocumentStore(SettingsStore settings, ILogger<DocumentStore> log)
    {
        _log = log;
        Root = Path.Combine(settings.DataDirectory, "vorgaenge");
        Directory.CreateDirectory(Root);
        Load();
    }

    public string Root { get; }
    public event Action<Vorgang>? Changed;

    private void Load()
    {
        foreach (var meta in Directory.EnumerateFiles(Root, MetaFile, SearchOption.AllDirectories))
        {
            try
            {
                var v = JsonSerializer.Deserialize<Vorgang>(File.ReadAllText(meta), Json);
                if (v is not null && !string.IsNullOrEmpty(v.Id))
                {
                    // Ein beim Beenden laufender Versand gilt als nicht versendet
                    if (v.Status == VorgangStatus.Sending) { v.Status = VorgangStatus.SendError; v.SendError = "Versand wurde unterbrochen (Dienst beendet)."; }
                    _items[v.Id] = (v, Path.GetDirectoryName(meta)!);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Vorgang {Meta} konnte nicht gelesen werden", meta); }
        }
        _log.LogInformation("{Count} Vorgänge geladen aus {Root}", _items.Count, Root);
    }

    public static string NewId(DateTimeOffset at) => $"{at:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>Legt einen neuen Vorgang mit seinen Dateien an.</summary>
    public void Create(Vorgang v, byte[] pdf, string text)
    {
        var dir = Path.Combine(Root, v.CreatedAt.ToString("yyyy"), v.CreatedAt.ToString("MM"), v.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, PdfFile), pdf);
        File.WriteAllText(Path.Combine(dir, TextFile), text);
        lock (_lock)
        {
            _items[v.Id] = (v, dir);
            WriteMeta(v, dir);
        }
        Changed?.Invoke(v);
    }

    /// <summary>Kopie eines Vorgangs (Änderungen nur über <see cref="Update"/>).</summary>
    public Vorgang? Get(string id)
    {
        lock (_lock) return _items.TryGetValue(id, out var x) ? Clone(x.V) : null;
    }

    public string? Directory_(string id)
    {
        lock (_lock) return _items.TryGetValue(id, out var x) ? x.Dir : null;
    }

    public string? FilePath(string id, string file)
    {
        var dir = Directory_(id);
        if (dir is null) return null;
        var p = Path.Combine(dir, file);
        return File.Exists(p) ? p : null;
    }

    public List<Vorgang> All()
    {
        lock (_lock) return _items.Values.Select(x => x.V).OrderByDescending(v => v.CreatedAt).Select(Clone).ToList();
    }

    /// <summary>Ändert einen Vorgang unter Sperre und speichert ihn. Liefert die neue Fassung oder null.</summary>
    public Vorgang? Update(string id, Func<Vorgang, bool> change)
    {
        Vorgang copy;
        lock (_lock)
        {
            if (!_items.TryGetValue(id, out var x)) return null;
            if (!change(x.V)) return Clone(x.V);
            WriteMeta(x.V, x.Dir);
            copy = Clone(x.V);
        }
        Changed?.Invoke(copy);
        return copy;
    }

    public bool Delete(string id)
    {
        string dir;
        lock (_lock)
        {
            if (!_items.Remove(id, out var x)) return false;
            dir = x.Dir;
        }
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) { _log.LogWarning(ex, "Ordner {Dir} konnte nicht gelöscht werden", dir); }
        return true;
    }

    private static void WriteMeta(Vorgang v, string dir)
    {
        var file = Path.Combine(dir, MetaFile);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(v, Json));
        File.Move(tmp, file, overwrite: true);
    }

    private static Vorgang Clone(Vorgang v) => JsonSerializer.Deserialize<Vorgang>(JsonSerializer.Serialize(v, Json), Json)!;
}
