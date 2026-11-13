using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace PdfRechnung.Documents;

public sealed class OutgoingMail
{
    public List<string> To { get; } = new();
    public List<string> Cc { get; } = new();
    public List<string> Bcc { get; } = new();
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>HTML-Fassung (null = nur Text).</summary>
    public string? HtmlBody { get; set; }
    /// <summary>Im HTML per cid: eingebundene Bilder (Logo).</summary>
    public List<InlineImage> Images { get; } = new();
    /// <summary>Abweichender Absender (Vorlage oder @@pf@@); leer = Einstellungen.</summary>
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    public List<(string Name, byte[] Data, string ContentType)> Attachments { get; } = new();

    public string From(MailOptions o) => string.IsNullOrWhiteSpace(FromAddress) ? o.FromAddress.Trim() : FromAddress.Trim();
    public string? Name(MailOptions o) => !string.IsNullOrWhiteSpace(FromName) ? FromName.Trim()
        : string.IsNullOrWhiteSpace(FromAddress) && !string.IsNullOrWhiteSpace(o.FromName) ? o.FromName.Trim() : null;
    public string? Reply(MailOptions o) => !string.IsNullOrWhiteSpace(ReplyTo) ? ReplyTo.Trim() : string.IsNullOrWhiteSpace(o.ReplyTo) ? null : o.ReplyTo.Trim();
}

/// <summary>Entschlüsselte Zugangsdaten für den Versand (nie gespeichert, nur zur Laufzeit).</summary>
public sealed record MailSecrets(string? SmtpPassword, string? GraphClientSecret);

/// <summary>
/// Versand per SMTP (MailKit) oder Microsoft Graph (Microsoft 365 / Exchange Online).
/// Graph: App-Registrierung mit Anwendungsberechtigung Mail.Send, gesendet wird im Namen der Absenderadresse.
/// </summary>
public static class MailSender
{
    public static readonly string[] SecurityModes = { "Auto", "StartTls", "SslOnConnect", "None" };
    public static readonly string[] Transports = { "Smtp", "Graph" };

