using System.Text;
using PdfRechnung.Edi;
using Xunit;

namespace PdfRechnung.Tests;

public class EdiValidatorTests
{
    // EANCOM-97-Rechnung (D.96A), gekürzt, ausgedachte Daten
    public const string Invoic = """
UNA:+.? '
UNB+UNOC:3+9012345000004:14+9098765000007:14+261116:1200+REF0001'
UNH+1+INVOIC:D:96A:UN:EAN008'
BGM+380+R2026-0815+9'
DTM+137:20261116:102'
NAD+BY+9098765000007::9'
NAD+SU+9012345000004::9'
CUX+2:EUR:4'
LIN+1++4012345000009:EN'
QTY+47:2'
MOA+203:100.00'
PRI+AAA:50.00'
TAX+7+VAT+++:::20+S'
UNS+S'
MOA+86:120.00'
TAX+7+VAT+++:::20+S'
MOA+124:20.00'
UNT+16+1'
UNZ+1+REF0001'
""";

    private static EdiReport Check(string s) => EdifactValidator.Validate(Encoding.Latin1.GetBytes(s), "test.edi");

    [Fact]
    public void Gueltige_Rechnung_hat_keine_Fehler()
    {
        var r = Check(Invoic);
        Assert.True(r.Valid, string.Join("\n", r.Issues.Select(i => $"{i.Level} {i.Segment} {i.Text}")));
        var m = Assert.Single(r.Messages);
        Assert.Equal("INVOIC", m.Type);
        Assert.StartsWith("D.96A", m.Directory);
        Assert.Equal("9012345000004:14", r.Sender);
        Assert.Contains("D.96A", r.Title);
    }

    [Fact]
    public void Zaehler_und_Referenzen_werden_geprueft()
    {
        var r = Check(Invoic.Replace("UNT+16+1", "UNT+15+2").Replace("UNZ+1+REF0001", "UNZ+2+REF0002"));
        Assert.Contains(r.Issues, i => i.Text.Contains("UNT: Segmentanzahl 15"));
        Assert.Contains(r.Issues, i => i.Text.Contains("UNT: Referenz"));
        Assert.Contains(r.Issues, i => i.Text.Contains("UNZ: Anzahl 2"));
        Assert.Contains(r.Issues, i => i.Text.Contains("UNZ: Referenz"));
    }

