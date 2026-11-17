using System.Xml.Linq;
using UglyToad.PdfPig;

namespace PdfRechnung.Edi;

/// <summary>Einstieg: erkennt das Format (EDIFACT, E-Rechnungs-XML, ZUGFeRD-PDF) und prüft die Datei.</summary>
public static class EdiValidation
{
    private static readonly string[] InvoiceNames = { "factur-x.xml", "zugferd-invoice.xml", "xrechnung.xml", "zugferd_invoice.xml", "ZUGFeRD-invoice.xml" };

    /// <summary>Lohnt sich eine Prüfung (EDIFACT, XML oder PDF)?</summary>
    public static bool CanValidate(byte[] data) =>
        EdifactValidator.Looks(data) || XmlInvoiceValidator.Looks(data) || IsPdf(data);

    public static bool IsPdf(ReadOnlySpan<byte> d) => d.Length > 5 && d[0] == '%' && d[1] == 'P' && d[2] == 'D' && d[3] == 'F';

    public static EdiReport Validate(byte[] data, string? fileName = null)
    {
        var r = ValidateCore(data, fileName);
        // Empfehlung erst ganz am Ende festlegen (beim PDF kommen Meldungen zur Hülle dazu)
        if (r.Acceptance is not null) r.Acceptance = r.Errors > 0 || r.Scenario is null ? "ablehnen" : "annehmen";
        return r;
    }

    private static EdiReport ValidateCore(byte[] data, string? fileName)
    {
        try
        {
            if (EdifactValidator.Looks(data)) return EdifactValidator.Validate(data, fileName);
            if (IsPdf(data)) return Pdf(data, fileName);
            if (XmlInvoiceValidator.Looks(data)) return XmlInvoiceValidator.Validate(data, fileName);
            var r = new EdiReport { Format = "unbekannt", FileName = fileName, Size = data.Length, Title = "Unbekanntes Format" };
            r.Warn("Format nicht erkannt – geprüft werden EDIFACT/EANCOM (beginnt mit UNA/UNB), E-Rechnungs-XML (CII, UBL) und ZUGFeRD/Factur-X-PDF.");
            return r;
        }
        catch (Exception ex)
        {
            var r = new EdiReport { Format = "unbekannt", FileName = fileName, Size = data.Length, Title = "Prüfung fehlgeschlagen" };
            r.Error("Die Prüfung ist abgebrochen: " + ex.Message);
            return r;
        }
    }

    /// <summary>ZUGFeRD/Factur-X: eingebettete XML suchen, PDF/A-3-Kennung und XMP prüfen, dann die XML prüfen.</summary>
    private static EdiReport Pdf(byte[] data, string? fileName)
    {
        var r = new EdiReport { Format = "ZUGFeRD-PDF", FileName = fileName, Size = data.Length, Title = "PDF" };
        r.Checks.Add("PDF: eingebettete Rechnung, PDF/A-3- und Factur-X-Kennung");
        using var pdf = PdfDocument.Open(data);
        byte[]? xml = null;
        string? xmlName = null;
        var names = new List<string>();
        if (pdf.Advanced.TryGetEmbeddedFiles(out var files))
            foreach (var f in files)
            {
                names.Add(f.Name);
                if (xml is null && InvoiceNames.Any(n => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    xml = f.Bytes.ToArray(); xmlName = f.Name;
                }
            }
        if (xml is null)
        {
            var anyXml = names.FirstOrDefault(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (anyXml is not null && files is not null)
            {
                var f = files.First(x => x.Name == anyXml);
                xml = f.Bytes.ToArray(); xmlName = f.Name;
                r.Warn($"Eingebettete XML heißt „{anyXml}“ – vorgesehen ist factur-x.xml (ZUGFeRD 2.x) bzw. xrechnung.xml.");
            }
        }

        // XMP: PDF/A-Kennung und Factur-X-Erweiterung
        string? part = null, conformance = null, fxLevel = null, fxFile = null;
        if (pdf.TryGetXmpMetadata(out var xmp))
        {
            var x = xmp.GetXDocument();
            string? Val(string local) =>
                x.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value
                ?? x.Descendants().Attributes().FirstOrDefault(a => a.Name.LocalName == local)?.Value;
            part = Val("part");
            conformance = Val("conformance");
            fxLevel = Val("ConformanceLevel");
            fxFile = Val("DocumentFileName");
        }
        if (xml is null)
        {
            r.Title = "PDF ohne E-Rechnung";
            r.Error(names.Count == 0 ? "Im PDF ist keine Datei eingebettet – keine ZUGFeRD/Factur-X-Rechnung." : $"Im PDF ist keine Rechnungs-XML eingebettet (gefunden: {string.Join(", ", names)}).");
            return r;
        }
        if (part != "3") r.Warn(part is null ? "Keine PDF/A-Kennung in den XMP-Metadaten – ZUGFeRD/Factur-X verlangt PDF/A-3." : $"PDF/A-{part}{conformance} – ZUGFeRD/Factur-X verlangt PDF/A-3.");
        else r.Info($"PDF/A-3{conformance} laut XMP (eine vollständige PDF/A-Prüfung ist das nicht).");
        if (fxLevel is null) r.Warn("Die Factur-X-Angaben in den XMP-Metadaten fehlen (fx:ConformanceLevel, fx:DocumentFileName).");
        else if (fxFile is not null && !fxFile.Equals(xmlName, StringComparison.OrdinalIgnoreCase))
            r.Warn($"XMP nennt die Datei „{fxFile}“, eingebettet ist „{xmlName}“.");

        XmlInvoiceValidator.Validate(xml, xmlName, r);
        r.Format = "ZUGFeRD-PDF";
        r.Title = $"PDF mit {r.Title}";
        if (fxLevel is not null && r.Issues.FirstOrDefault(i => i.Text.StartsWith("Spezifikationskennung:")) is { } spec)
        {
            var key = XmlInvoiceValidator.Profile(spec.Text["Spezifikationskennung: ".Length..], "CII").Key;
            var want = key switch { "en16931" => "EN 16931", "extended" => "EXTENDED", "basic" => "BASIC", "basicwl" => "BASIC WL", "minimum" => "MINIMUM", "xrechnung" => "XRECHNUNG", _ => null };
            if (want is not null && !fxLevel.Equals(want, StringComparison.OrdinalIgnoreCase) && !(want == "EN 16931" && fxLevel.Equals("COMFORT", StringComparison.OrdinalIgnoreCase)))
                r.Warn($"XMP fx:ConformanceLevel = „{fxLevel}“, die XML ist aber {want}.");
        }
        return r;
    }
}
