using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal partial class AiService
    {
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

        // 纯 IO 工具：不需要 UI 线程（不访问 MP 项目模型），可绕过 DispatcherHelper.Invoke 直接调用。
        // 这些工具的 DispatchTool 路径是纯内存字典查询或纯文件读，线程安全。
        // 加它们到这个集合 → 可在 Task.Run 内真正并行执行。
        private static readonly HashSet<string> PureIoTools = new HashSet<string>
        {
            "lookup_command",    // 纯内存字典 + HTML 文件读
            "read_skill",        // 纯文件读
            "discover_skills",   // 纯文件读（LoadMdSkills）
            "task_create",       // 纯内存操作（_tasks list）
            "task_update",       // 纯内存操作
            "task_list",         // 纯内存操作
            "task_get",          // 纯内存操作
            "enter_plan_mode",   // 纯内存操作（_planMode 字段）
            "exit_plan_mode"     // 纯内存操作 + OnConfirmPlan 回调（UI 线程安全）
        };

        private string ExecuteTool(string name, Dictionary<string, object> input)
        {
            try
            {
                // P0: lookup_command 同会话去重 — 避免重复查询同一命令的完整 HTML（每次 ~16KB）
                if (string.Equals(name, "lookup_command", StringComparison.OrdinalIgnoreCase))
                {
                    var dedupResult = TryDedupLookupCommand(input);
                    if (dedupResult != null)
                        return _json.Serialize(dedupResult);
                }

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
                    // Plan Mode 拒绝：AI 必须先 exit_plan_mode 让用户审批计划
                    if (_planMode)
                    {
                        return "BLOCKED: Plan Mode is active. You cannot modify controller state, programs, or VR/TABLE in Plan Mode. " +
                               "Use read-only tools (read_source / list_programs / get_status / lookup_command / etc.) to complete your investigation, " +
                               "then call exit_plan_mode to present your plan for user approval. After approval, Plan Mode is disabled and write tools become available.";
                    }
                    var argsPreview = _json.Serialize(input);
                    var accepted = OnConfirmWrite?.Invoke(name, argsPreview) ?? false;
                    if (!accepted)
                        return "User rejected this operation.";
                }

                // 纯 IO 工具绕过 DispatcherHelper 直接执行（可在 Task.Run 内并行）；
                // 其他工具仍封送到 UI 线程（MP 项目模型访问需要 STA）。
                var result = PureIoTools.Contains(name)
                    ? DispatchTool(name, input)
                    : DispatcherHelper.Invoke(() => DispatchTool(name, input));
                var resultStr = _json.Serialize(result);
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: {Truncate(resultStr, 300)}");
                return _json.Serialize(result);
            }
            catch (Exception ex)
            {
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: {Lang.L("错误", "ERROR")} - {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        private object DispatchTool(string name, Dictionary<string, object> input)
        {
            switch (name)
            {
                case "get_status": return Handlers.GetStatus();
                case "list_programs": return Handlers.ListPrograms();
                case "read_source":
                {
                    var readResult = Handlers.ReadSource(GetStr(input, "name"), input);
                    var srcText = readResult is string s ? s : _json.Serialize(readResult);
                    RecordFileRead(GetStr(input, "name"), Truncate(srcText, MaxRestoredFileChars));
                    return readResult;
                }
                case "get_iec_task_detail": return Handlers.GetIecTaskDetail(GetStr(input, "name"));
                case "read_iec_variables": return Handlers.ReadIecVariables(GetStr(input, "name"), GetStr(input, "scope"));
                case "write_iec_variables": return Handlers.WriteIecVariables(GetStr(input, "name"), GetStr(input, "scope"), GetStr(input, "text"));
                case "write_source":
                {
                    var progName = GetStr(input, "name");
                    if (Handlers.GetProgramDialect(progName) == "triobasic")
                    {
                        var code = GetStr(input, "sourceCode") ?? "";
                        var errs = ValidateTrioBasicCode(code);
                        errs.AddRange(ValidateWithTokenTable(code));
                        if (ShouldUseControllerValidation())
                            errs.AddRange(ValidateByController(code));
                        if (errs.Count > 0)
                            return new { error = "BLOCKED by TrioBASIC validation:\n  " + string.Join("\n  ", errs) };
                    }
                    return Handlers.WriteSource(progName, input);
                }
                case "patch_source":
                {
                    var progName = GetStr(input, "name");
                    if (Handlers.GetProgramDialect(progName) == "triobasic")
                    {
                        var errs = new List<string>();
                        if (input.TryGetValue("operations", out var opsObj) && opsObj is List<object> ops)
                        {
                            foreach (var op in ops)
                            {
                                var dict = op as Dictionary<string, object>;
                                if (dict == null) continue;
                                var newStr = GetStr(dict, "new_string") ?? GetStr(dict, "content") ?? "";
                                if (!string.IsNullOrEmpty(newStr))
                                {
                                    errs.AddRange(ValidateTrioBasicCode(newStr));
                                    errs.AddRange(ValidateWithTokenTable(newStr));
                                    if (ShouldUseControllerValidation())
                                        errs.AddRange(ValidateByController(newStr));
                                }
                            }
                        }
                        if (errs.Count > 0)
                            return new { error = "BLOCKED by TrioBASIC validation:\n  " + string.Join("\n  ", errs) };
                    }
                    return Handlers.PatchSource(progName, input);
                }
                case "read_vr": return Handlers.ReadVR(GetHexInt(input, "address"), GetInt(input, "count"));
                case "write_vr": return Handlers.WriteVR(GetHexInt(input, "address"), input);
                case "read_table": return Handlers.ReadTable(GetHexInt(input, "address"), GetInt(input, "count"));
                case "write_table": return Handlers.WriteTable(GetHexInt(input, "address"), input);
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
                case "read_drive_param": return Handlers.ReadDriveParam(GetInt(input, "axis"), GetHexInt(input, "address"), GetInt(input, "nd", 4));
                case "write_drive_param": return Handlers.WriteDriveParam(GetInt(input, "axis"), GetHexInt(input, "address"), input);
                case "scan_ethercat": return Handlers.ScanEtherCAT(GetInt(input, "slot", 0));
                case "read_ethercat_sdo":
                    return Handlers.EtherCATReadSDO(GetInt(input, "slot", 0),
                                                    (uint)GetLong(input, "position"),
                                                    (uint)GetHexLong(input, "index"),
                                                    (uint)GetHexLong(input, "subindex"),
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
                case "lookup_command": return LookupCommand(GetStr(input, "query"), GetStr(input, "full"), GetStr(input, "library"));
                case "read_skill": return ReadSkill(GetStr(input, "name"));
                case "discover_skills": return DiscoverSkills(GetStr(input, "category"));
                case "search_code": return Handlers.SearchCode(GetStr(input, "query"), GetBool(input, "caseSensitive"));
                case "update_memory":
                    {
                        var memContent = GetStr(input, "content");
                        if (string.IsNullOrEmpty(memContent))
                            return new { error = "content is required" };
                        SaveMemory(memContent);
                        return new { success = true, message = "Memory updated successfully." };
                    }

                case "task_create": return TaskCreate(GetStr(input, "subject"), GetStr(input, "description"));
                case "task_update": return TaskUpdate(GetInt(input, "id", 0), GetStr(input, "status"), GetStr(input, "subject"), GetStr(input, "description"));
                case "task_list": return TaskList();
                case "task_get": return TaskGet(GetInt(input, "id", 0));
                case "enter_plan_mode": return EnterPlanMode();
                case "exit_plan_mode": return ExitPlanMode(GetStr(input, "plan"));

                default: return new { error = $"Unknown tool: {name}" };
            }
        }

        // ---- Plan Mode 实现 ----

        private object EnterPlanMode()
        {
            _planMode = true;
            OnPlanModeChanged?.Invoke(true);
            return new
            {
                plan_mode = true,
                hint = "Plan Mode is now active. All write/modify tools (write_source, compile_program, run_program, write_vr, etc.) are blocked. " +
                       "Use read-only tools to investigate, then call exit_plan_mode with your plan to request user approval."
            };
        }

        private object ExitPlanMode(string plan)
        {
            if (string.IsNullOrEmpty(plan))
                return new { error = "plan is required — present what you intend to do and why" };

            // 请求用户审批。若 UI 未挂 OnConfirmPlan（Phase 1 范围：不动 UI），
            // 默认批准以避免 AI 在 plan mode 内死锁。
            // 用户可在 ChatPanel 接 OnConfirmPlan 回调启用审批 UI（Phase 2）。
            bool approved = OnConfirmPlan != null ? OnConfirmPlan(plan) : true;
            if (!approved)
            {
                // 用户拒绝：保持 plan mode，让 AI 继续调研或修订计划
                return new
                {
                    plan_mode = true,
                    approved = false,
                    hint = "User did not approve the plan. Plan Mode is still active. Revise the plan and call exit_plan_mode again, or do more investigation first."
                };
            }

            _planMode = false;
            OnPlanModeChanged?.Invoke(false);
            return new
            {
                plan_mode = false,
                approved = true,
                hint = "Plan approved. Write/modify tools are now available. Proceed with the approved plan."
            };
        }

        // ---- Task / Todo 系统实现 ----
        // 状态存储在 AiSession 的 _tasks 字段；不进入 conversation history。

        private object TaskCreate(string subject, string description)
        {
            if (string.IsNullOrEmpty(subject))
                return new { error = "subject is required" };
            lock (_tasksLock)
            {
                var t = new TaskItem
                {
                    Id = _nextTaskId++,
                    Subject = subject,
                    Description = description ?? "",
                    Status = "pending",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                _tasks.Add(t);
                return new
                {
                    id = t.Id,
                    subject = t.Subject,
                    description = t.Description,
                    status = t.Status,
                    hint = "Task created. Use task_update(id, status) to mark progress (pending | in_progress | completed)."
                };
            }
        }

        private object TaskUpdate(int id, string status, string subject, string description)
        {
            if (id <= 0)
                return new { error = "id is required" };
            lock (_tasksLock)
            {
                var t = _tasks.Find(x => x.Id == id);
                if (t == null)
                    return new { error = $"Task {id} not found" };
                if (!string.IsNullOrEmpty(status))
                {
                    var s = status.ToLowerInvariant();
                    if (s != "pending" && s != "in_progress" && s != "completed")
                        return new { error = "status must be: pending | in_progress | completed" };
                    t.Status = s;
                }
                if (!string.IsNullOrEmpty(subject))
                    t.Subject = subject;
                if (description != null)
                    t.Description = description;
                t.UpdatedAt = DateTime.Now;
                return new
                {
                    id = t.Id,
                    subject = t.Subject,
                    description = t.Description,
                    status = t.Status,
                    updated_at = t.UpdatedAt.Value.ToString("HH:mm:ss")
                };
            }
        }

        private object TaskList()
        {
            lock (_tasksLock)
            {
                if (_tasks.Count == 0)
                    return new { count = 0, tasks = new object[0], hint = "No tasks. Use task_create to start tracking multi-step work." };
                var summary = _tasks.Select(t => new
                {
                    id = t.Id,
                    subject = t.Subject,
                    status = t.Status
                }).ToList();
                return new
                {
                    count = summary.Count,
                    pending = summary.Count(x => x.status == "pending"),
                    in_progress = summary.Count(x => x.status == "in_progress"),
                    completed = summary.Count(x => x.status == "completed"),
                    tasks = summary
                };
            }
        }

        private object TaskGet(int id)
        {
            if (id <= 0)
                return new { error = "id is required" };
            lock (_tasksLock)
            {
                var t = _tasks.Find(x => x.Id == id);
                if (t == null)
                    return new { error = $"Task {id} not found" };
                return new
                {
                    id = t.Id,
                    subject = t.Subject,
                    description = t.Description,
                    status = t.Status,
                    created_at = t.CreatedAt.ToString("HH:mm:ss"),
                    updated_at = t.UpdatedAt.HasValue ? t.UpdatedAt.Value.ToString("HH:mm:ss") : null
                };
            }
        }

        // ---- Tool Definitions ----

        private static List<Dictionary<string, object>> _cachedToolDefs;
        private static readonly object _toolDefLock = new object();

        private static List<Dictionary<string, object>> GetToolDefinitions()
        {
            lock (_toolDefLock)
            {
                if (_cachedToolDefs == null)
                    _cachedToolDefs = BuildToolDefinitions();
                // 浅拷贝：CallApiStream 会在最后一个 tool 上加 cache_control，
                // 浅拷贝只复制 List 引用，不动内部 Dictionary，加 cache_control 时
                // new Dictionary(...) 创建新对象，不影响缓存。
                return new List<Dictionary<string, object>>(_cachedToolDefs);
            }
        }

        private static List<Dictionary<string, object>> BuildToolDefinitions()
        {
            return new List<Dictionary<string, object>>
            {
                Tool("get_status", "Get controller connection status, product name, firmware version, project name", NoParams()),
                Tool("list_programs", "List all programs in the current MotionPerfect project", NoParams()),
                Tool("read_source", "Read source code of a program. The system auto-returns the FIRST CHUNK for files >200 lines or >8000 chars — the response includes totalLines and a hint when more chunks exist. You MUST check totalLines vs endLine and continue with startLine=endLine+1 to read subsequent chunks; NEVER assume the first response contains the whole file. For IEC tasks, use pouName to read a specific POU (default: first POU). IEC ST POU returns local VAR...END_VAR block (from POU's variable group) PREPENDED to the code body — write_source expects this same shape.", Props(
                    ("name", "Program name", false),
                    ("pouName", "For IEC tasks only: specific POU name to read. If omitted, reads the first POU. Use get_iec_task_detail to list available POUs.", true),
                    ("startLine", "Starting 1-based line number (optional, for pagination)", true),
                    ("endLine", "Ending 1-based line number (optional, for pagination)", true)
                )),
                Tool("get_iec_task_detail", "Get the internal structure of an IEC task: list of POUs (programs), task/global/retain variable blocks (VAR...END_VAR text), and the user data type table. Use this first when working with an IEC task to see its full contents.", Props("name", "IEC task name")),
                Tool("write_source", "Write full source code to a program (auto-backup, requires confirmation). For IEC tasks, use pouName to target a specific POU — POU is auto-created (as ST/Main) if it does not exist.", Props(
                    ("name", "Program name", false),
                    ("sourceCode", "Full source code to write", false),
                    ("pouName", "For IEC tasks only: target POU name. Auto-created if missing. If omitted, writes to first POU (or creates MAIN).", true)
                )),
                Tool("patch_source", "Apply text-level edits to an EXISTING program by finding and replacing exact text snippets (auto-backup, requires confirmation). REQUIRES the program to already exist — cannot create new programs (use write_source or create_program first). More reliable than line-number based editing. For IEC tasks, pouName must point to an EXISTING POU.", PropsMixed(
                    ("name", "Program name", false, "string"),
                    ("pouName", "For IEC tasks only: target POU name (must exist; defaults to first POU)", true, "string"),
                    ("operations", "Array of {old_string:exact text to find in source, new_string:replacement text}. old_string must be unique in the source. If old_string is empty, new_string is appended to the end.", false, "array")
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
                Tool("lookup_command", "Look up command/keyword reference. Default returns name + signature + description (lightweight). Use full=true for complete HTML. Use library to scope to triobasic/iec/plcopen.", Props(
                    ("query", "Command name or keyword to search (e.g. MOVE, MC_MoveAbsolute, FOR)", false),
                    ("full", "Set to \"true\" to load complete HTML documentation (heavier, use only when examples or detailed parameters are needed)", true),
                    ("library", "Scope search to: triobasic | iec | plcopen (default: search all libraries)", true)
                )),
                Tool("read_skill", "Load full markdown content of a skill listed in the 'Available Skills' section of the system prompt. BLOCKING REQUIREMENT: when the user's request matches a skill's 'Use when:' description, invoke read_skill BEFORE writing any code or generating other response about the task. NEVER mention or claim to follow a skill without calling this tool first. Note: skills whose full body is already shown in the system prompt (e.g. 'Safe Coding Rules (MANDATORY)') are pre-loaded — do NOT re-read those.", Props(
                    ("name", "Skill name from Available Skills", false)
                )),
                Tool("discover_skills", "List available markdown skills with their name, description, and when_to_use trigger. Use this FIRST when you are unsure which skills exist or which one matches the user's task — cheaper than calling read_skill by trial and error. After finding a relevant skill, call read_skill(name) to load its full body.", Props(
                    ("category", "Filter by category (general | triobasic | iec | plcopen). null/empty returns all skills.", true)
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
                Tool("read_drive_params", "Read a drive parameter via DRIVE_READ", Props(
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
                Tool("list_project_items", "List all project items with name/type/group", NoParams()),
                Tool("update_memory", "Update your persistent memory (survives across sessions). Content replaces the entire memory file. Include all previous memories you want to keep plus new information. Use markdown formatting. Keep under 2000 tokens.", Props("content", "Full memory content to save")),
                Tool("task_create", "Create a new task to track a step in a multi-step workflow. Use this proactively when the user's request requires 3+ distinct steps. Each task gets a stable id you can update later.", Props(
                    ("subject", "Short imperative title (e.g. 'Fix axis 2 homing sequence')", false),
                    ("description", "What needs to be done, including acceptance criteria if relevant", true)
                )),
                Tool("task_update", "Update an existing task's status or content. Mark in_progress when starting work, completed when done. Keep exactly one task in_progress at a time when possible.", Props(
                    ("id", "Task id from task_create / task_list", false),
                    ("status", "New status: pending | in_progress | completed", true),
                    ("subject", "New subject (optional)", true),
                    ("description", "New description (optional)", true)
                )),
                Tool("task_list", "List all tasks with their current status. Use this to verify progress and pick the next task to work on.", NoParams()),
                Tool("task_get", "Get full details of a single task by id.", Props("id", "Task id")),
                Tool("enter_plan_mode", "Enter Plan Mode: all write/modify tools (write_source, patch_source, compile_program, run_program, write_vr, write_table, etc.) are blocked. Use this at the start of a non-trivial task to investigate first with read-only tools, then present a plan via exit_plan_mode for user approval before making any changes. Avoid for trivial single-step requests, AND for batch same-operation-on-many-items tasks (fix/check all programs) — those are independent sub-tasks; use `task_create` + process one at a time instead.", NoParams()),
                Tool("exit_plan_mode", "Present your plan to the user for approval and exit Plan Mode. The user will approve or reject. If rejected, Plan Mode stays active — revise the plan or do more investigation. If approved, write/modify tools become available.", Props(
                    ("plan", "The complete plan: what files/VR/programs you will change, why, and the verification steps. Be specific.", false)
                ))
            };
        }

        // ---- Tool-schema construction helpers ----

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, string[] required = null)
        {
            // 从 properties 中提取 __required（由 Props/PropsMixed 注入）
            var schema = new Dictionary<string, object>();
            var props = new Dictionary<string, object>();
            var reqList = new List<string>();
            if (required != null) reqList.AddRange(required);
            foreach (var kv in properties)
            {
                if (kv.Key == "__required")
                {
                    if (kv.Value is string[] arr) reqList.AddRange(arr);
                    continue;
                }
                props[kv.Key] = kv.Value;
            }
            schema["type"] = "object";
            schema["properties"] = props;
            if (reqList.Count > 0)
                schema["required"] = reqList.ToArray();

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
            if (required.Count > 0)
                dict["__required"] = required.ToArray();
            return dict;
        }

        // 支持 array / 其他类型字段的 schema 构造
        private static Dictionary<string, object> PropsMixed(params (string name, string desc, bool optional, string type)[] props)
        {
            var dict = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var (n, d, opt, t) in props)
            {
                dict[n] = new { type = string.IsNullOrEmpty(t) ? "string" : t, description = d };
                if (!opt) required.Add(n);
            }
            if (required.Count > 0)
                dict["__required"] = required.ToArray();
            return dict;
        }
    }
}
