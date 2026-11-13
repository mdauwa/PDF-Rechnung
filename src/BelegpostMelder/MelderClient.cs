using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace BelegpostMelder;

public sealed record BelegEvent(long Seq, string Id, string Title, string Status, string? Text, string Path, string Kind, bool Mail);

public enum MelderState { NotPaired, Connecting, Connected, Offline, Rejected }

/// <summary>
/// Hält die Verbindung zum Portal (Server-Sent Events) und meldet neue Belege.
/// Verbindungsabbrüche werden mit wachsender Wartezeit neu aufgebaut; verpasste Ereignisse liefert der Server nach.
/// </summary>
public sealed class MelderClient : IDisposable
{
    private readonly MelderConfig _cfg;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MelderClient(MelderConfig cfg) => _cfg = cfg;

    public event Action<MelderState, string>? StateChanged;
    public event Action<BelegEvent>? Received;
    /// <summary>Freigabe „über Outlook senden“ hat sich geändert.</summary>
    public event Action? PermissionsChanged;
    public MelderState State { get; private set; } = MelderState.NotPaired;

    public void Start()
    {
        Stop();
        if (!_cfg.Paired) { Set(MelderState.NotPaired, "Nicht verbunden – im Portal unter „Melder & Hinweise“ verbinden."); return; }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    public void Dispose() => Stop();

    private void Set(MelderState s, string text) { State = s; StateChanged?.Invoke(s, text); }

    /// <summary>HttpClient, der ein selbst signiertes Server-Zertifikat akzeptiert, wenn sein Fingerabdruck zur Kopplung passt.</summary>
    public static HttpClient CreateHttp(string? thumbprint, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || cert is not null && !string.IsNullOrEmpty(thumbprint)
                   && string.Equals(Convert.ToHexString(SHA256.HashData(cert.RawData)), thumbprint, StringComparison.OrdinalIgnoreCase),
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Belegpost-Melder/" + Application.ProductVersion.Split('+')[0]);
        return http;
    }

    /// <summary>Verbindung prüfen (beim Koppeln): liefert Benutzer und Portal-Titel oder wirft mit verständlicher Meldung.</summary>
    public static async Task<(string User, string Title, bool Outlook)> Hello(string server, string token, string? thumbprint)
    {
        using var http = CreateHttp(thumbprint, TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, server.TrimEnd('/') + "/melder/api/hallo");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req); }
        catch (HttpRequestException ex) { throw new InvalidOperationException($"Server {server} nicht erreichbar: {Inner(ex)}"); }
        catch (TaskCanceledException) { throw new InvalidOperationException($"Server {server} antwortet nicht."); }
        using (res)
        {
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Der Code ist nicht (mehr) gültig – im Portal bitte neu verbinden.");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return (doc.RootElement.GetProperty("user").GetString() ?? "", doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "Belegpost" : "Belegpost",
                doc.RootElement.TryGetProperty("outlook", out var o) && o.ValueKind == JsonValueKind.True);
        }
    }

    private static string Inner(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    private async Task Loop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            var token = _cfg.Token;
            if (token is null) { Set(MelderState.NotPaired, "Zugangsschlüssel fehlt – bitte neu verbinden."); return; }
            Set(MelderState.Connecting, "Verbinde mit " + _cfg.Server + " …");
            try
            {
                using var http = CreateHttp(_cfg.Thumbprint, Timeout.InfiniteTimeSpan);
                var url = _cfg.Server!.TrimEnd('/') + "/melder/api/ereignisse" + (_cfg.LastSeq is { } ls ? "?after=" + ls : "");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Set(MelderState.Rejected, "Dieser PC ist im Portal nicht mehr verbunden – bitte neu verbinden.");
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                Set(MelderState.Connected, "Wartet auf neue Belege – druckst du im ERP, erscheint der Beleg hier.");
                _ = SendSettings();
                delay = TimeSpan.FromSeconds(3);
                await ReadStream(await res.Content.ReadAsStreamAsync(ct), ct);
                Set(MelderState.Offline, "Verbindung beendet – verbinde neu …");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Set(MelderState.Offline, $"Keine Verbindung ({Inner(ex)}) – neuer Versuch in {delay.TotalSeconds:0} s");
            }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    /// <summary>Dem Server mitteilen, ob dieser PC Belege in Outlook öffnet (dann kein Autoversand für den Benutzer).</summary>
    public async Task SendSettings()
    {
        try
        {
            if (_cfg.Token is not { } token || _cfg.Server is null) return;
            using var http = CreateHttp(_cfg.Thumbprint, TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Server.TrimEnd('/') + "/melder/api/einstellungen")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { outlook = _cfg.OnNewDocument == "outlook" }), System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var allowed = doc.RootElement.TryGetProperty("outlookAllowed", out var a) && a.ValueKind == JsonValueKind.True;
            if (allowed != _cfg.OutlookAllowed) { _cfg.OutlookAllowed = allowed; try { _cfg.Save(); } catch { } PermissionsChanged?.Invoke(); }
        }
        catch { /* beim nächsten Verbinden erneut */ }
    }

    private async Task ReadStream(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        string? evName = null; var data = new System.Text.StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            // Lebenszeichen kommt alle 25 s; bleibt es 90 s aus, Verbindung neu aufbauen
            var lineTask = reader.ReadLineAsync(ct).AsTask();
            var done = await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(90), ct));
            if (done != lineTask) return;
            var line = await lineTask;
            if (line is null) return;
            if (line.Length == 0)
            {
                if (evName == "beleg" && data.Length > 0) Handle(data.ToString());
                evName = null; data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:")) evName = line[6..].Trim();
            else if (line.StartsWith("data:")) data.Append(line[5..].TrimStart());
        }
    }

    private void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var e = new BelegEvent(r.GetProperty("seq").GetInt64(), S("id"), S("title"), S("status"),
                r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null, S("path"), S("kind"),
                !r.TryGetProperty("mail", out var ml) || ml.ValueKind != JsonValueKind.False);
            _cfg.LastSeq = e.Seq;
            _cfg.LastDocumentPath = e.Path;
            _cfg.LastDocumentId = e.Id;
            try { _cfg.Save(); } catch { }
            Received?.Invoke(e);
        }
        catch { /* unbekanntes Format – ignorieren */ }
    }
}
