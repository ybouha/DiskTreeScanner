using System.Drawing.Drawing2D;

namespace DiskTreeScanner.App.Ui;

/// <summary>Base for owner-drawn controls: double-buffered, no flicker.</summary>
internal abstract class PaintedControl : Control
{
    protected PaintedControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected float DpiScale => Theme.Scale(this);
}

/// <summary>Flat toolbar button: vector glyph + caption, hover and pressed states, optional toggle.</summary>
internal sealed class FlatButton : PaintedControl
{
    private bool _hover;
    private bool _pressed;
    private bool _checked;

    public FlatButton()
    {
        Cursor = Cursors.Hand;
        TabStop = false;
        BackColor = Color.White;
    }

    /// <summary>Draws the icon (graphics, x, y, scale, color).</summary>
    public Action<Graphics, float, float, float, Color>? Glyph { get; set; }

    /// <summary>Icon color when enabled (defaults to the text color).</summary>
    public Color GlyphColor { get; set; } = Theme.Text;

    public Color DisabledGlyphColor { get; set; } = Theme.InputBorder;

    public int GlyphSize { get; set; } = 14;

    /// <summary>False on narrow windows: icon only (the caption stays in the tooltip).</summary>
    public bool ShowText { get; set; } = true;

    private string Caption => ShowText ? Text : string.Empty;

    public bool Checked
    {
        get => _checked;
        set
        {
            _checked = value;
            Invalidate();
        }
    }

