using System.IO;
using System.Windows.Media;

namespace WinNotch.Models;

public enum AgentProvider
{
    Antigravity,
    Teamwork,
    Codex,
    ClaudeCode,
    CustomHook
}

public enum AgentRunState
{
    Idle = 0,
    Completed = 1,
    Thinking = 2,
    Working = 3,
    Error = 4,
    WaitingApproval = 5
}

public sealed class AgentSessionItem
{
    private static readonly Brush DotWorkingBrush = CreateBrush("#60A5FA");
    private static readonly Brush DotThinkingBrush = CreateBrush("#A78BFA");
    private static readonly Brush DotWaitingBrush = CreateBrush("#FBBF24");
    private static readonly Brush DotDoneBrush = CreateBrush("#34D399");
    private static readonly Brush DotErrorBrush = CreateBrush("#FF453A");
    private static readonly Brush DotIdleBrush = CreateBrush("#6E7891");

    private static readonly Brush PillBgWorkingBrush = CreateBrush("#223B82F6");
    private static readonly Brush PillBdWorkingBrush = CreateBrush("#4A60A5FA");
    private static readonly Brush TextWorkingBrush = CreateBrush("#93C5FD");

    private static readonly Brush PillBgThinkingBrush = CreateBrush("#228B5CF6");
    private static readonly Brush PillBdThinkingBrush = CreateBrush("#4AA78BFA");
    private static readonly Brush TextThinkingBrush = CreateBrush("#C4B5FD");

    private static readonly Brush PillBgWaitingBrush = CreateBrush("#2AF59E0B");
    private static readonly Brush PillBdWaitingBrush = CreateBrush("#66FBBF24");
    private static readonly Brush TextWaitingBrush = CreateBrush("#FDE68A");

    private static readonly Brush PillBgDoneBrush = CreateBrush("#1E10B981");
    private static readonly Brush PillBdDoneBrush = CreateBrush("#4534D399");
    private static readonly Brush TextDoneBrush = CreateBrush("#6EE7B7");

    private static readonly Brush PillBgErrorBrush = CreateBrush("#26FF453A");
    private static readonly Brush PillBdErrorBrush = CreateBrush("#58FF453A");
    private static readonly Brush TextErrorBrush = CreateBrush("#FCA5A5");

    private static readonly Brush PillBgIdleBrush = CreateBrush("#18FFFFFF");
    private static readonly Brush PillBdIdleBrush = CreateBrush("#24FFFFFF");
    private static readonly Brush TextIdleBrush = CreateBrush("#9CA7BE");

