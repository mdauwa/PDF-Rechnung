namespace BelegpostMelder;

/// <summary>Symbol im Infobereich mit Menü; zeigt neue Belege als eigene Meldung an und öffnet sie im Browser.</summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly MelderConfig _cfg = MelderConfig.Load();
    private readonly MelderClient _client;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status, _autostart, _openLast, _openPortal, _lastOutlook, _actPortal, _actOutlook, _actHint;
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
        _lastOutlook = new ToolStripMenuItem("Letzten Beleg in Outlook", null, (_, _) => { if (_cfg.LastDocumentId is { } id) _ = OpenOutlook(id, null); });
        _actPortal = new ToolStripMenuItem("Ansicht im Portal öffnen", null, (_, _) => SetAction("portal"));
        _actOutlook = new ToolStripMenuItem("E-Mail in Outlook öffnen", null, (_, _) => SetAction("outlook"));
        _actHint = new ToolStripMenuItem("Nur Hinweis anzeigen", null, (_, _) => SetAction("hinweis"));
        var actions = new ToolStripMenuItem("Bei neuem Beleg");
        actions.DropDownItems.AddRange(new ToolStripItem[] { _actPortal, _actOutlook, _actHint });
        actions.DropDown.BackColor = Theme.Surface;
        if (actions.DropDown is ToolStripDropDownMenu dd) dd.Renderer = new MenuRenderer();
        _autostart = new ToolStripMenuItem("Mit Windows starten", null, (_, _) => { try { Program.Autostart = !Program.Autostart; } catch { } Refresh(); });
        var menu = new ContextMenuStrip
        {
            Renderer = new MenuRenderer(), Font = Theme.Body, ShowImageMargin = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        menu.Items.AddRange(new ToolStripItem[]
        {
            head, _status, new ToolStripSeparator(), _openPortal, _openLast, _lastOutlook, new ToolStripSeparator(), actions, _autostart,
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
        _client.PermissionsChanged += () => Post(Refresh);

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
        _actPortal.Checked = _cfg.Action == "portal";
        _actOutlook.Checked = _cfg.Action == "outlook";
        _actOutlook.Visible = _lastOutlook.Visible = _cfg.OutlookAllowed;
        _actHint.Checked = _cfg.Action == "hinweis";
        _lastOutlook.Enabled = _cfg.Paired && _cfg.LastDocumentId is not null;
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

    /// <summary>Was bei einem neuen Beleg passiert; Outlook-Modus wird dem Server gemeldet (kein Autoversand).</summary>
    public void SetAction(string action)
    {
        _cfg.OnNewDocument = action;
        _cfg.AutoOpen = action == "portal";
        _cfg.Save();
        _ = _client.SendSettings();
        Refresh();
    }

    /// <summary>Mail des Belegs als Entwurf in Outlook öffnen.</summary>
    public async Task OpenOutlook(string documentId, string? title)
    {
        try
        {
            var r = await OutlookBridge.OpenDraft(_cfg, documentId);
            if (!r.Ok) ToastWindow.Show(title ?? "Outlook", r.Message, ToastWindow.Kinds.Bad, () => OpenPath("/#/vorgang?id=" + Uri.EscapeDataString(documentId)), "Im Portal", 12);
            else if (!r.ViaOutlook) ToastWindow.Show(title ?? "E-Mail-Entwurf", r.Message, ToastWindow.Kinds.Info, null, seconds: 6);
        }
        catch (Exception ex)
        {
            ToastWindow.Show(title ?? "Outlook", "Der Entwurf konnte nicht geöffnet werden: " + ex.Message, ToastWindow.Kinds.Bad, null, seconds: 12);
        }
    }

    private void OnBeleg(BelegEvent e)
    {
        Refresh();
        // Aus dem Portal angestoßen: „In Outlook öffnen“
        if (e.Kind == "outlook") { _ = OpenOutlook(e.Id, e.Title); return; }
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
        Action? outlook = e.Mail && _cfg.OutlookAllowed ? () => _ = OpenOutlook(e.Id, title) : null;
        switch (_cfg.Action)
        {
            case "outlook" when e.Mail && _cfg.OutlookAllowed:
                _ = OpenOutlook(e.Id, title);
                ToastWindow.Show(title, text + "\nAls E-Mail in Outlook geöffnet.", kind, () => OpenPath(e.Path), "Im Portal", 6);
                break;
            case "portal":
                OpenPath(e.Path);
                ToastWindow.Show(title, text + "\nIm Browser geöffnet.", kind, outlook, "In Outlook", 6);
                break;
            default:
                ToastWindow.Show(title, text, kind, () => OpenPath(e.Path), "Öffnen", 12, outlook, outlook is null ? null : "In Outlook");
                break;
        }
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
            var (user, title, outlook) = await MelderClient.Hello(p.Server, p.Token, p.Thumbprint);
            _client.Stop();
            _cfg.Server = p.Server; _cfg.Token = p.Token; _cfg.Thumbprint = p.Thumbprint;
            _cfg.UserName = user; _cfg.PortalTitle = title; _cfg.OutlookAllowed = outlook; _cfg.LastSeq = null; _cfg.LastDocumentPath = null;
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
