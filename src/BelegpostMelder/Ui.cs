using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BelegpostMelder;

/// <summary>Farben und Schriften wie im Belegpost-Portal.</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0xF4, 0xF5, 0xF7);
    public static readonly Color Surface = Color.White;
    public static readonly Color Surface2 = Color.FromArgb(0xF8, 0xF9, 0xFB);
    public static readonly Color Border = Color.FromArgb(0xE3, 0xE6, 0xEB);
    public static readonly Color Text = Color.FromArgb(0x1C, 0x23, 0x30);
    public static readonly Color Muted = Color.FromArgb(0x66, 0x70, 0x85);
    public static readonly Color Accent = Color.FromArgb(0x1F, 0x5F, 0xBF);
    public static readonly Color AccentHover = Color.FromArgb(0x1A, 0x51, 0xA3);
    public static readonly Color AccentSoft = Color.FromArgb(0xE8, 0xF0, 0xFC);
    public static readonly Color Dark = Color.FromArgb(0x16, 0x20, 0x2E);
    public static readonly Color Ok = Color.FromArgb(0x3F, 0x9B, 0x62);
    public static readonly Color OkSoft = Color.FromArgb(0xE6, 0xF4, 0xEB);
    public static readonly Color Warn = Color.FromArgb(0xE3, 0xA5, 0x2B);
    public static readonly Color WarnSoft = Color.FromArgb(0xFD, 0xF4, 0xE1);
    public static readonly Color Bad = Color.FromArgb(0xD1, 0x45, 0x3B);
    public static readonly Color BadSoft = Color.FromArgb(0xFB, 0xEA, 0xE8);

    private static readonly string Family = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static Font Body => new(Family, 9.75f);
    public static Font Small => new(Family, 8.75f);
    public static Font Strong => new(Family, 9.75f, FontStyle.Bold);
    public static Font Title => new(FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI Semibold", 13f, FontStyle.Bold);
    public static Font Mono => new("Consolas", 9f);

    private static bool FontExists(string name)
    {
        using var f = new Font(name, 10f);
        return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static float Scale(Control c) => c.DeviceDpi / 96f;
}

/// <summary>Windows 11: abgerundete Fensterecken und Schatten (unter Windows 10 ohne Wirkung).</summary>
internal static class Dwm
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void RoundCorners(Form f, bool small = false)
    {
        try { var v = small ? 3 : 2; DwmSetWindowAttribute(f.Handle, 33, ref v, sizeof(int)); } catch { }
    }
}

/// <summary>Flache, abgerundete Schaltfläche (Primär, Sekundär oder Text).</summary>
internal sealed class ModernButton : Button
{
    public enum Kinds { Primary, Secondary, Ghost }
    public Kinds Kind { get; set; } = Kinds.Secondary;
    private bool _hover, _down;

    public ModernButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.Strong;
        Height = 36;
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        Theme.Smooth(g);
        var s = Theme.Scale(this);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color bg, fg, border;
        switch (Kind)
        {
            case Kinds.Primary:
                bg = !Enabled ? Color.FromArgb(0xA9, 0xBF, 0xE3) : _down ? Color.FromArgb(0x16, 0x47, 0x8F) : _hover ? Theme.AccentHover : Theme.Accent;
                fg = Color.White; border = bg; break;
            case Kinds.Ghost:
                bg = _hover ? Theme.AccentSoft : Parent?.BackColor ?? Theme.Surface;
                fg = Enabled ? Theme.Accent : Theme.Muted; border = bg; break;
            default:
                bg = _down ? Theme.Border : _hover ? Theme.Surface2 : Theme.Surface;
                fg = Enabled ? Theme.Text : Theme.Muted; border = Theme.Border; break;
        }
        using (var path = Theme.Round(r, 8 * s))
        {
            using var b = new SolidBrush(bg); g.FillPath(b, path);
            using var p = new Pen(border); g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(Color.FromArgb(120, Theme.Accent), 2 * s);
            using var path = Theme.Round(RectangleF.Inflate(r, -2 * s, -2 * s), 6 * s);
            g.DrawPath(fp, path);
        }
    }
}

