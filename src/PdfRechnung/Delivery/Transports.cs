using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;
using MimeKit;
using MimeKit.Cryptography;
using Renci.SshNet;

namespace PdfRechnung.Delivery;

/// <summary>Eine zu übertragende Datei.</summary>
public sealed record DeliveryFile(string Name, byte[] Data, string ContentType);

/// <summary>Übertragung per FTP/FTPS (FluentFTP) und SFTP (SSH.NET).</summary>
public static class FileTransfer
{
    /// <summary>Lädt die Dateien hoch und liefert eine Beschreibung des Ziels.</summary>
    public static async Task<string> UploadAsync(string protocol, ChannelSettings s, string? password, IReadOnlyList<DeliveryFile> files, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host)) throw new InvalidOperationException("Kein Server eingetragen.");
        return protocol == ChannelType.Sftp
            ? await Task.Run(() => Sftp(s, password, files, ct), ct)
            : await Ftp(s, password, protocol == ChannelType.Ftps, files, ct);
    }

    /// <summary>Nur verbinden und den Zielordner prüfen (Knopf „Verbindung testen“).</summary>
    public static Task<string> TestAsync(string protocol, ChannelSettings s, string? password, CancellationToken ct) =>
        UploadAsync(protocol, s, password, Array.Empty<DeliveryFile>(), ct);

    private static string Folder(ChannelSettings s)
    {
        var f = (s.Folder ?? "").Trim().Replace('\\', '/');
        if (f.Length == 0) return "";
        return f.TrimEnd('/');
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    private static async Task<string> Ftp(ChannelSettings s, string? password, bool tls, IReadOnlyList<DeliveryFile> files, CancellationToken ct)
    {
        var port = s.Port > 0 ? s.Port : tls && s.ImplicitTls ? 990 : 21;
        var user = string.IsNullOrWhiteSpace(s.User) ? "anonymous" : s.User.Trim();
        using var client = new AsyncFtpClient(s.Host.Trim(), user, password ?? "", port);
        client.Config.EncryptionMode = !tls ? FtpEncryptionMode.None : s.ImplicitTls ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        client.Config.DataConnectionType = s.ActiveMode ? FtpDataConnectionType.AutoActive : FtpDataConnectionType.AutoPassive;
        client.Config.ConnectTimeout = 20000;
        client.Config.ReadTimeout = 60000;
        if (tls)
        {
            var expected = Hex(s.Fingerprint);
            client.ValidateCertificate += (_, e) =>
            {
                if (expected.Length > 0)
                {
                    var actual = e.Certificate is null ? "" : Convert.ToHexString(SHA256.HashData(e.Certificate.GetRawCertData()));
                    e.Accept = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                }
                else e.Accept = e.PolicyErrors == System.Net.Security.SslPolicyErrors.None;
            };
        }
        await client.Connect(ct);
        var folder = Folder(s);
        if (folder.Length > 0 && !await client.DirectoryExists(folder, ct)) await client.CreateDirectory(folder, true, ct);
        foreach (var f in files)
        {
            var target = Join(folder, f.Name);
            if (s.UploadAsTemp)
            {
                var tmp = target + ".part";
                var st = await client.UploadBytes(f.Data, tmp, FtpRemoteExists.Overwrite, false, null, ct);
                if (st == FtpStatus.Failed) throw new IOException($"Hochladen von {f.Name} fehlgeschlagen.");
                if (await client.FileExists(target, ct)) await client.DeleteFile(target, ct);
                await client.Rename(tmp, target, ct);
            }
            else if (await client.UploadBytes(f.Data, target, FtpRemoteExists.Overwrite, false, null, ct) == FtpStatus.Failed)
                throw new IOException($"Hochladen von {f.Name} fehlgeschlagen.");
        }
        await client.Disconnect(ct);
        return $"{(tls ? "ftps" : "ftp")}://{s.Host.Trim()}:{port}/{folder}".TrimEnd('/');
    }

    private static string Sftp(ChannelSettings s, string? password, IReadOnlyList<DeliveryFile> files, CancellationToken ct)
    {
        var port = s.Port > 0 ? s.Port : 22;
        using var client = new SftpClient(s.Host.Trim(), port, s.User.Trim(), password ?? "");
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        var expected = (s.Fingerprint ?? "").Trim();
        if (expected.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) expected = expected[7..];
        expected = expected.TrimEnd('=');
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = e.FingerPrintSHA256;
            e.CanTrust = expected.Length == 0 || string.Equals(e.FingerPrintSHA256.TrimEnd('='), expected, StringComparison.Ordinal);
        };
        try { client.Connect(); }
        catch (Renci.SshNet.Common.SshConnectionException) when (expected.Length > 0 && seen is not null)
        {
            throw new InvalidOperationException($"Hostschlüssel passt nicht: erwartet SHA256:{expected}, Server meldet SHA256:{seen}.");
        }
        var folder = Folder(s);
        if (folder.Length > 0 && !client.Exists(folder))
        {
            var path = folder.StartsWith('/') ? "" : ".";
            foreach (var part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? "/" + part : path + "/" + part;
                if (!client.Exists(path)) client.CreateDirectory(path);
            }
        }
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Join(folder, f.Name);
            using var ms = new MemoryStream(f.Data);
            if (s.UploadAsTemp)
            {
                var tmp = target + ".part";
                client.UploadFile(ms, tmp, true);
                if (client.Exists(target)) client.DeleteFile(target);
                client.RenameFile(tmp, target);
            }
            else client.UploadFile(ms, target, true);
        }
        client.Disconnect();
        return $"sftp://{s.Host.Trim()}:{port}/{folder}".TrimEnd('/') + (seen is null ? "" : $" (Hostschlüssel SHA256:{seen})");
    }

    private static string Hex(string? s) => new((s ?? "").Where(Uri.IsHexDigit).ToArray());
}

