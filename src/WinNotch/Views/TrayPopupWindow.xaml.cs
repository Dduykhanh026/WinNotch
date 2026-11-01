using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WinNotch.Views;

public partial class TrayPopupWindow : Window
{
    private readonly Action _toggleNotchAction;
    private readonly Action _openAgentsAction;
    private readonly Action _openSystemAction;
    private readonly Action _openShelfAction;
    private readonly Action _toggleHideAction;
    private readonly Action _toggleCaffeineAction;
    private readonly Action _toggleMicMuteAction;
    private readonly Action _colorPickerAction;
    private readonly Action _openSettingsAction;
    private readonly Action _exitAppAction;
    private bool _isDisposing;

    public bool IsManuallyHiddenActive { get; private set; }
    public bool IsCaffeineActive { get; private set; }
    public bool IsMicMutedActive { get; private set; }

    public TrayPopupWindow(
        Action toggleNotchAction,
        Action openAgentsAction,
        Action openSystemAction,
        Action openShelfAction,
        Action toggleHideAction,
        Action toggleCaffeineAction,
        Action toggleMicMuteAction,
        Action colorPickerAction,
        Action openSettingsAction,
        Action exitAppAction)
    {
        InitializeComponent();
        _toggleNotchAction = toggleNotchAction;
        _openAgentsAction = openAgentsAction;
        _openSystemAction = openSystemAction;
        _openShelfAction = openShelfAction;
        _toggleHideAction = toggleHideAction;
        _toggleCaffeineAction = toggleCaffeineAction;
        _toggleMicMuteAction = toggleMicMuteAction;
        _colorPickerAction = colorPickerAction;
        _openSettingsAction = openSettingsAction;
        _exitAppAction = exitAppAction;
    }

    public void SyncLiveStates(bool isManuallyHidden, bool isCaffeineActive, bool isMicMuted)
    {
        IsManuallyHiddenActive = isManuallyHidden;
        IsCaffeineActive = isCaffeineActive;
        IsMicMutedActive = isMicMuted;

        ManuallyHiddenDot.Visibility = isManuallyHidden ? Visibility.Visible : Visibility.Collapsed;
        CaffeineStatusDot.Visibility = isCaffeineActive ? Visibility.Visible : Visibility.Collapsed;
        MicMuteStatusDot.Visibility = isMicMuted ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowAtCursor()
    {
        if (_isDisposing)
            return;

        System.Drawing.Point cursorPhys = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(cursorPhys);
        System.Drawing.Rectangle workPhys = screen.WorkingArea;

        Measure(new Size(Width, double.PositiveInfinity));
        double popupW = Width;
        double popupH = DesiredSize.Height > 40.0 ? DesiredSize.Height : 348.0;

        double scaleX = 1.0;
        double scaleY = 1.0;
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0.1) scaleX = dpi.DpiScaleX;
            if (dpi.DpiScaleY > 0.1) scaleY = dpi.DpiScaleY;
        }
        catch { }

        Point pos = CalculateClampedDipPosition(
            cursorPhys.X,
            cursorPhys.Y,
            workPhys,
            scaleX,
            scaleY,
            popupW,
            popupH);

        Left = pos.X;
        Top = pos.Y;

        Show();
        Activate();
        Focus();
    }

    public static Point CalculateClampedDipPosition(
        int cursorPhysX,
        int cursorPhysY,
        System.Drawing.Rectangle workAreaPhys,
        double dpiScaleX,
        double dpiScaleY,
        double popupDipWidth,
        double popupDipHeight)
    {
        double sx = Math.Max(0.5, dpiScaleX);
        double sy = Math.Max(0.5, dpiScaleY);

        double cursorX = cursorPhysX / sx;
        double cursorY = cursorPhysY / sy;

        double workLeft = workAreaPhys.Left / sx;
        double workTop = workAreaPhys.Top / sy;
        double workRight = workAreaPhys.Right / sx;
        double workBottom = workAreaPhys.Bottom / sy;

        const double margin = 8.0;

        // Default: center-right above the cursor near bottom taskbar, clamped strictly inside WorkingArea
        double targetX = cursorX - (popupDipWidth * 0.65);
        double targetY = cursorY - popupDipHeight - 8.0;

        // If taskbar is at the top of the screen, place below cursor instead
        if (targetY < workTop + margin)
        {
            targetY = cursorY + 8.0;
        }

        double clampedX = Math.Clamp(targetX, workLeft + margin, Math.Max(workLeft + margin, workRight - popupDipWidth - margin));
        double clampedY = Math.Clamp(targetY, workTop + margin, Math.Max(workTop + margin, workBottom - popupDipHeight - margin));

        return new Point(Math.Round(clampedX), Math.Round(clampedY));
    }

    public void DisposeClose()
    {
        _isDisposing = true;
        try { Close(); } catch { }
    }

    private void DismissPopup()
    {
        if (_isDisposing) return;
        try { Hide(); } catch { }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => DismissPopup();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DismissPopup();
            e.Handled = true;
        }
    }

    private void ExecuteAndDismiss(Action action)
    {
        DismissPopup();
        action();
    }

    private void OnToggleNotchClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_toggleNotchAction);
    private void OnOpenAgentsClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_openAgentsAction);
    private void OnOpenSystemClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_openSystemAction);
    private void OnOpenShelfClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_openShelfAction);
    private void OnToggleHideClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_toggleHideAction);
    private void OnToggleCaffeineClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_toggleCaffeineAction);
    private void OnToggleMicMuteClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_toggleMicMuteAction);
    private void OnColorPickerClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_colorPickerAction);
    private void OnSettingsClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_openSettingsAction);
    private void OnExitClick(object sender, RoutedEventArgs e) => ExecuteAndDismiss(_exitAppAction);
}
