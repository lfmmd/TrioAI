using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
        // ---- Shared static paths ----

        private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrioAI");
        private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
        private static readonly string HistoryDir = Path.Combine(DataDir, "chat_history");
        private static readonly string BackupDir = Path.Combine(DataDir, "backups");
        private static readonly string SkillsDir = Path.Combine(DataDir, "skills");
        private static readonly string PromptPath = Path.Combine(DataDir, "AI_INSTRUCTIONS.md");

        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        // ---- Core chat state ----

        private readonly HttpClient _http;
        private static string _lastCompileError;

        private const int DefaultMaxTokens = 8192;
        private const int EscalatedMaxTokens = 64000;
        private int _currentMaxTokens = DefaultMaxTokens;

        // GLM / 智谱 anthropic 兼容端点不执行 thinking.budget_tokens 截断（实测单块 thinking
        // 达 6.8 万字，见 chat_history 日志）。DispatchSseEvent 按此标记做客户端硬截断：
        // 超 budget_tokens×2 字符后丢弃后续 thinking_delta。只控制本端显示/存储/回传，
        // 不减少模型实际 output token 消耗——要省 token 须关 enableThinking 或换支持 budget 的端点。
        private const string ThinkTruncatedTag = "\n\n[…思考已截断：超过 budget_tokens 上限（GLM 端点未执行 budget_tokens 截断）…]";

        // Chat 串行保护:正常路径由 ChatPanel._isProcessing 拦截,此为防御层(防未来其他入口并发调用 Chat)
        private volatile bool _chatRunning;

        // 当前 ChatCore 的 CancellationToken，供 research 工具（在主循环 Task.Run 内经 ExecuteTool
        // 调用，拿不到 ct 参数）透传给 RunSubagent。每次 ChatCore 入口覆盖；research 同步跑在 ChatCore 内。
        private CancellationToken _currentCancellationToken;

        // ---- Token usage tracking ----
        private int _totalInputTokens, _totalOutputTokens, _totalCacheReadTokens, _totalCacheCreateTokens;
        public int TotalInputTokens => _totalInputTokens;
        public int TotalOutputTokens => _totalOutputTokens;
        public int TotalCacheReadTokens => _totalCacheReadTokens;
        public int TotalCacheCreateTokens => _totalCacheCreateTokens;

        public static void LogException(string context, Exception ex)
        {
            try
            {
                var dir = Path.GetDirectoryName(PromptPath);
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "perf_error.txt"),
                    string.Format("[{0}] {1}: {2}{3}{4}{3}",
                        DateTime.Now.ToString("HH:mm:ss.fff"), context,
                        ex.GetType().Name, ex.Message, Environment.NewLine));
            }
            catch { }
        }

        // Callbacks for ChatPanel
        public Action OnAiTextStart { get; set; }
        public Action<string> OnAiTextDelta { get; set; }
        public Action OnAiTextEnd { get; set; }
        public Action OnAiThinkingStart { get; set; }
        public Action<string> OnAiThinkingDelta { get; set; }
        public Action OnAiThinkingEnd { get; set; }
        public Action<string> OnSystemMessage { get; set; }
        public Action<string> OnToolStatus { get; set; }
        public Func<string, string, bool> OnConfirmWrite { get; set; }
        // Plan Mode 审批回调：参数是 AI 的 plan 文本，返回 true=批准（退出 plan mode），false=拒绝
        public Func<string, bool> OnConfirmPlan { get; set; }

        public AiService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            LoadConfig();
            Directory.CreateDirectory(HistoryDir);
            Directory.CreateDirectory(BackupDir);
            Directory.CreateDirectory(DataDir);
            try { Directory.CreateDirectory(MemoryDir); } catch { }
            // 首次创建提示词文件 — 用户手动修改后不会被覆盖。
            // 想恢复默认：删除文件，或在 UI 点击「初始化 Skills」。
            try { if (!File.Exists(PromptPath)) File.WriteAllText(PromptPath, DefaultPrompt); } catch { }
            SubscribeCompileEvents();
        }

        // ---- Compile State Monitoring ----

        private void SubscribeCompileEvents()
        {
            try
            {
                DispatcherHelper.Invoke(() =>
                {
                    var ctrl = Trio.SharedLibrary.MPSingletons.Controller;
                    if (ctrl != null)
                    {
                        ctrl.CompileStateChanged += OnCompileStateChanged;
                    }
                });
            }
            catch (Exception ex) { LogException("SubscribeCompileEvents", ex); }
        }

        private void OnCompileStateChanged(object sender, Trio.SharedLibrary.COMPILEStateEventArgs e)
        {
            try
            {
                if (e.ErrorCode != 0)
                {
                    var errMsg = string.Format(Lang.L("[编译错误] {0}: 第 {1} 行, 错误 #{2} - {3}",
                                                          "[Compile Error] {0}: line {1}, error #{2} - {3}"),
                        e.ProgramName ?? "?", e.ErrorLine, e.ErrorCode, e.ErrorDescription ?? "");
                    _lastCompileError = errMsg;
                    OnSystemMessage?.Invoke(errMsg);
                }
                else
                {
                    _lastCompileError = null;
                }
            }
            catch { }
        }

        // ---- Backup ----

        private void BackupSource(string programName)
        {
            try
            {
                var result = Handlers.ReadSource(programName);
                var srcObj = result as Dictionary<string, object>;
                if (srcObj == null) return;
                object sourceCode;
                if (!srcObj.TryGetValue("sourceCode", out sourceCode) || sourceCode == null) return;

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupName = $"{programName}_{timestamp}.bak";
                var backupPath = Path.Combine(BackupDir, backupName);
                File.WriteAllText(backupPath, sourceCode.ToString());
                OnToolStatus?.Invoke(Lang.L($"已备份: {backupName}", $"Backup saved: {backupName}"));
            }
            catch { }
        }

        // ---- Agentic Loop ----

        private const int MaxTurns = 50;
        // 失控循环检测阈值：连续 N 轮 assistant【text 或 thinking】指纹相同 → 判定循环，提前终止。
        // 旧版只看 text，对 thinking 逐字重复循环完全失明（每轮 text 不同或纯 tool_use → 永不触发 →
        // 跑满 MaxTurns=50；GLM 不守 budget 时每轮 thinking 可达数万字）。现 text/thinking 各自独立计数，
        // 任一达阈值即终止。只抓「逐字重复」型；「持续接续打转、每轮开头不同但绕圈」型需相似度检测，留待后续。
        private const int LoopDetectThreshold = 3;
        // thinking 指纹长度（前 N 字）。中文 thinking 前 120 字相同即可判定逐字重复。
        private const int ThinkingFingerprintLen = 120;
        // chars (≈100K tokens). 历史超此阈值时强制 TrimHistory；仍超则提示开新会话。
        private const int TokenBudgetLimit = 400_000;

        // 循环检测状态：text 与 thinking 各自的上一轮指纹 + 连续相同计数。
        private struct LoopState
        {
            public string LastTextFp;
            public int TextRepeat;
            public string LastThinkingFp;
            public int ThinkingRepeat;
        }

        /// <summary>
        /// 评估本轮 assistant 内容是否构成失控循环。同时算 text 指纹（前 60 字）与 thinking 指纹
        /// （前 ThinkingFingerprintLen 字），各自独立累计连续相同次数；任一达到 threshold 即判定触发。
        /// 替代旧版「只看 text」的内联逻辑——旧版对 thinking 逐字重复循环完全失明（text 每轮不同或纯
        /// tool_use → 永不触发 → 跑满 MaxTurns）。纯逻辑、不依赖 API，供 ChatCore 与单元测试共用。
        /// </summary>
        private static LoopState EvaluateLoopTurn(
            List<Dictionary<string, object>> content,
            LoopState prev,
            int threshold,
            out bool triggered,
            out bool thinkingLoop)
        {
            var sbText = new StringBuilder();
            var sbThink = new StringBuilder();
            foreach (var b in content)
            {
                var bt = GetStringValue(b, "type");
                if (bt == "text")
                {
                    var t = GetStringValue(b, "text");
                    if (!string.IsNullOrEmpty(t)) sbText.Append(t);
                }
                else if (bt == "thinking")
                {
                    var t = GetStringValue(b, "thinking");
                    if (!string.IsNullOrEmpty(t)) sbThink.Append(t);
                }
            }
            var textFp = sbText.ToString().Trim();
            if (textFp.Length > 60) textFp = textFp.Substring(0, 60);
            var thinkFp = sbThink.ToString().Trim();
            if (thinkFp.Length > ThinkingFingerprintLen) thinkFp = thinkFp.Substring(0, ThinkingFingerprintLen);

            var s = prev;
            if (textFp.Length > 0)
            {
                if (textFp == prev.LastTextFp) s.TextRepeat++;
                else { s.TextRepeat = 0; s.LastTextFp = textFp; }
            }
            if (thinkFp.Length > 0)
            {
                if (thinkFp == prev.LastThinkingFp) s.ThinkingRepeat++;
                else { s.ThinkingRepeat = 0; s.LastThinkingFp = thinkFp; }
            }
            thinkingLoop = s.ThinkingRepeat >= threshold;
            triggered = thinkingLoop || s.TextRepeat >= threshold;
            return s;
        }

        public void Chat(string userMessage, CancellationToken ct = default)
        {
            if (!HasApiKey)
            {
                OnSystemMessage?.Invoke(Lang.L("未配置 API Key。点击[设置]配置。",
                                                    "API key not configured. Click 'Settings' to set your API key."));
                return;
            }

            if (_chatRunning)
            {
                OnSystemMessage?.Invoke(Lang.L("上一条消息仍在处理,请稍候。",
                                                    "Previous message is still being processed. Please wait."));
                return;
            }

            _chatRunning = true;
            try { ChatCore(userMessage, ct); }
            finally { _chatRunning = false; }
        }

        private void ChatCore(string userMessage, CancellationToken ct = default)
        {
            // 每条用户消息从默认 max_tokens 开始,仅在当轮被截断时临时升级(避免升级后整个会话不回落)
            _currentMaxTokens = DefaultMaxTokens;

            // 供 research 工具透传取消令牌（ExecuteTool 无 ct 参数）
            _currentCancellationToken = ct;

            if (string.IsNullOrEmpty(_currentSessionId))
                _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            lock (_historyLock)
            {
                _conversationHistory.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", userMessage }
                });
            }

            var loop = default(LoopState);  // text/thinking 双指纹循环检测状态
            for (int turn = 0; turn < MaxTurns; turn++)
            {
                // 上下文预算检查：超阈值先尝试 TrimHistory；仍超则提示开新会话
                if (EstimateHistoryTokens() > TokenBudgetLimit)
                {
                    TrimHistory();
                    if (EstimateHistoryTokens() > TokenBudgetLimit)
                    {
                        OnSystemMessage?.Invoke(Lang.L(
                            "⚠ 已达上下文上限，建议开启新会话继续（当前会话历史已保留）",
                            "⚠ Context limit reached. Consider starting a new session (history preserved)."));
                        return;
                    }
                }

                StreamResult result;
                try { result = CallApiWithRetry(ct); }
                catch (OperationCanceledException)
                {
                    // User cancelled before any content_block_stop arrived — no
                    // assistant content was preserved. Append a sentinel so the
                    // next user prompt doesn't end up adjacent to the previous
                    // one (Anthropic API requires strict user/assistant
                    // alternation). Mirrors cc-haha's NO_RESPONSE_REQUESTED
                    // sentinel strategy.
                    lock (_historyLock)
                    {
                        _conversationHistory.Add(new Dictionary<string, object>
                        {
                            { "role", "assistant" },
                            { "content", new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        { "type", "text" },
                                        { "text", "[已中断]" }
                                    }
                                }
                            }
                        });
                    }
                    return;
                }

                if (result == null)
                {
                    OnSystemMessage?.Invoke(Lang.L("调用 AI API 失败。请检查 API Key、URL 和网络连接。",
                                                        "Failed to call AI API. Check your API key, URL, and network."));
                    return;
                }

                // Escalate: 输出被 max_tokens 截断 → 升级到 64000 重试本次（不前进 turn）
                // 适用场景：AI 在 write_source 写大程序被切；agentic loop 输出超长
                if (result.StopReason == "max_tokens" && _currentMaxTokens < EscalatedMaxTokens)
                {
                    OnSystemMessage?.Invoke(Lang.L($"⚠ 输出被 max_tokens={_currentMaxTokens} 截断，升级到 {EscalatedMaxTokens} 重试...",
                                                        $"⚠ Output truncated by max_tokens={_currentMaxTokens}, escalating to {EscalatedMaxTokens} for retry..."));
                    _currentMaxTokens = EscalatedMaxTokens;
                    turn--;  // 不算这一轮
                    continue;
                }

                // 累加 token 用量
                _totalInputTokens += result.InputTokens;
                _totalOutputTokens += result.OutputTokens;
                _totalCacheReadTokens += result.CacheReadTokens;
                _totalCacheCreateTokens += result.CacheCreateTokens;

                // 失控循环检测：text 指纹（前 60 字）与 thinking 指纹（前 ThinkingFingerprintLen 字）各自独立计数，
                // 任一连续 LoopDetectThreshold 轮相同 → 判定循环，提前终止。详见 EvaluateLoopTurn。
                loop = EvaluateLoopTurn(result.Content, loop, LoopDetectThreshold, out bool loopTriggered, out bool thinkingLoop);
                if (loopTriggered)
                {
                    var kindZh = thinkingLoop ? "推理" : "输出";
                    var kindEn = thinkingLoop ? "reasoning" : "output";
                    OnSystemMessage?.Invoke(Lang.L(
                        $"⚠ 检测到 AI 连续 {LoopDetectThreshold + 1} 轮重复相同{kindZh}，疑似循环{kindZh}，已提前终止。建议检查任务或开启新会话。",
                        $"⚠ AI repeated the same {kindEn} for {LoopDetectThreshold + 1} consecutive turns — possible reasoning loop, aborted early. Check the task or start a new session."));
                    return;
                }

                lock (_historyLock)
                {
                    _conversationHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "assistant" },
                        { "content", result.Content }
                    });
                }

                var toolUseBlocks = result.Content
                    .Where(b => GetStringValue(b, "type") == "tool_use")
                    .ToList();

                // Cancellation may leave tool_use blocks without tool_result → next API
                // call will reject the request.  Patch: add stub tool_results + sentinel.
                if (ct.IsCancellationRequested && toolUseBlocks.Count > 0)
                {
                    var stubResults = new List<Dictionary<string, object>>();
                    foreach (var tb in toolUseBlocks)
                    {
                        stubResults.Add(new Dictionary<string, object>
                        {
                            { "type", "tool_result" },
                            { "tool_use_id", GetStringValue(tb, "id") ?? "" },
                            { "content", "[Operation cancelled by user]" }
                        });
                    }
                    lock (_historyLock)
                    {
                        _conversationHistory.Add(new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", stubResults }
                        });
                        _conversationHistory.Add(new Dictionary<string, object>
                        {
                            { "role", "assistant" },
                            { "content", new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        { "type", "text" },
                                        { "text", "[已中断]" }
                                    }
                                }
                            }
                        });
                    }
                    return;
                }

                if (toolUseBlocks.Count == 0 || result.StopReason != "tool_use")
                {
                    if (result.StopReason == "max_tokens")
                        OnSystemMessage?.Invoke(Lang.L($"⚠ 即使 max_tokens={_currentMaxTokens} 仍被截断。建议改用 patch_source 分批写入（每个 operation 仅一行变更，几乎不受 token 限制）。",
                                                            $"⚠ Still truncated even with max_tokens={_currentMaxTokens}. Consider using patch_source for batched writes (each operation is one line change, almost unaffected by token limits)."));
                    return;
                }

                // 并行执行所有 tool_use 块：
                // - 纯 IO 工具（lookup_command / read_skill / discover_skills）真并行（已绕过 DispatcherHelper）
                // - 其他工具仍走 DispatcherHelper，被 UI 线程序列化（MP API 是 STA）
                // - 写类工具的 OnConfirmWrite 弹窗由 dispatcher 自动排队，不会冲突
                // 无论实际并行度如何，按原始 tool_use 顺序追加 tool_result（Anthropic 要求 id 对齐）
                var toolResultMap = new Dictionary<string, string>(StringComparer.Ordinal);
                var toolTasks = new List<Task>();
                foreach (var toolBlock in toolUseBlocks)
                {
                    var toolId = GetStringValue(toolBlock, "id");
                    var toolName = GetStringValue(toolBlock, "name");
                    var toolInput = GetDictValue(toolBlock, "input") ?? new Dictionary<string, object>();
                    var capturedId = toolId; // 闭包捕获

                    var task = Task.Run(() =>
                    {
                        string execResult;
                        try
                        {
                            execResult = ExecuteTool(toolName, toolInput);
                        }
                        catch (Exception ex)
                        {
                            // ExecuteTool 内部本应吞所有异常返回 error string，这里兜底
                            execResult = "Error: " + ex.Message;
                        }
                        lock (toolResultMap)
                        {
                            toolResultMap[capturedId] = execResult;
                        }
                    }, ct);
                    toolTasks.Add(task);
                }

                try { Task.WaitAll(toolTasks.ToArray()); }
                catch (OperationCanceledException)
                {
                    // 取消发生在工具执行阶段(非 API 流阶段):为已发起的 tool_use 补 stub tool_result +
                    // sentinel,避免下一条用户消息与上一个 user/tool_result 块相邻(Anthropic 要求严格交替)。
                    // 与 API 流取消(:324 附近)的清理保持一致。
                    var stubResults = new List<Dictionary<string, object>>();
                    foreach (var tb in toolUseBlocks)
                    {
                        stubResults.Add(new Dictionary<string, object>
                        {
                            { "type", "tool_result" },
                            { "tool_use_id", GetStringValue(tb, "id") ?? "" },
                            { "content", "[Operation cancelled by user]" }
                        });
                    }
                    lock (_historyLock)
                    {
                        _conversationHistory.Add(new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", stubResults }
                        });
                        _conversationHistory.Add(new Dictionary<string, object>
                        {
                            { "role", "assistant" },
                            { "content", new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        { "type", "text" },
                                        { "text", "[已中断]" }
                                    }
                                }
                            }
                        });
                    }
                    return;
                }
                catch (AggregateException) { /* 单 Task 内异常已在 Task 内 catch，这里忽略包裹 */ }

                // 按原始 tool_use 顺序构造 tool_result
                var toolResults = new List<Dictionary<string, object>>();
                foreach (var toolBlock in toolUseBlocks)
                {
                    var toolId = GetStringValue(toolBlock, "id");
                    var execResult = toolResultMap.ContainsKey(toolId) ? toolResultMap[toolId] : "Error: tool execution lost";
                    // 工具执行失败（异常/拒绝/验证拦截/未知工具）标 is_error，让模型按 Anthropic 约定区分失败与成功结果
                    var toolEntry = new Dictionary<string, object>
                    {
                        { "type", "tool_result" },
                        { "tool_use_id", toolId },
                        { "content", execResult }
                    };
                    if (IsToolError(execResult))
                        toolEntry["is_error"] = true;
                    toolResults.Add(toolEntry);
                }

                lock (_historyLock)
                {
                    _conversationHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", toolResults }
                    });
                    TrimHistory();
                }
            }

            OnSystemMessage?.Invoke(Lang.L(
                $"(已达到最大迭代次数 {MaxTurns})",
                $"(Reached maximum iterations {MaxTurns})"));
        }

        private class StreamResult
        {
            public List<Dictionary<string, object>> Content;
            public string StopReason;
            public int InputTokens;
            public int OutputTokens;
            public int CacheReadTokens;
            public int CacheCreateTokens;
        }

        /// <summary>
        /// 可重试的 API 异常：5xx / 429 / 网络抖动。CallApiStream 抛出，CallApiWithRetry 捕获后做指数退避。
        /// </summary>
        private class RetryableApiException : Exception
        {
            public int? StatusCode { get; }
            public int? RetryAfterSeconds { get; }
            public RetryableApiException(string message, int? statusCode = null, int? retryAfterSeconds = null)
                : base(message)
            {
                StatusCode = statusCode;
                RetryAfterSeconds = retryAfterSeconds;
            }
        }

        // ---- API Call (Streaming SSE) ----

        private StreamResult CallApiStream(CancellationToken ct)
        {
            // Prompt caching（前缀匹配）：稳定内容放前面，动态内容放后面。
            // Block 1: AI 指令 + skills + 记忆指令 + 语言 — 几乎不变，缓存命中率高
            // Block 2: 记忆内容 — 仅在用户要求记住时才变
            // Block 3: 项目上下文（控制器状态/程序列表/编译错误）— 每次都变
            var systemBlocks = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", BuildStablePrompt() },
                    { "cache_control", new { type = "ephemeral" } }
                }
            };

            if (_memoryEnabled)
            {
                var memText = LoadMemory();
                systemBlocks.Add(new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", string.IsNullOrEmpty(memText) ? "## Persistent Memory\n\n(No memory stored yet)" : "## Persistent Memory\n\n" + memText },
                    { "cache_control", new { type = "ephemeral" } }
                });
            }

            // 动态上下文放最后 — 变化不影响前面稳定块的缓存
            systemBlocks.Add(new Dictionary<string, object>
            {
                { "type", "text" },
                { "text", BuildDynamicContext() }
            });
            var tools = GetToolDefinitions();
            // tools 也打一个 breakpoint：tool schema 几乎不变，缓存命中率非常高。
            if (tools.Count > 0)
            {
                var lastTool = new Dictionary<string, object>(tools[tools.Count - 1])
                {
                    { "cache_control", new { type = "ephemeral" } }
                };
                tools[tools.Count - 1] = lastTool;
            }

            return CallApiOnce(systemBlocks, tools, BuildTrimmedMessages(),
                _currentMaxTokens, _enableThinking, _budgetTokens, suppressUiCallbacks: false, ct);
        }

        // 测试注入点：测试可替换为返回预制 StreamResult 的桩（生产为 null，走真实 HTTP）。
        // 签名与 CallApiOnce 完全一致。
        private delegate StreamResult CallApiOnceDelegate(
            List<Dictionary<string, object>> systemBlocks,
            List<Dictionary<string, object>> tools,
            List<Dictionary<string, object>> messages,
            int maxTokens, bool enableThinking, int budgetTokens,
            bool suppressUiCallbacks, CancellationToken ct);

        private CallApiOnceDelegate _callApiOnceOverride;

        /// <summary>
        /// 底层单次 API 请求（HTTP+SSE）。messages/tools/systemBlocks/thinking 由调用方传入，
        /// 不再硬绑主线 _conversationHistory / GetToolDefinitions —— research 子 agent 复用它跑独立上下文。
        /// suppressUiCallbacks=true 时（子 agent）临时置空 OnAiText*/OnAiThinking* 回调，避免子 agent
        /// 流式输出灌进主聊天框（ReadSseStream/DispatchSseEvent 硬编码触发这些回调）。
        /// 非线程安全：仅限 _chatRunning 串行保护下的单线程同步调用。
        /// </summary>
        private StreamResult CallApiOnce(
            List<Dictionary<string, object>> systemBlocks,
            List<Dictionary<string, object>> tools,
            List<Dictionary<string, object>> messages,
            int maxTokens,
            bool enableThinking,
            int budgetTokens,
            bool suppressUiCallbacks,
            CancellationToken ct)
        {
            if (_callApiOnceOverride != null)
                return _callApiOnceOverride(systemBlocks, tools, messages, maxTokens, enableThinking, budgetTokens, suppressUiCallbacks, ct);

            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "max_tokens", maxTokens },
                { "system", systemBlocks },
                { "messages", messages },
                { "tools", tools },
                { "stream", true }
            };

            if (enableThinking)
            {
                var thinking = new Dictionary<string, object>
                {
                    { "type", "enabled" },
                    { "budget_tokens", budgetTokens }
                };
                body["thinking"] = thinking;
                if (maxTokens <= budgetTokens)
                    body["max_tokens"] = budgetTokens + 8192;
            }

            var json = SerializeRequest(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, NormalizeApiUrl(_apiUrl));
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Content = content;

            System.Net.Http.HttpResponseMessage response = null;
            try
            {
                response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { throw; }
            catch (System.IO.IOException ex)
            {
                // 网络抖动 / 连接重置 → 让外层 retry 包装重试
                throw new RetryableApiException("Network IO error: " + ex.Message);
            }
            catch (HttpRequestException ex)
            {
                // HTTP 层错误（DNS、连接拒绝、TLS 等）→ 可重试
                throw new RetryableApiException("HTTP request error: " + ex.Message);
            }
            catch (Exception ex)
            {
                if (!suppressUiCallbacks)
                    OnSystemMessage?.Invoke(Lang.L($"API 错误: {ex.Message}",
                                                        $"API Error: {ex.Message}"));
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                var code = (int)response.StatusCode;
                int? retryAfter = null;
                if (response.Headers != null)
                {
                    var ra = response.Headers.RetryAfter;
                    if (ra != null && ra.Delta.HasValue)
                        retryAfter = (int)ra.Delta.Value.TotalSeconds;
                    else if (ra != null && ra.Date.HasValue)
                        retryAfter = (int)(ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                }
                response.Dispose();

                // 5xx 服务端错误 / 429 限流 → 抛 RetryableApiException 让外层重试
                if (code == 429 || (code >= 500 && code < 600))
                {
                    throw new RetryableApiException(
                        $"API {code}: {Truncate(errText, 300)}",
                        statusCode: code,
                        retryAfterSeconds: retryAfter);
                }

                // 4xx 其他（认证错、参数错等）→ 不可重试，直接报错
                if (!suppressUiCallbacks)
                    OnSystemMessage?.Invoke(Lang.L($"API 错误 ({code}): {Truncate(errText, 500)}",
                                                        $"API Error ({code}): {Truncate(errText, 500)}"));
                return null;
            }

            // UI 抑制：子 agent 模式临时置空流式回调，finally 恢复（仅同步调用安全）。
            Action savedTextStart = null, savedTextEnd = null, savedThinkStart = null, savedThinkEnd = null;
            Action<string> savedTextDelta = null, savedThinkDelta = null;
            if (suppressUiCallbacks)
            {
                savedTextStart = OnAiTextStart;       OnAiTextStart = null;
                savedTextDelta = OnAiTextDelta;       OnAiTextDelta = null;
                savedTextEnd   = OnAiTextEnd;         OnAiTextEnd = null;
                savedThinkStart = OnAiThinkingStart;  OnAiThinkingStart = null;
                savedThinkDelta = OnAiThinkingDelta;  OnAiThinkingDelta = null;
                savedThinkEnd  = OnAiThinkingEnd;     OnAiThinkingEnd = null;
            }
            try
            {
                return ReadSseStream(response, ct);
            }
            finally
            {
                if (suppressUiCallbacks)
                {
                    OnAiTextStart = savedTextStart;     OnAiTextDelta = savedTextDelta;     OnAiTextEnd = savedTextEnd;
                    OnAiThinkingStart = savedThinkStart; OnAiThinkingDelta = savedThinkDelta; OnAiThinkingEnd = savedThinkEnd;
                }
                response.Dispose();
            }
        }

        /// <summary>
        /// 调用 CallApiStream，对可重试错误（5xx / 429 / 网络 IO）做指数退避重试，最多 MaxAttempts 次。
        /// OperationCanceledException 直接抛出（用户取消，不重试）。
        /// 重试耗尽后返回 null（Chat loop 会显示通用失败消息）。
        /// </summary>
        private StreamResult CallApiWithRetry(CancellationToken ct)
        {
            const int MaxAttempts = 3;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    return CallApiStream(ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (RetryableApiException ex)
                {
                    if (attempt >= MaxAttempts - 1)
                    {
                        OnSystemMessage?.Invoke(Lang.L(
                            $"⚠ API 重试 {MaxAttempts} 次仍失败 ({ex.StatusCode?.ToString() ?? "network"}): {ex.Message}",
                            $"⚠ API failed after {MaxAttempts} retries ({ex.StatusCode?.ToString() ?? "network"}): {ex.Message}"));
                        return null;
                    }
                    int delay = ex.RetryAfterSeconds.HasValue && ex.RetryAfterSeconds.Value > 0
                        ? Math.Min(ex.RetryAfterSeconds.Value * 1000, 30000)
                        : GetBackoffDelay(attempt);
                    OnSystemMessage?.Invoke(Lang.L(
                        $"⚠ API {ex.StatusCode?.ToString() ?? "网络错误"}，{delay / 1000.0:F1}s 后重试（第 {attempt + 2}/{MaxAttempts} 次）...",
                        $"⚠ API {ex.StatusCode?.ToString() ?? "network error"}, retrying in {delay / 1000.0:F1}s (attempt {attempt + 2}/{MaxAttempts})..."));
                    SleepWithCancel(delay, ct);
                }
                catch (System.IO.IOException ex)
                {
                    if (attempt >= MaxAttempts - 1)
                    {
                        OnSystemMessage?.Invoke(Lang.L(
                            $"⚠ 网络错误重试 {MaxAttempts} 次仍失败: {ex.Message}",
                            $"⚠ Network error failed after {MaxAttempts} retries: {ex.Message}"));
                        return null;
                    }
                    int delay = GetBackoffDelay(attempt);
                    OnSystemMessage?.Invoke(Lang.L(
                        $"⚠ 网络错误: {ex.Message}，{delay / 1000.0:F1}s 后重试（第 {attempt + 2}/{MaxAttempts} 次）...",
                        $"⚠ Network error: {ex.Message}, retrying in {delay / 1000.0:F1}s (attempt {attempt + 2}/{MaxAttempts})..."));
                    SleepWithCancel(delay, ct);
                }
            }
            return null;
        }

        private static int GetBackoffDelay(int attempt)
        {
            // 1s, 2s, 4s 指数退避
            return (int)Math.Pow(2, attempt) * 1000;
        }

        private static void SleepWithCancel(int ms, CancellationToken ct)
        {
            try { Task.Delay(ms, ct).Wait(ct); }
            catch (OperationCanceledException) { throw; }
            catch (AggregateException ae)
            {
                // Task.Delay 偶发 AggregateException 包裹 OperationCanceledException
                if (ae.InnerExceptions.Count == 1 && ae.InnerExceptions[0] is OperationCanceledException)
                    throw;
                throw;
            }
        }


        private StreamResult ReadSseStream(HttpResponseMessage response, CancellationToken ct)
        {
            var result = new StreamResult
            {
                Content = new List<Dictionary<string, object>>(),
                StopReason = null
            };

            // Pending block state — hoisted out of `using` so we can flush on exit.
            string pendingType = null;       // "text" | "tool_use" | "thinking"
            string pendingText = null;       // text buffer
            string pendingToolId = null;
            string pendingToolName = null;
            StringBuilder pendingToolInput = null;
            string pendingThinkingText = null;
            string pendingSignature = null;        // thinking 块的 signature（signature_delta 覆盖式赋值）
            string pendingRedactedData = null;     // redacted_thinking 块的 data（content_block_start 一次性给出）

            using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            using (var reader = new StreamReader(stream))
            {
                string currentEvent = null;
                StringBuilder dataBuf = new StringBuilder();

                while (!ct.IsCancellationRequested)
                {
                    string line;
                    try { line = reader.ReadLine(); }
                    catch (OperationCanceledException) { throw; }
                    if (line == null) break; // EOF

                    if (line.Length == 0)
                    {
                        // Dispatch event
                        if (dataBuf.Length > 0)
                        {
                            DispatchSseEvent(currentEvent, dataBuf.ToString(), result,
                                ref pendingType, ref pendingText,
                                ref pendingToolId, ref pendingToolName, ref pendingToolInput,
                                ref pendingThinkingText, ref pendingSignature, ref pendingRedactedData);
                        }
                        currentEvent = null;
                        dataBuf.Clear();
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.Ordinal))
                        currentEvent = line.Substring(6).Trim();
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var d = line.Substring(5);
                        if (d.StartsWith(" ")) d = d.Substring(1);
                        if (dataBuf.Length > 0) dataBuf.Append('\n');
                        dataBuf.Append(d);
                    }
                    // ignore comments / id / retry
                }
            }

            // Flush any pending text block interrupted mid-stream so the partial
            // text is preserved in conversation history. Pending tool_use blocks
            // are dropped because their input_json is incomplete and would leave
            // an unmatched tool_use_id in history (mirrors cc-haha's
            // filterUnresolvedToolUses).
            if (pendingType == "text")
            {
                var finalText = pendingText ?? "";
                if (!string.IsNullOrEmpty(finalText))
                {
                    result.Content.Add(new Dictionary<string, object>
                    {
                        { "type", "text" },
                        { "text", finalText }
                    });
                }
                OnAiTextEnd?.Invoke();
            }
            else if (pendingType == "thinking")
            {
                // stream 中断在 thinking 块中途：未收到 content_block_stop，signature_delta 也未到（它只在 stop 前发）。
                // 此 partial 块无 signature，照 Anthropic 规范「thinking 块须匹配模型输出」不能回传 → 不进历史。
                // UI 已通过 OnAiThinkingDelta 看到内容，这里仅发收尾事件。
                OnAiThinkingEnd?.Invoke();
            }

            if (ct.IsCancellationRequested && result.Content.Count == 0)
                throw new OperationCanceledException(ct);

            return result;
        }

        private void DispatchSseEvent(
            string eventType, string dataJson, StreamResult result,
            ref string pendingType, ref string pendingText,
            ref string pendingToolId, ref string pendingToolName,
            ref StringBuilder pendingToolInput,
            ref string pendingThinkingText, ref string pendingSignature, ref string pendingRedactedData)
        {
            Dictionary<string, object> evt;
            try { evt = _json.Deserialize<Dictionary<string, object>>(dataJson); }
            catch { return; }
            if (evt == null) return;

            var type = GetStringValue(evt, "type") ?? eventType;
            if (string.IsNullOrEmpty(type)) return;

            switch (type)
            {
                case "message_start":
                {
                    var msgObj = GetDictValue(evt, "message");
                    if (msgObj != null)
                    {
                        var usage = GetDictValue(msgObj, "usage");
                        if (usage != null)
                        {
                            result.InputTokens = GetIntValue(usage, "input_tokens");
                            result.OutputTokens = GetIntValue(usage, "output_tokens");
                            result.CacheReadTokens = GetIntValue(usage, "cache_read_input_tokens");
                            result.CacheCreateTokens = GetIntValue(usage, "cache_creation_input_tokens");
                        }
                    }
                    break;
                }

                case "content_block_start":
                {
                    object blockObj;
                    if (!evt.TryGetValue("index", out blockObj)) break;
                    var block = GetDictValue(evt, "content_block");
                    if (block == null) break;
                    pendingType = GetStringValue(block, "type");
                    pendingText = null;
                    pendingToolId = null;
                    pendingToolName = null;
                    pendingToolInput = null;
                    pendingThinkingText = null;
                    pendingSignature = null;
                    pendingRedactedData = null;
                    if (pendingType == "text")
                    {
                        OnAiTextStart?.Invoke();
                    }
                    else if (pendingType == "thinking")
                    {
                        OnAiThinkingStart?.Invoke();
                    }
                    else if (pendingType == "redacted_thinking")
                    {
                        // data 在 content_block_start 一次性给出，无 delta 事件。
                        pendingRedactedData = GetStringValue(block, "data");
                    }
                    else if (pendingType == "tool_use")
                    {
                        pendingToolId = GetStringValue(block, "id");
                        pendingToolName = GetStringValue(block, "name");
                        pendingToolInput = new StringBuilder();
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var delta = GetDictValue(evt, "delta");
                    if (delta == null) break;
                    var deltaType = GetStringValue(delta, "type");
                    if (deltaType == "text_delta")
                    {
                        var text = GetStringValue(delta, "text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            pendingText = (pendingText ?? "") + text;
                            OnAiTextDelta?.Invoke(text);
                        }
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var pj = GetStringValue(delta, "partial_json");
                        if (!string.IsNullOrEmpty(pj) && pendingToolInput != null)
                            pendingToolInput.Append(pj);
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        var thinkingText = GetStringValue(delta, "thinking");
                        if (!string.IsNullOrEmpty(thinkingText))
                        {
                            // 客户端硬截断：GLM 端点不守 budget_tokens，按 budget_tokens×2 字符
                            //（中文 ~2 字/token）封顶，超限后丢弃后续 delta 并留截断标记。
                            var cur = pendingThinkingText ?? "";
                            if (cur.EndsWith(ThinkTruncatedTag, StringComparison.Ordinal))
                            {
                                // 已截断，后续 thinking_delta 一律丢弃
                            }
                            else if (cur.Length + thinkingText.Length > _budgetTokens * 2)
                            {
                                pendingThinkingText = cur + ThinkTruncatedTag;
                                OnAiThinkingDelta?.Invoke(ThinkTruncatedTag);
                            }
                            else
                            {
                                pendingThinkingText = cur + thinkingText;
                                OnAiThinkingDelta?.Invoke(thinkingText);
                            }
                        }
                    }
                    else if (deltaType == "signature_delta")
                    {
                        // signature_delta 携带完整 signature，覆盖式赋值（非累加）—— 对照 cc-haha claude.ts:2212。
                        var sig = GetStringValue(delta, "signature");
                        if (!string.IsNullOrEmpty(sig))
                            pendingSignature = sig;
                    }
                    break;
                }

                case "content_block_stop":
                {
                    if (pendingType == "text")
                    {
                        var finalText = pendingText ?? "";
                        result.Content.Add(new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", finalText }
                        });
                        OnAiTextEnd?.Invoke();
                    }
                    else if (pendingType == "tool_use")
                    {
                        var inputJson = pendingToolInput != null ? pendingToolInput.ToString() : "";
                        object inputParsed = new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(inputJson))
                        {
                            try
                            {
                                var p = _json.Deserialize<Dictionary<string, object>>(inputJson);
                                if (p != null) inputParsed = p;
                            }
                            catch { /* leave as empty dict */ }
                        }
                        result.Content.Add(new Dictionary<string, object>
                        {
                            { "type", "tool_use" },
                            { "id", pendingToolId ?? "" },
                            { "name", pendingToolName ?? "" },
                            { "input", inputParsed }
                        });
                    }
                    else if (pendingType == "thinking")
                    {
                        var tb = new Dictionary<string, object>
                        {
                            { "type", "thinking" },
                            { "thinking", pendingThinkingText ?? "" }
                        };
                        // 保留 signature：Anthropic extended thinking 规范要求多轮传回必须带有效 signature。
                        if (!string.IsNullOrEmpty(pendingSignature))
                            tb["signature"] = pendingSignature;
                        result.Content.Add(tb);
                        OnAiThinkingEnd?.Invoke();
                    }
                    else if (pendingType == "redacted_thinking")
                    {
                        result.Content.Add(new Dictionary<string, object>
                        {
                            { "type", "redacted_thinking" },
                            { "data", pendingRedactedData ?? "" }
                        });
                    }
                    pendingType = null;
                    pendingText = null;
                    pendingToolId = null;
                    pendingToolName = null;
                    pendingToolInput = null;
                    pendingThinkingText = null;
                    pendingSignature = null;
                    pendingRedactedData = null;
                    break;
                }

                case "message_delta":
                {
                    var delta = GetDictValue(evt, "delta");
                    if (delta != null)
                    {
                        var sr = GetStringValue(delta, "stop_reason");
                        if (!string.IsNullOrEmpty(sr)) result.StopReason = sr;
                    }
                    break;
                }

                case "message_stop":
                case "ping":
                    break;

                case "error":
                {
                    var err = GetDictValue(evt, "error");
                    var msg = err != null ? GetStringValue(err, "message") : dataJson;
                    OnSystemMessage?.Invoke(Lang.L("API 错误: ", "API Error: ") + Truncate(msg ?? Lang.L("未知", "unknown"), 500));
                    break;
                }
            }
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
