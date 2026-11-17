using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfRechnung.Documents;

/// <summary>
/// Bettet die CII-Rechnung als <c>factur-x.xml</c> in das Versand-PDF ein (ZUGFeRD 2.x / Factur-X):
/// Anhang mit AFRelationship „Alternative“, XMP-Metadaten mit PDF/A-3- und Factur-X-Kennung sowie sRGB-Farbprofil (OutputIntent).
/// Die PDF/A-3-Kennzeichnung erfolgt nach bestem Wissen – der Inhalt kommt vom Druckertreiber und wird nicht umgebaut.
/// </summary>
public static class FacturX
{
    public const string FileName = "factur-x.xml";

    /// <summary>Konformitätsstufe für die XMP-Metadaten (EN 16931 = Comfort).</summary>
    public static byte[] Embed(byte[] pdf, byte[] xml, string conformanceLevel = "EN 16931", string? title = null)
    {
        // 1. Lauf: Erzeuger feststellen, den PDFsharp beim Speichern einträgt (muss in XMP und Info übereinstimmen)
        var producer = ProducerAfterSave(pdf);

        using var input = new MemoryStream(pdf);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var now = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(title)) doc.Info.Title = title;
        doc.Info.ModificationDate = now;
        var catalog = doc.Internals.Catalog;

        // ---- eingebettete Datei ----
        var ef = new PdfDictionary(doc);
        ef.Elements["/Type"] = new PdfName("/EmbeddedFile");
        ef.Elements["/Subtype"] = new PdfName("/text#2Fxml");
        var param = new PdfDictionary(doc);
        param.Elements["/ModDate"] = new PdfDate(now);
        param.Elements["/Size"] = new PdfInteger(xml.Length);
        ef.Elements["/Params"] = param;
        ef.CreateStream(xml);
        doc.Internals.AddObject(ef);

        var spec = new PdfDictionary(doc);
        spec.Elements["/Type"] = new PdfName("/Filespec");
        spec.Elements["/F"] = new PdfString(FileName);
        spec.Elements["/UF"] = new PdfString(FileName, PdfStringEncoding.Unicode);
        spec.Elements["/Desc"] = new PdfString("Factur-X/ZUGFeRD Rechnung");
        spec.Elements["/AFRelationship"] = new PdfName("/Alternative");
        var efd = new PdfDictionary(doc);
        efd.Elements["/F"] = ef.Reference;
        efd.Elements["/UF"] = ef.Reference;
        spec.Elements["/EF"] = efd;
        doc.Internals.AddObject(spec);

        var names = catalog.Elements.GetDictionary("/Names") ?? new PdfDictionary(doc);
        var embedded = new PdfDictionary(doc);
        var arr = new PdfArray(doc);
        arr.Elements.Add(new PdfString(FileName));
        arr.Elements.Add(spec.Reference!);
        embedded.Elements["/Names"] = arr;
        names.Elements["/EmbeddedFiles"] = embedded;
        catalog.Elements["/Names"] = names;
        var af = new PdfArray(doc);
        af.Elements.Add(spec.Reference!);
        catalog.Elements["/AF"] = af;

        // ---- Farbprofil (OutputIntent) ----
        if (Srgb() is { } icc)
        {
            var profile = new PdfDictionary(doc);
            profile.Elements["/N"] = new PdfInteger(3);
            profile.CreateStream(icc);
            doc.Internals.AddObject(profile);
            var intent = new PdfDictionary(doc);
            intent.Elements["/Type"] = new PdfName("/OutputIntent");
            intent.Elements["/S"] = new PdfName("/GTS_PDFA1");
            intent.Elements["/OutputConditionIdentifier"] = new PdfString("sRGB IEC61966-2.1");
            intent.Elements["/Info"] = new PdfString("sRGB IEC61966-2.1");
            intent.Elements["/DestOutputProfile"] = profile.Reference;
            var intents = new PdfArray(doc);
            intents.Elements.Add(intent);
            catalog.Elements["/OutputIntents"] = intents;
        }

        // ---- XMP-Metadaten ----
        var meta = new PdfDictionary(doc);
        meta.Elements["/Type"] = new PdfName("/Metadata");
        meta.Elements["/Subtype"] = new PdfName("/XML");
        meta.CreateStream(Encoding.UTF8.GetBytes(Xmp(doc.Info.Title, producer, doc.Info.CreationDate, now, conformanceLevel)));
        doc.Internals.AddObject(meta);
        catalog.Elements["/Metadata"] = meta.Reference;

