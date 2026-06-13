using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
        // ---- History budget / trimming constants ----

        private const int MaxHistoryKeep = 100;
        private const int MaxRecentKeep = 30;
        private const int MaxToolResultLen = 16000;
        // microCompact：旧 tool_result 的 content 替换为占位符，保留 tool_use_id 不破坏配对
        private const int MaxRecentToolResults = 5;
        private const string ClearedToolResult = "[Old tool result content cleared]";
        // auto-compaction：利用模型上下文窗口的大部分空间，而非硬截断。
        // 500K chars ≈ 125K tokens — 对于 200K context 的模型留出足够空间给 system + tools + output。
        private const int HistoryTokenBudget = 500000;

        private void TrimHistory()
        {
            if (EstimateHistoryTokens() < HistoryTokenBudget
                && _conversationHistory.Count <= MaxHistoryKeep)
                return;

            OnSystemMessage?.Invoke(Lang.L($"⚠ 已触发历史裁剪: {_conversationHistory.Count} 条消息, 约 {EstimateHistoryTokens()} tokens",
                                           $"⚠ TrimHistory triggered: {_conversationHistory.Count} msgs, ~{EstimateHistoryTokens()} tokens"));

            // auto-compaction：将旧消息摘要压缩为一条，保留最近消息不变。
            if (CompactHistory())
                return;

            // 摘要失败时兜底：截断旧消息
            // 优先找 user+string 消息（普通用户输入），兜底找任意 user 消息（tool_result），
            // 确保 messages 首条始终是 user 角色。
            int start = _conversationHistory.Count - MaxRecentKeep;
            if (start < 1) start = 1;

            int found = -1;
            int foundAnyUser = -1;
            for (int i = start; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                if (GetStringValue(msg, "role") == "user")
                {
                    if (foundAnyUser < 0) foundAnyUser = i;
                    if (msg["content"] is string)
                    {
                        found = i;
                        break;
                    }
                }
            }
            // 兜底：如果没找到 user+string，用任意 user 消息（确保不以 assistant 开头）
            if (found < 0) found = foundAnyUser;

            if (found <= 0) return;

            var trimmed = new List<Dictionary<string, object>>(
                _conversationHistory.GetRange(found, _conversationHistory.Count - found));
            _conversationHistory.Clear();
            _conversationHistory.AddRange(trimmed);
        }

        /// Auto-compaction：调用 AI 将旧消息摘要为一条 user 消息，保留最近 MaxRecentKeep 条。
        /// 成功返回 true（历史已替换为摘要 + 最近消息）。
        /// 调用者必须持 _historyLock。内部在 API 调用前释放锁，调用后重新获取。
        private bool CompactHistory()
        {
            if (_conversationHistory.Count <= MaxRecentKeep + 2) return false;

            int compactEnd = _conversationHistory.Count - MaxRecentKeep;
            if (compactEnd < 2) return false;

            // 快照旧消息用于摘要（在锁内读取）
            var sb = new StringBuilder();
            for (int i = 0; i < compactEnd; i++)
            {
                var msg = _conversationHistory[i];
                var role = GetStringValue(msg, "role") ?? "?";
                object contentVal;
                if (!msg.TryGetValue("content", out contentVal)) continue;
                if (contentVal is string s)
                    sb.AppendFormat("[{0}]: {1}\n", role, s);
                else if (contentVal is List<Dictionary<string, object>> blocks)
                {
                    foreach (var b in blocks)
                    {
                        var c = GetStringValue(b, "content");
                        if (c == null) c = GetStringValue(b, "text");
                        if (c != null)
                        {
                            var type = GetStringValue(b, "type") ?? "";
                            var name = GetStringValue(b, "name") ?? "";
                            if (type == "tool_use")
                                sb.AppendFormat("[assistant/tool_use:{0}]: {1}\n", name, c);
                            else if (type == "tool_result")
                                sb.AppendFormat("[user/tool_result]: {0}\n", Truncate(c, 2000));
                            else
                                sb.AppendFormat("[{0}]: {1}\n", role, c);
                        }
                    }
                }
            }

            // 快照 _recentReadFiles（在锁内）
            var recentFiles = new List<Tuple<string, string>>(_recentReadFiles);

            if (sb.Length < 100) return false;

            var compactPrompt = string.Format(
                "Summarize the following conversation between a user and an AI assistant. " +
                "Preserve: 1) User's original request and intent 2) Key decisions and code changes made " +
                "3) Important error messages and fixes 4) Current state of work (what's done, what's pending). " +
                "Be concise but complete — this summary replaces the original messages.\n\n{0}",
                sb.ToString());

            // 释放锁 → API 调用（可能耗时数秒）
            System.Threading.Monitor.Exit(_historyLock);
            OnSystemMessage?.Invoke(Lang.L("正在自动压缩对话历史...",
                                           "Auto-compacting conversation history..."));
            string summary;
            try
            {
                summary = CallCompactApi(compactPrompt);
            }
            finally
            {
                System.Threading.Monitor.Enter(_historyLock);
            }
            if (string.IsNullOrEmpty(summary)) return false;

            try
            {
                // API 调用期间历史可能已变，重新计算 compactEnd
                compactEnd = _conversationHistory.Count - MaxRecentKeep;
                if (compactEnd < 2) return false;

                var newHistory = new List<Dictionary<string, object>>();
                newHistory.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", "[Conversation summary — older messages compacted]\n\n" + summary }
                });
                newHistory.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", "Understood. I have the context from the summary above and will continue from where we left off." }
                });

                // 压缩后恢复最近读过的文件上下文
                if (recentFiles.Count > 0)
                {
                    var fileSb = new StringBuilder("[Recently read source files — restored after compaction]\n");
                    foreach (var f in recentFiles)
                        fileSb.AppendFormat("\n### {0}\n```\n{1}\n```\n", f.Item1, f.Item2);
                    newHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", fileSb.ToString() }
                    });
                    newHistory.Add(new Dictionary<string, object>
                    {
                        { "role", "assistant" },
                        { "content", "Understood. I have the restored file contexts above." }
                    });
                }

                for (int i = compactEnd; i < _conversationHistory.Count; i++)
                    newHistory.Add(_conversationHistory[i]);

                _conversationHistory.Clear();
                _conversationHistory.AddRange(newHistory);

                OnSystemMessage?.Invoke(Lang.L($"已压缩为摘要。新历史: {_conversationHistory.Count} 条消息。",
                                               $"Compacted into summary. New history: {_conversationHistory.Count} msgs."));
                return true;
            }
            catch (Exception ex)
            {
                LogException("CompactHistory", ex);
                return false;
            }
        }

        private string CallCompactApi(string prompt)
        {
            // 复用 system blocks 以命中缓存，compact 调用也省 system prompt tokens
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
            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "max_tokens", 2048 },
                { "system", systemBlocks },
                { "messages", new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", prompt }
                        }
                    }
                }
            };

            var json = SerializeRequest(body);
            LogApiRequest("compact", json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, NormalizeApiUrl(_apiUrl));
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = content;

            var response = _http.SendAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }

            var respText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            response.Dispose();

            // 解析非流式响应
            var resp = _json.Deserialize<Dictionary<string, object>>(respText);
            if (resp == null) return null;
            var contentArr = resp["content"] as List<object>;
            if (contentArr == null || contentArr.Count == 0) return null;
            var firstBlock = contentArr[0] as Dictionary<string, object>;
            var text = firstBlock != null ? GetStringValue(firstBlock, "text") : null;
            return text;
        }

        // 粗略估算历史 token：chars/4。仅用于裁剪触发判断，不要求精确。
        // 调用者必须持 _historyLock。
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
                        var th = GetStringValue(b, "thinking");
                        if (th != null) chars += th.Length;
                    }
                }
            }
            return chars / 4;
        }

        private List<Dictionary<string, object>> BuildTrimmedMessages()
        {
            lock (_historyLock)
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

            // lookup_command 去重：同一 query+full+library 组合第一次出现保留完整内容，后续替换为引用占位符。
            // 只按 query 去重会导致：先查 brief → 后查 full 被误判为重复 → 完整 HTML 丢失。
            // 不区分 library 会导致：查 triobasic 的 MOVE → 再查 iec 的 MOVE 被误判为重复。
            var lookupMetaById = new Dictionary<string, Tuple<bool, string>>(StringComparer.Ordinal);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") != "tool_use") continue;
                    if (!string.Equals(GetStringValue(b, "name"), "lookup_command", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = GetStringValue(b, "id");
                    if (id == null) continue;
                    if (!(b.TryGetValue("input", out var inObj) && inObj is Dictionary<string, object> inDict)) continue;
                    var fullVal = GetStr(inDict, "full");
                    var isFull = string.Equals(fullVal, "true", StringComparison.OrdinalIgnoreCase);
                    var lib = GetStr(inDict, "library") ?? "";
                    lookupMetaById[id] = Tuple.Create(isFull, lib);
                }
            }

            var seenLookupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    Tuple<bool, string> meta;
                    if (!lookupMetaById.TryGetValue(id, out meta)) continue;
                    var key = query + "\t" + (meta.Item1 ? "full" : "brief") + "\t" + meta.Item2;
                    if (!seenLookupKeys.Add(key))
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
            // 最近 N 个 ∪ lookup_command / read_skill 的所有结果（参考类内容，AI 写代码时会反复引用，
            // 清空会导致它再查/再读一次浪费 API + 中断代码连贯性）
            var keepRecent = new HashSet<string>(
                allToolResultIds.Skip(Math.Max(0, allToolResultIds.Count - MaxRecentToolResults)));
            foreach (var id in allToolResultIds)
            {
                if (toolNameById.TryGetValue(id, out var name)
                    && (string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "read_skill", StringComparison.OrdinalIgnoreCase)))
                {
                    keepRecent.Add(id);
                }
            }

            // read_source 历史去重：同一文件 + 同一参数范围只保留最新读取结果
            var readSourceById = new Dictionary<string, string>(StringComparer.Ordinal);
            var readSourceKeyById = new Dictionary<string, string>(StringComparer.Ordinal);
            var latestReadSourceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _conversationHistory)
            {
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> bl)) continue;
                foreach (var b in bl)
                {
                    if (GetStringValue(b, "type") != "tool_use") continue;
                    if (!string.Equals(GetStringValue(b, "name"), "read_source", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = GetStringValue(b, "id");
                    if (id == null) continue;
                    if (!(b.TryGetValue("input", out var inObj) && inObj is Dictionary<string, object> inDict)) continue;
                    var progName = GetStr(inDict, "name");
                    if (progName == null) continue;
                    var startLine = GetStr(inDict, "startLine") ?? "";
                    var endLine = GetStr(inDict, "endLine") ?? "";
                    var key = progName.ToLowerInvariant() + "|" + startLine + "|" + endLine;
                    readSourceById[id] = progName;
                    readSourceKeyById[id] = key;
                    latestReadSourceId[key] = id; // last wins → latest
                }
            }
            var dupReadSourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in readSourceKeyById)
            {
                string latestId;
                if (latestReadSourceId.TryGetValue(kv.Value, out latestId) && latestId != kv.Key)
                    dupReadSourceIds.Add(kv.Key);
            }

            // 预计算 assistant 消息数量，用于判断哪些是"旧的"（清理 thinking、压缩 tool_use input）
            int totalAssistantMsgs = 0;
            foreach (var m in _conversationHistory)
                if (GetStringValue(m, "role") == "assistant") totalAssistantMsgs++;
            int assistantSeen = 0;
            const int KeepRecentThinking = 3;

            for (int idx = 0; idx < _conversationHistory.Count; idx++)
            {
                var msg = _conversationHistory[idx];
                var copy = new Dictionary<string, object>(msg);
                var content = copy["content"];

                // user 消息：tool_result 去重 / microCompact / 截断
                if (copy["role"] as string == "user" && content is List<Dictionary<string, object>> blocks)
                {
                    var trimmedBlocks = new List<Dictionary<string, object>>();
                    foreach (var block in blocks)
                    {
                        if (GetStringValue(block, "type") == "tool_result")
                        {
                            var tb = new Dictionary<string, object>(block);
                            var id = GetStringValue(tb, "tool_use_id");
                            if (id != null && duplicateLookupIds.Contains(id))
                            {
                                var q = lookupQueryById[id];
                                tb["content"] = "[Duplicate of lookup_command(\"" + q
                                    + "\") — full content preserved at the first call earlier in this conversation. "
                                    + "Reference that occurrence instead of asking again.]";
                            }
                            else if (id != null && dupReadSourceIds.Contains(id))
                            {
                                var pn = readSourceById.ContainsKey(id) ? readSourceById[id] : "?";
                                tb["content"] = "[Earlier read_source(\"" + pn
                                    + "\") — latest read preserved later in this conversation]";
                            }
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

                // assistant: 清理旧 thinking、压缩已清空 tool_use input、打 cache_control
                if (GetStringValue(copy, "role") == "assistant" && content is List<Dictionary<string, object>> asstBlocks && asstBlocks.Count > 0)
                {
                    assistantSeen++;
                    bool isOld = totalAssistantMsgs > KeepRecentThinking
                        && assistantSeen <= totalAssistantMsgs - KeepRecentThinking;
                    var newBlocks = new List<Dictionary<string, object>>();
                    foreach (var b in asstBlocks)
                    {
                        var bType = GetStringValue(b, "type") ?? "";
                        // 旧 thinking block 完全移除
                        if (bType == "thinking" && isOld)
                            continue;
                        // 已清空结果的 tool_use → 压缩 input
                        if (bType == "tool_use" && isOld)
                        {
                            var toolId = GetStringValue(b, "id");
                            if (toolId != null && !keepRecent.Contains(toolId))
                            {
                                var compressed = new Dictionary<string, object>(b);
                                compressed["input"] = new Dictionary<string, object> { { "_summarized", true } };
                                newBlocks.Add(compressed);
                                continue;
                            }
                        }
                        newBlocks.Add(b);
                    }
                    if (newBlocks.Count > 0)
                    {
                        var lastBlock = new Dictionary<string, object>(newBlocks[newBlocks.Count - 1]);
                        lastBlock["cache_control"] = new { type = "ephemeral" };
                        newBlocks[newBlocks.Count - 1] = lastBlock;
                    }
                    else
                    {
                        newBlocks.Add(new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", "[assistant message compacted]" },
                            { "cache_control", new { type = "ephemeral" } }
                        });
                    }
                    copy["content"] = newBlocks;
                }

                    messages.Add(copy);
                }

                // 防御：确保 messages 满足 API 约束：
                //   1. 首条必须是 user 角色
                //   2. tool_result 必须紧跟在 assistant 的 tool_use 后面
                //   3. messages 不能为空数组
                // TrimHistory/CompactHistory 截断可能破坏这些约束。
                // 参考 claudecodefx 的 ensureToolResultPairing 做法进行修复。
                EnsureValidMessageSequence(messages);

                return messages;
            }
        }
        /// <summary>
        /// 确保 messages 数组满足 API 约束：首条必须是 user，tool_result 必须紧跟 assistant 的 tool_use，
        /// 不能为空数组。参考 claudecodefx 的 ensureToolResultPairing 逻辑。
        /// </summary>
        private void EnsureValidMessageSequence(List<Dictionary<string, object>> messages)
        {
            if (messages.Count == 0) return;

            // 跨消息 tool_use ID 累积去重（防止两条 assistant 消息有相同 id 的 tool_use）
            var globalToolUseIds = new HashSet<string>(StringComparer.Ordinal);
            var repaired = false;

            // 第一遍：处理 assistant 消息 — 去重 tool_use ID，并记录有效的 tool_use ID
            var validToolUseIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> blocks)) continue;

                var filtered = new List<Dictionary<string, object>>();
                foreach (var b in blocks)
                {
                    if (GetStringValue(b, "type") == "tool_use")
                    {
                        var id = GetStringValue(b, "id");
                        if (id != null && globalToolUseIds.Contains(id))
                        {
                            // 跨消息重复的 tool_use ID → 丢弃
                            repaired = true;
                            continue;
                        }
                        if (id != null)
                        {
                            globalToolUseIds.Add(id);
                            validToolUseIds.Add(id);
                        }
                    }
                    filtered.Add(b);
                }

                if (filtered.Count != blocks.Count)
                {
                    // assistant 内容被清空了 → 插入占位文本
                    if (filtered.Count == 0)
                    {
                        filtered.Add(new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", "[Duplicate tool use removed]" }
                        });
                    }
                    m["content"] = filtered;
                }
            }

            // 第二遍：修复 user 消息 — 移除孤立和重复的 tool_result
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                var role = GetStringValue(m, "role");

                if (role == "user" && m["content"] is List<Dictionary<string, object>> blocks)
                {
                    // 检查是否有孤立的 tool_result（对应 tool_use 不在前面的 assistant 消息中）
                    var validBlocks = new List<Dictionary<string, object>>();
                    var seenResultIds = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var b in blocks)
                    {
                        if (GetStringValue(b, "type") == "tool_result")
                        {
                            var trId = GetStringValue(b, "tool_use_id");
                            // 去重：同一个 tool_use_id 只保留第一个
                            if (trId != null && seenResultIds.Contains(trId))
                            {
                                repaired = true;
                                continue;
                            }
                            if (trId != null) seenResultIds.Add(trId);

                            // 检查有没有对应的有效 tool_use
                            if (trId != null && validToolUseIds.Contains(trId))
                            {
                                validBlocks.Add(b);
                            }
                            else
                            {
                                repaired = true;
                            }
                        }
                        else
                        {
                            validBlocks.Add(b);
                        }
                    }

                    if (validBlocks.Count != blocks.Count)
                    {
                        if (validBlocks.Count == 0)
                        {
                            // 移除后没有内容了 → 替换为占位文本
                            m["content"] = "[Orphaned tool result removed due to conversation history trimming]";
                            repaired = true;
                        }
                        else
                        {
                            m["content"] = validBlocks;
                        }
                    }
                }
            }

            // 第三遍：处理 assistant 消息中有 tool_use 但后面没有对应 tool_result 的情况
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (GetStringValue(m, "role") != "assistant") continue;
                if (!(m["content"] is List<Dictionary<string, object>> blocks)) continue;

                var toolUseIds = new List<string>();
                foreach (var b in blocks)
                {
                    if (GetStringValue(b, "type") == "tool_use")
                    {
                        var id = GetStringValue(b, "id");
                        if (id != null) toolUseIds.Add(id);
                    }
                }
                if (toolUseIds.Count == 0) continue;

                // 找下一条 user 消息中的 tool_result id
                var resultIds = new HashSet<string>(StringComparer.Ordinal);
                if (i + 1 < messages.Count && GetStringValue(messages[i + 1], "role") == "user")
                {
                    var next = messages[i + 1];
                    if (next["content"] is List<Dictionary<string, object>> nextBlocks)
                    {
                        foreach (var b in nextBlocks)
                        {
                            if (GetStringValue(b, "type") == "tool_result")
                            {
                                var id = GetStringValue(b, "tool_use_id");
                                if (id != null) resultIds.Add(id);
                            }
                        }
                    }
                }

                // 找缺失的 tool_use id（有 tool_use 没有对应 tool_result）
                var missingIds = toolUseIds.Where(id => !resultIds.Contains(id)).ToList();
                if (missingIds.Count > 0)
                {
                    repaired = true;
                    // 插入合成的 tool_result
                    var synthBlocks = missingIds.Select(id => new Dictionary<string, object>
                    {
                        { "type", "tool_result" },
                        { "tool_use_id", id },
                        { "content", "[Tool result missing due to history trimming]" },
                        { "is_error", true }
                    }).ToList();

                    if (i + 1 < messages.Count && GetStringValue(messages[i + 1], "role") == "user")
                    {
                        // 下一条是 user 消息，在前面追加合成的 tool_result
                        var next = messages[i + 1];
                        if (next["content"] is List<Dictionary<string, object>> nextBlocks)
                        {
                            synthBlocks.AddRange(nextBlocks);
                            next["content"] = synthBlocks;
                        }
                        else
                        {
                            synthBlocks.Add(new Dictionary<string, object>
                            {
                                { "type", "text" },
                                { "text", next["content"]?.ToString() ?? "" }
                            });
                            next["content"] = synthBlocks;
                        }
                    }
                    else
                    {
                        // 需要插入一条新的 user 消息
                        messages.Insert(i + 1, new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", synthBlocks.Cast<Dictionary<string, object>>().ToList<Dictionary<string, object>>() }
                        });
                        i++; // 跳过刚插入的消息
                    }
                }
            }

            // 第四遍：确保首条是合法的 user 消息
            if (messages.Count > 0)
            {
                int firstValidUser = -1;
                for (int i = 0; i < messages.Count; i++)
                {
                    if (GetStringValue(messages[i], "role") == "user")
                    {
                        firstValidUser = i;
                        break;
                    }
                }

                if (firstValidUser > 0)
                {
                    OnSystemMessage?.Invoke(Lang.L($"⚠ 历史修复: 跳过开头 {firstValidUser} 条非 user 消息",
                                                   $"⚠ History repair: skipped {firstValidUser} leading non-user messages"));
                    messages.RemoveRange(0, firstValidUser);
                    repaired = true;
                }
                else if (firstValidUser < 0)
                {
                    // 全部是 assistant 消息 → 替换为一条占位 user 消息
                    OnSystemMessage?.Invoke(Lang.L("⚠ 历史修复: 未找到 user 消息，已插入占位消息",
                                                   "⚠ History repair: no user messages found, inserting placeholder"));
                    messages.Clear();
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", "[Conversation history was trimmed. Please continue helping the user.]" }
                    });
                    repaired = true;
                }
            }

            // 最终兜底：空数组 → 插入占位
            if (messages.Count == 0)
            {
                messages.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", "[Conversation history was trimmed. Please continue helping the user.]" }
                });
                repaired = true;
            }

            if (repaired)
                OnSystemMessage?.Invoke(Lang.L("⚠ 历史修复: 消息序列已被修复",
                                               "⚠ History repair: message sequence was repaired"));
        }

        /// <summary>
        /// 将参数值归一化为小写字符串，解决 JavaScriptSerializer 对 bool/string/缺失的类型不一致问题。
        /// bool true → "true", string "True" → "true", 缺失 key → ""
        /// </summary>
        private static string NormParam(Dictionary<string, object> d, string key)
        {
            object val;
            if (!d.TryGetValue(key, out val) || val == null) return "";
            if (val is bool b) return b ? "true" : "false";
            return val.ToString().Trim().ToLowerInvariant();
        }

        /// <summary>
        /// P0: lookup_command 同会话去重。扫描 _conversationHistory，如果同一 query+library+full
        /// 组合已经成功执行过，返回紧凑引用而非重新加载完整 HTML。
        /// CompactHistory 之后旧 tool_use/tool_result 对被替换为摘要文本，扫描不会命中 → 自然回退到正常执行。
        /// </summary>
        private object TryDedupLookupCommand(Dictionary<string, object> input)
        {
            lock (_historyLock)
            {
            // 确保去重判断与工具实际行为一致。
            // 例如：full=true(bool) → GetStr="True" → 工具视为非 full → 去重也应视为非 full。
            var nQuery = GetStr(input, "query");
            var nFull = GetStr(input, "full");       // null / "true" / "True" / "false" ...
            var nLibrary = GetStr(input, "library");  // null / "iec" / "triobasic" ...
            if (string.IsNullOrEmpty(nQuery)) return null;

            for (int i = 0; i < _conversationHistory.Count; i++)
            {
                var msg = _conversationHistory[i];
                if (GetStringValue(msg, "role") != "assistant") continue;
                if (!(msg["content"] is List<Dictionary<string, object>> blocks)) continue;

                foreach (var b in blocks)
                {
                    if (GetStringValue(b, "type") != "tool_use") continue;
                    if (!string.Equals(GetStringValue(b, "name"), "lookup_command",
                        StringComparison.OrdinalIgnoreCase)) continue;

                    object rawInput;
                    if (!b.TryGetValue("input", out rawInput)) continue;
                    var bInput = rawInput as Dictionary<string, object>;
                    if (bInput == null) continue;

                    var hQuery = GetStr(bInput, "query");
                    var hFull = GetStr(bInput, "full");
                    var hLibrary = GetStr(bInput, "library");

                    // query: 大小写不敏感匹配
                    if (!string.Equals(nQuery, hQuery, StringComparison.OrdinalIgnoreCase)) continue;
                    // full: full=true 可以替代不带 full 的查询（full 包含 brief 的全部信息）
                    // 允许命中：① 精确匹配 ② 当前非 full 但历史是 full（full→brief 方向去重）
                    // 不允许：当前是 full 但历史不是 full（brief 不能替代 full）
                    var hIsFull = string.Equals(hFull, "true", StringComparison.OrdinalIgnoreCase);
                    var nIsFull = string.Equals(nFull, "true", StringComparison.OrdinalIgnoreCase);
                    if (nIsFull && !hIsFull) continue;
                    // library: 大小写不敏感匹配
                    if (!string.Equals(nLibrary, hLibrary, StringComparison.OrdinalIgnoreCase)) continue;

                    // 找到相同参数的历史调用，检查其结果是否成功
                    var toolId = GetStringValue(b, "id");
                    if (toolId == null) continue;

                    // tool_result 紧跟在 tool_use 后面的 user 消息中（最多向下找 2 条）
                    for (int j = i + 1; j < _conversationHistory.Count && j <= i + 2; j++)
                    {
                        var rMsg = _conversationHistory[j];
                        if (GetStringValue(rMsg, "role") != "user") continue;
                        if (!(rMsg["content"] is List<Dictionary<string, object>> rBlocks)) continue;

                        foreach (var rb in rBlocks)
                        {
                            if (GetStringValue(rb, "type") != "tool_result") continue;
                            if (GetStringValue(rb, "tool_use_id") != toolId) continue;

                            var content = GetStringValue(rb, "content");
                            if (content != null && !content.Contains("\"error\":"))
                            {
                                LogApiRequest("dedup-hit",
                                    $"current(q={nQuery},f={nFull ?? "(null)"},l={nLibrary ?? "(null)"}) " +
                                    $"history(q={hQuery},f={hFull ?? "(null)"},l={hLibrary ?? "(null)"})");
                                return new
                                {
                                    results = new[]
                                    {
                                        new
                                        {
                                            name = nQuery,
                                            note = $"Already looked up '{nQuery}' (full={nFull}, library={nLibrary}) " +
                                                   "earlier in this conversation. The full result is preserved in the " +
                                                   "conversation history — reference that earlier tool_result instead " +
                                                   "of calling again."
                                        }
                                    }
                                };
                            }
                        }
                    }
                }
            }

            return null;
            }
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
    }
}
