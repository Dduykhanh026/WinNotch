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

    public event Action? ToggleHotkeyPressed;
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
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            if (isKeyDown)
            {
                // 1. Global Hotkey Alt + N
                if (kb.vkCode == NativeMethods.VK_N && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => ToggleHotkeyPressed?.Invoke());
                    return new IntPtr(1);
                }

                // 2. Windows 11 Volume OSD Replacement (user-mode WH_KEYBOARD_LL)
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
