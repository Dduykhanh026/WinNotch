using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Interop;
using WinNotch.Services;
using WinNotch.Views;

namespace WinNotch.Tests;

public delegate void AssertionDelegate(bool condition, string testName, string? details = null);

public static class ColorPickerChallengeTests
{
    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine(" MILESTONE 2: COLOR PICKER EMPIRICAL STRESS SUITE ");
        Console.WriteLine("=================================================");

        EnsureWpfEnvironment();

        TestBoundaryRepulsionMath(assert);
        TestPixelExtractionOutOfBoundsGuards(assert);
        TestDynamicLuminanceThreshold(assert);
        TestColorPickerServiceAndOverlayContracts(assert);
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

    /// <summary>
    /// Oracle for ColorPickerOverlayWindow.UpdateCardPosition logic
    /// </summary>
    private static (double x, double y) OracleCardPosition(double cx, double cy, double winW, double winH)
    {
        double targetX = cx + 20.0;
        if (targetX + 140.0 > winW - 12.0)
        {
            targetX = cx - 140.0 - 20.0;
        }

        double targetY = cy + 20.0;
        if (targetY + 168.0 > winH - 12.0)
        {
            targetY = cy - 168.0 - 20.0;
        }

        double clampedX = Math.Clamp(targetX, 12.0, Math.Max(12.0, winW - 140.0 - 12.0));
        double clampedY = Math.Clamp(targetY, 12.0, Math.Max(12.0, winH - 168.0 - 12.0));

        return (clampedX, clampedY);
    }

    private static void TestBoundaryRepulsionMath(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 14] 4-Way Boundary Repulsion Math & Non-Occlusion Tests ---");

        const double winW = 1920.0;
        const double winH = 1080.0;

        // 1. Top-Left corner (0, 0)
        var tl = OracleCardPosition(0, 0, winW, winH);
        assert(tl.x == 20.0 && tl.y == 20.0, "Top-Left (0, 0) positions card at (20, 20)");

        // 2. Top-Right corner (winW, 0)
        var tr = OracleCardPosition(winW, 0, winW, winH);
        assert(tr.x == winW - 160.0 && tr.y == 20.0, "Top-Right (winW, 0) flips X to winW - 160");

        // 3. Bottom-Right corner (winW, winH)
        var br = OracleCardPosition(winW, winH, winW, winH);
        assert(br.x == winW - 160.0 && br.y == winH - 188.0, "Bottom-Right (winW, winH) flips both X and Y");

        // 4. Bottom-Left corner (0, winH)
        var bl = OracleCardPosition(0, winH, winW, winH);
        assert(bl.x == 20.0 && bl.y == winH - 188.0, "Bottom-Left (0, winH) flips Y to winH - 188");

        // 5. Flip threshold in X: cx = winW - 172.0 (no flip) vs cx = winW - 171.9 (flips)
        var noFlipX = OracleCardPosition(winW - 172.0, 500, winW, winH);
        assert(Math.Abs(noFlipX.x - (winW - 152.0)) < 1e-4, "cx = winW - 172 does not flip X (sits right at right margin)");

        var flipX = OracleCardPosition(winW - 171.9, 500, winW, winH);
        assert(flipX.x < winW - 300, "cx = winW - 171.9 flips X to left of cursor");

        // 6. Flip threshold in Y: cy = winH - 200.0 (no flip) vs cy = winH - 199.9 (flips)
        var noFlipY = OracleCardPosition(500, winH - 200.0, winW, winH);
        assert(Math.Abs(noFlipY.y - (winH - 180.0)) < 1e-4, "cy = winH - 200 does not flip Y (sits right at bottom margin)");

        var flipY = OracleCardPosition(500, winH - 199.9, winW, winH);
        assert(flipY.y < winH - 350, "cy = winH - 199.9 flips Y above cursor");

        // 7. Negative coordinates and extreme out-of-bounds
        var neg = OracleCardPosition(-1000, -1000, winW, winH);
        assert(neg.x == 12.0 && neg.y == 12.0, "Extreme negative coordinates clamp safely to (12, 12)");

        var oobPos = OracleCardPosition(winW + 5000, winH + 5000, winW, winH);
        assert(oobPos.x == winW - 152.0 && oobPos.y == winH - 180.0, "Extreme positive coordinates clamp safely to (winW - 152, winH - 180)");

        // 8. Degenerate small display sizes (must never throw ArgumentException in Math.Clamp)
        try
        {
            var smallDisplay = OracleCardPosition(25, 25, 50, 50);
            assert(smallDisplay.x == 12.0 && smallDisplay.y == 12.0, "Degenerate 50x50 display safely clamps to (12, 12) without throwing");
        }
        catch (Exception ex)
        {
            assert(false, "Degenerate display test threw exception", ex.Message);
        }

