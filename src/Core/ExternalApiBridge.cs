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

	                // H. 网站收藏中枢: /api/bookmark/web
	                if (rawPath.Equals("/api/bookmark/web", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/bookmark/web", StringComparison.OrdinalIgnoreCase))
	                {
	                    HandleWebBookmarkRequest(req, resp);
	                    return;
	                }

	                // I. 文件夹导航中枢: /api/bookmark/folder
	                if (rawPath.Equals("/api/bookmark/folder", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/bookmark/folder", StringComparison.OrdinalIgnoreCase))
	                {
	                    HandleFolderBookmarkRequest(req, resp);
	                    return;
	                }

	                // J. 本地目录秒跳穿透: /api/navigate
	                if (rawPath.Equals("/api/navigate", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/navigate", StringComparison.OrdinalIgnoreCase))
	                {
	                    HandleNavigateRequest(req, resp);
	                    return;
	                }

	                // K. 全景心流专注: /api/focus
	                if (rawPath.Equals("/api/focus", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/focus", StringComparison.OrdinalIgnoreCase))
	                {
	                    HandleFocusRequest(req, resp);
	                    return;
	                }

		                // L. 任务打勾完成: /api/task/complete
		                if (rawPath.Equals("/api/task/complete", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/task/complete", StringComparison.OrdinalIgnoreCase))
		                {
		                    HandleTaskCompleteRequest(req, resp);
		                    return;
		                }

		                // M. 思源配置与模型预设中枢: /api/profile
		                if (rawPath.Equals("/api/profile", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/profile", StringComparison.OrdinalIgnoreCase))
		                {
		                    HandleProfileRequest(req, resp);
		                    return;
		                }

			                // N. 萃取当前实时配置为预设: /api/profile/capture
			                if (rawPath.Equals("/api/profile/capture", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/profile/capture", StringComparison.OrdinalIgnoreCase))
			                {
			                    HandleProfileCaptureRequest(req, resp);
			                    return;
			                }

			                // O. 全景通知与事件流: /api/notify
			                if (rawPath.Equals("/api/notify", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/notify", StringComparison.OrdinalIgnoreCase))
			                {
			                    HandleNotifyRequest(req, resp);
			                    return;
			                }

				                // P. 通知日报沉淀流水: /api/notify/journal
				                if (rawPath.Equals("/api/notify/journal", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/notify/journal", StringComparison.OrdinalIgnoreCase))
				                {
				                    HandleNotifyJournalRequest(req, resp);
				                    return;
				                }

					                // Q. 全景启动台大盘与极速启动: /api/launchpad
					                if (rawPath.Equals("/api/launchpad", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/launchpad", StringComparison.OrdinalIgnoreCase))
					                {
					                    HandleLaunchpadRequest(req, resp);
					                    return;
					                }

						                // R. 剪贴板批量拼版工作台: /api/clip/merge
						                if (rawPath.Equals("/api/clip/merge", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/clip/merge", StringComparison.OrdinalIgnoreCase))
						                {
						                    HandleClipMergeRequest(req, resp);
						                    return;
						                }

							                // S. 双向知识网络星轨拓扑全景: /api/graph/radial
							                if (rawPath.Equals("/api/graph/radial", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/graph/radial", StringComparison.OrdinalIgnoreCase))
							                {
							                    HandleRadialGraphRequest(req, resp);
							                    return;
							                }

							                // T. 全库知识体检与系统诊断: /api/doctor/diagnose
							                if (rawPath.Equals("/api/doctor/diagnose", StringComparison.OrdinalIgnoreCase) || rawPath.Equals("/doctor/diagnose", StringComparison.OrdinalIgnoreCase))
							                {
							                    HandleDoctorDiagnoseRequest(req, resp);
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

		        private static void HandleWebBookmarkRequest(HttpListenerRequest req, HttpListenerResponse resp)
		        {
		            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
		            {
		                string body = ReadRequestBody(req);
		                var dict = JsonHelper.ParseDict(body);
		                var entry = new WebBookmarkEntry
		                {
		                    Title = JsonHelper.GetString(dict, "title"),
		                    Url = JsonHelper.GetString(dict, "url"),
		                    Category = JsonHelper.GetString(dict, "category", "常用"),
		                    Tags = JsonHelper.GetString(dict, "tags"),
		                    Shortcut = JsonHelper.GetString(dict, "shortcut"),
		                    Notes = JsonHelper.GetString(dict, "notes")
		                };

		                ThreadPool.QueueUserWorkItem(async s =>
		                {
		                    bool ok = await WebBookmarkHub.SaveBookmarkAsync(entry);
		                    WriteJsonResponse(resp, 200, new { code = ok ? 0 : 1, msg = ok ? "网站收藏成功" : "收藏失败", entry = entry });
		                });
		                return;
		            }

		            // GET: 搜索网站收藏
		            string q = GetQueryStringUtf8(req, "q");
		            ThreadPool.QueueUserWorkItem(async s =>
		            {
		                var all = await WebBookmarkHub.LoadBookmarksAsync();
		                var filtered = WebBookmarkHub.Search(all, q);
		                WriteJsonResponse(resp, 200, new { code = 0, count = filtered.Count, data = filtered });
		            });
		        }

		        private static void HandleFolderBookmarkRequest(HttpListenerRequest req, HttpListenerResponse resp)
		        {
		            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
		            {
		                string body = ReadRequestBody(req);
		                var dict = JsonHelper.ParseDict(body);
		                var entry = new FolderBookmarkEntry
		                {
		                    Path = JsonHelper.GetString(dict, "path"),
		                    Alias = JsonHelper.GetString(dict, "alias"),
		                    Shortcut = JsonHelper.GetString(dict, "shortcut"),
		                    Tags = JsonHelper.GetString(dict, "tags"),
		                    SiyuanDocTitle = JsonHelper.GetString(dict, "doc_title")
		                };

		                ThreadPool.QueueUserWorkItem(async s =>
		                {
		                    bool ok = await FolderBookmarkHub.SaveFolderAsync(entry);
		                    WriteJsonResponse(resp, 200, new { code = ok ? 0 : 1, msg = ok ? "文件夹收藏成功" : "收藏失败", entry = entry });
		                });
		                return;
		            }

		            // GET: 搜索文件夹收藏
		            string q = GetQueryStringUtf8(req, "q");
		            ThreadPool.QueueUserWorkItem(async s =>
		            {
		                var all = await FolderBookmarkHub.LoadFoldersAsync();
		                var filtered = FolderBookmarkHub.Search(all, q);
		                WriteJsonResponse(resp, 200, new { code = 0, count = filtered.Count, data = filtered });
		            });
		        }

	        private static void HandleNavigateRequest(HttpListenerRequest req, HttpListenerResponse resp)
	        {
	            string path = "";
	            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
	            {
	                string body = ReadRequestBody(req);
	                var dict = JsonHelper.ParseDict(body);
	                path = JsonHelper.GetString(dict, "path");
	            }
	            else
	            {
	                path = req.QueryString["path"] ?? "";
	            }

	            if (string.IsNullOrEmpty(path))
	            {
	                WriteJsonResponse(resp, 400, new { code = 400, msg = "path 参数不能为空" });
	                return;
	            }

	            bool ok = FolderBookmarkHub.NavigateToFolder(path);
	            WriteJsonResponse(resp, 200, new { code = ok ? 0 : 1, msg = ok ? "导航成功" : "路径不存在或跳转失败", path = path });
	        }

	        private static void HandleFocusRequest(HttpListenerRequest req, HttpListenerResponse resp)
	        {
	            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
	            {
	                string body = ReadRequestBody(req);
	                var dict = JsonHelper.ParseDict(body);
	                int mins = JsonHelper.GetInt(dict, "minutes", 25);
	                string task = JsonHelper.GetString(dict, "task", "心流专注");
	                string bId = JsonHelper.GetString(dict, "task_block_id", "");
	                string doc = JsonHelper.GetString(dict, "doc_title", "");
	                bool autoCheck = JsonHelper.GetBool(dict, "auto_complete", true);

	                bool ok = FlowTimerManager.StartTimer(mins, task, bId, doc, autoCheck);
	                WriteJsonResponse(resp, 200, new { code = ok ? 0 : 1, msg = ok ? "心流番茄钟已开启" : "启动失败", minutes = mins, task = task });
	                return;
	            }

	            // GET: 获取全景专注状态
	            var status = DockTomatoBridge.GetIntegratedFocusStatus();
	            WriteJsonResponse(resp, 200, new { code = 0, msg = "获取成功", data = status });
	        }

	        private static void HandleTaskCompleteRequest(HttpListenerRequest req, HttpListenerResponse resp)
	        {
	            string id = "";
	            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
	            {
	                string body = ReadRequestBody(req);
	                var dict = JsonHelper.ParseDict(body);
	                id = JsonHelper.GetString(dict, "id");
	            }
	            else
	            {
	                id = req.QueryString["id"] ?? "";
	            }

	            if (string.IsNullOrEmpty(id))
	            {
	                WriteJsonResponse(resp, 400, new { code = 400, msg = "id 参数不能为空" });
	                return;
	            }

		            ThreadPool.QueueUserWorkItem(async s =>
		            {
		                bool ok = await FlowTimerManager.CompleteAssociatedTaskAsync(id);
		                WriteJsonResponse(resp, 200, new { code = ok ? 0 : 1, msg = ok ? "任务已打勾完成" : "任务完成失败或未找到待办块", id = id });
		            });
		        }

		        private static void HandleProfileRequest(HttpListenerRequest req, HttpListenerResponse resp)
		        {
		            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
		            {
		                string body = ReadRequestBody(req);
		                var dict = JsonHelper.ParseDict(body);
		                var entry = new SiYuanProfileEntry
		                {
		                    Name = JsonHelper.GetString(dict, "name"),
		                    Type = JsonHelper.GetString(dict, "type", "s3"),
		                    Description = JsonHelper.GetString(dict, "description")
		                };
		                var configDict = JsonHelper.GetDict(dict, "configData");
		                if (configDict != null) entry.ConfigData = configDict;

		                ThreadPool.QueueUserWorkItem(async s =>
		                {
		                    bool ok = await SiYuanProfileHub.SaveProfileAsync(entry);
		                    WriteJsonResponse(resp, 200, new { code = ok ? 0 : 1, msg = ok ? "配置预设已保存" : "保存失败", entry = entry });
		                });
		                return;
		            }

		            // GET: 检索配置预设
		            string q = GetQueryStringUtf8(req, "q");
		            string type = req.QueryString["type"] ?? "all";
		            ThreadPool.QueueUserWorkItem(async s =>
		            {
		                var all = await SiYuanProfileHub.LoadProfilesAsync(type);
		                var filtered = SiYuanProfileHub.Search(all, q, type);
		                WriteJsonResponse(resp, 200, new { code = 0, count = filtered.Count, data = filtered });
		            });
		        }

		        private static void HandleProfileCaptureRequest(HttpListenerRequest req, HttpListenerResponse resp)
		        {
		            string type = req.QueryString["type"] ?? "s3";
		            string name = GetQueryStringUtf8(req, "name");
			            var captured = SiYuanProfileHub.CaptureCurrentLiveProfile(type, name);
			            WriteJsonResponse(resp, 200, new { code = 0, msg = "萃取成功", data = captured });
			        }

			        private static void HandleNotifyRequest(HttpListenerRequest req, HttpListenerResponse resp)
			        {
			            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
			            {
			                string body = ReadRequestBody(req);
			                var dict = JsonHelper.ParseDict(body);
			                string title = JsonHelper.GetString(dict, "title", "外部通知");
			                string message = JsonHelper.GetString(dict, "message", "");
			                if (string.IsNullOrEmpty(message)) message = JsonHelper.GetString(dict, "text", "");
			                string level = JsonHelper.GetString(dict, "level", "info");
			                string source = JsonHelper.GetString(dict, "source", "webhook");

			                var item = OmniNotificationHub.Push(title, message, level, source);
			                WriteJsonResponse(resp, 200, new { code = 0, msg = "通知已记录", item = item });
			                return;
			            }

			            // GET
			            string q = GetQueryStringUtf8(req, "q");
			            string levelFilter = req.QueryString["level"] ?? "all";
			            string sourceFilter = req.QueryString["source"] ?? "all";
			            var list = OmniNotificationHub.GetHistory(levelFilter, sourceFilter, q);
			            WriteJsonResponse(resp, 200, new { code = 0, count = list.Count, data = list });
			        }

			        private static void HandleNotifyJournalRequest(HttpListenerRequest req, HttpListenerResponse resp)
			        {
			            var list = OmniNotificationHub.GetHistory("all", "all", "");
			            string md = OmniNotificationHub.BuildJournalMarkdown(list);
			            WriteJsonResponse(resp, 200, new { code = 0, markdown = md, count = list.Count });
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

	        private static void HandleLaunchpadRequest(HttpListenerRequest req, HttpListenerResponse resp)
	        {
	            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
	            {
	                string body = ReadRequestBody(req);
	                var dict = JsonHelper.ParseDict(body);
	                string type = JsonHelper.GetString(dict, "type", "web");
	                string target = JsonHelper.GetString(dict, "target");

	                if (string.IsNullOrEmpty(target))
	                {
	                    WriteJsonResponse(resp, 400, new { code = 400, msg = "target 参数不能为空" });
	                    return;
	                }

	                bool ok = false;
	                if (type.Equals("folder", StringComparison.OrdinalIgnoreCase))
	                {
	                    ok = FolderBookmarkHub.NavigateToFolder(target);
	                }
	                else
	                {
	                    try
	                    {
	                        System.Diagnostics.Process.Start(target);
	                        ok = true;
	                    }
	                    catch { }
	                }
	                WriteJsonResponse(resp, 200, new { code = ok ? 0 : 1, msg = ok ? "启动成功" : "启动失败", type = type, target = target });
	                return;
	            }

	            // GET: 聚合获取网站与文件夹启动台项
	            string q = GetQueryStringUtf8(req, "q");
	            ThreadPool.QueueUserWorkItem(async s =>
	            {
	                try
	                {
	                    var webAll = await WebBookmarkHub.LoadBookmarksAsync();
	                    var webFiltered = WebBookmarkHub.Search(webAll, q);

	                    var folderAll = await FolderBookmarkHub.LoadFoldersAsync();
	                    var folderFiltered = FolderBookmarkHub.Search(folderAll, q);

	                    WriteJsonResponse(resp, 200, new
	                    {
	                        code = 0,
	                        msg = "ok",
	                        data = new
	                        {
	                            total = webFiltered.Count + folderFiltered.Count,
	                            webCount = webFiltered.Count,
	                            folderCount = folderFiltered.Count,
	                            web = webFiltered,
	                            folder = folderFiltered
	                        }
		                    });
		                }
		                catch (Exception ex)
		                {
		                    WriteJsonResponse(resp, 500, new { code = 500, msg = "检索启动台异常: " + ex.Message });
		                }
		            });
		        }

		        private static void HandleClipMergeRequest(HttpListenerRequest req, HttpListenerResponse resp)
		        {
		            try
		            {
		                string modeStr = "bullets";
		                string title = "多源摘录汇总";
		                var snippets = new List<string>();

		                if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
		                {
		                    string body = ReadRequestBody(req);
		                    var dict = JsonHelper.ParseDict(body);
		                    modeStr = JsonHelper.GetString(dict, "mode", "bullets");
		                    title = JsonHelper.GetString(dict, "title", "多源摘录汇总");
		                    var rawList = JsonHelper.GetList(dict, "snippets");
		                    if (rawList != null)
		                    {
		                        foreach (var it in rawList) if (it != null) snippets.Add(it.ToString());
		                    }
		                }
		                else
		                {
		                    modeStr = GetQueryStringUtf8(req, "mode");
		                    if (string.IsNullOrEmpty(modeStr)) modeStr = "bullets";
		                    title = GetQueryStringUtf8(req, "title");
		                    if (string.IsNullOrEmpty(title)) title = "多源摘录汇总";
		                }

		                if (snippets.Count == 0)
		                {
		                    var recent = ClipboardWatcher.GetRecentItemsSnapshot();
		                    for (int i = 0; i < Math.Min(5, recent.Count); i++)
		                    {
		                        string t = !string.IsNullOrEmpty(recent[i].RawText) ? recent[i].RawText : recent[i].EnrichedMarkdown;
		                        if (!string.IsNullOrEmpty(t)) snippets.Add(t);
		                    }
		                }

		                MergeMode mode = MergeMode.Bullets;
		                if (modeStr.Equals("quotes", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Quotes;
		                else if (modeStr.Equals("sections", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Sections;
		                else if (modeStr.Equals("todo", StringComparison.OrdinalIgnoreCase) || modeStr.Equals("checklist", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Todo;
		                else if (modeStr.Equals("table", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Table;
		                else if (modeStr.Equals("code", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Code;
		                else if (modeStr.Equals("plain", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Plain;

		                string merged = ClipboardBatchMerger.MergeSnippets(snippets, mode, title);
		                WriteJsonResponse(resp, 200, new
		                {
		                    code = 0,
		                    msg = "ok",
		                    mode = modeStr,
		                    count = snippets.Count,
		                    markdown = merged
		                });
		            }
			            catch (Exception ex)
			            {
			                WriteJsonResponse(resp, 500, new { code = 500, msg = "拼版处理异常: " + ex.Message });
			            }
			        }

			        private static void HandleRadialGraphRequest(HttpListenerRequest req, HttpListenerResponse resp)
			        {
			            string blockId = GetQueryStringUtf8(req, "id");
			            string widthStr = req.QueryString["width"] ?? "900";
			            string heightStr = req.QueryString["height"] ?? "500";
			            int width = 900;
			            int height = 500;
			            int.TryParse(widthStr, out width);
			            int.TryParse(heightStr, out height);

			            ThreadPool.QueueUserWorkItem(async s =>
			            {
			                try
			                {
			                    string targetId = blockId;
			                    string targetTitle = "";

			                    if (string.IsNullOrEmpty(targetId))
			                    {
			                        string sql = "SELECT id, content FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 1";
			                        var rows = await SiYuanClient.SqlAsync(sql);
			                        if (rows != null && rows.Count > 0)
			                        {
			                            targetId = JsonHelper.GetString(rows[0], "id");
			                            targetTitle = JsonHelper.GetString(rows[0], "content");
			                        }
			                    }
			                    else
			                    {
			                        string sql = "SELECT id, content FROM blocks WHERE id='" + targetId.Replace("'", "''") + "' LIMIT 1";
			                        var rows = await SiYuanClient.SqlAsync(sql);
			                        if (rows != null && rows.Count > 0)
			                        {
			                            targetTitle = JsonHelper.GetString(rows[0], "content");
			                        }
			                    }

			                    if (string.IsNullOrEmpty(targetId))
			                    {
			                        WriteJsonResponse(resp, 200, new { code = 1, msg = "库中暂无可用笔记块" });
			                        return;
			                    }

				                    var links = await BiDirectionalGraphRadar.GetLinksForBlockAsync(targetId, targetTitle);
				                    string svg = GraphVisualizer.RenderFullRadialGraphSvg(targetId, links.TargetTitle, links.InboundBacklinks, links.OutboundReferences, width, height, true);

				                    WriteJsonResponse(resp, 200, new
			                    {
			                        code = 0,
			                        msg = "ok",
			                        data = new
			                        {
			                            centerId = targetId,
			                            centerTitle = links.TargetTitle,
			                            inCount = links.InboundBacklinks.Count,
			                            outCount = links.OutboundReferences.Count,
			                            total = links.TotalLinks,
			                            svg = svg,
			                            inbounds = links.InboundBacklinks,
			                            outbounds = links.OutboundReferences
			                        }
			                    });
			                }
			                catch (Exception ex)
			                {
				                    WriteJsonResponse(resp, 500, new { code = 500, msg = "生成星轨图谱异常: " + ex.Message });
				                }
			            });
			        }

			        private static void HandleDoctorDiagnoseRequest(HttpListenerRequest req, HttpListenerResponse resp)
			        {
			            ThreadPool.QueueUserWorkItem(async s =>
			            {
			                try
			                {
			                    var result = await SystemComprehensiveDoctor.RunFullAuditAsync();
			                    WriteJsonResponse(resp, 200, new
			                    {
			                        code = 0,
			                        msg = "ok",
			                        data = result
			                    });
			                }
			                catch (Exception ex)
			                {
			                    WriteJsonResponse(resp, 500, new { code = 500, msg = "体检诊断异常: " + ex.Message });
			                }
			            });
			        }

			        private static string GetQueryStringUtf8(HttpListenerRequest req, string paramName)
        {
            try
            {
                if (req.Url == null || string.IsNullOrEmpty(req.Url.Query)) return "";
                string query = req.Url.Query.TrimStart('?');
                foreach (var pair in query.Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                        if (k.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                        {
                            return Uri.UnescapeDataString(pair.Substring(eq + 1));
                        }
                    }
                }
            }
            catch { }
            return req.QueryString[paramName] ?? "";
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
