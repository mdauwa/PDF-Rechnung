using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace BelegpostMelder;

/// <summary>Avalonia-Anwendung ohne XAML: Symbol im Infobereich, ein kleines Fenster, Verbindung zum Server.</summary>
public sealed class MelderApp : Application
{
    public static bool StartHidden { get; set; }
    public static MelderController? Controller { get; private set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Controller = new MelderController(this, desktop);
            Controller.Start(StartHidden);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class MelderController
{
    private readonly MelderApp _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MelderConfig _cfg = MelderConfig.Load();
    private readonly MelderClient _client;
    private Window? _window;
    private TrayIcon? _tray;
    private string _status = "";
    private bool _tasksRunning;

    // Fenster-Elemente
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _userText = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBox _code = new() { Watermark = "Code aus dem Portal (Melder & Hinweise → Melder verbinden)", AcceptsReturn = false };
    private readonly Button _pair = new() { Content = "Verbinden" };
    private readonly ComboBox _action = new() { MinWidth = 260 };
    private readonly CheckBox _autostart = new() { Content = "Beim Anmelden starten" };
    private readonly TextBlock _mailText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly StackPanel _pairPanel = new() { Spacing = 8 };
    private readonly StackPanel _connectedPanel = new() { Spacing = 8 };

    public MelderController(MelderApp app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app; _desktop = desktop;
        _client = new MelderClient(_cfg);
        _client.StateChanged += (_, text) => Dispatcher.UIThread.Post(() => { _status = text; Refresh(); });
        _client.Received += e => Dispatcher.UIThread.Post(() => OnBeleg(e));
        _client.PermissionsChanged += () => Dispatcher.UIThread.Post(Refresh);
        _client.Connected += () => Dispatcher.UIThread.Post(() => _ = RunTasks());
    }

    public void Start(bool hidden)
    {
        Desktop.EnsureIcon();
        CreateTray();
        _client.Start();
        if (!hidden || !_cfg.Paired) ShowWindow();
    }

    // ---------------- Infobereich ----------------

    private void CreateTray()
    {
        try
        {
            using var s = typeof(MelderController).Assembly.GetManifestResourceStream("melder.png")!;
            var menu = new NativeMenu();
            menu.Add(Item("Fenster öffnen", ShowWindow));
            menu.Add(Item("Portal öffnen", () => OpenPath("/")));
            menu.Add(Item("Letzten Beleg öffnen", () => { if (_cfg.LastDocumentPath is { } p) OpenPath(p); }));
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(Item("Beenden", () => { _client.Stop(); _tray?.Dispose(); _desktop.Shutdown(); }));
            _tray = new TrayIcon { Icon = new WindowIcon(s), ToolTipText = "Belegpost Melder", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(_app, new TrayIcons { _tray });
        }
        catch { /* ohne Infobereich (z. B. GNOME ohne AppIndicator): das Fenster bleibt erreichbar */ }
    }

    private static NativeMenuItem Item(string text, Action action)
    {
        var i = new NativeMenuItem(text);
        i.Click += (_, _) => action();
        return i;
    }

    // ---------------- Fenster ----------------

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = BuildWindow();
            _window.Closing += (_, e) => { e.Cancel = true; _window.Hide(); }; // Fenster schließen = im Hintergrund weiterlaufen
        }
        Refresh();
        _window.Show();
        _window.Activate();
    }

    private Window BuildWindow()
    {
        Window w;
        using (var s = typeof(MelderController).Assembly.GetManifestResourceStream("melder.png")!)
            w = new Window { Title = "Belegpost Melder", Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, Icon = new WindowIcon(s),
                WindowStartupLocation = WindowStartupLocation.CenterScreen };

        _pair.Classes.Add("accent");
        _pair.Click += async (_, _) =>
        {
            if (PairingData.Parse(_code.Text) is not { } p) { _status = "Der Code ist nicht gültig – bitte im Portal neu erzeugen und vollständig einfügen."; Refresh(); return; }
            _pair.IsEnabled = false;
            await Pair(p);
            _pair.IsEnabled = true;
        };
        _pairPanel.Children.AddRange(new Control[]
        {
            new TextBlock { Text = "Melder verbinden", FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Im Portal unter „Melder & Hinweise“ auf „Melder verbinden“ klicken und den Code hier einfügen.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
            _code, _pair
        });

        _action.ItemsSource = new[] { "Bei neuem Beleg: Ansicht im Portal öffnen", "Bei neuem Beleg: E-Mail öffnen", "Bei neuem Beleg: nur Hinweis" };
        _action.SelectionChanged += (_, _) =>
        {
            var a = _action.SelectedIndex switch { 1 => "outlook", 2 => "hinweis", _ => "portal" };
            if (a == _cfg.OnNewDocument) return;
            _cfg.OnNewDocument = a; Save(); _ = _client.SendSettings();
        };
        _autostart.IsCheckedChanged += (_, _) => { try { Desktop.Autostart = _autostart.IsChecked == true; } catch { } };

        var portal = new Button { Content = "Portal öffnen" };
        portal.Click += (_, _) => OpenPath("/");
        var last = new Button { Content = "Letzten Beleg öffnen" };
        last.Click += (_, _) => { if (_cfg.LastDocumentPath is { } p) OpenPath(p); };
        var disconnect = new Button { Content = "Trennen" };
        disconnect.Click += (_, _) =>
        {
            _client.Stop();
            _cfg.Token = null; _cfg.Server = null; _cfg.LastSeq = null; Save();
            _status = "Getrennt – mit einem neuen Code wieder verbinden.";
            Refresh();
        };
        _connectedPanel.Children.AddRange(new Control[]
        {
            _userText,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { portal, last } },
            _action, _mailText, _autostart, disconnect
        });

        w.Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Belegpost Melder", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    _statusText, _pairPanel, _connectedPanel,
                    new TextBlock { Text = "Version " + MelderInfo.Version, Opacity = 0.5, FontSize = 11 }
                }
            }
        };
        return w;
    }

    private void Refresh()
    {
        if (_tray is not null) _tray.ToolTipText = "Belegpost Melder – " + (_client.State == MelderState.Connected ? "verbunden" : "nicht verbunden");
        if (_window is null) return;
        _statusText.Text = _status;
        _pairPanel.IsVisible = !_cfg.Paired;
        _connectedPanel.IsVisible = _cfg.Paired;
        _userText.Text = _cfg.Paired ? $"{_cfg.UserName} · {_cfg.Server}" : "";
        _action.SelectedIndex = _cfg.OnNewDocument switch { "outlook" => 1, "hinweis" => 2, _ => 0 };
        _autostart.IsChecked = Desktop.Autostart;
        _mailText.Text = "E-Mails öffnen sich " + (_cfg.MailClient == "standard"
            ? "im Standard-Mailprogramm (xdg-email)."
            : "in Outlook im Web (Entwurf über Microsoft 365).") + " Einstellbar im Portal beim Benutzer.";
    }

    private void Save() { try { _cfg.Save(); } catch { } }

    private async Task Pair(PairingData p)
    {
        _status = "Prüfe Verbindung zu " + p.Server + " …"; Refresh();
        try
        {
            var (user, title, outlook) = await MelderClient.Hello(p.Server, p.Token, p.Thumbprint);
            _client.Stop();
            _cfg.Server = p.Server; _cfg.Token = p.Token; _cfg.Thumbprint = p.Thumbprint;
            _cfg.UserName = user; _cfg.PortalTitle = title; _cfg.OutlookAllowed = outlook; _cfg.LastSeq = null; _cfg.LastDocumentPath = null;
            Save();
            try { if (!Desktop.Autostart) Desktop.Autostart = true; } catch { }
            _code.Text = "";
            _client.Start();
            Desktop.Notify("Melder verbunden", $"Angemeldet als {user}. Neu gedruckte Belege öffnen sich ab jetzt hier.");
        }
        catch (Exception ex) { _status = ex.Message; }
        Refresh();
    }

    // ---------------- Ereignisse ----------------

    private void OpenPath(string path)
    {
        if (_cfg.Server is null) return;
        Desktop.Open(_cfg.Server.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path));
    }

    private void OnBeleg(BelegEvent e)
    {
        Refresh();
        if (e.Kind == "outlook") { _ = OpenMail(e.Id, e.Title); return; }
        if (e.Kind == "auftrag") { _ = RunTasks(); return; }
        if (e.Kind == "einstellung") { _ = _client.RefreshInfo(); if (!string.IsNullOrEmpty(e.Text)) Desktop.Notify("Einstellung geändert", e.Text); return; }
        var title = e.Title.Length > 0 ? e.Title : "Neuer Beleg";
        if (e.Kind == "hinweis") { Desktop.Notify(title, e.Text ?? "", e.Status is "fehler" or "versandfehler"); return; }
        var text = e.Text ?? e.Status switch
        {
            "bereit" => "Bereit zum Versand.",
            "fehler" => "Bitte prüfen – der Beleg hat einen Fehler.",
            "warnung" => "Bitte prüfen.",
            "ohneVersand" => "Ohne E-Mail-Versand.",
            _ => "Neuer Beleg."
        };
        switch (_cfg.Action)
        {
            case "outlook" when e.Mail && _cfg.OutlookAllowed:
                _ = OpenMail(e.Id, title);
                break;
            case "portal":
                OpenPath(e.Path);
                Desktop.Notify(title, text + " Im Browser geöffnet.", e.Status is "fehler" or "versandfehler");
                break;
            default:
                Desktop.Notify(title, text, e.Status is "fehler" or "versandfehler");
                break;
        }
    }

    private async Task<bool> OpenMail(string id, string? title)
    {
        try
        {
            var r = await MailOpener.Open(_cfg, id);
            Desktop.Notify(title ?? "E-Mail", r.Message, !r.Ok);
            if (!r.Ok) _ = _client.ReportOutlookError(id, r.Message);
            return r.Ok;
        }
        catch (Exception ex)
        {
            Desktop.Notify(title ?? "E-Mail", ex.Message, true);
            _ = _client.ReportOutlookError(id, ex.Message);
            return false;
        }
    }

    // ---------------- Aufträge (Outlook-Entwurf, Speichern, Drucken) ----------------

    private async Task RunTasks()
    {
        if (_tasksRunning || !_cfg.Paired) return;
        _tasksRunning = true;
        try
        {
            for (var round = 0; round < 5; round++)
            {
                var pending = await _client.PendingTasks();
                if (pending.Count == 0) break;
                foreach (var t in pending)
                {
                    if (!await _client.ClaimTask(t.Id)) continue;
                    if (t.Kind == "outlook" && t.VorgangId is { } id)
                    {
                        if (_cfg.OutlookAllowed) await OpenMail(id, "E-Mail aus dem ERP");
                        else OpenPath("/#/vorgang?id=" + Uri.EscapeDataString(id));
                    }
                    else if (t.Kind == "speichern" && t.VorgangId is not null) await SaveTask(t);
                    else if (t.Kind == "drucken" && t.VorgangId is not null) await PrintTask(t);
                    await _client.CompleteTask(t.Id);
                }
            }
        }
        catch { /* beim nächsten Ereignis erneut */ }
        finally { _tasksRunning = false; }
    }

    /// <summary>@@pa@@ / @@pn@@: Speichern-Dialog im vorgegebenen Ordner (z. B. eingehängte Freigabe) mit dem fertigen PDF.</summary>
    private async Task SaveTask(MelderClient.MelderTask t)
    {
        var id = t.VorgangId!;
        var title = string.IsNullOrWhiteSpace(t.Title) ? "Beleg" : t.Title!;
        var pdf = await _client.DownloadPdf(id);
        if (pdf is null) { Desktop.Notify(title, "Das PDF konnte nicht geladen werden – bitte im Portal speichern.", true); await _client.ReportJob(id, t.Kind, false, "PDF nicht geladen"); return; }
        ShowWindow();
        var folder = (t.Folder ?? "").Trim();
        // Windows-Pfade (\\server\…, K:\…) gibt es unter Linux nicht – dann im zuletzt verwendeten Ordner starten
        IStorageFolder? start = null;
        if (folder.Length > 0 && folder.StartsWith('/'))
        {
            try { Directory.CreateDirectory(folder); } catch { }
            try { start = await _window!.StorageProvider.TryGetFolderFromPathAsync(folder); } catch { }
        }
        var file = await _window!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title + " speichern",
            SuggestedFileName = string.IsNullOrWhiteSpace(t.FileName) ? "Beleg.pdf" : t.FileName,
            DefaultExtension = "pdf",
            SuggestedStartLocation = start,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("PDF-Datei") { Patterns = new[] { "*.pdf" }, MimeTypes = new[] { "application/pdf" } } }
        });
        if (file is null) { await _client.ReportJob(id, t.Kind, false, "im Melder abgebrochen"); return; }
        try
        {
            await using (var s = await file.OpenWriteAsync()) await s.WriteAsync(pdf);
            var where = file.TryGetLocalPath() ?? file.Name;
            Desktop.Notify(title, "Gespeichert: " + where);
            await _client.ReportJob(id, t.Kind, true, where + (folder.Length > 0 && start is null ? $" (Ablageordner „{folder}“ hier nicht verfügbar)" : ""));
        }
        catch (Exception ex) { Desktop.Notify(title, "Speichern fehlgeschlagen: " + ex.Message, true); await _client.ReportJob(id, t.Kind, false, ex.Message); }
    }

    /// <summary>@@pp@@: fertiges PDF über CUPS drucken.</summary>
    private async Task PrintTask(MelderClient.MelderTask t)
    {
        var id = t.VorgangId!;
        var title = string.IsNullOrWhiteSpace(t.Title) ? "Beleg" : t.Title!;
        var pdf = await _client.DownloadPdf(id);
        if (pdf is null) { Desktop.Notify(title, "Das PDF konnte nicht geladen werden – bitte im Portal drucken.", true); await _client.ReportJob(id, t.Kind, false, "PDF nicht geladen"); return; }
        var dir = Path.Combine(Desktop.CacheFolder, "druck");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllBytesAsync(path, pdf);
            var printer = await Task.Run(() => Desktop.Print(path, t.Printer, string.IsNullOrWhiteSpace(t.FileName) ? title : Path.GetFileNameWithoutExtension(t.FileName)));
            Desktop.Notify(title, $"Auf „{printer}“ gedruckt.");
            await _client.ReportJob(id, t.Kind, true, $"auf „{printer}“ ({Environment.MachineName})");
        }
        catch (Exception ex) { Desktop.Notify(title, "Drucken fehlgeschlagen: " + ex.Message, true); await _client.ReportJob(id, t.Kind, false, ex.Message); }
        finally { _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => { try { File.Delete(path); } catch { } }); }
    }
}
