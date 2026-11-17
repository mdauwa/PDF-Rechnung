using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfRechnung.Api;

/// <summary>API-Schlüssel für die REST-API (Überwachung, Konnektoren). Gespeichert wird nur der SHA-256-Hash.</summary>
public sealed class ApiKey
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Erste Zeichen des Schlüssels zur Wiedererkennung (z. B. „bp_3fa9…“).</summary>
    public string Prefix { get; set; } = "";
    public string Hash { get; set; } = "";
    /// <summary>„status“ = Status abfragen, „austausch“ = Dateien austauschen (Konnektor/Filial-Client).</summary>
    public List<string> Scopes { get; set; } = new();
    /// <summary>Bei „austausch“: Standort-Kürzel, für den dieser Schlüssel Dateien abholt und hochlädt.</summary>
    public string Standort { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public string? LastAddress { get; set; }

    public const string ScopeStatus = "status", ScopeExchange = "austausch";
    public bool Has(string scope) => Active && Scopes.Contains(scope);
}

public sealed class ApiKeyStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private List<ApiKey> _items;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ApiKeyStore(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "apikeys.json");
        try { _items = File.Exists(_file) ? JsonSerializer.Deserialize<List<ApiKey>>(File.ReadAllText(_file), Json) ?? new() : new(); }
        catch (Exception) { _items = new(); }
    }

    public static string HashOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    public List<ApiKey> All() { lock (_lock) return _items.Select(Copy).ToList(); }

    /// <summary>Neuen Schlüssel anlegen. Der Klartext wird nur hier einmal zurückgegeben.</summary>
    public (ApiKey Key, string Secret) Create(string name, IEnumerable<string> scopes, string standort)
    {
        var secret = "bp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var k = new ApiKey
        {
            Id = Guid.NewGuid().ToString("N")[..10], Name = name.Trim(), Prefix = secret[..9], Hash = HashOf(secret),
            Scopes = scopes.Where(s => s is ApiKey.ScopeStatus or ApiKey.ScopeExchange).Distinct().ToList(), Standort = standort.Trim(), Created = DateTimeOffset.Now
        };
        lock (_lock) { _items.Add(k); Save(); }
        return (Copy(k), secret);
    }

    public bool Update(string id, string name, IEnumerable<string> scopes, string standort, bool active)
    {
        lock (_lock)
        {
            var k = _items.FirstOrDefault(x => x.Id == id);
            if (k is null) return false;
            k.Name = name.Trim(); k.Scopes = scopes.Where(s => s is ApiKey.ScopeStatus or ApiKey.ScopeExchange).Distinct().ToList();
            k.Standort = standort.Trim(); k.Active = active;
            Save();
            return true;
        }
    }

    public bool Delete(string id) { lock (_lock) { var n = _items.RemoveAll(x => x.Id == id); if (n > 0) Save(); return n > 0; } }

    /// <summary>Schlüssel aus der Anfrage prüfen (Header „X-Api-Key“ oder „Authorization: Bearer …“).</summary>
    public ApiKey? Authenticate(HttpContext ctx)
    {
        string? key = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key) && auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = auth[7..];
        if (string.IsNullOrWhiteSpace(key)) return null;
        var hash = HashOf(key);
        lock (_lock)
        {
            var k = _items.FirstOrDefault(x => x.Active && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.Hash), Encoding.ASCII.GetBytes(hash)));
            if (k is null) return null;
            var now = DateTimeOffset.Now;
            var addr = ctx.Connection.RemoteIpAddress?.ToString();
            if (k.LastUsed is null || now - k.LastUsed > TimeSpan.FromMinutes(5) || k.LastAddress != addr) { k.LastUsed = now; k.LastAddress = addr; Save(); }
            return Copy(k);
        }
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
        File.Move(tmp, _file, true);
    }

    private static ApiKey Copy(ApiKey k) => new()
    {
        Id = k.Id, Name = k.Name, Prefix = k.Prefix, Hash = k.Hash, Scopes = k.Scopes.ToList(), Standort = k.Standort, Active = k.Active,
        Created = k.Created, LastUsed = k.LastUsed, LastAddress = k.LastAddress
    };
}
