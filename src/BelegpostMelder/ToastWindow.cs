namespace BelegpostMelder;

/// <summary>
/// Eigene Meldung unten rechts (statt der alten Sprechblase): Titel, Text, „Öffnen“, schließt sich nach einigen Sekunden.
/// Fährt mit der Maus darüber, bleibt sie stehen.
/// </summary>
internal sealed class ToastWindow : Form
{
    public enum Kinds { Info, Ok, Warn, Bad }

    private static ToastWindow? _current;
    private readonly string _title, _text;
    private readonly Kinds _kind;
    private readonly Action? _open;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private readonly ModernButton _btnOpen, _btnClose;
    private Rectangle _closeRect;
    private int _ticks, _life;
    private bool _hover, _closing;

    public static void Show(string title, string text, Kinds kind, Action? open, string? openText = "Öffnen", int seconds = 10,
        Action? second = null, string? secondText = null)
    {
        _current?.CloseNow();
        _current = new ToastWindow(title, text, kind, open, openText, seconds, second, secondText);
        _current.Show();
    }

    private ToastWindow(string title, string text, Kinds kind, Action? open, string? openText, int seconds, Action? second, string? secondText)
    {
        _title = title; _text = text; _kind = kind; _open = open; _life = seconds * 1000 / _timer.Interval;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.None;
        Opacity = 0;

        var s = DeviceDpi / 96f;
        var w = (int)(372 * s);
        // Höhe nach Textlänge
        using var g = CreateGraphics();
        var textH = TextRenderer.MeasureText(g, text, Theme.Body, new Size(w - (int)(76 * s), 1000), TextFormatFlags.WordBreak).Height;
        textH = Math.Min(textH, (int)(60 * s));
        var h = (int)(96 * s) + textH + (open is null ? 0 : (int)(44 * s));
        Size = new Size(w, h);
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - w - (int)(16 * s), area.Bottom - h - (int)(16 * s));

        _btnOpen = new ModernButton { Kind = ModernButton.Kinds.Primary, Text = openText ?? "Öffnen", Visible = open is not null };
        _btnClose = new ModernButton { Kind = second is null ? ModernButton.Kinds.Ghost : ModernButton.Kinds.Secondary, Text = secondText ?? "Schließen", Visible = open is not null };
        var by = h - (int)(50 * s);
        _btnOpen.SetBounds((int)(60 * s), by, (int)(120 * s), (int)(34 * s));
        _btnClose.SetBounds((int)(188 * s), by, (int)(130 * s), (int)(34 * s));
        _btnOpen.Click += (_, _) => { CloseNow(); _open?.Invoke(); };
        _btnClose.Click += (_, _) => { CloseNow(); second?.Invoke(); };
        Controls.Add(_btnOpen); Controls.Add(_btnClose);

        _timer.Tick += (_, _) => Tick();
        MouseEnter += (_, _) => _hover = true;
        MouseLeave += (_, _) => _hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
        MouseMove += (_, e) => Cursor = _closeRect.Contains(e.Location) || _open is not null && e.Y < by ? Cursors.Hand : Cursors.Default;
        MouseClick += (_, e) =>
        {
            if (_closeRect.Contains(e.Location)) CloseNow();
            else if (_open is not null && e.Y < by) { CloseNow(); _open(); }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW (nicht in Alt+Tab), WS_EX_NOACTIVATE
            cp.ClassStyle |= 0x20000;        // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Dwm.RoundCorners(this);
        _timer.Start();
    }

    private void Tick()
    {
        if (_closing)
        {
            Opacity -= 0.12;
            if (Opacity <= 0.05) { _timer.Stop(); Close(); }
            return;
        }
        if (Opacity < 1) { Opacity = Math.Min(1, Opacity + 0.15); return; }
        if (_hover || ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
        if (++_ticks >= _life) _closing = true;
        if (_ticks % 5 == 0) Invalidate(new Rectangle(0, Height - 6, Width, 6));
    }

    private void CloseNow()
    {
        if (_current == this) _current = null;
        _closing = true;
        if (!_timer.Enabled) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Dispose(); base.OnFormClosed(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var s = DeviceDpi / 96f;
        var (fore, back) = _kind switch
        {
            Kinds.Ok => (Theme.Ok, Theme.OkSoft),
            Kinds.Warn => (Theme.Warn, Theme.WarnSoft),
            Kinds.Bad => (Theme.Bad, Theme.BadSoft),
            _ => (Theme.Accent, Theme.AccentSoft)
        };
        using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        using (var bar = new SolidBrush(fore)) g.FillRectangle(bar, 0, 0, (int)(4 * s), Height);

        // Symbol: Kreis in der Statusfarbe mit Dokument
        var ic = new RectangleF(18 * s, 18 * s, 30 * s, 30 * s);
        using (var bb = new SolidBrush(back)) g.FillEllipse(bb, ic);
        using (var pen = new Pen(fore, 1.6f * s))
        {
            var d = new RectangleF(ic.X + 9.5f * s, ic.Y + 7.5f * s, 11 * s, 15 * s);
            g.DrawRectangle(pen, d.X, d.Y, d.Width, d.Height);
            g.DrawLine(pen, d.X + 2.5f * s, d.Y + 5 * s, d.Right - 2.5f * s, d.Y + 5 * s);
            g.DrawLine(pen, d.X + 2.5f * s, d.Y + 8.5f * s, d.Right - 2.5f * s, d.Y + 8.5f * s);
        }

        using var small = Theme.Small;
        using var strong = Theme.Strong;
        var x = (int)(60 * s);
        TextRenderer.DrawText(g, "Belegpost", small, new Rectangle(x, (int)(14 * s), Width - x - (int)(40 * s), (int)(18 * s)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _title, strong, new Rectangle(x, (int)(32 * s), Width - x - (int)(16 * s), (int)(22 * s)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        var textBottom = _open is null ? Height - (int)(12 * s) : Height - (int)(58 * s);
        TextRenderer.DrawText(g, _text, Theme.Body, new Rectangle(x, (int)(56 * s), Width - x - (int)(16 * s), textBottom - (int)(56 * s)), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        // Schließen-Kreuz
        _closeRect = new Rectangle(Width - (int)(34 * s), (int)(10 * s), (int)(22 * s), (int)(22 * s));
        using var xp = new Pen(Theme.Muted, 1.4f * s);
        var c = _closeRect; var m = 7 * s;
        g.DrawLine(xp, c.Left + m, c.Top + m, c.Right - m, c.Bottom - m);
        g.DrawLine(xp, c.Right - m, c.Top + m, c.Left + m, c.Bottom - m);

        // Fortschritt bis zum Ausblenden
        if (_life > 0 && Opacity >= 1)
        {
            var frac = 1f - (float)_ticks / _life;
            using var pb = new SolidBrush(Color.FromArgb(60, fore));
            g.FillRectangle(pb, 4 * s, Height - 3 * s, (Width - 4 * s) * frac, 3 * s);
        }
    }
}
