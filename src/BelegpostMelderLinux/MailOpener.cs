using System.Net.Http.Headers;
using System.Text.Json;

namespace BelegpostMelder;

/// <summary>
/// E-Mail eines Belegs öffnen. Unter Linux gibt es kein klassisches Outlook:
/// „Outlook im Web“ (und „Outlook“) → Belegpost legt den Entwurf über Microsoft 365 an, der Browser öffnet ihn;
/// „Standard-Mailprogramm“ → xdg-email mit Empfängern, Betreff, Text und allen Anlagen (Thunderbird, Evolution, KMail …).
/// </summary>
public static class MailOpener
{
    public sealed record Result(bool Ok, string Message);

    public static async Task<Result> Open(MelderConfig cfg, string documentId)
    {
        var token = cfg.Token ?? throw new InvalidOperationException("Melder ist nicht verbunden.");
        using var http = MelderClient.CreateHttp(cfg.Thumbprint, TimeSpan.FromSeconds(90));
        var baseUrl = cfg.Server!.TrimEnd('/') + "/melder/api/vorgang/" + Uri.EscapeDataString(documentId);

        if (cfg.MailClient != "standard")
        {
            var web = await Web(http, token, baseUrl);
            // „Outlook“ gibt es unter Linux nicht – klappt Outlook im Web nicht (z. B. ohne Microsoft 365), das Mailprogramm nehmen
            if (web.Ok || cfg.MailClient == "outlookweb") return web;
            var std = await Standard(http, token, baseUrl, documentId);
            return std.Ok ? std : new Result(false, web.Message + " / " + std.Message);
        }
        return await Standard(http, token, baseUrl, documentId);
    }

    private static async Task<Result> Web(HttpClient http, string token, string baseUrl)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/webentwurf") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return new Result(false, Error(body) ?? $"Server meldet {(int)res.StatusCode}.");
        using var doc = JsonDocument.Parse(body);
        var url = Str(doc.RootElement, "url");
        if (string.IsNullOrEmpty(url)) return new Result(false, "Der Server hat keinen Link zum Entwurf geliefert.");
        return Desktop.Open(url) ? new Result(true, "Entwurf in Outlook im Web geöffnet.") : new Result(false, "Der Browser konnte nicht geöffnet werden (xdg-open).");
    }

    private static async Task<Result> Standard(HttpClient http, string token, string baseUrl, string id)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/mail");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return new Result(false, Error(body) ?? $"Server meldet {(int)res.StatusCode}.");
        using var doc = JsonDocument.Parse(body);
        var m = doc.RootElement;
        var folder = Path.Combine(Desktop.CacheFolder, Safe(id));
        Directory.CreateDirectory(folder);
        Cleanup();

        var args = new List<string> { "--utf8", "--subject", Str(m, "subject") ?? "", "--body", Str(m, "text") ?? "" };
        foreach (var (key, flag) in new[] { ("cc", "--cc"), ("bcc", "--bcc") })
            foreach (var a in List(m, key)) { args.Add(flag); args.Add(a); }
        foreach (var a in m.GetProperty("attachments").EnumerateArray())
        {
            if (!string.IsNullOrEmpty(Str(a, "contentId"))) continue; // eingebettetes Logo: nur in HTML sinnvoll
            var path = Path.Combine(folder, Safe(Str(a, "name") ?? "Anhang"));
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(Str(a, "data") ?? ""));
            args.Add("--attach"); args.Add(path);
        }
        var to = List(m, "to").ToList();
        if (to.Count == 0) to.Add("mailto:"); // xdg-email braucht einen Empfänger oder mailto:
        args.AddRange(to);

        var (code, err) = await Task.Run(() => Desktop.Run("xdg-email", args, 15_000));
        if (code != 0)
            return new Result(false, code == -1 ? "xdg-email fehlt (Paket xdg-utils installieren)." : $"Das Mailprogramm konnte nicht geöffnet werden{(err.Length > 0 ? ": " + err : ".")}");

        // Portal: Vorgang „An Outlook übergeben“ (kein Autoversand mehr), wie beim Windows-Melder
        try
        {
            using var mark = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/outlook") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            mark.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var _ = await http.SendAsync(mark);
        }
        catch { }
        return new Result(true, "Im Mailprogramm geöffnet.");
    }

    private static void Cleanup()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Desktop.CacheFolder))
                if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-2)) Directory.Delete(d, true);
        }
        catch { }
    }

    private static IEnumerable<string> List(JsonElement m, string n) =>
        m.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0) : Array.Empty<string>();
    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? Error(string body)
    {
        try { using var d = JsonDocument.Parse(body); return Str(d.RootElement, "error"); } catch { return null; }
    }
    private static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Append('\\')) name = name.Replace(c, '_');
        return name.Length == 0 ? "Anhang" : name;
    }
}
