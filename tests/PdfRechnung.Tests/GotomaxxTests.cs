using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung;
using PdfRechnung.Documents;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class GotomaxxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "belegpost-gotomaxx-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakePrinter : IGotomaxxPrinter
    {
        public GotomaxxJob? Job; public byte[]? Pdf; public string? Fail;
        public IReadOnlyList<string> Printers() => new[] { "PDFMAILER", "PDFMAILER Rechnung" };
        public Task<string> PrintAsync(GotomaxxJob job, byte[] pdf, IReadOnlyCollection<string> windowsUsers, string? clientName, GotomaxxOptions o, CancellationToken ct)
        {
            if (Fail is not null) throw new InvalidOperationException(Fail);
            Job = job; Pdf = pdf;
            return Task.FromResult("Test");
        }
    }

    private (DocumentStore Store, DocumentProcessor Proc, GotomaxxService Service, FakePrinter Printer) Create(GotomaxxOptions o)
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser> { new() { Id = "martin", Name = "Martin", OperatorNumbers = { "007" } } });
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var printer = new FakePrinter();
        var service = new GotomaxxService(store, new StaticOptions<GotomaxxOptions>(o), printer, NullLogger<GotomaxxService>.Instance, users);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, gotomaxx: service);
        return (store, proc, service, printer);
    }

    [Fact]
    public void Nur_Portal_Steuerzeichen_werden_entfernt()
    {
        var codes = new List<ControlCode>
        {
            new(1, "pb", "007"), new(1, "pi", "12345"), new(1, "pu", "FIRMA\\x"), new(1, "pv", "Anrede=Hallo"), new(1, "pt", "CI"),
            new(1, "pf", "a@b.at"), new(1, "pg", "PDFMAILER"), new(1, "xi", " ERP_VOUCHER_INDEX=12345"),
            new(1, "aa", "kunde@firma.at"), new(1, "ed", "Rechnung 1"), new(2, "nd", ""), new(1, "xi", "VOUCHER_ID=R-1"), new(1, "bp", "Firma A")
        };
        var lines = GotomaxxCodes.Forward(codes).Select(GotomaxxCodes.Line).ToList();
        Assert.Equal(new[] { "@@aakunde@firma.at@@", "@@edRechnung 1@@", "@@nd@@", "@@xiVOUCHER_ID=R-1@@", "@@bpFirma A@@" }, lines);
    }

    [Theory]
    [InlineData("@@pg@@", false, "")]
    [InlineData("@@pgPDFMAILER Rechnung@@", false, "PDFMAILER Rechnung")]
    [InlineData("@@pg0@@", true, null)]
    [InlineData("@@toa@b.at@@", true, "")]
    [InlineData("@@toa@b.at@@", false, null)]
    public void Ziel_aus_Steuerzeichen_und_Einstellung(string code, bool always, string? expected)
    {
        var p = ControlCodeParser.Parse(code);
        Assert.Equal(expected, GotomaxxCodes.Target(p, new GotomaxxOptions { Enabled = true, Printer = "PDFMAILER", Always = always }));
        Assert.Null(GotomaxxCodes.Target(p, new GotomaxxOptions { Enabled = false, Always = true }));
    }

    [Fact]
    public async Task Beleg_mit_pg_wird_ohne_Portal_Steuerzeichen_an_gotomaxx_gedruckt()
    {
        var (store, proc, service, printer) = Create(new GotomaxxOptions { Enabled = true, Printer = "PDFMAILER" });
        var v = proc.Process(TestData.Pdf(new[] { "@@pb007@@", "@@pi55@@", "@@pgPDFMAILER Rechnung@@", "@@aakunde@firma.at@@", "@@edAngebot 1@@" }), "Eingang", "a.pdf");
        Assert.Equal(VorgangStatus.Sending, v.Status);
        Assert.Equal(GotomaxxService.Channel, v.SendChannel);
        Assert.Equal("martin", v.OwnerUserId); // Zuordnung im Portal bleibt

        var (ok, msg) = await service.HandOverNowAsync(v.Id, "Test", CancellationToken.None);
        Assert.True(ok, msg);
        Assert.Equal("PDFMAILER Rechnung", printer.Job!.Printer);
        Assert.Equal(new[] { "@@aakunde@firma.at@@", "@@edAngebot 1@@" }, printer.Job.Codes.Select(c => c.Text));
        Assert.DoesNotContain(PdfText.Extract(printer.Pdf!).Lines, l => l.Line.Contains("@@"));

        var done = store.Get(v.Id)!;
        Assert.Equal(VorgangStatus.Gotomaxx, done.Status);
        Assert.Equal(StatusCode.AnGotomaxx, done.StatusCode);
        Assert.Contains(store.Events(v.Id), e => e.Code == StatusCode.AnGotomaxx && e.Channel == GotomaxxService.Channel);
    }

    [Fact]
    public async Task Fehler_beim_Druck_ergibt_840_und_ist_wiederholbar()
    {
        var (store, proc, service, printer) = Create(new GotomaxxOptions { Enabled = true, Printer = "PDFMAILER", Always = true });
        var v = proc.Process(TestData.Pdf(new[] { "@@aakunde@firma.at@@" }), "Eingang", "a.pdf");
        printer.Fail = "Drucker offline";
        var (ok, _) = await service.HandOverNowAsync(v.Id, "Test", CancellationToken.None);
        Assert.False(ok);
        var err = store.Get(v.Id)!;
        Assert.Equal(VorgangStatus.SendError, err.Status);
        Assert.Equal(StatusCode.GotomaxxFehler, err.StatusCode);

        printer.Fail = null;
        (ok, _) = await service.HandOverNowAsync(v.Id, "Test", CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(StatusCode.AnGotomaxx, store.Get(v.Id)!.StatusCode);
    }

    [Fact]
    public void Pg_ohne_eingerichtete_Weiterleitung_ist_ein_Fehler()
    {
        var (_, proc, _, _) = Create(new GotomaxxOptions { Enabled = false });
        var v = proc.Process(TestData.Pdf(new[] { "@@pg@@", "@@aakunde@firma.at@@" }), "Eingang", "a.pdf");
        Assert.Equal(VorgangStatus.Error, v.Status);
        Assert.Contains(v.Messages, m => m.Text.Contains("@@pg@@"));
    }
}
