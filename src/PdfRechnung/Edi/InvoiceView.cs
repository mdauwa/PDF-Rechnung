using System.Collections.Concurrent;
using System.Xml.Linq;
using Saxon.Api;

namespace PdfRechnung.Edi;

/// <summary>
/// Lesbare Rechnungsansicht für E-Rechnungen (XRechnung/EN 16931, UBL und CII, auch eingebettet im ZUGFeRD-PDF).
/// Verwendet unverändert die KoSIT XRechnung-Visualisierung (Apache 2.0, im Prüfpaket unter visualisierung\):
/// Schritt 1 UBL/CII → Zwischenformat, Schritt 2 Zwischenformat → HTML.
/// Rechtlich maßgeblich ist immer die XML – die Ansicht ist nur eine Darstellung und wird nicht archiviert.
/// </summary>
public static class InvoiceView
{
    public const string Notice = "Lesbare Darstellung der E-Rechnung (KoSIT XRechnung-Visualisierung). Maßgeblich und zu archivieren ist die XML-Datei – diese Ansicht ist keine Rechnung im Sinne der E-Rechnung.";

    private static readonly Processor Saxon = new(false);
    private static readonly ConcurrentDictionary<string, XsltExecutable> Cache = new();

    /// <summary>Kann für diese Datei eine Ansicht erzeugt werden? (UBL/CII-XML oder PDF mit eingebetteter Rechnung)</summary>
    public static byte[]? InvoiceXml(byte[] data)
    {
        if (EdiValidation.IsPdf(data)) return EdiValidation.EmbeddedInvoiceXml(data) is { } x && Kind(x) is not null ? x : null;
        return XmlInvoiceValidator.Looks(data) && Kind(data) is not null ? data : null;
    }

    /// <summary>cii, ubl-invoice, ubl-creditnote oder null.</summary>
    private static string? Kind(byte[] xml)
    {
        try
        {
            using var ms = new MemoryStream(xml);
            using var r = System.Xml.XmlReader.Create(ms, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            r.MoveToContent();
            return (r.NamespaceURI, r.LocalName) switch
            {
                (XmlInvoiceValidator.Cii, "CrossIndustryInvoice") => "cii",
                (XmlInvoiceValidator.UblInv, "Invoice") => "ubl-invoice",
                (XmlInvoiceValidator.UblCn, "CreditNote") => "ubl-creditnote",
                _ => null
            };
        }
        catch (Exception) { return null; }
    }

    /// <summary>HTML der Rechnungsansicht. Wirft bei nicht darstellbaren Dateien eine Ausnahme mit lesbarer Meldung.</summary>
    public static string Html(byte[] data, string lang = "de")
    {
        var xml = InvoiceXml(data) ?? throw new InvalidOperationException("Keine darstellbare E-Rechnung (erwartet XRechnung/EN 16931 als UBL oder CII, auch im ZUGFeRD-PDF).");
        var dir = Path.Combine(XmlInvoiceValidator.Root(), "visualisierung");
        var step1 = Path.Combine(dir, Kind(xml) switch { "cii" => "cii-xr.xsl", "ubl-invoice" => "ubl-invoice-xr.xsl", _ => "ubl-creditnote-xr.xsl" });
        XdmNode doc;
        using (var ms = new MemoryStream(xml))
        {
            var b = Saxon.NewDocumentBuilder();
            b.BaseUri = new Uri("file:///rechnung.xml");
            doc = b.Build(ms);
        }
        var t1 = Load(step1);
        t1.GlobalContextItem = doc;
        var mid = new XdmDestination();
        t1.ApplyTemplates(doc, mid);

        var t2 = Load(Path.Combine(dir, "xrechnung-html.xsl"));
        t2.SetStylesheetParameters(new Dictionary<QName, XdmValue> { [new QName("lang")] = new XdmAtomicValue(lang == "en" ? "en" : "de") });
        t2.GlobalContextItem = mid.XdmNode;
        using var sw = new StringWriter();
        var ser = Saxon.NewSerializer(sw);
        t2.ApplyTemplates(mid.XdmNode, ser);
        return AddNotice(sw.ToString());
    }

    private static Xslt30Transformer Load(string path) => Cache.GetOrAdd(path, p => Saxon.NewXsltCompiler().Compile(new Uri(p))).Load30();

    /// <summary>Hinweis oben in der Ansicht: maßgeblich ist die XML.</summary>
    private static string AddNotice(string html)
    {
        var banner = "<div style=\"font:13px Segoe UI,Arial,sans-serif;background:#fff4e5;color:#7a4b00;border-bottom:1px solid #f0c27b;padding:8px 14px\">"
            + Notice.Replace("&", "&amp;").Replace("<", "&lt;") + "</div>";
        var i = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return banner + html;
        var j = html.IndexOf('>', i);
        return j < 0 ? banner + html : html.Insert(j + 1, banner);
    }

    /// <summary>Antwort für den Browser: eigene, abgeschottete Seite (keine Cookies/Portal-Zugriffe aus der Ansicht heraus).</summary>
    public static IResult Result(HttpContext ctx, byte[] data)
    {
        string html;
        try { html = Html(data); }
        catch (Exception ex)
        {
            return Results.Content($"<!DOCTYPE html><meta charset=\"utf-8\"><title>Rechnungsansicht</title><p style=\"font-family:sans-serif\">Ansicht nicht möglich: {System.Net.WebUtility.HtmlEncode(ex.Message)}</p>",
                "text/html; charset=utf-8", statusCode: 422);
        }
        ctx.Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts allow-downloads allow-popups allow-modals; default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data: blob:; font-src data:; frame-ancestors 'self'";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
