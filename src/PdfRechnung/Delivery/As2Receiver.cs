using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;

namespace PdfRechnung.Delivery;

/// <summary>
/// AS2-Empfang (RFC 4130) unter <c>https://&lt;server&gt;:&lt;port&gt;/as2</c>: Partner anhand AS2-From/AS2-To erkennen (Versandweg AS2 mit Abruf ein),
/// entschlüsseln, Signatur prüfen, Datei im Zielordner ablegen und synchron mit einer MDN bestätigen (signiert, wenn angefordert).
/// </summary>
public static class As2Receiver
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/as2", async (HttpContext ctx, IOptionsMonitor<DeliveryOptions> opt, CustomerStore customers, Settings.SettingsStore settings,
            InboundService inbound, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("AS2");
            var from = Unquote(ctx.Request.Headers["AS2-From"].ToString());
            var to = Unquote(ctx.Request.Headers["AS2-To"].ToString());
            var messageId = ctx.Request.Headers["Message-ID"].ToString();
            var wantsMdn = ctx.Request.Headers.ContainsKey("Disposition-Notification-To");
            var wantsSigned = ctx.Request.Headers["Disposition-Notification-Options"].ToString().Contains("pkcs7-signature", StringComparison.OrdinalIgnoreCase);

            var channel = Find(opt.CurrentValue, customers, from, to);
            if (channel is null)
            {
                log.LogWarning("AS2: unbekannter Partner {From} → {To}", from, to);
                return Results.Text("Unbekannter AS2-Partner.", statusCode: 403);
            }
            var s = channel.Value.Settings;
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body);
            string disposition, mic = "";
            string? saved = null;
            try
            {
                var head = $"Content-Type: {ctx.Request.ContentType}\r\n" +
                           (ctx.Request.Headers.TryGetValue("Content-Transfer-Encoding", out var cte) ? $"Content-Transfer-Encoding: {cte}\r\n" : "") + "\r\n";
                using var ms = new MemoryStream(Encoding.ASCII.GetBytes(head).Concat(body.ToArray()).ToArray());
                var entity = await MimeEntity.LoadAsync(ms);
                using var sm = new TemporarySecureMimeContext();
                X509Certificate2? own = null;
                if (!string.IsNullOrWhiteSpace(s.OwnCertificate) && File.Exists(s.OwnCertificate))
                {
                    own = X509CertificateLoader.LoadPkcs12FromFile(s.OwnCertificate, settings.Secrets.Unprotect(s.OwnCertificatePassword), X509KeyStorageFlags.Exportable);
                    await using var pfx = File.OpenRead(s.OwnCertificate);
                    await sm.ImportAsync(pfx, settings.Secrets.Unprotect(s.OwnCertificatePassword) ?? "");
                }
                if (entity is ApplicationPkcs7Mime p7 && p7.SecureMimeType == SecureMimeType.EnvelopedData)
                {
                    if (own is null) throw new InvalidOperationException("verschlüsselt, aber kein eigenes Zertifikat hinterlegt");
                    entity = await p7.DecryptAsync(sm);
                }
                if (entity is MultipartSigned signed)
                {
                    if (!string.IsNullOrWhiteSpace(s.PartnerCertificate) && File.Exists(s.PartnerCertificate))
                    {
                        var partner = X509CertificateLoader.LoadCertificateFromFile(s.PartnerCertificate);
                        var sigs = await signed.VerifyAsync(sm);
                        if (!sigs.Any(x => x.SignerCertificate is SecureMimeDigitalCertificate c &&
                                string.Equals(c.Fingerprint, partner.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("Signatur stammt nicht vom hinterlegten Partner-Zertifikat");
                    }
                    var content = signed[0];
                    using var canon = new MemoryStream();
                    await content.WriteToAsync(new FormatOptions { NewLineFormat = NewLineFormat.Dos }, canon);
                    mic = Convert.ToBase64String(SHA256.HashData(canon.ToArray())) + ", sha-256";
                    entity = content;
                }
                else mic = Convert.ToBase64String(SHA256.HashData(body.ToArray())) + ", sha-256";

                if (entity is not MimePart part) throw new InvalidOperationException("keine Datei in der Nachricht");
                using var data = new MemoryStream();
                await part.Content.DecodeToAsync(data);
                var target = inbound.Target(s);
                Directory.CreateDirectory(target);
                var name = part.FileName ?? $"as2_{DateTime.Now:yyyyMMdd-HHmmss}{Extension(part.ContentType.MimeType)}";
                name = Documents.MailTemplate.SafeFileName(Path.GetFileNameWithoutExtension(name)) + Path.GetExtension(name);
                var path = Path.Combine(target, name);
                for (var i = 1; File.Exists(path); i++) path = Path.Combine(target, $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");
                await File.WriteAllBytesAsync(path + ".part", data.ToArray());
                File.Move(path + ".part", path);
                saved = path;
                ctx.RequestServices.GetService<InboundLog>()?.Add(ChannelType.As2, channel.Value.Label, channel.Value.CustomerId, channel.Value.Customer, path, data.ToArray());
                disposition = "automatic-action/MDN-sent-automatically; processed";
                log.LogInformation("AS2 von {From}: {File} gespeichert ({Label})", from, path, channel.Value.Label);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "AS2 von {From} nicht verarbeitet", from);
                disposition = "automatic-action/MDN-sent-automatically; processed/error: " + (ex is CryptographicException ? "decryption-failed" : "unexpected-processing-error");
            }
            if (!wantsMdn) return saved is null ? Results.StatusCode(500) : Results.Ok();

            // MDN (multipart/report) – signiert, wenn angefordert und ein eigenes Zertifikat da ist
            var report = new MultipartReport("disposition-notification")
            {
                new TextPart("plain") { Text = saved is null ? "Die Nachricht konnte nicht verarbeitet werden." : "Die Nachricht wurde empfangen und verarbeitet." },
                new MimePart("message", "disposition-notification")
                {
                    Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(
                        $"Reporting-UA: Belegpost\r\nOriginal-Recipient: rfc822; {to}\r\nFinal-Recipient: rfc822; {to}\r\nOriginal-Message-ID: {messageId}\r\n" +
                        (mic.Length > 0 ? $"Received-Content-MIC: {mic}\r\n" : "") + $"Disposition: {disposition}\r\n"))),
                    ContentTransferEncoding = ContentEncoding.SevenBit
                }
            };
            MimeEntity mdn = report;
            if (wantsSigned && !string.IsNullOrWhiteSpace(s.OwnCertificate) && File.Exists(s.OwnCertificate))
            {
                using var sm = new TemporarySecureMimeContext();
                var own = X509CertificateLoader.LoadPkcs12FromFile(s.OwnCertificate, settings.Secrets.Unprotect(s.OwnCertificatePassword), X509KeyStorageFlags.Exportable);
                mdn = await MultipartSigned.CreateAsync(sm, new CmsSigner(own) { DigestAlgorithm = DigestAlgorithm.Sha256 }, report);
            }
            using var outMs = new MemoryStream();
            await mdn.WriteToAsync(new FormatOptions { NewLineFormat = NewLineFormat.Dos }, outMs);
            var bytes = outMs.ToArray();
            var split = Find(bytes, "\r\n\r\n"u8.ToArray());
            var mimeHead = System.Text.RegularExpressions.Regex.Replace(Encoding.ASCII.GetString(bytes, 0, split), "\r\n[ \t]+", " ");
            var contentType = mimeHead.Split("\r\n").First(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))[13..].Trim();
            ctx.Response.Headers["AS2-Version"] = "1.2";
            ctx.Response.Headers["AS2-From"] = to;
            ctx.Response.Headers["AS2-To"] = from;
            ctx.Response.Headers["Message-ID"] = $"<belegpost-mdn-{Guid.NewGuid():N}@{Environment.MachineName.ToLowerInvariant()}>";
            return Results.Bytes(bytes[(split + 4)..], contentType);
        }).DisableAntiforgery();
    }

    /// <summary>Versandweg AS2 mit Abruf, dessen Partner-ID (AS2-To beim Senden) dem Absender entspricht.</summary>
    private static (ChannelSettings Settings, string Label, string? CustomerId, string? Customer)? Find(DeliveryOptions o, CustomerStore customers, string from, string to)
    {
        bool Ok(ChannelSettings s) => s.Type == ChannelType.As2 && s.InboundEnabled == "ja"
            && string.Equals(s.As2To.Trim(), from, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(s.As2From) || string.Equals(s.As2From.Trim(), to, StringComparison.OrdinalIgnoreCase));
        foreach (var c in customers.All().Where(c => c.Active))
            foreach (var ch in c.Channels.Where(x => x.Enabled && x.Settings.Type == ChannelType.As2))
            {
                var s = ChannelSettings.Merge(o.Global(ChannelType.As2), ch.Settings);
                if (Ok(s)) return (s, $"{c.Label}: {ch.Label}", c.Id, c.Label);
            }
        var g = o.Global(ChannelType.As2) is { } gl ? ChannelSettings.Merge(null, gl) : null;
        return g is not null && Ok(g) ? (g, "AS2 (global)", null, null) : null;
    }

    private static string Unquote(string s) => s.Trim().Trim('"');

    private static string Extension(string mime) => mime.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf", "application/xml" or "text/xml" => ".xml", "application/edifact" or "application/edi-x12" => ".edi", _ => ".bin"
    };

    private static int Find(byte[] data, byte[] pattern)
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
