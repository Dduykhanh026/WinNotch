using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WinNotch.Interop;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class NotificationService : IDisposable
{
    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private sealed record AppBrandVisual(BitmapSource? Icon, string AccentHex);
    private sealed record FlashHistoryEntry(long TimestampTicks, string LastTitle);
    private sealed record RecentToastMarker(long TimestampTicks, string AppKey);

    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly DispatcherTimer _immediateDebounceTimer;

    // Pinned delegate reference to prevent GC collection of native Win32 callback
    private readonly NativeMethods.WinEventDelegate _winEventDelegate;

    private readonly HashSet<uint> _seenToastIds = [];
    private readonly Queue<uint> _seenToastOrder = [];
    private readonly ConcurrentDictionary<string, AppBrandVisual> _brandCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IntPtr, FlashHistoryEntry> _flashCooldownByHwnd = new();
    private readonly ConcurrentDictionary<string, IntPtr> _recentFlashedHwndByAppKey = new(StringComparer.OrdinalIgnoreCase);

    private UserNotificationListener? _listener;
    private bool _listenerAllowed;
    private bool _isSeeded;
    private uint _highWaterToastId;
    private IntPtr _winEventHook;
    private IntPtr _notchHwnd;
    private bool _isSessionLocked;
    private bool _disposed;
    private int _previewSampleIndex;
    private RecentToastMarker _lastToastMarker = new(0, string.Empty);

    public event Action<HudPayload, bool>? NotificationHudRequested;

    public NotificationService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _winEventDelegate = OnWinEventProc;

        _heartbeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.0)
        };
        _heartbeatTimer.Tick += async (_, _) => await PollNewToastsAsync();

        _immediateDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(55)
        };
        _immediateDebounceTimer.Tick += async (_, _) =>
        {
            _immediateDebounceTimer.Stop();
            await PollNewToastsAsync();
        };
    }

    public async void Start(IntPtr notchHwnd)
    {
        _notchHwnd = notchHwnd;

        try
        {
            _winEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_SHOW,
                NativeMethods.EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        }
        catch
        {
            _winEventHook = IntPtr.Zero;
        }

        try
        {
            _listener = UserNotificationListener.Current;
            if (_listener != null)
            {
                UserNotificationListenerAccessStatus status = await _listener.RequestAccessAsync();
                _listenerAllowed = status == UserNotificationListenerAccessStatus.Allowed;

                if (_listenerAllowed)
                {
                    await SeedExistingNotificationsAsync();
                    _heartbeatTimer.Start();
                }
            }
        }
        catch
        {
            _listenerAllowed = false;
        }
    }

    public async void SetSessionLocked(bool locked)
    {
        _isSessionLocked = locked;
        if (locked)
        {
            _immediateDebounceTimer.Stop();
            return;
        }

        // Silently advance baseline past lock-screen notifications so they don't collide with Face ID unlock
        if (_listenerAllowed)
        {
            await SeedExistingNotificationsAsync();
        }
    }

    public async void NotifyWindowFlashed(IntPtr flashingHwnd)
    {
        if (_disposed || _isSessionLocked || !_settingsService.Current.EnableNotificationHud)
            return;

        if (flashingHwnd == IntPtr.Zero || flashingHwnd == _notchHwnd)
            return;

        if (!NativeMethods.IsWindow(flashingHwnd) || !NativeMethods.IsWindowVisible(flashingHwnd))
            return;

        if (flashingHwnd == NativeMethods.GetForegroundWindow())
            return;

        if (ShouldSuppressByDoNotDisturb())
            return;

        string winTitle = ReadWindowTitle(flashingHwnd);
        long nowTicks = Stopwatch.GetTimestamp();

        if (_flashCooldownByHwnd.TryGetValue(flashingHwnd, out FlashHistoryEntry? prev))
        {
            double elapsedSec = (double)(nowTicks - prev.TimestampTicks) / Stopwatch.Frequency;
            bool titleChanged = !string.Equals(prev.LastTitle, winTitle, StringComparison.Ordinal);

            if (elapsedSec < 2.2 || (!titleChanged && elapsedSec < 8.5))
            {
                return;
            }
        }

        _flashCooldownByHwnd[flashingHwnd] = new FlashHistoryEntry(nowTicks, winTitle);

        (string appName, string exePath, string procStem) = InspectWindowProcess(flashingHwnd);
        if (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(winTitle))
            return;

        // Ignore Windows shell host windows themselves
        if (procStem is "shellexperiencehost" or "searchhost" or "startmenuexperiencehost" or "lockapp" or "winnotch")
            return;

        string appKey = NormalizeAppKey(!string.IsNullOrWhiteSpace(procStem) ? procStem : appName);
        if (!string.IsNullOrEmpty(appKey))
        {
            _recentFlashedHwndByAppKey[appKey] = flashingHwnd;
        }

        // Wait briefly & trigger Toast poll so if the app ALSO posted a rich Windows Toast, the rich Toast wins cleanly
        await Task.Delay(190);
        if (_disposed || _isSessionLocked)
            return;

        if (_listenerAllowed)
        {
            await PollNewToastsAsync();
        }

        RecentToastMarker recent = _lastToastMarker;
        if (recent.TimestampTicks != 0)
        {
            double sinceToastSec = (double)(Stopwatch.GetTimestamp() - recent.TimestampTicks) / Stopwatch.Frequency;
            if (sinceToastSec < 2.4 && KeysLikelyMatch(recent.AppKey, appKey, appName))
            {
                return;
            }
        }

        string cacheKey = !string.IsNullOrWhiteSpace(exePath) ? $"exe:{exePath}" : $"app:{appName}";
        AppBrandVisual brand = await ResolveAppBrandAsync(cacheKey, aumid: string.Empty, exePath: exePath, appInfo: null);

        string displayApp = string.IsNullOrWhiteSpace(appName) ? "Ứng dụng" : appName;
        string headerTitle = displayApp;
        string bodyLine = "Tin nhắn hoặc hoạt động mới";

        if (!string.IsNullOrWhiteSpace(winTitle) &&
            !string.Equals(winTitle, displayApp, StringComparison.OrdinalIgnoreCase))
        {
            headerTitle = CleanWindowTitle(winTitle, displayApp);
            bodyLine = $"Thông báo mới từ {displayApp}";
        }

        EmitNotificationHud(
            appName: displayApp,
            senderOrTitle: headerTitle,
            bodyText: bodyLine,
            brand: brand,
            sourceAumid: exePath,
            targetHwnd: flashingHwnd,
            forcePreemptExpanded: false);
    }

    public async void TriggerPreviewNotification(bool? overrideHidePreview = null)
    {
        int idx = Interlocked.Increment(ref _previewSampleIndex) % 3;

        (string sampleApp, string sampleSender, string sampleBody, string fallbackAccent, string probeAumid) = idx switch
        {
            1 => (
                "Tin nhắn • Zalo",
                "Nhóm Thiết Kế Sản Phẩm",
                "Bản giao diện Dynamic Island mới nhìn mượt quá, chiều nay chốt release luôn nhé!",
                "#0A84FF",
                @"Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!App"),
            2 => (
                "Lịch & Nhắc việc",
                "Họp tổng kết Sprint 16:30",
                "Phòng họp trực tuyến sẽ mở sau 10 phút nữa • Nhấp để xem nhanh",
                "#BF5AF2",
                @"windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"),
            _ => (
                "WinNotch Dynamic Island",
                "Hiệu ứng thông báo đã sẵn sàng",
                "Rê chuột giữ để đọc lâu hơn, nhấp vào thẻ để mở thẳng ứng dụng hoặc bấm × để đóng.",
                "#32D74B",
                Environment.ProcessPath ?? string.Empty)
        };

        AppBrandVisual brand = await ResolveAppBrandAsync(
            $"preview:{idx}:{probeAumid}",
            aumid: probeAumid.Contains('!') ? probeAumid : string.Empty,
            exePath: File.Exists(probeAumid) ? probeAumid : string.Empty,
            appInfo: null,
            preferredFallbackHex: fallbackAccent);

        // Keep distinct showcase brand accents so user sees the Dynamic Island rim glow shift colors!
        var showcaseBrand = new AppBrandVisual(brand.Icon, idx == 1 ? "#0A84FF" : idx == 2 ? "#BF5AF2" : brand.AccentHex);

        EmitNotificationHud(
            appName: sampleApp,
            senderOrTitle: sampleSender,
            bodyText: sampleBody,
            brand: showcaseBrand,
            sourceAumid: string.Empty,
            targetHwnd: IntPtr.Zero,
            forcePreemptExpanded: true,
            overrideHidePreview: overrideHidePreview);
    }

    public void ActivateNotification(HudPayload payload)
    {
        if (payload == null) return;

        try
        {
            // 1. Direct flashing or matched window handle
            if (payload.TargetWindowHandle != IntPtr.Zero && NativeMethods.IsWindow(payload.TargetWindowHandle))
            {
                FocusExistingWindow(payload.TargetWindowHandle);
                return;
            }

            // 2. Search open top-level visible windows matching SourceAumid / Subtitle (AppName)
            IntPtr matched = FindBestMatchingOpenWindow(payload.SourceAumid, payload.Subtitle);
            if (matched != IntPtr.Zero)
            {
                FocusExistingWindow(matched);
                return;
            }

            // 3. Launch via AUMID or executable path
            string aumid = payload.SourceAumid;
            if (!string.IsNullOrWhiteSpace(aumid))
            {
                if (File.Exists(aumid))
                {
                    Process.Start(new ProcessStartInfo(aumid) { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $@"shell:AppsFolder\{aumid}",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch
        {
            // Ignore shell activation errors if target app closed
        }
    }

    private void OnWinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || _isSessionLocked || !_listenerAllowed || !_settingsService.Current.EnableNotificationHud)
            return;

        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero || hwnd == _notchHwnd)
            return;

        string cls = ReadWindowClass(hwnd);
        if (cls is "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow")
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    _immediateDebounceTimer.Stop();
                    _immediateDebounceTimer.Start();
                }
            });
        }
    }

    private async Task SeedExistingNotificationsAsync()
    {
        if (_listener == null || !_listenerAllowed) return;

        await _pollGate.WaitAsync();
        try
        {
            IReadOnlyList<UserNotification> existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (UserNotification n in existing)
            {
                RememberToastId(n.Id);
                if (n.Id > _highWaterToastId)
                {
                    _highWaterToastId = n.Id;
                }
            }
            _isSeeded = true;
        }
        catch
        {
            _isSeeded = true;
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollNewToastsAsync()
    {
        if (_disposed || _isSessionLocked || !_listenerAllowed || _listener == null || !_isSeeded)
            return;

        if (!_pollGate.Wait(0))
            return;

        try
        {
            IReadOnlyList<UserNotification> toasts = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (toasts.Count == 0) return;

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            UserNotification? newestCandidate = null;

            foreach (UserNotification n in toasts)
            {
                if (_seenToastIds.Contains(n.Id))
                    continue;

                RememberToastId(n.Id);
                bool isAboveBaseline = n.Id > _highWaterToastId;
                if (n.Id > _highWaterToastId)
                {
                    _highWaterToastId = n.Id;
                }

                if (!isAboveBaseline)
                    continue;

                // Ignore stale synced notifications older than 20 seconds
                if ((nowUtc - n.CreationTime).TotalSeconds > 20.0)
                    continue;

                newestCandidate = n;
            }

            if (newestCandidate == null || !_settingsService.Current.EnableNotificationHud)
                return;

            if (ShouldSuppressByDoNotDisturb())
                return;

            await ProcessAndEmitToastAsync(newestCandidate);
        }
        catch
        {
            // WpnUserService may transiently restart during Windows updates
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task ProcessAndEmitToastAsync(UserNotification notif)
    {
        string aumid = string.Empty;
        string appName = string.Empty;
        AppInfo? appInfo = null;

        try { appInfo = notif.AppInfo; } catch { }
        try { aumid = appInfo?.AppUserModelId ?? string.Empty; } catch { }
        try { appName = appInfo?.DisplayInfo?.DisplayName ?? string.Empty; } catch { }

        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = DeriveFriendlyNameFromAumid(aumid);
        }

        // Ignore our own process if ever triggered
        if (aumid.Contains("WinNotch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(appName, "WinNotch", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var lines = new List<string>(4);
        try
        {
            NotificationBinding? binding = notif.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null)
            {
                foreach (AdaptiveNotificationText t in binding.GetTextElements())
                {
                    string cleaned = CleanSingleLine(t.Text);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        lines.Add(cleaned);
                    }
                }
            }
        }
        catch { }

        string senderOrTitle;
        string bodyText;

        if (lines.Count == 0)
        {
            senderOrTitle = appName;
            bodyText = "Đã nhận 1 thông báo mới";
        }
        else if (lines.Count == 1)
        {
            senderOrTitle = appName;
            bodyText = lines[0];
        }
        else
        {
            senderOrTitle = lines[0];
            bodyText = string.Join(" • ", lines.Skip(1));
        }

        string appKey = NormalizeAppKey(!string.IsNullOrWhiteSpace(appName) ? appName : aumid);
        _lastToastMarker = new RecentToastMarker(Stopwatch.GetTimestamp(), appKey);

        IntPtr targetHwnd = IntPtr.Zero;
        if (!string.IsNullOrEmpty(appKey) && _recentFlashedHwndByAppKey.TryGetValue(appKey, out IntPtr flashedHwnd))
        {
            if (NativeMethods.IsWindow(flashedHwnd))
            {
                targetHwnd = flashedHwnd;
            }
        }

        string exeHint = File.Exists(aumid) ? aumid : string.Empty;
        string cacheKey = !string.IsNullOrWhiteSpace(aumid) ? $"aumid:{aumid}" : $"app:{appName}";
        AppBrandVisual brand = await ResolveAppBrandAsync(cacheKey, aumid, exeHint, appInfo);

        EmitNotificationHud(
            appName: appName,
            senderOrTitle: senderOrTitle,
            bodyText: bodyText,
            brand: brand,
            sourceAumid: aumid,
            targetHwnd: targetHwnd,
            forcePreemptExpanded: false);
    }

    private void EmitNotificationHud(
        string appName,
        string senderOrTitle,
        string bodyText,
        AppBrandVisual brand,
        string sourceAumid,
        IntPtr targetHwnd,
        bool forcePreemptExpanded,
        bool? overrideHidePreview = null)
    {
        bool hidePreview = overrideHidePreview ?? _settingsService.Current.HideNotificationPreviewText;
        string finalTitle = hidePreview
            ? (string.IsNullOrWhiteSpace(appName) ? "Thông báo mới" : appName)
            : (string.IsNullOrWhiteSpace(senderOrTitle) ? appName : senderOrTitle);

        string finalBody = hidePreview
            ? "Đã nhận 1 thông báo mới"
            : (string.IsNullOrWhiteSpace(bodyText) ? "Thông báo mới" : bodyText);

        var payload = new HudPayload
        {
            Kind = HudKind.Notification,
            Icon = IconsaxKind.Notification,
            Title = finalTitle,
            Subtitle = string.IsNullOrWhiteSpace(appName) ? "Hệ thống" : appName,
            BodyText = finalBody,
            TimestampBadge = "Bây giờ",
            AccentHex = brand.AccentHex,
            Thumbnail = brand.Icon,
            SourceAumid = sourceAumid,
            TargetWindowHandle = targetHwnd,
            IsInteractiveSlider = false
        };

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                NotificationHudRequested?.Invoke(payload, forcePreemptExpanded);
            }
        });
    }

    private async Task<AppBrandVisual> ResolveAppBrandAsync(
        string cacheKey,
        string aumid,
        string exePath,
        AppInfo? appInfo,
        string preferredFallbackHex = "#38BDF8")
    {
        if (_brandCache.TryGetValue(cacheKey, out AppBrandVisual? cached))
        {
            return cached;
        }

        BitmapSource? icon = null;

        // 1. Shell AppsFolder lookup (handles UWP, MSIX, Edge, Chrome, Settings, Start-Menu Win32 AUMIDs)
        if (!string.IsNullOrWhiteSpace(aumid) && !File.Exists(aumid))
        {
            icon = await Task.Run(() => ExtractShellIcon($@"shell:AppsFolder\{aumid}", 64));
        }

        // 2. WinRT AppInfo DisplayInfo.GetLogo stream fallback
        if (icon == null && appInfo != null)
        {
            icon = await TryLoadAppInfoLogoAsync(appInfo);
        }

        // 3. Direct executable file icon fallback
        if (icon == null && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            icon = await Task.Run(() => ExtractShellIcon(exePath, 64));
        }

        string accentHex = icon != null
            ? ExtractVibrantBrandColorHex(icon, preferredFallbackHex)
            : preferredFallbackHex;

        var resolved = new AppBrandVisual(icon, accentHex);
        _brandCache[cacheKey] = resolved;
        return resolved;
    }

    private static async Task<BitmapSource?> TryLoadAppInfoLogoAsync(AppInfo appInfo)
    {
        try
        {
            RandomAccessStreamReference? logoRef = appInfo.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logoRef == null) return null;

            using IRandomAccessStreamWithContentType winrtStream = await logoRef.OpenReadAsync();
            if (winrtStream.Size == 0) return null;

            using Stream netStream = winrtStream.AsStreamForRead();
            using var mem = new MemoryStream();
            await netStream.CopyToAsync(mem);
            mem.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.StreamSource = mem;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? ExtractShellIcon(string parsingPath, int pixelSize)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            int hr = NativeMethods.SHCreateItemFromParsingName(
                parsingPath,
                IntPtr.Zero,
                ShellItemImageFactoryGuid,
                out NativeMethods.IShellItemImageFactory? factory);

            if (hr != 0 || factory == null) return null;

            hr = factory.GetImage(
                new NativeMethods.SIZE(pixelSize, pixelSize),
                NativeMethods.SIIGBF.SIIGBF_RESIZETOFIT | NativeMethods.SIIGBF.SIIGBF_BIGGERSIZEOK,
                out hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }
    }

    private static string ExtractVibrantBrandColorHex(BitmapSource source, string fallbackHex)
    {
        try
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            if (w <= 0 || h <= 0) return fallbackHex;

            int stride = w * 4;
            byte[] pixels = new byte[h * stride];
            bgra.CopyPixels(pixels, stride, 0);

            double sumR = 0, sumG = 0, sumB = 0, weightTotal = 0;
            int stepX = Math.Max(1, w / 20);
            int stepY = Math.Max(1, h / 20);

            for (int y = 0; y < h; y += stepY)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += stepX)
                {
                    int idx = row + (x * 4);
                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    byte a = pixels[idx + 3];

                    if (a < 96) continue;

                    byte max = Math.Max(r, Math.Max(g, b));
                    byte min = Math.Min(r, Math.Min(g, b));
                    if (max < 36) continue;
                    if (min > 232) continue;

                    double sat = max == 0 ? 0.0 : (double)(max - min) / max;
                    if (sat < 0.18) continue;

                    double wgt = (a / 255.0) * (0.35 + (sat * 1.65));
                    sumR += r * wgt;
                    sumG += g * wgt;
                    sumB += b * wgt;
                    weightTotal += wgt;
                }
            }

            if (weightTotal < 0.5)
                return fallbackHex;

            double avgR = sumR / weightTotal;
            double avgG = sumG / weightTotal;
            double avgB = sumB / weightTotal;

            // Boost luminosity for crisp Apple Dark-Mode OLED rim & aura contrast
            double peak = Math.Max(avgR, Math.Max(avgG, avgB));
            if (peak > 1.0 && peak < 195.0)
            {
                double boost = 205.0 / peak;
                avgR = Math.Min(255.0, avgR * boost);
                avgG = Math.Min(255.0, avgG * boost);
                avgB = Math.Min(255.0, avgB * boost);
            }

            byte fr = (byte)Math.Clamp((int)Math.Round(avgR), 45, 255);
            byte fg = (byte)Math.Clamp((int)Math.Round(avgG), 45, 255);
            byte fb = (byte)Math.Clamp((int)Math.Round(avgB), 45, 255);
            return $"#{fr:X2}{fg:X2}{fb:X2}";
        }
        catch
        {
            return fallbackHex;
        }
    }

    private bool ShouldSuppressByDoNotDisturb()
    {
        if (!_settingsService.Current.RespectWindowsDoNotDisturb)
            return false;

        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out NativeMethods.QUERY_USER_NOTIFICATION_STATE state) == 0)
            {
                return state is NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_QUIET_TIME
                    or NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                    or NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
                    or NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
            }
        }
        catch { }

        return false;
    }

    private void RememberToastId(uint id)
    {
        if (_seenToastIds.Add(id))
        {
            _seenToastOrder.Enqueue(id);
            while (_seenToastOrder.Count > 256)
            {
                uint oldest = _seenToastOrder.Dequeue();
                _seenToastIds.Remove(oldest);
            }
        }
    }

    private static void FocusExistingWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }
        else
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        }

        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private IntPtr FindBestMatchingOpenWindow(string sourceAumid, string appDisplayName)
    {
        string targetKey = NormalizeAppKey(appDisplayName);
        string aumidKey = NormalizeAppKey(DeriveFriendlyNameFromAumid(sourceAumid));
        string exePathTarget = File.Exists(sourceAumid) ? sourceAumid : string.Empty;

        IntPtr found = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == _notchHwnd || !NativeMethods.IsWindowVisible(hwnd))
                return true;

            if (NativeMethods.GetWindowTextLengthW(hwnd) <= 0)
                return true;

            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            (string winAppName, string winExe, string procStem) = InspectWindowProcess(hwnd);
            if (!string.IsNullOrEmpty(exePathTarget) &&
                string.Equals(exePathTarget, winExe, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            string winKey = NormalizeAppKey(!string.IsNullOrWhiteSpace(winAppName) ? winAppName : procStem);
            string stemKey = NormalizeAppKey(procStem);

            if ((!string.IsNullOrEmpty(targetKey) && (KeysLikelyMatch(targetKey, winKey, procStem) || KeysLikelyMatch(targetKey, stemKey, winAppName))) ||
                (!string.IsNullOrEmpty(aumidKey) && (KeysLikelyMatch(aumidKey, winKey, procStem) || KeysLikelyMatch(aumidKey, stemKey, winAppName))))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static (string AppName, string ExePath, string ProcessStem) InspectWindowProcess(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return (string.Empty, string.Empty, string.Empty);

            using Process proc = Process.GetProcessById((int)pid);
            string stem = (proc.ProcessName ?? string.Empty).Trim().ToLowerInvariant();
            string exePath = string.Empty;
            string friendly = string.Empty;

            try
            {
                exePath = proc.MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    friendly = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim() ?? string.Empty;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(friendly) && !string.IsNullOrWhiteSpace(proc.ProcessName))
            {
                friendly = proc.ProcessName;
            }

            return (friendly, exePath, stem);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(Math.Min(len + 2, 260));
        int read = NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        return read > 0 ? CleanSingleLine(sb.ToString()) : string.Empty;
    }

    private static string ReadWindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(96);
        int read = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return read > 0 ? sb.ToString() : string.Empty;
    }

    private static string CleanWindowTitle(string title, string appName)
    {
        string suffixDash = $" - {appName}";
        if (title.EndsWith(suffixDash, StringComparison.OrdinalIgnoreCase) && title.Length > suffixDash.Length)
        {
            return title[..^suffixDash.Length].Trim();
        }
        return title;
    }

    private static string CleanSingleLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return string.Join(' ', raw.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string DeriveFriendlyNameFromAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "Hệ thống";
        if (File.Exists(aumid)) return Path.GetFileNameWithoutExtension(aumid);

        string core = aumid;
        int bang = core.IndexOf('!');
        if (bang > 0) core = core[..bang];
        int under = core.IndexOf('_');
        if (under > 0) core = core[..under];

        string[] parts = core.Split(['.', '\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string p = parts[i];
            if (!p.Equals("App", StringComparison.OrdinalIgnoreCase) &&
                !p.Equals("Stable", StringComparison.OrdinalIgnoreCase) &&
                !p.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }

        return parts.Length > 0 ? parts[^1] : "Hệ thống";
    }

    private static string NormalizeAppKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    private static bool KeysLikelyMatch(string keyA, string keyB, string? altText)
    {
        if (string.IsNullOrEmpty(keyA)) return false;

        if (!string.IsNullOrEmpty(keyB) &&
            (keyA.Contains(keyB, StringComparison.Ordinal) || keyB.Contains(keyA, StringComparison.Ordinal)))
        {
            return true;
        }

        string altKey = NormalizeAppKey(altText);
        return !string.IsNullOrEmpty(altKey) &&
               (keyA.Contains(altKey, StringComparison.Ordinal) || altKey.Contains(keyA, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _heartbeatTimer.Stop();
        _immediateDebounceTimer.Stop();

        if (_winEventHook != IntPtr.Zero)
        {
            try { NativeMethods.UnhookWinEvent(_winEventHook); } catch { }
            _winEventHook = IntPtr.Zero;
        }

        _pollGate.Dispose();
    }
}
