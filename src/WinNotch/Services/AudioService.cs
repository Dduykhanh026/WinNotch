using System.Diagnostics;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
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
    private WasapiLoopbackCapture? _loopbackCapture;

    private readonly float[] _sampleRing = new float[FftLength];
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private int _ringWritePos;
    private int _samplesSinceLastFft;
    private double _runningPeak = 0.045;
    private readonly double[] _smoothedBars = [0.12, 0.12, 0.12, 0.12];
    private long _lastFftEmitTicks;
    private long _suppressCallbackTicks;

    private bool _isMediaPlayingGate;
    private bool _disposed;

    public double VolumePercent { get; private set; } = 50.0;
    public bool IsMuted { get; private set; }
    public string OutputDeviceName { get; private set; } = "Loa hệ thống";

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
            AttachDefaultEndpoint();
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
        StepVolume(0, toggleMute: true);
        if (!showHud) return;
    }

    private void AttachDefaultEndpoint()
    {
        lock (_deviceLock)
        {
            DetachEndpointUnsafe();

            try
            {
                if (_enumerator != null && _enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    _activeDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
        });
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
        if (flow == DataFlow.Render && role == Role.Multimedia)
        {
            AttachDefaultEndpoint();
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;

        lock (_deviceLock)
        {
            DetachEndpointUnsafe();
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
