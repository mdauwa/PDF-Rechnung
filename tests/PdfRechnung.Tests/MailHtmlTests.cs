using PdfRechnung;
using PdfRechnung.Documents;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class MailHtmlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "belegpost-html-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // Typische Outlook-Zwischenablage: ganzes Dokument, Office-Kommentare, Klassen, mso-Stile, font, o:p, lokales Bild
    private const string OutlookPaste = """
        <html xmlns:o="urn:schemas-microsoft-com:office:office"><head><meta charset="utf-8"><style>p.MsoNormal{margin:0}</style>
        <!--[if gte mso 9]><xml><o:OfficeDocumentSettings></o:OfficeDocumentSettings></xml><![endif]--></head>
        <body lang="DE-AT"><div class="WordSection1">
        <p class="MsoNormal" style="mso-margin-top-alt:auto"><span style="font-family:Calibri;color:#1F3864;mso-fareast-language:DE">Mit freundlichen Grüßen<o:p></o:p></span></p>
        <p class="MsoNormal"><b><font color="#C00000" face="Arial">Tanja Lutz</font></b><o:p></o:p></p>
        <p class="MsoNormal"><a href="https://www.erpaustria.com" onclick="alert(1)">www.erpaustria.com</a></p>
        <p class="MsoNormal"><img src="file:///C:/Users/x/AppData/Local/Temp/clip_image001.png" width="120"></p>
        <script>alert(1)</script></div></body></html>
        """;

    [Fact]
    public void Outlook_Einfuegen_wird_bereinigt()
    {
        var h = MailHtml.Sanitize(OutlookPaste);
        Assert.DoesNotContain("<html", h); Assert.DoesNotContain("<head", h); Assert.DoesNotContain("<body", h);
        Assert.DoesNotContain("<style", h); Assert.DoesNotContain("script", h); Assert.DoesNotContain("onclick", h);
        Assert.DoesNotContain("mso-", h); Assert.DoesNotContain("class=", h); Assert.DoesNotContain("o:p", h);
        Assert.DoesNotContain("<font", h); Assert.DoesNotContain("file:///", h); Assert.DoesNotContain("<img", h); Assert.DoesNotContain("alert", h); Assert.DoesNotContain("rgba", h);
        Assert.Contains("Mit freundlichen Grüßen", h);
        Assert.Contains("color:#c00000", h);              // font → span mit Farbe
        Assert.Contains("font-family:Calibri", h);
        Assert.Contains("href=\"https://www.erpaustria.com\"", h);
    }

    [Fact]
    public void Bilder_werden_abgelegt_und_eingebettet()
    {
        var png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 });
        var stored = MailHtml.ForStorage($"<p>Logo <img src=\"data:image/png;base64,{png}\" alt=\"Logo\"></p>", _dir);
        Assert.Matches(@"src=""api/mailbilder/[0-9a-f]{20}\.png""", stored);

        var images = new List<InlineImage>();
        var html = MailHtml.EmbedImages(MailHtml.ToMailHtml(stored, new Dictionary<string, string>(), "#123456"), _dir, images);
        Assert.Single(images);
        Assert.Contains($"src=\"cid:{images[0].ContentId}\"", html);
        Assert.Contains("[Logo]", MailHtml.ToText(stored));
    }

    [Fact]
    public void Platzhalter_in_HTML_werden_maskiert_und_Text_bleibt_Text()
    {
        var html = MailHtml.ToMailHtml("<p>Hallo {Kunde}</p>", new Dictionary<string, string> { ["Kunde"] = "<b>Böse</b> & Co" }, "#1f5fbf");
        Assert.Contains("&lt;b&gt;Böse&lt;/b&gt; &amp; Co", html);
        Assert.Contains("<p style=\"margin:0 0 10px 0;\">", html);
        Assert.Contains("<strong>fett</strong>", MailHtml.ToMailHtml("**fett**", new Dictionary<string, string>(), "#1f5fbf"));
    }

    [Fact]
    public void Mail_mit_HTML_Signatur_und_Fusszeile()
    {
        var d = new MailDesign { Name = "CI", Footer = "<p><span style=\"color:#888\">ERP Austria GmbH</span><br><a href=\"https://www.erpaustria.com\">www.erpaustria.com</a></p>" };
        var logoDir = Path.Combine(_dir, "mailvorlagen");
        var r = MailDesigner.Render(d, "Sg. Damen u. Herren", new Dictionary<string, string> { ["Absender"] = "Tanja" }, logoDir, null,
            "<p>Mit freundlichen Grüßen<br><b>{Absender}</b></p>");
        Assert.Contains("<b>Tanja</b>", r.Html);
        Assert.Contains("color:#888888", r.Html);
        Assert.Contains("Mit freundlichen Grüßen\nTanja", r.Text);
        Assert.Contains("www.erpaustria.com", r.Text);
        Assert.DoesNotContain("<p>", r.Text);
    }
}
