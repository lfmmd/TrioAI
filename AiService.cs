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
    internal class AiService
    {
        private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrioAI");
        private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
        private static readonly string HistoryDir = Path.Combine(DataDir, "chat_history");
        private static readonly string BackupDir = Path.Combine(DataDir, "backups");
        private static readonly string SkillsDir = Path.Combine(DataDir, "skills");

        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private readonly HttpClient _http;
        private string _apiKey;
        private string _model;
        private string _apiUrl;
        private bool _showToolStatus = true;
        private bool _skillsInitialized;
        private readonly List<Dictionary<string, object>> _conversationHistory = new List<Dictionary<string, object>>();
        private string _currentSessionId;
        private static string _lastCompileError;

        private const int MaxHistoryKeep = 30;
        private const int MaxRecentKeep = 20;
        private const int MaxToolResultLen = 10000;
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

        // Callbacks for ChatPanel
        public Action OnAiTextStart { get; set; }
        public Action<string> OnAiTextDelta { get; set; }
        public Action OnAiTextEnd { get; set; }
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
            PerfLog("AiService ctor: dirs ensured");
            // 首次创建提示词文件 — 用户手动修改后不会被覆盖。
            // 想恢复默认：删除文件，或在 UI 点击「初始化 Skills」。
            try { if (!File.Exists(PromptPath)) File.WriteAllText(PromptPath, DefaultPrompt); } catch { }
            PerfLog("AiService ctor: prompt written");
            SubscribeCompileEvents();
            PerfLog("AiService ctor: SubscribeCompileEvents done");
        }

        // ---- Config ----

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var text = File.ReadAllText(ConfigPath);
                    var cfg = _json.Deserialize<Dictionary<string, object>>(text);
                    object val;
                    if (cfg.TryGetValue("apiKey", out val)) _apiKey = val?.ToString();
                    if (cfg.TryGetValue("model", out val)) _model = val?.ToString();
                    if (cfg.TryGetValue("apiUrl", out val)) _apiUrl = val?.ToString();
                    if (cfg.TryGetValue("showToolStatus", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _showToolStatus = b;
                    }
                    if (cfg.TryGetValue("skillsInitialized", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _skillsInitialized = b;
                    }
                }
            }
            catch { }
        }

        public void SaveConfig(string apiKey, string model, string apiUrl, bool? showToolStatus = null)
        {
            _apiKey = apiKey;
            if (!string.IsNullOrEmpty(model)) _model = model;
            if (!string.IsNullOrEmpty(apiUrl)) _apiUrl = apiUrl;
            if (showToolStatus.HasValue) _showToolStatus = showToolStatus.Value;
            var json = _json.Serialize(new { apiKey = _apiKey, model = _model, apiUrl = _apiUrl, showToolStatus = _showToolStatus, skillsInitialized = _skillsInitialized });
            File.WriteAllText(ConfigPath, json);
        }

        // Auto-append /v1/messages if user typed only the base (e.g. https://api.deepseek.com/anthropic).
        // Anthropic-compatible endpoints need the full path; missing it returns 404 NotFound.
        private static string NormalizeApiUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "https://api.deepseek.com/anthropic/v1/messages";
            url = url.Trim();
            if (url.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
                return url;
            if (url.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
                return url + "/v1/messages";
            if (url.EndsWith("/anthropic/", StringComparison.OrdinalIgnoreCase))
                return url + "v1/messages";
            return url;
        }

        public string CurrentConfig => _json.Serialize(new { apiKey = _apiKey ?? "", model = _model ?? "", apiUrl = _apiUrl ?? "", showToolStatus = _showToolStatus });
        public bool ShowToolStatus => _showToolStatus;
        public bool SkillsInitialized => _skillsInitialized;

        public string InitializeSkills()
        {
            var dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var srcDir = Path.Combine(dllDir, "skills");
            if (!Directory.Exists(srcDir))
                return "插件目录下未找到 skills 文件夹";

            if (!Directory.Exists(SkillsDir))
                Directory.CreateDirectory(SkillsDir);

            // Copy skills directories
            foreach (var dir in Directory.GetDirectories(srcDir, "*", SearchOption.TopDirectoryOnly))
            {
                var dest = Path.Combine(SkillsDir, Path.GetFileName(dir));
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);
                CopyDirectory(dir, dest);
            }

            // Deploy AI_INSTRUCTIONS.md (always overwrite to keep rules up-to-date)
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(PromptPath, DefaultPrompt);

            _skillsInitialized = true;
            _index = null; // force reload
            var json = _json.Serialize(new { apiKey = _apiKey ?? "", model = _model ?? "", apiUrl = _apiUrl ?? "", showToolStatus = _showToolStatus, skillsInitialized = true });
            File.WriteAllText(ConfigPath, json);
            return null;
        }

        private static void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
        }

        public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);
        public string Model => _model;
        public string ApiUrl => _apiUrl;

        // ---- Session / History ----

        public string CurrentSessionId => _currentSessionId;

        public string StartNewSession()
        {
            _conversationHistory.Clear();
            _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return _currentSessionId;
        }

        public void ClearHistory()
        {
            _conversationHistory.Clear();
            _currentSessionId = null;
        }

        public void SaveSession(string[] displayMessages)
        {
            if (string.IsNullOrEmpty(_currentSessionId)) return;
            var path = Path.Combine(HistoryDir, _currentSessionId + ".json");
            var data = new
            {
                id = _currentSessionId,
                updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                messages = displayMessages ?? new string[0]
            };
            File.WriteAllText(path, _json.Serialize(data));
        }

        public string LoadSession(string sessionId)
        {
            var path = Path.Combine(HistoryDir, sessionId + ".json");
            if (!File.Exists(path)) return null;
            _currentSessionId = sessionId;
            _conversationHistory.Clear();
            return File.ReadAllText(path);
        }

        public void DeleteSession(string sessionId)
        {
            var path = Path.Combine(HistoryDir, sessionId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }

        public List<SessionInfo> ListSessions()
        {
            var result = new List<SessionInfo>();
            if (!Directory.Exists(HistoryDir)) return result;
            foreach (var file in Directory.GetFiles(HistoryDir, "*.json").OrderByDescending(f => f))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var data = _json.Deserialize<Dictionary<string, object>>(text);
                    var id = Path.GetFileNameWithoutExtension(file);
                    object updated;
                    var timeStr = data.TryGetValue("updated", out updated) ? updated?.ToString() : id;
                    // Extract first user message as preview
                    string preview = "";
                    object msgs;
                    if (data.TryGetValue("messages", out msgs) && msgs is System.Collections.ArrayList al && al.Count > 0)
                    {
                        // messages are serialized ChatMessage objects
                        foreach (var m in al)
                        {
                            var md = m as Dictionary<string, object>;
                            if (md != null && GetStr(md, "role") == "User")
                            {
                                preview = GetStr(md, "text") ?? "";
                                if (preview.Length > 50) preview = preview.Substring(0, 50) + "...";
                                break;
                            }
                        }
                    }
                    result.Add(new SessionInfo { Id = id, Time = timeStr, Preview = preview });
                }
                catch { }
            }
            return result;
        }

        internal class SessionInfo
        {
            public string Id { get; set; }
            public string Time { get; set; }
            public string Preview { get; set; }
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
            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "max_tokens", _currentMaxTokens },
                { "system", systemPrompt },
                { "messages", BuildTrimmedMessages() },
                { "tools", GetToolDefinitions() },
                { "stream", true }
            };

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
            catch (Exception ex)
            {
                OnSystemMessage?.Invoke($"API Error: {ex.Message}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
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

        private void TrimHistory()
        {
            if (_conversationHistory.Count <= MaxHistoryKeep)
                return;

            int start = _conversationHistory.Count - MaxRecentKeep;
            if (start < 1) start = 1;

            // 裁剪起点必须是 plain-text user 消息：
            // - 不能从 assistant(tool_use) 起 — 会让上一轮的 tool_result 孤立
            // - 不能从 user(tool_result) 起 — 它对应的 tool_use 在前一条 assistant，会被丢掉
            int found = -1;
            for (int i = start; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                if (GetStringValue(msg, "role") == "user" && msg["content"] is string)
                {
                    found = i;
                    break;
                }
            }

            // 找不到合适的裁剪点 — 跳过本次裁剪。
            // token 超限远比 BadRequest（破坏 tool 配对）好处理。
            if (found <= 0)
                return;

            var trimmed = new List<Dictionary<string, object>>(
                _conversationHistory.GetRange(found, _conversationHistory.Count - found));
            _conversationHistory.Clear();
            _conversationHistory.AddRange(trimmed);
        }

        private List<Dictionary<string, object>> BuildTrimmedMessages()
        {
            var messages = new List<Dictionary<string, object>>(_conversationHistory.Count);
            foreach (var msg in _conversationHistory)
            {
                var copy = new Dictionary<string, object>(msg);
                var content = copy["content"];

                // Truncate tool_result strings in user messages
                if (copy["role"] as string == "user" && content is List<Dictionary<string, object>> blocks)
                {
                    var trimmedBlocks = new List<Dictionary<string, object>>();
                    foreach (var block in blocks)
                    {
                        if (GetStringValue(block, "type") == "tool_result")
                        {
                            var tb = new Dictionary<string, object>(block);
                            var c = GetStringValue(tb, "content");
                            if (c != null && c.Length > MaxToolResultLen)
                                tb["content"] = c.Substring(0, MaxToolResultLen) + "...[truncated]";
                            trimmedBlocks.Add(tb);
                        }
                        else
                        {
                            trimmedBlocks.Add(block);
                        }
                    }
                    copy["content"] = trimmedBlocks;
                }

                messages.Add(copy);
            }
            return messages;
        }

        private StreamResult ReadSseStream(HttpResponseMessage response, CancellationToken ct)
        {
            var result = new StreamResult
            {
                Content = new List<Dictionary<string, object>>(),
                StopReason = null
            };

            // Pending block state — hoisted out of `using` so we can flush on exit.
            string pendingType = null;       // "text" | "tool_use"
            string pendingText = null;       // text buffer
            string pendingToolId = null;
            string pendingToolName = null;
            StringBuilder pendingToolInput = null;

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
                                ref pendingToolId, ref pendingToolName, ref pendingToolInput);
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

            if (ct.IsCancellationRequested && result.Content.Count == 0)
                throw new OperationCanceledException(ct);

            return result;
        }

        private void DispatchSseEvent(
            string eventType, string dataJson, StreamResult result,
            ref string pendingType, ref string pendingText,
            ref string pendingToolId, ref string pendingToolName,
            ref StringBuilder pendingToolInput)
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
                    if (pendingType == "text")
                    {
                        OnAiTextStart?.Invoke();
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
                    pendingType = null;
                    pendingText = null;
                    pendingToolId = null;
                    pendingToolName = null;
                    pendingToolInput = null;
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

        // ---- Tool Execution ----

        private static readonly HashSet<string> SourceWriteTools = new HashSet<string>
        {
            "write_source", "patch_source"
        };

        private static readonly HashSet<string> WriteTools = new HashSet<string>
        {
            "write_source", "patch_source", "write_vr", "write_table",
            "create_program", "delete_program", "rename_program",
            "upload", "download", "compile_program", "run_program", "stop_program",
            "set_program_process"
        };

        private string ExecuteTool(string name, Dictionary<string, object> input)
        {
            try
            {
                // Backup source code before write operations
                if (SourceWriteTools.Contains(name))
                {
                    var progName = GetStr(input, "name");
                    if (!string.IsNullOrEmpty(progName))
                        BackupSource(progName);
                }

                // Write operations need confirmation
                if (WriteTools.Contains(name))
                {
                    var argsPreview = _json.Serialize(input);
                    var accepted = OnConfirmWrite?.Invoke(name, argsPreview) ?? false;
                    if (!accepted)
                        return "User rejected this operation.";
                }

                var result = DispatcherHelper.Invoke(() => DispatchTool(name, input));
                var resultStr = _json.Serialize(result);
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: {Truncate(resultStr, 300)}");
                return _json.Serialize(result);
            }
            catch (Exception ex)
            {
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: ERROR - {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        private object DispatchTool(string name, Dictionary<string, object> input)
        {
            switch (name)
            {
                case "get_status": return Handlers.GetStatus();
                case "list_programs": return Handlers.ListPrograms();
                case "read_source": return Handlers.ReadSource(GetStr(input, "name"));
                case "write_source": return Handlers.WriteSource(GetStr(input, "name"), input);
                case "patch_source": return Handlers.PatchSource(GetStr(input, "name"), input);
                case "read_vr": return Handlers.ReadVR(GetInt(input, "address"), GetInt(input, "count"));
                case "write_vr": return Handlers.WriteVR(GetInt(input, "address"), input);
                case "read_table": return Handlers.ReadTable(GetInt(input, "address"), GetInt(input, "count"));
                case "write_table": return Handlers.WriteTable(GetInt(input, "address"), input);
                case "list_axes": return Handlers.ListAxes();
                case "list_descriptors": return Handlers.ListDescriptors();
                case "create_program": return Handlers.CreateProgram(input);
                case "delete_program": return Handlers.DeleteProgram(GetStr(input, "name"));
                case "rename_program": return Handlers.RenameProgram(GetStr(input, "name"), input);
                case "compile_program": return Handlers.Compile(GetStr(input, "name"));
                case "run_program": return Handlers.RunProgram(GetStr(input, "name"), input);
                case "stop_program": return Handlers.StopProgram(GetStr(input, "name"), input);
                case "upload": return Handlers.Upload(GetStr(input, "name"));
                case "download": return Handlers.Download(GetStr(input, "name"));
                case "save_project": return Handlers.SaveProject();
                case "open_program": return Handlers.OpenProgram(GetStr(input, "name"));
                case "get_program_process": return Handlers.GetProgramProcess(GetStr(input, "name"));
                case "set_program_process": return Handlers.SetProgramProcess(GetStr(input, "name"), input);
                case "lookup_command": return LookupCommand(GetStr(input, "query"));
                case "search_code": return Handlers.SearchCode(GetStr(input, "query"), GetBool(input, "caseSensitive"));
                default: return new { error = $"Unknown tool: {name}" };
            }
        }

        // ---- Command Lookup (two-tier: index for search, skills.json for details) ----

        private class SkillIndexEntry
        {
            public string Name;
            public string Type;
            public string Desc;
            public int Start; // 1-based line number in skills.json
            public int End;
            public string Dir;
        }

        private static List<SkillIndexEntry> _index;
        private static DateTime _indexLoadTime;
        private static readonly Dictionary<string, Dictionary<string, object>> _skillDetailCache = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        private static List<SkillIndexEntry> LoadIndex()
        {
            if (_index != null && (DateTime.Now - _indexLoadTime).TotalMinutes < 10)
                return _index;

            _index = new List<SkillIndexEntry>();
            try
            {
                if (!Directory.Exists(SkillsDir)) return _index;
                foreach (var dir in Directory.GetDirectories(SkillsDir))
                {
                    var idxFile = Path.Combine(dir, "index.json");
                    if (!File.Exists(idxFile)) continue;
                    var text = File.ReadAllText(idxFile);
                    var items = _json.Deserialize<List<Dictionary<string, object>>>(text);
                    if (items == null) continue;
                    foreach (var item in items)
                        _index.Add(new SkillIndexEntry
                        {
                            Name = GetStr(item, "name") ?? "",
                            Type = GetStr(item, "type") ?? "",
                            Desc = GetStr(item, "desc") ?? "",
                            Start = GetInt(item, "start"),
                            End = GetInt(item, "end"),
                            Dir = dir
                        });
                }
                _indexLoadTime = DateTime.Now;
            }
            catch { }
            return _index;
        }

        private static Dictionary<string, object> LoadFullEntry(SkillIndexEntry entry)
        {
            if (entry == null) return null;
            Dictionary<string, object> cached;
            if (_skillDetailCache.TryGetValue(entry.Name, out cached))
                return cached;

            var file = Path.Combine(entry.Dir, "skills.json");
            if (!File.Exists(file)) return null;
            try
            {
                // Read only the lines belonging to this entry, then parse that block.
                // Block spans lines [Start, End] inclusive. The closing '  }' line may
                // have a trailing comma ('  },'); strip it so wrapping in [ ] yields valid JSON.
                var lines = File.ReadLines(file);
                var block = new List<string>(entry.End - entry.Start + 3);
                int i = 0;
                foreach (var raw in lines)
                {
                    i++;
                    if (i < entry.Start) continue;
                    if (i > entry.End) break;
                    var ln = raw;
                    if (i == entry.End)
                    {
                        var trimmed = ln.TrimEnd();
                        if (trimmed.EndsWith(",")) ln = ln.Substring(0, ln.LastIndexOf(','));
                    }
                    block.Add(ln);
                }
                var json = "[" + string.Join(Environment.NewLine, block) + "]";
                var items = _json.Deserialize<List<Dictionary<string, object>>>(json);
                if (items != null && items.Count > 0)
                {
                    _skillDetailCache[entry.Name] = items[0];
                    return items[0];
                }
            }
            catch { }
            return null;
        }

        private static SkillIndexEntry FindSkillEntry(string commandName)
        {
            var index = LoadIndex();
            var q = commandName.ToUpperInvariant();
            foreach (var e in index)
            {
                if (e.Name.ToUpperInvariant() == q)
                    return e;
            }
            return null;
        }

        private static object LookupCommand(string query)
        {
            if (string.IsNullOrEmpty(query))
                return new { error = "query is required" };

            var index = LoadIndex();
            if (index.Count == 0)
                return new { error = "No skill data found. Place index.json + skills.json in subfolders of " + SkillsDir };

            var q = query.ToUpperInvariant().Trim();

            // Phase 1: search lightweight index
            var matched = new List<SkillIndexEntry>();

            // Exact match
            foreach (var e in index)
            {
                if (e.Name.ToUpperInvariant() == q) { matched.Add(e); break; }
            }

            // Partial name match
            if (matched.Count == 0)
            {
                foreach (var e in index)
                {
                    if (e.Name.ToUpperInvariant().Contains(q)) matched.Add(e);
                    if (matched.Count >= 5) break;
                }
            }

            // Description match
            if (matched.Count == 0)
            {
                foreach (var e in index)
                {
                    if (e.Desc != null && e.Desc.ToUpperInvariant().Contains(q)) matched.Add(e);
                    if (matched.Count >= 3) break;
                }
            }

            if (matched.Count == 0)
                return new { error = $"No matching command found for '{query}'" };

            // Phase 2: load full details for matched entries
            var results = new List<object>();
            foreach (var m in matched)
            {
                var full = LoadFullEntry(m);
                if (full != null) results.Add(full);
            }

            if (results.Count == 0)
                return new { error = $"Index matched but full data not found for '{query}'" };

            return new { results };
        }

        // ---- Tool Definitions ----

        private static List<Dictionary<string, object>> GetToolDefinitions()
        {
            return new List<Dictionary<string, object>>
            {
                Tool("get_status", "Get controller connection status, product name, firmware version, project name", NoParams()),
                Tool("list_programs", "List all programs in the current MotionPerfect project", NoParams()),
                Tool("read_source", "Read source code of a program", Props("name", "Program name")),
                Tool("write_source", "Write full source code to a program (auto-backup, requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("sourceCode", "Full source code to write", false)
                )),
                Tool("patch_source", "Apply line-level edits to a program (auto-backup, requires confirmation)", PropsMixed(
                    ("name", "Program name", false, "string"),
                    ("operations", "Array of {action:replace|insert|delete, line:number, content:string}", false, "array")
                )),
                Tool("read_vr", "Read VR variable values from controller", Props(
                    ("address", "Starting VR address (0-based)", false),
                    ("count", "Number of values to read", true)
                )),
                Tool("write_vr", "Write a value to a VR variable (requires confirmation)", Props(
                    ("address", "VR address", false),
                    ("value", "Value to write", false)
                )),
                Tool("read_table", "Read TABLE values from controller", Props(
                    ("address", "Starting TABLE address", false),
                    ("count", "Number of values to read", true)
                )),
                Tool("write_table", "Write values to TABLE (requires confirmation)", PropsMixed(
                    ("address", "Starting TABLE address", false, "string"),
                    ("values", "Array of values to write", false, "array")
                )),
                Tool("list_axes", "List all configured axes on the controller", NoParams()),
                Tool("list_descriptors", "List available program type descriptors", NoParams()),
                Tool("create_program", "Create a new program in the project (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("type", "Program type: basic, text, etc.", true),
                    ("sourceCode", "Initial source code", true)
                )),
                Tool("delete_program", "Delete a program from the project (requires confirmation)", Props("name", "Program name")),
                Tool("compile_program", "Compile a program on the controller", Props("name", "Program name")),
                Tool("run_program", "Run a program on the controller (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("process", "Process number", true)
                )),
                Tool("stop_program", "Stop a running program (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("process", "Process number", true)
                )),
                Tool("upload", "Upload a program to the controller (requires confirmation)", Props("name", "Program name")),
                Tool("download", "Download a program from the controller", Props("name", "Program name")),
                Tool("save_project", "Save the current project", NoParams()),
                Tool("open_program", "Open a program in the editor. Call this before write_source or patch_source if the program is not already open.", Props("name", "Program name")),
                Tool("get_program_process", "Get process/auto-run settings for a program (isAutorun, autorunProcess, processAffinity)", Props("name", "Program name")),
                Tool("set_program_process", "Set process/auto-run settings for a program (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("isAutorun", "Whether program auto-runs on controller startup (true/false)", true),
                    ("autorunProcess", "Process number for auto-run", true),
                    ("processAffinity", "Process affinity (process slot to run on)", true)
                )),
                Tool("lookup_command", "Look up TrioBASIC command/keyword reference from the official manual.", Props(
                    ("query", "Command name or keyword to search (e.g. MOVE, CONNECT, ACCEL, FOR)", false)
                )),
                Tool("search_code", "Search for a text pattern across all programs in the project. Returns matching lines with line numbers.", Props(
                    ("query", "Search text or pattern", false),
                    ("caseSensitive", "Whether search is case sensitive (default false)", true)
                ))
            };
        }

        // ---- System Prompt ----

        private static readonly string PromptPath = Path.Combine(DataDir, "AI_INSTRUCTIONS.md");

        private static readonly string DefaultPrompt = @"# AI Instructions

