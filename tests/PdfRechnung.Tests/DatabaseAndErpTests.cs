using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Documents;
using PdfRechnung.Erp;
using PdfRechnung.Services;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class DatabaseAndErpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-db-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (DocumentStore Store, DocumentProcessor Proc, UserStore Users, SettingsStore Settings) Create()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser>
        {
            new() { Id = "martin", Name = "Martin", OperatorNumbers = { "007" }, PrintNames = { "FIRMA\\mmuster" }, SendViaOutlook = true },
            new() { Id = "eva", Name = "Eva", Visibility = "auswahl", AlsoSees = { "martin" } },
            new() { Id = "otto", Name = "Otto" }
        });
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users);
        return (store, proc, users, settings);
    }

    [Fact]
    public void Vorgang_liegt_in_der_Datenbank_mit_Belegindex_Zeitstrahl_und_Sichtbarkeit()
    {
        var (store, proc, _, _) = Create();
        var v = proc.Process(TestData.Pdf(new[] { "@@pb7@@", "@@pi0NR00012345@@", "@@xiVOUCHER_ID=R-1@@", "@@toa@kunde.at@@" }), "Eingang", "a.pdf");
        Assert.Equal("0NR00012345", v.ErpIndex);
        Assert.Equal("martin", v.OwnerUserId);
        var ev = store.Events(v.Id);
        Assert.Equal(StatusCode.Erfasst, ev[0].Code);
        Assert.Equal(v.StatusCode, ev[^1].Code);

        // Sichtbarkeit als Datenbankfilter
        var mine = new VorgangFilter { Owners = new[] { "martin" } };
        Assert.Single(store.Query(mine));
        Assert.Single(store.Query(new VorgangFilter { Owners = new[] { "eva", "martin" } }));
        Assert.Empty(store.Query(new VorgangFilter { Owners = new[] { "otto" } }));
        Assert.Single(store.Query(new VorgangFilter { SeeAll = true, Text = "r-1" }));
        Assert.Empty(store.Query(new VorgangFilter { SeeAll = true, Text = "gibtsnicht" }));
        Assert.Equal(1, store.CountByStatus(new VorgangFilter { SeeAll = true })[v.Status]);
        Assert.Single(store.ByErpIndex("0NR00012345"));

        // Statuswechsel → Zeitstrahl; zweiter Versand = 120
        store.Update(v.Id, x => { x.Status = VorgangStatus.Sent; x.SentAt = DateTimeOffset.Now; x.SendChannel = "Mail"; return true; });
        Assert.Equal(StatusCode.Versendet, store.Get(v.Id)!.StatusCode);
        store.Update(v.Id, x => { x.Status = VorgangStatus.SendError; return true; });
        store.Update(v.Id, x => { x.Status = VorgangStatus.Sent; return true; });
        Assert.Equal(StatusCode.ErneutVersendet, store.Get(v.Id)!.StatusCode);
        Assert.Equal(2, store.Get(v.Id)!.SendCount);
    }

    [Fact]
    public void Alte_meta_json_werden_beim_Start_uebernommen()
    {
        var dir = Path.Combine(_dir, "vorgaenge", "2026", "11", "20261101-100000-abcdef");
        Directory.CreateDirectory(dir);
        var old = new Vorgang { Id = "20261101-100000-abcdef", CreatedAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.FromHours(1)), Status = VorgangStatus.Sent, VoucherId = "ALT-1", SentAt = DateTimeOffset.Now };
        File.WriteAllText(Path.Combine(dir, DocumentStore.MetaFile), JsonSerializer.Serialize(old, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.WriteAllBytes(Path.Combine(dir, DocumentStore.PdfFile), new byte[] { 1 });
        var (store, _, _, _) = Create();
        var v = store.Get(old.Id)!;
        Assert.Equal("ALT-1", v.VoucherId);
        Assert.Equal(StatusCode.Versendet, v.StatusCode);
        Assert.True(File.Exists(Path.Combine(dir, DocumentStore.MetaFile + ".migriert")));
        Assert.NotNull(store.FilePath(old.Id, DocumentStore.PdfFile));
        Assert.True(store.Events(old.Id).Count >= 2);
    }

    [Fact]
    public void Benutzer_werden_aus_users_json_uebernommen()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "users.json"), """{"users":[{"id":"a1","name":"Anna","operatorNumbers":["003"]}],"devices":[]}""");
        var u = new UserStore(_dir);
        Assert.Equal("Anna", u.FindByOperator("3")!.Name);
        Assert.True(File.Exists(Path.Combine(_dir, "users.json.migriert")));
        Assert.Equal("Anna", new UserStore(_dir).FindById("a1")!.Name);
    }

    [Theory]
    [InlineData("192.168.10.0/24", "192.168.10.77", true)]
    [InlineData("192.168.10.0/24", "192.168.11.1", false)]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("10.1.2.3", "10.1.2.3", true)]
    [InlineData("10.1.2.3", "10.1.2.4", false)]
    [InlineData("fd00::/8", "fd12::1", true)]
    [InlineData("192.168.0.0/16", "::ffff:192.168.5.5", true)]
    public void IP_Bereiche_fuer_den_Status_Link(string net, string ip, bool expected)
    {
        Assert.True(Network.TryParse(net, out var n));
        Assert.Equal(expected, n.Contains(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Status_Link_zeigt_nur_Status_und_Belegnummer()
    {
        var (store, proc, _, _) = Create();
        var v = proc.Process(TestData.Pdf(new[] { "@@pi0NR00000555@@", "@@xiVOUCHER_ID=R-9@@", "@@togeheim@kunde.at@@", "@@xiBUYER_NAME=Geheimkunde@@" }), "Eingang", "b.pdf");
        var opt = new StatusLinkOptions { AllowedNetworks = { "127.0.0.0/8" } };
        var svc = new StatusLinkService(store.Db, store, new StaticOptions<StatusLinkOptions>(opt), new StaticOptions<PortalOptions>(new PortalOptions { PublicUrl = "https://bp.firma.at:5444" }),
            new StaticOptions<ServerOptions>(new ServerOptions()));
        var url = svc.UrlFor(v);
        Assert.StartsWith("https://bp.firma.at:5444/s/", url);
        var token = url[(url.LastIndexOf('/') + 1)..];
        Assert.Equal(token, svc.TokenFor(v)); // stabil
        Assert.True(token.Length >= 20);
        var view = svc.Resolve(token)!;
        Assert.Equal("R-9", view.VoucherId);
        var html = StatusLinkService.RenderHtml(view, "Belegpost");
        Assert.DoesNotContain("geheim@kunde.at", html);
        Assert.DoesNotContain("Geheimkunde", html);
        Assert.Contains("R-9", html);
        // PDF über den Link
        Assert.Single(view.Pdfs);
        Assert.NotNull(svc.PdfPath(view.Pdfs[0].VorgangId));
        Assert.Contains($"{token}/pdf/0", StatusLinkService.RenderHtml(view, "Belegpost", token));
        opt.ShowPdf = false;
        Assert.Empty(svc.Resolve(token)!.Pdfs);
        opt.ShowPdf = true;
        Assert.Null(svc.Resolve("falsch"));
        svc.Revoke(v);
        Assert.Null(svc.Resolve(token));

        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        Assert.True(svc.IsAllowed(ctx));
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
        Assert.False(svc.IsAllowed(ctx));
    }

    [Fact]
    public void Rueckgabefelder_Laenge_aus_Feldname_Pruefung_und_Kuerzen()
    {
        Assert.Equal(8, ErpValues.LengthFromName("BEL_3_8"));
        Assert.Null(ErpValues.LengthFromName("BEL_STATUS"));
        var o = new ErpOptions
        {
            Fields =
            {
                new() { Field = "BEL_1_3", Value = "statuscode" },
                new() { Field = "BEL_3_8", Value = "datum" },               // 10 Zeichen → Warnung
                new() { Field = "BEL_4_40", Value = "vorlage", Format = "{Statustext} am {Datum}" },
                new() { Field = "BEL_5_2", Value = "bediener" }             // zu kurz → Fehler
            },
            Codes = { new() { Code = StatusCode.VersendetOutlook, ErpValue = "111" } }
        };
        var (errors, warnings) = ErpValues.Validate(o);
        Assert.Contains(errors, e => e.Contains("BEL_5_2"));
        Assert.Contains(warnings, w => w.Contains("BEL_3_8"));
        o.Fields.RemoveAt(3);
        Assert.Empty(ErpValues.Validate(o).Errors);

        var v = new Vorgang { Id = "x", OperatorNumber = "007" };
        var at = new DateTimeOffset(2026, 11, 15, 10, 34, 0, TimeSpan.FromHours(1));
        var f = ErpFieldBuilder.Build(o, new ErpValueContext(v, StatusCode.VersendetOutlook, at, "Outlook", null));
        Assert.Equal("111", f["BEL_1_3"]);
        Assert.Equal(8, f["BEL_3_8"].Length);
        Assert.StartsWith("versendet über Outlook am ", f["BEL_4_40"]);
        Assert.False(ErpFieldBuilder.ShouldWrite(o, StatusCode.Erfasst));
        Assert.True(ErpFieldBuilder.ShouldWrite(o, StatusCode.Versendet));
    }

    /// <summary>WWSVC-Gegenstelle für Tests.</summary>
    private sealed class FakeWwsvc : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public int ValidateStatus = 200;
        public string? LastPutBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            string Body(int status, string extra = "") => $"{{\"COMRESULT\":{{\"STATUS\":{status}}}{extra}}}";
            HttpResponseMessage Json(int http, string body) => new((HttpStatusCode)http) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (path.Contains("/WWSERVICE/REGISTER/")) { Calls.Add("REGISTER"); return Json(200, Body(200, ",\"SERVICEPASS\":{\"PASSID\":\"PASS1\",\"APPID\":\"SECRET1\"}")); }
            if (path.Contains("/WWSERVICE/VALIDATE/")) { Calls.Add("VALIDATE"); return Json(ValidateStatus, Body(ValidateStatus)); }
            var content = await r.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);
            var fn = doc.RootElement.GetProperty("WWSVC_FUNCTION").GetProperty("FUNCTIONNAME").GetString();
            Calls.Add(fn! + "#" + r.Headers.GetValues("WWSVC-REQID").Single());
            if (fn == "BELEG.GET") return Json(200, Body(200, ",\"BELEGLISTE\":{\"BELEG\":[{\"BELNDX\":\"555\",\"BEL_1_3\":\"\"}],\"ANZAHL\":1}"));
            LastPutBody = content;
            return Json(200, Body(200));
        }
    }

    [Fact]
    public async Task Servicepass_wird_registriert_geprueft_und_Anfragenummer_gespeichert()
    {
        var fake = new FakeWwsvc();
        var s = new WwsSettings("https://erp.firma.at/WWSVC/", "VENDOR1", "APP1", "42", 1, "", "", AllowRegistration: true);
        Assert.Empty(s.Validate());
        var saved = new Dictionary<string, ErpServicePass>();
        ErpServicePass? Load(string b) => saved.TryGetValue(b, out var p) ? JsonSerializer.Deserialize<ErpServicePass>(JsonSerializer.Serialize(p)) : null;
        var mgr = new ErpServicePassManager(s, new WwsServiceClient(new HttpClient(fake), s), Load, (b, p) => saved[b] = JsonSerializer.Deserialize<ErpServicePass>(JsonSerializer.Serialize(p))!);

        Assert.NotNull(await mgr.ReadDocumentAsync("555", default));
        await mgr.WriteDocumentAsync("555", new Dictionary<string, string> { ["BEL_1_3"] = "100" }, default);
        Assert.Equal(new[] { "REGISTER", "BELEG.GET#1", "VALIDATE", "BELEG.PUT#3" }, fake.Calls);
        Assert.Contains("\"BEL_1_3\"", fake.LastPutBody);
        Assert.Contains("KEINE_VERBUCHUNG", fake.LastPutBody);
        var pass = saved.Values.Single();
        Assert.Equal("Ready", pass.State);
        Assert.Equal(3, pass.RequestId);

        // Freigabe ausstehend → Fehler „PendingApproval“, Zustand gespeichert
        fake.ValidateStatus = 202;
        var ex = await Assert.ThrowsAsync<ErpException>(() => mgr.CheckAsync(default));
        Assert.Equal("PendingApproval", ex.Code);
        Assert.Equal("PendingApproval", saved.Values.Single().State);
    }

    [Fact]
    public async Task Registrierung_mit_unklarem_Ausgang_wird_nie_blind_wiederholt()
    {
        var s = new WwsSettings("https://erp.firma.at/WWSVC/", "VENDOR1", "APP1", "42", 1, "", "", AllowRegistration: true);
        var saved = new Dictionary<string, ErpServicePass>();
        var handler = new FailingHandler();
        var mgr = new ErpServicePassManager(s, new WwsServiceClient(new HttpClient(handler), s), b => saved.GetValueOrDefault(b), (b, p) => saved[b] = p);
        await Assert.ThrowsAsync<ErpException>(() => mgr.CheckAsync(default));
        Assert.Equal("RegistrationOutcomeUnknown", saved.Values.Single().State);
        var again = await Assert.ThrowsAsync<ErpException>(() => mgr.CheckAsync(default));
        Assert.Equal("RegistrationOutcomeUnknown", again.Code);
        Assert.Equal(1, handler.Count); // kein zweiter REGISTER
        mgr.ResetUnknownRegistration();
        Assert.Equal("Missing", saved.Values.Single().State);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Count++;
            throw new HttpRequestException("weg", null, HttpStatusCode.ServiceUnavailable);
        }
    }

    [Fact]
    public void Warteschlange_pro_Belegindex_zaehlt_der_neueste_Status()
    {
        var (store, proc, _, settings) = Create();
        var v = proc.Process(TestData.Pdf(new[] { "@@pi0NR00000777@@", "@@toa@kunde.at@@" }), "Eingang", "c.pdf");
        var erp = new ErpFeedbackService(store.Db, store, settings, new StaticOptions<ErpOptions>(new ErpOptions()), new EmptyServices(),
            NullLogger<ErpFeedbackService>.Instance);
        var t0 = DateTimeOffset.Now;
        erp.Enqueue("0NR00000777", v.Id, StatusCode.WartetFreigabe, t0);
        erp.Enqueue("0NR00000777", v.Id, StatusCode.Versendet, t0.AddSeconds(5));
        erp.Enqueue("0NR00000777", v.Id, StatusCode.AnOutlook, t0.AddSeconds(1)); // älter → gleich ersetzt
        var jobs = erp.Jobs(null, v.Id);
        Assert.Equal(ErpJobStatus.Open, jobs.Single(j => j.Code == StatusCode.Versendet).Status);
        Assert.All(jobs.Where(j => j.Code != StatusCode.Versendet), j => Assert.Equal(ErpJobStatus.Superseded, j.Status));
    }

    [Fact]
    public void Warteschlange_trennt_Mandanten_und_zeigt_die_SecuredApp()
    {
        var (store, proc, _, settings) = Create();
        var v = proc.Process(TestData.Pdf(new[] { "@@pi0NR00000777@@", "@@toa@kunde.at@@" }), "Eingang", "m.pdf");
        var md = new MandantOptions { Items = { new Mandant { Number = "000004", OwnErp = true, ErpSecureAppId = "5" } } };
        var erp = new ErpFeedbackService(store.Db, store, settings, new StaticOptions<ErpOptions>(new ErpOptions { SecureAppId = "1" }), new EmptyServices(),
            NullLogger<ErpFeedbackService>.Instance, new StaticOptions<MandantOptions>(md));
        var t0 = DateTimeOffset.Now;
        erp.Enqueue("0NR00000777", v.Id, StatusCode.Versendet, t0, "4");
        erp.Enqueue("0NR00000777", v.Id + "x", StatusCode.Versendet, t0.AddSeconds(5), null); // gleicher Index in der Hauptverbindung
        var a = erp.Jobs(null, v.Id).Single();
        var b = erp.Jobs(null, v.Id + "x").Single();
        Assert.Equal(ErpJobStatus.Open, a.Status); // nicht durch den anderen Mandanten ersetzt
        Assert.Equal(("000004", "5"), (a.Mandant, a.SecureApp));
        Assert.Equal(("", "1"), (b.Mandant, b.SecureApp));

        // seitenweise mit Filter
        Assert.Equal(2, erp.JobsPage(null, null, null, 0, 1).Total);
        Assert.Single(erp.JobsPage(null, null, null, 1, 1).Items);
        Assert.Equal(v.Id, erp.JobsPage(null, null, "4", 0, 50).Items.Single().VorgangId);
        Assert.Equal(v.Id + "x", erp.JobsPage(null, null, "-", 0, 50).Items.Single().VorgangId);
        Assert.Equal(2, erp.JobsPage(ErpJobStatus.Open, "0NR00000777", null, 0, 50).Total);
        Assert.Equal(0, erp.JobsPage(null, "gibtsnicht", null, 0, 50).Total);

        // an einen anderen Mandanten neu senden, Vorgang mit umstellen; „Neu prüfen“ lässt den Mandanten dann stehen
        var neu = erp.Resend(b.Id, "4", applyToSource: false, "Admin")!;
        Assert.Equal(("000004", "5", ErpJobStatus.Open), (neu.Mandant, neu.SecureApp, neu.Status));
        Assert.Equal(ErpJobStatus.Discarded, erp.Jobs(null, v.Id + "x").Single(j => j.Id == b.Id).Status);
        var again = erp.Resend(a.Id, "", applyToSource: true, "Admin")!;
        Assert.Equal(("", "1"), (again.Mandant, again.SecureApp));
        Assert.True(store.Get(v.Id)!.MandantManual);
        Assert.Null(proc.Recheck(v.Id, "Test")!.Mandant);
        Assert.Throws<ArgumentException>(() => erp.Resend(a.Id, "999", false, "Admin"));
    }

    [Fact]
    public void Belegordner_je_Mandant_und_Verschieben()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var so = new StorageOptions();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance, new StaticOptions<StorageOptions>(so));
        var s = DefaultSettings.Create();
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings);
        var a = proc.Process(TestData.Pdf(new[] { "@@pm000004@@", "@@toa@kunde.at@@" }), "Eingang", "a.pdf");
        var b = proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Eingang", "b.pdf");
        var dirA = store.Directory_(a.Id)!;
        Assert.Contains(Path.Combine("vorgaenge", "Mandant-4", a.CreatedAt.ToString("yyyy"), a.CreatedAt.ToString("MM"), a.Id), dirA);
        Assert.Contains(Path.Combine("vorgaenge", "Allgemein"), store.Directory_(b.Id)!);
        Assert.Equal((2L, 0L), store.CountOutsideRoot());

        // neuer Belegordner: neue Vorgänge landen dort, vorhandene werden verschoben
        so.DocumentFolder = Path.Combine(_dir, "belege-neu");
        Assert.Equal((2L, 2L), store.CountOutsideRoot());
        var c = proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Eingang", "c.pdf");
        Assert.StartsWith(so.DocumentFolder, store.Directory_(c.Id)!);
        Assert.Equal(DocumentStore.RelocateResult.Moved, store.Relocate(a.Id, out var left));
        Assert.Null(left);
        Assert.StartsWith(Path.Combine(so.DocumentFolder, "Mandant-4"), store.Directory_(a.Id)!);
        Assert.True(File.Exists(store.FilePath(a.Id, DocumentStore.PdfFile)));
        Assert.False(Directory.Exists(dirA));
        Assert.Equal(DocumentStore.RelocateResult.Unchanged, store.Relocate(a.Id, out _));

        // Mandant geändert → Ordner gehört woanders hin
        string? changed = null;
        store.MandantChanged += id => changed = id;
        store.Update(b.Id, v => { v.Mandant = "7"; return true; });
        Assert.Equal(b.Id, changed);
        Assert.Equal(DocumentStore.RelocateResult.Moved, store.Relocate(b.Id, out _));
        Assert.Contains(Path.Combine("belege-neu", "Mandant-7"), store.Directory_(b.Id)!);
        Assert.Equal((3L, 0L), store.CountOutsideRoot());
    }

    [Fact]
    public void Vorgangsliste_blaettert()
    {
        var (store, proc, _, _) = Create();
        for (var i = 0; i < 5; i++) proc.Process(TestData.Pdf(new[] { "@@toa@kunde.at@@" }), "Eingang", $"{i}.pdf");
        var f = new VorgangFilter { SeeAll = true, Limit = 2 };
        Assert.Equal(5, store.Count(f));
        var p1 = store.Query(f); var p3 = store.Query(f with { Offset = 4 });
        Assert.Equal(2, p1.Count); Assert.Single(p3);
        Assert.DoesNotContain(p3[0].Id, p1.Select(x => x.Id));
    }

    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type t) => null; }

    [Fact]
    public void MAPI_Einstellung_Geraet_vor_Benutzer_vor_Standard_und_Ziel_PC()
    {
        var (_, _, users, _) = Create();
        var (d1, _) = users.AddDevice("martin", "Büro");
        var (d2, _) = users.AddDevice("martin", "Laptop");
        users.TouchDevice(d1.Id, "PC-BUERO");
        users.TouchDevice(d2.Id, "NB-MARTIN");
        Assert.Equal(d2.Id, users.TargetDevice("martin", "nb-martin")!.Id);
        Assert.Equal(d1.Id, users.TargetDevice("martin", "PC-BUERO")!.Id);

        Assert.Equal(MapiMode.Outlook, users.EffectiveMapiMode("martin", d1.Id, MapiMode.Outlook));
        users.Update(x => x.Users.First(u => u.Id == "martin").MapiMode = MapiMode.Portal);
        Assert.Equal(MapiMode.Portal, users.EffectiveMapiMode("martin", d1.Id, MapiMode.Outlook));
        users.SetDeviceMapi(d1.Id, MapiMode.Direct, "admin", "Admin");         // Admin überrollt
        Assert.Equal(MapiMode.Direct, users.EffectiveMapiMode("martin", d1.Id, MapiMode.Outlook));
        Assert.Equal("admin", users.Device(d1.Id)!.MapiSource);
        users.SetDeviceMapi(d1.Id, MapiMode.Outlook, "benutzer", "Martin");    // Benutzer ändert danach wieder
        Assert.Equal(MapiMode.Outlook, users.EffectiveMapiMode("martin", d1.Id, MapiMode.Portal));
        Assert.Equal("benutzer", users.Device(d1.Id)!.MapiSource);
        Assert.Equal(MapiMode.Portal, users.EffectiveMapiMode("martin", d2.Id, MapiMode.Outlook));
    }

    [Fact]
    public async Task MAPI_Mail_wird_Vorgang_und_Outlook_Auftrag_fuer_den_richtigen_PC()
    {
        var (store, _, users, settings) = Create();
        var s = DefaultSettings.Create();
        var hub = new EventHub();
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, events: hub);
        var send = new SendService(store, settings, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<StorageOptions>(s.Storage),
            new StaticOptions<MailTemplateOptions>(s.MailTemplates), NullLogger<SendService>.Instance);
        var tasks = new MelderTasks(store.Db, hub);
        var (pc, _) = users.AddDevice("martin", "Büro");
        users.TouchDevice(pc.Id, "PC-BUERO");
        var (other, _) = users.AddDevice("martin", "Laptop");
        var intake = new MapiIntake(proc, store, users, tasks, hub, send, new StaticOptions<MapiOptions>(new MapiOptions()), NullLogger<MapiIntake>.Instance);

        var msg = new MapiMessage
        {
            Subject = "Rechnung R-77", Body = "Sehr geehrte Damen und Herren,\r\nanbei die Rechnung.\r\n@@pi0NR41261010@@",
            ClientName = "PC-BUERO",
            Recipients = { new() { Type = "to", Address = "SMTP:kunde@firma.at" }, new() { Type = "cc", Address = "chef@firma.at" } },
            Attachments =
            {
                new() { Name = "R-77.pdf", Data = Convert.ToBase64String(TestData.Pdf(new[] { "@@xiVOUCHER_ID=R-77@@" })) },
                new() { Name = "AGB.txt", Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("AGB")) }
            }
        };
        var r = await intake.HandleAsync(msg, "FIRMA\\mmuster", default);
        Assert.True(r.Ok);
        Assert.Equal(MapiMode.Outlook, r.Mode);
        var v = store.Get(r.VorgangId!)!;
        Assert.Equal("martin", v.OwnerUserId);
        Assert.Equal("MAPI", v.Source);
        Assert.Equal(new[] { "kunde@firma.at" }, v.Mail.To);
        Assert.Equal(new[] { "chef@firma.at" }, v.Mail.Cc);
        Assert.Equal("Rechnung R-77", v.Mail.Subject);
        Assert.DoesNotContain("@@", v.Mail.Body);
        Assert.Equal("0NR41261010", v.ErpIndex);
        Assert.Single(v.Mail.Attachments);
        Assert.True(File.Exists(v.Mail.Attachments[0]));

        // Auftrag geht an den PC mit passendem RDP-Client-Namen – der andere PC sieht ihn nicht
        Assert.Single(tasks.Pending("martin", pc.Id));
        Assert.Empty(tasks.Pending("martin", other.Id));
        var t = tasks.Pending("martin", pc.Id)[0];
        Assert.NotNull(tasks.Claim(t.Id, "martin", pc.Id));
        Assert.Null(tasks.Claim(t.Id, "martin", pc.Id)); // nur einmal
        tasks.Complete(t.Id, "martin");
        Assert.Empty(tasks.Pending("martin", pc.Id));

        // Einstellung „Portal“ für diesen PC → kein Outlook-Auftrag
        users.SetDeviceMapi(pc.Id, MapiMode.Portal, "benutzer", "Martin");
        var r2 = await intake.HandleAsync(msg, "FIRMA\\mmuster", default);
        Assert.Equal(MapiMode.Portal, r2.Mode);
        Assert.Empty(tasks.Pending("martin", pc.Id));
    }

    [Fact]
    public async Task MAPI_Beleg_Regel_auf_MAPI_Betreff_uebersteuert_Outlook()
    {
        var (store, _, users, settings) = Create();
        var s = DefaultSettings.Create();
        s.MailTemplates.Items.Add(new MailDesign { Name = "MZ" });
        var hub = new EventHub();
        var global = new MailRuleOptions { Rules = { new MailRule { Name = "Rechnungsversand", TextContains = "Rechnung*", Template = "MZ", FromAddress = "office@erpaustria.com", Route = "direkt" } } };
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, events: hub,
            templates: new StaticOptions<MailTemplateOptions>(s.MailTemplates), rules: new StaticOptions<MailRuleOptions>(global), ruleStore: new MailRuleStore(settings));
        var send = new SendService(store, settings, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<StorageOptions>(s.Storage),
            new StaticOptions<MailTemplateOptions>(s.MailTemplates), NullLogger<SendService>.Instance);
        var tasks = new MelderTasks(store.Db, hub);
        var (pc, _) = users.AddDevice("martin", "Büro");
        users.TouchDevice(pc.Id, "PC-BUERO");
        var intake = new MapiIntake(proc, store, users, tasks, hub, send, new StaticOptions<MapiOptions>(new MapiOptions()), NullLogger<MapiIntake>.Instance);

        // Betreff nur über MAPI – im PDF steht kein @@ed@@
        var msg = new MapiMessage
        {
            Subject = "Rechnung R-77", Body = "anbei die Rechnung", ClientName = "PC-BUERO",
            Recipients = { new() { Type = "to", Address = "SMTP:kunde@firma.at" } },
            Attachments = { new() { Name = "R-77.pdf", Data = Convert.ToBase64String(TestData.Pdf(new[] { "@@mf R-77@@" })) } }
        };
        var r = await intake.HandleAsync(msg, "FIRMA\\mmuster", default);
        Assert.Equal(MapiMode.Direct, r.Mode);                 // nicht Outlook
        Assert.Empty(tasks.Pending("martin", pc.Id));
        var v = store.Get(r.VorgangId!)!;
        Assert.Equal("MZ", v.Mail.Template);
        Assert.Equal("office@erpaustria.com", v.Mail.FromAddress);
        Assert.Equal(MailRoute.Direct, v.Mail.Route);
        Assert.Contains("Rechnungsversand", v.Mail.RouteReason);

        // ohne passenden Betreff gilt wieder die MAPI-Einstellung (Outlook)
        msg.Subject = "Angebot A-1";
        var r2 = await intake.HandleAsync(msg, "FIRMA\\mmuster", default);
        Assert.Equal(MapiMode.Outlook, r2.Mode);
    }

    [Fact]
    public async Task Normale_MAPI_Mail_ohne_Steuerzeichen_geht_ungeprueft_nach_Outlook()
    {
        var (store, _, users, settings) = Create();
        var s = DefaultSettings.Create();
        s.Mail.AutoSend = true; s.Mail.AutoSendWithWarnings = true;
        var hub = new EventHub();
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users, events: hub);
        var send = new SendService(store, settings, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<StorageOptions>(s.Storage),
            new StaticOptions<MailTemplateOptions>(s.MailTemplates), NullLogger<SendService>.Instance);
        var tasks = new MelderTasks(store.Db, hub);
        var (mac, _) = users.AddDevice("martin", "Mac");
        users.TouchDevice(mac.Id, "Martins-MacBook-Pro");
        var intake = new MapiIntake(proc, store, users, tasks, hub, send, new StaticOptions<MapiOptions>(new MapiOptions()), NullLogger<MapiIntake>.Instance);

        // WordPad „Senden“: nur ein RTF-Anhang, kein Empfänger, kein Text – dazu ein PDF ohne Steuerzeichen
        var msg = new MapiMessage
        {
            Subject = "Dokument.rtf", ClientName = "MARTINS-MACBOO", // RDP kürzt auf 15 Zeichen
            Attachments =
            {
                new() { Name = "Dokument.rtf", Data = Convert.ToBase64String(Encoding.ASCII.GetBytes(@"{\rtf1 Hallo}")) },
                new() { Name = "Info.pdf", Data = Convert.ToBase64String(TestData.Pdf(Array.Empty<string>())) }
            }
        };
        Assert.False(MapiIntake.IsVoucher(msg, msg.Attachments.Select(a => (a.Name, Convert.FromBase64String(a.Data))).ToList()));
        var r = await intake.HandleAsync(msg, "FIRMA\\mmuster", default);
        Assert.True(r.Ok);
        Assert.Equal(MapiMode.Outlook, r.Mode);
        Assert.Null(r.Hint);
        var v = store.Get(r.VorgangId!)!;
        Assert.True(v.Mail.Plain);
        Assert.True(v.Mail.NoDocument);
        Assert.Equal("E-Mail", v.DocumentType);
        Assert.Equal(VorgangStatus.Outlook, v.Status);
        Assert.Empty(v.Messages); // keine Prüfung (kein Empfänger ist hier kein Fehler)
        Assert.Equal(2, v.Mail.Attachments.Count); // auch das PDF bleibt ein normaler Anhang
        Assert.Single(tasks.Pending("martin", mac.Id));

        // Mail für Outlook: nur Text, keine CI-Vorlage, kein Logo, Anhänge wie übergeben
        var (mail, err) = await send.BuildMail(v, default);
        Assert.Null(err);
        Assert.Null(mail!.HtmlBody);
        Assert.Empty(mail.Images);
        Assert.Equal(new[] { "Dokument.rtf", "Info.pdf" }, mail.Attachments.Select(a => a.Name));

        // „Neu prüfen“ ändert nichts
        Assert.Equal(VorgangStatus.Outlook, proc.Recheck(v.Id, "Test")!.Status);

        // ohne gekoppelten Melder: Portal mit Hinweis, nie Autoversand
        var r2 = await intake.HandleAsync(new MapiMessage { Subject = "Hallo", Recipients = { new() { Type = "to", Address = "a@b.at" } } }, "FIRMA\\unbekannt", default);
        Assert.Equal(MapiMode.Portal, r2.Mode);
        Assert.Contains("keinem Belegpost-Benutzer", r2.Hint);
        Assert.True(store.Get(r2.VorgangId!)!.Mail.Plain);
    }

    [Fact]
    public void Leere_Platzhalterseite_ist_ein_lesbares_PDF()
    {
        var (pages, _) = PdfText.Extract(MapiIntake.BlankPdf);
        Assert.Equal(1, pages);
    }

    [Theory]
    [InlineData("Martins-MacBook-Pro", "MARTINS-MACBOO", true)]
    [InlineData("Martins-MacBook-Pro.local", "martins-macbook-pro", true)]
    [InlineData("WORKSTATION-BUERO", "WORKSTATION-BU", true)] // Präfix ab 8 Zeichen
    [InlineData("PC-1", "PC-12", false)]           // zu kurz für Präfix
    [InlineData("LAPTOP-EVA", "PC-BUERO", false)]
    public void Computername_passt_auch_gekuerzt(string device, string client, bool expected) =>
        Assert.Equal(expected, UserStore.SameComputer(device, client));

    [Fact]
    public void Outlook_Bestaetigung_setzt_110_und_ohne_Bestaetigung_830()
    {
        var (store, proc, _, _) = Create();
        var v = proc.Process(TestData.Pdf(new[] { "@@pb007@@", "@@toa@kunde.at@@" }), "Eingang", "o.pdf");
        store.Update(v.Id, x => { x.Status = VorgangStatus.Outlook; x.OutlookHandedAt = DateTimeOffset.Now.AddDays(-2); return true; });
        Assert.Equal(StatusCode.AnOutlook, store.Get(v.Id)!.StatusCode);
        var watch = new OutlookWatchWorker(new MelderTasks(store.Db, new EventHub()), store, new EventHub(),
            new StaticOptions<MapiOptions>(new MapiOptions { OutlookConfirmHours = 24 }), NullLogger<OutlookWatchWorker>.Instance);
        watch.RunOnce();
        Assert.Equal(StatusCode.OutlookUnbestaetigt, store.Get(v.Id)!.StatusCode);
        // spätere Bestätigung
        store.Update(v.Id, x => { x.SendChannel = "Outlook"; x.OutlookConfirmedAt = DateTimeOffset.Now; x.SentAt = DateTimeOffset.Now; x.Status = VorgangStatus.Sent; return true; });
        Assert.Equal(StatusCode.VersendetOutlook, store.Get(v.Id)!.StatusCode);
    }
}

