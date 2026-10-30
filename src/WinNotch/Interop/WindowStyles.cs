using System.Runtime.InteropServices;

namespace WinNotch.Interop;

public sealed class MonitorDescriptor
{
    public IntPtr Handle { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public string FriendlyLabel { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public NativeMethods.RECT PhysicalBounds { get; init; }
    public double DpiScaleX { get; init; } = 1.0;
    public double DpiScaleY { get; init; } = 1.0;
}

internal static class WindowStyles
{
    public static void ApplyNotchOverlayStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero) return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long updated = clickThrough
            ? (exStyle | NativeMethods.WS_EX_TRANSPARENT)
            : (exStyle & ~NativeMethods.WS_EX_TRANSPARENT);

        if (updated != exStyle)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(updated));
        }
    }

    public static List<MonitorDescriptor> GetMonitors()
    {
        var result = new List<MonitorDescriptor>();
        int idx = 1;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFOEXW { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
            if (NativeMethods.GetMonitorInfoW(hMon, ref info))
            {
                uint dpiX = 96, dpiY = 96;
                if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out uint dy) == 0 && dx > 0)
                {
                    dpiX = dx;
                    dpiY = dy;
                }

                bool isPrimary = (info.dwFlags & 1u) != 0;
                string label = isPrimary
                    ? $"Màn hình chính #{idx} ({info.rcMonitor.Width}×{info.rcMonitor.Height})"
                    : $"Màn hình #{idx} ({info.rcMonitor.Width}×{info.rcMonitor.Height})";

                result.Add(new MonitorDescriptor
                {
                    Handle = hMon,
                    DeviceName = info.szDevice,
                    FriendlyLabel = label,
                    IsPrimary = isPrimary,
                    PhysicalBounds = info.rcMonitor,
                    DpiScaleX = dpiX / 96.0,
                    DpiScaleY = dpiY / 96.0
                });
                idx++;
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static MonitorDescriptor ResolveTargetMonitor(string? preferredDeviceName)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            return new MonitorDescriptor
            {
                DeviceName = "PRIMARY",
                FriendlyLabel = "Màn hình chính",
                IsPrimary = true,
                PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
                DpiScaleX = 1.25,
                DpiScaleY = 1.25
            };
        }

        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }
}
