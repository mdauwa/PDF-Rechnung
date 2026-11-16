using System.Security.Cryptography;
using System.Text;
using FluentFTP;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using Microsoft.Extensions.Options;
using MimeKit;
using Renci.SshNet;

namespace PdfRechnung.Delivery;

/// <summary>Ein Abruf-Auftrag: globaler Versandweg oder Versandweg eines Kunden mit eingeschaltetem Abruf.</summary>
public sealed record InboundJob(string Key, string Label, ChannelSettings Settings);

/// <summary>Ergebnis des letzten Abrufs (für die Anzeige).</summary>
public sealed record InboundRun(string Key, string Label, DateTimeOffset At, bool Ok, int Files, string Message);

/// <summary>
/// Holt regelmäßig Dateien ab: FTP/FTPS/SFTP (Ordner am Server), eXite (Befehlsdatei ser(H)), E-Mail per IMAP/POP3 (Anhänge).
/// AS2 kommt per HTTP herein (<see cref="As2Receiver"/>). Die Dateien landen im Zielordner des Versandwegs.
/// </summary>
public sealed class InboundService : BackgroundService
{
    private readonly IOptionsMonitor<DeliveryOptions> _opt;
    private readonly CustomerStore _customers;
    private readonly Settings.SettingsStore _settings;
    private readonly ILogger<InboundService> _log;
    private readonly Dictionary<string, DateTimeOffset> _due = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InboundRun> _runs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InboundService(IOptionsMonitor<DeliveryOptions> opt, CustomerStore customers, Settings.SettingsStore settings, ILogger<InboundService> log)
    {
        _opt = opt; _customers = customers; _settings = settings; _log = log;
    }

    public IReadOnlyList<InboundRun> Runs() => _runs.Values.OrderBy(r => r.Label).ToList();

    /// <summary>Alle Abrufe: global und je Kunde (gleiche Quelle und gleiches Ziel nur einmal).</summary>
    public List<InboundJob> Jobs()
    {
        var o = _opt.CurrentValue;
        var list = new List<InboundJob>();
        foreach (var g in o.Channels)
        {
            var s = ChannelSettings.Merge(null, g);
            if (Active(s)) list.Add(new InboundJob("global-" + g.Type, $"{ChannelType.Text(g.Type)} (global)", s));
        }
        foreach (var c in _customers.All().Where(c => c.Active))
            foreach (var ch in c.Channels.Where(x => x.Enabled))
            {
                var s = ChannelSettings.Merge(o.Global(ch.Settings.Type), ch.Settings);
                if (Active(s)) list.Add(new InboundJob($"kunde-{c.Id}-{ch.Id}", $"{c.Label}: {ch.Label}", s));
            }
        return list.GroupBy(j => Identity(j.Settings)).Select(g => g.First()).ToList();
    }

    /// <summary>Abruf ein oder Ausgangsordner gesetzt.</summary>
    private static bool Active(ChannelSettings s) =>
        (s.InboundEnabled == "ja" && Supported(s.Type)) || (!string.IsNullOrWhiteSpace(s.OutboxFolder) && OutboxSupported(s.Type));

    private static bool OutboxSupported(string type) => type is ChannelType.Ftp or ChannelType.Ftps or ChannelType.Sftp or ChannelType.Exite or ChannelType.As2;

    private static bool Supported(string type) => type is ChannelType.Ftp or ChannelType.Ftps or ChannelType.Sftp or ChannelType.Exite or ChannelType.Mail;

