using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung;
using PdfRechnung.Delivery;
using PdfRechnung.Documents;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class DeliveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-dl-" + Guid.NewGuid().ToString("N")[..8]);
    public DeliveryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Vorgang Beleg(string? buyer, params string[] to) => new()
    {
        Id = "v1", BuyerId = buyer, DocumentType = "Rechnung", Source = "Drucker",
        Mail = new MailData { To = to.ToList(), Subject = "Rechnung 4711", FileName = "Rechnung_4711" }
    };

    [Fact]
    public void Eingangsbuch_vermerkt_Datei_mit_Kopie_und_Vorgang()
    {
        var book = new InboundLog(PdfRechnung.Data.BelegpostDb.For(_dir), _dir);
        var target = Path.Combine(_dir, "eingang");
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, "ORDERS_1.edi");
        File.WriteAllText(path, "UNA:+.? 'UNB+UNOC:3");
        var nr = book.Add(ChannelType.Sftp, "Noris: EDI", "k1", "00123 – Noris", path, File.ReadAllBytes(path));
        Assert.True(nr > 0);
        Assert.Equal(1, book.Unread());

        File.Delete(path); // Eingangsordner hat die Datei verarbeitet – die Kopie bleibt
        var x = book.Download(nr)!.Value;
        Assert.NotNull(x.Path);
        Assert.Equal("UNA:+.? 'UNB+UNOC:3", File.ReadAllText(x.Path!));
        Assert.False(x.Entry.InTarget);
        Assert.True(x.Entry.HasCopy);

        book.LinkVorgang(path.ToUpperInvariant().Replace(_dir.ToUpperInvariant(), _dir), "v42");
        var e = Assert.Single(book.List("noris", null, null, ChannelType.Sftp, unreadOnly: true));
        Assert.Equal("v42", e.VorgangId);
        Assert.Equal("00123 – Noris", e.Customer);
        Assert.Empty(book.List("gibtsnicht", null, null, null, false));

        Assert.Equal(1, book.MarkRead(new[] { nr }));
        Assert.Equal(0, book.Unread());
        Assert.Empty(book.List(null, null, null, null, unreadOnly: true));
    }

    [Fact]
    public void Kunde_ueber_Nummer_oder_Mail_und_Regel_waehlt_Versandwege()
    {
        var store = new CustomerStore(_dir);
        var c = store.Save(new Customer
        {
            Number = "00123", Name = "Noris", Emails = { "*@noris.at" },
            Channels =
            {
                new CustomerChannel { Name = "Buchhaltung", Settings = new ChannelSettings { Type = ChannelType.Mail, To = { "rechnung@noris.at" } } },
                new CustomerChannel { Name = "EDI", Settings = new ChannelSettings { Type = ChannelType.Sftp, Content = ChannelContent.Xml } }
            }
        });
        var edi = c.Channels[1].Id;
        c.Rules.Add(new CustomerRule { Name = "Angebote nur per Mail", DocumentTypes = { "Angebot" }, Channels = { c.Channels[0].Id } });
        c.Rules.Add(new CustomerRule { Name = "Lieferscheine nie", DocumentTypes = { "Lieferschein" }, NoDelivery = true });
        store.Save(c);

        var p1 = DeliveryPlanner.Plan(store, Beleg("123"));
        Assert.Equal("Noris", p1.Customer!.Name);
        Assert.Equal(2, p1.Channels.Count);
        Assert.Contains("Kundennummer", p1.CustomerReason);

        var p2 = DeliveryPlanner.Plan(store, Beleg(null, "einkauf@noris.at"));
        Assert.Contains("E-Mail", p2.CustomerReason);

        var angebot = Beleg("123"); angebot.DocumentType = "Angebot";
        var p3 = DeliveryPlanner.Plan(store, angebot);
        Assert.Single(p3.Channels);
        Assert.DoesNotContain(p3.Channels, x => x.Id == edi);

        var ls = Beleg("123"); ls.DocumentType = "Lieferschein";
        Assert.True(DeliveryPlanner.Plan(store, ls).NoDelivery);

        Assert.Null(DeliveryPlanner.Plan(store, Beleg("999", "x@anders.at")).Customer);
    }

    [Fact]
    public void Kundeneinstellung_uebersteuert_nur_ausgefuellte_Felder()
    {
        var global = new ChannelSettings { Type = ChannelType.Sftp, Host = "sftp.firma.at", User = "belegpost", Password = "enc:g", Folder = "/out", InboundInterval = 10 };
        var m = ChannelSettings.Merge(global, new ChannelSettings { Type = ChannelType.Sftp, Folder = "/kunde42" });
        Assert.Equal("sftp.firma.at", m.Host);
        Assert.Equal("enc:g", m.Password);
        Assert.Equal("/kunde42", m.Folder);
        Assert.Equal(10, m.InboundInterval);
        Assert.Equal(ChannelContent.Pdf, m.Content);
        var own = ChannelSettings.Merge(global, new ChannelSettings { Type = ChannelType.Sftp, User = "kunde", Password = "enc:k" });
        Assert.Equal("enc:k", own.Password);
        Assert.Equal(ChannelContent.Xml, ChannelSettings.Merge(null, new ChannelSettings { Type = ChannelType.Exite }).Content);
    }

    private static X509Certificate2 SelfSigned(string cn, string pfxPath, string cerPath)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, "geheim"));
        File.WriteAllBytes(cerPath, cert.Export(X509ContentType.Cert));
        return cert;
    }

    [Fact]
    public async Task AS2_signiert_verschluesselt_an_eigenen_Empfang_mit_signierter_MDN()
    {
        var a = SelfSigned("Firma A", Path.Combine(_dir, "a.pfx"), Path.Combine(_dir, "a.cer"));
        var b = SelfSigned("Partner B", Path.Combine(_dir, "b.pfx"), Path.Combine(_dir, "b.cer"));
        var settings = new SettingsStore(_dir);
        var inboundDir = Path.Combine(_dir, "eingang-as2");

        // Empfänger „B“: Belegpost mit AS2-Abruf vom Partner „A“
        var receiveOpt = new DeliveryOptions { Channels = { new ChannelSettings {
            Type = ChannelType.As2, InboundEnabled = "ja", As2From = "B", As2To = "A", InboundTarget = inboundDir,
            OwnCertificate = Path.Combine(_dir, "b.pfx"), OwnCertificatePassword = settings.Secrets.Protect("geheim"), PartnerCertificate = Path.Combine(_dir, "a.cer") } } };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<DeliveryOptions>>(new StaticOptions<DeliveryOptions>(receiveOpt));
        builder.Services.AddSingleton(new CustomerStore(_dir));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<InboundService>();
        await using var app = builder.Build();
        As2Receiver.Map(app);
        await app.StartAsync();
        var url = app.Urls.First() + "/as2";

        // Sender „A“
        var send = new ChannelSettings { Type = ChannelType.As2, Url = url, As2From = "A", As2To = "B",
            OwnCertificate = Path.Combine(_dir, "a.pfx"), PartnerCertificate = Path.Combine(_dir, "b.cer"), Sign = "ja", Encrypt = "ja", RequestMdn = "ja" };
        var data = System.Text.Encoding.UTF8.GetBytes("<Invoice>4711</Invoice>");
        var result = await As2Transport.SendAsync(send, "geheim", new DeliveryFile("R4711.xml", data, "application/xml"), default);
        Assert.Contains("processed", result);
        var got = Directory.GetFiles(inboundDir);
        Assert.Single(got);
        Assert.Equal(data, File.ReadAllBytes(got[0]));
        await app.StopAsync();
    }
}
