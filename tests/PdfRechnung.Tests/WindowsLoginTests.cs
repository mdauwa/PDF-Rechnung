using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public class WindowsLoginTests
{
    [Theory]
    [InlineData(@"FIRMA\max", "", "max", "FIRMA", @"FIRMA\max")]
    [InlineData("max@firma.at", "FIRMA", "max@firma.at", null, "max@firma.at")]
    [InlineData("max", "FIRMA", "max", "FIRMA", @"FIRMA\max")]
    public void Benutzername_wird_fuer_die_AD_Pruefung_zerlegt(string input, string domain, string user, string? dom, string account)
    {
        var r = AdLogin.Split(input, domain);
        Assert.Equal((user, dom, account), (r.User, r.Domain, r.Account));
    }

    [Fact]
    public void Windows_Konto_findet_den_Benutzer_ueber_Druckbenutzer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var users = new UserStore(dir);
            users.Replace(new List<PortalUser> { new() { Id = "u1", Name = "Max", Email = "max.muster@firma.at", PrintNames = { "mmuster" } } });
            Assert.Equal("u1", users.FindByPrintName(@"FIRMA\mmuster")?.Id);
            Assert.Equal("u1", users.FindByPrintName(@"FIRMA\max.muster")?.Id); // Teil vor dem @ der E-Mail
            Assert.Null(users.FindByPrintName(@"FIRMA\jemand"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
