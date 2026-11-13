using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Documents;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class TextFixerTests
{
    [Theory]
    [InlineData("Sehr geehrter Herr MÃ¼ller", "Sehr geehrter Herr Müller")]
    [InlineData("GrÃ¶ÃŸe 10 â‚¬", "Größe 10 €")]
    [InlineData("Ã–sterreich â€“ StraÃŸe", "Österreich – Straße")]
    [InlineData("Mit freundlichen Gr��en", "Mit freundlichen Grüßen")]
    [InlineData("Zahlungsﬁrma­name", "Zahlungsfirmaname")]
    public void Kaputte_Umlaute_werden_korrigiert(string input, string expected)
    {
        var (text, changes) = TextFixer.Fix(input);
        Assert.Equal(expected, text);
        Assert.NotEmpty(changes);
    }

    [Theory]
    [InlineData("Sehr geehrte Frau Müller, Größe 5 €")]
    [InlineData("Ärger über Öl – „Zitat“")]
    [InlineData("Zeile 1\nZeile 2\tTab")]
    public void Korrekte_Texte_bleiben_unveraendert(string input)
    {
        var (text, changes) = TextFixer.Fix(input);
        Assert.Equal(input, text);
        Assert.Empty(changes);
    }

    [Fact]
    public void Zerlegte_Umlaute_werden_zusammengesetzt()
    {
        var (text, _) = TextFixer.Fix("Müller");
        Assert.Equal("Müller", text);
    }

    [Fact]
    public void Textdatei_in_ANSI_und_UTF8_wird_richtig_gelesen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfr-txt-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var ansi = Path.Combine(dir, "ansi.txt"); File.WriteAllBytes(ansi, TextFixer.Windows1252.GetBytes("Grüße aus Österreich"));
            var utf8 = Path.Combine(dir, "utf8.txt"); File.WriteAllText(utf8, "Grüße aus Österreich", new UTF8Encoding(false));
            var bom = Path.Combine(dir, "bom.txt"); File.WriteAllText(bom, "Grüße aus Österreich", new UTF8Encoding(true));
            Assert.Equal("Grüße aus Österreich", TextFixer.ReadTextFile(ansi));
            Assert.Equal("Grüße aus Österreich", TextFixer.ReadTextFile(utf8));
            Assert.Equal("Grüße aus Österreich", TextFixer.ReadTextFile(bom));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public sealed class PlaceholderTests
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Belegnummer"] = "R-1", ["Anrede"] = "Sehr geehrter Herr Maier", ["Leer"] = ""
    };

    [Fact]
    public void Werte_Ersatztext_und_fehlende_Platzhalter()
    {
        var missing = new List<string>();
        var r = Placeholders.Apply("{anrede}, Rechnung {Belegnummer}. {Gruss|Mit freundlichen Grüßen} {Unbekannt}{Leer}", Values, missing);
        Assert.Equal("Sehr geehrter Herr Maier, Rechnung R-1. Mit freundlichen Grüßen ", r);
        Assert.Equal(new[] { "Unbekannt" }, missing);
    }

    [Fact]
    public void Ersatztext_greift_auch_bei_leerem_Wert()
    {
        Assert.Equal("Hallo", Placeholders.Apply("{Leer|Hallo}", Values));
    }

    [Fact]
    public void Geschweifte_Klammern_ohne_Namen_bleiben_stehen()
    {
        Assert.Equal("a { b } {}", Placeholders.Apply("a { b } {}", Values));
    }
}

public sealed class MailDesignerTests
{
    private static MailTemplateOptions Options() => new()
    {
        Default = "Standard",
        Items =
        {
            new MailDesign { Name = "Standard" },
            new MailDesign { Name = "Firma B", Stationeries = { "FirmaB" } },
            new MailDesign { Name = "Service", Senders = { "service@firma.at" } }
        }
    };

