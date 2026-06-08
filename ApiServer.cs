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
                SendJson(ctx, 200, new { status = "ok", version = "1.1" });
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

                    case "project":
                        if (method == "POST" && segments.Length == 2)
                            HandleMethod(ctx, method, "POST", () => Handlers.CreateProject());
                        else if (method == "PUT" && segments.Length == 2)
                            HandleMethod(ctx, method, "PUT", () => Handlers.SaveProject());
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
                        HandleMethod(ctx, method, "GET", () => Handlers.ListAxes());
                        break;

                    case "chat":
                        HandleMethod(ctx, method, "POST", () => Handlers.OpenChat());
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