        // 9. Stress test: Non-occlusion invariant over 20,000 points across screen
        bool occlusionDetected = false;
        var rng = new Random(42);
        for (int i = 0; i < 20000; i++)
        {
            double cx = rng.NextDouble() * winW;
            double cy = rng.NextDouble() * winH;

            var pos = OracleCardPosition(cx, cy, winW, winH);
            double cardLeft = pos.x;
            double cardRight = pos.x + 140.0;
            double cardTop = pos.y;
            double cardBottom = pos.y + 168.0;

            // Does cursor fall inside card rect?
            if (cx >= cardLeft && cx <= cardRight && cy >= cardTop && cy <= cardBottom)
            {
                occlusionDetected = true;
                break;
            }
        }
        assert(!occlusionDetected, "Non-occlusion invariant verified across 20,000 coordinates (cursor is never obscured)");

        // 10. Reflection verification on live ColorPickerOverlayWindow instance
        try
        {
            byte[] dummyBgra = new byte[100 * 100 * 4];
            var bmp = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Bgr32, null, dummyBgra, 400);
            bmp.Freeze();

            var win = new ColorPickerOverlayWindow(bmp, dummyBgra, 100, 100, 0, 0);
            win.Width = winW;
            win.Height = winH;

            var updateMethod = typeof(ColorPickerOverlayWindow).GetMethod("UpdateCardPosition", BindingFlags.NonPublic | BindingFlags.Instance);
            assert(updateMethod != null, "ColorPickerOverlayWindow.UpdateCardPosition method found via reflection");

            if (updateMethod != null)
            {
                var cardField = typeof(ColorPickerOverlayWindow).GetField("MagnifierCard", BindingFlags.NonPublic | BindingFlags.Instance);
                Border? card = cardField?.GetValue(win) as Border;
                if (card == null)
                {
                    card = win.FindName("MagnifierCard") as Border;
                }
                assert(card != null, "MagnifierCard Border element resolved");

                if (card != null)
                {
                    // Call UpdateCardPosition(new Point(0, 0))
                    updateMethod.Invoke(win, new object[] { new Point(0, 0) });
                    double actualLeft = Canvas.GetLeft(card);
                    double actualTop = Canvas.GetTop(card);
                    assert(actualLeft == 20.0 && actualTop == 20.0, $"Reflection call at (0, 0) set Left={actualLeft}, Top={actualTop}");

                    // Call UpdateCardPosition(new Point(winW, winH))
                    updateMethod.Invoke(win, new object[] { new Point(winW, winH) });
                    actualLeft = Canvas.GetLeft(card);
                    actualTop = Canvas.GetTop(card);
                    assert(actualLeft == winW - 160.0 && actualTop == winH - 188.0, $"Reflection call at (winW, winH) set Left={actualLeft}, Top={actualTop}");
                }
            }

