using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfRechnung.Settings;

/// <summary>Portal-Benutzer – angelegt in der Oberfläche, Anmeldung über Microsoft Entra ID (E-Mail = Anmeldename).</summary>
/// <summary>Absender/Antwortadresse für eine ERP-Bedienernummer eines Benutzers.</summary>
public sealed class OperatorSender
{
    public string Operator { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ReplyTo { get; set; } = "";
}

public sealed class PortalUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>Weitere Anmeldenamen (Entra-UPN/E-Mail), falls sie von der E-Mail abweichen.</summary>
    public List<string> Logins { get; set; } = new();
    /// <summary>Darf Vorgänge versenden, verwerfen und Versanddaten ändern. Ohne dieses Recht: nur ansehen.</summary>
    public bool CanSend { get; set; } = true;
    /// <summary>Darf Belege lokal über das eigene Outlook senden (Entwurf über den Melder). Dann kein Autoversand seiner Belege.</summary>
    public bool SendViaOutlook { get; set; }
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
    /// <summary>Vorgabe des Admins für MAPI-Mails dieses Benutzers ("" = globaler Standard).</summary>
    public string MapiMode { get; set; } = "";
    /// <summary>Darstellung im Portal: "" = wie das System, "hell", "dunkel". Stellt der Benutzer selbst ein.</summary>
    public string Theme { get; set; } = "";

    /// <summary>Versand über den Server (Autoversand, „Senden“ im Portal): Absender für Belege dieses Benutzers ("" = Vorlage/Standard).</summary>
    public string SendFromAddress { get; set; } = "";
    public string SendFromName { get; set; } = "";
    /// <summary>Antwortadresse („Antwort an“) für Belege dieses Benutzers ("" = Vorlage/Standard).</summary>
    public string ReplyTo { get; set; } = "";
    /// <summary>Abweichender Absender/Antwortadresse je ERP-Bedienernummer (z. B. 000 = Firma A, 301 = Firma B). Leere Felder = Vorgabe oben.</summary>
    public List<OperatorSender> OperatorSenders { get; set; } = new();

    /// <summary>Absender, Name und Antwortadresse für eine ERP-Nummer: Eintrag der Nummer, sonst die Vorgabe des Benutzers.</summary>
    public (string? From, string? Name, string? ReplyTo, string? Source) SenderFor(string? operatorNumber)
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var no = UserStore.NormalizeOperator(operatorNumber);
        var e = no is null ? null : OperatorSenders.FirstOrDefault(x => UserStore.NormalizeOperator(x.Operator) == no);
        return (N(e?.FromAddress) ?? N(SendFromAddress), N(e?.FromName) ?? N(SendFromName), N(e?.ReplyTo) ?? N(ReplyTo),
            e is not null && (N(e.FromAddress) ?? N(e.FromName) ?? N(e.ReplyTo)) is not null ? $"ERP-Nr. {no} von {Name}" : $"Benutzer {Name}");
    }

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
    /// <summary>Der Melder öffnet neue Belege als E-Mail-Entwurf in Outlook – dann kein Autoversand für diesen Benutzer.</summary>
    public bool OpenInOutlook { get; set; }
    /// <summary>Computername, den der Melder meldet (Abgleich mit dem RDP-Client-Namen).</summary>
    public string? ComputerName { get; set; }
    /// <summary>MAPI-Mails auf diesem PC ("" = Vorgabe des Benutzers bzw. global).</summary>
    public string MapiMode { get; set; } = "";
    /// <summary>Wer den Wert zuletzt gesetzt hat: "benutzer" (im Melder), "admin" (überrollt) oder "" (Vorgabe).</summary>
    public string MapiSource { get; set; } = "";
    public string? MapiChangedBy { get; set; }
    public DateTimeOffset? MapiChangedAt { get; set; }
}

public sealed class UserData
{
    public List<PortalUser> Users { get; set; } = new();
    public List<MelderDevice> Devices { get; set; } = new();
    public LocalAdminPassword? LocalAdmin { get; set; }
}

