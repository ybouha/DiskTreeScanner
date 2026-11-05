using System.Runtime.InteropServices;

namespace DiskTreeScanner.App.Ui;

/// <summary>
/// Fast GDI text output on one HDC for the grid. TextRenderer acquires the HDC for every call and
/// DrawText lays every string out; here a frame uses one HDC, ExtTextOut with native alignment
/// (right-aligned numbers need no measuring), clipping to the cell, and cached font metrics. Only
/// text that overflows its cell is measured, to place the ellipsis.
/// </summary>
internal sealed unsafe partial class GdiText : IDisposable
{
    private const int TRANSPARENT = 1;
    private const uint TA_LEFT = 0;
    private const uint TA_RIGHT = 2;
    private const uint TA_CENTER = 6;
    private const uint ETO_CLIPPED = 4;
    private static readonly Dictionary<(string Face, float Points, bool Bold), (nint Handle, int Height)> Fonts = new();

    private readonly nint _hdc;
    private nint _previousFont;
    private nint _font;
    private int _fontHeight;
    private int _color = -1;
    private uint _align = uint.MaxValue;

    public GdiText(nint hdc)
    {
        _hdc = hdc;
        SetBkMode(hdc, TRANSPARENT);
    }

    public void Draw(ReadOnlySpan<char> text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
    {
        if (text.IsEmpty || bounds.Width <= 0)
            return;
        Select(font);
        int colorRef = color.R | (color.G << 8) | (color.B << 16);
        if (colorRef != _color)
        {
            SetTextColor(_hdc, colorRef);
            _color = colorRef;
        }
        uint align = (flags & TextFormatFlags.Right) != 0 ? TA_RIGHT : (flags & TextFormatFlags.HorizontalCenter) != 0 ? TA_CENTER : TA_LEFT;
        if (align != _align)
        {
            SetTextAlign(_hdc, align);
            _align = align;
        }
        int x = align == TA_RIGHT ? bounds.Right : align == TA_CENTER ? bounds.Left + bounds.Width / 2 : bounds.Left;
        int y = bounds.Top + (bounds.Height - _fontHeight) / 2;
        var clip = new RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        fixed (char* p = text)
        {
            if ((flags & TextFormatFlags.EndEllipsis) != 0 && align == TA_LEFT)
            {
                int fit;
                SIZE size;
                GetTextExtentExPointW(_hdc, p, text.Length, bounds.Width, &fit, null, &size);
                if (fit < text.Length)
                {
                    // Too long: as many characters as fit before the ellipsis.
                    char ellipsis = '…';
                    SIZE ellipsisSize;
                    GetTextExtentPoint32W(_hdc, &ellipsis, 1, &ellipsisSize);
                    GetTextExtentExPointW(_hdc, p, text.Length, Math.Max(0, bounds.Width - ellipsisSize.Cx), &fit, null, &size);
                    ExtTextOutW(_hdc, x, y, ETO_CLIPPED, &clip, p, fit, null);
                    SIZE head;
                    GetTextExtentPoint32W(_hdc, p, fit, &head);
                    ExtTextOutW(_hdc, x + head.Cx, y, ETO_CLIPPED, &clip, &ellipsis, 1, null);
                    return;
                }
            }
            ExtTextOutW(_hdc, x, y, ETO_CLIPPED, &clip, p, text.Length, null);
        }
    }

    private void Select(Font font)
    {
        var key = (font.Name, font.SizeInPoints, font.Bold);
        if (!Fonts.TryGetValue(key, out var entry))
        {
            // Points converted with the system DPI, like GDI+ and TextRenderer: the theme already
            // sized the font for the monitor.
            var logFont = new LOGFONTW
            {
                lfHeight = -(int)Math.Round(font.SizeInPoints * Theme.SystemDpi / 72f),
                lfWeight = font.Bold ? 700 : 400,
                lfCharSet = 1, // DEFAULT_CHARSET
                lfQuality = 5, // CLEARTYPE_QUALITY
            };
            ReadOnlySpan<char> face = font.Name.AsSpan(0, Math.Min(font.Name.Length, 31));
            for (int i = 0; i < face.Length; i++)
                logFont.lfFaceName[i] = face[i];
            nint handle = CreateFontIndirectW(&logFont);
            nint previous = SelectObject(_hdc, handle);
            int* metrics = stackalloc int[16]; // TEXTMETRICW: tmHeight is the first field
            GetTextMetricsW(_hdc, metrics);
            SelectObject(_hdc, previous);
            entry = (handle, metrics[0]);
            Fonts[key] = entry;
        }
        if (entry.Handle != _font)
        {
            nint previous = SelectObject(_hdc, entry.Handle);
            if (_previousFont == 0)
                _previousFont = previous;
            _font = entry.Handle;
            _fontHeight = entry.Height;
        }
    }

    public void Dispose()
    {
        if (_previousFont != 0)
            SelectObject(_hdc, _previousFont);
        SetTextAlign(_hdc, TA_LEFT);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx, Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        public fixed char lfFaceName[32];
    }

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExtTextOutW(nint hdc, int x, int y, uint options, RECT* rect, char* text, int count, int* dx);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextExtentExPointW(nint hdc, char* text, int count, int maxExtent, int* fit, int* dx, SIZE* size);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextExtentPoint32W(nint hdc, char* text, int count, SIZE* size);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextMetricsW(nint hdc, int* metrics);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextAlign(nint hdc, uint align);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial int SetTextColor(nint hdc, int color);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateFontIndirectW(LOGFONTW* logFont);
}
