using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Documents;
using PdfRechnung.Services;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class UserRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-ur-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private UserStore Users()
    {
        Directory.CreateDirectory(_dir);
        var u = new UserStore(_dir);
        u.Replace(new List<PortalUser>
        {
            new() { Id = "martin", Name = "Martin", Email = "m.muster@firma.at", OperatorNumbers = { "007" }, PrintNames = { "FIRMA\\mmuster" } },
            new() { Id = "eva", Name = "Eva", Email = "eva@firma.at", Visibility = "auswahl", AlsoSees = { "martin" } },
            new() { Id = "chef", Name = "Chef", Email = "chef@firma.at", Visibility = "alle" }
        });
        return u;
    }

    [Theory]
    [InlineData("007", "martin")]
    [InlineData("7", "martin")]
    [InlineData("FIRMA\\mmuster", "martin")]
    [InlineData("ANDERE\\MMUSTER", "martin")]
    [InlineData("eva@firma.at", "eva")]
    [InlineData("FIRMA\\eva", "eva")]
    [InlineData("008", null)]
    [InlineData("unbekannt", null)]
    public void Druck_Benutzer_und_Bedienernummer_werden_zugeordnet(string printName, string? expected)
    {
        Assert.Equal(expected, Users().FindByPrintName(printName)?.Id);
    }

    [Fact]
    public void Steuerzeichen_pu_ordnet_Beleg_zu_und_meldet_ihn_live()
    {
        var users = Users();
        var settings = new SettingsStore(_dir);
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var hub = new EventHub();
        using var sub = hub.Subscribe("martin", null);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, events: hub);

        var v = proc.Process(TestData.Pdf(new[] { "@@pu007@@", "@@xiVOUCHER_ID=R-1@@" }), "Eingang", "a.pdf", "TS01\\sammel");
        Assert.Equal("martin", v.OwnerUserId);
        Assert.Equal("007", v.PrintUser);
        Assert.True(sub.Reader.TryRead(out var e));
        Assert.Equal(v.Id, e!.DocumentId);
        Assert.Contains("R-1", e.Title);

        // Sichtbarkeit
        AccessInfo For(string id)
        {
            var pu = users.FindById(id)!;
            var vis = new HashSet<string> { pu.Id };
            if (pu.Visibility == "auswahl") vis.UnionWith(pu.AlsoSees);
            return new AccessInfo(true, pu.IsAdmin, false, true, pu.Id, pu.Name, pu.Email, pu.Visibility == "alle", vis);
        }
        Assert.True(For("martin").CanSee(v));
        Assert.True(For("eva").CanSee(v));   // Vertretung
        Assert.True(For("chef").CanSee(v));  // sieht alle
        var other = new Vorgang { OwnerUserId = "eva" };
        Assert.False(For("martin").CanSee(other));
        Assert.False(For("martin").CanSee(new Vorgang())); // ohne Zuordnung nur „alle“/Admin
    }

    [Theory]
    [InlineData("007", "007")]
    [InlineData("7", "007")]
    [InlineData(" 042 ", "042")]
    [InlineData("1234", null)]
    [InlineData("0", "000")]
    [InlineData("000", "000")]
    [InlineData("0007", "007")]
    [InlineData("A07", null)]
    [InlineData("", null)]
    public void Bedienernummer_wird_vereinheitlicht(string input, string? expected) =>
        Assert.Equal(expected, UserStore.NormalizeOperator(input));

    [Fact]
    public void Steuerzeichen_pb_ordnet_ueber_Bedienernummer_zu_und_warnt_bei_unbekannter()
    {
        var users = Users();
        var settings = new SettingsStore(_dir);
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users);

        var v = proc.Process(TestData.Pdf(new[] { "@@pb007@@" }), "Eingang", "a.pdf", "TS01\\eva");
        Assert.Equal("martin", v.OwnerUserId); // Bedienernummer vor Windows-Benutzer
        Assert.Equal("007", v.OperatorNumber);

        var u = proc.Process(TestData.Pdf(new[] { "@@pb099@@" }), "Eingang", "b.pdf");
        Assert.Null(u.OwnerUserId);
        Assert.Contains(u.Messages, m => m.Text.Contains("099") && m.Text.Contains("keinem Benutzer"));

        // Upload: Bedienernummer hat Vorrang vor dem hochladenden Benutzer, „0“ wird zu „000“
        users.Update(d => d.Users.First(x => x.Id == "eva").OperatorNumbers = new List<string> { "000" });
        var up = proc.Process(TestData.Pdf(new[] { "@@pb0@@" }), "Upload", "d.pdf", "Chef", null, "chef");
        Assert.Equal("eva", up.OwnerUserId);
        Assert.Equal("000", up.OperatorNumber);
        var plain = proc.Process(TestData.Pdf(new[] { "@@toa@b.at@@" }), "Upload", "e.pdf", "Chef", null, "chef");
        Assert.Equal("chef", plain.OwnerUserId);

        var bad = proc.Process(TestData.Pdf(new[] { "@@pbXY@@" }), "Eingang", "c.pdf");
        Assert.Contains(bad.Messages, m => m.Text.Contains("ungültig"));
    }

    [Fact]
    public void EventHub_liefert_verpasste_Ereignisse_nach()
    {
        var hub = new EventHub();
        var e1 = hub.Publish("u", "neu", "A", "A", "bereit");
        var e2 = hub.Publish("u", "neu", "B", "B", "bereit");
        hub.Publish("x", "neu", "C", "C", "bereit");
        using var sub = hub.Subscribe("u", e1.Seq);
        Assert.True(sub.Reader.TryRead(out var r));
        Assert.Equal(e2.Seq, r!.Seq);
        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public void Melder_Schluessel_wird_nur_als_Hash_gespeichert()
    {
        var users = Users();
        var (d, token) = users.AddDevice("martin", "Büro");
        Assert.Equal("martin", users.FindDevice(token)!.Value.User.Id);
        Assert.Null(users.FindDevice(token + "x"));
        Assert.DoesNotContain(token, File.ReadAllText(users.Path));
        Assert.True(users.RemoveDevice(d.Id, "martin"));
        Assert.Null(users.FindDevice(token));
    }
}

