namespace BelegpostMelder;

/// <summary>
/// Hauptfenster des Melders: Status, Einstellungen und Verbinden – in einem schlichten, modernen Layout
/// (dunkler Kopf wie im Portal, weiße Karten auf hellem Grund).
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly TrayContext _tray;
    private readonly MelderConfig _cfg;
    private readonly float _s;

    private readonly Card _statusCard = new(), _optionsCard = new(), _connectCard = new();
    private readonly Pill _pill = new();
    private readonly Label _statusText = Lbl(Theme.Body, Theme.Muted), _userLine = Lbl(Theme.Strong, Theme.Text), _serverLine = Lbl(Theme.Small, Theme.Muted);
    private readonly ModernButton _btnPortal = new() { Text = "Portal öffnen" }, _btnLast = new() { Text = "Letzten Beleg" };
    private readonly ToggleSwitch _tAuto = new() { Text = "Neue Belege automatisch öffnen", Hint = "Öffnet den Beleg im Browser, sobald du druckst" };
    private readonly ToggleSwitch _tStart = new() { Text = "Mit Windows starten", Hint = "Melder läuft unauffällig im Infobereich" };
    private readonly Label _connectTitle = Lbl(Theme.Strong, Theme.Text), _connectHint = Lbl(Theme.Small, Theme.Muted);
    private readonly TextBox _code = new() { Multiline = true, BorderStyle = BorderStyle.None, Font = Theme.Mono, ScrollBars = ScrollBars.None };
    private readonly Panel _codeFrame = new();
    private readonly ModernButton _btnPair = new() { Kind = ModernButton.Kinds.Primary, Text = "Verbinden" };
    private readonly ModernButton _btnPaste = new() { Kind = ModernButton.Kinds.Ghost, Text = "Einfügen" };
    private readonly ModernButton _btnRepair = new() { Kind = ModernButton.Kinds.Ghost, Text = "Neu verbinden" };
    private readonly ModernButton _btnDisconnect = new() { Kind = ModernButton.Kinds.Ghost, Text = "Trennen" };
    private bool _showPairing;

    private static Label Lbl(Font f, Color c) => new() { Font = f, ForeColor = c, AutoSize = false, BackColor = Theme.Surface, AutoEllipsis = true };

    public MainWindow(TrayContext tray, MelderConfig cfg)
    {
        _tray = tray; _cfg = cfg;
        AutoScaleMode = AutoScaleMode.None;
        _s = DeviceDpi / 96f;
        Text = "Belegpost Melder";
        Icon = TrayContext.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Body;
        DoubleBuffered = true;

        _statusCard.Controls.AddRange(new Control[] { _pill, _statusText, _userLine, _serverLine, _btnPortal, _btnLast });
        _optionsCard.Controls.AddRange(new Control[] { _tAuto, _tStart });
        _codeFrame.Controls.Add(_code);
        _codeFrame.Paint += (_, e) =>
        {
            Theme.Smooth(e.Graphics);
            using var p = new Pen(_code.Focused ? Theme.Accent : Theme.Border, _code.Focused ? 1.6f : 1f);
            using var path = Theme.Round(new RectangleF(0.5f, 0.5f, _codeFrame.Width - 1.5f, _codeFrame.Height - 1.5f), 8 * _s);
            e.Graphics.DrawPath(p, path);
        };
        _codeFrame.BackColor = Theme.Surface;
        _code.BackColor = Theme.Surface;
        _code.GotFocus += (_, _) => _codeFrame.Invalidate();
        _code.LostFocus += (_, _) => _codeFrame.Invalidate();
        _code.TextChanged += (_, _) => _btnPair.Enabled = PairingData.Parse(_code.Text) is not null;
        _connectCard.Controls.AddRange(new Control[] { _connectTitle, _connectHint, _codeFrame, _btnPaste, _btnPair, _btnRepair, _btnDisconnect });
        Controls.AddRange(new Control[] { _statusCard, _optionsCard, _connectCard });

        _btnPortal.Click += (_, _) => _tray.OpenPath("/");
        _btnLast.Click += (_, _) => { if (_cfg.LastDocumentPath is { } p) _tray.OpenPath(p); };
        _tAuto.CheckedChanged += (_, _) => { if (_cfg.AutoOpen != _tAuto.Checked) { _cfg.AutoOpen = _tAuto.Checked; _cfg.Save(); } };
        _tStart.CheckedChanged += (_, _) => { try { if (Program.Autostart != _tStart.Checked) Program.Autostart = _tStart.Checked; } catch { } };
        _btnPaste.Click += (_, _) => { try { if (Clipboard.ContainsText()) _code.Text = Clipboard.GetText().Trim(); } catch { } };
        _btnPair.Click += async (_, _) =>
        {
            if (PairingData.Parse(_code.Text) is not { } p) return;
            _btnPair.Enabled = false; _btnPair.Text = "Verbinde …";
            var ok = await _tray.Pair(p, quiet: true);
            _btnPair.Text = "Verbinden";
            if (ok) { _code.Text = ""; _showPairing = false; }
            else _btnPair.Enabled = true;
            RefreshView();
        };
        _btnRepair.Click += (_, _) => { _showPairing = true; RefreshView(); _code.Focus(); };
        _btnDisconnect.Click += (_, _) => { if (_tray.Disconnect()) RefreshView(); };

        ClientSize = new Size((int)(440 * _s), (int)(600 * _s));
        RefreshView();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Dwm.RoundCorners(this); }

    /// <summary>Schließen = ins Infobereich zurück (der Melder läuft weiter).</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    public void ShowPairing()
    {
        _showPairing = true;
        RefreshView();
        BringUp();
        try { if (Clipboard.ContainsText() && PairingData.Parse(Clipboard.GetText()) is not null) _code.Text = Clipboard.GetText().Trim(); } catch { }
        _code.Focus();
    }

    public void BringUp()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>Kopfbereich: dunkel wie die Portal-Seitenleiste, mit Logo.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Theme.Smooth(g);
        var head = new Rectangle(0, 0, ClientSize.Width, (int)(88 * _s));
        using (var b = new SolidBrush(Theme.Dark)) g.FillRectangle(b, head);
        using (var ico = TrayContext.AppIcon(48)) g.DrawIcon(ico, new Rectangle((int)(20 * _s), (int)(20 * _s), (int)(48 * _s), (int)(48 * _s)));
        using var title = Theme.Title;
        TextRenderer.DrawText(g, "Belegpost Melder", title, new Point((int)(80 * _s), (int)(22 * _s)), Color.White);
        TextRenderer.DrawText(g, _cfg.PortalTitle is { Length: > 0 } t && t != "Belegpost" ? t : "Neue Belege sofort öffnen", Theme.Body,
            new Point((int)(81 * _s), (int)(50 * _s)), Color.FromArgb(0xC9, 0xD2, 0xDE));
    }

    public void RefreshView()
    {
        if (IsDisposed) return;
        var s = _s;
        int pad = (int)(16 * s), inner = (int)(18 * s), w = ClientSize.Width - 2 * pad, iw = w - 2 * inner;
        int y = (int)(88 * s) + pad;

        // ---- Status ----
        var (text, fore, back) = _tray.State switch
        {
            MelderState.Connected => ("verbunden", Theme.Ok, Theme.OkSoft),
            MelderState.Connecting => ("verbinde …", Theme.Accent, Theme.AccentSoft),
            MelderState.Offline => ("keine Verbindung", Theme.Warn, Theme.WarnSoft),
            MelderState.Rejected => ("nicht mehr gekoppelt", Theme.Bad, Theme.BadSoft),
            _ => ("nicht verbunden", Theme.Muted, Theme.Surface2)
        };
        _pill.Set(text, fore, back);
        _pill.Location = new Point(inner, inner);
        _userLine.Text = _cfg.Paired ? $"Angemeldet als {_cfg.UserName}" : "Noch nicht mit dem Portal verbunden";
        _userLine.SetBounds(inner, inner + (int)(34 * s), iw, (int)(22 * s));
        _serverLine.Text = _cfg.Paired ? _cfg.Server : "";
        _serverLine.SetBounds(inner, inner + (int)(56 * s), iw, (int)(18 * s));
        _statusText.Text = _tray.StatusText;
        _statusText.SetBounds(inner, inner + (int)(78 * s), iw, (int)(38 * s));
        var by = inner + (int)(120 * s);
        var bw = (iw - (int)(10 * s)) / 2;
        _btnPortal.SetBounds(inner, by, bw, (int)(36 * s));
        _btnLast.SetBounds(inner + bw + (int)(10 * s), by, bw, (int)(36 * s));
        _btnPortal.Enabled = _cfg.Paired;
        _btnLast.Enabled = _cfg.Paired && _cfg.LastDocumentPath is not null;
        _statusCard.SetBounds(pad, y, w, by + (int)(36 * s) + inner);
        y = _statusCard.Bottom + (int)(12 * s);

        // ---- Einstellungen ----
        _tAuto.Checked = _cfg.AutoOpen;
        try { _tStart.Checked = Program.Autostart; } catch { }
        _tAuto.SetBounds(inner, (int)(8 * s), iw, (int)(48 * s));
        _tStart.SetBounds(inner, (int)(58 * s), iw, (int)(48 * s));
        _optionsCard.SetBounds(pad, y, w, (int)(114 * s));
        y = _optionsCard.Bottom + (int)(12 * s);

        // ---- Verbinden ----
        var pairing = !_cfg.Paired || _showPairing;
        _connectTitle.Text = pairing ? "Mit dem Portal verbinden" : "Verbindung";
        _connectTitle.SetBounds(inner, inner, iw, (int)(22 * s));
        _connectHint.Text = pairing
            ? "Im Portal links „Melder & Hinweise“ öffnen und auf „Melder verbinden“ klicken. Den Code hier einfügen – oder im Portal „Im Melder übernehmen“ wählen."
            : "Dieser PC ist mit deinem Portal-Konto verbunden. Trennen oder mit einem neuen Code neu verbinden.";
        _connectHint.SetBounds(inner, inner + (int)(24 * s), iw, (int)(48 * s));
        foreach (var c in new Control[] { _codeFrame, _btnPaste, _btnPair }) c.Visible = pairing;
        _btnRepair.Visible = _btnDisconnect.Visible = !pairing;
        var cy = inner + (int)(78 * s);
        if (pairing)
        {
            _codeFrame.SetBounds(inner, cy, iw, (int)(70 * s));
            _code.SetBounds((int)(10 * s), (int)(8 * s), iw - (int)(20 * s), (int)(54 * s));
            cy += (int)(80 * s);
            _btnPaste.SetBounds(inner, cy, (int)(110 * s), (int)(36 * s));
            _btnPair.SetBounds(inner + iw - (int)(140 * s), cy, (int)(140 * s), (int)(36 * s));
            _btnPair.Enabled = PairingData.Parse(_code.Text) is not null;
            AcceptButton = _btnPair;
        }
        else
        {
            _btnRepair.SetBounds(inner, cy, (int)(140 * s), (int)(36 * s));
            _btnDisconnect.SetBounds(inner + iw - (int)(110 * s), cy, (int)(110 * s), (int)(36 * s));
            AcceptButton = null;
        }
        _connectCard.SetBounds(pad, y, w, cy + (int)(36 * s) + inner);
        y = _connectCard.Bottom + pad;

        ClientSize = new Size(ClientSize.Width, y);
        Invalidate();
    }
}