    [Fact]
    public void Struktur_Pflichtangaben_und_Laengen()
    {
        var r = Check(Invoic.Replace("BGM+380+R2026-0815+9'\n", "").Replace("UNT+16+1", "UNT+15+1")
                            .Replace("NAD+BY+9098765000007::9", "NAD++9098765000007::9")
                            .Replace("QTY+47:2", "QTY+47:2X")
                            .Replace("CUX+2:EUR:4", "CUX+2:EUR:4'\nXYZ+1"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Fehler && i.Text.Contains("Pflichtsegment BGM fehlt"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Fehler && i.Text.Contains("3035"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Fehler && i.Text.Contains("numerisch"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Fehler && i.Text.Contains("XYZ"));
    }

    [Fact]
    public void Zeichensatz_und_Freigabezeichen()
    {
        var r = Check(Invoic.Replace("UNOC:3", "UNOA:3").Replace("R2026-0815", "r2026?+0815"));
        Assert.Contains(r.Issues, i => i.Text.Contains("UNOA kennt keine Kleinbuchstaben"));
        var segs = EdifactValidator.Parse(Invoic.Replace("R2026-0815", "R2026?+0815"), new EdiReport(), out _);
        Assert.Equal("R2026+0815", segs.First(s => s.Tag == "BGM").El(1));
    }

    // EANCOM 2002 (D.01B, EAN011) mit Positionsrabatt (ALC/PCD/MOA) unmittelbar vor UNS
    public const string InvoicEancom = """
UNA:+.? '
UNB+UNOC:3+9012345000004:14+9098765000007:14+20261116:1200+REF0002'
UNH+1+INVOIC:D:01B:UN:EAN011'
BGM+380+R2026-0816+9'
DTM+137:20261116:102'
RFF+ON:4711'
DTM+171:20261110:102'
NAD+BY+9098765000007::9'
LOC+7+9098765000014::9'
RFF+VA:ATU12345678'
DTM+171:20260101:102'
NAD+SU+9012345000004::9'
CUX+2:EUR:4'
LIN+1++4012345000009:SRV'
QTY+47:2'
MOA+203:90.00'
PRI+AAA:50.00'
TAX+7+VAT+++:::20+S'
ALC+A++++DI'
PCD+1:10'
MOA+204:10.00'
UNS+S'
CNT+2:1'
MOA+86:108.00'
MOA+79:90.00'
MOA+125:90.00'
MOA+124:18.00'
TAX+7+VAT+++:::20+S'
MOA+124:18.00'
UNT+28+1'
UNZ+1+REF0002'
""";

    [Fact]
    public void Eancom_Rabatt_vor_UNS_ist_gueltig()
    {
        var r = Check(InvoicEancom);
        Assert.True(r.Valid, string.Join("\n", r.Issues.Select(i => $"{i.Level} #{i.Segment} {i.Text}")));
        Assert.Contains("EANCOM", r.Messages[0].Directory);
    }

    [Fact]
    public void Unbekanntes_Verzeichnis_nur_Syntax()
    {
        var r = Check(Invoic.Replace("D:96A:UN:EAN008", "D:93A:UN"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Warnung && i.Text.Contains("D.93A"));
        Assert.True(r.Valid);
    }
}

public class XmlInvoiceValidatorTests
{
    private static byte[] Daten(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Daten", f));

    [Fact]
    public void CII_Beispiel_ist_gueltig_und_kaputtes_wird_erkannt()
    {
        PdfRechnung.Edi.XmlInvoiceValidator.DataDirectory = Path.Combine(Path.GetTempPath(), "pdfr-val");
        var ok = PdfRechnung.Edi.EdiValidation.Validate(Daten("CII_EN16931_Beispiel.xml"), "a.xml");
        Assert.True(ok.Valid, string.Join("\n", ok.Issues.Select(i => i.Text)));
        Assert.Equal("CII", ok.Format);
        Assert.Contains(ok.Checks, c => c.Contains("EN 16931"));

        var text = System.Text.Encoding.UTF8.GetString(Daten("CII_EN16931_Beispiel.xml")).Replace("<ram:GrandTotalAmount>", "<ram:GrandTotalAmount>9");
        var bad = PdfRechnung.Edi.EdiValidation.Validate(System.Text.Encoding.UTF8.GetBytes(text), "b.xml");
        Assert.Contains(bad.Issues, i => i.Level == EdiReport.Fehler && i.Text.Contains("BR-CO-15"));

        var schema = PdfRechnung.Edi.EdiValidation.Validate(System.Text.Encoding.UTF8.GetBytes(text.Replace("<ram:TypeCode>380</ram:TypeCode>", "<ram:TypeCode>380</ram:TypeCode><ram:Unsinn/>")), "c.xml");
        Assert.Contains(schema.Issues, i => i.Level == EdiReport.Fehler && i.Text.StartsWith("Schema:"));
    }

    [Fact]
    public void UBL_Beispiel_ist_gueltig()
    {
        PdfRechnung.Edi.XmlInvoiceValidator.DataDirectory = Path.Combine(Path.GetTempPath(), "pdfr-val");
        var r = PdfRechnung.Edi.EdiValidation.Validate(Daten("UBL_EN16931_Beispiel.xml"), "u.xml");
        Assert.True(r.Valid, string.Join("\n", r.Issues.Select(i => i.Text)));
        Assert.Equal("UBL", r.Format);
        Assert.Equal("annehmen", r.Acceptance);
        Assert.Contains("UBL Invoice", r.Scenario);
    }

    [Fact]
    public void Empfehlung_Regelstufe_und_XRechnung_Version()
    {
        PdfRechnung.Edi.XmlInvoiceValidator.DataDirectory = Path.Combine(Path.GetTempPath(), "pdfr-val");
        var text = System.Text.Encoding.UTF8.GetString(Daten("CII_EN16931_Beispiel.xml")).Replace("<ram:GrandTotalAmount>", "<ram:GrandTotalAmount>9");
        var bad = PdfRechnung.Edi.EdiValidation.Validate(System.Text.Encoding.UTF8.GetBytes(text), "b.xml");
        Assert.Equal("ablehnen", bad.Acceptance);
        var rule = bad.Issues.First(i => i.Level == EdiReport.Fehler && i.Rule is not null).Rule!;
        try
        {
            PdfRechnung.Edi.XmlInvoiceValidator.RuleLevels = () => bad.Issues.Where(i => i.Rule is not null).Select(i => i.Rule!).Distinct()
                .ToDictionary(x => x, x => EdiReport.Warnung, StringComparer.OrdinalIgnoreCase);
            var soft = PdfRechnung.Edi.EdiValidation.Validate(System.Text.Encoding.UTF8.GetBytes(text), "b.xml");
            Assert.Equal("annehmen", soft.Acceptance);
            Assert.Contains(soft.Issues, i => i.Rule == rule && i.Level == EdiReport.Warnung && i.Text.Contains("Stufe angepasst"));
            PdfRechnung.Edi.XmlInvoiceValidator.RuleLevels = () => new Dictionary<string, string> { [rule] = "aus" };
            Assert.DoesNotContain(PdfRechnung.Edi.EdiValidation.Validate(System.Text.Encoding.UTF8.GetBytes(text), "b.xml").Issues, i => i.Rule == rule);
        }
        finally { PdfRechnung.Edi.XmlInvoiceValidator.RuleLevels = () => new Dictionary<string, string>(); }

        var cii = System.Text.Encoding.UTF8.GetString(Daten("CII_EN16931_Beispiel.xml"));
        var old = System.Text.RegularExpressions.Regex.Replace(cii, @"(<ram:GuidelineSpecifiedDocumentContextParameter>\s*<ram:ID>)[^<]*", "$1urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_2.3");
        var r2 = PdfRechnung.Edi.EdiValidation.Validate(System.Text.Encoding.UTF8.GetBytes(old), "x.xml");
        Assert.Equal("ablehnen", r2.Acceptance);
        Assert.Null(r2.Scenario);
        Assert.Contains(r2.Issues, i => i.Text.Contains("Kein Prüfszenario für XRechnung 2.3"));
    }
}

public class EdiSampleDump
{
    [Fact]
    public void Beispieldateien_ausgeben()
    {
        var dir = Environment.GetEnvironmentVariable("EDI_SAMPLES");
        if (string.IsNullOrEmpty(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var data = File.ReadAllBytes(f);
            var r = PdfRechnung.Edi.EdiValidation.Validate(data, Path.GetFileName(f));
            Console.WriteLine($"DUMP == {f}: {r.Summary}");
            foreach (var i in r.Issues.Take(12)) Console.WriteLine($"DUMP   {i.Level} #{i.Segment} {i.Tag} {i.Position} {i.Text}");
        }
    }
}