        using var output = new MemoryStream();
        doc.Save(output, false);
        // PDFsharp schreibt beim Speichern eigene XMP-Metadaten und hängt sie an den Katalog. Unsere (mit PDF/A-3-
        // und Factur-X-Kennung) stehen als eigenes Objekt in der Datei: den Verweis im Katalog darauf umbiegen,
        // ohne die Länge zu ändern (die Querverweistabelle bleibt gültig).
        return PointMetadataTo(output.ToArray(), meta.Reference!.ObjectNumber);
    }

    private static byte[] PointMetadataTo(byte[] pdf, int objectNumber)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var root = System.Text.RegularExpressions.Regex.Matches(text, @"/Root\s+(\d+)\s+0\s+R").LastOrDefault()
            ?? throw new InvalidOperationException("Katalog (/Root) nicht gefunden.");
        var start = System.Text.RegularExpressions.Regex.Matches(text, $@"(?<![0-9]){root.Groups[1].Value}\s+0\s+obj").LastOrDefault()
            ?? throw new InvalidOperationException("Katalogobjekt nicht gefunden.");
        var end = text.IndexOf("endobj", start.Index, StringComparison.Ordinal);
        var m = new System.Text.RegularExpressions.Regex(@"/Metadata\s*(\d+)\s+0\s+R").Match(text, start.Index, end - start.Index);
        if (!m.Success) throw new InvalidOperationException("Metadaten-Verweis im Katalog nicht gefunden.");
        var old = m.Groups[1].Value; var neu = objectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (neu.Length > old.Length) throw new InvalidOperationException("Objektnummer der Metadaten zu lang.");
        var bytes = (byte[])pdf.Clone();
        var repl = Encoding.Latin1.GetBytes(neu.PadLeft(old.Length, ' ')); // „ 9 0 R“ statt „12 0 R“ – Leerzeichen sind erlaubt
        Array.Copy(repl, 0, bytes, m.Groups[1].Index, repl.Length);
        return bytes;
    }

    /// <summary>Liest die eingebettete factur-x.xml (bzw. zugferd-invoice.xml) aus einem PDF – für Tests und die Vorschau.</summary>
    public static byte[]? Extract(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var names = doc.Internals.Catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/EmbeddedFiles")?.Elements.GetArray("/Names");
        if (names is null) return null;
        for (var i = 0; i + 1 < names.Elements.Count; i += 2)
        {
            var spec = (names.Elements[i + 1] as PdfReference)?.Value as PdfDictionary;
            var file = (spec?.Elements.GetDictionary("/EF")?.Elements["/F"] as PdfReference)?.Value as PdfDictionary;
            if (file?.Stream is null) continue;
            var n = names.Elements.GetString(i);
            if (n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                return file.Stream.UnfilteredValue;
        }
        return null;
    }

    private static string ProducerAfterSave(byte[] pdf)
    {
        try
        {
            using var input = new MemoryStream(pdf);
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            using var ms = new MemoryStream();
            doc.Save(ms, false);
            using var again = PdfReader.Open(new MemoryStream(ms.ToArray()), PdfDocumentOpenMode.Import);
            return again.Info.Producer ?? "";
        }
        catch (Exception) { return ""; }
    }

    private static byte[]? Srgb()
    {
        try
        {
            using var s = typeof(PdfDocument).Assembly.GetManifestResourceStream("PdfSharp.Resources.sRGB2014.icc");
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception) { return null; }
    }

    private static string X(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";
    private static string D(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    private static string Xmp(string? title, string producer, DateTime created, DateTime modified, string level) => $"""
<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
  <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
    <rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">
      <pdfaid:part>3</pdfaid:part>
      <pdfaid:conformance>B</pdfaid:conformance>
    </rdf:Description>
    <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
      <dc:title><rdf:Alt><rdf:li xml:lang="x-default">{X(title)}</rdf:li></rdf:Alt></dc:title>
    </rdf:Description>
    <rdf:Description rdf:about="" xmlns:pdf="http://ns.adobe.com/pdf/1.3/">
      <pdf:Producer>{X(producer)}</pdf:Producer>
    </rdf:Description>
    <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/">
      <xmp:CreatorTool>Belegpost</xmp:CreatorTool>
      <xmp:CreateDate>{D(created == default ? modified : created)}</xmp:CreateDate>
      <xmp:ModifyDate>{D(modified)}</xmp:ModifyDate>
    </rdf:Description>
    <rdf:Description rdf:about="" xmlns:fx="urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#">
      <fx:DocumentType>INVOICE</fx:DocumentType>
      <fx:DocumentFileName>{FileName}</fx:DocumentFileName>
      <fx:Version>1.0</fx:Version>
      <fx:ConformanceLevel>{X(level)}</fx:ConformanceLevel>
    </rdf:Description>
    <rdf:Description rdf:about="" xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/" xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#" xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#">
      <pdfaExtension:schemas>
        <rdf:Bag>
          <rdf:li rdf:parseType="Resource">
            <pdfaSchema:schema>Factur-X PDFA Extension Schema</pdfaSchema:schema>
            <pdfaSchema:namespaceURI>urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#</pdfaSchema:namespaceURI>
            <pdfaSchema:prefix>fx</pdfaSchema:prefix>
            <pdfaSchema:property>
              <rdf:Seq>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentFileName</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The name of the embedded XML document</pdfaProperty:description></rdf:li>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentType</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The type of the hybrid document in capital letters, e.g. INVOICE or ORDER</pdfaProperty:description></rdf:li>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>Version</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The actual version of the standard applying to the embedded XML document</pdfaProperty:description></rdf:li>
                <rdf:li rdf:parseType="Resource"><pdfaProperty:name>ConformanceLevel</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The conformance level of the embedded XML document</pdfaProperty:description></rdf:li>
              </rdf:Seq>
            </pdfaSchema:property>
          </rdf:li>
        </rdf:Bag>
      </pdfaExtension:schemas>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>
""";
}
