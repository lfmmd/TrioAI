using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
        /// <summary>
        /// 运行 P0/P1 优化的全部回归测试。返回 (pass, fail, details)。
        /// 不需要控制器连接，纯逻辑验证。
        /// </summary>
        public static (int pass, int fail, string report) RunOptimizationTests()
        {
            var results = new List<(string name, bool ok, string detail)>();

            // ========== P0: TryDedupLookupCommand 测试 ==========
            var svc = new AiService();
            // 清空历史确保干净起点
            svc._conversationHistory.Clear();

            // --- P0-1: 首次调用不应去重 ---
            {
                var input = MakeLookupInput("MOVE", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-1 首次调用不去重", result == null,
                    result == null ? "OK: 返回 null，正常执行" : "FAIL: 不应去重但返回了结果"));
            }

            // 模拟历史：assistant 调用了 lookup_command("MOVE", full=true) → user 收到成功结果
            var toolId1 = "toolu_001";
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", toolId1, new Dictionary<string, object>
            {
                { "query", "MOVE" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult(toolId1, "{\"results\":[{\"name\":\"MOVE\",\"html\":\"...16KB...\"}]}"));

            // --- P0-2: 相同参数再次调用应去重 ---
            {
                var input = MakeLookupInput("MOVE", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                var json = result != null ? _json.Serialize(result) : "";
                bool ok = result != null && json.Contains("Already looked up");
                results.Add(("P0-2 相同参数去重", ok,
                    ok ? "OK: 返回引用提示" : $"FAIL: result={json}"));
            }

            // --- P0-3: 不同 query 不去重 ---
            {
                var input = MakeLookupInput("CONNECT", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-3 不同query不去重", result == null,
                    result == null ? "OK: CONNECT 首次调用正常" : "FAIL: 不应匹配 MOVE"));
            }

            // --- P0-4: 不同 full 参数不去重 ---
            {
                var input = MakeLookupInput("MOVE", "false", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-4 不同full不去重", result == null,
                    result == null ? "OK: full=false ≠ full=true" : "FAIL: full 参数不同不应去重"));
            }

            // --- P0-5: 不同 library 不去重 ---
            {
                var input = MakeLookupInput("MOVE", "true", "iec");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-5 不同library不去重", result == null,
                    result == null ? "OK: library=iec ≠ (empty)" : "FAIL: library 不同不应去重"));
            }

            // --- P0-6: 大小写不敏感匹配 ---
            {
                var input = MakeLookupInput("move", "True", "");
                var result = svc.TryDedupLookupCommand(input);
                var json = result != null ? _json.Serialize(result) : "";
                bool ok = result != null && json.Contains("Already looked up");
                results.Add(("P0-6 大小写不敏感", ok,
                    ok ? "OK: move/True 匹配 MOVE/true" : "FAIL: 大小写应不敏感"));
            }

            // --- P0-7: 之前返回 error 的不去重 ---
            svc._conversationHistory.Clear();
            var toolId2 = "toolu_002";
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", toolId2, new Dictionary<string, object>
            {
                { "query", "FOOBAR" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult(toolId2, "{\"error\":\"No matching command found for 'FOOBAR'\"}"));
            {
                var input = MakeLookupInput("FOOBAR", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-7 error结果不去重", result == null,
                    result == null ? "OK: 允许重试失败的查询" : "FAIL: error 结果不应缓存"));
            }

            // --- P0-8: CompactHistory 后不再匹配 ---
            svc._conversationHistory.Clear();
            var toolId3 = "toolu_003";
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", toolId3, new Dictionary<string, object>
            {
                { "query", "HOME" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult(toolId3, "{\"results\":[{\"name\":\"HOME\"}]}"));
            // 模拟 CompactHistory：用摘要替换旧消息
            svc._conversationHistory.Clear();
            svc._conversationHistory.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", "[Conversation summary] User asked about HOME command..." }
            });
            {
                var input = MakeLookupInput("HOME", "true", "");
                var result = svc.TryDedupLookupCommand(input);
                results.Add(("P0-8 压缩后不去重", result == null,
                    result == null ? "OK: 摘要文本不含 tool_use 结构，不匹配" : "FAIL: 压缩后应正常执行"));
            }

            // --- P0-9: 多个 tool_use 在同一 assistant 消息中 ---
            svc._conversationHistory.Clear();
            svc._conversationHistory.Add(MakeAssistantToolUse(new[]
            {
                ("lookup_command", "toolu_010", new Dictionary<string, object> { { "query", "BASE" }, { "full", "false" } }),
                ("lookup_command", "toolu_011", new Dictionary<string, object> { { "query", "SPEED" }, { "full", "false" } }),
                ("get_status", "toolu_012", new Dictionary<string, object>())
            }));
            svc._conversationHistory.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", new List<Dictionary<string, object>>
                {
                    MakeToolResultBlock("toolu_010", "{\"results\":[{\"name\":\"BASE\"}]}"),
                    MakeToolResultBlock("toolu_011", "{\"results\":[{\"name\":\"SPEED\"}]}"),
                    MakeToolResultBlock("toolu_012", "{\"status\":\"ok\"}")
                }}
            });
            {
                // BASE 应去重
                var r1 = svc.TryDedupLookupCommand(MakeLookupInput("BASE", "false", ""));
                // SPEED 应去重
                var r2 = svc.TryDedupLookupCommand(MakeLookupInput("SPEED", "false", ""));
                // DPOS 首次不应去重
                var r3 = svc.TryDedupLookupCommand(MakeLookupInput("DPOS", "false", ""));
                bool ok = r1 != null && r2 != null && r3 == null;
                results.Add(("P0-9 多tool_use混合", ok,
                    ok ? "OK: BASE/SPEED 去重，DPOS 不去重" :
                    $"FAIL: BASE={r1 != null}, SPEED={r2 != null}, DPOS={r3 == null}"));
            }

            // --- P0-10: 非 lookup_command 的 tool_use 不干扰 ---
            svc._conversationHistory.Clear();
            svc._conversationHistory.Add(MakeAssistantToolUse("read_source", "toolu_020", new Dictionary<string, object>
            {
                { "name", "MAIN" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult("toolu_020", "{\"sourceCode\":\"...\"}"));
            svc._conversationHistory.Add(MakeAssistantToolUse("lookup_command", "toolu_021", new Dictionary<string, object>
            {
                { "query", "WAITS" }, { "full", "true" }
            }));
            svc._conversationHistory.Add(MakeUserToolResult("toolu_021", "{\"results\":[{\"name\":\"WAITS\"}]}"));
            {
                var r = svc.TryDedupLookupCommand(MakeLookupInput("WAITS", "true", ""));
                bool ok = r != null && _json.Serialize(r).Contains("Already looked up");
                results.Add(("P0-10 非lookup不干扰", ok,
                    ok ? "OK: WAITS 正确匹配到 lookup_command 而非 read_source" : "FAIL"));
            }

            // ========== P1: GetToolDefinitions 缓存测试 ==========
            // --- P1-1: 返回正确数量的 tools ---
            {
                var tools1 = GetToolDefinitions();
                bool ok = tools1.Count == 59;
                results.Add(("P1-1 tool数量=59", ok,
                    ok ? "OK: 59 个 tool" : $"FAIL: 实际 {tools1.Count} 个"));
            }

            // --- P1-2: 多次调用返回新 list 实例 ---
            {
                var list1 = GetToolDefinitions();
                var list2 = GetToolDefinitions();
                bool ok = !ReferenceEquals(list1, list2);
                results.Add(("P1-2 每次返回新List", ok,
                    ok ? "OK: 浅拷贝，不是同一引用" : "FAIL: 返回了同一个 List 对象"));
            }

            // --- P1-3: 修改返回的 list 不影响缓存 ---
            {
                var list1 = GetToolDefinitions();
                var origCount = list1.Count;
                list1.Clear();
                var list2 = GetToolDefinitions();
                bool ok = list2.Count == origCount;
                results.Add(("P1-3 修改list不影响缓存", ok,
                    ok ? $"OK: Clear 后重新获取仍有 {origCount} 个" : $"FAIL: 缓存被污染，只剩 {list2.Count} 个"));
            }

            // --- P1-4: 添加 cache_control 不影响缓存 ---
            {
                var tools = GetToolDefinitions();
                if (tools.Count > 0)
                {
                    // 模拟 CallApiStream 的操作
                    var lastTool = new Dictionary<string, object>(tools[tools.Count - 1])
                    {
                        { "cache_control", new { type = "ephemeral" } }
                    };
                    tools[tools.Count - 1] = lastTool;
                }
                var tools2 = GetToolDefinitions();
                var lastTool2 = tools2[tools2.Count - 1];
                bool hasCacheCtrl = lastTool2.ContainsKey("cache_control");
                bool ok = !hasCacheCtrl;
                results.Add(("P1-4 cache_control不污染缓存", ok,
                    ok ? "OK: 缓存的 tool 没有 cache_control" : "FAIL: 缓存被 cache_control 污染了"));
            }

            // --- P1-5: tool schema 完整性 ---
            {
                var tools = GetToolDefinitions();
                bool allValid = true;
                var badTools = new List<string>();
                foreach (var t in tools)
                {
                    if (!t.ContainsKey("name") || !t.ContainsKey("description") || !t.ContainsKey("input_schema"))
                    {
                        allValid = false;
                        badTools.Add(GetStr(t, "name") ?? "(no name)");
                    }
                }
                results.Add(("P1-5 schema完整性", allValid,
                    allValid ? "OK: 全部 tool 含 name+description+input_schema" :
                    $"FAIL: 以下 tool 缺字段: {string.Join(", ", badTools)}"));
            }

            // ========== 报告 ==========
            var sb = new StringBuilder();
            sb.AppendLine("=== TrioAI Optimization Tests ===");
            sb.AppendLine();
            int pass = results.Count(r => r.ok);
            int fail = results.Count(r => !r.ok);
            foreach (var (name, ok, detail) in results)
            {
                sb.AppendFormat("{0}  {1}  — {2}\n", ok ? "PASS" : "FAIL", name, detail);
            }
            sb.AppendLine();
            sb.AppendFormat("结果: {0} passed, {1} failed / {2} total\n", pass, fail, results.Count);

            return (pass, fail, sb.ToString());
        }

        // ---- Test helpers ----

        private static Dictionary<string, object> MakeLookupInput(string query, string full, string library)
        {
            var d = new Dictionary<string, object> { { "query", query } };
            if (full != null) d["full"] = full;
            if (library != null && library.Length > 0) d["library"] = library;
            return d;
        }

        private static Dictionary<string, object> MakeAssistantToolUse(
            string toolName, string toolId, Dictionary<string, object> input)
        {
            return new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        { "type", "tool_use" },
                        { "id", toolId },
                        { "name", toolName },
                        { "input", input }
                    }
                }}
            };
        }

        private static Dictionary<string, object> MakeAssistantToolUse(
            (string name, string id, Dictionary<string, object> input)[] tools)
        {
            var blocks = new List<Dictionary<string, object>>();
            foreach (var (name, id, input) in tools)
            {
                blocks.Add(new Dictionary<string, object>
                {
                    { "type", "tool_use" },
                    { "id", id },
                    { "name", name },
                    { "input", input }
                });
            }
            return new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", blocks }
            };
        }

        private static Dictionary<string, object> MakeUserToolResult(string toolId, string content)
        {
            return new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", new List<Dictionary<string, object>>
                {
                    MakeToolResultBlock(toolId, content)
                }}
            };
        }

        private static Dictionary<string, object> MakeToolResultBlock(string toolId, string content)
        {
            return new Dictionary<string, object>
            {
                { "type", "tool_result" },
                { "tool_use_id", toolId },
                { "content", content }
            };
        }
    }
}
