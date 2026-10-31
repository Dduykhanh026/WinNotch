using System.IO;
using System.Media;
using System.Reflection;
using System.Windows.Threading;

namespace WinNotch.Services;

public sealed class TimerService : IDisposable
{
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _alarmStopTimer;

    public const int MaxTimerSeconds = 5999;

    private int _remainingSeconds;
    private int _totalSeconds;

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsAlarmRinging { get; private set; }

    public bool HasActiveTimer => _remainingSeconds > 0 && (IsRunning || IsPaused);
    public int RemainingSeconds => _remainingSeconds;

    public string FormattedTime
    {
        get
        {
            int mins = _remainingSeconds / 60;
            int secs = _remainingSeconds % 60;
            return $"{mins:00}:{secs:00}";
        }
    }

    public double ProgressRatio => _totalSeconds > 0
        ? Math.Clamp((double)_remainingSeconds / _totalSeconds, 0.0, 1.0)
        : 0.0;

    public event Action? StateChanged;

    public TimerService()
    {
        _tickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.0)
        };
        _tickTimer.Tick += OnSecondTick;

        _alarmStopTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8.0)
        };
        _alarmStopTimer.Tick += (_, _) => DismissAlarm();
    }

    public void StartPresetMinutes(int minutes)
    {
        DismissAlarm();
        _totalSeconds = Math.Max(60, minutes * 60);
        _remainingSeconds = _totalSeconds;
        IsRunning = true;
        IsPaused = false;
        _tickTimer.Start();
        StateChanged?.Invoke();
    }

    public void AddOneMinute()
    {
        DismissAlarm();
        _remainingSeconds += 60;
        _totalSeconds = Math.Max(_totalSeconds + 60, _remainingSeconds);
        if (!IsRunning && !IsPaused)
        {
            IsRunning = true;
            _tickTimer.Start();
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Steps the timer by a specified number of whole minutes.
    /// When the timer is active (running or paused), adjusts the remaining duration.
    /// Clamps between 0 and 5999 seconds. If the duration drops to or below zero,
    /// performs a clean reset without ringing the alarm.
    /// </summary>
    /// <param name="deltaMinutes">Number of minutes to add (positive) or subtract (negative).</param>
    public void StepMinutes(int deltaMinutes)
    {
        if (deltaMinutes == 0) return;
        DismissAlarm();

        if (!IsRunning && !IsPaused)
        {
            if (deltaMinutes <= 0) return;
            StartPresetMinutes(Math.Clamp(deltaMinutes, 1, 99));
            return;
        }

        int targetSeconds = _remainingSeconds + (deltaMinutes * 60);
        if (targetSeconds <= 0)
        {
            Reset();
            return;
        }

        _remainingSeconds = Math.Clamp(targetSeconds, 0, MaxTimerSeconds);
        _totalSeconds = Math.Max(_totalSeconds, _remainingSeconds);
        StateChanged?.Invoke();
    }

    public void TogglePause()
    {
        if (IsAlarmRinging)
        {
            DismissAlarm();
            return;
        }

        if (_remainingSeconds <= 0)
        {
            StartPresetMinutes(25);
            return;
        }

        if (IsRunning)
        {
            IsRunning = false;
            IsPaused = true;
            _tickTimer.Stop();
        }
        else
        {
            IsRunning = true;
            IsPaused = false;
            _tickTimer.Start();
        }

        StateChanged?.Invoke();
    }

    public void Reset()
    {
        DismissAlarm();
        _tickTimer.Stop();
        _remainingSeconds = 0;
        _totalSeconds = 0;
        IsRunning = false;
        IsPaused = false;
        StateChanged?.Invoke();
    }

    public void DismissAlarm()
    {
        if (!IsAlarmRinging) return;
        IsAlarmRinging = false;
        _alarmStopTimer.Stop();
        StateChanged?.Invoke();
    }

    private void OnSecondTick(object? sender, EventArgs e)
    {
        if (_remainingSeconds > 0)
        {
            _remainingSeconds--;
            StateChanged?.Invoke();
        }

        if (_remainingSeconds <= 0)
        {
            _tickTimer.Stop();
            IsRunning = false;
            IsPaused = false;
            TriggerAlarm();
        }
    }

    private void TriggerAlarm()
    {
        IsAlarmRinging = true;
        _alarmStopTimer.Stop();
        _alarmStopTimer.Start();
        StateChanged?.Invoke();

        Task.Run(PlayEmbeddedChime);
    }

    private static void PlayEmbeddedChime()
    {
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using Stream? stream = asm.GetManifestResourceStream("WinNotch.Assets.chime.wav");
            if (stream != null)
            {
                using var player = new SoundPlayer(stream);
                player.PlaySync();
                return;
            }
        }
        catch { }

        try { SystemSounds.Asterisk.Play(); } catch { }
    }

    public void Dispose()
    {
        _tickTimer.Stop();
        _alarmStopTimer.Stop();
    }
}
