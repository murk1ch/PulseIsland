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
        // expand vertically around the stroke center so the full text line height is captured.
        if (rawWidth >= 18.0 && rawHeight < MinimumSelectionSize)
        {
            double midY = (minY + maxY) * 0.5;
            minY = midY - MinimumTextLineHeight * 0.5;
            maxY = midY + MinimumTextLineHeight * 0.5;
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
}
