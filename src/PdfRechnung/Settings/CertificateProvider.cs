using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfRechnung.Settings;

public sealed record CertificateInfo(
    string Source, string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter,
    string Thumbprint, IReadOnlyList<string> DnsNames, bool SelfSigned, string? Warning);

/// <summary>
/// Liefert das Serverzertifikat für HTTPS:
/// 1. eigenes PFX (Server:CertificatePath + Passwort), sonst
/// 2. ein automatisch erzeugtes, selbst signiertes Zertifikat (verschlüsselt im Datenverzeichnis abgelegt,
///    wird bei Ablauf oder geänderten Hostnamen neu erstellt).
/// </summary>
public sealed class CertificateProvider
{
    private const string SelfSignedFile = "selfsigned.pfx.enc";
    private const string SelfSignedMarker = "PDF Rechnung selbst signiert";

    private readonly SettingsStore _store;
    private readonly bool _machineKeys;

    public CertificateProvider(SettingsStore store, bool machineKeys)
    {
        _store = store;
        _machineKeys = machineKeys;
    }

    public X509Certificate2? Current { get; private set; }
    public CertificateInfo? Info { get; private set; }

    public X509Certificate2 Load(ServerOptions server, ILogger log)
    {
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(server.CertificatePath))
        {
            var path = ResolvePath(server.CertificatePath);
            try
            {
                var cert = LoadPfx(File.ReadAllBytes(path), _store.Secrets.Unprotect(server.CertificatePassword));
                Validate(cert);
                if (cert.NotAfter < DateTime.Now) warning = $"Das Zertifikat ist am {cert.NotAfter:dd.MM.yyyy} abgelaufen.";
                else if (cert.NotAfter < DateTime.Now.AddDays(30)) warning = $"Das Zertifikat läuft am {cert.NotAfter:dd.MM.yyyy} ab.";
                return Set(cert, "PFX-Datei: " + path, warning);
            }
            catch (Exception ex)
            {
                warning = $"PFX-Zertifikat \"{path}\" konnte nicht geladen werden ({ex.Message}). Es wird das selbst signierte Zertifikat verwendet.";
                log.LogError(ex, "PFX-Zertifikat {Path} konnte nicht geladen werden – Fallback auf selbst signiertes Zertifikat", path);
            }
        }

        var self = GetOrCreateSelfSigned(server, log);
        return Set(self, "Selbst signiert (automatisch erzeugt)", warning);
    }

    /// <summary>Prüft eine hochgeladene PFX-Datei und liefert ihre Eckdaten.</summary>
    public CertificateInfo Inspect(byte[] pfx, string? password)
    {
        using var cert = LoadPfx(pfx, password);
        Validate(cert);
        return Describe(cert, "PFX-Datei", null);
    }

    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(_store.CertificateDirectory, path);

    public void DeleteSelfSigned()
    {
        var file = Path.Combine(_store.CertificateDirectory, SelfSignedFile);
        if (File.Exists(file)) File.Delete(file);
    }

    public static IReadOnlyList<string> HostNames(ServerOptions server)
    {
        var names = new List<string> { "localhost", Environment.MachineName };
        try
        {
            var fqdn = Dns.GetHostEntry(Environment.MachineName).HostName;
            if (!string.IsNullOrWhiteSpace(fqdn)) names.Add(fqdn);
        }
        catch { /* ohne DNS: nur Rechnername */ }
        try
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrWhiteSpace(domain)) names.Add($"{Environment.MachineName}.{domain}");
        }
        catch { /* egal */ }
        names.AddRange(server.AdditionalHostNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()));
        return names.Select(n => n.ToLowerInvariant())
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$"))
            .Distinct().ToList();
    }

    // ---------------- intern ----------------

    private X509Certificate2 Set(X509Certificate2 cert, string source, string? warning)
    {
        Current = cert;
        Info = Describe(cert, source, warning);
        return cert;
    }

    private X509Certificate2 GetOrCreateSelfSigned(ServerOptions server, ILogger log)
    {
        Directory.CreateDirectory(_store.CertificateDirectory);
        var file = Path.Combine(_store.CertificateDirectory, SelfSignedFile);
        var wanted = HostNames(server);

        if (File.Exists(file))
        {
            try
            {
                var existing = LoadPfx(_store.Secrets.UnprotectBytes(File.ReadAllBytes(file)), null);
                var names = DnsNames(existing);
                if (existing.NotAfter > DateTime.Now.AddDays(30) && wanted.All(w => names.Contains(w, StringComparer.OrdinalIgnoreCase)))
                    return existing;
                log.LogInformation("Selbst signiertes Zertifikat wird erneuert (Ablauf oder Hostnamen geändert).");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Selbst signiertes Zertifikat nicht lesbar – wird neu erstellt.");
            }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={Environment.MachineName}, O={SelfSignedMarker}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Serverauthentifizierung
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in wanted) san.AddDnsName(n);
        foreach (var ip in LocalAddresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        // Als PFX exportieren und neu laden – unter Windows braucht Kestrel einen nicht-flüchtigen Schlüssel.
        var pfx = created.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(file, _store.Secrets.ProtectBytes(pfx));
        log.LogInformation("Selbst signiertes Zertifikat erstellt für: {Names}", string.Join(", ", wanted));
        return LoadPfx(pfx, null);
    }

    private X509Certificate2 LoadPfx(byte[] data, string? password)
    {
        var flags = _machineKeys ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.DefaultKeySet;
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(data, password, flags);
#else
        return new X509Certificate2(data, password, flags);
#endif
    }

    private static void Validate(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException("Die PFX-Datei enthält keinen privaten Schlüssel.");
    }

    private static CertificateInfo Describe(X509Certificate2 cert, string source, string? warning) => new(
        source, cert.Subject, cert.Issuer, cert.NotBefore, cert.NotAfter, cert.Thumbprint,
        DnsNames(cert), cert.Subject == cert.Issuer, warning);

    private static List<string> DnsNames(X509Certificate2 cert)
    {
        var list = new List<string>();
        foreach (var ext in cert.Extensions)
            if (ext is X509SubjectAlternativeNameExtension san)
            {
                list.AddRange(san.EnumerateDnsNames());
                list.AddRange(san.EnumerateIPAddresses().Select(i => i.ToString()));
            }
        return list;
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            list.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)));
        }
        catch { /* egal */ }
        return list.Distinct();
    }
}
