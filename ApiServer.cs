using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace TrioAI.MPPlugIn
{
    internal class ApiServer : IDisposable
    {
        private const string Prefix = "http://localhost:9090/";
        private HttpListener _listener;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private bool _running;

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _running = true;
            _listener.BeginGetContext(OnRequest, null);
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        private void OnRequest(IAsyncResult ar)
        {
            if (!_running) return;
            HttpListenerContext ctx;
            try { ctx = _listener.EndGetContext(ar); }
            catch { return; }

            if (_running)
                _listener.BeginGetContext(OnRequest, null);

            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                SendJson(ctx, 500, new { error = ex.Message });
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.Trim('/').ToLowerInvariant();
            var method = ctx.Request.HttpMethod;

            // Handle CORS preflight
            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                ctx.Response.Close();
                return;
            }

            // Route matching
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0)
            {
                SendJson(ctx, 200, new { status = "ok", version = "1.6" });
                return;
            }

            // /api/...
            if (segments.Length >= 1 && segments[0] == "api")
            {
                var route = segments.Length >= 2 ? segments[1] : "";

                switch (route)
                {
                    case "status":
                        HandleMethod(ctx, method, "GET", () => Handlers.GetStatus());
                        break;

                    case "test":
                        HandleMethod(ctx, method, "GET", () =>
                        {
                            var (pass, fail, report) = AiService.RunOptimizationTests();
                            return new { pass, fail, report };
                        });
                        break;

                    case "project":
                        if (method == "POST" && segments.Length == 2)
                            HandleMethod(ctx, method, "POST", () => Handlers.CreateProject());
                        else if (method == "PUT" && segments.Length == 2)
                            HandleMethod(ctx, method, "PUT", () => Handlers.SaveProject());
                        else if (method == "POST" && segments.Length == 3 && segments[2] == "open")
                            HandleBody(ctx, method, body => Handlers.OpenProject(body));
                        else if (method == "GET" && segments.Length == 3 && segments[2] == "items")
                            HandleMethod(ctx, method, "GET", () => Handlers.ListProjectItems());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "programs":
                        if (segments.Length == 2)
                        {
                            if (method == "GET")
                                HandleMethod(ctx, method, "GET", () => Handlers.ListPrograms());
                            else if (method == "POST")
                                HandleBody(ctx, method, body => Handlers.CreateProgram(body));
                        }
                        else if (segments.Length >= 3)
                        {
                            var name = Uri.UnescapeDataString(segments[2]);
                            if (segments.Length == 3)
                            {
                                if (method == "DELETE")
                                    HandleMethod(ctx, method, "DELETE", () => Handlers.DeleteProgram(name));
                                else if (method == "PUT")
                                    HandleBody(ctx, method, body => Handlers.RenameProgram(name, body));
                            }
                            else if (segments.Length == 4)
                            {
                                var action = segments[3];
                                switch (action)
                                {
                                    case "source":
                                        if (method == "GET")
                                            HandleMethod(ctx, method, "GET", () => Handlers.ReadSource(name));
                                        else if (method == "PUT")
                                            HandleBody(ctx, method, body => Handlers.WriteSource(name, body));
                                        else if (method == "PATCH")
                                            HandleBody(ctx, method, body => Handlers.PatchSource(name, body));
                                        else
                                            SendJson(ctx, 405, new { error = "Method not allowed" });
                                        break;
                                    case "open":
                                        HandleMethod(ctx, method, "POST", () => Handlers.OpenProgram(name));
                                        break;
                                    case "rename":
                                        if (method == "PUT")
                                            HandleBody(ctx, method, body => Handlers.RenameProgram(name, body));
                                        else
                                            SendJson(ctx, 405, new { error = "Method not allowed" });
                                        break;
                                    case "copy":
                                        HandleBody(ctx, method, body => Handlers.CopyProgram(name, body));
                                        break;
                                    case "upload":
                                        HandleMethod(ctx, method, "POST", () => Handlers.Upload(name));
                                        break;
                                    case "download":
                                        HandleMethod(ctx, method, "POST", () => Handlers.Download(name));
                                        break;
                                    case "compile":
                                        HandleMethod(ctx, method, "POST", () => Handlers.Compile(name));
                                        break;
                                    case "run":
                                        HandleBody(ctx, method, body => Handlers.RunProgram(name, body));
                                        break;
                                    case "stop":
                                        HandleBody(ctx, method, body => Handlers.StopProgram(name, body));
                                        break;
                                    case "process":
                                        if (method == "GET")
                                            HandleMethod(ctx, method, "GET", () => Handlers.GetProgramProcess(name));
                                        else if (method == "PUT")
                                            HandleBody(ctx, method, body => Handlers.SetProgramProcess(name, body));
                                        else
                                            SendJson(ctx, 405, new { error = "Method not allowed" });
                                        break;
                                    case "breakpoints":
                                        if (method == "GET")
                                            HandleMethod(ctx, method, "GET", () => Handlers.ListBreakpoints(name));
                                        else if (method == "DELETE")
                                            HandleMethod(ctx, method, "DELETE", () => Handlers.ClearAllBreakpoints(name));
                                        else
                                            SendJson(ctx, 405, new { error = "Method not allowed" });
                                        break;
                                    case "breakpoint":
                                        if (method == "POST" || method == "PUT")
                                            HandleBody(ctx, method, body => Handlers.SetBreakpoint(name, body));
                                        else if (method == "DELETE")
                                            HandleBody(ctx, method, body => Handlers.SetBreakpoint(name,
                                                new Dictionary<string, object> { { "line", body.ContainsKey("line") ? body["line"] : 0 }, { "enable", false } }));
                                        else
                                            SendJson(ctx, 405, new { error = "Method not allowed" });
                                        break;
                                    default:
                                        SendJson(ctx, 404, new { error = "Not found" });
                                        break;
                                }
                            }
                        }
                        break;

                    case "vr":
                        if (segments.Length >= 2)
                        {
                            int addr;
                            if (!int.TryParse(segments[2], out addr))
                            {
                                SendJson(ctx, 400, new { error = "Invalid address" });
                                return;
                            }
                            if (method == "GET")
                            {
                                var countStr = ctx.Request.QueryString["count"] ?? "1";
                                int count;
                                int.TryParse(countStr, out count);
                                if (count <= 0) count = 1;
                                HandleMethod(ctx, method, "GET", () => Handlers.ReadVR(addr, count));
                            }
                            else if (method == "PUT")
                                HandleBody(ctx, method, body => Handlers.WriteVR(addr, body));
                        }
                        break;

                    case "table":
                        if (segments.Length >= 2)
                        {
                            int addr;
                            if (!int.TryParse(segments[2], out addr))
                            {
                                SendJson(ctx, 400, new { error = "Invalid address" });
                                return;
                            }
                            if (method == "GET")
                            {
                                var countStr = ctx.Request.QueryString["count"] ?? "1";
                                int count;
                                int.TryParse(countStr, out count);
                                if (count <= 0) count = 1;
                                HandleMethod(ctx, method, "GET", () => Handlers.ReadTable(addr, count));
                            }
                            else if (method == "PUT")
                                HandleBody(ctx, method, body => Handlers.WriteTable(addr, body));
                        }
                        break;

                    case "descriptors":
                        HandleMethod(ctx, method, "GET", () => Handlers.ListDescriptors());
                        break;

                    case "axes":
                        if (segments.Length == 2)
                        {
                            HandleMethod(ctx, method, "GET", () => Handlers.ListAxes());
                        }
                        else if (segments.Length == 3)
                        {
                            int axisIndex;
                            if (!int.TryParse(segments[2], out axisIndex))
                            {
                                SendJson(ctx, 400, new { error = "Invalid axis index" });
                                return;
                            }
                            HandleMethod(ctx, method, "GET", () => Handlers.GetAxisDetail(axisIndex));
                        }
                        else
                        {
                            SendJson(ctx, 404, new { error = "Not found" });
                        }
                        break;

                    case "sysvars":
                        if (segments.Length == 2)
                            HandleMethod(ctx, method, "GET", () => Handlers.GetSystemVariables());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "sysvar":
                        if (segments.Length == 3)
                        {
                            var svName = Uri.UnescapeDataString(segments[2]);
                            if (method == "GET")
                                HandleMethod(ctx, method, "GET", () => Handlers.ReadSysVar(svName));
                            else if (method == "PUT")
                                HandleBody(ctx, method, body => Handlers.WriteSysVar(svName, body));
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "io":
                        if (segments.Length == 3)
                        {
                            if (segments[2] == "digital" && method == "GET")
                                HandleMethod(ctx, method, "GET", () => Handlers.ListDigitalIO());
                            else if (segments[2] == "analogue" && method == "GET")
                                HandleMethod(ctx, method, "GET", () => Handlers.ListAnalogueIO());
                            else
                                SendJson(ctx, 404, new { error = "Not found" });
                        }
                        else if (segments.Length == 5)
                        {
                            int ioIndex;
                            if (!int.TryParse(segments[4], out ioIndex))
                            {
                                SendJson(ctx, 400, new { error = "Invalid IO index" });
                                return;
                            }
                            if (segments[2] == "digital")
                            {
                                if (segments[3] != "line") { SendJson(ctx, 404, new { error = "Not found" }); return; }
                                if (method == "GET")
                                    HandleMethod(ctx, method, "GET", () => Handlers.ReadDigitalIO(ioIndex));
                                else if (method == "PUT")
                                    HandleBody(ctx, method, body => Handlers.WriteDigitalIO(ioIndex, body));
                                else
                                    SendJson(ctx, 405, new { error = "Method not allowed" });
                            }
                            else if (segments[2] == "analogue")
                            {
                                if (segments[3] != "line") { SendJson(ctx, 404, new { error = "Not found" }); return; }
                                if (method == "GET")
                                    HandleMethod(ctx, method, "GET", () => Handlers.ReadAnalogueIO(ioIndex));
                                else if (method == "PUT")
                                    HandleBody(ctx, method, body => Handlers.WriteAnalogueIO(ioIndex, body));
                                else
                                    SendJson(ctx, 405, new { error = "Method not allowed" });
                            }
                            else
                                SendJson(ctx, 404, new { error = "Not found" });
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "plugins":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => Handlers.ListAttachedPlugins());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "oscilloscope":
                        if (segments.Length == 3 && segments[2] == "open" && method == "POST")
                            HandleMethod(ctx, method, "POST", () => Handlers.OpenOscilloscope());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "robots":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => Handlers.ListRobots());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "recipes":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => Handlers.ListRecipes());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "alarms":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => Handlers.ListAlarms());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "remote-devices":
                        if (segments.Length == 2 && method == "GET")
                            HandleMethod(ctx, method, "GET", () => Handlers.ListRemoteDevices());
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "msbus":
                        if (segments.Length == 3 && segments[2] == "scan" && method == "GET")
                        {
                            int slot = 0;
                            var slotStr = ctx.Request.QueryString["slot"];
                            if (!string.IsNullOrEmpty(slotStr)) int.TryParse(slotStr, out slot);
                            HandleMethod(ctx, method, "GET", () => Handlers.ScanMsBus(slot));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "ethercat":
                        if (segments.Length == 3 && segments[2] == "devices" && method == "GET")
                        {
                            int slot = 0;
                            var slotStr = ctx.Request.QueryString["slot"];
                            if (!string.IsNullOrEmpty(slotStr)) int.TryParse(slotStr, out slot);
                            HandleMethod(ctx, method, "GET", () => Handlers.ScanEtherCAT(slot));
                        }
                        else if (segments.Length == 3 && segments[2] == "sdo")
                        {
                            if (method == "GET")
                            {
                                int ecSlot = 0; uint pos = 0, idx = 0, sub = 0;
                                int.TryParse(ctx.Request.QueryString["slot"] ?? "0", out ecSlot);
                                uint.TryParse(ctx.Request.QueryString["position"] ?? "0", out pos);
                                uint.TryParse(ctx.Request.QueryString["index"] ?? "0",
                                              System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out idx);
                                uint.TryParse(ctx.Request.QueryString["subindex"] ?? "0", out sub);
                                var t = ctx.Request.QueryString["type"] ?? "uint16";
                                HandleMethod(ctx, method, "GET", () => Handlers.EtherCATReadSDO(ecSlot, pos, idx, sub, t));
                            }
                            else if (method == "PUT")
                                HandleBody(ctx, method, body => Handlers.EtherCATWriteSDOFromDict(body));
                            else
                                SendJson(ctx, 405, new { error = "Method not allowed" });
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "drive":
                        if (segments.Length == 4 && method == "GET")
                        {
                            int drvAxis, drvAddr;
                            if (!int.TryParse(segments[2], out drvAxis))
                            {
                                SendJson(ctx, 400, new { error = "Invalid axis" });
                                return;
                            }
                            if (!TryParseAddr(segments[3], out drvAddr))
                            {
                                SendJson(ctx, 400, new { error = "Invalid address" });
                                return;
                            }
                            var ndStr = ctx.Request.QueryString["nd"];
                            int nd = 4;
                            if (!string.IsNullOrEmpty(ndStr)) int.TryParse(ndStr, out nd);
                            HandleMethod(ctx, method, "GET", () => Handlers.ReadDriveParam(drvAxis, drvAddr, nd));
                        }
                        else if (segments.Length == 4 && method == "PUT")
                        {
                            int drvAxis, drvAddr;
                            if (!int.TryParse(segments[2], out drvAxis))
                            {
                                SendJson(ctx, 400, new { error = "Invalid axis" });
                                return;
                            }
                            if (!TryParseAddr(segments[3], out drvAddr))
                            {
                                SendJson(ctx, 400, new { error = "Invalid address" });
                                return;
                            }
                            HandleBody(ctx, method, body => Handlers.WriteDriveParam(drvAxis, drvAddr, body));
                        }
                        else
                            SendJson(ctx, 404, new { error = "Not found" });
                        break;

                    case "chat":
                        HandleMethod(ctx, method, "POST", () => Handlers.OpenChat());
                        break;

                    case "validate_basic":
                        if (method != "POST") { SendJson(ctx, 405, new { error = "Method not allowed" }); return; }
                        HandleBody(ctx, method, body =>
                        {
                            object v;
                            string code = body.TryGetValue("code", out v) && v != null ? v.ToString() : "";
                            return AiService.ValidateTrioBasicCodePublic(code);
                        });
                        break;

                    case "search_code":
                        if (method != "GET") { SendJson(ctx, 405, new { error = "Method not allowed" }); return; }
                        {
                            var queryStr = ctx.Request.QueryString["query"] ?? "";
                            var csStr = ctx.Request.QueryString["caseSensitive"];
                            bool cs;
                            bool.TryParse(csStr, out cs);
                            HandleMethod(ctx, method, "GET", () => Handlers.SearchCode(queryStr, cs));
                        }
                        break;

                    case "events":
                        if (method != "GET") { SendJson(ctx, 405, new { error = "Method not allowed" }); return; }
                        long sinceTicks = 0;
                        var sinceStr = ctx.Request.QueryString["since"];
                        if (!string.IsNullOrEmpty(sinceStr))
                            long.TryParse(sinceStr, out sinceTicks);
                        HandleMethod(ctx, method, "GET", () => Handlers.GetEvents(sinceTicks));
                        break;

                    case "processes":
                        if (segments.Length == 2 && method == "GET")
                        {
                            HandleMethod(ctx, method, "GET", () => Handlers.ListProcesses());
                        }
                        else if (segments.Length == 4 && segments[3] == "variables" && method == "GET")
                        {
                            int pid;
                            if (!int.TryParse(segments[2], out pid))
                            {
                                SendJson(ctx, 400, new { error = "Invalid pid" });
                                return;
                            }
                            var variable = ctx.Request.QueryString["name"];
                            var program = ctx.Request.QueryString["program"];
                            HandleMethod(ctx, method, "GET", () => Handlers.GetProcessVariable(pid, program, variable));
                        }
                        else
                        {
                            SendJson(ctx, 404, new { error = "Not found" });
                        }
                        break;

                    default:
                        SendJson(ctx, 404, new { error = "Not found" });
                        break;
                }
            }
            else
            {
                SendJson(ctx, 404, new { error = "Not found" });
            }
        }

        private void HandleMethod(HttpListenerContext ctx, string actual, string expected, Func<object> handler)
        {
            if (actual != expected)
            {
                SendJson(ctx, 405, new { error = "Method not allowed" });
                return;
            }
            var result = DispatcherHelper.Invoke(handler);
            SendJson(ctx, 200, result);
        }

        private static bool TryParseAddr(string s, out int value)
        {
            // Accept "0x4000", "0X4000", "4000" (hex), or decimal "16384"
            if (string.IsNullOrEmpty(s)) { value = 0; return false; }
            var str = s.Trim();
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(str.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out value);
            // No prefix: try decimal first, then hex (for bare hex like "4000" interpreted as hex per TrioBASIC convention)
            if (int.TryParse(str, out value)) return true;
            return int.TryParse(str, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private void HandleBody(HttpListenerContext ctx, string method, Func<Dictionary<string, object>, object> handler)
        {
            string bodyText;
            using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                bodyText = reader.ReadToEnd();

            var body = string.IsNullOrEmpty(bodyText) ? new Dictionary<string, object>() : _json.Deserialize<Dictionary<string, object>>(bodyText);
            var result = DispatcherHelper.Invoke(() => handler(body));
            SendJson(ctx, 200, result);
        }

        private void SendJson(HttpListenerContext ctx, int statusCode, object data)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            var bytes = Encoding.UTF8.GetBytes(_json.Serialize(data));
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        public void Dispose() => Stop();
    }
}
