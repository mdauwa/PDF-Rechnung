using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung;
using PdfRechnung.Documents;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class MailRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "belegpost-regeln-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static MailRuleContext Ctx(string? type = "Angebot", string? stationery = "Firma A", string to = "max@kunde.at", string? user = "martin") =>
        new(type, stationery, "office@firma.at", new[] { to }, "4711", "Kunde GmbH", "Drucker", null, "Angebot_N11260051", user);

    [Fact]
    public void Eigene_Regel_vor_globaler_und_erste_gewinnt_je_Aktion()
    {
        var global = new MailRuleSet { Rules =
        {
            new MailRule { Name = "Angebote", DocumentTypes = { "Angebot" }, Template = "Vertrieb", FromAddress = "vertrieb@firma.at", Route = "freigabe" },
            new MailRule { Name = "Alle", Bcc = { "archiv@firma.at" }, Template = "Standard" }
        } };
        var user = new MailRuleSet { Rules = { new MailRule { Name = "Meine Kunden", Recipients = { "*@kunde.at" }, FromAddress = "m.dauwa@firma.at" } } };
        var r = MailRuleEngine.Evaluate(Ctx(), user, global);
        Assert.Equal("m.dauwa@firma.at", r.FromAddress!.Text);
        Assert.Contains("eigene Regel", r.FromAddress.Reason);
        Assert.Equal("Vertrieb", r.Template!.Text);
        Assert.Equal(MailRoute.Approval, r.Route!.Text);
        Assert.Equal(new[] { "archiv@firma.at" }, r.Bcc);
    }

    [Fact]
    public void Bedingungen_Platzhalter_Benutzer_und_Stopp()
    {
        var global = new MailRuleSet { Rules =
        {
            new MailRule { Name = "Nur Tanja", Users = { "tanja" }, Template = "T" },
            new MailRule { Name = "Rechnungen", DocumentTypes = { "Rechnung" }, Template = "R" },
            new MailRule { Name = "Stopp", Stationeries = { "firma *" }, Stop = true },
            new MailRule { Name = "Danach", Template = "nie" }
        } };
        var r = MailRuleEngine.Evaluate(Ctx(), null, global);
        Assert.Null(r.Template);
        Assert.Contains("Regel „Stopp“", r.Applied);
    }

    [Fact]
    public void Signatur_Steuerzeichen_vor_Regel_vor_Benutzer_vor_global()
    {
        var global = new MailRuleSet { Signatures = { new SignatureDef { Name = "Firma", Text = "Firma", Default = true }, new SignatureDef { Name = "B", Text = "B", Stationeries = { "Firma B" } } } };
        var user = new MailRuleSet { Signatures =
        {
            new SignatureDef { Name = "Martin A", Text = "Martin – Firma A", Stationeries = { "Firma A" } },
            new SignatureDef { Name = "Martin Service", Text = "Martin – Service", Senders = { "service@*" } },
            new SignatureDef { Name = "Martin", Text = "Martin", Default = true }
        } };
        Assert.Equal("Martin A", MailRuleEngine.SelectSignature(null, null, "Firma A", "x@firma.at", user, global).Signature!.Name);
        Assert.Equal("Martin Service", MailRuleEngine.SelectSignature(null, null, "Firma C", "service@firma.at", user, global).Signature!.Name);
        Assert.Equal("Martin", MailRuleEngine.SelectSignature(null, null, "Firma B", "x@firma.at", user, global).Signature!.Name);
        Assert.Equal("Firma", MailRuleEngine.SelectSignature(null, null, null, null, null, global).Signature!.Name);
        Assert.Equal("B", MailRuleEngine.SelectSignature("b", "Regel", "Firma A", null, user, global).Signature!.Name);
        var (fallback, _, warn) = MailRuleEngine.SelectSignature("gibtsnicht", "@@ps@@", "Firma A", null, user, global);
        Assert.Equal("Martin A", fallback!.Name);
        Assert.NotNull(warn);
    }

    private (DocumentProcessor Proc, MailRuleStore Store) Processor(MailRuleOptions global)
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var users = new UserStore(_dir);
        users.Replace(new List<PortalUser> { new() { Id = "martin", Name = "Martin", OperatorNumbers = { "007" } } });
        var s = DefaultSettings.Create();
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var rules = new MailRuleStore(settings);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, users: users,
            rules: new StaticOptions<MailRuleOptions>(global), ruleStore: rules);
        return (proc, rules);
    }

    [Fact]
    public void Steuerzeichen_uebersteuern_Regeln()
    {
        var global = new MailRuleOptions
        {
            Signatures = { new SignatureDef { Name = "Global", Text = "Grüße {Absender}", Default = true }, new SignatureDef { Name = "Kurz", Text = "LG" } },
            Rules = { new MailRule { Name = "Alle", FromAddress = "regel@firma.at", Cc = { "cc@firma.at" }, Route = "freigabe", Signature = "Global" } }
        };
        var (proc, store) = Processor(global);
        store.Save("martin", new MailRuleSet { Rules = { new MailRule { Name = "Ich", Route = "keine" } } });

        var a = proc.Process(TestData.Pdf(new[] { "@@pb007@@", "@@aakunde@x.at@@" }), "Drucker", "a.pdf");
        Assert.Equal("regel@firma.at", a.Mail.FromAddress);
        Assert.Equal(new[] { "cc@firma.at" }, a.Mail.Cc);
        Assert.Equal(MailRoute.None, a.Mail.Route);            // eigene Regel vor globaler
        Assert.False(a.Mail.Enabled);
        Assert.Equal("Global", a.Mail.SignatureName);

        var b = proc.Process(TestData.Pdf(new[] { "@@pb007@@", "@@aakunde@x.at@@", "@@pfich@firma.at@@", "@@ccandere@firma.at@@", "@@pwdirekt@@", "@@psKurz@@" }), "Drucker", "b.pdf");
        Assert.Equal("ich@firma.at", b.Mail.FromAddress);
        Assert.Equal(new[] { "andere@firma.at" }, b.Mail.Cc);
        Assert.Equal(MailRoute.Direct, b.Mail.Route);
        Assert.True(b.Mail.Enabled);
        Assert.Equal("Kurz", b.Mail.SignatureName);

        var c = proc.Process(TestData.Pdf(new[] { "@@aakunde@x.at@@", "@@ps@@" }), "Drucker", "c.pdf");
        Assert.Equal("", c.Mail.SignatureText); // keine Signatur
        Assert.Equal(MailRoute.Approval, c.Mail.Route); // ohne Benutzer nur globale Regel
    }

    [Fact]
    public void Signatur_landet_in_der_Mail()
    {
        var r = MailDesigner.Render(null, "Hallo", new Dictionary<string, string> { ["Absender"] = "Firma" }, null, null, "Grüße\n{Absender}");
        Assert.Contains("Grüße", r.Text);
        Assert.Contains("Firma", r.Text);
        var d = new MailDesign { Name = "x", Signature = "Vorlage" };
        Assert.Contains("Eigene", MailDesigner.Render(d, "Hallo", new Dictionary<string, string>(), null, null, "Eigene").Text);
        Assert.Contains("Vorlage", MailDesigner.Render(d, "Hallo", new Dictionary<string, string>(), null, null, null).Text);
    }

    [Fact]
    public void Validierung()
    {
        var e = MailRuleStore.Validate(new MailRuleSet
        {
            Signatures = { new SignatureDef { Name = "A", Default = true }, new SignatureDef { Name = "a", Default = true } },
            Rules = { new MailRule { Name = "", Route = "irgendwo", Cc = { "kein-mail" }, Users = { "x" } } }
        }, global: false);
        Assert.Equal(6, e.Count);
    }
}
