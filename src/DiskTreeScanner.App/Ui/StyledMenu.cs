using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DiskTreeScanner.App.Ui;

internal enum MenuGlyph
{
    Open,
    Explorer,
    Properties,
    Copy,
    Filter,
    ClearFilter,
    DrillIn,
    GoUp,
    Export,
}

/// <summary>
/// Menu look shared by the title-bar menus and the context menu: roomy rows, rounded steel-tinted
/// highlight, thin separators, grey shortcuts on the right, faded disabled items, line icons.
/// </summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    public static readonly Color Highlight = Theme.Hex("#E3EAF3");
    public static readonly Color DisabledText = Theme.Hex("#A9B2BE");

    public MenuRenderer() : base(new Colors())
    {
        RoundedEdges = false;
    }

    /// <summary>Applies the row height and padding of the style to the menu's items (call after filling it).</summary>
    public static void Style(ToolStripDropDownMenu menu, Control dpiSource)
    {
        menu.Font = Theme.Body(dpiSource);
        menu.ShowImageMargin = true;
        menu.ImageScalingSize = new Size(dpiSource.Px(16), dpiSource.Px(16));
        menu.Padding = new Padding(0, menu.Items.Count > 0 && menu.Items[0] is MenuHeaderItem ? 0 : dpiSource.Px(4), 0, dpiSource.Px(4));
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is MenuHeaderItem header)
                header.PrepareLayout(dpiSource);
            else if (item is ToolStripMenuItem m)
            {
                m.Padding = new Padding(0, dpiSource.Px(3), 0, dpiSource.Px(3));
                if (m.HasDropDownItems && m.DropDown is ToolStripDropDownMenu sub)
                {
                    sub.Renderer = menu.Renderer;
                    Style(sub, dpiSource);
                }
            }
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Color.White);
        // The steel band of a header spans the whole menu, margins included.
        if (e.ToolStrip.Items.Count > 0 && e.ToolStrip.Items[0] is MenuHeaderItem header)
        {
            using var steel = new SolidBrush(Theme.Steel);
            e.Graphics.FillRectangle(steel, 0, 0, e.ToolStrip.Width, header.Bounds.Bottom);
        }
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
            return;
        }
        using var pen = new Pen(Theme.LineStrong);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is MenuHeaderItem || !e.Item.Selected || !e.Item.Enabled || e.Item.Owner is not ToolStripDropDown)
            return;
        float s = e.Item.Owner.DeviceDpi / 96f * Theme.ScaleOverride;
        var rect = new RectangleF(4 * s, 1, e.Item.Width - 8 * s, e.Item.Height - 2);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        Icons.AddRoundedRect(path, rect.X, rect.Y, rect.Width, rect.Height, 4 * s);
        using var brush = new SolidBrush(Highlight);
        g.FillPath(brush, path);
        g.SmoothingMode = SmoothingMode.None;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = (e.Item.Owner?.DeviceDpi ?? 96) / 96f * Theme.ScaleOverride;
        int y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawLine(pen, 10 * s, y, e.Item.Width - 10 * s, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool shortcut = e.Item is ToolStripMenuItem m && e.Text is { Length: > 0 } && e.Text == m.ShortcutKeyDisplayString;
        Color color = !e.Item.Enabled ? DisabledText : shortcut ? Theme.TextMuted : e.Item.ForeColor == Theme.StopActive ? Theme.StopActive : Theme.Text;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Item is null || e.Item.Owner is not ToolStripDropDown)
        {
            base.OnRenderArrow(e);
            return;
        }
        float s = e.Item.Owner.DeviceDpi / 96f * Theme.ScaleOverride;
        Rectangle r = e.ArrowRectangle;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(e.Item.Enabled ? Theme.TextSecondary : DisabledText, 1.3f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 1.5f * s, cy - 3.5f * s), new PointF(cx + 2 * s, cy), new PointF(cx - 1.5f * s, cy + 3.5f * s) });
        g.SmoothingMode = SmoothingMode.None;
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null)
            return;
        if (e.Item is not null && !e.Item.Enabled)
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.4f });
            Rectangle r = e.ImageRectangle;
            e.Graphics.DrawImage(e.Image, r, 0, 0, e.Image.Width, e.Image.Height, GraphicsUnit.Pixel, attributes);
            return;
        }
        e.Graphics.DrawImage(e.Image, e.ImageRectangle);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        float s = (e.Item?.Owner?.DeviceDpi ?? 96) / 96f * Theme.ScaleOverride;
        Rectangle r = e.ImageRectangle;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Steel, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 4.5f * s, cy), new PointF(cx - 1.5f * s, cy + 3 * s), new PointF(cx + 4.5f * s, cy - 3.5f * s) });
        g.SmoothingMode = SmoothingMode.None;
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemBorder => Highlight;
        public override Color MenuBorder => Theme.LineStrong;
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Color.White;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}

