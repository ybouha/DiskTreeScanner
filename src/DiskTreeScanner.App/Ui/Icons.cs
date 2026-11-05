using System.Drawing.Drawing2D;

namespace DiskTreeScanner.App.Ui;

/// <summary>The mockup's SVG icons, drawn with GDI+ (coordinates in the SVG's own units, scaled).</summary>
internal static class Icons
{
    private static Matrix Place(float x, float y, float scale) => new(scale, 0, 0, scale, x, y);

    private static void Fill(Graphics g, GraphicsPath path, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static void Stroke(Graphics g, GraphicsPath path, Color color, float width)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPath(pen, path);
    }

    private static GraphicsPath Polygon(params float[] xy)
    {
        var path = new GraphicsPath();
        var points = new PointF[xy.Length / 2];
        for (int i = 0; i < points.Length; i++)
            points[i] = new PointF(xy[2 * i], xy[2 * i + 1]);
        path.AddPolygon(points);
        return path;
    }

    /// <summary>16×14 folder.</summary>
    public static void Folder(Graphics g, float x, float y, float scale, bool dim)
    {
        using var back = Polygon(1, 2.5f, 6, 2.5f, 7.4f, 4.1f, 15, 4.1f, 15, 12.5f, 1, 12.5f);
        using var front = Polygon(1, 5, 15, 5, 15, 12.5f, 1, 12.5f);
        using Matrix m = Place(x, y, scale);
        back.Transform(m);
        front.Transform(m);
        Fill(g, back, dim ? Theme.FolderBackDim : Theme.FolderBack);
        Fill(g, front, dim ? Theme.FolderFrontDim : Theme.FolderFront);
    }

    /// <summary>16×14 drive.</summary>
    public static void Drive(Graphics g, float x, float y, float scale)
    {
        using var body = new GraphicsPath();
        AddRoundedRect(body, 1, 4, 14, 6, 1.5f);
        using Matrix m = Place(x, y, scale);
        body.Transform(m);
        Fill(g, body, Theme.Drive);
        using var led = new SolidBrush(Theme.ChromeText);
        g.FillEllipse(led, x + 11 * scale, y + 6 * scale, 2 * scale, 2 * scale);
    }

    /// <summary>16×14 document.</summary>
    public static void File(Graphics g, float x, float y, float scale)
    {
        using var page = Polygon(4, 1, 9, 1, 12, 4, 12, 13, 4, 13);
        using var fold = new GraphicsPath();
        fold.AddLines(new[] { new PointF(9, 1), new PointF(9, 4), new PointF(12, 4) });
        using Matrix m = Place(x, y, scale);
        page.Transform(m);
        fold.Transform(m);
        Fill(g, page, Color.White);
        Stroke(g, page, Theme.TextDim, 1.1f * scale);
        Stroke(g, fold, Theme.TextDim, 1.1f * scale);
    }

    /// <summary>13×13 play triangle (Scan).</summary>
    public static void Play(Graphics g, float x, float y, float scale, Color color)
    {
        using var path = Polygon(3, 1.5f, 11, 6.5f, 3, 11.5f);
        using Matrix m = Place(x, y, scale);
        path.Transform(m);
        Fill(g, path, color);
    }