    private static string Identity(ChannelSettings s) => string.Join("|", s.Type, s.Host.ToLowerInvariant(), s.Port, s.User.ToLowerInvariant(), s.InboundFolder, s.InboundPattern, s.InboundTarget.ToLowerInvariant(), s.MailProtocol, s.OutboxFolder.ToLowerInvariant());

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var job in Jobs())
                {
                    if (ct.IsCancellationRequested) break;
                    if (_due.TryGetValue(job.Key, out var due) && due > DateTimeOffset.Now) continue;
                    _due[job.Key] = DateTimeOffset.Now.AddMinutes(Math.Max(1, job.Settings.InboundInterval));
                    await RunAsync(job, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Abruf"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Einen Abruf ausführen (auch „Jetzt abrufen“ aus den Einstellungen).</summary>
    public async Task<InboundRun> RunAsync(InboundJob job, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var s = job.Settings;
            var password = _settings.Secrets.Unprotect(s.Password);
            var sent = !string.IsNullOrWhiteSpace(s.OutboxFolder) && OutboxSupported(s.Type) ? await OutboxAsync(s, password, ct) : new List<string>();
            if (s.InboundEnabled != "ja" || !Supported(s.Type))
                return _runs[job.Key] = new InboundRun(job.Key, job.Label, DateTimeOffset.Now, true, sent.Count,
                    sent.Count == 0 ? "Ausgangsordner leer" : $"{sent.Count} Datei(en) gesendet: {string.Join(", ", sent.Take(5))}{(sent.Count > 5 ? " …" : "")}");
            var target = Target(s);
            Directory.CreateDirectory(target);
            var files = s.Type switch
            {
                ChannelType.Ftp or ChannelType.Ftps => await FtpAsync(s, password, s.Type == ChannelType.Ftps, target, job.Key, ct),
                ChannelType.Sftp => await Task.Run(() => Sftp(s, password, target, job.Key, ct), ct),
                ChannelType.Exite => await ExiteAsync(s, password, target, ct),
                ChannelType.Mail => await MailAsync(s, password, target, job.Key, ct),
                _ => new List<string>()
            };
            var run = new InboundRun(job.Key, job.Label, DateTimeOffset.Now, true, files.Count + sent.Count,
                (sent.Count > 0 ? $"{sent.Count} gesendet · " : "") +
                (files.Count == 0 ? "keine neuen Dateien" : $"{files.Count} Datei(en) nach {target}: {string.Join(", ", files.Take(5))}{(files.Count > 5 ? " …" : "")}"));
            if (files.Count > 0) _log.LogInformation("Abruf {Label}: {Count} Datei(en) nach {Target}", job.Label, files.Count, target);
            return _runs[job.Key] = run;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Abruf {Label} fehlgeschlagen", job.Label);
            return _runs[job.Key] = new InboundRun(job.Key, job.Label, DateTimeOffset.Now, false, 0, ex.Message);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Ausgangsordner: jede passende Datei über den Versandweg senden und danach in den Archivordner verschieben.</summary>
    private async Task<List<string>> OutboxAsync(ChannelSettings s, string? password, CancellationToken ct)
    {
        var dir = s.OutboxFolder.Trim();
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Ausgangsordner nicht gefunden: " + dir);
        var archive = string.IsNullOrWhiteSpace(s.OutboxArchive) ? Path.Combine(dir, "gesendet") : s.OutboxArchive.Trim();
        var patterns = DeliveryService.Patterns(s.OutboxPattern);
        var sent = new List<string>();
        foreach (var path in Directory.GetFiles(dir).OrderBy(File.GetLastWriteTimeUtc))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || (patterns.Count > 0 && !patterns.Any(p => Documents.MailRuleEngine.Like(name, p)))) continue;
            byte[] data;
            try { data = await File.ReadAllBytesAsync(path, ct); } catch (IOException) { continue; } // noch in Benutzung
            var file = new DeliveryFile(name, data, MimeTypes.For(name));
            switch (s.Type)
            {
                case ChannelType.Exite: await ExiteTransport.SendAsync(s, password, file, ct); break;
                case ChannelType.As2: await As2Transport.SendAsync(s, _settings.Secrets.Unprotect(s.OwnCertificatePassword), file, ct); break;
                default:
                    var up = ChannelSettings.Merge(null, s);
                    await FileTransfer.UploadAsync(s.Type, up, password, new[] { file }, ct);
                    break;
            }
            Directory.CreateDirectory(archive);
            var dest = Path.Combine(archive, name);
            if (File.Exists(dest)) dest = Path.Combine(archive, $"{Path.GetFileNameWithoutExtension(name)}_{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(name)}");
            File.Move(path, dest);
            sent.Add(name);
        }
        return sent;
    }

    /// <summary>Zielordner: eingestellt, sonst daten\abruf\&lt;Versandweg&gt;.</summary>
    public string Target(ChannelSettings s) =>
        string.IsNullOrWhiteSpace(s.InboundTarget) ? Path.Combine(_settings.DataDirectory, "abruf", s.Type) : s.InboundTarget.Trim();

    private static bool Matches(ChannelSettings s, string name)
    {
        var p = DeliveryService.Patterns(s.InboundPattern);
        return p.Count == 0 || p.Any(x => Documents.MailRuleEngine.Like(name, x));
    }

    /// <summary>Datei im Zielordner ablegen, ohne Vorhandenes zu überschreiben. Erst als .part, dann umbenennen (der Eingangsordner sieht nie halbe Dateien).</summary>
    private static string Save(string target, string name, byte[] data)
    {
        name = Documents.MailTemplate.SafeFileName(Path.GetFileNameWithoutExtension(name)) + Path.GetExtension(name);
        var path = Path.Combine(target, name);
        for (var i = 1; File.Exists(path); i++) path = Path.Combine(target, $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");
        File.WriteAllBytes(path + ".part", data);
        File.Move(path + ".part", path);
        return Path.GetFileName(path);
    }

    // ---- „behalten“: schon geholte Dateien merken ----
    private string SeenFile(string key) => Path.Combine(_settings.DataDirectory, "abruf", "gesehen-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".txt");
    private HashSet<string> Seen(string key) => File.Exists(SeenFile(key)) ? File.ReadAllLines(SeenFile(key)).ToHashSet() : new();
    private void Remember(string key, HashSet<string> seen)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeenFile(key))!);
        File.WriteAllLines(SeenFile(key), seen.TakeLast(5000));
    }

    private static string Remote(ChannelSettings s) => (s.InboundFolder ?? "").Trim().Replace('\\', '/').TrimEnd('/');

    // ---------------- FTP / FTPS ----------------
    private async Task<List<string>> FtpAsync(ChannelSettings s, string? password, bool tls, string target, string key, CancellationToken ct)
    {
        var port = s.Port > 0 ? s.Port : tls && s.ImplicitTls ? 990 : 21;
        using var client = new AsyncFtpClient(s.Host.Trim(), string.IsNullOrWhiteSpace(s.User) ? "anonymous" : s.User.Trim(), password ?? "", port);
        client.Config.EncryptionMode = !tls ? FtpEncryptionMode.None : s.ImplicitTls ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        client.Config.DataConnectionType = s.ActiveMode ? FtpDataConnectionType.AutoActive : FtpDataConnectionType.AutoPassive;
        client.Config.DownloadDataType = FtpDataType.Binary;
        client.Config.ConnectTimeout = 20000;
        if (tls)
        {
            var expected = new string((s.Fingerprint ?? "").Where(Uri.IsHexDigit).ToArray());
            client.ValidateCertificate += (_, e) => e.Accept = expected.Length > 0
                ? e.Certificate is not null && string.Equals(Convert.ToHexString(SHA256.HashData(e.Certificate.GetRawCertData())), expected, StringComparison.OrdinalIgnoreCase)
                : e.PolicyErrors == System.Net.Security.SslPolicyErrors.None;
        }
        await client.Connect(ct);
        var folder = Remote(s);
        var seen = s.InboundAfter == "behalten" ? Seen(key) : null;
        var got = new List<string>();
        foreach (var item in await client.GetListing(folder, ct))
        {
            if (item.Type != FtpObjectType.File || item.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !Matches(s, item.Name)) continue;
            var id = $"{item.Name}|{item.Size}|{item.Modified:O}";
            if (seen?.Contains(id) == true) continue;
            var data = await client.DownloadBytes(item.FullName, ct) ?? throw new IOException($"{item.Name} konnte nicht geladen werden.");
            got.Add(Save(target, item.Name, data));
            switch (s.InboundAfter)
            {
                case "verschieben" when !string.IsNullOrWhiteSpace(s.InboundArchive):
                    var archive = s.InboundArchive.Trim().Replace('\\', '/').TrimEnd('/');
                    if (!await client.DirectoryExists(archive, ct)) await client.CreateDirectory(archive, true, ct);
                    await client.MoveFile(item.FullName, archive + "/" + item.Name, FtpRemoteExists.Overwrite, ct);
                    break;
                case "behalten": seen!.Add(id); break;
                default: await client.DeleteFile(item.FullName, ct); break;
            }
        }
        if (seen is not null) Remember(key, seen);
        await client.Disconnect(ct);
        return got;
    }

    // ---------------- SFTP ----------------
    private List<string> Sftp(ChannelSettings s, string? password, string target, string key, CancellationToken ct)
    {
        using var client = new SftpClient(s.Host.Trim(), s.Port > 0 ? s.Port : 22, s.User.Trim(), password ?? "");
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        var expected = (s.Fingerprint ?? "").Trim();
        if (expected.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) expected = expected[7..];
        expected = expected.TrimEnd('=');
        client.HostKeyReceived += (_, e) => e.CanTrust = expected.Length == 0 || string.Equals(e.FingerPrintSHA256.TrimEnd('='), expected, StringComparison.Ordinal);
        client.Connect();
        var folder = Remote(s);
        var seen = s.InboundAfter == "behalten" ? Seen(key) : null;
        var got = new List<string>();
        foreach (var f in client.ListDirectory(folder.Length == 0 ? "." : folder))
        {
            ct.ThrowIfCancellationRequested();
            if (!f.IsRegularFile || f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !Matches(s, f.Name)) continue;
            var id = $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc:O}";
            if (seen?.Contains(id) == true) continue;
            using var ms = new MemoryStream();
            client.DownloadFile(f.FullName, ms);
            got.Add(Save(target, f.Name, ms.ToArray()));
            switch (s.InboundAfter)
            {
                case "verschieben" when !string.IsNullOrWhiteSpace(s.InboundArchive):
                    var archive = s.InboundArchive.Trim().Replace('\\', '/').TrimEnd('/');
                    if (!client.Exists(archive)) client.CreateDirectory(archive);
                    var dest = archive + "/" + f.Name;
                    if (client.Exists(dest)) client.DeleteFile(dest);
                    client.RenameFile(f.FullName, dest);
                    break;
                case "behalten": seen!.Add(id); break;
                default: client.DeleteFile(f.FullName); break;
            }
        }
        if (seen is not null) Remember(key, seen);
        client.Disconnect();
        return got;
    }

    // ---------------- eXite: Befehlsdatei ser(H) so(1), Ergebnis in R… ----------------
    private static async Task<List<string>> ExiteAsync(ChannelSettings s, string? password, string target, CancellationToken ct)
    {
        var got = new List<string>();
        foreach (var (name, data) in await ExiteTransport.ReceiveAsync(s, password, 50, ct)) got.Add(Save(target, name, data));
        return got;
    }

    // ---------------- E-Mail: IMAP(S) / POP3(S), Anhänge ----------------
    private async Task<List<string>> MailAsync(ChannelSettings s, string? password, string target, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) throw new InvalidOperationException("Kein Mail-Server für den Abruf eingetragen.");
        var got = new List<string>();
        void TakeAttachments(MimeMessage m)
        {
            foreach (var a in m.Attachments.OfType<MimePart>())
            {
                var name = a.FileName ?? "anhang" + (a.ContentType.MimeType == "application/pdf" ? ".pdf" : ".bin");
                if (!Matches(s, name)) continue;
                using var ms = new MemoryStream();
                a.Content.DecodeTo(ms);
                got.Add(Save(target, name, ms.ToArray()));
            }
        }
        var ssl = s.MailProtocol is MailProtocol.Imaps or MailProtocol.Pop3s;
        var tls = ssl ? MailKit.Security.SecureSocketOptions.SslOnConnect : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable;
        if (s.MailProtocol is MailProtocol.Pop3 or MailProtocol.Pop3s)
        {
            using var pop = new Pop3Client();
            await pop.ConnectAsync(s.Host.Trim(), s.Port > 0 ? s.Port : ssl ? 995 : 110, tls, ct);
            await pop.AuthenticateAsync(s.User.Trim(), password ?? "", ct);
            var seen = s.InboundAfter == "behalten" ? Seen(key) : null;
            var uids = pop.SupportsUids ? await pop.GetMessageUidsAsync(ct) : null;
            for (var i = 0; i < pop.Count; i++)
            {
                var uid = uids?[i];
                if (uid is not null && seen?.Contains(uid) == true) continue;
                TakeAttachments(await pop.GetMessageAsync(i, ct));
                if (s.InboundAfter == "behalten") { if (uid is not null) seen!.Add(uid); }
                else await pop.DeleteMessageAsync(i, ct); // POP3 kennt keine Ordner: „verschieben“ = löschen
            }
            if (seen is not null) Remember(key, seen);
            await pop.DisconnectAsync(true, ct);
            return got;
        }
        using var imap = new ImapClient();
        await imap.ConnectAsync(s.Host.Trim(), s.Port > 0 ? s.Port : ssl ? 993 : 143, tls, ct);
        await imap.AuthenticateAsync(s.User.Trim(), password ?? "", ct);
        var box = string.IsNullOrWhiteSpace(s.InboundFolder) ? imap.Inbox : await imap.GetFolderAsync(s.InboundFolder.Trim(), ct);
        await box.OpenAsync(FolderAccess.ReadWrite, ct);
        IMailFolder? archive = s.InboundAfter == "verschieben" && !string.IsNullOrWhiteSpace(s.InboundArchive) ? await imap.GetFolderAsync(s.InboundArchive.Trim(), ct) : null;
        foreach (var uid in await box.SearchAsync(SearchQuery.NotSeen, ct))
        {
            TakeAttachments(await box.GetMessageAsync(uid, ct));
            switch (s.InboundAfter)
            {
                case "verschieben" when archive is not null: await box.MoveToAsync(uid, archive, ct); break;
                case "behalten": await box.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); break;
                default: await box.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct); break;
            }
        }
        if (s.InboundAfter is not ("behalten" or "verschieben")) await box.ExpungeAsync(ct);
        await imap.DisconnectAsync(true, ct);
        return got;
    }
}
