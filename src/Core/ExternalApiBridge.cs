using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 本地极速调用桥与 Quicker / AutoHotkey / Raycast 本地 Webhook 回路 (ExternalApiBridge)
    /// 1. 依托 .NET 内置 HttpListener 提供极速本地环回接口 (默认 127.0.0.1:43989)
    /// 2. 零外部依赖，0% 待机 CPU，响应延迟 < 1ms
    /// 3. 支持 Quicker / AutoHotkey / Stream Deck / curl / 本地脚本一键免切窗速记、意图分流与状态透视
    /// 4. 严苛本地环回安全隔离：严格绑定 127.0.0.1，拒绝任何外网/跨局域网嗅探
    /// </summary>
    public static class ExternalApiBridge
    {
        private static HttpListener _listener;
        private static Thread _listenThread;
        private static bool _isRunning = false;
        private static int _activePort = 43989;

        public static int ActivePort { get { return _activePort; } }
        public static bool IsRunning { get { return _isRunning; } }

        public static event Action<string, string> OnApiActionReceived;
        public static event Action<string, string> OnApiShowRequested;

        /// <summary>
        /// 启动本地 Webhook 监听服务
        /// </summary>
        public static void Start(int port = 43989)
        {
            if (_isRunning) return;

            // 隔离沙箱环境下自动避让为 43990 端口，杜绝与生产环境守护进程端口争抢
            var isIsolated = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIYUAN_URL"));
            if (isIsolated && port == 43989)
            {
                port = 43990;
            }

            _activePort = port;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", port));
                try { _listener.Prefixes.Add(string.Format("http://localhost:{0}/", port)); } catch { }
                _listener.Start();
                _isRunning = true;

                _listenThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "ExternalApiBridge_Listener"
                };
                _listenThread.Start();
                Logger.Info("ExternalApiBridge started on port " + port);
            }
            catch (Exception ex)
            {
                Logger.Error("ExternalApiBridge start failed on port " + port + ": " + ex.Message);
                _isRunning = false;
            }
        }

        /// <summary>
        /// 停止本地 Webhook 监听
        /// </summary>
        public static void Stop()
        {
            _isRunning = false;
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                    _listener = null;
                }
            }
            catch { }
        }

        private static void ListenLoop()
        {
            while (_isRunning && _listener != null && _listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(state => ProcessRequest(ctx));
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("ExternalApiBridge loop error: " + ex.Message);
                }
            }
        }

        private static void ProcessRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;

            try
            {
                // 1. 设置跨域与本地安全响应头
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Token, Authorization");

                if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = 200;
                    resp.Close();
                    return;
                }

                // 2. 本地安全令牌校验 (若配置了 WebhookToken)
                string configuredToken = AppSettings.Instance.WebhookToken;
                if (!string.IsNullOrEmpty(configuredToken))
                {
                    string headerToken = req.Headers["X-Token"];
                    if (string.IsNullOrEmpty(headerToken))
                    {
                        string auth = req.Headers["Authorization"];
                        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            headerToken = auth.Substring(7).Trim();
                        }
                    }
                    if (string.IsNullOrEmpty(headerToken))
                    {
                        headerToken = req.QueryString["token"];
                    }

                    if (headerToken != configuredToken)
                    {
                        WriteJsonResponse(resp, 401, new { code = 401, msg = "未提供有效本地安全鉴权令牌 (Unauthorized)" });
                        return;
                    }
                }

                // 3. 路由匹配
                string rawPath = req.Url.AbsolutePath.TrimEnd('/');
                if (string.IsNullOrEmpty(rawPath)) rawPath = "/";

                // A. 基础探针: GET /ping 或 GET /status 或 GET /
                if (rawPath == "/" || rawPath.Equals("/ping", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/status", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJsonResponse(resp, 200, new
                    {
                        code = 0,
                        msg = "思源外骨骼桌面助手在线",
                        version = AutoUpdater.CURRENT_VERSION,
                        siyuan_connected = SiYuanGuardian.IsAlive,
                        port = _activePort,
                        pid = System.Diagnostics.Process.GetCurrentProcess().Id
                    });
                    return;
                }

                // B. 免切窗速记入库: /capture 或 /api/capture (支持 GET / POST)
                if (rawPath.Equals("/capture", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/capture", StringComparison.OrdinalIgnoreCase))
                {
                    HandleCaptureRequest(req, resp);
                    return;
                }

                // C. 核心指令分流: /action 或 /api/action (支持 GET / POST)
                if (rawPath.Equals("/action", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/action", StringComparison.OrdinalIgnoreCase))
                {
                    HandleActionRequest(req, resp);
                    return;
                }

                // D. 窗口唤出与搜索直达: /show 或 /api/show 或 /api/window/show
                if (rawPath.Equals("/show", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/show", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/window/show", StringComparison.OrdinalIgnoreCase))
                {
                    HandleShowRequest(req, resp);
                    return;
                }

                // E. 今日时间轴速览: /today 或 /api/today 或 /api/today/timeline
                if (rawPath.Equals("/today", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/today", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/today/timeline", StringComparison.OrdinalIgnoreCase))
                {
                    HandleTodayTimelineRequest(resp);
                    return;
                }

                // F. 全库待办提取: /tasks 或 /api/tasks
                if (rawPath.Equals("/tasks", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/tasks", StringComparison.OrdinalIgnoreCase))
                {
                    HandleTasksRequest(resp);
                    return;
                }

                // G. 全库块检索: /search 或 /api/search
                if (rawPath.Equals("/search", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/api/search", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSearchRequest(req, resp);
                    return;
                }

                // 未匹配到路由
                WriteJsonResponse(resp, 404, new { code = 404, msg = "未找到对应 API 路由: " + rawPath });
            }
            catch (Exception ex)
            {
                Logger.Error("ExternalApiBridge request error: " + ex.Message);
                try
                {
                    WriteJsonResponse(resp, 500, new { code = 500, msg = "内部处理异常: " + ex.Message });
                }
                catch { }
            }
        }

        private static void HandleCaptureRequest(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string text = "";
            string title = "";
            string url = "";
            string app = "Quicker/External";
            string heading = "";
            string targetDoc = "";

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                string body = ReadRequestBody(req);
                if (!string.IsNullOrEmpty(body))
                {
                    string trimmed = body.Trim().Trim('\uFEFF');
                    if (trimmed.StartsWith("{"))
                    {
                        var dict = JsonHelper.ParseDict(trimmed);
                        text = JsonHelper.GetString(dict, "text", "");
                        if (string.IsNullOrEmpty(text)) text = JsonHelper.GetString(dict, "content", "");
                        title = JsonHelper.GetString(dict, "title", "");
                        url = JsonHelper.GetString(dict, "url", "");
                        app = JsonHelper.GetString(dict, "app", "Quicker/External");
                        heading = JsonHelper.GetString(dict, "heading", "");
                        targetDoc = JsonHelper.GetString(dict, "target_doc", "");
                    }
                    else
                    {
                        var form = ParseFormUrlEncoded(trimmed);
                        form.TryGetValue("text", out text);
                        if (string.IsNullOrEmpty(text)) form.TryGetValue("content", out text);
                        form.TryGetValue("title", out title);
                        form.TryGetValue("url", out url);
                        form.TryGetValue("app", out app);
                        form.TryGetValue("heading", out heading);
                        form.TryGetValue("target_doc", out targetDoc);
                    }

                    // 三层保底正则提取
                    if (string.IsNullOrEmpty(text))
                    {
                        var mText = Regex.Match(trimmed, @"""(?:text|content)""\s*:\s*""((?:\\.|[^""\\])*)""", RegexOptions.IgnoreCase);
                        if (mText.Success)
                        {
                            try { text = Regex.Unescape(mText.Groups[1].Value); } catch { text = mText.Groups[1].Value; }
                        }
                    }
                }
            }

            // 兜底允许从 QueryString 补齐 (例如 POST /api/capture?text=foo)
            if (string.IsNullOrEmpty(text) && req.QueryString != null)
            {
                text = req.QueryString["text"];
                if (string.IsNullOrEmpty(text)) text = req.QueryString["content"];
                if (string.IsNullOrEmpty(title)) title = req.QueryString["title"] ?? "";
                if (string.IsNullOrEmpty(url)) url = req.QueryString["url"] ?? "";
                if (string.IsNullOrEmpty(app) || app == "Quicker/External") app = req.QueryString["app"] ?? "Quicker/External";
                if (string.IsNullOrEmpty(heading)) heading = req.QueryString["heading"] ?? "";
                if (string.IsNullOrEmpty(targetDoc)) targetDoc = req.QueryString["target_doc"] ?? "";
            }

            if (string.IsNullOrEmpty(text))
            {
                WriteJsonResponse(resp, 400, new { code = 400, msg = "text 参数不能为空" });
                return;
            }

            // 构造速记上下文执行
            var ctx = new DesktopStepContext("SYS", text);
            ctx.SetVarValue("来源应用", app);
            if (!string.IsNullOrEmpty(title)) ctx.SetVarValue("来源标题", title);
            if (!string.IsNullOrEmpty(url)) ctx.SetVarValue("来源链接", url);
            if (!string.IsNullOrEmpty(heading)) ctx.SetVarValue("目标小节", heading);
            if (!string.IsNullOrEmpty(targetDoc)) ctx.SetVarValue("目标文档", targetDoc);

            ctx.OnNotificationRequested += (t, m) => Program.SafeNotify(t, m);
            SmartCaptureMain.Exec(ctx);

            WriteJsonResponse(resp, 200, new
            {
                code = 0,
                msg = "速记已成功写入思源日记",
                text = text,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        private static void HandleActionRequest(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string cmd = "";
            string text = "";

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                string body = ReadRequestBody(req);
                if (!string.IsNullOrEmpty(body))
                {
                    var dict = JsonHelper.ParseDict(body);
                    cmd = JsonHelper.GetString(dict, "cmd", "");
                    text = JsonHelper.GetString(dict, "text", "");
                }
            }
            else
            {
                cmd = req.QueryString["cmd"] ?? "";
                text = req.QueryString["text"] ?? "";
            }

            if (string.IsNullOrEmpty(cmd))
            {
                WriteJsonResponse(resp, 400, new { code = 400, msg = "cmd 指令不能为空" });
                return;
            }

            if (OnApiActionReceived != null)
            {
                try { OnApiActionReceived(cmd, text); } catch { }
            }

            var ctx = new DesktopStepContext(cmd, text);
            ctx.OnNotificationRequested += (t, m) => Program.SafeNotify(t, m);
            bool ok = ActionDispatcher.Dispatch(cmd, ctx);

            WriteJsonResponse(resp, 200, new
            {
                code = ok ? 0 : 1,
                msg = ok ? "指令已分发执行" : "未识别的动作指令",
                cmd = cmd
            });
        }

        private static void HandleShowRequest(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string panel = "master";
            string query = "";

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                string body = ReadRequestBody(req);
                if (!string.IsNullOrEmpty(body))
                {
                    var dict = JsonHelper.ParseDict(body);
                    panel = JsonHelper.GetString(dict, "panel", "master");
                    query = JsonHelper.GetString(dict, "query", "");
                }
            }
            else
            {
                panel = req.QueryString["panel"] ?? "master";
                query = req.QueryString["query"] ?? "";
            }

            if (OnApiShowRequested != null)
            {
                try { OnApiShowRequested(panel, query); } catch { }
            }

            WriteJsonResponse(resp, 200, new
            {
                code = 0,
                msg = "窗口展现已激活",
                panel = panel,
                query = query
            });
        }

        private static void HandleTodayTimelineRequest(HttpListenerResponse resp)
        {
            var now = DateTime.Now;
            string dateStr = now.ToString("yyyy-MM-dd");

            ThreadPool.QueueUserWorkItem(async s =>
            {
                try
                {
                    string sql = string.Format(
                        "SELECT b.id, b.content, b.type, b.subtype, b.created, b.updated FROM blocks b JOIN blocks d ON d.id = b.root_id WHERE d.type = 'd' AND d.hpath LIKE '%{0}%' AND b.type NOT IN ('d') ORDER BY b.created ASC",
                        dateStr);
                    var res = await SiYuanClient.PostAsync("/api/query/sql", new { stmt = sql });

                    WriteJsonResponse(resp, 200, new
                    {
                        code = 0,
                        msg = "获取成功",
                        date = dateStr,
                        count = res != null && res.Data != null ? ((System.Collections.IList)res.Data).Count : 0,
                        data = res != null ? res.Data : null
                    });
                }
                catch (Exception ex)
                {
                    WriteJsonResponse(resp, 500, new { code = 500, msg = ex.Message });
                }
            });
        }

        private static void HandleTasksRequest(HttpListenerResponse resp)
        {
            ThreadPool.QueueUserWorkItem(async s =>
            {
                try
                {
                    string sql = "SELECT b.id, b.content, b.root_id, b.updated FROM blocks b WHERE b.subtype = 't' AND b.markdown NOT LIKE '%[x]%' ORDER BY b.updated DESC LIMIT 50";
                    var res = await SiYuanClient.PostAsync("/api/query/sql", new { stmt = sql });

                    WriteJsonResponse(resp, 200, new
                    {
                        code = 0,
                        msg = "获取成功",
                        count = res != null && res.Data != null ? ((System.Collections.IList)res.Data).Count : 0,
                        data = res != null ? res.Data : null
                    });
                }
                catch (Exception ex)
                {
                    WriteJsonResponse(resp, 500, new { code = 500, msg = ex.Message });
                }
            });
        }

        private static void HandleSearchRequest(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string q = req.QueryString["q"] ?? "";
            if (string.IsNullOrEmpty(q) && req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                string body = ReadRequestBody(req);
                var dict = JsonHelper.ParseDict(body);
                q = JsonHelper.GetString(dict, "q", "");
            }

            if (string.IsNullOrEmpty(q))
            {
                WriteJsonResponse(resp, 400, new { code = 400, msg = "搜索关键词 q 不能为空" });
                return;
            }

            string safeQ = q.Replace("'", "''");
            ThreadPool.QueueUserWorkItem(async s =>
            {
                try
                {
                    string sql = string.Format(
                        "SELECT b.id, b.content, b.root_id, b.hpath, b.type, b.updated FROM blocks b WHERE b.content LIKE '%{0}%' AND b.type NOT IN ('d') ORDER BY b.updated DESC LIMIT 20",
                        safeQ);
                    var res = await SiYuanClient.PostAsync("/api/query/sql", new { stmt = sql });

                    WriteJsonResponse(resp, 200, new
                    {
                        code = 0,
                        msg = "搜索完成",
                        query = q,
                        data = res != null ? res.Data : null
                    });
                }
                catch (Exception ex)
                {
                    WriteJsonResponse(resp, 500, new { code = 500, msg = ex.Message });
                }
            });
        }

        private static string ReadRequestBody(HttpListenerRequest req)
        {
            try
            {
                if (req.InputStream == null) return "";
                using (var ms = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = req.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }
                    if (ms.Length == 0) return "";
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                Logger.Error("ReadRequestBody exception: " + ex.Message);
                return "";
            }
        }

        private static Dictionary<string, string> ParseFormUrlEncoded(string formBody)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(formBody)) return dict;
            var pairs = formBody.Split('&');
            foreach (var p in pairs)
            {
                if (string.IsNullOrEmpty(p)) continue;
                int eq = p.IndexOf('=');
                if (eq > 0)
                {
                    string k = Uri.UnescapeDataString(p.Substring(0, eq).Replace('+', ' '));
                    string v = Uri.UnescapeDataString(p.Substring(eq + 1).Replace('+', ' '));
                    dict[k] = v;
                }
            }
            return dict;
        }

        private static void WriteJsonResponse(HttpListenerResponse resp, int statusCode, object data)
        {
            try
            {
                resp.StatusCode = statusCode;
                resp.ContentType = "application/json; charset=utf-8";
                string json = JsonHelper.ToJson(data);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                resp.ContentLength64 = bytes.Length;
                resp.OutputStream.Write(bytes, 0, bytes.Length);
                resp.OutputStream.Close();
            }
            catch { }
        }
    }
}
