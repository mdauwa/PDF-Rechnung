using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung;
using PdfRechnung.Documents;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class MandantTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-md-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData(@"C:\WEBWARE\WWA.EXE 000 /M:10000 0 0 /S", "10000")]
    [InlineData(@"""C:\Program Files\WW\wwa.exe"" 007 /m:2", "2")]
    [InlineData(@"wwa.exe 000 -M:A1", "A1")]
    [InlineData(@"wwa.exe 000 /S", null)]
    [InlineData(null, null)]
    public void Mandant_aus_der_Befehlszeile(string? cmd, string? expected) => Assert.Equal(expected, MandantOptions.FromCommandLine(cmd));

    [Fact]
    public void Vornullen_zaehlen_nicht()
    {
        var o = new MandantOptions { Items = { new Mandant { Number = "000001", Name = "Eins" }, new Mandant { Number = "A01" } } };
        Assert.Equal("Eins", o.Find("1")?.Name);
        Assert.Equal("Eins", o.Find("0001")?.Name);
        Assert.NotNull(o.Find("a01"));
        Assert.Null(o.Find("10"));
        Assert.Null(o.Find("A1")); // nicht rein numerisch: Vornullen zählen
    }

    private DocumentProcessor Create(MandantOptions md, out DocumentStore store)
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var s = DefaultSettings.Create();
        store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        return new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, mandanten: new StaticOptions<MandantOptions>(md));
    }

    [Fact]
    public void Steuerzeichen_pm_uebersteuert_den_Prozess()
    {
        var proc = Create(new MandantOptions(), out var store);
        var a = proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Drucker", "a.pdf", originMandant: "10000", originMandantReason: "Prozess");
        Assert.Equal("10000", a.Mandant);
        var b = proc.Process(TestData.Pdf(new[] { "@@pm20000@@", "@@toa@kunde.at@@" }), "Drucker", "b.pdf", originMandant: "10000", originMandantReason: "Prozess");
        Assert.Equal("20000", b.Mandant);
        Assert.Equal("Steuerzeichen @@pm@@", b.MandantReason);
        Assert.Equal("20000", proc.Recheck(b.Id, "Test")!.Mandant);
        Assert.Equal("10000", proc.Recheck(a.Id, "Test")!.Mandant);
    }

    [Fact]
    public void Ohne_Mandant_im_Beleg_gilt_der_Mandant_des_Benutzers()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser> { new() { Id = "u1", Name = "Max", Email = "max@firma.at", PrintNames = { "m.mustermann" }, Mandanten = { "4" } } });
        var md = new MandantOptions { Items = { new Mandant { Number = "000004", Name = "Graz", OwnErp = true, ErpSecureAppId = "5" }, new Mandant { Number = "10000" } } };
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, mandanten: new StaticOptions<MandantOptions>(md));

        // kein @@pm@@, kein /M:, kein Drucker-Mandant → Mandant des Benutzers
        var a = proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Drucker", "a.pdf", user: "m.mustermann");
        Assert.Equal("000004", a.Mandant);
        Assert.StartsWith("Mandant des Benutzers Max", a.MandantReason);
        Assert.Equal("000004", proc.Recheck(a.Id, "Test")!.Mandant);

        // nicht eingerichteter Mandant aus dem Prozess → ebenfalls der des Benutzers
        var b = proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Drucker", "b.pdf", user: "m.mustermann", originMandant: "777", originMandantReason: "Prozess");
        Assert.Equal("000004", b.Mandant);

        // eingerichteter Mandant aus dem Prozess geht vor; Herkunft bleibt nach „Neu prüfen“ erhalten
        var c = proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Drucker", "c.pdf", user: "m.mustermann", originMandant: "10000", originMandantReason: "Prozess");
        Assert.Equal("10000", c.Mandant);
        Assert.Equal("Prozess", proc.Recheck(c.Id, "Test")!.MandantReason);

        // Benutzer ohne Mandant → allgemeine Verbindung
        var d = proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Drucker", "d.pdf", user: "jemand.anderer");
        Assert.Null(d.Mandant);
    }

    [Fact]
    public void ERP_Verbindung_je_Mandant_mit_Rueckfall_auf_allgemein()
    {
        var global = new ErpOptions { Enabled = true, Endpoint = "https://erp/WWSVC/", Vendor = "v", Application = "a", SecureAppId = "1", Username = "u", Password = "enc:x",
            Fields = { new ErpFieldMapping { Field = "BEL_1_3" } } };
        var eigen = new Mandant { Number = "2", OwnErp = true, ErpSecureAppId = "22" }.ErpFor(global);
        Assert.Equal("22", eigen.SecureAppId);
        Assert.Equal("https://erp/WWSVC/", eigen.Endpoint);
        Assert.Equal("u", eigen.Username);
        Assert.Equal("enc:x", eigen.Password);
        Assert.Same(global.Fields, eigen.Fields);
        // eigene SecuredApp → eigener Servicepass (Bindung und Pass-Datei hängen an der SecuredApp-ID)
        Assert.NotEqual(PdfRechnung.Erp.WwsSettings.From(global, "pw").Binding(), PdfRechnung.Erp.WwsSettings.From(eigen, "pw").Binding());
        Assert.False(new Mandant { Number = "3", ErpOff = true }.ErpFor(global).Enabled);
        Assert.Same(global, new Mandant { Number = "4" }.ErpFor(global));
    }
}

public class StackedCodesTests
{
    [Fact]
    public void Steuerzeichen_an_derselben_Stelle_werden_einzeln_gelesen()
    {
        var codes = new[] { "@@aakunde@firma.at@@", "@@edAngebot 4711@@", "@@saSehr geehrte Damen und Herren,@@", "@@sa@@", "@@pm10000@@" };
        var (_, lines) = PdfText.Extract(TestData.PdfStacked(codes));
        var p = ControlCodeParser.Parse(lines);
        Assert.Equal(new[] { "kunde@firma.at" }, p.To);
        Assert.Equal("Angebot 4711", p.Subject);
        Assert.Equal(2, p.BodyLines.Count);
        Assert.Equal("10000", p.Mandant);
    }
}

public class GotomaxxSmtpProfileTests
{
    [Fact]
    public void SMTP_Profil_sp_wird_Absender_und_ab_ac_ad_bn_sind_ohne_Warnung()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfr-sp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsStore(dir);
            var s = DefaultSettings.Create();
            var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
            var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice), NullLogger<DocumentProcessor>.Instance, settings: settings);
            var v = proc.Process(TestData.PdfStacked(new[] { "@@spausgangsrechnung@noris.at@@", "@@toa@kunde.at@@", "@@ab@@", "@@ac@@", "@@ad@@", "@@bn000@@" }), "Eingang", "a.pdf");
            Assert.Equal("ausgangsrechnung@noris.at", v.Mail.FromAddress);
            Assert.DoesNotContain(v.Messages, m => m.Text.Contains("Unbekanntes Steuerzeichen"));
            Assert.Contains(v.Messages, m => m.Text.Contains("@@bn000@@"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class ExtraPrinterTests
{
    [Fact]
    public void Zusatzdrucker_hat_eigenen_Anschluss()
    {
        var x = new ExtraPrinter { Id = "a1b2c3d4", Name = "Belegpost Firma B" };
        Assert.Equal(@"\\.\pipe\belegpost-druck-a1b2c3d4", x.PortName);
        Assert.True(PdfRechnung.Printing.PrinterIntake.IsValidId(x.Id));
        Assert.False(PdfRechnung.Printing.PrinterIntake.IsValidId("../x"));
        var o = new InboxOptions { Printers = { x } };
        Assert.Same(x, o.FindPrinter("A1B2C3D4"));
    }
}