/// <summary>
/// Steel header at the top of the context menu: item name, a subtitle (location and size) and a
/// pill with a colored dot giving the kind of item. Not selectable. A menu item (not a plain
/// ToolStripItem) so that the menu gives it the full width instead of indenting it past the
/// image margin.
/// </summary>
internal sealed class MenuHeaderItem : ToolStripMenuItem
{
    private readonly string _title;
    private readonly string _subtitle;
    private readonly string _pill;
    private readonly Color _dot;

    public MenuHeaderItem(string title, string subtitle, string pill, Color dot)
    {
        _title = title;
        _subtitle = subtitle;
        _pill = pill;
        _dot = dot;
        AutoSize = true;
    }

    public override bool CanSelect => false;

    /// <summary>
    /// The menu sizes itself from its items' text and shortcut, not from <see cref="GetPreferredSize"/>:
    /// give it the title and, as the "shortcut", the pill padded to the width the header needs.
    /// </summary>
    public void PrepareLayout(Control dpiSource)
    {
        float s = Theme.Scale(dpiSource);
        Font = Theme.Font(dpiSource, 14, FontStyle.Bold);
        Font pillFont = Theme.Font(dpiSource, 11.5f, FontStyle.Bold);
        int title = TextRenderer.MeasureText(_title, Font).Width;
        int pill = TextRenderer.MeasureText(_pill, pillFont).Width;
        int subtitle = TextRenderer.MeasureText(_subtitle, Theme.Font(dpiSource, 12)).Width;
        int needed = Math.Clamp(Math.Max(title + pill + (int)(60 * s), subtitle + (int)(40 * s)), (int)(270 * s), (int)(440 * s));
        Font body = Theme.Body(dpiSource);
        int space = Math.Max(1, TextRenderer.MeasureText("  ", body).Width - TextRenderer.MeasureText(" ", body).Width);
        int padding = Math.Max(0, (needed - title - TextRenderer.MeasureText(_pill, body).Width - (int)(60 * s)) / space);
        Text = _title;
        ShortcutKeyDisplayString = _pill + new string(' ', padding);
        ShowShortcutKeys = true;
    }

    private float S => (Owner?.DeviceDpi ?? 96) / 96f * Theme.ScaleOverride;

    private Control Fonts => Owner ?? throw new InvalidOperationException("The header must belong to a menu.");

