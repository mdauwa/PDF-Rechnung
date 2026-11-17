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
        // Gesucht wird nach dem Wort, der Buchstabe kommt an die 3. Stelle
        Assert.Equal(new[] { "0NN    4711" }, GotomaxxLogService.Candidates(o, E("Angebot - 4711", "4711")));
        Assert.Equal(new[] { "0Nb    4711" }, GotomaxxLogService.Candidates(o, E("Bestellung 4711.pdf", "4711")));
        var falsch = new GotomaxxLogOptions { TypeMap = { new() { Text = "Angebot (N)", Code = "" } } };
        Assert.Equal(new[] { "0NN    4711" }, GotomaxxLogService.Candidates(falsch, E("Angebot - 4711", "4711")));
    }

    private sealed class NoServices : IServiceProvider { public object? GetService(Type t) => null; }

    [Fact]
    public void Mandant_des_Benutzers_geht_vor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfr-gxm-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsStore(dir);
            var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
            var users = new UserStore(dir);
            users.Replace(new List<PortalUser> {
                new() { Id = "u1", Name = "A", Email = "a@f.at", Mandanten = { "010000", "20000" } },
                new() { Id = "u2", Name = "B", Email = "b@f.at", Mandanten = { "99999" } } }); // nicht eingerichtet
            var mand = new PdfRechnung.MandantOptions { Items = { new() { Number = "10000" }, new() { Number = "20000" } } };
            var erp = new ErpFeedbackService(store.Db, store, settings, new StaticOptions<PdfRechnung.ErpOptions>(new PdfRechnung.ErpOptions()), new NoServices(), NullLogger<ErpFeedbackService>.Instance);
            var opt = new GotomaxxLogOptions { Mandant = "30000" };
            var log = new GotomaxxLogService(store.Db, store, new StaticOptions<GotomaxxLogOptions>(opt), erp, settings, NullLogger<GotomaxxLogService>.Instance, users,
                mandanten: new StaticOptions<PdfRechnung.MandantOptions>(mand));
            GotomaxxLogEntry For(string? owner) => E("Rechnung - 1", "1") with { Owner = owner };
            Assert.Equal(new string?[] { "010000", "20000" }, log.MandantenFor(For("u1"), opt));
            Assert.Equal(new string?[] { "30000" }, log.MandantenFor(For("u2"), opt));   // im Zweifel global
            Assert.Equal(new string?[] { "30000" }, log.MandantenFor(For(null), opt));
            Assert.Equal(new string?[] { null }, log.MandantenFor(For(null), new GotomaxxLogOptions()));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

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
                cmd.CommandText = "INSERT INTO Entry(profileName,user,computer,source,timestamp,state,detail) VALUES('Rechnungsversand','x','PC1','Rechnung - 99',$t,1,'')";
                cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$t", now - 400L * 86400); cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO Entry(profileName,user,computer,source,timestamp,state,detail) VALUES('Lager','x','PC2','Rechnung - 1',$t,3,'')";
                cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$t", now); cmd.ExecuteNonQuery();
            }
            var settings = new SettingsStore(dir);
            var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
            var users = new UserStore(dir);
            users.Replace(new List<PortalUser> { new() { Id = "u1", Name = "Max", Email = "max@firma.at", PrintNames = { "m.mustermann" } } });
            var hub = new PdfRechnung.Services.EventHub();
            var sub = hub.Subscribe("u1", null);
            var erp = new ErpFeedbackService(store.Db, store, settings, new StaticOptions<PdfRechnung.ErpOptions>(new PdfRechnung.ErpOptions()), new NoServices(), NullLogger<ErpFeedbackService>.Instance);
            var opt = new GotomaxxLogOptions { Enabled = true, Folder = gx, Profiles = { "Rechnungsversand" } };
            var log = new GotomaxxLogService(store.Db, store, new StaticOptions<GotomaxxLogOptions>(opt), erp, settings, NullLogger<GotomaxxLogService>.Instance, users, hub);
            Assert.Equal(3, await log.ScanAsync(CancellationToken.None));
            Assert.Null(log.State.Error);
            var all = log.List("Rechnungsversand", null, null, null, null);
            Assert.Equal(3, all.Count);
            // Ältere Einträge werden beim ersten Lesen nur angezeigt, nicht ans ERP gemeldet
            Assert.Equal(GotomaxxErpStatus.Old, all.Single(e => e.DocNumber == "99").ErpStatus);
            Assert.Null(all.Single(e => e.DocNumber == "99").Owner);
            var bad = all.Single(e => e.State == 3);
            Assert.Equal("u1", bad.Owner);
            Assert.Contains("Schwerwiegender Fehler", bad.Error);
            Assert.Equal("815", bad.DocNumber);
            Assert.Equal(GotomaxxErpStatus.Off, bad.ErpStatus); // ERP-Rückmeldung nicht eingerichtet
            Assert.True(sub.Reader.TryRead(out var ev));
            Assert.Equal("hinweis", ev!.Kind);
            Assert.Single(log.List(null, null, "fehler", null, null, owners: new[] { "u1" }));
            Assert.Empty(log.List(null, null, null, null, null, owners: new[] { "u2" }));
            Assert.Equal(0, await log.ScanAsync(CancellationToken.None)); // nichts Neues, auch „Lager“ nicht erneut gelesen
            Assert.Equal(new[] { "Rechnungsversand" }, log.ProfileNames());
            Assert.Equal(new[] { "Lager", "Rechnungsversand" }, log.AvailableProfiles());
            // Benutzer später angelegt, Anmeldung mit Domäne bzw. E-Mail – Protokoll hat den Namen ohne Domäne
            users.Replace(new List<PortalUser> { new() { Id = "u1", Name = "Max", Email = "max@firma.at", PrintNames = { "m.mustermann" } },
                new() { Id = "u3", Name = "Lisa", Email = "x@firma.at", Logins = { "FIRMA\\x" } } });
            await log.ScanAsync(CancellationToken.None);
            Assert.Equal("u3", log.List(null, "Rechnung - 99", null, null, null).Single().Owner);
            // Liste muss sich wie im Portal serialisieren lassen (Web-Standard: camelCase)
            var json = System.Text.Json.JsonSerializer.Serialize(all.Select(x => GotomaxxLogApi.Item(x, erp)), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.Contains("\"state\":\"fehler\"", json);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