/// <summary>
/// eXite® (Editel) über FTPS laut „eXite FTPS protocol description“ 1.0.1: Server www.exite.net, Port 21, explizites TLS, Binärmodus.
/// Senden: Datendatei „S&lt;name&gt;“ hochladen, dann Befehlsdatei „C&lt;name&gt;“ mit <c>ser(S)</c> <c>typ(X|E)</c>.
/// eXite benennt die Befehlsdatei bei Erfolg in „J…“ um, bei Fehler in „E…“ (Grund im eXite-Protokoll der Online Services).
/// </summary>
public static class ExiteTransport
{
    public const string DefaultHost = "www.exite.net";

    private static async Task<AsyncFtpClient> ConnectAsync(ChannelSettings s, string? password, CancellationToken ct)
    {
        var host = string.IsNullOrWhiteSpace(s.Host) ? DefaultHost : s.Host.Trim();
        var client = new AsyncFtpClient(host, s.User.Trim(), password ?? "", s.Port > 0 ? s.Port : 21);
        client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.UploadDataType = FtpDataType.Binary;
        client.Config.DownloadDataType = FtpDataType.Binary;
        client.Config.ConnectTimeout = 20000;
        await client.Connect(ct);
        return client;
    }

    public static async Task<string> TestAsync(ChannelSettings s, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.User)) throw new InvalidOperationException("eXite: Benutzername des Postfachs fehlt.");
        using var client = await ConnectAsync(s, password, ct);
        var n = (await client.GetListing(Folder(s), ct)).Length;
        await client.Disconnect(ct);
        return $"Anmeldung am eXite-Postfach in Ordnung ({n} Dateien im Verzeichnis).";
    }

    public static async Task<string> SendAsync(ChannelSettings s, string? password, DeliveryFile file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.User)) throw new InvalidOperationException("eXite: Benutzername des Postfachs fehlt.");
        var type = s.ExiteType?.Trim().ToUpperInvariant() == "E" ? "E" : "X";
        var name = $"BP{DateTime.Now:yyMMddHHmmss}{Random.Shared.Next(100, 999)}";
        var folder = Folder(s);
        string P(string n) => folder.Length == 0 ? n : folder + "/" + n;
        var cmd = new StringBuilder();
        cmd.Append("* Belegpost ").Append(file.Name).Append("\r\n");
        cmd.Append("ser(S)\r\n").Append($"typ({type})\r\n");
        if (!string.IsNullOrWhiteSpace(s.ExiteMessageClass)) cmd.Append($"umc({s.ExiteMessageClass.Trim()})\r\n");
        if (s.ExiteAck == "ja") cmd.Append("ack(1)\r\n");

        using var client = await ConnectAsync(s, password, ct);
        // erst die Daten, dann der Befehl – eXite verarbeitet beim Eintreffen der Befehlsdatei
        if (await client.UploadBytes(file.Data, P("S" + name), FtpRemoteExists.Overwrite, false, null, ct) == FtpStatus.Failed)
            throw new IOException("eXite: Datendatei konnte nicht hochgeladen werden.");
        if (await client.UploadBytes(Encoding.ASCII.GetBytes(cmd.ToString()), P("C" + name), FtpRemoteExists.Overwrite, false, null, ct) == FtpStatus.Failed)
            throw new IOException("eXite: Befehlsdatei konnte nicht hochgeladen werden.");

        // Ergebnis abwarten: J… = verarbeitet, E… = Fehler
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (await client.FileExists(P("J" + name), ct))
            {
                try { await client.DeleteFile(P("J" + name), ct); } catch { /* Aufräumen ist optional */ }
                await client.Disconnect(ct);
                return $"eXite-Postfach {s.User.Trim()}: {name} verarbeitet ({file.Name}, typ {type})";
            }
            if (await client.FileExists(P("E" + name), ct))
            {
                await client.Disconnect(ct);
                throw new InvalidOperationException($"eXite hat {name} mit Fehler abgelehnt – Grund im eXite-Protokoll (eXite Online Services).");
            }
        }
        await client.Disconnect(ct);
        throw new TimeoutException($"eXite hat {name} nach 60 Sekunden noch nicht verarbeitet (weder J- noch E-Datei) – bitte im eXite-Postfach prüfen.");
    }

    private static string Folder(ChannelSettings s) => (s.Folder ?? "").Trim().Replace('\\', '/').TrimEnd('/');
}