    /// <summary>12×12 square (Stop).</summary>
    public static void StopSquare(Graphics g, float x, float y, float scale, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, x + 2 * scale, y + 2 * scale, 8 * scale, 8 * scale);
    }

    /// <summary>14×14 circular arrow (Refresh).</summary>
    public static void Refresh(Graphics g, float x, float y, float scale, Color color)
    {
        using var pen = new Pen(color, 1.4f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawArc(pen, x + 2 * scale, y + 2 * scale, 10 * scale, 10 * scale, -40, 310);
        g.DrawLines(pen, new[] { new PointF(x + 12 * scale, y + 1.5f * scale), new PointF(x + 12 * scale, y + 4 * scale), new PointF(x + 9.5f * scale, y + 4 * scale) });
    }

    /// <summary>13×13 magnifier.</summary>
    public static void Search(Graphics g, float x, float y, float scale, Color color)
    {
        using var pen = new Pen(color, 1.3f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, x + 1.5f * scale, y + 1.5f * scale, 8 * scale, 8 * scale);
        g.DrawLine(pen, x + 8.5f * scale, y + 8.5f * scale, x + 12 * scale, y + 12 * scale);
    }

    /// <summary>14×14 funnel.</summary>
    public static void Funnel(Graphics g, float x, float y, float scale, Color color, bool filled = false)
    {
        using var path = Polygon(1.5f, 2, 12.5f, 2, 8.3f, 7, 8.3f, 11.5f, 5.7f, 12.5f, 5.7f, 7);
        using Matrix m = Place(x, y, scale);
        path.Transform(m);
        if (filled)
            Fill(g, path, color);
        Stroke(g, path, color, 1.3f * scale);
    }

    /// <summary>14×14 folder outline (Open in Explorer).</summary>
    public static void FolderOutline(Graphics g, float x, float y, float scale, Color color)
    {
        using var path = Polygon(1.5f, 4, 1.5f, 2.5f, 5.5f, 2.5f, 6.5f, 4, 12.5f, 4, 12.5f, 12, 1.5f, 12);
        using Matrix m = Place(x, y, scale);
        path.Transform(m);
        Stroke(g, path, color, 1.3f * scale);
    }

    /// <summary>14×14 box with outgoing arrow (Export).</summary>
    public static void Export(Graphics g, float x, float y, float scale, Color color)
    {
        using var pen = new Pen(color, 1.3f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF P(float px, float py) => new(x + px * scale, y + py * scale);
        g.DrawLines(pen, new[] { P(5, 2.5f), P(2, 2.5f), P(2, 11.5f), P(11, 11.5f), P(11, 8.5f) });
        g.DrawLine(pen, P(7, 7), P(12.5f, 1.5f));
        g.DrawLines(pen, new[] { P(8.5f, 1.5f), P(12.5f, 1.5f), P(12.5f, 5.5f) });
    }

    /// <summary>16×16 application logo (title bar).</summary>
    public static void Logo(Graphics g, float x, float y, float scale, Color color)
    {
        using var pen = new Pen(color, 1.4f * scale);
        using var frame = new GraphicsPath();
        AddRoundedRect(frame, 1.5f, 1.5f, 13, 13, 1.5f);
        using Matrix m = Place(x, y, scale);
        frame.Transform(m);
        g.DrawPath(pen, frame);
        PointF P(float px, float py) => new(x + px * scale, y + py * scale);
        g.DrawLine(pen, P(8, 1.5f), P(8, 14.5f));
        g.DrawLine(pen, P(8, 7), P(14.5f, 7));
        g.DrawLine(pen, P(1.5f, 10), P(8, 10));
    }

    /// <summary>10×10 sort arrow (down when descending).</summary>
    public static void SortArrow(Graphics g, float x, float y, float scale, bool descending, Color color)
    {
        using GraphicsPath path = descending ? Polygon(1, 3, 9, 3, 5, 8) : Polygon(1, 7, 9, 7, 5, 2);
        using Matrix m = Place(x, y, scale);
        path.Transform(m);
        Fill(g, path, color);
    }

    /// <summary>10×10 caret (activity log).</summary>
    public static void Caret(Graphics g, float x, float y, float scale, bool up, Color color)
    {
        using var pen = new Pen(color, 1.3f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF P(float px, float py) => new(x + px * scale, y + py * scale);
        if (up)
            g.DrawLines(pen, new[] { P(2, 6.5f), P(5, 3.5f), P(8, 6.5f) });
        else
            g.DrawLines(pen, new[] { P(2, 3.5f), P(5, 6.5f), P(8, 3.5f) });
    }

    public static void WindowMinimize(Graphics g, Rectangle box, float scale, Color color)
    {
        using var pen = new Pen(color, 1 * scale);
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
        g.DrawLine(pen, cx - 5 * scale, cy, cx + 5 * scale, cy);
    }

    public static void WindowMaximize(Graphics g, Rectangle box, float scale, Color color, bool maximized)
    {
        using var pen = new Pen(color, 1 * scale);
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
        if (!maximized)
        {
            g.DrawRectangle(pen, cx - 4.5f * scale, cy - 4.5f * scale, 9 * scale, 9 * scale);
            return;
        }
        g.DrawRectangle(pen, cx - 4.5f * scale, cy - 2.5f * scale, 7 * scale, 7 * scale);
        g.DrawLines(pen, new[] { new PointF(cx - 2.5f * scale, cy - 2.5f * scale), new PointF(cx - 2.5f * scale, cy - 4.5f * scale), new PointF(cx + 4.5f * scale, cy - 4.5f * scale), new PointF(cx + 4.5f * scale, cy + 2.5f * scale), new PointF(cx + 2.5f * scale, cy + 2.5f * scale) });
    }

    public static void WindowClose(Graphics g, Rectangle box, float scale, Color color)
    {
        using var pen = new Pen(color, 1.1f * scale);
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
        g.DrawLine(pen, cx - 5 * scale, cy - 5 * scale, cx + 5 * scale, cy + 5 * scale);
        g.DrawLine(pen, cx + 5 * scale, cy - 5 * scale, cx - 5 * scale, cy + 5 * scale);
    }

    public static void AddRoundedRect(GraphicsPath path, float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
    }

    /// <summary>Window / taskbar icon drawn from the logo.</summary>
    public static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Theme.Chrome);
            using var path = new GraphicsPath();
            AddRoundedRect(path, 0, 0, 32, 32, 6);
            g.FillPath(back, path);
            Logo(g, 2, 2, 1.75f, Theme.SteelLight);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