/// <summary>
/// Benutzer, Melder-Geräte und Admin-Kennwort in der Datenbank (Tabellen benutzer, geraet, wert).
/// Die wenigen Datensätze werden zusätzlich im Speicher gehalten; eine ältere users.json wird einmalig übernommen.
/// </summary>
public sealed class UserStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly PdfRechnung.Data.BelegpostDb _db;
    private UserData _data;

    public UserStore(string dataDirectory)
    {
        _db = PdfRechnung.Data.BelegpostDb.For(dataDirectory);
        Path = System.IO.Path.Combine(dataDirectory, "users.json");
        _data = LoadDb();
        if (_data.Users.Count == 0 && _data.Devices.Count == 0 && _data.LocalAdmin is null && File.Exists(Path))
        {
            try
            {
                _data = JsonSerializer.Deserialize<UserData>(File.ReadAllText(Path), Json) ?? new UserData();
                Save(_data);
                File.Move(Path, Path + ".migriert", overwrite: true);
                _db.Audit("System", "Übernahme Benutzer", null, $"{_data.Users.Count} Benutzer, {_data.Devices.Count} Geräte");
            }
            catch (JsonException) { _data = new UserData(); }
        }
    }

    /// <summary>Frühere Ablage (users.json) – nur noch für die einmalige Übernahme.</summary>
    public string Path { get; }

    private UserData LoadDb()
    {
        var d = new UserData();
        using var c = _db.Open();
        using (var cmd = PdfRechnung.Data.BelegpostDb.Cmd(c, "SELECT daten FROM benutzer"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) if (JsonSerializer.Deserialize<PortalUser>(r.GetString(0), Json) is { } u) d.Users.Add(u);
        using (var cmd = PdfRechnung.Data.BelegpostDb.Cmd(c, "SELECT daten FROM geraet"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) if (JsonSerializer.Deserialize<MelderDevice>(r.GetString(0), Json) is { } g) d.Devices.Add(g);
        if (PdfRechnung.Data.BelegpostDb.Scalar(c, "SELECT daten FROM wert WHERE schluessel='lokalerAdmin'") is string la)
            d.LocalAdmin = JsonSerializer.Deserialize<LocalAdminPassword>(la, Json);
        d.Users = d.Users.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        d.Devices = d.Devices.OrderBy(x => x.Created).ToList();
        return d;
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
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        PdfRechnung.Data.BelegpostDb.Exec(c, "DELETE FROM benutzer; DELETE FROM geraet;");
        foreach (var u in data.Users)
            PdfRechnung.Data.BelegpostDb.Exec(c, "INSERT INTO benutzer(id, daten) VALUES(@i,@d)", ("@i", u.Id), ("@d", JsonSerializer.Serialize(u, Json)));
        foreach (var g in data.Devices)
            PdfRechnung.Data.BelegpostDb.Exec(c, "INSERT INTO geraet(id, benutzer_id, token_hash, daten) VALUES(@i,@u,@t,@d)",
                ("@i", g.Id), ("@u", g.UserId), ("@t", g.TokenHash), ("@d", JsonSerializer.Serialize(g, Json)));
        if (data.LocalAdmin is null) PdfRechnung.Data.BelegpostDb.Exec(c, "DELETE FROM wert WHERE schluessel='lokalerAdmin'");
        else PdfRechnung.Data.BelegpostDb.Exec(c, "INSERT OR REPLACE INTO wert(schluessel, daten) VALUES('lokalerAdmin', @d)", ("@d", JsonSerializer.Serialize(data.LocalAdmin, Json)));
        tx.Commit();
    }

    public PdfRechnung.Data.BelegpostDb Db => _db;

    /// <summary>Ersetzt die Benutzerliste aus der Oberfläche. Anmeldezeitpunkte bleiben erhalten.</summary>
    public void Replace(List<PortalUser> users)
    {
        lock (_lock)
        {
            var known = _data.Users.ToDictionary(u => u.Id);
            foreach (var u in users)
            {
                if (string.IsNullOrWhiteSpace(u.Id)) u.Id = Guid.NewGuid().ToString("N")[..12];
                if (known.TryGetValue(u.Id, out var old)) { u.LastLogin = old.LastLogin; u.Theme = old.Theme; }
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

    /// <summary>ERP-Bedienernummer vereinheitlichen: nur Ziffern, 3-stellig mit Vornullen („7“ → „007“); sonst null.</summary>
    public static string? NormalizeOperator(string? s)
    {
        // Ziffern mit beliebig vielen Vornullen („7“, „07“, „007“, „0007“) → „007“; höchstens 999
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 10 || !t.All(char.IsAsciiDigit)) return null;
        var n = int.Parse(t);
        return n > 999 ? null : n.ToString("000");
    }

    /// <summary>Benutzer zur ERP-Bedienernummer (nur aktive).</summary>
    public PortalUser? FindByOperator(string? number)
    {
        var no = NormalizeOperator(number);
        if (no is null) return null;
        lock (_lock) return _data.Users.FirstOrDefault(u => u.Active && u.OperatorNumbers.Any(n => NormalizeOperator(n) == no));
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
            .Select(d => new MelderDevice { Id = d.Id, UserId = d.UserId, Name = d.Name, Created = d.Created, LastSeen = d.LastSeen, OpenInOutlook = d.OpenInOutlook,
                ComputerName = d.ComputerName, MapiMode = d.MapiMode, MapiSource = d.MapiSource, MapiChangedBy = d.MapiChangedBy, MapiChangedAt = d.MapiChangedAt }).ToList();
    }

    public bool RemoveDevice(string id, string? userId)
    {
        var removed = false;
        Update(x => removed = x.Devices.RemoveAll(d => d.Id == id && (userId is null || d.UserId == userId)) > 0);
        return removed;
    }

    public void SetDeviceOutlook(string id, bool outlook) => Update(d =>
    {
        var x = d.Devices.FirstOrDefault(v => v.Id == id);
        if (x is not null) x.OpenInOutlook = outlook;
    });

    /// <summary>Hat dieser Benutzer einen Melder, der Belege in Outlook öffnet?</summary>
    public bool UsesOutlook(string? userId)
    {
        if (userId is null) return false;
        lock (_lock) return _data.Users.Any(u => u.Id == userId && u.Active && u.SendViaOutlook);
    }

    public void TouchDevice(string id, string? computerName = null)
    {
        lock (_lock)
        {
            var d = _data.Devices.FirstOrDefault(x => x.Id == id);
            if (d is null) return;
            var pc = string.IsNullOrWhiteSpace(computerName) ? null : computerName.Trim()[..Math.Min(63, computerName.Trim().Length)];
            var pcChanged = pc is not null && !string.Equals(pc, d.ComputerName, StringComparison.OrdinalIgnoreCase);
            if (!pcChanged && d.LastSeen is { } ls && DateTimeOffset.Now - ls < TimeSpan.FromMinutes(5)) return;
            d.LastSeen = DateTimeOffset.Now;
            if (pc is not null) d.ComputerName = pc;
            Save(_data);
        }
    }

    // ---------------- MAPI-Einstellung (Benutzer → PC) ----------------

    /// <summary>Wirksame MAPI-Einstellung für ein Gerät: Gerät → Benutzer-Vorgabe → global.</summary>
    public string EffectiveMapiMode(string userId, string? deviceId, string globalDefault)
    {
        lock (_lock)
        {
            var d = deviceId is null ? null : _data.Devices.FirstOrDefault(x => x.Id == deviceId);
            if (d is { MapiMode.Length: > 0 }) return d.MapiMode;
            var u = _data.Users.FirstOrDefault(x => x.Id == userId);
            if (u is { MapiMode.Length: > 0 }) return u.MapiMode;
            return globalDefault;
        }
    }

    /// <summary>MAPI-Einstellung eines Geräts setzen – vom Benutzer im Melder oder vom Admin (überrollen).</summary>
    public bool SetDeviceMapi(string deviceId, string mode, string source, string by)
    {
        var ok = false;
        Update(x =>
        {
            var d = x.Devices.FirstOrDefault(v => v.Id == deviceId);
            if (d is null) return;
            d.MapiMode = mode; d.MapiSource = mode.Length == 0 ? "" : source; d.MapiChangedBy = by; d.MapiChangedAt = DateTimeOffset.Now;
            ok = true;
        });
        return ok;
    }

    /// <summary>Gerät, an dem der Benutzer gerade sitzt: Computername = RDP-Client, sonst der zuletzt aktive Melder.</summary>
    public MelderDevice? TargetDevice(string userId, string? clientName)
    {
        lock (_lock)
        {
            var mine = _data.Devices.Where(d => d.UserId == userId).ToList();
            if (!string.IsNullOrWhiteSpace(clientName))
            {
                var c = clientName.Trim();
                var hit = mine.FirstOrDefault(d => string.Equals(d.ComputerName, c, StringComparison.OrdinalIgnoreCase))
                    ?? mine.FirstOrDefault(d => string.Equals(d.Name, c, StringComparison.OrdinalIgnoreCase))
                    ?? mine.Where(d => SameComputer(d.ComputerName, c) || SameComputer(d.Name, c)).OrderByDescending(d => d.LastSeen ?? d.Created).FirstOrDefault();
                if (hit is not null) return hit;
            }
            return mine.OrderByDescending(d => d.LastSeen ?? d.Created).FirstOrDefault();
        }
    }

    /// <summary>
    /// Computername des Melders ↔ RDP-Clientname. RDP-Clients schicken oft gekürzte (NetBIOS, 15 Zeichen) oder anders
    /// geschriebene Namen – z. B. Mac „Markus-MacBook-Pro.local“ ↔ „MARKUS-MACBOOK-“. Verglichen wird ohne Domäne,
    /// Satz- und Leerzeichen; ein gekürzter Name passt, wenn er der Anfang des anderen ist (mindestens 8 Zeichen).
    /// </summary>
    internal static bool SameComputer(string? deviceName, string? clientName)
    {
        var a = NormalizeComputer(deviceName);
        var b = NormalizeComputer(clientName);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 8 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static string NormalizeComputer(string? name)
    {
        var n = (name ?? "").Trim();
        var dot = n.IndexOf('.');
        if (dot > 0) n = n[..dot];
        return new string(n.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    public MelderDevice? Device(string id)
    {
        lock (_lock) return _data.Devices.FirstOrDefault(d => d.Id == id);
    }

    /// <summary>Benutzer zum Windows-Konto (DOMÄNE\name) – über Druck-Benutzernamen bzw. Anmeldenamen.</summary>
    public PortalUser? FindByWindowsAccount(string account) => FindByPrintName(account);

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
