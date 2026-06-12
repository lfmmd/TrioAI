using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
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

        // ---- Perf diagnostics (remove after diagnosis) ----
        private static readonly Stopwatch _perfSw = Stopwatch.StartNew();
        private static readonly List<string> _perfLines = new List<string>();
        private static readonly object _perfLock = new object();
        public static void PerfLog(string label)
        {
            try
            {
                var line = string.Format("{0,8:F1} ms  {1}", _perfSw.Elapsed.TotalMilliseconds, label);
                lock (_perfLock) { _perfLines.Add(line); }
            }
            catch { }
        }
        public static void PerfLogFlush()
        {
            List<string> snapshot;
            lock (_perfLock) { snapshot = new List<string>(_perfLines); }
            if (snapshot.Count == 0) snapshot.Add("(no entries)");
            string dir = null;
            try
            {
                dir = Path.GetDirectoryName(PromptPath); // same dir as AI_INSTRUCTIONS.md
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "perf_log.txt"),
                    string.Join(Environment.NewLine, snapshot) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                try
                {
                    var errPath = Path.Combine(dir ?? Path.GetTempPath(), "perf_error.txt");
                    File.AppendAllText(errPath, ex.ToString() + Environment.NewLine);
                }
                catch { }
            }
        }
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

        // DEBUG: 将完整 API 请求体写入日志文件，用于排查 messages 参数非法等问题。
        // 日志路径：%APPDATA%/TrioAI/api_debug_log.txt，关闭调试时删除此调用即可。
        private static void LogApiRequest(string label, string json)
        {
            try
            {
                var dir = Path.GetDirectoryName(PromptPath);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "api_debug_log.txt");
                var header = string.Format("{0}[{1}] len={2}{3}",
                    Environment.NewLine + "---" + Environment.NewLine,
                    DateTime.Now.ToString("HH:mm:ss.fff"), label,
                    json.Length);
                File.AppendAllText(path, header + Environment.NewLine + json + Environment.NewLine);
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

        public AiService()
        {
            PerfLog("AiService ctor: enter");
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            PerfLog("AiService ctor: HttpClient created");
            LoadConfig();
            PerfLog("AiService ctor: LoadConfig done");
            Directory.CreateDirectory(HistoryDir);
            Directory.CreateDirectory(BackupDir);
            Directory.CreateDirectory(DataDir);
            try { Directory.CreateDirectory(MemoryDir); } catch { }
            PerfLog("AiService ctor: dirs ensured");
            // 首次创建提示词文件 — 用户手动修改后不会被覆盖。
            // 想恢复默认：删除文件，或在 UI 点击「初始化 Skills」。
            try { if (!File.Exists(PromptPath)) File.WriteAllText(PromptPath, DefaultPrompt); } catch { }
            PerfLog("AiService ctor: prompt written");
            SubscribeCompileEvents();
            PerfLog("AiService ctor: SubscribeCompileEvents done");
        }

        // ---- Compile State Monitoring ----

        private void SubscribeCompileEvents()
        {
            PerfLog("SubscribeCompileEvents: enter");
            try
            {
                DispatcherHelper.Invoke(() =>
                {
                    PerfLog("SubscribeCompileEvents: inside dispatcher invoke");
                    var ctrl = Trio.SharedLibrary.MPSingletons.Controller;
                    PerfLog("SubscribeCompileEvents: got Controller");
                    if (ctrl != null)
                    {
                        ctrl.CompileStateChanged += OnCompileStateChanged;
                        PerfLog("SubscribeCompileEvents: subscribed");
                    }
                    else
                    {
                        PerfLog("SubscribeCompileEvents: Controller is null");
                    }
                });
            }
            catch (Exception ex) { PerfLog("SubscribeCompileEvents EXCEPTION: " + ex.Message); }
            PerfLog("SubscribeCompileEvents: exit");
        }

        private void OnCompileStateChanged(object sender, Trio.SharedLibrary.COMPILEStateEventArgs e)
        {
            try
            {
                if (e.ErrorCode != 0)
                {
                    var errMsg = string.Format("[Compile Error] {0}: line {1}, error #{2} - {3}",
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
                OnToolStatus?.Invoke($"Backup saved: {backupName}");
            }
            catch { }
        }

        // ---- Agentic Loop ----

        public void Chat(string userMessage, CancellationToken ct = default)
        {
            if (!HasApiKey)
            {
                OnSystemMessage?.Invoke("API key not configured. Click 'Settings' to set your API key.");
                return;
            }

            if (string.IsNullOrEmpty(_currentSessionId))
                _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            _conversationHistory.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", userMessage }
            });

            var systemPrompt = BuildSystemPrompt();

            for (int turn = 0; turn < 20; turn++)
            {
                StreamResult result;
                try { result = CallApiStream(systemPrompt, ct); }
                catch (OperationCanceledException)
                {
                    // User cancelled before any content_block_stop arrived — no
                    // assistant content was preserved. Append a sentinel so the
                    // next user prompt doesn't end up adjacent to the previous
                    // one (Anthropic API requires strict user/assistant
                    // alternation). Mirrors cc-haha's NO_RESPONSE_REQUESTED
                    // sentinel strategy.
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
                    return;
                }
                catch (System.IO.IOException ex)
                {
                    OnSystemMessage?.Invoke($"Network error: {ex.Message}");
                    return;
                }

                if (result == null)
                {
                    OnSystemMessage?.Invoke("Failed to call AI API. Check your API key, URL, and network.");
                    return;
                }

                // Escalate: 输出被 max_tokens 截断 → 升级到 64000 重试本次（不前进 turn）
                // 适用场景：AI 在 write_source 写大程序被切；agentic loop 输出超长
                if (result.StopReason == "max_tokens" && _currentMaxTokens < EscalatedMaxTokens)
                {
                    OnSystemMessage?.Invoke($"⚠ 输出被 max_tokens={_currentMaxTokens} 截断，升级到 {EscalatedMaxTokens} 重试...");
                    _currentMaxTokens = EscalatedMaxTokens;
                    turn--;  // 不算这一轮
                    continue;
                }

                _conversationHistory.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", result.Content }
                });

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
                    return;
                }

                if (toolUseBlocks.Count == 0 || result.StopReason != "tool_use")
                {
                    if (result.StopReason == "max_tokens")
                        OnSystemMessage?.Invoke($"⚠ 即使 max_tokens={_currentMaxTokens} 仍被截断。建议改用 patch_source 分批写入（每个 operation 仅一行变更，几乎不受 token 限制）。");
                    return;
                }

                var toolResults = new List<Dictionary<string, object>>();
                foreach (var toolBlock in toolUseBlocks)
                {
                    var toolId = GetStringValue(toolBlock, "id");
                    var toolName = GetStringValue(toolBlock, "name");
                    var toolInput = GetDictValue(toolBlock, "input") ?? new Dictionary<string, object>();

                    var execResult = ExecuteTool(toolName, toolInput);
                    toolResults.Add(new Dictionary<string, object>
                    {
                        { "type", "tool_result" },
                        { "tool_use_id", toolId },
                        { "content", execResult }
                    });
                }

                _conversationHistory.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", toolResults }
                });

                TrimHistory();
            }

            OnSystemMessage?.Invoke("(Reached maximum iterations)");
        }

        private class StreamResult
        {
            public List<Dictionary<string, object>> Content;
            public string StopReason;
        }

        // ---- API Call (Streaming SSE) ----

        private StreamResult CallApiStream(string systemPrompt, CancellationToken ct)
        {
            // Prompt caching: 把 system 转成 content-block 数组并打 cache_control 标记。
            // 这样 system 前缀（含环境信息、可用 skills、参考库列表）只算一次完整 token，
            // 5 分钟 TTL 内命中走缓存（输入 token 计费 1/10）。
            var systemBlocks = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", systemPrompt },
                    { "cache_control", new { type = "ephemeral" } }
                }
            };
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

            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "max_tokens", _currentMaxTokens },
                { "system", systemBlocks },
                { "messages", BuildTrimmedMessages() },
                { "tools", tools },
                { "stream", true }
            };

            if (_enableThinking)
            {
                body["budget_tokens"] = _budgetTokens;
                if (_currentMaxTokens <= _budgetTokens)
                    body["max_tokens"] = _budgetTokens + 8192;
            }

            var json = SerializeRequest(body);
            LogApiRequest("stream", json);
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
            catch (Exception ex)
            {
                OnSystemMessage?.Invoke($"API Error: {ex.Message}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                LogApiRequest("error-" + response.StatusCode, errText);
                OnSystemMessage?.Invoke($"API Error ({response.StatusCode}): {Truncate(errText, 500)}");
                response.Dispose();
                return null;
            }

            try
            {
                return ReadSseStream(response, ct);
            }
            finally
            {
                response.Dispose();
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
                                ref pendingThinkingText);
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
                var finalThinking = pendingThinkingText ?? "";
                if (!string.IsNullOrEmpty(finalThinking))
                {
                    result.Content.Add(new Dictionary<string, object>
                    {
                        { "type", "thinking" },
                        { "thinking", finalThinking }
                    });
                }
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
            ref string pendingThinkingText)
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
                    // nothing to do
                    break;

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
                    if (pendingType == "text")
                    {
                        OnAiTextStart?.Invoke();
                    }
                    else if (pendingType == "thinking")
                    {
                        OnAiThinkingStart?.Invoke();
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
                            pendingThinkingText = (pendingThinkingText ?? "") + thinkingText;
                            OnAiThinkingDelta?.Invoke(thinkingText);
                        }
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
                        result.Content.Add(new Dictionary<string, object>
                        {
                            { "type", "thinking" },
                            { "thinking", pendingThinkingText ?? "" }
                        });
                        OnAiThinkingEnd?.Invoke();
                    }
                    pendingType = null;
                    pendingText = null;
                    pendingToolId = null;
                    pendingToolName = null;
                    pendingToolInput = null;
                    pendingThinkingText = null;
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
                    LogApiRequest("sse-error", msg ?? dataJson);
                    OnSystemMessage?.Invoke("API Error: " + Truncate(msg ?? "unknown", 500));
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
