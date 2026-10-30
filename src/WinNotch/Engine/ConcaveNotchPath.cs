using System.Windows;
using System.Windows.Media;
using WinNotch.Views.Controls;

namespace WinNotch.Engine;

public static class ConcaveNotchPath
{
    public const double HostCanvasWidth = 760.0;
    public const double HostCanvasHeight = 320.0;

    /// <summary>
    /// Builds the filled concave-top-ear, Apple G2 continuous-squircle-bottom Notch geometry flush with Y = 0.
    /// </summary>
    public static StreamGeometry BuildFillGeometry(double bodyWidth, double bodyHeight, double earRadius, double bottomRadius)
    {
        var geometry = new StreamGeometry();
        if (bodyHeight <= 0.5 || bodyWidth <= 4.0)
        {
            geometry.Freeze();
            return geometry;
        }

        using (StreamGeometryContext ctx = geometry.Open())
        {
            WriteNotchContour(ctx, bodyWidth, bodyHeight, earRadius, bottomRadius, isFilled: true, isClosed: true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Builds an open stroke path along the ears, sides, and bottom (excluding Y = 0 so the top bezel has zero seam).
    /// </summary>
    public static StreamGeometry BuildRimStrokeGeometry(double bodyWidth, double bodyHeight, double earRadius, double bottomRadius)
    {
        var geometry = new StreamGeometry();
        if (bodyHeight <= 1.0 || bodyWidth <= 4.0)
        {
            geometry.Freeze();
            return geometry;
        }

        using (StreamGeometryContext ctx = geometry.Open())
        {
            WriteNotchContour(ctx, bodyWidth, bodyHeight, earRadius, bottomRadius, isFilled: false, isClosed: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void WriteNotchContour(
        StreamGeometryContext ctx,
        double bodyWidth,
        double bodyHeight,
        double earRadius,
        double bottomRadius,
        bool isFilled,
        bool isClosed)
    {
        double earR = Math.Clamp(earRadius, 0.0, Math.Min(bodyHeight * 0.45, 24.0));
        double maxBottomExtent = Math.Min(Math.Max(0.0, bodyHeight - earR), bodyWidth * 0.5);
        double botR = Math.Clamp(bottomRadius, 0.0, maxBottomExtent);

        double left = (HostCanvasWidth - bodyWidth) * 0.5;
        double right = left + bodyWidth;
        double b = bodyHeight;

        ctx.BeginFigure(new Point(left - earR, 0.0), isFilled: isFilled, isClosed: isClosed);

        // 1. Left Concave Top Ear: (left - earR, 0) -> (left, earR) CW
        if (earR > 0.1)
        {
            ctx.ArcTo(new Point(left, earR), new Size(earR, earR), 0.0, false, SweepDirection.Clockwise, true, true);
        }

        if (botR <= 0.1)
        {
            ctx.LineTo(new Point(left, b), true, true);
            ctx.LineTo(new Point(right, b), true, true);
            ctx.LineTo(new Point(right, earR), true, true);
        }
        else
        {
            SquircleBorder.ComputeCornerMetrics(botR, maxBottomExtent, out SquircleBorder.CornerMetrics m);

            // 2. Left Vertical Wall -> Bottom-Left Apple Continuous Squircle Corner
            if (b - m.P > earR + 0.01)
            {
                ctx.LineTo(new Point(left, b - m.P), true, true);
            }
            ctx.BezierTo(new Point(left, b - m.Cp1), new Point(left, b - m.Cp2), new Point(left + m.EndB, b - m.EndA), true, true);
            ctx.BezierTo(new Point(left + m.ArcB, b - m.ArcA), new Point(left + m.ArcA, b - m.ArcB), new Point(left + m.EndA, b - m.EndB), true, true);
            ctx.BezierTo(new Point(left + m.Cp2, b), new Point(left + m.Cp1, b), new Point(left + m.P, b), true, true);

            // 3. Bottom Horizontal Edge
            if (right - m.P > left + m.P + 0.01)
            {
                ctx.LineTo(new Point(right - m.P, b), true, true);
            }

            // 4. Bottom-Right Apple Continuous Squircle Corner -> Right Vertical Wall
            ctx.BezierTo(new Point(right - m.Cp1, b), new Point(right - m.Cp2, b), new Point(right - m.EndA, b - m.EndB), true, true);
            ctx.BezierTo(new Point(right - m.ArcA, b - m.ArcB), new Point(right - m.ArcB, b - m.ArcA), new Point(right - m.EndB, b - m.EndA), true, true);
            ctx.BezierTo(new Point(right, b - m.Cp2), new Point(right, b - m.Cp1), new Point(right, b - m.P), true, true);

            if (b - m.P > earR + 0.01)
            {
                ctx.LineTo(new Point(right, earR), true, true);
            }
        }

        // 5. Right Concave Top Ear: (right, earR) -> (right + earR, 0) CW
        if (earR > 0.1)
        {
            ctx.ArcTo(new Point(right + earR, 0.0), new Size(earR, earR), 0.0, false, SweepDirection.Clockwise, true, true);
        }
    }
}
