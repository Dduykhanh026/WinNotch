using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using WinNotch.Engine;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.Tests;

public static class M3HoverTimerChallengeTests
{
    private static readonly FieldInfo SuppressTicksField = typeof(NotchStateMachine).GetField("_suppressHoverExpandUntilTicks", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo HoverTimerField = typeof(NotchStateMachine).GetField("_hoverExpandTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo IsPointerInsideField = typeof(NotchStateMachine).GetField("_isPointerInsideNotch", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo TriggerAlarmMethod = typeof(TimerService).GetMethod("TriggerAlarm", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo TotalSecondsField = typeof(TimerService).GetField("_totalSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine(" MILESTONE 3: HOVER SUPPRESSION & TIMER STEPPING ADVERSARIAL SUITE ");
        Console.WriteLine("===================================================================");

        EnsureWpfEnvironment();

        // Suite 1: Hover Suppression Exact Timing & High-Frequency Rolling Window
        TestHoverSuppressionExactTiming(assert);
        TestHoverSuppressionRollingWindowHighFrequency(assert);
        TestHoverSuppressionConcurrentTorture(assert);
        TestHoverSuppressionTactileMicroSwellRelaxation(assert);

        // Suite 2: Pointer Re-entry During Active Suppression
        TestPointerReentryDuringActiveSuppression(assert);
        TestPointerReentryAfterSuppressionExpires(assert);
        TestRapidPointerReentryBurst(assert);

        // Suite 3: Pointer Left & Cursor Slip Resilience
        TestPointerLeftCursorSlipResilience(assert);
        TestPointerLeftCleanupWhenExpired(assert);
        TestCursorJitterOscillationUnderSuppression(assert);

        // Suite 4: TimerService.StepMinutes Boundary Conditions & States
        TestTimerServiceStepMinutesZero(assert);
        TestTimerServiceStepMinutesIdleState(assert);
        TestTimerServiceStepMinutesRunningState(assert);
        TestTimerServiceStepMinutesDropToZeroOrBelowCleanReset(assert);
        TestTimerServiceStepMinutesMaxTimerSecondsClamping(assert);
        TestTimerServiceStepMinutesPausedState(assert);
        TestTimerServiceStepMinutesWhileAlarmRinging(assert);
        TestTimerServiceStepMinutesRapidAlternatingStress(assert);
        TestTimerServiceStepMinutesLargeDeltaOverflowBehavior(assert);
    }

    private static void EnsureWpfEnvironment()
    {
        if (Application.Current == null)
        {
            _ = new Application();
        }
    }

    private static long GetSuppressTicks(NotchStateMachine sm) => (long)SuppressTicksField.GetValue(sm)!;
    private static void SetSuppressTicks(NotchStateMachine sm, long ticks) => SuppressTicksField.SetValue(sm, ticks);
    private static DispatcherTimer GetHoverTimer(NotchStateMachine sm) => (DispatcherTimer)HoverTimerField.GetValue(sm)!;
    private static bool GetIsPointerInside(NotchStateMachine sm) => (bool)IsPointerInsideField.GetValue(sm)!;
    private static int GetTotalSeconds(TimerService ts) => (int)TotalSecondsField.GetValue(ts)!;
    private static void InvokeTriggerAlarm(TimerService ts) => TriggerAlarmMethod.Invoke(ts, null);

    // =========================================================================
    // SUITE 1: HOVER SUPPRESSION TIMING & ROLLING WINDOW
    // =========================================================================

    private static void TestHoverSuppressionExactTiming(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S1] Hover Suppression Exact 850ms Timing Oracle ---");

        var settings = new AppSettings { HoverExpandDelayMs = 160 };
        var sm = new NotchStateMachine(settings);

        long beforeTicks = Stopwatch.GetTimestamp();
        sm.SuppressPendingHoverExpand();
        long afterTicks = Stopwatch.GetTimestamp();
        long suppressTicks = GetSuppressTicks(sm);

        assert(suppressTicks > 0, "SuppressPendingHoverExpand sets _suppressHoverExpandUntilTicks to non-zero");

        long expectedDelta = (long)(0.85 * Stopwatch.Frequency);
        long actualDeltaFromBefore = suppressTicks - beforeTicks;
        long actualDeltaFromAfter = suppressTicks - afterTicks;

        double msFromBefore = actualDeltaFromBefore * 1000.0 / Stopwatch.Frequency;
        double msFromAfter = actualDeltaFromAfter * 1000.0 / Stopwatch.Frequency;

        Console.WriteLine($"Stopwatch frequency: {Stopwatch.Frequency} Hz | Window delta: {expectedDelta} ticks");
        Console.WriteLine($"Actual window duration: {msFromAfter:F2}ms - {msFromBefore:F2}ms (target: 850.00ms)");

        assert(msFromBefore >= 845.0 && msFromAfter <= 855.0,
            $"Exact 850ms window enforced within precision tolerance [845ms, 855ms] (observed: {msFromAfter:F2}ms)");

        // Verify hover timer is stopped
        var hoverTimer = GetHoverTimer(sm);
        assert(!hoverTimer.IsEnabled, "Hover expand timer is stopped by SuppressPendingHoverExpand()");
    }

