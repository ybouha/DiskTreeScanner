namespace DiskTreeScanner.App.Ui;

/// <summary>
/// Drop-down under a column header to edit that column's filter: a pattern for the name, bounds
/// for the numbers, a date range for the last change, with a few presets. Enter applies, Escape
/// closes. <see cref="Applied"/> receives the new criterion (also when cleared).
/// </summary>
internal sealed class ColumnFilterPopup : ToolStripDropDown
{
    private readonly FilterColumn _column;
    private readonly Control _owner;
    private readonly TextBox _name = new();
    private readonly TextBox _min = new();
    private readonly TextBox _max = new();
    // Dates in the application's language (the control would follow the system's).
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Custom, CustomFormat = L.Numbers.DateTimeFormat.ShortDatePattern, ShowCheckBox = true, Checked = false };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Custom, CustomFormat = L.Numbers.DateTimeFormat.ShortDatePattern, ShowCheckBox = true, Checked = false };
    private readonly Label _error = new() { AutoSize = false, ForeColor = Theme.Warning };

    public ColumnFilterPopup(Control owner, FilterColumn column, string title, ColumnFilters filters)
    {
        _owner = owner;
        _column = column;
        AutoSize = false;
        Padding = Padding.Empty;
        DropShadowEnabled = true;
        Renderer = new PopupRenderer();

        int width = Px(column == FilterColumn.Name ? 300 : 280);
        var panel = new Panel { BackColor = Color.White, Width = width };
        int x = Px(14), y = Px(12), inner = width - 2 * Px(14);
        panel.Controls.Add(new Label
        {
            Text = title.ToUpper(L.Ui),
            Font = Theme.Caption(owner),
            ForeColor = Theme.TextSecondary,
            Location = new Point(x, y),
            AutoSize = true,
        });
        y += Px(24);

        switch (column)
        {
            case FilterColumn.Name:
                y = AddHint(panel, L.S("FilterNameHint"), x, y, inner);
                _name.Text = filters.Name ?? string.Empty;
                Place(panel, _name, x, y, inner);
                y += _name.Height + Px(10);
                break;
            case FilterColumn.Modified:
                y = AddDateRow(panel, L.S("FilterFrom"), _from, filters[column].Min, x, y, inner);
                y = AddDateRow(panel, L.S("FilterTo"), _to, filters[column].Max, x, y, inner);
                y = AddPresets(panel, x, y, inner,
                    (L.S("Preset7Days"), () => SetDates(DateTime.Today.AddDays(-7), null)),
                    (L.S("Preset30Days"), () => SetDates(DateTime.Today.AddDays(-30), null)),
                    (L.S("PresetYear"), () => SetDates(DateTime.Today.AddYears(-1), null)),
                    (L.S("PresetOlderYear"), () => SetDates(null, DateTime.Today.AddYears(-1).AddDays(-1))));
                break;
            default:
                string hint = column switch
                {
                    FilterColumn.Size or FilterColumn.Allocated => L.S("FilterBytesHint"),
                    FilterColumn.Percent => L.S("FilterPercentHint"),
                    _ => L.S("FilterCountHint"),
                };
                y = AddHint(panel, hint, x, y, inner);
                Bounds b = filters[column];
                _min.Text = b.Min is { } min ? Format(min) : string.Empty;
                _max.Text = b.Max is { } max ? Format(max) : string.Empty;
                y = AddRangeRow(panel, x, y, inner);
                if (column is FilterColumn.Size or FilterColumn.Allocated)
                {
                    y = AddPresets(panel, x, y, inner,
                        ("≥ 10 " + Unit("MB"), () => SetRange("10 MB")),
                        ("≥ 100 " + Unit("MB"), () => SetRange("100 MB")),
                        ("≥ 1 " + Unit("GB"), () => SetRange("1 GB")),
                        ("≥ 10 " + Unit("GB"), () => SetRange("10 GB")));
                }
                else if (column == FilterColumn.Percent)
                {
                    y = AddPresets(panel, x, y, inner, ("≥ 1 %", () => SetRange("1")), ("≥ 5 %", () => SetRange("5")), ("≥ 10 %", () => SetRange("10")), ("≥ 25 %", () => SetRange("25")));
                }
                break;
        }

        _error.Font = Theme.Small(owner);
        _error.Location = new Point(x, y);
        _error.Size = new Size(inner, Px(18));
        panel.Controls.Add(_error);
        y += Px(20);

        var clear = new ChipButton { Text = L.S("FilterClear"), Height = Px(28) };
        var apply = new ChipButton { Text = L.S("FilterApply"), Height = Px(28), Primary = true };
        clear.Width = clear.PreferredWidth + Px(8);
        apply.Width = apply.PreferredWidth + Px(8);
        apply.Location = new Point(x + inner - apply.Width, y);
        clear.Location = new Point(apply.Left - Px(8) - clear.Width, y);
        clear.Click += (_, _) => Commit(clear: true);
        apply.Click += (_, _) => Commit(clear: false);
        panel.Controls.Add(clear);
        panel.Controls.Add(apply);
        y += apply.Height + Px(12);
        panel.Height = y;

        foreach (Control c in panel.Controls)
        {
            if (c is TextBox or DateTimePicker)
                c.KeyDown += OnFieldKeyDown;
        }
        var host = new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size };
        Items.Add(host);
        Size = new Size(panel.Width + 2, panel.Height + 2);
    }

    /// <summary>New criterion for the column; null bounds / null name when cleared.</summary>
    public event Action<FilterColumn, Bounds, string?>? Applied;

    public void ShowUnder(Rectangle screenCell)
    {
        Show(new Point(screenCell.Left, screenCell.Bottom));
        Control first = _column switch
        {
            FilterColumn.Name => _name,
            FilterColumn.Modified => _from,
            _ => _min,
        };
        first.Focus();
        if (first is TextBox box)
            box.SelectAll();
    }

    private int Px(float value) => _owner.Px(value);

    private string Unit(string unit) => L.Language == "fr" ? unit.Replace('B', 'o') : unit;

    private string Format(long value) => _column switch
    {
        FilterColumn.Size or FilterColumn.Allocated => ValueText.Bytes(value),
        FilterColumn.Percent => (value / 100.0).ToString("0.##", L.Numbers),
        _ => value.ToString(L.Numbers),
    };

    private int AddHint(Panel panel, string text, int x, int y, int width)
    {
        var hint = new Label { Text = text, Font = Theme.Small(_owner), ForeColor = Theme.TextDim, Location = new Point(x, y), MaximumSize = new Size(width, 0), AutoSize = true };
        panel.Controls.Add(hint);
        return y + hint.PreferredHeight + Px(6);
    }

    private void Place(Panel panel, Control control, int x, int y, int width)
    {
        control.Font = Theme.Body(_owner);
        control.Location = new Point(x, y);
        control.Width = width;
        panel.Controls.Add(control);
    }

    private int AddRangeRow(Panel panel, int x, int y, int width)
    {
        int labelWidth = Px(34);
        int box = (width - 2 * labelWidth - Px(12)) / 2;
        var min = new Label { Text = L.S("FilterMin"), Font = Theme.Small(_owner), ForeColor = Theme.TextSecondary, AutoSize = false, Size = new Size(labelWidth, Px(24)), TextAlign = ContentAlignment.MiddleLeft, Location = new Point(x, y) };
        Place(panel, _min, x + labelWidth, y, box);
        var max = new Label { Text = L.S("FilterMax"), Font = Theme.Small(_owner), ForeColor = Theme.TextSecondary, AutoSize = false, Size = new Size(labelWidth, Px(24)), TextAlign = ContentAlignment.MiddleLeft, Location = new Point(x + labelWidth + box + Px(12), y) };
        Place(panel, _max, max.Right, y, box);
        panel.Controls.Add(min);
        panel.Controls.Add(max);
        return y + _min.Height + Px(10);
    }

    private int AddDateRow(Panel panel, string label, DateTimePicker picker, long? value, int x, int y, int width)
    {
        int labelWidth = Px(64);
        panel.Controls.Add(new Label { Text = label, Font = Theme.Small(_owner), ForeColor = Theme.TextSecondary, AutoSize = false, Size = new Size(labelWidth, Px(26)), TextAlign = ContentAlignment.MiddleLeft, Location = new Point(x, y) });
        Place(panel, picker, x + labelWidth, y, width - labelWidth);
        if (value is { } filetime)
        {
            picker.Value = DateTime.FromFileTimeUtc(filetime).ToLocalTime().Date;
            picker.Checked = true;
        }
        return y + picker.Height + Px(8);
    }

    private int AddPresets(Panel panel, int x, int y, int width, params (string Text, Action Action)[] presets)
    {
        int gap = Px(6);
        int cx = x;
        int rowHeight = Px(24);
        foreach (var (text, action) in presets)
        {
            var chip = new ChipButton { Text = text, Height = rowHeight };
            chip.Width = chip.PreferredWidth;
            if (cx + chip.Width > x + width && cx > x)
            {
                cx = x;
                y += rowHeight + gap;
            }
            chip.Location = new Point(cx, y);
            chip.Click += (_, _) => action();
            panel.Controls.Add(chip);
            cx += chip.Width + gap;
        }
        return y + rowHeight + Px(8);
    }

    private void SetRange(string min)
    {
        _min.Text = _column is FilterColumn.Size or FilterColumn.Allocated && ValueText.TryParseBytes(min, out long? bytes) && bytes is { } b ? ValueText.Bytes(b) : min;
        _max.Text = string.Empty;
        Commit(clear: false);
    }

    private void SetDates(DateTime? from, DateTime? to)
    {
        _from.Checked = from is not null;
        if (from is { } f)
            _from.Value = f;
        _to.Checked = to is not null;
        if (to is { } t)
            _to.Value = t;
        Commit(clear: false);
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            Commit(clear: false);
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            Close();
        }
    }

    private void Commit(bool clear)
    {
        if (clear)
        {
            Applied?.Invoke(_column, default, null);
            Close();
            return;
        }
        if (_column == FilterColumn.Name)
        {
            string text = _name.Text.Trim();
            Applied?.Invoke(_column, default, text.Length == 0 ? null : text);
            Close();
            return;
        }
        long? min, max;
        if (_column == FilterColumn.Modified)
        {
            min = _from.Checked ? _from.Value.Date.ToFileTimeUtc() : null;
            max = _to.Checked ? _to.Value.Date.AddDays(1).AddTicks(-1).ToFileTimeUtc() : null;
        }
        else
        {
            bool ok = _column switch
            {
                FilterColumn.Size or FilterColumn.Allocated => ValueText.TryParseBytes(_min.Text, out min) & ValueText.TryParseBytes(_max.Text, out max),
                FilterColumn.Percent => ValueText.TryParsePercent(_min.Text, out min) & ValueText.TryParsePercent(_max.Text, out max),
                _ => ValueText.TryParseCount(_min.Text, out min) & ValueText.TryParseCount(_max.Text, out max),
            };
            if (!ok)
            {
                _error.Text = L.S("FilterInvalid");
                return;
            }
        }
        if (min is { } a && max is { } b && a > b)
        {
            _error.Text = L.S("FilterMinAboveMax");
            return;
        }
        Applied?.Invoke(_column, new Bounds(min, max), null);
        Close();
    }

    /// <summary>Rounded button: steel when primary, outlined otherwise.</summary>
    private sealed class ChipButton : PaintedControl
    {
        private bool _hover;

        public ChipButton()
        {
            Cursor = Cursors.Hand;
            BackColor = Color.White;
        }

        public bool Primary { get; init; }

        public int PreferredWidth => TextRenderer.MeasureText(Text, Theme.Small(this), Size.Empty, TextFormatFlags.NoPadding).Width + this.Px(22);

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
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            Icons.AddRoundedRect(path, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f, 4 * DpiScale);
            Color fill = Primary ? (_hover ? Theme.Chrome : Theme.Steel) : (_hover ? Theme.BarBack : Color.White);
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);
            using (var pen = new Pen(Primary ? fill : Theme.InputBorder))
                g.DrawPath(pen, path);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            TextRenderer.DrawText(g, Text, Theme.Small(this), ClientRectangle, Primary ? Color.White : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    private sealed class PopupRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Color.White);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Theme.LineStrong);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }
}
