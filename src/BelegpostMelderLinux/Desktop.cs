using System.Diagnostics;

namespace BelegpostMelder;

/// <summary>Anbindung an den Linux-Desktop über die üblichen Freedesktop-Werkzeuge (xdg-open, xdg-email, notify-send, lp).</summary>
public static class Desktop
{
    public static string IconPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "belegpost", "melder.png");
    public static string CacheFolder => Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "belegpost");

    /// <summary>Symbol für Benachrichtigungen und den Autostart-Eintrag ablegen.</summary>
    public static void EnsureIcon()
    {
        try
        {
            if (File.Exists(IconPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(IconPath)!);
            using var s = typeof(Desktop).Assembly.GetManifestResourceStream("melder.png");
            if (s is null) return;
            using var f = File.Create(IconPath);
            s.CopyTo(f);
        }
        catch { }
    }

    /// <summary>Programm starten und auf das Ende warten; liefert Exitcode und Fehlertext (−1 = nicht gefunden).</summary>
    public static (int Code, string Error) Run(string program, IEnumerable<string> args, int timeoutMs = 30_000)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (-1, program + " konnte nicht gestartet werden.");
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) return (0, ""); // läuft weiter (z. B. Mailprogramm) – gilt als geöffnet
            return (p.ExitCode, err.Wait(1000) ? err.Result.Trim() : "");
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, $"„{program}“ ist nicht installiert."); }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>Adresse im Standardbrowser öffnen.</summary>
    public static bool Open(string url)
    {
        var (code, _) = Run("xdg-open", new[] { url }, 10_000);
        return code == 0;
    }

    /// <summary>Desktop-Benachrichtigung (libnotify). Ohne notify-send passiert nichts.</summary>
    public static void Notify(string title, string text, bool error = false)
    {
        try
        {
            var args = new List<string> { "-a", "Belegpost Melder", "-u", error ? "critical" : "normal" };
            if (File.Exists(IconPath)) { args.Add("-i"); args.Add(IconPath); }
            args.Add(title); args.Add(text);
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { }
    }

    private static string AutostartFile => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "belegpost-melder.desktop");

    /// <summary>Beim Anmelden am Desktop starten (~/.config/autostart).</summary>
    public static bool Autostart
    {
        get => File.Exists(AutostartFile);
        set
        {
            if (!value) { if (File.Exists(AutostartFile)) File.Delete(AutostartFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            var exe = Environment.ProcessPath ?? "belegpost-melder";
            File.WriteAllText(AutostartFile, $"""
                [Desktop Entry]
                Type=Application
                Name=Belegpost Melder
                Comment=Meldet neue Belege aus Belegpost
                Exec="{exe}" --hintergrund
                Icon={IconPath}
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """ + "\n");
        }
    }

    /// <summary>PDF mit CUPS drucken; leerer Drucker = Standarddrucker. Liefert den Druckernamen für die Meldung.</summary>
    public static string Print(string file, string? printer, string title)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(printer)) { args.Add("-d"); args.Add(printer.Trim()); }
        args.Add("-t"); args.Add(title);
        args.Add(file);
        var (code, err) = Run("lp", args);
        if (code != 0) throw new InvalidOperationException(code == -1 ? "Drucken braucht CUPS („lp“ ist nicht installiert)." : err.Length > 0 ? err : $"lp meldet Fehler {code}.");
        return string.IsNullOrWhiteSpace(printer) ? "Standarddrucker" : printer.Trim();
    }
}
