using System.Diagnostics;
using System.Management;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinNotch.Interop;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class DisplayPowerService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _powerPollTimer;

    private MonitorDescriptor _targetMonitor;
    private ManagementEventWatcher? _wmiBrightnessWatcher;
    private bool _useWmiBrightness;
    private long _suppressWmiEventTicks;

    private bool? _lastChargingState;
    private int _lastWarnedBatteryThreshold = 100;

    public bool IsBrightnessSupported { get; private set; }
    public double BrightnessPercent { get; private set; } = 80.0;

    public bool HasBattery { get; private set; }
    public bool IsCharging { get; private set; }
    public int BatteryPercent { get; private set; } = 100;
    public string BatteryStatusDetail { get; private set; } = string.Empty;

    public bool IsCaffeineActive { get; private set; }

    public event Action? StateChanged;
    public event Action<HudPayload>? HudRequested;

    public DisplayPowerService(SettingsService settingsService, MonitorDescriptor initialMonitor)
    {
        _settingsService = settingsService;
        _targetMonitor = initialMonitor;

        _powerPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _powerPollTimer.Tick += (_, _) => PollPowerStatus(allowHud: true);
    }

    public void Start()
    {
        InitializeBrightnessBackend();
        PollPowerStatus(allowHud: false);

        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        _powerPollTimer.Start();
    }

    public void UpdateTargetMonitor(MonitorDescriptor monitor)
    {
        _targetMonitor = monitor;
        if (!_useWmiBrightness)
        {
            TryReadDdcBrightness();
        }
    }

    public void SetBrightnessPercent(double percent, bool showHud)
    {
        double clamped = Math.Round(Math.Clamp(percent, 0.0, 100.0));
        BrightnessPercent = clamped;
        _suppressWmiEventTicks = Stopwatch.GetTimestamp();

        byte targetVal = (byte)clamped;
        Task.Run(() =>
        {
            if (_useWmiBrightness)
            {
                TrySetWmiBrightness(targetVal);
            }
            else
            {
                TrySetDdcBrightness(targetVal);
            }
        });

        StateChanged?.Invoke();
        if (showHud)
        {
            EmitBrightnessHud();
        }
    }

    public void StepBrightness(int deltaPercent)
    {
        SetBrightnessPercent(BrightnessPercent + deltaPercent, showHud: true);
    }

    public void ToggleCaffeine(bool showHud = true)
    {
        SetCaffeine(!IsCaffeineActive, showHud);
    }

    public void SetCaffeine(bool active, bool showHud = true)
    {
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => SetCaffeine(active, showHud));
            return;
        }

        if (IsCaffeineActive == active)
        {
            if (showHud)
            {
                EmitCaffeineHud();
            }
            return;
        }

        IsCaffeineActive = active;

        try
        {
            if (active)
            {
                NativeMethods.SetThreadExecutionState(
                    NativeMethods.EXECUTION_STATE.ES_CONTINUOUS |
                    NativeMethods.EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                    NativeMethods.EXECUTION_STATE.ES_DISPLAY_REQUIRED);
            }
            else
            {
                NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS);
            }
        }
        catch { }

        StateChanged?.Invoke();

        if (showHud)
        {
            EmitCaffeineHud();
        }
    }

    private void EmitCaffeineHud()
    {
        HudRequested?.Invoke(new HudPayload
        {
            Kind = HudKind.Caffeine,
            Icon = IconsaxKind.Coffee,
            Title = "Giữ sáng màn hình",
            ValueText = IsCaffeineActive ? "Đang bật" : "Đã tắt",
            NormalizedValue = IsCaffeineActive ? 1.0 : 0.0,
            IsInteractiveSlider = false,
            AccentHex = "#F59E0B"
        });
    }

    private void InitializeBrightnessBackend()
    {
        if (TryReadWmiBrightness(out byte level))
        {
            _useWmiBrightness = true;
            IsBrightnessSupported = true;
            BrightnessPercent = level;
            StartWmiBrightnessWatcher();
        }
        else if (TryReadDdcBrightness())
        {
            _useWmiBrightness = false;
            IsBrightnessSupported = true;
        }
        else
        {
            IsBrightnessSupported = false;
        }

        StateChanged?.Invoke();
    }

    private static bool TryReadWmiBrightness(out byte brightness)
    {
        brightness = 80;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                brightness = Convert.ToByte(obj["CurrentBrightness"]);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void TrySetWmiBrightness(byte brightness)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject obj in searcher.Get().OfType<ManagementObject>())
            {
                obj.InvokeMethod("WmiSetBrightness", [1u, brightness]);
                break;
            }
        }
        catch { }
    }

    private void StartWmiBrightnessWatcher()
    {
        try
        {
            _wmiBrightnessWatcher = new ManagementEventWatcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessEvent");
            _wmiBrightnessWatcher.EventArrived += (_, args) =>
            {
                double elapsedMs = ((double)(Stopwatch.GetTimestamp() - _suppressWmiEventTicks) / Stopwatch.Frequency) * 1000.0;
                if (elapsedMs < 250.0) return;

                try
                {
                    byte level = Convert.ToByte(args.NewEvent["Brightness"]);
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        BrightnessPercent = level;
                        StateChanged?.Invoke();
                        EmitBrightnessHud();
                    });
                }
                catch { }
            };
            _wmiBrightnessWatcher.Start();
        }
        catch
        {
            _wmiBrightnessWatcher = null;
        }
    }

    private bool TryReadDdcBrightness()
    {
        IntPtr hMon = _targetMonitor.Handle;
        if (hMon == IntPtr.Zero) return false;

        try
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0)
                return false;

            var monitors = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors))
                return false;

            try
            {
                if (NativeMethods.GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                {
                    BrightnessPercent = Math.Round(((double)(cur - min) / (max - min)) * 100.0);
                    return true;
                }
            }
            finally
            {
                NativeMethods.DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch { }
        return false;
    }

    private void TrySetDdcBrightness(byte percent)
    {
        IntPtr hMon = _targetMonitor.Handle;
        if (hMon == IntPtr.Zero) return;

        try
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0)
                return;

            var monitors = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors))
                return;

            try
            {
                if (NativeMethods.GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                {
                    uint val = min + (uint)Math.Round((percent / 100.0) * (max - min));
                    NativeMethods.SetMonitorBrightness(monitors[0].hPhysicalMonitor, val);
                }
            }
            finally
            {
                NativeMethods.DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch { }
    }

    private void EmitBrightnessHud()
    {
        HudRequested?.Invoke(new HudPayload
        {
            Kind = HudKind.Brightness,
            Icon = IconsaxKind.Sun1,
            Title = "Độ sáng",
            ValueText = $"{BrightnessPercent:0}%",
            NormalizedValue = BrightnessPercent / 100.0,
            IsInteractiveSlider = true,
            AccentHex = "#FFD60A"
        });
    }

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => PollPowerStatus(allowHud: true));
        }
    }

    private void PollPowerStatus(bool allowHud)
    {
        if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.SYSTEM_POWER_STATUS status))
            return;

        // BatteryFlag == 128 means no system battery (desktop PC)
        bool hasBat = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent <= 100;
        bool charging = status.ACLineStatus == 1;
        int pct = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 100;

        HasBattery = hasBat;
        IsCharging = charging;
        BatteryPercent = pct;

        if (!hasBat)
        {
            BatteryStatusDetail = "Nguồn trực tiếp";
        }
        else if (charging)
        {
            BatteryStatusDetail = $"Đang sạc nguồn • {pct}%";
        }
        else if (status.BatteryLifeTime > 0 && (uint)status.BatteryLifeTime != 0xFFFFFFFF)
        {
            int hours = status.BatteryLifeTime / 3600;
            int minutes = (status.BatteryLifeTime % 3600) / 60;
            if (hours > 0 && minutes > 0)
            {
                BatteryStatusDetail = $"Pin {pct}% • Còn ~{hours}g {minutes}p";
            }
            else if (hours > 0)
            {
                BatteryStatusDetail = $"Pin {pct}% • Còn ~{hours}g";
            }
            else
            {
                BatteryStatusDetail = $"Pin {pct}% • Còn ~{Math.Max(1, minutes)}p";
            }
        }
        else
        {
            BatteryStatusDetail = $"Pin {pct}%";
        }

        StateChanged?.Invoke();

        if (!hasBat) return;

        if (charging)
        {
            _lastWarnedBatteryThreshold = 100;
        }

        if (allowHud && _settingsService.Current.EnableBatteryHud)
        {
            if (_lastChargingState.HasValue && _lastChargingState.Value != charging)
            {
                HudRequested?.Invoke(new HudPayload
                {
                    Kind = HudKind.Battery,
                    Icon = charging ? IconsaxKind.BatteryCharging : IconsaxKind.BatteryFull,
                    Title = charging ? "Đang sạc nguồn" : "Đang dùng pin",
                    ValueText = $"{pct}%",
                    NormalizedValue = pct / 100.0,
                    IsInteractiveSlider = false,
                    AccentHex = charging ? "#30D158" : "#FFFFFF"
                });
            }
            else if (!charging)
            {
                if (pct <= 10 && _lastWarnedBatteryThreshold > 10)
                {
                    _lastWarnedBatteryThreshold = 10;
                    HudRequested?.Invoke(new HudPayload
                    {
                        Kind = HudKind.Battery,
                        Icon = IconsaxKind.BatteryLow,
                        Title = "Pin yếu nghiêm trọng",
                        ValueText = $"{pct}%",
                        NormalizedValue = pct / 100.0,
                        IsInteractiveSlider = false,
                        AccentHex = "#FF453A"
                    });
                }
                else if (pct <= 20 && _lastWarnedBatteryThreshold > 20)
                {
                    _lastWarnedBatteryThreshold = 20;
                    HudRequested?.Invoke(new HudPayload
                    {
                        Kind = HudKind.Battery,
                        Icon = IconsaxKind.BatteryLow,
                        Title = "Pin yếu",
                        ValueText = $"{pct}%",
                        NormalizedValue = pct / 100.0,
                        IsInteractiveSlider = false,
                        AccentHex = "#FF9F0A"
                    });
                }
            }
        }

        _lastChargingState = charging;
    }

    public void Dispose()
    {
        try
        {
            NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS);
        }
        catch { }
        IsCaffeineActive = false;

        _powerPollTimer.Stop();
        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;

        if (_wmiBrightnessWatcher != null)
        {
            try
            {
                _wmiBrightnessWatcher.Stop();
                _wmiBrightnessWatcher.Dispose();
            }
            catch { }
            _wmiBrightnessWatcher = null;
        }
    }
}
