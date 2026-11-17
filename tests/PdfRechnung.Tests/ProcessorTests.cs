using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class ProcessorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (DocumentStore Store, DocumentProcessor Proc) Create(Action<AppSettings>? cfg = null)
    {
        var settings = new SettingsStore(_dir);
        var s = DefaultSettings.Create();
        cfg?.Invoke(s);
        var store = new DocumentStore(settings, NullLogger<DocumentStore>.Instance);
        var proc = new DocumentProcessor(store, new StaticOptions<MailOptions>(s.Mail), new StaticOptions<InvoiceOptions>(s.Invoice), NullLogger<DocumentProcessor>.Instance);
        return (store, proc);
    }

    [Fact]
    public void PDF_wird_gelesen_und_als_Vorgang_abgelegt()
    {
        var (store, proc) = Create(s => s.Mail.Enabled = true);
        var v = proc.Process(TestData.Pdf(TestData.InvoiceCodes), "Upload", "test.pdf", "Tester");

        Assert.Equal(TestData.InvoiceCodes.Length, v.Codes.Count);
        Assert.Equal("Rechnung", v.DocumentType);
        Assert.Equal("R2026-0815", v.VoucherId);
        Assert.Equal("Kunde & Co KG", v.BuyerName);
        Assert.Equal(150m, v.GrossAmount);
        Assert.Equal(new[] { "einkauf@kunde.at" }, v.Mail.To);
        Assert.Equal("Rechnung_R2026-0815", v.Mail.FileName);
        Assert.Equal("Sehr geehrte Damen und Herren,\n\nanbei Ihre Rechnung.", v.Mail.Body);
        // Briefpapier fehlt → Fehler, kein Autoversand
        Assert.Equal(VorgangStatus.Error, v.Status);
        // ZUGFeRD 2 Comfort erzeugt: XML abgelegt, im Versand-PDF eingebettet, gültig
        Assert.Equal("ZUGFeRD 2 Comfort (EN 16931)", v.EInvoiceProfile);
        Assert.NotNull(store.FilePath(v.Id, DocumentStore.XmlFile));
        Assert.Contains(v.Messages, m => m.Text.Contains("erzeugt und geprüft") && m.Text.EndsWith("gültig."));
        Assert.DoesNotContain(v.Messages, m => m.Text.StartsWith("E-Rechnung:") && m.Level == DocumentChecker.Error);
        Assert.Equal(File.ReadAllBytes(store.FilePath(v.Id, DocumentStore.XmlFile)!), FacturX.Extract(File.ReadAllBytes(store.FilePath(v.Id, DocumentStore.SendFile)!)));
        // Validator erkennt das Versand-PDF als ZUGFeRD und findet keine Fehler (wie „E-Rechnung prüfen“ im Portal)
        var rep = PdfRechnung.Edi.EdiValidation.Validate(File.ReadAllBytes(store.FilePath(v.Id, DocumentStore.SendFile)!), "versand.pdf");
        Assert.True(rep.Valid, string.Join("\n", rep.Issues.Select(i => i.Text)));
        // Versand-PDF ohne Steuerzeichen
        var send = store.FilePath(v.Id, DocumentStore.SendFile);
        Assert.NotNull(send);
        Assert.Empty(ControlCodeParser.Parse(PdfText.Extract(File.ReadAllBytes(send!)).Lines).Codes);
        Assert.True(v.RemovedCodes > 0);
        // Briefpapier „Briefpapier Standard“ ist nicht angelegt → Fehler
        Assert.Contains(v.Messages, m => m.Text.Contains("Briefpapier „Briefpapier Standard“ ist nicht angelegt"));

        // neu laden (wie nach einem Neustart)
        var store2 = new DocumentStore(new SettingsStore(_dir), NullLogger<DocumentStore>.Instance);
        var again = store2.Get(v.Id)!;
        Assert.Equal(v.VoucherId, again.VoucherId);
        Assert.NotNull(store2.FilePath(v.Id, DocumentStore.PdfFile));
    }

    [Fact]
    public void Korrekturen_der_Rechnungsdaten_wirken_auf_die_E_Rechnung_und_ueberstehen_Neu_pruefen()
    {
        var (store, proc) = Create(s => s.Mail.Enabled = true);
        var v = proc.Process(TestData.Pdf(TestData.InvoiceCodes), "Upload", "test.pdf", "Tester");
        var gross = v.GrossAmount;
        store.Update(v.Id, x =>
        {
            x.Corrections = new InvoiceCorrections
            {
                Fields = { ["BUYER_TAX_ID"] = "ATU99999999", ["ROUTING_ID"] = "991-12345-67", ["SELLER_BIC"] = "" },
                Lines = { [0] = new LineCorrection { UnitCode = "HUR", Name = "Beratung vor Ort" } }
            };
            return true;
        });
        var after = proc.Recheck(v.Id, "Tester")!;
        var xml = File.ReadAllText(store.FilePath(v.Id, DocumentStore.XmlFile)!);
        Assert.Contains("ATU99999999", xml);
        Assert.Contains("<ram:BuyerReference>991-12345-67</ram:BuyerReference>", xml);
        Assert.DoesNotContain("BKAUATWW", xml);                     // leerer Wert = Feld entfernt
        Assert.Contains("unitCode=\"HUR\"", xml);
        Assert.Contains("Beratung vor Ort", xml);
        Assert.Equal(gross, after.GrossAmount);                       // Beträge unverändert
        Assert.Equal("Beratung vor Ort", after.Invoice.Lines[0].Name);
        // Korrekturen bleiben beim nächsten „Neu prüfen“ erhalten
        Assert.Equal("ATU99999999", proc.Recheck(v.Id, "Tester")!.Invoice.Fields["BUYER_TAX_ID"]);
    }

    [Fact]
    public void Steuerkategorie_ist_nur_bei_null_Prozent_aenderbar()
    {
        var p = ControlCodeParser.Parse(string.Join("\n", TestData.InvoiceCodes));
        new InvoiceCorrections { Lines = { [0] = new LineCorrection { VatCategory = "AE" } } }.Apply(p);
        Assert.Equal("S", p.Table("ITEM_DETAILS").First().Get("ITEM_VAT_CATEGORY")); // 20 % → unverändert
        new InvoiceCorrections { Fields = { ["GROSS_AMOUNT"] = "1,00" } }.Apply(p);
        Assert.Equal("150,00", p.Field("GROSS_AMOUNT"));                           // Beträge sind gesperrt
    }

    [Fact]
    public void Beleg_ohne_Steuerzeichen_bekommt_Vorlagen_und_Warnung()
    {
        var (_, proc) = Create(s => { s.Mail.Enabled = true; s.Mail.DefaultSubject = "Beleg {Belegnummer}"; });
        var v = proc.Process(TestData.Pdf(Array.Empty<string>(), "Brief"), "Eingang", "brief.pdf");
        Assert.Empty(v.Codes);
        Assert.Equal("Beleg", v.Mail.Subject);
        Assert.Equal(VorgangStatus.Error, v.Status); // kein Empfänger
        Assert.Contains(v.Messages, m => m.Text.Contains("keine Steuerzeichen"));
    }

    [Fact]
    public void Versanddaten_bleiben_bei_neuer_Pruefung_erhalten()
    {
        var (store, proc) = Create(s => s.Mail.Enabled = true);
        var codes = TestData.InvoiceCodes.Where(c => !c.StartsWith("@@xt") && !c.StartsWith("@@xi") && !c.StartsWith("@@xr") && !c.StartsWith("@@<<") && !c.StartsWith("@@bp")).ToArray();
        var v = proc.Process(TestData.Pdf(codes), "Upload", "brief.pdf");
        Assert.Equal(VorgangStatus.Ready, v.Status);
        store.Update(v.Id, x => { x.Mail.To = new() { "neu@kunde.at" }; x.Mail.Edited = true; return true; });
        var r = proc.Recheck(v.Id, "Tester")!;
        Assert.Equal(new[] { "neu@kunde.at" }, r.Mail.To);
        Assert.Equal(VorgangStatus.Ready, r.Status);
    }

    [Fact]
    public void Steuerzeichen_neben_anderem_Text_und_auf_Folgeseiten()
    {
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var helv = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var cour = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Courier);
        var p1 = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p1.AddText("Rechnung Nr. 4711", 12, new UglyToad.PdfPig.Core.PdfPoint(60, 700), helv);
        p1.AddText("@@toa@b.at@@", 4, new UglyToad.PdfPig.Core.PdfPoint(170, 700), cour);   // gleiche Grundlinie
        p1.AddText("Text davor @@edBetreff im Text@@ danach", 9, new UglyToad.PdfPig.Core.PdfPoint(60, 500), helv);
        var p2 = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p2.AddText("@@fnDatei_Seite2@@", 4, new UglyToad.PdfPig.Core.PdfPoint(400, 20), cour);
        var (_, lines) = PdfText.Extract(b.Build());
        var p = ControlCodeParser.Parse(lines);
        Assert.Equal(new[] { "a@b.at" }, p.To);
        Assert.Equal("Betreff im Text", p.Subject);
        Assert.Equal("Datei_Seite2", p.FileName);
        Assert.Equal(2, p.Codes.Single(c => c.Code == "fn").Page);
    }
}
