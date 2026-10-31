using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WinNotch.Interop;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class InputHookService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly NativeMethods.LowLevelKeyboardProc _hookCallback;
    private readonly DispatcherTimer _pointerSensorTimer;

    private IntPtr _hookHandle;
    private MonitorDescriptor _targetMonitor;

    private bool _wasLeftButtonDown;
    private NativeMethods.POINT _mouseDownOrigin;
    private bool _dragCandidateStartedOutsideTop;
    private bool _isDragZoneTriggered;
    private bool _isGhostActive;
    private bool _isAltNDown;
    private bool _isAltMDown;
    private bool _isAltCDown;
    private bool _isCapsLockDown;

    public event Action? ToggleHotkeyPressed;
    public event Action? MicMuteHotkeyPressed;
    public event Action? ColorPickerHotkeyPressed;
    public event Action<bool>? CapsLockToggled;
    public event Action<int, bool>? VolumeKeyIntercepted; // (deltaPercent, toggleMute)
    public event Action<bool>? GhostModeChanged;
    public event Action<bool>? DragSensorZoneChanged;

    public InputHookService(SettingsService settingsService, MonitorDescriptor initialMonitor)
    {
        _settingsService = settingsService;
        _targetMonitor = initialMonitor;
        _hookCallback = HookProc;

        _pointerSensorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _pointerSensorTimer.Tick += OnPointerSensorTick;
    }

    public void Start()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            IntPtr hMod = NativeMethods.GetModuleHandleW(null);
            _hookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _hookCallback, hMod, 0);
        }
        _pointerSensorTimer.Start();
    }

    public void UpdateTargetMonitor(MonitorDescriptor monitor)
    {
        _targetMonitor = monitor;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            bool isKeyUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            if (isKeyUp)
            {
                if (kb.vkCode == NativeMethods.VK_M) _isAltMDown = false;
                else if (kb.vkCode == NativeMethods.VK_C) _isAltCDown = false;
                else if (kb.vkCode == NativeMethods.VK_N) _isAltNDown = false;
                else if (kb.vkCode == NativeMethods.VK_MENU)
                {
                    _isAltMDown = false;
                    _isAltCDown = false;
                    _isAltNDown = false;
                }
                else if (kb.vkCode == NativeMethods.VK_CAPITAL)
                {
                    _isCapsLockDown = false;
                }
            }

            if (isKeyDown)
            {
                bool isAltPressed = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0 || (kb.flags & 0x20) != 0;

                // 1. Global Hotkey Alt + N (Toggle Expand with auto-repeat guard)
                if (kb.vkCode == NativeMethods.VK_N && isAltPressed)
                {
                    if (!_isAltNDown)
                    {
                        _isAltNDown = true;
                        Application.Current?.Dispatcher.BeginInvoke(() => ToggleHotkeyPressed?.Invoke());
                    }
                    return new IntPtr(1);
                }

                // 2. Global Hotkey Alt + M (System Microphone Mute Toggle)
                if (kb.vkCode == NativeMethods.VK_M && isAltPressed)
                {
                    if (!_isAltMDown)
                    {
                        _isAltMDown = true;
                        Application.Current?.Dispatcher.BeginInvoke(() => MicMuteHotkeyPressed?.Invoke());
                    }
                    return new IntPtr(1);
                }

                // 3. Global Hotkey Alt + C (Screen Color Picker)
                if (kb.vkCode == NativeMethods.VK_C && isAltPressed)
                {
                    if (!_isAltCDown)
                    {
                        _isAltCDown = true;
                        Application.Current?.Dispatcher.BeginInvoke(() => ColorPickerHotkeyPressed?.Invoke());
                    }
                    return new IntPtr(1);
                }

                // 4. Caps Lock Status Notification (VK_CAPITAL)
                // Note: Do NOT swallow the key (allow CallNextHookEx to propagate to OS and apps)
                if (kb.vkCode == NativeMethods.VK_CAPITAL)
                {
                    if (!_isCapsLockDown)
                    {
                        _isCapsLockDown = true;
                        if (_settingsService.Current.EnableCapsLockHud)
                        {
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                short state = NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL);
                                bool isToggledOn = (state & 0x0001) != 0;
                                CapsLockToggled?.Invoke(isToggledOn);
                            });
                        }
                    }
                }

                // 5. Windows 11 Volume OSD Replacement (user-mode WH_KEYBOARD_LL)
                AppSettings cfg = _settingsService.Current;
                if (cfg.ReplaceSystemVolumeOsd)
                {
                    if (kb.vkCode == NativeMethods.VK_VOLUME_UP)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() => VolumeKeyIntercepted?.Invoke(+2, false));
                        return new IntPtr(1);
                    }
                    if (kb.vkCode == NativeMethods.VK_VOLUME_DOWN)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() => VolumeKeyIntercepted?.Invoke(-2, false));
                        return new IntPtr(1);
                    }
                    if (kb.vkCode == NativeMethods.VK_VOLUME_MUTE)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() => VolumeKeyIntercepted?.Invoke(0, true));
                        return new IntPtr(1);
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void OnPointerSensorTick(object? sender, EventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT pt)) return;

        AppSettings cfg = _settingsService.Current;
        NativeMethods.RECT mon = _targetMonitor.PhysicalBounds;
        double scaleX = _targetMonitor.DpiScaleX;
        double scaleY = _targetMonitor.DpiScaleY;
        double centerX = mon.Left + (mon.Width * 0.5);

        // 1. Ctrl-Hold Ghost Pass-Through Mode (protects underlying browser tabs)
        bool ctrlDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        bool inGhostZone = Math.Abs(pt.X - centerX) <= (365 * scaleX) &&
                           pt.Y >= mon.Top &&
                           pt.Y <= mon.Top + (268 * scaleY);

        bool nextGhost = cfg.EnableCtrlGhostPassthrough && ctrlDown && (inGhostZone || _isGhostActive);
        if (!ctrlDown) nextGhost = false;

        if (nextGhost != _isGhostActive)
        {
            _isGhostActive = nextGhost;
            GhostModeChanged?.Invoke(_isGhostActive);
        }

        // 2. Top-Center OLE Drag-In Sensor (420 x 70 DIPs)
        bool leftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        double startThresholdY = mon.Top + (75 * scaleY);

        if (leftDown && !_wasLeftButtonDown)
        {
            _mouseDownOrigin = pt;
            // Only arm drag sensor if drag originated outside the top tab bar zone
            _dragCandidateStartedOutsideTop = pt.Y > startThresholdY || Math.Abs(pt.X - centerX) > (240 * scaleX);
        }
        else if (!leftDown && _wasLeftButtonDown)
        {
            _dragCandidateStartedOutsideTop = false;
            if (_isDragZoneTriggered)
            {
                _isDragZoneTriggered = false;
                DragSensorZoneChanged?.Invoke(false);
            }
        }

        _wasLeftButtonDown = leftDown;

        if (leftDown && _dragCandidateStartedOutsideTop && !_isGhostActive)
        {
            double movedDistSq = Math.Pow(pt.X - _mouseDownOrigin.X, 2) + Math.Pow(pt.Y - _mouseDownOrigin.Y, 2);
            bool inEnterSensor = Math.Abs(pt.X - centerX) <= (210 * scaleX) &&
                                 pt.Y >= mon.Top &&
                                 pt.Y <= mon.Top + (70 * scaleY);
            bool inKeepSensor = Math.Abs(pt.X - centerX) <= (250 * scaleX) &&
                                pt.Y >= mon.Top &&
                                pt.Y <= mon.Top + (125 * scaleY);

            if (!_isDragZoneTriggered && movedDistSq > (22 * 22) && inEnterSensor)
            {
                _isDragZoneTriggered = true;
                DragSensorZoneChanged?.Invoke(true);
            }
            else if (_isDragZoneTriggered && !inKeepSensor)
            {
                _isDragZoneTriggered = false;
                DragSensorZoneChanged?.Invoke(false);
            }
        }
    }

    public void Dispose()
    {
        _pointerSensorTimer.Stop();
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
