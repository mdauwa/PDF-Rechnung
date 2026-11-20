using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Documents;
using PdfRechnung.Printing;
using PdfRechnung.Services;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

/// <summary>@@pa@@ / @@pp@@ (Speichern und Drucken über den Melder) und das Trennzeichen beim Weiterleiten an gotomaxx.</summary>
public sealed class MelderJobsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-melder-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Steuerzeichen_pa_und_pp_werden_gelesen()
    {
        var p = ControlCodeParser.Parse("@@pa@@\n@@ppHP Buero@@");
        Assert.Equal("", p.SaveFolder);
        Assert.Equal("HP Buero", p.PrintPrinter);
        var q = ControlCodeParser.Parse("@@pa2026\\Kunden@@\n@@pp0@@");
        Assert.Equal("2026\\Kunden", q.SaveFolder);
        Assert.Equal("0", q.PrintPrinter);
        Assert.Null(ControlCodeParser.Parse("@@edBetreff@@").SaveFolder);
        // gehören nur Belegpost – nicht an gotomaxx weitergeben
        Assert.True(GotomaxxCodes.IsPortalOnly(new ControlCode(1, "pa", "")));
        Assert.True(GotomaxxCodes.IsPortalOnly(new ControlCode(1, "pp", "")));
    }

    [Fact]
    public void Ablageordner_wird_aus_Einstellung_und_Steuerzeichen_gebildet()
    {
        var v = new Vorgang { CreatedAt = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero), BuyerName = "Muster: GmbH", BuyerId = "4711" };
        Assert.Equal(@"\\srv\Ablage", MelderApi.SaveFolderFor("", @"\\srv\Ablage", v));
        Assert.Equal(@"\\srv\Ablage\Rechnungen", MelderApi.SaveFolderFor("Rechnungen", @"\\srv\Ablage\", v));
        Assert.Equal(@"K:\Belege", MelderApi.SaveFolderFor(@"K:\Belege", @"\\srv\Ablage", v));
        Assert.Equal(@"\\nas\x", MelderApi.SaveFolderFor(@"\\nas\x", @"\\srv\Ablage", v));
        Assert.Equal(@"\\srv\Ablage\2026\11\Muster_ GmbH", MelderApi.SaveFolderFor(@"{Jahr}\{Monat}\{Kunde}", @"\\srv\Ablage", v));
        Assert.Equal("", MelderApi.SaveFolderFor(null, "", v));
    }

    [Fact]
    public void Druck_mit_pa_und_pp_legt_Auftraege_fuer_den_Melder_am_Druck_PC_an()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser> { new() { Id = "martin", Name = "Martin", OperatorNumbers = { "007" } } });
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var hub = new EventHub();
        var tasks = new MelderTasks(store.Db, hub);
        var (rds, _) = users.AddDevice("martin", "RDS");
        users.TouchDevice(rds.Id, "RDS01", "windows", new[] { "outlook", "speichern", "drucken" });
        var (pc, _) = users.AddDevice("martin", "Büro");
        users.TouchDevice(pc.Id, "PC-BUERO", "windows", new[] { "outlook", "speichern", "drucken" });
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, events: hub, melderTasks: tasks);

        var v = proc.Process(TestData.Pdf(new[] { "@@pb7@@", "@@pa@@", "@@ppKyocera@@", "@@toa@kunde.at@@", "@@mfRechnung_4711@@" }), "Drucker", "a.pdf", "FIRMA\\mmuster", "RDS01");
        Assert.Equal("", v.SaveRequest);
        Assert.Equal("Kyocera", v.PrintRequest);
        var mine = tasks.Pending("martin", rds.Id);
        Assert.Equal(new[] { MelderTasks.KindSave, MelderTasks.KindPrint }, mine.Select(t => t.Kind));
        Assert.Equal("", mine[0].Data);
        Assert.Equal("Kyocera", mine[1].Data);
        Assert.Empty(tasks.Pending("martin", pc.Id)); // nur der Melder am Computer, an dem gedruckt wurde
        Assert.Contains(store.Get(v.Id)!.History, h => h.Text?.Contains("Speichern über den Melder") == true);
        Assert.Equal("Rechnung_4711.pdf", MelderApi.PdfFileName(v));

        // @@pa0@@ / @@pp0@@ = nichts
        var w = proc.Process(TestData.Pdf(new[] { "@@pb7@@", "@@pa0@@", "@@pp0@@" }), "Drucker", "b.pdf", "FIRMA\\mmuster", "RDS01");
        Assert.Null(w.SaveRequest);
        Assert.Null(w.PrintRequest);
        Assert.Equal(2, tasks.Pending("martin", rds.Id).Count);
    }

    [Fact]
    public void Trennzeichen_aus_der_gotomaxx_Einstellungsdatei()
    {
        const string cfg = "local_settings_filter 0001\r\npdfmailer\r\n{\r\n  defaultFileName \"${dateiName}\"\r\n  commandDelimiter $$\r\n  openPreview true\r\n}\r\n";
        Assert.Equal("$$", GotomaxxProfile.DelimiterFromConfig(cfg));
        Assert.Equal("##", GotomaxxProfile.DelimiterFromConfig("pdfmailer\n{\n  commandDelimiter \"##\"\n}\n"));
        Assert.Null(GotomaxxProfile.DelimiterFromConfig("pdfmailer\n{\n  openPreview true\n}\n"));
        Assert.Null(GotomaxxProfile.DelimiterFromConfig(null));

        Assert.Equal("$$aakunde@firma.at$$", GotomaxxProfile.WithDelimiter("@@aakunde@firma.at@@", "$$"));
        Assert.Equal("@@aa@@", GotomaxxProfile.WithDelimiter("@@aa@@", "@@"));
        Assert.Equal(("$$", "Trennzeichen $$"), GotomaxxProfile.Resolve("$$", "PDFMAILER", null));
        Assert.Equal(("@@", ""), GotomaxxProfile.Resolve("", "PDFMAILER", null));
        Assert.True(GotomaxxProfile.IsValidSetting("auto"));
        Assert.True(GotomaxxProfile.IsValidSetting("$$"));
        Assert.False(GotomaxxProfile.IsValidSetting("$ $"));
        Assert.False(GotomaxxProfile.IsValidSetting("zu lang"));
    }

    [Fact]
    public void Job_traegt_das_eingestellte_Trennzeichen()
    {
        var v = new Vorgang { Id = "x" };
        v.Codes.Add(new ControlCode(1, "aa", "kunde@firma.at"));
        var job = GotomaxxCodes.Job(v, "PDFMAILER", new PdfRechnung.GotomaxxOptions { CommandDelimiter = "$$" });
        Assert.Equal("$$", job.Delimiter);
        Assert.Equal("@@aakunde@firma.at@@", job.Codes[0].Text); // ersetzt wird erst beim Druck
        Assert.Equal("@@", GotomaxxCodes.Job(v, "P", new PdfRechnung.GotomaxxOptions { CommandDelimiter = " " }).Delimiter);
    }

    [Fact]
    public void Speichern_nach_dem_Senden_kommt_erst_mit_Outlook_bzw_Versand()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser> { new() { Id = "martin", Name = "Martin", OperatorNumbers = { "007" }, SendViaOutlook = true } });
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var hub = new EventHub();
        var tasks = new MelderTasks(store.Db, hub);
        var (rds, _) = users.AddDevice("martin", "RDS");
        users.TouchDevice(rds.Id, "RDS01", "windows", new[] { "outlook", "speichern" });
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, events: hub, melderTasks: tasks);
        var send = new SendService(store, settings, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<StorageOptions>(s.Storage),
            new StaticOptions<MailTemplateOptions>(s.MailTemplates), NullLogger<SendService>.Instance, melderTasks: tasks, users: users);

        var v = proc.Process(TestData.Pdf(new[] { "@@pb7@@", "@@pnArchiv@@", "@@toa@kunde.at@@" }), "Drucker", "a.pdf", "FIRMA\\mmuster", "RDS01");
        Assert.Equal("Archiv", v.SaveAfterSendRequest);
        Assert.Empty(tasks.Pending("martin", rds.Id)); // noch nicht gesendet

        send.MarkOutlook(v.Id, "Martin (Outlook)");
        var t = Assert.Single(tasks.Pending("martin", rds.Id));
        Assert.Equal(MelderTasks.KindSave, t.Kind);
        Assert.Equal("Archiv", t.Data);
        Assert.True(store.Get(v.Id)!.SaveAfterSendQueued);

        send.AfterSend(v.Id); // nur einmal
        Assert.Single(tasks.Pending("martin", rds.Id));
        Assert.True(GotomaxxCodes.IsPortalOnly(new ControlCode(1, "pn", "")));
    }

    [Fact]
    public void Alte_Melder_bekommen_keine_Speicher_Auftraege()
    {
        Directory.CreateDirectory(_dir);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser> { new() { Id = "martin", Name = "Martin" } });
        var (alt, _) = users.AddDevice("martin", "Mac alt");
        users.TouchDevice(alt.Id, "MAC1");
        var (neu, _) = users.AddDevice("martin", "Linux");
        users.TouchDevice(neu.Id, "LINUX1", "linux", new[] { "outlookweb", "speichern", "drucken" });
        var a = users.Device(alt.Id)!; var n = users.Device(neu.Id)!;
        Assert.True(UserStore.Can(a, MelderTasks.KindOutlook));
        Assert.False(UserStore.Can(a, MelderTasks.KindSave));
        Assert.True(UserStore.Can(n, MelderTasks.KindSave));
        Assert.Equal("linux", n.Platform);
        Assert.Null(users.DeviceOn("martin", "MAC1", MelderTasks.KindSave));
        Assert.Equal(alt.Id, users.DeviceOn("martin", "MAC1")!.Id);
    }

    [Fact]
    public void Mailprogramm_je_Benutzer_sonst_Vorgabe()
    {
        Assert.Equal(MailClients.OutlookWeb, MailClients.Effective(new PortalUser { MailClient = "outlookweb" }, "outlook"));
        Assert.Equal(MailClients.Standard, MailClients.Effective(new PortalUser(), "standard"));
        Assert.Equal(MailClients.Outlook, MailClients.Effective(new PortalUser { MailClient = "quatsch" }, null));
        Assert.True(MailClients.IsValid(""));
        Assert.False(MailClients.IsValid("thunderbird"));
    }
}
