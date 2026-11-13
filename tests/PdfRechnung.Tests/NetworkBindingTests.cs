using System.Net;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public sealed class NetworkBindingTests
{
    [Fact]
    public void Leer_bedeutet_alle_Adressen()
    {
        Assert.Null(NetworkBinding.Resolve(new List<string>(), out var w));
        Assert.Empty(w);
    }

    [Fact]
    public void Ungueltige_oder_fehlende_Adressen_werden_uebersprungen_und_fallen_auf_alle_zurueck()
    {
        Assert.Null(NetworkBinding.Resolve(new[] { "keine-ip", "203.0.113.250" }, out var w));
        Assert.Contains(w, x => x.Contains("keine-ip"));
        Assert.Contains(w, x => x.Contains("203.0.113.250"));
    }

    [Fact]
    public void Vorhandene_Adresse_plus_localhost()
    {
        var ip = NetworkBinding.LocalAddresses().FirstOrDefault(a => IPAddress.Parse(a).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (ip is null) return; // Testumgebung ohne Netzwerkkarte
        var r = NetworkBinding.Resolve(new[] { ip }, out var w);
        Assert.NotNull(r);
        Assert.Contains(IPAddress.Loopback, r!);
        Assert.Contains(IPAddress.Parse(ip), r!);
        Assert.Empty(w);
    }
}
