using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Views;

namespace WinNotch.Services;

public sealed class ColorPickerService : IDisposable
{
    private readonly NotchStateMachine _stateMachine;
    private ColorPickerOverlayWindow? _activeOverlay;
    private bool _isCapturing;

    public event Action<HudPayload>? ColorPickedHudRequested;

    public bool IsPickerActive => _isCapturing || _activeOverlay != null;

    public ColorPickerService(NotchStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    public void StartColorPicker()
    {
        _ = StartColorPickerAsync();
    }

    public async Task StartColorPickerAsync()
    {
        if (IsPickerActive)
        {
            _activeOverlay?.Activate();
            return;
        }

        _isCapturing = true;

        try
        {
            // 1. Pre-capture notch suppression: collapse if expanded and allow spring animation to settle
            if (_stateMachine.State is NotchVisualState.Expanded or NotchVisualState.HudPop or NotchVisualState.DragReady)
            {
                _stateMachine.CollapseImmediately();
                await Task.Delay(150);
            }
            else
            {
                _stateMachine.CollapseImmediately();
                await Task.Delay(50);
            }

            // 2. Query multi-monitor physical VirtualScreen dimensions
            int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int vWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int vHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            if (vWidth <= 0 || vHeight <= 0)
            {
                Debug.WriteLine("[ColorPickerService] Invalid virtual screen dimensions.");
                return;
            }

            // 3. Perform physical GDI capture
            if (!CaptureVirtualScreen(vx, vy, vWidth, vHeight, out byte[] bgraPixels, out BitmapSource frozenSource))
            {
                Debug.WriteLine("[ColorPickerService] Screen capture failed.");
                return;
            }

            // 4. Create and launch overlay window
            var overlay = new ColorPickerOverlayWindow(
                frozenSource,
                bgraPixels,
                vWidth,
                vHeight,
                vx,
                vy,
                onColorSelected: OnColorPicked);

            _activeOverlay = overlay;
            overlay.Closed += (_, _) =>
            {
                if (_activeOverlay == overlay)
                {
                    _activeOverlay = null;
                }
            };

            overlay.Show();
            overlay.Activate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ColorPickerService] StartColorPicker exception: {ex}");
        }
        finally
        {
            _isCapturing = false;
        }
    }

