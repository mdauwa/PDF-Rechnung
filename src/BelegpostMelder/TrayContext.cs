namespace BelegpostMelder;

/// <summary>Symbol im Infobereich mit Menü; zeigt neue Belege als eigene Meldung an und öffnet sie im Browser.</summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly MelderConfig _cfg = MelderConfig.Load();
    private readonly MelderClient _client;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status, _auto, _autostart, _openLast, _openPortal;
    private readonly WindowsFormsSynchronizationContext _ui;
    private MainWindow? _window;

    public MelderState State => _client.State;
    public string StatusText { get; private set; } = "";

    public TrayContext(bool startedHidden)
    {
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        _client = new MelderClient(_cfg);

        var head = new ToolStripMenuItem("Belegpost Melder öffnen", null, (_, _) => ShowWindow()) { Font = new Font(Theme.Body, FontStyle.Bold) };
        _status = new ToolStripMenuItem("…") { Enabled = false };
        _openPortal = new ToolStripMenuItem("Portal öffnen", null, (_, _) => OpenPath("/"));
        _openLast = new ToolStripMenuItem("Letzten Beleg öffnen", null, (_, _) => { if (_cfg.LastDocumentPath is { } p) OpenPath(p); });
        _auto = new ToolStripMenuItem("Neue Belege automatisch öffnen", null, (_, _) => { _cfg.AutoOpen = !_cfg.AutoOpen; _cfg.Save(); Refresh(); });
        _autostart = new ToolStripMenuItem("Mit Windows starten", null, (_, _) => { try { Program.Autostart = !Program.Autostart; } catch { } Refresh(); });
        var menu = new ContextMenuStrip
        {
            Renderer = new MenuRenderer(), Font = Theme.Body, ShowImageMargin = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        menu.Items.AddRange(new ToolStripItem[]
        {
            head, _status, new ToolStripSeparator(), _openPortal, _openLast, new ToolStripSeparator(), _auto, _autostart,
            new ToolStripSeparator(), new ToolStripMenuItem("Beenden", null, (_, _) => ExitThread())
        });
        foreach (ToolStripItem i in menu.Items) if (i is ToolStripMenuItem) i.Padding = new Padding(4, 5, 4, 5);

        _icon = new NotifyIcon { Icon = AppIcon(16), Text = "Belegpost Melder", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _client.StateChanged += (s, text) => Post(() =>
        {
            StatusText = text;
            _status.Text = text.Length > 60 ? text[..59] + "…" : text;
            _icon.Text = s switch
            {
                MelderState.Connected => "Belegpost Melder – verbunden",
                MelderState.NotPaired => "Belegpost Melder – nicht verbunden",
                MelderState.Rejected => "Belegpost Melder – bitte neu verbinden",
                _ => "Belegpost Melder – keine Verbindung"
            };
            Refresh();
        });
        _client.Received += e => Post(() => OnBeleg(e));

        Refresh();
        if (_cfg.Paired) _client.Start();
        else
        {
            StatusText = "Noch nicht verbunden.";
            if (!startedHidden) Post(() => Window.ShowPairing());
        }
    }

    public void Post(Action a) => _ui.Post(_ => a(), null);

    private MainWindow Window => _window is { IsDisposed: false } ? _window : _window = new MainWindow(this, _cfg);

    private void ShowWindow() { Window.RefreshView(); Window.BringUp(); }

    /// <summary>Programmsymbol in der gewünschten Größe (aus der eingebetteten .ico).</summary>
    public static Icon AppIcon(int size)
    {
        try
        {
            using var s = typeof(TrayContext).Assembly.GetManifestResourceStream("Melder.ico");
            if (s is not null) return new Icon(s, new Size(size, size));
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }

    private void Refresh()
    {
        _auto.Checked = _cfg.AutoOpen;
        try { _autostart.Checked = Program.Autostart; } catch { }
        _openPortal.Enabled = _cfg.Paired;
        _openLast.Enabled = _cfg.Paired && _cfg.LastDocumentPath is not null;
        if (!_cfg.Paired) _status.Text = "Nicht verbunden";
        if (_window is { IsDisposed: false, Visible: true }) _window.RefreshView();
    }

    public void OpenPath(string path)
    {
        if (_cfg.Server is null) return;
        Program.OpenUrl(_cfg.Server.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path));
    }

    private void OnBeleg(BelegEvent e)
    {
        Refresh();
        var kind = e.Status switch
        {
            "fehler" or "versandfehler" => ToastWindow.Kinds.Bad,
            "warnung" => ToastWindow.Kinds.Warn,
            "bereit" or "versendet" => ToastWindow.Kinds.Ok,
            _ => ToastWindow.Kinds.Info
        };
        var text = e.Text ?? e.Status switch
        {
            "bereit" => "Bereit zum Versand.",
            "fehler" => "Bitte prüfen – der Beleg hat einen Fehler.",
            "warnung" => "Bitte prüfen.",
            "ohneVersand" => "Ohne E-Mail-Versand.",
            _ => "Neuer Beleg."
        };
        var title = e.Title.Length > 0 ? e.Title : "Neuer Beleg";
        if (_cfg.AutoOpen)
        {
            OpenPath(e.Path);
            ToastWindow.Show(title, text + "\nIm Browser geöffnet.", kind, null, seconds: 6);
        }
        else ToastWindow.Show(title, text, kind, () => OpenPath(e.Path));
    }

    /// <summary>Link aus dem Portal („Im Melder übernehmen“) oder anderer Aufruf.</summary>
    public void HandleLink(string link)
    {
        var p = PairingData.Parse(link);
        if (p is null) { ShowWindow(); return; }
        _ = Pair(p, quiet: false);
    }

    public void ShowStatus() => ShowWindow();

    public async Task<bool> Pair(PairingData p, bool quiet)
    {
        StatusText = "Prüfe Verbindung zu " + p.Server + " …";
        Refresh();
        try
        {
            var (user, title) = await MelderClient.Hello(p.Server, p.Token, p.Thumbprint);
            _client.Stop();
            _cfg.Server = p.Server; _cfg.Token = p.Token; _cfg.Thumbprint = p.Thumbprint;
            _cfg.UserName = user; _cfg.PortalTitle = title; _cfg.LastSeq = null; _cfg.LastDocumentPath = null;
            _cfg.Save();
            try { if (!Program.Autostart) Program.Autostart = true; } catch { }
            _client.Start();
            Refresh();
            ToastWindow.Show("Melder verbunden", $"Angemeldet als {user}. Neu gedruckte Belege öffnen sich ab jetzt hier.", ToastWindow.Kinds.Ok, null, seconds: 6);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            Refresh();
            if (!quiet) { ShowWindow(); }
            ToastWindow.Show("Verbinden hat nicht geklappt", ex.Message, ToastWindow.Kinds.Bad, null, seconds: 12);
            return false;
        }
    }

    public bool Disconnect()
    {
        if (MessageBox.Show(_window, "Diesen PC vom Portal trennen?\nNeue Belege werden dann nicht mehr gemeldet.", "Belegpost Melder",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        _client.Stop();
        _cfg.Token = null; _cfg.LastSeq = null; _cfg.LastDocumentPath = null;
        _cfg.Save();
        _client.Start(); // meldet „nicht verbunden“
        StatusText = "Getrennt.";
        Refresh();
        return true;
    }

    protected override void ExitThreadCore()
    {
        _client.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _window?.Dispose();
        base.ExitThreadCore();
    }
}
