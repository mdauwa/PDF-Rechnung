using System.Diagnostics;
using System.Net.Sockets;

namespace PdfRechnung.Settings;

/// <summary>Laufzeit-Infos und Neustart/Beenden – je nachdem, ob als Dienst oder im Einrichtungsmodus (Doppelklick) gestartet.</summary>
public sealed class RuntimeControl
{
    private readonly IHostApplicationLifetime _lifetime;

    public RuntimeControl(IHostApplicationLifetime lifetime, bool isService)
    {
        _lifetime = lifetime;
        IsService = isService;
    }

    public bool IsService { get; }
    /// <summary>Einrichtungsmodus: per Doppelklick gestartet, nur über localhost erreichbar.</summary>
    public bool SetupMode => !IsService;

    /// <summary>
    /// Dienst: mit Exitcode 1 beenden → die Dienst-Wiederherstellung startet ihn nach 5 s neu.
    /// Einrichtungsmodus: neue Instanz starten, die auf das Ende dieser Instanz wartet.
    /// </summary>
    public void Restart()
    {
        if (!IsService) StartSelf($"--wait-pid {Environment.ProcessId}");
        else Environment.ExitCode = 1;
        StopSoon();
    }

    public void StopSoon() => _ = Task.Run(async () =>
    {
        await Task.Delay(800); // Antwort an den Browser noch ausliefern
        _lifetime.StopApplication();
    });

    /// <summary>Startet dieses Programm erneut (funktioniert auch bei "dotnet PdfRechnung.dll").</summary>
    public static void StartSelf(string arguments)
    {
        var (file, prefix) = SelfCommand();
        Process.Start(new ProcessStartInfo(file, prefix + arguments) { UseShellExecute = false });
    }

    public static (string File, string Prefix) SelfCommand()
    {
        var exe = Environment.ProcessPath ?? "PdfRechnung.exe";
        var isDotnetHost = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return isDotnetHost ? (exe, $"\"{Environment.GetCommandLineArgs()[0]}\" ") : (exe, "");
    }

    /// <summary>Verknüpfung "PdfRechnung.url" neben der EXE – zum Öffnen per Doppelklick.</summary>
    public static void WriteShortcut(ServerOptions server)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "PdfRechnung.url"),
                $"[InternetShortcut]\r\nURL=https://{Environment.MachineName.ToLowerInvariant()}:{server.HttpsPort}/\r\n" +
                $"IconFile={ServiceManager.ExecutablePath}\r\nIconIndex=0\r\n");
        }
        catch { /* ohne Schreibrechte egal */ }
    }

    public static int? ReadShortcutPort()
    {
        try
        {
            var line = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PdfRechnung.url"))
                .FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
            return line is null ? null : new Uri(line[4..]).Port;
        }
        catch { return null; }
    }

    public static bool WaitForPort(int port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var c = new TcpClient();
                c.Connect("127.0.0.1", port);
                return true;
            }
            catch { Thread.Sleep(500); }
        }
        return false;
    }
}
