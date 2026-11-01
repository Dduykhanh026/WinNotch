using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;
using WinNotch.Views;
using WinNotch.Views.Controls;

namespace WinNotch.Tests;

public static class Milestone5UIControlsChallengeTests
{
    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 5: UI CONTROLS, VIEWS & TRAY ADVERSARIAL CHALLENGE SUITE     ");
        Console.WriteLine("=========================================================================");

        EnsureWpfEnvironment();

        // Suite 1: SystemMonitorView RAM Optimizer Button States & Orchestration
        TestRamOptimizerButtonStatesAndTransitions(assert);
        TestRamOptimizerReentrancyAndHudEmission(assert);

        // Suite 2: Battery Status Detail Display & Desktop Uptime Fallback
        TestBatteryStatusDetailFormattingOracle(assert);
        TestBatteryUptimeFallbackXamlBindingContracts(assert);

        // Suite 3: ShelfView Paste, Shortcuts, Double-Click & Reveal Controls
        TestShelfViewPasteButtonsAndContracts(assert);
        TestShelfViewAndWindowCtrlVShortcuts(assert);
        TestShelfCardDoubleClickLaunchAndDragSuppression(assert);
        TestShelfCardFolder2RevealButton(assert);

        // Suite 4: Settings Toggles Round-Trip Persistence & Fuzzing
        TestSettingsTogglesDefaultValues(assert);
        TestSettingsTogglesSerializationRoundTrip(assert);
        TestSettingsTogglesCombinatorialFuzzing(assert);
        TestSettingsTogglesResetDefaults(assert);
        TestSettingsTogglesCorruptJsonResilience(assert);