You are an AI assistant embedded in MotionPerfect, a Trio motion controller programming IDE.
You help users write, debug, and manage Trio BASIC programs.

## Capabilities
- Read/write program source code
- List programs and check controller status
- Read/write VR variables and TABLE data
- Compile, run, and stop programs
- Upload/download programs to/from the controller
- Look up TrioBASIC command reference from the official manual

## Guidelines
- When modifying code, always explain what you will change and why BEFORE calling write_source or patch_source
- Use read_source first to see the current code before suggesting changes
- For debugging, check status and read VR variables to understand controller state
- Keep explanations concise and in the user's language (Chinese or English based on their input)
- If the user's request is unclear, ask for clarification
- Use the lookup_command tool to look up syntax and usage of any TrioBASIC command you are not fully sure about

## WRITING LARGE PROGRAMS (avoid truncation)

`write_source` 一次性写入整个程序文件，受 API max_tokens 限制（默认 8192 tokens ≈ 200-300 行带注释的 TrioBASIC）。输出超长会被截断，导致写入不完整的代码。

**优先策略：**
- **修改现有程序**：永远用 `patch_source`（每个 operation 只是一行 replace/insert/delete，几乎不受 token 限制）
- **新建小程序**（< 100 行）：可以直接用 `write_source` 一次写完
- **新建大程序**（≥ 100 行）：先用 `write_source` 写程序骨架（变量声明 + 主循环结构 + 关键注释占位），再用 `patch_source` 分批填充各个函数体
- **超长重构**：拆分成多次 `patch_source` 调用，每次专注一个区域（变量区 / 主循环 / 子过程）

