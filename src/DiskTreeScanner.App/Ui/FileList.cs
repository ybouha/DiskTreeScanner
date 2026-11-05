using System.Drawing.Drawing2D;
using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.App.Ui;

/// <summary>Largest files / search results: virtual list with File, Folder, Size and Kind columns.</summary>
internal sealed class FileList : PaintedControl
{
    private static readonly int[] Widths = [200, 0, 84, 176];
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, TabStop = false };
    private readonly Dictionary<int, (string Name, string Folder, FileKind Kind)> _rows = new();
    private readonly int[] _x = new int[5];
    private readonly char[] _buffer = new char[64];
    private TreeModel? _model;
    private int[] _nodes = [];
    private int _top;
    private int _selected = -1;
    private int _hover = -1;

    // Column widths chosen by the user (DIP, 0 = default; the folder column takes the rest).
    private readonly int[] _userWidths = new int[4];
    private int _resizing = -1;
    private int _resizeStartX;
    private int _resizeStartWidth;

    /// <summary>Raised when the user has resized a column (see <see cref="ColumnWidths"/>).</summary>
    public event EventHandler? ColumnsResized;

    /// <summary>User column widths in DIP (0 = default): file, folder (unused), size, kind.</summary>
    public int[] ColumnWidths
    {
        get => (int[])_userWidths.Clone();
        set
        {
            Array.Clear(_userWidths);
            if (value is not null && value.Length == _userWidths.Length)
                value.CopyTo(_userWidths, 0);
            Invalidate();
        }
    }

    private int BaseWidthOf(int column) => _userWidths[column] > 0 ? _userWidths[column] : Widths[column];

    /// <summary>Column resized by the separator under the point: the file column by its right edge, size and kind by their left edge.</summary>
    private int ResizableColumnAt(int x, int y)
    {
        if (y >= HeaderHeight)
            return -1;
        int grip = this.Px(4);
        if (Math.Abs(x - _x[1]) <= grip)
            return 0;
        if (Math.Abs(x - _x[2]) <= grip)
            return 2;
        if (Math.Abs(x - _x[3]) <= grip)
            return 3;
        return -1;
    }

    private void DragResize(int x)
    {
        int column = _resizing;
        float scale = DpiScale;
        int delta = x - _resizeStartX;
        int pixels = column == 0 ? _resizeStartWidth + delta : _resizeStartWidth - delta;
        int width = ClientSize.Width - _scroll.Width;
        int others = 0;
        foreach (int c in (ReadOnlySpan<int>)[0, 2, 3])
        {
            if (c != column)
                others += BaseWidthOf(c);
        }
        int minimum = column == 0 ? 80 : 60;
        int maximum = Math.Max(minimum, (int)((width - this.Px(110)) / scale) - others);
        _userWidths[column] = Math.Clamp((int)Math.Round(pixels / scale), minimum, maximum);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_resizing < 0)
            return;
        _resizing = -1;
        Capture = false;
        ColumnsResized?.Invoke(this, EventArgs.Empty);
    }

    public FileList()
    {
        SetStyle(ControlStyles.Selectable, true);
        BackColor = Color.White;
        Controls.Add(_scroll);
        _scroll.Scroll += (_, e) =>
        {
            _top = e.NewValue;
            Invalidate();
        };
    }

    public event Action<int>? NodeSelected;

    public event Action<int>? NodeActivated;

    public event Action<int, Point>? ContextRequested;

    private int RowHeight => this.Px(24);

    private int HeaderHeight => this.Px(22) + this.Px(24);

    private int VisibleRows => Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);

    public void SetItems(TreeModel? model, int[] nodes)
    {
        _model = model;
        _nodes = nodes;
        _rows.Clear();
        _top = 0;
        _selected = -1;
        UpdateScroll();
        Invalidate();
    }

    public void RefreshLabels()
    {
        _rows.Clear();
        Invalidate();
    }

    private void UpdateScroll()
    {
        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, _nodes.Length - 1);
        _scroll.LargeChange = VisibleRows;
        _scroll.Enabled = _nodes.Length > VisibleRows;
        _top = Math.Clamp(_top, 0, Math.Max(0, _nodes.Length - VisibleRows));
        _scroll.Value = Math.Min(_top, _scroll.Maximum);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    private int RowAt(int y)
    {
        if (y < HeaderHeight)
            return -1;
        int row = _top + (y - HeaderHeight) / RowHeight;
        return row < _nodes.Length ? row : -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int resized = ResizableColumnAt(e.X, e.Y);
        if (resized >= 0 && e.Button == MouseButtons.Left)
        {
            _resizing = resized;
            _resizeStartX = e.X;
            _resizeStartWidth = resized == 0 ? _x[1] - _x[0] : _x[resized + 1] - _x[resized];
            Capture = true;
            return;
        }
        int row = RowAt(e.Y);
        if (row < 0)
            return;
        _selected = row;
        Invalidate();
        NodeSelected?.Invoke(_nodes[row]);
        if (e.Button == MouseButtons.Right)
            ContextRequested?.Invoke(_nodes[row], PointToScreen(e.Location));
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int resized = ResizableColumnAt(e.X, e.Y);
        if (resized >= 0)
        {
            _userWidths[resized] = 0; // back to the default width
            _resizing = -1;
            Capture = false;
            Invalidate();
            ColumnsResized?.Invoke(this, EventArgs.Empty);
            return;
        }
        int row = RowAt(e.Y);
        if (row >= 0)
            NodeActivated?.Invoke(_nodes[row]);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _top = Math.Clamp(_top - e.Delta / 120 * 3, 0, Math.Max(0, _nodes.Length - VisibleRows));
        _scroll.Value = Math.Min(_top, _scroll.Maximum);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_resizing >= 0)
        {
            DragResize(e.X);
            return;
        }
        Cursor = ResizableColumnAt(e.X, e.Y) >= 0 ? Cursors.VSplit : Cursors.Default;
        int row = RowAt(e.Y);
        if (row != _hover)
        {
            _hover = row;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_nodes.Length == 0)
            return;
        int row = _selected;
        if (e.KeyCode == Keys.Down)
            row = Math.Min(_nodes.Length - 1, row + 1);
        else if (e.KeyCode == Keys.Up)
            row = Math.Max(0, row - 1);
        else if (e.KeyCode == Keys.Enter && row >= 0)
        {
            NodeActivated?.Invoke(_nodes[row]);
            return;
        }
        else
            return;
        _selected = row;
        if (row < _top)
            _top = row;
        else if (row >= _top + VisibleRows)
            _top = row - VisibleRows + 1;
        _scroll.Value = Math.Min(_top, _scroll.Maximum);
        Invalidate();
        NodeSelected?.Invoke(_nodes[row]);
        e.Handled = true;
    }

    private (string Name, string Folder, FileKind Kind) Row(int node)
    {
        if (!_rows.TryGetValue(node, out var row))
        {
            TreeModel m = _model!;
            string name = m.GetName(node);
            string folder = m.Parent[node] >= 0 ? m.GetPath(m.Parent[node]) : string.Empty;
            FileKind kind = m.IsDirectory(node) ? FileKind.File : FileKinds.Classify(m, node, name, folder);
            row = (name, folder, kind);
            _rows[node] = row;
        }
        return row;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Color.White);
        int width = ClientSize.Width - _scroll.Width;
        // File, Size and Kind (default or user widths) shrink on narrow panes; the Folder column takes the rest.
        int baseFixed = this.Px(BaseWidthOf(0)) + this.Px(BaseWidthOf(2)) + this.Px(BaseWidthOf(3));
        double factor = Math.Clamp((width - this.Px(110)) / (double)Math.Max(1, baseFixed), 0.55, 1.0);
        int fileWidth = (int)(this.Px(BaseWidthOf(0)) * factor);
        int sizeWidth = Math.Max(this.Px(60), (int)(this.Px(BaseWidthOf(2)) * factor));
        int kindWidth = (int)(this.Px(BaseWidthOf(3)) * factor);
        _x[0] = 0;
        _x[1] = fileWidth;
        _x[2] = _x[1] + Math.Max(this.Px(60), width - fileWidth - sizeWidth - kindWidth);
        _x[3] = _x[2] + sizeWidth;
        _x[4] = _x[3] + kindWidth;

        int h1 = this.Px(22), h2 = this.Px(24);
        using var headerBack = new SolidBrush(Theme.HeaderBack);
        g.FillRectangle(headerBack, 0, 0, width, h1 + h2);
        using var line = new Pen(Theme.Line);
        using var strong = new Pen(Theme.LineStrong);
        using var soft = new Pen(Theme.LineSoft);
        g.DrawLine(line, 0, h1 - 1, width, h1 - 1);
        g.DrawLine(strong, 0, h1 + h2 - 1, width, h1 + h2 - 1);
        TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, L.S("GroupFile"), Theme.Caption(this), new Rectangle(_x[0], 0, _x[1], h1), Theme.TextSecondary, center);
        TextRenderer.DrawText(g, L.S("GroupLocation"), Theme.Caption(this), new Rectangle(_x[1], 0, _x[2] - _x[1], h1), Theme.TextSecondary, center);
        TextRenderer.DrawText(g, L.S("GroupSizeKind"), Theme.Caption(this), new Rectangle(_x[2], 0, _x[4] - _x[2], h1), Theme.TextSecondary, center);
        g.DrawLine(line, _x[1] - 1, 0, _x[1] - 1, h1);
        g.DrawLine(line, _x[2] - 1, 0, _x[2] - 1, h1);

        using (var sorted = new SolidBrush(Theme.SortedHeaderBack))
            g.FillRectangle(sorted, _x[2], h1, _x[3] - _x[2], h2 - 1);
        string[] labels = [L.S("ColFile"), L.S("ColFolder"), L.S("ColSize"), L.S("ColKind")];
        for (int c = 0; c < 4; c++)
        {
            var cell = new Rectangle(_x[c] + this.Px(8), h1, _x[c + 1] - _x[c] - this.Px(16), h2);
            TextFormatFlags flags = (c == 2 ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, labels[c], c == 2 ? Theme.GridBold(this) : Theme.Grid(this), cell, Theme.Text, flags);
            if (c < 3)
                g.DrawLine(soft, _x[c + 1] - 1, h1, _x[c + 1] - 1, h1 + h2);
        }
        if (_model is null)
            return;

        g.SetClip(new Rectangle(0, HeaderHeight, width, ClientSize.Height - HeaderHeight));
        int rowHeight = RowHeight;
        int end = Math.Min(_nodes.Length, _top + VisibleRows + 1);
        using var rowLine = new Pen(Theme.RowLine);
        using var tint = new SolidBrush(Theme.SortedCellBack);
        using var selectedBack = new SolidBrush(Theme.SelectedBack);
        using var hoverBack = new SolidBrush(Theme.HoverBack);
        using var dot = new SolidBrush(Color.Gray);
        TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        TextFormatFlags rightFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        for (int r = _top; r < end; r++)
        {
            int node = _nodes[r];
            var (name, folder, kind) = Row(node);
            int y = HeaderHeight + (r - _top) * rowHeight;
            if (r == _selected)
                g.FillRectangle(selectedBack, 0, y, width, rowHeight);
            else
            {
                if (r == _hover)
                    g.FillRectangle(hoverBack, 0, y, width, rowHeight);
                g.FillRectangle(tint, _x[2], y, _x[3] - _x[2], rowHeight);
            }
            g.DrawLine(rowLine, 0, y + rowHeight - 1, width, y + rowHeight - 1);
            bool directory = _model.IsDirectory(node);
            TextRenderer.DrawText(g, directory ? name + @"\" : name, Theme.GridBold(this), new Rectangle(_x[0] + this.Px(8), y, _x[1] - _x[0] - this.Px(12), rowHeight), Theme.Text, left);
            TextRenderer.DrawText(g, folder, Theme.Mono(this), new Rectangle(_x[1] + this.Px(8), y, _x[2] - _x[1] - this.Px(12), rowHeight), Theme.TextSecondary, left | TextFormatFlags.PathEllipsis);
            int n = Fmt.Size(_model.Allocated[node] > 0 && directory ? _model.Allocated[node] : _model.Size[node], _buffer);
            TextRenderer.DrawText(g, _buffer.AsSpan(0, n), Theme.GridBold(this), new Rectangle(_x[2] + this.Px(4), y, _x[3] - _x[2] - this.Px(12), rowHeight), Theme.Text, rightFlags);
            float s = DpiScale;
            dot.Color = directory ? Theme.FolderBack : FileKinds.Dot(kind);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(dot, _x[3] + this.Px(8), y + (rowHeight - 9 * s) / 2, 9 * s, 9 * s);
            g.SmoothingMode = SmoothingMode.None;
            string label = directory ? L.S("ColFolder") : FileKinds.Label(kind);
            TextRenderer.DrawText(g, label, Theme.Grid(this), new Rectangle(_x[3] + this.Px(24), y, _x[4] - _x[3] - this.Px(28), rowHeight), Theme.Text, left);
        }
        g.ResetClip();
    }
}
