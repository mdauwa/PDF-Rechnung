using PdfRechnung.Documents;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public class ControlCodeTests
{
    private static ParsedCodes Parse() => ControlCodeParser.Parse(string.Join("\n", TestData.InvoiceCodes));

    [Fact]
    public void Liest_Format_Felder_und_Tabellen()
    {
        var p = Parse();
        Assert.Equal("gotomaxx.invoice.zugferd2.comfort", p.InvoiceFormat);
        Assert.Equal("Kunde & Co KG", p.Field("BUYER_NAME"));
        Assert.Equal("R2026-0815", p.Field("VOUCHER_ID"));
        Assert.Equal(2, p.Table("ITEM_DETAILS").Count());
        Assert.Single(p.Table("ALLOWANCE_CHARGE"));
        Assert.Empty(p.Unknown);
    }

    [Fact]
    public void Fortsetzungszeile_haengt_an_vorige_Tabellenzeile()
    {
        var row = Parse().Table("ITEM_DETAILS").Last();
        Assert.Equal("15,00", row.Get("ITEM_TOTAL_AMOUNT"));
        Assert.Equal("20", row.Get("ITEM_VAT_PERCENT"));
    }

    [Fact]
    public void Liest_Maildaten()
    {
        var p = Parse();
        Assert.Equal(new[] { "einkauf@kunde.at" }, p.To);
        Assert.Equal(new[] { "buchhaltung@kunde.at" }, p.Cc);
        Assert.Equal("Ihre Rechnung R2026-0815", p.Subject);
        Assert.Equal(new[] { "Sehr geehrte Damen und Herren,", "", "anbei Ihre Rechnung." }, p.BodyLines);
        Assert.Equal("Briefpapier Standard", p.Stationery);
        Assert.Equal("Rechnung_R2026-0815", p.FileName);
    }

    [Fact]
    public void Mehrere_Empfaenger_und_Sonderbefehle()
    {
        var p = ControlCodeParser.Parse("x @@toa@b.at;c@d.at@@ y @@ml0@@ @@rpc@@ @@nd@@ @@zz unbekannt@@ @@moX@@");
        Assert.Equal(new[] { "a@b.at", "c@d.at" }, p.To);
        Assert.False(p.MailEnabled);
        Assert.Single(p.RemovePages);
        Assert.Single(p.SplitAfterPages);
        Assert.Single(p.Unknown);
        Assert.Single(p.Ignored);
    }

    [Theory]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1234,56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-12,5", -12.5)]
    [InlineData("12,50-", -12.5)]
    [InlineData("1.000.000", 1000000)]
    [InlineData(" 20 ", 20)]
    public void Zahlen(string s, double expected) => Assert.Equal((decimal)expected, ControlCodeParser.Number(s));

    [Fact]
    public void Datum() => Assert.Equal(new DateOnly(2026, 11, 13), ControlCodeParser.Date("13.11.2026"));

    [Fact]
    public void Summen_und_Steuer_werden_nachgerechnet()
    {
        var inv = InvoiceData.From(Parse(), DefaultSettings.Units);
        Assert.Equal(115m, inv.LineTotal);
        Assert.Equal(10m, inv.ChargeTotal);
        Assert.Equal(125m, inv.NetAmount);
        Assert.Equal(25m, inv.TaxAmount);
        Assert.Equal(150m, inv.GrossAmount);
        Assert.Equal("HUR", inv.Lines[0].UnitCode);
        Assert.Equal("H87", inv.Lines[1].UnitCode);
    }

    [Fact]
    public void Pruefung_meldet_nicht_unterstuetztes_Format_und_fehlende_Steuernummer_nicht()
    {
        var p = Parse();
        var v = new Vorgang { Mail = new MailData { To = { "einkauf@kunde.at" }, Subject = "x" }, Invoice = InvoiceData.From(p, DefaultSettings.Units) };
        var s = DefaultSettings.Create(); s.Mail.Enabled = true;
        var m = DocumentChecker.Check(v, p, s);
        Assert.Contains(m, x => x.Level == DocumentChecker.Error && x.Text.Contains("noch nicht unterstützt"));
        Assert.DoesNotContain(m, x => x.Text.Contains("Summenprüfung"));
        Assert.DoesNotContain(m, x => x.Text.Contains("SELLER_TAX_ID"));
    }

    [Fact]
    public void Pruefung_findet_Summenabweichung_und_fehlenden_Empfaenger()
    {
        var codes = TestData.InvoiceCodes.Select(c => c.Replace("GROSS_AMOUNT=150,00", "GROSS_AMOUNT=151,00")).Where(c => !c.StartsWith("@@to")).ToArray();
        var p = ControlCodeParser.Parse(string.Join("\n", codes));
        var v = new Vorgang { Invoice = InvoiceData.From(p, DefaultSettings.Units) };
        var m = DocumentChecker.Check(v, p, DefaultSettings.Create());
        Assert.Contains(m, x => x.Level == DocumentChecker.Error && x.Text.StartsWith("Summenprüfung"));
        Assert.Contains(m, x => x.Level == DocumentChecker.Error && x.Text.Contains("Kein E-Mail-Empfänger"));
    }

    private static List<CheckMessage> CheckWith(params (string From, string To)[] replace)
    {
        var text = string.Join("\n", TestData.InvoiceCodes);
        foreach (var (f, t) in replace) text = text.Replace(f, t);
        var p = ControlCodeParser.Parse(text);
        var v = new Vorgang { Mail = new MailData { To = { "einkauf@kunde.at" }, Subject = "x" }, Invoice = InvoiceData.From(p, DefaultSettings.Units) };
        return DocumentChecker.Check(v, p, DefaultSettings.Create());
    }

    [Fact]
    public void Feldkatalog_ist_vollstaendig_und_eigenformuliert()
    {
        Assert.True(ERechnungCatalog.All.Count > 200);
        Assert.Equal("Rechnungsnummer", ERechnungCatalog.Field("VOUCHER_ID")!.Text);
        Assert.Equal("BT-1", ERechnungCatalog.Field("VOUCHER_ID")!.Bt);
        Assert.Equal("380", ERechnungCatalog.Field("DOCUMENT_TYPE")!.Default);
        Assert.NotNull(ERechnungCatalog.Field("ITEM_LINE_ID", "ITEM_CLASSIFICATION"));
        Assert.All(ERechnungCatalog.All, f => Assert.False(string.IsNullOrWhiteSpace(f.Text)));
    }

    [Fact]
    public void Pruefung_je_Format_aus_dem_Feldkatalog()
    {
        // Tippfehler im Feldnamen, falsches Datum, falsches Land
        var m = CheckWith(("VOUCHER_ID=", "VOUCHR_ID="), ("BUYER_ADDRESS_COUNTRY=AT", "BUYER_ADDRESS_COUNTRY=Österreich"));
        Assert.Contains(m, x => x.Text.Contains("Unbekannte Felder") && x.Text.Contains("VOUCHR_ID"));
        Assert.Contains(m, x => x.Level == DocumentChecker.Error && x.Text.Contains("Rechnungsnummer") && x.Text.Contains("VOUCHER_ID"));
        Assert.Contains(m, x => x.Level == DocumentChecker.Error && x.Text.Contains("Ländercode"));

        // XRechnung verlangt Leitweg-ID; ZUGFeRD Comfort nicht
        var x1 = CheckWith(("@@xt gotomaxx.invoice.zugferd2.comfort@@", "@@xt gotomaxx.invoice.xrechnung3.cii@@"));
        Assert.Contains(x1, x => x.Level == DocumentChecker.Error && x.Text.Contains("ROUTING_ID"));
        Assert.DoesNotContain(CheckWith(), x => x.Text.Contains("ROUTING_ID"));

        // Extended-Feld in Comfort wird nicht übertragen
        var x2 = CheckWith(("@@xi VOUCHER_DATE=", "@@xi ULTIMATE_SHIPPING_ADDRESS_NAME=Endkunde@@\n@@xi VOUCHER_DATE="));
        Assert.Contains(x2, x => x.Level == DocumentChecker.Info && x.Text.Contains("nicht übertragen") && x.Text.Contains("ULTIMATE_SHIPPING_ADDRESS_NAME"));
    }

    [Fact]
    public void Beleg_ohne_ERechnung_ist_bereit()
    {
        var p = ControlCodeParser.Parse("@@toa@b.at@@ @@edAngebot 4711@@");
        var v = new Vorgang { Mail = new MailData { To = { "a@b.at" }, Subject = "Angebot 4711" }, Invoice = InvoiceData.From(p, DefaultSettings.Units), Codes = p.Codes };
        var s = DefaultSettings.Create(); s.Mail.Enabled = true;
        v.Messages = DocumentChecker.Check(v, p, s);
        v.UpdateStatus();
        Assert.Equal(VorgangStatus.Ready, v.Status);
    }
}

