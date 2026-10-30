using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace WinNotch.Views.Controls;

/// <summary>
/// 60-240Hz True-3D Vector Replica of iOS Dynamic Island Face ID (LAUIPearlGlyphView).
/// Zero text, pure graphical 4-stage continuous topology:
///   1. Iconic Face ID Smiling Face + 4 Rounded Corner Brackets (#32D74B)
///   2. Brackets continuously close into a sphere & peel into 3 continuous 3D gyroscopic Pearl rings
///   3. 3D orbital rings physically converge & snap into a single crisp circle
///   4. Checkmark progressively draws left-to-right inside the circle
/// Runs CompositionTarget.Rendering ONLY during the ~2.2s sequence (0.0% idle CPU).
/// </summary>
public sealed class FaceIdUnlockGlyph : FrameworkElement
{
    // Exact iOS Dark-Mode System Face ID Green (#32D74B)
    private const byte GreenR = 50;
    private const byte GreenG = 215;
    private const byte GreenB = 75;

    private const double CanvasSize = 72.0;
    private const double Cx = 36.0;
    private const double Cy = 36.0;
    private const double SphereRadius = 23.5;

    public static readonly DependencyProperty PulseTokenProperty = DependencyProperty.Register(
        nameof(PulseToken),
        typeof(int),
        typeof(FaceIdUnlockGlyph),
        new FrameworkPropertyMetadata(0, OnPulseTokenChanged));

    public int PulseToken
    {
        get => (int)GetValue(PulseTokenProperty);
        set => SetValue(PulseTokenProperty, value);
    }

    private double _elapsed;
    private long _startTimestamp;
    private bool _isAnimating;

