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
        Assert.False(new Mandant { Number = "3", ErpOff = true }.ErpFor(global).Enabled);
        Assert.Same(global, new Mandant { Number = "4" }.ErpFor(global));
    }
}
