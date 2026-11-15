using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung;
using PdfRechnung.Documents;
using PdfRechnung.Printing;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class PrintOriginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "belegpost-origin-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly DateTime T = new(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc);
    private const string Cmd000 = @"K:\WebWARE5\APP\WWA.EXE 000 /M:10000 7c0398c1016e2ba79b826c7e344bfbc0@-0472B9A8 0 0 /S";
    private const string Cmd007 = @"""K:\WebWARE5\APP\WWA.EXE"" 007 /M:10000 abc 0 0 /S";

    private static RpcCall Call(double seconds, int pid, string cmd, Guid? iface = null, string endpoint = "") =>
        new(T.AddSeconds(seconds), pid, "wwa.exe", iface ?? PrintOriginMatcher.Winspool, 17, endpoint, cmd, PrintOriginMatcher.OperatorFromCommandLine(cmd, 1));

    [Theory]
    [InlineData(Cmd000, 1, "000")]
    [InlineData(Cmd007, 1, "007")]
    [InlineData(Cmd000, 2, null)]          // „/M:10000“ ist keine Nummer
    [InlineData("WWA.EXE", 1, null)]
    public void Benutzernummer_aus_der_Befehlszeile(string cmd, int index, string? expected) =>
        Assert.Equal(expected, PrintOriginMatcher.OperatorFromCommandLine(cmd, index));

    [Fact]
    public void Naechster_Druckaufruf_gewinnt()
    {
        var calls = new[] { Call(-15, 1, Cmd007), Call(-0.2, 2, Cmd000), Call(-8, 1, Cmd007) };
        var r = PrintOriginMatcher.Match(calls, T, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Equal("000", r.Operator);
        Assert.Equal(2, r.Pid);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void Gleichzeitige_Drucke_ergeben_Warnung()
    {
        var calls = new[] { Call(-0.2, 2, Cmd000), Call(-0.5, 1, Cmd007) };
        var r = PrintOriginMatcher.Match(calls, T, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Equal("000", r.Operator);
        Assert.NotNull(r.Warning);
    }

    [Fact]
    public void Andere_Aufrufe_und_zu_alte_zaehlen_nicht()
    {
        var calls = new[] { Call(-0.1, 2, Cmd000, Guid.NewGuid(), "irgendwas"), Call(-60, 1, Cmd007) };
        Assert.Null(PrintOriginMatcher.Match(calls, T, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)).Operator);
        // 32-Bit-Druckhelfer (splwow64) zählt als Druckaufruf
        var umpd = new[] { Call(-0.1, 2, Cmd000, Guid.NewGuid(), "UmpdProxy_0_3e7_0_3d") };
        Assert.Equal("000", PrintOriginMatcher.Match(umpd, T, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)).Operator);
    }

    private DocumentProcessor Processor(out UserStore users)
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        users = new UserStore(_dir);
        users.Replace(new List<PortalUser>
        {
            new() { Id = "admin", Name = "Admin", OperatorNumbers = { "000" } },
            new() { Id = "martin", Name = "Martin", OperatorNumbers = { "007" } }
        });
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        return new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users);
    }

    [Fact]
    public void Prozess_ordnet_zu_Steuerzeichen_haben_Vorrang()
    {
        var proc = Processor(out _);
        var a = proc.Process(TestData.Pdf(new[] { "@@toa@b.at@@" }), "Drucker", "a.pdf", "SYSTEM", "SERVER", processOperator: "000");
        Assert.Equal("admin", a.OwnerUserId);
        Assert.Equal("000", a.OperatorNumber);

        var b = proc.Process(TestData.Pdf(new[] { "@@pb007@@", "@@toa@b.at@@" }), "Drucker", "b.pdf", "SYSTEM", "SERVER", processOperator: "000");
        Assert.Equal("martin", b.OwnerUserId);
        Assert.Equal("007", b.OperatorNumber);

        var c = proc.Process(TestData.Pdf(new[] { "@@toa@b.at@@" }), "Drucker", "c.pdf", "SYSTEM", "SERVER", processOperator: "123");
        Assert.Null(c.OwnerUserId);
        Assert.Contains(c.Messages, m => m.Text.Contains("ERP-Benutzer 123"));
    }
}
