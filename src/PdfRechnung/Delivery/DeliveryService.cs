using Microsoft.Extensions.Options;
using PdfRechnung.Documents;

namespace PdfRechnung.Delivery;

/// <summary>Welche Versandwege für einen Beleg gelten (Kunde → Regel → Versandwege).</summary>
public sealed record DeliveryPlan(Customer? Customer, string? CustomerReason, List<CustomerChannel> Channels, string? Rule, bool NoDelivery)
{
    public static readonly DeliveryPlan None = new(null, null, new(), null, false);
    public bool HasChannels => Channels.Count > 0;
}

public static class DeliveryPlanner
{
    public static DeliveryPlan Plan(CustomerStore? customers, Vorgang v)
    {
        if (customers is null || v.Mail.Plain) return DeliveryPlan.None;
        var (c, why) = customers.Find(v);
        if (c is null) return DeliveryPlan.None;
        var active = c.Channels.Where(x => x.Enabled).ToList();
        var ctx = new MailRuleContext(v.DocumentType, v.Stationery, v.Mail.FromAddress, v.Mail.To.Concat(v.MapiTo).ToList(), v.BuyerId, v.BuyerName,
            v.Source, v.Invoice.FormatCode ?? v.FormatName, $"{v.Mail.Subject} {v.Mail.FileName} {v.OriginalFileName}", v.OwnerUserId);
        foreach (var r in c.Rules.Where(r => r.Enabled))
        {
            if (!MailRuleEngine.Matches(r.AsMailRule(), ctx, false)) continue;
            var label = string.IsNullOrWhiteSpace(r.Name) ? "Regel" : $"Regel „{r.Name.Trim()}“";
            if (r.NoDelivery) return new DeliveryPlan(c, why, new(), label, true);
            var ids = r.Channels.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new DeliveryPlan(c, why, ids.Count == 0 ? active : active.Where(x => ids.Contains(x.Id)).ToList(), label, false);
        }
        return new DeliveryPlan(c, why, active, null, false);
    }

    /// <summary>Geplante Versandwege in den Vorgang übernehmen (bereits erfolgreiche bleiben stehen).</summary>
    public static void Apply(Vorgang v, DeliveryPlan plan)
    {
        v.CustomerId = plan.Customer?.Id;
        v.CustomerName = plan.Customer?.Label;
        v.CustomerReason = plan.Customer is null ? null : plan.CustomerReason + (plan.Rule is null ? "" : " · " + plan.Rule);
        var done = v.Deliveries.Where(d => d.Status == "ok").ToDictionary(d => d.ChannelId);
        v.Deliveries = plan.Channels.Select(ch => done.TryGetValue(ch.Id, out var ok) ? ok
            : new VorgangDelivery { ChannelId = ch.Id, Type = ch.Settings.Type, Label = ch.Label }).ToList();
    }
}

/// <summary>Führt die Versandwege eines Kunden aus (FTP, FTPS, SFTP, AS2, eXite, gotomaxx). E-Mail übernimmt der <see cref="SendService"/>.</summary>
public sealed class DeliveryService
{
    private readonly CustomerStore _customers;
    private readonly IOptionsMonitor<DeliveryOptions> _opt;
    private readonly DocumentStore _store;
    private readonly Settings.SettingsStore _settings;
    private readonly IServiceProvider _sp;

    public DeliveryService(CustomerStore customers, IOptionsMonitor<DeliveryOptions> opt, DocumentStore store, Settings.SettingsStore settings, IServiceProvider sp)
    {
        _customers = customers; _opt = opt; _store = store; _settings = settings; _sp = sp;
    }

    public CustomerStore Customers => _customers;

    public DeliveryPlan Plan(Vorgang v) => DeliveryPlanner.Plan(_customers, v);

    /// <summary>Wirksame Einstellungen eines Versandwegs: global + Kunde.</summary>
    public ChannelSettings Effective(ChannelSettings own) => ChannelSettings.Merge(_opt.CurrentValue.Global(own.Type), own);

    public string? Secret(string? enc) => _settings.Secrets.Unprotect(enc);