    [Theory]
    [InlineData("service", "FirmaB", "x@firma.at", "Service")] // @@pt@@ hat Vorrang
    [InlineData(null, "firmab", "service@firma.at", "Firma B")] // Briefpapier vor Absender
    [InlineData(null, null, "Service@Firma.at", "Service")]
    [InlineData(null, "Anderes", "x@firma.at", "Standard")]
    public void Vorlage_wird_in_der_richtigen_Reihenfolge_gewaehlt(string? pt, string? bp, string? sender, string expected)
    {
        var (d, reason, warn) = MailDesigner.Select(Options(), pt, bp, sender);
        Assert.Equal(expected, d!.Name);
        Assert.NotNull(reason);
        Assert.Null(warn);
    }

    [Fact]
    public void Unbekannte_Vorlage_meldet_Warnung_und_nimmt_Ersatz()
    {
        var (d, _, warn) = MailDesigner.Select(Options(), "Gibtsnicht", null, null);
        Assert.Equal("Standard", d!.Name);
        Assert.Contains("Gibtsnicht", warn);
    }

    [Fact]
    public void HTML_Mail_mit_Logo_Farben_und_sicherem_Text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfr-logo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "logo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
            var d = new MailDesign
            {
                Name = "CI", LogoFile = "logo.png", PrimaryColor = "#C8102E", HeaderText = "Ihre {Belegart}",
                Signature = "Mit freundlichen Grüßen\n**{Absender}**", Footer = "Firma GmbH · www.firma.at"
            };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Belegart"] = "Rechnung", ["Absender"] = "Firma GmbH" };
            var r = MailDesigner.Render(d, "Hallo <script>alert(1)</script>\n\n- Punkt 1\n- Punkt 2\nFragen: info@firma.at", values, dir,
                new MailFacts("Rechnung", "R-1", "01.11.2026", "120,00 EUR", "R-1.pdf"));

            Assert.Contains("cid:logo@belegpost", r.Html);
            Assert.Single(r.Images);
            Assert.Equal("image/png", r.Images[0].ContentType);
            Assert.Contains("#C8102E", r.Html);
            Assert.Contains("Ihre Rechnung", r.Html);
            Assert.Contains("&lt;script&gt;", r.Html);
            Assert.DoesNotContain("<script>", r.Html);
            Assert.Contains("<li>Punkt 1</li>", r.Html);
            Assert.Contains("href=\"mailto:info@firma.at\"", r.Html);
            Assert.Contains("<strong>Firma GmbH</strong>", r.Html);
            Assert.Contains("120,00 EUR", r.Html);
            Assert.Contains("https://www.firma.at", r.Html);
            Assert.Contains("Hallo <script>", r.Text); // Textfassung bleibt Text
            Assert.Contains("data:image/png;base64,", MailPreview.Inline(r));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Eigenes_HTML_bekommt_Inhalt_und_maskierte_Werte()
    {
        var d = new MailDesign { Name = "X", Kind = "html", CustomHtml = "<div>{Inhalt}</div><p>{Kunde}</p>" };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Kunde"] = "A & B <GmbH>" };
        var r = MailDesigner.Render(d, "Text", values, null);
        Assert.Equal("<div><p style=\"margin:0 0 14px 0;\">Text</p></div><p>A &amp; B &lt;GmbH&gt;</p>", r.Html);
    }
}

