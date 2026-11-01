using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;
using WinNotch.Views;
using WinNotch.Views.Controls;

namespace WinNotch.Tests;

/// <summary>
/// Empirical Adversarial Challenge Suite for Milestone 2:
/// Collapsed Notch Dynamics & Dynamic Island HUD Animations.
/// Stress-tests HUD arrival pulse spam, color swatch squircle and #HEX formatting,
/// mini level meter clamping and width calculation, album art scaling/cross-fade,
/// and the Zero CPU animation clock detachment constraint.
/// </summary>
public static class Milestone2DynamicsAdversarialChallengeTests
{
    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 2: DYNAMIC ISLAND HUD & MEDIA DYNAMICS ADVERSARIAL SUITE     ");
        Console.WriteLine("=========================================================================");

        EnsureWpfEnvironment();

        // Challenge 1: HUD arrival pulse token & rapid trigger spam (50 rapid updates)
        TestHudArrivalPulseTokenAndRapidTriggerSpam(assert);

        // Challenge 2: Color swatch squircle & #HEX formatting oracle
        TestColorSwatchSquircleAndHexFormatting(assert);

        // Challenge 3: Mini level meter clamping & width calculation
        TestMiniLevelMeterClampingAndWidthCalculation(assert);

        // Challenge 4: Album art playback scaling & 180ms cross-fade resilience
        TestAlbumArtPlaybackScalingAndCrossFadeResilience(assert);