public sealed class HandoverLoginTests
{
    private const string Secret = "geheimer-schluessel-1234567890-abcdef";

    [Fact]
    public void Gueltiges_Token_wird_angenommen_und_nur_einmal()
    {
        var t = HandoverLogin.Create(Secret, "m.muster@firma.at", "Martin");
        var r = HandoverLogin.Validate(t, Secret);
        Assert.True(r.Ok, r.Error);
        Assert.Contains("m.muster@firma.at", r.Names);
        Assert.Equal("Martin", r.DisplayName);
        Assert.False(HandoverLogin.Validate(t, Secret).Ok); // jti schon verwendet
    }

    [Fact]
    public void Falscher_Schluessel_abgelaufen_oder_zu_lang_gueltig_wird_abgelehnt()
    {
        Assert.Contains("Signatur", HandoverLogin.Validate(HandoverLogin.Create("anderer", "a@b.at"), Secret).Error);
        Assert.Contains("abgelaufen", HandoverLogin.Validate(HandoverLogin.Create(Secret, "a@b.at", now: DateTimeOffset.UtcNow.AddMinutes(-10)), Secret).Error);
        Assert.Contains("zu lange", HandoverLogin.Validate(HandoverLogin.Create(Secret, "a@b.at", life: TimeSpan.FromHours(1)), Secret).Error);
        Assert.False(HandoverLogin.Validate("kein.token", Secret).Ok);
    }

    [Fact]
    public void Kopplungscode_ist_lesbar()
    {
        var code = MelderApi.PairingCode(new MelderApi.PairingData("https://srv:5444", "tok", "AB12", "Belegpost", "Martin"));
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(code.Replace('-', '+').Replace('_', '/').PadRight(code.Length + (4 - code.Length % 4) % 4, '=')));
        Assert.Contains("\"s\":\"https://srv:5444\"", json);
        Assert.Contains("\"t\":\"tok\"", json);
    }
}

public sealed class OutlookHandoverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-ol-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Mail_fuer_Outlook_enthaelt_Anhang_und_Status_bleibt_Outlook()
    {
        Directory.CreateDirectory(_dir);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser> { new() { Id = "martin", Name = "Martin", Email = "m@firma.at", OperatorNumbers = { "007" } } });
        var (dev, _) = users.AddDevice("martin", "PC");
        Assert.False(users.UsesOutlook("martin"));
        users.SetDeviceOutlook(dev.Id, true);
        Assert.True(users.UsesOutlook("martin"));

        var settings = new SettingsStore(_dir);
        var s = DefaultSettings.Create();
        s.Mail.Enabled = true; s.Mail.FromAddress = "rechnung@firma.at";
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users);
        var v = proc.Process(TestData.Pdf(new[] { "@@pb7@@", "@@tokunde@example.at@@", "@@edRechnung 1@@" }), "Eingang", "a.pdf");

        var send = new SendService(store, settings, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<StorageOptions>(s.Storage),
            new StaticOptions<MailTemplateOptions>(s.MailTemplates), NullLogger<SendService>.Instance);
        var (mail, err) = await send.BuildMail(store.Get(v.Id)!, CancellationToken.None);
        Assert.Null(err);
        Assert.Equal(new[] { "kunde@example.at" }, mail!.To);
        Assert.Single(mail.Attachments);
        Assert.EndsWith(".pdf", mail.Attachments[0].Name);
        Assert.NotNull(mail.HtmlBody);

        var mime = MailSender.BuildMime(s.Mail, mail, includeCopyTo: false);
        Assert.Equal("Rechnung 1", mime.Subject);

        send.MarkOutlook(v.Id, "Martin (Outlook, PC)");
        var after = store.Get(v.Id)!;
        Assert.Equal(VorgangStatus.Outlook, after.Status);
        after.UpdateStatus();
        Assert.Equal(VorgangStatus.Outlook, after.Status);

        // Autoversand lässt Belege von Outlook-Benutzern aus
        s.Mail.AutoSend = true; s.Mail.AutoSendDelayMinutes = 0;
        var v2 = proc.Process(TestData.Pdf(new[] { "@@pb7@@", "@@tokunde@example.at@@" }), "Eingang", "b.pdf");
        var worker = new AutoSendWorker(new StaticOptions<MailOptions>(s.Mail), store, send, NullLogger<AutoSendWorker>.Instance, users);
        await worker.RunOnce(CancellationToken.None);
        Assert.Equal(0, store.Get(v2.Id)!.SendAttempts);
    }
}
