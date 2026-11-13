using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BelegpostMelder;

/// <summary>
/// Öffnet die Mail eines Belegs als Entwurf im lokalen Outlook – mit Empfängern, Betreff, HTML-Text (CI-Vorlage),
/// eingebettetem Logo und allen Anhängen. Der Benutzer prüft und klickt selbst auf „Senden“.
///
/// Weg 1: klassisches Outlook per COM. Es werden nur Eigenschaften gesetzt (keine Adressen gelesen, kein Send),
///        deshalb erscheint keine Sicherheitsabfrage von Outlook.
/// Weg 2 (kein klassisches Outlook, z. B. „neues Outlook“): .eml-Entwurf (X-Unsent) mit dem Standard-Mailprogramm öffnen.
/// </summary>
internal static class OutlookBridge
{
    private const string PR_ATTACH_CONTENT_ID = "http://schemas.microsoft.com/mapi/proptag/0x3712001F";
    private const string PR_ATTACHMENT_HIDDEN = "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B";

    public sealed record Result(bool Ok, string Message, bool ViaOutlook);

    public static async Task<Result> OpenDraft(MelderConfig cfg, string documentId)
    {
        var token = cfg.Token ?? throw new InvalidOperationException("Melder ist nicht verbunden.");
        using var http = MelderClient.CreateHttp(cfg.Thumbprint, TimeSpan.FromSeconds(60));
        var baseUrl = cfg.Server!.TrimEnd('/') + "/melder/api/vorgang/" + Uri.EscapeDataString(documentId);

        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/mail");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return new Result(false, Error(body) ?? $"Server meldet {(int)res.StatusCode}.", false);

        using var doc = JsonDocument.Parse(body);
        var m = doc.RootElement;
        var folder = Path.Combine(Path.GetTempPath(), "Belegpost", documentId);
        Directory.CreateDirectory(folder);

        Result result;
        if (ClassicOutlookAvailable())
        {
            try
            {
                CreateViaCom(m, folder);
                result = new Result(true, "In Outlook geöffnet.", true);
            }
            catch (Exception ex)
            {
                result = await OpenEml(http, baseUrl, token, folder, documentId, "Outlook-Automatisierung nicht möglich (" + ex.Message + ") – ");
            }
        }
        else result = await OpenEml(http, baseUrl, token, folder, documentId, "");

        if (result.Ok)
        {
            // Portal: Vorgang „An Outlook übergeben“ (kein Autoversand mehr)
            try
            {
                using var mark = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/outlook");
                mark.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var _ = await http.SendAsync(mark);
            }
            catch { /* nicht kritisch */ }
        }
        Cleanup();
        return result;
    }

    private static bool ClassicOutlookAvailable() => Type.GetTypeFromProgID("Outlook.Application") is not null;

    private static void CreateViaCom(JsonElement m, string folder)
    {
        var type = Type.GetTypeFromProgID("Outlook.Application")!;
        dynamic? app = null, item = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            item = app.CreateItem(0); // olMailItem
            item.To = Join(m, "to");
            item.CC = Join(m, "cc");
            item.BCC = Join(m, "bcc");
            item.Subject = Str(m, "subject") ?? "";

            foreach (var a in m.GetProperty("attachments").EnumerateArray())
            {
                var name = Safe(Str(a, "name") ?? "Anhang");
                var path = Path.Combine(folder, name);
                File.WriteAllBytes(path, Convert.FromBase64String(Str(a, "data") ?? ""));
                dynamic att = item.Attachments.Add(path, 1 /* olByValue */, Type.Missing, name);
                var cid = Str(a, "contentId");
                if (!string.IsNullOrEmpty(cid))
                {
                    // Logo im HTML (cid:…) – als eingebettetes, verstecktes Bild
                    att.PropertyAccessor.SetProperty(PR_ATTACH_CONTENT_ID, cid);
                    att.PropertyAccessor.SetProperty(PR_ATTACHMENT_HIDDEN, true);
                }
                Marshal.FinalReleaseComObject(att);
            }

            var html = Str(m, "html");
            if (!string.IsNullOrEmpty(html)) item.HTMLBody = html;
            else item.Body = Str(m, "text") ?? "";

            item.Display(false);
            try { item.GetInspector.Activate(); } catch { }
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (app is not null) Marshal.FinalReleaseComObject(app);
        }
    }

    private static async Task<Result> OpenEml(HttpClient http, string baseUrl, string token, string folder, string id, string prefix)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/eml");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return new Result(false, prefix + (Error(await res.Content.ReadAsStringAsync()) ?? "Entwurf konnte nicht geladen werden."), false);
        var path = Path.Combine(folder, "Entwurf-" + Safe(id) + ".eml");
        await File.WriteAllBytesAsync(path, await res.Content.ReadAsByteArrayAsync());
        var outlook = FindOutlookExe();
        if (outlook is not null) Process.Start(new ProcessStartInfo(outlook, $"/eml \"{path}\"") { UseShellExecute = false });
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return new Result(true, prefix + "Entwurf im Mailprogramm geöffnet.", false);
    }

    /// <summary>Pfad zu outlook.exe (klassisch) aus der Registry „App Paths“.</summary>
    private static string? FindOutlookExe()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE");
            return key?.GetValue("") is string p && File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>Alte Entwurfsdateien (älter als 2 Tage) entfernen.</summary>
    private static void Cleanup()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "Belegpost");
            foreach (var d in Directory.EnumerateDirectories(root))
                if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-2)) Directory.Delete(d, true);
        }
        catch { }
    }

    private static string Join(JsonElement m, string name) =>
        m.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? string.Join("; ", a.EnumerateArray().Select(x => x.GetString())) : "";
    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? Error(string body)
    {
        try { using var d = JsonDocument.Parse(body); return Str(d.RootElement, "error"); } catch { return null; }
    }
    private static string Safe(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Length > 120 ? n[..120] : n;
    }
}
