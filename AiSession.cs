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
        private string _currentSessionId;
        private const int MaxRestoredFiles = 5;
        private const int MaxRestoredFileChars = 4000;
        private readonly List<Tuple<string, string>> _recentReadFiles = new List<Tuple<string, string>>();

        public int HistoryMessageCount => _conversationHistory.Count;
        public int HistoryTokenEstimate => EstimateHistoryTokens();

        // ---- Session / History ----

        public string CurrentSessionId => _currentSessionId;

        public string StartNewSession()
        {
            _conversationHistory.Clear();
            _recentReadFiles.Clear();
            _totalInputTokens = _totalOutputTokens = _totalCacheReadTokens = _totalCacheCreateTokens = 0;
            _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return _currentSessionId;
        }

        internal void RecordFileRead(string name, string content)
        {
            var summary = content.Length > MaxRestoredFileChars
                ? content.Substring(0, MaxRestoredFileChars) + "\n...[truncated]"
                : content;
            _recentReadFiles.RemoveAll(f => f.Item1 == name);
            _recentReadFiles.Add(Tuple.Create(name, summary));
            while (_recentReadFiles.Count > MaxRestoredFiles)
                _recentReadFiles.RemoveAt(0);
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
            // 快照历史，防止 Chat 线程并发修改导致枚举异常
            List<Dictionary<string, object>> historySnapshot;
            lock (_conversationHistory)
            {
                historySnapshot = new List<Dictionary<string, object>>(_conversationHistory);
            }
            var data = new Dictionary<string, object>
            {
                { "id", _currentSessionId },
                { "updated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "messages", displayMessages ?? new string[0] },
                { "history", historySnapshot }
            };
            try { File.WriteAllText(path, SerializeRequest(data)); } catch { }
        }

        public string LoadSession(string sessionId)
        {
            var path = Path.Combine(HistoryDir, sessionId + ".json");
            if (!File.Exists(path)) return null;
            _currentSessionId = sessionId;
            _conversationHistory.Clear();

            var text = File.ReadAllText(path);
            try
            {
                var data = _json.Deserialize<Dictionary<string, object>>(text);
                if (data != null && data.TryGetValue("history", out var histObj))
                {
                    // JavaScriptSerializer 反序列化数组为 ArrayList，里面是 Dictionary<string,object>
                    if (histObj is System.Collections.ArrayList al)
                    {
                        foreach (var item in al)
                        {
                            if (item is Dictionary<string, object> d)
                                _conversationHistory.Add(d);
                        }
                    }
                }
            }
            catch { }

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
    }
}
