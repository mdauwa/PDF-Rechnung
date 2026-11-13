using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace PdfRechnung.Settings;

/// <summary>
/// Speicherort und Zugriff auf die Einstellungen (settings.json) im Datenverzeichnis.
/// Windows: C:\ProgramData\Belegpost (ein älteres C:\ProgramData\PdfRechnung wird übernommen) – andere Systeme / Tests: Umgebungsvariable BELEGPOST_DATA.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _lock = new();
    private static int _version;

    public SettingsStore(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(DataDirectory);
        Secrets = new SecretProtector(Path.Combine(DataDirectory, "keys"));
    }

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string CertificateDirectory => Path.Combine(DataDirectory, "cert");
    public SecretProtector Secrets { get; }

    /// <summary>Nach dem Speichern – Program.cs lädt damit die Konfiguration sofort neu.</summary>
    public event Action? Saved;

    /// <summary>Wird bei jedem Speichern erhöht (z. B. um Caches zu verwerfen).</summary>
    public static int Version => _version;

    public static string DefaultDataDirectory()
    {
        var env = Environment.GetEnvironmentVariable("BELEGPOST_DATA") ?? Environment.GetEnvironmentVariable("PDFRECHNUNG_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        if (!OperatingSystem.IsWindows()) return Path.Combine(AppContext.BaseDirectory, "data");
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dir = Path.Combine(root, "Belegpost");
        var legacy = Path.Combine(root, "PdfRechnung");
        // Ältere Installationen (vor der Umbenennung) übernehmen
        if (!Directory.Exists(dir) && File.Exists(Path.Combine(legacy, "settings.json")))
        {
            try { Directory.Move(legacy, dir); }
            catch { return legacy; } // z. B. noch vom alten Dienst geöffnet – dann vorerst weiter dort
        }
        return dir;
    }

    /// <summary>Legt settings.json mit Standardwerten an, falls sie noch nicht existiert.</summary>
    public void EnsureCreated()
    {
        if (!File.Exists(SettingsPath)) { Save(DefaultSettings.Create()); return; }
        var s = Load();
        if (DefaultSettings.Migrate(s)) Save(s);
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(SettingsPath)) return DefaultSettings.Create();
            using var fs = File.OpenRead(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(fs, Json) ?? DefaultSettings.Create();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, SettingsPath, overwrite: true);
            Interlocked.Increment(ref _version);
        }
        Saved?.Invoke();
    }

    /// <summary>Nur Administratoren und SYSTEM dürfen das Datenverzeichnis lesen (enthält Schlüssel und Zertifikate).</summary>
    [SupportedOSPlatform("windows")]
    public void RestrictAccess()
    {
        var dir = new DirectoryInfo(DataDirectory);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);
    }
}

/// <summary>Verschlüsselt Passwörter in settings.json (ASP.NET Core Data Protection, Schlüssel per DPAPI geschützt).</summary>
public sealed class SecretProtector
{
    private const string Prefix = "enc:";
    private readonly IDataProtector _protector;

    public SecretProtector(string keyDirectory)
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyDirectory), b =>
        {
            b.SetApplicationName("PdfRechnung");
            b.SetDefaultKeyLifetime(TimeSpan.FromDays(3650));
            if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi(protectToLocalMachine: true);
        });
        _protector = provider.CreateProtector("PdfRechnung.Secrets.v1");
    }

    public string? Protect(string? plain) =>
        string.IsNullOrEmpty(plain) ? null : Prefix + _protector.Protect(plain);

    /// <summary>Entschlüsselt "enc:…"-Werte; unverschlüsselte Werte (von Hand eingetragen) werden unverändert zurückgegeben.</summary>
    public string? Unprotect(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : value.StartsWith(Prefix, StringComparison.Ordinal) ? _protector.Unprotect(value[Prefix.Length..])
        : value;

    public byte[] ProtectBytes(byte[] data) => _protector.Protect(data);
    public byte[] UnprotectBytes(byte[] data) => _protector.Unprotect(data);
}