public class ErpPdfMailerCodeTests
{
    // So druckt das ERP die PDFMAILER-Steuerzeichen (Ausdruck „Microsoft Print to PDF“ vom 13.11.2026, Daten anonymisiert)
    private const string Block = """
        @@mfAngebot_N11260051_13.11.2026@@
        @@aaoffice@kunde.example@@
        @@edAngebot 11260051 13.11.2026@@
        @@mbK:\WebWARE5\APP\PDFMailer\DAT00004\Angebot.txt@@
        @@od0@@
        @@maK:\WebWARE5\APP\PDFMailer\DAT00004\ERP Austria Group Dienstleistungspreise 2027.pdf@@
        """;

    [Fact]
    public void Liest_PDFMAILER_Block_aus_dem_ERP()
    {
        var p = ControlCodeParser.Parse(Block);
        Assert.Equal(6, p.Codes.Count);
        Assert.Empty(p.Unknown);
        Assert.Equal("Angebot_N11260051_13.11.2026", p.MailFileName);
        Assert.Equal(new[] { "office@kunde.example" }, p.To);
        Assert.Equal("Angebot 11260051 13.11.2026", p.Subject);
        Assert.Equal(@"K:\WebWARE5\APP\PDFMailer\DAT00004\Angebot.txt", p.BodyFile);
        Assert.Single(p.Attachments);
    }

    [Theory]
    [InlineData(@"K:\WebWARE5\APP\a.txt", @"\\srv\WebWARE5\APP\a.txt")]
    [InlineData(@"k:\WebWARE5\x.pdf", @"\\srv\WebWARE5\x.pdf")]
    [InlineData(@"C:\Temp\x.pdf", @"C:\Temp\x.pdf")]
    public void Pfadzuordnung(string input, string expected) =>
        Assert.Equal(expected, DocumentProcessor.MapPath(input, new[] { @"K:\=\\srv\" }));
}
