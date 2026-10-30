using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using WinNotch.Interop;

namespace WinNotch.Services;

/// <summary>
/// 100% Offline Camera/Microphone Privacy Dots (ConsentStore Registry Monitor).
/// </summary>
public sealed class PrivacyService : IDisposable
{
    private const string ConsentStoreRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _pollTimer;

    public bool IsCameraActive { get; private set; }
    public bool IsMicrophoneActive { get; private set; }
    public string CameraActiveApp { get; private set; } = string.Empty;
    public string MicrophoneActiveApp { get; private set; } = string.Empty;

    public event Action? StateChanged;

    public PrivacyService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.0)
        };
        _pollTimer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        _pollTimer.Start();
    }

    private void Refresh()
    {
        if (_settingsService.Current.EnablePrivacyDots)
        {
            (bool camOn, string camApp) = CheckCapabilityActive("webcam");
            (bool micOn, string micApp) = CheckCapabilityActive("microphone");
            IsCameraActive = camOn;
            CameraActiveApp = camApp;
            IsMicrophoneActive = micOn;
            MicrophoneActiveApp = micApp;
        }
        else
        {
            IsCameraActive = false;
            IsMicrophoneActive = false;
            CameraActiveApp = string.Empty;
            MicrophoneActiveApp = string.Empty;
        }

        StateChanged?.Invoke();
    }

    private static (bool Active, string AppName) CheckCapabilityActive(string capability)
    {
        try
        {
            using RegistryKey? capKey = Registry.CurrentUser.OpenSubKey($@"{ConsentStoreRoot}\{capability}", writable: false);
            if (capKey == null) return (false, string.Empty);

            foreach (string subName in capKey.GetSubKeyNames())
            {
                if (string.Equals(subName, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using RegistryKey? nonPkg = capKey.OpenSubKey("NonPackaged", writable: false);
                    if (nonPkg == null) continue;

                    foreach (string exeKeyName in nonPkg.GetSubKeyNames())
                    {
                        using RegistryKey? appKey = nonPkg.OpenSubKey(exeKeyName, writable: false);
                        if (IsSubKeyCurrentlyInUse(appKey))
                        {
                            return (true, FormatRegistryAppName(exeKeyName));
                        }
                    }
                }
                else
                {
                    using RegistryKey? appKey = capKey.OpenSubKey(subName, writable: false);
                    if (IsSubKeyCurrentlyInUse(appKey))
                    {
                        return (true, FormatRegistryAppName(subName));
                    }
                }
            }
        }
        catch { }

        return (false, string.Empty);
    }

    private static bool IsSubKeyCurrentlyInUse(RegistryKey? appKey)
    {
        if (appKey == null) return false;
        object? startObj = appKey.GetValue("LastUsedTimeStart");
        object? stopObj = appKey.GetValue("LastUsedTimeStop");

        if (startObj is long start && stopObj is long stop)
        {
            return start > 0 && stop == 0;
        }
        return false;
    }

    private static string FormatRegistryAppName(string rawKey)
    {
        if (rawKey.Contains('#'))
        {
            string normalized = rawKey.Replace('#', '\\');
            string exeName = Path.GetFileNameWithoutExtension(normalized);
            return string.IsNullOrWhiteSpace(exeName) ? rawKey : exeName;
        }

        int underscore = rawKey.IndexOf('_');
        string clean = underscore > 0 ? rawKey[..underscore] : rawKey;
        int dot = clean.LastIndexOf('.');
        if (dot > 0 && dot < clean.Length - 1) clean = clean[(dot + 1)..];
        return clean;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
    }
}
