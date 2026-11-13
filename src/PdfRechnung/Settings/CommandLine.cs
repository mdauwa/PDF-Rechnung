namespace PdfRechnung.Settings;

/// <summary>
/// Befehlszeilen-Modi, die die Oberfläche intern (mit UAC-Abfrage) aufruft:
///   --install [--wait-pid N] [--open-browser]   Dienst einrichten und starten
///   --uninstall                                 Dienst entfernen
///   --reset-admin-password                      Admin-Kennwort löschen (beim nächsten Aufruf am Server neu festlegen)
/// Protokoll: install.log im Datenverzeichnis (wird in der Oberfläche angezeigt).
/// </summary>
public static class CommandLine
{
    public static bool Handles(string[] args) => args.Contains("--install") || args.Contains("--uninstall") || args.Contains("--reset-admin-password");

    public static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Run(string[] args, SettingsStore store)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Die Dienst-Installation ist nur unter Windows möglich.");
            return 2;
        }
        if (!ServiceManager.IsElevated())
        {
            // Sich selbst mit Administratorrechten neu starten
            return ServiceManager.RunElevated(string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))) ? 0 : 1223;
        }

        var logFile = Path.Combine(store.DataDirectory, "install.log");
        using var writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
        void Log(string m)
        {
            var line = $"{DateTime.Now:HH:mm:ss}  {m}";
            Console.WriteLine(line);
            writer.WriteLine(line);
        }

        try
        {
            if (int.TryParse(Value(args, "--wait-pid"), out var pid))
            {
                Log("Warte auf Ende der Einrichtungs-Instanz …");
                ServiceManager.WaitForProcessExit(pid, TimeSpan.FromSeconds(30));
            }

            store.EnsureCreated();
            var settings = store.Load();

            if (args.Contains("--reset-admin-password"))
            {
                new UserStore(store.DataDirectory).ClearLocalAdminPassword();
                Log("Admin-Kennwort gelöscht. Beim nächsten Aufruf direkt am Server (https://localhost) wird ein neues festgelegt.");
                return 0;
            }

            if (args.Contains("--uninstall"))
            {
                Log("Entferne Dienst …");
                ServiceManager.Uninstall(Log);
                Log("Dienst entfernt.");
                return 0;
            }

            // Programm lokal ablegen (Dienst sieht keine Netzlaufwerke wie Y:) und von dort registrieren
            var exe = ServiceManager.Deploy(Log);
            Log($"Installiere Dienst „{ServiceManager.DisplayName}“ für {exe} …");
            ServiceManager.Install(store, settings.Server, Log, exe);
            RuntimeControl.WriteShortcut(settings.Server);

            if (RuntimeControl.WaitForPort(settings.Server.HttpsPort, TimeSpan.FromSeconds(40)))
            {
                Log($"Dienst läuft: https://{Environment.MachineName.ToLowerInvariant()}:{settings.Server.HttpsPort}/");
                if (args.Contains("--open-browser"))
                    ServiceManager.OpenBrowser($"https://localhost:{settings.Server.HttpsPort}/#/einstellungen");
            }
            else
            {
                Log("Achtung: Der Dienst antwortet nicht. Details in der Ereignisanzeige (Anwendung, Quelle PdfRechnung).");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log("FEHLER: " + ex.Message);
            // Zurück in die Einrichtung, damit der Fehler sichtbar wird
            if (args.Contains("--open-browser")) RuntimeControl.StartSelf("");
            return 1;
        }
    }
}
