using System.Windows;
using System.Windows.Media;

namespace WinNotch.Views.Controls;

/// <summary>
/// GPU-accelerated Monotone Cubic Hermite Sparkline Graph (iStat Menus / macOS Activity Monitor style).
/// Supports single-series (CPU %) and dual-series (Download / Upload) with zero-overshoot splines and gradient fill.
/// </summary>
public sealed class SparklineGraph : FrameworkElement
{
    private static readonly Pen MidGridPen = CreateFrozenGridPen();

    public static readonly DependencyProperty PrimaryValuesProperty = DependencyProperty.Register(
        nameof(PrimaryValues), typeof(double[]), typeof(SparklineGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(
        nameof(SecondaryValues), typeof(double[]), typeof(SparklineGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PrimaryColorProperty = DependencyProperty.Register(
        nameof(PrimaryColor), typeof(Color), typeof(SparklineGraph),
        new FrameworkPropertyMetadata(Color.FromRgb(76, 154, 255), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryColorProperty = DependencyProperty.Register(
        nameof(SecondaryColor), typeof(Color), typeof(SparklineGraph),
        new FrameworkPropertyMetadata(Color.FromRgb(52, 211, 153), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFixedPercentScaleProperty = DependencyProperty.Register(
        nameof(IsFixedPercentScale), typeof(bool), typeof(SparklineGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinAutoPeakProperty = DependencyProperty.Register(
        nameof(MinAutoPeak), typeof(double), typeof(SparklineGraph),
        new FrameworkPropertyMetadata(65536.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? PrimaryValues
    {
        get => (double[]?)GetValue(PrimaryValuesProperty);
        set => SetValue(PrimaryValuesProperty, value);
    }

    public double[]? SecondaryValues
    {
        get => (double[]?)GetValue(SecondaryValuesProperty);
        set => SetValue(SecondaryValuesProperty, value);
    }

    public Color PrimaryColor
    {
        get => (Color)GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    public Color SecondaryColor
    {
        get => (Color)GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    public bool IsFixedPercentScale
    {
        get => (bool)GetValue(IsFixedPercentScaleProperty);
        set => SetValue(IsFixedPercentScaleProperty, value);
    }

    public double MinAutoPeak
    {
        get => (double)GetValue(MinAutoPeakProperty);
        set => SetValue(MinAutoPeakProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 6.0 || h < 6.0) return;

        // Subtle mid-level reference guide line
        double midY = Math.Round(h * 0.5) + 0.5;
        dc.DrawLine(MidGridPen, new Point(0, midY), new Point(w, midY));

        double[]? primary = PrimaryValues;
        double[]? secondary = SecondaryValues;

        double maxScale = 100.0;
        if (!IsFixedPercentScale)
        {
            double peak = Math.Max(1.0, MinAutoPeak);
            if (primary != null)
            {
                for (int i = 0; i < primary.Length; i++)
                    if (primary[i] > peak) peak = primary[i];
            }
            if (secondary != null)
            {
                for (int i = 0; i < secondary.Length; i++)
                    if (secondary[i] > peak) peak = secondary[i];
            }
            maxScale = peak * 1.12;
        }

        if (secondary is { Length: >= 2 })
        {
            DrawSeries(dc, secondary, w, h, maxScale, SecondaryColor, fillTopAlpha: 44, strokeWidth: 1.25, drawLiveDot: false);
        }

        if (primary is { Length: >= 2 })
        {
            DrawSeries(dc, primary, w, h, maxScale, PrimaryColor, fillTopAlpha: 68, strokeWidth: 1.5, drawLiveDot: true);
        }
    }

    private static void DrawSeries(
        DrawingContext dc,
        double[] values,
        double width,
        double height,
        double maxScale,
        Color color,
        byte fillTopAlpha,
        double strokeWidth,
        bool drawLiveDot)
    {
        int n = values.Length;
        double topPad = 2.5;
        double botPad = 1.5;
        double usableH = Math.Max(2.0, height - topPad - botPad);
        double rightEdge = Math.Max(1.0, width - (drawLiveDot ? 2.5 : 0.5));

        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / (n - 1) * rightEdge;
            double norm = Math.Clamp(values[i] / maxScale, 0.0, 1.0);
            double y = topPad + ((1.0 - norm) * usableH);
            pts[i] = new Point(x, y);
        }

        // Fritsch-Carlson monotone cubic tangents (guarantees C1 smoothness with zero overshoot below baseline)
        var tangents = ComputeMonotoneTangents(pts);

        var strokeGeom = new StreamGeometry();
        using (StreamGeometryContext sCtx = strokeGeom.Open())
        {
            sCtx.BeginFigure(pts[0], isFilled: false, isClosed: false);
            WriteSplineSegments(sCtx, pts, tangents);
        }
        strokeGeom.Freeze();

        var areaGeom = new StreamGeometry();
        using (StreamGeometryContext aCtx = areaGeom.Open())
        {
            aCtx.BeginFigure(new Point(pts[0].X, height), isFilled: true, isClosed: true);
            aCtx.LineTo(pts[0], isStroked: false, isSmoothJoin: false);
            WriteSplineSegments(aCtx, pts, tangents);
            aCtx.LineTo(new Point(pts[n - 1].X, height), isStroked: false, isSmoothJoin: false);
        }
        areaGeom.Freeze();

        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(fillTopAlpha, color.R, color.G, color.B),
            Color.FromArgb(2, color.R, color.G, color.B),
            new Point(0, 0),
            new Point(0, 1));
        fillBrush.Freeze();

        var strokeBrush = new SolidColorBrush(color);
        strokeBrush.Freeze();

        var pen = new Pen(strokeBrush, strokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        dc.DrawGeometry(fillBrush, null, areaGeom);
        dc.DrawGeometry(null, pen, strokeGeom);

        if (drawLiveDot)
        {
            Point tip = pts[n - 1];
            var haloBrush = new SolidColorBrush(Color.FromArgb(72, color.R, color.G, color.B));
            haloBrush.Freeze();
            dc.DrawEllipse(haloBrush, null, tip, 3.6, 3.6);
            dc.DrawEllipse(strokeBrush, null, tip, 1.85, 1.85);
        }
    }

    private static double[] ComputeMonotoneTangents(Point[] pts)
    {
        int n = pts.Length;
        var d = new double[n - 1];
        var m = new double[n];

        for (int i = 0; i < n - 1; i++)
        {
            double dx = Math.Max(1e-5, pts[i + 1].X - pts[i].X);
            d[i] = (pts[i + 1].Y - pts[i].Y) / dx;
        }

        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            m[i] = (d[i - 1] * d[i] <= 0.0) ? 0.0 : (d[i - 1] + d[i]) * 0.5;
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(d[i]) < 1e-6)
            {
                m[i] = 0.0;
                m[i + 1] = 0.0;
            }
            else
            {
                double a = m[i] / d[i];
                double b = m[i + 1] / d[i];
                double hypot = Math.Sqrt((a * a) + (b * b));
                if (hypot > 3.0)
                {
                    double t = 3.0 / hypot;
                    m[i] = t * a * d[i];
                    m[i + 1] = t * b * d[i];
                }
            }
        }

        return m;
    }

    private static void WriteSplineSegments(StreamGeometryContext ctx, Point[] pts, double[] m)
    {
        for (int i = 0; i < pts.Length - 1; i++)
        {
            double dx = (pts[i + 1].X - pts[i].X) / 3.0;
            var cp1 = new Point(pts[i].X + dx, pts[i].Y + (m[i] * dx));
            var cp2 = new Point(pts[i + 1].X - dx, pts[i + 1].Y - (m[i + 1] * dx));
            ctx.BezierTo(cp1, cp2, pts[i + 1], isStroked: true, isSmoothJoin: true);
        }
    }

    private static Pen CreateFrozenGridPen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        brush.Freeze();
        var pen = new Pen(brush, 1.0)
        {
            DashStyle = new DashStyle([3.0, 3.0], 0.0)
        };
        pen.DashStyle.Freeze();
        pen.Freeze();
        return pen;
    }
}
