using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
        // ---- JSON Helpers ----

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
