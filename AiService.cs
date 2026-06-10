using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
        private static bool _includeSkillImages = false;
        private readonly List<Dictionary<string, object>> _conversationHistory = new List<Dictionary<string, object>>();
        private string _currentSessionId;
        private static string _lastCompileError;

        private const int MaxHistoryKeep = 30;
        private const int MaxRecentKeep = 20;
        private const int MaxToolResultLen = 16000;
        // microCompact：旧 tool_result 的 content 替换为占位符，保留 tool_use_id 不破坏配对
        private const int MaxRecentToolResults = 5;
        private const string ClearedToolResult = "[Old tool result content cleared]";
        // 按 token 估算触发裁剪（chars/4），而非按消息条数 — 30 条纯对话仅 5k token，但 5 个 lookup 就 20k+
        private const int HistoryTokenBudget = 30000;
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
                    if (cfg.TryGetValue("includeSkillImages", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _includeSkillImages = b;
                    }
                }
            }
            catch { }
        }

        public void SaveConfig(string apiKey, string model, string apiUrl, bool? showToolStatus = null, bool? includeSkillImages = null)
        {
            _apiKey = apiKey;
            if (!string.IsNullOrEmpty(model)) _model = model;
            if (!string.IsNullOrEmpty(apiUrl)) _apiUrl = apiUrl;
            if (showToolStatus.HasValue) _showToolStatus = showToolStatus.Value;
            if (includeSkillImages.HasValue && includeSkillImages.Value != _includeSkillImages)
            {
                _includeSkillImages = includeSkillImages.Value;
                _skillDetailCache.Clear(); // img stripping is cached per page; force re-read
            }
            var json = _json.Serialize(new { apiKey = _apiKey, model = _model, apiUrl = _apiUrl, showToolStatus = _showToolStatus, skillsInitialized = _skillsInitialized, includeSkillImages = _includeSkillImages });
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
        public bool IncludeSkillImages => _includeSkillImages;

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
            var json = _json.Serialize(new { apiKey = _apiKey ?? "", model = _model ?? "", apiUrl = _apiUrl ?? "", showToolStatus = _showToolStatus, skillsInitialized = true, includeSkillImages = _includeSkillImages });
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
            // token 估算（chars/4）超过预算才裁剪；条数兜底（防 token 估算漏判）
            if (EstimateHistoryTokens() < HistoryTokenBudget
                && _conversationHistory.Count <= MaxHistoryKeep)
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

        // 粗略估算历史 token：chars/4。仅用于裁剪触发判断，不要求精确。
        private int EstimateHistoryTokens()
        {
            int chars = 0;
            foreach (var m in _conversationHistory)
            {
                if (!m.ContainsKey("content")) continue;
                var content = m["content"];
                if (content is string s)
                {
                    chars += s.Length;
                }
                else if (content is List<Dictionary<string, object>> blocks)
                {
                    foreach (var b in blocks)
                    {
                        var c = GetStringValue(b, "content");
                        if (c != null) chars += c.Length;
                        var t = GetStringValue(b, "text");
                        if (t != null) chars += t.Length;
                    }
                }
            }
            return chars / 4;
        }

        private List<Dictionary<string, object>> BuildTrimmedMessages()
        {
            var messages = new List<Dictionary<string, object>>(_conversationHistory.Count);

            // 先建立 tool_use_id → tool_name 映射（来自 assistant 消息的 tool_use block）
            // 同时为 lookup_command 建立 tool_use_id → query 映射，用于去重。
            var toolNameById = new Dictionary<string, string>(StringComparer.Ordinal);
            var lookupQueryById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") == "tool_use")
                    {
                        var id = GetStringValue(b, "id");
                        var name = GetStringValue(b, "name");
                        if (id != null && name != null) toolNameById[id] = name;

                        if (string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase)
                            && id != null
                            && b.TryGetValue("input", out var inObj)
                            && inObj is Dictionary<string, object> inDict)
                        {
                            object q;
                            if (inDict.TryGetValue("query", out q) && q != null)
                                lookupQueryById[id] = q.ToString();
                        }
                    }
                }
            }

            // lookup_command 去重：同一 query 第一次出现保留完整内容，后续替换为引用占位符。
            // tool_result 是按 conversation 顺序遍历，遇到第二次同 query 就标记。
            var seenLookupQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateLookupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "user") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") != "tool_result") continue;
                    var id = GetStringValue(b, "tool_use_id");
                    if (id == null) continue;
                    if (!lookupQueryById.TryGetValue(id, out var query)) continue;
                    // 第一次见到这个 query：保留。后续：标记为重复。
                    if (!seenLookupQueries.Add(query))
                        duplicateLookupIds.Add(id);
                }
            }

            // microCompact：扫描全部 user 消息里的 tool_result，按出现顺序收集 tool_use_id。
            // 保留最后 N 个完整内容，更早的 content 清空（保留 tool_use_id 防止 tool_use/tool_result 配对断裂）。
            // 工具结果（HTML 帮助页、文件内容）是请求 token 的大头，旧的清空能省 30%+。
            var allToolResultIds = new List<string>();
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "user") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") == "tool_result")
                    {
                        var id = GetStringValue(b, "tool_use_id");
                        if (id != null) allToolResultIds.Add(id);
                    }
                }
            }
            // 最近 N 个 ∪ lookup_command 的所有结果（参考类命令文档，AI 写代码时会反复引用，
            // 清空会导致它再查一次浪费 API + 中断代码连贯性）
            var keepRecent = new HashSet<string>(
                allToolResultIds.Skip(Math.Max(0, allToolResultIds.Count - MaxRecentToolResults)));
            foreach (var id in allToolResultIds)
            {
                if (toolNameById.TryGetValue(id, out var name)
                    && string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase))
                {
                    keepRecent.Add(id);
                }
            }

            // 找到历史里最后一条 assistant 消息的下标，给它打 cache_control。
            // 这样下一轮请求时，前缀 [system + tools + history up to last assistant] 命中缓存。
            int lastAssistantIdx = -1;
            for (int i = _conversationHistory.Count - 1; i >= 0; i--)
            {
                if (GetStringValue(_conversationHistory[i], "role") == "assistant")
                {
                    lastAssistantIdx = i;
                    break;
                }
            }

            for (int idx = 0; idx < _conversationHistory.Count; idx++)
            {
                var msg = _conversationHistory[idx];
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
                            var id = GetStringValue(tb, "tool_use_id");
                            // lookup_command 去重：同 query 已答过 → 内容替换为引用占位符（~80 字节 vs 16KB）
                            if (id != null && duplicateLookupIds.Contains(id))
                            {
                                var q = lookupQueryById[id];
                                tb["content"] = "[Duplicate of lookup_command(\"" + q
                                    + "\") — full content preserved at the first call earlier in this conversation. "
                                    + "Reference that occurrence instead of asking again.]";
                            }
                            // microCompact：不在最近 N 个里 → content 替换为占位符（id 保留）
                            else if (id != null && !keepRecent.Contains(id))
                            {
                                tb["content"] = ClearedToolResult;
                            }
                            else
                            {
                                var c = GetStringValue(tb, "content");
                                if (c != null && c.Length > MaxToolResultLen)
                                    tb["content"] = SmartTruncate(c, MaxToolResultLen);
                            }
                            trimmedBlocks.Add(tb);
                        }
                        else
                        {
                            trimmedBlocks.Add(block);
                        }
                    }
                    copy["content"] = trimmedBlocks;
                }

                // 给最后一条 assistant 消息的最后一个 content block 打 cache_control
                if (idx == lastAssistantIdx && content is List<Dictionary<string, object>> asstBlocks && asstBlocks.Count > 0)
                {
                    var newBlocks = new List<Dictionary<string, object>>(asstBlocks.Count);
                    for (int j = 0; j < asstBlocks.Count; j++)
                    {
                        if (j == asstBlocks.Count - 1)
                        {
                            var lastBlock = new Dictionary<string, object>(asstBlocks[j])
                            {
                                { "cache_control", new { type = "ephemeral" } }
                            };
                            newBlocks.Add(lastBlock);
                        }
                        else
                        {
                            newBlocks.Add(asstBlocks[j]);
                        }
                    }
                    copy["content"] = newBlocks;
                }

                messages.Add(copy);
            }
            return messages;
        }

        // 智能截断：优先在 HTML heading / paragraph / table 边界处切，避免把语法表或参数说明截成半句。
        // 比纯字符 cut 慢一点但只对超长 tool_result 触发（lookup_command 帮助页为主）。
        private static readonly string[] _truncateBoundaries =
            { "</h2>", "</h3>", "</h4>", "</h5>", "</table>", "</ul>", "</ol>", "</p>" };

        private static string SmartTruncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            int searchStart = (int)(maxLen * 0.7);
            int best = -1;
            foreach (var b in _truncateBoundaries)
            {
                int idx = s.LastIndexOf(b, maxLen, StringComparison.OrdinalIgnoreCase);
                if (idx > searchStart && idx + b.Length > best)
                    best = idx + b.Length;
            }
            int cut = best > 0 ? best : maxLen;
            return s.Substring(0, cut) + "\n...[truncated " + cut + "/" + s.Length + " chars]";
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
            "set_program_process", "write_iec_variables"
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
                case "read_source": return Handlers.ReadSource(GetStr(input, "name"), input);
                case "get_iec_task_detail": return Handlers.GetIecTaskDetail(GetStr(input, "name"));
                case "read_iec_variables": return Handlers.ReadIecVariables(GetStr(input, "name"), GetStr(input, "scope"));
                case "write_iec_variables": return Handlers.WriteIecVariables(GetStr(input, "name"), GetStr(input, "scope"), GetStr(input, "text"));
                case "write_source":
                {
                    var code = GetStr(input, "sourceCode") ?? "";
                    var errs = ValidateTrioBasicCode(code);
                    if (errs.Count > 0)
                        return new { error = "BLOCKED by TrioBASIC validation:\n  " + string.Join("\n  ", errs) };
                    return Handlers.WriteSource(GetStr(input, "name"), input);
                }
                case "patch_source":
                {
                    // patch_source 的 operations 列表里每个 op 的 content 字段都要校验
                    var errs = new List<string>();
                    if (input.TryGetValue("operations", out var opsObj) && opsObj is List<object> ops)
                    {
                        foreach (var op in ops)
                        {
                            var dict = op as Dictionary<string, object>;
                            if (dict == null) continue;
                            if (dict.TryGetValue("content", out var v) && v is string s && !string.IsNullOrEmpty(s))
                                errs.AddRange(ValidateTrioBasicCode(s));
                        }
                    }
                    if (errs.Count > 0)
                        return new { error = "BLOCKED by TrioBASIC validation:\n  " + string.Join("\n  ", errs) };
                    return Handlers.PatchSource(GetStr(input, "name"), input);
                }
                case "read_vr": return Handlers.ReadVR(GetInt(input, "address"), GetInt(input, "count"));
                case "write_vr": return Handlers.WriteVR(GetInt(input, "address"), input);
                case "read_table": return Handlers.ReadTable(GetInt(input, "address"), GetInt(input, "count"));
                case "write_table": return Handlers.WriteTable(GetInt(input, "address"), input);
                case "list_axes": return Handlers.ListAxes();
                case "get_axis_detail": return Handlers.GetAxisDetail(GetInt(input, "index"));
                case "copy_program": return Handlers.CopyProgram(GetStr(input, "name"), input);
                case "get_sysvars": return Handlers.GetSystemVariables();
                case "read_sysvar": return Handlers.ReadSysVar(GetStr(input, "name"));
                case "write_sysvar": return Handlers.WriteSysVar(GetStr(input, "name"), input);
                case "list_digital_io": return Handlers.ListDigitalIO();
                case "read_digital_io": return Handlers.ReadDigitalIO(GetInt(input, "index"));
                case "write_digital_io": return Handlers.WriteDigitalIO(GetInt(input, "index"), input);
                case "list_analogue_io": return Handlers.ListAnalogueIO();
                case "read_analogue_io": return Handlers.ReadAnalogueIO(GetInt(input, "index"));
                case "write_analogue_io": return Handlers.WriteAnalogueIO(GetInt(input, "index"), input);
                case "list_breakpoints": return Handlers.ListBreakpoints(GetStr(input, "name"));
                case "set_breakpoint": return Handlers.SetBreakpoint(GetStr(input, "name"), input);
                case "clear_all_breakpoints": return Handlers.ClearAllBreakpoints(GetStr(input, "name"));
                case "list_processes": return Handlers.ListProcesses();
                case "get_process_variable": return Handlers.GetProcessVariable(GetInt(input, "pid"), GetStr(input, "program"), GetStr(input, "variable"));
                case "get_events": return Handlers.GetEvents(GetLong(input, "since"));
                case "read_drive_param": return Handlers.ReadDriveParam(GetInt(input, "axis"), GetInt(input, "address"), GetInt(input, "nd", 4));
                case "write_drive_param": return Handlers.WriteDriveParam(GetInt(input, "axis"), GetInt(input, "address"), input);
                case "scan_ethercat": return Handlers.ScanEtherCAT(GetInt(input, "slot", 0));
                case "read_ethercat_sdo":
                    return Handlers.EtherCATReadSDO(GetInt(input, "slot", 0),
                                                    (uint)GetLong(input, "position"),
                                                    (uint)GetLong(input, "index"),
                                                    (uint)GetLong(input, "subindex"),
                                                    GetStr(input, "type") ?? "uint16");
                case "write_ethercat_sdo": return Handlers.EtherCATWriteSDOFromDict(input);
                case "scan_msbus": return Handlers.ScanMsBus(GetInt(input, "slot", 0));
                case "list_remote_devices": return Handlers.ListRemoteDevices();
                case "list_robots": return Handlers.ListRobots();
                case "list_recipes": return Handlers.ListRecipes();
                case "list_alarms": return Handlers.ListAlarms();
                case "list_plugins": return Handlers.ListAttachedPlugins();
                case "open_oscilloscope": return Handlers.OpenOscilloscope();
                case "open_project": return Handlers.OpenProject(input);
                case "list_project_items": return Handlers.ListProjectItems();
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
                case "read_skill": return ReadSkill(GetStr(input, "name"));
                case "search_code": return Handlers.SearchCode(GetStr(input, "query"), GetBool(input, "caseSensitive"));
                default: return new { error = $"Unknown tool: {name}" };
            }
        }

        // ---- Command Lookup (index for search, HTML file per entry for details) ----

        private class SkillIndexEntry
        {
            public string Name;
            public string Type;
            public string Desc;
            public string File;  // HTML file name within Dir
            public string Dir;
        }

        // Markdown skill: skills/general/<name>/SKILL.md (cc-haha style, name + description frontmatter)
        private class MdSkillEntry
        {
            public string Name;
            public string Description;
            public string Dir;
            public string Body;
        }

        private static List<SkillIndexEntry> _index;
        private static DateTime _indexLoadTime;
        private static readonly Dictionary<string, Dictionary<string, object>> _skillDetailCache = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        private static List<MdSkillEntry> _mdSkills;
        private static DateTime _mdSkillsLoadTime;
        private static readonly object _mdSkillLock = new object();

        // ============ TrioBASIC 代码校验（标识符白名单 + 签名语法）============
        // 双层防线：(1) 提取代码里所有"系统标识符候选"，凡是不在 TrioBASIC 索引/HTML
        // 文件名/关键字白名单里的，一律拒绝写入；(2) 解析 lookup_command 索引的 desc
        // 字段提取签名，校验调用点的参数数量 / 赋值目标合法性。
        // 设计取舍：不依赖小模型，纯 C# 静态扫描，零 API 开销，确定性 100%。

        private static HashSet<string> _triobasicIds;                       // 所有合法标识符（命令/函数/参数/关键字）
        private static Dictionary<string, CommandSignature> _signatures;    // 签名表（key 大写）
        private static volatile bool _validationIndexBuilt;
        private static readonly object _validationLock = new object();

        // 兜底关键字 — 控制流 / 类型 / 运算符，部分没有 HTML 文件
        private static readonly HashSet<string> _builtinKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if","then","else","elseif","endif","for","next","to","step","while","wend",
            "do","loop","until","repeat","gosub","return","goto","exit",
            "dim","const","global","local","integer","float","string","as",
            "print","input","and","or","not","mod","xor","shl","shr",
            "true","false","rem","on","select","case","end","using","with",
            "default",     // SELECT CASE DEFAULT
            "waits",       // 等待同步（区别于 WAIT UNTIL，无 HTML 单独条目）
            "until_io",    // WAIT UNTIL_IO 等
            // 常见用户变量前缀（避免假阳性）
            "i","j","k","x","y","z","t","n","cnt","idx","temp","tmp",
        };

        private class CommandSignature
        {
            public string Name;
            public int MinArgs = 0;
            public int? MaxArgs = null;     // null = 不限
            public bool ReturnsValue = false;
            public bool IsAssignable = false;
            public string RawSignature = "";
        }

        // desc 提取签名 — 三种模式：
        //   "value = NAME(args) ..."   函数返回值
        //   "NAME(args) = value ..."   可赋值函数（如 AIN_CONFIG）
        //   "NAME(args) ..."           命令/函数
        private static readonly Regex _reDescSigWithValue =
            new Regex(@"^\s*\w+\s*=\s*(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex _reDescSigCall =
            new Regex(@"^\s*(\w+)\s*\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex _reDescSigAssign =
            new Regex(@"^\s*(\w+)\s*\([^)]*\)\s*=", RegexOptions.Compiled);

        private static void EnsureValidationIndex()
        {
            if (_validationIndexBuilt) return;
            lock (_validationLock)
            {
                if (_validationIndexBuilt) return;
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sigs = new Dictionary<string, CommandSignature>(StringComparer.OrdinalIgnoreCase);

                // 1. 从 index.json 加载所有条目
                foreach (var entry in LoadIndex())
                {
                    if (string.IsNullOrEmpty(entry?.Name)) continue;
                    ids.Add(entry.Name);
                    var sig = ParseSignature(entry.Name, entry.Desc ?? "");
                    if (sig != null) sigs[entry.Name] = sig;
                }

                // 2. 扫描 skills/triobasic/ 下所有 .html 文件名（兜底 index.json 不全的情况，如关键字 IF/FOR/DIM）
                //    注意：仅扫 triobasic 目录。IEC/PLCopen 的库（AO-printf 之类）不能混进 TrioBASIC 白名单，
                //    否则 AI 写 printf() 这种 IEC 函数会被误判为合法 TrioBASIC。
                var triobasicDir = Path.Combine(SkillsDir, "triobasic");
                if (Directory.Exists(triobasicDir))
                {
                    foreach (var html in Directory.GetFiles(triobasicDir, "*.html"))
                    {
                        var name = Path.GetFileNameWithoutExtension(html);
                        // 仅纳入合法 identifier 名（字母数字下划线，>=2 字符）
                        if (name.Length >= 2 && IsIdentifierLike(name))
                            ids.Add(name);
                    }
                }

                _triobasicIds = ids;
                _signatures = sigs;
                _validationIndexBuilt = true;
            }
        }

        private static bool IsIdentifierLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_') return false;
            foreach (var c in s)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        private static CommandSignature ParseSignature(string name, string desc)
        {
            var sig = new CommandSignature { Name = name };
            if (string.IsNullOrWhiteSpace(desc)) return sig;

            // regex 都用 ^ 锚定开头，直接对原 desc 调用即可。
            // 之前用 split('.') 取第一句反而把 `ANYBUS(..., parameters...)` 里的 `...` 误切断。

            // Pattern 1: value = NAME(args)
            var m = _reDescSigWithValue.Match(desc);
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                sig.ReturnsValue = true;
                SetArgCount(sig, m.Groups[2].Value);
                sig.RawSignature = ("value = " + name + "(" + m.Groups[2].Value + ")").Trim();
                return sig;
            }

            // Pattern 2: NAME(args) = value
            m = _reDescSigAssign.Match(desc);
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                sig.IsAssignable = true;
                var m2 = _reDescSigCall.Match(desc);
                if (m2.Success) SetArgCount(sig, m2.Groups[2].Value);
                sig.RawSignature = (name + "(" + (m2?.Groups[2].Value ?? "") + ") = value").Trim();
                return sig;
            }

            // Pattern 3: NAME(args)
            m = _reDescSigCall.Match(desc);
            if (m.Success && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                SetArgCount(sig, m.Groups[2].Value);
                sig.RawSignature = (name + "(" + m.Groups[2].Value + ")").Trim();
                return sig;
            }

            return sig;
        }

        private static void SetArgCount(CommandSignature sig, string argsStr)
        {
            if (string.IsNullOrWhiteSpace(argsStr))
            {
                sig.MinArgs = 0;
                sig.MaxArgs = 0;
                return;
            }
            // TrioBASIC 文档的可选参数形式："axis0[, axis1[, axis2[, ...]]]"
            // 第一个 '[' 之前的是必填，之后的全部可选。
            int bracketIdx = argsStr.IndexOf('[');
            string requiredPart = bracketIdx >= 0 ? argsStr.Substring(0, bracketIdx) : argsStr;
            bool hasOptional = bracketIdx >= 0 || argsStr.Contains("...");

            var parts = requiredPart.Split(',');
            int required = 0;
            foreach (var p in parts)
            {
                var t = p.Trim().TrimEnd('[');
                if (string.IsNullOrEmpty(t)) continue;
                if (t == "..." || t == "…") continue;
                required++;
            }
            sig.MinArgs = required;
            sig.MaxArgs = hasOptional ? (int?)null : required;
        }

        // 已知「只读」函数 — 不能作为赋值左侧。系统变量（VR/TABLE/MPOS/DPOS 等）默认双向，不在本表。
        private static readonly HashSet<string> _knownReadOnly =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ABS","SIN","COS","TAN","ASIN","ACOS","ATAN","ATAN2",
                "SQRT","SQR","EXP","LOG","LN","POW","POWER",
                "INT","FLOAT","FRAC","ROUND","FIX","SIGN",
                "MAX","MIN","MOD",
                "RND","RANDOM",
                "INSTR","LEFT","RIGHT","MID","LEN","UPPER","LOWER","CHR","ASC","VAL","STR",
                "GET_PARM","AXIS_STATE","GET_VR_SCALE","GET_TABLE_SCALE",
            };

        // 校验代码 — 返回错误列表（空 = 通过）
        private static readonly Regex _reLineComment =
            new Regex(@"'[^*\r\n]*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _reAllCapsIdentifier =
            new Regex(@"\b[A-Z][A-Z_0-9]{1,}\b", RegexOptions.Compiled);
        private static readonly Regex _reFuncCallSite =
            new Regex(@"\b([A-Za-z_][A-Za-z_0-9]*)\s*\(([^)]*)\)", RegexOptions.Compiled);

        public static object ValidateTrioBasicCodePublic(string code)
        {
            var errs = ValidateTrioBasicCode(code);
            return new { ok = errs.Count == 0, errors = errs };
        }

        private static List<string> ValidateTrioBasicCode(string code)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(code)) return errors;
            try { EnsureValidationIndex(); }
            catch { return errors; }  // 索引构建失败 → 跳过校验，让工具正常执行

            // 去注释
            var clean = _reLineComment.Replace(code, "");

            // ---- Phase 1: 标识符白名单校验 ----
            // 提取所有全大写连续 token（系统标识符风格），凡是不在白名单的 → 可疑
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _reAllCapsIdentifier.Matches(clean))
            {
                var name = m.Value;
                if (_builtinKeywords.Contains(name)) continue;
                if (_triobasicIds != null && _triobasicIds.Contains(name)) continue;
                unknown.Add(name);
            }
            if (unknown.Count > 0)
            {
                errors.Add("Identifiers not in TrioBASIC reference: " +
                    string.Join(", ", unknown.OrderBy(x => x)));
                errors.Add("  → Call lookup_command for each, or replace with verified commands.");
            }

            // ---- Phase 2: 调用签名校验 ----
            // TrioBASIC 不支持用户自定义函数：任何 Name(args) 形式都是系统命令/函数/数组。
            // 若 Name 不在 _triobasicIds（含 VR/TABLE/ABS/MOVE... 全部命令）→ 必为幻觉。
            var lines = clean.Split('\n');
            var seen = new HashSet<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (Match m in _reFuncCallSite.Matches(line))
                {
                    var funcName = m.Groups[1].Value;
                    var argsStr = m.Groups[2].Value;

                    // 已知函数 → 走签名校验
                    if (_signatures != null && _signatures.TryGetValue(funcName, out var sig))
                    {
                        int argCount = string.IsNullOrWhiteSpace(argsStr) ? 0
                            : argsStr.Split(',').Length;

                        if (argCount < sig.MinArgs)
                        {
                            var k = funcName.ToUpper() + "@L" + (i + 1) + "few";
                            if (seen.Add(k))
                                errors.Add(string.Format("Line {0}: {1} called with {2} arg(s), but signature requires ≥{3}: \"{4}\"",
                                    i + 1, funcName, argCount, sig.MinArgs, sig.RawSignature));
                        }
                        else if (sig.MaxArgs.HasValue && argCount > sig.MaxArgs.Value)
                        {
                            var k = funcName.ToUpper() + "@L" + (i + 1) + "many";
                            if (seen.Add(k))
                                errors.Add(string.Format("Line {0}: {1} called with {2} arg(s), but signature accepts ≤{3}: \"{4}\"",
                                    i + 1, funcName, argCount, sig.MaxArgs, sig.RawSignature));
                        }

                        var afterIdx = m.Index + m.Length;
                        var after = afterIdx < line.Length ? line.Substring(afterIdx).TrimStart() : "";
                        if (after.StartsWith("=") && _knownReadOnly.Contains(funcName))
                        {
                            var k = funcName.ToUpper() + "@L" + (i + 1) + "assign";
                            if (seen.Add(k))
                                errors.Add(string.Format("Line {0}: cannot assign to {1}(...) — it's a read-only function, use as expression",
                                    i + 1, funcName));
                        }
                        continue;
                    }

                    // 未知调用：Name(...) 不在 _triobasicIds → 幻觉命令
                    if (_triobasicIds == null || !_triobasicIds.Contains(funcName))
                    {
                        // 跳过明显是其他语言的关键字（已被 system prompt 拦，这里不重复报）
                        if (string.Equals(funcName, "if", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(funcName, "while", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(funcName, "for", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var k = funcName.ToUpper() + "@unknown";
                        if (seen.Add(k))
                        {
                            unknown.Add(funcName.ToUpper());
                            if (!errors.Exists(e => e.StartsWith("Identifiers not in TrioBASIC reference")))
                                errors.Add("Identifiers not in TrioBASIC reference: " +
                                    string.Join(", ", unknown.OrderBy(x => x)));
                        }
                    }
                }
            }

            return errors;
        }

        private static List<SkillIndexEntry> LoadIndex()
        {
            if (_index != null && (DateTime.Now - _indexLoadTime).TotalMinutes < 10)
                return _index;

            _index = new List<SkillIndexEntry>();
            try
            {
                if (!Directory.Exists(SkillsDir)) return _index;
                // 仅加载 triobasic 子目录。iec/plcopen 是不同的语言，混进白名单会让
                // AI 写 printf() / AO-printf() 这种 IEC 函数被误判为合法 TrioBASIC。
                var triobasicDir = Path.Combine(SkillsDir, "triobasic");
                var idxFile = Path.Combine(triobasicDir, "index.json");
                if (File.Exists(idxFile))
                {
                    var text = File.ReadAllText(idxFile);
                    var items = _json.Deserialize<List<Dictionary<string, object>>>(text);
                    if (items != null)
                    {
                        foreach (var item in items)
                            _index.Add(new SkillIndexEntry
                            {
                                Name = GetStr(item, "name") ?? "",
                                Type = GetStr(item, "type") ?? "",
                                Desc = GetStr(item, "desc") ?? "",
                                File = GetStr(item, "file"),
                                Dir = triobasicDir
                            });
                    }
                }
                _indexLoadTime = DateTime.Now;
            }
            catch { }
            return _index;
        }

        // ---- Markdown Skills (skills/general/<name>/SKILL.md) ----

        private static List<MdSkillEntry> LoadMdSkills()
        {
            lock (_mdSkillLock)
            {
                if (_mdSkills != null && (DateTime.Now - _mdSkillsLoadTime).TotalMinutes < 10)
                    return _mdSkills;
                var list = new List<MdSkillEntry>();
                try
                {
                    var generalDir = Path.Combine(SkillsDir, "general");
                    if (Directory.Exists(generalDir))
                    {
                        foreach (var sub in Directory.GetDirectories(generalDir))
                        {
                            var skillFile = Path.Combine(sub, "SKILL.md");
                            if (!File.Exists(skillFile)) continue;
                            var entry = ParseSkillMd(skillFile, sub);
                            if (entry != null) list.Add(entry);
                        }
                    }
                    _mdSkills = list;
                    _mdSkillsLoadTime = DateTime.Now;
                }
                catch { }
                return _mdSkills;
            }
        }

        // Minimal frontmatter parser: only reads `name` and `description` from a leading
        // --- block. Trims surrounding quotes. Returns null if name is missing or no FM.
        private static MdSkillEntry ParseSkillMd(string filePath, string dir)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
                if (!text.StartsWith("---", StringComparison.Ordinal)) return null;
                int start = 3;
                if (start < text.Length && (text[start] == '\r' || text[start] == '\n')) start++;
                int end = text.IndexOf("\n---", start, StringComparison.Ordinal);
                if (end < 0) return null;
                var frontmatter = text.Substring(start, end - start);
                int bodyStart = end + 4;
                if (bodyStart < text.Length && text[bodyStart] == '\r') bodyStart++;
                if (bodyStart < text.Length && text[bodyStart] == '\n') bodyStart++;
                var body = text.Substring(bodyStart).TrimEnd();

                string name = null, desc = null;
                foreach (var rawLine in frontmatter.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim().ToLowerInvariant();
                    var val = line.Substring(idx + 1).Trim();
                    if (val.Length >= 2 && ((val[0] == '"' && val[val.Length - 1] == '"') ||
                                            (val[0] == '\'' && val[val.Length - 1] == '\'')))
                        val = val.Substring(1, val.Length - 2);
                    if (key == "name") name = val;
                    else if (key == "description") desc = val;
                }
                if (string.IsNullOrEmpty(name)) return null;
                return new MdSkillEntry
                {
                    Name = name,
                    Description = desc ?? "",
                    Dir = dir,
                    Body = body,
                };
            }
            catch { }
            return null;
        }

        private static object ReadSkill(string name)
        {
            if (string.IsNullOrEmpty(name))
                return new { error = "name is required" };
            var skills = LoadMdSkills();
            if (skills.Count == 0)
                return new { error = "No markdown skills installed. Place <name>/SKILL.md under skills/general/." };
            var q = name.ToUpperInvariant();
            var match = skills.Find(s => s.Name.ToUpperInvariant() == q);
            if (match == null)
                return new { error = $"Skill '{name}' not found. Available: " + string.Join(", ", skills.ConvertAll(s => s.Name)) };
            return new
            {
                name = match.Name,
                description = match.Description,
                markdown = match.Body,
            };
        }

        private static string BuildSkillsCatalog()
        {
            var sb = new StringBuilder();
            // HTML 参考库（lookup_command 索引）— 让 AI 知道这些库存在且应主动查
            var index = LoadIndex();
            if (index.Count > 0)
            {
                var byLib = new Dictionary<string, List<SkillIndexEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in index)
                {
                    var lib = Path.GetFileName(e.Dir ?? "");
                    if (!byLib.ContainsKey(lib)) byLib[lib] = new List<SkillIndexEntry>();
                    byLib[lib].Add(e);
                }
                sb.AppendLine("## Reference Libraries (lookup_command)");
                sb.AppendLine("Use the **lookup_command** tool to verify ANY command/function/function-block before writing code. Coverage:");
                foreach (var kv in byLib)
                    sb.AppendFormat("- **{0}**: {1} entries\n", kv.Key, kv.Value.Count);
                sb.AppendLine();
                sb.AppendLine("MANDATORY: Before writing TrioBASIC, IEC ST/SFC/LD/FBD, or PLCopen code, call lookup_command for any identifier you are not 100% sure exists in the official reference. Do NOT trust your training-data memory of these dialects.");
                sb.AppendLine();
            }
            // Markdown skill（read_skill 索引）
            var skills = LoadMdSkills();
            if (skills.Count > 0)
            {
                sb.AppendLine("## Available Skills");
                sb.AppendLine("These markdown skills are installed. Use the read_skill tool to load full content.");
                sb.AppendLine();
                foreach (var s in skills)
                    sb.AppendFormat("- **{0}**: {1}\n", s.Name, s.Description ?? "");

                // safe-coding 是规范性 skill（安全约束、禁用命令清单），每轮嵌入 system prompt。
                // 不能依赖 AI 主动 read_skill — 之前没 MANDATORY 触发语，AI 写代码时靠训练
                // 记忆硬写；而且 microCompact 5 轮后会清空 read_skill 返回的 tool_result。
                // 全文 ~200 token，成本可忽略。
                var safeCoding = skills.Find(s =>
                    string.Equals(s.Name, "safe-coding", StringComparison.OrdinalIgnoreCase));
                if (safeCoding != null && !string.IsNullOrEmpty(safeCoding.Body))
                {
                    sb.AppendLine();
                    sb.AppendLine("## Safe Coding Rules (MANDATORY)");
                    sb.AppendLine("Follow these rules whenever writing motion control code. Violations are unacceptable:");
                    sb.AppendLine();
                    sb.AppendLine(safeCoding.Body.Trim());
                }
            }
            return sb.ToString();
        }

        // Strip noise tags (script/style/head/comments/img) from a help HTML page so
        // the LLM gets a compact body. img is dropped unless IncludeSkillImages is set.
        private static readonly System.Text.RegularExpressions.Regex _reHead =
            new System.Text.RegularExpressions.Regex(@"<head\b.*?</head>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex _reScript =
            new System.Text.RegularExpressions.Regex(@"<script\b.*?</script>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex _reStyle =
            new System.Text.RegularExpressions.Regex(@"<style\b.*?</style>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex _reComment =
            new System.Text.RegularExpressions.Regex(@"<!--.*?-->",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex _reImg =
            new System.Text.RegularExpressions.Regex(@"<img\b[^>]*/?>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string CleanHelpHtml(string html, bool includeImages)
        {
            if (string.IsNullOrEmpty(html)) return html;
            html = _reComment.Replace(html, "");
            html = _reHead.Replace(html, "");
            html = _reScript.Replace(html, "");
            html = _reStyle.Replace(html, "");
            if (!includeImages)
                html = _reImg.Replace(html, "");
            // Collapse runs of blank lines left behind by the stripping above.
            return System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");
        }

        private static Dictionary<string, object> LoadFullEntry(SkillIndexEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.File)) return null;
            // Cache key includes Dir so two skills with same-named commands don't collide.
            string cacheKey = (entry.Dir ?? "") + "|" + entry.Name;
            Dictionary<string, object> cached;
            if (_skillDetailCache.TryGetValue(cacheKey, out cached))
                return cached;

            var htmlPath = Path.Combine(entry.Dir, entry.File);
            if (!File.Exists(htmlPath)) return null;
            try
            {
                var raw = File.ReadAllText(htmlPath);
                var body = CleanHelpHtml(raw, _includeSkillImages);
                var dict = new Dictionary<string, object>
                {
                    { "name", entry.Name },
                    { "type", entry.Type ?? "" },
                    { "description", entry.Desc ?? "" },
                    { "html", body },
                };
                _skillDetailCache[cacheKey] = dict;
                return dict;
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
                return new { error = "No skill data found. Place index.json + HTML pages in subfolders of " + SkillsDir };

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
                Tool("read_source", "Read source code of a program. For files >200 lines or >8000 chars, auto-returns first chunk + totalLines + hint; use startLine/endLine to paginate subsequent chunks. For IEC tasks, use pouName to read a specific POU (default: first POU). IEC ST POU returns local VAR...END_VAR block (from POU's variable group) PREPENDED to the code body — write_source expects this same shape.", Props(
                    ("name", "Program name", false),
                    ("pouName", "For IEC tasks only: specific POU name to read. If omitted, reads the first POU. Use get_iec_task_detail to list available POUs.", true),
                    ("startLine", "Starting 1-based line number (optional, for pagination)", true),
                    ("endLine", "Ending 1-based line number (optional)", true)
                )),
                Tool("get_iec_task_detail", "Get the internal structure of an IEC task: list of POUs (programs), task/global/retain variable blocks (VAR...END_VAR text), and the user data type table. Use this first when working with an IEC task to see its full contents.", Props("name", "IEC task name")),
                Tool("write_source", "Write full source code to a program (auto-backup, requires confirmation). For IEC tasks, use pouName to target a specific POU — POU is auto-created (as ST/Main) if it does not exist.", Props(
                    ("name", "Program name", false),
                    ("sourceCode", "Full source code to write", false),
                    ("pouName", "For IEC tasks only: target POU name. Auto-created if missing. If omitted, writes to first POU (or creates MAIN).", true)
                )),
                Tool("patch_source", "Apply line-level edits to a program (auto-backup, requires confirmation). For IEC tasks, pouName must point to an EXISTING POU (use get_iec_task_detail to list).", PropsMixed(
                    ("name", "Program name", false, "string"),
                    ("pouName", "For IEC tasks only: target POU name (must exist; defaults to first POU)", true, "string"),
                    ("operations", "Array of {action:replace|insert|delete, line:number, content:string}", false, "array")
                )),
                Tool("read_iec_variables", "Read IEC task variable block as VAR...END_VAR text. scope: task (task-local globals), global (controller-wide), retain (retained across power cycle).", Props(
                    ("name", "IEC task name", false),
                    ("scope", "Variable scope: task | global | retain", false)
                )),
                Tool("write_iec_variables", "Replace an IEC task variable block from VAR...END_VAR text (requires confirmation). Same scope semantics as read_iec_variables.", Props(
                    ("name", "IEC task name", false),
                    ("scope", "Variable scope: task | global | retain", false),
                    ("text", "Full VAR...END_VAR text to import (replaces existing variables in this scope)", false)
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
                Tool("read_skill", "Load full markdown content of a skill listed in the 'Available Skills' section of the system prompt.", Props(
                    ("name", "Skill name from Available Skills", false)
                )),
                Tool("search_code", "Search for a text pattern across all programs in the project. Returns matching lines with line numbers.", Props(
                    ("query", "Search text or pattern", false),
                    ("caseSensitive", "Whether search is case sensitive (default false)", true)
                )),
                Tool("get_axis_detail", "Get detailed info for a single axis (Type, IsEncoderType, DefaultFriendlyName, Motor type, plus all list_axes fields).", Props(
                    ("index", "Axis index (0-based)", false)
                )),
                Tool("copy_program", "Copy an existing program to a new name (requires confirmation)", Props(
                    ("name", "Source program name", false),
                    ("newName", "New program name", false),
                    ("storage", "Storage: internalStorage (default) or sdcardStorage", true)
                )),
                Tool("get_sysvars", "Read structured system variables (WDog, MotionError, ServoPeriod, UnitError, SystemError, FlashStatus)", NoParams()),
                Tool("read_sysvar", "Read any named controller system variable (e.g. PROCESS_RUNNING)", Props("name", "Variable name")),
                Tool("write_sysvar", "Write a named system variable (requires confirmation)", Props(
                    ("name", "Variable name", false),
                    ("value", "Value to write", false)
                )),
                Tool("list_digital_io", "List all digital IO lines", NoParams()),
                Tool("read_digital_io", "Read digital IO state (input + output) at index", Props("index", "IO line index")),
                Tool("write_digital_io", "Write a digital output (requires confirmation)", Props(
                    ("index", "IO line index", false),
                    ("value", "Bool value to write", false)
                )),
                Tool("list_analogue_io", "List all analogue IO lines", NoParams()),
                Tool("read_analogue_io", "Read analogue IO state at index", Props("index", "IO line index")),
                Tool("write_analogue_io", "Write an analogue output (requires confirmation)", Props(
                    ("index", "IO line index", false),
                    ("value", "Numeric value to write", false)
                )),
                Tool("list_processes", "List all running processes on the controller (pid, status, program, line)", NoParams()),
                Tool("get_process_variable", "Read a runtime variable from a running BASIC program", Props(
                    ("pid", "Process ID", false),
                    ("program", "Program (module) name", false),
                    ("variable", "Variable name", false)
                )),
                Tool("get_events", "Pull subscribed controller events since given UTC ticks (program_state, connection_state, async_message, io_changed, compile_state)", Props(
                    ("since", "UTC ticks; default 0 = all buffered events", true)
                )),
                Tool("list_breakpoints", "List breakpoints in a TrioBASIC program (line numbers)", Props("name", "Program name")),
                Tool("set_breakpoint", "Set or clear a breakpoint in a TrioBASIC program (requires confirmation)", Props(
                    ("name", "Program name", false),
                    ("line", "1-based line number", false),
                    ("enable", "true to set, false to clear (default true)", true)
                )),
                Tool("clear_all_breakpoints", "Remove all breakpoints in a TrioBASIC program (requires confirmation)", Props("name", "Program name")),
                Tool("read_drive_param", "Read a drive parameter via DRIVE_READ", Props(
                    ("axis", "Axis number", false),
                    ("address", "Hex drive parameter address", false),
                    ("nd", "Number of fraction digits (default 4)", true)
                )),
                Tool("write_drive_param", "Write a drive parameter via DRIVE_WRITE (requires confirmation)", Props(
                    ("axis", "Axis number", false),
                    ("address", "Hex drive parameter address", false),
                    ("value", "Value to write (number or string)", false)
                )),
                Tool("scan_ethercat", "Scan EtherCAT devices on a slot", Props(
                    ("slot", "Slot number (default 0)", true)
                )),
                Tool("read_ethercat_sdo", "Read an EtherCAT SDO via CANopen", Props(
                    ("slot", "Slot (default 0)", true),
                    ("position", "Device position", false),
                    ("index", "Object index (decimal; e.g. 0x1000 = 4096)", false),
                    ("subindex", "Subindex", true),
                    ("type", "uint16 (default) / int32 / real32 / bool / string / ...", true)
                )),
                Tool("write_ethercat_sdo", "Write an EtherCAT SDO via CANopen (requires confirmation)", Props(
                    ("slot", "Slot (default 0)", true),
                    ("position", "Device position", false),
                    ("index", "Object index (decimal)", false),
                    ("subindex", "Subindex", true),
                    ("type", "Data type (default uint16)", true),
                    ("value", "Value", false)
                )),
                Tool("scan_msbus", "Scan MS Bus modules on a slot", Props(
                    ("slot", "Slot (default 0)", true)
                )),
                Tool("list_remote_devices", "List configured remote device gateways (Modbus TCP/RTU) and their devices", NoParams()),
                Tool("list_robots", "List configured robots (index, name, model, type, axes)", NoParams()),
                Tool("list_recipes", "List Recipe project items", NoParams()),
                Tool("list_alarms", "List alarms from AlarmSupport project items", NoParams()),
                Tool("list_plugins", "Probe which controller-attached plugin services are available (IRobotService, RemoteDeviceManager)", NoParams()),
                Tool("open_oscilloscope", "Open the Oscilloscope tool window", NoParams()),
                Tool("open_project", "Open an existing project from a path (requires confirmation)", Props(
                    ("path", "Project file path", false)
                )),
                Tool("list_project_items", "List all project items with name/type/group", NoParams())
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

## STRICT TRIOBASIC SYNTAX COMPLIANCE (MANDATORY — READ BEFORE WRITING ANY CODE)

TrioBASIC is a niche BASIC dialect. Your training data massively over-represents VB/VB.NET/QBasic/FreeBASIC/PowerBASIC; without explicit effort you WILL drift into those dialects and produce code that fails to compile. The rules below are non-negotiable.

- You may ONLY use keywords, commands, functions, operators, and syntax that exist in the TrioBASIC reference (verified via lookup_command). TrioBASIC is NOT the same as other BASIC dialects.
- FORBIDDEN: Do not invent, guess, or hallucinate TrioBASIC commands. Every command/keyword you write must exist in the official reference.
- MANDATORY: Before writing ANY code that uses a command or syntax you are not 100% certain about, call lookup_command to verify it exists and matches the official syntax. This includes motion commands, axis parameters, system parameters, mathematical functions, and string functions.
- MANDATORY: If the user's request cannot be fulfilled with valid TrioBASIC, do NOT approximate or substitute with made-up commands. Explain what TrioBASIC supports and propose an alternative using only verified commands.

### AFTER-WRITE SELF-CHECK (MANDATORY — DO THIS BEFORE EVERY write_source / patch_source)

1. Scan the code you are about to write. List every command/keyword/function name in it.
2. For each, ask: ""Did I verify this exists in TrioBASIC via lookup_command earlier in this conversation?""
3. If NO for any identifier, call lookup_command for it NOW.
4. If lookup_command returns ""not found"", DO NOT submit the code — rewrite using a verified alternative, or ask the user.
5. Cross-check your code against the dialect table below. If you spot any WRONG pattern, rewrite it as the CORRECT form.

The cost of 1-2 extra lookup_command calls is far less than the cost of code that fails to compile.

### TrioBASIC vs other-BASIC — CORRECT vs WRONG side-by-side (MEMORIZE)

TrioBASIC is case-insensitive. Keywords are conventionally UPPERCASE.

| WRONG (other BASIC)                            | CORRECT (TrioBASIC)                                            |
|------------------------------------------------|----------------------------------------------------------------|
| `Dim x As Integer`                             | `x = 0` (no Dim, no As-clause; types are implicit)            |
| `Dim arr(10) As Integer`                       | `DIM arr(10)` or just assign: `arr(0) = 1`                     |
| `Function F(a,b) As Integer ... End Function`  | (no Function/Sub) — use top-level code, or `GOSUB label ... RETURN` |
| `Sub S(x) ... End Sub`                         | (no Sub) — same as above                                       |
| `Class`, `Module`, `Imports`, `Option Explicit`| (none exist) — TrioBASIC is flat, no OOP                       |
| `If x = 1 Then` ... `End If`                   | `IF x = 1 THEN` ... `ENDIF` (one word)                         |
| `ElseIf` / `Else If`                           | `ELSEIF` (one word)                                            |
| `For i = 1 To 10 Step 2` ... `Next`            | `FOR i = 1 TO 10 STEP 2` ... `NEXT i`                          |
| `For Each x In arr`                            | (no For Each) — use indexed `FOR` loop                         |
| `Do While cond` ... `Loop`                     | `DO WHILE cond` ... `LOOP`  OR  `WHILE cond` ... `WEND`        |
| `Do Until cond` ... `Loop`                     | `DO UNTIL cond` ... `LOOP`                                     |
| `Exit For` / `Exit Sub`                        | (no Exit) — use conditional `GOTO` out of loop, or `RETURN`    |
| `Try ... Catch ... End Try`                    | (no Try/Catch) — use `IF err <> 0 THEN ...` after a call       |
| `Throw New Exception(...)`                     | (no Throw) — `PRINT ""error: ""; ...` then RETURN or stop      |
| `Console.WriteLine(x)` / `Debug.Print x`       | `PRINT x`                                                      |
| `MsgBox(...)`, `InputBox(...)`                 | (none) — `PRINT` for output only                               |
| `Math.Sqrt(x)`, `Math.Abs(x)`, `Math.PI`       | `SQRT(x)`, `ABS(x)`, `4 * ATAN(1)` or define `CONST PI = 3.14159` |
| `x.ToString()`                                 | `STR(x)`                                                       |
| `Integer.Parse(""123"")` / `CInt(...)`         | `VAL(""123"")`                                                 |
| `Const PI As Double = 3.14`                    | `CONST PI = 3.14` (no As-clause)                               |
| `Boolean` / `Integer` / `String` annotations   | (no type annotations) — just identifiers                       |
| `==`, `!=` comparison                          | `=` for both assignment AND equality (no `==`); `<>` for not-equal |
| `REM` comment                                  | `' comment` (TrioBASIC — verify REM if you really want it)     |

When unsure about ANY row, call lookup_command before writing.

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

## STRICT NAMING RULES (MANDATORY)
TrioBASIC reserves system variables (e.g. `VR`, `TABLE`, `AXIS`, `OP`, `DP`, `DPOS`, `MPOS`, `SERVO`, `WDOG`, `BASE`, `SPEED`, `ACCEL`, `DECEL`, `CREEP`, `FE_LIMIT`, `SERIAL`, `IN`, `OUT`, `RUN`, `CONNECT`, `RAPID`, `MOVE`, `HOME`, `CAM`, `DATUM`, `PRINT`, `FOR`, `NEXT`, `IF`, `THEN`, `ELSE`, `ENDIF`, `WHILE`, `WEND`, `REPEAT`, `UNTIL`, `GOTO`, `GOSUB`, `RETURN`, `GLOBAL`, `LOCAL`, `DIM`, `INTEGER`, `FLOAT`, `STRING`) and all built-in function names. These names are **case-insensitive reserved identifiers** — TrioBASIC treats `MOVE`, `move`, `Move` as the same identifier.

- FORBIDDEN: Never declare a user variable, label, or subroutine whose name matches any system variable or built-in function name — NOT EVEN WITH DIFFERENT CASE. `move = 1`, `Move = 1`, `vr_count = 0` (if `VR_COUNT` is reserved), `for_x = 5` (if `FOR_X` is reserved) are all forbidden. TrioBASIC is case-insensitive, so `MyMove`, `MYMOVE`, `mymove` collide equally.
- MANDATORY: Before using ANY identifier as a variable name, verify it is NOT in the reserved list above. If you are not 100% certain whether a name is reserved, call `lookup_command` with the candidate name — if a command/keyword/system-variable matches (case-insensitively), the name is reserved and you MUST pick a different identifier.
- Use prefixes like `my_`, `usr_`, `g_`, or domain-specific nouns (`step_count`, `axis_done`, `cycle_index`) to avoid colliding with reserved identifiers.
- Reserved names also include any motion-command name (`MOVE`, `MOVECIRC`, `MOVEMODIFY`, `MFAST`, `MSYNC`, `CONNECT`, `CANCEL`, `RAPID`, `HOME`, `DATUM`, `CAM`, `CAMBOX`, `GEAR`, `STOP`, `FORWARD`, `REVERSE`), I/O keywords (`IN`, `OUT`, `OP`, `PSWITCH`, `COMPARE`), and all built-in functions (`SIN`, `COS`, `ABS`, `INT`, `MAX`, `MIN`, `SQRT`, `RAND`, `BIT`, `LEN`, `INSTR`, `MID`, `LEFT`, `RIGHT`, `VAL`, `STR`, etc.).
";

        private static string BuildSystemPrompt()
        {
            try
            {
                var prompt = File.Exists(PromptPath) ? File.ReadAllText(PromptPath) : DefaultPrompt;
                var context = BuildProjectContext();
                var skills = BuildSkillsCatalog();
                var lang = System.Threading.Thread.CurrentThread.CurrentUICulture.Name;
                var langInstruction = GetLanguageInstruction(lang);
                var parts = new List<string> { prompt, context };
                if (!string.IsNullOrEmpty(skills)) parts.Add(skills);
                parts.Add(langInstruction);
                return string.Join("\n\n", parts);
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

                        // Program list summary — 数量+类型分布，不列每个名字（占 token 且每次重发）
                        var items = proj.Items;
                        if (items != null)
                        {
                            var itemList = items.ToList();
                            if (itemList.Count > 0)
                            {
                                var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                foreach (var item in itemList)
                                {
                                    var t = item.Type.ToString();
                                    if (!byType.ContainsKey(t)) byType[t] = 0;
                                    byType[t]++;
                                }
                                sb.AppendFormat("- Programs: {0} items ({1})\n",
                                    itemList.Count,
                                    string.Join(", ", byType.Select(kv => kv.Value + " " + kv.Key)));
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

        private static int GetInt(Dictionary<string, object> d, string key, int defaultValue)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                if (val is double dd) return (int)dd;
                if (val is long dl) return (int)dl;
                int result;
                if (int.TryParse(val.ToString(), out result)) return result;
            }
            return defaultValue;
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                if (val is double dd) return (long)dd;
                if (val is long dl) return dl;
                if (val is int di) return di;
                long result;
                if (long.TryParse(val.ToString(), out result)) return result;
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
