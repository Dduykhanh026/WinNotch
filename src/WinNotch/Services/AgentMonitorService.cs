using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using WinNotch.Interop;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class AgentMonitorService : IDisposable
{
    public const string PipeName = "WinNotch_AgentActivity_v1";

    private static readonly HashSet<string> TerminalAndIdeProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "Code", "Cursor", "Windsurf", "Antigravity", "agy",
        "pwsh", "powershell", "cmd", "wezterm-gui", "alacritty", "mintty", "devenv", "rider64"
    };

    private readonly SettingsService _settingsService;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, TrackedAgentState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ParentId, string Role)> _subagentRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _dirtyPaths = new();
    private readonly Dictionary<string, (AgentRunState State, long Ticks)> _lastHudBySession = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _pipeTask;

    private string _agyBrainDir = string.Empty;
    private string _teamworkDir = string.Empty;
    private string _codexDir = string.Empty;
    private string _claudeDir = string.Empty;
    private volatile bool _teamworkDirty = true;
    private bool _isSeeded;
    private int _previewToggleIndex;

    public AgentDashboardSnapshot LatestSnapshot { get; private set; } = AgentDashboardSnapshot.Empty;

    public event Action<AgentDashboardSnapshot>? SnapshotUpdated;
    public event Action<HudPayload>? AgentHudRequested;

    public AgentMonitorService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Start()
    {
        if (_cts != null) return;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _agyBrainDir = Path.Combine(home, ".gemini", "antigravity-cli", "brain");
        _teamworkDir = Path.Combine(home, ".agents", "teamwork");
        _codexDir = Path.Combine(home, ".codex", "sessions");
        _claudeDir = Path.Combine(home, ".claude", "projects");

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        TryStartWatcher(_agyBrainDir, "transcript.jsonl", includeSubdirs: true);
        TryStartWatcher(_teamworkDir, "*.md", includeSubdirs: true);
        TryStartWatcher(_codexDir, "*.jsonl", includeSubdirs: true);
        TryStartWatcher(_claudeDir, "*.jsonl", includeSubdirs: true);

        _pipeTask = Task.Run(() => RunNamedPipeServerLoopAsync(token), token);
        _loopTask = Task.Run(() => RunMonitorLoopAsync(token), token);
    }

    public void ClearCompletedSessions()
    {
        lock (_stateLock)
        {
            foreach (TrackedAgentState s in _sessions.Values)
            {
                if (s.State is AgentRunState.Completed or AgentRunState.Idle or AgentRunState.Error)
                {
                    s.DismissedByUser = true;
                }
            }
        }

        PublishSnapshot();
    }

    public void TriggerPreviewAgentHud()
    {
        _previewToggleIndex = (_previewToggleIndex + 1) % 2;
        bool isWaitingSample = _previewToggleIndex == 1;

        AgentHudRequested?.Invoke(new HudPayload
        {
            Kind = HudKind.AgentAlert,
            Icon = isWaitingSample ? IconsaxKind.Hierarchy : IconsaxKind.Bot,
            Title = isWaitingSample ? "AGY · WinNotch Power Suite" : "Teamwork · Mốc 6/6 Hoàn tất",
            Subtitle = isWaitingSample ? "Chờ bạn duyệt!" : "Hoàn tất",
            BodyText = isWaitingSample
                ? "Chờ bạn phê duyệt bản kế hoạch agent_monitor_plan.md để tiếp tục thực thi."
                : "Đã biên dịch Release (0 Warning, 0 Error) và đóng gói xong 3 bản cài đặt.",
            TimestampBadge = isWaitingSample ? "Cần phản hồi" : "Xong",
            AccentHex = isWaitingSample ? "#FBBF24" : "#34D399"
        });
    }

    public bool CopyHooksConfigToClipboard()
    {
        const string hooksSample = """
{
  "winnotch-live-activity": {
    "PreToolUse": [
      {
        "matcher": "*",
        "hooks": [
          {
            "type": "command",
            "command": "powershell -NoProfile -Command \"$in=[Console]::In.ReadToEnd(); $p=New-Object System.IO.Pipes.NamedPipeClientStream('.','WinNotch_AgentActivity_v1','Out'); try{$p.Connect(80); $w=New-Object System.IO.StreamWriter($p); $w.WriteLine($in); $w.Flush()}catch{}finally{$p.Dispose()}; Write-Output '{}'\""
          }
        ]
      }
    ],
    "Stop": [
      {
        "type": "command",
        "command": "powershell -NoProfile -Command \"$p=New-Object System.IO.Pipes.NamedPipeClientStream('.','WinNotch_AgentActivity_v1','Out'); try{$p.Connect(80); $w=New-Object System.IO.StreamWriter($p); $w.WriteLine('{\\\"state\\\":\\\"done\\\",\\\"name\\\":\\\"Hook Session\\\"}'); $w.Flush()}catch{}finally{$p.Dispose()}; Write-Output '{}'\""
      }
    ]
  }
}
""";
        try
        {
            Clipboard.SetText(hooksSample);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool FocusAgentHostWindow(string? sessionId = null)
    {
        IntPtr explicitHwnd = IntPtr.Zero;
        int explicitPid = 0;
        string workspaceHint = "WinNotch";

        lock (_stateLock)
        {
            TrackedAgentState? target = null;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _sessions.TryGetValue(sessionId, out target);
            }
            target ??= _sessions.Values
                .OrderByDescending(s => s.State == AgentRunState.WaitingApproval)
                .ThenByDescending(s => s.LastUpdatedAtUtc)
                .FirstOrDefault();

            if (target != null)
            {
                explicitHwnd = target.TargetWindowHandle;
                explicitPid = target.ProcessId;
                if (!string.IsNullOrWhiteSpace(target.WorkspaceHint))
                    workspaceHint = target.WorkspaceHint;
            }
        }

        if (explicitHwnd != IntPtr.Zero && NativeMethods.IsWindow(explicitHwnd) && NativeMethods.IsWindowVisible(explicitHwnd))
        {
            BringHwndToFront(explicitHwnd);
            return true;
        }

        IntPtr bestHwnd = IntPtr.Zero;
        int bestScore = -1;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            int titleLen = NativeMethods.GetWindowTextLengthW(hWnd);
            if (titleLen <= 0) return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;

            if (explicitPid > 0 && pid == (uint)explicitPid)
            {
                bestHwnd = hWnd;
                bestScore = 1000;
                return false;
            }

            string procName = string.Empty;
            try
            {
                using Process proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch
            {
                return true;
            }

            if (!TerminalAndIdeProcessNames.Contains(procName))
                return true;

            var sb = new StringBuilder(titleLen + 2);
            NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            int score = 10;
            if (procName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
                procName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
                procName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }

            if (title.Contains("agy", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("antigravity", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("codex", StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
            }

            if (!string.IsNullOrWhiteSpace(workspaceHint) &&
                title.Contains(workspaceHint, StringComparison.OrdinalIgnoreCase))
            {
                score += 45;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestHwnd = hWnd;
            }

            return true;
        }, IntPtr.Zero);

        if (bestHwnd != IntPtr.Zero)
        {
            BringHwndToFront(bestHwnd);
            return true;
        }

        return false;
    }

    private static void BringHwndToFront(IntPtr hWnd)
    {
        try
        {
            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            }
            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        catch { }
    }

    private void TryStartWatcher(string directoryPath, string filter, bool includeSubdirs)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return;

            var watcher = new FileSystemWatcher(directoryPath, filter)
            {
                IncludeSubdirectories = includeSubdirs,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 32768
            };

            FileSystemEventHandler handler = (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.FullPath))
                {
                    if (e.FullPath.StartsWith(_teamworkDir, StringComparison.OrdinalIgnoreCase))
                        _teamworkDirty = true;
                    else
                        _dirtyPaths.Enqueue(e.FullPath);
                }
            };

            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Renamed += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.FullPath))
                    _dirtyPaths.Enqueue(e.FullPath);
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch
        {
            // Directory inaccessible or deleted; non-fatal
        }
    }

    private async Task RunMonitorLoopAsync(CancellationToken token)
    {
        try
        {
            SeedRecentSessionsOnDisk();
            _isSeeded = true;
            PublishSnapshot();

            int tick = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1400));
            while (!token.IsCancellationRequested)
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    break;

                if (!_settingsService.Current.EnableAgentMonitor)
                    continue;

                tick++;
                bool anyUpdated = DrainDirtyFileEvents(allowHud: true);

                // Fast NTFS open-handle length check on active/recent tracked files (< 0.02 ms)
                if (CheckTrackedFileGrowth(allowHud: true))
                    anyUpdated = true;

                // Refresh Teamwork progress & discover newly created brain folders every ~4.2s
                if (_teamworkDirty || (tick % 3 == 0))
                {
                    _teamworkDirty = false;
                    if (RefreshTeamworkSwarmState(allowHud: true))
                        anyUpdated = true;

                    if (DiscoverNewBrainFolders(allowHud: true))
                        anyUpdated = true;
                }

                // Periodic idle decay & elapsed clock refresh when active agents exist
                bool hasLiveElapsed;
                lock (_stateLock)
                {
                    if (ApplyInactivityDecayUnsafe())
                        anyUpdated = true;

                    hasLiveElapsed = _sessions.Values.Any(s =>
                        !s.DismissedByUser &&
                        s.State is AgentRunState.Working or AgentRunState.Thinking or AgentRunState.WaitingApproval);
                }

                if (anyUpdated || hasLiveElapsed || (tick % 10 == 0))
                {
                    PublishSnapshot();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void SeedRecentSessionsOnDisk()
    {
        DateTime cutoffUtc = DateTime.UtcNow.AddMinutes(-90);

        // 1. Seed recent Antigravity CLI conversations (main sessions first so subagent roles resolve!)
        if (Directory.Exists(_agyBrainDir))
        {
            try
            {
                var recentLogs = new List<(string ConvId, FileInfo File)>();
                foreach (string convDir in Directory.EnumerateDirectories(_agyBrainDir))
                {
                    string logPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                    if (!File.Exists(logPath)) continue;

                    var fi = new FileInfo(logPath);
                    if (fi.LastWriteTimeUtc >= cutoffUtc)
                    {
                        recentLogs.Add((Path.GetFileName(convDir), fi));
                    }
                }

                // Parse oldest-started first so parent invoke_subagent steps populate _subagentRoles before children
                foreach (var (convId, fi) in recentLogs.OrderBy(x => x.File.CreationTimeUtc).TakeLast(16))
                {
                    ProcessTranscriptFile(fi.FullName, convId, AgentProvider.Antigravity, allowHud: false);
                }

                ApplyDiscoveredSubagentLabels();
            }
            catch { }
        }

        // 2. Seed Teamwork Swarm if active within last 60 minutes
        RefreshTeamworkSwarmState(allowHud: false);

        // 3. Seed recent Codex CLI sessions
        SeedRecentJsonlTree(_codexDir, AgentProvider.Codex, cutoffUtc);

        // 4. Seed recent Claude Code CLI sessions
        SeedRecentJsonlTree(_claudeDir, AgentProvider.ClaudeCode, cutoffUtc);

        lock (_stateLock)
        {
            ApplyInactivityDecayUnsafe();
        }
    }

    private void SeedRecentJsonlTree(string rootDir, AgentProvider provider, DateTime cutoffUtc)
    {
        if (!Directory.Exists(rootDir)) return;
        try
        {
            var dirInfo = new DirectoryInfo(rootDir);
            foreach (FileInfo fi in dirInfo.EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                                           .Where(f => f.LastWriteTimeUtc >= cutoffUtc)
                                           .OrderByDescending(f => f.LastWriteTimeUtc)
                                           .Take(4))
            {
                string id = $"{provider}:{Path.GetFileNameWithoutExtension(fi.Name)}";
                ProcessTranscriptFile(fi.FullName, id, provider, allowHud: false);
            }
        }
        catch { }
    }

    private bool DiscoverNewBrainFolders(bool allowHud)
    {
        if (!Directory.Exists(_agyBrainDir)) return false;
        bool anyAdded = false;
        DateTime cutoffUtc = DateTime.UtcNow.AddMinutes(-10);

        try
        {
            foreach (string convDir in Directory.EnumerateDirectories(_agyBrainDir))
            {
                string convId = Path.GetFileName(convDir);
                lock (_stateLock)
                {
                    if (_sessions.ContainsKey(convId)) continue;
                }

                string logPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                if (!File.Exists(logPath)) continue;

                var fi = new FileInfo(logPath);
                if (fi.LastWriteTimeUtc >= cutoffUtc)
                {
                    if (ProcessTranscriptFile(logPath, convId, AgentProvider.Antigravity, allowHud))
                        anyAdded = true;
                }
            }

            if (anyAdded)
                ApplyDiscoveredSubagentLabels();
        }
        catch { }

        return anyAdded;
    }

    private bool DrainDirtyFileEvents(bool allowHud)
    {
        if (_dirtyPaths.IsEmpty) return false;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_dirtyPaths.TryDequeue(out string? p))
        {
            if (!string.IsNullOrWhiteSpace(p))
                unique.Add(p);
        }

        bool anyChanged = false;
        foreach (string path in unique)
        {
            if (!File.Exists(path)) continue;

            if (path.StartsWith(_agyBrainDir, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith("transcript.jsonl", StringComparison.OrdinalIgnoreCase))
            {
                string? convId = ExtractAntigravityConvId(path);
                if (!string.IsNullOrEmpty(convId) &&
                    ProcessTranscriptFile(path, convId, AgentProvider.Antigravity, allowHud))
                {
                    anyChanged = true;
                }
            }
            else if (path.StartsWith(_codexDir, StringComparison.OrdinalIgnoreCase))
            {
                string id = $"Codex:{Path.GetFileNameWithoutExtension(path)}";
                if (ProcessTranscriptFile(path, id, AgentProvider.Codex, allowHud))
                    anyChanged = true;
            }
            else if (path.StartsWith(_claudeDir, StringComparison.OrdinalIgnoreCase))
            {
                string id = $"ClaudeCode:{Path.GetFileNameWithoutExtension(path)}";
                if (ProcessTranscriptFile(path, id, AgentProvider.ClaudeCode, allowHud))
                    anyChanged = true;
            }
        }

        if (anyChanged)
            ApplyDiscoveredSubagentLabels();

        return anyChanged;
    }

    private bool CheckTrackedFileGrowth(bool allowHud)
    {
        List<(string Id, string Path, AgentProvider Provider, long Offset)> candidates;
        lock (_stateLock)
        {
            DateTime recentCutoff = DateTime.UtcNow.AddMinutes(-35);
            candidates = _sessions.Values
                .Where(s => !string.IsNullOrEmpty(s.TranscriptPath) && s.LastUpdatedAtUtc >= recentCutoff)
                .Select(s => (s.SessionId, s.TranscriptPath, s.Provider, s.LastByteOffset))
                .ToList();
        }

        bool anyChanged = false;
        foreach (var c in candidates)
        {
            try
            {
                var fi = new FileInfo(c.Path);
                if (fi.Exists && fi.Length != c.Offset)
                {
                    if (ProcessTranscriptFile(c.Path, c.Id, c.Provider, allowHud))
                        anyChanged = true;
                }
            }
            catch { }
        }

        if (anyChanged)
            ApplyDiscoveredSubagentLabels();

        return anyChanged;
    }

    private bool ProcessTranscriptFile(string filePath, string sessionId, AgentProvider provider, bool allowHud)
    {
        TrackedAgentState session;
        lock (_stateLock)
        {
            if (!_sessions.TryGetValue(sessionId, out session!))
            {
                DateTime createdUtc = DateTime.UtcNow;
                try { createdUtc = File.GetCreationTimeUtc(filePath); } catch { }

                string shortId = sessionId.Length >= 8 ? sessionId[..8] : sessionId;
                session = new TrackedAgentState
                {
                    SessionId = sessionId,
                    Provider = provider,
                    TranscriptPath = filePath,
                    DisplayName = provider switch
                    {
                        AgentProvider.Codex => $"Codex · {shortId}",
                        AgentProvider.ClaudeCode => $"Claude · {shortId}",
                        _ => $"Phiên {shortId}"
                    },
                    WorkspaceHint = string.Empty,
                    StartedAtUtc = createdUtc,
                    ActiveTurnStartedAtUtc = createdUtc
                };
                _sessions[sessionId] = session;
            }
        }

        AgentRunState prevState;
        bool updated;
        lock (_stateLock)
        {
            updated = provider switch
            {
                AgentProvider.Codex => AgentTranscriptParser.ParseCodexIncremental(session, out prevState),
                AgentProvider.ClaudeCode => AgentTranscriptParser.ParseClaudeIncremental(session, out prevState),
                _ => AgentTranscriptParser.ParseAntigravityIncremental(session, _subagentRoles, out prevState)
            };

            if (_subagentRoles.TryGetValue(sessionId, out var roleMeta))
            {
                session.IsSubagent = true;
                session.ParentSessionId = roleMeta.ParentId;
                if (session.DisplayName.StartsWith("Phiên ", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(session.DisplayName))
                {
                    session.DisplayName = roleMeta.Role;
                }

                if (_sessions.TryGetValue(roleMeta.ParentId, out TrackedAgentState? parent))
                {
                    SyncParentChildProjectUnsafe(parent, session);
                }
            }
        }

        if (updated && allowHud && _isSeeded)
        {
            MaybeEmitTransitionHud(session, prevState);
        }

        return updated;
    }

    private void ApplyDiscoveredSubagentLabels()
    {
        lock (_stateLock)
        {
            foreach (var (childId, meta) in _subagentRoles)
            {
                if (_sessions.TryGetValue(childId, out TrackedAgentState? child))
                {
                    child.IsSubagent = true;
                    child.ParentSessionId = meta.ParentId;
                    if (child.DisplayName.StartsWith("Phiên ", StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(child.DisplayName))
                    {
                        child.DisplayName = meta.Role;
                    }

                    if (_sessions.TryGetValue(meta.ParentId, out TrackedAgentState? parent))
                    {
                        SyncParentChildProjectUnsafe(parent, child);
                    }
                }
            }
        }
    }

    private static void SyncParentChildProjectUnsafe(TrackedAgentState parent, TrackedAgentState child)
    {
        if (string.IsNullOrWhiteSpace(child.ProjectName) && !string.IsNullOrWhiteSpace(parent.ProjectName))
            child.ProjectName = parent.ProjectName;
        else if (string.IsNullOrWhiteSpace(parent.ProjectName) && !string.IsNullOrWhiteSpace(child.ProjectName))
            parent.ProjectName = child.ProjectName;

        if (string.IsNullOrWhiteSpace(child.WorkspaceHint) && !string.IsNullOrWhiteSpace(parent.WorkspaceHint))
            child.WorkspaceHint = parent.WorkspaceHint;
        else if (string.IsNullOrWhiteSpace(parent.WorkspaceHint) && !string.IsNullOrWhiteSpace(child.WorkspaceHint))
            parent.WorkspaceHint = child.WorkspaceHint;
    }

    private bool RefreshTeamworkSwarmState(bool allowHud)
    {
        if (!AgentTranscriptParser.TryReadTeamworkProgressSummary(
                _teamworkDir,
                out string projectName,
                out string activeMilestone,
                out int doneM,
                out int totalM,
                out int progressPct,
                out int activeWorkers,
                out DateTime earliestStartUtc,
                out DateTime latestWriteUtc))
        {
            return false;
        }

        // Ignore stale historical teamwork folders older than 60 minutes
        if ((DateTime.UtcNow - latestWriteUtc).TotalMinutes > 60.0)
            return false;

        const string swarmId = "teamwork:active-swarm";
        string resolvedProject = !string.IsNullOrWhiteSpace(projectName) ? projectName : "WinNotch";
        AgentRunState prevState;
        TrackedAgentState swarm;
        bool changed = false;

        lock (_stateLock)
        {
            if (!_sessions.TryGetValue(swarmId, out swarm!))
            {
                swarm = new TrackedAgentState
                {
                    SessionId = swarmId,
                    Provider = AgentProvider.Teamwork,
                    ProjectName = resolvedProject,
                    DisplayName = resolvedProject,
                    WorkspaceHint = resolvedProject,
                    StartedAtUtc = earliestStartUtc,
                    ActiveTurnStartedAtUtc = earliestStartUtc
                };
                _sessions[swarmId] = swarm;
            }

            prevState = swarm.State;
            bool allMilestonesDone = totalM > 0 && doneM >= totalM && string.IsNullOrEmpty(activeMilestone);
            bool recentlyActive = (DateTime.UtcNow - latestWriteUtc).TotalMinutes <= 7.0;

            AgentRunState nextState = allMilestonesDone
                ? AgentRunState.Completed
                : recentlyActive ? AgentRunState.Working : AgentRunState.Completed;

            int nextProgress = nextState == AgentRunState.Completed ? 100 : progressPct;

            string milestoneBadge = totalM > 0 ? $"Mốc {Math.Min(doneM + 1, totalM)}/{totalM}" : "Đang điều phối";
            string nextAction = allMilestonesDone
                ? $"Đã hoàn tất toàn bộ {totalM}/{totalM} mốc"
                : !string.IsNullOrEmpty(activeMilestone)
                    ? activeMilestone
                    : $"{milestoneBadge} · {Math.Max(1, activeWorkers)} nhánh thực thi";

            if (swarm.State != nextState ||
                swarm.CurrentActionText != nextAction ||
                swarm.ProgressPercent != nextProgress ||
                swarm.ProjectName != resolvedProject)
            {
                swarm.ProjectName = resolvedProject;
                swarm.DisplayName = resolvedProject;
                swarm.WorkspaceHint = resolvedProject;
                swarm.State = nextState;
                swarm.CurrentActionText = nextAction;
                swarm.ProgressPercent = nextProgress;
                if (swarm.ActiveTurnStartedAtUtc == default || earliestStartUtc < swarm.ActiveTurnStartedAtUtc)
                    swarm.ActiveTurnStartedAtUtc = earliestStartUtc;
                swarm.TurnCompletedAtUtc = nextState == AgentRunState.Completed ? latestWriteUtc : null;
                swarm.LastUpdatedAtUtc = latestWriteUtc;
                swarm.DismissedByUser = false;
                changed = true;
            }
        }

        if (changed && allowHud && _isSeeded)
        {
            MaybeEmitTransitionHud(swarm, prevState);
        }

        return changed;
    }

    private bool ApplyInactivityDecayUnsafe()
    {
        DateTime now = DateTime.UtcNow;
        bool changed = false;

        foreach (TrackedAgentState s in _sessions.Values)
        {
            double idleMinutes = (now - s.LastUpdatedAtUtc).TotalMinutes;

            // Subagents or sessions that haven't logged a tool step in > 4.5 minutes while Working/Thinking
            // have likely finished or exited without a final marker.
            if (s.State is AgentRunState.Working or AgentRunState.Thinking && idleMinutes > 4.5)
            {
                s.State = AgentRunState.Completed;
                s.ProgressPercent = 100;
                s.TurnCompletedAtUtc ??= s.LastUpdatedAtUtc;
                changed = true;
            }
            else if (s.State == AgentRunState.WaitingApproval && idleMinutes > 25.0)
            {
                s.State = AgentRunState.Idle;
                changed = true;
            }
        }

        return changed;
    }

    private void MaybeEmitTransitionHud(TrackedAgentState session, AgentRunState prevState)
    {
        if (!_settingsService.Current.EnableAgentMonitor || !_settingsService.Current.EnableAgentApprovalHud)
            return;

        AgentRunState next = session.State;
        if (next == prevState) return;

        // Avoid noisy completion HUDs for short ephemeral subagents; ALWAYS surface WaitingApproval & Error!
        bool shouldAlert = next switch
        {
            AgentRunState.WaitingApproval => true,
            AgentRunState.Error => true,
            AgentRunState.Completed => !session.IsSubagent &&
                                       prevState is AgentRunState.Working or AgentRunState.Thinking &&
                                       (DateTime.UtcNow - session.ActiveTurnStartedAtUtc).TotalSeconds >= 10.0,
            _ => false
        };

        if (!shouldAlert) return;

        long nowTicks = Stopwatch.GetTimestamp();
        lock (_stateLock)
        {
            if (_lastHudBySession.TryGetValue(session.SessionId, out var last))
            {
                double elapsedSec = (double)(nowTicks - last.Ticks) / Stopwatch.Frequency;
                if (last.State == next && elapsedSec < 4.0)
                    return;
            }
            _lastHudBySession[session.SessionId] = (next, nowTicks);
        }

        string tag = session.Provider switch
        {
            AgentProvider.Teamwork => "Teamwork",
            AgentProvider.Codex => "Codex",
            AgentProvider.ClaudeCode => "Claude",
            _ => "AGY"
        };

        var payload = new HudPayload
        {
            Kind = HudKind.AgentAlert,
            Icon = next == AgentRunState.WaitingApproval ? IconsaxKind.Hierarchy : IconsaxKind.Bot,
            Title = $"{tag} · {session.DisplayName}",
            Subtitle = next switch
            {
                AgentRunState.WaitingApproval => "Chờ bạn duyệt!",
                AgentRunState.Error => "Gặp lỗi",
                _ => "Hoàn tất"
            },
            BodyText = session.CurrentActionText,
            TimestampBadge = next == AgentRunState.WaitingApproval ? "Cần phản hồi" : "Xong",
            AccentHex = next switch
            {
                AgentRunState.WaitingApproval => "#FBBF24",
                AgentRunState.Error => "#FF453A",
                _ => "#34D399"
            },
            SourceAumid = session.SessionId,
            TargetWindowHandle = session.TargetWindowHandle
        };

        Application.Current?.Dispatcher.BeginInvoke(() => AgentHudRequested?.Invoke(payload));
    }

    private async Task RunNamedPipeServerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    ProcessHookPayload(payload);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(500, token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private void ProcessHookPayload(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string id = ReadJsonString(root, "conversationId");
            if (string.IsNullOrWhiteSpace(id)) id = ReadJsonString(root, "id");
            if (string.IsNullOrWhiteSpace(id)) id = "hook:default";

            string name = ReadJsonString(root, "name");
            if (string.IsNullOrWhiteSpace(name)) name = ReadJsonString(root, "lastUserInput");
            if (string.IsNullOrWhiteSpace(name)) name = "Custom Hook Agent";
            if (name.Length > 36) name = name[..36] + "…";

            string rawState = ReadJsonString(root, "state").ToLowerInvariant();
            string project = ReadJsonString(root, "project");
            string workspace = ReadJsonString(root, "workspace");
            string toolName = ReadJsonString(root, "toolName");
            string detail = ReadJsonString(root, "detail");
            int explicitProgress = root.TryGetProperty("progress", out JsonElement prEl) && prEl.TryGetInt32(out int pr) ? Math.Clamp(pr, 0, 100) : -1;
            int pid = root.TryGetProperty("pid", out JsonElement pEl) && pEl.TryGetInt32(out int p) ? p : 0;

            AgentRunState nextState = rawState switch
            {
                "waiting" or "approval" or "ask" => AgentRunState.WaitingApproval,
                "done" or "completed" or "stop" => AgentRunState.Completed,
                "error" or "failed" => AgentRunState.Error,
                "thinking" => AgentRunState.Thinking,
                _ => AgentRunState.Working
            };

            string action = !string.IsNullOrWhiteSpace(detail)
                ? detail
                : !string.IsNullOrWhiteSpace(toolName)
                    ? AgentTranscriptParser.SummarizeToolCall(toolName, root)
                    : nextState == AgentRunState.Completed ? "Đã hoàn tất qua Hook" : "Đang thực thi qua Hook";

            DateTime nowUtc = DateTime.UtcNow;
            TrackedAgentState session;
            AgentRunState prevState;
            lock (_stateLock)
            {
                if (!_sessions.TryGetValue(id, out session!))
                {
                    session = new TrackedAgentState
                    {
                        SessionId = id,
                        Provider = AgentProvider.CustomHook,
                        ProjectName = project,
                        DisplayName = name,
                        WorkspaceHint = workspace,
                        StartedAtUtc = nowUtc,
                        ActiveTurnStartedAtUtc = nowUtc
                    };
                    _sessions[id] = session;
                }

                prevState = session.State;
                if (!string.IsNullOrWhiteSpace(project))
                    session.ProjectName = project;
                if (!string.IsNullOrWhiteSpace(workspace))
                    session.WorkspaceHint = workspace;

                if (prevState is AgentRunState.Idle or AgentRunState.Completed &&
                    nextState is AgentRunState.Working or AgentRunState.Thinking or AgentRunState.WaitingApproval)
                {
                    session.ActiveTurnStartedAtUtc = nowUtc;
                    session.TurnCompletedAtUtc = null;
                }

                session.State = nextState;
                session.CurrentActionText = action;
                session.ProgressPercent = explicitProgress >= 0
                    ? explicitProgress
                    : nextState switch
                    {
                        AgentRunState.Completed => 100,
                        AgentRunState.WaitingApproval => Math.Max(session.ProgressPercent, 35),
                        _ => Math.Max(session.ProgressPercent, 50)
                    };
                if (nextState == AgentRunState.Completed)
                    session.TurnCompletedAtUtc = nowUtc;

                session.LastUpdatedAtUtc = nowUtc;
                session.DismissedByUser = false;
                if (pid > 0) session.ProcessId = pid;
            }

            MaybeEmitTransitionHud(session, prevState);
            PublishSnapshot();
        }
        catch { }
    }

    private void PublishSnapshot()
    {
        if (!_settingsService.Current.EnableAgentMonitor)
        {
            LatestSnapshot = AgentDashboardSnapshot.Empty;
            Application.Current?.Dispatcher.BeginInvoke(() => SnapshotUpdated?.Invoke(AgentDashboardSnapshot.Empty));
            return;
        }

        List<AgentSessionItem> orderedTree;
        int activeCount = 0;
        int waitingCount = 0;
        int completedCount = 0;

        lock (_stateLock)
        {
            DateTime now = DateTime.UtcNow;
            List<AgentSessionItem> candidates = _sessions.Values
                .Where(s => !s.DismissedByUser &&
                            s.State != AgentRunState.Idle &&
                            !(s.IsEphemeralHelper && s.State == AgentRunState.Completed) &&
                            ((s.State is AgentRunState.Working or AgentRunState.Thinking or AgentRunState.WaitingApproval) ||
                             (now - s.LastUpdatedAtUtc).TotalMinutes <= 25.0))
                .Select(s => new AgentSessionItem
                {
                    SessionId = s.SessionId,
                    ParentSessionId = s.ParentSessionId,
                    Provider = s.Provider,
                    ProjectName = s.ProjectName,
                    DisplayName = s.DisplayName,
                    WorkspaceHint = s.WorkspaceHint,
                    State = s.State,
                    CurrentActionText = s.CurrentActionText,
                    ProgressPercent = s.ProgressPercent,
                    StartedAtUtc = s.StartedAtUtc,
                    ActiveTurnStartedAtUtc = s.ActiveTurnStartedAtUtc,
                    TurnCompletedAtUtc = s.TurnCompletedAtUtc,
                    LastUpdatedAtUtc = s.LastUpdatedAtUtc,
                    IsSubagent = s.IsSubagent,
                    ProcessId = s.ProcessId,
                    TargetWindowHandle = s.TargetWindowHandle
                })
                .ToList();

            orderedTree = BuildProjectTreeItems(candidates);

            foreach (AgentSessionItem item in orderedTree)
            {
                if (item.State == AgentRunState.WaitingApproval) waitingCount++;
                else if (item.IsActive) activeCount++;
                else if (item.State == AgentRunState.Completed) completedCount++;
            }
        }

        AgentSessionItem? primary = orderedTree.FirstOrDefault(i => i.IsWaitingApproval)
            ?? orderedTree.FirstOrDefault(i => i.IsParentRow && i.IsActive)
            ?? orderedTree.FirstOrDefault(i => i.IsActive)
            ?? orderedTree.FirstOrDefault();

        string collapsedText = BuildCollapsedBadgeText(primary, activeCount, waitingCount);

        var snapshot = new AgentDashboardSnapshot
        {
            ActiveCount = activeCount,
            WaitingApprovalCount = waitingCount,
            CompletedRecentCount = completedCount,
            Sessions = orderedTree,
            PrimarySession = primary,
            CollapsedBadgeText = collapsedText
        };

        LatestSnapshot = snapshot;
        Application.Current?.Dispatcher.BeginInvoke(() => SnapshotUpdated?.Invoke(snapshot));
    }

    internal static List<AgentSessionItem> BuildProjectTreeItems(IReadOnlyList<AgentSessionItem> candidates)
    {
        if (candidates.Count == 0)
            return [];

        var parents = new List<AgentSessionItem>();
        var children = new List<AgentSessionItem>();

        foreach (AgentSessionItem item in candidates)
        {
            if (item.IsSubagent)
                children.Add(item);
            else
                parents.Add(item);
        }

        var parentById = new Dictionary<string, AgentSessionItem>(StringComparer.OrdinalIgnoreCase);
        var bestParentByProject = new Dictionary<string, AgentSessionItem>(StringComparer.OrdinalIgnoreCase);
        AgentSessionItem? activeTeamworkParent = null;

        foreach (AgentSessionItem p in parents
                     .OrderByDescending(p => p.IsWaitingApproval)
                     .ThenByDescending(p => p.IsActive)
                     .ThenByDescending(p => p.Provider == AgentProvider.Teamwork)
                     .ThenByDescending(p => p.LastUpdatedAtUtc))
        {
            parentById[p.SessionId] = p;
            string proj = p.ResolvedProjectName;
            if (!bestParentByProject.ContainsKey(proj))
                bestParentByProject[proj] = p;

            if (p.Provider == AgentProvider.Teamwork && activeTeamworkParent == null)
                activeTeamworkParent = p;
        }

        var childrenByParentId = new Dictionary<string, List<AgentSessionItem>>(StringComparer.OrdinalIgnoreCase);
        var orphanChildrenByProject = new Dictionary<string, List<AgentSessionItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (AgentSessionItem child in children)
        {
            AgentSessionItem? targetParent = null;
            if (!string.IsNullOrWhiteSpace(child.ParentSessionId))
            {
                parentById.TryGetValue(child.ParentSessionId, out targetParent);
            }

            if (targetParent == null)
            {
                bestParentByProject.TryGetValue(child.ResolvedProjectName, out targetParent);
            }

            targetParent ??= activeTeamworkParent;

            if (targetParent != null)
            {
                if (!childrenByParentId.TryGetValue(targetParent.SessionId, out List<AgentSessionItem>? list))
                {
                    list = [];
                    childrenByParentId[targetParent.SessionId] = list;
                }
                list.Add(child);
            }
            else
            {
                string proj = child.ResolvedProjectName;
                if (!orphanChildrenByProject.TryGetValue(proj, out List<AgentSessionItem>? list))
                {
                    list = [];
                    orphanChildrenByProject[proj] = list;
                }
                list.Add(child);
            }
        }

        // Synthesize a clean project parent row if subagents exist without their top-level parent in the window
        foreach (var (proj, orphanKids) in orphanChildrenByProject)
        {
            AgentSessionItem lead = orphanKids
                .OrderByDescending(c => c.IsWaitingApproval)
                .ThenByDescending(c => c.IsActive)
                .ThenByDescending(c => c.LastUpdatedAtUtc)
                .First();

            string synthId = !string.IsNullOrWhiteSpace(lead.ParentSessionId)
                ? lead.ParentSessionId
                : $"group:{proj}";

            int avgProgress = (int)Math.Round(orphanKids.Average(c => c.ClampedProgressPercent));
            DateTime minStart = orphanKids.Min(c => c.ActiveTurnStartedAtUtc != default ? c.ActiveTurnStartedAtUtc : c.StartedAtUtc);
            DateTime maxUpdate = orphanKids.Max(c => c.LastUpdatedAtUtc);
            bool anyActive = orphanKids.Any(c => c.IsActive);

            var synthParent = new AgentSessionItem
            {
                SessionId = synthId,
                Provider = lead.Provider,
                ProjectName = proj,
                DisplayName = proj,
                WorkspaceHint = lead.WorkspaceHint,
                State = lead.State,
                CurrentActionText = lead.CurrentActionText,
                ProgressPercent = anyActive ? Math.Min(96, avgProgress) : 100,
                StartedAtUtc = minStart,
                ActiveTurnStartedAtUtc = minStart,
                TurnCompletedAtUtc = anyActive ? null : maxUpdate,
                LastUpdatedAtUtc = maxUpdate,
                IsSubagent = false,
                ProcessId = lead.ProcessId,
                TargetWindowHandle = lead.TargetWindowHandle
            };

            parents.Add(synthParent);
            childrenByParentId[synthId] = orphanKids;
        }

        var orderedGroups = parents
            .Select(p =>
            {
                List<AgentSessionItem> kids = childrenByParentId.TryGetValue(p.SessionId, out List<AgentSessionItem>? k)
                    ? k.OrderByDescending(c => c.IsWaitingApproval)
                       .ThenByDescending(c => c.IsActive)
                       .ThenByDescending(c => c.LastUpdatedAtUtc)
                       .Take(12)
                       .ToList()
                    : [];

                bool groupWaiting = p.IsWaitingApproval || kids.Any(c => c.IsWaitingApproval);
                bool groupActive = p.IsActive || kids.Any(c => c.IsActive);
                DateTime groupLatest = kids.Count > 0
                    ? (kids.Max(c => c.LastUpdatedAtUtc) > p.LastUpdatedAtUtc ? kids.Max(c => c.LastUpdatedAtUtc) : p.LastUpdatedAtUtc)
                    : p.LastUpdatedAtUtc;

                return (Parent: p, Kids: kids, GroupWaiting: groupWaiting, GroupActive: groupActive, GroupLatest: groupLatest);
            })
            .OrderByDescending(g => g.GroupWaiting)
            .ThenByDescending(g => g.GroupActive)
            .ThenByDescending(g => g.GroupLatest)
            .Take(8)
            .ToList();

        var result = new List<AgentSessionItem>(orderedGroups.Count * 3);
        foreach (var g in orderedGroups)
        {
            string groupId = g.Parent.SessionId;
            result.Add(g.Parent.WithTreePresentation(isExpanded: true, childCount: g.Kids.Count, groupId: groupId));
            foreach (AgentSessionItem kid in g.Kids)
            {
                result.Add(kid.WithTreePresentation(isExpanded: true, childCount: 0, groupId: groupId));
            }
        }

        return result;
    }

    private static string BuildCollapsedBadgeText(AgentSessionItem? primary, int activeCount, int waitingCount)
    {
        if (waitingCount > 0)
        {
            return "Agent · Chờ duyệt!";
        }

        if (activeCount <= 0 || primary == null)
            return string.Empty;

        return $"{primary.ResolvedProjectName} · {primary.ProgressText}";
    }

    private static string? ExtractAntigravityConvId(string fullPath)
    {
        try
        {
            // ...\.gemini\antigravity-cli\brain\<conv-id>\.system_generated\logs\transcript.jsonl
            DirectoryInfo? logsDir = Directory.GetParent(fullPath);
            DirectoryInfo? sysGenDir = logsDir?.Parent;
            DirectoryInfo? convDir = sysGenDir?.Parent;
            return convDir?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadJsonString(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(prop, out JsonElement v) &&
            v.ValueKind == JsonValueKind.String)
        {
            return v.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        foreach (FileSystemWatcher w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch { }
        }
        _watchers.Clear();
        _cts?.Dispose();
        _cts = null;
    }
}
