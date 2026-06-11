using System;
using System.Collections.Generic;
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

        private string ExecuteTool(string name, Dictionary<string, object> input)
        {
            try
            {
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
                    var argsPreview = _json.Serialize(input);
                    var accepted = OnConfirmWrite?.Invoke(name, argsPreview) ?? false;
                    if (!accepted)
                        return "User rejected this operation.";
                }

                var result = DispatcherHelper.Invoke(() => DispatchTool(name, input));
                var resultStr = _json.Serialize(result);
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: {Truncate(resultStr, 300)}");
                return _json.Serialize(result);
            }
            catch (Exception ex)
            {
                if (_showToolStatus)
                    OnToolStatus?.Invoke($"{name}: ERROR - {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        private object DispatchTool(string name, Dictionary<string, object> input)
        {
            switch (name)
            {
                case "get_status": return Handlers.GetStatus();
                case "list_programs": return Handlers.ListPrograms();
                case "read_source": return Handlers.ReadSource(GetStr(input, "name"), input);
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
                                    errs.AddRange(ValidateTrioBasicCode(newStr));
                            }
                        }
                        if (errs.Count > 0)
                            return new { error = "BLOCKED by TrioBASIC validation:\n  " + string.Join("\n  ", errs) };
                    }
                    return Handlers.PatchSource(progName, input);
                }
                case "read_vr": return Handlers.ReadVR(GetInt(input, "address"), GetInt(input, "count"));
                case "write_vr": return Handlers.WriteVR(GetInt(input, "address"), input);
                case "read_table": return Handlers.ReadTable(GetInt(input, "address"), GetInt(input, "count"));
                case "write_table": return Handlers.WriteTable(GetInt(input, "address"), input);
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
                case "read_drive_param": return Handlers.ReadDriveParam(GetInt(input, "axis"), GetInt(input, "address"), GetInt(input, "nd", 4));
                case "write_drive_param": return Handlers.WriteDriveParam(GetInt(input, "axis"), GetInt(input, "address"), input);
                case "scan_ethercat": return Handlers.ScanEtherCAT(GetInt(input, "slot", 0));
                case "read_ethercat_sdo":
                    return Handlers.EtherCATReadSDO(GetInt(input, "slot", 0),
                                                    (uint)GetLong(input, "position"),
                                                    (uint)GetLong(input, "index"),
                                                    (uint)GetLong(input, "subindex"),
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
                case "search_code": return Handlers.SearchCode(GetStr(input, "query"), GetBool(input, "caseSensitive"));
                default: return new { error = $"Unknown tool: {name}" };
            }
        }

        // ---- Tool Definitions ----

        private static List<Dictionary<string, object>> GetToolDefinitions()
        {
            return new List<Dictionary<string, object>>
            {
                Tool("get_status", "Get controller connection status, product name, firmware version, project name", NoParams()),
                Tool("list_programs", "List all programs in the current MotionPerfect project", NoParams()),
                Tool("read_source", "Read source code of a program. For files >200 lines or >8000 chars, auto-returns first chunk + totalLines + hint; use startLine/endLine to paginate subsequent chunks. For IEC tasks, use pouName to read a specific POU (default: first POU). IEC ST POU returns local VAR...END_VAR block (from POU's variable group) PREPENDED to the code body — write_source expects this same shape.", Props(
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
                Tool("patch_source", "Apply text-level edits to a program by finding and replacing exact text snippets (auto-backup, requires confirmation). More reliable than line-number based editing. For IEC tasks, pouName must point to an EXISTING POU.", PropsMixed(
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
                Tool("read_skill", "Load full markdown content of a skill listed in the 'Available Skills' section of the system prompt.", Props(
                    ("name", "Skill name from Available Skills", false)
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
                Tool("list_project_items", "List all project items with name/type/group", NoParams())
            };
        }

        // ---- Tool-schema construction helpers ----

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, string[] required = null)
        {
            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties }
            };
            if (required != null && required.Length > 0)
                schema["required"] = required;

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
            return dict;
        }

        // 支持 array / 其他类型字段的 schema 构造
        private static Dictionary<string, object> PropsMixed(params (string name, string desc, bool optional, string type)[] props)
        {
            var dict = new Dictionary<string, object>();
            foreach (var (n, d, opt, t) in props)
                dict[n] = new { type = string.IsNullOrEmpty(t) ? "string" : t, description = d };
            return dict;
        }
    }
}