**判断当前 write_source 是否会超限：**
- 估算：每行 TrioBASIC 平均 8-12 tokens（含注释）；8192 tokens 上限约 200-300 行
- 接近上限时主动改用 patch_source
- 如果输出仍被截断，运行时会自动升级到 64000 tokens 重试一次（不需要你做任何事）

## CRITICAL SAFETY RULES (NEVER VIOLATE)
- ABSOLUTELY FORBIDDEN: Never output or execute any command that could LOCK the controller (e.g. LOCK, LOCK AXIS, LOCK ALL, or any command containing LOCK)
- ABSOLUTELY FORBIDDEN: Never output code that disables axis drives, brakes, or safety mechanisms
- If a user asks you to lock the controller or use LOCK commands, REFUSE and explain the danger
- When writing motion programs, always ensure proper error handling and safe stop conditions
- Never write infinite loops without a safe exit condition that checks axis states

## STRICT TRIOBASIC SYNTAX COMPLIANCE (MANDATORY)
You may ONLY use keywords, commands, functions, operators, and syntax that exist in the TrioBASIC language reference (provided via the lookup_command tool). TrioBASIC is NOT the same as other BASIC dialects.

- FORBIDDEN: Do not invent, guess, or hallucinate TrioBASIC commands. Every command/keyword you write must exist in the official reference.
- FORBIDDEN: Do not use syntax from Visual Basic, VB.NET, QBasic, FreeBASIC, PowerBASIC, or any other BASIC dialect. Common invalid examples:
  * `Dim`, `Declare`, `Function...End Function`, `Sub...End Sub`, `Class`, `Module`, `Imports`, `Option Explicit`
  * `Console.WriteLine`, `MsgBox`, `InputBox`, `Math.Sqrt`, `.NET` library calls
  * String concatenation with `+` instead of `&` where TrioBASIC requires it
  * `Integer`, `String`, `Boolean`, `Double` type declarations (TrioBASIC uses no As-clause typing)
  * `Try...Catch`, `Throw`, `Using`, `Yield`, `Await`, `Async`
