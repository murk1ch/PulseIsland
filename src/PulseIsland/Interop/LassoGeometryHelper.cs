using System.Windows;

namespace PulseIsland.Interop;

/// <summary>
/// Computes a clamped bounding box from freehand lasso mouse points or horizontal text underline strokes.
/// Automatically expands thin horizontal strokes vertically so single-line text is captured completely.
/// </summary>
public static class LassoGeometryHelper
{
    public const double MinimumSelectionSize = 10.0;
    public const double MinimumTextLineHeight = 36.0;

    public static Int32Rect? ComputeBoundingBox(
        IReadOnlyList<Point> points,
        Rect boundsLimit,
        double padding = 6.0)
    {
        if (points is null || points.Count < 2)
            return null;

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (var pt in points)
        {
            if (pt.X < minX) minX = pt.X;
            if (pt.Y < minY) minY = pt.Y;
            if (pt.X > maxX) maxX = pt.X;
            if (pt.Y > maxY) maxY = pt.Y;
        }

        double rawWidth = maxX - minX;
        double rawHeight = maxY - minY;

        // Reject tiny accidental single-clicks where neither width nor height is meaningful
        if (rawWidth < MinimumSelectionSize && rawHeight < MinimumSelectionSize)
            return null;

        // If the user underlined or struck through a line of text horizontally (wide stroke, small height),
        // expand vertically (slightly biased upward since underlines are often drawn beneath text)
        // so the full text line height is captured completely.
        if (rawWidth >= 14.0 && rawHeight < MinimumSelectionSize)
        {
            double midY = (minY + maxY) * 0.5;
            minY = midY - MinimumTextLineHeight * 0.62;
            maxY = midY + MinimumTextLineHeight * 0.48;
        }
        else if (rawWidth < MinimumSelectionSize || rawHeight < MinimumSelectionSize)
        {
            return null;
        }

        double x = Math.Max(boundsLimit.Left, minX - padding);
        double y = Math.Max(boundsLimit.Top, minY - padding);
        double right = Math.Min(boundsLimit.Right, maxX + padding);
        double bottom = Math.Min(boundsLimit.Bottom, maxY + padding);

        int width = (int)Math.Round(right - x);
        int height = (int)Math.Round(bottom - y);

        if (width < (int)MinimumSelectionSize || height < (int)MinimumSelectionSize)
            return null;

        return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), width, height);
    }

    /// <summary>
    /// Computes a live resizable rectangle strictly between drag anchor <paramref name="start"/>
    /// and current pointer <paramref name="current"/>, so moving the cursor back shrinks the selection immediately.
    /// </summary>
    public static Int32Rect? ComputeRectangleBox(
        Point start,
        Point current,
        Rect boundsLimit,
        double padding = 2.0)
    {
        double minX = Math.Min(start.X, current.X);
        double minY = Math.Min(start.Y, current.Y);
        double maxX = Math.Max(start.X, current.X);
        double maxY = Math.Max(start.Y, current.Y);

        double rawWidth = maxX - minX;
        double rawHeight = maxY - minY;

        if (rawWidth < MinimumSelectionSize && rawHeight < MinimumSelectionSize)
            return null;

        // If the user drew a thin horizontal underline stroke across a single line of text,
        // expand vertically so the line is captured cleanly.
        if (rawWidth >= 18.0 && rawHeight < MinimumSelectionSize)
        {
            double midY = (minY + maxY) * 0.5;
            minY = midY - MinimumTextLineHeight * 0.62;
            maxY = midY + MinimumTextLineHeight * 0.48;
        }
        else if (rawWidth < MinimumSelectionSize || rawHeight < MinimumSelectionSize)
        {
            return null;
        }

        double x = Math.Max(boundsLimit.Left, minX - padding);
        double y = Math.Max(boundsLimit.Top, minY - padding);
        double right = Math.Min(boundsLimit.Right, maxX + padding);
        double bottom = Math.Min(boundsLimit.Bottom, maxY + padding);

        int width = (int)Math.Round(right - x);
        int height = (int)Math.Round(bottom - y);

        if (width < (int)MinimumSelectionSize || height < (int)MinimumSelectionSize)
            return null;

        return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), width, height);
    }

    public static Int32Rect ExpandRectClamped(Int32Rect rect, Rect boundsLimit, int expandX = 18, int expandY = 26)
    {
        double x = Math.Max(boundsLimit.Left, rect.X - expandX);
        double y = Math.Max(boundsLimit.Top, rect.Y - expandY);
        double right = Math.Min(boundsLimit.Right, rect.X + rect.Width + expandX);
        double bottom = Math.Min(boundsLimit.Bottom, rect.Y + rect.Height + expandY);
        int w = Math.Max(16, (int)Math.Round(right - x));
        int h = Math.Max(16, (int)Math.Round(bottom - y));
        return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), w, h);
    }
}
