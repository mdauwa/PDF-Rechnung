using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfRechnung.Settings;

/// <summary>Portal-Benutzer – angelegt in der Oberfläche, Anmeldung über Microsoft Entra ID (E-Mail = Anmeldename).</summary>
public sealed class PortalUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>Weitere Anmeldenamen (Entra-UPN/E-Mail), falls sie von der E-Mail abweichen.</summary>
    public List<string> Logins { get; set; } = new();
    /// <summary>Darf Vorgänge versenden, verwerfen und Versanddaten ändern. Ohne dieses Recht: nur ansehen.</summary>
    public bool CanSend { get; set; } = true;
    public bool IsAdmin { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastLogin { get; set; }

    public IEnumerable<string> AllLogins() =>
        (Email is null ? Logins : Logins.Prepend(Email)).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim());
}

/// <summary>Kennwort des lokalen Administrators (Anmeldung nur direkt am Server), PBKDF2-SHA256.</summary>
public sealed class LocalAdminPassword
{
    public string Hash { get; set; } = "";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = 210_000;
    public DateTimeOffset? ChangedAt { get; set; }
}

public sealed class UserData
{
    public List<PortalUser> Users { get; set; } = new();
    public LocalAdminPassword? LocalAdmin { get; set; }
}

/// <summary>
/// Benutzer und Admin-Kennwort in users.json im Datenverzeichnis (getrennt von settings.json,
/// damit die Einstellungs-Oberfläche keine Kennwort-Hashes zu sehen bekommt).
/// </summary>
public sealed class UserStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private UserData _data;

    public UserStore(string dataDirectory)
    {
        Path = System.IO.Path.Combine(dataDirectory, "users.json");
        _data = LoadFile() ?? new UserData();
        if (!File.Exists(Path)) Save(_data);
    }

    public string Path { get; }

    private UserData? LoadFile()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<UserData>(File.ReadAllText(Path), Json) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Kopie der Daten (ohne Kennwort-Hash) zum Lesen.</summary>
    public UserData Snapshot()
    {
        lock (_lock)
        {
            var copy = JsonSerializer.Deserialize<UserData>(JsonSerializer.Serialize(_data, Json), Json)!;
            copy.LocalAdmin = null;
            return copy;
        }
    }

    public PortalUser? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return _data.Users.FirstOrDefault(u => u.Id == id);
    }

    /// <summary>Sucht den Benutzer zu einer Entra-Anmeldung (E-Mail/UPN, ohne Groß-/Kleinschreibung).</summary>
    public PortalUser? FindByLogin(IEnumerable<string?> names)
    {
        var set = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return null;
        lock (_lock) return _data.Users.FirstOrDefault(u => u.AllLogins().Any(set.Contains));
    }

    public void Update(Action<UserData> change)
    {
        lock (_lock)
        {
            change(_data);
            Save(_data);
        }
    }

    private void Save(UserData data)
    {
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>Ersetzt die Benutzerliste aus der Oberfläche. Anmeldezeitpunkte bleiben erhalten.</summary>
    public void Replace(List<PortalUser> users)
    {
        lock (_lock)
        {
            var known = _data.Users.ToDictionary(u => u.Id);
            foreach (var u in users)
            {
                if (string.IsNullOrWhiteSpace(u.Id)) u.Id = Guid.NewGuid().ToString("N")[..12];
                if (known.TryGetValue(u.Id, out var old)) u.LastLogin = old.LastLogin;
            }
            _data.Users = users;
            Save(_data);
        }
    }

    public void TouchLogin(string id) => Update(d =>
    {
        var u = d.Users.FirstOrDefault(x => x.Id == id);
        if (u is not null) u.LastLogin = DateTimeOffset.Now;
    });

    // ---------------- lokaler Administrator ----------------

    public bool HasLocalAdminPassword { get { lock (_lock) return _data.LocalAdmin is { Hash.Length: > 0 }; } }

    public void SetLocalAdminPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var pw = new LocalAdminPassword { Salt = Convert.ToBase64String(salt), ChangedAt = DateTimeOffset.Now };
        pw.Hash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, pw.Iterations, HashAlgorithmName.SHA256, 32));
        Update(d => d.LocalAdmin = pw);
    }

    public void ClearLocalAdminPassword() => Update(d => d.LocalAdmin = null);

    public bool VerifyLocalAdminPassword(string password)
    {
        LocalAdminPassword? pw;
        lock (_lock) pw = _data.LocalAdmin;
        if (pw is null || string.IsNullOrEmpty(pw.Hash)) return false;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(pw.Salt), pw.Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(pw.Hash));
    }
}
