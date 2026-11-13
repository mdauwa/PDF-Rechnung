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
