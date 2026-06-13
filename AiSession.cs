using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
        // ---- Session-related instance fields ----

        private readonly List<Dictionary<string, object>> _conversationHistory = new List<Dictionary<string, object>>();
        private readonly object _historyLock = new object();
        private string _currentSessionId;
        private const int MaxRestoredFiles = 5;
        private const int MaxRestoredFileChars = 4000;
        private readonly List<Tuple<string, string>> _recentReadFiles = new List<Tuple<string, string>>();

        public int HistoryMessageCount { get { lock (_historyLock) { return _conversationHistory.Count; } } }
        public int HistoryTokenEstimate { get { lock (_historyLock) { return EstimateHistoryTokens(); } } }

        // ---- Session / History ----

        public string CurrentSessionId => _currentSessionId;

        public string StartNewSession()
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
                _recentReadFiles.Clear();
                _totalInputTokens = _totalOutputTokens = _totalCacheReadTokens = _totalCacheCreateTokens = 0;
                _currentMaxTokens = DefaultMaxTokens;
                _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                return _currentSessionId;
            }
        }

        internal void RecordFileRead(string name, string content)
        {
            var summary = content.Length > MaxRestoredFileChars
                ? content.Substring(0, MaxRestoredFileChars) + "\n...[truncated]"
                : content;
            lock (_historyLock)
            {
                _recentReadFiles.RemoveAll(f => f.Item1 == name);
                _recentReadFiles.Add(Tuple.Create(name, summary));
                while (_recentReadFiles.Count > MaxRestoredFiles)
                    _recentReadFiles.RemoveAt(0);
            }
        }

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
                _currentSessionId = null;
            }
        }

        public void SaveSession(string displayMessagesJson)
        {
            if (string.IsNullOrEmpty(_currentSessionId)) return;
            var path = Path.Combine(HistoryDir, _currentSessionId + ".json");
            List<Dictionary<string, object>> historySnapshot;
            lock (_historyLock)
            {
                historySnapshot = new List<Dictionary<string, object>>(_conversationHistory);
            }
            // 解析 display messages JSON → object
            object displayMsgs = new object[0];
            try { if (!string.IsNullOrEmpty(displayMessagesJson)) displayMsgs = _json.Deserialize<object>(displayMessagesJson) ?? displayMsgs; } catch { }
            var data = new Dictionary<string, object>
            {
                { "id", _currentSessionId },
                { "updated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "messages", displayMsgs },
                { "history", historySnapshot }
            };
            try { File.WriteAllText(path, SerializeRequest(data)); } catch { }
        }

        public string LoadSession(string sessionId)
        {
            var path = Path.Combine(HistoryDir, sessionId + ".json");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);

            lock (_historyLock)
            {
                _currentSessionId = sessionId;
                _conversationHistory.Clear();
                try
                {
                    var data = _json.Deserialize<Dictionary<string, object>>(text);

                    // 加载原生 history（tool_use/tool_result 序列）。
                    if (data != null && data.TryGetValue("history", out var histObj)
                        && histObj is System.Collections.ArrayList al)
                    {
                        foreach (var item in al)
                        {
                            if (item is Dictionary<string, object> d)
                            {
                                NormalizeHistoryContent(d);
                                _conversationHistory.Add(d);
                            }
                        }
                    }

                    // 从 display messages 生成可读摘要，注入 history 开头
                    // AI 模型往往无法从 tool_use/tool_result 交换中理解对话上下文，
                    // 摘要帮助 AI 理解之前的对话内容。
                    if (data != null && data.TryGetValue("messages", out var msgsObj)
                        && msgsObj is System.Collections.ArrayList displayMsgs
                        && displayMsgs.Count > 0
                        && _conversationHistory.Count > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("[Previous session context — restored on load]");
                        foreach (var m in displayMsgs)
                        {
                            if (!(m is Dictionary<string, object> md)) continue;
                            var role = md.ContainsKey("role") ? md["role"]?.ToString() : "?";
                            var msg = md.ContainsKey("text") ? md["text"]?.ToString() : "";
                            if (string.IsNullOrEmpty(msg)) continue;
                            var label = role == "User" ? "User" : role == "AI" ? "AI" : "System";
                            var trimmed = msg.Length > 300 ? msg.Substring(0, 300) + "..." : msg;
                            sb.AppendLine($"{label}: {trimmed}");
                        }
                        _conversationHistory.Insert(0, new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", sb.ToString() }
                        });
                        _conversationHistory.Insert(1, new Dictionary<string, object>
                        {
                            { "role", "assistant" },
                            { "content", "Understood. I have the previous session context above and will continue from there." }
                        });
                    }
                }
                catch (Exception ex)
                {
                    try { AiService.LogException("LoadSession", ex); } catch { }
                }
            }
            return text;
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
                    if (data == null) continue;
                    var id = Path.GetFileNameWithoutExtension(file);
                    object updated;
                    var timeStr = data.TryGetValue("updated", out updated) ? updated?.ToString() : id;
                    string preview = "";
                    object msgs;
                    if (data.TryGetValue("messages", out msgs) && msgs is System.Collections.ArrayList al && al.Count > 0)
                    {
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

        /// JavaScriptSerializer 反序列化后 content 数组变成 ArrayList，
        /// 但代码中大量使用 `content is List&lt;Dictionary&lt;string,object&gt;&gt;` 类型检查。
        /// 加载时必须把 ArrayList 转回 List，否则所有 assistant 消息的 tool_use/text block 都会被跳过。
        private static void NormalizeHistoryContent(Dictionary<string, object> msg)
        {
            object content;
            if (!msg.TryGetValue("content", out content)) return;
            if (content is System.Collections.ArrayList al)
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var item in al)
                {
                    if (item is Dictionary<string, object> d)
                        list.Add(d);
                }
                msg["content"] = list;
            }
        }

        internal class SessionInfo
        {
            public string Id { get; set; }
            public string Time { get; set; }
            public string Preview { get; set; }
        }
    }
}