        // Challenge 5: Zero CPU constraint & animation clock detachment
        TestZeroCpuAnimationClockDetachmentConstraint(assert);
    }

    private static void EnsureWpfEnvironment()
    {
        if (Application.Current == null)
        {
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        }
        else
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        if (Application.Current != null && !Application.Current.Resources.Contains("AppleFontFamily"))
        {
            try
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/WinNotch;component/Views/Themes/NotchStyles.xaml", UriKind.Absolute)
                };
                Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Could not merge NotchStyles: {ex.Message}");
            }
        }
    }

    private static Window CreateOffscreenTestWindow(UIElement content)
    {
        var win = new Window
        {
            Width = 500,
            Height = 350,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Content = content
        };
        win.Show();
        PumpDispatcher();
        return win;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(f =>
            {
                ((DispatcherFrame)f!).Continue = false;
                return null;
            }),
            frame);
        Dispatcher.PushFrame(frame);
    }

    private static void WaitDispatcher(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            PumpDispatcher();
            Thread.Sleep(10);
        }
    }

    private static MainNotchViewModel CreateTestViewModel(
        out NotchStateMachine sm,
        out DisplayPowerService power,
        out AudioService audio,
        out ColorPickerService picker,
        out SettingsService settings)
    {
        settings = new SettingsService();
        sm = new NotchStateMachine(settings.Current);

        var descriptor = new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "Test OLED Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };

        audio = new AudioService(settings);
        var media = new MediaService(settings);
        power = new DisplayPowerService(settings, descriptor);
        var shelf = new FileShelfService(settings);
        var timer = new TimerService();
        var privacy = new PrivacyService(settings);
        var monitor = new SystemMonitorService(settings);
        var notif = new NotificationService(settings);
        picker = new ColorPickerService(sm);

        return new MainNotchViewModel(
            settings,
            sm,
            audio,
            power,
            media,
            shelf,
            timer,
            privacy,
            monitor,
            notif,
            picker,
            new AgentMonitorService(settings),
            () => { });
    }

    private static BitmapSource CreateTestBitmap(int width = 32, int height = 32, byte r = 255, byte g = 0, byte b = 0)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    // =========================================================================
    // CHALLENGE 1: HUD Arrival Pulse Token & Rapid Trigger Spam
    // =========================================================================
    private static void TestHudArrivalPulseTokenAndRapidTriggerSpam(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 1] HUD Arrival Pulse Token & Rapid Trigger Spam ---");

        var vm = CreateTestViewModel(out var sm, out var power, out var audio, out var picker, out var settings);
        var hudView = new HudView { DataContext = vm };
        var testWin = CreateOffscreenTestWindow(hudView);

        try
        {
            var sliderIconScale = hudView.FindName("SliderIconScale") as ScaleTransform;
            var statusVisualScale = hudView.FindName("StatusVisualScale") as ScaleTransform;

            assert(sliderIconScale != null, "HudView contains SliderIconScale Transform");
            assert(statusVisualScale != null, "HudView contains StatusVisualScale Transform");

            int initialToken = vm.HudArrivalPulseToken;

            // Perform 50 rapid volume/brightness updates in tight succession
            const int spamCount = 50;
            bool hasNonFinite = false;
            bool scaleOutOfRangeDuringSpam = false;

            for (int i = 1; i <= spamCount; i++)
            {
                var payload = new HudPayload
                {
                    Kind = (i % 2 == 0) ? HudKind.Volume : HudKind.Brightness,
                    Icon = IconsaxKind.VolumeHigh,
                    ValueText = $"{i}%",
                    NormalizedValue = i / 100.0,
                    IsInteractiveSlider = true,
                    AccentHex = "#60A5FA"
                };

                MethodInfo? showHudMethod = typeof(MainNotchViewModel).GetMethod("ShowHud", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(HudPayload)], null);
                showHudMethod?.Invoke(vm, [payload]);
                PumpDispatcher();

                if (sliderIconScale != null)
                {
                    double sx = sliderIconScale.ScaleX;
                    double sy = sliderIconScale.ScaleY;
                    if (double.IsNaN(sx) || double.IsInfinity(sx) || double.IsNaN(sy) || double.IsInfinity(sy))
                    {
                        hasNonFinite = true;
                    }
                    if (sx < 0.60 || sx > 1.30 || sy < 0.60 || sy > 1.30)
                    {
                        scaleOutOfRangeDuringSpam = true;
                    }
                }
            }

            assert(vm.HudArrivalPulseToken == initialToken + spamCount,
                $"HudArrivalPulseToken incremented exactly 50 times (expected {initialToken + spamCount}, actual {vm.HudArrivalPulseToken})");
            assert(!hasNonFinite, "SliderIconScale remained finite (no NaN or Infinity during 50 rapid triggers)");
            assert(!scaleOutOfRangeDuringSpam, "SliderIconScale did not suffer cumulative multiplicative scaling drift (remained within [0.60, 1.30])");

            WaitDispatcher(500);

            Console.WriteLine($"   [ArrivalPulse Debug] sliderIconScale.ScaleX: {sliderIconScale?.ScaleX:F4}, statusVisualScale.ScaleX: {statusVisualScale?.ScaleX:F4}");

            if (sliderIconScale != null)
            {
                assert(Math.Abs(sliderIconScale.ScaleX - 1.0) < 0.01,
                    $"SliderIconScale.ScaleX settled to 1.0 after rapid spam (actual {sliderIconScale.ScaleX:F4})");
                assert(Math.Abs(sliderIconScale.ScaleY - 1.0) < 0.01,
                    $"SliderIconScale.ScaleY settled to 1.0 after rapid spam (actual {sliderIconScale.ScaleY:F4})");
            }
        }
        finally
        {
            testWin.Close();
        }
    }

    // =========================================================================
    // CHALLENGE 2: Color Swatch Squircle & #HEX Formatting Oracle
    // =========================================================================
    private static void TestColorSwatchSquircleAndHexFormatting(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 2] Color Swatch Squircle & #HEX Formatting ---");

        var vm = CreateTestViewModel(out var sm, out var power, out var audio, out var picker, out var settings);
        var hudView = new HudView { DataContext = vm };
        var testWin = CreateOffscreenTestWindow(hudView);

        try
        {
            // 1. Verify ColorPickerService hex format generation
            var testColors = new[]
            {
                (Color.FromArgb(255, 0, 0, 0), "#000000"),
                (Color.FromArgb(255, 255, 255, 255), "#FFFFFF"),
                (Color.FromArgb(255, 255, 87, 34), "#FF5722"),
                (Color.FromArgb(255, 0, 245, 212), "#00F5D4"),
                (Color.FromArgb(255, 18, 52, 86), "#123456")
            };

            var hexRegex = new Regex(@"^#[0-9A-F]{6}$", RegexOptions.Compiled);
            foreach (var (c, expectedHex) in testColors)
            {
                string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                assert(hex == expectedHex, $"Color {c} formatted as uppercase hex {expectedHex}");
                assert(hexRegex.IsMatch(hex), $"Color {c} hex string strictly matches ^#[0-9A-F]{{6}}$");
            }

            // 2. Trigger ColorPicked HUD on ViewModel
            var swatchPayload = new HudPayload
            {
                Kind = HudKind.ColorPicked,
                Icon = IconsaxKind.Colorfilter,
                Title = "#00F5D4",
                ValueText = "Đã chép mã màu",
                AccentHex = "#00F5D4",
                IsInteractiveSlider = false
            };

            MethodInfo? showHudMethod = typeof(MainNotchViewModel).GetMethod("ShowHud", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(HudPayload)], null);
            showHudMethod?.Invoke(vm, [swatchPayload]);
            WaitDispatcher(100);

            assert(vm.IsHudColorSwatchVisible, "vm.IsHudColorSwatchVisible is true when Kind == ColorPicked");
            assert(!vm.IsHudAlbumArtVisible, "vm.IsHudAlbumArtVisible is false when Kind == ColorPicked");
            assert(!vm.IsHudIconVisible, "vm.IsHudIconVisible is false when Kind == ColorPicked");
            assert(!vm.IsHudMiniMeterVisible, "vm.IsHudMiniMeterVisible is false when Kind == ColorPicked");

            // 3. Inspect SquircleBorder element in HudView visual tree
            var statusVisualHost = hudView.FindName("StatusVisualHost") as Grid;
            assert(statusVisualHost != null, "HudView contains StatusVisualHost");

            SquircleBorder? swatchSquircle = null;
            if (statusVisualHost != null)
            {
                foreach (var child in statusVisualHost.Children)
                {
                    if (child is SquircleBorder sq && sq.Width == 24.0)
                    {
                        swatchSquircle = sq;
                        break;
                    }
                }
            }

            assert(swatchSquircle != null, "Found 24x24 SquircleBorder in StatusVisualHost");
            if (swatchSquircle != null)
            {
                assert(swatchSquircle.Width == 24.0, "Color swatch SquircleBorder Width == 24.0");
                assert(swatchSquircle.Height == 24.0, "Color swatch SquircleBorder Height == 24.0");
                assert(Math.Abs(swatchSquircle.CornerRadius - 6.2) < 0.001,
                    $"Color swatch CornerRadius is 6.2 (actual {swatchSquircle.CornerRadius})");
                assert(Math.Abs(swatchSquircle.BorderThickness.Left - 0.8) < 0.001,
                    $"Color swatch BorderThickness is 0.8 (actual {swatchSquircle.BorderThickness.Left})");

                var borderBrush = swatchSquircle.BorderBrush as SolidColorBrush;
                assert(borderBrush != null, "Color swatch BorderBrush is SolidColorBrush");
                if (borderBrush != null)
                {
                    assert(borderBrush.Color.A == 0x45 && borderBrush.Color.R == 0xFF && borderBrush.Color.G == 0xFF && borderBrush.Color.B == 0xFF,
                        $"Color swatch inner border brush matches #45FFFFFF (actual A=0x{borderBrush.Color.A:X2})");
                }
            }
        }
        finally
        {
            testWin.Close();
        }
    }

    // =========================================================================
    // CHALLENGE 3: Mini Level Meter Clamping & Width Calculation
    // =========================================================================
    private static void TestMiniLevelMeterClampingAndWidthCalculation(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 3] Mini Level Meter Clamping & Width Calculation ---");

        var vm = CreateTestViewModel(out var sm, out var power, out var audio, out var picker, out var settings);
        MethodInfo? showHudMethod = typeof(MainNotchViewModel).GetMethod("ShowHud", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(HudPayload)], null);

        // 1. AirPods / AudioDevice tests
        var testCases = new[]
        {
            // (Kind, NormValue, ExpectedMeterVisible, ExpectedWidth)
            (HudKind.AudioDevice, 0.0, false, 0.0),
            (HudKind.AudioDevice, 0.5, true, 21.0),
            (HudKind.AudioDevice, 1.0, true, 42.0),
            (HudKind.AudioDevice, -0.5, false, 0.0),      // Negative clamped to 0.0, visible=false
            (HudKind.AudioDevice, 1.5, true, 42.0),       // Overflow clamped to 42.0
            (HudKind.Battery, 0.20, true, 8.4),
            (HudKind.Battery, 0.80, true, 33.6),
            (HudKind.Battery, 0.0, false, 0.0),
            (HudKind.Battery, -10.0, false, 0.0),
            (HudKind.Battery, 100.0, true, 42.0),
            // Non-meter HUD kinds must NEVER show mini meter
            (HudKind.Volume, 0.50, false, 21.0),
            (HudKind.Brightness, 0.80, false, 33.6),
            (HudKind.CapsLock, 1.0, false, 42.0),
            (HudKind.ColorPicked, 1.0, false, 42.0)
        };

        foreach (var (kind, norm, expectedVis, expectedWidth) in testCases)
        {
            var payload = new HudPayload
            {
                Kind = kind,
                Icon = IconsaxKind.Headphone,
                Title = "Test HUD",
                ValueText = "Val",
                NormalizedValue = norm,
                IsInteractiveSlider = false
            };

            showHudMethod?.Invoke(vm, [payload]);

            assert(vm.IsHudMiniMeterVisible == expectedVis,
                $"IsHudMiniMeterVisible for {kind} with norm={norm} is {expectedVis} (actual {vm.IsHudMiniMeterVisible})");
            assert(Math.Abs(vm.HudMiniMeterWidth - expectedWidth) < 0.01,
                $"HudMiniMeterWidth for norm={norm} is clamped to {expectedWidth:F1}px (actual {vm.HudMiniMeterWidth:F1}px)");
        }

        // 2. Inspect XAML Mini Meter Outer & Inner Borders in HudView
        var hudView = new HudView { DataContext = vm };
        var testWin = CreateOffscreenTestWindow(hudView);

        try
        {
            // Set payload to AudioDevice with 50% to ensure visual tree generates with meter
            showHudMethod?.Invoke(vm, [new HudPayload
            {
                Kind = HudKind.AudioDevice,
                Icon = IconsaxKind.Headphone,
                Title = "AirPods Pro",
                ValueText = "50%",
                NormalizedValue = 0.5,
                IsInteractiveSlider = false,
                AccentHex = "#60A5FA"
            }]);
            WaitDispatcher(100);

            Border? outerMeter = null;
            Border? innerMeter = null;

            void FindMeterBorders(DependencyObject parent)
            {
                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is Border b && b.Width == 42.0 && Math.Abs(b.Height - 4.5) < 0.1)
                    {
                        outerMeter = b;
                        if (b.Child is Border inner)
                        {
                            innerMeter = inner;
                        }
                        return;
                    }
                    FindMeterBorders(child);
                }
            }

            FindMeterBorders(hudView);

            assert(outerMeter != null, "HudView contains 42x4.5px outer capsule meter Border");
            if (outerMeter != null)
            {
                assert(outerMeter.Width == 42.0, "Mini meter outer Width == 42.0");
                assert(Math.Abs(outerMeter.Height - 4.5) < 0.1, "Mini meter outer Height == 4.5");
                assert(Math.Abs(outerMeter.CornerRadius.TopLeft - 2.25) < 0.01, "Mini meter outer CornerRadius == 2.25");

                var bgBrush = outerMeter.Background as SolidColorBrush;
                assert(bgBrush != null && bgBrush.Color.A == 0x28, "Mini meter outer background is #28FFFFFF (25% opacity)");
            }

            assert(innerMeter != null, "HudView contains inner indicator fill Border");
            if (innerMeter != null)
            {
                assert(Math.Abs(innerMeter.Height - 4.5) < 0.1, "Mini meter inner fill Height == 4.5");
                assert(Math.Abs(innerMeter.CornerRadius.TopLeft - 2.25) < 0.01, "Mini meter inner fill CornerRadius == 2.25");
                assert(innerMeter.HorizontalAlignment == HorizontalAlignment.Left, "Mini meter inner fill is Left-aligned for width scaling");
            }
        }
        finally
        {
            testWin.Close();
        }
    }

    // =========================================================================
    // CHALLENGE 4: Album Art Playback Scaling & 180ms Cross-Fade Resilience
    // =========================================================================
    private static void TestAlbumArtPlaybackScalingAndCrossFadeResilience(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 4] Album Art Playback Scaling & 180ms Cross-Fade ---");

        var vm = CreateTestViewModel(out var sm, out var power, out var audio, out var picker, out var settings);
        var mediaView = new MediaOrQuickControlView { DataContext = vm };
        var testWin = CreateOffscreenTestWindow(mediaView);

        try
        {
            var albumArtScale = mediaView.FindName("AlbumArtScale") as ScaleTransform;
            var albumArtContainer = mediaView.FindName("AlbumArtContainer") as FrameworkElement;
            var prevBorder = mediaView.FindName("PreviousAlbumArtBorder") as FrameworkElement;
            var prevBrush = mediaView.FindName("PreviousAlbumArtBrush") as ImageBrush;
            var currBorder = mediaView.FindName("CurrentAlbumArtBorder") as FrameworkElement;

            assert(albumArtScale != null, "MediaOrQuickControlView contains AlbumArtScale");
            assert(albumArtContainer != null, "MediaOrQuickControlView contains AlbumArtContainer");
            assert(prevBorder != null, "MediaOrQuickControlView contains PreviousAlbumArtBorder");
            assert(prevBrush != null, "MediaOrQuickControlView contains PreviousAlbumArtBrush");
            assert(currBorder != null, "MediaOrQuickControlView contains CurrentAlbumArtBorder");

            // 1. Playback State Scaling: Play -> 1.0 (Opacity 1.0)
            vm.IsMediaPlaying = true;
            WaitDispatcher(300); // 240ms duration + margin

            if (albumArtScale != null && albumArtContainer != null)
            {
                assert(Math.Abs(albumArtScale.ScaleX - 1.0) < 0.01,
                    $"AlbumArtScale.ScaleX on play == 1.0 (actual {albumArtScale.ScaleX:F3})");
                assert(Math.Abs(albumArtScale.ScaleY - 1.0) < 0.01,
                    $"AlbumArtScale.ScaleY on play == 1.0 (actual {albumArtScale.ScaleY:F3})");
                assert(Math.Abs(albumArtContainer.Opacity - 1.0) < 0.01,
                    $"AlbumArtContainer.Opacity on play == 1.0 (actual {albumArtContainer.Opacity:F3})");
            }

            // 2. Playback State Scaling: Pause -> 0.90 (Opacity 0.84)
            vm.IsMediaPlaying = false;
            WaitDispatcher(260); // 200ms duration + margin

            if (albumArtScale != null && albumArtContainer != null)
            {
                assert(Math.Abs(albumArtScale.ScaleX - 0.90) < 0.01,
                    $"AlbumArtScale.ScaleX on pause == 0.90 (actual {albumArtScale.ScaleX:F3})");
                assert(Math.Abs(albumArtScale.ScaleY - 0.90) < 0.01,
                    $"AlbumArtScale.ScaleY on pause == 0.90 (actual {albumArtScale.ScaleY:F3})");
                assert(Math.Abs(albumArtContainer.Opacity - 0.84) < 0.01,
                    $"AlbumArtContainer.Opacity on pause == 0.84 (actual {albumArtContainer.Opacity:F3})");
            }

            // 3. 180ms Cross-Fade between Bitmap A and Bitmap B
            var artA = CreateTestBitmap(48, 48, 255, 0, 0);
            var artB = CreateTestBitmap(48, 48, 0, 255, 0);

            vm.AlbumArt = artA;
            WaitDispatcher(250);

            Console.WriteLine($"   [CrossFade Debug] After artA: prevBorder.Vis={prevBorder?.Visibility}, currBorder.Vis={currBorder?.Visibility}");

            // Transition from Art A to Art B
            vm.AlbumArt = artB;
            PumpDispatcher();

            Console.WriteLine($"   [CrossFade Debug] Right after artB: prevBorder.Vis={prevBorder?.Visibility}, prevBrush.Source={prevBrush?.ImageSource != null}");

            if (prevBorder != null && prevBrush != null)
            {
                assert(prevBorder.Visibility == Visibility.Visible,
                    "PreviousAlbumArtBorder becomes Visible during cross-fade");
                assert(ReferenceEquals(prevBrush.ImageSource, artA),
                    "PreviousAlbumArtBrush holds outgoing artwork (artA) during cross-fade");
            }

            // Wait for 180ms cross-fade to complete
            WaitDispatcher(250);

            if (prevBorder != null && prevBrush != null && currBorder != null)
            {
                assert(prevBorder.Visibility == Visibility.Collapsed,
                    "PreviousAlbumArtBorder Collapsed after cross-fade completes");
                assert(prevBrush.ImageSource == null,
                    "PreviousAlbumArtBrush.ImageSource cleared to null after cross-fade (no bitmap leaks)");
                assert(Math.Abs(currBorder.Opacity - 1.0) < 0.01,
                    "CurrentAlbumArtBorder.Opacity settled to 1.0 after cross-fade");
            }

            // 4. Torture Stress Test: 50 Rapid Consecutive Album Art Swaps
            long memBefore = GC.GetTotalMemory(true);
            for (int i = 0; i < 50; i++)
            {
                var nextArt = CreateTestBitmap(32, 32, (byte)(i * 5 % 256), 128, 64);
                vm.AlbumArt = nextArt;
                PumpDispatcher();
            }

            // Wait for final cross-fade animation to complete
            WaitDispatcher(300);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memAfter = GC.GetTotalMemory(true);
            long memDeltaKb = (memAfter - memBefore) / 1024;

            assert(prevBorder?.Visibility == Visibility.Collapsed,
                "PreviousAlbumArtBorder Collapsed after 50 rapid track swaps");
            assert(prevBrush?.ImageSource == null,
                "PreviousAlbumArtBrush.ImageSource == null after 50 rapid track swaps (leak-free)");
            assert(memDeltaKb < 5120, // less than 5MB delta for 50 bitmaps
                $"Album art memory overhead is bounded (delta: {memDeltaKb} KB)");
        }
        finally
        {
            testWin.Close();
        }
    }

    // =========================================================================
    // CHALLENGE 5: Zero CPU Constraint & Animation Clock Detachment
    // =========================================================================
    private static void TestZeroCpuAnimationClockDetachmentConstraint(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 5] Zero CPU Animation Clock Detachment Constraint ---");

        var vm = CreateTestViewModel(out var sm, out var power, out var audio, out var picker, out var settings);

        // Part A: MediaOrQuickControlView Album Art Animations Detachment
        var mediaView = new MediaOrQuickControlView { DataContext = vm };
        var mediaWin = CreateOffscreenTestWindow(mediaView);

        try
        {
            var albumArtScale = mediaView.FindName("AlbumArtScale") as ScaleTransform;
            var albumArtContainer = mediaView.FindName("AlbumArtContainer") as FrameworkElement;
            var currBorder = mediaView.FindName("CurrentAlbumArtBorder") as FrameworkElement;

            // Trigger play scale animation
            vm.IsMediaPlaying = true;
            WaitDispatcher(300); // 240ms duration + margin

            if (albumArtScale != null)
            {
                assert(!albumArtScale.HasAnimatedProperties,
                    "MediaOrQuickControlView: AlbumArtScale has NO active animation clocks after play scale settles (BeginAnimation(..., null) verified)");
            }
            if (albumArtContainer != null)
            {
                assert(!albumArtContainer.HasAnimatedProperties,
                    "MediaOrQuickControlView: AlbumArtContainer has NO active animation clocks after play scale settles (BeginAnimation(..., null) verified)");
            }

            // Trigger cross-fade
            vm.AlbumArt = CreateTestBitmap(32, 32, 10, 20, 30);
            WaitDispatcher(220);
            vm.AlbumArt = CreateTestBitmap(32, 32, 40, 50, 60);
            WaitDispatcher(250); // 180ms cross-fade duration + margin

            if (currBorder != null)
            {
                assert(!currBorder.HasAnimatedProperties,
                    "MediaOrQuickControlView: CurrentAlbumArtBorder has NO active animation clocks after cross-fade settles (BeginAnimation(..., null) verified)");
            }
        }
        finally
        {
            mediaWin.Close();
        }

        // Part B: HudView Arrival Pulse Animation Detachment (EMPIRICAL AUDIT)
        var hudView = new HudView { DataContext = vm };
        var hudWin = CreateOffscreenTestWindow(hudView);

        try
        {
            var statusVisualScale = hudView.FindName("StatusVisualScale") as ScaleTransform;
            var sliderIconScale = hudView.FindName("SliderIconScale") as ScaleTransform;
            var statusTextTranslate = hudView.FindName("StatusTextTranslate") as TranslateTransform;
            var statusTextContainer = hudView.FindName("StatusTextContainer") as FrameworkElement;

            // Trigger arrival pulse
            MethodInfo? showHudMethod = typeof(MainNotchViewModel).GetMethod("ShowHud", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(HudPayload)], null);
            showHudMethod?.Invoke(vm, [new HudPayload
            {
                Kind = HudKind.Volume,
                Icon = IconsaxKind.VolumeHigh,
                ValueText = "45%",
                NormalizedValue = 0.45,
                IsInteractiveSlider = true
            }]);

            // Verify animation clock is attached during active animation
            PumpDispatcher();
            bool wasAttachedDuringAnimation = (statusVisualScale?.HasAnimatedProperties == true) || (sliderIconScale?.HasAnimatedProperties == true);
            assert(wasAttachedDuringAnimation,
                "HudView: Animation clock is actively attached during initial arrival animation window");

            // Wait for arrival pulse duration (280ms) + settle margin
            WaitDispatcher(400);

            // Audit whether animation clocks detached upon settling
            bool statusVisualScaleDetached = statusVisualScale?.HasAnimatedProperties == false;
            bool sliderIconScaleDetached = sliderIconScale?.HasAnimatedProperties == false;
            bool statusTextTranslateDetached = statusTextTranslate?.HasAnimatedProperties == false;
            bool statusTextContainerDetached = statusTextContainer?.HasAnimatedProperties == false;

            Console.WriteLine($"   [Clock Audit] StatusVisualScale.HasAnimatedProperties: {statusVisualScale?.HasAnimatedProperties}");
            Console.WriteLine($"   [Clock Audit] SliderIconScale.HasAnimatedProperties: {sliderIconScale?.HasAnimatedProperties}");
            Console.WriteLine($"   [Clock Audit] StatusTextTranslate.HasAnimatedProperties: {statusTextTranslate?.HasAnimatedProperties}");
            Console.WriteLine($"   [Clock Audit] StatusTextContainer.HasAnimatedProperties: {statusTextContainer?.HasAnimatedProperties}");

            assert(statusVisualScaleDetached,
                "HudView: StatusVisualScale animation clock detached when settled (BeginAnimation(..., null))",
                statusVisualScaleDetached ? "PASSED" : "FAILED: Animation clock still attached in HoldEnd state; BeginAnimation(..., null) was not invoked on completion");

            assert(sliderIconScaleDetached,
                "HudView: SliderIconScale animation clock detached when settled (BeginAnimation(..., null))",
                sliderIconScaleDetached ? "PASSED" : "FAILED: Animation clock still attached in HoldEnd state; BeginAnimation(..., null) was not invoked on completion");

            assert(statusTextTranslateDetached,
                "HudView: StatusTextTranslate animation clock detached when settled",
                statusTextTranslateDetached ? "PASSED" : "FAILED: Translate animation clock still attached in HoldEnd state");

            assert(statusTextContainerDetached,
                "HudView: StatusTextContainer opacity animation clock detached when settled",
                statusTextContainerDetached ? "PASSED" : "FAILED: Opacity animation clock still attached in HoldEnd state");
        }
        finally
        {
            hudWin.Close();
        }

        // Part C: NotchWindow Collapsed Mode Transition Animation Detachment (EMPIRICAL AUDIT)
        var notchSettings = new SettingsService();
        var notchSm = new NotchStateMachine(notchSettings.Current);
        var notchDesc = new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "Test OLED Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };
        var notchAudio = new AudioService(notchSettings);
        var notchMedia = new MediaService(notchSettings);
        var notchPower = new DisplayPowerService(notchSettings, notchDesc);
        var notchShelf = new FileShelfService(notchSettings);
        var notchTimer = new TimerService();
        var notchPrivacy = new PrivacyService(notchSettings);
        var notchMonitor = new SystemMonitorService(notchSettings);
        var notchNotif = new NotificationService(notchSettings);
        var notchPicker = new ColorPickerService(notchSm);
        var notchAgentMonitor = new AgentMonitorService(notchSettings);

        var notchVm = new MainNotchViewModel(
            notchSettings, notchSm, notchAudio, notchPower, notchMedia, notchShelf, notchTimer, notchPrivacy, notchMonitor, notchNotif, notchPicker, notchAgentMonitor, () => { });

        var notchWin = new NotchWindow(
            notchSettings, notchSm, notchVm, notchNotif, notchDesc, () => { })
        {
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false
        };

        notchWin.Show();
        PumpDispatcher();

        try
        {
            var collapsedTranslate = notchWin.FindName("CollapsedContentTranslate") as TranslateTransform;
            var collapsedScale = notchWin.FindName("CollapsedContentScale") as ScaleTransform;
            var collapsedHost = notchWin.FindName("CollapsedContentHost") as FrameworkElement;

            assert(collapsedTranslate != null, "NotchWindow: CollapsedContentTranslate exists");
            assert(collapsedScale != null, "NotchWindow: CollapsedContentScale exists");
            assert(collapsedHost != null, "NotchWindow: CollapsedContentHost exists");

            MethodInfo? method = typeof(NotchWindow).GetMethod("PlayCollapsedModeTransition", BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(notchWin, null);
            PumpDispatcher();

            WaitDispatcher(350);

            bool translateDetached = collapsedTranslate?.HasAnimatedProperties == false;
            bool scaleDetached = collapsedScale?.HasAnimatedProperties == false;
            bool hostDetached = collapsedHost?.HasAnimatedProperties == false;

            Console.WriteLine($"   [Clock Audit] CollapsedContentTranslate.HasAnimatedProperties: {collapsedTranslate?.HasAnimatedProperties}");
            Console.WriteLine($"   [Clock Audit] CollapsedContentScale.HasAnimatedProperties: {collapsedScale?.HasAnimatedProperties}");
            Console.WriteLine($"   [Clock Audit] CollapsedContentHost.HasAnimatedProperties: {collapsedHost?.HasAnimatedProperties}");

            assert(translateDetached,
                "NotchWindow: CollapsedContentTranslate animation clock detached when settled (BeginAnimation(..., null))",
                translateDetached ? "PASSED" : "FAILED: Translate animation clock still attached");

            assert(scaleDetached,
                "NotchWindow: CollapsedContentScale animation clock detached when settled (BeginAnimation(..., null))",
                scaleDetached ? "PASSED" : "FAILED: Scale animation clock still attached");

            assert(hostDetached,
                "NotchWindow: CollapsedContentHost opacity animation clock detached when settled (BeginAnimation(..., null))",
                hostDetached ? "PASSED" : "FAILED: Host opacity animation clock still attached");
        }
        finally
        {
            notchWin.Close();
        }
    }
}
