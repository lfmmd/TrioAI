using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
        // ---- Config-related instance fields ----

        private string _apiKey;
        private string _model;
        private string _apiUrl;
        private bool _showToolStatus = true;
        private bool _skillsInitialized;
        private static bool _includeSkillImages = false;
        private static bool _enableControllerValidation = false;
        private static bool _enableThinking = false;
        private static int _budgetTokens = 10000;
        private static bool _showThinking = true;
        private static bool _memoryEnabled = true;
        private static bool _localizeThinking = true;

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
                    if (cfg.TryGetValue("enableControllerValidation", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _enableControllerValidation = b;
                    }
                    if (cfg.TryGetValue("enableThinking", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _enableThinking = b;
                    }
                    if (cfg.TryGetValue("budgetTokens", out val))
                    {
                        int i;
                        if (val != null && int.TryParse(val.ToString(), out i)) _budgetTokens = i;
                    }
                    if (cfg.TryGetValue("showThinking", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _showThinking = b;
                    }
                    if (cfg.TryGetValue("memoryEnabled", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _memoryEnabled = b;
                    }
                    if (cfg.TryGetValue("localizeThinking", out val))
                    {
                        bool b;
                        if (val != null && bool.TryParse(val.ToString(), out b)) _localizeThinking = b;
                    }
                }
            }
            catch { }
        }

        public void SaveConfig(string apiKey, string model, string apiUrl, bool? showToolStatus = null, bool? includeSkillImages = null, bool? enableControllerValidation = null, bool? enableThinking = null, int? budgetTokens = null, bool? showThinking = null, bool? memoryEnabled = null, bool? localizeThinking = null)
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
            if (enableControllerValidation.HasValue) _enableControllerValidation = enableControllerValidation.Value;
            if (enableThinking.HasValue) _enableThinking = enableThinking.Value;
            if (budgetTokens.HasValue) _budgetTokens = budgetTokens.Value;
            if (showThinking.HasValue) _showThinking = showThinking.Value;
            if (memoryEnabled.HasValue) _memoryEnabled = memoryEnabled.Value;
            if (localizeThinking.HasValue) _localizeThinking = localizeThinking.Value;
            var json = _json.Serialize(new { apiKey = _apiKey, model = _model, apiUrl = _apiUrl, showToolStatus = _showToolStatus, skillsInitialized = _skillsInitialized, includeSkillImages = _includeSkillImages, enableControllerValidation = _enableControllerValidation, enableThinking = _enableThinking, budgetTokens = _budgetTokens, showThinking = _showThinking, memoryEnabled = _memoryEnabled, localizeThinking = _localizeThinking });
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
        public static bool EnableControllerValidation => _enableControllerValidation;

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
            var json = _json.Serialize(new { apiKey = _apiKey ?? "", model = _model ?? "", apiUrl = _apiUrl ?? "", showToolStatus = _showToolStatus, skillsInitialized = true, includeSkillImages = _includeSkillImages, enableControllerValidation = _enableControllerValidation, enableThinking = _enableThinking, budgetTokens = _budgetTokens, showThinking = _showThinking, memoryEnabled = _memoryEnabled, localizeThinking = _localizeThinking });
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
        public static bool EnableThinking => _enableThinking;
        public static int BudgetTokens => _budgetTokens;
        public static bool ShowThinking => _showThinking;
        public static bool MemoryEnabled => _memoryEnabled;
        public static bool LocalizeThinking => _localizeThinking;
    }
}
