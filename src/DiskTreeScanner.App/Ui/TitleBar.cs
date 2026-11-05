using System.Drawing.Drawing2D;

namespace DiskTreeScanner.App.Ui;

/// <summary>
/// Dark custom title bar: logo, title, menu (File, Scan, View, Options, Help), EN/FR switch and
/// window buttons. Areas without a control answer HTTRANSPARENT to WM_NCHITTEST so the form turns
/// them into the caption (drag, double-click to maximize, Aero Snap, system menu).
/// </summary>
internal sealed class TitleBar : PaintedControl
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private string[] _menus = [];
    private Rectangle[] _menuRects = [];
    private Rectangle _languageRect;
    private Rectangle _minimizeRect;
    private Rectangle _maximizeRect;
    private Rectangle _closeRect;
    private int _hoverMenu = -1;
    private int _hoverButton = -1;
    private int _openMenu = -1;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private bool _maximized;
    private readonly Segmented _language = new() { Dark = true, Options = ["EN", "FR"] };

    public TitleBar()
    {
        BackColor = Theme.Chrome;
        Controls.Add(_language);
        _language.SelectedIndexChanged += (_, _) => LanguageSelected?.Invoke(_language.SelectedIndex == 1 ? "fr" : "en");
    }

    public event Action<int, Rectangle>? MenuClicked;

    public event Action<string>? LanguageSelected;

    public event EventHandler? MinimizeClicked;

    public event EventHandler? MaximizeClicked;

    public event EventHandler? CloseClicked;

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            Invalidate();
        }
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value;
            Invalidate();
        }
    }

    public bool Maximized
    {
        get => _maximized;
        set
        {
            _maximized = value;
            Invalidate();
        }
    }

    public string Language
    {
        set => _language.SelectedIndex = value == "fr" ? 1 : 0;
    }

    public string[] Menus
    {
        get => _menus;
        set
        {
            _menus = value;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>Highlights the menu whose dropdown is open (-1 for none).</summary>
    public int OpenMenu
    {
        get => _openMenu;
        set
        {
            _openMenu = value;
            Invalidate();
        }
    }

    public int MenuCount => _menus.Length;

    public Rectangle MenuScreenRect(int index) => RectangleToScreen(_menuRects[index]);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = Height;
        int button = this.Px(46);
        _closeRect = new Rectangle(Width - button, 0, button, h);
        _maximizeRect = new Rectangle(_closeRect.Left - button, 0, button, h);
        _minimizeRect = new Rectangle(_maximizeRect.Left - button, 0, button, h);
        int languageWidth = this.Px(64);
        int languageHeight = this.Px(20);
        _languageRect = new Rectangle(_minimizeRect.Left - this.Px(16) - languageWidth, (h - languageHeight) / 2, languageWidth, languageHeight);
        _language.Bounds = _languageRect;

        using Graphics g = CreateGraphics();
        int x = this.Px(12) + this.Px(16) + this.Px(8);
        x += TextRenderer.MeasureText(g, _title, Theme.Font(this, 13, FontStyle.Bold)).Width;
        if (_subtitle.Length > 0)
            x += this.Px(4) + TextRenderer.MeasureText(g, _subtitle, Theme.Font(this, 13)).Width;
        x += this.Px(12);
        _menuRects = new Rectangle[_menus.Length];
        for (int i = 0; i < _menus.Length; i++)
        {
            int w = TextRenderer.MeasureText(g, _menus[i], Theme.Font(this, 13)).Width + this.Px(12);
            _menuRects[i] = new Rectangle(x, (h - this.Px(26)) / 2, w, this.Px(26));
            x += w + this.Px(2);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        PerformLayout();
    }

    /// <summary>Recomputes menu positions after the title or subtitle changed.</summary>
    public void Relayout()
    {
        PerformLayout();
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            Point p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            if (!IsInteractive(p))
            {
                m.Result = HTTRANSPARENT;
                return;
            }
        }
        base.WndProc(ref m);
    }

    private bool IsInteractive(Point p)
    {
        if (_minimizeRect.Contains(p) || _maximizeRect.Contains(p) || _closeRect.Contains(p) || _languageRect.Contains(p))
            return true;
        foreach (Rectangle r in _menuRects)
        {
            if (r.Contains(p))
                return true;
        }
        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int menu = Array.FindIndex(_menuRects, r => r.Contains(e.Location));
        int button = _minimizeRect.Contains(e.Location) ? 0 : _maximizeRect.Contains(e.Location) ? 1 : _closeRect.Contains(e.Location) ? 2 : -1;
        if (menu != _hoverMenu || button != _hoverButton)
        {
            _hoverMenu = menu;
            _hoverButton = button;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverMenu = -1;
        _hoverButton = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        int menu = Array.FindIndex(_menuRects, r => r.Contains(e.Location));
        if (menu >= 0)
            MenuClicked?.Invoke(menu, RectangleToScreen(_menuRects[menu]));
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
            return;
        if (_minimizeRect.Contains(e.Location))
            MinimizeClicked?.Invoke(this, EventArgs.Empty);
        else if (_maximizeRect.Contains(e.Location))
            MaximizeClicked?.Invoke(this, EventArgs.Empty);
        else if (_closeRect.Contains(e.Location))
            CloseClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Chrome);
        float s = DpiScale;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Icons.Logo(g, this.Px(12), (Height - 16 * s) / 2, s, Theme.SteelLight);
        g.SmoothingMode = SmoothingMode.None;
        int x = this.Px(12) + this.Px(16) + this.Px(8);
        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        Size title = TextRenderer.MeasureText(g, _title, Theme.Font(this, 13, FontStyle.Bold));
        TextRenderer.DrawText(g, _title, Theme.Font(this, 13, FontStyle.Bold), new Rectangle(x, 0, title.Width, Height), Theme.ChromeText, flags);
        x += title.Width;
        if (_subtitle.Length > 0)
        {
            int maxWidth = Math.Max(0, (_menuRects.Length > 0 ? _menuRects[0].Left : _languageRect.Left) - x - this.Px(8));
            TextRenderer.DrawText(g, _subtitle, Theme.Font(this, 13), new Rectangle(x + this.Px(4), 0, maxWidth, Height), Theme.ChromeMuted, flags | TextFormatFlags.EndEllipsis);
        }
        for (int i = 0; i < _menus.Length; i++)
        {
            if (i == _hoverMenu || i == _openMenu)
            {
                using var back = new SolidBrush(Theme.Steel);
                g.FillRectangle(back, _menuRects[i]);
            }
            TextRenderer.DrawText(g, _menus[i], Theme.Font(this, 13), _menuRects[i], i == _openMenu ? Color.White : Theme.ChromeMenu,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        Rectangle[] buttons = [_minimizeRect, _maximizeRect, _closeRect];
        for (int i = 0; i < 3; i++)
        {
            if (i == _hoverButton)
            {
                using var back = new SolidBrush(i == 2 ? Theme.CloseHover : Theme.Steel);
                g.FillRectangle(back, buttons[i]);
            }
        }
        Color glyph = Theme.ChromeText;
        Icons.WindowMinimize(g, _minimizeRect, s, glyph);
        Icons.WindowMaximize(g, _maximizeRect, s, glyph, _maximized);
        Icons.WindowClose(g, _closeRect, s, _hoverButton == 2 ? Color.White : glyph);
    }
}
