using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PdfRechnung.Data;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

/// <summary>
/// Status-Link /s/&lt;token&gt;: zufälliges Token (128 Bit) je ERP-Belegindex bzw. je Vorgang ohne Index.
/// Ohne Anmeldung abrufbar, aber nur aus freigegebenen IP-Bereichen. Zeigt keine vertraulichen Daten.
/// </summary>
public sealed class StatusLinkService
{
    private readonly BelegpostDb _db;
    private readonly DocumentStore _store;
    private readonly IOptionsMonitor<StatusLinkOptions> _opt;
    private readonly IOptionsMonitor<PortalOptions> _portal;
    private readonly IOptionsMonitor<ServerOptions> _server;

    public StatusLinkService(BelegpostDb db, DocumentStore store, IOptionsMonitor<StatusLinkOptions> opt,
        IOptionsMonitor<PortalOptions> portal, IOptionsMonitor<ServerOptions> server)
    {
        _db = db; _store = store; _opt = opt; _portal = portal; _server = server;
    }

    public static string KeyFor(Vorgang v) => v.ErpIndex is { Length: > 0 } b ? "belndx:" + b : "vorgang:" + v.Id;

    /// <summary>Token zum Vorgang (wird beim ersten Bedarf erzeugt).</summary>
    public string TokenFor(Vorgang v)
    {
        var key = KeyFor(v);
        using var c = _db.Open();
        if (BelegpostDb.Scalar(c, "SELECT token FROM status_link WHERE schluessel=@k AND widerrufen=0", ("@k", key)) is string t) return t;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        BelegpostDb.Exec(c, "INSERT INTO status_link(token, schluessel, erstellt) VALUES(@t,@k,@e) ON CONFLICT(schluessel) DO UPDATE SET token=@t, erstellt=@e, widerrufen=0",
            ("@t", token), ("@k", key), ("@e", BelegpostDb.Ms(DateTimeOffset.Now)));
        return token;
    }

    public string UrlFor(Vorgang v) => BaseUrl() + "/s/" + TokenFor(v);