public class ErpIndexFormatTests
{
    [Theory]
    [InlineData("0NR41261010", "0NR41261010")]
    [InlineData(" aAR00000001 ", "aAR00000001")]
    [InlineData("ZNL12345678", "ZNL12345678")]
    [InlineData("12345", null)]
    [InlineData("0XR41261010", null)]      // Archiv nur A oder N
    [InlineData("0NR4126101", null)]       // zu kurz
    [InlineData("0NR412610100", null)]     // zu lang
    [InlineData("0NR4126101A", null)]      // Belegnummer nur Ziffern
    [InlineData("0nR41261010", null)]      // Archiv groß
    public void Belegindex_11_Zeichen(string input, string? expected)
        => Assert.Equal(expected, PdfRechnung.Documents.ControlCodeParser.NormalizeErpIndex(input));
}

public class StatusBitmapTests
{
    [Fact]
    public void Statusbitmaps_ab_950_und_uebersteuerbar()
    {
        Assert.Equal("950", StatusCode.DefaultBitmap(StatusCode.Erfasst));
        Assert.Equal("964", StatusCode.DefaultBitmap(StatusCode.Verworfen));
        Assert.Equal("bw000955.png", StatusCode.BitmapFile(StatusCode.DefaultBitmap(StatusCode.VersendetOutlook)));
        var o = new PdfRechnung.ErpOptions { Codes = { new PdfRechnung.ErpStatusCode { Code = StatusCode.Versendet, Bitmap = "1234" } } };
        Assert.Equal("1234", ErpFieldBuilder.Bitmap(o, StatusCode.Versendet));
        Assert.Equal("955", ErpFieldBuilder.Bitmap(o, StatusCode.VersendetOutlook));
        var m = new PdfRechnung.ErpFieldMapping { Field = "BEL_9_6", Value = "statusbitmap" };
        Assert.Equal("1234", ErpFieldBuilder.Value(o, m, new ErpValueContext(new Vorgang(), StatusCode.Versendet, DateTimeOffset.Now, null, null)));
    }
}