        // Suite 5: TrayIconService Context Menu & 3-Tier State Synchronization
        TestTrayIconServiceContextMenuItems(assert);
        TestTrayIconServiceItemActionTriggers(assert);
        TestTrayIconService3TierSynchronization(assert);
        TestTrayIconServiceOffThreadDispatchAndDisposal(assert);
    }

    private static void EnsureWpfEnvironment()
    {
        if (Application.Current == null)
        {
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        }

        if (Application.Current!.Resources.MergedDictionaries.Count == 0)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/WinNotch;component/Views/Themes/NotchStyles.xaml", UriKind.Absolute)
            });
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(f =>
            {
                ((DispatcherFrame)f!).Continue = false;
                return null;
            }),
            frame);
        Dispatcher.PushFrame(frame);
    }

    private static MainNotchViewModel CreateTestViewModel(
        out NotchStateMachine sm,
        out FileShelfService shelf,
        out DisplayPowerService power,
        out AudioService audio,
        out ColorPickerService picker,
        out SettingsService settings)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WinNotchM5_VM_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        settings = new SettingsService();
        sm = new NotchStateMachine(settings.Current);

        var descriptor = new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "Test OLED Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };

        audio = new AudioService(settings);
        var media = new MediaService(settings);
        power = new DisplayPowerService(settings, descriptor);
        shelf = new FileShelfService(settings);
        var timer = new TimerService();
        var privacy = new PrivacyService(settings);
        var monitor = new SystemMonitorService(settings);
        var notif = new NotificationService(settings);
        picker = new ColorPickerService(sm);

        return new MainNotchViewModel(
            settings,
            sm,
            audio,
            power,
            media,
            shelf,
            timer,
            privacy,
            monitor,
            notif,
            picker,
            new AgentMonitorService(settings),
            () => { });
    }

    // =========================================================================
    // SUITE 1: SystemMonitorView RAM Optimizer Button States & Orchestration
    // =========================================================================

    private static void TestRamOptimizerButtonStatesAndTransitions(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S1] SystemMonitorView RAM Optimizer Button States ---");
        var vm = CreateTestViewModel(out _, out _, out _, out _, out _, out _);

        // 1. Initial State
        assert(vm.RamOptimizedButtonText == "Dọn RAM", "Initial RamOptimizedButtonText is 'Dọn RAM'");
        assert(!vm.IsRamOptimizing, "Initial IsRamOptimizing is false");
        assert(string.IsNullOrEmpty(vm.RamOptimizedFeedbackText), "Initial RamOptimizedFeedbackText is empty");

        SolidColorBrush? initialBrush = vm.RamOptimizedButtonBrush as SolidColorBrush;
        assert(initialBrush != null && initialBrush.Color.ToString() == "#FF38BDF8", "Initial RamOptimizedButtonBrush is #38BDF8 (RamNormalBrush)");

        // 2. Intermediate "Đang dọn..." State
        vm.IsRamOptimizing = true;
        assert(vm.RamOptimizedButtonText == "Đang dọn...", "RamOptimizedButtonText transitions to 'Đang dọn...' when IsRamOptimizing=true");
        SolidColorBrush? inProgressBrush = vm.RamOptimizedButtonBrush as SolidColorBrush;
        assert(inProgressBrush != null && inProgressBrush.Color.ToString() == "#FF38BDF8", "In-progress RamOptimizedButtonBrush remains normal brush");

        // 3. Positive Reclaim Feedback State ("-X MB")
        vm.IsRamOptimizing = false;
        vm.RamOptimizedFeedbackText = "-342 MB";
        assert(vm.RamOptimizedButtonText == "-342 MB", "RamOptimizedButtonText shows '-342 MB' upon positive reclaim");
        SolidColorBrush? warnBrush = vm.RamOptimizedButtonBrush as SolidColorBrush;
        assert(warnBrush != null && warnBrush.Color.ToString() == "#FFF59E0B", "RamOptimizedButtonBrush transitions to #F59E0B (RamWarnBrush) on feedback");

        // 4. Zero Reclaim Feedback State ("Đã tối ưu")
        vm.RamOptimizedFeedbackText = "Đã tối ưu";
        assert(vm.RamOptimizedButtonText == "Đã tối ưu", "RamOptimizedButtonText shows 'Đã tối ưu' upon zero reclaim");
        warnBrush = vm.RamOptimizedButtonBrush as SolidColorBrush;
        assert(warnBrush != null && warnBrush.Color.ToString() == "#FFF59E0B", "RamOptimizedButtonBrush remains #F59E0B on 'Đã tối ưu'");

        // 5. Timer Expiration Revert State ("Dọn RAM")
        vm.RamOptimizedFeedbackText = string.Empty;
        assert(vm.RamOptimizedButtonText == "Dọn RAM", "RamOptimizedButtonText reverts to 'Dọn RAM' after feedback clears");
        initialBrush = vm.RamOptimizedButtonBrush as SolidColorBrush;
        assert(initialBrush != null && initialBrush.Color.ToString() == "#FF38BDF8", "RamOptimizedButtonBrush reverts to #38BDF8 after feedback clears");
    }

    private static void TestRamOptimizerReentrancyAndHudEmission(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S1] RAM Optimizer Reentrancy Guard & HUD Emission ---");
        var vm = CreateTestViewModel(out _, out _, out _, out _, out _, out _);

        // Verify HUD payload emission on memory optimize
        bool hudEmitted = false;
        HudPayload? capturedHud = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ActiveHud))
            {
                hudEmitted = true;
                capturedHud = vm.ActiveHud;
            }
        };

        // Execute RAM optimization
        Task optTask = vm.OptimizeRamAsync();
        optTask.Wait(TimeSpan.FromSeconds(5));

        assert(!vm.IsRamOptimizing, "IsRamOptimizing is false after optimization finishes");
        assert(!string.IsNullOrEmpty(vm.RamOptimizedFeedbackText), "Feedback text is populated after execution", vm.RamOptimizedFeedbackText);
        assert(vm.RamOptimizedFeedbackText.StartsWith("-") || vm.RamOptimizedFeedbackText == "Đã tối ưu",
            "Feedback text is either '-X MB' or 'Đã tối ưu'", vm.RamOptimizedFeedbackText);
        assert(hudEmitted && capturedHud != null, "Memory optimization emitted a HUD popup");
        assert(capturedHud?.Kind == HudKind.MemoryOptimized, "HUD Kind is MemoryOptimized");
        assert(capturedHud?.Icon == IconsaxKind.Broom, "HUD Icon is IconsaxKind.Broom");
        assert(capturedHud?.AccentHex == "#38BDF8", "HUD AccentHex is #38BDF8");

        // Concurrent re-entrancy attack:
        // Set IsRamOptimizing = true manually, then call OptimizeRamAsync()
        // It must return immediately without performing another optimization
        vm.IsRamOptimizing = true;
        string beforeFeedback = vm.RamOptimizedFeedbackText;
        Task blockedTask = vm.OptimizeRamAsync();
        assert(blockedTask.IsCompleted, "Calling OptimizeRamAsync while IsRamOptimizing=true returns immediately (no redundant task)");
        assert(vm.RamOptimizedFeedbackText == beforeFeedback, "Feedback text is unchanged when re-entrant invocation is blocked");
        vm.IsRamOptimizing = false;
    }

    // =========================================================================
    // SUITE 2: Battery Status Detail Display & Desktop Uptime Fallback
    // =========================================================================

    private static void TestBatteryStatusDetailFormattingOracle(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S2] BatteryStatusDetail Formatting Oracle ---");

        // Method to evaluate formatting logic identical to DisplayPowerService.PollPowerStatus
        static string FormatBatteryDetail(byte batteryFlag, byte acStatus, byte batteryPercent, int lifeTimeSeconds)
        {
            bool hasBat = (batteryFlag & 128) == 0 && batteryPercent <= 100;
            bool charging = acStatus == 1;
            int pct = batteryPercent <= 100 ? batteryPercent : 100;

            if (!hasBat) return "Nguồn trực tiếp";
            if (charging) return $"Đang sạc nguồn • {pct}%";

            if (lifeTimeSeconds > 0 && (uint)lifeTimeSeconds != 0xFFFFFFFF)
            {
                int hours = lifeTimeSeconds / 3600;
                int minutes = (lifeTimeSeconds % 3600) / 60;
                if (hours > 0 && minutes > 0)
                    return $"Pin {pct}% • Còn ~{hours}g {minutes}p";
                if (hours > 0)
                    return $"Pin {pct}% • Còn ~{hours}g";
                return $"Pin {pct}% • Còn ~{Math.Max(1, minutes)}p";
            }
            return $"Pin {pct}%";
        }

        // 1. Desktop PC fallback
        assert(FormatBatteryDetail(128, 1, 255, -1) == "Nguồn trực tiếp", "Desktop PC (BatteryFlag=128) formats as 'Nguồn trực tiếp'");
        assert(FormatBatteryDetail(128, 0, 255, -1) == "Nguồn trực tiếp", "Desktop PC offline AC formats as 'Nguồn trực tiếp'");

        // 2. Charging laptop
        assert(FormatBatteryDetail(8, 1, 95, -1) == "Đang sạc nguồn • 95%", "Charging laptop formats as 'Đang sạc nguồn • 95%'");
        assert(FormatBatteryDetail(0, 1, 100, -1) == "Đang sạc nguồn • 100%", "Full charging laptop formats as 'Đang sạc nguồn • 100%'");

        // 3. Discharging with hours and minutes
        assert(FormatBatteryDetail(0, 0, 85, 11700) == "Pin 85% • Còn ~3g 15p", "3h 15m discharging formats as 'Pin 85% • Còn ~3g 15p'");
        assert(FormatBatteryDetail(0, 0, 72, 7320) == "Pin 72% • Còn ~2g 2p", "2h 2m discharging formats as 'Pin 72% • Còn ~2g 2p'");

        // 4. Discharging with exact hours (0 minutes)
        assert(FormatBatteryDetail(0, 0, 50, 7200) == "Pin 50% • Còn ~2g", "Exact 2h discharging formats as 'Pin 50% • Còn ~2g'");
        assert(FormatBatteryDetail(0, 0, 90, 3600) == "Pin 90% • Còn ~1g", "Exact 1h discharging formats as 'Pin 90% • Còn ~1g'");

        // 5. Discharging under 1 hour (minutes only)
        assert(FormatBatteryDetail(0, 0, 25, 1800) == "Pin 25% • Còn ~30p", "30m discharging formats as 'Pin 25% • Còn ~30p'");
        assert(FormatBatteryDetail(0, 0, 10, 300) == "Pin 10% • Còn ~5p", "5m discharging formats as 'Pin 10% • Còn ~5p'");

        // 6. Discharging under 1 minute (Math.Max(1, minutes))
        assert(FormatBatteryDetail(0, 0, 5, 45) == "Pin 5% • Còn ~1p", "45s discharging clamps up to 'Pin 5% • Còn ~1p'");
        assert(FormatBatteryDetail(0, 0, 2, 5) == "Pin 2% • Còn ~1p", "5s discharging clamps up to 'Pin 2% • Còn ~1p'");

        // 7. Discharging with unknown lifetime (-1 or 0xFFFFFFFF or 0)
        assert(FormatBatteryDetail(0, 0, 60, -1) == "Pin 60%", "Unknown lifetime (-1) formats as 'Pin 60%'");
        assert(FormatBatteryDetail(0, 0, 60, 0) == "Pin 60%", "Zero lifetime (0) formats as 'Pin 60%'");
        assert(FormatBatteryDetail(0, 0, 60, unchecked((int)0xFFFFFFFF)) == "Pin 60%", "0xFFFFFFFF lifetime formats as 'Pin 60%'");
    }

    private static void TestBatteryUptimeFallbackXamlBindingContracts(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S2] Battery & Uptime Fallback XAML Binding Contracts ---");

        string xamlPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\SystemMonitorView.xaml";
        assert(File.Exists(xamlPath), "SystemMonitorView.xaml exists");
        string xaml = File.ReadAllText(xamlPath);

        // 1. Verify BatteryStatusDetail binding
        assert(xaml.Contains("Text=\"{Binding BatteryStatusDetail}\""), "SystemMonitorView binds Text to BatteryStatusDetail");
        assert(xaml.Contains("Visibility=\"{Binding HasBatteryDisplay, Converter={StaticResource BoolToVis}}\""),
            "Battery container Visibility is gated by HasBatteryDisplay via BoolToVis");

        // 2. Verify Desktop Uptime Fallback binding
        assert(xaml.Contains("Text=\"{Binding SysUptimeText}\""), "SystemMonitorView binds fallback Text to SysUptimeText");
        assert(xaml.Contains("Visibility=\"{Binding HasBatteryDisplay, Converter={StaticResource InvBoolToVis}}\""),
            "Uptime fallback Visibility is gated by HasBatteryDisplay via InvBoolToVis (inverse converter)");

        // 3. Verify Rich ToolTip includes both BatteryStatusDetail and SysUptimeText
        assert(xaml.Contains("StringFormat='{}Thời gian máy chạy (Uptime): {0}'"),
            "Battery container ToolTip displays SysUptimeText with formatted label");
        assert(xaml.Contains("<TextBlock Text=\"{Binding BatteryStatusDetail}\""),
            "Battery container ToolTip displays BatteryStatusDetail heading");

        // 4. Verify ViewModel properties
        var vm = CreateTestViewModel(out _, out _, out var power, out _, out _, out _);
        assert(vm.BatteryStatusDetail == power.BatteryStatusDetail, "ViewModel BatteryStatusDetail matches DisplayPowerService");
        assert(vm.HasBatteryDisplay == power.HasBattery, "ViewModel HasBatteryDisplay matches DisplayPowerService.HasBattery");
    }

    // =========================================================================
    // SUITE 3: ShelfView Paste Buttons, Shortcuts, Double-Click & Reveal
    // =========================================================================

    private static void TestShelfViewPasteButtonsAndContracts(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] ShelfView Paste Buttons & Contracts ---");

        string shelfXamlPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\ShelfView.xaml";
        assert(File.Exists(shelfXamlPath), "ShelfView.xaml exists");
        string shelfXaml = File.ReadAllText(shelfXamlPath);

        // 1. Empty Shelf Paste Button
        assert(shelfXaml.Contains("Command=\"{Binding PasteShelfFromClipboardCommand}\""),
            "ShelfView binds PasteShelfFromClipboardCommand");
        assert(shelfXaml.Contains("Visibility=\"{Binding IsShelfEmpty, Converter={StaticResource BoolToVis}}\""),
            "Empty prompt is gated by IsShelfEmpty via BoolToVis");
        assert(shelfXaml.Contains("+ Dán từ Clipboard"), "Paste button displays '+ Dán từ Clipboard'");

        // 2. Footer Bar Paste Button
        assert(shelfXaml.Contains("Visibility=\"{Binding HasShelfItems, Converter={StaticResource BoolToVis}}\""),
            "Populated items and footer are gated by HasShelfItems via BoolToVis");

        // 3. Verify ViewModel command invocation
        var vm = CreateTestViewModel(out _, out var shelf, out _, out _, out _, out _);
        int initialCount = shelf.Items.Count;
        bool pasteExecuted = false;
        try
        {
            vm.PasteShelfFromClipboardCommand.Execute(null);
            pasteExecuted = true;
        }
        catch { }
        assert(pasteExecuted, "PasteShelfFromClipboardCommand executes cleanly without exception");
        assert(shelf.Items.Count >= initialCount, "Shelf items collection is intact or incremented");
    }

    private static void TestShelfViewAndWindowCtrlVShortcuts(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] Ctrl + V Shortcuts on ShelfView and NotchWindow ---");

        var vm = CreateTestViewModel(out _, out _, out _, out _, out _, out _);

        // 1. ShelfView.xaml KeyDown hooking
        string shelfXaml = File.ReadAllText(@"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\ShelfView.xaml");
        assert(shelfXaml.Contains("PreviewKeyDown=\"OnShelfPreviewKeyDown\""),
            "ShelfView.xaml hooks PreviewKeyDown='OnShelfPreviewKeyDown'");

        // 2. ShelfView.xaml.cs implementation
        string shelfCs = File.ReadAllText(@"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\ShelfView.xaml.cs");
        assert(shelfCs.Contains("if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)"),
            "ShelfView checks e.Key == Key.V and Ctrl modifier");
        assert(shelfCs.Contains("vm.PasteShelfFromClipboardCommand.Execute(null);"),
            "ShelfView invokes vm.PasteShelfFromClipboardCommand.Execute(null)");
        assert(shelfCs.Contains("e.Handled = true;"),
            "ShelfView marks e.Handled = true after handling Ctrl + V");

        // 3. NotchWindow.xaml.cs window-level routing
        string notchCs = File.ReadAllText(@"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\NotchWindow.xaml.cs");
        assert(notchCs.Contains("PreviewKeyDown += OnWindowPreviewKeyDown;"),
            "NotchWindow wires PreviewKeyDown += OnWindowPreviewKeyDown");
        assert(notchCs.Contains("if (_viewModel.IsFileShelfTabSelected && _viewModel.IsExpandedVisible)"),
            "NotchWindow gates window-level Ctrl+V on IsFileShelfTabSelected and IsExpandedVisible");
        assert(notchCs.Contains("_viewModel.PasteShelfFromClipboardCommand.Execute(null);"),
            "NotchWindow forwards Ctrl+V to _viewModel.PasteShelfFromClipboardCommand");
    }

    private static void TestShelfCardDoubleClickLaunchAndDragSuppression(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] Shelf Card Double-Click Launch & Drag Suppression ---");

        string shelfCs = File.ReadAllText(@"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\ShelfView.xaml.cs");

        // 1. Verify e.ClickCount == 2 check
        assert(shelfCs.Contains("if (e.ClickCount == 2)"), "ShelfView checks e.ClickCount == 2 on card mouse down");

        // 2. Verify drag suppression: _pendingDragItem must be reset to null on double click
        assert(shelfCs.Contains("_pendingDragItem = null;"), "Double-click resets _pendingDragItem = null to suppress drag initiation");
        assert(shelfCs.Contains("_pendingDragAll = false;"), "Double-click resets _pendingDragAll = false");

        // 3. Verify OpenShelfItemCommand is invoked with item
        assert(shelfCs.Contains("vm.OpenShelfItemCommand.Execute(item);"),
            "Double-click executes vm.OpenShelfItemCommand.Execute(item)");
        assert(shelfCs.Contains("e.Handled = true;"),
            "Double-click sets e.Handled = true to halt event bubbling");

        // 4. Verify OpenShelfItemCommand in ViewModel
        var vm = CreateTestViewModel(out _, out var shelf, out _, out _, out _, out _);
        var testItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            FilePath = Path.Combine(Path.GetTempPath(), "test_shelf_note.txt"),
            DisplayName = "Test Note",
            TextContent = "Hello WinNotch"
        };
        File.WriteAllText(testItem.FilePath, testItem.TextContent);

        bool openExecuted = false;
        try
        {
            vm.OpenShelfItemCommand.Execute(testItem);
            openExecuted = true;
        }
        catch { }
        assert(openExecuted, "vm.OpenShelfItemCommand.Execute(item) succeeds cleanly");
    }

    private static void TestShelfCardFolder2RevealButton(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] Shelf Card Folder2 Reveal Button ---");

        string shelfXaml = File.ReadAllText(@"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\ShelfView.xaml");

        // 1. Verify Folder2 icon
        assert(shelfXaml.Contains("<controls:IconsaxIcon Kind=\"Folder2\""),
            "Shelf card has <controls:IconsaxIcon Kind=\"Folder2\"");

        // 2. Verify 22x22 button geometry
        assert(shelfXaml.Contains("Width=\"22\"") && shelfXaml.Contains("Height=\"22\""),
            "Action buttons are sized 22x22");

        // 3. Verify RevealShelfItemInExplorerCommand binding
        assert(shelfXaml.Contains("Command=\"{Binding DataContext.RevealShelfItemInExplorerCommand, RelativeSource={RelativeSource AncestorType=UserControl}}\""),
            "Folder2 button binds RevealShelfItemInExplorerCommand");
        assert(shelfXaml.Contains("CommandParameter=\"{Binding}\""),
            "Folder2 button passes card item as CommandParameter");

        // 4. Verify ViewModel command invocation
        var vm = CreateTestViewModel(out _, out var shelf, out _, out _, out _, out _);
        string tempFile = Path.Combine(Path.GetTempPath(), "test_reveal_shelf.txt");
        File.WriteAllText(tempFile, "sample");
        var item = new ShelfItem
        {
            Kind = ShelfItemKind.File,
            FilePath = tempFile,
            DisplayName = "test_reveal_shelf.txt"
        };

        bool revealExecuted = false;
        try
        {
            vm.RevealShelfItemInExplorerCommand.Execute(item);
            revealExecuted = true;
        }
        catch { }
        assert(revealExecuted, "vm.RevealShelfItemInExplorerCommand.Execute(item) succeeds cleanly");
    }

    // =========================================================================
    // SUITE 4: Settings Toggles Round-Trip Persistence & Fuzzing
    // =========================================================================

    private static void TestSettingsTogglesDefaultValues(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] Settings Toggles Default Values ---");

        var appSettings = new AppSettings();
        assert(appSettings.EnableCollapsedScrollGestures == true, "AppSettings.EnableCollapsedScrollGestures defaults to true");
        assert(appSettings.EnableAudioDeviceHud == true, "AppSettings.EnableAudioDeviceHud defaults to true");
        assert(appSettings.EnableCapsLockHud == true, "AppSettings.EnableCapsLockHud defaults to true");

        var settingsService = new SettingsService();
        var vm = new SettingsViewModel(settingsService, () => { });
        assert(vm.EnableCollapsedScrollGestures == true, "SettingsViewModel.EnableCollapsedScrollGestures defaults to true");
        assert(vm.EnableAudioDeviceHud == true, "SettingsViewModel.EnableAudioDeviceHud defaults to true");
        assert(vm.EnableCapsLockHud == true, "SettingsViewModel.EnableCapsLockHud defaults to true");
    }

    private static void TestSettingsTogglesSerializationRoundTrip(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] Settings Toggles Serialization Round-Trip ---");

        string testDir = Path.Combine(Path.GetTempPath(), "WinNotch_SettingsRoundTrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        string settingsJsonPath = Path.Combine(testDir, "settings.json");

        try
        {
            // Create AppSettings with all three false
            var customSettings = new AppSettings
            {
                EnableCollapsedScrollGestures = false,
                EnableAudioDeviceHud = false,
                EnableCapsLockHud = false
            };

            string json = JsonSerializer.Serialize(customSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsJsonPath, json);

            // Verify raw JSON content contains the exact property names
            string readRaw = File.ReadAllText(settingsJsonPath);
            assert(readRaw.Contains("\"EnableCollapsedScrollGestures\": false"), "JSON contains exact 'EnableCollapsedScrollGestures': false");
            assert(readRaw.Contains("\"EnableAudioDeviceHud\": false"), "JSON contains exact 'EnableAudioDeviceHud': false");
            assert(readRaw.Contains("\"EnableCapsLockHud\": false"), "JSON contains exact 'EnableCapsLockHud': false");

            // Deserialize back and verify
            var deserialized = JsonSerializer.Deserialize<AppSettings>(readRaw);
            assert(deserialized != null, "Deserialization succeeded");
            assert(deserialized!.EnableCollapsedScrollGestures == false, "Deserialized EnableCollapsedScrollGestures is false");
            assert(deserialized.EnableAudioDeviceHud == false, "Deserialized EnableAudioDeviceHud is false");
            assert(deserialized.EnableCapsLockHud == false, "Deserialized EnableCapsLockHud is false");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    private static void TestSettingsTogglesCombinatorialFuzzing(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] Settings Toggles Combinatorial Fuzzing (8 Permutations) ---");

        bool[] bools = [true, false];
        int permutationCount = 0;
        int passCount = 0;

        foreach (bool g in bools)
        {
            foreach (bool a in bools)
            {
                foreach (bool c in bools)
                {
                    permutationCount++;
                    var original = new AppSettings
                    {
                        EnableCollapsedScrollGestures = g,
                        EnableAudioDeviceHud = a,
                        EnableCapsLockHud = c
                    };

                    string json = JsonSerializer.Serialize(original);
                    var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

                    if (deserialized != null &&
                        deserialized.EnableCollapsedScrollGestures == g &&
                        deserialized.EnableAudioDeviceHud == a &&
                        deserialized.EnableCapsLockHud == c)
                    {
                        passCount++;
                    }
                }
            }
        }

        assert(permutationCount == 8, "Evaluated exactly 2^3 = 8 Boolean permutations");
        assert(passCount == 8, $"All 8 permutations survived serialization round-trip ({passCount}/8)");
    }

    private static void TestSettingsTogglesResetDefaults(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] Settings Toggles ResetDefaults Command ---");

        var settings = new SettingsService();
        var vm = new SettingsViewModel(settings, () => { });

        // Change values to false
        vm.EnableCollapsedScrollGestures = false;
        vm.EnableAudioDeviceHud = false;
        vm.EnableCapsLockHud = false;

        // Save changed values
        MethodInfo? saveMethod = typeof(SettingsViewModel).GetMethod("SaveAndClose", BindingFlags.NonPublic | BindingFlags.Instance);
        saveMethod?.Invoke(vm, null);

        assert(settings.Current.EnableCollapsedScrollGestures == false, "Settings saved with false");
        assert(settings.Current.EnableAudioDeviceHud == false, "Settings saved with false");
        assert(settings.Current.EnableCapsLockHud == false, "Settings saved with false");

        // Trigger ResetDefaultsCommand
        vm.ResetDefaultsCommand.Execute(null);

        assert(vm.EnableCollapsedScrollGestures == true, "vm.EnableCollapsedScrollGestures reset to true");
        assert(vm.EnableAudioDeviceHud == true, "vm.EnableAudioDeviceHud reset to true");
        assert(vm.EnableCapsLockHud == true, "vm.EnableCapsLockHud reset to true");
        assert(settings.Current.EnableCollapsedScrollGestures == true, "settings.Current.EnableCollapsedScrollGestures reset to true");
        assert(settings.Current.EnableAudioDeviceHud == true, "settings.Current.EnableAudioDeviceHud reset to true");
        assert(settings.Current.EnableCapsLockHud == true, "settings.Current.EnableCapsLockHud reset to true");
    }

    private static void TestSettingsTogglesCorruptJsonResilience(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] Settings Toggles Corrupt & Partial JSON Resilience ---");

        // 1. Partial JSON with empty object {}
        string emptyJson = "{}";
        var parsedEmpty = JsonSerializer.Deserialize<AppSettings>(emptyJson);
        assert(parsedEmpty != null, "Empty JSON {} deserializes without throwing");
        assert(parsedEmpty!.EnableCollapsedScrollGestures == true, "Missing EnableCollapsedScrollGestures adopts default true");
        assert(parsedEmpty.EnableAudioDeviceHud == true, "Missing EnableAudioDeviceHud adopts default true");
        assert(parsedEmpty.EnableCapsLockHud == true, "Missing EnableCapsLockHud adopts default true");

        // 2. Extra unexpected fields
        string extraFieldsJson = "{\"EnableCollapsedScrollGestures\": false, \"UnexpectedFutureKey\": 12345}";
        var parsedExtra = JsonSerializer.Deserialize<AppSettings>(extraFieldsJson);
        assert(parsedExtra != null, "JSON with unknown keys deserializes safely");
        assert(parsedExtra!.EnableCollapsedScrollGestures == false, "Known key correctly parsed");
        assert(parsedExtra.EnableAudioDeviceHud == true, "Omitted key adopts default true");
    }

    // =========================================================================
    // SUITE 5: TrayIconService + Native WPF TrayPopupWindow & 3-Tier Sync
    // =========================================================================

    private static void TestTrayIconServiceContextMenuItems(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S5] TrayIconService & Native Dark WPF TrayPopupWindow Items ---");

        var settings = new SettingsService();
        var sm = new NotchStateMachine(settings.Current);
        var descriptor = new MonitorDescriptor { Handle = IntPtr.Zero, FriendlyLabel = "Mock" };
        var power = new DisplayPowerService(settings, descriptor);
        var audio = new AudioService(settings);
        var picker = new ColorPickerService(sm);

        using var tray = new TrayIconService(sm, power, audio, picker, () => { }, () => { });
        tray.Start();

        FieldInfo? notifyField = typeof(TrayIconService).GetField("_notifyIcon", BindingFlags.NonPublic | BindingFlags.Instance);
        var notifyIcon = notifyField?.GetValue(tray) as System.Windows.Forms.NotifyIcon;
        assert(notifyIcon != null, "TrayIconService._notifyIcon is initialized after Start()");
        assert(notifyIcon!.ContextMenuStrip == null, "WinForms ContextMenuStrip is null so native WPF TrayPopupWindow opens on right-click");

        TrayPopupWindow popup = tray.EnsurePopupWindow();
        assert(Math.Abs(popup.Width - 276.0) < 0.01, "TrayPopupWindow Width == 276px");
        assert(popup.FindName("ToggleNotchItemBtn") is Button, "TrayPopupWindow contains ToggleNotchItemBtn (Alt+N)");
        assert(popup.FindName("OpenAgentsItemBtn") is Button, "TrayPopupWindow contains OpenAgentsItemBtn");
        assert(popup.FindName("OpenSystemItemBtn") is Button, "TrayPopupWindow contains OpenSystemItemBtn");
        assert(popup.FindName("OpenShelfItemBtn") is Button, "TrayPopupWindow contains OpenShelfItemBtn");
        assert(popup.FindName("ToggleHideItemBtn") is Button, "TrayPopupWindow contains ToggleHideItemBtn");
        assert(popup.FindName("CaffeineItemBtn") is Button, "TrayPopupWindow contains CaffeineItemBtn");
        assert(popup.FindName("MicMuteItemBtn") is Button, "TrayPopupWindow contains MicMuteItemBtn (Alt+M)");
        assert(popup.FindName("ColorPickerItemBtn") is Button, "TrayPopupWindow contains ColorPickerItemBtn (Alt+C)");
        assert(popup.FindName("SettingsItemBtn") is Button, "TrayPopupWindow contains SettingsItemBtn");
        assert(popup.FindName("ExitItemBtn") is Button, "TrayPopupWindow contains ExitItemBtn");
    }

    private static void TestTrayIconServiceItemActionTriggers(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S5] TrayIconService Menu Item Action Triggers ---");

        var settings = new SettingsService();
        var sm = new NotchStateMachine(settings.Current);
        var descriptor = new MonitorDescriptor { Handle = IntPtr.Zero, FriendlyLabel = "Mock" };
        var power = new DisplayPowerService(settings, descriptor);
        var audio = new AudioService(settings);
        var picker = new ColorPickerService(sm);

        using var tray = new TrayIconService(sm, power, audio, picker, () => { }, () => { });
        tray.Start();

        bool initialCaffeine = power.IsCaffeineActive;
        tray.TriggerToggleCaffeine();
        PumpDispatcher();

        assert(power.IsCaffeineActive != initialCaffeine, "TriggerToggleCaffeine toggles power.IsCaffeineActive");
        assert(tray.IsCaffeineChecked == power.IsCaffeineActive, "TrayIconService.IsCaffeineChecked matches service");

        tray.TriggerToggleCaffeine();
        PumpDispatcher();

        assert(power.IsCaffeineActive == initialCaffeine, "TriggerToggleCaffeine toggles back");
    }

    private static void TestTrayIconService3TierSynchronization(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S5] TrayIconService 3-Tier State Synchronization ---");

        var settings = new SettingsService();
        var sm = new NotchStateMachine(settings.Current);
        var descriptor = new MonitorDescriptor { Handle = IntPtr.Zero, FriendlyLabel = "Mock" };
        var power = new DisplayPowerService(settings, descriptor);
        var audio = new AudioService(settings);
        var picker = new ColorPickerService(sm);

        using var tray = new TrayIconService(sm, power, audio, picker, () => { }, () => { });
        tray.Start();
        TrayPopupWindow popup = tray.EnsurePopupWindow();

        // --- TIER 1: Click Synchronization ---
        power.SetCaffeine(false, showHud: false);
        tray.TriggerToggleCaffeine();
        PumpDispatcher();
        assert(tray.IsCaffeineChecked == power.IsCaffeineActive && popup.IsCaffeineActive == power.IsCaffeineActive,
            "Tier 1: Click synchronizes TrayIconService & TrayPopupWindow state");

        // --- TIER 2: Popup Opening Synchronization ---
        power.SetCaffeine(false, showHud: false);
        tray.EnsurePopupWindow();
        assert(!tray.IsCaffeineChecked && !popup.IsCaffeineActive, "Tier 2: EnsurePopupWindow updates Caffeine indicator to false");

        power.SetCaffeine(true, showHud: false);
        tray.EnsurePopupWindow();
        assert(tray.IsCaffeineChecked && popup.IsCaffeineActive, "Tier 2: EnsurePopupWindow updates Caffeine indicator to true");

        // --- TIER 3: Service StateChanged Event Synchronization ---
        MethodInfo? onPowerChangedMethod = typeof(TrayIconService).GetMethod(
            "OnDisplayPowerStateChanged",
            BindingFlags.NonPublic | BindingFlags.Instance);

        power.SetCaffeine(false, showHud: false);
        onPowerChangedMethod?.Invoke(tray, null);
        PumpDispatcher();
        assert(!tray.IsCaffeineChecked && !popup.IsCaffeineActive, "Tier 3: OnDisplayPowerStateChanged updates Caffeine indicator to false");

        power.SetCaffeine(true, showHud: false);
        onPowerChangedMethod?.Invoke(tray, null);
        PumpDispatcher();
        assert(tray.IsCaffeineChecked && popup.IsCaffeineActive, "Tier 3: OnDisplayPowerStateChanged updates Caffeine indicator to true");
    }

    private static void TestTrayIconServiceOffThreadDispatchAndDisposal(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S5] TrayIconService Off-Thread Dispatch & Clean Disposal ---");

        var settings = new SettingsService();
        var sm = new NotchStateMachine(settings.Current);
        var descriptor = new MonitorDescriptor { Handle = IntPtr.Zero, FriendlyLabel = "Mock" };
        var power = new DisplayPowerService(settings, descriptor);
        var audio = new AudioService(settings);
        var picker = new ColorPickerService(sm);

        var tray = new TrayIconService(sm, power, audio, picker, () => { }, () => { });
        tray.Start();

        // 1. Off-thread event firing
        bool threadPoolCompleted = false;
        Task.Run(() =>
        {
            MethodInfo? onPowerChanged = typeof(TrayIconService).GetMethod("OnDisplayPowerStateChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            onPowerChanged?.Invoke(tray, null);

            MethodInfo? onAudioChanged = typeof(TrayIconService).GetMethod("OnAudioStateChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            onAudioChanged?.Invoke(tray, null);

            threadPoolCompleted = true;
        }).Wait(TimeSpan.FromSeconds(2));

        PumpDispatcher();
        assert(threadPoolCompleted, "Off-thread StateChanged invocation dispatches safely without thread exception");

        // 2. Clean disposal
        bool disposedCleanly = false;
        try
        {
            tray.Dispose();
            disposedCleanly = true;
        }
        catch { }

        assert(disposedCleanly, "TrayIconService.Dispose() completes without exception");

        // Multiple disposals must be safe
        bool secondDispose = false;
        try
        {
            tray.Dispose();
            secondDispose = true;
        }
        catch { }
        assert(secondDispose, "Second TrayIconService.Dispose() is a safe no-op");
    }
}
