namespace DiskTreeScanner.App.Ui;

/// <summary>A KPI tile: caption, value, detail, optional gauge.</summary>
internal sealed record Kpi(string Caption, string Value, string Detail, Color? ValueColor = null, double Gauge = -1, Color? GaugeColor = null);

/// <summary>The six KPI tiles (Selection, Total space, Space used, Space free, Items, Last scan).</summary>
internal sealed class KpiTiles : PaintedControl
{
    private readonly ToolTip _tip = new() { InitialDelay = 400 };
    private Kpi[] _tiles = [];
    private int _hover = -1;

    public KpiTiles() => BackColor = Color.White;

    /// <summary>Full text in a tooltip: narrow windows truncate the details.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = -1;
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (TileRect(i).Contains(e.Location))
                hover = i;
        }
        if (hover == _hover)
            return;
        _hover = hover;
        _tip.SetToolTip(this, hover >= 0 ? $"{_tiles[hover].Caption}\n{_tiles[hover].Value}  {_tiles[hover].Detail}" : null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The highlighted tile (2 px dark border).</summary>
    public int ActiveIndex { get; set; }

    public Kpi[] Tiles
    {
        get => _tiles;
        set
        {
            _tiles = value;
            Invalidate();
        }
    }

    public event Action<int>? TileClicked;

    private Rectangle TileRect(int i)
    {
        int gap = this.Px(8);
        int count = Math.Max(1, _tiles.Length);
        int width = (Width - gap * (count - 1)) / count;
        return new Rectangle(i * (width + gap), 0, width, Height - 1);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (TileRect(i).Contains(e.Location))
                TileClicked?.Invoke(i);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        using var back = new SolidBrush(Theme.TileBack);
        Font caption = Theme.Font(this, 11, FontStyle.Bold);
        Font value = Theme.Font(this, 16, FontStyle.Bold);
        Font detail = Theme.Small(this);
        TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        for (int i = 0; i < _tiles.Length; i++)
        {
            Kpi tile = _tiles[i];
            Rectangle r = TileRect(i);
            g.FillRectangle(back, r);
            bool active = i == ActiveIndex;
            using (var border = new Pen(active ? Theme.Chrome : Theme.LineStrong, active ? this.Px(2) : 1))
            {
                if (active)
                    g.DrawRectangle(border, r.X + this.Px(1), r.Y + this.Px(1), r.Width - this.Px(2), r.Height - this.Px(2));
                else
                    g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            bool tight = r.Height < this.Px(54);
            int x = r.X + (r.Width < this.Px(180) ? this.Px(8) : this.Px(12));
            int y = r.Y + (tight ? this.Px(4) : this.Px(6));
            TextRenderer.DrawText(g, tile.Caption, caption, new Rectangle(x, y, r.Right - x - this.Px(4), this.Px(16)), Theme.TextSecondary, flags | TextFormatFlags.EndEllipsis);
            y += TextRenderer.MeasureText(g, "Tg", caption, Size.Empty, flags).Height + (tight ? 0 : this.Px(2));
            Size valueSize = TextRenderer.MeasureText(g, tile.Value, value, Size.Empty, flags);
            int available = r.Right - x - this.Px(8);
            TextRenderer.DrawText(g, tile.Value, value, new Rectangle(x, y, Math.Min(valueSize.Width, available), valueSize.Height), tile.ValueColor ?? Theme.Text, flags | TextFormatFlags.EndEllipsis);
            int detailX = x + valueSize.Width + this.Px(8);
            int detailHeight = TextRenderer.MeasureText(g, "Tg", detail, Size.Empty, flags).Height;
            if (detailX < r.Right - this.Px(16))
            {
                TextRenderer.DrawText(g, tile.Detail, detail, new Rectangle(detailX, y + valueSize.Height - detailHeight - this.Px(1), r.Right - detailX - this.Px(8), detailHeight),
                    Theme.TextSecondary, flags | TextFormatFlags.EndEllipsis);
            }
            if (tile.Gauge >= 0)
            {
                int gy = Math.Min(y + valueSize.Height + this.Px(3), r.Bottom - this.Px(7));
                var gauge = new Rectangle(x, gy, r.Right - x - this.Px(10), this.Px(4));
                using var gaugeBack = new SolidBrush(Theme.LineStrong);
                using var gaugeFill = new SolidBrush(tile.GaugeColor ?? Theme.GaugeNormal);
                g.FillRectangle(gaugeBack, gauge);
                g.FillRectangle(gaugeFill, gauge.X, gauge.Y, (int)(gauge.Width * Math.Clamp(tile.Gauge, 0, 1)), gauge.Height);
            }
        }
    }
}
