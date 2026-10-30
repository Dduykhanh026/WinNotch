using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class MediaService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _timelineTickTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    private string _lastTrackSignature = string.Empty;
    private bool _hasInitialReadCompleted;
    private DateTime _lastTimelineAnchorUtc = DateTime.UtcNow;
    private TimeSpan _anchorPosition = TimeSpan.Zero;

    public MediaSnapshot Snapshot { get; private set; } = MediaSnapshot.Empty;

    public event Action<MediaSnapshot>? MediaUpdated;
    public event Action<HudPayload>? TrackChangeHudRequested;

    public MediaService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _timelineTickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.0)
        };
        _timelineTickTimer.Tick += OnTimelineTick;
    }

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnSessionsChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            await BindBestSessionAsync();
            _timelineTickTimer.Start();
        }
        catch
        {
            // GSMTC service unavailable on stripped Windows editions
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _currentSession;
        if (session == null) return;
        try { await session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextTrackAsync()
    {
        var session = _currentSession;
        if (session == null) return;
        try { await session.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousTrackAsync()
    {
        var session = _currentSession;
        if (session == null) return;
        try { await session.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SeekToSecondsAsync(double seconds)
    {
        var session = _currentSession;
        if (session == null) return;
        try
        {
            var target = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
            _anchorPosition = target;
            _lastTimelineAnchorUtc = DateTime.UtcNow;
            await session.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch { }
    }

    public bool TryLaunchLastMediaApp()
    {
        string hint = _settingsService.Current.LastMediaAppHint;
        try
        {
            if (!string.IsNullOrWhiteSpace(hint))
            {
                if (hint.Contains('!'))
                {
                    // Packaged UWP/MSIX AUMID (e.g., SpotifyAB.SpotifyMusic_...!Spotify)
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $@"shell:AppsFolder\{hint}",
                        UseShellExecute = true
                    });
                    return true;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = hint,
                    UseShellExecute = true
                });
                return true;
            }

            // Smart fallback: Desktop Spotify if installed, else Windows Media Player URI
            string spotifyExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Spotify",
                "Spotify.exe");
            if (File.Exists(spotifyExe))
            {
                Process.Start(new ProcessStartInfo(spotifyExe) { UseShellExecute = true });
                return true;
            }

            Process.Start(new ProcessStartInfo("mswindowsmusic:") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        await BindBestSessionAsync();
    }

    private async Task BindBestSessionAsync()
    {
        if (_manager == null) return;

        await _refreshLock.WaitAsync();
        try
        {
            DetachCurrentSession();

            var sessions = _manager.GetSessions();
            GlobalSystemMediaTransportControlsSession? chosen = sessions.FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ?? _manager.GetCurrentSession()
                ?? sessions.FirstOrDefault();

            _currentSession = chosen;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnSessionDataChanged;
                _currentSession.PlaybackInfoChanged += OnSessionDataChanged;
                _currentSession.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        await RefreshSnapshotAsync();
    }

    private void DetachCurrentSession()
    {
        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged -= OnSessionDataChanged;
                _currentSession.PlaybackInfoChanged -= OnSessionDataChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            catch { }
            _currentSession = null;
        }
    }

    private async void OnSessionDataChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        await RefreshSnapshotAsync();
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            var tl = sender.GetTimelineProperties();
            if (tl != null)
            {
                TimeSpan dur = tl.EndTime > tl.StartTime ? tl.EndTime - tl.StartTime : TimeSpan.Zero;
                TimeSpan pos = tl.Position > tl.StartTime ? tl.Position - tl.StartTime : TimeSpan.Zero;
                _anchorPosition = pos;
                _lastTimelineAnchorUtc = DateTime.UtcNow;

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!Snapshot.HasSession) return;
                    Snapshot = new MediaSnapshot
                    {
                        HasSession = true,
                        IsPlaying = Snapshot.IsPlaying,
                        Title = Snapshot.Title,
                        Artist = Snapshot.Artist,
                        SourceAppId = Snapshot.SourceAppId,
                        SourceDisplayName = Snapshot.SourceDisplayName,
                        Position = pos,
                        Duration = dur,
                        AlbumArt = Snapshot.AlbumArt,
                        DominantColor = Snapshot.DominantColor
                    };
                    MediaUpdated?.Invoke(Snapshot);
                });
            }
        }
        catch { }
    }

    private async Task RefreshSnapshotAsync()
    {
        var session = _currentSession;
        if (session == null)
        {
            _lastTrackSignature = string.Empty;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Snapshot = MediaSnapshot.Empty;
                MediaUpdated?.Invoke(Snapshot);
            });
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            string title = props?.Title?.Trim() ?? string.Empty;
            string artist = props?.Artist?.Trim() ?? string.Empty;
            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            string appId = session.SourceAppUserModelId ?? string.Empty;
            string friendlyApp = FormatSourceAppName(appId);

            if (string.IsNullOrWhiteSpace(title) && !isPlaying)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Snapshot = MediaSnapshot.Empty;
                    MediaUpdated?.Invoke(Snapshot);
                });
                return;
            }

            if (!string.IsNullOrWhiteSpace(appId))
            {
                _settingsService.UpdateLastMediaApp(appId, friendlyApp);
            }

            TimeSpan duration = timeline != null && timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            TimeSpan position = timeline != null && timeline.Position > timeline.StartTime
                ? timeline.Position - timeline.StartTime
                : TimeSpan.Zero;

            _anchorPosition = position;
            _lastTimelineAnchorUtc = DateTime.UtcNow;

            (BitmapSource? art, Color dominant) = await LoadAlbumArtAndColorAsync(props?.Thumbnail);

            var nextSnapshot = new MediaSnapshot
            {
                HasSession = true,
                IsPlaying = isPlaying,
                Title = string.IsNullOrWhiteSpace(title) ? friendlyApp : title,
                Artist = string.IsNullOrWhiteSpace(artist) ? friendlyApp : artist,
                SourceAppId = appId,
                SourceDisplayName = friendlyApp,
                Position = position,
                Duration = duration,
                AlbumArt = art,
                DominantColor = dominant
            };

            string sig = $"{nextSnapshot.Title}|{nextSnapshot.Artist}";
            bool isNewTrack = _hasInitialReadCompleted &&
                              !string.IsNullOrWhiteSpace(nextSnapshot.Title) &&
                              !string.Equals(sig, _lastTrackSignature, StringComparison.Ordinal) &&
                              isPlaying;

            _lastTrackSignature = sig;
            _hasInitialReadCompleted = true;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Snapshot = nextSnapshot;
                MediaUpdated?.Invoke(Snapshot);

                if (isNewTrack && _settingsService.Current.EnableTrackChangeHud)
                {
                    TrackChangeHudRequested?.Invoke(new HudPayload
                    {
                        Kind = HudKind.TrackChange,
                        Icon = IconsaxKind.Music,
                        Title = nextSnapshot.Title,
                        ValueText = nextSnapshot.Artist,
                        NormalizedValue = 1.0,
                        IsInteractiveSlider = false,
                        AccentHex = $"#{dominant.R:X2}{dominant.G:X2}{dominant.B:X2}",
                        Thumbnail = art
                    });
                }
            });
        }
        catch
        {
            // Session closed mid-query
        }
    }

    private void OnTimelineTick(object? sender, EventArgs e)
    {
        if (!Snapshot.HasSession || !Snapshot.IsPlaying || Snapshot.Duration <= TimeSpan.Zero)
            return;

        TimeSpan elapsed = DateTime.UtcNow - _lastTimelineAnchorUtc;
        TimeSpan estimated = _anchorPosition + elapsed;
        if (estimated > Snapshot.Duration) estimated = Snapshot.Duration;

        Snapshot = new MediaSnapshot
        {
            HasSession = Snapshot.HasSession,
            IsPlaying = Snapshot.IsPlaying,
            Title = Snapshot.Title,
            Artist = Snapshot.Artist,
            SourceAppId = Snapshot.SourceAppId,
            SourceDisplayName = Snapshot.SourceDisplayName,
            Position = estimated,
            Duration = Snapshot.Duration,
            AlbumArt = Snapshot.AlbumArt,
            DominantColor = Snapshot.DominantColor
        };
        MediaUpdated?.Invoke(Snapshot);
    }

    private static async Task<(BitmapSource? Image, Color DominantColor)> LoadAlbumArtAndColorAsync(IRandomAccessStreamReference? thumbRef)
    {
        Color fallback = Color.FromRgb(88, 134, 255);
        if (thumbRef == null) return (null, fallback);

        try
        {
            using IRandomAccessStreamWithContentType winrtStream = await thumbRef.OpenReadAsync();
            using var mem = new MemoryStream();
            await winrtStream.AsStreamForRead().CopyToAsync(mem);
            mem.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 128;
            bmp.StreamSource = mem;
            bmp.EndInit();
            bmp.Freeze();

            Color dominant = ExtractVibrantDominantColor(bmp, fallback);
            return (bmp, dominant);
        }
        catch
        {
            return (null, fallback);
        }
    }

    private static Color ExtractVibrantDominantColor(BitmapSource source, Color fallback)
    {
        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            if (w <= 0 || h <= 0) return fallback;

            byte[] pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);

            long sumR = 0, sumG = 0, sumB = 0, weightSum = 0;
            int step = Math.Max(1, (w * h) / 250);

            for (int i = 0; i < pixels.Length - 4; i += 4 * step)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int sat = max - min;

                // Favor saturated mid-bright pixels so Ambient Glow looks lush instead of muddy gray
                if (max < 35 || min > 235) continue;
                int weight = 1 + (sat / 16);

                sumR += r * weight;
                sumG += g * weight;
                sumB += b * weight;
                weightSum += weight;
            }

            if (weightSum == 0) return fallback;

            byte fr = (byte)Math.Clamp((sumR / weightSum) + 18, 45, 245);
            byte fg = (byte)Math.Clamp((sumG / weightSum) + 18, 45, 245);
            byte fb = (byte)Math.Clamp((sumB / weightSum) + 18, 45, 245);
            return Color.FromRgb(fr, fg, fb);
        }
        catch
        {
            return fallback;
        }
    }

    private static string FormatSourceAppName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "Trình phát nhạc";
        string lower = appId.ToLowerInvariant();
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("msedge") || lower.Contains("edge")) return "Microsoft Edge";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("applemusic")) return "Apple Music";
        if (lower.Contains("vlc")) return "VLC Media Player";
        if (lower.Contains("zune") || lower.Contains("mediaplayer")) return "Windows Media Player";

        string name = Path.GetFileNameWithoutExtension(appId.Split('!')[0]);
        return string.IsNullOrWhiteSpace(name) ? "Trình phát nhạc" : name;
    }

    public void Dispose()
    {
        _timelineTickTimer.Stop();
        DetachCurrentSession();
        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnSessionsChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
    }
}
