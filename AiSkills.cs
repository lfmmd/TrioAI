using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
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

        private static List<SkillIndexEntry> LoadIndex()
        {
            if (_index != null && (DateTime.Now - _indexLoadTime).TotalMinutes < 10)
                return _index;

            _index = new List<SkillIndexEntry>();
            try
            {
                if (!Directory.Exists(SkillsDir)) return _index;
                // 加载所有库子目录（triobasic / iec / plcopen），每条 entry 的 Dir 标识来源。
                foreach (var lib in new[] { "triobasic", "iec", "plcopen" })
                {
                    var libDir = Path.Combine(SkillsDir, lib);
                    var idxFile = Path.Combine(libDir, "index.json");
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
                            File = GetStr(item, "file"),
                            Dir = libDir
                        });
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
                if (bodyStart < text.Length && (text[bodyStart] == '\r')) bodyStart++;
                if (bodyStart < text.Length && (text[bodyStart] == '\n')) bodyStart++;
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
        private static readonly Regex _reHead =
            new Regex(@"<head\b.*?</head>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reScript =
            new Regex(@"<script\b.*?</script>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reStyle =
            new Regex(@"<style\b.*?</style>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reComment =
            new Regex(@"<!--.*?-->",
                RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _reImg =
            new Regex(@"<img\b[^>]*/?>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            return Regex.Replace(html, @"\n{3,}", "\n\n");
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

        private static object LookupCommand(string query, string fullFlag, string library = null)
        {
            if (string.IsNullOrEmpty(query))
                return new { error = "query is required" };

            var index = LoadIndex();

            // 按库过滤
            IEnumerable<SkillIndexEntry> searchSet = index;
            if (!string.IsNullOrEmpty(library))
            {
                var libLower = library.ToLowerInvariant().Trim();
                searchSet = index.Where(e =>
                    Path.GetFileName(e.Dir ?? "").Equals(libLower, StringComparison.OrdinalIgnoreCase));
            }
            if (!searchSet.Any())
                return new { error = string.IsNullOrEmpty(library)
                    ? "No skill data found."
                    : $"No skill data found for library '{library}'." };

            var q = query.ToUpperInvariant().Trim();

            // 搜索：精确 → 名称包含 → 描述包含
            var matched = new List<SkillIndexEntry>();

            foreach (var e in searchSet)
            {
                if (e.Name.ToUpperInvariant() == q) { matched.Add(e); break; }
            }
            if (matched.Count == 0)
            {
                foreach (var e in searchSet)
                {
                    if (e.Name.ToUpperInvariant().Contains(q)) matched.Add(e);
                    if (matched.Count >= 5) break;
                }
            }
            if (matched.Count == 0)
            {
                foreach (var e in searchSet)
                {
                    if (e.Desc != null && e.Desc.ToUpperInvariant().Contains(q)) matched.Add(e);
                    if (matched.Count >= 3) break;
                }
            }

            if (matched.Count == 0)
                return new { error = $"No matching command found for '{query}'" };

            // Layer 2（默认）：名称 + 签名 + 描述，不加载 HTML
            if (!string.Equals(fullFlag, "true", StringComparison.OrdinalIgnoreCase))
            {
                var summaries = new List<object>();
                foreach (var m in matched)
                {
                    var sigText = "";
                    if (_signatures != null && _signatures.TryGetValue(m.Name, out var sig))
                        sigText = sig.RawSignature;
                    summaries.Add(new
                    {
                        name = m.Name,
                        signature = sigText,
                        description = m.Desc ?? "",
                        library = Path.GetFileName(m.Dir ?? "")
                    });
                }
                return new
                {
                    results = summaries,
                    hint = "Pass full=true for complete HTML documentation with examples."
                };
            }

            // Layer 3：完整 HTML（含 192KB 预算）
            var results = new List<object>();
            foreach (var m in matched)
            {
                var full = LoadFullEntry(m);
                if (full != null) results.Add(full);
            }

            if (results.Count == 0)
                return new { error = $"Index matched but full data not found for '{query}'" };

            const int MaxTotalHtml = 192 * 1024;
            long totalHtmlLen = 0;
            foreach (Dictionary<string, object> d in results)
                if (d["html"] is string h) totalHtmlLen += h.Length;

            if (totalHtmlLen > MaxTotalHtml)
            {
                var scale = (double)MaxTotalHtml / totalHtmlLen;
                for (int i = 0; i < results.Count; i++)
                {
                    var d = (Dictionary<string, object>)results[i];
                    var html = (string)d["html"];
                    var maxLen = Math.Max(1024, (int)(html.Length * scale));
                    if (html.Length > maxLen)
                    {
                        results[i] = new Dictionary<string, object>(d)
                        {
                            ["html"] = html.Substring(0, maxLen)
                                + "\n\n[... truncated — total lookup results capped at 192KB ...]"
                        };
                    }
                }
            }

            return new { results };
        }
    }
}
