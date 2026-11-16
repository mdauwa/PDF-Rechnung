using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Saxon.Api;

namespace PdfRechnung.Edi;

/// <summary>
/// Prüft E-Rechnungs-XML (CII = ZUGFeRD/Factur-X/XRechnung CII, UBL = XRechnung UBL/Peppol):
/// Wohlgeformtheit, Schema (XSD) und die offiziellen Geschäftsregeln (EN 16931, XRechnung) als Schematron.
/// Die Prüfartefakte liegen eingebettet in Edi/Validierung.zip und werden beim ersten Gebrauch in den Datenordner entpackt.
/// Eigene Artefakte (z. B. aus dem FeRD-Paket) können unter &lt;Datenordner&gt;\validierung\eigene abgelegt werden:
///   eigene\&lt;profil&gt;\*.xsd (Hauptschema) und eigene\&lt;profil&gt;\*.xslt (Schematron als XSLT), profil = extended, basic, minimum, basicwl, en16931, xrechnung.
/// </summary>
public static class XmlInvoiceValidator
{
    public const string Cii = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    public const string Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    public const string UblInv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public const string UblCn = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    public const string Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string Svrl = "http://purl.oclc.org/dsdl/svrl";

    private static string? _root;
    private static readonly object Gate = new();
    private static readonly Processor Saxon = new(false);
    private static readonly ConcurrentDictionary<string, XsltExecutable> Xslt = new();
    private static readonly ConcurrentDictionary<string, XmlSchemaSet> Xsd = new();

