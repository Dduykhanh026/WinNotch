using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
using WinNotch.Interop;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class AudioService : IMMNotificationClient, IDisposable
{
    private const int FftExponent = 10; // 2^10 = 1024 samples
    private const int FftLength = 1 << FftExponent;

    private readonly SettingsService _settingsService;
    private readonly object _deviceLock = new();

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _activeDevice;
    private MMDevice? _activeCaptureDevice;
    private WasapiLoopbackCapture? _loopbackCapture;

    private readonly float[] _sampleRing = new float[FftLength];
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private int _ringWritePos;
    private int _samplesSinceLastFft;
    private double _runningPeak = 0.045;
    private readonly double[] _smoothedBars = [0.12, 0.12, 0.12, 0.12];
    private long _lastFftEmitTicks;
    private long _suppressCallbackTicks;
    private long _suppressCaptureCallbackTicks;
    private long _selfSwitchingOutputTicks;
    private string _lastRenderDeviceId = string.Empty;
    private bool _hasInitialRenderAttach;

    private bool _isMediaPlayingGate;
    private bool _disposed;

    public double VolumePercent { get; private set; } = 50.0;
    public bool IsMuted { get; private set; }
    public string OutputDeviceName { get; private set; } = "Loa hệ thống";
    public int ActiveOutputDeviceCount { get; private set; } = 1;

    public bool HasMicrophone { get; private set; }
    public bool IsMicMuted { get; private set; }
    public string MicrophoneDeviceName { get; private set; } = "Không có Micro";

    public event Action? StateChanged;
    public event Action<HudPayload>? VolumeHudRequested;
    public event Action<double, double, double, double>? FftBarsUpdated;

    public AudioService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Start()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
            AttachDefaultEndpoint(emitConnectionHud: false);
            AttachDefaultCaptureEndpoint();
        }
        catch
        {
            OutputDeviceName = "Không tìm thấy thiết bị âm thanh";
        }
    }

    public void SetMediaPlayingGate(bool isMediaPlaying)
    {
        if (_isMediaPlayingGate == isMediaPlaying) return;
        _isMediaPlayingGate = isMediaPlaying;
        SyncFftCaptureState();
    }

    public void StepVolume(int deltaPercent, bool toggleMute)
    {
        try
        {
            MMDevice? dev;
            lock (_deviceLock) dev = _activeDevice;
            if (dev == null) return;

            _suppressCallbackTicks = Stopwatch.GetTimestamp();

            if (toggleMute)
            {
                bool nextMute = !dev.AudioEndpointVolume.Mute;
                dev.AudioEndpointVolume.Mute = nextMute;
                IsMuted = nextMute;
            }
            else
            {
                float current = dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                float next = Math.Clamp(current + (deltaPercent / 100f), 0f, 1f);
                dev.AudioEndpointVolume.MasterVolumeLevelScalar = next;
                if (deltaPercent > 0 && dev.AudioEndpointVolume.Mute)
                {
                    dev.AudioEndpointVolume.Mute = false;
                }
                VolumePercent = Math.Round(next * 100.0);
                IsMuted = dev.AudioEndpointVolume.Mute;
            }

            StateChanged?.Invoke();
            EmitVolumeHud();
        }
        catch
        {
            // Device may be switching
        }
    }

    public void SetVolumePercent(double percent, bool showHud)
    {
        try
        {
            MMDevice? dev;
            lock (_deviceLock) dev = _activeDevice;
            if (dev == null) return;

            _suppressCallbackTicks = Stopwatch.GetTimestamp();
            float scalar = (float)Math.Clamp(percent / 100.0, 0.0, 1.0);
            dev.AudioEndpointVolume.MasterVolumeLevelScalar = scalar;
            if (scalar > 0.005f && dev.AudioEndpointVolume.Mute)
            {
                dev.AudioEndpointVolume.Mute = false;
            }

            VolumePercent = Math.Round(scalar * 100.0);
            IsMuted = dev.AudioEndpointVolume.Mute;

            StateChanged?.Invoke();
            if (showHud) EmitVolumeHud();
        }
        catch
        {
            // Ignore transient endpoint error
        }
    }

    public void ToggleMute(bool showHud)
    {
        try
        {
            MMDevice? dev;
            lock (_deviceLock) dev = _activeDevice;
            if (dev == null) return;

            _suppressCallbackTicks = Stopwatch.GetTimestamp();
            bool nextMute = !dev.AudioEndpointVolume.Mute;
            dev.AudioEndpointVolume.Mute = nextMute;
            IsMuted = nextMute;

            StateChanged?.Invoke();
            if (showHud)
            {
                EmitVolumeHud();
            }
        }
        catch
        {
            // Ignore transient endpoint error
        }
    }

    public bool CycleOutputDevice(bool showHud = true)
    {
        try
        {
            MMDeviceEnumerator? enumerator = _enumerator;
            if (enumerator == null) return false;

            var endpoints = new List<(string Id, string Name)>();
            foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    endpoints.Add((endpoint.ID, endpoint.FriendlyName));
                }
                catch { }
                finally
                {
                    try { endpoint.Dispose(); } catch { }
                }
            }

            ActiveOutputDeviceCount = Math.Max(1, endpoints.Count);
            if (endpoints.Count <= 1)
            {
                StateChanged?.Invoke();
                if (showHud && endpoints.Count == 1)
                {
                    EmitAudioDeviceHud();
                }
                return false;
            }

            endpoints.Sort((a, b) =>
            {
                int cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
            });

            string currentId;
            lock (_deviceLock)
            {
                currentId = _activeDevice?.ID ?? _lastRenderDeviceId;
            }

            int currentIndex = endpoints.FindIndex(e => string.Equals(e.Id, currentId, StringComparison.OrdinalIgnoreCase));
            int nextIndex = (currentIndex + 1) % endpoints.Count;
            string targetId = endpoints[nextIndex].Id;

            _selfSwitchingOutputTicks = Stopwatch.GetTimestamp();

            object? comObj = null;
            try
            {
                comObj = new NativeMethods.PolicyConfigClient();
                if (comObj is NativeMethods.IPolicyConfig policyConfig)
                {
                    policyConfig.SetDefaultEndpoint(targetId, (int)Role.Console);
                    policyConfig.SetDefaultEndpoint(targetId, (int)Role.Multimedia);
                    policyConfig.SetDefaultEndpoint(targetId, (int)Role.Communications);
                }
                else
                {
                    return false;
                }
            }
            finally
            {
                if (comObj != null && Marshal.IsComObject(comObj))
                {
                    Marshal.ReleaseComObject(comObj);
                }
            }

            AttachDefaultEndpoint(emitConnectionHud: false);
            if (showHud)
            {
                EmitAudioDeviceHud();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ToggleMicrophoneMute(bool showHud = true)
    {
        try
        {
            MMDeviceEnumerator? enumerator = _enumerator;
            if (enumerator == null) return;

            bool nextMute;
            bool anyApplied = false;

            lock (_deviceLock)
            {
                if (_activeCaptureDevice == null)
                {
                    AttachDefaultCaptureEndpointUnsafe();
                }

                if (_activeCaptureDevice != null)
                {
                    try
                    {
                        _ = _activeCaptureDevice.AudioEndpointVolume.Mute;
                    }
                    catch
                    {
                        AttachDefaultCaptureEndpointUnsafe();
                    }
                }

                if (_activeCaptureDevice == null)
                {
                    HasMicrophone = false;
                    if (showHud)
                    {
                        EmitMicrophoneHud();
                    }
                    return;
                }

                nextMute = !_activeCaptureDevice.AudioEndpointVolume.Mute;
                _suppressCaptureCallbackTicks = Stopwatch.GetTimestamp();
                _activeCaptureDevice.AudioEndpointVolume.Mute = nextMute;
                IsMicMuted = nextMute;
                anyApplied = true;
            }

            // Synchronize mute across all active physical/virtual capture endpoints so no meeting app leaks audio
            try
            {
                foreach (MMDevice cap in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    try
                    {
                        cap.AudioEndpointVolume.Mute = nextMute;
                        anyApplied = true;
                    }
                    catch { }
                    finally
                    {
                        try { cap.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            if (anyApplied)
            {
                void Notify()
                {
                    StateChanged?.Invoke();
                    if (showHud)
                    {
                        EmitMicrophoneHud();
                    }
                }

                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.BeginInvoke(Notify);
                }
                else
                {
                    Notify();
                }
            }
        }
        catch
        {
            // Ignore transient capture endpoint error
        }
    }

    private void AttachDefaultEndpoint(bool emitConnectionHud = true)
    {
        bool deviceChanged = false;

        lock (_deviceLock)
        {
            DetachEndpointUnsafe();

            try
            {
                if (_enumerator != null)
                {
                    try
                    {
                        var renderList = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                        ActiveOutputDeviceCount = Math.Max(1, renderList.Count);
                        foreach (MMDevice d in renderList)
                        {
                            try { d.Dispose(); } catch { }
                        }
                    }
                    catch
                    {
                        ActiveOutputDeviceCount = 1;
                    }
                }

                if (_enumerator != null && _enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    _activeDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    string newId = _activeDevice.ID ?? string.Empty;
                    deviceChanged = _hasInitialRenderAttach && !string.Equals(newId, _lastRenderDeviceId, StringComparison.OrdinalIgnoreCase);
                    _lastRenderDeviceId = newId;
                    _hasInitialRenderAttach = true;

                    OutputDeviceName = _activeDevice.FriendlyName;
                    VolumePercent = Math.Round(_activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0);
                    IsMuted = _activeDevice.AudioEndpointVolume.Mute;
                    _activeDevice.AudioEndpointVolume.OnVolumeNotification += OnEndpointVolumeNotification;
                }
                else
                {
                    OutputDeviceName = "Không có loa đầu ra";
                }
            }
            catch
            {
                OutputDeviceName = "Thiết bị âm thanh mặc định";
            }
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StateChanged?.Invoke();
            SyncFftCaptureState();
            if (emitConnectionHud && deviceChanged && _settingsService.Current.EnableAudioDeviceHud)
            {
                EmitAudioDeviceHud();
            }
        });
    }

    private void AttachDefaultCaptureEndpoint()
    {
        lock (_deviceLock)
        {
            AttachDefaultCaptureEndpointUnsafe();
        }

        Application.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke());
    }

    private void AttachDefaultCaptureEndpointUnsafe()
    {
        DetachCaptureEndpointUnsafe();

        try
        {
            if (_enumerator == null)
            {
                HasMicrophone = false;
                IsMicMuted = false;
                MicrophoneDeviceName = "Không có Micro";
                return;
            }

            MMDevice? capture = null;
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                capture = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            else if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
            {
                capture = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            else if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                capture = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }

            if (capture != null)
            {
                _activeCaptureDevice = capture;
                HasMicrophone = true;
                MicrophoneDeviceName = capture.FriendlyName;
                IsMicMuted = capture.AudioEndpointVolume.Mute;
                capture.AudioEndpointVolume.OnVolumeNotification += OnCaptureVolumeNotification;
            }
            else
            {
                HasMicrophone = false;
                IsMicMuted = false;
                MicrophoneDeviceName = "Không có Micro";
            }
        }
        catch
        {
            HasMicrophone = false;
            IsMicMuted = false;
            MicrophoneDeviceName = "Không có Micro";
        }
    }

    private void DetachEndpointUnsafe()
    {
        StopFftCaptureUnsafe();

        if (_activeDevice != null)
        {
            try
            {
                _activeDevice.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolumeNotification;
                _activeDevice.Dispose();
            }
            catch { }
            _activeDevice = null;
        }
    }

    private void DetachCaptureEndpointUnsafe()
    {
        if (_activeCaptureDevice != null)
        {
            try
            {
                _activeCaptureDevice.AudioEndpointVolume.OnVolumeNotification -= OnCaptureVolumeNotification;
                _activeCaptureDevice.Dispose();
            }
            catch { }
            _activeCaptureDevice = null;
        }
    }

    private void OnEndpointVolumeNotification(AudioVolumeNotificationData data)
    {
        double elapsedMs = ((double)(Stopwatch.GetTimestamp() - _suppressCallbackTicks) / Stopwatch.Frequency) * 1000.0;
        bool wasSelfTriggered = elapsedMs < 180.0;

        double newPct = Math.Round(data.MasterVolume * 100.0);
        bool newMute = data.Muted;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            bool changed = Math.Abs(VolumePercent - newPct) >= 0.5 || IsMuted != newMute;
            VolumePercent = newPct;
            IsMuted = newMute;
            StateChanged?.Invoke();

            if (!wasSelfTriggered && changed)
            {
                EmitVolumeHud();
            }
        });
    }

    private void EmitVolumeHud()
    {
        IconsaxKind icon = IsMuted
            ? IconsaxKind.VolumeSlash
            : VolumePercent <= 0.5
                ? IconsaxKind.VolumeMute
                : VolumePercent < 50
                    ? IconsaxKind.VolumeLow
                    : IconsaxKind.VolumeHigh;

        VolumeHudRequested?.Invoke(new HudPayload
        {
            Kind = HudKind.Volume,
            Icon = icon,
            Title = OutputDeviceName,
            ValueText = IsMuted ? "Tắt tiếng" : $"{VolumePercent:0}%",
            NormalizedValue = IsMuted ? 0.0 : VolumePercent / 100.0,
            IsInteractiveSlider = true,
            IsMuted = IsMuted,
            AccentHex = IsMuted ? "#FF453A" : "#FFFFFF"
        });
    }

    private void EmitAudioDeviceHud()
    {
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(EmitAudioDeviceHud);
            return;
        }

        string devName = CleanAudioDeviceName(OutputDeviceName);
        bool isHeadphone = IsHeadphoneDevice(OutputDeviceName, devName);

        VolumeHudRequested?.Invoke(new HudPayload
        {
            Kind = HudKind.AudioDevice,
            Icon = isHeadphone ? IconsaxKind.Headphone : IconsaxKind.VolumeHigh,
            Title = devName,
            ValueText = IsMuted ? "Tắt tiếng" : $"{VolumePercent:0}%",
            NormalizedValue = IsMuted ? 0.0 : VolumePercent / 100.0,
            IsInteractiveSlider = false,
            IsMuted = IsMuted,
            AccentHex = IsMuted ? "#FF453A" : "#60A5FA"
        });
    }

    public static string CleanAudioDeviceName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Loa hệ thống";
        }

        int paren = raw.IndexOf('(');
        string clean = paren > 0 ? raw[..paren].Trim() : raw.Trim();
        return string.IsNullOrWhiteSpace(clean) ? raw.Trim() : clean;
    }

    public static bool IsHeadphoneDevice(string? rawName, string? cleanName = null)
    {
        string combined = $"{rawName} {cleanName}";
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return combined.Contains("airpod", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("bud", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("head", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("tai nghe", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("earphone", StringComparison.OrdinalIgnoreCase);
    }

    private void OnCaptureVolumeNotification(AudioVolumeNotificationData data)
    {
        double elapsedMs = ((double)(Stopwatch.GetTimestamp() - _suppressCaptureCallbackTicks) / Stopwatch.Frequency) * 1000.0;
        bool wasSelfTriggered = elapsedMs < 180.0;

        bool newMute = data.Muted;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            bool changed = IsMicMuted != newMute;
            IsMicMuted = newMute;
            StateChanged?.Invoke();

            if (!wasSelfTriggered && changed)
            {
                EmitMicrophoneHud();
            }
        });
    }

    private void EmitMicrophoneHud()
    {
        void Invoke()
        {
            if (!HasMicrophone)
            {
                VolumeHudRequested?.Invoke(new HudPayload
                {
                    Kind = HudKind.Microphone,
                    Icon = IconsaxKind.MicrophoneSlash,
                    Title = "Không tìm thấy Micro",
                    ValueText = "Chưa kết nối",
                    NormalizedValue = 0.0,
                    IsInteractiveSlider = false,
                    IsMuted = true,
                    AccentHex = "#FF9F0A"
                });
                return;
            }

            VolumeHudRequested?.Invoke(new HudPayload
            {
                Kind = HudKind.Microphone,
                Icon = IsMicMuted ? IconsaxKind.MicrophoneSlash : IconsaxKind.Microphone,
                Title = IsMicMuted ? "Đã khóa Micro" : "Micro đã bật",
                ValueText = IsMicMuted ? "Đang tắt tiếng" : "Đang thu âm",
                NormalizedValue = IsMicMuted ? 0.0 : 1.0,
                IsInteractiveSlider = false,
                IsMuted = IsMicMuted,
                AccentHex = IsMicMuted ? "#FF453A" : "#30D158"
            });
        }

        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(Invoke);
        }
        else
        {
            Invoke();
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        SyncFftCaptureState();
    }

    private void SyncFftCaptureState()
    {
        bool shouldCapture = !_disposed && _isMediaPlayingGate && _settingsService.Current.EnableFftVisualizer;

        lock (_deviceLock)
        {
            if (shouldCapture)
            {
                if (_loopbackCapture == null && _activeDevice != null)
                {
                    try
                    {
                        _loopbackCapture = new WasapiLoopbackCapture(_activeDevice);
                        _ringWritePos = 0;
                        _samplesSinceLastFft = 0;
                        Array.Clear(_sampleRing, 0, FftLength);
                        _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
                        _loopbackCapture.RecordingStopped += (_, _) => { };
                        _loopbackCapture.StartRecording();
                    }
                    catch
                    {
                        StopFftCaptureUnsafe();
                    }
                }
            }
            else
            {
                StopFftCaptureUnsafe();
                ResetBarsToIdle();
            }
        }
    }

    private void StopFftCaptureUnsafe()
    {
        if (_loopbackCapture != null)
        {
            try
            {
                _loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
                _loopbackCapture.StopRecording();
                _loopbackCapture.Dispose();
            }
            catch { }
            _loopbackCapture = null;
        }
    }

    private void ResetBarsToIdle()
    {
        for (int i = 0; i < 4; i++) _smoothedBars[i] = 0.0;
        Application.Current?.Dispatcher.BeginInvoke(() => FftBarsUpdated?.Invoke(0.0, 0.0, 0.0, 0.0));
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _loopbackCapture == null) return;

        WaveFormat fmt = _loopbackCapture.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample != 4 && bytesPerSample != 2) return;

        int frameSize = bytesPerSample * channels;
        int frames = e.BytesRecorded / frameSize;
        const int hopSize = FftLength / 2; // 50% overlap (~10.6ms @ 48kHz) for 60+ FPS fluidity

        for (int i = 0; i < frames; i++)
        {
            int offset = i * frameSize;
            float sample = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                ? BitConverter.ToSingle(e.Buffer, offset)
                : BitConverter.ToInt16(e.Buffer, offset) / 32768f;

            if (channels >= 2)
            {
                float right = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(e.Buffer, offset + bytesPerSample)
                    : BitConverter.ToInt16(e.Buffer, offset + bytesPerSample) / 32768f;
                sample = (sample + right) * 0.5f;
            }

            _sampleRing[_ringWritePos] = sample;
            _ringWritePos = (_ringWritePos + 1) & (FftLength - 1);
            _samplesSinceLastFft++;

            if (_samplesSinceLastFft >= hopSize)
            {
                _samplesSinceLastFft = 0;
                ProcessFftFrame(fmt.SampleRate);
            }
        }
    }

    private void ProcessFftFrame(int sampleRate)
    {
        long now = Stopwatch.GetTimestamp();
        if (((double)(now - _lastFftEmitTicks) / Stopwatch.Frequency) < 0.015)
        {
            return; // Emit up to ~66 FPS for silky wave choreography
        }
        _lastFftEmitTicks = now;

        for (int n = 0; n < FftLength; n++)
        {
            int ringIdx = (_ringWritePos + n) & (FftLength - 1);
            double window = 0.5 * (1.0 - Math.Cos((2.0 * Math.PI * n) / (FftLength - 1)));
            _fftBuffer[n].X = (float)(_sampleRing[ringIdx] * window);
            _fftBuffer[n].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftExponent, _fftBuffer);

        double binHz = (double)sampleRate / FftLength;
        ReadOnlySpan<(double Low, double High, double Weight)> bands =
        [
            (45, 190, 1.00),   // Bass / Kick drum
            (190, 780, 1.45),  // Low-Mid warmth
            (780, 2600, 2.15), // Vocal / Lead melody
            (2600, 8200, 3.10) // Presence / Hi-hat shimmer
        ];

        Span<double> rawEnergy = stackalloc double[4];
        double framePeak = 0.0;

        for (int b = 0; b < 4; b++)
        {
            int startBin = Math.Max(1, (int)(bands[b].Low / binHz));
            int endBin = Math.Min((FftLength / 2) - 1, (int)(bands[b].High / binHz));
            if (endBin < startBin) endBin = startBin;

            double sumSq = 0.0;
            for (int k = startBin; k <= endBin; k++)
            {
                double re = _fftBuffer[k].X;
                double im = _fftBuffer[k].Y;
                sumSq += (re * re) + (im * im);
            }

            double rms = Math.Sqrt(sumSq / (endBin - startBin + 1)) * bands[b].Weight;
            rawEnergy[b] = rms;
            if (rms > framePeak) framePeak = rms;
        }

        // Adaptive automatic gain control so loud songs never flatline at 100% and soft songs still dance
        if (framePeak > _runningPeak)
        {
            _runningPeak = (_runningPeak * 0.60) + (framePeak * 0.40);
        }
        else
        {
            _runningPeak = Math.Max(0.018, _runningPeak * 0.985);
        }

        for (int b = 0; b < 4; b++)
        {
            double normalized = rawEnergy[b] / Math.Max(0.018, _runningPeak * 1.08);
            double shaped = Math.Clamp(Math.Pow(Math.Clamp(normalized, 0.0, 1.25), 0.72), 0.0, 1.0);

            double alpha = shaped > _smoothedBars[b] ? 0.62 : 0.26;
            _smoothedBars[b] = (_smoothedBars[b] * (1.0 - alpha)) + (shaped * alpha);
        }

        double b0 = _smoothedBars[0], b1 = _smoothedBars[1], b2 = _smoothedBars[2], b3 = _smoothedBars[3];
        Application.Current?.Dispatcher.BeginInvoke(() => FftBarsUpdated?.Invoke(b0, b1, b2, b3));
    }

    // IMMNotificationClient implementation for audio device hot-plugging
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
        {
            AttachDefaultEndpoint(emitConnectionHud: true);
        }
        else if (flow == DataFlow.Capture && (role == Role.Communications || role == Role.Multimedia || role == Role.Console))
        {
            AttachDefaultCaptureEndpoint();
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        lock (_deviceLock)
        {
            if (_activeCaptureDevice != null && string.Equals(_activeCaptureDevice.ID, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                if (newState != DeviceState.Active)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => AttachDefaultCaptureEndpoint());
                }
            }
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId) { }

    public void OnDeviceRemoved(string deviceId)
    {
        lock (_deviceLock)
        {
            if (_activeCaptureDevice != null && string.Equals(_activeCaptureDevice.ID, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                Application.Current?.Dispatcher.BeginInvoke(() => AttachDefaultCaptureEndpoint());
            }
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;

        lock (_deviceLock)
        {
            DetachEndpointUnsafe();
            DetachCaptureEndpointUnsafe();
            if (_enumerator != null)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(this);
                    _enumerator.Dispose();
                }
                catch { }
                _enumerator = null;
            }
        }
    }
}
