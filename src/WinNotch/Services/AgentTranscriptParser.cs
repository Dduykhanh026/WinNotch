using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinNotch.Models;

namespace WinNotch.Services;

internal sealed class TrackedAgentState
{
    public required string SessionId { get; init; }
    public required AgentProvider Provider { get; init; }
    public string TranscriptPath { get; set; } = string.Empty;
    public long LastByteOffset { get; set; }
    public string ParentSessionId { get; set; } = string.Empty;
    public bool IsSubagent { get; set; }
    public bool IsEphemeralHelper { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string WorkspaceHint { get; set; } = string.Empty;
    public AgentRunState State { get; set; } = AgentRunState.Idle;
    public string CurrentActionText { get; set; } = "Đang khởi tạo...";
    public int ProgressPercent { get; set; }
    public int ReadStepCount { get; set; }
    public int WriteStepCount { get; set; }
    public int CommandStepCount { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ActiveTurnStartedAtUtc { get; set; }
    public DateTime? TurnCompletedAtUtc { get; set; }
    public bool PendingFeedbackInTurn { get; set; }
    public bool DismissedByUser { get; set; }
    public int ProcessId { get; set; }
    public IntPtr TargetWindowHandle { get; set; }
}

internal static class AgentTranscriptParser
{
    private static readonly Regex UserRequestTagRegex = new(
        @"<USER_REQUEST>\s*(?<body1>[\s\S]*?)\s*</USER_REQUEST>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SubagentIdJsonRegex = new(
        @"""conversationId""\s*:\s*""(?<id>[0-9a-fA-F\-]{36})""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TeamworkMilestoneLineRegex = new(
        @"^-\s*\[(?<chk>[xX ])\]\s*\*{0,2}(?<ms>(?:Milestone|M)\s*\d+)\b[:\*\s—–-]*(?<title>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TeamworkProjectHeaderRegex = new(
        @"^#\s*Project\s+(?:Progress|Plan)\s*[—–:-]+\s*(?<proj>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WorkspaceLineRegex = new(
        @"(?:-\s*|Working directory:\s*|Command Working Directory:\s*|Workspace:\s*)(?<path>[A-Za-z]:[\\/][^\r\n<>""|?*]+|/[A-Za-z0-9._\-/]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SubagentPersonaRegex = new(
        @"(?:You are\s+\*{0,2}(?<persona>[a-zA-Z0-9_\- ]{3,42})\*{0,2}|#\s*Subagent Briefing:\s*(?<brief>[^\r\n]{3,48})|Role:\s*""?(?<role>[^""\r\n]{3,42})""?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ExcludedFolderSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gemini", ".agents", ".codex", ".claude", "AppData", "Local", "Roaming", "Temp", "tmp",
        "scratch", "Windows", "System32", "Program Files", "Program Files (x86)", "Users",
        "Documents", "Desktop", "Downloads", "Projects", "repos", "source", "AGY",
        "src", "tests", "test", "scripts", "artifacts", "bin", "obj", "Views", "ViewModels",
        "Models", "Services", "Controls", "Engine", "Interop", "Themes", "Assets"
    };

    /// <summary>
    /// Incrementally parses only newly appended bytes of an Antigravity CLI transcript.jsonl file.
    /// Reads the initial step_index 0 header on large transcripts as well so workspace & subagent persona metadata are never missed.
    /// </summary>
    public static bool ParseAntigravityIncremental(
        TrackedAgentState session,
        Dictionary<string, (string ParentId, string Role)> discoveredSubagentRoles,
        out AgentRunState previousState)
    {
        previousState = session.State;
        if (string.IsNullOrWhiteSpace(session.TranscriptPath) || !File.Exists(session.TranscriptPath))
            return false;

        bool changed = false;
        try
        {
            using var fs = new FileStream(
                session.TranscriptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            long len = fs.Length;
            if (len < session.LastByteOffset)
            {
                session.LastByteOffset = 0;
            }

            var pendingRoles = new Queue<string>();

            // When attaching to a large (> 96 KB) existing transcript for the first time,
            // inspect the first 2 lines first so <user_information> Active Workspaces and Subagent Briefing are always captured!
            if (session.LastByteOffset == 0 && len > 98304)
            {
                using (var headReader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
                {
                    for (int h = 0; h < 2; h++)
                    {
                        string? headLine = headReader.ReadLine();
                        if (string.IsNullOrWhiteSpace(headLine)) break;
                        InspectMetadataOnlyLine(session, headLine);
                    }
                }

                fs.Seek(len - 98304, SeekOrigin.Begin);
                SkipPartialLine(fs);
            }
            else
            {
                fs.Seek(session.LastByteOffset, SeekOrigin.Begin);
            }

            if (fs.Position >= len)
                return false;

            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (ApplyAntigravityJsonLine(session, line, pendingRoles, discoveredSubagentRoles))
                {
                    changed = true;
                }
            }

            session.LastByteOffset = fs.Length;
        }
        catch
        {
            // Transient file write contention; next tick will resume from LastByteOffset
        }

        return changed;
    }

    private static void InspectMetadataOnlyLine(TrackedAgentState session, string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            string content = GetStr(root, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                TryUpdateProjectFromText(session, content);
                TryUpdateSubagentPersonaFromText(session, content);
            }
        }
        catch { }
    }

    private static bool ApplyAntigravityJsonLine(
        TrackedAgentState session,
        string line,
        Queue<string> pendingRoles,
        Dictionary<string, (string ParentId, string Role)> discoveredSubagentRoles)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            string stepType = GetStr(root, "type");
            string status = GetStr(root, "status");
            DateTime stepTimestamp = ParseIsoUtcOrNow(GetStr(root, "created_at"), session.LastUpdatedAtUtc);
            string rawStepContent = GetStr(root, "content");

            if (!string.IsNullOrWhiteSpace(rawStepContent))
            {
                TryUpdateProjectFromText(session, rawStepContent);
                if (stepType is "SYSTEM_MESSAGE" or "USER_INPUT")
                {
                    TryUpdateSubagentPersonaFromText(session, rawStepContent);
                }

                // Match ALL spawned subagent conversation IDs returned in tool output steps (supports 3-5 parallel subagents!)
                if (pendingRoles.Count > 0)
                {
                    MatchCollection matches = SubagentIdJsonRegex.Matches(rawStepContent);
                    foreach (Match m in matches)
                    {
                        if (!m.Success) continue;
                        string childId = m.Groups["id"].Value;
                        string role = pendingRoles.Count > 0 ? pendingRoles.Dequeue() : "Nhánh thực thi";
                        discoveredSubagentRoles[childId] = (session.SessionId, HumanizeSubagentRole(role));
                    }
                }
            }

            if (session.ActiveTurnStartedAtUtc == default && stepTimestamp != default)
            {
                session.ActiveTurnStartedAtUtc = stepTimestamp;
            }

            if (status.Equals("ERROR", StringComparison.OrdinalIgnoreCase) && stepType == "PLANNER_RESPONSE")
            {
                session.State = AgentRunState.Error;
                session.CurrentActionText = "Lượt thực thi gặp lỗi";
                session.LastUpdatedAtUtc = stepTimestamp;
                session.TurnCompletedAtUtc = stepTimestamp;
                session.DismissedByUser = false;
                return true;
            }

            if (stepType == "SYSTEM_MESSAGE")
            {
                // Subagents often receive their initial assignment via step_index 0 SYSTEM_MESSAGE
                if (session.State == AgentRunState.Idle)
                {
                    session.State = AgentRunState.Thinking;
                    session.ActiveTurnStartedAtUtc = stepTimestamp;
                    session.TurnCompletedAtUtc = null;
                    session.ProgressPercent = 12;
                    session.LastUpdatedAtUtc = stepTimestamp;
                    session.CurrentActionText = "Khởi tạo & phân tích nhiệm vụ...";
                    return true;
                }
                return false;
            }

            if (stepType == "USER_INPUT")
            {
                string extractedPrompt = ExtractCleanUserPrompt(rawStepContent);
                bool isSystemWakeupOnly = string.IsNullOrWhiteSpace(extractedPrompt) &&
                                          rawStepContent.Contains("<SYSTEM_MESSAGE>", StringComparison.Ordinal);

                if (!string.IsNullOrWhiteSpace(extractedPrompt) &&
                    !extractedPrompt.StartsWith("[Approved]", StringComparison.OrdinalIgnoreCase) &&
                    !extractedPrompt.Equals("Proceed", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.IsSubagent &&
                        (string.IsNullOrWhiteSpace(session.DisplayName) ||
                         session.DisplayName.StartsWith("Phiên ", StringComparison.Ordinal)))
                    {
                        session.DisplayName = TruncateOneLine(extractedPrompt, 38);
                    }
                }

                session.PendingFeedbackInTurn = false;
                session.DismissedByUser = false;
                session.State = AgentRunState.Thinking;
                session.TurnCompletedAtUtc = null;

                if (!isSystemWakeupOnly)
                {
                    // Genuine user request starts a fresh turn timer & progress curve
                    session.ActiveTurnStartedAtUtc = stepTimestamp;
                    session.ReadStepCount = 0;
                    session.WriteStepCount = 0;
                    session.CommandStepCount = 0;
                    session.ProgressPercent = 12;
                    session.CurrentActionText = string.IsNullOrWhiteSpace(extractedPrompt)
                        ? "Đang phân tích yêu cầu..."
                        : $"Phân tích: {TruncateOneLine(extractedPrompt, 44)}";
                }
                else
                {
                    if (session.ActiveTurnStartedAtUtc == default)
                        session.ActiveTurnStartedAtUtc = stepTimestamp;
                    session.ProgressPercent = Math.Max(session.ProgressPercent == 100 ? 18 : session.ProgressPercent, 18);
                    session.CurrentActionText = "Đồng bộ phản hồi từ Subagent...";
                }

                session.LastUpdatedAtUtc = stepTimestamp;
                return true;
            }

            if (stepType == "PLANNER_RESPONSE")
            {
                session.LastUpdatedAtUtc = stepTimestamp;
                session.DismissedByUser = false;
                if (session.ActiveTurnStartedAtUtc == default)
                    session.ActiveTurnStartedAtUtc = stepTimestamp;

                if (root.TryGetProperty("tool_calls", out JsonElement toolsEl) &&
                    toolsEl.ValueKind == JsonValueKind.Array &&
                    toolsEl.GetArrayLength() > 0)
                {
                    session.TurnCompletedAtUtc = null;
                    if (session.ProgressPercent >= 100)
                        session.ProgressPercent = 20;

                    bool asksUser = false;
                    bool requestsArtifactFeedback = false;
                    string primaryAction = string.Empty;
                    int toolCount = toolsEl.GetArrayLength();

                    foreach (JsonElement tool in toolsEl.EnumerateArray())
                    {
                        string toolName = GetStr(tool, "name");
                        JsonElement args = tool.TryGetProperty("args", out JsonElement a) ? a : default;

                        TryExtractProjectFromToolArgs(session, args);
                        AdvanceProgressByTool(session, toolName, args);

                        if (toolName == "ask_question")
                        {
                            asksUser = true;
                            primaryAction = "Đang hỏi ý kiến xác nhận của bạn";
                        }
                        else if ((toolName is "write_file" or "edit_file" or "write_to_file") &&
                                 HasFeedbackRequest(args))
                        {
                            requestsArtifactFeedback = true;
                            string planFile = ExtractAnyFileNameFromArgs(args);
                            primaryAction = string.IsNullOrWhiteSpace(planFile)
                                ? "Chờ bạn duyệt bản kế hoạch"
                                : $"Chờ bạn duyệt kế hoạch · {planFile}";
                        }
                        else if (toolName == "invoke_subagent")
                        {
                            string role = GetStr(args, "Role");
                            if (string.IsNullOrWhiteSpace(role)) role = GetStr(args, "TypeName");
                            string cleanRole = HumanizeSubagentRole(string.IsNullOrWhiteSpace(role) ? "Nhánh thực thi" : role);
                            pendingRoles.Enqueue(cleanRole);
                            if (string.IsNullOrEmpty(primaryAction))
                                primaryAction = $"Điều phối nhánh · {cleanRole}";
                        }
                        else if (string.IsNullOrEmpty(primaryAction))
                        {
                            primaryAction = SummarizeToolCall(toolName, args);
                        }
                    }

                    if (requestsArtifactFeedback)
                    {
                        session.PendingFeedbackInTurn = true;
                    }

                    if (asksUser || requestsArtifactFeedback)
                    {
                        session.State = AgentRunState.WaitingApproval;
                        session.ProgressPercent = Math.Clamp(Math.Max(session.ProgressPercent, 35), 12, 95);
                        session.CurrentActionText = primaryAction;
                    }
                    else
                    {
                        session.State = AgentRunState.Working;
                        session.CurrentActionText = toolCount > 1
                            ? $"{primaryAction} (+{toolCount - 1} tác vụ)"
                            : primaryAction;
                    }
                    return true;
                }

                // Planner finished turn with zero tool calls
                if (session.PendingFeedbackInTurn)
                {
                    session.State = AgentRunState.WaitingApproval;
                    session.ProgressPercent = Math.Clamp(Math.Max(session.ProgressPercent, 35), 12, 95);
                    if (!session.CurrentActionText.StartsWith("Chờ bạn duyệt", StringComparison.Ordinal))
                    {
                        session.CurrentActionText = "Chờ bạn phê duyệt để tiếp tục";
                    }
                }
                else
                {
                    if (rawStepContent.Contains("teamwork_prompt_", StringComparison.OrdinalIgnoreCase))
                    {
                        session.State = AgentRunState.WaitingApproval;
                        session.ProgressPercent = Math.Clamp(Math.Max(session.ProgressPercent, 35), 12, 95);
                        session.CurrentActionText = "Chờ bạn duyệt mô tả nhiệm vụ Teamwork";
                    }
                    else
                    {
                        session.State = AgentRunState.Completed;
                        session.ProgressPercent = 100;
                        session.TurnCompletedAtUtc = stepTimestamp;
                        session.CurrentActionText = string.IsNullOrWhiteSpace(rawStepContent)
                            ? "Hoàn tất lượt thực thi"
                            : TruncateOneLine(StripMarkdownFormatting(rawStepContent), 52);
                    }
                }
                return true;
            }
        }
        catch
        {
            // Ignore malformed or partial JSON line
        }

        return false;
    }

    /// <summary>
    /// Incrementally parses OpenAI Codex CLI session JSONL files (~/.codex/sessions/**/*.jsonl).
    /// </summary>
    public static bool ParseCodexIncremental(TrackedAgentState session, out AgentRunState previousState)
    {
        previousState = session.State;
        if (string.IsNullOrWhiteSpace(session.TranscriptPath) || !File.Exists(session.TranscriptPath))
            return false;

        bool changed = false;
        try
        {
            using var fs = new FileStream(session.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len < session.LastByteOffset) session.LastByteOffset = 0;
            if (session.LastByteOffset == 0 && len > 65536)
            {
                fs.Seek(len - 65536, SeekOrigin.Begin);
                SkipPartialLine(fs);
            }
            else
            {
                fs.Seek(session.LastByteOffset, SeekOrigin.Begin);
            }

            if (fs.Position >= len) return false;

            using var reader = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    string topType = GetStr(root, "type");
                    DateTime ts = ParseIsoUtcOrNow(GetStr(root, "timestamp"), session.LastUpdatedAtUtc);

                    if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
                        continue;

                    string pType = GetStr(payload, "type");
                    if (topType == "session_meta")
                    {
                        string cwd = GetStr(payload, "cwd");
                        if (!string.IsNullOrWhiteSpace(cwd))
                        {
                            session.WorkspaceHint = cwd;
                            if (TryExtractProjectNameFromPath(cwd, out string proj))
                            {
                                session.ProjectName = proj;
                                session.DisplayName = proj;
                            }
                        }
                    }
                    else if (topType == "event_msg" && pType == "user_message")
                    {
                        session.State = AgentRunState.Thinking;
                        session.ActiveTurnStartedAtUtc = ts;
                        session.TurnCompletedAtUtc = null;
                        session.LastUpdatedAtUtc = ts;
                        session.ReadStepCount = 0;
                        session.WriteStepCount = 0;
                        session.CommandStepCount = 0;
                        session.ProgressPercent = 12;
                        session.DismissedByUser = false;
                        string msg = GetStr(payload, "message");
                        session.CurrentActionText = string.IsNullOrWhiteSpace(msg)
                            ? "Đang suy luận yêu cầu..."
                            : $"Phân tích: {TruncateOneLine(msg, 44)}";
                        changed = true;
                    }
                    else if (topType == "event_msg" && pType == "agent_reasoning")
                    {
                        session.State = AgentRunState.Thinking;
                        session.LastUpdatedAtUtc = ts;
                        session.ProgressPercent = Math.Max(session.ProgressPercent, 16);
                        session.CurrentActionText = "Đang suy luận giải pháp...";
                        changed = true;
                    }
                    else if (topType == "response_item" && pType == "function_call")
                    {
                        string fnName = GetStr(payload, "name");
                        session.State = AgentRunState.Working;
                        session.LastUpdatedAtUtc = ts;
                        AdvanceProgressByTool(session, fnName, default);
                        session.CurrentActionText = SummarizeToolCall(fnName, default);
                        changed = true;
                    }
                    else if (topType == "response_item" && pType == "message" && GetStr(payload, "role") == "assistant")
                    {
                        session.State = AgentRunState.Completed;
                        session.ProgressPercent = 100;
                        session.TurnCompletedAtUtc = ts;
                        session.LastUpdatedAtUtc = ts;
                        session.CurrentActionText = "Hoàn tất lượt phản hồi";
                        changed = true;
                    }
                }
                catch { }
            }

            session.LastByteOffset = fs.Length;
        }
        catch { }

        return changed;
    }

    /// <summary>
    /// Incrementally parses Claude Code CLI project JSONL files (~/.claude/projects/**/*.jsonl).
    /// </summary>
    public static bool ParseClaudeIncremental(TrackedAgentState session, out AgentRunState previousState)
    {
        previousState = session.State;
        if (string.IsNullOrWhiteSpace(session.TranscriptPath) || !File.Exists(session.TranscriptPath))
            return false;

        bool changed = false;
        try
        {
            using var fs = new FileStream(session.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len < session.LastByteOffset) session.LastByteOffset = 0;
            if (session.LastByteOffset == 0 && len > 65536)
            {
                fs.Seek(len - 65536, SeekOrigin.Begin);
                SkipPartialLine(fs);
            }
            else
            {
                fs.Seek(session.LastByteOffset, SeekOrigin.Begin);
            }

            if (fs.Position >= len) return false;

            using var reader = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    string topType = GetStr(root, "type");
                    DateTime ts = ParseIsoUtcOrNow(GetStr(root, "timestamp"), session.LastUpdatedAtUtc);
                    string cwd = GetStr(root, "cwd");
                    if (!string.IsNullOrWhiteSpace(cwd))
                    {
                        session.WorkspaceHint = cwd;
                        if (string.IsNullOrWhiteSpace(session.ProjectName) &&
                            TryExtractProjectNameFromPath(cwd, out string proj))
                        {
                            session.ProjectName = proj;
                            session.DisplayName = proj;
                        }
                    }

                    if (topType == "user")
                    {
                        session.State = AgentRunState.Thinking;
                        session.ActiveTurnStartedAtUtc = ts;
                        session.TurnCompletedAtUtc = null;
                        session.LastUpdatedAtUtc = ts;
                        session.ReadStepCount = 0;
                        session.WriteStepCount = 0;
                        session.CommandStepCount = 0;
                        session.ProgressPercent = 12;
                        session.DismissedByUser = false;
                        session.CurrentActionText = "Đang phân tích yêu cầu...";
                        changed = true;
                    }
                    else if (topType == "assistant" && root.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.Object)
                    {
                        session.LastUpdatedAtUtc = ts;
                        bool hasToolUse = false;
                        bool asksQuestion = false;
                        string toolLabel = string.Empty;

                        if (msg.TryGetProperty("content", out JsonElement contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement block in contentArr.EnumerateArray())
                            {
                                if (GetStr(block, "type") == "tool_use")
                                {
                                    hasToolUse = true;
                                    string tName = GetStr(block, "name");
                                    JsonElement inputArgs = block.TryGetProperty("input", out JsonElement inp) ? inp : default;
                                    TryExtractProjectFromToolArgs(session, inputArgs);
                                    AdvanceProgressByTool(session, tName, inputArgs);

                                    if (tName.Contains("AskUser", StringComparison.OrdinalIgnoreCase))
                                    {
                                        asksQuestion = true;
                                        toolLabel = "Đang chờ bạn trả lời xác nhận";
                                    }
                                    else if (string.IsNullOrEmpty(toolLabel))
                                    {
                                        toolLabel = SummarizeToolCall(tName, inputArgs);
                                    }
                                }
                            }
                        }

                        if (asksQuestion)
                        {
                            session.State = AgentRunState.WaitingApproval;
                            session.ProgressPercent = Math.Clamp(Math.Max(session.ProgressPercent, 35), 12, 95);
                            session.CurrentActionText = toolLabel;
                        }
                        else if (hasToolUse)
                        {
                            session.State = AgentRunState.Working;
                            session.CurrentActionText = toolLabel;
                        }
                        else
                        {
                            session.State = AgentRunState.Completed;
                            session.ProgressPercent = 100;
                            session.TurnCompletedAtUtc = ts;
                            session.CurrentActionText = "Hoàn tất lượt phản hồi";
                        }
                        changed = true;
                    }
                }
                catch { }
            }

            session.LastByteOffset = fs.Length;
        }
        catch { }

        return changed;
    }

    /// <summary>
    /// Reads Teamwork orchestrator progress.md to surface real project name, live milestone, exact %, and runtime timestamps.
    /// </summary>
    public static bool TryReadTeamworkProgressSummary(
        string teamworkRootDir,
        out string projectName,
        out string activeMilestoneText,
        out int completedMilestones,
        out int totalMilestones,
        out int progressPercent,
        out int activeRecentWorkers,
        out DateTime earliestStartUtc,
        out DateTime latestWriteUtc)
    {
        projectName = string.Empty;
        activeMilestoneText = string.Empty;
        completedMilestones = 0;
        totalMilestones = 0;
        progressPercent = 0;
        activeRecentWorkers = 0;
        earliestStartUtc = DateTime.MaxValue;
        latestWriteUtc = DateTime.MinValue;

        if (!Directory.Exists(teamworkRootDir))
            return false;

        try
        {
            DateTime nowUtc = DateTime.UtcNow;
            string? bestOrchestratorProgress = null;
            DateTime bestOrchestratorWrite = DateTime.MinValue;
            string? bestFallbackProgress = null;
            DateTime bestFallbackWrite = DateTime.MinValue;

            foreach (string subDir in Directory.EnumerateDirectories(teamworkRootDir))
            {
                var di = new DirectoryInfo(subDir);
                bool isOrchestrator = di.Name.Contains("orchestrator", StringComparison.OrdinalIgnoreCase);
                DateTime dirTouch = di.LastWriteTimeUtc;

                foreach (FileInfo fi in di.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if (fi.LastWriteTimeUtc > dirTouch)
                        dirTouch = fi.LastWriteTimeUtc;
                    if (fi.CreationTimeUtc < earliestStartUtc)
                        earliestStartUtc = fi.CreationTimeUtc;

                    if (fi.Name.Equals("progress.md", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isOrchestrator && fi.LastWriteTimeUtc > bestOrchestratorWrite)
                        {
                            bestOrchestratorWrite = fi.LastWriteTimeUtc;
                            bestOrchestratorProgress = fi.FullName;
                        }
                        else if (fi.LastWriteTimeUtc > bestFallbackWrite)
                        {
                            bestFallbackWrite = fi.LastWriteTimeUtc;
                            bestFallbackProgress = fi.FullName;
                        }
                    }
                }

                if (dirTouch > latestWriteUtc)
                    latestWriteUtc = dirTouch;

                if ((nowUtc - dirTouch).TotalMinutes <= 6.0 &&
                    !di.Name.Equals("interviewer", StringComparison.OrdinalIgnoreCase))
                {
                    activeRecentWorkers++;
                }
            }

            string? bestProgressFile = bestOrchestratorProgress ?? bestFallbackProgress;
            if (bestProgressFile != null && File.Exists(bestProgressFile))
            {
                using var fs = new FileStream(bestProgressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                while (sr.ReadLine() is { } line)
                {
                    string trimmed = line.Trim();

                    if (string.IsNullOrEmpty(projectName))
                    {
                        Match hm = TeamworkProjectHeaderRegex.Match(trimmed);
                        if (hm.Success)
                        {
                            projectName = ExtractProjectTokenFromHeader(hm.Groups["proj"].Value);
                        }
                    }

                    Match m = TeamworkMilestoneLineRegex.Match(trimmed);
                    if (!m.Success) continue;

                    totalMilestones++;
                    char chk = m.Groups["chk"].Value[0];
                    if (chk == 'x' || chk == 'X')
                    {
                        completedMilestones++;
                    }
                    else if (string.IsNullOrEmpty(activeMilestoneText))
                    {
                        string msRaw = m.Groups["ms"].Value.Trim();
                        string msShort = Regex.Replace(msRaw, @"^Milestone\s*", "Mốc ", RegexOptions.IgnoreCase);
                        if (msShort.StartsWith("M", StringComparison.OrdinalIgnoreCase) && !msShort.StartsWith("Mốc", StringComparison.OrdinalIgnoreCase))
                            msShort = "Mốc " + msShort[1..].Trim();

                        string rawTitle = StripMarkdownFormatting(m.Groups["title"].Value);
                        int bracketIdx = rawTitle.IndexOf("— [", StringComparison.Ordinal);
                        if (bracketIdx > 4)
                            rawTitle = rawTitle[..bracketIdx].Trim();
                        int parenIdx = rawTitle.IndexOf('(');
                        if (parenIdx > 4)
                            rawTitle = rawTitle[..parenIdx].Trim();

                        activeMilestoneText = string.IsNullOrWhiteSpace(rawTitle)
                            ? msShort
                            : TruncateOneLine($"{msShort}: {rawTitle}", 48);
                    }
                }
            }

            if (totalMilestones > 0)
            {
                if (completedMilestones >= totalMilestones && string.IsNullOrEmpty(activeMilestoneText))
                {
                    progressPercent = 100;
                }
                else
                {
                    double credit = completedMilestones + (!string.IsNullOrEmpty(activeMilestoneText) ? 0.45 : 0.0);
                    progressPercent = Math.Clamp((int)Math.Round((credit / totalMilestones) * 100.0), 8, 96);
                }
            }
            else
            {
                progressPercent = activeRecentWorkers > 0 ? 25 : 100;
            }

            if (earliestStartUtc == DateTime.MaxValue)
                earliestStartUtc = latestWriteUtc;

            return latestWriteUtc != DateTime.MinValue;
        }
        catch
        {
            return false;
        }
    }

    internal static string SummarizeToolCall(string toolName, JsonElement args)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return "Đang thực thi tác vụ...";

        return toolName switch
        {
            "edit_file" or "replace_file_content" or "multi_replace_file_content" or "Edit" or "MultiEdit" or "apply_patch"
                => FormatFileTool("Sửa", args),
            "write_file" or "write_to_file" or "Write"
                => FormatFileTool("Ghi", args),
            "read_file" or "view_file" or "Read"
                => FormatFileTool("Đọc", args),
            "view_file_outline" or "view_code_item"
                => FormatFileTool("Đọc cấu trúc", args),
            "list_dir" or "LS"
                => FormatFolderTool("Quét thư mục", args),
            "find_by_name" or "Glob" or "glob"
                => FormatSearchTool("Tìm tệp", args, "Pattern", "SearchDirectory"),
            "grep_search" or "Grep" or "ripgrep"
                => FormatSearchTool("Tìm mã nguồn", args, "Query", "SearchPath"),
            "run_command" or "bash" or "shell" or "Bash"
                => FormatCommandTool(args),
            "command_status" or "manage_task"
                => "Kiểm tra tiến trình tác vụ nền",
            "schedule"
                => "Thiết lập lịch chạy tự động",
            "search_web" or "WebSearch"
                => FormatSearchTool("Tra cứu web", args, "query", "domain"),
            "read_url_content" or "WebFetch"
                => "Đọc tài liệu trực tuyến",
            "browser_subagent"
                => "Thao tác kiểm thử trình duyệt",
            "send_message"
                => "Đồng bộ tin nhắn điều phối Agent",
            "manage_subagents"
                => "Quản lý trạng thái đội Subagents",
            "invoke_subagent" or "Task"
                => FormatSubagentTool(args),
            "define_subagent"
                => "Định nghĩa chuyên viên Subagent",
            "TodoWrite"
                => "Cập nhật danh sách công việc",
            _ => HumanizeUnknownToolName(toolName, args)
        };
    }

    private static void AdvanceProgressByTool(TrackedAgentState session, string toolName, JsonElement args)
    {
        string lower = toolName.ToLowerInvariant();

        if (lower is "read_file" or "view_file" or "view_file_outline" or "view_code_item" or
            "list_dir" or "find_by_name" or "grep_search" or "read" or "ls" or "glob" or "grep" or
            "search_web" or "read_url_content")
        {
            session.ReadStepCount++;
            int next = Math.Min(38, 16 + (session.ReadStepCount * 4));
            session.ProgressPercent = Math.Max(session.ProgressPercent, next);
            return;
        }

        if (lower is "edit_file" or "write_file" or "write_to_file" or "replace_file_content" or
            "multi_replace_file_content" or "apply_patch" or "edit" or "multiedit" or "write" or
            "invoke_subagent" or "send_message" or "task" or "todowrite")
        {
            session.WriteStepCount++;
            int next = Math.Min(74, 46 + (session.WriteStepCount * 6));
            session.ProgressPercent = Math.Max(session.ProgressPercent, next);
            return;
        }

        if (lower is "run_command" or "bash" or "shell" or "command_status" or "manage_task")
        {
            session.CommandStepCount++;
            string cmd = (GetStr(args, "CommandLine") + " " + GetStr(args, "toolSummary")).ToLowerInvariant();
            bool isVerificationOrBuild = cmd.Contains("build") || cmd.Contains("test") ||
                                         cmd.Contains("publish") || cmd.Contains("release") ||
                                         cmd.Contains("push") || session.WriteStepCount > 0;
            int baseFloor = isVerificationOrBuild ? 78 : 42;
            int cap = isVerificationOrBuild ? 92 : 68;
            int next = Math.Min(cap, baseFloor + (session.CommandStepCount * 4));
            session.ProgressPercent = Math.Max(session.ProgressPercent, next);
            return;
        }

        session.ProgressPercent = Math.Max(session.ProgressPercent, 24);
    }

    private static string FormatFileTool(string verb, JsonElement args)
    {
        string file = ExtractAnyFileNameFromArgs(args);
        return string.IsNullOrWhiteSpace(file) ? $"{verb} tệp dự án" : $"{verb} · {file}";
    }

    private static string FormatFolderTool(string verb, JsonElement args)
    {
        string raw = FirstNonEmptyArg(args, "DirectoryPath", "Path", "path", "SearchDirectory");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string trimmed = raw.Trim('"', '\'', ' ', '\\', '/');
            string leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(leaf))
                return $"{verb} · {leaf}";
        }
        return verb;
    }

    private static string FormatSearchTool(string verb, JsonElement args, string primaryKey, string fallbackPathKey)
    {
        string query = FirstNonEmptyArg(args, primaryKey, "Pattern", "Query", "pattern", "query");
        if (!string.IsNullOrWhiteSpace(query))
            return $"{verb} · {TruncateOneLine(query, 28)}";

        string folder = FirstNonEmptyArg(args, fallbackPathKey, "SearchPath", "SearchDirectory", "Path");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            string leaf = Path.GetFileName(folder.Trim('"', '\'', ' ', '\\', '/'));
            if (!string.IsNullOrWhiteSpace(leaf))
                return $"{verb} · {leaf}";
        }
        return verb;
    }

    private static string FormatSubagentTool(JsonElement args)
    {
        string role = FirstNonEmptyArg(args, "Role", "description", "TypeName");
        return string.IsNullOrWhiteSpace(role)
            ? "Điều phối nhánh Subagent"
            : $"Điều phối · {HumanizeSubagentRole(TruncateOneLine(role, 32))}";
    }

    private static string FormatCommandTool(JsonElement args)
    {
        string summary = FirstNonEmptyArg(args, "toolSummary", "description");
        if (!string.IsNullOrWhiteSpace(summary))
            return $"Chạy lệnh · {TruncateOneLine(summary, 36)}";

        string cmd = FirstNonEmptyArg(args, "CommandLine", "command");
        return string.IsNullOrWhiteSpace(cmd) ? "Chạy lệnh hệ thống" : $"Chạy lệnh · {TruncateOneLine(cmd, 34)}";
    }

    private static string HumanizeUnknownToolName(string toolName, JsonElement args)
    {
        string file = ExtractAnyFileNameFromArgs(args);
        string cleanName = toolName.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(file)
            ? $"Thực thi · {cleanName}"
            : $"Thực thi {cleanName} · {file}";
    }

    internal static string HumanizeSubagentRole(string rawRole)
    {
        if (string.IsNullOrWhiteSpace(rawRole))
            return "Nhánh thực thi";

        string trimmed = rawRole.Trim().Trim('*', '"', '\'');
        string lower = trimmed.ToLowerInvariant();

        if (lower.Contains("interviewer"))
            return "Phỏng vấn yêu cầu";
        if (lower.Contains("orchestrator"))
            return "Điều phối dự án";
        if (lower.Contains("auditor"))
            return "Kiểm toán độc lập";
        if (lower.Contains("challenger"))
            return "Kiểm thử đối kháng";
        if (lower.Contains("reviewer"))
            return "Kiểm duyệt mã nguồn";
        if (lower.Contains("spec_miner") || lower.Contains("explorer"))
            return "Khảo sát kiến trúc";
        if (lower.Contains("worker"))
        {
            Match m = Regex.Match(trimmed, @"m(?<m>\d+).*?(?:worker[_-]?(?<w>\d+))?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string ms = m.Groups["m"].Value;
                string wk = m.Groups["w"].Success ? $" #{m.Groups["w"].Value}" : string.Empty;
                return $"Thực thi Mốc {ms}{wk}";
            }
            return "Thực thi mã nguồn";
        }

        return TruncateOneLine(trimmed.Replace('_', ' '), 34);
    }

    private static void TryUpdateSubagentPersonaFromText(TrackedAgentState session, string text)
    {
        if (text.Contains("teamwork_interviewer", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Task Interviewer", StringComparison.OrdinalIgnoreCase))
        {
            session.IsSubagent = true;
            session.IsEphemeralHelper = true;
            if (string.IsNullOrWhiteSpace(session.DisplayName) || session.DisplayName.StartsWith("Phiên ", StringComparison.Ordinal))
                session.DisplayName = "Phỏng vấn yêu cầu";
            return;
        }

        if (text.Contains("# Subagent Briefing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("You are **", StringComparison.OrdinalIgnoreCase))
        {
            session.IsSubagent = true;
            Match pm = SubagentPersonaRegex.Match(text);
            if (pm.Success)
            {
                string raw = pm.Groups["persona"].Success ? pm.Groups["persona"].Value
                    : pm.Groups["brief"].Success ? pm.Groups["brief"].Value
                    : pm.Groups["role"].Value;

                if (!string.IsNullOrWhiteSpace(raw) &&
                    (string.IsNullOrWhiteSpace(session.DisplayName) || session.DisplayName.StartsWith("Phiên ", StringComparison.Ordinal)))
                {
                    session.DisplayName = HumanizeSubagentRole(raw);
                }
            }
        }
    }

    private static void TryUpdateProjectFromText(TrackedAgentState session, string text)
    {
        if (!string.IsNullOrWhiteSpace(session.ProjectName))
            return;

        MatchCollection matches = WorkspaceLineRegex.Matches(text);
        string bestProject = string.Empty;
        string bestPath = string.Empty;
        int bestDepth = -1;

        foreach (Match m in matches)
        {
            if (!m.Success) continue;
            string candidatePath = m.Groups["path"].Value.Trim().TrimEnd('\\', '/', '"', '\'', ')', ']');
            if (TryExtractProjectNameFromPath(candidatePath, out string proj, out int depth) && depth > bestDepth)
            {
                bestDepth = depth;
                bestProject = proj;
                bestPath = candidatePath;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestProject))
        {
            session.ProjectName = bestProject;
            session.WorkspaceHint = bestPath;
        }
    }

    private static void TryExtractProjectFromToolArgs(TrackedAgentState session, JsonElement args)
    {
        if (!string.IsNullOrWhiteSpace(session.ProjectName) || args.ValueKind != JsonValueKind.Object)
            return;

        string candidate = FirstNonEmptyArg(
            args,
            "Path", "TargetFile", "DirectoryPath", "SearchPath", "SearchDirectory", "file_path", "path");

        if (!string.IsNullOrWhiteSpace(candidate) &&
            TryExtractProjectNameFromPath(candidate, out string proj))
        {
            session.ProjectName = proj;
            if (string.IsNullOrWhiteSpace(session.WorkspaceHint))
                session.WorkspaceHint = proj;
        }
    }

    internal static bool TryExtractProjectNameFromPath(string rawPath, out string projectName) =>
        TryExtractProjectNameFromPath(rawPath, out projectName, out _);

    private static bool TryExtractProjectNameFromPath(string rawPath, out string projectName, out int depthScore)
    {
        projectName = string.Empty;
        depthScore = -1;
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        string cleaned = rawPath.Trim('"', '\'', ' ', '\t', '\r', '\n');
        if (cleaned.Contains(".gemini", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains(".agents", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("/tmp/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = cleaned.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        // Ignore bare user home folder like C:\Users\ASUS (3 segments: "C:", "Users", "ASUS")
        if (parts.Length == 3 &&
            parts[0].EndsWith(':') &&
            parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // If path contains a standard repo boundary ("src", "tests", "scripts"), the segment immediately before it is the repo root!
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Equals("src", StringComparison.OrdinalIgnoreCase) ||
                parts[i].Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                parts[i].Equals("scripts", StringComparison.OrdinalIgnoreCase))
            {
                string repoCandidate = parts[i - 1];
                if (!ExcludedFolderSegments.Contains(repoCandidate) && !repoCandidate.EndsWith(':'))
                {
                    projectName = repoCandidate;
                    depthScore = i + 10;
                    return true;
                }
            }
        }

        // Otherwise walk backward from leaf (skipping filenames with extensions and generic subfolders)
        for (int i = parts.Length - 1; i >= 1; i--)
        {
            string seg = parts[i];
            if (seg.EndsWith(':')) continue;
            if (seg.Contains('.') && i == parts.Length - 1) continue; // skip file.cs / file.sln
            if (ExcludedFolderSegments.Contains(seg)) continue;

            // Skip Windows username immediately after "Users"
            if (i >= 1 && parts[i - 1].Equals("Users", StringComparison.OrdinalIgnoreCase))
                continue;

            projectName = seg;
            depthScore = i;
            return true;
        }

        return false;
    }

    private static string ExtractProjectTokenFromHeader(string rawHeader)
    {
        string clean = StripMarkdownFormatting(rawHeader).Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return string.Empty;

        // e.g. "WinNotch UI Evolution Wave" -> "WinNotch" if first token is PascalCase/repo-like, or concise title
        string[] words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[0].Length >= 3 && !words[0].Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return words[0].Trim(':', '-', '—');
        }
        return TruncateOneLine(clean, 24);
    }

    private static bool HasFeedbackRequest(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (!args.TryGetProperty("ArtifactMetadata", out JsonElement meta)) return false;

        if (meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("RequestFeedback", out JsonElement rf) &&
            rf.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (meta.ValueKind == JsonValueKind.String)
        {
            string rawMeta = meta.GetString() ?? string.Empty;
            if (rawMeta.Contains("\"RequestFeedback\":true", StringComparison.OrdinalIgnoreCase) ||
                rawMeta.Contains("\"RequestFeedback\": true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractAnyFileNameFromArgs(JsonElement args)
    {
        string raw = FirstNonEmptyArg(args, "Path", "TargetFile", "file_path", "path", "AbsolutePath");
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            return Path.GetFileName(raw.Trim('"', '\'', ' ', '\\', '/')).Trim('"', '\'', ' ');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmptyArg(JsonElement args, params string[] keys)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (string key in keys)
        {
            string val = GetStr(args, key);
            if (!string.IsNullOrWhiteSpace(val))
                return val;
        }
        return string.Empty;
    }

    private static string ExtractCleanUserPrompt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        Match m = UserRequestTagRegex.Match(raw);
        if (m.Success)
        {
            return m.Groups["body1"].Value.Trim();
        }

        if (raw.Contains("<SYSTEM_MESSAGE>", StringComparison.Ordinal) ||
            raw.Contains("<CONTEXT_SUMMARY>", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return raw.Trim();
    }

    private static string StripMarkdownFormatting(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return s.Replace("**", string.Empty)
                .Replace("`", string.Empty)
                .Replace("###", string.Empty)
                .Replace("##", string.Empty)
                .Replace(">", string.Empty)
                .Trim();
    }

    private static string TruncateOneLine(string s, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        string single = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (single.Contains("  ", StringComparison.Ordinal))
            single = single.Replace("  ", " ", StringComparison.Ordinal);

        return single.Length <= maxChars ? single : single[..maxChars].TrimEnd() + "…";
    }

    private static void SkipPartialLine(FileStream fs)
    {
        int b;
        while ((b = fs.ReadByte()) != -1)
        {
            if (b == '\n') break;
        }
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(prop, out JsonElement v) &&
            v.ValueKind == JsonValueKind.String)
        {
            string raw = v.GetString() ?? string.Empty;
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(raw) ?? raw.Trim('"');
                }
                catch
                {
                    return raw.Trim('"');
                }
            }
            return raw;
        }
        return string.Empty;
    }

    private static DateTime ParseIsoUtcOrNow(string iso, DateTime fallbackUtc)
    {
        if (!string.IsNullOrWhiteSpace(iso) &&
            DateTimeOffset.TryParse(iso, out DateTimeOffset dto))
        {
            return dto.UtcDateTime;
        }
        return fallbackUtc != default ? fallbackUtc : DateTime.UtcNow;
    }
}
