using PdfRechnung.Documents;
using PdfRechnung.Edi;
using PdfRechnung.Settings;
using Xunit;
using Xunit.Abstractions;

namespace PdfRechnung.Tests;

public class CiiWriterTests
{
    private readonly ITestOutputHelper _o;
    public CiiWriterTests(ITestOutputHelper o) => _o = o;

    private static InvoiceData Invoice(IEnumerable<string>? codes = null) =>
        InvoiceData.From(ControlCodeParser.Parse(string.Join("\n", codes ?? TestData.InvoiceCodes)), DefaultSettings.Units);

    private EdiReport Check(byte[] xml)
    {
        XmlInvoiceValidator.DataDirectory = Path.Combine(Path.GetTempPath(), "pdfr-val");
        var r = EdiValidation.Validate(xml, "x.xml");
        foreach (var i in r.Issues) _o.WriteLine($"{i.Level} {i.Rule} {i.Text}");
        return r;
    }

    [Fact]
    public void ZUGFeRD_EN16931_ist_gueltig()
    {
        var r = Check(CiiWriter.Write(Invoice(), new(CiiWriter.ProfileEn16931)));
        Assert.True(r.Valid);
        Assert.Equal("CII", r.Format);
    }

    [Fact]
    public void XRechnung_CII_ist_gueltig()
    {
        var codes = TestData.InvoiceCodes.Concat(new[]
        {
            "@@xi SELLER_CONTACT_NAME=Max Muster@@", "@@xi SELLER_CONTACT_PHONE=+43 1 234@@", "@@xi SELLER_CONTACT_EMAIL=office@erp.at@@",
            "@@xi ROUTING_ID=04011000-1234512345-06@@", "@@xi PAYMENT_TERMS_DUE_DATE=27.11.2026@@"
        });
        var r = Check(CiiWriter.Write(Invoice(codes), new(CiiWriter.ProfileXRechnung3, "einkauf@kunde.at")));
        Assert.True(r.Valid);
    }

    [Fact]
    public void Lastschrift_mit_Mandat_und_Glaeubiger_ID()
    {
        var codes = TestData.InvoiceCodes.Select(c => c.Replace("PAYMENT_MEANS_TYPE_CODE=58", "PAYMENT_MEANS_TYPE_CODE=59")).Concat(new[]
        {
            "@@xi PAYMENT_DIRECTDEBIT_MANDATE_ID=MD10001N1@@", "@@xi PAYMENT_DIRECTDEBIT_REFERENCE_ID=AT18ZZZ00000040643@@",
            "@@xi PAYMENT_DIRECTDEBIT_IBAN=AT551200079015623100@@", "@@xi PAYMENT_TERMS_DUE_DATE=20.11.2026@@"
        });
        var xml = System.Text.Encoding.UTF8.GetString(CiiWriter.Write(Invoice(codes), new(CiiWriter.ProfileEn16931)));
        Assert.Contains("<ram:CreditorReferenceID>AT18ZZZ00000040643</ram:CreditorReferenceID>", xml);
        Assert.Contains("<ram:DirectDebitMandateID>MD10001N1</ram:DirectDebitMandateID>", xml);
        Assert.True(Check(System.Text.Encoding.UTF8.GetBytes(xml)).Valid);
    }
}

public class FacturXTests
{
    [Fact]
    public void XML_wird_eingebettet_und_ist_wieder_lesbar()
    {
        var pdf = TestData.Pdf(new[] { "Rechnung" });
        var d = InvoiceData.From(ControlCodeParser.Parse(string.Join("\n", TestData.InvoiceCodes)), DefaultSettings.Units);
        var xml = CiiWriter.Write(d, new(CiiWriter.ProfileEn16931));
        var z = FacturX.Embed(pdf, xml, "EN 16931", "Rechnung R2026-0815");
        Assert.Equal(xml, FacturX.Extract(z));

        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(z), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var cat = doc.Internals.Catalog;
        Assert.NotNull(cat.Elements.GetArray("/AF"));
        Assert.NotNull(cat.Elements.GetArray("/OutputIntents"));
        var meta = ((cat.Elements["/Metadata"] as PdfSharp.Pdf.Advanced.PdfReference)!.Value as PdfSharp.Pdf.PdfDictionary)!;
        var xmp = System.Text.Encoding.UTF8.GetString(meta.Stream!.UnfilteredValue);
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", xmp);
        Assert.Contains("<fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>", xmp);
        Assert.Contains($"<pdf:Producer>{System.Security.SecurityElement.Escape(doc.Info.Producer)}</pdf:Producer>", xmp); // PDF/A: Info und XMP gleich
    }
}
