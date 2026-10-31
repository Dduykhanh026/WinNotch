using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WinNotch.Models;

namespace WinNotch.Views.Controls;

public sealed class IconsaxIcon : FrameworkElement
{
    private sealed record IconSpec(bool IsFilled, Geometry[] Geometries);
    private static readonly Dictionary<IconsaxKind, IconSpec> Cache = BuildCache();

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconsaxKind), typeof(IconsaxIcon),
        new FrameworkPropertyMetadata(IconsaxKind.Setting2, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconsaxIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(IconsaxIcon),
        new FrameworkPropertyMetadata(1.85, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconsaxIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public IconsaxKind Kind { get => (IconsaxKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double s = Math.Max(4.0, Size);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Max(4.0, Math.Min(ActualWidth > 0 ? ActualWidth : Size, ActualHeight > 0 ? ActualHeight : Size));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, s, s));
        if (!Cache.TryGetValue(Kind, out IconSpec? spec) || Foreground == null) return;

        double scale = s / 24.0;
        dc.PushTransform(new ScaleTransform(scale, scale));
        try
        {
            if (spec.IsFilled)
            {
                foreach (Geometry g in spec.Geometries)
                    dc.DrawGeometry(Foreground, null, g);
            }
            else
            {
                var pen = new Pen(Foreground, StrokeWidth)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                foreach (Geometry g in spec.Geometries)
                    dc.DrawGeometry(null, pen, g);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private static IconSpec Make(bool filled, params string[] paths)
    {
        var geoms = new Geometry[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            Geometry g = Geometry.Parse(paths[i]);
            g.Freeze();
            geoms[i] = g;
        }
        return new IconSpec(filled, geoms);
    }

    private static Dictionary<IconsaxKind, IconSpec> BuildCache() => new()
    {
        [IconsaxKind.Category] = Make(false, "M 5 10 h 2 c 2 0 3 -1 3 -3 V 5 c 0 -2 -1 -3 -3 -3 H 5 C 3 2 2 3 2 5 v 2 c 0 2 1 3 3 3 Z M 17 10 h 2 c 2 0 3 -1 3 -3 V 5 c 0 -2 -1 -3 -3 -3 h -2 c -2 0 -3 1 -3 3 v 2 c 0 2 1 3 3 3 Z M 17 22 h 2 c 2 0 3 -1 3 -3 v -2 c 0 -2 -1 -3 -3 -3 h -2 c -2 0 -3 1 -3 3 v 2 c 0 2 1 3 3 3 Z M 5 22 h 2 c 2 0 3 -1 3 -3 v -2 c 0 -2 -1 -3 -3 -3 H 5 c -2 0 -3 1 -3 3 v 2 c 0 2 1 3 3 3 Z"),
        [IconsaxKind.Folder2] = Make(false, "M 22 11 v 6 c 0 4 -1 5 -5 5 H 7 c -4 0 -5 -1 -5 -5 V 7 c 0 -4 1 -5 5 -5 h 1.5 c 1.5 0 1.83 0.44 2.4 1.2 l 1.5 2 c 0.38 0.5 0.6 0.8 1.6 0.8 h 3 c 4 0 5 1 5 5 Z", "M 8 2 h 9 c 2 0 3 1 3 3 v 1.38"),
        [IconsaxKind.Pin] = Make(false, "M 15.1 3.4 l 5.5 5.5 c 0.6 0.6 0.6 1.6 0 2.2 l -1.9 1.9 c -0.5 0.5 -1.2 0.6 -1.8 0.4 l -2.3 -0.8 -3.4 3.4 c 0.5 1.3 0.2 2.8 -0.8 3.8 l -0.6 0.6 -7.4 -7.4 0.6 -0.6 c 1 -1 2.5 -1.3 3.8 -0.8 l 3.4 -3.4 -0.8 -2.3 c -0.2 -0.6 -0.1 -1.3 0.4 -1.8 l 1.9 -1.9 c 0.6 -0.6 1.6 -0.6 2.2 0 Z", "M 3 21 l 4.8 -4.8"),
        [IconsaxKind.Setting2] = Make(false, "M 12 15 a 3 3 0 1 0 0 -6 3 3 0 0 0 0 6 Z", "M 2 12.88 v -1.76 c 0 -1.04 0.85 -1.9 1.9 -1.9 1.81 0 2.55 -1.28 1.64 -2.85 -0.52 -0.9 -0.21 -2.07 0.7 -2.59 l 1.73 -0.99 c 0.79 -0.47 1.81 -0.19 2.28 0.6 l 0.11 0.19 c 0.9 1.57 2.38 1.57 3.29 0 l 0.11 -0.19 c 0.47 -0.79 1.49 -1.07 2.28 -0.6 l 1.73 0.99 c 0.91 0.52 1.22 1.69 0.7 2.59 -0.91 1.57 -0.17 2.85 1.64 2.85 1.04 0 1.9 0.85 1.9 1.9 v 1.76 c 0 1.04 -0.85 1.9 -1.9 1.9 -1.81 0 -2.55 1.28 -1.64 2.85 0.52 0.91 0.21 2.07 -0.7 2.59 l -1.73 0.99 c -0.79 0.47 -1.81 0.19 -2.28 -0.6 l -0.11 -0.19 c -0.9 -1.57 -2.38 -1.57 -3.29 0 l -0.11 0.19 c -0.47 0.79 -1.49 1.07 -2.28 0.6 l -1.73 -0.99 a 1.899 1.899 0 0 1 -0.7 -2.59 c 0.91 -1.57 0.17 -2.85 -1.64 -2.85 -1.05 0 -1.9 -0.86 -1.9 -1.9 Z"),
        [IconsaxKind.VolumeHigh] = Make(false, "M 2 10 v 4 c 0 2 1 3 3 3 h 1.43 c 0.37 0 0.74 0.11 1.06 0.3 l 2.92 1.83 c 2.52 1.58 4.59 0.43 4.59 -2.54 V 7.41 c 0 -2.98 -2.07 -4.12 -4.59 -2.54 L 7.49 6.7 c -0.32 0.19 -0.69 0.3 -1.06 0.3 H 5 c -2 0 -3 1 -3 3 Z", "M 18 8 a 6.66 6.66 0 0 1 0 8", "M 19.83 5.5 a 10.83 10.83 0 0 1 0 13"),
        [IconsaxKind.VolumeLow] = Make(false, "M 3.33 10 v 4 c 0 2 1 3 3 3 h 1.43 c 0.37 0 0.74 0.11 1.06 0.3 l 2.92 1.83 c 2.52 1.58 4.59 0.43 4.59 -2.54 V 7.41 c 0 -2.98 -2.07 -4.12 -4.59 -2.54 L 8.82 6.7 c -0.32 0.19 -0.69 0.3 -1.06 0.3 H 6.33 c -2 0 -3 1 -3 3 Z", "M 19.33 8 a 6.66 6.66 0 0 1 0 8"),
        [IconsaxKind.VolumeMute] = Make(false, "M 5.5 10 v 4 c 0 2 1 3 3 3 h 1.43 c 0.37 0 0.74 0.11 1.06 0.3 l 2.92 1.83 c 2.52 1.58 4.59 0.43 4.59 -2.54 V 7.41 c 0 -2.98 -2.07 -4.12 -4.59 -2.54 L 10.99 6.7 c -0.32 0.19 -0.69 0.3 -1.06 0.3 H 8.5 c -2 0 -3 1 -3 3 Z"),
        [IconsaxKind.VolumeSlash] = Make(false, "M 15 8.37 v -0.96 c 0 -2.98 -2.07 -4.12 -4.59 -2.54 L 7.49 6.7 c -0.32 0.19 -0.69 0.3 -1.06 0.3 H 5 c -2 0 -3 1 -3 3 v 4 c 0 2 1 3 3 3 h 2", "M 10.41 19.13 c 2.52 1.58 4.59 0.43 4.59 -2.54 v -3.64", "M 18.81 9.42 c 0.9 2.15 0.63 4.66 -0.81 6.58", "M 22 2 L 2 22"),
        [IconsaxKind.Headphone] = Make(false, "M 5.46 18.49 v -2.92 c 0 -0.97 0.76 -1.84 1.84 -1.84 0.97 0 1.84 0.76 1.84 1.84 v 2.81 c 0 1.95 -1.62 3.57 -3.57 3.57 -1.95 0 -3.57 -1.63 -3.57 -3.57 v -6.16 C 1.89 6.6 6.33 2.05 11.95 2.05 17.57 2.05 22 6.6 22 12.11 v 6.16 c 0 1.95 -1.62 3.57 -3.57 3.57 -1.95 0 -3.57 -1.62 -3.57 -3.57 v -2.81 c 0 -0.97 0.76 -1.84 1.84 -1.84 0.97 0 1.84 0.76 1.84 1.84 v 3.03"),
        [IconsaxKind.Sun1] = Make(true, "M 12 19 a 7 7 0 1 0 0 -14 7 7 0 0 0 0 14 Z M 12 22.96 c -0.55 0 -1 -0.41 -1 -0.96 v -0.08 c 0 -0.55 0.45 -1 1 -1 s 1 0.45 1 1 -0.45 1.04 -1 1.04 Z m 7.14 -2.82 c -0.26 0 -0.51 -0.1 -0.71 -0.29 l -0.13 -0.13 a 0.996 0.996 0 1 1 1.41 -1.41 l 0.13 0.13 a 0.996 0.996 0 0 1 -0.7 1.7 Z m -14.28 0 c -0.26 0 -0.51 -0.1 -0.71 -0.29 a 0.996 0.996 0 0 1 0 -1.41 l 0.13 -0.13 a 0.996 0.996 0 1 1 1.41 1.41 l -0.13 0.13 c -0.19 0.19 -0.45 0.29 -0.7 0.29 Z M 22 13 h -0.08 c -0.55 0 -1 -0.45 -1 -1 s 0.45 -1 1 -1 1.04 0.45 1.04 1 -0.41 1 -0.96 1 Z M 2.08 13 H 2 c -0.55 0 -1 -0.45 -1 -1 s 0.45 -1 1 -1 1.04 0.45 1.04 1 -0.41 1 -0.96 1 Z m 16.93 -7.01 c -0.26 0 -0.51 -0.1 -0.71 -0.29 a 0.996 0.996 0 0 1 0 -1.41 l 0.13 -0.13 a 0.996 0.996 0 1 1 1.41 1.41 l -0.13 0.13 c -0.19 0.19 -0.44 0.29 -0.7 0.29 Z m -14.02 0 c -0.26 0 -0.51 -0.1 -0.71 -0.29 l -0.13 -0.14 a 0.996 0.996 0 1 1 1.41 -1.41 l 0.13 0.13 c 0.39 0.39 0.39 1.02 0 1.41 -0.19 0.2 -0.45 0.3 -0.7 0.3 Z M 12 3.04 c -0.55 0 -1 -0.41 -1 -0.96 V 2 c 0 -0.55 0.45 -1 1 -1 s 1 0.45 1 1 -0.45 1.04 -1 1.04 Z"),
        [IconsaxKind.BatteryCharging] = Make(false, "M 20.5 9.5 C 22 9.5 22 10 22 11 v 2 c 0 1 0 1.5 -1.5 1.5", "M 10 8 l -1.89 2.5 c -0.39 0.67 0.09 1.5 0.86 1.5 h 2.3 c 0.77 0 1.25 0.83 0.87 1.5 L 10 16", "M 7 19 c -4 0 -5 -1 -5 -5 v -4 c 0 -4 1 -5 5 -5", "M 13 5 c 4 0 5 1 5 5 v 4 c 0 4 -1 5 -5 5"),
        [IconsaxKind.BatteryFull] = Make(false, "M 20.5 9.5 C 22 9.5 22 10 22 11 v 2 c 0 1 0 1.5 -1.5 1.5", "M 6.38 10 c 0.33 1.31 0.33 2.69 0 4", "M 9.88 10 c 0.33 1.31 0.33 2.69 0 4", "M 13.38 10 c 0.33 1.31 0.33 2.69 0 4", "M 13 19 H 7 c -4 0 -5 -1 -5 -5 v -4 c 0 -4 1 -5 5 -5 h 6 c 4 0 5 1 5 5 v 4 c 0 4 -1 5 -5 5 Z"),
        [IconsaxKind.BatteryLow] = Make(false, "M 13 19 H 7 c -4 0 -5 -1 -5 -5 v -4 c 0 -4 1 -5 5 -5 h 6 c 4 0 5 1 5 5 v 4 c 0 4 -1 5 -5 5 Z", "M 20.5 9.5 C 22 9.5 22 10 22 11 v 2 c 0 1 0 1.5 -1.5 1.5", "M 6.38 10 c 0.33 1.31 0.33 2.69 0 4"),
        [IconsaxKind.Flash] = Make(true, "M 17.91 10.72 h -3.09 v -7.2 c 0 -1.68 -0.91 -2.02 -2.02 -0.76 l -0.8 0.91 -6.77 7.7 c -0.93 1.05 -0.54 1.91 0.86 1.91 h 3.09 v 7.2 c 0 1.68 0.91 2.02 2.02 0.76 l 0.8 -0.91 6.77 -7.7 c 0.93 -1.05 0.54 -1.91 -0.86 -1.91 Z"),
        [IconsaxKind.Music] = Make(false, "M 6.28 22 a 3.12 3.12 0 1 0 0 -6.24 3.12 3.12 0 0 0 0 6.24 Z", "M 20.84 16.8 V 4.6 c 0 -2.6 -1.63 -2.96 -3.28 -2.51 l -6.24 1.7 C 10.18 4.1 9.4 5 9.4 6.3 v 12.57", "M 17.72 19.92 a 3.12 3.12 0 1 0 0 -6.24 3.12 3.12 0 0 0 0 6.24 Z", "M 9.4 9.52 L 20.84 6.4"),
        [IconsaxKind.Previous] = Make(true, "M 20.24 7.22 v 9.57 c 0 1.96 -2.13 3.19 -3.83 2.21 l -4.15 -2.39 -4.15 -2.4 c -1.7 -0.98 -1.7 -3.43 0 -4.41 l 4.15 -2.4 4.15 -2.39 c 1.7 -0.98 3.83 0.24 3.83 2.21 Z M 3.762 18.93 c -0.41 0 -0.75 -0.34 -0.75 -0.75 V 5.82 c 0 -0.41 0.34 -0.75 0.75 -0.75 s 0.75 0.34 0.75 0.75 v 12.36 c 0 0.41 -0.34 0.75 -0.75 0.75 Z"),
        [IconsaxKind.Next] = Make(true, "M 3.762 7.22 v 9.57 c 0 1.96 2.13 3.19 3.83 2.21 l 4.15 -2.39 4.15 -2.4 c 1.7 -0.98 1.7 -3.43 0 -4.41 l -4.15 -2.4 -4.15 -2.39 c -1.7 -0.98 -3.83 0.24 -3.83 2.21 Z M 20.238 18.93 c -0.41 0 -0.75 -0.34 -0.75 -0.75 V 5.82 c 0 -0.41 0.34 -0.75 0.75 -0.75 s 0.75 0.34 0.75 0.75 v 12.36 c 0 0.41 -0.33 0.75 -0.75 0.75 Z"),
        [IconsaxKind.Play] = Make(true, "M 4 12 V 8.44 c 0 -4.42 3.13 -6.23 6.96 -4.02 l 3.09 1.78 3.09 1.78 c 3.83 2.21 3.83 5.83 0 8.04 l -3.09 1.78 -3.09 1.78 C 7.13 21.79 4 19.98 4 15.56 V 12 Z"),
        [IconsaxKind.Pause] = Make(true, "M 10.65 19.11 V 4.89 c 0 -1.35 -0.57 -1.89 -2.01 -1.89 H 5.01 C 3.57 3 3 3.54 3 4.89 v 14.22 C 3 20.46 3.57 21 5.01 21 h 3.63 c 1.44 0 2.01 -0.54 2.01 -1.89 Z M 21.002 19.11 V 4.89 c 0 -1.35 -0.57 -1.89 -2.01 -1.89 h -3.63 c -1.43 0 -2.01 0.54 -2.01 1.89 v 14.22 c 0 1.35 0.57 1.89 2.01 1.89 h 3.63 c 1.44 0 2.01 -0.54 2.01 -1.89 Z"),
        [IconsaxKind.Calendar] = Make(false, "M 8 2 v 3", "M 16 2 v 3", "M 3.5 9.09 h 17", "M 21 8.5 V 17 c 0 3 -1.5 5 -5 5 H 8 c -3.5 0 -5 -2 -5 -5 V 8.5 c 0 -3 1.5 -5 5 -5 h 8 c 3.5 0 5 2 5 5 Z"),
        [IconsaxKind.ArrowLeft] = Make(false, "M 15 19.92 L 8.48 13.4 c -0.77 -0.77 -0.77 -2.03 0 -2.8 L 15 4.08"),
        [IconsaxKind.ArrowRight] = Make(false, "M 8.91 19.92 l 6.52 -6.52 c 0.77 -0.77 0.77 -2.03 0 -2.8 L 8.91 4.08"),
        [IconsaxKind.Timer] = Make(false, "M 20.75 13.25 c 0 4.83 -3.92 8.75 -8.75 8.75 s -8.75 -3.92 -8.75 -8.75 S 7.17 4.5 12 4.5 s 8.75 3.92 8.75 8.75 Z", "M 12 8 v 5", "M 9 2 h 6"),
        [IconsaxKind.Reset] = Make(false, "M 14.55 21.67 C 18.84 20.54 22 16.64 22 12 c 0 -5.52 -4.44 -10 -10 -10 C 5.33 2 2 7.56 2 7.56", "M 2 3 v 4.56 h 4.44", "M 2 12 c 0 5.52 4.48 10 10 10"),
        [IconsaxKind.Cpu] = Make(false, "M 9.6 20 h 4.8 c 4 0 5.6 -1.6 5.6 -5.6 V 9.6 c 0 -4 -1.6 -5.6 -5.6 -5.6 H 9.6 C 5.6 4 4 5.6 4 9.6 v 4.8 c 0 4 1.6 5.6 5.6 5.6 Z", "M 10.5 17 h 3 c 2.5 0 3.5 -1 3.5 -3.5 v -3 C 17 8 16 7 13.5 7 h -3 C 8 7 7 8 7 10.5 v 3 C 7 16 8 17 10.5 17 Z", "M 8.01 4 V 2 M 12 4 V 2 M 16 4 V 2 M 20 8 h 2 M 20 12 h 2 M 20 16 h 2 M 16 20 v 2 M 12.01 20 v 2 M 8.01 20 v 2 M 2 8 h 2 M 2 12 h 2 M 2 16 h 2"),
        [IconsaxKind.ImportTray] = Make(false, "M 16.44 8.9 c 3.6 0.31 5.07 2.16 5.07 6.21 v 0.13 c 0 4.47 -1.79 6.26 -6.26 6.26 H 8.74 c -4.47 0 -6.26 -1.79 -6.26 -6.26 v -0.13 c 0 -4.02 1.45 -5.87 4.99 -6.2", "M 12 2 v 12.88", "M 15.35 12.65 L 12 16 l -3.35 -3.35"),
        [IconsaxKind.ExportTray] = Make(false, "M 16.44 8.9 c 3.6 0.31 5.07 2.16 5.07 6.21 v 0.13 c 0 4.47 -1.79 6.26 -6.26 6.26 H 8.74 c -4.47 0 -6.26 -1.79 -6.26 -6.26 v -0.13 c 0 -4.02 1.45 -5.87 4.99 -6.2", "M 12 15 V 3.62", "M 15.35 5.85 L 12 2.5 8.65 5.85"),
        [IconsaxKind.DocumentText] = Make(false, "M 21 7 v 10 c 0 3 -1.5 5 -5 5 H 8 c -3.5 0 -5 -2 -5 -5 V 7 c 0 -3 1.5 -5 5 -5 h 8 c 3.5 0 5 2 5 5 Z", "M 14.5 4.5 v 2 c 0 1.1 0.9 2 2 2 h 2", "M 8 13 h 4", "M 8 17 h 8"),
        [IconsaxKind.Folder] = Make(false, "M 22 11 v 6 c 0 4 -1 5 -5 5 H 7 c -4 0 -5 -1 -5 -5 V 7 c 0 -4 1 -5 5 -5 h 1.5 c 1.5 0 1.83 0.44 2.4 1.2 l 1.5 2 c 0.38 0.5 0.6 0.8 1.6 0.8 h 3 c 4 0 5 1 5 5 Z"),
        [IconsaxKind.Gallery] = Make(false, "M 9 22 h 6 c 5 0 7 -2 7 -7 V 9 c 0 -5 -2 -7 -7 -7 H 9 C 4 2 2 4 2 9 v 6 c 0 5 2 7 7 7 Z", "M 9 10 a 2 2 0 1 0 0 -4 2 2 0 0 0 0 4 Z", "M 2.67 18.95 l 4.93 -3.31 c 0.79 -0.53 1.93 -0.47 2.64 0.14 l 0.33 0.29 c 0.78 0.67 2.04 0.67 2.82 0 l 4.16 -3.57 c 0.78 -0.67 2.04 -0.67 2.82 0 L 22 13.9"),
        [IconsaxKind.NoteText] = Make(false, "M 8 2 v 3 M 16 2 v 3 M 21 8.5 V 17 c 0 3 -1.5 5 -5 5 H 8 c -3.5 0 -5 -2 -5 -5 V 8.5 c 0 -3 1.5 -5 5 -5 h 8 c 3.5 0 5 2 5 5 Z M 8 11 h 8 M 8 16 h 4"),
        [IconsaxKind.Copy] = Make(false, "M 16 12.9 v 4.2 c 0 3.5 -1.4 4.9 -4.9 4.9 H 6.9 C 3.4 22 2 20.6 2 17.1 v -4.2 C 2 9.4 3.4 8 6.9 8 h 4.2 c 3.5 0 4.9 1.4 4.9 4.9 z", "M 22 6.9 v 4.2 c 0 3.5 -1.4 4.9 -4.9 4.9 H 16 v -3.1 C 16 9.4 14.6 8 11.1 8 H 8 V 6.9 C 8 3.4 9.4 2 12.9 2 h 4.2 C 20.6 2 22 3.4 22 6.9 z"),
        [IconsaxKind.Trash] = Make(false, "M 21 5.98 c -3.33 -0.33 -6.68 -0.5 -10.02 -0.5 -1.98 0 -3.96 0.1 -5.94 0.3 L 3 5.98", "M 8.5 4.97 l 0.22 -1.31 C 8.88 2.71 9 2 10.69 2 h 2.62 c 1.69 0 1.82 0.75 1.97 1.67 l 0.22 1.3", "M 18.85 9.14 l -0.65 10.07 C 18.09 20.78 18 22 15.21 22 H 8.79 C 6 22 5.91 20.78 5.8 19.21 L 5.15 9.14", "M 10.33 16.5 h 3.33", "M 9.5 12.5 h 5"),
        [IconsaxKind.Close] = Make(false, "M 6.5 6.5 l 11 11", "M 17.5 6.5 l -11 11"),
        [IconsaxKind.Activity] = Make(false, "M 9 22 h 6 c 5 0 7 -2 7 -7 V 9 c 0 -5 -2 -7 -7 -7 H 9 C 4 2 2 4 2 9 v 6 c 0 5 2 7 7 7 Z", "M 7.33 14.49 l 2.38 -3.09 c 0.34 -0.44 0.97 -0.52 1.41 -0.18 l 1.83 1.44 c 0.44 0.34 1.07 0.26 1.41 -0.17 l 2.31 -2.98"),
        [IconsaxKind.Wifi] = Make(false, "M 4.91 11.84 c 4.3 -3.32 9.89 -3.32 14.19 0", "M 2 8.36 c 6.06 -4.68 13.94 -4.68 20 0", "M 6.79 15.49 c 3.15 -2.44 7.26 -2.44 10.41 0", "M 12 19.5 a 0.9 0.9 0 1 0 0 -1.8 0.9 0.9 0 0 0 0 1.8 Z"),
        [IconsaxKind.Global] = Make(false, "M 12 22 a 10 10 0 1 0 0 -20 10 10 0 0 0 0 20 Z", "M 8 3 h 1 a 28.42 28.42 0 0 0 0 18 H 8", "M 15 3 a 28.42 28.42 0 0 1 0 18", "M 3 16 v -1 a 28.42 28.42 0 0 0 18 0 v 1", "M 3 9 a 28.42 28.42 0 0 1 18 0"),
        [IconsaxKind.Driver] = Make(false, "M 19.32 10 H 4.69 c -1.48 0 -2.68 -1.21 -2.68 -2.68 V 4.69 c 0 -1.48 1.21 -2.68 2.68 -2.68 h 14.63 C 20.8 2.01 22 3.22 22 4.69 v 2.63 C 22 8.79 20.79 10 19.32 10 Z", "M 19.32 22 H 4.69 c -1.48 0 -2.68 -1.21 -2.68 -2.68 v -2.63 c 0 -1.48 1.21 -2.68 2.68 -2.68 h 14.63 c 1.48 0 2.68 1.21 2.68 2.68 v 2.63 C 22 20.79 20.79 22 19.32 22 Z", "M 6 5 v 2", "M 10 5 v 2", "M 6 17 v 2", "M 10 17 v 2", "M 14 6 h 4", "M 14 18 h 4"),
        [IconsaxKind.Ram] = Make(false, "M 3.5 17 V 7 c 0 -3 1.47 -5 4.99 -5 h 7 c 3.47 0 5 2 5 5 v 8.83 c 0 0.53 -0.21 1.04 -0.59 1.41 l -1.17 1.17 c -0.38 0.38 -0.89 0.59 -1.42 0.59 H 8.49 C 4.97 19 3.5 17 3.5 17 Z", "M 7.99 21.97 l -0.01 -2.97", "M 12 21.97 l -0.01 -2.97", "M 16.01 21.97 L 16 19", "M 8.5 7 h 7", "M 8.5 11 h 7"),
        [IconsaxKind.ArrowDown] = Make(false, "M 18.07 14.43 L 12 20.5 l -6.07 -6.07", "M 12 3.5 v 16.83"),
        [IconsaxKind.ArrowUp] = Make(false, "M 18.07 9.57 L 12 3.5 5.93 9.57", "M 12 20.5 V 3.67"),
        [IconsaxKind.Setting4] = Make(false, "M 22 6.5 h -6", "M 6 6.5 H 2", "M 10 10 a 3.5 3.5 0 1 0 0 -7 3.5 3.5 0 0 0 0 7 Z", "M 22 17.5 h -4", "M 8 17.5 H 2", "M 14 21 a 3.5 3.5 0 1 0 0 -7 3.5 3.5 0 0 0 0 7 Z"),
        [IconsaxKind.Scan] = Make(false, "M 2 9 V 6.5 C 2 4.01 4.01 2 6.5 2 H 9", "M 15 2 h 2.5 C 19.99 2 22 4.01 22 6.5 V 9", "M 22 16 v 1.5 c 0 2.49 -2.01 4.5 -4.5 4.5 H 16", "M 9 22 H 6.5 C 4.01 22 2 19.99 2 17.5 V 15", "M 17 12 H 7"),
        [IconsaxKind.Lock] = Make(false, "M 6 10 V 8 c 0 -3.31 1 -6 6 -6 s 6 2.69 6 6 v 2", "M 12 18.5 a 2.5 2.5 0 1 0 0 -5 2.5 2.5 0 0 0 0 5 Z", "M 17 22 H 7 c -4 0 -5 -1 -5 -5 v -2 c 0 -4 1 -5 5 -5 h 10 c 4 0 5 1 5 5 v 2 c 0 4 -1 5 -5 5 Z"),
        [IconsaxKind.Notification] = Make(false, "M 12.02 2.91 c -3.31 0 -6 2.69 -6 6 v 2.89 c 0 0.61 -0.26 1.54 -0.57 2.06 L 4.3 15.77 c -0.71 1.18 -0.22 2.49 1.08 2.93 4.31 1.44 8.96 1.44 13.27 0 1.21 -0.4 1.74 -1.83 1.08 -2.93 l -1.15 -1.91 c -0.3 -0.52 -0.56 -1.45 -0.56 -2.06 V 8.91 c 0 -3.3 -2.7 -6 -6 -6 Z", "M 13.87 3.2 a 6.754 6.754 0 0 0 -3.7 0 c 0.29 -0.74 1.01 -1.26 1.85 -1.26 0.84 0 1.56 0.52 1.85 1.26 Z", "M 15.02 19.06 c 0 1.65 -1.35 3 -3 3 -0.82 0 -1.58 -0.34 -2.12 -0.88 a 3.01 3.01 0 0 1 -0.88 -2.12"),
        [IconsaxKind.Microphone] = Make(false, "M 12 15.5 c 2.21 0 4 -1.79 4 -4 V 6 c 0 -2.21 -1.79 -4 -4 -4 S 8 3.79 8 6 v 5.5 c 0 2.21 1.79 4 4 4 Z", "M 4.35 9.65 v 1.7 C 4.35 15.57 7.78 19 12 19 c 4.22 0 7.65 -3.43 7.65 -7.65 v -1.7", "M 12 19 v 3", "M 8.5 22 h 7"),
        [IconsaxKind.MicrophoneSlash] = Make(false, "M 16 10.8 V 6 c 0 -2.21 -1.79 -4 -4 -4 S 8 3.79 8 6 v 5", "M 9.2 14.3 A 3.98 3.98 0 0 0 12 15.5 c 1.54 0 2.88 -0.87 3.55 -2.15", "M 4.35 9.65 v 1.7 c 0 1.97 0.75 3.77 1.98 5.13", "M 8.85 18.33 A 7.58 7.58 0 0 0 12 19 c 4.22 0 7.65 -3.43 7.65 -7.65 v -1.7", "M 12 19 v 3", "M 8.5 22 h 7", "M 21.5 2.5 L 2.5 21.5"),
        [IconsaxKind.Coffee] = Make(false, "M 17.79 10.47 v 4.32 c 0 3.43 -2.78 6.21 -6.21 6.21 H 9.21 C 5.78 21 3 18.22 3 14.79 v -4.32 C 3 9.11 4.11 8 5.47 8 h 9.85 c 1.36 0 2.47 1.11 2.47 2.47 Z", "M 17.79 10.5 h 1.46 c 1.52 0 2.75 1.23 2.75 2.75 S 20.77 16 19.25 16 h -1.46", "M 7 3.2 v 2.3", "M 10.4 2.5 v 3", "M 13.8 3.2 v 2.3"),
        [IconsaxKind.Colorfilter] = Make(false, "M 14.2 6.3 l 3.5 3.5", "M 15.6 4.9 l 2.1 -2.1 a 2.12 2.12 0 0 1 3 3 l -2.1 2.1 -1.4 1.4 -7.8 7.8 c -0.45 0.45 -1.02 0.77 -1.65 0.92 L 4.5 18.8 c -0.78 0.19 -1.49 -0.52 -1.3 -1.3 l 0.78 -3.25 c 0.15 -0.63 0.47 -1.2 0.92 -1.65 l 7.8 -7.8 1.4 -1.4", "M 19 16.5 c 0 1.8 -1.8 3.8 -1.8 3.8 s -1.8 -2 -1.8 -3.8 a 1.8 1.8 0 1 1 3.6 0 Z"),
        [IconsaxKind.Broom] = Make(false, "M 10.5 3.5 L 12.1 8.4 L 17 10 L 12.1 11.6 L 10.5 16.5 L 8.9 11.6 L 4 10 L 8.9 8.4 Z", "M 18.5 15.5 L 19.3 17.7 L 21.5 18.5 L 19.3 19.3 L 18.5 21.5 L 17.7 19.3 L 15.5 18.5 L 17.7 17.7 Z"),
        [IconsaxKind.Add] = Make(false, "M 6 12 h 12", "M 12 18 V 6"),
        [IconsaxKind.Bot] = Make(false, "M 8 8 h 8 c 2.8 0 4 1.2 4 4 v 4 c 0 2.8 -1.2 4 -4 4 H 8 c -2.8 0 -4 -1.2 -4 -4 v -4 c 0 -2.8 1.2 -4 4 -4 Z", "M 12 4 v 4", "M 10 4 h 4", "M 9 13 v 1.5", "M 15 13 v 1.5", "M 9.5 17 h 5", "M 2 13 h 2", "M 20 13 h 2"),
        [IconsaxKind.Hierarchy] = Make(false, "M 5 8 a 3 3 0 1 0 0 -6 3 3 0 0 0 0 6 Z", "M 5 22 a 3 3 0 1 0 0 -6 3 3 0 0 0 0 6 Z", "M 19 22 a 3 3 0 1 0 0 -6 3 3 0 0 0 0 6 Z", "M 5 8 v 8", "M 5.13 9 c 0.45 2.5 2.4 4 5.87 4 h 3 c 2.5 0 4.5 1.2 4.88 3"),
    };
}
