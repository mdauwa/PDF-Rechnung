namespace BelegpostMelder;

/// <summary>Symbol im Infobereich mit Menü; zeigt neue Belege an und öffnet sie im Browser.</summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly MelderConfig _cfg = MelderConfig.Load();
    private readonly MelderClient _client;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status, _auto, _autostart, _openLast, _openPortal, _disconnect;
    private readonly WindowsFormsSynchronizationContext _ui;
    private string? _balloonPath;

    public TrayContext(bool startedHidden)
    {
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        _client = new MelderClient(_cfg);

        _status = new ToolStripMenuItem("…") { Enabled = false };
        _openPortal = new ToolStripMenuItem("Portal öffnen", null, (_, _) => Open("/"));
        _openLast = new ToolStripMenuItem("Letzten Beleg öffnen", null, (_, _) => { if (_cfg.LastDocumentPath is { } p) Open(p); });
        _auto = new ToolStripMenuItem("Neue Belege automatisch öffnen", null, (_, _) => { _cfg.AutoOpen = !_cfg.AutoOpen; _cfg.Save(); Refresh(); });
        _autostart = new ToolStripMenuItem("Mit Windows starten", null, (_, _) => { try { Program.Autostart = !Program.Autostart; } catch { } Refresh(); });
        _disconnect = new ToolStripMenuItem("Verbindung trennen", null, (_, _) => Disconnect());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _status, new ToolStripSeparator(), _openPortal, _openLast, new ToolStripSeparator(), _auto, _autostart,
            new ToolStripSeparator(), new ToolStripMenuItem("Verbinden …", null, (_, _) => AskPairing()), _disconnect,
            new ToolStripSeparator(), new ToolStripMenuItem("Beenden", null, (_, _) => ExitThread())
        });

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Belegpost Melder",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        _icon.BalloonTipClicked += (_, _) => { if (_balloonPath is { } p) Open(p); };

        _client.StateChanged += (s, text) => Post(() => { _status.Text = text; _icon.Text = Short("Belegpost Melder – " + text); Refresh(); });
        _client.Received += e => Post(() => OnBeleg(e));

        Refresh();
        if (_cfg.Paired) _client.Start();
        else if (!startedHidden) Post(AskPairing);
        else _status.Text = "Nicht verbunden – Rechtsklick → Verbinden …";
    }

    public void Post(Action a) => _ui.Post(_ => a(), null);

    private static Icon LoadIcon()
    {
        try
        {
            using var s = typeof(TrayContext).Assembly.GetManifestResourceStream("Melder.ico");
            if (s is not null) return new Icon(s, SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    private static string Short(string s) => s.Length > 63 ? s[..62] + "…" : s; // NotifyIcon.Text max. 63 Zeichen

    private void Refresh()
    {
        _auto.Checked = _cfg.AutoOpen;
        try { _autostart.Checked = Program.Autostart; } catch { }
        _openPortal.Enabled = _cfg.Paired;
        _openLast.Enabled = _cfg.Paired && _cfg.LastDocumentPath is not null;
        _disconnect.Enabled = _cfg.Paired;
        if (!_cfg.Paired) _status.Text = "Nicht verbunden";
    }

    private void Open(string path)
    {
        if (_cfg.Server is null) return;
        Program.OpenUrl(_cfg.Server.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path));
    }

    private void OnBeleg(BelegEvent e)
    {
        Refresh();
        _balloonPath = e.Path;
        var warn = e.Status is "fehler" or "warnung" or "versandfehler";
        if (_cfg.AutoOpen) Open(e.Path);
        _icon.ShowBalloonTip(8000, e.Title.Length > 0 ? e.Title : "Neuer Beleg",
            (e.Text ?? (e.Status switch { "bereit" => "Bereit zum Versand", "fehler" => "Bitte prüfen – Fehler", "warnung" => "Bitte prüfen", _ => "Neuer Beleg" }))
            + (_cfg.AutoOpen ? "" : "\nKlicken zum Öffnen"),
            warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public void ShowStatus()
    {
        if (!_cfg.Paired) { AskPairing(); return; }
        _icon.ShowBalloonTip(4000, "Belegpost Melder", _status.Text ?? "", ToolTipIcon.Info);
    }

    /// <summary>Link aus dem Portal („Im Melder übernehmen“) oder anderer Aufruf.</summary>
    public void HandleLink(string link)
    {
        var p = PairingData.Parse(link);
        if (p is null) { ShowStatus(); return; }
        _ = Pair(p);
    }

    private void AskPairing()
    {
        using var f = new PairForm(_cfg.Server);
        if (f.ShowDialog() == DialogResult.OK && PairingData.Parse(f.Code) is { } p) _ = Pair(p);
        else if (f.DialogResult == DialogResult.OK) MessageBox.Show("Der Code ist ungültig. Bitte im Portal unter „Melder & Hinweise“ neu erzeugen.", "Belegpost Melder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task Pair(PairingData p)
    {
        _status.Text = "Prüfe Verbindung zu " + p.Server + " …";
        try
        {
            var (user, title) = await MelderClient.Hello(p.Server, p.Token, p.Thumbprint);
            _client.Stop();
            _cfg.Server = p.Server; _cfg.Token = p.Token; _cfg.Thumbprint = p.Thumbprint;
            _cfg.UserName = user; _cfg.PortalTitle = title; _cfg.LastSeq = null;
            _cfg.Save();
            try { if (!Program.Autostart) Program.Autostart = true; } catch { }
            Refresh();
            _client.Start();
            _icon.ShowBalloonTip(6000, "Belegpost Melder verbunden", $"Angemeldet als {user}. Neu gedruckte Belege öffnen sich ab jetzt automatisch.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Verbinden hat nicht geklappt:\n\n" + ex.Message, "Belegpost Melder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Refresh();
        }
    }

    private void Disconnect()
    {
        if (MessageBox.Show("Diesen PC vom Portal trennen?", "Belegpost Melder", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _client.Stop();
        _cfg.Token = null; _cfg.LastSeq = null; _cfg.LastDocumentPath = null;
        _cfg.Save();
        Refresh();
    }

    protected override void ExitThreadCore()
    {
        _client.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Kleiner Dialog zum Einfügen des Kopplungscodes.</summary>
internal sealed class PairForm : Form
{
    private readonly TextBox _code = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f) };
    public string Code => _code.Text;

    public PairForm(string? server)
    {
        Text = "Belegpost Melder verbinden";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(520, 300);
        MinimizeBox = MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        TopMost = true;
        var info = new Label
        {
            Dock = DockStyle.Top, Height = 70, Padding = new Padding(4),
            Text = "Im Portal links „Melder & Hinweise“ öffnen, „Melder verbinden“ klicken und den angezeigten Code hier einfügen."
                   + (server is null ? "" : $"\nBisher verbunden mit: {server}")
        };
        var ok = new Button { Text = "Verbinden", DialogResult = DialogResult.OK, Width = 110 };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Width = 110 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        body.Controls.Add(_code);
        Controls.Add(body); Controls.Add(buttons); Controls.Add(info);
        AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) =>
        {
            try { if (Clipboard.ContainsText() && PairingData.Parse(Clipboard.GetText()) is not null) _code.Text = Clipboard.GetText(); } catch { }
        };
    }
}
