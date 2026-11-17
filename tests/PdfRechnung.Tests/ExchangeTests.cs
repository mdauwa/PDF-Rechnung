using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Api;
using PdfRechnung.Exchange;
using PdfRechnung.Monitoring;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class ExchangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-ex-" + Guid.NewGuid().ToString("N")[..8]);
    public ExchangeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static FilialverbundOptions Opt(string outDir, string inDir) => new()
    {
        Enabled = true, OutFolder = outDir, InFolder = inDir,
        Standorte = { new Standort { Code = "F01" }, new Standort { Code = "F02" }, new Standort { Code = "F03", ForwardTo = "F01" }, new Standort { Code = "F04", Active = false } },
        Gruppen = { new StandortGruppe { Name = "ALLE", Members = { "F01", "F02", "F03", "F04" } } },
        Regeln = { new VerteilRegel { Pattern = "ART*.dta", Targets = "ALLE" }, new VerteilRegel { Pattern = "*_F02.dta", Targets = "F02" } }
    };

    [Fact]
    public async Task Filialverbund_verteilt_nach_Ordner_Gruppe_und_Regel()
    {
        var outDir = Path.Combine(_dir, "aus"); var inDir = Path.Combine(_dir, "ein");
        Directory.CreateDirectory(Path.Combine(outDir, "F01")); Directory.CreateDirectory(Path.Combine(outDir, "ALLE")); Directory.CreateDirectory(Path.Combine(outDir, "XYZ"));
        void Write(string p) { File.WriteAllText(p, "þSKZþART"); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-1)); }
        Write(Path.Combine(outDir, "F01", "preise.dta"));
        Write(Path.Combine(outDir, "ALLE", "kunden.dta"));
        Write(Path.Combine(outDir, "XYZ", "weg.dta"));
        Write(Path.Combine(outDir, "ARTIKEL.dta"));
        Write(Path.Combine(outDir, "lager_F02.dta"));
        Write(Path.Combine(outDir, "unbekannt.dta"));
        Write(Path.Combine(outDir, "notiz.txt"));
        var settings = new SettingsStore(_dir);
        var db = PdfRechnung.Data.BelegpostDb.For(_dir);
        var store = new ExchangeStore(db, _dir);
        var fv = new FilialverbundService(new StaticOptions<FilialverbundOptions>(Opt(outDir, inDir)), store, settings, NullLogger<FilialverbundService>.Instance);
        Assert.Equal(4, await fv.DistributeAsync(CancellationToken.None));
        Assert.Equal(2, fv.State.Unassigned.Count);  // XYZ\weg.dta, unbekannt.dta
        Assert.Equal(new[] { "ARTIKEL.dta", "kunden.dta", "preise.dta" }, store.Pending("F01").Select(e => e.FileName).Order());
        Assert.Equal(new[] { "ARTIKEL.dta", "kunden.dta", "lager_F02.dta" }, store.Pending("F02").Select(e => e.FileName).Order());
        Assert.Empty(store.Pending("F04")); // inaktiv
        Assert.True(File.Exists(Path.Combine(outDir, "notiz.txt")));
        Assert.True(Directory.GetFiles(Path.Combine(outDir, "verteilt"), "*", SearchOption.AllDirectories).Length == 4);

        // Rückläufer von F03 → Eingang\F03 und weiter an F01
        var (nr, path, fwd) = fv.Receive("F03", "umsatz.dta", new byte[] { 1, 2, 3 }, null);
        Assert.True(File.Exists(path));
        Assert.Contains(Path.Combine("ein", "F03"), path);
        Assert.Equal(new[] { "F01" }, fwd);
        Assert.Contains(store.Pending("F01"), e => e.FileName == "umsatz.dta");

        // Quittung
        var e = store.Pending("F02").First();
        store.SetStatus(e.Nr, ExchangeStatus.Failed, "3 Sätze nicht übernommen");
        Assert.Contains(store.Problems(TimeSpan.FromHours(1)), x => x.Nr == e.Nr);
        store.Beat("F02", "1.0", "PC1", null, "10.0.0.1");
        Assert.Equal("PC1", store.Beats()["f02"].Machine);
    }

    [Fact]
    public void ApiSchluessel_nur_Hash_gespeichert()
    {
        var keys = new ApiKeyStore(_dir);
        var (k, secret) = keys.Create("Monitoring", new[] { ApiKey.ScopeStatus, "unsinn" }, "");
        Assert.StartsWith("bp_", secret);
        Assert.Equal(new[] { ApiKey.ScopeStatus }, k.Scopes);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_dir, "apikeys.json")));
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Headers["Authorization"] = "Bearer " + secret;
        Assert.Equal(k.Id, keys.Authenticate(ctx)?.Id);
        ctx.Request.Headers["Authorization"] = "Bearer bp_falsch";
        Assert.Null(keys.Authenticate(ctx));
        Assert.Equal(k.Id, new ApiKeyStore(_dir).All().Single().Id);
    }

    [Fact]
    public void Benachrichtigung_Mailtext()
    {
        var r = new HealthReport(HealthLevel.Error, DateTimeOffset.Now, "1.0", "SRV", TimeSpan.FromHours(1), new());
        var due = new List<HealthCheck> { new("versandweg-x", "versandwege", "SFTP Hagebau", HealthLevel.Error, "Verbindung abgelehnt", DateTimeOffset.Now.AddHours(-1)) };
        var (subject, text, html) = AlertService.Compose(r, due, new(), new() { ("Ausgang EDI", DateTimeOffset.Now.AddHours(-3)) });
        Assert.Contains("1 Problem ohne Reaktion", subject);
        Assert.Contains("SFTP Hagebau", text);
        Assert.Contains("Wieder in Ordnung", html);
        Assert.True(PdfRechnung.Delivery.ScheduleEngine.Allowed("Mo-Fr", "07:00", "18:00", new DateTime(2026, 11, 16, 9, 0, 0)));
        Assert.False(PdfRechnung.Delivery.ScheduleEngine.Allowed("Mo-Fr", "07:00", "18:00", new DateTime(2026, 11, 15, 9, 0, 0)));
    }
}
