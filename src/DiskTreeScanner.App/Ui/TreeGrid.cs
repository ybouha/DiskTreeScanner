using System.Drawing.Drawing2D;
using DiskTreeScanner.Core.Analysis;
using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.App.Ui;

/// <summary>
/// Owner-drawn, double-buffered, fully virtual tree grid. Only visible rows are painted, from a
/// flat array of the expanded rows (node keys + depth). A folder's children are sorted only when it
/// is expanded, and the order is cached. Row keys: a node index (≥ 0), or −(folder + 2) for the
/// "[N Files in …]" row grouping the files directly in a folder.
/// </summary>
internal sealed class TreeGrid : Control
{
    private enum Column { Marker, Folder, Percent, Size, Allocated, Items, Files, Folders, Modified }

    private static readonly int[] BaseWidths = [24, 0, 140, 112, 116, 104, 104, 88, 140];
    private const int GroupHeaderHeight = 22;
    private const int ColumnHeaderHeight = 25;
    private const int RowHeightBase = 23;

    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, TabStop = false };
    private readonly HashSet<int> _expanded = new();
    private readonly Dictionary<int, int[]> _children = new();
    private readonly Dictionary<int, FilesGroup> _groups = new();
    private readonly char[] _text = new char[600];
    private readonly int[] _columnX = new int[BaseWidths.Length + 1];
    private TreeModel? _model;
    private int _root;
    private string _rootLabel = string.Empty;
    private bool _rootIsDrive;
    private int[] _rows = new int[256];
    private int[] _depths = new int[256];
    private int _rowCount;
    private int _top;
    private int _selectedRow = -1;
    private int _hoverRow = -1;
    private IReadOnlyDictionary<int, FilterTotals>? _filter;
    private bool _showFilesRow = true;

    // Column widths chosen by the user (DIP, 0 = default) and the drag in progress.
    private readonly int[] _userWidths = new int[BaseWidths.Length];
    private int _resizing = -1;
    private int _resizeStartX;
    private int _resizeStartWidth;
    private Column? _hoverHeader;

    public TreeGrid()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
        Controls.Add(_scroll);
        _scroll.Scroll += (_, e) =>
        {
            _top = e.NewValue;
            Invalidate();
        };
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Raised for Enter / double-click on a folder (drill in).</summary>
    public event Action<int>? DrillIn;

    /// <summary>Raised for Backspace (go up one level).</summary>
    public event Action? GoUp;

    /// <summary>Raised for Enter / double-click on a file.</summary>
    public event Action<int>? OpenFile;

    public event Action<int, Point>? ContextRequested;

    public event EventHandler? SortChanged;

    /// <summary>Raised when a column header's funnel is clicked, with the header cell in screen coordinates.</summary>
    public event Action<FilterColumn, Rectangle>? ColumnFilterRequested;

    /// <summary>Raised when the user has resized a column (see <see cref="ColumnWidths"/>).</summary>
    public event EventHandler? ColumnsResized;

    /// <summary>Criteria of the column filters: funnels show which are set; % and counts hide rows.</summary>
    public ColumnFilters? ColumnFilters { get; set; }

    /// <summary>User column widths in DIP (0 = default), indexed like the columns.</summary>
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

    /// <summary>Diagnostics: screen rectangle of a column's header.</summary>
    internal Rectangle HeaderCellOnScreen(FilterColumn column)
    {
        int c = (int)column + 1;
        return RectangleToScreen(new Rectangle(_columnX[c], 0, _columnX[c + 1] - _columnX[c], HeaderHeight));
    }

    /// <summary>Diagnostics: screen point inside the selected row.</summary>
    internal Point SelectedRowOnScreen() => PointToScreen(new Point(this.Px(140), RowTop(Math.Max(_top, _selectedRow)) + RowHeight / 2));

    /// <summary>Diagnostics: drags the separator left of <paramref name="column"/> by <paramref name="dx"/> pixels, through the mouse handlers.</summary>
    internal void SimulateResize(FilterColumn column, int dx)
    {
        int x = _columnX[(int)column + 1];
        int y = this.Px(GroupHeaderHeight + 8);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x + dx, y, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x + dx, y, 0));
    }

    /// <summary>Re-applies the row criteria of <see cref="ColumnFilters"/> after a change.</summary>
    public void RefreshColumnFilters()
    {
        ResetCaches();
        RebuildRows(SelectedKey);
    }

    /// <summary>Duration of the last paint (diagnostics).</summary>
    public double LastPaintMilliseconds { get; private set; }

    public void ExpandSelected()
    {
        if (_selectedRow >= 0 && !IsExpanded(_rows[_selectedRow]))
            Expand(_selectedRow);
    }

    public void SelectRow(int row) => SetSelectedRow(row);

    public void ScrollRows(int delta)
    {
        _top += delta;
        ClampTop();
        _scroll.Value = Math.Min(_top, Math.Max(0, _scroll.Maximum));
        Invalidate();
    }

    public SortColumn Sort { get; private set; } = SortColumn.Allocated;

    public bool Descending { get; private set; } = true;

    public TreeModel? Model => _model;

    public int Root => _root;

    public bool FilterActive => _filter is not null;

    public int RowCount => _rowCount;

    /// <summary>Selected row key, or int.MinValue when nothing is selected.</summary>
    public int SelectedKey => _selectedRow >= 0 && _selectedRow < _rowCount ? _rows[_selectedRow] : int.MinValue;

    /// <summary>Node for the selection: the folder for a files row.</summary>
    public int SelectedNode
    {
        get
        {
            int key = SelectedKey;
            if (key == int.MinValue)
                return -1;
            return key >= 0 ? key : FolderOfFilesKey(key);
        }
    }

    public bool SelectedIsFilesRow => SelectedKey is < 0 and not int.MinValue;

    public bool ShowFilesRow
    {
        get => _showFilesRow;
        set
        {
            if (_showFilesRow == value)
                return;
            _showFilesRow = value;
            ResetCaches();
            RebuildRows(SelectedKey);
        }
    }

    private int RowHeight => this.Px(RowHeightBase);

    private int HeaderHeight => this.Px(GroupHeaderHeight) + this.Px(ColumnHeaderHeight);

    private int VisibleRows => Math.Max(1, (ClientSize.Height - HeaderHeight) / RowHeight);

    public static int FilesKey(int folder) => -(folder + 2);

    public static int FolderOfFilesKey(int key) => -key - 2;

    public void SetModel(TreeModel? model, int root, string rootLabel, bool rootIsDrive)
    {
        _model = model;
        _root = root;
        _rootLabel = rootLabel;
        _rootIsDrive = rootIsDrive;
        _filter = null;
        _expanded.Clear();
        ResetCaches();
        _top = 0;
        _selectedRow = -1;
        if (model is not null)
            _expanded.Add(root);
        RebuildRows(model is null ? int.MinValue : root);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restricts the tree to nodes present in <paramref name="filter"/> (null clears).</summary>
    public void SetFilter(IReadOnlyDictionary<int, FilterTotals>? filter)
    {
        _filter = filter;
        ResetCaches();
        RebuildRows(SelectedKey);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSort(SortColumn column, bool descending)
    {
        Sort = column;
        Descending = descending;
        ResetCaches();
        RebuildRows(SelectedKey);
    }

    /// <summary>Re-reads labels (language change) without losing expansion or selection.</summary>
    public void RefreshLabels()
    {
        foreach (FilesGroup group in _groups.Values)
            group.Label = null;
        Invalidate();
    }

    private void ResetCaches()
    {
        _children.Clear();
        _groups.Clear();
    }

    // ---- Values (filtered or not) -------------------------------------------------------

    private FilesGroup Group(int folder)
    {
        // The group is registered before the children are sorted (sorting reads it): no recursion.
        if (_groups.TryGetValue(folder, out FilesGroup? group))
            return group;
        DisplayChildren(folder);
        return _groups.TryGetValue(folder, out group) ? group : FilesGroup.Empty;
    }

    private long SizeOf(int key) => key >= 0
        ? (_filter is not null && _filter.TryGetValue(key, out FilterTotals t) ? t.Size : _model!.Size[key])
        : Group(FolderOfFilesKey(key)).Size;

    private long AllocatedOf(int key) => key >= 0
        ? (_filter is not null && _filter.TryGetValue(key, out FilterTotals t) ? t.Allocated : _model!.Allocated[key])
        : Group(FolderOfFilesKey(key)).Allocated;

    private long ModifiedOf(int key) => key >= 0
        ? (_filter is not null && _filter.TryGetValue(key, out FilterTotals t) ? t.Modified : _model!.Modified[key])
        : Group(FolderOfFilesKey(key)).Modified;

    private int ItemsOf(int key) => key >= 0
        ? (_filter is not null && _filter.TryGetValue(key, out FilterTotals t) ? t.Items : _model!.ItemCount(key))
        : Group(FolderOfFilesKey(key)).Count;

    private int FilesOf(int key) => key >= 0
        ? (_filter is not null && _filter.TryGetValue(key, out FilterTotals t) ? t.Files : _model!.FileCount(key))
        : Group(FolderOfFilesKey(key)).Count;

    private int FoldersOf(int key) => key >= 0
        ? (_filter is not null && _filter.TryGetValue(key, out FilterTotals t) ? t.Folders : _model!.FolderCount(key))
        : 0;

    private bool IsShown(int node) => _filter is null || _filter.ContainsKey(node);

    /// <summary>
    /// Row criteria of the column filters for a child of <paramref name="folder"/>: counts apply to
    /// folders, the share of the parent to every row.
    /// </summary>
    private bool PassesRowCriteria(int node, int folder)
    {
        ColumnFilters? f = ColumnFilters;
        if (f is null || !f.HasRowCriteria)
            return true;
        if (_model!.IsDirectory(node)
            && !(f[FilterColumn.Items].Contains(ItemsOf(node)) && f[FilterColumn.Files].Contains(FilesOf(node)) && f[FilterColumn.Folders].Contains(FoldersOf(node))))
        {
            return false;
        }
        Bounds percent = f[FilterColumn.Percent];
        if (!percent.IsSet)
            return true;
        long parent = PercentValue(folder);
        long share = parent > 0 ? (long)Math.Round(10000.0 * PercentValue(node) / parent) : 0;
        return percent.Contains(share);
    }

    /// <summary>Metric of the "% of Parent" bar: size when sorting by size, allocation otherwise.</summary>
    private long PercentValue(int key) => Sort == SortColumn.Size ? SizeOf(key) : AllocatedOf(key);

    // ---- Children ----------------------------------------------------------------------

    private int[] DisplayChildren(int folder)
    {
        if (_children.TryGetValue(folder, out int[]? cached))
            return cached;
        TreeModel m = _model!;
        var items = new List<int>();
        var group = new FilesGroup();
        foreach (int c in m.Children(folder))
        {
            if (!IsShown(c) || !PassesRowCriteria(c, folder))
                continue;
            if (m.IsDirectory(c))
            {
                items.Add(c);
                continue;
            }
            group.Count++;
            group.Size += SizeOf(c);
            group.Allocated += AllocatedOf(c);
            group.Modified = Math.Max(group.Modified, ModifiedOf(c));
        }
        if (group.Count > 0)
        {
            _groups[folder] = group;
            if (_showFilesRow)
                items.Add(FilesKey(folder));
        }
        int[] sorted = items.ToArray();
        _children[folder] = sorted; // cached first: sorting reads the files row totals
        SortKeys(sorted);
        return sorted;
    }

    private int[] GroupFiles(int folder)
    {
        FilesGroup group = Group(folder);
        if (group.Sorted is not null)
            return group.Sorted;
        var files = new List<int>(group.Count);
        foreach (int c in _model!.Children(folder))
        {
            if (!_model.IsDirectory(c) && IsShown(c) && PassesRowCriteria(c, folder))
                files.Add(c);
        }
        int[] sorted = files.ToArray();
        SortKeys(sorted);
        group.Sorted = sorted;
        return sorted;
    }

    private void SortKeys(int[] keys)
    {
        if (keys.Length < 2)
            return;
        if (Sort == SortColumn.Name)
        {
            SortByName(keys);
            return;
        }
        var values = new long[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            int k = keys[i];
            long v = Sort switch
            {
                SortColumn.Size => SizeOf(k),
                SortColumn.Items => ItemsOf(k),
                SortColumn.Files => FilesOf(k),
                SortColumn.Folders => FoldersOf(k),
                SortColumn.Modified => ModifiedOf(k),
                _ => AllocatedOf(k),
            };
            values[i] = Descending ? -v : v;
        }
        Array.Sort(values, keys);
        // Array.Sort is not stable: order ties by position (native int sort, no comparer allocation).
        int start = 0;
        for (int i = 1; i <= keys.Length; i++)
        {
            if (i == keys.Length || values[i] != values[start])
            {
                if (i - start > 1)
                    Array.Sort(keys, start, i - start);
                start = i;
            }
        }
    }

    /// <summary>
    /// Name order of the volume's case table: each name is upper-cased once through the table, then
    /// the keys are sorted ordinally (vectorized), which is the same order as comparing through the
    /// table. The files row stays last.
    /// </summary>
    private void SortByName(int[] keys)
    {
        TreeModel m = _model!;
        var upper = new string[keys.Length];
        var nodes = new int[keys.Length];
        int n = 0;
        int filesRow = int.MinValue;
        foreach (int key in keys)
        {
            if (key < 0)
            {
                filesRow = key;
                continue;
            }
            upper[n] = string.Create(m.NameLengthOf(key), (m, key), static (span, s) =>
            {
                int length = s.m.GetName(s.key, span);
                s.m.UpCase.ToUpper(span[..length], span);
            });
            nodes[n++] = key;
        }
        Array.Sort(upper, nodes, 0, n, StringComparer.Ordinal);
        if (Descending)
            Array.Reverse(nodes, 0, n);
        nodes.AsSpan(0, n).CopyTo(keys);
        if (filesRow != int.MinValue)
            keys[n] = filesRow;
    }

    /// <summary>O(1): no child list is built just to draw a chevron.</summary>
    private bool IsExpandable(int key)
    {
        if (_model is null)
            return false;
        if (key < 0)
            return Group(FolderOfFilesKey(key)).Count > 0;
        if (!_model.IsDirectory(key))
            return false;
        if (_filter is not null)
            return _filter.TryGetValue(key, out FilterTotals t) && (_showFilesRow ? t.Items > 0 : t.Folders > 0);
        return _showFilesRow ? _model.HasChildren(key) : _model.FolderCount(key) > 0;
    }

    private bool IsExpanded(int key) => _expanded.Contains(key);

    // ---- Rows --------------------------------------------------------------------------

    private void RebuildRows(int keepKey)
    {
        _rowCount = 0;
        if (_model is not null && IsShown(_root))
        {
            Append(_root, 0);
            if (_expanded.Contains(_root))
                AppendChildren(_root, 0);
        }
        _selectedRow = keepKey == int.MinValue ? -1 : IndexOf(keepKey);
        if (_selectedRow < 0 && _rowCount > 0)
            _selectedRow = 0;
        UpdateScroll();
        EnsureVisible(_selectedRow);
        Invalidate();
    }

    private void AppendChildren(int key, int depth)
    {
        int[] children = key >= 0 ? DisplayChildren(key) : GroupFiles(FolderOfFilesKey(key));
        foreach (int child in children)
        {
            Append(child, depth + 1);
            if (_expanded.Contains(child) && (child < 0 || _model!.IsDirectory(child)))
                AppendChildren(child, depth + 1);
        }
    }

    private void Append(int key, int depth)
    {
        if (_rowCount == _rows.Length)
        {
            Array.Resize(ref _rows, _rows.Length * 2);
            Array.Resize(ref _depths, _depths.Length * 2);
        }
        _rows[_rowCount] = key;
        _depths[_rowCount] = depth;
        _rowCount++;
    }

    private int IndexOf(int key)
    {
        for (int i = 0; i < _rowCount; i++)
        {
            if (_rows[i] == key)
                return i;
        }
        return -1;
    }

    private void Toggle(int row)
    {
        if (row < 0 || row >= _rowCount)
            return;
        int key = _rows[row];
        if (_expanded.Contains(key))
            Collapse(row);
        else
            Expand(row);
    }

    private void Expand(int row)
    {
        int key = _rows[row];
        if (!IsExpandable(key) || !_expanded.Add(key))
            return;
        int depth = _depths[row];
        // Collect the new rows (including descendants that were expanded before), then insert.
        int before = _rowCount;
        int[] savedRows = _rows;
        int[] savedDepths = _depths;
        _rows = new int[64];
        _depths = new int[64];
        _rowCount = 0;
        AppendChildren(key, depth);
        int[] newRows = _rows;
        int[] newDepths = _depths;
        int added = _rowCount;
        _rows = savedRows;
        _depths = savedDepths;
        _rowCount = before;
        EnsureCapacity(_rowCount + added);
        Array.Copy(_rows, row + 1, _rows, row + 1 + added, _rowCount - row - 1);
        Array.Copy(_depths, row + 1, _depths, row + 1 + added, _rowCount - row - 1);
        Array.Copy(newRows, 0, _rows, row + 1, added);
        Array.Copy(newDepths, 0, _depths, row + 1, added);
        _rowCount += added;
        if (_selectedRow > row)
            _selectedRow += added;
        UpdateScroll();
        Invalidate();
    }

    private void Collapse(int row)
    {
        int key = _rows[row];
        if (!_expanded.Remove(key))
            return;
        int depth = _depths[row];
        int end = row + 1;
        while (end < _rowCount && _depths[end] > depth)
            end++;
        int removed = end - row - 1;
        if (removed > 0)
        {
            Array.Copy(_rows, end, _rows, row + 1, _rowCount - end);
            Array.Copy(_depths, end, _depths, row + 1, _rowCount - end);
            _rowCount -= removed;
            if (_selectedRow > row && _selectedRow < end)
                _selectedRow = row;
            else if (_selectedRow >= end)
                _selectedRow -= removed;
        }
        UpdateScroll();
        Invalidate();
    }

    private void EnsureCapacity(int count)
    {
        if (count <= _rows.Length)
            return;
        int capacity = Math.Max(count, _rows.Length * 2);
        Array.Resize(ref _rows, capacity);
        Array.Resize(ref _depths, capacity);
    }

    /// <summary>Selects a node, expanding its ancestors below the root.</summary>
    public void SelectNode(int node)
    {
        if (_model is null || node < 0 || !_model.Contains(_root, node))
            return;
        var chain = new Stack<int>();
        for (int x = _model.Parent[node]; x >= 0 && x != _root; x = _model.Parent[x])
            chain.Push(x);
        bool changed = _expanded.Add(_root);
        foreach (int x in chain)
            changed |= _expanded.Add(x);
        int key = node;
        if (!_model.IsDirectory(node) && _showFilesRow && _model.Parent[node] >= 0)
        {
            int filesKey = FilesKey(_model.Parent[node]);
            changed |= _expanded.Add(filesKey);
        }
        int before = SelectedKey;
        if (changed)
            RebuildRows(before);
        int row = IndexOf(key);
        if (row < 0 && !_model.IsDirectory(node))
            row = IndexOf(FilesKey(_model.Parent[node]));
        SetSelectedRow(row); // raises SelectionChanged: the rebuild above kept the previous selection
    }

    private void SetSelectedRow(int row)
    {
        if (_rowCount == 0)
            return;
        row = Math.Clamp(row, 0, _rowCount - 1);
        if (row == _selectedRow)
            return;
        _selectedRow = row;
        EnsureVisible(row);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureVisible(int row)
    {
        if (row < 0)
            return;
        int visible = VisibleRows;
        if (row < _top)
            _top = row;
        else if (row >= _top + visible)
            _top = row - visible + 1;
        ClampTop();
        _scroll.Value = Math.Min(_top, Math.Max(0, _scroll.Maximum));
    }

    private void ClampTop()
    {
        _top = Math.Max(0, Math.Min(_top, Math.Max(0, _rowCount - VisibleRows)));
    }

    private void UpdateScroll()
    {
        int visible = VisibleRows;
        _scroll.Minimum = 0;
        _scroll.LargeChange = Math.Max(1, visible);
        _scroll.SmallChange = 1;
        _scroll.Maximum = Math.Max(0, _rowCount - 1);
        _scroll.Enabled = _rowCount > visible;
        ClampTop();
        _scroll.Value = Math.Min(_top, _scroll.Maximum);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    // ---- Input -------------------------------------------------------------------------

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Enter or Keys.Back or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rowCount == 0)
            return;
        int row = Math.Max(0, _selectedRow);
        switch (e.KeyCode)
        {
            case Keys.Up:
                SetSelectedRow(row - 1);
                break;
            case Keys.Down:
                SetSelectedRow(row + 1);
                break;
            case Keys.PageUp:
                SetSelectedRow(row - VisibleRows + 1);
                break;
            case Keys.PageDown:
                SetSelectedRow(row + VisibleRows - 1);
                break;
            case Keys.Home:
                SetSelectedRow(0);
                break;
            case Keys.End:
                SetSelectedRow(_rowCount - 1);
                break;
            case Keys.Right:
                if (!IsExpanded(_rows[row]))
                    Expand(row);
                else if (row + 1 < _rowCount && _depths[row + 1] > _depths[row])
                    SetSelectedRow(row + 1);
                break;
            case Keys.Left:
                if (IsExpanded(_rows[row]) && row > 0)
                {
                    Collapse(row);
                }
                else
                {
                    int depth = _depths[row];
                    for (int r = row - 1; r >= 0; r--)
                    {
                        if (_depths[r] < depth)
                        {
                            SetSelectedRow(r);
                            break;
                        }
                    }
                }
                break;
            case Keys.Space:
                Toggle(row);
                break;
            case Keys.Enter:
                Activate(row);
                break;
            case Keys.Back:
                GoUp?.Invoke();
                break;
            case Keys.Apps:
                ContextRequested?.Invoke(_rows[row], PointToScreen(new Point(this.Px(60), RowTop(row) + RowHeight)));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Shift | Keys.F10) && _selectedRow >= 0)
        {
            ContextRequested?.Invoke(_rows[_selectedRow], PointToScreen(new Point(this.Px(60), RowTop(_selectedRow) + RowHeight)));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Activate(int row)
    {
        int key = _rows[row];
        if (key < 0)
            Toggle(row);
        else if (_model!.IsDirectory(key))
            DrillIn?.Invoke(key);
        else
            OpenFile?.Invoke(key);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Y < HeaderHeight)
        {
            if (e.Button != MouseButtons.Left)
                return;
            int resized = ResizableColumnAt(e.X);
            if (resized >= 0)
            {
                _resizing = resized;
                _resizeStartX = e.X;
                _resizeStartWidth = _columnX[resized + 1] - _columnX[resized];
                Capture = true;
                return;
            }
            HeaderClick(e.X, e.Y);
            return;
        }
        int row = RowAt(e.Y);
        if (row < 0)
            return;
        if (e.Button == MouseButtons.Left && IsOnChevron(row, e.X))
        {
            Toggle(row);
            return;
        }
        SetSelectedRow(row);
        if (e.Button == MouseButtons.Right)
            ContextRequested?.Invoke(_rows[row], PointToScreen(e.Location));
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Y < HeaderHeight)
        {
            int resized = ResizableColumnAt(e.X);
            if (e.Button == MouseButtons.Left && resized >= 0)
            {
                _userWidths[resized] = 0; // back to the default width
                _resizing = -1;
                Capture = false;
                Invalidate();
                ColumnsResized?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        int row = RowAt(e.Y);
        if (e.Button == MouseButtons.Left && row >= 0 && !IsOnChevron(row, e.X))
            Activate(row);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = SystemInformation.MouseWheelScrollLines;
        _top -= e.Delta / 120 * Math.Max(1, lines);
        ClampTop();
        _scroll.Value = Math.Min(_top, Math.Max(0, _scroll.Maximum));
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
        int row = e.Y >= HeaderHeight ? RowAt(e.Y) : -1;
        if (row != _hoverRow)
        {
            InvalidateRow(_hoverRow);
            _hoverRow = row;
            InvalidateRow(row);
        }
        Column? header = e.Y < HeaderHeight ? ColumnAt(e.X) : null;
        if (header != _hoverHeader)
        {
            _hoverHeader = header;
            Invalidate(new Rectangle(0, 0, ClientSize.Width, HeaderHeight));
        }
        if (e.Y >= HeaderHeight)
            Cursor = Cursors.Default;
        else if (ResizableColumnAt(e.X) >= 0)
            Cursor = Cursors.VSplit;
        else
            Cursor = ColumnAt(e.X) is not Column.Marker && (FunnelAt(e.X, e.Y) is not null || ColumnAt(e.X) is not Column.Percent) ? Cursors.Hand : Cursors.Default;
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

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverRow = -1;
        _hoverHeader = null;
        Invalidate();
    }

    /// <summary>
    /// Column resized by the separator under <paramref name="x"/>, or -1. The columns right of the
    /// folder are anchored to the right edge, so a separator resizes the column on its right and
    /// follows the mouse; the folder column takes what is left.
    /// </summary>
    private int ResizableColumnAt(int x)
    {
        int grip = this.Px(4);
        for (int c = (int)Column.Percent; c < BaseWidths.Length; c++)
        {
            if (Math.Abs(x - _columnX[c]) <= grip)
                return c;
        }
        return -1;
    }

    private void DragResize(int x)
    {
        int column = _resizing;
        float scale = Theme.Scale(this);
        int minimum = column == (int)Column.Percent ? 60 : 44;
        int width = ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0);
        int others = 0;
        for (int c = 0; c < BaseWidths.Length; c++)
        {
            if (c != column && c != (int)Column.Folder)
                others += BaseWidthOf(c);
        }
        int maximum = Math.Max(minimum, (int)((width - this.Px(170)) / scale) - others);
        int pixels = _resizeStartWidth - (x - _resizeStartX);
        _userWidths[column] = Math.Clamp((int)Math.Round(pixels / scale), minimum, maximum);
        Invalidate();
    }

    private int BaseWidthOf(int column) => _userWidths[column] > 0 ? _userWidths[column] : BaseWidths[column];

    /// <summary>Filter funnel under the point: right end of the folder header, left end of the others.</summary>
    private FilterColumn? FunnelAt(int x, int y)
    {
        if (y < this.Px(GroupHeaderHeight) || y >= HeaderHeight)
            return null;
        Column column = ColumnAt(x);
        return column switch
        {
            Column.Marker => null,
            Column.Folder => x > _columnX[(int)Column.Folder + 1] - this.Px(24) ? FilterColumn.Name : null,
            _ => x < _columnX[(int)column] + this.Px(22) ? (FilterColumn)((int)column - 1) : null,
        };
    }

    private void HeaderClick(int x, int y)
    {
        Column column = ColumnAt(x);
        if (FunnelAt(x, y) is { } filterColumn)
        {
            var cell = new Rectangle(_columnX[(int)column], 0, _columnX[(int)column + 1] - _columnX[(int)column], HeaderHeight);
            ColumnFilterRequested?.Invoke(filterColumn, RectangleToScreen(cell));
            return;
        }
        SortColumn? sort = column switch
        {
            Column.Folder => SortColumn.Name,
            Column.Size => SortColumn.Size,
            Column.Allocated => SortColumn.Allocated,
            Column.Items => SortColumn.Items,
            Column.Files => SortColumn.Files,
            Column.Folders => SortColumn.Folders,
            Column.Modified => SortColumn.Modified,
            _ => null,
        };
        if (sort is not { } s)
            return;
        bool descending = s == Sort ? !Descending : s != SortColumn.Name;
        SetSort(s, descending);
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    private int RowAt(int y)
    {
        if (y < HeaderHeight)
            return -1;
        int row = _top + (y - HeaderHeight) / RowHeight;
        return row < _rowCount ? row : -1;
    }

    private int RowTop(int row) => HeaderHeight + (row - _top) * RowHeight;

    private void InvalidateRow(int row)
    {
        if (row >= _top && row < _top + VisibleRows + 1)
            Invalidate(new Rectangle(0, RowTop(row), ClientSize.Width, RowHeight));
    }

    private Column ColumnAt(int x)
    {
        for (int c = 0; c < BaseWidths.Length; c++)
        {
            if (x < _columnX[c + 1])
                return (Column)c;
        }
        return Column.Modified;
    }

    private bool IsOnChevron(int row, int x)
    {
        int left = _columnX[(int)Column.Folder] + this.Px(4 + _depths[row] * 18);
        return x >= left - this.Px(2) && x < left + this.Px(16) && IsExpandable(_rows[row]);
    }

    // ---- Painting ----------------------------------------------------------------------

    /// <summary>
    /// The fixed columns (default or user widths) shrink together, down to 62 %, on narrow windows;
    /// the folder column takes the rest.
    /// </summary>
    private void LayoutColumns(int width)
    {
        int baseFixed = 0;
        for (int c = 0; c < BaseWidths.Length; c++)
        {
            if (c != (int)Column.Folder)
                baseFixed += this.Px(BaseWidthOf(c));
        }
        double factor = Math.Clamp((width - this.Px(170)) / (double)Math.Max(1, baseFixed), 0.62, 1.0);
        int fixedTotal = 0;
        for (int c = 0; c < BaseWidths.Length; c++)
        {
            if (c != (int)Column.Folder)
                fixedTotal += (int)(this.Px(BaseWidthOf(c)) * factor);
        }
        int folder = Math.Max(this.Px(120), width - fixedTotal);
        int x = 0;
        for (int c = 0; c < BaseWidths.Length; c++)
        {
            _columnX[c] = x;
            x += c == (int)Column.Folder ? folder : (int)(this.Px(BaseWidthOf(c)) * factor);
        }
        _columnX[BaseWidths.Length] = x;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        PaintGrid(e);
        LastPaintMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Two passes: shapes with GDI+, then all row text through one HDC (see <see cref="GdiText"/>).</summary>
    private void PaintGrid(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int width = ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0);
        LayoutColumns(width);
        g.Clear(Color.White);
        PaintHeaders(g, width);
        int headerHeight = HeaderHeight;
        if (_model is null || _rowCount == 0)
        {
            TextRenderer.DrawText(g, L.S("DescriptionEmpty"), Theme.Body(this), new Rectangle(0, headerHeight, width, this.Px(60)), Theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        int rowHeight = RowHeight;
        int last = Math.Min(_rowCount, _top + VisibleRows + 1);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        g.SetClip(new Rectangle(0, headerHeight, width, ClientSize.Height - headerHeight));
        for (int row = _top; row < last; row++)
            PaintRowShapes(g, row, headerHeight + (row - _top) * rowHeight, rowHeight, width);
        g.ResetClip();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        nint hdc = g.GetHdc();
        try
        {
            using var text = new GdiText(hdc);
            for (int row = _top; row < last; row++)
                PaintRowText(text, row, headerHeight + (row - _top) * rowHeight, rowHeight);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastPaintBreakdown = $"{last - _top} rows: shapes {System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds:F2} ms, text {System.Diagnostics.Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds:F2} ms";
    }

    /// <summary>Time per paint phase (diagnostics).</summary>
    public string LastPaintBreakdown { get; private set; } = string.Empty;

    private readonly Dictionary<(int Kind, int Dpi), Bitmap> _icons = new();

    /// <summary>Row icons pre-rendered once per DPI (vector paths per row cost more than the text).</summary>
    private Bitmap Icon(int kind)
    {
        int dpi = Theme.Dpi(this);
        if (_icons.TryGetValue((kind, dpi), out Bitmap? icon))
            return icon;
        float scale = dpi / 96f;
        icon = new Bitmap((int)Math.Ceiling(16 * scale) + 1, (int)Math.Ceiling(14 * scale) + 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(icon))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            switch (kind)
            {
                case 0:
                    Icons.Drive(g, 0, 0, scale);
                    break;
                case 1:
                    Icons.Folder(g, 0, 0, scale, dim: false);
                    break;
                case 2:
                    Icons.Folder(g, 0, 0, scale, dim: true);
                    break;
                default:
                    Icons.File(g, 0, 0, scale);
                    break;
            }
        }
        _icons[(kind, dpi)] = icon;
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Bitmap icon in _icons.Values)
                icon.Dispose();
            _icons.Clear();
        }
        base.Dispose(disposing);
    }

    private void PaintHeaders(Graphics g, int width)
    {
        int h1 = this.Px(GroupHeaderHeight);
        int h2 = this.Px(ColumnHeaderHeight);
        using var headerBack = new SolidBrush(Theme.HeaderBack);
        g.FillRectangle(headerBack, 0, 0, width, h1 + h2);
        using var line = new Pen(Theme.Line);
        using var strong = new Pen(Theme.LineStrong);
        using var soft = new Pen(Theme.LineSoft);
        g.DrawLine(line, 0, h1 - 1, width, h1 - 1);
        g.DrawLine(strong, 0, h1 + h2 - 1, width, h1 + h2 - 1);

        // Group header: FOLDER | SIZE | CONTENT | LAST CHANGE
        (string Key, int From, int To)[] groups =
        [
            ("GroupFolder", 0, (int)Column.Folder + 1),
            ("GroupSize", (int)Column.Percent, (int)Column.Allocated + 1),
            ("GroupContent", (int)Column.Items, (int)Column.Folders + 1),
            ("GroupLastChange", (int)Column.Modified, (int)Column.Modified + 1),
        ];
        foreach (var (key, from, to) in groups)
        {
            var rect = new Rectangle(_columnX[from], 0, _columnX[to] - _columnX[from], h1);
            TextRenderer.DrawText(g, L.S(key), Theme.Caption(this), Rectangle.Inflate(rect, -this.Px(4), 0), Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (to < BaseWidths.Length)
                g.DrawLine(line, _columnX[to] - 1, 0, _columnX[to] - 1, h1 - 1);
        }

        // Column header.
        Column sorted = Sort switch
        {
            SortColumn.Name => Column.Folder,
            SortColumn.Size => Column.Size,
            SortColumn.Items => Column.Items,
            SortColumn.Files => Column.Files,
            SortColumn.Folders => Column.Folders,
            SortColumn.Modified => Column.Modified,
            _ => Column.Allocated,
        };
        string[] labels = ["", L.S("ColFolder"), L.S("ColPercent"), L.S("ColSize"), L.S("ColAllocated"), L.S("ColItems"), L.S("ColFiles"), L.S("ColFolders"), L.S("ColModified")];
        float scale = Theme.Scale(this);
        for (int c = 0; c < labels.Length; c++)
        {
            var cell = new Rectangle(_columnX[c], h1, _columnX[c + 1] - _columnX[c], h2 - 1);
            bool isSorted = (Column)c == sorted;
            if (isSorted)
            {
                using var back = new SolidBrush(Theme.SortedHeaderBack);
                g.FillRectangle(back, cell);
            }
            if (c < labels.Length - 1)
                g.DrawLine(soft, cell.Right - 1, cell.Top, cell.Right - 1, cell.Bottom);
            if (c == 0)
                continue;
            Rectangle text = Rectangle.Inflate(cell, -this.Px(8), 0);
            Font font = isSorted ? Theme.GridBold(this) : Theme.Grid(this);
            var filterColumn = (FilterColumn)(c - 1);
            bool filtered = ColumnFilters?.IsActive(filterColumn) == true || (c == (int)Column.Folder && FilterActive && ColumnFilters is null);
            Color funnel = filtered ? Theme.Steel : _hoverHeader == (Column)c ? Theme.TextSecondary : Theme.InputBorder;
            float funnelY = cell.Top + (cell.Height - 11 * scale) / 2;
            if (c == (int)Column.Folder)
            {
                TextRenderer.DrawText(g, labels[c], font, text, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Icons.Funnel(g, text.Right - 11 * scale, funnelY, 0.8f * scale, filtered ? Theme.Steel : Theme.TextMuted, filtered);
                if (isSorted)
                    Icons.SortArrow(g, text.Right - 26 * scale, cell.Top + (cell.Height - 9 * scale) / 2, 0.9f * scale, Descending, Theme.Steel);
                g.SmoothingMode = SmoothingMode.None;
                continue;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Icons.Funnel(g, cell.Left + 6 * scale, funnelY, 0.8f * scale, funnel, filtered);
            if (isSorted)
            {
                Icons.SortArrow(g, text.Right - 9 * scale, cell.Top + (cell.Height - 9 * scale) / 2, 0.9f * scale, Descending, Theme.Steel);
                text.Width -= (int)(14 * scale);
            }
            g.SmoothingMode = SmoothingMode.None;
            text = Rectangle.FromLTRB(cell.Left + (int)(20 * scale), text.Top, text.Right, text.Bottom);
            TextRenderer.DrawText(g, labels[c], font, text, Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    private static readonly SolidBrush SelectedBrush = new(Theme.SelectedBack);
    private static readonly SolidBrush HoverBrush = new(Theme.HoverBack);
    private static readonly SolidBrush SortedBrush = new(Theme.SortedCellBack);
    private static readonly SolidBrush BarBackBrush = new(Theme.BarBack);
    private static readonly SolidBrush BarBrush = new(Theme.Bar);
    private static readonly SolidBrush ChevronBrush = new(Theme.TextSecondary);
    private static readonly Pen RowLinePen = new(Theme.RowLine);
    private static readonly Pen SelectedLinePen = new(Theme.SelectedLine);

    private Column SortedColumn => Sort switch
    {
        SortColumn.Name => Column.Folder,
        SortColumn.Size => Column.Size,
        SortColumn.Items => Column.Items,
        SortColumn.Files => Column.Files,
        SortColumn.Folders => Column.Folders,
        SortColumn.Modified => Column.Modified,
        _ => Column.Allocated,
    };

    private bool IsDim(int key) =>
        key >= 0 && key != _root && _model!.IsDirectory(key) && (_model.Flags[key] & (uint)(NodeFlags.Hidden | NodeFlags.System)) != 0;

    /// <summary>Percentage of the parent's size or allocation (by the sort metric).</summary>
    private double PercentOf(int row)
    {
        int key = _rows[row];
        if (key == _root)
            return 100;
        int parentKey = ParentKey(row);
        long parentValue = parentKey == int.MinValue ? 0 : PercentValue(parentKey);
        return parentValue > 0 ? 100.0 * PercentValue(key) / parentValue : 0;
    }

    private Rectangle BarRect(int y, int height) =>
        Rectangle.FromLTRB(_columnX[(int)Column.Percent] + this.Px(4), y + this.Px(3), _columnX[(int)Column.Percent + 1] - this.Px(4), y + height - this.Px(4));

    private void PaintRowShapes(Graphics g, int row, int y, int height, int width)
    {
        TreeModel m = _model!;
        int key = _rows[row];
        bool selected = row == _selectedRow;
        bool isFolder = key >= 0 && m.IsDirectory(key);
        Column sorted = SortedColumn;
        float scale = Theme.Scale(this);

        if (selected)
        {
            g.FillRectangle(SelectedBrush, 0, y, width, height);
        }
        else
        {
            if (row == _hoverRow)
                g.FillRectangle(HoverBrush, 0, y, width, height);
            if (sorted != Column.Folder)
                g.FillRectangle(SortedBrush, _columnX[(int)sorted], y, _columnX[(int)sorted + 1] - _columnX[(int)sorted], height);
        }
        g.DrawLine(selected ? SelectedLinePen : RowLinePen, 0, y + height - 1, width, y + height - 1);

        // Folder cell: chevron and icon.
        int folderLeft = _columnX[(int)Column.Folder];
        int folderRight = _columnX[(int)Column.Folder + 1];
        int x = folderLeft + this.Px(4 + _depths[row] * 18);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (IsExpandable(key))
        {
            float cx = x + 6 * scale, cy = y + height / 2f;
            PointF[] triangle = IsExpanded(key)
                ? [new(cx - 3.5f * scale, cy - 1.8f * scale), new(cx + 3.5f * scale, cy - 1.8f * scale), new(cx, cy + 2.2f * scale)]
                : [new(cx - 1.8f * scale, cy - 3.5f * scale), new(cx + 2.2f * scale, cy), new(cx - 1.8f * scale, cy + 3.5f * scale)];
            g.FillPolygon(ChevronBrush, triangle);
        }
        g.SmoothingMode = SmoothingMode.None;
        x += this.Px(18);
        int iconY = y + (int)((height - 14 * scale) / 2);
        int kind = key == _root && _rootIsDrive ? 0 : isFolder ? (IsDim(key) ? 2 : 1) : 3;
        g.DrawImageUnscaled(Icon(kind), x, iconY);
        if (selected)
        {
            using var outline = new Pen(Theme.Chrome, 2 * scale);
            g.DrawRectangle(outline, folderLeft + scale, y + scale, folderRight - folderLeft - 2 * scale, height - 2 * scale - 1);
        }

        // % of parent bar.
        Rectangle bar = BarRect(y, height);
        g.FillRectangle(BarBackBrush, bar);
        g.FillRectangle(BarBrush, bar.X, bar.Y, (int)(bar.Width * Math.Clamp(PercentOf(row), 0, 100) / 100), bar.Height);
    }

    private void PaintRowText(GdiText t, int row, int y, int height)
    {
        TreeModel m = _model!;
        int key = _rows[row];
        bool isFilesRow = key < 0;
        bool isFolder = !isFilesRow && m.IsDirectory(key);
        Color text = IsDim(key) ? Theme.TextDim : Theme.Text;
        Column sorted = SortedColumn;
        Span<char> buffer = _text;
        const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

        if (row == _selectedRow)
            t.Draw("▶", Theme.Font(this, 10), new Rectangle(_columnX[0], y, _columnX[1] - _columnX[0], height), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        int folderRight = _columnX[(int)Column.Folder + 1];
        int x = _columnX[(int)Column.Folder] + this.Px(4 + _depths[row] * 18) + this.Px(40);
        var nameRect = new Rectangle(x, y, Math.Max(0, folderRight - x - this.Px(4)), height);
        if (key == _root)
        {
            t.Draw(_rootLabel, Theme.Grid(this), nameRect, text, Left);
        }
        else if (isFilesRow)
        {
            FilesGroup group = Group(FolderOfFilesKey(key));
            group.Label ??= L.F("FilesRow", Fmt.Count(group.Count), m.GetPath(FolderOfFilesKey(key)));
            t.Draw(group.Label, Theme.Grid(this), nameRect, text, Left);
        }
        else
        {
            int n = m.GetName(key, buffer);
            t.Draw(buffer[..n], Theme.Grid(this), nameRect, text, Left);
        }

        int p = Fmt.Percent(PercentOf(row), buffer);
        t.Draw(buffer[..p], Theme.Grid(this), Rectangle.Inflate(BarRect(y, height), -this.Px(4), 0), Theme.Text, Right);

        Cell(t, Column.Size, y, height, Fmt.Size(SizeOf(key), buffer), text, sorted == Column.Size);
        Cell(t, Column.Allocated, y, height, Fmt.Size(AllocatedOf(key), buffer), text, sorted == Column.Allocated);
        if (isFolder || isFilesRow)
        {
            Cell(t, Column.Items, y, height, Fmt.Count(ItemsOf(key), buffer), text, sorted == Column.Items);
            Cell(t, Column.Files, y, height, Fmt.Count(FilesOf(key), buffer), text, sorted == Column.Files);
            if (isFilesRow)
            {
                "—".CopyTo(buffer);
                Cell(t, Column.Folders, y, height, 1, text, sorted == Column.Folders);
            }
            else
            {
                Cell(t, Column.Folders, y, height, Fmt.Count(FoldersOf(key), buffer), text, sorted == Column.Folders);
            }
        }
        Cell(t, Column.Modified, y, height, Fmt.Date(ModifiedOf(key), buffer), Theme.TextSecondary, sorted == Column.Modified);
    }

    private void Cell(GdiText t, Column column, int y, int height, int length, Color color, bool bold)
    {
        var rect = new Rectangle(_columnX[(int)column] + this.Px(8), y, _columnX[(int)column + 1] - _columnX[(int)column] - this.Px(16), height);
        t.Draw(_text.AsSpan(0, length), bold ? Theme.GridBold(this) : Theme.Grid(this), rect, color,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Folder the row's percentage refers to: the parent folder (also for files listed under a files row).</summary>
    private int ParentKey(int row)
    {
        int key = _rows[row];
        if (key < 0)
            return FolderOfFilesKey(key);
        return key == _root ? int.MinValue : _model!.Parent[key];
    }

    private sealed class FilesGroup
    {
        public static readonly FilesGroup Empty = new();

        public int Count;
        public long Size;
        public long Allocated;
        public long Modified;
        public int[]? Sorted;
        public string? Label;
    }
}