/// <summary>Schalter (wie in den Windows-Einstellungen) mit Beschriftung.</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    public event EventHandler? CheckedChanged;
    public string Hint { get; set; } = "";

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        Height = 44;
        TabStop = true;
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        Theme.Smooth(g);
        var s = Theme.Scale(this);
        float w = 40 * s, h = 22 * s;
        var track = new RectangleF(Width - w - 2 * s, (Height - h) / 2, w, h);
        using (var path = Theme.Round(track, h / 2))
        {
            using var b = new SolidBrush(_checked ? Theme.Accent : Color.FromArgb(0xC7, 0xCD, 0xD6)); g.FillPath(b, path);
            if (Focused) { using var fp = new Pen(Color.FromArgb(90, Theme.Accent), 3 * s); g.DrawPath(fp, path); }
        }
        var d = h - 6 * s;
        var knob = new RectangleF(_checked ? track.Right - d - 3 * s : track.X + 3 * s, track.Y + 3 * s, d, d);
        using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, knob);

        var textRect = new Rectangle(0, 0, (int)(track.X - 12 * s), Height);
        if (string.IsNullOrEmpty(Hint))
            TextRenderer.DrawText(g, Text, Font, textRect, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        else
        {
            using var small = Theme.Small;
            var half = Height / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, textRect.Width, half + 2), Theme.Text, TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Hint, small, new Rectangle(0, half + 2, textRect.Width, half - 2), Theme.Muted, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>Kleines farbiges Status-Etikett („verbunden“, „getrennt“ …).</summary>
internal sealed class Pill : Control
{
    public Color Fore { get; set; } = Theme.Ok;
    public Color Back { get; set; } = Theme.OkSoft;

    public Pill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Small;
        Height = 24;
    }

    public void Set(string text, Color fore, Color back)
    {
        Text = text; Fore = fore; Back = back;
        var s = Theme.Scale(this);
        Width = TextRenderer.MeasureText(text, Font).Width + (int)(30 * s);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        Theme.Smooth(g);
        var s = Theme.Scale(this);
        using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
        using (var b = new SolidBrush(Back)) g.FillPath(b, path);
        var dot = 7 * s;
        using (var db = new SolidBrush(Fore)) g.FillEllipse(db, 10 * s, (Height - dot) / 2, dot, dot);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(22 * s), 0, Width - (int)(26 * s), Height), Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>Weiße Karte mit abgerundeten Ecken und feinem Rand.</summary>
internal sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        Theme.Smooth(g);
        var s = Theme.Scale(this);
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10 * s);
        using var b = new SolidBrush(Theme.Surface); g.FillPath(b, path);
        using var p = new Pen(Theme.Border); g.DrawPath(p, path);
    }
}

/// <summary>Kontextmenü im Infobereich: flach, hell, mit Abstand – passend zu Windows 11.</summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public MenuRenderer() : base(new Colors()) { RoundedEdges = true; }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics; Theme.Smooth(g);
        var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = Theme.Round(r, 5);
        using var b = new SolidBrush(Theme.AccentSoft); g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics; Theme.Smooth(g);
        var r = e.ImageRectangle;
        using var p = new Pen(Theme.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(p, new[] { new PointF(r.Left + 3, r.Top + r.Height / 2f), new PointF(r.Left + r.Width * 0.42f, r.Bottom - 4), new PointF(r.Right - 3, r.Top + 4) });
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Surface;
        public override Color MenuItemSelected => Theme.AccentSoft;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}

/// <summary>Auswahl aus wenigen Möglichkeiten als Segmente (wie Tabs), z. B. „Portal | Outlook | Nur Hinweis“.</summary>
internal sealed class Segmented : Control
{
    private (string Key, string Text)[] _items = Array.Empty<(string, string)>();
    private string _value = "";
    private int _hover = -1;
    public event EventHandler? ValueChanged;

    public Segmented()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        Height = 38;
    }

    public void SetItems(params (string Key, string Text)[] items) { _items = items; Invalidate(); }

    public string Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }

    private RectangleF Seg(int i)
    {
        var s = Theme.Scale(this);
        var w = (Width - 8 * s) / Math.Max(1, _items.Length);
        return new RectangleF(4 * s + i * w, 4 * s, w, Height - 8 * s);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Enumerable.Range(0, _items.Length).FirstOrDefault(i => Seg(i).Contains(e.Location), -1);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        for (var i = 0; i < _items.Length; i++) if (Seg(i).Contains(e.Location)) Value = _items[i].Key;
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        Theme.Smooth(g);
        var s = Theme.Scale(this);
        using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 9 * s))
        using (var b = new SolidBrush(Theme.Surface2)) { g.FillPath(b, path); using var p = new Pen(Theme.Border); g.DrawPath(p, path); }
        for (var i = 0; i < _items.Length; i++)
        {
            var r = Seg(i);
            var active = _items[i].Key == _value;
            if (active || i == _hover)
            {
                using var path = Theme.Round(r, 7 * s);
                using var b = new SolidBrush(active ? Theme.Accent : Theme.AccentSoft);
                g.FillPath(b, path);
            }
            using var f = active ? Theme.Strong : Theme.Body;
            TextRenderer.DrawText(g, _items[i].Text, f, Rectangle.Round(r), active ? Color.White : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}
