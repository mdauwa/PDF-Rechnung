using Microsoft.Extensions.Logging.Abstractions;
using PdfRechnung.Delivery;
using PdfRechnung.Settings;
using Xunit;
namespace PdfRechnung.Tests;
/// <summary>Nur mit FTP_TEST=1 und einem FTP-Server auf 127.0.0.1:2121 (Benutzer u/p, z. B. pyftpdlib).</summary>
public class FtpIntegrationTests
{
    [Fact]
    public async Task Ftp_hoch_und_abholen()
    {
        if (Environment.GetEnvironmentVariable("FTP_TEST") != "1") return;
        var dir = Path.Combine(Path.GetTempPath(), "ftpt-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        var settings = new SettingsStore(dir);
        var s = new ChannelSettings { Type = ChannelType.Ftp, Host = "127.0.0.1", Port = 2121, User = "u", Folder = "/out/kunde", UploadAsTemp = true };
        var where = await FileTransfer.UploadAsync(ChannelType.Ftp, s, "p", new[] { new DeliveryFile("R1.pdf", new byte[] { 1, 2, 3 }, "application/pdf"), new DeliveryFile("R1.edi", new byte[] { 9 }, "application/edifact") }, default);
        Assert.True(File.Exists("/tmp/ftproot/out/kunde/R1.pdf"));
        var opt = new DeliveryOptions { Channels = { new ChannelSettings { Type = ChannelType.Ftp, Host = "127.0.0.1", Port = 2121, User = "u", Password = settings.Secrets.Protect("p"),
            InboundEnabled = "ja", InboundFolder = "/out/kunde", InboundPattern = "*.edi", InboundTarget = Path.Combine(dir, "ziel"), InboundAfter = "verschieben", InboundArchive = "/archiv" } } };
        var inbound = new InboundService(new StaticOptions<DeliveryOptions>(opt), new CustomerStore(dir), settings, NullLogger<InboundService>.Instance);
        var job = Assert.Single(inbound.Jobs());
        var run = await inbound.RunAsync(job, default);
        Assert.True(run.Ok, run.Message);
        Assert.Equal(1, run.Files);
        Assert.True(File.Exists(Path.Combine(dir, "ziel", "R1.edi")));
        Assert.True(File.Exists("/tmp/ftproot/archiv/R1.edi"));
        Assert.True(File.Exists("/tmp/ftproot/out/kunde/R1.pdf"));
    }
}

/// <summary>Nur mit SFTP_TEST=1 und einem SFTP-Server auf 127.0.0.1:3373 (z. B. pip „sftpserver“, Wurzel = Startordner).</summary>
public class SftpIntegrationTests
{
    [Fact]
    public async Task Sftp_hoch_und_abholen()
    {
        if (Environment.GetEnvironmentVariable("SFTP_TEST") != "1") return;
        var dir = Path.Combine(Path.GetTempPath(), "sftpt-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        var settings = new SettingsStore(dir);
        var s = new ChannelSettings { Type = ChannelType.Sftp, Host = "127.0.0.1", Port = 3373, User = "u", Folder = "out/k1" };
        var where = await FileTransfer.UploadAsync(ChannelType.Sftp, s, "p", new[] { new DeliveryFile("R2.xml", new byte[] { 1, 2 }, "application/xml") }, default);
        Assert.Contains("SHA256:", where);
        var opt = new DeliveryOptions { Channels = { new ChannelSettings { Type = ChannelType.Sftp, Host = "127.0.0.1", Port = 3373, User = "u", Password = settings.Secrets.Protect("p"),
            InboundEnabled = "ja", InboundFolder = "out/k1", InboundTarget = Path.Combine(dir, "ziel"), InboundAfter = "loeschen" } } };
        var inbound = new InboundService(new StaticOptions<DeliveryOptions>(opt), new CustomerStore(dir), settings, NullLogger<InboundService>.Instance);
        var run = await inbound.RunAsync(Assert.Single(inbound.Jobs()), default);
        Assert.True(run.Ok, run.Message);
        Assert.True(File.Exists(Path.Combine(dir, "ziel", "R2.xml")));
    }
}