    public void CancelColorPicker()
    {
        if (_activeOverlay != null)
        {
            try
            {
                _activeOverlay.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ColorPickerService] Error closing overlay: {ex.Message}");
            }
            _activeOverlay = null;
        }
    }

    public void OnColorPicked(Color color, bool formatAsRgb)
    {
        string hexCode = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        string formattedCode = formatAsRgb
            ? $"rgb({color.R}, {color.G}, {color.B})"
            : hexCode;

        // Clipboard copy with retry loop
        TryCopyTextToClipboard(formattedCode);

        // Generate 64x64 frozen swatch thumbnail
        BitmapSource swatch = CreateColorSwatch(color, 64, 64);

        // Emit HUD payload for Dynamic Island feedback
        ColorPickedHudRequested?.Invoke(new HudPayload
        {
            Kind = HudKind.ColorPicked,
            Icon = IconsaxKind.Colorfilter,
            Title = hexCode,
            ValueText = "Đã chép mã màu",
            AccentHex = hexCode,
            Thumbnail = swatch
        });
    }

    public static bool TryCopyTextToClipboard(string text, int retries = 5, int delayMs = 40)
    {
        if (string.IsNullOrEmpty(text)) return false;

        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            return Application.Current.Dispatcher.Invoke(() => TryCopyTextToClipboard(text, retries, delayMs));
        }

        for (int i = 0; i < retries; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                if (i == retries - 1) return false;
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ColorPickerService] Clipboard copy error: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    public static BitmapSource CreateColorSwatch(Color color, int width = 64, int height = 64)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        byte b = color.B;
        byte g = color.G;
        byte r = color.R;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96, 96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        bitmap.Freeze();
        return bitmap;
    }

    public static Color GetPixelColor(byte[] bgraPixels, int vWidth, int vHeight, int localX, int localY)
    {
        if (localX < 0 || localX >= vWidth || localY < 0 || localY >= vHeight)
        {
            return Colors.Black;
        }

        int index = (localY * vWidth + localX) * 4;
        if (index + 2 >= bgraPixels.Length)
        {
            return Colors.Black;
        }

        byte b = bgraPixels[index + 0];
        byte g = bgraPixels[index + 1];
        byte r = bgraPixels[index + 2];
        return Color.FromRgb(r, g, b);
    }

    public static bool CaptureVirtualScreen(
        int vx, int vy, int vWidth, int vHeight,
        out byte[] bgraPixels,
        out BitmapSource frozenSource)
    {
        bgraPixels = Array.Empty<byte>();
        frozenSource = null!;

        IntPtr hScreenDC = NativeMethods.GetDC(IntPtr.Zero);
        if (hScreenDC == IntPtr.Zero) return false;

        IntPtr hMemoryDC = NativeMethods.CreateCompatibleDC(hScreenDC);
        if (hMemoryDC == IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hScreenDC);
            return false;
        }

        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hScreenDC, vWidth, vHeight);
        if (hBitmap == IntPtr.Zero)
        {
            NativeMethods.DeleteDC(hMemoryDC);
            NativeMethods.ReleaseDC(IntPtr.Zero, hScreenDC);
            return false;
        }

        IntPtr hOldBitmap = NativeMethods.SelectObject(hMemoryDC, hBitmap);
        bool bltSuccess = false;

        try
        {
            bltSuccess = NativeMethods.BitBlt(
                hMemoryDC,
                0, 0,
                vWidth, vHeight,
                hScreenDC,
                vx, vy,
                NativeMethods.SRCCOPY);
        }
        finally
        {
            // CRITICAL: Unselect hBitmap from hMemoryDC before calling GetDIBits!
            NativeMethods.SelectObject(hMemoryDC, hOldBitmap);
        }

        if (!bltSuccess)
        {
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(hMemoryDC);
            NativeMethods.ReleaseDC(IntPtr.Zero, hScreenDC);
            return false;
        }

        try
        {
            int stride = vWidth * 4;
            int totalBytes = stride * vHeight;
            byte[] rawPixels = new byte[totalBytes];

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = vWidth,
                    biHeight = -vHeight, // Top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                    biSizeImage = 0,
                    biXPelsPerMeter = 0,
                    biYPelsPerMeter = 0,
                    biClrUsed = 0,
                    biClrImportant = 0
                }
            };

            int linesCopied = NativeMethods.GetDIBits(
                hMemoryDC,
                hBitmap,
                0,
                (uint)vHeight,
                rawPixels,
                ref bmi,
                NativeMethods.DIB_RGB_COLORS);

            if (linesCopied == 0)
            {
                // Fallback to bottom-up DIB if driver rejects negative biHeight
                bmi.bmiHeader.biHeight = vHeight;
                linesCopied = NativeMethods.GetDIBits(
                    hMemoryDC,
                    hBitmap,
                    0,
                    (uint)vHeight,
                    rawPixels,
                    ref bmi,
                    NativeMethods.DIB_RGB_COLORS);

                if (linesCopied > 0)
                {
                    FlipVertical(rawPixels, vWidth, vHeight);
                }
            }

            if (linesCopied == 0)
            {
                // Secondary fallback: WPF Imaging interop
                var fallbackBmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                fallbackBmp.CopyPixels(rawPixels, stride, 0);
            }

            bgraPixels = rawPixels;

            var bmpSource = BitmapSource.Create(
                vWidth,
                vHeight,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                rawPixels,
                stride);

            bmpSource.Freeze();
            frozenSource = bmpSource;
            return true;
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(hMemoryDC);
            NativeMethods.ReleaseDC(IntPtr.Zero, hScreenDC);
        }
    }

    private static void FlipVertical(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        byte[] rowBuffer = new byte[stride];
        int half = height / 2;

        for (int y = 0; y < half; y++)
        {
            int topOffset = y * stride;
            int bottomOffset = (height - 1 - y) * stride;

            Buffer.BlockCopy(pixels, topOffset, rowBuffer, 0, stride);
            Buffer.BlockCopy(pixels, bottomOffset, pixels, topOffset, stride);
            Buffer.BlockCopy(rowBuffer, 0, pixels, bottomOffset, stride);
        }
    }

    public void Dispose()
    {
        CancelColorPicker();
    }
}
