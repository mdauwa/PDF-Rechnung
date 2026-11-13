using PdfRechnung.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class ComposerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfr-bp-" + Guid.NewGuid().ToString("N")[..8]);
    public ComposerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Beleg: Text oben bei y=780 (≈ 20 mm vom oberen Rand) und unten bei y=40, dazu Steuerzeichen.</summary>
    private static byte[] Beleg(int pages = 1, bool withCodes = true)
    {
        var b = new PdfDocumentBuilder();
        var helv = b.AddStandard14Font(Standard14Font.Helvetica);
        var cour = b.AddStandard14Font(Standard14Font.Courier);
        for (var i = 0; i < pages; i++)
        {
            var p = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            p.AddText($"Rechnung 4711 Seite {i + 1}", 12, new PdfPoint(60, 780), helv);
            p.AddText("Summe 150,00 EUR", 10, new PdfPoint(60, 400), helv);
            if (withCodes && i == 0)
            {
                p.AddText("@@toa@b.at@@", 4, new PdfPoint(400, 820), cour);
                p.AddText("@@bpFirma A@@", 4, new PdfPoint(400, 815), cour);
                p.AddText("Vor dem Code @@edBetreff@@ nach dem Code", 9, new PdfPoint(60, 300), helv);
            }
        }
        return b.Build();
    }

    private string Briefpapier()
    {
        var b = new PdfDocumentBuilder();
        var helv = b.AddStandard14Font(Standard14Font.Helvetica);
        var p = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p.AddText("BRIEFKOPF FIRMA A", 16, new PdfPoint(60, 810), helv);
        p.AddText("Fusszeile Firma A", 7, new PdfPoint(60, 20), helv);
        File.WriteAllBytes(Path.Combine(_dir, "a.pdf"), b.Build());
        return "a.pdf";
    }

    private static string Text(byte[] pdf) => string.Join("\n", PdfText.Extract(pdf).Lines.Select(l => l.Line));

    [Fact]
    public void Steuerzeichen_werden_aus_dem_PDF_entfernt_der_Rest_bleibt()
    {
        var src = Beleg();
        var p = ControlCodeParser.Parse(PdfText.Extract(src).Lines);
        var r = PdfComposer.Compose(src, p, new StationeryOptions(), _dir);
        var text = Text(r.Pdf);
        Assert.DoesNotContain("@@", text);
        Assert.Contains("Rechnung 4711", text);
        Assert.Contains("Summe 150,00 EUR", text);
        Assert.True(r.RemovedCodes >= 2);
        Assert.Empty(ControlCodeParser.Parse(PdfText.Extract(r.Pdf).Lines).Codes);
    }

    [Fact]
    public void Briefpapier_per_Steuerzeichen_wird_unterlegt()
    {
        var src = Beleg();
        var p = ControlCodeParser.Parse(PdfText.Extract(src).Lines);
        var st = new StationeryOptions { Items = { new Stationery { Name = "Firma A", FirstPageFile = Briefpapier() } } };
        var r = PdfComposer.Compose(src, p, st, _dir);
        Assert.Equal("Firma A", r.StationeryName);
        var text = Text(r.Pdf);
        Assert.Contains("BRIEFKOPF FIRMA A", text);
        Assert.Contains("Rechnung 4711", text);
        Assert.Empty(r.Messages);
    }

    [Fact]
    public void Folgeseiten_ohne_Briefpapier()
    {
        var src = Beleg(pages: 2, withCodes: false);
        var st = new StationeryOptions { Default = "Firma A", Items = { new Stationery { Name = "Firma A", FirstPageFile = Briefpapier(), FollowingMode = "none" } } };
        var r = PdfComposer.Compose(src, new ParsedCodes(), st, _dir);
        using var doc = UglyToad.PdfPig.PdfDocument.Open(r.Pdf);
        Assert.Contains("BRIEFKOPF", doc.GetPage(1).Text);
        Assert.DoesNotContain("BRIEFKOPF", doc.GetPage(2).Text);
    }

    [Fact]
    public void Inhalt_im_Kopfbereich_gibt_Warnung_einpassen_behebt_es()
    {
        var src = Beleg(withCodes: false);
        var st = new Stationery { Name = "Firma A", FirstPageFile = Briefpapier(), MarginTop = 35, MarginBottom = 20 };
        var o = new StationeryOptions { Default = "Firma A", Items = { st } };
        var r = PdfComposer.Compose(src, new ParsedCodes(), o, _dir);
        Assert.Contains(r.Messages, m => m.Level == DocumentChecker.Warning && m.Text.Contains("Kopfbereich"));

        st.FitContent = true;
        var fitted = PdfComposer.Compose(src, new ParsedCodes(), o, _dir);
        Assert.Empty(fitted.Messages);
        // Der oberste Text liegt jetzt unterhalb des Kopfbereichs
        using var doc = UglyToad.PdfPig.PdfDocument.Open(fitted.Pdf);
        // Titel (12 pt, durch das Einpassen verkleinert) – nicht die Buchstaben des Briefkopfs (16 pt)
        var title = doc.GetPage(1).Letters.Where(l => l.PointSize is > 9 and < 12).Max(l => l.GlyphRectangle.Top);
        Assert.True(title < 842 - 35 * PdfComposer.MmToPt, $"Titel bei {title}");
    }

    [Theory]
    [InlineData("Firma A", true, null)]
    [InlineData("firma a", false, "meinten Sie")]
    [InlineData("Firma X", false, "nicht angelegt")]
    public void Auswahl_per_Name(string name, bool found, string? error)
    {
        var o = new StationeryOptions { Items = { new Stationery { Name = "Firma A" } } };
        var p = ControlCodeParser.Parse($"@@bp{name}@@");
        var (item, err) = PdfComposer.Select(p, o);
        Assert.Equal(found, item is not null);
        if (error is null) Assert.Null(err); else Assert.Contains(error, err);
    }

    [Fact]
    public void Leeres_bp_bedeutet_kein_Briefpapier_auch_wenn_Standard_gesetzt()
    {
        var o = new StationeryOptions { Default = "Firma A", Items = { new Stationery { Name = "Firma A" } } };
        Assert.Null(PdfComposer.Select(ControlCodeParser.Parse("@@bp@@"), o).Item);
        Assert.Equal("Firma A", PdfComposer.Select(new ParsedCodes(), o).Item?.Name);
    }

    [Fact]
    public void Vorschau_wird_erzeugt()
    {
        var pdf = PdfComposer.Preview(new Stationery { Name = "Firma A", FirstPageFile = Briefpapier(), MarginTop = 35, MarginBottom = 20 }, _dir);
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(2, doc.NumberOfPages);
        Assert.Contains("BRIEFKOPF", doc.GetPage(1).Text);
    }
}
