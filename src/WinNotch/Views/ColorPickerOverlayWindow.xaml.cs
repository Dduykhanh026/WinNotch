using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Interop;

namespace WinNotch.Views;

public partial class ColorPickerOverlayWindow : Window
{
    private readonly BitmapSource _screenSnapshot;
    private readonly byte[] _bgraPixels;
    private readonly int _physWidth;
    private readonly int _physHeight;
    private readonly int _virtualLeft;
    private readonly int _virtualTop;
    private readonly Action<Color, bool>? _onColorSelected;

    private readonly byte[] _gridBuffer = new byte[11 * 11 * 4];
    private readonly WriteableBitmap _magnifierBitmap = new(11, 11, 96, 96, PixelFormats.Bgra32, null);

    private Color _currentSampledColor = Colors.White;
    private bool _isCompleted;

    public ColorPickerOverlayWindow(
        BitmapSource screenSnapshot,
        byte[] bgraPixels,
        int physWidth,
        int physHeight,
        int virtualLeft,
        int virtualTop,
        Action<Color, bool>? onColorSelected = null)
    {
        InitializeComponent();

        _screenSnapshot = screenSnapshot;
        _bgraPixels = bgraPixels;
        _physWidth = physWidth;
        _physHeight = physHeight;
        _virtualLeft = virtualLeft;
        _virtualTop = virtualTop;
        _onColorSelected = onColorSelected;

        // Position window to cover entire VirtualScreen canvas in DIPs
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        ScreenFreezeImage.Source = _screenSnapshot;
        MagnifierImage.Source = _magnifierBitmap;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        // Set ToolWindow and Topmost, but omit WS_EX_NOACTIVATE so we capture keyboard (Esc, Shift, Arrows)
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        Activate();
        Focus();

        // Sample initial position
        UpdateSampling();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        UpdateSampling();
    }