    public FaceIdUnlockGlyph()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            if (PulseToken > 0 && !_isAnimating && _elapsed < 1.9)
            {
                RestartSequence();
            }
        };
        Unloaded += (_, _) => StopAnimation();
    }

    private static void OnPulseTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FaceIdUnlockGlyph glyph)
        {
            glyph.RestartSequence();
        }
    }

    private void RestartSequence()
    {
        _elapsed = 0.0;
        _startTimestamp = Stopwatch.GetTimestamp();
        if (!_isAnimating)
        {
            _isAnimating = true;
            CompositionTarget.Rendering += OnCompositionRendering;
        }
        InvalidateVisual();
    }

    private void StopAnimation()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        _elapsed = (double)(now - _startTimestamp) / Stopwatch.Frequency;
        InvalidateVisual();

        if (_elapsed >= 2.20)
        {
            StopAnimation();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(CanvasSize, CanvasSize);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : CanvasSize;
        double h = ActualHeight > 0 ? ActualHeight : CanvasSize;
        double scale = Math.Min(w, h) / CanvasSize;

        dc.PushTransform(new ScaleTransform(scale, scale));
        try
        {
            DrawPearlSequence(dc, _elapsed);
        }
        finally
        {
            dc.Pop();
        }
    }

    private static void DrawPearlSequence(DrawingContext dc, double t)
    {
        // Entry bloom as Dynamic Island expands downward (0.00s .. 0.18s)
        double entryAlpha = SmoothStep(0.0, 0.13, t);
        double entryScale = 0.84 + (0.16 * EaseOutBack(Math.Clamp(t / 0.22, 0.0, 1.0)));

        // Subtle Taptic-style lock-in pulse when 3D rings snap into the final circle at t ~ 1.16s
        double lockPulse = 1.0;
        if (t is >= 1.12 and <= 1.44)
        {
            double u = (t - 1.12) / 0.32;
            lockPulse = 1.0 + (0.048 * Math.Sin(u * Math.PI) * Math.Exp(-2.0 * u));
        }

        double totalScale = entryScale * lockPulse;
        dc.PushTransform(new ScaleTransform(totalScale, totalScale, Cx, Cy));
        try
        {
            // =====================================================================
            // PHASE 1 (0.00s .. 0.46s): Face ID Smiling Face + Morphing Brackets
            // =====================================================================
            double faceFade = 1.0 - SmoothStep(0.30, 0.46, t);
            if (faceFade > 0.005)
            {
                double faceAlpha = entryAlpha * faceFade;
                double faceShrink = 1.0 - (0.28 * SmoothStep(0.30, 0.46, t));
                double parallaxX = Math.Sin(t * 11.0) * 1.4 * faceFade;

                var facePen = CreateSolidGreenPen(faceAlpha, 3.0);
                dc.PushTransform(new ScaleTransform(faceShrink, faceShrink, Cx, Cy));
                try
                {
                    DrawFaceFeatures(dc, facePen, parallaxX);
                }
                finally
                {
                    dc.Pop();
                }
            }

            // Brackets continuously close their 4 gaps & round their corners into R=23.5 circle (0.30s .. 0.48s)
            if (t < 0.48)
            {
                double morphToCircle = SmoothStep(0.30, 0.48, t);
                var bracketPen = CreateSolidGreenPen(entryAlpha, 3.05);
                DrawMorphingBracketsToCircle(dc, bracketPen, morphToCircle);
            }

            // =====================================================================
            // PHASE 2 & 3 (0.48s .. 1.18s): Continuous 3D Gyroscopic Pearl Rings
            // =====================================================================
            if (t is >= 0.48 and < 1.18)
            {
                double peelIn = SmoothStep(0.48, 0.65, t);
                double foldOut = 1.0 - SmoothStep(0.96, 1.18, t);
                double tiltEnv = peelIn * foldOut;

                Draw3DPearlRings(dc, t, tiltEnv);
            }

            // =====================================================================
            // PHASE 4 (1.18s .. 2.20s): Solid Crisp Circle + Progressive Checkmark
            // =====================================================================
            if (t >= 1.18)
            {
                var circlePen = CreateSolidGreenPen(1.0, 3.05);
                dc.DrawEllipse(null, circlePen, new Point(Cx, Cy), SphereRadius, SphereRadius);
            }

            if (t >= 1.15)
            {
                double checkProgress = EaseOutCubic(Math.Clamp((t - 1.15) / 0.23, 0.0, 1.0));
                var checkPen = CreateSolidGreenPen(1.0, 3.35);
                DrawProgressiveCheckmark(dc, checkPen, checkProgress);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private static void DrawFaceFeatures(DrawingContext dc, Pen pen, double panX)
    {
        // Left & Right Vertical Capsule Eyes
        dc.DrawLine(pen, new Point(26.2 + panX, 25.4), new Point(26.2 + panX, 30.6));
        dc.DrawLine(pen, new Point(45.8 + panX, 25.4), new Point(45.8 + panX, 30.6));

        // Iconic Apple Face ID J-Nose + Smile Arc
        double noseX = 36.6 + (panX * 1.2);
        double smilePan = panX * 0.85;

        var geom = new StreamGeometry();
        using (StreamGeometryContext ctx = geom.Open())
        {
            // J-Nose
            ctx.BeginFigure(new Point(noseX, 25.8), false, false);
            ctx.LineTo(new Point(noseX, 35.2), true, true);
            ctx.QuadraticBezierTo(new Point(noseX, 38.2), new Point(noseX - 3.4, 38.2), true, true);

            // Smile Arc
            ctx.BeginFigure(new Point(27.2 + smilePan, 43.8), false, false);
            ctx.QuadraticBezierTo(new Point(36.0 + smilePan, 50.2), new Point(44.8 + smilePan, 43.8), true, true);
        }

        dc.DrawGeometry(null, pen, geom);
    }

    private static void DrawMorphingBracketsToCircle(DrawingContext dc, Pen pen, double morph)
    {
        double half = Lerp(24.0, SphereRadius, morph);
        double cr = Lerp(11.0, SphereRadius, morph);
        double straightHalf = Math.Max(0.0, half - cr);
        double arm = Lerp(Math.Min(5.2, straightHalf), straightHalf, morph);

        double l = Cx - half;
        double t = Cy - half;
        double r = Cx + half;
        double b = Cy + half;

        var geom = new StreamGeometry();
        using (StreamGeometryContext ctx = geom.Open())
        {
            // Top-Left Bracket
            ctx.BeginFigure(new Point(l, t + cr + arm), false, false);
            if (arm > 0.01) ctx.LineTo(new Point(l, t + cr), true, true);
            ctx.ArcTo(new Point(l + cr, t), new Size(cr, cr), 0, false, SweepDirection.Clockwise, true, true);
            if (arm > 0.01) ctx.LineTo(new Point(l + cr + arm, t), true, true);

            // Top-Right Bracket
            ctx.BeginFigure(new Point(r - cr - arm, t), false, false);
            if (arm > 0.01) ctx.LineTo(new Point(r - cr, t), true, true);
            ctx.ArcTo(new Point(r, t + cr), new Size(cr, cr), 0, false, SweepDirection.Clockwise, true, true);
            if (arm > 0.01) ctx.LineTo(new Point(r, t + cr + arm), true, true);

            // Bottom-Right Bracket
            ctx.BeginFigure(new Point(r, b - cr - arm), false, false);
            if (arm > 0.01) ctx.LineTo(new Point(r, b - cr), true, true);
            ctx.ArcTo(new Point(r - cr, b), new Size(cr, cr), 0, false, SweepDirection.Clockwise, true, true);
            if (arm > 0.01) ctx.LineTo(new Point(r - cr - arm, b), true, true);

            // Bottom-Left Bracket
            ctx.BeginFigure(new Point(l + cr + arm, b), false, false);
            if (arm > 0.01) ctx.LineTo(new Point(l + cr, b), true, true);
            ctx.ArcTo(new Point(l, b - cr), new Size(cr, cr), 0, false, SweepDirection.Clockwise, true, true);
            if (arm > 0.01) ctx.LineTo(new Point(l, b - cr - arm), true, true);
        }

        dc.DrawGeometry(null, pen, geom);
    }

    /// <summary>
    /// Renders 3 continuous closed 3D gyroscopic Pearl rings.
    /// Because Z is linear across any rotated planar circle in 3D space, an Absolute LinearGradientBrush
    /// from the back-most projected pole (-Z) to the front-most projected pole (+Z) delivers exact
    /// per-pixel 3D depth shading with zero seam or vertex-cap overlap artifacts.
    /// </summary>
    private static void Draw3DPearlRings(DrawingContext dc, double t, double tiltEnv)
    {
        double phase = t - 0.48;
        double companionVis = SmoothStep(0.0, 0.28, tiltEnv);

        // Draw companion rings (2, 1) first, then primary ring (0) cleanly on top
        Span<(int id, double rx, double ry, double rz)> rings = stackalloc (int, double, double, double)[3]
        {
            (2,
             tiltEnv * (0.98 * Math.Sin((phase * 6.8) + 2.40)),
             tiltEnv * (1.34 * Math.Sin((phase * 5.3) + 0.85)),
             (phase * 2.6) + 2.10),

            (1,
             tiltEnv * (1.26 * Math.Cos((phase * 5.6) + 1.15)),
             tiltEnv * (1.10 * Math.Sin((phase * 6.4) + 2.05)),
             (-phase * 2.9) + 1.05),

            (0,
             tiltEnv * (1.15 * Math.Sin((phase * 6.0) + 0.25)),
             tiltEnv * (1.30 * Math.Cos((phase * 5.1) + 0.10)),
             phase * 3.4)
        };

        for (int idx = 0; idx < rings.Length; idx++)
        {
            int id = rings[idx].id;
            if (id > 0 && companionVis <= 0.005)
                continue;

            DrawSingleContinuous3DRing(
                dc,
                rings[idx].rx,
                rings[idx].ry,
                rings[idx].rz,
                isPrimary: id == 0,
                tiltEnv,
                companionVis);
        }
    }

    private static void DrawSingleContinuous3DRing(
        DrawingContext dc,
        double rotX,
        double rotY,
        double rotZ,
        bool isPrimary,
        double tiltEnv,
        double companionVis)
    {
        const int segments = 64;
        const double focal = 150.0;

        double cosX = Math.Cos(rotX), sinX = Math.Sin(rotX);
        double cosY = Math.Cos(rotY), sinY = Math.Sin(rotY);
        double cosZ = Math.Cos(rotZ), sinZ = Math.Sin(rotZ);

        static (Point Pt, double Z) ProjectPoint(
            double theta,
            double cosX, double sinX,
            double cosY, double sinY,
            double cosZ, double sinZ)
        {
            double lx = SphereRadius * Math.Cos(theta);
            double ly = SphereRadius * Math.Sin(theta);

            double x1 = (lx * cosZ) - (ly * sinZ);
            double y1 = (lx * sinZ) + (ly * cosZ);

            double y2 = y1 * cosX;
            double z2 = y1 * sinX;

            double x3 = (x1 * cosY) + (z2 * sinY);
            double z3 = (-x1 * sinY) + (z2 * cosY);

            double persp = focal / (focal - z3);
            return (new Point(Cx + (x3 * persp), Cy + (y2 * persp)), z3);
        }

        // Build single closed seamless ring geometry
        var geom = new StreamGeometry();
        using (StreamGeometryContext ctx = geom.Open())
        {
            var first = ProjectPoint(0.0, cosX, sinX, cosY, sinY, cosZ, sinZ);
            ctx.BeginFigure(first.Pt, isFilled: false, isClosed: true);

            for (int i = 1; i < segments; i++)
            {
                double theta = (i * 2.0 * Math.PI) / segments;
                var p = ProjectPoint(theta, cosX, sinX, cosY, sinY, cosZ, sinZ);
                ctx.LineTo(p.Pt, isStroked: true, isSmoothJoin: true);
            }
        }

        double frontAlpha = isPrimary ? 1.0 : (0.94 * companionVis);
        double backAlpha = isPrimary ? Lerp(1.0, 0.26, tiltEnv) : (0.22 * companionVis);
        double strokeWidth = isPrimary ? Lerp(3.05, 2.35, tiltEnv) : 2.15;

        // Exact analytical front (+Z) and back (-Z) poles on the rotated circle
        double zAt0 = ProjectPoint(0.0, cosX, sinX, cosY, sinY, cosZ, sinZ).Z;
        double zAtHalfPi = ProjectPoint(Math.PI * 0.5, cosX, sinX, cosY, sinY, cosZ, sinZ).Z;
        double zPeak = Math.Sqrt((zAt0 * zAt0) + (zAtHalfPi * zAtHalfPi));

        Brush strokeBrush;
        if (zPeak > 0.25)
        {
            double thetaFront = Math.Atan2(zAtHalfPi, zAt0);
            Point ptFront = ProjectPoint(thetaFront, cosX, sinX, cosY, sinY, cosZ, sinZ).Pt;
            Point ptBack = ProjectPoint(thetaFront + Math.PI, cosX, sinX, cosY, sinY, cosZ, sinZ).Pt;

            if ((ptFront - ptBack).Length >= 1.5)
            {
                strokeBrush = new LinearGradientBrush(
                    GreenWithAlpha(backAlpha),
                    GreenWithAlpha(frontAlpha),
                    ptBack,
                    ptFront)
                {
                    MappingMode = BrushMappingMode.Absolute
                };
            }
            else
            {
                strokeBrush = new SolidColorBrush(GreenWithAlpha((frontAlpha + backAlpha) * 0.5));
            }
        }
        else
        {
            strokeBrush = new SolidColorBrush(GreenWithAlpha(frontAlpha));
        }

        var pen = new Pen(strokeBrush, strokeWidth)
        {
            LineJoin = PenLineJoin.Round
        };
        dc.DrawGeometry(null, pen, geom);
    }

    private static void DrawProgressiveCheckmark(DrawingContext dc, Pen pen, double progress)
    {
        if (progress <= 0.005) return;

        var p0 = new Point(25.8, 36.6);
        var p1 = new Point(33.1, 43.9);
        var p2 = new Point(47.0, 29.3);

        double len1 = (p1 - p0).Length;
        double len2 = (p2 - p1).Length;
        double total = len1 + len2;
        double targetDist = total * Math.Clamp(progress, 0.0, 1.0);

        var geom = new StreamGeometry();
        using (StreamGeometryContext ctx = geom.Open())
        {
            ctx.BeginFigure(p0, false, false);
            if (targetDist <= len1)
            {
                double f = targetDist / len1;
                ctx.LineTo(new Point(Lerp(p0.X, p1.X, f), Lerp(p0.Y, p1.Y, f)), true, true);
            }
            else
            {
                ctx.LineTo(p1, true, true);
                double f = (targetDist - len1) / len2;
                ctx.LineTo(new Point(Lerp(p1.X, p2.X, f), Lerp(p1.Y, p2.Y, f)), true, true);
            }
        }

        dc.DrawGeometry(null, pen, geom);
    }

    private static Color GreenWithAlpha(double alpha)
    {
        byte a = (byte)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 255.0);
        return Color.FromArgb(a, GreenR, GreenG, GreenB);
    }

    private static Pen CreateSolidGreenPen(double alpha, double thickness) =>
        new(new SolidColorBrush(GreenWithAlpha(alpha)), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / Math.Max(0.0001, edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private static double EaseOutCubic(double x)
    {
        double inv = 1.0 - Math.Clamp(x, 0.0, 1.0);
        return 1.0 - (inv * inv * inv);
    }

    private static double EaseOutBack(double x)
    {
        const double c1 = 1.45;
        const double c3 = c1 + 1.0;
        double p = Math.Clamp(x, 0.0, 1.0) - 1.0;
        return 1.0 + (c3 * p * p * p) + (c1 * p * p);
    }
}