    /// <summary>Ordner der Prüfartefakte; muss einmal gesetzt werden (Program.cs), sonst Temp-Ordner.</summary>
    public static string DataDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "belegpost");

    public static bool Looks(ReadOnlySpan<byte> data)
    {
        var s = System.Text.Encoding.UTF8.GetString(data[..Math.Min(data.Length, 4096)]);
        return s.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith('<');
    }

    public static string Root()
    {
        lock (Gate)
        {
            if (_root is not null) return _root;
            using var s = typeof(XmlInvoiceValidator).Assembly.GetManifestResourceStream("Edi.Validierung.zip")!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()))[..12];
            var dir = Path.Combine(DataDirectory, "validierung", "paket-" + hash);
            if (!File.Exists(Path.Combine(dir, "STAND.txt")))
            {
                var tmp = dir + ".tmp";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                ms.Position = 0;
                using (var zip = new ZipArchive(ms)) zip.ExtractToDirectory(tmp);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(tmp, dir);
            }
            Directory.CreateDirectory(Path.Combine(DataDirectory, "validierung", "eigene"));
            return _root = dir;
        }
    }

    public static string OwnDirectory => Path.Combine(DataDirectory, "validierung", "eigene");

    // ------------------------------------------------------------------ Prüfen

    public static EdiReport Validate(byte[] data, string? fileName = null, EdiReport? into = null)
    {
        var r = into ?? new EdiReport { Format = "XML", FileName = fileName, Size = data.Length };
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(data);
            doc = XDocument.Load(ms, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            r.AddAt(EdiReport.Fehler, $"Kein gültiges XML: {ex.Message}", ex.LineNumber);
            r.Checks.Add("Wohlgeformtheit");
            return r;
        }
        r.Checks.Add("Wohlgeformtheit");
        r.Content ??= Pretty(data);
        var root = doc.Root!;
        var ns = root.Name.NamespaceName;
        string syntax;
        string? spec;
        if (ns == Cii && root.Name.LocalName == "CrossIndustryInvoice")
        {
            syntax = "CII";
            spec = root.Descendants(XName.Get("GuidelineSpecifiedDocumentContextParameter", Ram)).Elements(XName.Get("ID", Ram)).FirstOrDefault()?.Value.Trim();
        }
        else if ((ns == UblInv && root.Name.LocalName == "Invoice") || (ns == UblCn && root.Name.LocalName == "CreditNote"))
        {
            syntax = "UBL";
            spec = root.Element(XName.Get("CustomizationID", Cbc))?.Value.Trim();
        }
        else if (Connect.ConnectValidator.Detect(r.FileName, doc) is not null)
        {
            r.Checks.Clear();
            return Connect.ConnectValidator.Validate(data, r.FileName, r);
        }
        else
        {
            r.Format = "XML";
            r.Title = $"XML <{root.Name.LocalName}>";
            r.Info($"Keine E-Rechnung (Wurzelelement {root.Name.LocalName}{(ns.Length > 0 ? $", Namensraum {ns}" : "")}) – nur Wohlgeformtheit geprüft.");
            return r;
        }
        if (r.Format is "XML" or "") r.Format = syntax;
        var profile = Profile(spec, syntax);
        r.Title = $"{profile.Name} ({syntax})";
        if (spec is null) r.Error(syntax == "CII" ? "Spezifikationskennung fehlt (GuidelineSpecifiedDocumentContextParameter/ID, BT-24)." : "Spezifikationskennung fehlt (cbc:CustomizationID, BT-24).");
        else r.Info($"Spezifikationskennung: {spec}");

        var pkg = Root();
        // ---- Schema ----
        var own = OwnFiles(profile.Key, "*.xsd");
        var xsd = own ?? (syntax == "CII"
            ? Path.Combine(pkg, "cii", "xsd", "uncefact", "data", "standard", "CrossIndustryInvoice_100pD16B.xsd")
            : Path.Combine(pkg, "ubl", "xsd", "maindoc", root.Name.LocalName == "Invoice" ? "UBL-Invoice-2.1.xsd" : "UBL-CreditNote-2.1.xsd"));
        var xsdErrorsAreWarnings = own is null && syntax == "CII" && profile.Key is "extended" or "basic" or "minimum" or "basicwl";
        SchemaCheck(data, xsd, r, xsdErrorsAreWarnings);
        r.Checks.Add(own is null ? (syntax == "CII" ? "Schema UN/CEFACT CII D16B" : "Schema UBL 2.1") : $"Schema (eigenes: {Path.GetFileName(xsd)})");
        if (xsdErrorsAreWarnings)
            r.Info($"Für {profile.Name} gilt das FeRD-Schema des Profils. Es ist noch nicht hinterlegt, daher wird gegen das EN-16931-Schema geprüft und Abweichungen nur als Warnung gemeldet (FeRD-Paket unter {OwnDirectory}\\{profile.Key} ablegen).");

        // ---- Regeln ----
        var rules = new List<(string File, string Name)>();
        if (profile.Key is "en16931" or "xrechnung" or "extended" or "peppol")
            rules.Add((Path.Combine(pkg, "en16931", syntax == "CII" ? "EN16931-CII-validation.xslt" : "EN16931-UBL-validation.xslt"), "Regeln EN 16931"));
        if (profile.Key == "xrechnung")
            rules.Add((Path.Combine(pkg, "xrechnung", syntax == "CII" ? "XRechnung-CII-validation.xslt" : "XRechnung-UBL-validation.xslt"), "Regeln XRechnung (KoSIT)"));
        foreach (var f in OwnAll(profile.Key, "*.xsl*")) rules.Add((f, $"eigene Regeln {Path.GetFileName(f)}"));
        if (profile.Key is "basic" or "minimum" or "basicwl" && rules.Count == 0)
            r.Info($"Für {profile.Name} sind keine Regeln hinterlegt (FeRD-Schematron unter {OwnDirectory}\\{profile.Key} ablegen) – nur Schema geprüft.");
        if (profile.Key == "extended" && OwnAll("extended", "*.xsl*").Count == 0)
            r.Info($"Extended: geprüft wird mit den EN-16931-Regeln. Die zusätzlichen FeRD-Regeln für Extended unter {OwnDirectory}\\extended ablegen.");
        if (profile.Key == "peppol") r.Info("Peppol BIS: die Peppol-eigenen Regeln sind nicht hinterlegt – geprüft wird mit den EN-16931-Regeln.");
        foreach (var (file, name) in rules)
        {
            try { SchematronCheck(data, file, r); r.Checks.Add(name); }
            catch (Exception ex) { r.Warn($"{name} konnten nicht ausgeführt werden: {ex.Message}"); }
        }
        return r;
    }

    public sealed record ProfileInfo(string Key, string Name);

    public static ProfileInfo Profile(string? spec, string syntax)
    {
        var s = (spec ?? "").ToLowerInvariant();
        if (s.Contains("xrechnung")) return new("xrechnung", s.Contains("extension") ? "XRechnung (Extension)" : "XRechnung");
        if (s.Contains("peppol")) return new("peppol", "Peppol BIS Billing 3.0");
        if (s.Contains("extended")) return new("extended", "ZUGFeRD/Factur-X Extended");
        if (s.Contains("basicwl")) return new("basicwl", "ZUGFeRD/Factur-X Basic WL");
        if (s.Contains(":basic")) return new("basic", "ZUGFeRD/Factur-X Basic");
        if (s.Contains("minimum")) return new("minimum", "ZUGFeRD/Factur-X Minimum");
        if (s.StartsWith("urn:cen.eu:en16931:2017")) return new("en16931", syntax == "CII" ? "ZUGFeRD/Factur-X EN 16931 (Comfort)" : "EN 16931");
        return new("en16931", syntax == "CII" ? "CII (unbekanntes Profil)" : "UBL (unbekanntes Profil)");
    }

    private static string? OwnFiles(string profile, string pattern) => OwnAll(profile, pattern).FirstOrDefault();

    private static List<string> OwnAll(string profile, string pattern)
    {
        var dir = Path.Combine(OwnDirectory, profile);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Order().ToList() : new();
    }

    private static void SchemaCheck(byte[] data, string xsdPath, EdiReport r, bool asWarning)
    {
        var set = Xsd.GetOrAdd(xsdPath, p =>
        {
            var s = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
            // Schemas, die ohne schemaLocation importiert werden (UBL: CCTS, XMLDSig, XAdES), vorab laden
            var ext = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(p))!, "extern");
            if (Directory.Exists(ext))
                foreach (var f in new[] { "xmldsig-core-schema.xsd", "XAdES01903v132-201601.xsd", "XAdES01903v141-201601.xsd", "CCTS_CCT_SchemaModule.xsd" }.Select(x => Path.Combine(ext, x)).Where(File.Exists))
                    using (var xr = XmlReader.Create(f, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore })) s.Add(null, xr);
            using (var reader = XmlReader.Create(p, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
                s.Add(null, reader);
            s.Compile();
            return s;
        });
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = set, DtdProcessing = DtdProcessing.Prohibit };
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
        var count = 0;
        settings.ValidationEventHandler += (_, e) =>
        {
            if (count++ >= 100) return;
            var level = e.Severity == XmlSeverityType.Warning || asWarning ? EdiReport.Warnung : EdiReport.Fehler;
            r.AddAt(level, "Schema: " + e.Message, e.Exception?.LineNumber, e.Exception is { LinePosition: > 0 } x ? $"Spalte {x.LinePosition}" : null);
        };
        using var ms = new MemoryStream(data);
        using var rd = XmlReader.Create(ms, settings);
        while (rd.Read()) { }
        if (count > 100) r.Warn($"… insgesamt {count} Schema-Meldungen.");
    }

    private static void SchematronCheck(byte[] data, string xsltPath, EdiReport r)
    {
        var exe = Xslt.GetOrAdd(xsltPath, p => Saxon.NewXsltCompiler().Compile(new Uri(p)));
        var t = exe.Load30();
        XdmNode doc;
        using (var ms = new MemoryStream(data))
        {
            var b = Saxon.NewDocumentBuilder();
            b.BaseUri = new Uri("file:///beleg.xml");
            doc = b.Build(ms);
        }
        t.GlobalContextItem = doc;
        var dest = new XdmDestination();
        t.ApplyTemplates(doc, dest);
        var svrl = XDocument.Parse(dest.XdmNode.OuterXml);
        var lines = LineIndex(data);
        foreach (var fa in svrl.Descendants(XName.Get("failed-assert", Svrl)).Concat(svrl.Descendants(XName.Get("successful-report", Svrl))))
        {
            var flag = fa.Attribute("flag")?.Value ?? fa.Attribute("role")?.Value ?? "fatal";
            var id = fa.Attribute("id")?.Value;
            var loc = fa.Attribute("location")?.Value;
            var text = string.Join(" ", fa.Elements(XName.Get("text", Svrl)).Select(x => x.Value.Trim()));
            if (text.Length == 0) text = fa.Value.Trim();
            if (id is not null && !text.Contains(id)) text = $"[{id}] {text}";
            var level = flag.Equals("warning", StringComparison.OrdinalIgnoreCase) ? EdiReport.Warnung
                : flag.Equals("information", StringComparison.OrdinalIgnoreCase) ? EdiReport.Hinweis : EdiReport.Fehler;
            r.AddAt(level, text, Line(lines, loc), Short(loc));
        }
    }

    /// <summary>XPath aus dem SVRL (…/*:Element[namespace-uri()=…][1]/…) grob einer Zeile zuordnen.</summary>
    private static int? Line(XDocument? doc, string? loc)
    {
        if (doc is null || string.IsNullOrEmpty(loc)) return null;
        try
        {
            XElement? cur = null;
            loc = System.Text.RegularExpressions.Regex.Replace(loc, @"Q\{[^}]*\}", "");
            foreach (var step in loc.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = System.Text.RegularExpressions.Regex.Match(step, @"^\*:([\w.-]+)(?:\[namespace-uri\(\)='[^']*'\])?(?:\[(\d+)\])?$");
                if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(step, @"^(?:\w+:)?([\w.-]+)(?:\[(\d+)\])?$");
                if (!m.Success) break;
                var name = m.Groups[1].Value;
                var idx = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                var pool = cur is null ? new[] { doc.Root! } : cur.Elements();
                var next = pool.Where(e => e.Name.LocalName == name).Skip(idx - 1).FirstOrDefault();
                if (next is null) break;
                cur = next;
            }
            return cur is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : null;
        }
        catch { return null; }
    }

    private static XDocument? LineIndex(byte[] data)
    {
        try { using var ms = new MemoryStream(data); return XDocument.Load(ms, LoadOptions.SetLineInfo); } catch { return null; }
    }

    private static string? Short(string? loc)
    {
        if (loc is null) return null;
        var s = System.Text.RegularExpressions.Regex.Replace(loc, @"\[namespace-uri\(\)='[^']*'\]|Q\{[^}]*\}", "").Replace("*:", "").Replace("[1]", "");
        return s.Length > 120 ? "…" + s[^117..] : s;
    }

    private static string Pretty(byte[] data)
    {
        var t = System.Text.Encoding.UTF8.GetString(data).TrimStart('﻿');
        return t.Length > 400_000 ? t[..400_000] + "\n…" : t;
    }
}