    private static void TestHoverSuppressionRollingWindowHighFrequency(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S1] Hover Suppression High-Frequency Rolling Window ---");

        var sm = new NotchStateMachine(new AppSettings());

        sm.SuppressPendingHoverExpand();
        long firstSuppressTicks = GetSuppressTicks(sm);

        // Simulate 20 rapid mouse wheel scroll ticks spaced 30ms apart (total elapsed ~600ms)
        const int scrollCount = 20;
        long lastTicks = firstSuppressTicks;
        bool rollingExtendedEachStep = true;

        for (int i = 0; i < scrollCount; i++)
        {
            Thread.Sleep(25);
            sm.SuppressPendingHoverExpand();
            long currentTicks = GetSuppressTicks(sm);
            if (currentTicks <= lastTicks)
            {
                rollingExtendedEachStep = false;
            }
            lastTicks = currentTicks;
        }

        assert(rollingExtendedEachStep, $"{scrollCount} rapid scroll events continuously rolled the suppression window forward");

        long finalSuppressTicks = GetSuppressTicks(sm);
        long nowTicks = Stopwatch.GetTimestamp();
        long remainingTicks = finalSuppressTicks - nowTicks;
        double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;

        Console.WriteLine($"After {scrollCount} scrolls across ~500ms, remaining suppression window: {remainingMs:F2}ms");
        assert(remainingMs > 750.0 && remainingMs <= 855.0,
            $"Rolling window persists ~850ms into future from the LAST scroll event (remaining: {remainingMs:F2}ms)");
    }

    private static void TestHoverSuppressionConcurrentTorture(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S1] Hover Suppression Multi-Threaded Concurrent Torture ---");

        var sm = new NotchStateMachine(new AppSettings());
        const int threadCount = 6;
        const int iterationsPerThread = 2000;
        var barrier = new Barrier(threadCount);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        sm.SuppressPendingHoverExpand();
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
            threads[t].Start();
        }

        foreach (var th in threads)
        {
            th.Join();
        }

        assert(exceptions.IsEmpty, $"Concurrent suppression torture ({threadCount * iterationsPerThread} calls) completed with 0 exceptions");

        long finalTicks = GetSuppressTicks(sm);
        long now = Stopwatch.GetTimestamp();
        assert(finalTicks >= now, "Final _suppressHoverExpandUntilTicks is non-negative and ahead of now");
    }

    private static void TestHoverSuppressionTactileMicroSwellRelaxation(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S1] Hover Suppression Tactile Micro-Swell Relaxation ---");

        var sm = new NotchStateMachine(new AppSettings());

        // Baseline: CollapsedIdle
        int relaxEventsFired = 0;
        bool lastRelaxFlag = false;
        NotchDimensions lastRelaxDim = default;

        sm.HoverBreatheNudged += (dim, relax) =>
        {
            if (relax)
            {
                relaxEventsFired++;
                lastRelaxFlag = true;
                lastRelaxDim = dim;
            }
        };

        // Pointer enters: micro-swell expands
        sm.NotifyPointerEntered();

        // Wheel scrolls: suppression halts swell and snaps back to resting dimensions
        sm.SuppressPendingHoverExpand();

        assert(relaxEventsFired >= 1 && lastRelaxFlag,
            "SuppressPendingHoverExpand triggers HoverBreatheNudged with relax=true in CollapsedIdle");
        assert(Math.Abs(lastRelaxDim.Width - NotchStateMachine.DimCollapsedIdle.Width) < 0.001,
            $"Hover micro-swell snapped back exactly to DimCollapsedIdle width ({lastRelaxDim.Width} == {NotchStateMachine.DimCollapsedIdle.Width})");

        // Test in CollapsedActive state (Music playing)
        sm.SetBackgroundActivity(true, CollapsedContentKind.MusicOnly, 0);
        assert(sm.State == NotchVisualState.CollapsedActive, "State transitioned to CollapsedActive");

        relaxEventsFired = 0;
        sm.NotifyPointerEntered();
        sm.SuppressPendingHoverExpand();

        assert(relaxEventsFired >= 1 && lastRelaxFlag,
            "SuppressPendingHoverExpand triggers HoverBreatheNudged with relax=true in CollapsedActive");
        assert(Math.Abs(lastRelaxDim.Width - NotchStateMachine.DimCollapsedMusic.Width) < 0.001,
            $"Hover micro-swell snapped back exactly to DimCollapsedMusic width ({lastRelaxDim.Width} == {NotchStateMachine.DimCollapsedMusic.Width})");
    }

    // =========================================================================
    // SUITE 2: POINTER RE-ENTRY DURING ACTIVE SUPPRESSION
    // =========================================================================

    private static void TestPointerReentryDuringActiveSuppression(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S2] Pointer Re-entry Blocked During Active Suppression ---");

        var sm = new NotchStateMachine(new AppSettings { HoverExpandDelayMs = 160 });
        var hoverTimer = GetHoverTimer(sm);

        int breatheCount = 0;
        sm.HoverBreatheNudged += (_, relax) =>
        {
            if (!relax) breatheCount++;
        };

        // Activate suppression window (850ms)
        sm.SuppressPendingHoverExpand();
        assert(!hoverTimer.IsEnabled, "Hover timer is stopped after SuppressPendingHoverExpand");

        // Pointer enters while suppression is active
        sm.NotifyPointerEntered();

        assert(!hoverTimer.IsEnabled, "NotifyPointerEntered() does NOT start hover expand timer while suppression is active");
        assert(breatheCount == 0, "NotifyPointerEntered() does NOT trigger tactile micro-swell while suppression is active");
        assert(sm.State == NotchVisualState.CollapsedIdle, "State remains CollapsedIdle during active suppression");
        assert(GetIsPointerInside(sm), "Pointer inside flag is correctly tracked as true");
    }

    private static void TestPointerReentryAfterSuppressionExpires(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S2] Pointer Re-entry Allowed After Suppression Expires ---");

        var sm = new NotchStateMachine(new AppSettings { HoverExpandDelayMs = 160 });
        var hoverTimer = GetHoverTimer(sm);

        int breatheSwellCount = 0;
        sm.HoverBreatheNudged += (_, relax) =>
        {
            if (!relax) breatheSwellCount++;
        };

        // Activate suppression, then simulate timestamp expiration
        sm.SuppressPendingHoverExpand();
        SetSuppressTicks(sm, Stopwatch.GetTimestamp() - (long)(0.1 * Stopwatch.Frequency)); // Expired 100ms ago

        // Pointer re-enters
        sm.NotifyPointerEntered();

        assert(GetSuppressTicks(sm) == 0, "Expired _suppressHoverExpandUntilTicks is reset to 0 on NotifyPointerEntered()");
        assert(hoverTimer.IsEnabled, "Hover expand timer is STARTED after suppression window expires");
        assert(breatheSwellCount == 1, "Tactile micro-swell breathe (relax=false) is triggered after suppression window expires");
    }

    private static void TestRapidPointerReentryBurst(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S2] Rapid Pointer Re-entry Burst Torture ---");

        var sm = new NotchStateMachine(new AppSettings());
        var hoverTimer = GetHoverTimer(sm);

        sm.SuppressPendingHoverExpand();

        int forbiddenStarts = 0;
        for (int i = 0; i < 200; i++)
        {
            sm.NotifyPointerEntered();
            if (hoverTimer.IsEnabled)
            {
                forbiddenStarts++;
            }
        }

        assert(forbiddenStarts == 0, $"200 rapid NotifyPointerEntered() calls strictly blocked from starting timer (forbidden={forbiddenStarts})");
    }

    // =========================================================================
    // SUITE 3: POINTER LEFT & CURSOR SLIP RESILIENCE
    // =========================================================================

    private static void TestPointerLeftCursorSlipResilience(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S3] Cursor Slip Does NOT Prematurely Clear Suppression ---");

        var sm = new NotchStateMachine(new AppSettings());
        var hoverTimer = GetHoverTimer(sm);

        sm.SuppressPendingHoverExpand();
        long activeTicks = GetSuppressTicks(sm);
        assert(activeTicks > Stopwatch.GetTimestamp(), "Suppression window established");

        // Cursor slips out
        sm.NotifyPointerLeft();

        long ticksAfterLeave = GetSuppressTicks(sm);
        assert(ticksAfterLeave == activeTicks, "NotifyPointerLeft() does NOT clear active unexpired suppression window");
        assert(!GetIsPointerInside(sm), "IsPointerInsideNotch correctly tracked as false");

        // Cursor immediately re-enters before 850ms window expires
        sm.NotifyPointerEntered();

        assert(!hoverTimer.IsEnabled, "Cursor re-entry after slip remains suppressed (timer NOT started)");
        assert(GetSuppressTicks(sm) == activeTicks, "Suppression window intact across slip cycle");
    }

    private static void TestPointerLeftCleanupWhenExpired(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S3] NotifyPointerLeft Cleans Up Expired Suppression Window ---");

        var sm = new NotchStateMachine(new AppSettings());

        sm.SuppressPendingHoverExpand();
        SetSuppressTicks(sm, Stopwatch.GetTimestamp() - (long)(0.05 * Stopwatch.Frequency)); // Expired 50ms ago

        // Cursor leaves after expiration
        sm.NotifyPointerLeft();

        long ticksAfterLeave = GetSuppressTicks(sm);
        assert(ticksAfterLeave == 0, "NotifyPointerLeft() resets _suppressHoverExpandUntilTicks to 0 when expired");
    }

    private static void TestCursorJitterOscillationUnderSuppression(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S3] Cursor Jitter Oscillation Torture (50 Cycles) ---");

        var sm = new NotchStateMachine(new AppSettings());
        var hoverTimer = GetHoverTimer(sm);

        sm.SuppressPendingHoverExpand();
        long initialTicks = GetSuppressTicks(sm);

        int timerLeaks = 0;
        int swellLeaks = 0;
        sm.HoverBreatheNudged += (_, relax) =>
        {
            if (!relax) swellLeaks++;
        };

        for (int i = 0; i < 50; i++)
        {
            sm.NotifyPointerLeft();
            sm.NotifyPointerEntered();
            if (hoverTimer.IsEnabled) timerLeaks++;
        }

        assert(timerLeaks == 0, $"50 slip-and-reenter cycles resulted in 0 timer starts (leaks={timerLeaks})");
        assert(swellLeaks == 0, $"50 slip-and-reenter cycles resulted in 0 tactile micro-swell leaks (leaks={swellLeaks})");
        assert(GetSuppressTicks(sm) == initialTicks, "Suppression window remained locked at exact target ticks throughout jitter");
    }

    // =========================================================================
    // SUITE 4: TIMERSERVICE.STEPMINUTES BOUNDARY CONDITIONS & STATES
    // =========================================================================

    private static void TestTimerServiceStepMinutesZero(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes(0) No-Op Oracle ---");

        using var ts = new TimerService();
        int stateChangedCount = 0;
        ts.StateChanged += () => stateChangedCount++;

        // Delta 0 from idle
        ts.StepMinutes(0);
        assert(!ts.IsRunning && !ts.IsPaused && ts.RemainingSeconds == 0, "StepMinutes(0) on idle is a complete no-op");
        assert(stateChangedCount == 0, "StateChanged does not fire on delta 0");

        // Delta 0 from running
        ts.StartPresetMinutes(5);
        stateChangedCount = 0;
        ts.StepMinutes(0);
        assert(ts.IsRunning && ts.RemainingSeconds == 300, "StepMinutes(0) on running timer retains duration and state");
        assert(stateChangedCount == 0, "StateChanged does not fire on delta 0 while running");
    }

    private static void TestTimerServiceStepMinutesIdleState(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes From Idle State ---");

        using var ts = new TimerService();

        // 1. Negative delta from idle does nothing
        ts.StepMinutes(-1);
        assert(!ts.IsRunning && !ts.IsPaused && ts.RemainingSeconds == 0, "StepMinutes(-1) on idle timer does nothing");
        ts.StepMinutes(-99);
        assert(!ts.IsRunning && ts.RemainingSeconds == 0, "StepMinutes(-99) on idle timer does nothing");

        // 2. Positive delta from idle starts preset
        ts.StepMinutes(1);
        assert(ts.IsRunning && !ts.IsPaused, "StepMinutes(+1) from idle starts running timer");
        assert(ts.RemainingSeconds == 60, $"RemainingSeconds is exactly 60 (got {ts.RemainingSeconds})");
        assert(GetTotalSeconds(ts) == 60, $"TotalSeconds is exactly 60 (got {GetTotalSeconds(ts)})");
        assert(ts.FormattedTime == "01:00", $"FormattedTime is 01:00 (got {ts.FormattedTime})");

        ts.Reset();

        ts.StepMinutes(25);
        assert(ts.IsRunning && ts.RemainingSeconds == 1500, "StepMinutes(+25) from idle sets 1500 seconds (25m)");
        assert(ts.FormattedTime == "25:00", $"FormattedTime is 25:00 (got {ts.FormattedTime})");

        ts.Reset();

        // 3. Positive delta exceeding 99 clamped to 99 from idle
        ts.StepMinutes(150);
        assert(ts.IsRunning && ts.RemainingSeconds == 99 * 60, "StepMinutes(+150) from idle clamped to 99 minutes (5940s)");
        assert(ts.FormattedTime == "99:00", $"FormattedTime is 99:00 (got {ts.FormattedTime})");
    }

    private static void TestTimerServiceStepMinutesRunningState(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes Running Increments and Decrements ---");

        using var ts = new TimerService();
        ts.StartPresetMinutes(5); // 300 seconds

        // Add 1 minute
        ts.StepMinutes(1);
        assert(ts.IsRunning && ts.RemainingSeconds == 360, "StepMinutes(+1) while running increases 300s -> 360s");
        assert(ts.FormattedTime == "06:00", $"FormattedTime is 06:00 (got {ts.FormattedTime})");

        // Add 4 minutes
        ts.StepMinutes(4);
        assert(ts.IsRunning && ts.RemainingSeconds == 600, "StepMinutes(+4) while running increases 360s -> 600s");
        assert(ts.FormattedTime == "10:00", $"FormattedTime is 10:00 (got {ts.FormattedTime})");

        // Subtract 2 minutes
        ts.StepMinutes(-2);
        assert(ts.IsRunning && ts.RemainingSeconds == 480, "StepMinutes(-2) while running decreases 600s -> 480s");
        assert(ts.FormattedTime == "08:00", $"FormattedTime is 08:00 (got {ts.FormattedTime})");

        // Subtract 7 minutes
        ts.StepMinutes(-7);
        assert(ts.IsRunning && ts.RemainingSeconds == 60, "StepMinutes(-7) while running decreases 480s -> 60s");
        assert(ts.FormattedTime == "01:00", $"FormattedTime is 01:00 (got {ts.FormattedTime})");
    }

    private static void TestTimerServiceStepMinutesDropToZeroOrBelowCleanReset(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes Dropping <= 0 Resets Cleanly Without Chime ---");

        using var ts = new TimerService();

        // Scenario A: Stepping exactly to 0
        ts.StartPresetMinutes(2); // 120s
        ts.StepMinutes(-2); // Target = 0

        assert(ts.RemainingSeconds == 0, "RemainingSeconds drops to 0");
        assert(!ts.IsRunning, "IsRunning is false after drop to 0");
        assert(!ts.IsPaused, "IsPaused is false after drop to 0");
        assert(!ts.IsAlarmRinging, "IsAlarmRinging is FALSE (clean reset, no chime!)");
        assert(!ts.HasActiveTimer, "HasActiveTimer is false after drop to 0");
        assert(ts.FormattedTime == "00:00", "FormattedTime is 00:00");
        assert(ts.ProgressRatio == 0.0, "ProgressRatio is 0.0");

        // Scenario B: Stepping deeply below 0
        ts.StartPresetMinutes(1); // 60s
        ts.StepMinutes(-10); // Target = -540s

        assert(ts.RemainingSeconds == 0, "Stepping -10 from 1m sets RemainingSeconds to 0");
        assert(!ts.IsRunning && !ts.IsPaused, "Timer is cleanly halted");
        assert(!ts.IsAlarmRinging, "Alarm chime does NOT trigger when stepped negative");
        assert(GetTotalSeconds(ts) == 0, "TotalSeconds is reset to 0");
    }

    private static void TestTimerServiceStepMinutesMaxTimerSecondsClamping(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes MaxTimerSeconds (5999) Clamping ---");

        using var ts = new TimerService();
        ts.StartPresetMinutes(90); // 5400s

        // Step +15 mins -> 5400 + 900 = 6300s -> clamped to 5999
        ts.StepMinutes(15);
        assert(ts.RemainingSeconds == TimerService.MaxTimerSeconds,
            $"Stepping beyond 5999 clamps to MaxTimerSeconds ({ts.RemainingSeconds} == {TimerService.MaxTimerSeconds})");
        assert(ts.FormattedTime == "99:59", $"FormattedTime for 5999s is 99:59 (got {ts.FormattedTime})");
        assert(GetTotalSeconds(ts) >= 5999, $"TotalSeconds tracked appropriately ({GetTotalSeconds(ts)})");
        assert(ts.ProgressRatio <= 1.0, $"ProgressRatio clamped <= 1.0 (got {ts.ProgressRatio})");

        // Step +10 mins again -> remains clamped at 5999
        ts.StepMinutes(10);
        assert(ts.RemainingSeconds == 5999, "Additional positive steps remain clamped at 5999");
        assert(ts.FormattedTime == "99:59", "FormattedTime remains 99:59");

        // Step -1 min -> 5999 - 60 = 5939
        ts.StepMinutes(-1);
        assert(ts.RemainingSeconds == 5939, $"Step -1 from 5999 gives 5939s (got {ts.RemainingSeconds})");
        assert(ts.FormattedTime == "98:59", $"FormattedTime is 98:59 (got {ts.FormattedTime})");
    }

    private static void TestTimerServiceStepMinutesPausedState(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes Paused State Handling ---");

        using var ts = new TimerService();
        ts.StartPresetMinutes(5); // 300s
        ts.TogglePause();

        assert(ts.IsPaused && !ts.IsRunning, "Timer is paused");

        // Step while paused
        ts.StepMinutes(2);
        assert(ts.IsPaused && !ts.IsRunning, "Timer REMAINS paused after StepMinutes(+2)");
        assert(ts.RemainingSeconds == 420, $"RemainingSeconds adjusted to 420s (got {ts.RemainingSeconds})");
        assert(ts.FormattedTime == "07:00", $"FormattedTime is 07:00 (got {ts.FormattedTime})");

        // Step negative while paused
        ts.StepMinutes(-3);
        assert(ts.IsPaused && !ts.IsRunning, "Timer REMAINS paused after StepMinutes(-3)");
        assert(ts.RemainingSeconds == 240, $"RemainingSeconds adjusted to 240s (got {ts.RemainingSeconds})");

        // Step <= 0 while paused: should reset cleanly
        ts.StepMinutes(-5);
        assert(!ts.IsPaused && !ts.IsRunning, "Dropping <= 0 while paused resets timer completely");
        assert(ts.RemainingSeconds == 0, "RemainingSeconds is 0");
        assert(!ts.IsAlarmRinging, "Alarm chime does NOT trigger");
    }

    private static void TestTimerServiceStepMinutesWhileAlarmRinging(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes Dismisses Ringing Alarm ---");

        using var ts = new TimerService();

        // Scenario A: Natural timer expiration -> alarm ringing in idle state
        InvokeTriggerAlarm(ts);
        assert(ts.IsAlarmRinging, "Alarm is ringing in idle state");

        // Step +5 mins while alarm is ringing starts new timer
        ts.StepMinutes(5);
        assert(!ts.IsAlarmRinging, "StepMinutes(+5) immediately dismissed the ringing alarm");
        assert(ts.IsRunning && ts.RemainingSeconds == 300, "New timer was started with 300s (5m)");

        // Scenario B: Alarm ringing in idle state dismissed with negative step
        ts.Reset();
        InvokeTriggerAlarm(ts);
        assert(ts.IsAlarmRinging, "Alarm is ringing again in idle state");

        ts.StepMinutes(-1);
        assert(!ts.IsAlarmRinging, "StepMinutes(-1) dismissed the ringing alarm");
        assert(!ts.IsRunning && !ts.IsPaused && ts.RemainingSeconds == 0, "Timer remains idle after negative step dismissal");

        // Scenario C: Alarm ringing while running -> negative step dismisses alarm AND decrements duration
        ts.StartPresetMinutes(5); // 300s
        InvokeTriggerAlarm(ts);
        ts.StepMinutes(-1);
        assert(!ts.IsAlarmRinging, "StepMinutes(-1) dismissed alarm while running");
        assert(ts.IsRunning && ts.RemainingSeconds == 240, "Running timer decremented to 240s");
    }

    private static void TestTimerServiceStepMinutesRapidAlternatingStress(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes Rapid Alternating Stress (5,000 Cycles) ---");

        using var ts = new TimerService();
        ts.StartPresetMinutes(10); // 600s

        int[] stepDeltas = [1, -1, 2, -2, 5, -3, 4, -6, 1, -1];
        var formatRegex = new Regex(@"^\d{2}:\d{2}$");

        bool allBoundsValid = true;
        bool allRatiosValid = true;
        bool allFormatsValid = true;

        for (int i = 0; i < 5000; i++)
        {
            int delta = stepDeltas[i % stepDeltas.Length];
            ts.StepMinutes(delta);

            if (ts.RemainingSeconds < 0 || ts.RemainingSeconds > 5999)
            {
                allBoundsValid = false;
            }
            if (ts.ProgressRatio < 0.0 || ts.ProgressRatio > 1.0)
            {
                allRatiosValid = false;
            }
            if (!formatRegex.IsMatch(ts.FormattedTime))
            {
                allFormatsValid = false;
            }

            // If it ever drops to 0, pump it back up
            if (ts.RemainingSeconds == 0)
            {
                ts.StepMinutes(10);
            }
        }

        assert(allBoundsValid, "All 5,000 steps maintained RemainingSeconds strictly within [0, 5999]");
        assert(allRatiosValid, "All 5,000 steps maintained ProgressRatio strictly within [0.0, 1.0]");
        assert(allFormatsValid, "All 5,000 steps produced valid mm:ss FormattedTime strings");
    }

    private static void TestTimerServiceStepMinutesLargeDeltaOverflowBehavior(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3-S4] TimerService.StepMinutes Large Delta Arithmetic Safety ---");

        using var ts = new TimerService();
        ts.StartPresetMinutes(5); // 300s

        // Test very large positive delta (e.g. 100,000 minutes = 6,000,000 seconds)
        // Does not overflow signed 32-bit int, clamps to 5999
        ts.StepMinutes(100_000);
        assert(ts.RemainingSeconds == 5999, $"StepMinutes(100,000) clamps safely to 5999 (got {ts.RemainingSeconds})");

        // Test very large negative delta (e.g. -100,000 minutes)
        ts.StepMinutes(-100_000);
        assert(ts.RemainingSeconds == 0 && !ts.IsRunning, "StepMinutes(-100,000) resets safely to 0 without throwing");
    }
}
