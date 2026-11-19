using PdfRechnung.Printing;
using Xunit;

namespace PdfRechnung.Tests;

public class GotomaxxProfileEditorTests
{
    private const string Cfg = "local_settings_filter 0000001000\r\npdfmailer\r\n{\r\n  defaultFileName \"${dateiName}\"\r\n  openPreview true\r\n}\r\ninvoice\r\n{\r\n  automaticSummations true\r\n}\r\n";

    [Fact]
    public void Trennzeichen_einfuegen_aendern_und_entfernen()
    {
        var a = GotomaxxProfileEditor.SetDelimiter(Cfg, "$$", out var c1);
        Assert.True(c1);
        Assert.Equal("local_settings_filter 0000001000\r\npdfmailer\r\n{\r\n  commandDelimiter $$\r\n  defaultFileName \"${dateiName}\"\r\n  openPreview true\r\n}\r\ninvoice\r\n{\r\n  automaticSummations true\r\n}\r\n", a);
        Assert.Equal("$$", GotomaxxProfile.DelimiterFromConfig(a));

        GotomaxxProfileEditor.SetDelimiter(a, "$$", out var same);
        Assert.False(same);

        var b = GotomaxxProfileEditor.SetDelimiter(a, "##", out _);
        Assert.Equal("##", GotomaxxProfile.DelimiterFromConfig(b));

        var back = GotomaxxProfileEditor.SetDelimiter(b, "", out var c3);
        Assert.True(c3);
        Assert.Equal(Cfg, back);
        GotomaxxProfileEditor.SetDelimiter(Cfg, "@@", out var none);
        Assert.False(none); // Standard und kein Eintrag: nichts zu tun
    }

    [Fact]
    public void Ohne_Datei_oder_Abschnitt_wird_er_angelegt()
    {
        var n = GotomaxxProfileEditor.SetDelimiter(null, "$$", out var c);
        Assert.True(c);
        Assert.Equal("$$", GotomaxxProfile.DelimiterFromConfig(n));
        Assert.StartsWith("pdfmailer", n);

        var noBlock = "local_settings_filter 1\nproxy\n{\n  autoConfig false\n}\n";
        var x = GotomaxxProfileEditor.SetDelimiter(noBlock, "$$", out _);
        Assert.Equal("local_settings_filter 1\npdfmailer\n{\n  commandDelimiter $$\n}\nproxy\n{\n  autoConfig false\n}\n", x);

        // commandDelimiter in einem anderen Abschnitt wird nicht angefasst
        var other = "spooling\n{\n  commandDelimiter xx\n}\npdfmailer\n{\n  save true\n}\n";
        var y = GotomaxxProfileEditor.SetDelimiter(other, "$$", out _);
        Assert.Contains("  commandDelimiter xx", y);
        Assert.Contains("pdfmailer\n{\n  commandDelimiter $$\n  save true", y);
        Assert.Null(GotomaxxProfile.DelimiterFromConfig(other)); // nur der Abschnitt pdfmailer zählt
        Assert.Equal("$$", GotomaxxProfile.DelimiterFromConfig(y));
    }
}
