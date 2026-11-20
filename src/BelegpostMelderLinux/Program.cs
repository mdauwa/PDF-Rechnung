using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Threading;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace BelegpostMelder;

/// <summary>
/// Belegpost Melder für Linux. Aufruf:
///   belegpost-melder                 Fenster und Symbol im Infobereich
///   belegpost-melder --hintergrund   nur das Symbol (Autostart)
///   belegpost-melder --koppeln CODE  mit einem Code aus dem Portal verbinden
/// Läuft er schon, holt ein zweiter Aufruf nur das Fenster nach vorn.
/// </summary>
public static class Program
{
    private static string SocketPath => Path.Combine(Desktop.CacheFolder, "melder.sock");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version")) { Console.WriteLine(MelderInfo.Version); return 0; }
        var code = Arg(args, "--koppeln");
        if (code is not null)
        {
            if (PairingData.Parse(code) is not { } p) { Console.Error.WriteLine("Der Code ist nicht gültig."); return 2; }
            try
            {
                var (user, title, outlook) = MelderClient.Hello(p.Server, p.Token, p.Thumbprint).GetAwaiter().GetResult();
                var cfg = MelderConfig.Load();
                cfg.Server = p.Server; cfg.Token = p.Token; cfg.Thumbprint = p.Thumbprint;
                cfg.UserName = user; cfg.PortalTitle = title; cfg.OutlookAllowed = outlook; cfg.LastSeq = null;
                cfg.Save();
                try { if (!Desktop.Autostart) Desktop.Autostart = true; } catch { }
                Console.WriteLine($"Verbunden mit {p.Server} als {user}.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        // Zweiter Start: laufenden Melder bitten, sein Fenster zu zeigen
        if (TrySignalRunning()) return 0;
        MelderApp.StartHidden = args.Contains("--hintergrund");
        _ = Task.Run(Listen);
        try
        {
            return AppBuilder.Configure<MelderApp>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex) when (ex.Message.Contains("XOpenDisplay") || Environment.GetEnvironmentVariable("DISPLAY") is null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null)
        {
            Console.Error.WriteLine("Kein grafischer Desktop gefunden – der Melder braucht eine angemeldete Desktop-Sitzung (X11 oder Wayland mit XWayland).");
            return 3;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool TrySignalRunning()
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            s.Send(Encoding.UTF8.GetBytes("zeigen\n"));
            return true;
        }
        catch { return false; }
    }

    private static async Task Listen()
    {
        try
        {
            Directory.CreateDirectory(Desktop.CacheFolder);
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(SocketPath));
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            server.Listen(4);
            while (true)
            {
                using var c = await server.AcceptAsync();
                Dispatcher.UIThread.Post(() => MelderApp.Controller?.ShowWindow());
            }
        }
        catch { /* ohne Einzelinstanz weiter */ }
    }
}
