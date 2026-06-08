using System;
using System.Collections.Generic;
using System.Linq;
using Trio.CommunicationsLibrary;
using Trio.SharedLibrary;

namespace TrioAI.MPPlugIn
{
    internal static class Handlers
    {
        private static IMainWindow MW => MPSingletons.MainWindow;
        private static IProject Project => MW?.Project;
        private static IController Controller => MPSingletons.Controller;

        // ---- Project ----
        public static object CreateProject()
        {
            var mw = MW;
            if (mw == null) return Error("No main window");
            var pm = mw.ProjectManager;
            if (pm == null) return Error("No project manager");

            var project = pm.CreateProject();
            return new { success = true, fileName = project?.FileName };
        }

        public static object SaveProject()
        {
            var proj = Project;
            if (proj == null || !proj.IsProject) return Error("No project loaded");

            var mw = MW;
            mw.ProjectManager.SaveProject(proj, ProjectComponent.ProjectFile | ProjectComponent.ItemsContent, true, true);
            return new { success = true };
        }

        // ---- Status ----
        public static object GetStatus()
        {
            var ctrl = Controller;
            var proj = Project;
            return new
            {
                connected = ctrl?.IsConnected ?? false,
                connectionState = ctrl?.ConnectionState.ToString() ?? "Unknown",
                productName = ctrl?.ProductName,
                firmwareVersion = ctrl?.FullVersionString,
                serialNumber = ctrl?.SerialNumber,
                projectName = proj?.FileName,
                isProject = proj?.IsProject ?? false,
                uiLanguage = System.Threading.Thread.CurrentThread.CurrentUICulture.Name
            };
        }

        // ---- Programs ----
        public static object ListPrograms()
        {
            var proj = Project;
            if (proj == null) return new { items = new object[0] };

            var items = proj.Items.Select(i =>
            {
                var result = new Dictionary<string, object>
                {
                    { "name", i.ItemName },
                    { "type", i.Type.ToString() },
                    { "itemType", i.Descriptor?.ItemType },
                    { "isEditable", i.IsEditableType },
                    { "isExecutable", i.IsExecutableType },
                    { "storage", i.Storage.ToString() },
                    { "fileName", i.FileName }
                };

                var procInfo = GetRunnableInfo(i);
                if (procInfo != null)
                {
                    result["isAutorun"] = procInfo.Item1;
                    result["autorunProcess"] = procInfo.Item2;
                    result["processAffinity"] = procInfo.Item3;
                }

                return result;
            }).ToList();

            return new { items };
        }

        public static object CreateProgram(Dictionary<string, object> body)
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var name = GetString(body, "name");
            if (string.IsNullOrEmpty(name)) return Error("name is required");

            var typeName = GetString(body, "type") ?? "basic";
            var sourceCode = GetString(body, "sourceCode");

            var programType = TypeFromString(typeName);
            var descriptor = RegistryProjectItemDescriptors.GetDescriptorByProgramType(programType, name, null);
            if (descriptor == null)
                descriptor = RegistryProjectItemDescriptors.FindDescriptor(name, null, null);
            if (descriptor == null) return Error($"Unknown type: {typeName}");

            var item = proj.CreateAndAddItem(name, descriptor, null, null);
            if (item == null) return Error("Failed to create item");

            if (!string.IsNullOrEmpty(sourceCode) && item is TextProjectItemBase textItem)
            {
                textItem.SaveSourceCode(sourceCode);

                var openError = EnsureDocumentOpen(item);
                if (openError != null)
                    return new { success = true, name = item.ItemName, warning = $"Created but editor not opened: {openError}" };

                SetEditorTextSync(item, sourceCode);
            }

            return new { success = true, name = item.ItemName };
        }

