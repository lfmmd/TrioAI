using System;
using System.Collections.Generic;
using System.Linq;
using Trio.CommunicationsLibrary;
using Trio.SharedLibrary;
using Trio.SharedLibrary.ControllerServices;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
        // ---- 控制器级代码校验（TokenTable + ValidationService）----

        private static HashSet<string> _controllerTokenNames;
        private static bool _controllerTokenNamesLoaded;
        private static readonly object _tokenTableLock = new object();

        /// <summary>
        /// 从 IController.TokenTable 缓存所有 token 名称（离线可用，连接后缓存）。
        /// </summary>
        private static HashSet<string> GetControllerTokenNames()
        {
            if (_controllerTokenNamesLoaded) return _controllerTokenNames;
            lock (_tokenTableLock)
            {
                if (_controllerTokenNamesLoaded) return _controllerTokenNames;
                try
                {
                    var ctrl = DispatcherHelper.Invoke(() => MPSingletons.Controller);
                    if (ctrl == null || !ctrl.IsConnected) return null;
                    var table = ctrl.TokenTable;
                    if (table?.Tokens == null) return null;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in table.Tokens)
                    {
                        if (!string.IsNullOrEmpty(t._name))
                            names.Add(t._name);
                    }
                    _controllerTokenNames = names;
                    _controllerTokenNamesLoaded = true;
                    return names;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// 用 TokenTable 校验代码：分词后检查 isSystem=true 的 token 是否在控制器 token 表中。
        /// </summary>
        private static List<string> ValidateWithTokenTable(string code)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(code)) return errors;

            var tokenNames = GetControllerTokenNames();
            if (tokenNames == null || tokenNames.Count == 0) return errors;

            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 跳过单字符标识符（i, j, k 等）和已知控制流
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "if", "then", "else", "elseif", "endif", "for", "next", "to", "step",
                "while", "wend", "do", "loop", "until", "gosub", "return", "goto",
                "exit", "dim", "const", "global", "local", "as", "print", "input",
                "rem", "on", "select", "case", "end", "using", "with", "default",
                "and", "or", "not", "mod", "xor", "shl", "shr", "true", "false",
                "integer", "float", "string", "waits"
            };

            var clean = _reLineComment.Replace(code, "");
            CompileApiCompat.EnumTokensCompat(clean, 0, clean.Length - 1,
                (string word, bool isSystem, int pos, int pStart, int pEnd, int tokenEnd) =>
                {
                    if (word == null) return false;
                    if (!isSystem) return true;
                    if (word.Length <= 1) return true;
                    if (skip.Contains(word)) return true;
                    if (tokenNames.Contains(word)) return true;
                    unknown.Add(word);
                    return true;
                });

            if (unknown.Count > 0)
                errors.Add("TokenTable 校验 — 以下标识符不在控制器 token 表中: " +
                    string.Join(", ", unknown.OrderBy(x => x)));

            return errors;
        }

        /// <summary>
        /// 用控制器 ValidationService 逐行校验代码（EXECUTE "line",0 模式）。
        /// 仅在 enableControllerValidation=true 且连接模拟器时启用。
        /// </summary>
        private static List<string> ValidateByController(string code)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(code)) return errors;

            try
            {
                var ctrl = DispatcherHelper.Invoke(() => MPSingletons.Controller);
                if (ctrl == null || !ctrl.IsConnected) return errors;

                // 预扫描 DIM 语句，收集用户声明的变量名。
                // 控制器 ValidationService 逐行验证时不理解上下文，
                // 会把 DIM 声明的变量名（如 conv_speed、cycle_no）误报为非法命令。
                var dimVars = ScanDimVariables(code);

                var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                var svc = new ValidationService(ctrl, Guid.NewGuid());
                var seenErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("'")) continue;

                    try
                    {
                        if (!svc.ValidateCommand(line, out string error, out int errorCode))
                        {
                            if (string.IsNullOrEmpty(error)) continue;
                            // 跳过由 DIM 变量名引起的误报
                            if (IsDimVarError(error, dimVars)) continue;
                            if (seenErrors.Add(error))
                                errors.Add(string.Format("Line {0}: {1} (error #{2})", i + 1, error, errorCode));
                        }
                    }
                    catch { /* 超时或断开，跳过此行 */ }
                }
            }
            catch { /* controller 不可用 */ }

            return errors;
        }

        /// <summary>
        /// 扫描代码中的 DIM/LOCAL/GLOBAL 语句，提取用户声明的变量名。
        /// </summary>
        private static HashSet<string> ScanDimVariables(string code)
        {
            var vars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clean = _reLineComment.Replace(code, "");
            foreach (var line in clean.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                var trimmed = line.Trim();
                // 匹配 DIM/LOCAL/GLOBAL 声明：DIM var1, var2(10), var3
                if (!trimmed.StartsWith("DIM ", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("LOCAL ", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("GLOBAL ", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 提取 DIM 之后的内容
                var declPart = trimmed.IndexOf(' ') >= 0 ? trimmed.Substring(trimmed.IndexOf(' ') + 1) : "";
                foreach (var token in declPart.Split(','))
                {
                    var name = token.Trim();
                    // 去掉数组下标：arr(10) → arr
                    var parenIdx = name.IndexOf('(');
                    if (parenIdx > 0) name = name.Substring(0, parenIdx);
                    // 去掉 AS 子句：var AS INTEGER → var
                    var asIdx = name.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
                    if (asIdx > 0) name = name.Substring(0, asIdx);
                    name = name.Trim();
                    if (name.Length >= 2 && IsIdentifierLike(name))
                        vars.Add(name);
                }
            }
            return vars;
        }

        /// <summary>
        /// 判断验证错误是否由 DIM 变量名引起（如 "Variable 'conv_speed' is not permitted on Command Line"）。
        /// </summary>
        private static bool IsDimVarError(string error, HashSet<string> dimVars)
        {
            if (dimVars == null || dimVars.Count == 0) return false;
            foreach (var v in dimVars)
            {
                if (error.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 判断当前连接是否为模拟器。
        /// </summary>
        private static bool IsSimulatorConnected()
        {
            try
            {
                return DispatcherHelper.Invoke(() =>
                {
                    var ctrl = MPSingletons.Controller;
                    if (ctrl == null || !ctrl.IsConnected) return false;
                    var desc = ctrl.ConnectionSettings?.Description;
                    return desc != null && desc.StartsWith("Simulator", StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { return false; }
        }

        // 线程安全获取方法
        internal static bool ShouldUseControllerValidation()
        {
            // 逐行 EXECUTE 验证对多行 TrioBASIC 程序必然误报：控制器 ValidationService 用命令行模式
            // 逐行验证，不接受 runtime 命令（GOSUB/PRINT/RUN → #25）、变量赋值（#115）、多行结构
            // （IF/WHILE/WEND/ELSEIF → #39/40/41）。这些在真实程序里完全合法，逐行验证无法理解多行上下文。
            // 弊大于利（误报远多于真错），已禁用。真实代码错误由 ValidateTrioBasicCode（签名）+
            // ValidateWithTokenTable（token 表）两层白名单覆盖，且二者都支持多行程序。
            // 若将来控制器提供「编译验证」API（非 EXECUTE 逐行），可在此重启用。
            return false;
        }
    }
}
