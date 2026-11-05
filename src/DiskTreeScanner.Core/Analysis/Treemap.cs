using System.Drawing;

namespace DiskTreeScanner.Core.Analysis;

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk).</summary>
public static class Treemap
{
    /// <summary>
    /// Lays out <paramref name="values"/> (sorted in decreasing order) in <paramref name="bounds"/>.
    /// Items are added to the current strip while that improves its worst aspect ratio; a strip runs
    /// along the shorter side of the remaining rectangle.
    /// </summary>
    public static RectangleF[] Squarify(IReadOnlyList<double> values, RectangleF bounds)
    {
        int n = values.Count;
        var result = new RectangleF[n];
        double total = 0;
        foreach (double v in values)
            total += Math.Max(0, v);
        if (n == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return result;
        double scale = bounds.Width * (double)bounds.Height / total;
        var areas = new double[n];
        for (int i = 0; i < n; i++)
            areas[i] = Math.Max(0, values[i]) * scale;

        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        int start = 0;
        while (start < n)
        {
            double side = Math.Min(w, h);
            if (side <= 0)
                break;
            int end = start;
            double sum = 0;
            double worst = double.MaxValue;
            while (end < n)
            {
                double next = sum + areas[end];
                double nextWorst = Worst(areas, start, end + 1, next, side);
                if (end > start && nextWorst > worst)
                    break;
                sum = next;
                worst = nextWorst;
                end++;
            }
            if (sum <= 0)
                break;
            if (w >= h)
            {
                // Vertical strip on the left.
                double stripWidth = sum / h;
                double cy = y;
                for (int i = start; i < end; i++)
                {
                    double ih = areas[i] / stripWidth;
                    result[i] = new RectangleF((float)x, (float)cy, (float)stripWidth, (float)ih);
                    cy += ih;
                }
                x += stripWidth;
                w -= stripWidth;
            }
            else
            {
                // Horizontal strip on the top.
                double stripHeight = sum / w;
                double cx = x;
                for (int i = start; i < end; i++)
                {
                    double iw = areas[i] / stripHeight;
                    result[i] = new RectangleF((float)cx, (float)y, (float)iw, (float)stripHeight);
                    cx += iw;
                }
                y += stripHeight;
                h -= stripHeight;
            }
            start = end;
        }
        return result;
    }

    private static double Worst(double[] areas, int start, int end, double sum, double side)
    {
        double max = 0, min = double.MaxValue;
        for (int i = start; i < end; i++)
        {
            max = Math.Max(max, areas[i]);
            min = Math.Min(min, areas[i]);
        }
        if (min <= 0)
            return double.MaxValue;
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }
}
