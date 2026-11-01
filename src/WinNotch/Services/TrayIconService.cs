using System.IO;
using System.Windows;
using WinNotch.Engine;
using WinNotch.Models;
using WinNotch.Views;

namespace WinNotch.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotchStateMachine _stateMachine;
    private readonly DisplayPowerService? _displayPowerService;
    private readonly AudioService? _audioService;
    private readonly ColorPickerService? _colorPickerService;
    private readonly Action _openSettingsAction;
    private readonly Action _exitAppAction;

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _appIcon;
    private TrayPopupWindow? _popupWindow;
    private bool _isDisposed;

    public bool IsCaffeineChecked { get; private set; }
    public bool IsMicMutedChecked { get; private set; }
    public bool IsManuallyHiddenChecked { get; private set; }

    public TrayIconService(
        NotchStateMachine stateMachine,
        Action openSettingsAction,
        Action exitAppAction)
        : this(stateMachine, null, null, null, openSettingsAction, exitAppAction)
    {
    }

    public TrayIconService(
        NotchStateMachine stateMachine,
        DisplayPowerService? displayPowerService,
        AudioService? audioService,
        ColorPickerService? colorPickerService,
        Action openSettingsAction,
        Action exitAppAction)
    {
        _stateMachine = stateMachine;
        _displayPowerService = displayPowerService;
        _audioService = audioService;
        _colorPickerService = colorPickerService;
        _openSettingsAction = openSettingsAction;
        _exitAppAction = exitAppAction;

        if (_displayPowerService != null)
        {
            _displayPowerService.StateChanged += OnDisplayPowerStateChanged;
        }
        if (_audioService != null)
        {
            _audioService.StateChanged += OnAudioStateChanged;
        }
    }

    public void Start()
    {
        if (_isDisposed)
            return;

        _appIcon = LoadEmbeddedAppIcon();
        SyncLiveStateOnUiThread();

        // ContextMenuStrip is intentionally null so right-click opens our native dark frosted WPF TrayPopupWindow
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _appIcon ?? System.Drawing.SystemIcons.Application,
            Text = "WinNotch — Dynamic Notch (Alt + N)",
            ContextMenuStrip = null,
            Visible = true
        };

        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        _notifyIcon.DoubleClick += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(() => _stateMachine.ToggleExpandViaHotkey());
    }

    public TrayPopupWindow EnsurePopupWindow()
    {
        if (_popupWindow == null)
        {
            _popupWindow = new TrayPopupWindow(
                toggleNotchAction: () => _stateMachine.ToggleExpandViaHotkey(),
                openAgentsAction: () => _stateMachine.ExpandAndPin(NotchTab.Agents),
                openSystemAction: () => _stateMachine.ExpandAndPin(NotchTab.System),
                openShelfAction: () => _stateMachine.ExpandAndPin(NotchTab.FileShelf),
                toggleHideAction: TriggerToggleHide,
                toggleCaffeineAction: TriggerToggleCaffeine,
                toggleMicMuteAction: TriggerToggleMicMute,
                colorPickerAction: () => _colorPickerService?.StartColorPicker(),
                openSettingsAction: _openSettingsAction,
                exitAppAction: _exitAppAction);
        }

        SyncLiveStateOnUiThread();
        return _popupWindow;
    }

    public void TriggerToggleHide()
    {
        _stateMachine.IsManuallyHidden = !_stateMachine.IsManuallyHidden;
        SyncLiveStateOnUiThread();
    }

    public void TriggerToggleCaffeine()
    {
        _displayPowerService?.ToggleCaffeine(showHud: true);
        SyncLiveStateOnUiThread();
    }

    public void TriggerToggleMicMute()
    {
        _audioService?.ToggleMicrophoneMute(showHud: true);
        SyncLiveStateOnUiThread();
    }

    public void SyncLiveState()
    {
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(SyncLiveStateOnUiThread);
        }
        else
        {
            SyncLiveStateOnUiThread();
        }
    }

    private void SyncLiveStateOnUiThread()
    {
        IsManuallyHiddenChecked = _stateMachine.IsManuallyHidden;
        IsCaffeineChecked = _displayPowerService?.IsCaffeineActive ?? false;
        IsMicMutedChecked = _audioService?.IsMicMuted ?? false;

        _popupWindow?.SyncLiveStates(IsManuallyHiddenChecked, IsCaffeineChecked, IsMicMutedChecked);
    }

    private void OnNotifyIconMouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button != System.Windows.Forms.MouseButtons.Right || _isDisposed)
            return;

        if (Application.Current != null)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (_isDisposed) return;
                TrayPopupWindow popup = EnsurePopupWindow();
                popup.ShowAtCursor();
            });
        }
    }

    private void OnDisplayPowerStateChanged() => SyncLiveState();

    private void OnAudioStateChanged() => SyncLiveState();

    private static System.Drawing.Icon? LoadEmbeddedAppIcon()
    {
        try
        {
            var resInfo = Application.GetResourceStream(new Uri("pack://application:,,,/WinNotch;component/Assets/app.ico"));
            if (resInfo?.Stream != null)
            {
                using Stream s = resInfo.Stream;
                return new System.Drawing.Icon(s);
            }
        }
        catch { }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch { }

        return null;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        if (_displayPowerService != null)
        {
            _displayPowerService.StateChanged -= OnDisplayPowerStateChanged;
        }
        if (_audioService != null)
        {
            _audioService.StateChanged -= OnAudioStateChanged;
        }

        if (_popupWindow != null)
        {
            TrayPopupWindow popup = _popupWindow;
            _popupWindow = null;
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                Application.Current.Dispatcher.BeginInvoke(popup.DisposeClose);
            else
                popup.DisposeClose();
        }

        if (_notifyIcon != null)
        {
            _notifyIcon.MouseUp -= OnNotifyIconMouseUp;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _appIcon?.Dispose();
        _appIcon = null;
    }
}
