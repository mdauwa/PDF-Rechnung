using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

public sealed class MapiRecipient
{
    /// <summary>to, cc, bcc</summary>
    public string Type { get; set; } = "to";
    public string Address { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class MapiAttachment
{
    public string Name { get; set; } = "";
    public string Data { get; set; } = ""; // Base64
}

/// <summary>Nachricht, die BelegpostMapi.dll über die Named Pipe übergibt.</summary>
public sealed class MapiMessage
{
    public int Version { get; set; } = 1;
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public List<MapiRecipient> Recipients { get; set; } = new();
    public List<MapiAttachment> Attachments { get; set; } = new();
    /// <summary>Client-Computername der RDP-Sitzung (WTSClientName) – daran wird der PC des Benutzers erkannt.</summary>
    public string? ClientName { get; set; }
    public int SessionId { get; set; }
    public string? Process { get; set; }
    /// <summary>MAPI_DIALOG war gesetzt (ERP wollte den Mail-Dialog zeigen).</summary>
    public bool Dialog { get; set; }
}

public sealed record MapiResult(bool Ok, string Message, string? VorgangId = null, string? Mode = null);

/// <summary>
/// Nimmt MAPI-Mails aus dem ERP an (BelegpostMapi.dll → Named Pipe) und legt daraus einen Vorgang an. Danach je nach
/// Einstellung (PC → Benutzer → global): Outlook-Entwurf über den Melder, Prüfung im Portal oder direkter Versand.
/// </summary>
public sealed class MapiIntake
{
    public const long MaxBytes = 60L * 1024 * 1024;
    private readonly DocumentProcessor _proc;
    private readonly DocumentStore _store;
    private readonly UserStore _users;
    private readonly MelderTasks _tasks;
    private readonly EventHub _hub;
    private readonly SendService _send;
    private readonly IOptionsMonitor<MapiOptions> _opt;
    private readonly ILogger<MapiIntake> _log;

    public MapiIntake(DocumentProcessor proc, DocumentStore store, UserStore users, MelderTasks tasks, EventHub hub, SendService send,
        IOptionsMonitor<MapiOptions> opt, ILogger<MapiIntake> log)
    {
        _proc = proc; _store = store; _users = users; _tasks = tasks; _hub = hub; _send = send; _opt = opt; _log = log;
    }

    public async Task<MapiResult> HandleAsync(MapiMessage m, string windowsAccount, CancellationToken ct)
    {
        var files = m.Attachments.Select(a => (Name: SafeName(a.Name), Data: Convert.FromBase64String(a.Data))).ToList();
        var pdfIndex = files.FindIndex(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && f.Data.Length > 4 && Encoding.ASCII.GetString(f.Data, 0, 4) == "%PDF");
        var pdf = pdfIndex >= 0 ? files[pdfIndex].Data : BlankPdf;
        var fileName = pdfIndex >= 0 ? files[pdfIndex].Name : null;
        var extras = files.Where((_, i) => i != pdfIndex).ToList();

        var to = Addresses(m, "to");
        var cc = Addresses(m, "cc");
        var bcc = Addresses(m, "bcc");
        var owner = _users.FindByWindowsAccount(windowsAccount);

        var v = _proc.Process(pdf, "MAPI", fileName ?? "E-Mail", windowsAccount, m.ClientName, owner?.Id, beforeSave: x =>
        {
            // Was das ERP über MAPI übergibt, hat Vorrang (Empfänger, Betreff, Text); Steuerzeichen im PDF (z. B. @@pi@@, @@pb@@) gelten weiter.
            if (to.Count > 0) x.Mail.To = to;
            if (cc.Count > 0) x.Mail.Cc = cc;
            if (bcc.Count > 0) x.Mail.Bcc = bcc;
            if (!string.IsNullOrWhiteSpace(m.Subject)) x.Mail.Subject = m.Subject.Trim();
            if (!string.IsNullOrWhiteSpace(m.Body)) x.Mail.Body = StripCodes(m.Body.Replace("\r\n", "\n"));
            x.Mail.Enabled = true;
            x.Mail.Edited = true; // „Neu prüfen“ überschreibt die MAPI-Daten nicht
            if (pdfIndex < 0) { x.Mail.NoDocument = true; x.DocumentType = "E-Mail"; }
            if (x.ErpIndex is null && m.Body is not null && System.Text.RegularExpressions.Regex.Match(m.Body, @"@@pi\s*(\d{1,18})@@") is { Success: true } pi)
                x.ErpIndex = ControlCodeParser.NormalizeErpIndex(pi.Groups[1].Value);
            if (extras.Count > 0)
            {
                var dir = Path.Combine(_store.DirectoryFor(x), "anlagen");
                Directory.CreateDirectory(dir);
                foreach (var (name, data) in extras)
                {
                    var path = Unique(Path.Combine(dir, name));
                    File.WriteAllBytes(path, data);
                    x.Mail.Attachments.Add(path);
                }
            }
            if (owner is null)
                x.Messages.Add(new CheckMessage(DocumentChecker.Warning, $"Windows-Benutzer „{windowsAccount}“ ist keinem Portal-Benutzer zugeordnet (Einstellungen → Benutzer → Druck-Benutzernamen).", "mapi"));
        }, publish: false);

        var userId = v.OwnerUserId;
        if (userId is null)
        {
            _log.LogInformation("MAPI-Mail von {Account}: kein Portal-Benutzer – Vorgang {Id} bleibt im Portal", windowsAccount, v.Id);
            return new MapiResult(true, "Im Portal abgelegt.", v.Id, MapiMode.Portal);
        }
        var user = _users.FindById(userId)!;
        var device = _users.TargetDevice(userId, m.ClientName);
        var mode = _users.EffectiveMapiMode(userId, device?.Id, _opt.CurrentValue.DefaultMode);
        if (mode == MapiMode.Outlook && !user.SendViaOutlook) mode = MapiMode.Portal; // Outlook nicht freigeschaltet
        var title = string.Join(" ", new[] { v.DocumentType, v.VoucherId }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (title.Length == 0) title = v.Mail.Subject;

        switch (mode)
        {
            case MapiMode.Outlook when v.Status != VorgangStatus.Error:
                _tasks.Create(userId, device?.Id, MelderTasks.KindOutlook, v.Id, title);
                _log.LogInformation("MAPI-Mail {Id} → Outlook auf {Device}", v.Id, device?.Name ?? "(nächster Melder)");
                return new MapiResult(true, "Wird in Outlook geöffnet.", v.Id, mode);
            case MapiMode.Direct when v.Status is VorgangStatus.Ready or VorgangStatus.Warning:
                var (ok, msg) = await _send.SendAsync(v.Id, user.Name + " (MAPI)", force: v.Status == VorgangStatus.Warning, ct);
                if (ok) return new MapiResult(true, "Versendet.", v.Id, mode);
                _hub.Publish(userId, "neu", v.Id, title, VorgangStatus.SendError, msg, true);
                return new MapiResult(true, "Versand fehlgeschlagen – im Portal.", v.Id, mode);
            default:
                var first = v.Messages.FirstOrDefault(x => x.Level == DocumentChecker.Error)?.Text ?? v.Messages.FirstOrDefault(x => x.Level == DocumentChecker.Warning)?.Text;
                _hub.Publish(userId, "neu", v.Id, title, v.Status, first, true);
                return new MapiResult(true, "Im Portal zur Prüfung.", v.Id, MapiMode.Portal);
        }
    }

    private static List<string> Addresses(MapiMessage m, string type) =>
        m.Recipients.Where(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Address.Trim())
            .Select(a => a.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase) ? a[5..] : a)
            .Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string StripCodes(string body) => System.Text.RegularExpressions.Regex.Replace(body, @"@@[a-z]{2}[^@]{0,500}@@", "").Trim();

    public static string SafeName(string? name)
    {
        var n = Path.GetFileName(name ?? "");
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().Trim('.');
        return n.Length == 0 ? "Anlage" : n.Length > 120 ? n[^120..] : n;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>Leere A4-Seite als Platzhalter, wenn die MAPI-Mail kein PDF enthält (wird nicht angehängt).</summary>
    public static readonly byte[] BlankPdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
        + "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Resources<<>>>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");
}

/// <summary>
/// Named Pipe \\.\pipe\belegpost-mapi für BelegpostMapi.dll (nur lokal, nur angemeldete Windows-Benutzer).
/// Protokoll: 4 Byte Länge (Little Endian) + UTF-8-JSON; Antwort genauso.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MapiPipeServer : BackgroundService
{
    public const string PipeName = "belegpost-mapi";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly IServiceProvider _sp;
    private readonly ILogger<MapiPipeServer> _log;

    public MapiPipeServer(IServiceProvider sp, ILogger<MapiPipeServer> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Zugriffe über das Netzwerk verweigern – nur Prozesse auf diesem Server
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        _log.LogInformation("MAPI-Annahme bereit (\\\\.\\pipe\\{Pipe})", PipeName);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
                await pipe.WaitForConnectionAsync(ct);
                var p = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(p, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "MAPI-Pipe");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        MapiResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            string account = "";
            pipe.RunAsClient(() => account = WindowsIdentity.GetCurrent().Name);
            var len = new byte[4];
            await pipe.ReadExactlyAsync(len, timeout.Token);
            var n = BitConverter.ToInt32(len);
            if (n <= 0 || n > MapiIntake.MaxBytes) throw new InvalidDataException("Nachricht zu groß.");
            var buf = new byte[n];
            await pipe.ReadExactlyAsync(buf, timeout.Token);
            var msg = JsonSerializer.Deserialize<MapiMessage>(buf, Json) ?? throw new InvalidDataException("Leere Nachricht.");
            result = await _sp.GetRequiredService<MapiIntake>().HandleAsync(msg, account, timeout.Token);
            _log.LogInformation("MAPI-Mail von {Account} ({Client}): {Msg}", account, msg.ClientName, result.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MAPI-Mail konnte nicht angenommen werden");
            result = new MapiResult(false, "Belegpost konnte die Mail nicht annehmen: " + ex.Message);
        }
        try
        {
            var outBytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            await pipe.WriteAsync(BitConverter.GetBytes(outBytes.Length), ct);
            await pipe.WriteAsync(outBytes, ct);
            await pipe.FlushAsync(ct);
            pipe.WaitForPipeDrain();
        }
        catch (Exception) { /* Client bereits weg */ }
    }
}