    /// <summary>Die Dateien des Belegs für einen Versandweg (PDF, XML oder beides).</summary>
    public async Task<List<DeliveryFile>> FilesAsync(Vorgang v, ChannelSettings s, CancellationToken ct)
    {
        var files = new List<DeliveryFile>();
        var baseName = MailTemplate.SafeFileName(string.IsNullOrWhiteSpace(s.FileName) ? v.Mail.FileName : MailTemplate.Apply(s.FileName, v));
        if (ChannelContent.HasPdf(s.Content))
        {
            var pdf = _store.FilePath(v.Id, DocumentStore.SendFile) ?? throw new FileNotFoundException("Das Versand-PDF fehlt – bitte „Neu prüfen“.");
            files.Add(new DeliveryFile(baseName + ".pdf", await File.ReadAllBytesAsync(pdf, ct), "application/pdf"));
        }
        if (ChannelContent.HasAttachments(s.Content))
        {
            var patterns = Patterns(s.AttachmentPattern);
            var found = v.Mail.Attachments.Where(File.Exists).Where(a => patterns.Count == 0 || patterns.Any(p => MailRuleEngine.Like(Path.GetFileName(a), p))).ToList();
            if (found.Count == 0) throw new InvalidOperationException("Der Beleg hat keine passenden Anlagen (@@ma@@/@@mc@@" + (patterns.Count > 0 ? ", Muster " + s.AttachmentPattern : "") + ").");
            foreach (var a in found) files.Add(new DeliveryFile(Path.GetFileName(a), await File.ReadAllBytesAsync(a, ct), MimeTypes.For(a)));
        }
        if (ChannelContent.HasXml(s.Content))
        {
            var xml = _store.FilePath(v.Id, "einvoice.xml");
            if (xml is null)
                throw new InvalidOperationException(string.IsNullOrEmpty(v.FormatName)
                    ? "Für diesen Beleg ist keine E-Rechnung angefordert (@@xt@@) – es gibt keine XML."
                    : "Die E-Rechnungs-XML wird von Belegpost noch nicht erzeugt – bitte als Inhalt „PDF“ wählen.");
            files.Add(new DeliveryFile(baseName + ".xml", await File.ReadAllBytesAsync(xml, ct), "application/xml"));
        }
        return files;
    }

    /// <summary>„*.xml; *.edi“ → Muster.</summary>
    public static List<string> Patterns(string? s) =>
        (s ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Einen Nicht-Mail-Versandweg ausführen. Liefert das Ziel (für den Verlauf).</summary>
    public async Task<string> DeliverAsync(Vorgang v, CustomerChannel ch, CancellationToken ct)
    {
        var s = Effective(ch.Settings);
        switch (s.Type)
        {
            case ChannelType.Ftp or ChannelType.Ftps or ChannelType.Sftp:
                return await FileTransfer.UploadAsync(s.Type, s, Secret(s.Password), await FilesAsync(v, s, ct), ct);
            case ChannelType.Exite:
            {
                var targets = new List<string>();
                foreach (var f in await FilesAsync(v, s, ct)) targets.Add(await ExiteTransport.SendAsync(s, Secret(s.Password), f, ct));
                return string.Join("; ", targets);
            }
            case ChannelType.As2:
            {
                var files = await FilesAsync(v, s, ct);
                var targets = new List<string>();
                foreach (var f in files) targets.Add(await As2Transport.SendAsync(s, Secret(s.OwnCertificatePassword), f, ct));
                return string.Join("; ", targets);
            }
            case ChannelType.Gotomaxx:
            {
                var g = _sp.GetService<GotomaxxService>() ?? throw new InvalidOperationException("gotomaxx ist nicht verfügbar.");
                return await g.PrintForDeliveryAsync(v, s.Printer, ct);
            }
            default:
                throw new InvalidOperationException($"Versandweg „{ChannelType.Text(s.Type)}“ wird hier nicht ausgeführt.");
        }
    }

    /// <summary>Verbindung eines Versandwegs testen (ohne Datei).</summary>
    public async Task<string> TestAsync(ChannelSettings own, CancellationToken ct)
    {
        var s = Effective(own);
        return s.Type switch
        {
            ChannelType.Ftp or ChannelType.Ftps or ChannelType.Sftp => await FileTransfer.TestAsync(s.Type, s, Secret(s.Password), ct),
            ChannelType.Exite => await ExiteTransport.TestAsync(s, Secret(s.Password), ct),
            ChannelType.As2 => TestAs2(s),
            ChannelType.Gotomaxx => string.IsNullOrWhiteSpace(s.Printer) ? throw new InvalidOperationException("Kein Drucker gewählt.") : $"Drucker „{s.Printer}“ gewählt.",
            _ => "E-Mail: Test über Einstellungen → E-Mail & Versand."
        };
    }

    private string TestAs2(ChannelSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Url)) throw new InvalidOperationException("Keine URL.");
        if (s.Sign != "nein")
        {
            if (!File.Exists(s.OwnCertificate)) throw new InvalidOperationException("Eigenes Zertifikat nicht gefunden: " + s.OwnCertificate);
            using var c = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(s.OwnCertificate, Secret(s.OwnCertificatePassword));
            if (!c.HasPrivateKey) throw new InvalidOperationException("Das eigene Zertifikat hat keinen privaten Schlüssel.");
        }
        if (s.Encrypt != "nein" && !File.Exists(s.PartnerCertificate)) throw new InvalidOperationException("Partner-Zertifikat nicht gefunden: " + s.PartnerCertificate);
        return "Zertifikate gelesen – gesendet wird erst mit einem Beleg (die Gegenstelle bestätigt per MDN).";
    }
}
