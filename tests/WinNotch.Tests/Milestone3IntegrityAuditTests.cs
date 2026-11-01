using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;

namespace WinNotch.Tests;

public static class Milestone3IntegrityAuditTests
{
    private static MonitorDescriptor CreateTestMonitor() => new()
    {
        Handle = IntPtr.Zero,
        DeviceName = @"\\.\DISPLAY1",
        FriendlyLabel = "Test OLED Display",
        IsPrimary = true,
        PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
        DpiScaleX = 1.0,
        DpiScaleY = 1.0
    };

    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine(" MILESTONE 3: FORENSIC INTEGRITY AUDIT SUITE     ");
        Console.WriteLine(" InputHookService, StateMachine, TimerService    ");
        Console.WriteLine("=================================================");

        EnsureWpfEnvironment();

        TestTimerServiceStepMinutes(assert);
        TestNotchStateMachineHoverSuppression(assert);
        TestInputHookServiceHotkeysAndGuards(assert);
        TestMainNotchViewModelCapsLockHudAndTimer(assert);
    }

    private static void EnsureWpfEnvironment()
    {
        if (Application.Current == null)
        {
            _ = new Application();
        }
    }

    private static void TestTimerServiceStepMinutes(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 18] TimerService StepMinutes Logic & Bounds ---");

        using var timer = new TimerService();

        // 1. Initial State
        assert(timer.RemainingSeconds == 0, "Initial RemainingSeconds == 0");
        assert(!timer.HasActiveTimer, "Initial HasActiveTimer == false");
        assert(timer.FormattedTime == "00:00", "Initial FormattedTime == '00:00'");
        assert(TimerService.MaxTimerSeconds == 5999, "MaxTimerSeconds == 5999 (99m 59s)");

        // 2. Step with 0
        timer.StepMinutes(0);
        assert(timer.RemainingSeconds == 0, "StepMinutes(0) does nothing");

        // 3. Step negative while inactive
        timer.StepMinutes(-5);
        assert(timer.RemainingSeconds == 0, "StepMinutes(-5) while inactive does nothing");

        // 4. Step positive while inactive -> starts preset
        timer.StepMinutes(25);
        assert(timer.IsRunning, "StepMinutes(25) from inactive starts running");
        assert(timer.RemainingSeconds == 1500, "StepMinutes(25) sets 1500 seconds", $"Actual: {timer.RemainingSeconds}");
        assert(timer.FormattedTime == "25:00", "FormattedTime == '25:00'");

        // 5. Step positive while active
        timer.StepMinutes(1);
        assert(timer.RemainingSeconds == 1560, "StepMinutes(+1) adds 60s -> 1560s", $"Actual: {timer.RemainingSeconds}");
        assert(timer.FormattedTime == "26:00", "FormattedTime == '26:00'");

        // 6. Step negative while active
        timer.StepMinutes(-6);
        assert(timer.RemainingSeconds == 1200, "StepMinutes(-6) subtracts 360s -> 1200s", $"Actual: {timer.RemainingSeconds}");
        assert(timer.FormattedTime == "20:00", "FormattedTime == '20:00'");

        // 7. Clamp to MaxTimerSeconds
        timer.StepMinutes(200);
        assert(timer.RemainingSeconds == 5999, "StepMinutes(+200) clamps to 5999s", $"Actual: {timer.RemainingSeconds}");
        assert(timer.FormattedTime == "99:59", "FormattedTime == '99:59'");

        // 8. Clean reset on delta <= 0 (target <= 0) without alarm ringing
        timer.StepMinutes(-200);
        assert(timer.RemainingSeconds == 0, "StepMinutes(-200) resets to 0s", $"Actual: {timer.RemainingSeconds}");
        assert(!timer.IsRunning, "IsRunning == false after reset");
        assert(!timer.IsAlarmRinging, "IsAlarmRinging == false (clean reset did NOT ring alarm)");

        // 9. StateChanged event invocation
        int stateChangedCount = 0;
        timer.StateChanged += () => stateChangedCount++;
        timer.StepMinutes(5);
        assert(stateChangedCount > 0, "StateChanged fired on StepMinutes(5)");
        assert(timer.RemainingSeconds == 300, "RemainingSeconds == 300s");
        timer.Reset();
    }

    private static void TestNotchStateMachineHoverSuppression(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 19] NotchStateMachine Hover Suppression & Invariants ---");

        var sm = new NotchStateMachine(new AppSettings());

        // 1. Verify layout dimensions preservation (#050507 OLED pure black)
        assert(NotchStateMachine.DimExpanded.Width == 684.0 && NotchStateMachine.DimExpanded.Height == 272.0,
            "DimExpanded preserved at 684x272", $"{NotchStateMachine.DimExpanded.Width}x{NotchStateMachine.DimExpanded.Height}");
        assert(NotchStateMachine.DimCollapsedDualActive.Width == 276.0 && NotchStateMachine.DimCollapsedDualActive.Height == 32.0,
            "DimCollapsedDualActive preserved at 276x32");
        assert(NotchStateMachine.DimHudPop.Width == 376.0 && NotchStateMachine.DimHudPop.Height == 48.0,
            "DimHudPop preserved at 376x48");

        // 2. Initial state
        assert(sm.State == NotchVisualState.CollapsedIdle, "Initial state == CollapsedIdle");

        // 3. SuppressPendingHoverExpand call
        bool breatheNudged = false;
        sm.HoverBreatheNudged += (dims, isCollapsing) =>
        {
            breatheNudged = true;
        };

        sm.SuppressPendingHoverExpand();
        assert(breatheNudged, "SuppressPendingHoverExpand triggered HoverBreatheNudged relaxing breathe");

        // 4. Verify suppression window rejects pointer enter
        var ticksField = typeof(NotchStateMachine).GetField("_suppressHoverExpandUntilTicks", BindingFlags.NonPublic | BindingFlags.Instance);
        assert(ticksField != null, "_suppressHoverExpandUntilTicks field exists via reflection");
        long suppressionTicks = (long)ticksField!.GetValue(sm)!;
        long currentTicks = Stopwatch.GetTimestamp();
        assert(suppressionTicks > currentTicks, "Suppression window is in future", $"diff ms: {(suppressionTicks - currentTicks) * 1000.0 / Stopwatch.Frequency:F1}");

        // While suppressed, NotifyPointerEntered must return early and NOT start hover expand
        sm.NotifyPointerEntered();
        var hoverTimerField = typeof(NotchStateMachine).GetField("_hoverExpandTimer", BindingFlags.NonPublic | BindingFlags.Instance);
        var hoverTimer = (DispatcherTimer)hoverTimerField!.GetValue(sm)!;
        assert(!hoverTimer.IsEnabled, "Hover timer is NOT running while suppression is active");
        assert(sm.State == NotchVisualState.CollapsedIdle, "State remains CollapsedIdle during suppression");

        // 5. Simulate expiration of suppression window
        ticksField.SetValue(sm, (long)(currentTicks - 100)); // expired in past
        sm.NotifyPointerEntered();
        assert(hoverTimer.IsEnabled, "Hover timer is armed once suppression window has expired");

        // 6. NotifyPointerLeft stops hover timer
        sm.NotifyPointerLeft();
        assert(!hoverTimer.IsEnabled, "Hover timer stopped on NotifyPointerLeft");
    }

    private static void TestInputHookServiceHotkeysAndGuards(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 20] InputHookService Hotkeys & Auto-Repeat Guards ---");

        var settings = new SettingsService();
        settings.Current.EnableCapsLockHud = true;
        using var hookService = new InputHookService(settings, CreateTestMonitor());

        // Use reflection to access private HookProc
        var hookProcMethod = typeof(InputHookService).GetMethod("HookProc", BindingFlags.NonPublic | BindingFlags.Instance);
        assert(hookProcMethod != null, "HookProc method located via reflection");

        // Set up event listeners
        int micMuteEvents = 0;
        int colorPickerEvents = 0;
        int toggleHotkeysEvents = 0;
        bool? lastCapsLockState = null;

        hookService.MicMuteHotkeyPressed += () => Interlocked.Increment(ref micMuteEvents);
        hookService.ColorPickerHotkeyPressed += () => Interlocked.Increment(ref colorPickerEvents);
        hookService.ToggleHotkeyPressed += () => Interlocked.Increment(ref toggleHotkeysEvents);
        hookService.CapsLockToggled += state => lastCapsLockState = state;

        // Helper to invoke HookProc
        IntPtr InvokeHook(int msg, int vkCode, uint flags = 0)
        {
            var kb = new NativeMethods.KBDLLHOOKSTRUCT
            {
                vkCode = (uint)vkCode,
                scanCode = 0,
                flags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };
            IntPtr lParam = Marshal.AllocHGlobal(Marshal.SizeOf(kb));
            try
            {
                Marshal.StructureToPtr(kb, lParam, false);
                return (IntPtr)hookProcMethod!.Invoke(hookService, new object[] { 0, new IntPtr(msg), lParam })!;
            }
            finally
            {
                Marshal.FreeHGlobal(lParam);
            }
        }

        // 1. Alt + M Key Down (LLKHF_ALTDOWN = 0x20)
        IntPtr resM1 = InvokeHook(NativeMethods.WM_SYSKEYDOWN, NativeMethods.VK_M, 0x20);
        assert(resM1 == new IntPtr(1), "Alt + M is swallowed (returns 1)");
        PumpDispatcher();
        assert(micMuteEvents == 1, "MicMuteHotkeyPressed fired on first Alt+M keydown");

        // 2. Alt + M Auto-repeat Key Down (Typematic repeat guard)
        IntPtr resM2 = InvokeHook(NativeMethods.WM_SYSKEYDOWN, NativeMethods.VK_M, 0x20);
        assert(resM2 == new IntPtr(1), "Alt + M auto-repeat is swallowed (returns 1)");
        PumpDispatcher();
        assert(micMuteEvents == 1, "MicMuteHotkeyPressed NOT fired on typematic repeat (guard holds!)");

        // 3. Alt + M Key Up
        InvokeHook(NativeMethods.WM_SYSKEYUP, NativeMethods.VK_M, 0x20);

        // 4. Alt + M Key Down again after release
        InvokeHook(NativeMethods.WM_SYSKEYDOWN, NativeMethods.VK_M, 0x20);
        PumpDispatcher();
        assert(micMuteEvents == 2, "MicMuteHotkeyPressed fired again after key release");

        // 5. Alt + C Key Down
        IntPtr resC1 = InvokeHook(NativeMethods.WM_SYSKEYDOWN, NativeMethods.VK_C, 0x20);
        assert(resC1 == new IntPtr(1), "Alt + C is swallowed (returns 1)");
        PumpDispatcher();
        assert(colorPickerEvents == 1, "ColorPickerHotkeyPressed fired on first Alt+C keydown");

        // 6. Alt + C Auto-repeat
        IntPtr resC2 = InvokeHook(NativeMethods.WM_SYSKEYDOWN, NativeMethods.VK_C, 0x20);
        assert(resC2 == new IntPtr(1), "Alt + C auto-repeat is swallowed (returns 1)");
        PumpDispatcher();
        assert(colorPickerEvents == 1, "ColorPickerHotkeyPressed NOT fired on auto-repeat");

        // 7. Alt + N Key Down
        IntPtr resN1 = InvokeHook(NativeMethods.WM_SYSKEYDOWN, NativeMethods.VK_N, 0x20);
        assert(resN1 == new IntPtr(1), "Alt + N is swallowed (returns 1)");
        PumpDispatcher();
        assert(toggleHotkeysEvents == 1, "ToggleHotkeyPressed fired on first Alt+N keydown");

        // 8. Caps Lock (VK_CAPITAL) Key Down - must NOT swallow
        IntPtr resCaps = InvokeHook(NativeMethods.WM_KEYDOWN, NativeMethods.VK_CAPITAL, 0);
        assert(resCaps != new IntPtr(1), "Caps Lock (VK_CAPITAL) is NOT swallowed (passed to OS/apps)");
        PumpDispatcher();
        assert(lastCapsLockState.HasValue, "CapsLockToggled event was fired on Dispatcher");

        // 9. Caps Lock Auto-repeat Guard
        bool? stateBeforeRepeat = lastCapsLockState;
        lastCapsLockState = null;
        InvokeHook(NativeMethods.WM_KEYDOWN, NativeMethods.VK_CAPITAL, 0);
        PumpDispatcher();
        assert(lastCapsLockState == null, "CapsLockToggled NOT fired on typematic auto-repeat while key remains held");

        // Key up resets Caps Lock flag
        InvokeHook(NativeMethods.WM_KEYUP, NativeMethods.VK_CAPITAL, 0);
        InvokeHook(NativeMethods.WM_KEYDOWN, NativeMethods.VK_CAPITAL, 0);
        PumpDispatcher();
        assert(lastCapsLockState != null, "CapsLockToggled fired again after key up and down");
    }

    private static void TestMainNotchViewModelCapsLockHudAndTimer(AssertionDelegate assert)
    {
        Console.WriteLine("--- [SUITE 21] MainNotchViewModel Caps Lock HUD & Timer Delegation ---");

        var settings = new SettingsService();
        var sm = new NotchStateMachine(new AppSettings());
        var monitor = CreateTestMonitor();
        var audio = new AudioService(settings);
        var media = new MediaService(settings);
        var power = new DisplayPowerService(settings, monitor);
        var fileShelf = new FileShelfService(settings);
        var timer = new TimerService();
        var privacy = new PrivacyService(settings);
        var sysMon = new SystemMonitorService(settings);
        var notif = new NotificationService(settings);
        var picker = new ColorPickerService(sm);

        var vm = new MainNotchViewModel(
            settings,
            sm,
            audio,
            power,
            media,
            fileShelf,
            timer,
            privacy,
            sysMon,
            notif,
            picker,
            new AgentMonitorService(settings),
            () => { });

        // 1. Caps Lock HUD: ON
        vm.EmitCapsLockHud(true);
        assert(vm.ActiveHud.Kind == HudKind.CapsLock, "ActiveHud.Kind == HudKind.CapsLock");
        assert(vm.ActiveHud.Title == "Caps Lock", "ActiveHud.Title == 'Caps Lock'");
        assert(vm.ActiveHud.ValueText == "Đang bật", "ActiveHud.ValueText == 'Đang bật'");
        assert(vm.ActiveHud.AccentHex == "#30D158", "ActiveHud.AccentHex == '#30D158' (Apple Green)");
        assert(vm.ActiveHud.Icon == IconsaxKind.Lock, "ActiveHud.Icon == IconsaxKind.Lock");
        assert(sm.State == NotchVisualState.HudPop, "State is HudPop after EmitCapsLockHud(true)");

        // 2. Caps Lock HUD: OFF
        vm.EmitCapsLockHud(false);
        assert(vm.ActiveHud.ValueText == "Đã tắt", "ActiveHud.ValueText == 'Đã tắt'");
        assert(vm.ActiveHud.AccentHex == "#8E98AE", "ActiveHud.AccentHex == '#8E98AE' (Slate Gray)");

        // 3. StepTimerMinutes RelayCommand delegation
        assert(timer.RemainingSeconds == 0, "Timer initially at 0s");
        vm.StepTimerMinutesCommand.Execute(10);
        assert(timer.RemainingSeconds == 600, "StepTimerMinutesCommand(10) set timer to 600s", $"Actual: {timer.RemainingSeconds}");
        assert(timer.FormattedTime == "10:00", "Timer formatted time == '10:00'");

        // Cleanup
        timer.Reset();
        sm.CollapseImmediately();
        timer.Dispose();
        audio.Dispose();
        power.Dispose();
        sysMon.Dispose();
        notif.Dispose();
        picker.Dispose();
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(f =>
        {
            ((DispatcherFrame)f).Continue = false;
            return null;
        }), frame);
        Dispatcher.PushFrame(frame);
    }
}
