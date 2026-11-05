using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskTreeScanner.App.Settings;
using DiskTreeScanner.Core.Analysis;
using DiskTreeScanner.Core.Export;
using DiskTreeScanner.Core.Fallback;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Platform;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Mft;
using DiskTreeScanner.Ntfs.Usn;

namespace DiskTreeScanner.App.Ui;

/// <summary>Main window, laid out after mockup\FolderSizes-maquette.html (reference size 1440×900).</summary>
internal sealed partial class MainForm : Form
{
    private const int TitleHeight = 32;
    private const int ToolbarHeight = 38;
    private const int StatusHeight = 26;
    private const int LargestFilesShown = 300;

    private readonly AppSettings _settings;
    private readonly ActivityLog _log;
    private readonly ScanCoordinator _coordinator = new(new MftScanEngine(), new DirectoryScanEngine());
    private readonly Icon _icon = Icons.CreateAppIcon();
    private readonly string? _startupPath;

    // Chrome and toolbar.
    private readonly TitleBar _titleBar = new();
    private readonly Panel _toolbar = new();
    private readonly Label _selectLabel = new() { AutoSize = true, BackColor = Color.White };
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.System };
    private readonly FlatButton _scanButton = new();
    private readonly FlatButton _stopButton = new();
    private readonly FlatButton _refreshButton = new();
    private readonly FlatButton _searchLabel = new();
    private readonly TextBox _searchBox = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly FlatButton _filterButton = new();
    private readonly FlatButton _explorerButton = new();
    private readonly FlatButton _exportButton = new();
    private readonly Label _unitLabel = new() { AutoSize = true, BackColor = Color.White };
    private readonly Segmented _unit = new();
    private readonly ProgressLine _progressLine = new();
    private readonly Panel _banner = new();
    private readonly Label _bannerText = new() { AutoSize = false, BackColor = Theme.BannerBack };
    private readonly FlatButton _bannerButton = new();
    private int[] _separators = [];

    // Content.
    private readonly Panel _content = new();
    private readonly PageHeader _header = new();
    private readonly KpiTiles _kpis = new();
    private readonly Panel _kpiSpacer = new();
    private readonly ToolTip _tips = new() { InitialDelay = 400 };
    private readonly SectionCaption _foldersCaption = new();
    private readonly Panel _gridFrame = new();
    private readonly TreeGrid _grid = new();
    private readonly GripSplitter _grip = new();
    private readonly Panel _bottom = new();
    private readonly SectionCaption _filesCaption = new();
    private readonly Panel _filesFrame = new();
    private readonly FileList _files = new();
    private readonly SectionCaption _treemapCaption = new() { MonospaceDetail = true };
    private readonly TreemapView _treemap = new();
    private readonly Panel _treemapPanel = new();
    private readonly FooterLine _footer = new();
    private readonly DarkStatusBar _status = new();

    private readonly ContextMenuStrip _context = new();
    private readonly ContextMenuStrip[] _menus = new ContextMenuStrip[5];
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _selectionTimer = new() { Interval = 40 };

    // State.
    private ScanResult? _result;
    private Task? _scanTask;
    private CancellationTokenSource? _scanCancel;
    private ScanProgress? _progress;
    private string? _scanningPath;
    private CancellationTokenSource? _selectionCancel;
    private CancellationTokenSource? _searchCancel;
    private NamePattern? _searchPattern;
    private SearchResult? _search;
    private bool _filterOn;
    private readonly ColumnFilters _columnFilters = new();
    private Dictionary<int, FilterTotals>? _filterTotals;
    private IReadOnlyList<WslDistribution> _wsl = Array.Empty<WslDistribution>();
    private string _statusMessage = string.Empty;
    private int _largestTotal;
    private int[] _largest = [];
    private long _renderStart;

    public MainForm(AppSettings settings, ActivityLog log, string? startupPath)
    {
        _settings = settings;
        _log = log;
        _startupPath = startupPath;
        Text = "DiskTreeScanner";
        Icon = _icon;
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        // Per-monitor DPI: every size is applied explicitly for the current DPI (ApplyMetrics),
        // so WinForms must not rescale bounds on its own.
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        BuildLayout();
        BuildMenus();
        Wire();
        ApplyMetrics();
        SetInitialBounds(settings);
        ApplyTexts();
        PopulateTargets();
        UpdateAll();
    }

    /// <summary>Set for --screenshot / --ui-bench runs: preferences are not saved.</summary>
    internal bool Diagnostic { get; set; }

    /// <summary>Default window size, in device-independent pixels (16:10 like the mockup).</summary>
    private static readonly Size DefaultLogicalSize = new(960, 600);

    // ---- Layout ------------------------------------------------------------------------

    private void BuildLayout()
    {
        SuspendLayout();
        _titleBar.Dock = DockStyle.Top;
        _titleBar.Title = "DiskTreeScanner";
        _titleBar.Language = L.Language;

        _toolbar.Dock = DockStyle.Top;
        _toolbar.BackColor = Color.White;
        _scanButton.Glyph = (g, x, y, s, c) => Icons.Play(g, x, y, s, c);
        _scanButton.GlyphColor = Theme.Green;
        _scanButton.GlyphSize = 13;
        _stopButton.Glyph = (g, x, y, s, c) => Icons.StopSquare(g, x, y, s, c);
        _stopButton.GlyphColor = Theme.StopActive;
        _stopButton.GlyphSize = 12;
        _refreshButton.Glyph = (g, x, y, s, c) => Icons.Refresh(g, x, y, s, c);
        _refreshButton.GlyphColor = Theme.Green;
        _searchLabel.Glyph = (g, x, y, s, c) => Icons.Search(g, x, y, s, c);
        _searchLabel.GlyphSize = 13;
        _filterButton.Glyph = (g, x, y, s, c) => Icons.Funnel(g, x, y, s, c, _filterOn);
        _explorerButton.Glyph = (g, x, y, s, c) => Icons.FolderOutline(g, x, y, s, c);
        _exportButton.Glyph = (g, x, y, s, c) => Icons.Export(g, x, y, s, c);
        _unit.Options = ["Auto", "MB", "GB"];
        _unit.SelectedIndex = (int)_settings.SizeUnit;
        _toolbar.Controls.AddRange([_selectLabel, _target, _scanButton, _stopButton, _refreshButton, _searchLabel, _searchBox, _filterButton, _explorerButton, _exportButton, _unitLabel, _unit]);
        _toolbar.Layout += (_, _) => LayoutToolbar();
        _toolbar.Paint += PaintToolbar;

        _progressLine.Dock = DockStyle.Top;

        _banner.Dock = DockStyle.Top;
        _banner.BackColor = Theme.BannerBack;
        _bannerText.ForeColor = Theme.Text;
        _bannerText.TextAlign = ContentAlignment.MiddleLeft;
        _bannerText.Dock = DockStyle.Fill;
        _bannerButton.Dock = DockStyle.Right;
        _bannerButton.BackColor = Theme.BannerBack;
        _bannerButton.Glyph = (g, x, y, s, c) => DrawShield(g, x, y, s);
        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(_bannerButton);
        _banner.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.BannerBorder);
            e.Graphics.DrawLine(pen, 0, _banner.Height - 1, _banner.Width, _banner.Height - 1);
        };
        _banner.Visible = !Elevation.IsElevated;

        // Content (padding 12px 18px 0 18px).
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Color.White;
        _header.Dock = DockStyle.Top;
        _kpis.Dock = DockStyle.Top;
        _kpiSpacer.Dock = DockStyle.Top;
        _kpiSpacer.BackColor = Color.White;
        _foldersCaption.Dock = DockStyle.Top;
        _gridFrame.Dock = DockStyle.Fill;
        _gridFrame.Padding = new Padding(1);
        _gridFrame.BackColor = Theme.LineStrong;
        _grid.Dock = DockStyle.Fill;
        _gridFrame.Controls.Add(_grid);
        _grip.Dock = DockStyle.Bottom;
        _grip.Target = _bottom;
        _grip.MinimumTarget = 110;
        _grip.MaximumTarget = MaximumBottomHeight;
        _bottom.Dock = DockStyle.Bottom;
        _bottom.BackColor = Color.White;

        // Bottom panes: largest files (flex) | treemap (452 px, less on narrow windows).
        _treemapPanel.Dock = DockStyle.Right;
        _treemapPanel.BackColor = Color.White;
        _treemapCaption.Dock = DockStyle.Top;
        _treemap.Dock = DockStyle.Fill;
        _treemapPanel.Controls.Add(_treemap);
        _treemapPanel.Controls.Add(_treemapCaption);
        var filesPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        _filesCaption.Dock = DockStyle.Top;
        _filesFrame.Dock = DockStyle.Fill;
        _filesFrame.Padding = new Padding(1);
        _filesFrame.BackColor = Theme.LineStrong;
        _files.Dock = DockStyle.Fill;
        _filesFrame.Controls.Add(_files);
        filesPanel.Controls.Add(_filesFrame);
        filesPanel.Controls.Add(_filesCaption);
        _bottom.Controls.Add(filesPanel);
        _bottom.Controls.Add(_treemapPanel);

        _footer.Dock = DockStyle.Bottom;

        // Docking runs from the last added control to the first.
        _content.Controls.Add(_gridFrame);
        _content.Controls.Add(_grip);
        _content.Controls.Add(_bottom);
        _content.Controls.Add(_footer);
        _content.Controls.Add(_foldersCaption);
        _content.Controls.Add(_kpiSpacer);
        _content.Controls.Add(_kpis);
        _content.Controls.Add(_header);

        _status.Dock = DockStyle.Bottom;

        Controls.Add(_content);
        Controls.Add(_status);
        Controls.Add(_banner);
        Controls.Add(_progressLine);
        Controls.Add(_toolbar);
        Controls.Add(_titleBar);
        ResumeLayout(true);
    }

    private static void DrawShield(Graphics g, float x, float y, float s)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(x + 7 * s, y + 1 * s), new PointF(x + 13 * s, y + 3 * s), new PointF(x + 12 * s, y + 9 * s),
            new PointF(x + 7 * s, y + 13 * s), new PointF(x + 2 * s, y + 9 * s), new PointF(x + 1 * s, y + 3 * s),
        });
        using var blue = new SolidBrush(Theme.Hex("#2F6FBA"));
        using var gold = new SolidBrush(Theme.Hex("#E8B931"));
        g.FillPath(blue, path);
        g.SetClip(new RectangleF(x + 7 * s, y, 7 * s, 7 * s));
        g.FillPath(gold, path);
        g.SetClip(new RectangleF(x, y + 7 * s, 7 * s, 7 * s));
        g.FillPath(gold, path);
        g.ResetClip();
    }

    /// <summary>Sizes that depend on the DPI (startup and every monitor change).</summary>
    private void ApplyMetrics()
    {
        SuspendLayout();
        Font = Theme.Body(this);
        MinimumSize = new Size(this.Px(720), this.Px(480));
        _titleBar.Height = this.Px(TitleHeight);
        _toolbar.Height = this.Px(ToolbarHeight);
        _target.Font = Theme.Body(this);
        _searchBox.Font = Theme.Small(this);
        _selectLabel.Font = Theme.Body(this);
        _unitLabel.Font = Theme.Small(this);
        _progressLine.Height = Math.Max(1, this.Px(2));
        _banner.Height = this.Px(34);
        _banner.Padding = new Padding(this.Px(18), 0, this.Px(12), 0);
        _bannerText.Font = Theme.Small(this);
        _bannerButton.Width = _bannerButton.PreferredWidthFor() + this.Px(8);
        _content.Padding = new Padding(this.Px(18), this.Px(12), this.Px(18), 0);
        _grip.Height = this.Px(12);
        _treemapPanel.Padding = new Padding(this.Px(16), 0, 0, 0);
        _treemapCaption.Height = this.Px(22);
        _filesCaption.Height = this.Px(22);
        _footer.Height = this.Px(34);
        _status.Height = this.Px(StatusHeight);
        foreach (ContextMenuStrip? menu in _menus)
        {
            if (menu is not null)
                MenuRenderer.Style(menu, this);
        }
        _context.Font = Theme.Body(this);
        ResumeLayout(false);
        AdaptLayout();
    }

    private bool _adapting;

    /// <summary>
    /// Choices that depend on the window size: on small windows (600 px high) the page title and the
    /// footer line (its facts are also in the tiles and the status bar) are dropped and tiles get
    /// shorter; the bottom panes take at most 40 % of the room they share with the tree; on narrow
    /// windows the treemap gets narrower and the toolbar shows icons only.
    /// </summary>
    private void AdaptLayout()
    {
        if (_adapting)
            return;
        _adapting = true;
        try
        {
            SuspendLayout();
            bool compact = ClientSize.Height < this.Px(760);
            _header.Compact = compact;
            _header.Height = compact ? this.Px(22) : this.Px(50);
            _kpis.Height = compact ? this.Px(46) : this.Px(58);
            _kpiSpacer.Height = compact ? this.Px(8) : this.Px(12);
            _foldersCaption.Height = compact ? this.Px(24) : this.Px(30);
            _footer.Visible = !compact;
            int preferred = this.Px(_settings.BottomPaneHeight) + this.Px(22);
            _bottom.Height = Math.Clamp(preferred, this.Px(110), Math.Max(this.Px(110), MaximumBottomHeight()));
            int contentWidth = ClientSize.Width - this.Px(36);
            _treemapPanel.Width = Math.Clamp((int)(contentWidth * 0.34), this.Px(220), this.Px(452));
            ResumeLayout(true);
            _toolbar.PerformLayout();
            _toolbar.Invalidate();
            _header.Invalidate();
        }
        finally
        {
            _adapting = false;
        }
    }

    /// <summary>Largest height of the bottom panes: 40 % of the room left to the tree and the panes.</summary>
    private int MaximumBottomHeight()
    {
        int chrome = _titleBar.Height + _toolbar.Height + _progressLine.Height + (_banner.Visible ? _banner.Height : 0) + _status.Height;
        int fixedParts = _content.Padding.Top + _header.Height + _kpis.Height + _kpiSpacer.Height + _foldersCaption.Height
            + (_footer.Visible ? _footer.Height : 0) + _grip.Height;
        return Math.Max(0, (ClientSize.Height - chrome - fixedParts) * 2 / 5);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e); // resizes the window to the rectangle suggested for the new monitor
        ApplyMetrics();
        ApplyBorderColor();
        _titleBar.Relayout();
        PerformLayout();
        Invalidate(true);
    }

    private void SetInitialBounds(AppSettings settings)
    {
        Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size logical = settings.Window is [_, _, int w, int h] && w >= 600 && h >= 400 ? new Size(w, h) : DefaultLogicalSize;
        Size = new Size(Math.Min(this.Px(logical.Width), work.Width), Math.Min(this.Px(logical.Height), work.Height));
        if (settings.Window is [int x, int y, _, _] && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(x + this.Px(60), y + this.Px(16)))))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }
        if (settings.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void LayoutToolbar()
    {
        // Icons only (captions in tooltips) when the full toolbar does not fit.
        bool compact = !LayoutToolbar(compact: false);
        if (compact)
            LayoutToolbar(compact: true);
    }

    /// <summary>Places the toolbar items; false when they do not fit with the size unit switch.</summary>
    private bool LayoutToolbar(bool compact)
    {
        foreach (FlatButton b in new[] { _refreshButton, _searchLabel, _explorerButton, _exportButton })
            b.ShowText = !compact;
        _target.Width = compact ? this.Px(150) : this.Px(190);
        _searchBox.Width = compact ? this.Px(130) : this.Px(180);
        int h = _toolbar.Height;
        int x = this.Px(12);
        int Center(Control c) => (h - c.Height) / 2;
        _selectLabel.Location = new Point(x, (h - _selectLabel.Height) / 2);
        x = _selectLabel.Right + this.Px(4);
        _target.Location = new Point(x, Center(_target));
        x = _target.Right + this.Px(6);
        var separators = new List<int>();
        void Place(FlatButton b, int? width = null)
        {
            b.Size = new Size(width ?? b.PreferredWidthFor(), this.Px(28));
            b.Location = new Point(x, (h - b.Height) / 2);
            x = b.Right + this.Px(2);
        }
        Place(_scanButton);
        Place(_stopButton);
        separators.Add(x + this.Px(3));
        x += this.Px(8);
        Place(_refreshButton);
        Place(_searchLabel);
        _searchBox.Location = new Point(x + this.Px(2), Center(_searchBox));
        x = _searchBox.Right + this.Px(4);
        Place(_filterButton, this.Px(32));
        separators.Add(x + this.Px(3));
        x += this.Px(8);
        Place(_explorerButton);
        Place(_exportButton);
        int right = _toolbar.Width - this.Px(12);
        _unit.Size = new Size(this.Px(104), this.Px(24));
        _unit.Location = new Point(right - _unit.Width, (h - _unit.Height) / 2);
        _unitLabel.Location = new Point(_unit.Left - this.Px(8) - _unitLabel.Width, (h - _unitLabel.Height) / 2);
        bool roomForLabel = _unitLabel.Left > x + this.Px(8);
        bool roomForUnit = _unit.Left > x + this.Px(8);
        _unit.Visible = roomForUnit;
        _unitLabel.Visible = roomForLabel;
        _separators = separators.ToArray();
        return compact || roomForLabel;
    }

    private void PaintToolbar(object? sender, PaintEventArgs e)
    {
        using var line = new Pen(Theme.Line);
        e.Graphics.DrawLine(line, 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
        int top = (_toolbar.Height - this.Px(20)) / 2;
        foreach (int x in _separators)
            e.Graphics.DrawLine(line, x, top, x, top + this.Px(20));
    }

    // ---- Menus -------------------------------------------------------------------------

    private ToolStripMenuItem Item(string key, Action action, Keys keys = Keys.None)
    {
        var item = new ToolStripMenuItem(L.S(key), null, (_, _) => action()) { Tag = key };
        if (keys != Keys.None)
            item.ShortcutKeys = keys;
        return item;
    }

    private void BuildMenus()
    {
        var renderer = new MenuRenderer();
        for (int i = 0; i < _menus.Length; i++)
        {
            int index = i;
            _menus[i] = new ContextMenuStrip { Renderer = renderer, ShowImageMargin = true, Font = Theme.Body(this) };
            _menus[i].Closed += (_, _) => _titleBar.OpenMenu = -1;
            _menus[i].Opening += (_, _) => UpdateMenuStates(index);
        }
        _menus[0].Items.AddRange(new ToolStripItem[]
        {
            Item("ScanFolder", BrowseAndScan, Keys.Control | Keys.O),
            Item("ExportReportMenu", ExportSelection, Keys.Control | Keys.E),
            new ToolStripSeparator(),
            Item("OpenLogFolder", OpenLogFolder),
            new ToolStripSeparator(),
            Item("Exit", Close),
        });
        _menus[1].Items.AddRange(new ToolStripItem[]
        {
            Item("Scan", () => StartScan(), Keys.F5),
            Item("Stop", StopScan),
            Item("Refresh", Refresh, Keys.Control | Keys.R),
            new ToolStripSeparator(),
            Item("RestartAsAdmin", RestartElevated),
        });
        var units = new ToolStripMenuItem(L.S("SizeUnit")) { Tag = "SizeUnit" };
        units.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("Auto", null, (_, _) => SetUnit(SizeUnit.Auto)) { Tag = "UnitAuto" },
            new ToolStripMenuItem("MB", null, (_, _) => SetUnit(SizeUnit.MB)),
            new ToolStripMenuItem("GB", null, (_, _) => SetUnit(SizeUnit.GB)),
        });
        _menus[2].Items.AddRange(new ToolStripItem[]
        {
            units,
            Item("ShowFilesInTree", () =>
            {
                _settings.ShowFilesInTree = !_settings.ShowFilesInTree;
                _grid.ShowFilesRow = _settings.ShowFilesInTree;
            }),
            Item("GoUp", GoUp),
            new ToolStripSeparator(),
            Item("ActivityLog", ShowActivityLog),
        });
        var hardLinks = new ToolStripMenuItem(L.S("HardLinks")) { Tag = "HardLinks" };
        hardLinks.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("HardLinksOnce", () => SetHardLinks(HardLinkMode.FirstLinkOnly)),
            Item("HardLinksEach", () => SetHardLinks(HardLinkMode.EveryLink)),
            Item("HardLinksMixed", () => SetHardLinks(HardLinkMode.SizeEveryLinkAllocatedOnce)),
        });
        _menus[3].Items.AddRange(new ToolStripItem[]
        {
            Item("IncludeSpecial", () => ToggleOption(() => _settings.IncludeNtfsSpecialFiles = !_settings.IncludeNtfsSpecialFiles)),
            Item("IncludeStreams", () => ToggleOption(() => _settings.IncludeAlternateStreams = !_settings.IncludeAlternateStreams)),
            hardLinks,
            new ToolStripSeparator(),
            Item("ForceDirectoryScan", () => ToggleOption(() => _settings.ForceDirectoryScan = !_settings.ForceDirectoryScan)),
        });
        _menus[4].Items.AddRange(new ToolStripItem[]
        {
            Item("Shortcuts", () => MessageBox.Show(this, L.S("ShortcutsText"), L.S("Shortcuts"), MessageBoxButtons.OK, MessageBoxIcon.Information)),
            Item("About", () => MessageBox.Show(this, L.F("AboutText", typeof(MainForm).Assembly.GetName().Version?.ToString(3)), L.S("About"), MessageBoxButtons.OK, MessageBoxIcon.Information)),
        });

        _context.Renderer = renderer;
        _context.Font = Theme.Body(this);
    }

    private void UpdateMenuStates(int menu)
    {
        foreach (ToolStripItem item in _menus[menu].Items)
        {
            if (item is not ToolStripMenuItem m)
                continue;
            switch (m.Tag as string)
            {
                case "Scan":
                    m.Enabled = _scanTask is null;
                    break;
                case "Stop":
                    m.Enabled = _scanTask is not null;
                    break;
                case "Refresh":
                    m.Enabled = _scanTask is null && _result is not null;
                    break;
                case "RestartAsAdmin":
                    m.Enabled = !Elevation.IsElevated;
                    break;
                case "ExportReportMenu":
                    m.Enabled = _result is not null;
                    break;
                case "ShowFilesInTree":
                    m.Checked = _settings.ShowFilesInTree;
                    break;
                case "GoUp":
                    m.Enabled = _result is not null && _grid.Root > 0;
                    break;
                case "SizeUnit":
                    for (int i = 0; i < 3; i++)
                        ((ToolStripMenuItem)m.DropDownItems[i]).Checked = (int)_settings.SizeUnit == i;
                    break;
                case "IncludeSpecial":
                    m.Checked = _settings.IncludeNtfsSpecialFiles;
                    break;
                case "IncludeStreams":
                    m.Checked = _settings.IncludeAlternateStreams;
                    break;
                case "ForceDirectoryScan":
                    m.Checked = _settings.ForceDirectoryScan;
                    break;
                case "HardLinks":
                    for (int i = 0; i < 3; i++)
                        ((ToolStripMenuItem)m.DropDownItems[i]).Checked = (int)_settings.HardLinks == i;
                    break;
            }
        }
    }

    // ---- Events ------------------------------------------------------------------------

    private void Wire()
    {
        _titleBar.MenuClicked += (index, rect) =>
        {
            _titleBar.OpenMenu = index;
            _menus[index].Show(rect.Left, rect.Bottom);
        };
        _titleBar.LanguageSelected += language =>
        {
            _settings.Language = language;
            L.SetLanguage(language);
        };
        _titleBar.MinimizeClicked += (_, _) => WindowState = FormWindowState.Minimized;
        _titleBar.MaximizeClicked += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        _titleBar.CloseClicked += (_, _) => Close();
        L.Changed += ApplyTexts;

        _scanButton.Click += (_, _) => StartScan();
        _stopButton.Click += (_, _) => StopScan();
        _refreshButton.Click += (_, _) => Refresh();
        _searchLabel.Click += (_, _) => _searchBox.Focus();
        _filterButton.Click += (_, _) => ToggleFilter();
        _explorerButton.Click += (_, _) => OpenSelectionInExplorer();
        _exportButton.Click += (_, _) => ExportSelection();
        _bannerButton.Click += (_, _) => RestartElevated();
        _unit.SelectedIndexChanged += (_, _) => SetUnit((SizeUnit)_unit.SelectedIndex);
        _target.SelectedIndexChanged += OnTargetChanged;
        _searchBox.KeyDown += OnSearchKeyDown;

        _grid.SelectionChanged += (_, _) =>
        {
            _selectionTimer.Stop();
            _selectionTimer.Start();
        };
        _selectionTimer.Tick += (_, _) =>
        {
            _selectionTimer.Stop();
            UpdateSelection();
        };
        _grid.DrillIn += DrillIn;
        _grid.GoUp += GoUp;
        _grid.OpenFile += node => OpenNode(node);
        _grid.ContextRequested += (key, point) => ShowContext(key < 0 ? TreeGrid.FolderOfFilesKey(key) : key, point);
        _grid.SortChanged += (_, _) =>
        {
            _settings.SortColumn = _grid.Sort;
            _settings.SortDescending = _grid.Descending;
            UpdateCaptions();
            UpdateSelection();
        };
        _grid.ColumnFilters = _columnFilters;
        _grid.ColumnFilterRequested += (column, cell) => ShowColumnFilter(column, cell);
        _grid.ColumnWidths = _settings.TreeColumnWidths ?? [];
        _grid.ColumnsResized += (_, _) => _settings.TreeColumnWidths = _grid.ColumnWidths;
        _files.ColumnWidths = _settings.FileColumnWidths ?? [];
        _files.ColumnsResized += (_, _) => _settings.FileColumnWidths = _files.ColumnWidths;
        _files.NodeSelected += node => _grid.SelectNode(node);
        _files.NodeActivated += node => OpenSelectionInExplorer(node);
        _files.ContextRequested += ShowContext;
        _treemap.TileClicked += node => _grid.SelectNode(node);
        _treemap.TileDoubleClicked += node =>
        {
            if (_result is not null && _result.Model.IsDirectory(node))
                DrillIn(node);
        };
        _grip.Resized += (_, _) => _settings.BottomPaneHeight = (int)((_bottom.Height - this.Px(22)) / Theme.Scale(this));
        _status.ActivityClicked += (_, _) => ShowActivityLog();
        _status.LogsClicked += (_, _) => OpenLogFolder();
        _log.EntryAdded += entry =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => _status.LogCount = _log.Count);
        };
        _progressTimer.Tick += (_, _) => UpdateProgress();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyBorderColor();
        _grid.Focus();
        _ = LoadWslAsync();
        if (_startupPath is not null)
            StartScan(_startupPath);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _titleBar.Maximized = WindowState == FormWindowState.Maximized;
        if (WindowState != FormWindowState.Minimized)
            AdaptLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scanCancel?.Cancel();
        if (!Diagnostic)
        {
            _settings.Maximized = WindowState == FormWindowState.Maximized;
            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            // Position in screen pixels, size in device-independent pixels (restored at any DPI).
            float scale = Theme.Scale(this);
            _settings.Window = [bounds.X, bounds.Y, (int)Math.Round(bounds.Width / scale), (int)Math.Round(bounds.Height / scale)];
            _settings.Language = L.Language;
            _settings.Save();
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5:
                StartScan();
                return true;
            case Keys.Escape:
                if (_scanTask is not null)
                    StopScan();
                else if (_searchPattern is not null || _searchBox.Text.Length > 0)
                    ClearSearch();
                return true;
            case Keys.Control | Keys.F:
                _searchBox.Focus();
                _searchBox.SelectAll();
                return true;
            case Keys.Control | Keys.R:
                Refresh();
                return true;
            case Keys.Control | Keys.E:
                ExportSelection();
                return true;
            case Keys.Control | Keys.O:
                BrowseAndScan();
                return true;
            case Keys.Alt | Keys.Up:
                GoUp();
                return true;
            case Keys.Alt | Keys.Enter when _grid.Focused && _result is not null && IsRealPath(_grid.SelectedNode):
                Shell.Properties(this, _result.Model.GetPath(_grid.SelectedNode));
                return true;
            case Keys.Control | Keys.C when _grid.Focused && _result is not null && _grid.SelectedNode >= 0:
                Shell.CopyPath(_result.Model.GetPath(_grid.SelectedNode));
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- Texts -------------------------------------------------------------------------

    private void ApplyTexts()
    {
        _titleBar.Menus = [L.S("MenuFile"), L.S("MenuScan"), L.S("MenuView"), L.S("MenuOptions"), L.S("MenuHelp")];
        _titleBar.Language = L.Language;
        _selectLabel.Text = L.S("Select");
        _selectLabel.Font = Theme.Body(this);
        _selectLabel.ForeColor = Theme.TextSecondary;
        _scanButton.Text = L.S("Scan");
        _stopButton.Text = L.S("Stop");
        _refreshButton.Text = L.S("Refresh");
        _searchLabel.Text = L.S("Search");
        _searchBox.PlaceholderText = L.S("SearchPlaceholder");
        _explorerButton.Text = L.S("OpenInExplorer");
        _exportButton.Text = L.S("ExportReport");
        _unitLabel.Text = L.S("SizeUnit");
        _unitLabel.Font = Theme.Small(this);
        _unitLabel.ForeColor = Theme.TextSecondary;
        _unit.Options = [L.S("UnitAuto"), L.S("UnitMB"), L.S("UnitGB")];
        _bannerText.Text = L.S("AdminBanner");
        _bannerButton.Text = L.S("RestartAsAdmin");
        _bannerButton.Width = _bannerButton.PreferredWidthFor() + this.Px(8);
        _header.Title = L.S("TreeView");
        _tips.SetToolTip(_filterButton, L.S("FilterTip"));
        _tips.SetToolTip(_scanButton, L.S("Scan") + " (F5)");
        _tips.SetToolTip(_stopButton, L.S("Stop") + " (Esc)");
        _tips.SetToolTip(_refreshButton, L.S("Refresh") + " (Ctrl+R)");
        _tips.SetToolTip(_searchLabel, L.S("Search"));
        _tips.SetToolTip(_explorerButton, L.S("OpenInExplorer"));
        _tips.SetToolTip(_exportButton, L.S("ExportReport") + " (Ctrl+E)");
        foreach (ContextMenuStrip menu in _menus)
            Retranslate(menu.Items);
        if (_target.Items.Count > 0 && _target.Items[^1] is TargetItem { IsBrowse: true })
        {
            int selected = _target.SelectedIndex;
            _target.Items[^1] = TargetItem.Browse();
            _target.SelectedIndex = selected;
        }
        _grid.RefreshLabels();
        _files.RefreshLabels();
        _treemap.RefreshLabels();
        _toolbar.PerformLayout();
        _toolbar.Invalidate();
        UpdateAll();
    }

    private static void Retranslate(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is string key && key is not ("UnitAuto"))
                item.Text = L.S(key);
            if (item is ToolStripMenuItem { HasDropDownItems: true } menu)
                Retranslate(menu.DropDownItems);
        }
    }

    // ---- Targets -----------------------------------------------------------------------

    private sealed record TargetItem(string Display, string Path, bool IsBrowse = false)
    {
        public override string ToString() => Display;

        public static TargetItem Browse() => new(L.S("BrowseFolder"), string.Empty, IsBrowse: true);
    }

    private bool _populating;

    private void PopulateTargets(string? select = null)
    {
        _populating = true;
        try
        {
            string? current = select ?? (_target.SelectedItem as TargetItem)?.Path ?? _settings.LastPath;
            _target.BeginUpdate();
            _target.Items.Clear();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                        continue;
                    string label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel;
                    _target.Items.Add(new TargetItem($"[{drive.Name.TrimEnd('\\')}] {label}", drive.Name));
                }
                catch (IOException)
                {
                }
            }
            foreach (WslDistribution distribution in _wsl)
                _target.Items.Add(new TargetItem(distribution.UncRoot + (distribution.IsRunning ? string.Empty : " ·"), distribution.UncRoot));
            if (current is not null && !_target.Items.Cast<TargetItem>().Any(t => string.Equals(t.Path, current, StringComparison.OrdinalIgnoreCase)))
                _target.Items.Add(new TargetItem(current, current));
            _target.Items.Add(TargetItem.Browse());
            int index = current is null ? -1 : _target.Items.Cast<TargetItem>().ToList().FindIndex(t => string.Equals(t.Path, current, StringComparison.OrdinalIgnoreCase));
            _target.SelectedIndex = index >= 0 ? index : (_target.Items.Count > 1 ? 0 : -1);
            _target.EndUpdate();
        }
        finally
        {
            _populating = false;
        }
    }

    private void OnTargetChanged(object? sender, EventArgs e)
    {
        if (_populating || _target.SelectedItem is not TargetItem item || !item.IsBrowse)
            return;
        BrowseAndScan();
    }

    private void BrowseAndScan()
    {
        using var dialog = new FolderBrowserDialog { Description = L.S("ChooseFolder"), UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (_result is not null)
            dialog.InitialDirectory = _result.Model.GetPath(_grid.Root);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            PopulateTargets(dialog.SelectedPath);
            StartScan(dialog.SelectedPath);
        }
        else
        {
            PopulateTargets(_settings.LastPath);
        }
    }

    private string? SelectedTargetPath => _target.SelectedItem is TargetItem { IsBrowse: false } item ? item.Path : null;

    private async Task LoadWslAsync()
    {
        try
        {
            _wsl = await Task.Run(() => WslDistributions.List(queryRunning: true, TimeSpan.FromSeconds(4)));
            if (_wsl.Count > 0)
                _log.Info($"WSL distributions: {string.Join(", ", _wsl.Select(d => d.Name + (d.IsRunning ? " (running)" : " (stopped)")))}");
            PopulateTargets();
            UpdateStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    // ---- Scan --------------------------------------------------------------------------

    private async void StartScan(string? path = null)
    {
        if (_scanTask is not null)
            return;
        string? target = path ?? SelectedTargetPath;
        if (target is null)
        {
            BrowseAndScan();
            return;
        }
        ClearSearch(reapply: false);
        _scanCancel = new CancellationTokenSource();
        _progress = new ScanProgress();
        _scanningPath = target;
        ScanOptions options = _settings.ToScanOptions();
        SetScanning(true);
        Task<ScanResult> task = Task.Run(() => _coordinator.Scan(target, options, _progress, _log, _scanCancel.Token));
        _scanTask = task;
        try
        {
            ScanResult result = await task;
            _settings.LastPath = target;
            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            _statusMessage = L.S("StatusCancelled");
            _log.Info($"Scan of {target} cancelled.");
        }
        catch (Exception ex)
        {
            _statusMessage = L.F("StatusFailed", ex.Message);
            _log.Error($"Scan of {target} failed: {ex}");
        }
        finally
        {
            _scanTask = null;
            _scanningPath = null;
            _scanCancel?.Dispose();
            _scanCancel = null;
            SetScanning(false);
            UpdateAll();
        }
    }

    private void StopScan() => _scanCancel?.Cancel();

    private new async void Refresh()
    {
        if (_scanTask is not null || _result is null)
            return;
        ScanResult current = _result;
        if (current.Engine == "MFT" && current.Usn is not null && current.Model.RecordNumber is not null)
        {
            SetScanning(true);
            _progress = new ScanProgress { Phase = "USN" };
            var refreshTask = Task.Run(() => UsnRefresher.TryRefresh(current, _log, CancellationToken.None));
            _scanTask = refreshTask;
            try
            {
                UsnRefreshResult? refreshed = await refreshTask;
                if (refreshed is not null)
                {
                    int keep = _grid.SelectedNode >= 0 ? refreshed.Map(_grid.SelectedNode) : -1;
                    ApplyResult(refreshed.Result, keepSelection: keep);
                    _statusMessage = L.F("StatusRefreshed", Fmt.Duration(refreshed.Duration), Fmt.Count(refreshed.Changes));
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"USN refresh failed ({ex.Message}); rescanning.");
            }
            finally
            {
                _scanTask = null;
                SetScanning(false);
                UpdateAll();
            }
        }
        StartScan(current.Target.Path);
    }

    private void SetScanning(bool scanning)
    {
        _scanButton.Enabled = !scanning;
        _refreshButton.Enabled = !scanning && _result is not null;
        _stopButton.Enabled = scanning;
        _target.Enabled = !scanning;
        _progressLine.Running = scanning;
        _progressLine.Fraction = -1;
        if (scanning)
            _progressTimer.Start();
        else
            _progressTimer.Stop();
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_progress is null || _scanTask is null)
            return;
        ScanProgress p = _progress;
        double fraction = p.Fraction;
        _progressLine.Fraction = fraction;
        string detail = p.Engine == "MFT" && p.BytesTotal > 0
            ? L.F("StatusMftProgress", Fmt.SizeAuto(p.BytesDone), Fmt.SizeAuto(p.BytesTotal), Fmt.Compact((long)p.ItemsPerSecond))
            : L.F("StatusDirProgress", Fmt.Count(p.Items), Fmt.Count(p.Directories), Fmt.Compact((long)p.ItemsPerSecond));
        _statusMessage = L.F("StatusScanning", _scanningPath ?? string.Empty, detail);
        UpdateStatus();
        UpdateKpis();
    }

    private void ApplyResult(ScanResult result, int keepSelection = -1)
    {
        ScanResult? previous = _result;
        _result = result;
        _renderStart = Stopwatch.GetTimestamp();
        _grid.SetSort(_settings.SortColumn, _settings.SortDescending);
        _grid.ShowFilesRow = _settings.ShowFilesInTree;
        _grid.SetModel(result.Model, result.ViewRoot, RootLabel(result, result.ViewRoot), IsDriveRoot(result, result.ViewRoot));
        if (keepSelection >= 0)
            _grid.SelectNode(keepSelection);
        _largest = [];
        _filterTotals = null;
        ReapplyFilter();
        UpdateSelection();
        UpdateAll();
        _grid.Update();
        double firstRender = Stopwatch.GetElapsedTime(_renderStart).TotalMilliseconds;
        _statusMessage = L.F("StatusComplete", Fmt.Duration(result.Duration), Fmt.Count(result.Model.ItemCount(result.ViewRoot)));
        _log.Info($"Scan complete: {result.Engine} engine, {result.Model.ItemCount(result.ViewRoot):N0} items in {result.Duration.TotalMilliseconds:F0} ms; first render {firstRender:F1} ms.");
        foreach (PhaseTiming phase in result.Timings.Phases)
            _log.Info($"  {phase.Name}: {phase.Duration.TotalMilliseconds:F1} ms {phase.Detail}");
        foreach (string warning in result.Warnings)
            _log.Warn(warning);
        if (previous is not null)
        {
            // Release the previous model (hundreds of MB on a large volume) right away.
            BeginInvoke(() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true));
        }
        UpdateStatus();
    }

    private static bool IsDriveRoot(ScanResult result, int node) => node == 0 && result.Target.IsVolumeRoot;

    private string RootLabel(ScanResult result, int node)
    {
        if (node != 0)
            return result.Model.GetPath(node);
        if (result.Target.IsVolumeRoot && result.Target.Kind is not (DriveKind.Network or DriveKind.Wsl))
        {
            string root = result.Target.VolumeRoot ?? result.Target.Path;
            string label = result.Target.Label.Length > 0 ? result.Target.Label : root;
            return $"[{root.TrimEnd('\\')}] {label}";
        }
        return result.Target.Path;
    }

    // ---- Navigation --------------------------------------------------------------------

    private void DrillIn(int node)
    {
        if (_result is null || !_result.Model.IsDirectory(node) || node == _grid.Root)
            return;
        _grid.SetModel(_result.Model, node, RootLabel(_result, node), false);
        ReapplyFilter();
        UpdateAll();
    }

    private void GoUp()
    {
        if (_result is null || _grid.Root <= 0)
            return;
        int previous = _grid.Root;
        int parent = _result.Model.Parent[previous];
        _grid.SetModel(_result.Model, parent, RootLabel(_result, parent), IsDriveRoot(_result, parent));
        ReapplyFilter();
        _grid.SelectNode(previous);
        UpdateAll();
    }

    // ---- Selection ---------------------------------------------------------------------

    private void UpdateSelection()
    {
        UpdateKpis();
        UpdateFooter();
        if (_result is null)
        {
            _files.SetItems(null, []);
            _treemap.SetFolder(null, -1, false, true);
            UpdateCaptions();
            return;
        }
        TreeModel m = _result.Model;
        int node = _grid.SelectedNode;
        if (node < 0)
            node = _grid.Root;
        bool filesRow = _grid.SelectedIsFilesRow;
        int folder = m.IsDirectory(node) ? node : Math.Max(0, m.Parent[node]);
        _treemap.SetFolder(m, folder, filesRow, _grid.Sort != SortColumn.Size);
        _treemapCaption.Set(L.S("Treemap"), m.GetPath(folder));
        if (_searchPattern is null)
            ComputeLargest(m, node, filesRow);
        UpdateCaptions();
    }

    private void ComputeLargest(TreeModel model, int node, bool filesRow)
    {
        _selectionCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _selectionCancel = cancel;
        // The value criteria of the column filters (size, allocation, date) also apply to this list.
        ItemFilter? values = _columnFilters.HasValueCriteria ? _columnFilters.ToItemFilter(null) : null;
        Task.Run(() =>
        {
            if (filesRow)
            {
                var files = new List<int>();
                foreach (int c in model.Children(node))
                {
                    if (!model.IsDirectory(c) && (values is null || values.MatchesValues(model, c)))
                        files.Add(c);
                }
                files.Sort((a, b) => model.Size[b].CompareTo(model.Size[a]));
                return (Items: files.Take(LargestFilesShown).ToArray(), Total: files.Count);
            }
            int[] top = LargestFiles.Find(model, node, LargestFilesShown, values, out int kept, cancellationToken: cancel.Token);
            return (Items: top, Total: values is not null ? kept : model.IsDirectory(node) ? model.FileCount(node) : 1);
        }, cancel.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !cancel.IsCancellationRequested && _result?.Model == model && _searchPattern is null)
            {
                _largest = t.Result.Items;
                _largestTotal = t.Result.Total;
                _files.SetItems(model, _largest);
                UpdateCaptions();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Search and filter -------------------------------------------------------------

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            RunSearch();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            ClearSearch();
        }
    }

    private void RunSearch() => ApplyFilters();

    /// <summary>
    /// Applies the search text and the column filters: the search lists matches in the bottom pane;
    /// the name pattern (when the filter is on) and the value criteria restrict the tree, whose
    /// totals are recomputed in the background; the row criteria are applied by the grid.
    /// </summary>
    private void ApplyFilters()
    {
        _searchCancel?.Cancel();
        _searchCancel = null;
        NamePattern? pattern = _result is null ? null : NamePattern.Parse(_searchBox.Text, _result.Model.UpCase);
        _searchPattern = pattern;
        _columnFilters.Name = _filterOn ? pattern?.Text : null;
        if (_result is null)
        {
            _search = null;
            _filterTotals = null;
            _grid.Invalidate();
            UpdateCaptions();
            return;
        }
        _grid.RefreshColumnFilters();
        TreeModel model = _result.Model;
        ItemFilter filter = _columnFilters.ToItemFilter(_filterOn ? pattern : null);
        if (pattern is null && filter.IsEmpty)
        {
            _search = null;
            _filterTotals = null;
            if (_grid.FilterActive)
                _grid.SetFilter(null);
            UpdateSelection();
            return;
        }
        var cancel = new CancellationTokenSource();
        _searchCancel = cancel;
        int root = _grid.Root;
        if (pattern is not null)
            _filesCaption.Set(L.S("SearchResults"), L.S("Searching"));
        Task.Run(() =>
        {
            SearchResult? found = pattern is null ? null : NameSearch.Find(model, root, pattern, filter, 2000, cancel.Token);
            Dictionary<int, FilterTotals>? totals = filter.IsEmpty ? null : NameSearch.Filter(model, root, filter, cancel.Token);
            return (found, totals);
        }, cancel.Token).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || cancel.IsCancellationRequested || _result?.Model != model)
                return;
            _search = t.Result.found;
            _filterTotals = t.Result.totals;
            _grid.SetFilter(_filterTotals);
            if (_search is not null && pattern is not null)
            {
                _files.SetItems(model, _search.Matches);
                _log.Info($"Search \"{pattern.Text}\": {_search.TotalMatches:N0} matches, {Format(_search.TotalAllocated)} allocated.");
            }
            UpdateCaptions();
        }, TaskScheduler.FromCurrentSynchronizationContext());

        static string Format(long bytes) => DiskTreeScanner.Core.Formatting.Format.Bytes(bytes);
    }

    private void ClearSearch() => ClearSearch(reapply: true);

    private void ClearSearch(bool reapply)
    {
        _searchCancel?.Cancel();
        _searchPattern = null;
        _search = null;
        if (_searchBox.Text.Length > 0)
            _searchBox.Text = string.Empty;
        if (reapply)
            ApplyFilters();
        else
            UpdateCaptions();
    }

    private void ToggleFilter()
    {
        SetNameFilter(!_filterOn);
        if (_filterOn && _searchBox.Text.Trim().Length == 0)
        {
            _searchBox.Focus();
            return;
        }
        ApplyFilters();
    }

    private void SetNameFilter(bool on)
    {
        _filterOn = on;
        _filterButton.Checked = on;
        _filterButton.Invalidate();
    }

    private void ReapplyFilter()
    {
        if (_searchPattern is not null || _columnFilters.Any)
            ApplyFilters();
    }

    /// <summary>Opens the filter drop-down of a column header.</summary>
    private ToolStripDropDown ShowColumnFilter(FilterColumn column, Rectangle headerCell)
    {
        if (column == FilterColumn.Name)
            _columnFilters.Name = _filterOn ? _searchBox.Text.Trim() is { Length: > 0 } text ? text : null : null;
        var popup = new ColumnFilterPopup(_grid, column, ColumnLabel(column), _columnFilters);
        popup.Applied += (c, bounds, name) =>
        {
            if (c == FilterColumn.Name)
            {
                _searchBox.Text = name ?? string.Empty;
                SetNameFilter(name is not null);
            }
            else
            {
                _columnFilters[c] = bounds;
            }
            _log.Info(_columnFilters.IsActive(c) || (c == FilterColumn.Name && name is not null)
                ? $"Column filter {c}: {(c == FilterColumn.Name ? name : _columnFilters.Describe(c))}."
                : $"Column filter {c} cleared.");
            ApplyFilters();
        };
        popup.Closed += (_, _) => BeginInvoke(popup.Dispose);
        popup.ShowUnder(headerCell);
        return popup;
    }

    private void ClearAllFilters()
    {
        _columnFilters.ClearValues();
        SetNameFilter(false);
        ClearSearch();
    }

    private static string ColumnLabel(FilterColumn column) => L.S(column switch
    {
        FilterColumn.Name => "ColFolder",
        FilterColumn.Percent => "ColPercent",
        FilterColumn.Size => "ColSize",
        FilterColumn.Allocated => "ColAllocated",
        FilterColumn.Items => "ColItems",
        FilterColumn.Files => "ColFiles",
        FilterColumn.Folders => "ColFolders",
        _ => "ColModified",
    });

    /// <summary>"Size ≥ 1 GB · Modified ≥ 01/01/2025" for the active value and row criteria.</summary>
    private string DescribeColumnFilters()
    {
        var parts = new List<string>();
        foreach (FilterColumn c in Enum.GetValues<FilterColumn>())
        {
            if (c != FilterColumn.Name && _columnFilters.IsActive(c))
                parts.Add($"{ColumnLabel(c)} {_columnFilters.Describe(c)}");
        }
        return string.Join(" · ", parts);
    }

    // ---- Commands ----------------------------------------------------------------------

    private bool IsRealPath(int node) => _result is not null && node >= 0 && !_result.Model.HasFlag(node, NodeFlags.Synthetic) && !IsUnderOrphans(node);

    private bool IsUnderOrphans(int node)
    {
        TreeModel m = _result!.Model;
        for (int x = node; x > 0; x = m.Parent[x])
        {
            if (m.HasFlag(x, NodeFlags.Synthetic))
                return true;
        }
        return false;
    }

    private void OpenNode(int node)
    {
        if (IsRealPath(node))
            Shell.Open(_result!.Model.GetPath(node));
    }

    private void OpenSelectionInExplorer() => OpenSelectionInExplorer(_grid.SelectedNode);

    private void OpenSelectionInExplorer(int node)
    {
        if (!IsRealPath(node))
            return;
        Shell.OpenInExplorer(_result!.Model.GetPath(node), _result.Model.IsDirectory(node));
    }

    private void ShowContext(int node, Point screen)
    {
        if (_result is null || node < 0)
            return;
        TreeModel m = _result.Model;
        bool real = IsRealPath(node);
        bool folder = m.IsDirectory(node);
        string path = m.GetPath(node);
        string name = node == 0 ? RootLabel(_result, 0) : m.GetName(node);
        string parentPath = node > 0 ? m.GetPath(m.Parent[node]) : string.Empty;
        long allocated = m.Allocated[node];

        // Header: name, location and size, and a pill with the kind of item.
        string pill;
        Color dot;
        if (node == 0 && IsDriveRoot(_result, 0))
        {
            (pill, dot) = (L.S("PillDrive"), Theme.Drive);
        }
        else if (folder)
        {
            bool system = m.HasFlag(node, NodeFlags.System) || m.HasFlag(node, NodeFlags.Hidden);
            (pill, dot) = system ? (L.S("KindSystem"), Theme.TextMuted) : (L.S("ColFolder"), Theme.FolderFront);
        }
        else
        {
            FileKind kind = FileKinds.Classify(m, node, name, parentPath);
            (pill, dot) = (FileKinds.Label(kind), FileKinds.Dot(kind));
        }
        string subtitle = parentPath.Length > 0 ? $"{parentPath} · {Fmt.SizeAuto(allocated)}" : Fmt.SizeAuto(allocated);

        _context.Items.Clear();
        _context.Items.Add(new MenuHeaderItem(name, subtitle, pill, dot));
        _context.Items.Add(MenuItem("CtxOpen", MenuGlyph.Open, () => Shell.Open(path), real, folder ? null : L.S("KeyEnter")));
        _context.Items.Add(MenuItem("OpenInExplorer", MenuGlyph.Explorer, () => Shell.OpenInExplorer(path, folder), real));
        _context.Items.Add(MenuItem("CtxProperties", MenuGlyph.Properties, () => Shell.Properties(this, path), real, L.S("KeyAltEnter")));
        _context.Items.Add(new ToolStripSeparator());

        var copy = MenuItem("CtxCopy", MenuGlyph.Copy, null, true);
        copy.DropDownItems.Add(MenuItem("CtxCopyFullPath", null, () => Shell.CopyPath(path), true, "Ctrl+C"));
        copy.DropDownItems.Add(MenuItem("CtxCopyName", null, () => Shell.CopyPath(name), true));
        string size = Fmt.SizeAuto(folder ? allocated : m.Size[node]);
        copy.DropDownItems.Add(new ToolStripMenuItem(L.F("CtxCopySize", size), null, (_, _) => Shell.CopyPath(size)));
        if (parentPath.Length > 0)
            copy.DropDownItems.Add(MenuItem("CtxCopyParent", null, () => Shell.CopyPath(parentPath), true));
        copy.DropDown.Renderer = _context.Renderer;
        _context.Items.Add(copy);

        string extension = folder ? string.Empty : Path.GetExtension(name);
        if (extension.Length > 1)
        {
            string pattern = "*" + extension;
            _context.Items.Add(new ToolStripMenuItem(L.F("CtxFilterExtension", pattern), MenuGlyphs.Get(MenuGlyph.Filter, this), (_, _) =>
            {
                _searchBox.Text = pattern;
                SetNameFilter(true);
                ApplyFilters();
            }));
        }
        if (_columnFilters.Any || _filterOn || _searchPattern is not null)
            _context.Items.Add(MenuItem("CtxClearFilters", MenuGlyph.ClearFilter, ClearAllFilters, true));
        _context.Items.Add(new ToolStripSeparator());
        _context.Items.Add(MenuItem("CtxDrillIn", MenuGlyph.DrillIn, () => DrillIn(node), folder && node != _grid.Root, L.S("KeyEnter")));
        _context.Items.Add(MenuItem("GoUp", MenuGlyph.GoUp, GoUp, _grid.Root > 0, L.S("KeyBackspace")));
        _context.Items.Add(new ToolStripSeparator());
        _context.Items.Add(MenuItem("ExportReportMenu", MenuGlyph.Export, () => ExportNode(node), folder, "Ctrl+E"));
        MenuRenderer.Style(_context, this);
        _context.Show(screen);
    }

    private ToolStripMenuItem MenuItem(string key, MenuGlyph? glyph, Action? action, bool enabled, string? shortcut = null)
    {
        var item = new ToolStripMenuItem(L.S(key), glyph is { } g ? MenuGlyphs.Get(g, this) : null, action is null ? null : (_, _) => action()) { Enabled = enabled };
        if (shortcut is not null)
        {
            item.ShortcutKeyDisplayString = shortcut;
            item.ShowShortcutKeys = true;
        }
        return item;
    }

    private void ExportSelection()
    {
        if (_result is null)
        {
            MessageBox.Show(this, L.S("NothingToExport"), L.S("ExportTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int node = _grid.SelectedNode >= 0 ? _grid.SelectedNode : _grid.Root;
        if (!_result.Model.IsDirectory(node))
            node = _result.Model.Parent[node];
        ExportNode(node);
    }

    private async void ExportNode(int node)
    {
        if (_result is null || node < 0)
            return;
        TreeModel model = _result.Model;
        string name = node == 0 ? (model.RootPath.Trim('\\').Replace(':', '_').Replace('\\', '_').Replace('$', '_')) : model.GetName(node);
        using var dialog = new SaveFileDialog
        {
            Title = L.S("ExportTitle"),
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"DiskTreeScanner {name} {DateTime.Now:yyyy-MM-dd HHmm}.csv",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string file = dialog.FileName;
        _statusMessage = L.F("StatusExporting", file);
        UpdateStatus();
        try
        {
            var clock = Stopwatch.StartNew();
            long rows = await Task.Run(() => CsvExporter.Export(model, node, file, CancellationToken.None));
            _statusMessage = L.F("StatusExported", Fmt.Count(rows), file);
            _log.Info($"Exported {rows:N0} rows of {model.GetPath(node)} to {file} in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _statusMessage = ex.Message;
            _log.Error($"Export to {file} failed: {ex.Message}");
        }
        UpdateStatus();
    }

    private void SetUnit(SizeUnit unit)
    {
        _settings.SizeUnit = unit;
        Fmt.Unit = unit;
        _unit.SelectedIndex = (int)unit;
        _grid.Invalidate();
        _files.Invalidate();
        _treemap.RefreshLabels();
        UpdateAll();
    }

    private void SetHardLinks(HardLinkMode mode) => ToggleOption(() => _settings.HardLinks = mode);

    private void ToggleOption(Action change)
    {
        change();
        _settings.Save();
        _log.Info(L.S("OptionsApplyNext"));
        _statusMessage = L.S("OptionsApplyNext");
        UpdateStatus();
    }

    private void RestartElevated()
    {
        if (Elevation.IsElevated)
            return;
        string arguments = SelectedTargetPath is { } path ? $"\"{path.TrimEnd('\\')}{(path.EndsWith('\\') ? "\\\\" : string.Empty)}\"" : string.Empty;
        _settings.Save();
        if (Elevation.RestartElevated(arguments))
            Close();
    }

    private void ShowActivityLog()
    {
        using var form = new ActivityLogForm(_log, _icon);
        form.ShowDialog(this);
    }

    private void OpenLogFolder()
    {
        string folder = Path.GetDirectoryName(_log.FilePath ?? AppSettings.FilePath) ?? AppSettings.Folder;
        Directory.CreateDirectory(folder);
        Shell.OpenInExplorer(folder, isDirectory: true);
    }

    // ---- Panels ------------------------------------------------------------------------

    private void UpdateAll()
    {
        UpdateTitle();
        UpdateKpis();
        UpdateCaptions();
        UpdateFooter();
        UpdateStatus();
        _refreshButton.Enabled = _scanTask is null && _result is not null;
        _explorerButton.Enabled = _result is not null;
        _exportButton.Enabled = _result is not null;
    }

    private void UpdateTitle()
    {
        string subtitle = _result is null ? string.Empty : "— " + RootLabel(_result, _grid.Root);
        if (_titleBar.Subtitle != subtitle)
        {
            _titleBar.Subtitle = subtitle;
            _titleBar.Relayout();
        }
        Text = _result is null ? "DiskTreeScanner" : $"DiskTreeScanner {subtitle}";
        if (_result is null)
        {
            _header.Description = L.S("DescriptionEmpty");
            return;
        }
        string sort = SortName(_grid.Sort);
        _header.Description = L.F(_result.Engine == "MFT" ? "DescriptionMft" : "DescriptionDirectory", _result.Model.GetPath(_grid.Root), sort);
    }

    private static string SortName(SortColumn column) => L.S(column switch
    {
        SortColumn.Name => "ColName",
        SortColumn.Size => "ColSize",
        SortColumn.Items => "ColItems",
        SortColumn.Files => "ColFiles",
        SortColumn.Folders => "ColFolders",
        SortColumn.Modified => "ColModified",
        _ => "ColAllocated",
    }).ToLower(L.Ui);

    private void UpdateCaptions()
    {
        if (_result is null)
        {
            _foldersCaption.Set(L.S("Folders"), string.Empty);
            _filesCaption.Set(L.S("LargestFiles"), string.Empty);
            _treemapCaption.Set(L.S("Treemap"), string.Empty);
            return;
        }
        TreeModel m = _result.Model;
        int root = _grid.Root;
        string sort = SortName(_grid.Sort);
        string columns = DescribeColumnFilters();
        if (columns.Length > 0)
        {
            // Totals of what the filters keep under the root (row criteria only hide rows).
            FilterTotals kept = _filterTotals is not null && _filterTotals.TryGetValue(root, out FilterTotals t) ? t : default;
            string detail = _filterTotals is not null
                ? L.F("ColumnFilteredCaption", columns, Fmt.Count(kept.Files), Fmt.Size(kept.Allocated))
                : L.F("ColumnRowFilteredCaption", columns);
            if (_columnFilters.Name is { } name)
                detail = L.F("NameFilterPrefix", name) + detail;
            _foldersCaption.Set(L.S("Folders"), detail);
        }
        else if (_grid.FilterActive && _search is not null)
        {
            _foldersCaption.Set(L.S("Folders"), L.F("FilteredCaption", _searchPattern?.Text, Fmt.Count(_search.TotalMatches), Fmt.Size(_search.TotalAllocated)));
        }
        else
        {
            _foldersCaption.Set(L.S("Folders"), L.F("FoldersCaption", Fmt.Count(m.ChildCount(root)), sort));
        }
        if (_searchPattern is not null && _search is not null)
            _filesCaption.Set(L.S("SearchResults"), L.F("SearchCaption", _searchPattern.Text, Fmt.Count(_search.TotalMatches), Fmt.Size(_search.TotalAllocated)));
        else if (_searchPattern is null)
            _filesCaption.Set(L.S("LargestFiles"), L.F("LargestCaption", Fmt.Count(Math.Min(_largest.Length, _largestTotal)), Fmt.Count(_largestTotal)));
    }

    private void UpdateKpis()
    {
        var tiles = new Kpi[6];
        if (_result is null)
        {
            tiles[0] = new Kpi(L.S("KpiSelection"), "—", string.Empty);
            tiles[1] = new Kpi(L.S("KpiTotal"), "—", string.Empty);
            tiles[2] = new Kpi(L.S("KpiUsed"), "—", string.Empty, Gauge: 0);
            tiles[3] = new Kpi(L.S("KpiFree"), "—", string.Empty);
            tiles[4] = new Kpi(L.S("KpiItems"), "—", string.Empty);
            tiles[5] = ScanTile(new Kpi(L.S("KpiLastScan"), "—", L.S("NotScanned")));
            _kpis.Tiles = tiles;
            return;
        }
        ScanResult r = _result;
        TreeModel m = r.Model;
        int selected = _grid.SelectedNode >= 0 ? _grid.SelectedNode : _grid.Root;
        long selectionSize = _grid.SelectedIsFilesRow ? FilesSize(m, selected) : m.Size[selected];
        string selectionPath = _grid.SelectedIsFilesRow ? L.F("FilesRow", Fmt.Count(FilesCount(m, selected)), m.GetPath(selected)) : m.GetPath(selected);
        tiles[0] = new Kpi(L.S("KpiSelection"), Fmt.Size(selectionSize), selectionPath);
        long total = r.VolumeTotalBytes, free = r.VolumeFreeBytes, used = Math.Max(0, total - free);
        double usedFraction = total > 0 ? (double)used / total : 0;
        double freeFraction = total > 0 ? (double)free / total : 0;
        string media = r.Media switch { MediaKind.Ssd => "SSD", MediaKind.Hdd => "HDD", _ => r.Target.Kind.ToString() };
        tiles[1] = new Kpi(L.S("KpiTotal"), Fmt.Size(total), $"{r.Target.FileSystem} · {media}");
        tiles[2] = new Kpi(L.S("KpiUsed"), Fmt.Size(used), Fmt.Percent(usedFraction * 100), Gauge: usedFraction, GaugeColor: usedFraction > 0.9 ? Theme.Gauge : Theme.GaugeNormal);
        bool low = total > 0 && freeFraction < 0.10;
        tiles[3] = new Kpi(L.S("KpiFree"), Fmt.Size(free), Fmt.Percent(freeFraction * 100) + (low ? " · " + L.S("BelowTenPercent") : string.Empty), low ? Theme.Warning : null);
        int root = r.ViewRoot;
        tiles[4] = new Kpi(L.S("KpiItems"), Fmt.Count(m.ItemCount(root)), L.F("FilesAndFolders", Fmt.Compact(m.FileCount(root)), Fmt.Compact(m.FolderCount(root))));
        tiles[5] = ScanTile(new Kpi(L.S("KpiLastScan"), Fmt.Duration(r.Duration), $"{L.S(r.Engine == "MFT" ? "EngineMft" : "EngineDirectory")} · {Fmt.Time(r.CompletedAt)}"));
        _kpis.Tiles = tiles;
    }

    private Kpi ScanTile(Kpi idle)
    {
        if (_scanTask is null || _progress is null)
            return idle;
        double fraction = _progress.Fraction;
        string value = fraction >= 0 ? Fmt.Percent(fraction * 100) : Fmt.Duration(_progress.Elapsed);
        return new Kpi(L.S("KpiLastScan"), value, L.S("Scanning"), Gauge: fraction >= 0 ? fraction : -1);
    }

    private static long FilesSize(TreeModel m, int folder)
    {
        long size = 0;
        foreach (int c in m.Children(folder))
        {
            if (!m.IsDirectory(c))
                size += m.Size[c];
        }
        return size;
    }

    private static int FilesCount(TreeModel m, int folder)
    {
        int count = 0;
        foreach (int c in m.Children(folder))
        {
            if (!m.IsDirectory(c))
                count++;
        }
        return count;
    }

    private void UpdateFooter()
    {
        if (_result is null)
        {
            _footer.Set(string.Empty, string.Empty);
            return;
        }
        TreeModel m = _result.Model;
        int root = _result.ViewRoot;
        long slack = 0;
        // Slack: allocation beyond the logical size (cluster tails), over the whole scan.
        slack = Math.Max(0, m.Allocated[root] - m.Size[root]);
        string left = L.F("FooterScan", Fmt.Time(_result.CompletedAt), Fmt.Size(m.Size[root]), Fmt.Size(m.Allocated[root]), Fmt.Size(slack));
        int selected = _grid.SelectedNode >= 0 ? _grid.SelectedNode : _grid.Root;
        string right = L.F("FooterSelection", m.GetPath(selected), Fmt.Size(m.Size[selected]));
        _footer.Set(left, right);
    }

    private void UpdateStatus()
    {
        _status.LogCount = _log.Count;
        _status.Message = _statusMessage.Length > 0 ? _statusMessage : L.S("StatusReady");
        var indicators = new List<(Color, string)>();
        if (_result is not null)
        {
            ScanResult r = _result;
            double freeFraction = r.VolumeTotalBytes > 0 ? (double)r.VolumeFreeBytes / r.VolumeTotalBytes : 1;
            string volume = (r.Target.VolumeRoot ?? r.Target.Path).TrimEnd('\\');
            indicators.Add((freeFraction < 0.10 ? Theme.OrangeDot : Theme.GreenDot, L.F("FreeIndicator", volume, Fmt.Size(r.VolumeFreeBytes))));
            indicators.Add(r.Engine == "MFT"
                ? (Theme.GreenDot, L.S("IndicatorMft"))
                : (Theme.OrangeDot, L.F("IndicatorDirectory", r.Target.FileSystem.Length > 0 ? r.Target.FileSystem : r.Target.Kind.ToString())));
        }
        var running = _wsl.Where(d => d.IsRunning).Select(d => d.Name).ToList();
        indicators.Add(running.Count > 0 ? (Theme.GreenDot, L.F("IndicatorWsl", string.Join(" · ", running))) : (Theme.TextMuted, L.S("IndicatorNoWsl")));
        _status.Indicators = indicators.ToArray();
    }

    // ---- Diagnostics ------------------------------------------------------------------

    /// <summary>
    /// Once the startup scan is done, selects a node, applies diagnostic filters and column drags,
    /// optionally opens the context menu or a column's filter drop-down, renders the window (with
    /// the drop-down at its place) to a PNG and exits.
    /// </summary>
    internal void CaptureWhenIdle(string file, string? select, string? popup = null, IReadOnlyList<string>? filters = null, IReadOnlyList<string>? resizes = null)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        int idleTicks = 0;
        bool selected = false;
        timer.Tick += (_, _) =>
        {
            if (_scanTask is not null || _result is null)
            {
                idleTicks = 0;
                return;
            }
            if (!selected)
            {
                selected = true;
                if (select is not null)
                {
                    int node = _result.Model.FindRelative(select);
                    if (node >= 0)
                        _grid.SelectNode(node);
                }
                foreach (string spec in filters ?? [])
                    ApplyDiagnosticFilter(spec);
                if (filters is { Count: > 0 })
                    ApplyFilters();
                return;
            }
            if (++idleTicks < 4)
                return; // let the background panels (largest files, filters) arrive
            timer.Stop();
            foreach (string spec in resizes ?? [])
            {
                if (spec.Split(':') is [string c, string d] && Enum.TryParse(c, true, out FilterColumn column) && int.TryParse(d, out int dx))
                    _grid.SimulateResize(column, _grid.Px(dx));
            }
            _grid.Update();
            ToolStripDropDown? dropDown = null;
            if (string.Equals(popup, "context", StringComparison.OrdinalIgnoreCase))
            {
                int node = _grid.SelectedNode;
                ShowContext(node >= 0 ? node : _grid.Root, _grid.SelectedRowOnScreen());
                dropDown = _context;
            }
            else if (popup is not null && Enum.TryParse(popup, true, out FilterColumn filterColumn))
            {
                dropDown = ShowColumnFilter(filterColumn, _grid.HeaderCellOnScreen(filterColumn));
            }
            Application.DoEvents();
            // PrintWindow renders only this window, as DWM composes it (DrawToBitmap would paint a
            // classic caption over the custom title bar; a screen copy would include other windows).
            base.Refresh();
            Rectangle canvas = Bounds;
            if (dropDown is { Visible: true })
                canvas = Rectangle.Union(canvas, dropDown.Bounds);
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(40, 44, 52));
                Render(g, Handle, Bounds.Location - (Size)canvas.Location, Size);
                if (dropDown is { Visible: true })
                    Render(g, dropDown.Handle, dropDown.Bounds.Location - (Size)canvas.Location, dropDown.Size);
            }
            dropDown?.Close();
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            _log.Info($"Screenshot saved to {file}.");
            Close();
        };
        timer.Start();

        static void Render(Graphics target, nint window, Point at, Size size)
        {
            using var image = new Bitmap(size.Width, size.Height);
            using (Graphics g = Graphics.FromImage(image))
            {
                nint hdc = g.GetHdc();
                PrintWindow(window, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }
            target.DrawImageUnscaled(image, at);
        }
    }

    /// <summary>Diagnostics: "Size:100MB:" or "Files:1000:" sets a column criterion (min:max, either may be empty).</summary>
    private void ApplyDiagnosticFilter(string spec)
    {
        if (spec.Split(':') is not [string c, string min, string max] || !Enum.TryParse(c, true, out FilterColumn column))
            return;
        if (column == FilterColumn.Name)
        {
            _searchBox.Text = min;
            SetNameFilter(min.Length > 0);
            return;
        }
        bool ok = column switch
        {
            FilterColumn.Size or FilterColumn.Allocated => ValueText.TryParseBytes(min, out long? a) & ValueText.TryParseBytes(max, out long? b) && Set(a, b),
            FilterColumn.Percent => ValueText.TryParsePercent(min, out long? a) & ValueText.TryParsePercent(max, out long? b) && Set(a, b),
            FilterColumn.Modified => Set(DateTime.TryParse(min, out DateTime from) ? from.ToFileTimeUtc() : null, DateTime.TryParse(max, out DateTime to) ? to.ToFileTimeUtc() : null),
            _ => ValueText.TryParseCount(min, out long? a) & ValueText.TryParseCount(max, out long? b) && Set(a, b),
        };
        if (!ok)
            _log.Warn($"Diagnostic filter ignored: {spec}");

        bool Set(long? a, long? b)
        {
            _columnFilters[column] = new Bounds(a, b);
            return true;
        }
    }

    /// <summary>Once the startup scan is done, times clicks, expansions and scrolling, logs them and exits.</summary>
    internal void BenchmarkWhenIdle(string reportFile)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            if (_scanTask is not null || _result is null)
                return;
            timer.Stop();
            var lines = new List<string> { $"UI benchmark on {_result.Model.ItemCount(0):N0} items (each time includes a synchronous repaint of the grid)" };
            double Measure(Action action)
            {
                long t0 = Stopwatch.GetTimestamp();
                action();
                _grid.Update();
                return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            foreach (string path in new[] { "Windows", @"Windows\WinSxS", @"Windows\System32", "Users", "Program Files", @"Program Files\WindowsApps" })
            {
                int node = _result.Model.FindRelative(path);
                if (node < 0)
                    continue;
                double select = Measure(() => _grid.SelectNode(node));
                double expand = Measure(() => _grid.ExpandSelected());
                lines.Add($"  {path,-28} children {_result.Model.ChildCount(node),7:N0}: select {select,6:F2} ms, expand (sort + insert rows) {expand,6:F2} ms, rows now {_grid.RowCount:N0}");
            }
            var scroll = new List<double>();
            for (int i = 0; i < 60; i++)
                scroll.Add(Measure(() => _grid.ScrollRows(i < 30 ? 37 : -11)));
            var click = new List<double>();
            for (int i = 0; i < 40; i++)
                click.Add(Measure(() => _grid.SelectRow(3 + i * 7 % 30)));
            var paint = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                _grid.Invalidate();
                _grid.Update();
                paint.Add(_grid.LastPaintMilliseconds);
            }
            lines.Add($"  grid size {_grid.Width}x{_grid.Height}, last paint: {_grid.LastPaintBreakdown}");
            lines.Add($"  scroll (page)   : mean {scroll.Average():F2} ms, max {scroll.Max():F2} ms over {scroll.Count}");
            lines.Add($"  click (select)  : mean {click.Average():F2} ms, max {click.Max():F2} ms over {click.Count}");
            lines.Add($"  full grid paint : mean {paint.Average():F2} ms, max {paint.Max():F2} ms");
            long sel0 = Stopwatch.GetTimestamp();
            UpdateSelection();
            _kpis.Update();
            _treemap.Update();
            lines.Add($"  selection panels (tiles, footer, treemap render): {Stopwatch.GetElapsedTime(sel0).TotalMilliseconds:F2} ms (largest files computed in background)");
            File.WriteAllLines(reportFile, lines);
            foreach (string line in lines)
                _log.Info(line);
            Close();
        };
        timer.Start();
    }

    // ---- Window chrome -----------------------------------------------------------------

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;
    private const int DWMWA_BORDER_COLOR = 34;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private void ApplyBorderColor()
    {
        int color = Theme.Chrome.R | (Theme.Chrome.G << 8) | (Theme.Chrome.B << 16); // COLORREF
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref color, sizeof(int)); // Windows 11; ignored before
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != 0)
        {
            // Remove the caption, keep the system resize borders on the left, right and bottom.
            uint dpi = (uint)DeviceDpi;
            int frameX = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
            int frameY = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
            RECT r = Marshal.PtrToStructure<RECT>(m.LParam);
            r.Left += frameX;
            r.Right -= frameX;
            r.Bottom -= frameY;
            if (IsZoomed(Handle))
                r.Top += frameY; // a maximized window overhangs the screen by its frame
            Marshal.StructureToPtr(r, m.LParam, false);
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == HTCLIENT)
        {
            Point p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            int edge = this.Px(5);
            if (!IsZoomed(Handle) && p.Y < edge)
                m.Result = p.X < edge * 2 ? HTTOPLEFT : p.X > ClientSize.Width - edge * 2 ? HTTOPRIGHT : HTTOP;
            else if (p.Y < _titleBar.Height)
                m.Result = HTCAPTION;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);

    private const uint PW_RENDERFULLCONTENT = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);
}