    private void UpdateSampling()
    {
        if (_isCompleted) return;

        // 1. Physical Cursor Coordinates
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT pt)) return;

        int px = pt.X - _virtualLeft;
        int py = pt.Y - _virtualTop;

        px = Math.Clamp(px, 0, _physWidth - 1);
        py = Math.Clamp(py, 0, _physHeight - 1);

        // 2. Sample Center Pixel Color
        int centerIndex = (py * _physWidth + px) * 4;
        if (centerIndex + 3 < _bgraPixels.Length)
        {
            byte b = _bgraPixels[centerIndex + 0];
            byte g = _bgraPixels[centerIndex + 1];
            byte r = _bgraPixels[centerIndex + 2];
            _currentSampledColor = Color.FromRgb(r, g, b);

            // Update Swatch & Text Readout
            SwatchBrush.Color = _currentSampledColor;
            HexCodeText.Text = $"#{r:X2}{g:X2}{b:X2}";
            RgbCodeText.Text = $"RGB({r}, {g}, {b})";

            // 3. Dynamic Contrast Calculation (ITU-R BT.601 Perceived Luminance)
            double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            bool isDark = luminance < 0.5;

            Brush contrastBrush = isDark ? Brushes.White : Brushes.Black;
            Brush backingBrush = isDark ? Brushes.Black : Brushes.White;

            ReticleInner.BorderBrush = contrastBrush;
            ReticleOuter.BorderBrush = backingBrush;
            TickTop.Stroke = contrastBrush;
            TickBottom.Stroke = contrastBrush;
            TickLeft.Stroke = contrastBrush;
            TickRight.Stroke = contrastBrush;
        }

        // 4. Extract 11x11 Grid into WriteableBitmap
        ExtractMagnifierGrid(px, py);

        // 5. Update Floating Card Position with Boundary Repulsion
        Point cursorDips = Mouse.GetPosition(this);
        UpdateCardPosition(cursorDips);
    }

    private void ExtractMagnifierGrid(int centerX, int centerY)
    {
        int stride = _physWidth * 4;

        for (int row = 0; row < 11; row++)
        {
            int sy = Math.Clamp(centerY + (row - 5), 0, _physHeight - 1);
            int srcRowOffset = sy * stride;
            int dstRowOffset = row * 11 * 4;

            for (int col = 0; col < 11; col++)
            {
                int sx = Math.Clamp(centerX + (col - 5), 0, _physWidth - 1);
                int srcIndex = srcRowOffset + (sx * 4);
                int dstIndex = dstRowOffset + (col * 4);

                _gridBuffer[dstIndex + 0] = _bgraPixels[srcIndex + 0]; // Blue
                _gridBuffer[dstIndex + 1] = _bgraPixels[srcIndex + 1]; // Green
                _gridBuffer[dstIndex + 2] = _bgraPixels[srcIndex + 2]; // Red
                _gridBuffer[dstIndex + 3] = 255;                      // Alpha
            }
        }

        _magnifierBitmap.WritePixels(new Int32Rect(0, 0, 11, 11), _gridBuffer, 11 * 4, 0);
    }

    private void UpdateCardPosition(Point cursorDips)
    {
        double cx = cursorDips.X;
        double cy = cursorDips.Y;

        double winW = ActualWidth > 0 ? ActualWidth : Width;
        double winH = ActualHeight > 0 ? ActualHeight : Height;

        // Default: 20 DIPs to right and below cursor
        double targetX = cx + 20.0;
        if (targetX + 140.0 > winW - 12.0)
        {
            // Repel from right edge: flip to left of cursor
            targetX = cx - 140.0 - 20.0;
        }

        double targetY = cy + 20.0;
        if (targetY + 168.0 > winH - 12.0)
        {
            // Repel from bottom edge: flip above cursor
            targetY = cy - 168.0 - 20.0;
        }

        double clampedX = Math.Clamp(targetX, 12.0, Math.Max(12.0, winW - 140.0 - 12.0));
        double clampedY = Math.Clamp(targetY, 12.0, Math.Max(12.0, winH - 168.0 - 12.0));

        Canvas.SetLeft(MagnifierCard, clampedX);
        Canvas.SetTop(MagnifierCard, clampedY);
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ConfirmPick(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void OnWindowMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPicker();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelPicker();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            UpdateShiftVisualFeedback(true);
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            ConfirmPick(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        // 1-Pixel Nudging with Arrow Keys
        int dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left: dx = -1; break;
            case Key.Right: dx = 1; break;
            case Key.Up: dy = -1; break;
            case Key.Down: dy = 1; break;
        }

        if (dx != 0 || dy != 0)
        {
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
            {
                NativeMethods.SetCursorPos(pt.X + dx, pt.Y + dy);
                UpdateSampling();
            }
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            UpdateShiftVisualFeedback(false);
        }
    }

    private void UpdateShiftVisualFeedback(bool isShiftActive)
    {
        var primaryBrush = (Brush)FindResource("TextPrimaryBrush");
        var secondaryBrush = (Brush)FindResource("TextSecondaryBrush");

        if (isShiftActive)
        {
            HexCodeText.Foreground = secondaryBrush;
            HexCodeText.FontWeight = FontWeights.Normal;
            RgbCodeText.Foreground = primaryBrush;
            RgbCodeText.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            HexCodeText.Foreground = primaryBrush;
            HexCodeText.FontWeight = FontWeights.SemiBold;
            RgbCodeText.Foreground = secondaryBrush;
            RgbCodeText.FontWeight = FontWeights.Regular;
        }
    }

    private void ConfirmPick(bool isShiftActive)
    {
        if (_isCompleted) return;
        _isCompleted = true;

        _onColorSelected?.Invoke(_currentSampledColor, isShiftActive);
        CloseCleanly();
    }

    private void CancelPicker()
    {
        if (_isCompleted) return;
        _isCompleted = true;

        CloseCleanly();
    }

    private void CloseCleanly()
    {
        ScreenFreezeImage.Source = null;
        MagnifierImage.Source = null;
        Close();
    }
}
