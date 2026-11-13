using System.Net;
using System.Text.Json.Nodes;
using PdfRechnung.Documents;
using Xunit;

namespace PdfRechnung.Tests;

/// <summary>Versand über Microsoft Graph gegen einen nachgebauten Graph-Endpunkt.</summary>
[Collection("Graph")]
public sealed class GraphMailTests : IDisposable
{
    private sealed class FakeGraph : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string Body, string? Range)> Calls { get; } = new();
        public HttpStatusCode SendStatus { get; set; } = HttpStatusCode.Accepted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();
            var body = r.Content is null ? "" : r.Method == HttpMethod.Put ? $"<{(await r.Content.ReadAsByteArrayAsync(ct)).Length} Bytes>" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r.Method, url, r.Headers.Authorization?.ToString(), body, r.Content?.Headers.ContentRange?.ToString()));
            HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
                new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

            if (url.Contains("/oauth2/v2.0/token")) return Json("""{"access_token":"tok123","expires_in":3600}""");
            if (url.EndsWith("/sendMail"))
                return SendStatus == HttpStatusCode.Accepted ? new HttpResponseMessage(HttpStatusCode.Accepted)
                    : Json("""{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""", SendStatus);
            if (url.EndsWith("/messages")) return Json("""{"id":"MSG1"}""", HttpStatusCode.Created);
            if (url.EndsWith("/createUploadSession")) return Json("""{"uploadUrl":"https://upload.example/session1"}""");
            if (url.StartsWith("https://upload.example")) return new HttpResponseMessage(HttpStatusCode.OK);
            if (url.EndsWith("/attachments")) return Json("{}", HttpStatusCode.Created);
            if (url.EndsWith("/send")) return new HttpResponseMessage(HttpStatusCode.Accepted);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private readonly HttpClient _old = GraphMailSender.Http;
    private readonly FakeGraph _fake = new();

    public GraphMailTests() => GraphMailSender.Http = new HttpClient(_fake);
    public void Dispose() => GraphMailSender.Http = _old;

    private static MailOptions Options() => new()
    {
        Enabled = true, Transport = "Graph", GraphTenantId = "tenant-" + Guid.NewGuid().ToString("N")[..6], GraphClientId = "client",
        FromAddress = "rechnung@firma.at", FromName = "Firma GmbH", CopyTo = "archiv@firma.at"
    };

    private static OutgoingMail Mail(int attachmentBytes)
    {
        var m = new OutgoingMail { Subject = "Rechnung 4711", Body = "Anbei die Rechnung." };
        m.To.Add("kunde@example.at"); m.Cc.Add("cc@example.at");
        m.Attachments.Add(("Rechnung_4711.pdf", new byte[attachmentBytes], "application/pdf"));
        return m;
    }

    [Fact]
    public async Task Kleine_Mail_geht_per_sendMail_im_Namen_des_Absenders()
    {
        await MailSender.SendAsync(Options(), new MailSecrets(null, "geheim"), Mail(50_000), CancellationToken.None);

        Assert.Contains(_fake.Calls, c => c.Url.Contains("/oauth2/v2.0/token") && c.Body.Contains("client_credentials") && c.Body.Contains("graph.microsoft.com%2F.default"));
        var send = _fake.Calls.Single(c => c.Url.EndsWith("/sendMail"));
        Assert.Equal("https://graph.microsoft.com/v1.0/users/rechnung%40firma.at/sendMail", send.Url);
        Assert.Equal("Bearer tok123", send.Auth);
        var msg = JsonNode.Parse(send.Body)!["message"]!;
        Assert.Equal("Rechnung 4711", msg["subject"]!.GetValue<string>());
        Assert.Equal("kunde@example.at", msg["toRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>());
        Assert.Equal("archiv@firma.at", msg["bccRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>());
        Assert.Equal("Rechnung_4711.pdf", msg["attachments"]![0]!["name"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(send.Body)!["saveToSentItems"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Grosse_Anlage_wird_in_Teilen_hochgeladen()
    {
        await MailSender.SendAsync(Options(), new MailSecrets(null, "geheim"), Mail(7_000_000), CancellationToken.None);

        Assert.DoesNotContain(_fake.Calls, c => c.Url.EndsWith("/sendMail"));
        Assert.Contains(_fake.Calls, c => c.Url.EndsWith("/users/rechnung%40firma.at/messages"));
        Assert.Contains(_fake.Calls, c => c.Url.EndsWith("/messages/MSG1/attachments/createUploadSession"));
        var puts = _fake.Calls.Where(c => c.Method == HttpMethod.Put).ToList();
        Assert.Equal(3, puts.Count); // 7 MB in Blöcken zu 3,125 MiB
        Assert.All(puts, p => Assert.Null(p.Auth)); // Upload-URL ist vorsigniert
        Assert.Equal("bytes 0-3276799/7000000", puts[0].Range);
        Assert.Equal("bytes 6553600-6999999/7000000", puts[2].Range);
        Assert.Contains(_fake.Calls, c => c.Url.EndsWith("/messages/MSG1/send"));
    }

    [Fact]
    public async Task Fehlende_Berechtigung_wird_verstaendlich_gemeldet()
    {
        _fake.SendStatus = HttpStatusCode.Forbidden;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MailSender.SendAsync(Options(), new MailSecrets(null, "geheim"), Mail(1000), CancellationToken.None));
        Assert.Contains("Mail.Send", ex.Message);
    }

    [Fact]
    public async Task Ohne_Schluessel_kommt_klare_Meldung()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MailSender.SendAsync(Options(), new MailSecrets(null, null), Mail(1000), CancellationToken.None));
        Assert.Contains("nicht vollständig eingerichtet", ex.Message);
        Assert.Empty(_fake.Calls);
    }
}

[CollectionDefinition("Graph", DisableParallelization = true)]
public sealed class GraphCollection;