public sealed class MailTemplateProcessorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-mt-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Platzhalter_aus_Steuerzeichen_Vorlage_Absender_und_Korrektur()
    {
        var settings = new SettingsStore(_dir);
        var s = DefaultSettings.Create();
        s.Mail.Enabled = true;
        s.Mail.FromAddress = "rechnung@firma.at";
        s.MailTemplates.Items.Add(new MailDesign
        {
            Name = "Service", FromName = "Firma Service",
            Subject = "{Belegart} {Belegnummer} für {Kunde}",
            Body = "{Anrede|Sehr geehrte Damen und Herren},\n\nanbei {Belegart} {Belegnummer}.\n{Zusatz}"
        });
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice),
            NullLogger<DocumentProcessor>.Instance, settings: settings, templates: new StaticOptions<MailTemplateOptions>(s.MailTemplates));

        // Mailtext aus Datei (@@mb@@), die das ERP als UTF-8 mit „doppelt kodierten“ Umlauten geschrieben hat
        Directory.CreateDirectory(_dir);
        var bodyFile = Path.Combine(_dir, "text.txt");
        File.WriteAllText(bodyFile, "{Anrede|Sehr geehrte Damen und Herren},\r\n\r\nanbei {Belegart} {Belegnummer} – GrÃ¼ÃŸe\r\n{Zusatz}", new UTF8Encoding(false));
        var codes = new[]
        {
            "@@mb" + bodyFile + "@@",
            "@@xiVOUCHER_ID=R-77@@", "@@xiBUYER_NAME=Kunde KG@@", "@@xiDOCUMENT_TYPE=380@@",
            "@@toeinkauf@kunde.at@@", "@@ptService@@", "@@pfservice@firma.at@@",
            "@@pvAnrede=Sehr geehrter Herr Maier@@"
        };
        var v = proc.Process(TestData.Pdf(codes), "Upload", "t.pdf");

        Assert.Equal("Service", v.Mail.Template);
        Assert.Equal("service@firma.at", v.Mail.FromAddress);
        Assert.Equal("Firma Service", v.Mail.FromName);
        Assert.Equal("Rechnung R-77 für Kunde KG", v.Mail.Subject);
        Assert.StartsWith("Sehr geehrter Herr Maier,\n\nanbei Rechnung R-77 – Grüße", v.Mail.Body);
        Assert.Equal(new[] { "Zusatz" }, v.Mail.MissingPlaceholders);
        Assert.Contains(v.Messages, m => m.Level == DocumentChecker.Warning && m.Text.Contains("{Zusatz}"));
        Assert.Contains(v.Messages, m => m.Level == DocumentChecker.Info && m.Text.Contains("Text automatisch korrigiert"));

        var r = SendService.Render(v, s.MailTemplates, settings.DataDirectory);
        Assert.Contains("Grüße", r.Html);
        Assert.Contains("R-77", r.Html);
    }
}

[Collection("Graph")]
public sealed class GraphHtmlTests : IDisposable
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            Calls.Add((url, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            if (url.Contains("/token"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"t","expires_in":3600}""", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        }
    }

    private readonly HttpClient _old = GraphMailSender.Http;
    private readonly Capture _cap = new();
    public GraphHtmlTests() => GraphMailSender.Http = new HttpClient(_cap);
    public void Dispose() => GraphMailSender.Http = _old;

    [Fact]
    public async Task HTML_Mail_mit_Inline_Logo_und_Absender_aus_der_Vorlage()
    {
        var o = new MailOptions { Enabled = true, Transport = "Graph", GraphTenantId = "t-" + Guid.NewGuid().ToString("N")[..6], GraphClientId = "c", FromAddress = "rechnung@firma.at" };
        var m = new OutgoingMail { Subject = "S", Body = "Text", HtmlBody = "<p>HTML</p>", FromAddress = "service@firma.at", FromName = "Service" };
        m.To.Add("kunde@example.at");
        m.Images.Add(new InlineImage("logo@belegpost", "logo.png", new byte[] { 1, 2, 3 }, "image/png"));
        await MailSender.SendAsync(o, new MailSecrets(null, "x"), m, CancellationToken.None);

        var send = _cap.Calls.Single(c => c.Url.EndsWith("/sendMail"));
        Assert.Contains("/users/service%40firma.at/sendMail", send.Url);
        var msg = JsonNode.Parse(send.Body)!["message"]!;
        Assert.Equal("HTML", msg["body"]!["contentType"]!.GetValue<string>());
        Assert.Equal("Service", msg["from"]!["emailAddress"]!["name"]!.GetValue<string>());
        var logo = msg["attachments"]![0]!;
        Assert.True(logo["isInline"]!.GetValue<bool>());
        Assert.Equal("logo@belegpost", logo["contentId"]!.GetValue<string>());
    }
}
