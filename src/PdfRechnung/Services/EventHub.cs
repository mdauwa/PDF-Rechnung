using System.Threading.Channels;

namespace PdfRechnung.Services;

/// <summary>Ereignis für einen Benutzer, z. B. „neuer Beleg von dir gedruckt“.</summary>
public sealed record PortalEvent(long Seq, DateTimeOffset At, string UserId, string Kind, string DocumentId, string Title, string Status, string? Text, bool Mail = true);

/// <summary>
/// Verteilt Ereignisse live an offene Portal-Tabs und an gekoppelte Melder (Server-Sent Events).
/// Die letzten Ereignisse werden kurz aufgehoben, damit nach einem Verbindungsabbruch nichts verloren geht.
/// </summary>
public sealed class EventHub
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    private readonly object _lock = new();
    private readonly List<PortalEvent> _recent = new();
    private readonly List<(string UserId, Channel<PortalEvent> Channel)> _subs = new();
    private long _seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // über Neustarts hinweg aufsteigend

    public PortalEvent Publish(string userId, string kind, string documentId, string title, string status, string? text = null, bool mail = true)
    {
        PortalEvent e;
        lock (_lock)
        {
            e = new PortalEvent(++_seq, DateTimeOffset.Now, userId, kind, documentId, title, status, text, mail);
            _recent.Add(e);
            _recent.RemoveAll(x => DateTimeOffset.Now - x.At > Keep);
            if (_recent.Count > 500) _recent.RemoveRange(0, _recent.Count - 500);
            foreach (var s in _subs.Where(s => s.UserId == userId)) s.Channel.Writer.TryWrite(e);
        }
        return e;
    }

    /// <summary>Abonnieren; mit <paramref name="after"/> werden verpasste Ereignisse (letzte 10 Minuten) nachgeliefert.</summary>
    public Subscription Subscribe(string userId, long? after)
    {
        var ch = Channel.CreateBounded<PortalEvent>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock)
        {
            if (after is { } a)
                foreach (var e in _recent.Where(x => x.UserId == userId && x.Seq > a)) ch.Writer.TryWrite(e);
            _subs.Add((userId, ch));
        }
        return new Subscription(this, userId, ch);
    }

    public int Subscribers(string userId) { lock (_lock) return _subs.Count(s => s.UserId == userId); }

    private void Remove(Channel<PortalEvent> ch)
    {
        lock (_lock) _subs.RemoveAll(s => s.Channel == ch);
    }

    public sealed class Subscription : IDisposable
    {
        private readonly EventHub _hub;
        private readonly Channel<PortalEvent> _ch;
        internal Subscription(EventHub hub, string userId, Channel<PortalEvent> ch) { _hub = hub; _ch = ch; UserId = userId; }
        public string UserId { get; }
        public ChannelReader<PortalEvent> Reader => _ch.Reader;
        public void Dispose() => _hub.Remove(_ch);
    }

    /// <summary>Ereignisse als text/event-stream schreiben, bis die Verbindung endet. Alle 25 s ein Lebenszeichen.</summary>
    public static async Task Stream(HttpContext ctx, Subscription sub, CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await ctx.Response.WriteAsync("retry: 5000\n: verbunden\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                if (await sub.Reader.WaitToReadAsync(wait.Token))
                    while (sub.Reader.TryRead(out var e))
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            seq = e.Seq, at = e.At, kind = e.Kind, id = e.DocumentId, title = e.Title, status = e.Status, text = e.Text, mail = e.Mail,
                            path = "/#/vorgang?id=" + Uri.EscapeDataString(e.DocumentId)
                        });
                        await ctx.Response.WriteAsync($"id: {e.Seq}\nevent: beleg\ndata: {json}\n\n", ct);
                    }
                else break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
}