- MANDATORY: Before writing ANY code that uses a command or syntax you are not 100% certain about, call lookup_command to verify it exists and matches the official syntax. This includes motion commands, axis parameters, system parameters, mathematical functions, and string functions.
- MANDATORY: If the user's request cannot be fulfilled with valid TrioBASIC, do NOT approximate or substitute with made-up commands. Instead, explain what TrioBASIC supports and propose an alternative using only verified commands.
- When in doubt, ALWAYS verify with lookup_command BEFORE writing code. The cost of one extra tool call is far less than the cost of generating code that fails to compile.

## STRICT NAMING RULES (MANDATORY)
TrioBASIC reserves system variables (e.g. `VR`, `TABLE`, `AXIS`, `OP`, `DP`, `DPOS`, `MPOS`, `SERVO`, `WDOG`, `BASE`, `SPEED`, `ACCEL`, `DECEL`, `CREEP`, `FE_LIMIT`, `SERIAL`, `IN`, `OUT`, `RUN`, `CONNECT`, `RAPID`, `MOVE`, `HOME`, `CAM`, `DATUM`, `PRINT`, `FOR`, `NEXT`, `IF`, `THEN`, `ELSE`, `ENDIF`, `WHILE`, `WEND`, `REPEAT`, `UNTIL`, `GOTO`, `GOSUB`, `RETURN`, `GLOBAL`, `LOCAL`, `DIM`, `INTEGER`, `FLOAT`, `STRING`) and all built-in function names. These names are **case-insensitive reserved identifiers** — TrioBASIC treats `MOVE`, `move`, `Move` as the same identifier.