        public static object DeleteProgram(string name)
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            proj.RemoveItem(item, true);
            return new { success = true };
        }

        public static object RenameProgram(string name, Dictionary<string, object> body)
        {
            var newName = GetString(body, "newName");
            if (string.IsNullOrEmpty(newName)) return Error("newName is required");

            var proj = Project;
            if (proj == null) return Error("No project loaded");

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            proj.RenameItem(item, newName);
            return new { success = true, name = newName };
        }

        // ---- Source Code ----
        public static object ReadSource(string name, Dictionary<string, object> body = null)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var textItem = item as TextProjectItemBase;
            if (textItem == null) return Error($"Program is not a text item: {name}");

            var source = textItem.LoadSourceCode();
            var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int totalLines = lines.Length;

            int? startOpt = body != null ? GetInt(body, "startLine") : null;
            int? endOpt = body != null ? GetInt(body, "endLine") : null;

            // 显式分页：返回指定行范围
            if (startOpt.HasValue || endOpt.HasValue)
            {
                int start = Math.Max(1, startOpt ?? 1);
                if (start > totalLines) return Error($"startLine {start} exceeds total lines {totalLines}");
                int end = Math.Min(totalLines, endOpt ?? totalLines);
                if (end < start) end = start;
                var slice = string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));
                return new
                {
                    sourceCode = slice,
                    startLine = start,
                    endLine = end,
                    totalLines,
                    truncated = false
                };
            }

            // 无分页参数：完整返回。但若文件特别大，自动截到前 200 行或 8000 字符（避免触发 MaxToolResultLen），并提示 AI 用 startLine 分页
            const int AutoPageLines = 200;
            const int AutoPageChars = 8000;
            if (totalLines > AutoPageLines || source.Length > AutoPageChars)
            {
                int end = Math.Min(AutoPageLines, totalLines);
                int charCount = 0;
                for (int i = 0; i < end; i++)
                {
                    charCount += lines[i].Length + 1;
                    if (charCount > AutoPageChars) { end = i; break; }
                }
                var slice = string.Join("\n", lines.Take(end));
                return new
                {
                    sourceCode = slice,
                    startLine = 1,
                    endLine = end,
                    totalLines,
                    truncated = true,
                    hint = $"Large file ({totalLines} lines, {source.Length} chars). To read the next chunk, call read_source with startLine={end + 1}."
                };
            }

            return new { sourceCode = source, totalLines };
        }

        public static object OpenProgram(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var mw = MW;
            if (mw == null) return Error("No main window");

            try
            {
                string error = null;
                DispatcherHelper.Invoke(() =>
                {
                    var doc = mw.OpenProgramEditor((IItemBase)(object)item, null, null, true);
                    if (doc == null)
                        error = "OpenProgramEditor returned null";
                });
                if (error != null)
                    return Error(error);
                return new { success = true };
            }
            catch (Exception ex)
            {
                return Error($"Failed to open: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string EnsureDocumentOpen(IProjectItem item)
        {
            var mw = MW;
            if (mw == null) return "No main window";
            try
            {
                var openDoc = mw.HasOpenedDocumentFor(item);
                if (openDoc != null) return null;

                var doc = mw.OpenProgramEditor((IItemBase)(object)item, null, null, true);
                if (doc != null) return null;

                return "OpenProgramEditor returned null";
            }
            catch (Exception ex)
            {
                return $"EnsureDocumentOpen: {ex.GetType().Name}: {ex.Message}";
            }
        }

        public static object WriteSource(string name, Dictionary<string, object> body)
        {
            var sourceCode = GetString(body, "sourceCode") ?? "";

            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var textItem = item as TextProjectItemBase;
            if (textItem == null) return Error($"Program is not a text item: {name}");

            var mw = MW;
            object result = null;
            DispatcherHelper.Invoke(() =>
            {
                textItem.SaveSourceCode(sourceCode);
                var openError = EnsureDocumentOpen(item);
                if (openError != null)
                {
                    result = new { success = false, error = openError };
                    return;
                }

                SetEditorTextSync(item, sourceCode);
                result = new { success = true };
            });
            return result ?? Error("Write failed");
        }

        public static object PatchSource(string name, Dictionary<string, object> body)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var textItem = item as TextProjectItemBase;
            if (textItem == null) return Error($"Program is not a text item: {name}");

            var ops = GetArray(body, "operations");
            // AI 偶尔会把 array 序列化成 JSON 字符串发过来（schema 没声明 array 时尤其常见）。
            // 兼容这种情况：尝试反序列化字符串。
            if ((ops == null || ops.Length == 0) &&
                body.TryGetValue("operations", out var rawOps) && rawOps is string opsStr && !string.IsNullOrEmpty(opsStr))
            {
                try
                {
                    var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                    ops = js.Deserialize<object[]>(opsStr);
                }
                catch { }
            }
            if (ops == null || ops.Length == 0) return Error("operations is required");

            var source = textItem.LoadSourceCode();
            var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var sortedOps = ops.Select(op => op as Dictionary<string, object>)
                .Where(d => d != null)
                .Select(d => new
                {
                    action = GetString(d, "action") ?? "replace",
                    line = GetInt(d, "line") ?? 0,
                    content = GetString(d, "content") ?? ""
                })
                .OrderByDescending(o => o.line)
                .ToList();

            foreach (var op in sortedOps)
            {
                int idx = op.line - 1;
                if (op.action == "delete")
                {
                    if (idx >= 0 && idx < lines.Length)
                        lines = lines.Take(idx).Concat(lines.Skip(idx + 1)).ToArray();
                }
                else if (op.action == "insert")
                {
                    if (idx >= 0 && idx <= lines.Length)
                        lines = lines.Take(idx).Concat(new[] { op.content }).Concat(lines.Skip(idx)).ToArray();
                }
                else
                {
                    if (idx >= 0 && idx < lines.Length)
                        lines[idx] = op.content;
                }
            }

            var newSource = string.Join("\n", lines);

            var mw = MW;
            object result = null;
            DispatcherHelper.Invoke(() =>
            {
                textItem.SaveSourceCode(newSource);
                EnsureDocumentOpen(item);

                SetEditorTextSync(item, newSource);

                result = new { success = true, sourceCode = newSource };
            });
            return result ?? Error("Patch failed");
        }

        private static void SetEditorTextSync(IProjectItem item, string text)
        {
            var mw = MW;
            // Try immediate set (works if doc was already open and visual tree exists)
            var openDoc = mw?.HasOpenedDocumentFor(item);
            if (openDoc != null && TrySetEditorText(openDoc, text))
                return;

            // Doc just opened — pump dispatcher to let visual tree build, then retry
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    openDoc = mw?.HasOpenedDocumentFor(item);
                    if (openDoc != null)
                        TrySetEditorText(openDoc, text);
                }));
        }

        private static bool TrySetEditorText(IDocumentContainer doc, string text)
        {
            try
            {
                var content = doc.Content as System.Windows.FrameworkElement;
                if (content == null) return false;
                var found = FindControlWithTextProperty(content);
                if (found == null) return false;
                found.GetType().GetProperty("Text").SetValue(found, text, null);
                return true;
            }
            catch { return false; }
        }

        private static System.Windows.DependencyObject FindControlWithTextProperty(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                var name = child.GetType().Name;
                // Look for AvalonEdit TextEditor or ProgramEditor
                if (name == "TextEditor" || name == "TextArea")
                {
                    var tp = child.GetType().GetProperty("Text");
                    if (tp != null && tp.CanWrite) return child;
                }
                // ProgramEditor itself might have Text
                if (name == "ProgramEditor")
                {
                    var tp = child.GetType().GetProperty("Text");
                    if (tp != null && tp.CanWrite) return child;
                    // Search inside ProgramEditor for TextEditor
                    var inner = FindControlWithTextProperty(child);
                    if (inner != null) return inner;
                    return null;
                }
                var result = FindControlWithTextProperty(child);
                if (result != null) return result;
            }
            return null;
        }

        // ---- Transfer ----
        public static object Upload(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var ctrl = Controller;
            var context = new TransferToControllerContext((TransferToControllerFlags)2, null);
            bool ok = ctrl.TransferToController(item, context);
            return ok ? new { success = true } : Error(context.error?.ErrorString ?? "Transfer failed");
        }

        public static object Download(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            try
            {
                Controller.TransferFromController(item);
                return new { success = true };
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        // ---- Compile / Run / Stop ----
        public static object Compile(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");
            if (item.RemoteState == null) return Error("No remote state");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var err = Controller.CompileProgram(item.RemoteState);
            if (err != null)
            {
                return new
                {
                    success = false,
                    error = err.errorText ?? $"Compile error #{err.errorNumber}",
                    errorCode = err.errorNumber,
                    errorLine = System.Math.Max(err.lineNumber, 0),
                    errorDescription = err.errorText,
                    includeProgramName = err.includeProgramName,
                    includeProgramLine = err.includeProgramLine
                };
            }
            return new { success = true };
        }

        public static object RunProgram(string name, Dictionary<string, object> body = null)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");
            if (item.RemoteState == null) return Error("No remote state");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            int? processNum = body != null ? GetInt(body, "process") : null;
            if (processNum.HasValue)
                Controller.RunProgram(item.RemoteState, processNum.Value);
            else
                Controller.RunProgram(item.RemoteState);
            return new { success = true };
        }

        public static object StopProgram(string name, Dictionary<string, object> body = null)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");
            if (item.RemoteState == null) return Error("No remote state");

            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            int? processNum = body != null ? GetInt(body, "process") : null;
            if (processNum.HasValue)
                Controller.StopProgram(item.RemoteState, processNum.Value);
            else
                Controller.StopProgram(item.RemoteState);
            return new { success = true };
        }

        // ---- Program Process Settings ----

        public static object GetProgramProcess(string name)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var procInfo = GetRunnableInfo(item);
            if (procInfo == null)
                return Error("This program does not support process settings (not a runnable item or no remote state)");

            return new
            {
                name = item.ItemName,
                isAutorun = procInfo.Item1,
                autorunProcess = procInfo.Item2,
                processAffinity = procInfo.Item3
            };
        }

        public static object SetProgramProcess(string name, Dictionary<string, object> body)
        {
            var item = FindItem(name);
            if (item == null) return Error($"Program not found: {name}");

            var target = FindRunnableTarget(item);
            if (target == null)
                return Error("This program does not support process settings");

            try
            {
                var type = target.GetType().GetInterface("IRunnableItem");

                if (body.ContainsKey("isAutorun"))
                {
                    var prop = type.GetProperty("IsAutorun");
                    prop.SetValue(target, Convert.ToBoolean(body["isAutorun"]), null);
                }
                if (body.ContainsKey("autorunProcess"))
                {
                    var prop = type.GetProperty("AutorunProcess");
                    prop.SetValue(target, Convert.ToInt32(body["autorunProcess"]), null);
                }
                if (body.ContainsKey("processAffinity"))
                {
                    var prop = type.GetProperty("ProcessAffinity");
                    prop.SetValue(target, Convert.ToInt32(body["processAffinity"]), null);
                }

                // Read back
                var procInfo = GetRunnableInfo(item);
                return new
                {
                    success = true,
                    isAutorun = procInfo.Item1,
                    autorunProcess = procInfo.Item2,
                    processAffinity = procInfo.Item3
                };
            }
            catch (Exception ex)
            {
                return Error($"Failed to set process: {ex.Message}");
            }
        }

        private static object FindRunnableTarget(IProjectItem item)
        {
            try
            {
                // Try item itself first (RoboTool pattern)
                if (item.GetType().GetInterface("IRunnableItem") != null)
                    return item;
                // Then try RemoteState (IEC pattern)
                if (item.RemoteState != null && item.RemoteState.GetType().GetInterface("IRunnableItem") != null)
                    return item.RemoteState;
            }
            catch { }
            return null;
        }

        private static Tuple<bool, int, int> GetRunnableInfo(IProjectItem item)
        {
            try
            {
                var target = FindRunnableTarget(item);
                if (target == null) return null;

                var type = target.GetType().GetInterface("IRunnableItem");
                if (type == null) return null;

                var isAutorun = (bool)type.GetProperty("IsAutorun").GetValue(target);
                var autorunProcess = (int)type.GetProperty("AutorunProcess").GetValue(target);
                var processAffinity = (int)type.GetProperty("ProcessAffinity").GetValue(target);
                return Tuple.Create(isAutorun, autorunProcess, processAffinity);
            }
            catch { return null; }
        }

        // ---- VR ----
        public static object ReadVR(int address, int count)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var values = new List<double?>();
            for (int i = 0; i < count; i++)
            {
                double val;
                if (VR.ExecuteGet(queue, address + i, out val, context))
                    values.Add(val);
                else
                    values.Add(null);
            }
            return new { values };
        }

        public static object WriteVR(int address, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            double value = GetDouble(body, "value");
            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            bool ok = VR.ExecuteSet(queue, address, value, context);
            return ok ? new { success = true } : Error("Write VR failed");
        }

        // ---- TABLE ----
        public static object ReadTable(int address, int count)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var values = new List<double?>();

            for (int i = 0; i < count; i++)
            {
                var range = new TABLE.RangeGet_t(address + i, 1, 1, -1);
                var buf = new double?[1];
                if (TABLE.ExecuteGet(queue, range, buf, context) && buf[0].HasValue)
                    values.Add(buf[0].Value);
                else
                    values.Add(null);
            }
            return new { values };
        }

        public static object WriteTable(int address, Dictionary<string, object> body)
        {
            var connErr = RequireDirectConnection();
            if (connErr != null) return connErr;

            var vals = GetArray(body, "values");
            if (vals == null || vals.Length == 0) return Error("values array is required");

            var doubles = vals.Select(v => Convert.ToDouble(v)).ToArray();
            var ctrl = Controller;
            var queue = ctrl.CommandQueue;
            var context = new SimpleCommunicationsContext();
            var range = new TABLE.RangeSet_t(address, doubles);
            bool ok = TABLE.ExecuteSet(queue, range, context);
            return ok ? new { success = true } : Error("Write TABLE failed");
        }

        // ---- Descriptors ----
        public static object ListDescriptors()
        {
            var descs = RegistryProjectItemDescriptors.Descriptors.Select(d => new
            {
                friendlyName = d.FriendlyName,
                type = d.Type.ToString(),
                itemType = d.ItemType,
                defaultStorage = d.DefaultStorage.ToString()
            }).ToList();

            return new { descriptors = descs };
        }

        // ---- Chat Panel ----
        public static object OpenChat()
        {
            var mw = MW;
            if (mw == null) return Error("No main window");
            try
            {
                var factory = ChatPanel.Factory;
                var factoryBase = factory as ToolFactoryBase;
                var isInstalled = factoryBase?.IsInstalled ?? false;

                // Try CreateInstance directly
                IToolControl directInstance = null;
                string directError = null;
                string toolName = ChatPanel.RegisteredToolName;
                try
                {
                    directInstance = factory.CreateInstance(toolName);
                }
                catch (Exception ex2) { directError = ex2.Message; }

                // Try OpenToolWindow by factory
                var resultByFactory = mw.OpenToolWindow(factory, true);

                // Try OpenToolWindow by name
                var resultByName = mw.OpenToolWindow(toolName, true);

                return new
                {
                    isInstalled,
                    toolName,
                    directCreateType = directInstance?.GetType().Name,
                    directCreateTool = directInstance?.Tool?.Title,
                    directError,
                    resultByFactory = resultByFactory?.GetType().Name,
                    resultByName = resultByName?.GetType().Name
                };
            }
            catch (Exception ex)
            {
                return Error("Chat failed: " + ex.GetType().Name + " - " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        // ---- Axes ----
        public static object ListAxes()
        {
            var ctrl = Controller;
            if (ctrl == null) return Error("Not connected to controller");

            var axes = ctrl.Axes;
            if (axes == null) return new { axes = new object[0] };

            var result = axes.Select(a => new
            {
                index = a.Index,
                name = a.FriendlyName,
                typeName = a.TypeName,
                slot = a.Slot,
                type = a.Type.ToString()
            }).ToList();

            return new { axes = result };
        }

        // ---- Search Code ----
        public static object SearchCode(string query, bool caseSensitive)
        {
            var proj = Project;
            if (proj == null) return Error("No project loaded");
            if (string.IsNullOrEmpty(query)) return Error("query is required");

            var results = new List<object>();
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var maxResults = 50;

            foreach (var item in proj.Items)
            {
                if (results.Count >= maxResults) break;

                var textItem = item as TextProjectItemBase;
                if (textItem == null) continue;

                try
                {
                    var source = textItem.LoadSourceCode();
                    if (source == null) continue;

                    var lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (results.Count >= maxResults) break;
                        if (lines[i].IndexOf(query, comparison) >= 0)
                        {
                            results.Add(new
                            {
                                program = item.ItemName,
                                line = i + 1,
                                text = lines[i].Trim()
                            });
                        }
                    }
                }
                catch { }
            }

            return new { query, matchCount = results.Count, results };
        }

        // ---- Helpers ----
        private static object RequireDirectConnection()
        {
            var ctrl = Controller;
            if (ctrl == null) return Error("Not connected to controller");
            var state = ctrl.ConnectionState;
            if (state == ConnectionState.Disconnected || state == ConnectionState.External)
                return Error("Not connected to controller");
            if (state == ConnectionState.ConnTool)
                return Error("Controller is in Tool mode — switch to Direct or Sync mode for this operation");
            return null; // ok
        }

        private static IProjectItem FindItem(string name)
        {
            var proj = Project;
            if (proj == null) return null;
            return proj.Items.FirstOrDefault(i =>
                string.Equals(i.ItemName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static ProgramType_t TypeFromString(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "basic": return ProgramType_t.basic;
                case "encryptedbasic": return ProgramType_t.encryptedBasic;
                case "library":
                case "basiclibrary": return ProgramType_t.basicLibrary;
                case "text":
                case "textfile": return ProgramType_t.textFile;
                case "mcconfig": return ProgramType_t.mcConfig;
                case "iecprogram": return ProgramType_t.iecProgram;
                case "ieclibrary": return ProgramType_t.iecLibrary;
                case "hmidesign": return ProgramType_t.hmiDesign;
                case "hmilibrary": return ProgramType_t.hmiLibrary;
                case "robot": return ProgramType_t.robot;
                case "robotbasic": return ProgramType_t.robotBasic;
                case "realtimelibrary":
                case "realtimebasiclibrary": return ProgramType_t.realtimeBasicLibrary;
                default: return ProgramType_t.basic;
            }
        }

        private static object Error(string msg) => new { success = false, error = msg };

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null) return val.ToString();
            return null;
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
                return Convert.ToDouble(val);
            return 0;
        }

        private static int? GetInt(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val) && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result))
                    return result;
            }
            return null;
        }

        private static object[] GetArray(Dictionary<string, object> d, string key)
        {
            object val;
            if (d.TryGetValue(key, out val))
            {
                // string 也是 IEnumerable<char> — 必须排除，否则会被拆成字符数组
                if (val is string) return null;
                if (val is System.Collections.IEnumerable en)
                    return en.Cast<object>().ToArray();
            }
            return null;
        }
    }
}