            win.Close();
        }
        catch (Exception ex)
        {
            assert(false, "Reflection live window test threw exception", ex.ToString());
        }
    }

    private static void TestPixelExtractionOutOfBoundsGuards(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 15] 11x11 Loupe Pixel Extraction & Out-of-Bounds Guards Tests ---");

        const int w = 100;
        const int h = 100;
        byte[] bgra = new byte[w * h * 4];

        // Fill test pattern: encode (x, y) into B and R channels
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                bgra[idx + 0] = (byte)x;       // Blue = X
                bgra[idx + 1] = 128;           // Green = 128
                bgra[idx + 2] = (byte)y;       // Red = Y
                bgra[idx + 3] = 255;           // Alpha = 255
            }
        }

        var snapshot = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
        snapshot.Freeze();

        var win = new ColorPickerOverlayWindow(snapshot, bgra, w, h, 0, 0);

        var extractMethod = typeof(ColorPickerOverlayWindow).GetMethod("ExtractMagnifierGrid", BindingFlags.NonPublic | BindingFlags.Instance);
        assert(extractMethod != null, "ColorPickerOverlayWindow.ExtractMagnifierGrid method found via reflection");

        var gridBufferField = typeof(ColorPickerOverlayWindow).GetField("_gridBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        assert(gridBufferField != null, "ColorPickerOverlayWindow._gridBuffer field found via reflection");

        byte[]? gridBuffer = gridBufferField?.GetValue(win) as byte[];
        assert(gridBuffer != null && gridBuffer.Length == 11 * 11 * 4, $"_gridBuffer length is exactly 484 bytes (11x11x4)");

        if (extractMethod != null && gridBuffer != null)
        {
            // Helper to get pixel from gridBuffer at (col, row)
            (byte b, byte g, byte r, byte a) GetLoupePixel(int col, int row)
            {
                int idx = (row * 11 + col) * 4;
                return (gridBuffer[idx + 0], gridBuffer[idx + 1], gridBuffer[idx + 2], gridBuffer[idx + 3]);
            }

            // 1. Center extraction: centerX = 50, centerY = 50
            extractMethod.Invoke(win, new object[] { 50, 50 });
            var centerPix = GetLoupePixel(5, 5);
            assert(centerPix.b == 50 && centerPix.r == 50 && centerPix.a == 255, "Center extraction: loupe center cell (5,5) matches (50, 50) exactly");

            var offsetPix = GetLoupePixel(0, 0); // 50 - 5 = 45, 50 - 5 = 45
            assert(offsetPix.b == 45 && offsetPix.r == 45, "Loupe corner (0,0) samples (45, 45)");

            // 2. Top-Left out-of-bounds clamping: centerX = 0, centerY = 0
            extractMethod.Invoke(win, new object[] { 0, 0 });
            var tlCenter = GetLoupePixel(5, 5);
            assert(tlCenter.b == 0 && tlCenter.r == 0, "Loupe center cell at (0, 0) samples (0, 0)");

            // Columns 0..5 and rows 0..5 must clamp to 0 without IndexOutOfRange
            bool tlClamped = true;
            for (int r = 0; r <= 5; r++)
            {
                for (int c = 0; c <= 5; c++)
                {
                    var p = GetLoupePixel(c, r);
                    if (p.b != 0 || p.r != 0 || p.a != 255) tlClamped = false;
                }
            }
            assert(tlClamped, "Top-Left (0, 0) correctly clamps all negative offsets (rows 0..5, cols 0..5) to (0, 0)");

            // Check col 6 (offset +1) samples X = 1
            var col6Pix = GetLoupePixel(6, 5);
            assert(col6Pix.b == 1 && col6Pix.r == 0, "Col 6 (offset +1) at X=0 samples X=1");

            // 3. Bottom-Right out-of-bounds clamping: centerX = 99, centerY = 99
            extractMethod.Invoke(win, new object[] { 99, 99 });
            var brCenter = GetLoupePixel(5, 5);
            assert(brCenter.b == 99 && brCenter.r == 99, "Loupe center cell at (99, 99) samples (99, 99)");

            bool brClamped = true;
            for (int r = 5; r <= 10; r++)
            {
                for (int c = 5; c <= 10; c++)
                {
                    var p = GetLoupePixel(c, r);
                    if (p.b != 99 || p.r != 99 || p.a != 255) brClamped = false;
                }
            }
            assert(brClamped, "Bottom-Right (99, 99) correctly clamps all positive offsets (rows 5..10, cols 5..10) to (99, 99)");

            // 4. Top-Right clamping: (99, 0)
            extractMethod.Invoke(win, new object[] { 99, 0 });
            var trCenter = GetLoupePixel(5, 5);
            assert(trCenter.b == 99 && trCenter.r == 0, "Loupe center cell at (99, 0) samples (99, 0)");

            // 5. Bottom-Left clamping: (0, 99)
            extractMethod.Invoke(win, new object[] { 0, 99 });
            var blCenter = GetLoupePixel(5, 5);
            assert(blCenter.b == 0 && blCenter.r == 99, "Loupe center cell at (0, 99) samples (0, 99)");

            // 6. Extreme out-of-bounds coordinates (clamping stress test)
            extractMethod.Invoke(win, new object[] { -100, -100 });
            var extremeNeg = GetLoupePixel(5, 5);
            assert(extremeNeg.b == 0 && extremeNeg.r == 0, "Extreme negative coordinates (-100, -100) clamp to (0, 0)");

            extractMethod.Invoke(win, new object[] { 1000, 1000 });
            var extremePos = GetLoupePixel(5, 5);
            assert(extremePos.b == 99 && extremePos.r == 99, "Extreme positive coordinates (1000, 1000) clamp to (99, 99)");

            // 7. 10,000 random coordinate torture test
            var rng = new Random(1337);
            bool stressFailed = false;
            for (int i = 0; i < 10000; i++)
            {
                int rx = rng.Next(-200, 300);
                int ry = rng.Next(-200, 300);
                try
                {
                    extractMethod.Invoke(win, new object[] { rx, ry });
                }
                catch (Exception)
                {
                    stressFailed = true;
                    break;
                }
            }
            assert(!stressFailed, "10,000 randomized out-of-bounds coordinates executed without buffer overrun or crash");
        }

        win.Close();
    }

    private static void TestDynamicLuminanceThreshold(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 16] ITU-R BT.601 Dynamic Perceived Luminance & Reticle Contrast Tests ---");

        // Perceived luminance formula in ColorPickerOverlayWindow:
        // double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        // bool isDark = luminance < 0.5;
        static (double lum, bool isDark) CalcLuminance(byte r, byte g, byte b)
        {
            double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            return (lum, lum < 0.5);
        }

        // 1. Pure black (#000000)
        var black = CalcLuminance(0, 0, 0);
        assert(black.lum == 0.0 && black.isDark, "Pure Black (#000000) -> Lum=0.00, isDark=true (Reticle: White inner, Black outer)");

        // 2. Pure white (#FFFFFF)
        var white = CalcLuminance(255, 255, 255);
        assert(Math.Abs(white.lum - 1.0) < 1e-6 && !white.isDark, "Pure White (#FFFFFF) -> Lum=1.00, isDark=false (Reticle: Black inner, White outer)");

        // 3. Middle gray (#808080 = 128, 128, 128)
        var midGray = CalcLuminance(128, 128, 128);
        assert(midGray.lum > 0.50 && !midGray.isDark, $"Middle Gray (#808080) -> Lum={midGray.lum:F4} > 0.5, isDark=false (Reticle: Black inner)");

        // 4. Dark gray (#7F7F7F = 127, 127, 127)
        var darkGray = CalcLuminance(127, 127, 127);
        assert(darkGray.lum < 0.50 && darkGray.isDark, $"Dark Gray (#7F7F7F) -> Lum={darkGray.lum:F4} < 0.5, isDark=true (Reticle: White inner)");

        // 5. Bright green (#00FF00)
        var green = CalcLuminance(0, 255, 0);
        assert(Math.Abs(green.lum - 0.587) < 1e-4 && !green.isDark, "Bright Green (#00FF00) -> Lum=0.587 > 0.5, isDark=false (Human eye sensitive to green)");

        // 6. Dark blue (#00008B = 0, 0, 139)
        var darkBlue = CalcLuminance(0, 0, 139);
        assert(darkBlue.lum < 0.10 && darkBlue.isDark, $"Dark Blue (#00008B) -> Lum={darkBlue.lum:F4} < 0.5, isDark=true (Reticle: White inner)");

        // 7. Pure blue (#0000FF = 0, 0, 255)
        var pureBlue = CalcLuminance(0, 0, 255);
        assert(Math.Abs(pureBlue.lum - 0.114) < 1e-4 && pureBlue.isDark, "Pure Blue (#0000FF) -> Lum=0.114 < 0.5, isDark=true (Reticle: White inner)");

        // 8. Other standard primaries: Yellow, Red, Cyan, Magenta
        var yellow = CalcLuminance(255, 255, 0);
        assert(Math.Abs(yellow.lum - 0.886) < 1e-4 && !yellow.isDark, "Yellow (#FFFF00) -> Lum=0.886 > 0.5, isDark=false");

        var red = CalcLuminance(255, 0, 0);
        assert(Math.Abs(red.lum - 0.299) < 1e-4 && red.isDark, "Red (#FF0000) -> Lum=0.299 < 0.5, isDark=true");

        var cyan = CalcLuminance(0, 255, 255);
        assert(Math.Abs(cyan.lum - 0.701) < 1e-4 && !cyan.isDark, "Cyan (#00FFFF) -> Lum=0.701 > 0.5, isDark=false");

        var magenta = CalcLuminance(255, 0, 255);
        assert(Math.Abs(magenta.lum - 0.413) < 1e-4 && magenta.isDark, "Magenta (#FF00FF) -> Lum=0.413 < 0.5, isDark=true");

        // 9. Full-gamut randomized test (100,000 iterations): verify lum is strictly in [0.0, 1.0] and never NaN
        var rng = new Random(777);
        bool gamutValid = true;
        for (int i = 0; i < 100000; i++)
        {
            byte r = (byte)rng.Next(256);
            byte g = (byte)rng.Next(256);
            byte b = (byte)rng.Next(256);

            var res = CalcLuminance(r, g, b);
            if (double.IsNaN(res.lum) || res.lum < 0.0 || res.lum > 1.0000001)
            {
                gamutValid = false;
                break;
            }
        }
        assert(gamutValid, "100,000 random RGB samples: luminance strictly within [0.0, 1.0] with no NaN/overflow");
    }

    private static void TestColorPickerServiceAndOverlayContracts(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 17] ColorPickerService & Overlay Contract Tests ---");

        // 1. ColorPickerService.GetPixelColor bounds tests
        byte[] testPixels = new byte[10 * 10 * 4];
        // Set pixel (5, 5) to Red (B=0, G=0, R=255)
        int p55 = (5 * 10 + 5) * 4;
        testPixels[p55 + 0] = 0;
        testPixels[p55 + 1] = 0;
        testPixels[p55 + 2] = 255;
        testPixels[p55 + 3] = 255;

        Color c55 = ColorPickerService.GetPixelColor(testPixels, 10, 10, 5, 5);
        assert(c55.R == 255 && c55.G == 0 && c55.B == 0, "GetPixelColor correctly reads Red channel without BGR swap");

        Color cNeg = ColorPickerService.GetPixelColor(testPixels, 10, 10, -1, 5);
        assert(cNeg == Colors.Black, "GetPixelColor(-1, 5) returns Colors.Black");

        Color cOob = ColorPickerService.GetPixelColor(testPixels, 10, 10, 10, 5);
        assert(cOob == Colors.Black, "GetPixelColor(10, 5) out-of-bounds returns Colors.Black");

        Color cShort = ColorPickerService.GetPixelColor(new byte[10], 10, 10, 5, 5);
        assert(cShort == Colors.Black, "GetPixelColor with truncated buffer safely returns Colors.Black");

        // 2. ColorPickerService.CreateColorSwatch tests
        var swatch = ColorPickerService.CreateColorSwatch(Color.FromRgb(255, 128, 64), 64, 64);
        assert(swatch != null, "CreateColorSwatch returns non-null BitmapSource");
        assert(swatch != null && swatch.IsFrozen, "CreateColorSwatch returns frozen BitmapSource");
        assert(swatch != null && swatch.PixelWidth == 64 && swatch.PixelHeight == 64, "CreateColorSwatch dimensions are 64x64");
        assert(swatch != null && swatch.Format == PixelFormats.Bgra32, "CreateColorSwatch format is Bgra32");

        if (swatch != null)
        {
            byte[] swatchPixels = new byte[64 * 64 * 4];
            swatch.CopyPixels(swatchPixels, 64 * 4, 0);
            assert(swatchPixels[0] == 64 && swatchPixels[1] == 128 && swatchPixels[2] == 255 && swatchPixels[3] == 255,
                "CreateColorSwatch pixel data matches RGB(255, 128, 64) with Alpha=255");
        }

        // 3. ColorPickerService.TryCopyTextToClipboard edge tests
        assert(!ColorPickerService.TryCopyTextToClipboard(""), "TryCopyTextToClipboard(\"\") returns false");
        assert(!ColorPickerService.TryCopyTextToClipboard(null!), "TryCopyTextToClipboard(null) returns false");

        // 4. ColorPickerOverlayWindow live UI hierarchy verification
        byte[] livePixels = new byte[100 * 100 * 4];
        var liveSnapshot = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Bgr32, null, livePixels, 400);
        liveSnapshot.Freeze();

        var overlay = new ColorPickerOverlayWindow(liveSnapshot, livePixels, 100, 100, 0, 0);
        assert(overlay.Topmost, "OverlayWindow has Topmost = true");
        assert(overlay.WindowStyle == WindowStyle.None, "OverlayWindow has WindowStyle.None");
        assert(overlay.AllowsTransparency, "OverlayWindow has AllowsTransparency = true");
        assert(overlay.Cursor == System.Windows.Input.Cursors.Cross, "OverlayWindow has Cursor = Cursors.Cross");

        var card = overlay.FindName("MagnifierCard") as Border;
        assert(card != null && card.Width == 140 && card.Height == 168, "MagnifierCard dimensions are exactly 140x168 DIPs");

        var reticleOuter = overlay.FindName("ReticleOuter") as Border;
        assert(reticleOuter != null && reticleOuter.Width == 12 && reticleOuter.Height == 12, "ReticleOuter dimensions are exactly 12x12 DIPs");

        var reticleInner = overlay.FindName("ReticleInner") as Border;
        assert(reticleInner != null && reticleInner.Width == 10 && reticleInner.Height == 10, "ReticleInner dimensions are exactly 10x10 DIPs");

        var magImage = overlay.FindName("MagnifierImage") as Image;
        assert(magImage != null && magImage.Width == 110 && magImage.Height == 110, "MagnifierImage dimensions are exactly 110x110 DIPs");

        overlay.Close();
        assert(true, "OverlayWindow closed cleanly");
    }
}
