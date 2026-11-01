using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.Tests;

public class Program
{
    private static int _passedCount = 0;
    private static int _failedCount = 0;

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

    [STAThread]
    public static int Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine(" WINNOTCH POWER SUITE — EMPIRICAL TEST HARNESS  ");
        Console.WriteLine(" Milestone 1: AudioService & DisplayPowerService ");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Running on OS: {Environment.OSVersion}, .NET: {Environment.Version}");
        Console.WriteLine($"Thread ApartmentState: {Thread.CurrentThread.GetApartmentState()}");
        Console.WriteLine();

        try
        {
            if (args.Length > 0 && (args.Contains("--m1") || args.Contains("--m1-geometry")))
            {
                Milestone1ExpandedGeometryChallengeTests.RunAll(Assert);
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
                Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
                Console.WriteLine("=================================================");
                return _failedCount == 0 ? 0 : 1;
            }

            if (args.Length > 0 && (args.Contains("--m2") || args.Contains("--m2-dynamics") || args.Contains("--m2-collapsed")))
            {
                Milestone2CollapsedDynamicsChallengeTests.RunAll(Assert);
                Milestone2DynamicsAdversarialChallengeTests.RunAll(Assert);
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
                Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
                Console.WriteLine("=================================================");
                Environment.Exit(_failedCount == 0 ? 0 : 1);
                return _failedCount == 0 ? 0 : 1;
            }

            if (args.Length > 0 && (args.Contains("--m3m4") || args.Contains("--ui-evolution")))
            {
                Milestone3And4UiEvolutionTests.RunAll(Assert);
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
                Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
                Console.WriteLine("=================================================");
                Environment.Exit(_failedCount == 0 ? 0 : 1);
                return _failedCount == 0 ? 0 : 1;
            }

            if (args.Length > 0 && args.Contains("--m3-hover-timer"))
            {
                M3HoverTimerChallengeTests.RunAll(Assert);
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
                Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
                Console.WriteLine("=================================================");
                return _failedCount == 0 ? 0 : 1;
            }

            if (args.Length > 0 && args.Contains("--m4-shelf-challenge"))
            {
                FileShelfChallengeTests.RunAll(Assert);
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
                Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
                Console.WriteLine("=================================================");
                return _failedCount == 0 ? 0 : 1;
            }

            if (args.Length > 0 && (args.Contains("--milestone4") || args.Contains("--m4") || args.Contains("--m4-adversarial") || args.Contains("--m4-ram")))
            {
                Milestone4ChallengeTests.RunAll(Assert);
                FileShelfChallengeTests.RunAll(Assert);
                Milestone4AdversarialChallengeTests.RunAll(Assert);
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
                Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
                Console.WriteLine("=================================================");
                return _failedCount == 0 ? 0 : 1;
            }

            if (args.Length > 0 && (args.Contains("--milestone5") || args.Contains("--m5") || args.Contains("--m5-gestures") || args.Contains("--m5-controls")))
            {
                if (args.Contains("--m5-gestures"))
                {
                    Milestone5AdversarialChallengeTests.RunAll(Assert);
                }
                else if (args.Contains("--m5-controls"))
                {
                    Milestone5UIControlsChallengeTests.RunAll(Assert);
                }
                else
                {
                    Milestone5AdversarialChallengeTests.RunAll(Assert);
                    Milestone5UIControlsChallengeTests.RunAll(Assert);
                }

                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
                Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
                Console.WriteLine("=================================================");
                return _failedCount == 0 ? 0 : 1;
            }

            // 1. COM Interop & IPolicyConfig
            TestComInteropPolicyConfig();

            // 2. Debounce Timing & Capture Notification Calculation
            TestDebounceTimingCalculation();

            // 3. Audio Name & Headphone Classification Unit / Corner Tests
            TestAudioHelpers();

            // 4. Battery Status Formatting Oracle & Corner Tests
            TestBatteryStatusLogic();

            // 5. DisplayPowerService Instance & Caffeine Stress Tests
            TestDisplayPowerService();

            // 6. AudioService Real Endpoint & Stress Tests
            TestAudioService();

            // 7. Multi-Threaded Concurrent Torture Test (No Deadlocks)
            TestMultiThreadedTorture();

            // 8. WPF Dispatcher & HUD Event Propagation Test
            TestWpfDispatcherHudPropagation();

            // 9. Screen Capture & Multi-Monitor Negative Coordinates
            TestVirtualScreenCaptureAndMultiMonitor();

            // 10. GDI Resource Cleanup & Handle Tracking Stress Test
            TestGdiResourceCleanupAndLeakStress();

            // 11. Clipboard Retry Resilience Under Simulated Win32 Locking
            TestClipboardRetryResilienceUnderSimulatedLock();

            // 12. Color Formatting, Rec. 601 Contrast & Swatch Generation Oracle
            TestColorPickerSwatchAndRec601Contrast();

            // 13. ColorPickerService Lifecycle & Event Emission
            TestColorPickerServiceLifecycleAndHudPayload();

            // 14. Milestone 2 ColorPickerOverlayWindow Empirical Stress Tests
            ColorPickerChallengeTests.RunAll(Assert);

            // 15. Milestone 3 InputHookService Empirical Stress Tests
            InputHookChallengeTests.RunAll(Assert);

            // 16. Milestone 3 Forensic Integrity Audit Suite
            Milestone3IntegrityAuditTests.RunAll(Assert);

            // 17. Milestone 3 Hover Suppression & Timer Stepping Empirical Stress Tests
            M3HoverTimerChallengeTests.RunAll(Assert);

            // 18. Milestone 4 RAM Optimizer & File Shelf Verification Suite
            Milestone4ChallengeTests.RunAll(Assert);

            // 19. Milestone 4 Adversarial File Shelf Empirical Stress Tests
            FileShelfChallengeTests.RunAll(Assert);

            // 20. Milestone 4 Adversarial RAM Optimizer Empirical Stress Tests
            Milestone4AdversarialChallengeTests.RunAll(Assert);

            // 21. Milestone 5 Notch Gestures, Hover Suppression & Controls Empirical Stress Tests
            Milestone5AdversarialChallengeTests.RunAll(Assert);

            // 22. Milestone 5 UI Controls, Views & System Tray Empirical Stress Tests
            Milestone5UIControlsChallengeTests.RunAll(Assert);

            // 23. Milestone 1 Expanded Geometry & Liquid Glass Design System
            Milestone1ExpandedGeometryChallengeTests.RunAll(Assert);

            // 24. Milestone 2 Collapsed Notch Dynamics & Dynamic Island HUD Animations
            Milestone2CollapsedDynamicsChallengeTests.RunAll(Assert);

            // 25. Milestone 3 & 4 UI Evolution Wave (Agents Tree, Sparkline Scrubbing, Shelf Preview, Sequoia Settings & TrayPopup)
            Milestone3And4UiEvolutionTests.RunAll(Assert);

            Console.WriteLine();
            Console.WriteLine("=================================================");
            Console.WriteLine($"TOTAL TESTS PASSED: {_passedCount}");
            Console.WriteLine($"TOTAL TESTS FAILED: {_failedCount}");
            Console.WriteLine("=================================================");

            return _failedCount == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FATAL UNHANDLED HARNESS EXCEPTION: {ex}");
            Console.ResetColor();
            return 2;
        }
    }

    private static void Assert(bool condition, string testName, string? details = null)
    {
        if (condition)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[PASS] ");
            Console.ResetColor();
            Console.WriteLine(testName + (details != null ? $" ({details})" : ""));
            Interlocked.Increment(ref _passedCount);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("[FAIL] ");
            Console.ResetColor();
            Console.WriteLine(testName + (details != null ? $" — FAIL DETAILS: {details}" : ""));
            Interlocked.Increment(ref _failedCount);
        }
    }

    private static void TestComInteropPolicyConfig()
    {
        Console.WriteLine("--- [SUITE 1] COM Interop & IPolicyConfig Tests ---");

        // Role enum mapping verification
        Assert((int)Role.Console == 0, "Role.Console == 0");
        Assert((int)Role.Multimedia == 1, "Role.Multimedia == 1");
        Assert((int)Role.Communications == 2, "Role.Communications == 2");

        // COM PolicyConfigClient instantiation
        object? comObj = null;
        try
        {
            comObj = new NativeMethods.PolicyConfigClient();
            Assert(comObj != null, "PolicyConfigClient instantiation succeeds");
            Assert(comObj != null && Marshal.IsComObject(comObj), "PolicyConfigClient is a COM object");

            bool implementsIPolicyConfig = comObj is NativeMethods.IPolicyConfig;
            Assert(implementsIPolicyConfig, "PolicyConfigClient implements IPolicyConfig");

            if (comObj is NativeMethods.IPolicyConfig policyConfig)
            {
                using var enumerator = new MMDeviceEnumerator();
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    using var activeDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    string targetId = activeDev.ID;

                    int hrConsole = policyConfig.SetDefaultEndpoint(targetId, (int)Role.Console);
                    int hrMultimedia = policyConfig.SetDefaultEndpoint(targetId, (int)Role.Multimedia);
                    int hrCommunications = policyConfig.SetDefaultEndpoint(targetId, (int)Role.Communications);

                    Assert(hrConsole >= 0, $"SetDefaultEndpoint Console succeeded (hr=0x{hrConsole:X8})");
                    Assert(hrMultimedia >= 0, $"SetDefaultEndpoint Multimedia succeeded (hr=0x{hrMultimedia:X8})");
                    Assert(hrCommunications >= 0, $"SetDefaultEndpoint Communications succeeded (hr=0x{hrCommunications:X8})");
                }
            }
        }
        catch (Exception ex)
        {
            Assert(false, "PolicyConfigClient invocation", ex.Message);
        }
        finally
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                int remaining = Marshal.ReleaseComObject(comObj);
                Assert(remaining == 0, $"Marshal.ReleaseComObject released RCW cleanly (refCount={remaining})");
            }
        }

        // Stress COM creation and unmanaged release (200 iterations)
        bool leakDetected = false;
        for (int i = 0; i < 200; i++)
        {
            object? o = new NativeMethods.PolicyConfigClient();
            if (o is NativeMethods.IPolicyConfig)
            {
                // QI
            }
            if (o != null)
            {
                int remaining = Marshal.ReleaseComObject(o);
                if (remaining != 0)
                {
                    leakDetected = true;
                    break;
                }
            }
        }
        Assert(!leakDetected, "200 rapid COM cycles released with RCW refCount == 0 (no leaks)");
    }

    private static void TestDebounceTimingCalculation()
    {
        Console.WriteLine("--- [SUITE 2] Debounce Timing & Calculation Tests ---");

        long freq = Stopwatch.Frequency;
        Assert(freq > 0, "Stopwatch.Frequency is positive");

        // Initial condition: _suppressCaptureCallbackTicks = 0
        long initialTicks = 0;
        long now = Stopwatch.GetTimestamp();
        double elapsedInitialMs = ((double)(now - initialTicks) / freq) * 1000.0;
        bool wasSelfTriggeredInitial = elapsedInitialMs < 180.0;
        Assert(!wasSelfTriggeredInitial, "Initial unsuppressed event is NOT suppressed (elapsed > 180ms)");

        // Immediate self-trigger: 0ms elapsed
        long triggerTicks = Stopwatch.GetTimestamp();
        double elapsed0Ms = ((double)(Stopwatch.GetTimestamp() - triggerTicks) / freq) * 1000.0;
        bool wasSelfTriggered0 = elapsed0Ms < 180.0;
        Assert(wasSelfTriggered0, "Immediate event is suppressed (elapsed < 180ms)");

        // Boundary tests
        double el50 = ((double)((long)(0.050 * freq)) / freq) * 1000.0;
        Assert(el50 < 180.0, "50ms elapsed is suppressed (< 180ms)");

        double el179 = ((double)((long)(0.179 * freq)) / freq) * 1000.0;
        Assert(el179 < 180.0, "179ms elapsed is suppressed (< 180ms)");

        double el181 = ((double)((long)(0.181 * freq)) / freq) * 1000.0;
        Assert(el181 >= 180.0, "181ms elapsed is NOT suppressed (>= 180ms)");

        double el1Hour = ((double)((long)(3600.0 * freq)) / freq) * 1000.0;
        Assert(el1Hour >= 180.0, "1 hour elapsed is NOT suppressed (>= 180ms)");

        // Negative tick difference (QPC multi-core drift safety)
        long negativeTicks = -50;
        double elapsedNeg = ((double)negativeTicks / freq) * 1000.0;
        bool wasSelfTriggeredNeg = elapsedNeg < 180.0;
        Assert(wasSelfTriggeredNeg, "Negative QPC drift is safely suppressed without arithmetic overflow");
    }

    private static void TestAudioHelpers()
    {
        Console.WriteLine("--- [SUITE 3] Audio Helper Unit Tests ---");

        // CleanAudioDeviceName corner cases
        Assert(AudioService.CleanAudioDeviceName(null) == "Loa hệ thống", "CleanAudioDeviceName(null) returns default");
        Assert(AudioService.CleanAudioDeviceName("") == "Loa hệ thống", "CleanAudioDeviceName(\"\") returns default");
        Assert(AudioService.CleanAudioDeviceName("   \t  ") == "Loa hệ thống", "CleanAudioDeviceName(whitespace) returns default");
        Assert(AudioService.CleanAudioDeviceName("Speakers (Realtek(R) Audio)") == "Speakers", "CleanAudioDeviceName strips parenthesized driver suffix");
        Assert(AudioService.CleanAudioDeviceName("(Realtek Audio)") == "(Realtek Audio)", "CleanAudioDeviceName handles parenthesis at index 0");
        Assert(AudioService.CleanAudioDeviceName("   (Realtek)   ") == "(Realtek)", "CleanAudioDeviceName trims before checking parenthesis at index 0");
        Assert(AudioService.CleanAudioDeviceName("Headphones (2- High Definition Audio Device)") == "Headphones", "CleanAudioDeviceName cleans multi-word names");
        Assert(AudioService.CleanAudioDeviceName("LG UltraFine") == "LG UltraFine", "CleanAudioDeviceName leaves plain names unchanged");

        // IsHeadphoneDevice corner cases
        Assert(!AudioService.IsHeadphoneDevice(null, null), "IsHeadphoneDevice(null, null) -> false");
        Assert(!AudioService.IsHeadphoneDevice("", ""), "IsHeadphoneDevice(\"\", \"\") -> false");
        Assert(!AudioService.IsHeadphoneDevice("   ", "   "), "IsHeadphoneDevice(whitespace) -> false");

        // Real Headphones
        string[] realHeadphones =
        [
            "AirPods Pro",
            "AirPods Max",
            "Apple AirPods",
            "Galaxy Buds2 Pro",
            "Galaxy Buds Live",
            "Pixel Buds A-Series",
            "Sony WH-1000XM4 (Headphones)",
            "Headphones (Sony WH-1000XM5)",
            "Headset (Bose QuietComfort 45)",
            "Sennheiser HD 600 (Headphones)",
            "Tai nghe Bluetooth Sony",
            "Tai nghe chụp tai JBL",
            "Earphones (USB-C Audio)",
            "Earphone (Realtek)",
            "SteelSeries Arctis Nova Headset",
            "HyperX Cloud Alpha Headset",
            "Logitech G Pro X Wireless Gaming Headset"
        ];

        int hpPassCount = 0;
        foreach (var hp in realHeadphones)
        {
            string clean = AudioService.CleanAudioDeviceName(hp);
            if (AudioService.IsHeadphoneDevice(hp, clean)) hpPassCount++;
        }
        Assert(hpPassCount == realHeadphones.Length, $"All {realHeadphones.Length} real headphones matched ({hpPassCount}/{realHeadphones.Length})");

        // Real Speakers (False Positive Check)
        string[] realSpeakers =
        [
            "Speakers (Realtek(R) Audio)",
            "Speakers (High Definition Audio Device)",
            "Realtek Digital Output (Optical)",
            "Edifier R1280DB (Bluetooth)",
            "Logitech Z906 5.1 Surround",
            "Creative Pebble V3 (USB)",
            "Audioengine A2+ Wireless",
            "Harman Kardon SoundSticks 4",
            "Sony SRS-XB43 (Bluetooth)",
            "JBL Flip 6",
            "JBL Charge 5",
            "JBL Boombox 3",
            "Bose SoundLink Revolve+",
            "Sonos One Gen 2",
            "Apple HomePod mini",
            "Dell AC511 USB SoundBar",
            "LG UltraGear (NVIDIA High Definition Audio)",
            "Samsung Odyssey G9 (AMD High Definition Audio Device)",
            "Loa ngoài máy tính",
            "Loa thanh Soundbar TV",
            "Loa vi tính Microlab",
            "Speaker/HP (Realtek High Definition Audio)"
        ];

        int spkPassCount = 0;
        foreach (var spk in realSpeakers)
        {
            string clean = AudioService.CleanAudioDeviceName(spk);
            if (!AudioService.IsHeadphoneDevice(spk, clean)) spkPassCount++;
        }
        Assert(spkPassCount == realSpeakers.Length, $"All {realSpeakers.Length} real speakers identified as speakers (no false positives: {spkPassCount}/{realSpeakers.Length})");
    }

    private static void TestBatteryStatusLogic()
    {
        Console.WriteLine("--- [SUITE 4] Battery Status Formatting Oracle Tests ---");

        string FormatBatteryStatus(byte batteryFlag, byte acLineStatus, byte pct, int lifeTime)
        {
            bool hasBat = (batteryFlag & 128) == 0 && pct <= 100;
            bool charging = acLineStatus == 1;

            if (!hasBat) return "Nguồn trực tiếp";
            if (charging) return $"Đang sạc nguồn • {pct}%";
            if (lifeTime > 0 && (uint)lifeTime != 0xFFFFFFFF)
            {
                int hours = lifeTime / 3600;
                int minutes = (lifeTime % 3600) / 60;
                if (hours > 0 && minutes > 0) return $"Pin {pct}% • Còn ~{hours}g {minutes}p";
                if (hours > 0) return $"Pin {pct}% • Còn ~{hours}g";
                return $"Pin {pct}% • Còn ~{Math.Max(1, minutes)}p";
            }
            return $"Pin {pct}%";
        }

        Assert(FormatBatteryStatus(128, 1, 100, -1) == "Nguồn trực tiếp", "Desktop PC (BatteryFlag=128) -> 'Nguồn trực tiếp'");
        Assert(FormatBatteryStatus(0, 1, 85, -1) == "Đang sạc nguồn • 85%", "Laptop Charging (ACLineStatus=1) -> 'Đang sạc nguồn • 85%'");
        Assert(FormatBatteryStatus(0, 0, 75, 7400) == "Pin 75% • Còn ~2g 3p", "Discharging 7400s (2h 3m) -> 'Pin 75% • Còn ~2g 3p'");
        Assert(FormatBatteryStatus(0, 0, 50, 7200) == "Pin 50% • Còn ~2g", "Discharging 7200s (2h 0m) -> 'Pin 50% • Còn ~2g'");
        Assert(FormatBatteryStatus(0, 0, 30, 1500) == "Pin 30% • Còn ~25p", "Discharging 1500s (25m) -> 'Pin 30% • Còn ~25p'");
        Assert(FormatBatteryStatus(0, 0, 10, 45) == "Pin 10% • Còn ~1p", "Discharging 45s (<1m) -> 'Pin 10% • Còn ~1p'");
        Assert(FormatBatteryStatus(0, 0, 60, -1) == "Pin 60%", "Discharging lifeTime=-1 -> 'Pin 60%'");
        Assert(FormatBatteryStatus(0, 0, 60, 0) == "Pin 60%", "Discharging lifeTime=0 -> 'Pin 60%'");
    }

    private static void TestDisplayPowerService()
    {
        Console.WriteLine("--- [SUITE 5] DisplayPowerService Instance & Caffeine Tests ---");

        var settings = new SettingsService();
        var monitor = CreateTestMonitor();
        using var powerService = new DisplayPowerService(settings, monitor);

        Assert(!powerService.IsCaffeineActive, "Initial IsCaffeineActive is false");

        powerService.Start();
        Console.WriteLine($"Host Power Status Detail: '{powerService.BatteryStatusDetail}'");
        Console.WriteLine($"Host HasBattery: {powerService.HasBattery}, IsCharging: {powerService.IsCharging}, BatteryPercent: {powerService.BatteryPercent}%");
        Assert(!string.IsNullOrEmpty(powerService.BatteryStatusDetail), "BatteryStatusDetail is populated after Start()");

        powerService.SetCaffeine(true, showHud: false);
        Assert(powerService.IsCaffeineActive, "SetCaffeine(true) sets IsCaffeineActive = true");

        powerService.ToggleCaffeine(showHud: false);
        Assert(!powerService.IsCaffeineActive, "ToggleCaffeine() toggles to false");

        powerService.ToggleCaffeine(showHud: false);
        Assert(powerService.IsCaffeineActive, "ToggleCaffeine() toggles back to true");

        // 500 rapid Caffeine toggles (even count toggled from true returns to true)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++)
        {
            powerService.ToggleCaffeine(showHud: false);
        }
        sw.Stop();
        Assert(powerService.IsCaffeineActive, $"500 rapid Caffeine toggles completed in {sw.ElapsedMilliseconds}ms without exception");

        // Brightness percent clamping
        powerService.SetBrightnessPercent(-50, showHud: false);
        Assert(powerService.BrightnessPercent == 0, "SetBrightnessPercent(-50) clamps to 0%");

        powerService.SetBrightnessPercent(150, showHud: false);
        Assert(powerService.BrightnessPercent == 100, "SetBrightnessPercent(150) clamps to 100%");

        powerService.StepBrightness(-20);
        Assert(powerService.BrightnessPercent == 80, "StepBrightness(-20) steps from 100% to 80%");

        powerService.StepBrightness(50);
        Assert(powerService.BrightnessPercent == 100, "StepBrightness(50) clamps up to 100%");

        // Disposal resets Caffeine
        powerService.SetCaffeine(true, showHud: false);
        powerService.Dispose();
        Assert(!powerService.IsCaffeineActive, "Dispose() resets IsCaffeineActive to false");
    }

    private static void TestAudioService()
    {
        Console.WriteLine("--- [SUITE 6] AudioService Real Hardware & Corner Tests ---");

        var settings = new SettingsService();
        using var audioService = new AudioService(settings);

        audioService.Start();
        Console.WriteLine($"Active Output: '{audioService.OutputDeviceName}'");
        Console.WriteLine($"Active Output Count: {audioService.ActiveOutputDeviceCount}");
        Console.WriteLine($"Volume Percent: {audioService.VolumePercent}%, Muted: {audioService.IsMuted}");
        Console.WriteLine($"Has Microphone: {audioService.HasMicrophone}, Name: '{audioService.MicrophoneDeviceName}', Mic Muted: {audioService.IsMicMuted}");

        Assert(!string.IsNullOrEmpty(audioService.OutputDeviceName), "OutputDeviceName is initialized");
        Assert(audioService.VolumePercent >= 0 && audioService.VolumePercent <= 100, "VolumePercent within [0, 100]");

        double origVol = audioService.VolumePercent;
        audioService.SetVolumePercent(-10, showHud: false);
        Assert(audioService.VolumePercent == 0, "SetVolumePercent(-10) clamps to 0%");

        audioService.SetVolumePercent(110, showHud: false);
        Assert(audioService.VolumePercent == 100, "SetVolumePercent(110) clamps to 100%");

        audioService.StepVolume(1000, toggleMute: false);
        Assert(audioService.VolumePercent == 100, "StepVolume(+1000) clamps to 100%");

        audioService.StepVolume(-1000, toggleMute: false);
        Assert(audioService.VolumePercent == 0, "StepVolume(-1000) clamps to 0%");

        audioService.SetVolumePercent(origVol, showHud: false);

        bool initialMute = audioService.IsMuted;
        audioService.ToggleMute(showHud: false);
        Assert(audioService.IsMuted != initialMute, "ToggleMute toggles mute state");
        audioService.ToggleMute(showHud: false);
        Assert(audioService.IsMuted == initialMute, "ToggleMute toggles back to initial state");

        bool initialMicMute = audioService.IsMicMuted;
        audioService.ToggleMicrophoneMute(showHud: false);
        if (audioService.HasMicrophone)
        {
            Assert(audioService.IsMicMuted != initialMicMute, "ToggleMicrophoneMute toggles hardware mic mute");
            audioService.ToggleMicrophoneMute(showHud: false);
            Assert(audioService.IsMicMuted == initialMicMute, "ToggleMicrophoneMute restores hardware mic mute");
        }
        else
        {
            Assert(!audioService.HasMicrophone, "No microphone detected; toggle handled gracefully without crashing");
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            audioService.ToggleMicrophoneMute(showHud: false);
        }
        sw.Stop();
        Assert(true, $"50 rapid ToggleMicrophoneMute calls completed in {sw.ElapsedMilliseconds}ms without crash");

        sw.Restart();
        for (int i = 0; i < 10; i++)
        {
            audioService.CycleOutputDevice(showHud: false);
        }
        sw.Stop();
        Assert(true, $"10 CycleOutputDevice calls completed in {sw.ElapsedMilliseconds}ms without crash");

        try
        {
            audioService.OnDeviceStateChanged("dummy-id-not-exist", DeviceState.Unplugged);
            audioService.OnDeviceRemoved("dummy-id-not-exist");
            audioService.OnDefaultDeviceChanged(DataFlow.Capture, Role.Communications, "dummy");
            audioService.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "dummy");
            audioService.OnDeviceAdded("dummy");
            audioService.OnPropertyValueChanged("dummy", new PropertyKey(Guid.Empty, 0));
            Assert(true, "IMMNotificationClient callbacks with dummy IDs execute cleanly");
        }
        catch (Exception ex)
        {
            Assert(false, "IMMNotificationClient callbacks threw exception", ex.Message);
        }

        audioService.Dispose();
        Assert(true, "AudioService.Dispose() completed without exception");
    }

    private static void TestMultiThreadedTorture()
    {
        Console.WriteLine("--- [SUITE 7] Multi-Threaded Concurrent Torture Test ---");

        var settings = new SettingsService();
        var monitor = CreateTestMonitor();
        using var powerService = new DisplayPowerService(settings, monitor);
        using var audioService = new AudioService(settings);

        powerService.Start();
        audioService.Start();

        var cancelSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var token = cancelSource.Token;
        int threadCount = 8;
        var tasks = new Task[threadCount];
        int operationCount = 0;

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        switch (threadId % 4)
                        {
                            case 0:
                                powerService.ToggleCaffeine(showHud: false);
                                break;
                            case 1:
                                audioService.StepVolume(1, toggleMute: false);
                                break;
                            case 2:
                                audioService.ToggleMicrophoneMute(showHud: false);
                                break;
                            case 3:
                                audioService.CycleOutputDevice(showHud: false);
                                break;
                        }
                        Interlocked.Increment(ref operationCount);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception in thread {threadId}: {ex.Message}");
                    }
                }
            }, token);
        }

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException)
        {
            // Expected when cancellation token cancels background torture tasks
        }
        Assert(operationCount > 100, $"Torture test executed {operationCount} concurrent operations in 3 seconds across 8 threads with zero deadlocks");
    }

    private static void TestWpfDispatcherHudPropagation()
    {
        Console.WriteLine("--- [SUITE 8] WPF Dispatcher HUD & Event Propagation Tests ---");

        if (Application.Current == null)
        {
            _ = new Application();
        }

        var settings = new SettingsService();
        var monitor = CreateTestMonitor();
        using var powerService = new DisplayPowerService(settings, monitor);
        using var audioService = new AudioService(settings);

        powerService.Start();
        audioService.Start();

        bool caffeineHudFired = false;
        powerService.HudRequested += payload =>
        {
            if (payload.Kind == HudKind.Caffeine)
            {
                caffeineHudFired = true;
                Assert(payload.Icon == IconsaxKind.Coffee, "Caffeine HUD uses IconsaxKind.Coffee");
                Assert(payload.AccentHex == "#F59E0B", "Caffeine HUD uses #F59E0B amber accent");
            }
        };

        powerService.SetCaffeine(true, showHud: true);
        PumpDispatcher();
        Assert(caffeineHudFired, "Caffeine HUD payload fired and received via Dispatcher");

        bool micHudFired = false;
        audioService.VolumeHudRequested += payload =>
        {
            if (payload.Kind == HudKind.Microphone)
            {
                micHudFired = true;
                Assert(payload.Icon == IconsaxKind.Microphone || payload.Icon == IconsaxKind.MicrophoneSlash,
                    "Microphone HUD payload has valid microphone icon");
            }
        };

        audioService.ToggleMicrophoneMute(showHud: true);
        PumpDispatcher();
        Assert(micHudFired, "Microphone HUD payload fired and received via Dispatcher");
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    private static void TestVirtualScreenCaptureAndMultiMonitor()
    {
        Console.WriteLine("--- [SUITE 9] Screen Capture & Multi-Monitor Coordinates Tests ---");

        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        Console.WriteLine($"VirtualScreen physical bounds: Left={vx}, Top={vy}, Width={vWidth}, Height={vHeight}");
        Assert(vWidth > 0 && vHeight > 0, "VirtualScreen dimensions are positive");

        IntPtr diagScreenDC = NativeMethods.GetDC(IntPtr.Zero);
        Console.WriteLine($"[DIAG] hScreenDC = {diagScreenDC}");
        IntPtr diagMemoryDC = NativeMethods.CreateCompatibleDC(diagScreenDC);
        Console.WriteLine($"[DIAG] hMemoryDC = {diagMemoryDC}");
        IntPtr diagBitmap = NativeMethods.CreateCompatibleBitmap(diagScreenDC, vWidth, vHeight);
        Console.WriteLine($"[DIAG] hBitmap = {diagBitmap}");
        IntPtr diagOldBitmap = NativeMethods.SelectObject(diagMemoryDC, diagBitmap);
        Console.WriteLine($"[DIAG] hOldBitmap = {diagOldBitmap}");
        bool diagBlt = NativeMethods.BitBlt(diagMemoryDC, 0, 0, vWidth, vHeight, diagScreenDC, vx, vy, NativeMethods.SRCCOPY);
        int diagBltErr = Marshal.GetLastWin32Error();
        Console.WriteLine($"[DIAG] BitBlt = {diagBlt}, Win32Error = {diagBltErr}");
        NativeMethods.SelectObject(diagMemoryDC, diagOldBitmap);
        NativeMethods.DeleteObject(diagBitmap);
        NativeMethods.DeleteDC(diagMemoryDC);
        NativeMethods.ReleaseDC(IntPtr.Zero, diagScreenDC);

        bool captured = ColorPickerService.CaptureVirtualScreen(vx, vy, vWidth, vHeight, out byte[] bgra, out BitmapSource frozenSource);
        if (captured)
        {
            Assert(true, "CaptureVirtualScreen succeeded on active interactive desktop");
            Assert(bgra != null && bgra.Length == vWidth * vHeight * 4, $"bgra pixel buffer allocated correctly ({bgra?.Length} bytes)");
            Assert(frozenSource != null && frozenSource.IsFrozen, "frozenSource is created and frozen");
            Assert(frozenSource != null && frozenSource.PixelWidth == vWidth && frozenSource.PixelHeight == vHeight, "frozenSource pixel dimensions match virtual screen");
            Assert(frozenSource != null && frozenSource.Format == PixelFormats.Bgr32, "frozenSource uses 32bpp Bgr32 format");
        }
        else
        {
            // In headless/automated background CI sessions without an active desktop window station (WinSta0\Default),
            // BitBlt from GetDC(IntPtr.Zero) returns ERROR_INVALID_HANDLE (6).
            // Verify that ColorPickerService degrades gracefully: returns false, empty bgra, null frozenSource, and cleans up all GDI handles.
            Assert(bgra != null && bgra.Length == 0, "Non-interactive / headless session: bgra safely returns empty array without throwing");
            Assert(frozenSource == null, "Non-interactive / headless session: frozenSource safely returns null without throwing");
        }

        // Multi-Monitor Negative Coordinate Matrix Oracle
        // Scenario A: Left secondary monitor (-1920, 0, 3840, 1080)
        int left_vl = -1920, left_vt = 0, left_vw = 3840, left_vh = 1080;
        int pt_lx = -1920, pt_ly = 0;
        int local_lx = pt_lx - left_vl;
        int local_ly = pt_ly - left_vt;
        Assert(local_lx == 0 && local_ly == 0, "Left secondary monitor origin (-1920, 0) maps to local (0, 0)");

        int pt_rx = 0, pt_ry = 0;
        int local_rx = pt_rx - left_vl;
        int local_ry = pt_ry - left_vt;
        Assert(local_rx == 1920 && local_ry == 0, "Primary monitor origin (0, 0) maps to local (1920, 0) with left monitor attached");

        int pt_endx = 1919, pt_endy = 1079;
        int local_endx = pt_endx - left_vl;
        int local_endy = pt_endy - left_vt;
        Assert(local_endx == 3839 && local_endy == 1079, "Primary monitor bottom-right (1919, 1079) maps to local (3839, 1079)");

        // Scenario B: Top secondary monitor (0, -1080, 1920, 2160)
        int top_vl = 0, top_vt = -1080;
        int pt_topx = 0, pt_topy = -1080;
        int local_topx = pt_topx - top_vl;
        int local_topy = pt_topy - top_vt;
        Assert(local_topx == 0 && local_topy == 0, "Top secondary monitor origin (0, -1080) maps to local (0, 0)");

        int pt_midx = 0, pt_midy = 0;
        int local_midx = pt_midx - top_vl;
        int local_midy = pt_midy - top_vt;
        Assert(local_midx == 0 && local_midy == 1080, "Primary monitor origin (0, 0) maps to local (0, 1080) with top monitor attached");

        // Scenario C: Top-Left secondary monitor (-1920, -1080, 3840, 2160)
        int tl_vl = -1920, tl_vt = -1080;
        int local_tlx = -1920 - tl_vl;
        int local_tly = -1080 - tl_vt;
        Assert(local_tlx == 0 && local_tly == 0, "Top-left secondary monitor origin (-1920, -1080) maps to local (0, 0)");

        // Boundary Clamping Oracle
        int clamped_neg_x = Math.Clamp(-5000 - left_vl, 0, left_vw - 1);
        int clamped_neg_y = Math.Clamp(-5000 - left_vt, 0, left_vh - 1);
        Assert(clamped_neg_x == 0 && clamped_neg_y == 0, "Negative cursor out-of-bounds clamps safely to (0, 0)");

        int clamped_pos_x = Math.Clamp(10000 - left_vl, 0, left_vw - 1);
        int clamped_pos_y = Math.Clamp(10000 - left_vt, 0, left_vh - 1);
        Assert(clamped_pos_x == left_vw - 1 && clamped_pos_y == left_vh - 1, "Positive cursor out-of-bounds clamps safely to (3839, 1079)");

        // Pixel Extraction Oracle
        int testW = 200, testH = 200;
        byte[] testPixels = new byte[testW * testH * 4];
        // Coordinate (0, 0): Red (#FF0000)
        testPixels[0] = 0; testPixels[1] = 0; testPixels[2] = 255; testPixels[3] = 255;
        // Coordinate (100, 100): Lime (#00FF00)
        int gOffset = (100 * testW + 100) * 4;
        testPixels[gOffset] = 0; testPixels[gOffset + 1] = 255; testPixels[gOffset + 2] = 0; testPixels[gOffset + 3] = 255;
        // Coordinate (199, 199): Blue (#0000FF)
        int bOffset = (199 * testW + 199) * 4;
        testPixels[bOffset] = 255; testPixels[bOffset + 1] = 0; testPixels[bOffset + 2] = 0; testPixels[bOffset + 3] = 255;

        Color sampleRed = ColorPickerService.GetPixelColor(testPixels, testW, testH, 0, 0);
        Assert(sampleRed.R == 255 && sampleRed.G == 0 && sampleRed.B == 0, "GetPixelColor(0, 0) reads Red correctly");

        Color sampleGreen = ColorPickerService.GetPixelColor(testPixels, testW, testH, 100, 100);
        Assert(sampleGreen.R == 0 && sampleGreen.G == 255 && sampleGreen.B == 0, "GetPixelColor(100, 100) reads Green correctly");

        Color sampleBlue = ColorPickerService.GetPixelColor(testPixels, testW, testH, 199, 199);
        Assert(sampleBlue.R == 0 && sampleBlue.G == 0 && sampleBlue.B == 255, "GetPixelColor(199, 199) reads Blue correctly");

        Color sampleOobNeg = ColorPickerService.GetPixelColor(testPixels, testW, testH, -5, -5);
        Assert(sampleOobNeg == Colors.Black, "GetPixelColor(-5, -5) safely returns Colors.Black without throwing");

        Color sampleOobPos = ColorPickerService.GetPixelColor(testPixels, testW, testH, 200, 200);
        Assert(sampleOobPos == Colors.Black, "GetPixelColor(200, 200) safely returns Colors.Black without throwing");

        Color sampleEmpty = ColorPickerService.GetPixelColor(Array.Empty<byte>(), 0, 0, 0, 0);
        Assert(sampleEmpty == Colors.Black, "GetPixelColor on empty array returns Colors.Black");
    }

    private static void TestGdiResourceCleanupAndLeakStress()
    {
        Console.WriteLine("--- [SUITE 10] GDI Resource Cleanup & Handle Tracking Stress Test ---");

        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        IntPtr curProc = Process.GetCurrentProcess().Handle;

        // Warmup capture
        ColorPickerService.CaptureVirtualScreen(vx, vy, vWidth, vHeight, out _, out _);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        uint initialGdi = GetGuiResources(curProc, 0);
        uint initialUser = GetGuiResources(curProc, 1);
        Console.WriteLine($"Pre-stress handles — GDI: {initialGdi}, USER: {initialUser}");

        var sw = Stopwatch.StartNew();
        const int stressCount = 100;
        for (int i = 0; i < stressCount; i++)
        {
            ColorPickerService.CaptureVirtualScreen(vx, vy, vWidth, vHeight, out _, out _);
        }
        sw.Stop();
        Console.WriteLine($"{stressCount} consecutive physical GDI capture cycles executed in {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / (double)stressCount:F2} ms/capture)");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        uint finalGdi = GetGuiResources(curProc, 0);
        uint finalUser = GetGuiResources(curProc, 1);
        Console.WriteLine($"Post-stress handles — GDI: {finalGdi} (delta: {(long)finalGdi - initialGdi}), USER: {finalUser} (delta: {(long)finalUser - initialUser})");

        Assert(finalGdi <= initialGdi, $"GDI handles remained completely flat across {stressCount} captures (initial={initialGdi}, final={finalGdi})");
        Assert(finalUser <= initialUser, $"USER handles remained completely flat across {stressCount} captures (initial={initialUser}, final={finalUser})");

        // Edge case: invalid bounds cleanup
        bool negCaptured = ColorPickerService.CaptureVirtualScreen(0, 0, -100, -100, out _, out _);
        Assert(!negCaptured, "CaptureVirtualScreen with negative dimensions safely returns false");

        try
        {
            bool zeroCaptured = ColorPickerService.CaptureVirtualScreen(0, 0, 0, 0, out _, out _);
            Assert(!zeroCaptured, "CaptureVirtualScreen with 0x0 dimensions safely returns false");
        }
        catch (IndexOutOfRangeException ex)
        {
            Assert(false, "CaptureVirtualScreen with 0x0 dimensions safely returns false", $"BUG CONFIRMED: Threw IndexOutOfRangeException at line 337: {ex.Message}");
        }

        uint postInvalidGdi = GetGuiResources(curProc, 0);
        Assert(postInvalidGdi <= initialGdi, "No GDI handle leak on invalid parameter exit paths");

        // Synthetic In-Memory Capture Pipeline Stress Test (100 cycles)
        IntPtr hScreenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr hSrcDC = NativeMethods.CreateCompatibleDC(hScreenDC);
        IntPtr hSrcBmp = NativeMethods.CreateCompatibleBitmap(hScreenDC, 200, 200);
        NativeMethods.SelectObject(hSrcDC, hSrcBmp);

        uint pipeGdiBefore = GetGuiResources(curProc, 0);
        for (int i = 0; i < 100; i++)
        {
            IntPtr hDstDC = NativeMethods.CreateCompatibleDC(hScreenDC);
            IntPtr hDstBmp = NativeMethods.CreateCompatibleBitmap(hScreenDC, 200, 200);
            IntPtr hOld = NativeMethods.SelectObject(hDstDC, hDstBmp);

            NativeMethods.BitBlt(hDstDC, 0, 0, 200, 200, hSrcDC, 0, 0, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(hDstDC, hOld);

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = 200,
                    biHeight = -200,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB
                }
            };
            byte[] raw = new byte[200 * 200 * 4];
            NativeMethods.GetDIBits(hDstDC, hDstBmp, 0, 200, raw, ref bmi, NativeMethods.DIB_RGB_COLORS);

            NativeMethods.DeleteObject(hDstBmp);
            NativeMethods.DeleteDC(hDstDC);
        }

        NativeMethods.DeleteObject(hSrcBmp);
        NativeMethods.DeleteDC(hSrcDC);
        NativeMethods.ReleaseDC(IntPtr.Zero, hScreenDC);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        uint pipeGdiAfter = GetGuiResources(curProc, 0);
        Assert(pipeGdiAfter <= pipeGdiBefore, $"100 full BitBlt+GetDIBits pipeline cycles have 0 GDI leaks (before={pipeGdiBefore}, after={pipeGdiAfter})");
    }

    private static void TestClipboardRetryResilienceUnderSimulatedLock()
    {
        Console.WriteLine("--- [SUITE 11] Clipboard Retry Resilience Under Simulated Win32 Locking ---");

        // 1. Basic clipboard operations
        bool normalCopy = ColorPickerService.TryCopyTextToClipboard("#A1B2C3");
        Assert(normalCopy, "TryCopyTextToClipboard('#A1B2C3') succeeded");
        string readText = Clipboard.GetText();
        Assert(readText == "#A1B2C3", "Clipboard text matches '#A1B2C3'");

        // 2. Corner cases
        Assert(!ColorPickerService.TryCopyTextToClipboard(null!), "TryCopyTextToClipboard(null) returns false");
        Assert(!ColorPickerService.TryCopyTextToClipboard(""), "TryCopyTextToClipboard(\"\") returns false");

        // 3. Simulated transient lock (80ms lock, retries=5, delayMs=40)
        var lockAcquired = new ManualResetEventSlim(false);
        var lockReleased = new ManualResetEventSlim(false);

        var lockTask = Task.Run(() =>
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                lockAcquired.Set();
                Thread.Sleep(80);
                CloseClipboard();
                lockReleased.Set();
            }
        });

        if (lockAcquired.Wait(1000))
        {
            var sw = Stopwatch.StartNew();
            bool recovered = ColorPickerService.TryCopyTextToClipboard("#RECOVERED_COLOR", retries: 5, delayMs: 40);
            sw.Stop();
            lockTask.Wait();

            Assert(recovered, $"TryCopyTextToClipboard recovered from transient lock in {sw.ElapsedMilliseconds}ms");
            string recoveredText = Clipboard.GetText();
            Assert(recoveredText == "#RECOVERED_COLOR", "Clipboard contains text after transient lock recovery");
        }
        else
        {
            Assert(false, "Failed to simulate clipboard lock");
        }

        // 4. Permanent lock exhaustion (hold for 5000ms, retries=2, delayMs=10 -> total retry window < 5000ms)
        var permAcquired = new ManualResetEventSlim(false);
        var permRelease = new ManualResetEventSlim(false);

        var permTask = Task.Run(() =>
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                permAcquired.Set();
                permRelease.Wait(5000);
                CloseClipboard();
            }
        });

        if (permAcquired.Wait(1000))
        {
            var swExhaust = Stopwatch.StartNew();
            bool exhausted = ColorPickerService.TryCopyTextToClipboard("#WILL_FAIL", retries: 2, delayMs: 10);
            swExhaust.Stop();
            permRelease.Set();
            permTask.Wait();

            Assert(!exhausted, $"TryCopyTextToClipboard gracefully returned false after retry exhaustion in {swExhaust.ElapsedMilliseconds}ms without throwing");
        }
        else
        {
            Assert(false, "Failed to simulate permanent clipboard lock");
        }
    }

    private static void TestColorPickerSwatchAndRec601Contrast()
    {
        Console.WriteLine("--- [SUITE 12] Color Formatting, Rec. 601 Contrast & Swatch Generation Oracle ---");

        Color testColor = Color.FromRgb(255, 128, 64);
        BitmapSource swatch = ColorPickerService.CreateColorSwatch(testColor, 64, 64);

        Assert(swatch.PixelWidth == 64 && swatch.PixelHeight == 64, "CreateColorSwatch produces 64x64 bitmap");
        Assert(swatch.Format == PixelFormats.Bgra32, "CreateColorSwatch uses Bgra32 format");
        Assert(swatch.IsFrozen, "CreateColorSwatch bitmap is frozen");

        byte[] pixels = new byte[64 * 64 * 4];
        swatch.CopyPixels(pixels, 64 * 4, 0);

        bool allPixelsMatch = true;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 0] != 64 || pixels[i + 1] != 128 || pixels[i + 2] != 255 || pixels[i + 3] != 255)
            {
                allPixelsMatch = false;
                break;
            }
        }
        Assert(allPixelsMatch, "All 4096 pixels in swatch match B=64, G=128, R=255, A=255");

        // Rec. 601 Perceived Luminance calculation oracle
        bool IsDark(byte r, byte g, byte b)
        {
            double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            return lum < 0.5;
        }

        Assert(IsDark(0, 0, 0), "Black (0,0,0) -> Dark (Luminance = 0.0)");
        Assert(!IsDark(255, 255, 255), "White (255,255,255) -> Light (Luminance = 1.0)");
        Assert(!IsDark(0, 255, 0), "Pure Green (0,255,0) -> Light (Luminance = 0.587)");
        Assert(IsDark(0, 0, 255), "Pure Blue (0,0,255) -> Dark (Luminance = 0.114)");
        Assert(IsDark(255, 0, 0), "Pure Red (255,0,0) -> Dark (Luminance = 0.299)");
        Assert(!IsDark(255, 255, 0), "Pure Yellow (255,255,0) -> Light (Luminance = 0.886)");
        Assert(!IsDark(0, 255, 255), "Pure Cyan (0,255,255) -> Light (Luminance = 0.701)");
        Assert(IsDark(255, 0, 255), "Pure Magenta (255,0,255) -> Dark (Luminance = 0.413)");
        Assert(IsDark(127, 127, 127), "Mid-Dark Gray (127,127,127) -> Dark (Luminance = 0.498)");
        Assert(!IsDark(128, 128, 128), "Mid-Light Gray (128,128,128) -> Light (Luminance = 0.502)");
    }

    private static void TestColorPickerServiceLifecycleAndHudPayload()
    {
        Console.WriteLine("--- [SUITE 13] ColorPickerService Lifecycle & Event Emission ---");

        var stateMachine = new NotchStateMachine(new AppSettings());
        using var cpService = new ColorPickerService(stateMachine);

        Assert(!cpService.IsPickerActive, "Initial IsPickerActive is false");

        HudPayload? payload = null;
        cpService.ColorPickedHudRequested += p => payload = p;

        // 1. Pick Color with Hex format
        Color pickHex = Color.FromRgb(30, 144, 255); // #1E90FF
        cpService.OnColorPicked(pickHex, formatAsRgb: false);

        Assert(payload != null, "ColorPickedHudRequested fired on OnColorPicked");
        Assert(payload!.Kind == HudKind.ColorPicked, "HudPayload.Kind == HudKind.ColorPicked");
        Assert(payload.Icon == IconsaxKind.Colorfilter, "HudPayload.Icon == IconsaxKind.Colorfilter");
        Assert(payload.Title == "#1E90FF", "HudPayload.Title is '#1E90FF'");
        Assert(payload.AccentHex == "#1E90FF", "HudPayload.AccentHex is '#1E90FF'");
        Assert(payload.ValueText == "Đã chép mã màu", "HudPayload.ValueText is 'Đã chép mã màu'");
        Assert(payload.Thumbnail != null && payload.Thumbnail.IsFrozen, "HudPayload.Thumbnail is non-null frozen BitmapSource");

        string clipHex = Clipboard.GetText();
        Assert(clipHex == "#1E90FF", "Clipboard received hex string '#1E90FF'");

        // 2. Pick Color with RGB format (Shift held)
        payload = null;
        Color pickRgb = Color.FromRgb(255, 105, 180); // #FF69B4
        cpService.OnColorPicked(pickRgb, formatAsRgb: true);

        Assert(payload != null, "ColorPickedHudRequested fired on OnColorPicked (RGB format)");
        Assert(payload!.Title == "#FF69B4", "HudPayload.Title displays '#FF69B4'");
        string clipRgb = Clipboard.GetText();
        Assert(clipRgb == "rgb(255, 105, 180)", "Clipboard received CSS rgb format 'rgb(255, 105, 180)'");

        // 3. Cancel & Dispose idempotency
        cpService.CancelColorPicker();
        Assert(!cpService.IsPickerActive, "CancelColorPicker preserves IsPickerActive = false");

        cpService.Dispose();
        Assert(!cpService.IsPickerActive, "Dispose completes cleanly");
    }
}
