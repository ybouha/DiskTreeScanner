namespace DiskTreeScanner.App.Ui;

/// <summary>Colors and fonts of the UI, DPI-aware.</summary>
/// <remarks>
/// Sizes are given in CSS pixels at 96 DPI and scaled to the DPI of the monitor showing the control
/// (per-monitor v2). GDI converts points to pixels with the system DPI, so a font for a control on a
/// monitor at <c>dpi</c> is created at <c>px × 0.75 × dpi / SystemDpi</c> points.
/// </remarks>
internal static class Theme
{
    public static readonly Color Chrome = Hex("#1E2A38");
    public static readonly Color ChromeText = Hex("#E6EDF5");
    public static readonly Color ChromeMuted = Hex("#9FB0C4");
    public static readonly Color ChromeMenu = Hex("#C9D4E1");
    public static readonly Color ChromeBorder = Hex("#4A5D74");
    public static readonly Color Steel = Hex("#3D5370");
    public static readonly Color SteelLight = Hex("#9FB4D6");
    public static readonly Color CloseHover = Hex("#C42B1C");

    public static readonly Color Text = Hex("#1F2933");
    public static readonly Color TextSecondary = Hex("#4A5563");
    public static readonly Color TextDim = Hex("#6B7684");
    public static readonly Color TextMuted = Hex("#8A94A3");
    public static readonly Color Link = Hex("#2F5D9A");

    public static readonly Color Line = Hex("#D5DBE3");
    public static readonly Color LineStrong = Hex("#C5CFDB");
    public static readonly Color LineSoft = Hex("#E1E6EC");
    public static readonly Color RowLine = Hex("#EDF0F4");
    public static readonly Color InputBorder = Hex("#B8C3D1");

    public static readonly Color HeaderBack = Hex("#F3F5F8");
    public static readonly Color SortedHeaderBack = Hex("#E4E9F0");
    /// <summary>rgba(61, 83, 112, 0.07) over white.</summary>
    public static readonly Color SortedCellBack = Hex("#F1F3F5");
    public static readonly Color TileBack = Hex("#E8EDF3");
    public static readonly Color SelectedBack = Hex("#FFF6C2");
    public static readonly Color SelectedLine = Hex("#E8D98A");
    public static readonly Color HoverBack = Hex("#F7F9FB");
    public static readonly Color BarBack = Hex("#EEF2F7");
    public static readonly Color Bar = Hex("#8FA7CF");

    public static readonly Color FolderBack = Hex("#D9A520");
    public static readonly Color FolderFront = Hex("#F2C447");
    public static readonly Color FolderBackDim = Hex("#D9C891");
    public static readonly Color FolderFrontDim = Hex("#EBDCAB");
    public static readonly Color Drive = Hex("#8A94A3");

    public static readonly Color Green = Hex("#2E8B3E");
    public static readonly Color GreenDot = Hex("#4FB35F");
    public static readonly Color Amber = Hex("#C08A1A");
    public static readonly Color OrangeDot = Hex("#E0A43A");
    public static readonly Color Warning = Hex("#A3540F");
    public static readonly Color Gauge = Hex("#B5661B");
    public static readonly Color GaugeNormal = Hex("#56709A");
    public static readonly Color StopActive = Hex("#B23A3A");
    public static readonly Color BannerBack = Hex("#FFF4D6");
    public static readonly Color BannerBorder = Hex("#E8C770");

    public static readonly Color[] TreemapShades =
    [
        Hex("#3D5370"), Hex("#56709A"), Hex("#6F88B3"), Hex("#8DA2C6"), Hex("#A9BBD8"), Hex("#C3D0E4"), Hex("#DBE3EE"),
    ];

    private static readonly Dictionary<(float, FontStyle, string, int), Font> Fonts = new();
    private static readonly string MonoFamily = FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";

    /// <summary>DPI GDI uses to turn points into pixels on screen device contexts (the system DPI).</summary>
    public static int SystemDpi { get; } = QuerySystemDpi();

    /// <summary>Diagnostics only (--scale): multiplies every control's DPI to check layouts at 125–200 %.</summary>
    public static float ScaleOverride { get; set; } = 1f;

    private static int QuerySystemDpi()
    {
        using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
        return Math.Max(96, (int)Math.Round(g.DpiY));
    }

    /// <summary>Effective DPI of a control (its monitor's DPI).</summary>
    public static int Dpi(Control control) => (int)Math.Round(control.DeviceDpi * ScaleOverride);

    /// <summary>Scale factor of a control relative to 96 DPI.</summary>
    public static float Scale(Control control) => Dpi(control) / 96f;

    /// <summary>Scales a 96-DPI pixel length to the control's DPI.</summary>
    public static int Px(this Control control, float px) => (int)Math.Round(px * Dpi(control) / 96f);

    /// <summary>Font from a CSS pixel size, for the DPI of the monitor showing <paramref name="control"/>.</summary>
    public static Font Font(Control control, float px, FontStyle style = FontStyle.Regular, string family = "Segoe UI")
    {
        int dpi = Dpi(control);
        var key = (px, style, family, dpi);
        if (!Fonts.TryGetValue(key, out Font? font))
        {
            font = new Font(family, px * 0.75f * dpi / SystemDpi, style, GraphicsUnit.Point);
            Fonts[key] = font;
        }
        return font;
    }

    public static Font Body(Control control) => Font(control, 13);

    public static Font Small(Control control) => Font(control, 12);

    public static Font Caption(Control control) => Font(control, 11, FontStyle.Bold);

    public static Font Grid(Control control) => Font(control, 12.5f);

    public static Font GridBold(Control control) => Font(control, 12.5f, FontStyle.Bold);

    public static Font Mono(Control control) => Font(control, 11.5f, FontStyle.Regular, MonoFamily);

    public static Color Hex(string hex) => ColorTranslator.FromHtml(hex);
}
