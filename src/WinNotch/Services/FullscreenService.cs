using System.Text;
using System.Windows.Threading;
using WinNotch.Interop;

namespace WinNotch.Services;

/// <summary>
/// Auto-Hide Taskbar &amp; 125% Per-Monitor DPI safe Fullscreen Detector (SPEC Section 3.3).
/// </summary>
public sealed class FullscreenService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _pollTimer;
    private readonly StringBuilder _classBuffer = new(128);

    private MonitorDescriptor _targetMonitor;
    private IntPtr _ownHwnd;
    private bool _isFullscreen;

    public event Action<bool>? FullscreenStateChanged;

    public FullscreenService(SettingsService settingsService, MonitorDescriptor targetMonitor)
    {
        _settingsService = settingsService;
        _targetMonitor = targetMonitor;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _pollTimer.Tick += (_, _) => Evaluate();
    }

    public void Start(IntPtr ownNotchHwnd)
    {
        _ownHwnd = ownNotchHwnd;
        _pollTimer.Start();
    }

    public void UpdateTargetMonitor(MonitorDescriptor monitor)
    {
        _targetMonitor = monitor;
        Evaluate();
    }

    public void RefreshNow() => Evaluate();

    private void Evaluate()
    {
        if (!_settingsService.Current.EnableFullscreenHide)
        {
            UpdateState(false);
            return;
        }

        bool detected = DetectFullscreenOnTargetMonitor();
        UpdateState(detected);
    }

    private bool DetectFullscreenOnTargetMonitor()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _ownHwnd || !NativeMethods.IsWindowVisible(fg))
        {
            return false;
        }

        // Ignore Windows Desktop & Shell surfaces (Progman / WorkerW / Taskbar)
        _classBuffer.Clear();
        if (NativeMethods.GetClassNameW(fg, _classBuffer, _classBuffer.Capacity) > 0)
        {
            string cls = _classBuffer.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "LockScreenBackstopFrame")
            {
                return false;
            }
        }

        IntPtr fgMonitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (_targetMonitor.Handle != IntPtr.Zero && fgMonitor != _targetMonitor.Handle)
        {
            return false;
        }

        // Condition 1: Exclusive D3D Fullscreen or Presentation Mode
        if (NativeMethods.SHQueryUserNotificationState(out var quns) == 0)
        {
            if (quns is NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                     or NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
            {
                return true;
            }
        }

        // Condition 2: True borderless fullscreen (F11, YouTube Fullscreen, VLC, Borderless Game)
        // Maximized desktop apps (including frameless Electron/Chromium apps on Auto-Hide Taskbar)
        // carry WS_MAXIMIZE + WS_THICKFRAME with over-padded WindowRect (e.g. -8,-8), whereas true
        // fullscreen windows strip WS_THICKFRAME/WS_MAXIMIZE and match exact physical monitor bounds.
        long style = NativeMethods.GetWindowLongPtr(fg, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_MAXIMIZE) != 0)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(fg, out NativeMethods.RECT winRect))
        {
            return false;
        }

        NativeMethods.RECT mon = _targetMonitor.PhysicalBounds;
        const int tol = 1;
        bool exactMonitorBounds =
            Math.Abs(winRect.Left - mon.Left) <= tol &&
            Math.Abs(winRect.Top - mon.Top) <= tol &&
            Math.Abs(winRect.Right - mon.Right) <= tol &&
            Math.Abs(winRect.Bottom - mon.Bottom) <= tol;

        bool hasResizableCaptionFrame =
            (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION &&
            (style & NativeMethods.WS_THICKFRAME) != 0;

        return exactMonitorBounds && !hasResizableCaptionFrame;
    }

    private void UpdateState(bool next)
    {
        if (_isFullscreen == next) return;
        _isFullscreen = next;
        FullscreenStateChanged?.Invoke(_isFullscreen);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
    }
}