- FORBIDDEN: Never declare a user variable, label, or subroutine whose name matches any system variable or built-in function name — NOT EVEN WITH DIFFERENT CASE. `move = 1`, `Move = 1`, `vr_count = 0` (if `VR_COUNT` is reserved), `for_x = 5` (if `FOR_X` is reserved) are all forbidden. TrioBASIC is case-insensitive, so `MyMove`, `MYMOVE`, `mymove` collide equally.
- MANDATORY: Before using ANY identifier as a variable name, verify it is NOT in the reserved list above. If you are not 100% certain whether a name is reserved, call `lookup_command` with the candidate name — if a command/keyword/system-variable matches (case-insensitively), the name is reserved and you MUST pick a different identifier.
- Use prefixes like `my_`, `usr_`, `g_`, or domain-specific nouns (`step_count`, `axis_done`, `cycle_index`) to avoid colliding with reserved identifiers.
- Reserved names also include any motion-command name (`MOVE`, `MOVECIRC`, `MOVEMODIFY`, `MFAST`, `MSYNC`, `CONNECT`, `CANCEL`, `RAPID`, `HOME`, `DATUM`, `CAM`, `CAMBOX`, `GEAR`, `STOP`, `FORWARD`, `REVERSE`), I/O keywords (`IN`, `OUT`, `OP`, `PSWITCH`, `COMPARE`), and all built-in functions (`SIN`, `COS`, `ABS`, `INT`, `MAX`, `MIN`, `SQRT`, `RAND`, `BIT`, `LEN`, `INSTR`, `MID`, `LEFT`, `RIGHT`, `VAL`, `STR`, etc.).

