using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;
using WinNotch.Views;
using WinNotch.Views.Controls;

namespace WinNotch.Tests;

public static class Milestone3And4UiEvolutionTests
{
    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 3 & 4: UI EVOLUTION WAVE — AGENTS TREE, SPARKLINE, SHELF,     ");
        Console.WriteLine(" SEQUOIA SETTINGS & FROSTED TRAY POPUP EMPIRICAL VERIFICATION SUITE      ");
        Console.WriteLine("=========================================================================");

        if (Application.Current == null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        if (Application.Current!.Resources.MergedDictionaries.Count == 0)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/WinNotch;component/Views/Themes/NotchStyles.xaml", UriKind.Absolute)
            });
        }

        TestPitchBlackOuterNotchShellInvariant(assert);
        TestAgentSingleColumnTreeAndVietnameseToolTranslation(assert);
        TestAgentsDynamicHeightAndExpandCollapseFiltering(assert);
        TestSparklineHoverScrubbingReadoutOracle(assert);
        TestShelfQuickLookPreviewModels(assert);
        TestSequoiaSettingsWindowAndPresetPersistence(assert);
        TestTrayPopupDpiClampingMatrix(assert);
    }

    private static void TestPitchBlackOuterNotchShellInvariant(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3M4-1] Pure Pitch-Black #050507 Outer Notch Shell Mandate ---");
        string stylesXaml = File.ReadAllText(@"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Themes\NotchStyles.xaml");
        assert(stylesXaml.Contains("<SolidColorBrush x:Key=\"NotchShellBrush\" Color=\"#050507\" />"),
            "NotchShellBrush remains strictly pure pitch-black #050507");
        assert(stylesXaml.Contains("<SolidColorBrush x:Key=\"NotchRimBrush\" Color=\"#1FFFFFFF\" />"),
            "NotchRimBrush remains subtle neutral #1FFFFFFF inner specular edge");
    }

    private static void TestAgentSingleColumnTreeAndVietnameseToolTranslation(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3M4-2] Single-Column 4-Field Project Tree & 100% Vietnamese Tools ---");

        // 1. Verify AgentMonitorView is single-column (no 2-column split)
        var agentView = new AgentMonitorView();
        assert(agentView.FindName("AgentSingleColumnCard") is SquircleBorder,
            "AgentMonitorView renders a single full-width AgentSingleColumnCard (no 2-column split)");
        assert(agentView.FindName("AgentProjectTreeItems") is ItemsControl,
            "AgentMonitorView binds AgentProjectTreeItems to FilteredAgentSessions");

        // 2. Verify Vietnamese tool translation produces zero 'Tool · ...' English strings
        Type parserType = typeof(AgentMonitorService).Assembly.GetType("WinNotch.Services.AgentTranscriptParser", throwOnError: true)!;
        MethodInfo? summarizeMethod = parserType.GetMethod(
            "SummarizeToolCall",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        assert(summarizeMethod != null, "Located AgentTranscriptParser.SummarizeToolCall");

        using JsonDocument doc = JsonDocument.Parse("""{"Path":"C:/Users/ASUS/Documents/AGY/WinNotch/src/WinNotch/Views/NotchWindow.xaml","CommandLine":"dotnet build WinNotch.sln -c Release"}""");
        string[] tools = ["list_dir", "read_file", "edit_file", "write_file", "grep_search", "find_by_name", "run_command", "invoke_subagent", "send_message"];
        foreach (string t in tools)
        {
            string summary = (string)summarizeMethod!.Invoke(null, [t, doc.RootElement])!;
            assert(!string.IsNullOrWhiteSpace(summary) && !summary.Contains("Tool ·", StringComparison.OrdinalIgnoreCase),
                $"Tool '{t}' translated to clean Vietnamese: '{summary}'");
        }

        // 3. Verify Parent-Child Project Tree grouping via BuildProjectTreeItems
        MethodInfo? treeMethod = typeof(AgentMonitorService).GetMethod(
            "BuildProjectTreeItems",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        assert(treeMethod != null, "Located AgentMonitorService.BuildProjectTreeItems");

        DateTime t0 = DateTime.UtcNow.AddMinutes(-3);
        var candidates = new List<AgentSessionItem>
        {
            new()
            {
                SessionId = "sub-1",
                ParentSessionId = "parent-1",
                ProjectName = "WinNotch",
                DisplayName = "Chuyên viên Mốc 3",
                State = AgentRunState.Working,
                CurrentActionText = "Sửa AgentMonitorView.xaml",
                ProgressPercent = 68,
                ActiveTurnStartedAtUtc = t0.AddSeconds(20),
                LastUpdatedAtUtc = DateTime.UtcNow,
                IsSubagent = true
            },
            new()
            {
                SessionId = "parent-1",
                ProjectName = "WinNotch",
                DisplayName = "Phiên 26c6d2f5",
                State = AgentRunState.Working,
                CurrentActionText = "Điều phối thực thi giao diện",
                ProgressPercent = 64,
                ActiveTurnStartedAtUtc = t0,
                LastUpdatedAtUtc = DateTime.UtcNow.AddSeconds(-2),
                IsSubagent = false
            }
        };

        var tree = (List<AgentSessionItem>)treeMethod!.Invoke(null, [candidates])!;
        assert(tree.Count == 2, $"Tree built 2 ordered rows (got {tree.Count})");
        assert(tree[0].IsParentRow && tree[0].PrimaryRowLabel == "WinNotch" && tree[0].ChildCount == 1,
            $"Row 0 is Parent Project 'WinNotch' with ChildCount=1 (got '{tree[0].PrimaryRowLabel}', kids={tree[0].ChildCount})");
        assert(tree[1].IsSubagent && tree[1].PrimaryRowLabel == "Chuyên viên Mốc 3" && tree[1].GroupId == "parent-1",
            $"Row 1 is Child Subagent 'Chuyên viên Mốc 3' under GroupId='parent-1'");
        assert(tree[0].ProgressText == "64%" && tree[1].ProgressText == "68%",
            "Both parent and child rows expose tabular ProgressText");
        assert(tree[0].RuntimeText.Contains(':'), $"RuntimeText formats live mm:ss stopwatch ('{tree[0].RuntimeText}')");
    }

    private static void TestAgentsDynamicHeightAndExpandCollapseFiltering(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3M4-3] Dynamic 2/3 Screen Height Scaling, Filter & Branch Collapse ---");

        var settings = new SettingsService();
        var sm = new NotchStateMachine(settings.Current);
        var mon = new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "1080p Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };

        var vm = new MainNotchViewModel(
            settings,
            sm,
            new AudioService(settings),
            new DisplayPowerService(settings, mon),
            new MediaService(settings),
            new FileShelfService(settings),
            new TimerService(),
            new PrivacyService(settings),
            new SystemMonitorService(settings),
            new NotificationService(settings),
            new ColorPickerService(sm),
            new AgentMonitorService(settings),
            () => { });

        // Populate 1 parent + 8 subagents + 1 completed project -> 10 rows total
        vm.AgentSessions.Clear();
        vm.AgentSessions.Add(new AgentSessionItem
        {
            SessionId = "p1",
            GroupId = "p1",
            ProjectName = "WinNotch",
            State = AgentRunState.Working,
            ProgressPercent = 72,
            IsSubagent = false
        });
        for (int i = 1; i <= 8; i++)
        {
            vm.AgentSessions.Add(new AgentSessionItem
            {
                SessionId = $"c{i}",
                ParentSessionId = "p1",
                GroupId = "p1",
                ProjectName = "WinNotch",
                DisplayName = $"Worker #{i}",
                State = i <= 5 ? AgentRunState.Working : AgentRunState.Completed,
                ProgressPercent = i <= 5 ? 60 : 100,
                IsSubagent = true
            });
        }
        vm.AgentSessions.Add(new AgentSessionItem
        {
            SessionId = "p2",
            GroupId = "p2",
            ProjectName = "OtherRepo",
            State = AgentRunState.Completed,
            ProgressPercent = 100,
            IsSubagent = false
        });

        vm.HasAnyAgents = true;
        vm.RebuildFilteredAgentSessions();

        double fullDesired = vm.DesiredAgentsHeight;
        assert(vm.FilteredAgentSessions.Count == 10, $"All 10 rows visible in 'Tất cả' filter (got {vm.FilteredAgentSessions.Count})");
        assert(fullDesired > 272.0, $"DesiredAgentsHeight grows dynamically beyond 272px for 10 rows ({fullDesired}px)");

        double ceiling1080 = Math.Floor(1080.0 * (2.0 / 3.0)); // 720px
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, fullDesired, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == Math.Min(fullDesired, ceiling1080),
            $"StateMachine expands Agents tab height to {sm.CurrentExpandedDimensions.Height}px (<= 2/3 ceiling {ceiling1080}px)");

        // Collapse parent group p1 -> hides 8 child rows -> height shrinks back to base!
        vm.ToggleAgentGroupExpandCommand.Execute("p1");
        assert(vm.FilteredAgentSessions.Count == 2, $"Collapsing p1 hides 8 subagents -> 2 parent rows remain (got {vm.FilteredAgentSessions.Count})");
        assert(vm.DesiredAgentsHeight == vm.ActiveExpandedHeight,
            $"Collapsing subagents shrinks DesiredAgentsHeight back to base {vm.ActiveExpandedHeight}px");

        // Expand p1 back and switch filter to 'Đang chạy'
        vm.ToggleAgentGroupExpandCommand.Execute("p1");
        vm.SelectActiveAgentsFilterCommand.Execute(null);
        assert(vm.FilteredAgentSessions.Count == 6,
            $"Active filter keeps parent p1 + 5 active workers and hides completed rows (got {vm.FilteredAgentSessions.Count})");
    }

    private static void TestSparklineHoverScrubbingReadoutOracle(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3M4-4] SparklineGraph Interactive Cursor Scrubbing Readout ---");

        var cpuGraph = new SparklineGraph
        {
            Width = 100,
            Height = 40,
            IsFixedPercentScale = true,
            IsHoverReadoutEnabled = true,
            PrimaryValues = [15.0, 42.0, 88.0]
        };

        bool okMid = cpuGraph.TryResolveHoverReadout(48.75, 100.0, out int midIdx, out string midText);
        assert(okMid && midIdx == 1 && midText == "42% • 1s trước",
            $"CPU Sparkline midpoint resolves sample[1] -> '42% • 1s trước' (got idx={midIdx}, '{midText}')");

        bool okEnd = cpuGraph.TryResolveHoverReadout(98.0, 100.0, out int endIdx, out string endText);
        assert(okEnd && endIdx == 2 && endText == "88% • Hiện tại",
            $"CPU Sparkline right edge resolves latest sample -> '88% • Hiện tại' (got '{endText}')");

        var netGraph = new SparklineGraph
        {
            Width = 200,
            Height = 52,
            IsFixedPercentScale = false,
            IsHoverReadoutEnabled = true,
            PrimaryValues = [1024.0 * 1024.0 * 4.0, 1024.0 * 512.0],
            SecondaryValues = [1024.0 * 128.0, 1024.0 * 64.0]
        };

        bool okNet = netGraph.TryResolveHoverReadout(0.0, 200.0, out int netIdx, out string netText);
        assert(okNet && netIdx == 0 && netText.Contains('↓') && netText.Contains('↑') && netText.Contains("1s trước"),
            $"Dual Network Sparkline formats both ↓ Down and ↑ Up speeds with age ('{netText}')");
        assert(!netGraph.IsHovering, "SparklineGraph IsHovering defaults to false (0% background CPU)");
    }

    private static void TestShelfQuickLookPreviewModels(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3M4-5] ShelfItem Quick-Look Image & Text Preview Properties ---");

        var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
        bmp.Freeze();

        var imgItem = new ShelfItem
        {
            Kind = ShelfItemKind.ImageClip,
            DisplayName = "Screenshot.png",
            SubLabel = "PNG • 120 KB",
            Thumbnail = bmp
        };
        assert(imgItem.HasImagePreview && !imgItem.HasTextSnippetPreview && imgItem.KindBadgeText == "Ảnh chụp",
            "ImageClip ShelfItem enables HasImagePreview and badge 'Ảnh chụp'");

        var urlItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            DisplayName = "github.com",
            TextContent = "https://github.com/Dduykhanh026/WinNotch"
        };
        assert(urlItem.IsUrl && urlItem.HasTextSnippetPreview && urlItem.KindBadgeText == "Liên kết URL",
            "URL ShelfItem enables HasTextSnippetPreview and badge 'Liên kết URL'");
    }

    private static void TestSequoiaSettingsWindowAndPresetPersistence(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3M4-6] Sequoia Frameless SettingsWindow (720x560) & Preset Cards ---");

        var settingsService = new SettingsService();
        string originalPreset = settingsService.Current.ExpandedSizePreset;
        string originalSpring = settingsService.Current.SpringPreset;

        var win = new SettingsWindow(settingsService);
        try
        {
            assert(win.WindowStyle == WindowStyle.None && win.AllowsTransparency,
                "SettingsWindow is frameless transparent dark Sequoia window");
            assert(Math.Abs(win.Width - 720.0) < 0.01 && Math.Abs(win.Height - 560.0) < 0.01,
                "SettingsWindow dimensions are 720 x 560");
            assert(win.FindName("SidebarCat0Btn") is Button && win.FindName("SidebarCat4Btn") is Button,
                "SettingsWindow contains all 5 left sidebar category buttons (0..4)");

            var vm = (SettingsViewModel)win.DataContext;
            vm.SelectCategoryCommand.Execute("1");
            assert(vm.IsCat1AgentsSelected, "Selecting category 1 activates AI Agents pane");

            vm.SelectExpandedSizeCommand.Execute("Spacious");
            vm.SelectSpringPresetCardCommand.Execute("Snappy");
            assert(vm.IsSpaciousSizeSelected && vm.IsSnappySpringSelected,
                "Visual preset cards update IsSpaciousSizeSelected and IsSnappySpringSelected");
        }
        finally
        {
            // Restore original user settings cleanly
            AppSettings restored = settingsService.Current.Clone();
            restored.ExpandedSizePreset = originalPreset;
            restored.SpringPreset = originalSpring;
            settingsService.Save(restored);
            win.Close();
        }
    }

    private static void TestTrayPopupDpiClampingMatrix(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M3M4-7] TrayPopupWindow Multi-DPI Taskbar Clamping Matrix ---");

        var work1080 = new System.Drawing.Rectangle(0, 0, 1920, 1032);
        double[] dpis = [1.0, 1.25, 1.5];

        foreach (double dpi in dpis)
        {
            Point bottomRight = TrayPopupWindow.CalculateClampedDipPosition(1918, 1030, work1080, dpi, dpi, 276.0, 348.0);
            double maxRightDip = (work1080.Right / dpi) - 276.0 - 8.0;
            double maxBottomDip = (work1080.Bottom / dpi) - 348.0 - 8.0;

            assert(bottomRight.X >= 8.0 && bottomRight.X <= Math.Round(maxRightDip) + 1.0,
                $"DPI {dpi * 100:0}%: Bottom-right X={bottomRight.X} stays strictly inside WorkingArea");
            assert(bottomRight.Y >= 8.0 && bottomRight.Y <= Math.Round(maxBottomDip) + 1.0,
                $"DPI {dpi * 100:0}%: Bottom-right Y={bottomRight.Y} stays strictly above taskbar");

            Point topLeft = TrayPopupWindow.CalculateClampedDipPosition(2, 4, work1080, dpi, dpi, 276.0, 348.0);
            assert(topLeft.X >= 8.0 && topLeft.Y >= 8.0,
                $"DPI {dpi * 100:0}%: Top-left taskbar position ({topLeft.X}, {topLeft.Y}) never clips off top/left edge");
        }
    }
}
