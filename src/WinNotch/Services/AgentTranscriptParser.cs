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
    public string DisplayName { get; set; } = string.Empty;
    public string WorkspaceHint { get; set; } = string.Empty;
    public AgentRunState State { get; set; } = AgentRunState.Idle;
    public string CurrentActionText { get; set; } = "Đang khởi tạo...";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ActiveTurnStartedAtUtc { get; set; } = DateTime.UtcNow;
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
        @"^-\s*\[(?<chk>[xX ])\]\s*\*{0,2}(?<ms>M\d+)\b[:\*\s—–-]*(?<title>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Incrementally parses only newly appended bytes of an Antigravity CLI transcript.jsonl file.
    /// Zero full-file rescans after initial tail seed.
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
                // File was truncated/recreated
                session.LastByteOffset = 0;
            }

            // On first attach of a huge historical transcript (> 96 KB), seek near tail so startup is < 1ms
            if (session.LastByteOffset == 0 && len > 98304)
            {
                fs.Seek(len - 98304, SeekOrigin.Begin);
                SkipPartialLine(fs);
            }
            else
            {
                fs.Seek(session.LastByteOffset, SeekOrigin.Begin);
            }

            if (fs.Position >= len)
                return false;

            string? pendingSubagentRole = null;
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (ApplyAntigravityJsonLine(session, line, ref pendingSubagentRole, discoveredSubagentRoles))
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

    private static bool ApplyAntigravityJsonLine(
        TrackedAgentState session,
        string line,
        ref string? pendingSubagentRole,
        Dictionary<string, (string ParentId, string Role)> discoveredSubagentRoles)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            string stepType = GetStr(root, "type");
            string status = GetStr(root, "status");
            DateTime stepTimestamp = ParseIsoUtcOrNow(GetStr(root, "created_at"), session.LastUpdatedAtUtc);

            // Detect spawned subagent conversation IDs returned in tool output steps
            if (pendingSubagentRole != null && root.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                string rawContent = contentEl.GetString() ?? string.Empty;
                Match m = SubagentIdJsonRegex.Match(rawContent);
                if (m.Success)
                {
                    string childId = m.Groups["id"].Value;
                    discoveredSubagentRoles[childId] = (session.SessionId, pendingSubagentRole);
                    pendingSubagentRole = null;
                }
            }

            if (status.Equals("ERROR", StringComparison.OrdinalIgnoreCase) && stepType == "PLANNER_RESPONSE")
            {
                session.State = AgentRunState.Error;
                session.CurrentActionText = "Lượt thực thi gặp lỗi";
                session.LastUpdatedAtUtc = stepTimestamp;
                session.DismissedByUser = false;
                return true;
            }

            if (stepType == "USER_INPUT")
            {
                string content = GetStr(root, "content");
                string extractedPrompt = ExtractCleanUserPrompt(content);

                // Ignore passive background <SYSTEM_MESSAGE> wakeups when naming the session
                if (!string.IsNullOrWhiteSpace(extractedPrompt) &&
                    !extractedPrompt.StartsWith("[Approved]", StringComparison.OrdinalIgnoreCase) &&
                    !extractedPrompt.Equals("Proceed", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(session.DisplayName) ||
                        session.DisplayName.StartsWith("Phiên ", StringComparison.Ordinal))
                    {
                        session.DisplayName = TruncateOneLine(extractedPrompt, 38);
                    }
                }

                session.PendingFeedbackInTurn = false;
                session.DismissedByUser = false;
                session.State = AgentRunState.Thinking;
                session.CurrentActionText = string.IsNullOrWhiteSpace(extractedPrompt)
                    ? "Đang phân tích ngữ cảnh..."
                    : $"Đang xử lý: {TruncateOneLine(extractedPrompt, 32)}";
                session.ActiveTurnStartedAtUtc = stepTimestamp;
                session.LastUpdatedAtUtc = stepTimestamp;
                return true;
            }

            if (stepType == "PLANNER_RESPONSE")
            {
                session.LastUpdatedAtUtc = stepTimestamp;
                session.DismissedByUser = false;

                if (root.TryGetProperty("tool_calls", out JsonElement toolsEl) &&
                    toolsEl.ValueKind == JsonValueKind.Array &&
                    toolsEl.GetArrayLength() > 0)
                {
                    bool asksUser = false;
                    bool requestsArtifactFeedback = false;
                    string primaryAction = string.Empty;
                    int toolCount = toolsEl.GetArrayLength();

                    foreach (JsonElement tool in toolsEl.EnumerateArray())
                    {
                        string toolName = GetStr(tool, "name");
                        JsonElement args = tool.TryGetProperty("args", out JsonElement a) ? a : default;

                        if (toolName == "ask_question")
                        {
                            asksUser = true;
                            primaryAction = "Đang hỏi ý kiến bạn (ask_question)";
                        }
                        else if ((toolName == "write_file" || toolName == "edit_file") &&
                                 HasFeedbackRequest(args))
                        {
                            requestsArtifactFeedback = true;
                            string planFile = ExtractFileNameFromArgs(args, "Path");
                            primaryAction = string.IsNullOrWhiteSpace(planFile)
                                ? "Chờ bạn duyệt bản kế hoạch"
                                : $"Chờ bạn duyệt: {planFile}";
                        }
                        else if (toolName == "invoke_subagent")
                        {
                            string role = GetStr(args, "Role");
                            if (string.IsNullOrWhiteSpace(role)) role = GetStr(args, "TypeName");
                            pendingSubagentRole = string.IsNullOrWhiteSpace(role) ? "Subagent" : role;
                            primaryAction = $"Gọi Subagent · {pendingSubagentRole}";
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
                        session.CurrentActionText = primaryAction;
                    }
                    else
                    {
                        session.State = AgentRunState.Working;
                        session.CurrentActionText = toolCount > 1
                            ? $"{primaryAction} (+{toolCount - 1})"
                            : primaryAction;
                    }
                    return true;
                }

                // Planner finished turn with zero tool calls
                if (session.PendingFeedbackInTurn)
                {
                    session.State = AgentRunState.WaitingApproval;
                    if (!session.CurrentActionText.StartsWith("Chờ bạn duyệt", StringComparison.Ordinal))
                    {
                        session.CurrentActionText = "Chờ bạn phê duyệt để tiếp tục";
                    }
                }
                else
                {
                    string replyText = GetStr(root, "content");
                    if (replyText.Contains("teamwork_prompt_", StringComparison.OrdinalIgnoreCase))
                    {
                        session.State = AgentRunState.WaitingApproval;
                        session.CurrentActionText = "Chờ bạn duyệt mô tả nhiệm vụ Teamwork";
                    }
                    else
                    {
                        session.State = AgentRunState.Completed;
                        session.CurrentActionText = string.IsNullOrWhiteSpace(replyText)
                            ? "Đã hoàn tất lượt thực thi"
                            : TruncateOneLine(StripMarkdownFormatting(replyText), 42);
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
                            session.WorkspaceHint = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                            if (!string.IsNullOrWhiteSpace(session.WorkspaceHint))
                                session.DisplayName = $"Codex · {session.WorkspaceHint}";
                        }
                    }
                    else if (topType == "event_msg" && pType == "user_message")
                    {
                        session.State = AgentRunState.Thinking;
                        session.ActiveTurnStartedAtUtc = ts;
                        session.LastUpdatedAtUtc = ts;
                        session.DismissedByUser = false;
                        string msg = GetStr(payload, "message");
                        session.CurrentActionText = string.IsNullOrWhiteSpace(msg)
                            ? "Đang suy luận..."
                            : $"Yêu cầu: {TruncateOneLine(msg, 34)}";
                        changed = true;
                    }
                    else if (topType == "event_msg" && pType == "agent_reasoning")
                    {
                        session.State = AgentRunState.Thinking;
                        session.LastUpdatedAtUtc = ts;
                        session.CurrentActionText = "Đang suy luận giải pháp...";
                        changed = true;
                    }
                    else if (topType == "response_item" && pType == "function_call")
                    {
                        string fnName = GetStr(payload, "name");
                        session.State = AgentRunState.Working;
                        session.LastUpdatedAtUtc = ts;
                        session.CurrentActionText = string.IsNullOrWhiteSpace(fnName)
                            ? "Đang gọi công cụ..."
                            : $"Chạy lệnh · {fnName}";
                        changed = true;
                    }
                    else if (topType == "response_item" && pType == "message" && GetStr(payload, "role") == "assistant")
                    {
                        session.State = AgentRunState.Completed;
                        session.LastUpdatedAtUtc = ts;
                        session.CurrentActionText = "Đã phản hồi hoàn tất";
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
                    if (!string.IsNullOrWhiteSpace(cwd) && string.IsNullOrWhiteSpace(session.WorkspaceHint))
                    {
                        session.WorkspaceHint = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                        if (!string.IsNullOrWhiteSpace(session.WorkspaceHint))
                            session.DisplayName = $"Claude · {session.WorkspaceHint}";
                    }

                    if (topType == "user")
                    {
                        session.State = AgentRunState.Thinking;
                        session.LastUpdatedAtUtc = ts;
                        session.DismissedByUser = false;
                        session.CurrentActionText = "Đang phân tích...";
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
                                    if (tName.Contains("AskUser", StringComparison.OrdinalIgnoreCase))
                                    {
                                        asksQuestion = true;
                                        toolLabel = "Đang chờ bạn trả lời";
                                    }
                                    else if (string.IsNullOrEmpty(toolLabel))
                                    {
                                        toolLabel = $"Tool · {tName}";
                                    }
                                }
                            }
                        }

                        if (asksQuestion)
                        {
                            session.State = AgentRunState.WaitingApproval;
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
                            session.CurrentActionText = "Đã hoàn tất lượt trả lời";
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
    /// Reads Teamwork orchestrator progress.md to surface live milestone & active worker swarm summary.
    /// </summary>
    public static bool TryReadTeamworkProgressSummary(
        string teamworkRootDir,
        out string activeMilestoneText,
        out int completedMilestones,
        out int totalMilestones,
        out int activeRecentWorkers,
        out DateTime latestWriteUtc)
    {
        activeMilestoneText = string.Empty;
        completedMilestones = 0;
        totalMilestones = 0;
        activeRecentWorkers = 0;
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
                        string ms = m.Groups["ms"].Value;
                        string rawTitle = StripMarkdownFormatting(m.Groups["title"].Value);
                        int parenIdx = rawTitle.IndexOf('(');
                        if (parenIdx > 4)
                            rawTitle = rawTitle[..parenIdx].Trim();

                        activeMilestoneText = string.IsNullOrWhiteSpace(rawTitle)
                            ? ms
                            : TruncateOneLine($"{ms}: {rawTitle}", 42);
                    }
                }
            }

            return latestWriteUtc != DateTime.MinValue;
        }
        catch
        {
            return false;
        }
    }

    private static string SummarizeToolCall(string toolName, JsonElement args)
    {
        return toolName switch
        {
            "edit_file" => FormatFileTool("Sửa", args),
            "write_file" => FormatFileTool("Tạo", args),
            "read_file" => FormatFileTool("Đọc", args),
            "run_command" => FormatCommandTool(args),
            "search_web" => "Tìm kiếm tài liệu web",
            "send_message" => "Điều phối tin nhắn Subagent",
            "manage_subagents" => "Quản lý đội Subagents",
            _ => $"Tool · {toolName}"
        };
    }

    private static string FormatFileTool(string verb, JsonElement args)
    {
        string file = ExtractFileNameFromArgs(args, "Path");
        return string.IsNullOrWhiteSpace(file) ? $"{verb} tệp" : $"{verb} · {file}";
    }

    private static string FormatCommandTool(JsonElement args)
    {
        string summary = GetStr(args, "toolSummary");
        if (!string.IsNullOrWhiteSpace(summary))
            return $"Lệnh · {TruncateOneLine(summary, 30)}";

        string cmd = GetStr(args, "CommandLine");
        return string.IsNullOrWhiteSpace(cmd) ? "Chạy lệnh hệ thống" : $"Lệnh · {TruncateOneLine(cmd, 28)}";
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

    private static string ExtractFileNameFromArgs(JsonElement args, string propName)
    {
        string raw = GetStr(args, propName).Trim('"', '\'', ' ', '\\', '/');
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            return Path.GetFileName(raw).Trim('"', '\'', ' ');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractCleanUserPrompt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        Match m = UserRequestTagRegex.Match(raw);
        if (m.Success)
        {
            return m.Groups["body1"].Value.Trim();
        }

        if (raw.Contains("<SYSTEM_MESSAGE>", StringComparison.Ordinal))
            return string.Empty;

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
            // Antigravity compact transcript.jsonl double-encodes tool_calls[].args string values as JSON literals
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
