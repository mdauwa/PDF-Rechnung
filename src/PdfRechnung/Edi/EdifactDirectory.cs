using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

namespace PdfRechnung.Edi;

/// <summary>Knoten im Nachrichtenaufbau: Segment oder Segmentgruppe (erstes Kind = Auslösesegment).</summary>
public sealed class EdiNode
{
    public string Id { get; init; } = "";
    public bool Mandatory { get; init; }
    public int Max { get; init; }
    public List<EdiNode>? Children { get; init; }
    public bool IsGroup => Children is not null;
    /// <summary>Segment, mit dem der Knoten beginnt.</summary>
    public string Trigger => Children is { Count: > 0 } c ? c[0].Trigger : Id;
}

public sealed record EdiElementDef(string Code, string Name, string Type, int Min, int Max);
public sealed record EdiPart(string Code, bool Mandatory);
public sealed record EdiSegmentDef(string Tag, string Name, List<EdiPart> Parts);
public sealed record EdiCompositeDef(string Code, string Name, List<EdiPart> Parts);
public sealed record EdiMessageDef(string Type, string? Name, List<EdiNode> Structure);

/// <summary>
/// Ein UN/EDIFACT-Verzeichnis (D.96A, D.01B, D.12A, D.20B) bzw. EANCOM 2002.
/// Daten: EDIFlow (MIT-Lizenz), umgewandelt mit tools/edi_verzeichnisse.py, eingebettet als Edi/Verzeichnisse/*.json.gz.
/// </summary>
public sealed class EdifactDirectory
{
    public string Key { get; private init; } = "";
    public string Name => Key == "EANCOM2002" ? "EANCOM 2002 (D.01B)" : $"D.{Key[1..]}";
    public Dictionary<string, EdiSegmentDef> Segments { get; } = new();
    public Dictionary<string, EdiCompositeDef> Composites { get; } = new();
    public Dictionary<string, EdiElementDef> Elements { get; } = new();
    public Dictionary<string, EdiMessageDef> Messages { get; } = new();
    public Dictionary<string, HashSet<string>> Codes { get; } = new();

    public static readonly string[] Available = { "D96A", "D01B", "D12A", "D20B", "EANCOM2002" };
    private static readonly ConcurrentDictionary<string, EdifactDirectory?> Cache = new();

    public static EdifactDirectory? Get(string key) => Cache.GetOrAdd(key.ToUpperInvariant(), Load);

    /// <summary>Verzeichnis zur Nachrichtenkennung aus UNH (S009): z. B. INVOIC:D:96A:UN:EAN008.</summary>
    public static (EdifactDirectory? Dir, string Wanted) For(string type, string? version, string? release, string? association)
    {
        var wanted = $"{version}.{release}".ToUpperInvariant();
        if (!string.Equals(version, "D", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(release)) return (null, wanted);
        var key = "D" + release.ToUpperInvariant();
        // EANCOM (EAN0xx) ist eine Teilmenge des UN-Verzeichnisses: geprüft wird gegen das vollständige Verzeichnis,
        // weil die EANCOM-Strukturdaten aus EDIFlow lückenhaft sind (z. B. LOC in SG2, DTM in SG3 fehlen).
        return (Available.Contains(key) ? Get(key) : null, wanted);
    }

    private static EdifactDirectory? Load(string key)
    {
        using var s = typeof(EdifactDirectory).Assembly.GetManifestResourceStream($"Edi.{key}.json.gz");
        if (s is null) return null;
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        var r = doc.RootElement;
        var d = new EdifactDirectory { Key = key };
        static List<EdiPart> Parts(JsonElement e) => e.EnumerateArray().Select(x => new EdiPart(x[0].GetString()!, x[1].GetInt32() == 1)).ToList();
        foreach (var p in r.GetProperty("segments").EnumerateObject())
            d.Segments[p.Name] = new EdiSegmentDef(p.Name, p.Value.GetProperty("n").GetString() ?? "", Parts(p.Value.GetProperty("e")));
        foreach (var p in r.GetProperty("composites").EnumerateObject())
            d.Composites[p.Name] = new EdiCompositeDef(p.Name, p.Value.GetProperty("n").GetString() ?? "", Parts(p.Value.GetProperty("e")));
        foreach (var p in r.GetProperty("elements").EnumerateObject())
            d.Elements[p.Name] = new EdiElementDef(p.Name, p.Value[0].GetString() ?? "", p.Value[1].GetString() ?? "an", p.Value[2].GetInt32(), p.Value[3].GetInt32());
        foreach (var p in r.GetProperty("messages").EnumerateObject())
            d.Messages[p.Name] = new EdiMessageDef(p.Name, p.Value.GetProperty("n").GetString(), p.Value.GetProperty("s").EnumerateArray().Select(Node).ToList());
        foreach (var p in r.GetProperty("codes").EnumerateObject())
            d.Codes[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        return d;
    }

    private static EdiNode Node(JsonElement e) => new()
    {
        Id = e[0].GetString()!,
        Mandatory = e[1].GetInt32() == 1,
        Max = e[2].GetInt32(),
        Children = e.GetArrayLength() > 3 ? e[3].EnumerateArray().Select(Node).ToList() : null
    };
}
