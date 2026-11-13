using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32;

namespace BelegpostMelder;

internal static class Program
{
    public const string UrlScheme = "belegpost-melder";
    private const string RunValue = "Belegpost Melder";
    private static string PipeName => "BelegpostMelder-" + Environment.UserName;

    [STAThread]
    private static int Main(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(UrlScheme + ":", StringComparison.OrdinalIgnoreCase));

        // Nur ein Melder je Windows-Sitzung. Ein zweiter Start (z. B. über den Link aus dem Portal) gibt den Link weiter.
        using var mutex = new Mutex(true, @"Local\BelegpostMelder", out var first);
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(3000);
                using var w = new StreamWriter(pipe);
                w.WriteLine(arg ?? "zeigen");
            }
            catch { /* läuft gerade nicht ansprechbar */ }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        RegisterUrlScheme();
        var ctx = new TrayContext(startedHidden: args.Contains("--hintergrund"));
        StartPipeServer(ctx);
        if (arg is not null) ctx.HandleLink(arg);
        Application.Run(ctx);
        return 0;
    }

    /// <summary>Link „belegpost-melder:…“ öffnet diesen Melder (nur für den aktuellen Benutzer, keine Adminrechte nötig).</summary>
    private static void RegisterUrlScheme()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{UrlScheme}");
            key.SetValue("", "URL:Belegpost Melder");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch { /* ohne Link geht es per Code */ }
    }

    public static bool Autostart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\" --hintergrund");
            else key.DeleteValue(RunValue, false);
        }
    }

    private static void StartPipeServer(TrayContext ctx)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var r = new StreamReader(pipe);
                    var line = r.ReadLine();
                    if (line is not null) ctx.Post(() => { if (line == "zeigen") ctx.ShowStatus(); else ctx.HandleLink(line); });
                }
                catch { Thread.Sleep(1000); }
            }
        }) { IsBackground = true, Name = "Melder-Pipe" };
        t.Start();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Browser konnte nicht geöffnet werden:\n" + ex.Message, "Belegpost Melder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
