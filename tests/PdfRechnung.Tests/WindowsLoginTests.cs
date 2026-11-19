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

    [Fact]
    public void Benutzer_wird_angelegt_oder_mit_vorhandenem_verknuepft()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var users = new UserStore(dir);
            users.Replace(new List<PortalUser> { new() { Id = "u1", Name = "Max", Email = "max@firma.at", IsAdmin = true } });
            // vorhandene E-Mail → kein neuer Benutzer, nur Windows-Konto ergänzt
            var (a, createdA) = users.Provision(@"FIRMA\mmax", "Max Muster", "max@firma.at", new[] { "max@firma.local" });
            Assert.False(createdA);
            Assert.Equal("u1", a.Id);
            Assert.Contains(@"FIRMA\mmax", a.PrintNames);
            // neu, ohne E-Mail (lokales Konto) → Anmeldename = Windows-Konto, nie Admin
            var (b, createdB) = users.Provision(@"SERVER\lisa", "Lisa Lokal", null, Array.Empty<string?>());
            Assert.True(createdB);
            Assert.False(b.IsAdmin);
            Assert.Equal("eigene", b.Visibility);
            Assert.Contains(@"SERVER\lisa", b.Logins);
            Assert.Equal(b.Id, users.FindByPrintName(@"SERVER\lisa")?.Id);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Ldap_Filter_wird_maskiert() => Assert.Equal(@"a\2a\28b\29\5c", UserDirectory.Escape(@"a*(b)\"));
}
