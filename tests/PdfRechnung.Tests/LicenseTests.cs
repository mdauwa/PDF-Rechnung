using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Licensing;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public class LicenseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lic-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Active; public string[] Modules = Array.Empty<string>(); public bool Down;
        public List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r);
            if (Down) throw new HttpRequestException("keine Verbindung");
            if (r.RequestUri!.AbsolutePath.EndsWith("/registrations") && r.Headers.GetValues("X-Registration-Token").Single() != "reg-token")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"invalid_registration_token\"}", Encoding.UTF8, "application/json") });
            var mods = string.Join(",", Modules.Select(m => $"{{\"Id\":\"{Guid.NewGuid()}\",\"Code\":\"{m}\",\"Name\":\"{m}\",\"Enabled\":true}}"));
            var body = $"{{\"id\":\"11111111-1111-1111-1111-111111111111\",\"licenseCode\":\"L2-abc\",\"customerNumber\":{(Active ? "\"10001\"" : "null")},\"enabled\":{(Active ? "true" : "false")},\"active\":{(Active ? "true" : "false")},\"revision\":1,\"modules\":[{(Active ? mods : "")}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Registrierung_Testzeit_Module_und_Offlinefrist()
    {
        Directory.CreateDirectory(_dir);
        var settings = new SettingsStore(_dir);
        var now = DateTimeOffset.UtcNow;
        var server = new FakeServer();
        var old = LicenseService.RegistrationToken;
        LicenseService.RegistrationToken = "reg-token";
        try
        {
            var svc = new LicenseService(settings, NullLogger<LicenseService>.Instance, server, () => now, publishGlobally: false);
            Assert.Equal("test", svc.Gate.Mode);       // neue Installation: Testzeit
            Assert.True(svc.Gate.Base && svc.Gate.EInvoice && svc.Gate.Exchange);

            Assert.True(await svc.RefreshAsync(default)); // registriert, noch nicht freigeschaltet
            var reg = server.Requests[0];
            Assert.EndsWith("/registrations", reg.RequestUri!.AbsolutePath);
            Assert.Matches("^[0-9a-f]{64}$", reg.Headers.GetValues("X-Installation-Token").Single());
            Assert.Equal("test", svc.Gate.Mode);

            now = now.AddDays(31);                       // Testzeit vorbei
            await svc.RefreshAsync(default);
            Assert.False(svc.Gate.Base);
            Assert.EndsWith("/license", server.Requests[^1].RequestUri!.AbsolutePath);

            server.Active = true; server.Modules = new[] { License.ModuleEInvoice };
            await svc.RefreshAsync(default);
            Assert.Equal("lizenziert", svc.Gate.Mode);
            Assert.True(svc.Gate.Base); Assert.True(svc.Gate.EInvoice); Assert.False(svc.Gate.Exchange);

            // Neustart: Stand und Installation bleiben (verschlüsselt im Datenordner)
            var again = new LicenseService(settings, NullLogger<LicenseService>.Instance, server, () => now, publishGlobally: false);
            Assert.Equal("lizenziert", again.Gate.Mode);

            server.Down = true;                          // Server weg: 14 Tage weiter gültig
            now = now.AddDays(13);
            Assert.False(await again.RefreshAsync(default));
            Assert.True(again.Gate.Base);
            now = now.AddDays(2);
            await again.RefreshAsync(default);
            Assert.Equal("abgelaufen", again.Gate.Mode);
            Assert.False(again.Gate.Base);

            server.Down = false; server.Active = false;  // gesperrt: keine Testzeit mehr
            await again.RefreshAsync(default);
            Assert.Equal("ungueltig", again.Gate.Mode);
        }
        finally { LicenseService.RegistrationToken = old; }
    }

    [Fact]
    public void Falsches_Registrierungstoken_wird_gemeldet()
    {
        Directory.CreateDirectory(_dir);
        var old = LicenseService.RegistrationToken;
        LicenseService.RegistrationToken = "falsch";
        try
        {
            var svc = new LicenseService(new SettingsStore(_dir), NullLogger<LicenseService>.Instance, new FakeServer(), publishGlobally: false);
            Assert.False(svc.RefreshAsync(default).Result);
            Assert.Contains("Registrierungstoken", ((dynamic)svc.Status()).LastError as string ?? "");
        }
        finally { LicenseService.RegistrationToken = old; }
    }
}