    public string BaseUrl()
    {
        var u = _portal.CurrentValue.PublicUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(u)) return u;
        var s = _server.CurrentValue;
        return $"https://{CertificateProvider.HostNames(s).FirstOrDefault() ?? Environment.MachineName}:{s.HttpsPort}";
    }

    /// <summary>Neues Token erzeugen (alter Link wird ungültig).</summary>
    public string Renew(Vorgang v)
    {
        using (var c = _db.Open())
            BelegpostDb.Exec(c, "UPDATE status_link SET widerrufen=1 WHERE schluessel=@k", ("@k", KeyFor(v)));
        using (var c = _db.Open())
            BelegpostDb.Exec(c, "DELETE FROM status_link WHERE schluessel=@k", ("@k", KeyFor(v)));
        return TokenFor(v);
    }

    public void Revoke(Vorgang v)
    {
        using var c = _db.Open();
        BelegpostDb.Exec(c, "UPDATE status_link SET widerrufen=1 WHERE schluessel=@k", ("@k", KeyFor(v)));
    }

    /// <summary>Daten für die Anzeige – oder null (unbekannt, widerrufen, abgelaufen).</summary>
    public StatusView? Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 40) return null;
        string key;
        using (var c = _db.Open())
        {
            if (BelegpostDb.Scalar(c, "SELECT schluessel FROM status_link WHERE token=@t AND widerrufen=0", ("@t", token)) is not string k) return null;
            key = k;
            BelegpostDb.Exec(c, "UPDATE status_link SET letzter_abruf=@z WHERE token=@t", ("@z", BelegpostDb.Ms(DateTimeOffset.Now)), ("@t", token));
        }
        List<Vorgang> vorgaenge;
        List<VorgangEvent> events;
        if (key.StartsWith("belndx:", StringComparison.Ordinal))
        {
            var b = key["belndx:".Length..];
            vorgaenge = _store.ByErpIndex(b);
            events = _store.EventsForErpIndex(b);
        }
        else
        {
            var v = _store.Get(key["vorgang:".Length..]);
            vorgaenge = v is null ? new() : new() { v };
            events = v is null ? new() : _store.Events(v.Id);
        }
        if (vorgaenge.Count == 0) return null;
        var days = _opt.CurrentValue.ExpiryDays;
        var last = events.Count > 0 ? events[^1].At : vorgaenge[0].CreatedAt;
        if (days > 0 && last < DateTimeOffset.Now.AddDays(-days)) return null;
        var latest = vorgaenge[0];
        var current = events.LastOrDefault(e => StatusCode.IsStatus(e.Code))?.Code ?? latest.StatusCode;
        var show = _opt.CurrentValue.ShowVoucherNumber;
        // PDFs: je Vorgang (neueste zuerst) das Versand-PDF, sonst das gedruckte
        var pdfs = new List<StatusPdf>();
        if (_opt.CurrentValue.ShowPdf)
            foreach (var x in vorgaenge)
                if (PdfPath(x.Id) is not null)
                    pdfs.Add(new StatusPdf(pdfs.Count, x.Id, x.CreatedAt, $"{(show ? x.DocumentType : "Beleg")}{(show && x.VoucherId is { } nr ? " " + nr : "")}"));
        return new StatusView(
            show ? latest.DocumentType : "Beleg",
            show ? latest.VoucherId : null,
            current, StatusCode.Text(current), StatusCode.Light(current),
            events.Select(e => new StatusStep(e.At, e.Code, e.CodeText, PublicChannel(e.Channel))).ToList(), pdfs);
    }

    /// <summary>Versand-PDF des Vorgangs, sonst das gedruckte.</summary>
    public string? PdfPath(string vorgangId) =>
        _store.FilePath(vorgangId, DocumentStore.SendFile) ?? _store.FilePath(vorgangId, DocumentStore.PdfFile);

    /// <summary>Kanal nur grob (keine Server-/Ordnernamen).</summary>
    private static string? PublicChannel(string? c) => c switch
    {
        "Mail" => "E-Mail",
        "Outlook" => "Outlook",
        "ERP" => "ERP",
        _ => null
    };

    // ---------------- Zugriff nach IP ----------------

    public bool IsAllowed(HttpContext ctx)
    {
        var o = _opt.CurrentValue;
        if (!o.Enabled) return false;
        var ip = ClientAddress(ctx, o.TrustedProxies);
        return ip is not null && o.AllowedNetworks.Any(n => Network.TryParse(n, out var net) && net.Contains(ip));
    }

    public static IPAddress? ClientAddress(HttpContext ctx, IEnumerable<string> trustedProxies)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null) return null;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var proxies = trustedProxies.Select(p => Network.TryParse(p, out var n) ? n : null).Where(n => n is not null).ToList();
        if (proxies.Any(p => p!.Contains(remote)) && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff))
        {
            // von rechts: erste Adresse, die kein vertrauenswürdiger Proxy ist
            foreach (var part in xff.ToString().Split(',').Select(x => x.Trim()).Reverse())
            {
                if (!IPAddress.TryParse(part, out var a)) break;
                if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
                if (!proxies.Any(p => p!.Contains(a))) return a;
                remote = a;
            }
        }
        return remote;
    }

    // ---------------- Seite ----------------

    public static string RenderHtml(StatusView v, string title, string? token = null)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var color = v.Light switch { "ok" => "#1f8a4c", "fehler" => "#c0392b", "neutral" => "#7f8c8d", _ => "#d68910" };
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<meta name=\"robots\" content=\"noindex,nofollow\"><meta name=\"referrer\" content=\"no-referrer\">")
          .Append("<title>Belegstatus</title><style>")
          .Append("body{font-family:Segoe UI,system-ui,sans-serif;background:#f4f6f8;color:#1d2733;margin:0;padding:32px 16px}")
          .Append(".card{max-width:560px;margin:0 auto;background:#fff;border-radius:14px;box-shadow:0 4px 18px rgba(0,0,0,.08);padding:28px}")
          .Append("h1{font-size:15px;font-weight:600;color:#5d6b7a;margin:0 0 6px}h2{font-size:22px;margin:0 0 18px}")
          .Append(".st{display:flex;align-items:center;gap:10px;font-size:17px;font-weight:600;margin-bottom:22px}")
          .Append(".dot{width:14px;height:14px;border-radius:50%;flex:none}")
          .Append("ol{list-style:none;margin:0;padding:0;border-left:2px solid #dde3e9}li{position:relative;padding:0 0 16px 18px}")
          .Append("li:before{content:'';position:absolute;left:-6px;top:5px;width:10px;height:10px;border-radius:50%;background:#9aa8b6}")
          .Append("li:last-child:before{background:").Append(color).Append("}")
          .Append(".t{font-weight:600}.d{color:#5d6b7a;font-size:13px}.f{margin-top:18px;color:#8a97a5;font-size:12px}")
          .Append(".card.wide{max-width:980px}.pdf{margin:6px 0 22px}.pdf a{display:inline-block;margin:0 8px 8px 0;padding:7px 12px;border-radius:8px;border:1px solid #c9d3dd;color:#1d2733;text-decoration:none;font-size:14px}")
          .Append(".pdf a.p{background:#1f5fbf;border-color:#1f5fbf;color:#fff}.pdf .d{display:block;margin-bottom:6px}")
          .Append("iframe{width:100%;height:75vh;border:1px solid #dde3e9;border-radius:8px;margin-top:6px;background:#fff}")
          .Append("</style></head><body><div class=\"card").Append(v.Pdfs.Count > 0 && token is not null ? " wide" : "").Append("\">")
          .Append("<h1>").Append(H(title)).Append(" · Belegstatus</h1>")
          .Append("<h2>").Append(H(v.DocumentType)).Append(v.VoucherId is null ? "" : " " + H(v.VoucherId)).Append("</h2>")
          .Append("<div class=\"st\"><span class=\"dot\" style=\"background:").Append(color).Append("\"></span>").Append(H(v.StatusText)).Append("</div>");
        if (token is not null && v.Pdfs.Count > 0)
        {
            var t = WebUtility.UrlEncode(token);
            sb.Append("<div class=\"pdf\">");
            foreach (var p in v.Pdfs)
            {
                if (v.Pdfs.Count > 1) sb.Append("<span class=\"d\">").Append(H(p.Label)).Append(" vom ").Append(p.Created.ToString("dd.MM.yyyy HH:mm")).Append("</span>");
                sb.Append("<a class=\"p\" href=\"").Append(t).Append("/pdf/").Append(p.Nr).Append("\" target=\"_blank\" rel=\"noopener\">Beleg ansehen (PDF)</a>")
                  .Append("<a href=\"").Append(t).Append("/pdf/").Append(p.Nr).Append("?download=true\">Herunterladen</a>");
            }
            sb.Append("<iframe src=\"").Append(t).Append("/pdf/0#view=FitH\" title=\"Beleg\"></iframe></div>");
        }
        sb.Append("<ol>");
        foreach (var s in v.Steps)
            sb.Append("<li><div class=\"t\">").Append(H(s.Text)).Append(s.Channel is null ? "" : " (" + H(s.Channel) + ")")
              .Append("</div><div class=\"d\">").Append(s.At.ToString("dd.MM.yyyy HH:mm")).Append("</div></li>");
        sb.Append("</ol><div class=\"f\">Stand ").Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm")).Append("</div></div></body></html>");
        return sb.ToString();
    }

    public const string NotFoundHtml = "<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"robots\" content=\"noindex,nofollow\"><title>Nicht gefunden</title></head>"
        + "<body style=\"font-family:Segoe UI,system-ui,sans-serif;padding:40px;color:#444\">Diese Seite ist nicht verfügbar.</body></html>";

    public static void Map(WebApplication app)
    {
        static void Headers(HttpContext ctx)
        {
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Cache-Control"] = "no-store";
        }

        app.MapGet("/s/{token}", (string token, HttpContext ctx, StatusLinkService svc, IOptionsSnapshot<PortalOptions> portal, StatusLinkLimiter limiter) =>
        {
            Headers(ctx);
            var json = token.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (json) token = token[..^5];
            if (!svc.IsAllowed(ctx) || !limiter.Allow(ctx.Connection.RemoteIpAddress) || svc.Resolve(token) is not { } v)
                return json ? Results.NotFound(new { error = "nicht gefunden" }) : Results.Content(NotFoundHtml, "text/html; charset=utf-8", statusCode: 404);
            if (json)
                return Results.Ok(new
                {
                    belegart = v.DocumentType, belegnummer = v.VoucherId, status = v.Code, statusText = v.StatusText, ampel = v.Light,
                    verlauf = v.Steps.Select(s => new { zeit = s.At, code = s.Code, text = s.Text, kanal = s.Channel }),
                    pdf = v.Pdfs.Select(p => new { nr = p.Nr, bezeichnung = p.Label, erstellt = p.Created, url = $"{token}/pdf/{p.Nr}" })
                });
            return Results.Content(RenderHtml(v, portal.Value.Title, token), "text/html; charset=utf-8");
        });

        // Beleg als PDF (inline ansehen; ?download=1 zum Speichern)
        app.MapGet("/s/{token}/pdf/{nr:int}", (string token, int nr, bool? download, HttpContext ctx, StatusLinkService svc, StatusLinkLimiter limiter) =>
        {
            Headers(ctx);
            if (!svc.IsAllowed(ctx) || !limiter.Allow(ctx.Connection.RemoteIpAddress) || svc.Resolve(token) is not { } v
                || nr < 0 || nr >= v.Pdfs.Count || svc.PdfPath(v.Pdfs[nr].VorgangId) is not { } path)
                return Results.Content(NotFoundHtml, "text/html; charset=utf-8", statusCode: 404);
            var name = string.Concat(v.Pdfs[nr].Label.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim() + ".pdf";
            ctx.Response.Headers["Content-Disposition"] = $"{(download == true ? "attachment" : "inline")}; filename*=UTF-8''{Uri.EscapeDataString(name)}";
            return Results.File(path, "application/pdf");
        });
    }
}

