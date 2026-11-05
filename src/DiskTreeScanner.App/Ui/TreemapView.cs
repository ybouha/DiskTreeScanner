using System.Drawing.Drawing2D;
using DiskTreeScanner.Core.Analysis;
using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.App.Ui;

/// <summary>
/// Squarified treemap of the selected folder's children, rendered with GDI+ into a cached bitmap
/// that is rebuilt only when the selection, the metric or the size changes. Shades of steel by rank.
/// </summary>
internal sealed class TreemapView : PaintedControl
{
    private const int MaxTiles = 24;
    private readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 100 };
    private readonly List<Tile> _tiles = new();
    private Bitmap? _cache;
    private TreeModel? _model;
    private int _folder = -1;
    private bool _filesOnly;
    private bool _byAllocated = true;
    private int _hover = -1;

    public TreemapView()
    {
        BackColor = Theme.HeaderBack;
    }

    public event Action<int>? TileClicked;

    public event Action<int>? TileDoubleClicked;

    private sealed record Tile(RectangleF Rect, int Node, string Name, long Value, double Percent, Color Back, Color Fore);

    public void SetFolder(TreeModel? model, int folder, bool filesOnly, bool byAllocated)
    {
        if (model == _model && folder == _folder && filesOnly == _filesOnly && byAllocated == _byAllocated && _cache is not null)
            return;
        _model = model;
        _folder = folder;
        _filesOnly = filesOnly;
        _byAllocated = byAllocated;
        Discard();
        Invalidate();
    }

    public void RefreshLabels()
    {
        Discard();
        Invalidate();
    }

    private void Discard()
    {
        _cache?.Dispose();
        _cache = null;
        _tiles.Clear();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Discard();
    }

    private long Value(int node) => _byAllocated ? _model!.Allocated[node] : _model!.Size[node];

    private void Build(Rectangle area)
    {
        _tiles.Clear();
        if (_model is null || _folder < 0 || area.Width < 8 || area.Height < 8)
            return;
        TreeModel m = _model;
        var children = new List<int>();
        foreach (int c in m.Children(_folder))
        {
            if (_filesOnly && m.IsDirectory(c))
                continue;
            if (Value(c) > 0)
                children.Add(c);
        }
        children.Sort((a, b) => Value(b).CompareTo(Value(a)));
        long total = 0;
        foreach (int c in children)
            total += Value(c);
        if (total <= 0)
            return;

        // Keep tiles big enough to read; the tail becomes "Other (n)".
        double areaPerByte = area.Width * (double)area.Height / total;
        double minimumArea = 34 * DpiScale * 26 * DpiScale;
        int shown = 0;
        while (shown < children.Count && shown < MaxTiles && Value(children[shown]) * areaPerByte >= minimumArea)
            shown++;
        if (shown == 0)
            shown = Math.Min(children.Count, 1);
        long other = 0;
        for (int i = shown; i < children.Count; i++)
            other += Value(children[i]);
        var values = new List<double>(shown + 1);
        for (int i = 0; i < shown; i++)
            values.Add(Value(children[i]));
        int otherCount = children.Count - shown;
        if (otherCount > 0)
            values.Add(other);
        // Squarify expects decreasing values: "Other" may be larger than the last tile, keep it last anyway.
        RectangleF[] rects = Treemap.Squarify(values, area);
        Color[] shades = Theme.TreemapShades;
        for (int i = 0; i < values.Count; i++)
        {
            bool isOther = i == shown;
            int rank = isOther ? shades.Length - 1 : Math.Min(i, shades.Length - 2);
            Color back = shades[rank];
            Color fore = rank <= 2 ? Color.White : Theme.Text;
            string name = isOther ? L.F("Other", otherCount) : m.GetName(children[i]);
            _tiles.Add(new Tile(rects[i], isOther ? -1 : children[i], name, (long)values[i], 100.0 * values[i] / total, back, fore));
        }
    }

    private void Render()
    {
        Discard();
        if (Width <= 0 || Height <= 0)
            return;
        // Opaque pixel format: GDI (ClearType) text smears on bitmaps with an alpha channel.
        var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Theme.HeaderBack);
            int pad = this.Px(5);
            var area = new Rectangle(pad, pad, Width - 2 * pad, Height - 2 * pad);
            Build(area);
            using var white = new Pen(Color.White);
            // GDI+ anti-aliased text: GDI ClearType smears when drawn into a memory bitmap.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            foreach (Tile tile in _tiles)
            {
                Rectangle r = Rectangle.Round(tile.Rect);
                using (var back = new SolidBrush(tile.Back))
                    g.FillRectangle(back, r);
                g.DrawRectangle(white, r.X, r.Y, r.Width - 1, r.Height - 1);
                int tx = r.X + this.Px(6), ty = r.Y + this.Px(4);
                int textWidth = r.Width - this.Px(10);
                if (textWidth < this.Px(16) || r.Height < this.Px(18))
                    continue;
                Font title = Theme.Font(this, 12, FontStyle.Bold);
                int titleHeight = (int)Math.Ceiling(title.GetHeight(g));
                using var fore = new SolidBrush(tile.Fore);
                g.DrawString(tile.Name, title, fore, new RectangleF(tx, ty, textWidth, titleHeight), format);
                if (r.Height >= this.Px(36))
                {
                    string size = tile.Percent >= 10 ? $"{Fmt.Size(tile.Value)} · {Fmt.Percent(tile.Percent)}" : Fmt.Size(tile.Value);
                    g.DrawString(size, Theme.Font(this, 11.5f), fore, new RectangleF(tx, ty + titleHeight, textWidth, titleHeight), format);
                }
            }
        }
        _cache = bitmap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_cache is null || _cache.Size != ClientSize)
            Render();
        if (_cache is not null)
            e.Graphics.DrawImageUnscaled(_cache, 0, 0);
        if (_hover >= 0 && _hover < _tiles.Count)
        {
            using var pen = new Pen(Theme.Chrome, 2 * DpiScale);
            Rectangle r = Rectangle.Round(_tiles[_hover].Rect);
            e.Graphics.DrawRectangle(pen, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
        }
        using var border = new Pen(Theme.LineStrong);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private int TileAt(Point p) => _tiles.FindIndex(t => t.Rect.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = TileAt(e.Location);
        if (hover == _hover)
            return;
        _hover = hover;
        Invalidate();
        if (hover >= 0)
        {
            Tile t = _tiles[hover];
            _tip.SetToolTip(this, $"{t.Name}\n{Fmt.Size(t.Value)} · {Fmt.Percent(t.Percent)}");
        }
        else
        {
            _tip.SetToolTip(this, null);
        }
        Cursor = hover >= 0 && _tiles[hover].Node >= 0 ? Cursors.Hand : Cursors.Default;
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
        int index = TileAt(e.Location);
        if (index >= 0 && _tiles[index].Node >= 0)
            TileClicked?.Invoke(_tiles[index].Node);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int index = TileAt(e.Location);
        if (index >= 0 && _tiles[index].Node >= 0)
            TileDoubleClicked?.Invoke(_tiles[index].Node);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Discard();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