## Typical TrioBASIC vs other-BASIC confusions to avoid
- Use `IF ... THEN ... ELSE ... ENDIF` (one word `ENDIF`), not `End If`
- Use `FOR ... NEXT`, `WHILE ... WEND`, `DO ... LOOP UNTIL/WHILE` per TrioBASIC spec
- Use `PRINT` for output, not `Console.WriteLine` or `Debug.Print`
- Use `=` for assignment AND equality test (no `==`)
- Variable typing is implicit (no `Dim x As Integer`); just `x = 0`
- Comments use single-quote `'`, not `REM` (unless verified)
- Hex literals use `&H...` per TrioBASIC reference — verify before use
";

        private static string BuildSystemPrompt()
        {
            try
            {
                var prompt = File.Exists(PromptPath) ? File.ReadAllText(PromptPath) : DefaultPrompt;
                var context = BuildProjectContext();
                var lang = System.Threading.Thread.CurrentThread.CurrentUICulture.Name;
                var langInstruction = GetLanguageInstruction(lang);
                return prompt + "\n\n" + context + "\n\n" + langInstruction;
            }
            catch { }
            return DefaultPrompt;
        }

        private static string BuildProjectContext()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("## Current Environment");
                DispatcherHelper.Invoke(() =>
                {
                    var mw = Trio.SharedLibrary.MPSingletons.MainWindow;
                    var ctrl = Trio.SharedLibrary.MPSingletons.Controller;
                    var proj = mw?.Project;

                    // Connection
                    if (ctrl != null && ctrl.IsConnected)
                    {
                        sb.AppendFormat("- Controller: {0} ({1}), FW: {2}\n",
                            ctrl.ProductName ?? "Unknown",
                            ctrl.SerialNumber ?? "?",
                            ctrl.FullVersionString ?? "?");
                    }
                    else
                    {
                        sb.AppendLine("- Controller: Not connected");
                    }

                    // Project
                    if (proj != null && proj.IsProject)
                    {
                        sb.AppendFormat("- Project: {0}\n", proj.FileName ?? "?");

                        // Program list summary
                        var items = proj.Items;
                        if (items != null)
                        {
                            var itemList = items.ToList();
                            if (itemList.Count > 0)
                            {
                                sb.AppendFormat("- Programs ({0}):", itemList.Count);
                                foreach (var item in itemList)
                                {
                                    sb.AppendFormat(" {0}({1})", item.ItemName, item.Type);
                                }
                                sb.AppendLine();
                            }
                        }
                    }
                    else
                    {
                        sb.AppendLine("- Project: No project loaded");
                    }

                    // Pending compile error
                    if (_lastCompileError != null)
                    {
                        sb.AppendFormat("- Last compile error: {0}\n", _lastCompileError);
                    }
                });
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string GetLanguageInstruction(string cultureName)
        {
            if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Chinese (中文). All explanations, code comments, and interactions should be in Chinese.";
            if (cultureName.StartsWith("de", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in German (Deutsch). Alle Erklaerungen und Interaktionen auf Deutsch.";
            if (cultureName.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in French (Francais). Toutes les explications et interactions en francais.";
            if (cultureName.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Russian. Все объяснения и взаимодействия на русском языке.";
            if (cultureName.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Spanish. Todas las explicaciones e interacciones en espanol.";
            if (cultureName.StartsWith("it", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Italian. Tutte le spiegazioni e interazioni in italiano.";
            if (cultureName.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Portuguese. Todas as explicacoes e interacoes em portugues.";
            if (cultureName.StartsWith("hu", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Hungarian. Minden magyarazat es interakcio magyarul.";
            if (cultureName.StartsWith("ro", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Romanian. Toate explicatiile si interactiunile in romana.";
            if (cultureName.StartsWith("sv", StringComparison.OrdinalIgnoreCase))
                return "IMPORTANT: You MUST respond in Swedish. Alla forklaringar och interaktioner pa svenska.";
            return "IMPORTANT: You MUST respond in English.";
        }

        // ---- JSON Helpers ----

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, string[] required = null)
        {
            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties }
            };
            if (required != null && required.Length > 0)
                schema["required"] = required;

            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "input_schema", schema }
            };
        }

        private static Dictionary<string, object> NoParams() => new Dictionary<string, object>();

        private static Dictionary<string, object> Props(string name, string desc)
        {
            return new Dictionary<string, object>
            {
                { name, new { type = "string", description = desc } }
            };
        }

        private static Dictionary<string, object> Props(params (string name, string desc, bool optional)[] props)
        {
            var dict = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var (n, d, opt) in props)
            {
                dict[n] = new { type = "string", description = d };
                if (!opt) required.Add(n);
            }
            return dict;
        }

        // 支持 array / 其他类型字段的 schema 构造
        private static Dictionary<string, object> PropsMixed(params (string name, string desc, bool optional, string type)[] props)
        {
            var dict = new Dictionary<string, object>();
            foreach (var (n, d, opt, t) in props)
                dict[n] = new { type = string.IsNullOrEmpty(t) ? "string" : t, description = d };
            return dict;
        }

        private static string SerializeRequest(Dictionary<string, object> body)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in body)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(EscapeJson(kv.Key)).Append('"').Append(':');
                sb.Append(SerializeValue(kv.Value));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string SerializeValue(object val)
        {
            if (val == null) return "null";
            if (val is string s) return "\"" + EscapeJson(s) + "\"";
            if (val is bool b) return b ? "true" : "false";
            if (val is int || val is long || val is double || val is float || val is decimal)
                return val.ToString();
            if (val is Dictionary<string, object> dict)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(EscapeJson(kv.Key)).Append('"').Append(':');
                    sb.Append(SerializeValue(kv.Value));
                }
                sb.Append('}');
                return sb.ToString();
            }
            if (val is IList<object> list)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(SerializeValue(item));
                }
                sb.Append(']');
                return sb.ToString();
            }
            return _json.Serialize(val);
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string GetStringValue(Dictionary<string, object> dict, string key)
        {
            object val;
            return dict.TryGetValue(key, out val) ? val?.ToString() : null;
        }

        private static Dictionary<string, object> GetDictValue(Dictionary<string, object> dict, string key)
        {
            object val;
            if (dict.TryGetValue(key, out val) && val is Dictionary<string, object> d) return d;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                try { return _json.Deserialize<Dictionary<string, object>>(_json.Serialize(val)); } catch { }
            }
            return null;
        }

        private static List<Dictionary<string, object>> GetContentBlocks(Dictionary<string, object> response)
        {
            object val;
            if (!response.TryGetValue("content", out val)) return new List<Dictionary<string, object>>();
            if (val is List<Dictionary<string, object>> list) return list;
            if (val is System.Collections.ArrayList al)
            {
                var result = new List<Dictionary<string, object>>();
                foreach (var item in al)
                {
                    if (item is Dictionary<string, object> d)
                        result.Add(d);
                    else
                    {
                        try { result.Add(_json.Deserialize<Dictionary<string, object>>(_json.Serialize(item))); } catch { }
                    }
                }
                return result;
            }
            return new List<Dictionary<string, object>>();
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            object val;
            return d.TryGetValue(key, out val) && val != null ? val.ToString() : null;
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result)) return result;
            }
            return 0;
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
                return val.ToString().ToLowerInvariant() == "true";
            return false;
        }
    }
}