public sealed record StatusStep(DateTimeOffset At, string Code, string Text, string? Channel);
public sealed record StatusPdf(int Nr, string VorgangId, DateTimeOffset Created, string Label);
public sealed record StatusView(string DocumentType, string? VoucherId, string? Code, string StatusText, string Light, List<StatusStep> Steps, List<StatusPdf>? PdfList = null)
{
    public List<StatusPdf> Pdfs => PdfList ?? new();
}

/// <summary>Einfaches Rate-Limit je IP für den Status-Link (60 Abrufe pro Minute).</summary>
public sealed class StatusLinkLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTime Window, int Count)> _hits = new();

    public bool Allow(IPAddress? ip)
    {
        var key = ip?.ToString() ?? "?";
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_hits.Count > 10_000) _hits.Clear();
            if (!_hits.TryGetValue(key, out var h) || now - h.Window > TimeSpan.FromMinutes(1)) h = (now, 0);
            h.Count++;
            _hits[key] = h;
            return h.Count <= 60;
        }
    }
}

/// <summary>IP-Bereich in CIDR-Schreibweise (IPv4/IPv6) oder einzelne Adresse.</summary>
public sealed class Network
{
    private readonly byte[] _prefix;
    private readonly int _bits;
    private readonly AddressFamily _family;

    private Network(IPAddress a, int bits)
    {
        _prefix = a.GetAddressBytes();
        _bits = bits;
        _family = a.AddressFamily;
    }

    public static bool TryParse(string? s, out Network network)
    {
        network = null!;
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        var slash = t.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? t : t[..slash], out var a)) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        var max = a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var bits = max;
        if (slash >= 0 && (!int.TryParse(t[(slash + 1)..], out bits) || bits < 0 || bits > max)) return false;
        network = new Network(a, bits);
        return true;
    }

    public bool Contains(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != _family) return false;
        var b = ip.GetAddressBytes();
        int full = _bits / 8, rest = _bits % 8;
        for (var i = 0; i < full; i++) if (b[i] != _prefix[i]) return false;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (b[full] & mask) == (_prefix[full] & mask);
    }
}
