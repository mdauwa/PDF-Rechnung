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
    /// <summary>Windows- bzw. ERP-Benutzernamen, unter denen diese Person druckt (Druckauftrag oder @@pu@@).</summary>
    public List<string> PrintNames { get; set; } = new();
    /// <summary>ERP-Bedienernummern (z. B. „007“) – im Druck per @@pu007@@; führende Nullen sind egal.</summary>
    public List<string> OperatorNumbers { get; set; } = new();
    /// <summary>
    /// Welche Belege sieht der Benutzer? "eigene" = nur seine, "auswahl" = seine + die von <see cref="AlsoSees"/>,
    /// "alle" = alle Belege. Administratoren sehen immer alle.
    /// </summary>
    public string Visibility { get; set; } = "eigene";
    /// <summary>Benutzer-IDs, deren Belege zusätzlich sichtbar sind (z. B. Vertretung, Sekretariat).</summary>
    public List<string> AlsoSees { get; set; } = new();

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

/// <summary>Gekoppelter „Belegpost Melder“ auf dem PC eines Benutzers.</summary>
public sealed class MelderDevice
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SHA-256 des Zugangsschlüssels (Base64). Der Schlüssel selbst liegt nur auf dem PC.</summary>
    public string TokenHash { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
}

public sealed class UserData
{
    public List<PortalUser> Users { get; set; } = new();
    public List<MelderDevice> Devices { get; set; } = new();
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
            foreach (var d in copy.Devices) d.TokenHash = "";
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

    /// <summary>
    /// Benutzer zu einem Druck-Benutzernamen: „DOMÄNE\name“, „name@domäne“ oder „name“.
    /// Verglichen wird mit den Druck-Benutzernamen, den Anmeldenamen und der E-Mail – jeweils auch ohne Domäne.
    /// </summary>
    public PortalUser? FindByPrintName(string? printName)
    {
        if (string.IsNullOrWhiteSpace(printName)) return null;
        var full = printName.Trim();
        var shortName = Short(full);
        lock (_lock)
        {
            var active = _data.Users.Where(u => u.Active).ToList();
            if (OperatorNumber(full) is { } no)
            {
                var byNo = active.FirstOrDefault(u => u.OperatorNumbers.Any(n => OperatorNumber(n) == no));
                if (byNo is not null) return byNo;
            }
            return active.FirstOrDefault(u => u.PrintNames.Any(n => Eq(n, full)))
                ?? active.FirstOrDefault(u => u.PrintNames.Any(n => Eq(Short(n), shortName)))
                ?? active.FirstOrDefault(u => u.AllLogins().Any(n => Eq(n, full)))
                ?? (active.Where(u => u.AllLogins().Any(n => Eq(Short(n), shortName))).ToList() is { Count: 1 } one ? one[0] : null);
        }
        static bool Eq(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bedienernummer als Zahl („007“ → 7), sonst null.</summary>
    public static int? OperatorNumber(string? s) =>
        s is not null && s.Trim().Length is > 0 and <= 6 && s.Trim().All(char.IsAsciiDigit) ? int.Parse(s.Trim()) : null;

    /// <summary>„DOMÄNE\name“ → „name“, „name@firma.at“ → „name“.</summary>
    public static string Short(string name)
    {
        var n = name.Trim();
        var bs = n.LastIndexOf('\\');
        if (bs >= 0) n = n[(bs + 1)..];
        var at = n.IndexOf('@');
        if (at > 0) n = n[..at];
        return n;
    }

    // ---------------- Melder ----------------

    /// <summary>Neuen Melder koppeln. Liefert den Zugangsschlüssel (nur dieses eine Mal im Klartext).</summary>
    public (MelderDevice Device, string Token) AddDevice(string userId, string name)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var d = new MelderDevice
        {
            Id = Guid.NewGuid().ToString("N")[..12], UserId = userId, Name = name.Trim().Length == 0 ? "PC" : name.Trim()[..Math.Min(60, name.Trim().Length)],
            TokenHash = HashToken(token), Created = DateTimeOffset.Now
        };
        Update(x => x.Devices.Add(d));
        return (d, token);
    }

    public static string HashToken(string token) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Melder anhand des Schlüssels finden (nur aktive Benutzer).</summary>
    public (MelderDevice Device, PortalUser User)? FindDevice(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token.Trim());
        lock (_lock)
        {
            var d = _data.Devices.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(x.TokenHash), System.Text.Encoding.ASCII.GetBytes(hash)));
            var u = d is null ? null : _data.Users.FirstOrDefault(x => x.Id == d.UserId && x.Active);
            return d is null || u is null ? null : (d, u);
        }
    }

    public List<MelderDevice> Devices(string? userId)
    {
        lock (_lock) return _data.Devices.Where(d => userId is null || d.UserId == userId)
            .Select(d => new MelderDevice { Id = d.Id, UserId = d.UserId, Name = d.Name, Created = d.Created, LastSeen = d.LastSeen }).ToList();
    }

    public bool RemoveDevice(string id, string? userId)
    {
        var removed = false;
        Update(x => removed = x.Devices.RemoveAll(d => d.Id == id && (userId is null || d.UserId == userId)) > 0);
        return removed;
    }

    public void TouchDevice(string id)
    {
        lock (_lock)
        {
            var d = _data.Devices.FirstOrDefault(x => x.Id == id);
            if (d is null || d.LastSeen is { } ls && DateTimeOffset.Now - ls < TimeSpan.FromMinutes(5)) return;
            d.LastSeen = DateTimeOffset.Now;
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
