using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Documents;
using PdfRechnung.Erp;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public class GotomaxxLogTests
{
    private const string Detail = "events\n{\n  entry\n  {\n    name Speichern\n    info \\'C:/Temp/erledigt\\\\beleg.pdf\\'\n  }\n  entry\n  {\n    name Drucken\n    error \"Schwerwiegender Fehler\"\n    info Wien-VK-S3060-F1\n  }\n  entry\n  {\n    name E-Mail\n    error\n    info Interaktiv\n  }\n}\n";

    [Fact]
    public void Ereignisse_und_Fehler_werden_gelesen()
    {
        var ev = GotomaxxLogService.ParseEvents(Detail);
        Assert.Equal(new[] { "Speichern", "Drucken", "E-Mail" }, ev.Select(e => e.Name));
        Assert.Null(ev[0].Error);
        Assert.Equal("Schwerwiegender Fehler", ev[1].Error);
        Assert.Equal("Wien-VK-S3060-F1", ev[1].Info);
        Assert.Equal("", ev[2].Error);
        Assert.Equal("C:/Temp/erledigt\\beleg.pdf", ev[0].Info);
    }

    [Theory]
    [InlineData("WA-Auftrag (alle Belege) - 20241001 (0NA)", "WA-Auftrag", "20241001")]
    [InlineData("Rechnung - 2025123", "Rechnung", "2025123")]
    [InlineData("Dokument", null, null)]
    public void Belegart_und_Nummer_aus_dem_Dokumentnamen(string title, string? art, string? nr)
    {
        var (a, n) = GotomaxxLogService.ParseTitle(title, new GotomaxxLogOptions());
        Assert.Equal(art, a);
        Assert.Equal(nr, n);
    }

    private static GotomaxxLogEntry E(string source, string nr) =>
        new(1, "u.dat", 1, "P", null, null, source, DateTimeOffset.Now, 1, null, null, null, nr, null, null, null, null, null);

    [Fact]
    public void Belegindex_wird_erraten()
    {
        var o = new GotomaxxLogOptions();
        Assert.Equal(new[] { "0NA20241001" }, GotomaxxLogService.Candidates(o, E("WA-Auftrag (alle Belege) - 20241001 (0NA)", "20241001")));
        Assert.Equal(new[] { "0NR 1234567" }, GotomaxxLogService.Candidates(o, E("Rechnung - 1234567", "1234567")));
        Assert.Equal(new[] { "0NL    4711" }, GotomaxxLogService.Candidates(o, E("Lieferschein 4711", "4711")));
        Assert.Equal(new[] { "0NR    4711", "0NL    4711", "0NA    4711", "0NN    4711", "0Nb    4711" }, GotomaxxLogService.Candidates(o, E("Beleg - 4711", "4711")));
    }

    private sealed class NoServices : IServiceProvider { public object? GetService(Type t) => null; }

    [Fact]
    public async Task Protokoll_wird_gelesen_zugeordnet_und_gemeldet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfr-gx-" + Guid.NewGuid().ToString("N")[..8]);
        var gx = Path.Combine(dir, "gx"); Directory.CreateDirectory(gx);
        try
        {
            // gotomaxx-Datenbank wie im Original (WAL)
            var file = Path.Combine(gx, "usage_test.dat");
            using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Entry(entryId INTEGER PRIMARY KEY AUTOINCREMENT, profileName TEXT, user TEXT, computer TEXT, source TEXT, timestamp INTEGER, state INTEGER, detail TEXT);";
                cmd.ExecuteNonQuery();
                var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                foreach (var (src, st, det) in new[] { ("Rechnung - 4711", 1, "events\n{\n  entry\n  {\n    name E-Mail\n    info a@b.at\n  }\n}"), ("Lieferschein - 815", 3, Detail) })
                {
                    cmd.CommandText = "INSERT INTO Entry(profileName,user,computer,source,timestamp,state,detail) VALUES('Rechnungsversand','m.mustermann','PC1',$s,$t,$st,$d)";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$s", src); cmd.Parameters.AddWithValue("$t", now); cmd.Parameters.AddWithValue("$st", st); cmd.Parameters.AddWithValue("$d", det);
                    cmd.ExecuteNonQuery();
                }
            }
            var settings = new SettingsStore(dir);
            var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
            var users = new UserStore(dir);
            users.Replace(new List<PortalUser> { new() { Id = "u1", Name = "Max", Email = "max@firma.at", PrintNames = { "m.mustermann" } } });
            var hub = new PdfRechnung.Services.EventHub();
            var sub = hub.Subscribe("u1", null);
            var erp = new ErpFeedbackService(store.Db, store, settings, new StaticOptions<PdfRechnung.ErpOptions>(new PdfRechnung.ErpOptions()), new NoServices(), NullLogger<ErpFeedbackService>.Instance);
            var opt = new GotomaxxLogOptions { Enabled = true, Folder = gx };
            var log = new GotomaxxLogService(store.Db, store, new StaticOptions<GotomaxxLogOptions>(opt), erp, settings, NullLogger<GotomaxxLogService>.Instance, users, hub);
            Assert.Equal(2, await log.ScanAsync(CancellationToken.None));
            Assert.Null(log.State.Error);
            var all = log.List("Rechnungsversand", null, null, null, null);
            Assert.Equal(2, all.Count);
            var bad = all.Single(e => e.State == 3);
            Assert.Equal("u1", bad.Owner);
            Assert.Contains("Schwerwiegender Fehler", bad.Error);
            Assert.Equal("815", bad.DocNumber);
            Assert.Equal(GotomaxxErpStatus.Off, bad.ErpStatus); // ERP-Rückmeldung nicht eingerichtet
            Assert.True(sub.Reader.TryRead(out var ev));
            Assert.Equal("hinweis", ev!.Kind);
            Assert.Single(log.List(null, null, "fehler", null, null, owners: new[] { "u1" }));
            Assert.Empty(log.List(null, null, null, null, null, owners: new[] { "u2" }));
            Assert.Equal(0, await log.ScanAsync(CancellationToken.None)); // nichts Neues
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