    public int PreferredWidthFor(Graphics? g = null)
    {
        int text = string.IsNullOrEmpty(Caption) ? 0 : TextRenderer.MeasureText(Caption, Theme.Body(this), Size.Empty, TextFormatFlags.NoPadding).Width;
        int icon = Glyph is null ? 0 : this.Px(GlyphSize);
        int gap = icon > 0 && text > 0 ? this.Px(6) : 0;
        return icon + gap + text + this.Px(text > 0 ? 20 : 18);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        if (Enabled && (_pressed || _checked))
        {
            using var back = new SolidBrush(Theme.SortedHeaderBack);
            g.FillRectangle(back, ClientRectangle);
        }
        else if (Enabled && _hover)
        {
            using var back = new SolidBrush(Theme.BarBack);
            g.FillRectangle(back, ClientRectangle);
        }
        string caption = Caption;
        int text = string.IsNullOrEmpty(caption) ? 0 : TextRenderer.MeasureText(g, caption, Theme.Body(this), Size.Empty, TextFormatFlags.NoPadding).Width;
        int icon = Glyph is null ? 0 : this.Px(GlyphSize);
        int gap = icon > 0 && text > 0 ? this.Px(6) : 0;
        int x = (Width - icon - gap - text) / 2;
        if (Glyph is not null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Glyph(g, x, (Height - icon) / 2f, DpiScale, Enabled ? GlyphColor : DisabledGlyphColor);
            g.SmoothingMode = SmoothingMode.None;
        }
        if (text > 0)
        {
            TextRenderer.DrawText(g, caption, Theme.Body(this), new Rectangle(x + icon + gap, 0, text + this.Px(4), Height), Enabled ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>Segmented switch (Auto | MB | GB, EN | FR).</summary>
internal sealed class Segmented : PaintedControl
{
    private string[] _options = [];
    private int _selected;
    private int _hover = -1;

    public Segmented()
    {
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public event EventHandler? SelectedIndexChanged;

    /// <summary>Dark variant for the title bar.</summary>
    public bool Dark { get; set; }

    public float FontPx { get; set; } = 12;

    public string[] Options
    {
        get => _options;
        set
        {
            _options = value;
            Invalidate();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
        }
    }

    public int PreferredWidthFor()
    {
        int width = 2;
        foreach (string o in _options)
            width += TextRenderer.MeasureText(o, Theme.Font(this, FontPx, FontStyle.Bold)).Width + this.Px(4);
        return width;
    }

    private Rectangle Segment(int index)
    {
        int width = Math.Max(1, (Width - 2) / Math.Max(1, _options.Length));
        return new Rectangle(1 + index * width, 1, index == _options.Length - 1 ? Width - 2 - index * width : width, Height - 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = -1;
        for (int i = 0; i < _options.Length; i++)
        {
            if (Segment(i).Contains(e.Location))
                hover = i;
        }
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        for (int i = 0; i < _options.Length; i++)
        {
            if (Segment(i).Contains(e.Location) && i != _selected)
            {
                _selected = i;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Dark ? Theme.Chrome : Color.White);
        using var border = new Pen(Dark ? Theme.ChromeBorder : Theme.InputBorder);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        Font font = Dark ? Theme.Font(this, FontPx, FontStyle.Bold) : Theme.Font(this, FontPx);
        for (int i = 0; i < _options.Length; i++)
        {
            Rectangle r = Segment(i);
            bool selected = i == _selected;
            if (selected)
            {
                using var back = new SolidBrush(Theme.Steel);
                g.FillRectangle(back, r);
            }
            else if (i == _hover)
            {
                using var back = new SolidBrush(Dark ? Theme.ChromeBorder : Theme.BarBack);
                g.FillRectangle(back, r);
            }
            if (!Dark && i > 0)
                g.DrawLine(border, r.Left - 1, 0, r.Left - 1, Height);
            Color color = selected ? Color.White : Dark ? Theme.ChromeMuted : Theme.Text;
            TextRenderer.DrawText(g, _options[i], font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>Thin progress line under the toolbar while scanning (indeterminate when the total is unknown).</summary>
internal sealed class ProgressLine : PaintedControl
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private double _fraction = -1;
    private int _phase;

    public ProgressLine()
    {
        BackColor = Color.White;
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 3) % 200;
            Invalidate();
        };
    }

    public bool Running
    {
        get => _timer.Enabled;
        set
        {
            _timer.Enabled = value;
            Invalidate();
        }
    }

    /// <summary>Progress in [0, 1], or negative for indeterminate.</summary>
    public double Fraction
    {
        get => _fraction;
        set
        {
            _fraction = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Line);
        if (!Running)
            return;
        using var fill = new SolidBrush(Theme.Steel);
        if (_fraction >= 0)
        {
            e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * _fraction), Height);
        }
        else
        {
            int w = Width / 4;
            int x = (int)((Width + w) * _phase / 200.0) - w;
            e.Graphics.FillRectangle(fill, x, 0, w, Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>"Tree View" title and its description line.</summary>
internal sealed class PageHeader : PaintedControl
{
    private string _description = string.Empty;

    public PageHeader() => BackColor = Color.White;

    public string Title { get; set; } = string.Empty;

    /// <summary>Small windows: the description only, without the "Tree View" title.</summary>
    public bool Compact { get; set; }

    public string Description
    {
        get => _description;
        set
        {
            _description = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        int y = 0;
        if (!Compact)
        {
            Font title = Theme.Font(this, 21, FontStyle.Regular, "Segoe UI Light");
            int titleHeight = TextRenderer.MeasureText(g, "Tg", title).Height;
            TextRenderer.DrawText(g, Title, title, new Point(-this.Px(1), 0), Theme.Text, TextFormatFlags.NoPrefix);
            y = titleHeight + this.Px(1);
        }
        TextRenderer.DrawText(g, Description, Theme.Body(this), new Rectangle(0, y, Width, this.Px(20)), Theme.TextSecondary,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

/// <summary>Section caption: bold label followed by a secondary sentence (FOLDERS  21 top-level entries…).</summary>
internal sealed class SectionCaption : PaintedControl
{
    private string _label = string.Empty;
    private string _detail = string.Empty;

    public SectionCaption() => BackColor = Color.White;

    public bool MonospaceDetail { get; set; }

    public void Set(string label, string detail)
    {
        _label = label;
        _detail = detail;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        Font bold = Theme.Font(this, 11, FontStyle.Bold);
        Size label = TextRenderer.MeasureText(g, _label, bold, Size.Empty, TextFormatFlags.NoPadding);
        int baseline = Height - this.Px(4);
        TextRenderer.DrawText(g, _label, bold, new Point(0, baseline - label.Height), Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        Font detail = MonospaceDetail ? Theme.Mono(this) : Theme.Small(this);
        Size size = TextRenderer.MeasureText(g, "Tg", detail, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _detail, detail, new Rectangle(label.Width + this.Px(8), baseline - size.Height + this.Px(1), Width - label.Width - this.Px(8), size.Height),
            Theme.TextSecondary, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

/// <summary>Footer line: scan summary on the left, selection on the right.</summary>
internal sealed class FooterLine : PaintedControl
{
    private string _left = string.Empty;
    private string _right = string.Empty;

    public FooterLine() => BackColor = Color.White;

    public void Set(string left, string right)
    {
        _left = left;
        _right = right;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        using var line = new Pen(Theme.LineSoft);
        g.DrawLine(line, 0, 0, Width, 0);
        var rect = new Rectangle(0, 1, Width, Height - 1);
        int rightWidth = TextRenderer.MeasureText(g, _right, Theme.Small(this)).Width;
        TextRenderer.DrawText(g, _right, Theme.Small(this), rect, Theme.TextSecondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
        TextRenderer.DrawText(g, _left, Theme.Small(this), new Rectangle(0, 1, Math.Max(0, Width - rightWidth - this.Px(16)), Height - 1), Theme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Dotted grip between the tree and the bottom panes; dragging resizes the panes.</summary>
internal sealed class GripSplitter : PaintedControl
{
    private int _dragStart = -1;
    private int _startHeight;

    public GripSplitter()
    {
        BackColor = Color.White;
        Cursor = Cursors.HSplit;
    }

    public Control? Target { get; set; }

    public int MinimumTarget { get; set; } = 120;

    /// <summary>Largest height the target may take (physical pixels), from the window layout.</summary>
    public Func<int>? MaximumTarget { get; set; }

    public event EventHandler? Resized;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Target is null)
            return;
        _dragStart = Cursor.Position.Y;
        _startHeight = Target.Height;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart < 0 || Target is null)
            return;
        int available = MaximumTarget?.Invoke() ?? (Parent is null ? 600 : Parent.ClientSize.Height - this.Px(260));
        int height = Math.Clamp(_startHeight - (Cursor.Position.Y - _dragStart), this.Px(MinimumTarget), Math.Max(this.Px(MinimumTarget), available));
        if (height != Target.Height)
        {
            Target.Height = height;
            Resized?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragStart = -1;
        Capture = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        using var pen = new Pen(Theme.TextMuted) { DashStyle = DashStyle.Dot };
        int cx = Width / 2, cy = Height / 2;
        int w = this.Px(18);
        e.Graphics.DrawLine(pen, cx - w / 2, cy - 1, cx + w / 2, cy - 1);
        e.Graphics.DrawLine(pen, cx - w / 2, cy + 2, cx + w / 2, cy + 2);
    }
}
