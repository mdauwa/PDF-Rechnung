using PdfRechnung.Services;

namespace PdfRechnung.Edi;

/// <summary>Tools → Prüfen: Dateien hochladen oder Text einfügen und prüfen (EDIFACT, E-Rechnungs-XML, ZUGFeRD-PDF).</summary>
public static class ToolsApi
{
    public sealed record TextRequest(string? Text, string? Name);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/tools").AddEndpointFilter(async (ctx, next) =>
        {
            var a = ctx.HttpContext.RequestServices.GetRequiredService<PortalAccess>().For(ctx.HttpContext);
            return a.Authenticated ? await next(ctx) : Results.Json(new { error = "Bitte anmelden." }, statusCode: 401);
        });

        g.MapPost("/pruefen", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Datei fehlt." });
            var form = await req.ReadFormAsync();
            var list = new List<EdiReport>();
            foreach (var f in form.Files.Take(20))
            {
                if (f.Length > 50 * 1024 * 1024) { list.Add(TooBig(f.FileName, f.Length)); continue; }
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms);
                list.Add(WithView(EdiValidation.Validate(ms.ToArray(), f.FileName), ms.ToArray()));
            }
            return Results.Ok(list);
        }).DisableAntiforgery();

        g.MapPost("/pruefen-text", (TextRequest req) =>
        {
            var text = req.Text ?? "";
            if (text.Length == 0) return Results.BadRequest(new { error = "Kein Inhalt." });
            // Eingefügter EDIFACT-Text mit ISO-8859-Zeichensatz (UNOA–UNOC …) so kodieren, wie er als Datei käme
            var t = text.TrimStart();
            var latin = (t.StartsWith("UNA") || t.StartsWith("UNB")) && !t.Contains("UNOW") && !t.Contains("UNOY") && text.All(c => c <= '\u00FF');
            var bytes = latin ? System.Text.Encoding.Latin1.GetBytes(text) : System.Text.Encoding.UTF8.GetBytes(text);
            return Results.Ok(new[] { WithView(EdiValidation.Validate(bytes, string.IsNullOrWhiteSpace(req.Name) ? "eingefügt" : req.Name), bytes) });
        });

        // Lesbare Rechnungsansicht einer hier geprüften Datei (Kopie liegt höchstens 2 Stunden im Datenordner)
        g.MapGet("/ansicht/{token}", (string token, HttpContext ctx) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-f0-9]{32}$")) return Results.NotFound();
            var path = Path.Combine(ViewDir, token + ".bin");
            return File.Exists(path) ? InvoiceView.Result(ctx, File.ReadAllBytes(path)) : Results.NotFound(new { error = "Die Datei ist nicht mehr vorhanden – bitte erneut prüfen." });
        });

        g.MapPost("/bericht", (EdiReport r) => Results.Text(EdiReportText.Format(r), "text/plain; charset=utf-8"));
    }

    private static string ViewDir => Path.Combine(XmlInvoiceValidator.DataDirectory, "temp", "ansicht");

    /// <summary>Darstellbare E-Rechnung: Kopie für die Ansicht ablegen und die Adresse in den Bericht schreiben.</summary>
    private static EdiReport WithView(EdiReport r, byte[] data)
    {
        try
        {
            if (InvoiceView.InvoiceXml(data) is null) return r;
            Directory.CreateDirectory(ViewDir);
            foreach (var old in Directory.GetFiles(ViewDir).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-2))) try { File.Delete(old); } catch (IOException) { }
            var token = Guid.NewGuid().ToString("N");
            File.WriteAllBytes(Path.Combine(ViewDir, token + ".bin"), data);
            r.ViewUrl = $"api/tools/ansicht/{token}";
        }
        catch (Exception) { }
        return r;
    }

    private static EdiReport TooBig(string name, long size)
    {
        var r = new EdiReport { FileName = name, Size = size, Format = "unbekannt", Title = "zu groß" };
        r.Error("Datei größer als 50 MB.");
        return r;
    }
}
