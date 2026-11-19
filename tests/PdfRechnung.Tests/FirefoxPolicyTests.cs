using System.Text.Json.Nodes;
using PdfRechnung.Settings;
using Xunit;

namespace PdfRechnung.Tests;

public class FirefoxPolicyTests
{
    [Fact]
    public void Portal_Adressen_aus_Servername_Domaene_und_oeffentlicher_Adresse()
    {
        var s = new ServerOptions { HttpsPort = 5444, AdditionalHostNames = new[] { "belegpost.firma.at" } };
        var o = FirefoxPolicy.PortalOrigins(s, new PortalOptions { PublicUrl = "https://Belegpost.firma.at:5444/" }, "MARTIN-VM40", "firma.local");
        Assert.Equal(new[] { "https://martin-vm40:5444", "https://martin-vm40.firma.local:5444", "https://belegpost.firma.at:5444" }, o);
        Assert.True(FirefoxPolicy.HasNonFqdn(o));
        Assert.False(FirefoxPolicy.HasNonFqdn(o.Skip(1)));
    }

    [Fact]
    public void Policies_json_wird_ergaenzt_ohne_Fremdes_zu_aendern()
    {
        const string existing = """{ "policies": { "DisableTelemetry": true, "Authentication": { "NTLM": ["intranet.firma.at"] } } }""";
        var text = FirefoxPolicy.MergeJson(existing, new[] { "https://srv:5444" }, Array.Empty<string>(), nonFqdn: true, enterpriseRoots: true, out var changed);
        Assert.True(changed);
        var p = JsonNode.Parse(text)!["policies"]!;
        Assert.True(p["DisableTelemetry"]!.GetValue<bool>());
        Assert.Equal(new[] { "intranet.firma.at", "https://srv:5444" }, p["Authentication"]!["NTLM"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { "https://srv:5444" }, p["Authentication"]!["SPNEGO"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(p["Authentication"]!["AllowNonFQDN"]!["NTLM"]!.GetValue<bool>());
        Assert.True(p["Certificates"]!["ImportEnterpriseRoots"]!.GetValue<bool>());

        // zweites Mal: nichts zu tun
        FirefoxPolicy.MergeJson(text, new[] { "https://srv:5444" }, new[] { "https://srv:5444" }, true, true, out var again);
        Assert.False(again);

        // Adresse geändert: alte eigene raus, fremde bleibt
        var t2 = FirefoxPolicy.MergeJson(text, new[] { "https://srv.firma.local:5444" }, new[] { "https://srv:5444" }, false, true, out _);
        var ntlm = JsonNode.Parse(t2)!["policies"]!["Authentication"]!["NTLM"]!.AsArray().Select(x => x!.GetValue<string>());
        Assert.Equal(new[] { "intranet.firma.at", "https://srv.firma.local:5444" }, ntlm);

        // abgeschaltet: eigene Einträge weg, fremde bleiben
        var t3 = FirefoxPolicy.MergeJson(t2, Array.Empty<string>(), new[] { "https://srv.firma.local:5444" }, false, true, out _);
        var a3 = JsonNode.Parse(t3)!["policies"]!["Authentication"]!;
        Assert.Null(a3["SPNEGO"]);
        Assert.Equal(new[] { "intranet.firma.at" }, a3["NTLM"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void Neue_policies_json_und_ungueltige_Datei()
    {
        var text = FirefoxPolicy.MergeJson(null, new[] { "https://srv.firma.local:5444" }, Array.Empty<string>(), false, true, out var changed);
        Assert.True(changed);
        Assert.NotNull(JsonNode.Parse(text)!["policies"]!["Authentication"]!["SPNEGO"]);
        Assert.Throws<InvalidOperationException>(() => FirefoxPolicy.MergeJson("{ kaputt", new[] { "x" }, Array.Empty<string>(), false, true, out _));
    }
}
