using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinNotch.Services;

namespace WinNotch.Views.Controls;

/// <summary>
/// GPU-accelerated Monotone Cubic Hermite Sparkline Graph (iStat Menus / macOS Activity Monitor style).
/// Supports single-series (CPU %) and dual-series (Download / Upload) with zero-overshoot splines,
/// gradient fill, and interactive cursor hover scrubbing readout (0% background CPU when mouse leaves).
/// </summary>
public sealed class SparklineGraph : FrameworkElement
{
    private static readonly Pen MidGridPen = CreateFrozenGridPen();
    private static readonly Pen HoverHairlinePen = CreateFrozenSolidPen(Color.FromArgb(72, 255, 255, 255), 1.0);
    private static readonly Pen DotWhiteRingPen = CreateFrozenSolidPen(Color.FromArgb(230, 255, 255, 255), 1.15);
    private static readonly Brush HoverBadgeBgBrush = CreateFrozenSolidBrush(Color.FromArgb(234, 11, 14, 21));
    private static readonly Pen HoverBadgeBorderPen = CreateFrozenSolidPen(Color.FromArgb(62, 255, 255, 255), 1.0);
    private static readonly Brush HoverBadgeTextBrush = CreateFrozenSolidBrush(Color.FromRgb(244, 247, 254));
    private static readonly Typeface HoverTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

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

    public static readonly DependencyProperty IsHoverReadoutEnabledProperty = DependencyProperty.Register(
        nameof(IsHoverReadoutEnabled), typeof(bool), typeof(SparklineGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private double? _hoverX;

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

    public bool IsHoverReadoutEnabled
    {
        get => (bool)GetValue(IsHoverReadoutEnabledProperty);
        set => SetValue(IsHoverReadoutEnabledProperty, value);
    }

    public bool IsHovering => _hoverX.HasValue;

    public bool TryResolveHoverReadout(double x, double width, out int sampleIndex, out string readoutText)
    {
        sampleIndex = -1;
        readoutText = string.Empty;

        double[]? primary = PrimaryValues;
        if (primary is not { Length: >= 2 } || width < 6.0)
            return false;

        int n = primary.Length;
        double rightEdge = Math.Max(1.0, width - 2.5);
        double ratio = Math.Clamp(x / rightEdge, 0.0, 1.0);
        sampleIndex = Math.Clamp((int)Math.Round(ratio * (n - 1)), 0, n - 1);

        int secAgo = (n - 1) - sampleIndex;
        string ageLabel = secAgo <= 0 ? "Hiện tại" : $"{secAgo}s trước";

        if (IsFixedPercentScale)
        {
            readoutText = $"{Math.Round(primary[sampleIndex]):0}% • {ageLabel}";
        }
        else
        {
            string downStr = SystemMonitorService.FormatSpeed(primary[sampleIndex], compact: true);
            double[]? secondary = SecondaryValues;
            if (secondary != null && sampleIndex < secondary.Length)
            {
                string upStr = SystemMonitorService.FormatSpeed(secondary[sampleIndex], compact: true);
                readoutText = $"↓ {downStr}  ↑ {upStr} • {ageLabel}";
            }
            else
            {
                readoutText = $"↓ {downStr} • {ageLabel}";
            }
        }

        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsHoverReadoutEnabled)
            return;

        double x = Math.Clamp(e.GetPosition(this).X, 0.0, Math.Max(1.0, ActualWidth));
        if (!_hoverX.HasValue || Math.Abs(_hoverX.Value - x) >= 0.35)
        {
            _hoverX = x;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverX.HasValue)
        {
            _hoverX = null;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 6.0 || h < 6.0) return;

        if (IsHoverReadoutEnabled)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        }

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

        Point[]? secondaryPts = null;
        if (secondary is { Length: >= 2 })
        {
            secondaryPts = DrawSeries(dc, secondary, w, h, maxScale, SecondaryColor, fillTopAlpha: 44, strokeWidth: 1.25, drawLiveDot: false);
        }

        Point[]? primaryPts = null;
        if (primary is { Length: >= 2 })
        {
            primaryPts = DrawSeries(dc, primary, w, h, maxScale, PrimaryColor, fillTopAlpha: 68, strokeWidth: 1.5, drawLiveDot: !_hoverX.HasValue);
        }

        if (IsHoverReadoutEnabled &&
            _hoverX.HasValue &&
            primaryPts != null &&
            TryResolveHoverReadout(_hoverX.Value, w, out int idx, out string readoutText) &&
            idx >= 0 &&
            idx < primaryPts.Length)
        {
            DrawHoverScrubOverlay(dc, w, h, primaryPts[idx], secondaryPts != null && idx < secondaryPts.Length ? secondaryPts[idx] : null, readoutText);
        }
    }

    private void DrawHoverScrubOverlay(
        DrawingContext dc,
        double width,
        double height,
        Point primaryPt,
        Point? secondaryPt,
        string readoutText)
    {
        double guideX = Math.Round(primaryPt.X) + 0.5;
        dc.DrawLine(HoverHairlinePen, new Point(guideX, 1.0), new Point(guideX, Math.Max(1.5, height - 1.0)));

        if (secondaryPt.HasValue)
        {
            var secBrush = new SolidColorBrush(SecondaryColor);
            secBrush.Freeze();
            dc.DrawEllipse(secBrush, DotWhiteRingPen, secondaryPt.Value, 2.5, 2.5);
        }

        var priBrush = new SolidColorBrush(PrimaryColor);
        priBrush.Freeze();
        dc.DrawEllipse(priBrush, DotWhiteRingPen, primaryPt, 2.8, 2.8);

        double pixelsPerDip = 1.0;
        try { pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }

        var ft = new FormattedText(
            readoutText,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            HoverTypeface,
            9.2,
            HoverBadgeTextBrush,
            pixelsPerDip);

        double padX = 5.0;
        double padY = 2.0;
        double badgeW = Math.Min(width - 2.0, ft.Width + (padX * 2.0));
        double badgeH = ft.Height + (padY * 2.0);
        double badgeX = Math.Clamp(primaryPt.X - (badgeW * 0.5), 1.0, Math.Max(1.0, width - badgeW - 1.0));
        double badgeY = 1.0;

        dc.DrawRoundedRectangle(HoverBadgeBgBrush, HoverBadgeBorderPen, new Rect(badgeX, badgeY, badgeW, badgeH), 4.2, 4.2);
        dc.DrawText(ft, new Point(badgeX + padX, badgeY + padY));
    }

    private static Point[] DrawSeries(
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
        double rightEdge = Math.Max(1.0, width - 2.5);

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

        return pts;
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

    private static SolidColorBrush CreateFrozenSolidBrush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenSolidPen(Color c, double thickness)
    {
        var pen = new Pen(CreateFrozenSolidBrush(c), thickness);
        pen.Freeze();
        return pen;
    }
}
