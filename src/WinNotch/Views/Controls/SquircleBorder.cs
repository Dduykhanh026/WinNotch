using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinNotch.Views.Controls;

/// <summary>
/// Renders a container with authentic Apple CALayer.cornerCurve = .continuous (G2 superellipse squircle)
/// corner smoothing. Supports SolidColorBrush, GradientBrush, and ImageBrush backgrounds with full GPU
/// anti-aliasing so child artwork and buttons never exhibit abrupt circular-arc kinks or 90-degree leaks.
/// </summary>
public sealed class SquircleBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background),
            typeof(Brush),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty =
        DependencyProperty.Register(
            nameof(BorderBrush),
            typeof(Brush),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.Register(
            nameof(BorderThickness),
            typeof(Thickness),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(
                new Thickness(0),
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(double),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty =
        DependencyProperty.Register(
            nameof(Padding),
            typeof(Thickness),
            typeof(SquircleBorder),
            new FrameworkPropertyMetadata(
                new Thickness(0),
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness
    {
        get => (Thickness)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double bt = Math.Max(0.0, BorderThickness.Left);
        Thickness pad = Padding;
        double insetW = (bt * 2.0) + pad.Left + pad.Right;
        double insetH = (bt * 2.0) + pad.Top + pad.Bottom;

        UIElement? child = Child;
        if (child == null)
        {
            return new Size(insetW, insetH);
        }

        var childConstraint = new Size(
            Math.Max(0.0, constraint.Width - insetW),
            Math.Max(0.0, constraint.Height - insetH));

        child.Measure(childConstraint);
        return new Size(child.DesiredSize.Width + insetW, child.DesiredSize.Height + insetH);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        UIElement? child = Child;
        if (child != null)
        {
            double bt = Math.Max(0.0, BorderThickness.Left);
            Thickness pad = Padding;
            double left = bt + pad.Left;
            double top = bt + pad.Top;
            double w = Math.Max(0.0, arrangeSize.Width - left - bt - pad.Right);
            double h = Math.Max(0.0, arrangeSize.Height - top - bt - pad.Bottom);

            child.Arrange(new Rect(left, top, w, h));
        }
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0.5 || h <= 0.5) return;

        Brush? bg = Background;
        Brush? strokeBrush = BorderBrush;
        double bt = strokeBrush != null ? Math.Max(0.0, BorderThickness.Left) : 0.0;

        if (bg == null && bt <= 0.0) return;

        double halfStroke = bt * 0.5;
        var drawRect = new Rect(
            halfStroke,
            halfStroke,
            Math.Max(0.0, w - bt),
            Math.Max(0.0, h - bt));

        if (drawRect.Width <= 0.1 || drawRect.Height <= 0.1) return;

        double effectiveRadius = Math.Max(0.0, CornerRadius - halfStroke);
        StreamGeometry geom = BuildContinuousSquircleGeometry(drawRect, effectiveRadius);

        Pen? pen = null;
        if (strokeBrush != null && bt > 0.0)
        {
            pen = new Pen(strokeBrush, bt) { LineJoin = PenLineJoin.Round };
        }

        dc.DrawGeometry(bg, pen, geom);
    }

    /// <summary>
    /// Builds a closed G2-continuous Apple squircle (60% corner smoothing) inside <paramref name="rect"/>,
    /// seamlessly blending back to a pure circular arc if radius approaches half the shortest dimension.
    /// </summary>
    public static StreamGeometry BuildContinuousSquircleGeometry(Rect rect, double radius)
    {
        var geometry = new StreamGeometry();
        double halfMin = Math.Min(rect.Width, rect.Height) * 0.5;
        double r = Math.Clamp(radius, 0.0, halfMin);

        using (StreamGeometryContext ctx = geometry.Open())
        {
            if (r <= 0.05)
            {
                ctx.BeginFigure(rect.TopLeft, isFilled: true, isClosed: true);
                ctx.LineTo(rect.TopRight, isStroked: true, isSmoothJoin: true);
                ctx.LineTo(rect.BottomRight, isStroked: true, isSmoothJoin: true);
                ctx.LineTo(rect.BottomLeft, isStroked: true, isSmoothJoin: true);
            }
            else
            {
                ComputeCornerMetrics(r, halfMin, out CornerMetrics m);

                double l = rect.Left;
                double t = rect.Top;
                double ri = rect.Right;
                double b = rect.Bottom;

                // Start at top-left shoulder on top edge, wind clockwise
                ctx.BeginFigure(new Point(l + m.P, t), isFilled: true, isClosed: true);

                // 1. Top edge -> Top-Right Continuous Corner
                if (ri - m.P > l + m.P + 0.01)
                {
                    ctx.LineTo(new Point(ri - m.P, t), isStroked: true, isSmoothJoin: true);
                }
                ctx.BezierTo(new Point(ri - m.Cp1, t), new Point(ri - m.Cp2, t), new Point(ri - m.EndA, t + m.EndB), true, true);
                ctx.BezierTo(new Point(ri - m.ArcA, t + m.ArcB), new Point(ri - m.ArcB, t + m.ArcA), new Point(ri - m.EndB, t + m.EndA), true, true);
                ctx.BezierTo(new Point(ri, t + m.Cp2), new Point(ri, t + m.Cp1), new Point(ri, t + m.P), true, true);

                // 2. Right edge -> Bottom-Right Continuous Corner
                if (b - m.P > t + m.P + 0.01)
                {
                    ctx.LineTo(new Point(ri, b - m.P), isStroked: true, isSmoothJoin: true);
                }
                ctx.BezierTo(new Point(ri, b - m.Cp1), new Point(ri, b - m.Cp2), new Point(ri - m.EndB, b - m.EndA), true, true);
                ctx.BezierTo(new Point(ri - m.ArcB, b - m.ArcA), new Point(ri - m.ArcA, b - m.ArcB), new Point(ri - m.EndA, b - m.EndB), true, true);
                ctx.BezierTo(new Point(ri - m.Cp2, b), new Point(ri - m.Cp1, b), new Point(ri - m.P, b), true, true);

                // 3. Bottom edge -> Bottom-Left Continuous Corner
                if (ri - m.P > l + m.P + 0.01)
                {
                    ctx.LineTo(new Point(l + m.P, b), isStroked: true, isSmoothJoin: true);
                }
                ctx.BezierTo(new Point(l + m.Cp1, b), new Point(l + m.Cp2, b), new Point(l + m.EndA, b - m.EndB), true, true);
                ctx.BezierTo(new Point(l + m.ArcA, b - m.ArcB), new Point(l + m.ArcB, b - m.ArcA), new Point(l + m.EndB, b - m.EndA), true, true);
                ctx.BezierTo(new Point(l, b - m.Cp2), new Point(l, b - m.Cp1), new Point(l, b - m.P), true, true);

                // 4. Left edge -> Top-Left Continuous Corner
                if (b - m.P > t + m.P + 0.01)
                {
                    ctx.LineTo(new Point(l, t + m.P), isStroked: true, isSmoothJoin: true);
                }
                ctx.BezierTo(new Point(l, t + m.Cp1), new Point(l, t + m.Cp2), new Point(l + m.EndB, t + m.EndA), true, true);
                ctx.BezierTo(new Point(l + m.ArcB, t + m.ArcA), new Point(l + m.ArcA, t + m.ArcB), new Point(l + m.EndA, t + m.EndB), true, true);
                ctx.BezierTo(new Point(l + m.Cp2, t), new Point(l + m.Cp1, t), new Point(l + m.P, t), true, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    internal readonly record struct CornerMetrics(
        double P,
        double Cp1,
        double Cp2,
        double EndA,
        double EndB,
        double ArcA,
        double ArcB);

    internal static void ComputeCornerMetrics(double r, double maxExtent, out CornerMetrics metrics)
    {
        // Exact G2 Apple/Figma xi=0.60 constants vs degenerate pure circle (xi=0.0, kappa=0.55228475)
        const double sqP = 1.5286650;
        const double sqCp1 = 0.9503585;
        const double sqCp2 = 0.7599212;
        const double sqEndA = 0.5460095;
        const double sqEndB = 0.1089935;
        const double sqArcA = 0.3578475;
        const double sqArcB = 0.2048669;

        const double cirK = 0.55228475; // Distance from tangent intersection: 1 - 0.55228475 = 0.44771525
        const double cirOffset = 1.0 - cirK;

        double blend = r > 0.001
            ? Math.Clamp((maxExtent - r) / (0.5286650 * r), 0.0, 1.0)
            : 1.0;

        metrics = new CornerMetrics(
            P: r * Lerp(1.0, sqP, blend),
            Cp1: r * Lerp(1.0, sqCp1, blend),
            Cp2: r * Lerp(1.0, sqCp2, blend),
            EndA: r * Lerp(1.0, sqEndA, blend),
            EndB: r * Lerp(0.0, sqEndB, blend),
            ArcA: r * Lerp(cirOffset, sqArcA, blend),
            ArcB: r * Lerp(0.0, sqArcB, blend));
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
