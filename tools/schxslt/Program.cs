using Saxon.Api;
var proc = new Processor(false);
if (args[0] == "compile")
{
    // args: compile <pipeline.xsl> <in.sch> <out.xslt>
    var exe = proc.NewXsltCompiler().Compile(new Uri(Path.GetFullPath(args[1])));
    var t = exe.Load30();
    var doc = proc.NewDocumentBuilder().Build(new Uri(Path.GetFullPath(args[2])));
    var ser = proc.NewSerializer(File.Create(args[3]));
    t.ApplyTemplates(doc, ser);
    Console.WriteLine("ok " + args[3]);
    return;
}
var comp = proc.NewXsltCompiler();
var sw = System.Diagnostics.Stopwatch.StartNew();
var x0 = comp.Compile(new Uri(Path.GetFullPath(args[0])));
Console.WriteLine($"compile {sw.ElapsedMilliseconds} ms");
foreach (var f in args.Skip(1))
{
    sw.Restart();
    var t = x0.Load30();
    var doc = proc.NewDocumentBuilder().Build(new Uri(Path.GetFullPath(f)));
    var dest = new XdmDestination();
    t.ApplyTemplates(doc, dest);
    var x = System.Xml.Linq.XDocument.Parse(dest.XdmNode.OuterXml);
    System.Xml.Linq.XNamespace s = "http://purl.oclc.org/dsdl/svrl";
    var fails = x.Descendants(s + "failed-assert").ToList();
    Console.WriteLine($"{Path.GetFileName(f)}: {fails.Count} failed, {sw.ElapsedMilliseconds} ms");
    foreach (var fa in fails.Take(4)) Console.WriteLine($"  {fa.Attribute("id")?.Value} {fa.Attribute("flag")?.Value} {fa.Value.Trim()[..Math.Min(140, fa.Value.Trim().Length)]}");
}