    public override Size GetPreferredSize(Size constrainingSize)
    {
        float s = S;
        int title = TextRenderer.MeasureText(_title, Theme.Font(Fonts, 14, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width;
        int subtitle = TextRenderer.MeasureText(_subtitle, Theme.Font(Fonts, 12), Size.Empty, TextFormatFlags.NoPadding).Width;
        int pill = PillWidth();
        int width = Math.Clamp(Math.Max(title + pill + (int)(36 * s), subtitle + (int)(28 * s)), (int)(270 * s), (int)(440 * s));
        // The menu does not stretch this item: take the width of the widest other item.
        if (Owner is not null)
        {
            foreach (ToolStripItem item in Owner.Items)
            {
                if (item != this && item.Available)
                    width = Math.Max(width, item.GetPreferredSize(Size.Empty).Width);
            }
        }
        return new Size(width, (int)(56 * s));
    }

    private int PillWidth() => _pill.Length == 0 ? 0 : TextRenderer.MeasureText(_pill, Theme.Font(Fonts, 11.5f, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width + (int)(26 * S);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        float s = S;
        int width = Width;
        using (var back = new SolidBrush(Theme.Steel))
            g.FillRectangle(back, 0, 0, width, Height);
        int left = (int)(14 * s);
        int pill = PillWidth();
        int pillHeight = (int)(20 * s);
        int titleTop = (int)(9 * s);
        int titleHeight = (int)(20 * s);
        var titleRect = new Rectangle(left, titleTop, width - left - pill - (int)(22 * s), titleHeight);
        TextRenderer.DrawText(g, _title, Theme.Font(Fonts, 14, FontStyle.Bold), titleRect, Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        var subtitleRect = new Rectangle(left, titleTop + titleHeight + (int)(2 * s), width - 2 * left, (int)(18 * s));
        TextRenderer.DrawText(g, _subtitle, Theme.Font(Fonts, 12), subtitleRect, Theme.ChromeMenu,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
        if (pill == 0)
            return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float px = width - pill - 12 * s, py = titleTop + (titleHeight - pillHeight) / 2f;
        using (var path = new GraphicsPath())
        {
            Icons.AddRoundedRect(path, px, py, pill, pillHeight, pillHeight / 2f);
            using var fill = new SolidBrush(Color.FromArgb(48, 255, 255, 255));
            g.FillPath(fill, path);
        }
        float dot = 7 * s;
        using (var brush = new SolidBrush(_dot))
            g.FillEllipse(brush, px + 9 * s, py + (pillHeight - dot) / 2, dot, dot);
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, _pill, Theme.Font(Fonts, 11.5f, FontStyle.Bold), new Rectangle((int)(px + 20 * s), (int)py, pill - (int)(22 * s), pillHeight), Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

/// <summary>16 px line icons of the menus, rendered once per DPI.</summary>
internal static class MenuGlyphs
{
    private static readonly Dictionary<(MenuGlyph, int), Bitmap> Cache = new();

    public static Bitmap Get(MenuGlyph glyph, Control dpiSource)
    {
        int dpi = Theme.Dpi(dpiSource);
        if (Cache.TryGetValue((glyph, dpi), out Bitmap? bitmap))
            return bitmap;
        float s = dpi / 96f;
        int size = (int)Math.Ceiling(16 * s);
        bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Draw(g, glyph, s, Theme.TextSecondary);
        }
        Cache[(glyph, dpi)] = bitmap;
        return bitmap;
    }

    private static void Draw(Graphics g, MenuGlyph glyph, float s, Color color)
    {
        using var pen = new Pen(color, 1.35f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF P(float x, float y) => new(x * s, y * s);
        switch (glyph)
        {
            case MenuGlyph.Open:
                Icons.Export(g, 1 * s, 1 * s, s, color);
                break;
            case MenuGlyph.Explorer:
                Icons.FolderOutline(g, 1 * s, 1 * s, s, color);
                break;
            case MenuGlyph.Properties:
                g.DrawEllipse(pen, 2 * s, 2 * s, 12 * s, 12 * s);
                g.DrawLine(pen, P(8, 7.2f), P(8, 11.2f));
                using (var dot = new SolidBrush(color))
                    g.FillEllipse(dot, 7.1f * s, 4.2f * s, 1.8f * s, 1.8f * s);
                break;
            case MenuGlyph.Copy:
                using (var back = new GraphicsPath())
                {
                    back.AddLines(new[] { P(5.5f, 4), P(5.5f, 2.5f), P(13.5f, 2.5f), P(13.5f, 10.5f), P(12, 10.5f) });
                    g.DrawPath(pen, back);
                }
                using (var front = new GraphicsPath())
                {
                    Icons.AddRoundedRect(front, 2.5f * s, 5.5f * s, 8 * s, 8 * s, 1.2f * s);
                    g.DrawPath(pen, front);
                }
                break;
            case MenuGlyph.Filter:
                Icons.Funnel(g, 1 * s, 1 * s, s, color);
                break;
            case MenuGlyph.ClearFilter:
                Icons.Funnel(g, 0, 1 * s, 0.9f * s, color);
                g.DrawLine(pen, P(10.5f, 9.5f), P(14.5f, 13.5f));
                g.DrawLine(pen, P(14.5f, 9.5f), P(10.5f, 13.5f));
                break;
            case MenuGlyph.DrillIn:
                g.DrawLines(pen, new[] { P(10, 2.5f), P(13.5f, 2.5f), P(13.5f, 13.5f), P(10, 13.5f) });
                g.DrawLine(pen, P(2.5f, 8), P(9.5f, 8));
                g.DrawLines(pen, new[] { P(6.8f, 5.3f), P(9.5f, 8), P(6.8f, 10.7f) });
                break;
            case MenuGlyph.GoUp:
                g.DrawLine(pen, P(8, 13.5f), P(8, 3));
                g.DrawLines(pen, new[] { P(4.2f, 6.8f), P(8, 3), P(11.8f, 6.8f) });
                break;
            case MenuGlyph.Export:
                g.DrawLine(pen, P(8, 2.5f), P(8, 10));
                g.DrawLines(pen, new[] { P(5, 7), P(8, 10), P(11, 7) });
                g.DrawLines(pen, new[] { P(2.5f, 10.5f), P(2.5f, 13.5f), P(13.5f, 13.5f), P(13.5f, 10.5f) });
                break;
        }
    }
}