/// <summary>
/// AS2 (RFC 4130): Datei signieren (SHA-256) und verschlüsseln (AES-256), per HTTP(S) senden, synchrone Empfangsbestätigung (MDN) auswerten.
/// </summary>
public static class As2Transport
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(2) };

    public static async Task<string> SendAsync(ChannelSettings s, string? certPassword, DeliveryFile file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Url)) throw new InvalidOperationException("AS2: keine Adresse (URL) des Partners eingetragen.");
        if (string.IsNullOrWhiteSpace(s.As2From) || string.IsNullOrWhiteSpace(s.As2To)) throw new InvalidOperationException("AS2: AS2-From und AS2-To angeben.");
        var sign = s.Sign != "nein";
        var encrypt = s.Encrypt != "nein";
        var mdn = s.RequestMdn != "nein";

        var part = new MimePart(file.ContentType)
        {
            Content = new MimeContent(new MemoryStream(file.Data)),
            ContentTransferEncoding = ContentEncoding.Binary,
            FileName = file.Name
        };
        MimeEntity entity = part;
        using var ctx = new TemporarySecureMimeContext();
        if (sign)
        {
            if (string.IsNullOrWhiteSpace(s.OwnCertificate) || !File.Exists(s.OwnCertificate)) throw new InvalidOperationException("AS2: eigenes Zertifikat (.pfx) nicht gefunden.");
            var own = X509CertificateLoader.LoadPkcs12FromFile(s.OwnCertificate, certPassword, X509KeyStorageFlags.Exportable);
            var signer = new CmsSigner(own) { DigestAlgorithm = DigestAlgorithm.Sha256 };
            entity = await MultipartSigned.CreateAsync(ctx, signer, entity, ct);
        }
        if (encrypt)
        {
            if (string.IsNullOrWhiteSpace(s.PartnerCertificate) || !File.Exists(s.PartnerCertificate)) throw new InvalidOperationException("AS2: Zertifikat des Partners (.cer) nicht gefunden.");
            var partner = X509CertificateLoader.LoadCertificateFromFile(s.PartnerCertificate);
            var recipients = new CmsRecipientCollection { new CmsRecipient(partner) { EncryptionAlgorithms = new[] { EncryptionAlgorithm.Aes256 } } };
            entity = await ApplicationPkcs7Mime.EncryptAsync(ctx, recipients, entity, ct);
        }

        if (entity is ApplicationPkcs7Mime enc) enc.ContentTransferEncoding = ContentEncoding.Binary;
        // MIME-Kopf → HTTP-Kopf, Rest → HTTP-Inhalt
        using var raw = new MemoryStream();
        await entity.WriteToAsync(new FormatOptions { NewLineFormat = NewLineFormat.Dos }, raw, ct);
        var bytes = raw.ToArray();
        var split = IndexOf(bytes, "\r\n\r\n"u8.ToArray());
        if (split < 0) throw new InvalidOperationException("AS2: Nachricht konnte nicht aufgebaut werden.");
        var body = bytes[(split + 4)..];

        var messageId = $"<belegpost-{Guid.NewGuid():N}@{Environment.MachineName.ToLowerInvariant()}>";
        using var req = new HttpRequestMessage(HttpMethod.Post, s.Url.Trim()) { Content = new ByteArrayContent(body) };
        var mimeHead = System.Text.RegularExpressions.Regex.Replace(Encoding.ASCII.GetString(bytes, 0, split), "\r\n[ \t]+", " ");
        string? H(string name) => mimeHead.Split("\r\n").FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();
        req.Content.Headers.TryAddWithoutValidation("Content-Type", H("Content-Type") ?? "application/octet-stream");
        if (H("Content-Transfer-Encoding") is { } cte) req.Content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", cte);
        req.Headers.TryAddWithoutValidation("AS2-Version", "1.2");
        req.Headers.TryAddWithoutValidation("AS2-From", Quote(s.As2From.Trim()));
        req.Headers.TryAddWithoutValidation("AS2-To", Quote(s.As2To.Trim()));
        req.Headers.TryAddWithoutValidation("Message-ID", messageId);
        req.Headers.TryAddWithoutValidation("MIME-Version", "1.0");
        req.Headers.TryAddWithoutValidation("Subject", file.Name);
        if (mdn)
        {
            req.Headers.TryAddWithoutValidation("Disposition-Notification-To", s.As2From.Trim());
            if (sign) req.Headers.TryAddWithoutValidation("Disposition-Notification-Options",
                "signed-receipt-protocol=optional, pkcs7-signature; signed-receipt-micalg=optional, sha-256");
        }
        using var resp = await Http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"AS2: Partner antwortet mit HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        if (!mdn) return $"{s.Url.Trim()} ({messageId})";

        var disposition = ReadDisposition(resp.Content.Headers.ContentType, respBody);
        if (disposition is null) throw new InvalidOperationException("AS2: keine Empfangsbestätigung (MDN) erhalten.");
        if (!disposition.Contains("processed", StringComparison.OrdinalIgnoreCase)
            || disposition.Contains("error", StringComparison.OrdinalIgnoreCase) || disposition.Contains("failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AS2: Partner meldet " + disposition.Trim());
        return $"{s.Url.Trim()} ({messageId}, MDN: {disposition.Trim()})";
    }

    /// <summary>„Disposition: …“ aus der MDN (multipart/report, ggf. signiert).</summary>
    private static string? ReadDisposition(MediaTypeHeaderValue? contentType, byte[] body)
    {
        if (contentType is null || body.Length == 0) return null;
        var head = Encoding.ASCII.GetBytes("Content-Type: " + contentType + "\r\n\r\n");
        using var ms = new MemoryStream(head.Concat(body).ToArray());
        MimeEntity mdn;
        try { mdn = MimeEntity.Load(ms); } catch { return null; }
        foreach (var e in mdn is Multipart mp ? Flatten(mp) : new[] { mdn })
            if (e is MimePart p && p.ContentType.MimeType.Equals("message/disposition-notification", StringComparison.OrdinalIgnoreCase))
            {
                using var r = new StreamReader(p.Content.Open());
                var text = r.ReadToEnd();
                return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Disposition:", StringComparison.OrdinalIgnoreCase))?["Disposition:".Length..];
            }
            else if (e is MessageDeliveryStatus or TextPart) continue;
        return null;
    }

    private static IEnumerable<MimeEntity> Flatten(Multipart mp)
    {
        foreach (var e in mp)
        {
            if (e is Multipart inner) foreach (var x in Flatten(inner)) yield return x;
            else yield return e;
        }
    }

    private static string Quote(string id) => id.Contains(' ') ? $"\"{id}\"" : id;

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i <= data.Length - pattern.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < pattern.Length && ok; j++) ok = data[i + j] == pattern[j];
            if (ok) return i;
        }
        return -1;
    }
}