    public static Task SendAsync(MailOptions o, MailSecrets secrets, OutgoingMail mail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mail.From(o))) throw new InvalidOperationException("Keine Absenderadresse eingetragen (Einstellungen → E-Mail).");
        if (mail.To.Count == 0) throw new InvalidOperationException("Kein Empfänger.");
        return o.Transport == "Graph"
            ? GraphMailSender.SendAsync(o, secrets.GraphClientSecret, mail, ct)
            : SendSmtpAsync(o, secrets.SmtpPassword, mail, ct);
    }

    /// <summary>Weitere Empfänger aus „Kopie jeder Mail an“ (BCC).</summary>
    internal static IEnumerable<string> CopyTo(MailOptions o) =>
        (o.CopyTo ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>MIME-Nachricht (HTML + Text, eingebettetes Logo, Anlagen) – für SMTP und als .eml-Entwurf.</summary>
    public static MimeMessage BuildMime(MailOptions o, OutgoingMail mail, bool includeCopyTo)
    {
        var msg = new MimeMessage();
        if (!string.IsNullOrWhiteSpace(mail.From(o))) msg.From.Add(new MailboxAddress(mail.Name(o), mail.From(o)));
        if (mail.Reply(o) is { } reply) msg.ReplyTo.Add(MailboxAddress.Parse(reply));
        foreach (var a in mail.To) msg.To.Add(MailboxAddress.Parse(a));
        foreach (var a in mail.Cc) msg.Cc.Add(MailboxAddress.Parse(a));
        foreach (var a in includeCopyTo ? mail.Bcc.Concat(CopyTo(o)) : mail.Bcc) msg.Bcc.Add(MailboxAddress.Parse(a));
        msg.Subject = mail.Subject;

        var builder = new BodyBuilder { TextBody = mail.Body };
        if (mail.HtmlBody is not null)
        {
            builder.HtmlBody = mail.HtmlBody;
            foreach (var img in mail.Images)
            {
                var res = builder.LinkedResources.Add(img.Name, img.Data, ContentType.Parse(img.ContentType));
                res.ContentId = img.ContentId;
            }
        }
        foreach (var (name, data, type) in mail.Attachments)
            builder.Attachments.Add(name, data, ContentType.Parse(type));
        msg.Body = builder.ToMessageBody();
        return msg;
    }

    private static async Task SendSmtpAsync(MailOptions o, string? password, OutgoingMail mail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Host)) throw new InvalidOperationException("Kein SMTP-Server eingetragen (Einstellungen → E-Mail).");
        var msg = BuildMime(o, mail, includeCopyTo: true);

        var security = o.Security switch
        {
            "StartTls" => SecureSocketOptions.StartTls,
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto
        };
        using var client = new SmtpClient { Timeout = 60_000 };
        await client.ConnectAsync(o.Host.Trim(), o.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(o.User)) await client.AuthenticateAsync(o.User.Trim(), password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>
/// Versand über Microsoft Graph mit Client-Credentials (App-Registrierung, Anwendungsberechtigung Mail.Send).
/// Kleine Mails (Anlagen zusammen &lt; 3 MB) gehen per /sendMail; größere werden als Entwurf angelegt,
/// die Anlagen per Upload-Sitzung hochgeladen und dann gesendet (bis 150 MB je Anlage).
/// </summary>
public static class GraphMailSender
{
    public const string GraphBase = "https://graph.microsoft.com/v1.0";
    public const int InlineLimit = 3 * 1024 * 1024;
    /// <summary>Blockgröße für Upload-Sitzungen: Vielfaches von 320 KiB (Vorgabe von Microsoft), hier 3,125 MiB.</summary>
    private const int Chunk = 320 * 1024 * 10;

    /// <summary>Für Tests austauschbar.</summary>
    public static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromMinutes(5) };
    public static string LoginBase { get; set; } = "https://login.microsoftonline.com";

    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Key, string Token, DateTimeOffset Expires)? _token;

    public static async Task SendAsync(MailOptions o, string? secret, OutgoingMail mail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.GraphTenantId) || string.IsNullOrWhiteSpace(o.GraphClientId) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Microsoft 365 ist nicht vollständig eingerichtet: Mandanten-ID, Client-ID und geheimer Clientschlüssel fehlen (Einstellungen → E-Mail).");
        var token = await TokenAsync(o.GraphTenantId.Trim(), o.GraphClientId.Trim(), secret, ct);
        var user = Uri.EscapeDataString(mail.From(o));
        var message = Message(o, mail);
        var total = mail.Attachments.Sum(a => (long)a.Data.Length);

        if (total < InlineLimit)
        {
            message["attachments"] = new JsonArray(mail.Images.Select(i => (JsonNode)InlineAttachment(i))
                .Concat(mail.Attachments.Select(a => (JsonNode)FileAttachment(a))).ToArray());
            var body = new JsonObject { ["message"] = message, ["saveToSentItems"] = o.GraphSaveToSentItems };
            await Send(HttpMethod.Post, $"{GraphBase}/users/{user}/sendMail", token, body, ct);
            return;
        }

        // Große Anlagen: Entwurf → Anlagen → senden
        var draft = await Send(HttpMethod.Post, $"{GraphBase}/users/{user}/messages", token, message, ct);
        var id = Uri.EscapeDataString(draft?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Graph hat keinen Entwurf angelegt."));
        foreach (var img in mail.Images)
            await Send(HttpMethod.Post, $"{GraphBase}/users/{user}/messages/{id}/attachments", token, InlineAttachment(img), ct);
        foreach (var a in mail.Attachments)
        {
            if (a.Data.Length < InlineLimit)
            {
                await Send(HttpMethod.Post, $"{GraphBase}/users/{user}/messages/{id}/attachments", token, FileAttachment(a), ct);
                continue;
            }
            var session = await Send(HttpMethod.Post, $"{GraphBase}/users/{user}/messages/{id}/attachments/createUploadSession", token,
                new JsonObject { ["AttachmentItem"] = new JsonObject { ["attachmentType"] = "file", ["name"] = a.Name, ["size"] = a.Data.Length, ["contentType"] = a.ContentType } }, ct);
            var url = session?["uploadUrl"]?.GetValue<string>() ?? throw new InvalidOperationException("Graph hat keine Upload-Sitzung angelegt.");
            for (var pos = 0; pos < a.Data.Length; pos += Chunk)
            {
                var len = Math.Min(Chunk, a.Data.Length - pos);
                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(a.Data, pos, len) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                req.Content.Headers.ContentRange = new ContentRangeHeaderValue(pos, pos + len - 1, a.Data.Length);
                using var res = await Http.SendAsync(req, ct); // Upload-URL ist vorsigniert: ohne Authorization-Header
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Upload der Anlage {a.Name} fehlgeschlagen: {await Error(res, ct)}");
            }
        }
        await Send(HttpMethod.Post, $"{GraphBase}/users/{user}/messages/{id}/send", token, null, ct);
    }

    private static JsonObject Message(MailOptions o, OutgoingMail mail)
    {
        static JsonArray Recipients(IEnumerable<string> list) =>
            new(list.Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a } }).ToArray());
        var msg = new JsonObject
        {
            ["subject"] = mail.Subject,
            ["body"] = mail.HtmlBody is null
                ? new JsonObject { ["contentType"] = "Text", ["content"] = mail.Body }
                : new JsonObject { ["contentType"] = "HTML", ["content"] = mail.HtmlBody },
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = mail.From(o), ["name"] = mail.Name(o) } },
            ["toRecipients"] = Recipients(mail.To),
            ["ccRecipients"] = Recipients(mail.Cc),
            ["bccRecipients"] = Recipients(mail.Bcc.Concat(MailSender.CopyTo(o)))
        };
        if (mail.Reply(o) is { } reply) msg["replyTo"] = Recipients(new[] { reply });
        return msg;
    }

    private static JsonObject InlineAttachment(InlineImage i) => new()
    {
        ["@odata.type"] = "#microsoft.graph.fileAttachment",
        ["name"] = i.Name,
        ["contentType"] = i.ContentType,
        ["contentBytes"] = Convert.ToBase64String(i.Data),
        ["contentId"] = i.ContentId,
        ["isInline"] = true
    };

    private static JsonObject FileAttachment((string Name, byte[] Data, string ContentType) a) => new()
    {
        ["@odata.type"] = "#microsoft.graph.fileAttachment",
        ["name"] = a.Name,
        ["contentType"] = a.ContentType,
        ["contentBytes"] = Convert.ToBase64String(a.Data)
    };

    private static async Task<JsonNode?> Send(HttpMethod method, string url, string token, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = body is null ? new StringContent("") : new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Explain(res.StatusCode, await Error(res, ct)));
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static async Task<string> TokenAsync(string tenant, string clientId, string secret, CancellationToken ct)
    {
        var key = $"{tenant}|{clientId}|{secret.GetHashCode()}";
        await TokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } t && t.Key == key && t.Expires > DateTimeOffset.UtcNow.AddMinutes(2)) return t.Token;
            using var res = await Http.PostAsync($"{LoginBase}/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            }), ct);
            var json = await res.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException("Anmeldung bei Microsoft 365 fehlgeschlagen: " +
                    (json?["error_description"]?.GetValue<string>()?.Split('\n')[0] ?? res.ReasonPhrase));
            var token = json!["access_token"]!.GetValue<string>();
            var expires = DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600);
            _token = (key, token, expires);
            return token;
        }
        finally { TokenLock.Release(); }
    }

    private static async Task<string> Error(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        try { return JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text; }
        catch (JsonException) { return string.IsNullOrWhiteSpace(text) ? res.ReasonPhrase ?? "" : text; }
    }

    private static string Explain(System.Net.HttpStatusCode code, string message) => code switch
    {
        System.Net.HttpStatusCode.Forbidden =>
            $"Microsoft 365 verweigert den Versand ({message}). Hat die App-Registrierung die Anwendungsberechtigung „Mail.Send“ mit Administratorzustimmung, und darf sie für dieses Postfach senden?",
        System.Net.HttpStatusCode.NotFound =>
            $"Absenderpostfach nicht gefunden ({message}). Die Absenderadresse muss ein Postfach in Microsoft 365 sein.",
        _ => $"Microsoft 365: {message}"
    };
}
