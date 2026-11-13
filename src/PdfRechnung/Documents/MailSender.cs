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
    public List<(string Name, byte[] Data, string ContentType)> Attachments { get; } = new();
}

/// <summary>Versand per SMTP (MailKit). Microsoft 365: smtp.office365.com, Port 587, StartTls, SMTP AUTH für das Postfach aktivieren.</summary>
public static class MailSender
{
    public static readonly string[] SecurityModes = { "Auto", "StartTls", "SslOnConnect", "None" };

    public static async Task SendAsync(MailOptions o, string? password, OutgoingMail mail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Host)) throw new InvalidOperationException("Kein SMTP-Server eingetragen (Einstellungen → E-Mail).");
        if (string.IsNullOrWhiteSpace(o.FromAddress)) throw new InvalidOperationException("Keine Absenderadresse eingetragen (Einstellungen → E-Mail).");
        if (mail.To.Count == 0) throw new InvalidOperationException("Kein Empfänger.");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(o.FromName) ? null : o.FromName.Trim(), o.FromAddress.Trim()));
        if (!string.IsNullOrWhiteSpace(o.ReplyTo)) msg.ReplyTo.Add(MailboxAddress.Parse(o.ReplyTo.Trim()));
        foreach (var a in mail.To) msg.To.Add(MailboxAddress.Parse(a));
        foreach (var a in mail.Cc) msg.Cc.Add(MailboxAddress.Parse(a));
        foreach (var a in mail.Bcc) msg.Bcc.Add(MailboxAddress.Parse(a));
        foreach (var a in (o.CopyTo ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.Bcc.Add(MailboxAddress.Parse(a));
        msg.Subject = mail.Subject;

        var builder = new BodyBuilder { TextBody = mail.Body };
        foreach (var (name, data, type) in mail.Attachments)
            builder.Attachments.Add(name, data, ContentType.Parse(type));
        msg.Body = builder.ToMessageBody();

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