    public string SessionId { get; init; } = string.Empty;
    public string ParentSessionId { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public AgentProvider Provider { get; init; } = AgentProvider.Antigravity;
    public string ProjectName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = "AI Agent";
    public string WorkspaceHint { get; init; } = string.Empty;
    public AgentRunState State { get; init; } = AgentRunState.Idle;
    public string CurrentActionText { get; init; } = "Đang chờ tác vụ...";
    public int ProgressPercent { get; init; }
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ActiveTurnStartedAtUtc { get; init; }
    public DateTime? TurnCompletedAtUtc { get; init; }
    public DateTime LastUpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public bool IsSubagent { get; init; }
    public int ChildCount { get; init; }
    public bool IsExpanded { get; init; } = true;
    public int ProcessId { get; init; }
    public IntPtr TargetWindowHandle { get; init; }

    public bool IsActive => State is AgentRunState.Working or AgentRunState.Thinking or AgentRunState.WaitingApproval;
    public bool IsWaitingApproval => State == AgentRunState.WaitingApproval;
    public bool IsParentRow => !IsSubagent;
    public bool HasChildren => !IsSubagent && ChildCount > 0;
    public string ExpandChevron => IsExpanded ? "▾" : "▸";

    public string ResolvedProjectName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ProjectName))
                return ProjectName;

            if (!string.IsNullOrWhiteSpace(WorkspaceHint))
            {
                try
                {
                    string trimmed = WorkspaceHint.TrimEnd('\\', '/');
                    string? leaf = Path.GetFileName(trimmed);
                    if (!string.IsNullOrWhiteSpace(leaf))
                        return leaf;
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(DisplayName) &&
                !DisplayName.StartsWith("Phiên ", StringComparison.Ordinal) &&
                DisplayName != "AI Agent")
            {
                return DisplayName;
            }

            return "Dự án AI";
        }
    }

    /// <summary>
    /// Column 1 label: Real [Tên Dự Án] on parent rows, or [Vai trò Subagent] on indented child rows.
    /// </summary>
    public string PrimaryRowLabel => IsSubagent
        ? (!string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.StartsWith("Phiên ", StringComparison.Ordinal)
            ? DisplayName
            : "Nhánh thực thi")
        : ResolvedProjectName;

    public string SubCountBadgeText => ChildCount > 0 ? $"+{ChildCount}" : string.Empty;

    public int ClampedProgressPercent => State == AgentRunState.Completed
        ? 100
        : Math.Clamp(ProgressPercent, 0, 100);

    public double ProgressRatio => ClampedProgressPercent / 100.0;

    public string ProgressText => $"{ClampedProgressPercent}%";

    /// <summary>
    /// Pixel width for the 54px inline capsule progress bar in AgentMonitorView.
    /// </summary>
    public double ProgressBarFillWidth => Math.Max(3.5, Math.Round(ProgressRatio * 54.0, 1));

    public string ProviderBadge => Provider switch
    {
        AgentProvider.Antigravity => IsSubagent ? "AGY SUB" : "ANTIGRAVITY",
        AgentProvider.Teamwork => "TEAMWORK",
        AgentProvider.Codex => "CODEX CLI",
        AgentProvider.ClaudeCode => "CLAUDE CODE",
        _ => "AGENT HOOK"
    };

    public string ShortProviderTag => Provider switch
    {
        AgentProvider.Antigravity => "AGY",
        AgentProvider.Teamwork => "Team",
        AgentProvider.Codex => "Codex",
        AgentProvider.ClaudeCode => "Claude",
        _ => "Agent"
    };

    public IconsaxKind ProviderIcon => Provider switch
    {
        AgentProvider.Teamwork => IconsaxKind.Hierarchy,
        AgentProvider.Antigravity => IconsaxKind.Bot,
        AgentProvider.Codex => IconsaxKind.Cpu,
        AgentProvider.ClaudeCode => IconsaxKind.Flash,
        _ => IconsaxKind.Activity
    };

    public string StatusBadgeText => State switch
    {
        AgentRunState.WaitingApproval => "Chờ duyệt!",
        AgentRunState.Working => "Đang chạy",
        AgentRunState.Thinking => "Đang nghĩ",
        AgentRunState.Completed => "Hoàn tất",
        AgentRunState.Error => "Gặp lỗi",
        _ => "Đang rảnh"
    };

    /// <summary>
    /// Live per-turn stopwatch (mm:ss or hh:mm:ss) that counts active turn duration
    /// and freezes at the exact turn completion timestamp once Completed.
    /// </summary>
    public string RuntimeText
    {
        get
        {
            DateTime startRef = ActiveTurnStartedAtUtc != default ? ActiveTurnStartedAtUtc : StartedAtUtc;
            DateTime endRef = IsActive
                ? DateTime.UtcNow
                : (TurnCompletedAtUtc ?? LastUpdatedAtUtc);
            TimeSpan span = endRef > startRef ? endRef - startRef : TimeSpan.Zero;
            if (span.TotalHours >= 1.0)
                return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
            return $"{span.Minutes:00}:{span.Seconds:00}";
        }
    }

    public string ElapsedText => RuntimeText;

    public string RelativeTimeText
    {
        get
        {
            TimeSpan ago = DateTime.UtcNow - LastUpdatedAtUtc;
            if (ago.TotalSeconds < 6) return "Vừa xong";
            if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds}s trước";
            if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}p trước";
            return $"{(int)ago.TotalHours}g trước";
        }
    }

    public Brush StatusDotBrush => State switch
    {
        AgentRunState.WaitingApproval => DotWaitingBrush,
        AgentRunState.Working => DotWorkingBrush,
        AgentRunState.Thinking => DotThinkingBrush,
        AgentRunState.Completed => DotDoneBrush,
        AgentRunState.Error => DotErrorBrush,
        _ => DotIdleBrush
    };

    public Brush ProgressBarBrush => State switch
    {
        AgentRunState.WaitingApproval => DotWaitingBrush,
        AgentRunState.Completed => DotDoneBrush,
        AgentRunState.Error => DotErrorBrush,
        AgentRunState.Thinking => DotThinkingBrush,
        _ => DotWorkingBrush
    };

    public Brush StatusPillBackgroundBrush => State switch
    {
        AgentRunState.WaitingApproval => PillBgWaitingBrush,
        AgentRunState.Working => PillBgWorkingBrush,
        AgentRunState.Thinking => PillBgThinkingBrush,
        AgentRunState.Completed => PillBgDoneBrush,
        AgentRunState.Error => PillBgErrorBrush,
        _ => PillBgIdleBrush
    };

    public Brush StatusPillBorderBrush => State switch
    {
        AgentRunState.WaitingApproval => PillBdWaitingBrush,
        AgentRunState.Working => PillBdWorkingBrush,
        AgentRunState.Thinking => PillBdThinkingBrush,
        AgentRunState.Completed => PillBdDoneBrush,
        AgentRunState.Error => PillBdErrorBrush,
        _ => PillBdIdleBrush
    };

    public Brush StatusTextBrush => State switch
    {
        AgentRunState.WaitingApproval => TextWaitingBrush,
        AgentRunState.Working => TextWorkingBrush,
        AgentRunState.Thinking => TextThinkingBrush,
        AgentRunState.Completed => TextDoneBrush,
        AgentRunState.Error => TextErrorBrush,
        _ => TextIdleBrush
    };

    public AgentSessionItem WithTreePresentation(bool isExpanded, int childCount, string groupId) => new()
    {
        SessionId = SessionId,
        ParentSessionId = ParentSessionId,
        GroupId = groupId,
        Provider = Provider,
        ProjectName = ProjectName,
        DisplayName = DisplayName,
        WorkspaceHint = WorkspaceHint,
        State = State,
        CurrentActionText = CurrentActionText,
        ProgressPercent = ProgressPercent,
        StartedAtUtc = StartedAtUtc,
        ActiveTurnStartedAtUtc = ActiveTurnStartedAtUtc,
        TurnCompletedAtUtc = TurnCompletedAtUtc,
        LastUpdatedAtUtc = LastUpdatedAtUtc,
        IsSubagent = IsSubagent,
        ChildCount = childCount,
        IsExpanded = isExpanded,
        ProcessId = ProcessId,
        TargetWindowHandle = TargetWindowHandle
    };

    private static Brush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public sealed class AgentDashboardSnapshot
{
    public static AgentDashboardSnapshot Empty { get; } = new();

    public int ActiveCount { get; init; }
    public int WaitingApprovalCount { get; init; }
    public int CompletedRecentCount { get; init; }
    public IReadOnlyList<AgentSessionItem> Sessions { get; init; } = [];
    public AgentSessionItem? PrimarySession { get; init; }

    public bool HasAnySessions => Sessions.Count > 0;
    public bool HasActiveOrWaiting => ActiveCount > 0 || WaitingApprovalCount > 0;
    public bool IsCollapsedWaiting => WaitingApprovalCount > 0;

    public string CollapsedBadgeText { get; init; } = string.Empty;
    public string CollapsedAccentHex => IsCollapsedWaiting ? "#FBBF24" : "#60A5FA";
}
