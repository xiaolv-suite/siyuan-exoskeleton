using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class SiYuanConnection
    {
        public string Url { get; set; }
        public string Token { get; set; }
    }

    public class SiYuanResponse
    {
        public int Code { get; set; }
        public string Msg { get; set; }
        public object Data { get; set; }
        public string RawText { get; set; }
        public bool Success { get { return Code == 0; } }
        public string Error { get { return Code != 0 ? (string.IsNullOrEmpty(Msg) ? "Kernel Error (" + Code + ")" : Msg) : ""; } }
    }

    public class NotebookInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Closed { get; set; }
    }

    /// <summary>
    /// 现代化思源笔记原生内核客户端（高并发连接池、异步管道与自愈探测）
    /// 彻底取代 Quicker 时代的单线程 HttpWebRequest 与脆弱正则表达式提取
    /// </summary>
    public static class SiYuanClient
    {
        private static readonly HttpClient _httpClient;
        private static string _cachedUrl = "";
        private static string _cachedToken = "";
        private static int _lastCacheTicks = 0;
        private const int CacheTtlMs = 60000;
        private static readonly object _lock = new object();

        private static string _discoveredWorkspaceDir = @"D:\SIYUAN";
        public static string DiscoveredWorkspaceDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIYUAN_URL")))
                {
                    var explicitWorkspace = Environment.GetEnvironmentVariable("XIAOLV_WORKSPACE_DIR");
                    if (string.IsNullOrWhiteSpace(explicitWorkspace) || !Path.IsPathRooted(explicitWorkspace)) return "";
                    try
                    {
                        var resolvedWorkspace = Path.GetFullPath(explicitWorkspace);
                        return Directory.Exists(resolvedWorkspace) ? resolvedWorkspace : "";
                    }
                    catch { return ""; }
                }
                return _discoveredWorkspaceDir;
            }
            private set { _discoveredWorkspaceDir = value; }
        }

        public static string WorkspaceAssetsDir
        {
            get
            {
                bool explicitConnection = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIYUAN_URL"));
                string workspace = DiscoveredWorkspaceDir;
                if (explicitConnection)
                {
                    if (string.IsNullOrEmpty(workspace))
                    {
                        throw new InvalidOperationException("SIYUAN_URL requires an existing absolute XIAOLV_WORKSPACE_DIR for local workspace access.");
                    }
                    return Path.Combine(workspace, "data", "assets");
                }
                try
                {
                    var dir = Path.Combine(workspace, "data", "assets");
                    if (Directory.Exists(dir)) return dir;
                }
                catch { }
                return @"D:\SIYUAN\data\assets";
            }
        }

        static SiYuanClient()
        {
            try
            {
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
            }
            catch { }

            var handler = new HttpClientHandler
            {
                Proxy = null,
                UseProxy = false,
                UseCookies = false
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        public static void InvalidateCache()
        {
            lock (_lock)
            {
                _cachedUrl = "";
                _cachedToken = "";
                _lastCacheTicks = 0;
            }
        }

        public static SiYuanConnection GetConnectionInfo(bool forceRefresh = false)
        {
            lock (_lock)
            {
                // 1. 系统环境变量显式指定（沙箱隔离靶场优先）
                var envUrl = Environment.GetEnvironmentVariable("SIYUAN_URL");
                var envTok = Environment.GetEnvironmentVariable("SIYUAN_TOKEN");
                if (!string.IsNullOrWhiteSpace(envUrl))
                {
                    // An explicit endpoint must never borrow credentials or a workspace from a production installation.
                    return new SiYuanConnection { Url = envUrl.Trim().TrimEnd('/'), Token = (envTok ?? "").Trim() };
                }

                int now = Environment.TickCount;
                if (!forceRefresh && !string.IsNullOrEmpty(_cachedUrl) && (now - _lastCacheTicks) < CacheTtlMs)
                {
                    return new SiYuanConnection { Url = _cachedUrl, Token = _cachedToken };
                }

                string url = "";
                string token = "";
                DiscoverEnv(ref url, ref token, forceRefresh);

                _cachedUrl = url;
                _cachedToken = token;
                _lastCacheTicks = now;
                return new SiYuanConnection { Url = url, Token = token };
            }
        }

        private static bool DiscoverEnv(ref string url, ref string token, bool forceRefresh)
        {
            bool found = false;

            // 1. 系统环境变量
            try
            {
                var envUrl = Environment.GetEnvironmentVariable("SIYUAN_URL");
                var envToken = Environment.GetEnvironmentVariable("SIYUAN_TOKEN");
                if (!string.IsNullOrWhiteSpace(envUrl)) { url = envUrl.TrimEnd('/'); found = true; }
                if (!string.IsNullOrWhiteSpace(envToken)) { token = envToken.Trim(); found = true; }
                if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(token)) return true;
            }
            catch { }

            // 2. 读取 %APPDATA%\siyuan\env
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var envPath = Path.Combine(appData, "siyuan", "env");
                if (File.Exists(envPath))
                {
                    foreach (var line in File.ReadAllLines(envPath))
                    {
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = line.Substring(0, eq).Trim();
                        var v = line.Substring(eq + 1).Trim();
                        if (k == "SIYUAN_TOKEN" && (forceRefresh || string.IsNullOrWhiteSpace(token))) { token = v; found = true; }
                        if (k == "SIYUAN_URL" && (forceRefresh || string.IsNullOrWhiteSpace(url))) { url = v.TrimEnd('/'); found = true; }
                    }
                }
            }
            catch { }

            // 3. 从工作区 conf/conf.json 嗅探 Token
            if (string.IsNullOrWhiteSpace(token) || forceRefresh)
            {
                var candidatePaths = new List<string>
                {
                    Path.Combine("D:\\SIYUAN", "conf", "conf.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SiYuan", "conf", "conf.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "siyuan", "conf.json"),
                    Path.Combine("C:\\SIYUAN", "conf", "conf.json"),
                    Path.Combine("E:\\SIYUAN", "conf", "conf.json")
                };

                foreach (var confPath in candidatePaths)
                {
                    if (File.Exists(confPath))
                    {
                        try
                        {
                            var text = File.ReadAllText(confPath, Encoding.UTF8);
                            var dict = JsonHelper.ParseDict(text);
                            var apiDict = JsonHelper.GetDict(dict, "api");
                            var tok = apiDict != null ? JsonHelper.GetString(apiDict, "token") : "";
                            if (string.IsNullOrEmpty(tok)) tok = JsonHelper.GetString(dict, "token");

                            if (string.IsNullOrEmpty(tok))
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(text, "\"token\"\\s*:\\s*\"([^\"]+)\"");
                                if (m.Success) tok = m.Groups[1].Value;
                            }

                            if (!string.IsNullOrEmpty(tok))
                            {
                                token = tok;
                                try
                                {
                                    var parentConf = Path.GetDirectoryName(confPath);
                                    if (!string.IsNullOrEmpty(parentConf))
                                    {
                                        var wsParent = Directory.GetParent(parentConf);
                                        if (wsParent != null && Directory.Exists(wsParent.FullName))
                                        {
                                            DiscoveredWorkspaceDir = wsParent.FullName;
                                        }
                                    }
                                }
                                catch { }
                                found = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            // 4. 端口快速嗅探（支持 IPv4 与 IPv6 双环回自适应探测）
            if (string.IsNullOrWhiteSpace(url) || forceRefresh)
            {
                string matchedUrl = "";
                int[] ports = new int[] { 6806, 3727, 6808, 2542 };
                string[] hosts = new string[] { "127.0.0.1", "[::1]" };
                foreach (var h in hosts)
                {
                    foreach (var pt in ports)
                    {
                        var testUrl = "http://" + h + ":" + pt;
                        if (ProbePort(testUrl, token))
                        {
                            matchedUrl = testUrl;
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
                if (string.IsNullOrEmpty(matchedUrl))
                {
                    matchedUrl = DiscoverDynamicKernelPort(token);
                }
                url = string.IsNullOrEmpty(matchedUrl) ? "http://127.0.0.1:6806" : matchedUrl;
            }

            return found;
        }

        private static bool ProbePort(string baseUrl, string token)
        {
            try
            {
                var endpoint = string.IsNullOrEmpty(token) ? "/api/system/version" : "/api/notebook/lsNotebooks";
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + endpoint))
                {
                    if (!string.IsNullOrEmpty(token))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
                    }
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    using (var resp = _httpClient.SendAsync(req).GetAwaiter().GetResult())
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            return text.Contains("\"code\":0");
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static string DiscoverDynamicKernelPort(string token)
        {
            try
            {
                var procs = Process.GetProcessesByName("SiYuan-Kernel");
                if (procs == null || procs.Length == 0) return "";

                var pidSet = new HashSet<int>();
                foreach (var p in procs)
                {
                    try { pidSet.Add(p.Id); } catch { }
                }
                if (pidSet.Count == 0) return "";

                var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                string output;
                using (var proc = Process.Start(psi))
                {
                    output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(1500);
                }

                var candidatePorts = new List<int>();
                foreach (var rawLine in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim();
                    if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase) || !line.Contains("LISTENING")) continue;

                    var parts = System.Text.RegularExpressions.Regex.Split(line, "\\s+");
                    if (parts.Length < 5) continue;

                    int pid;
                    if (int.TryParse(parts[4], out pid) && pidSet.Contains(pid))
                    {
                        var localAddr = parts[1];
                        int colon = localAddr.LastIndexOf(':');
                        if (colon > 0)
                        {
                            int port;
                            if (int.TryParse(localAddr.Substring(colon + 1), out port) && port > 0 && port < 65536)
                            {
                                if (!candidatePorts.Contains(port)) candidatePorts.Add(port);
                            }
                        }
                    }
                }

                candidatePorts.Sort();
                foreach (var pt in candidatePorts)
                {
                    var u = "http://127.0.0.1:" + pt;
                    if (ProbePort(u, token)) return u;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 异步 POST 请求
        /// </summary>
        public static async Task<SiYuanResponse> PostAsync(string endpoint, object payload)
        {
            var info = GetConnectionInfo(false);
            return await PostAsync(info.Url, info.Token, endpoint, payload);
        }

        /// <summary>
        /// 异步获取当前打开的笔记本列表
        /// </summary>
        public static async Task<List<NotebookInfo>> GetNotebooksAsync()
        {
            var list = new List<NotebookInfo>();
            try
            {
                var res = await PostAsync("/api/notebook/lsNotebooks", new { });
                if (res != null && res.Success && res.Data != null)
                {
                    var dict = res.Data as Dictionary<string, object>;
                    if (dict != null)
                    {
                        var nbs = JsonHelper.GetList(dict, "notebooks");
                        if (nbs != null)
                        {
                            foreach (var it in nbs)
                            {
                                var nObj = it as Dictionary<string, object>;
                                if (nObj != null)
                                {
                                    list.Add(new NotebookInfo
                                    {
                                        Id = JsonHelper.GetString(nObj, "id"),
                                        Name = JsonHelper.GetString(nObj, "name"),
                                        Closed = JsonHelper.GetBool(nObj, "closed")
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public static async Task<SiYuanResponse> PostAsync(string url, string token, string endpoint, object payload)
        {
            if (string.IsNullOrEmpty(url))
            {
                var info = GetConnectionInfo(false);
                url = info.Url;
                if (string.IsNullOrEmpty(token)) token = info.Token;
            }

            var res = await ExecuteHttpRequestAsync(url, token, endpoint, payload);

            // 1. 令牌失效自愈
            if (res.Code == 401 || res.Code == 403)
            {
                InvalidateCache();
                var info = GetConnectionInfo(true);
                res = await ExecuteHttpRequestAsync(info.Url, info.Token, endpoint, payload);
            }
            // 2. 防暴力封禁自愈避让（IPv4/IPv6 动态穿透）
            else if (!res.Success && (res.Msg.Contains("认证失败次数过多") || res.Msg.Contains("稍后重试")))
            {
                string altUrl = url.Contains("127.0.0.1") ? url.Replace("127.0.0.1", "[::1]") : url.Replace("[::1]", "127.0.0.1");
                var altRes = await ExecuteHttpRequestAsync(altUrl, token, endpoint, payload);
                if (altRes.Success)
                {
                    lock (_lock) { _cachedUrl = altUrl; }
                    res = altRes;
                }
            }

            return res;
        }

        /// <summary>
        /// 同步 POST 请求（兼顾既有同步调用链）
        /// </summary>
        public static SiYuanResponse Post(string endpoint, object payload)
        {
            return Task.Run(() => PostAsync(endpoint, payload)).GetAwaiter().GetResult();
        }

        public static SiYuanResponse Post(string url, string token, string endpoint, object payload)
        {
            return Task.Run(() => PostAsync(url, token, endpoint, payload)).GetAwaiter().GetResult();
        }

        private static async Task<SiYuanResponse> ExecuteHttpRequestAsync(string url, string token, string endpoint, object payload)
        {
            if (string.IsNullOrEmpty(endpoint))
            {
                return new SiYuanResponse { Code = 400, Msg = "端点不能为空", RawText = "" };
            }
            if (!endpoint.StartsWith("/")) endpoint = "/" + endpoint;

            try
            {
                string jsonBody = payload is string ? (string)payload : JsonHelper.ToJson(payload);
                if (string.IsNullOrWhiteSpace(jsonBody)) jsonBody = "{}";

                using (var req = new HttpRequestMessage(HttpMethod.Post, url + endpoint))
                {
                    if (!string.IsNullOrEmpty(token))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
                    }
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    using (var resp = await _httpClient.SendAsync(req).ConfigureAwait(false))
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        // 结构化解析
                        var dict = JsonHelper.ParseDict(text);
                        int code = JsonHelper.GetInt(dict, "code", (int)resp.StatusCode);
                        string msg = JsonHelper.GetString(dict, "msg", "");
                        object data;
                        dict.TryGetValue("data", out data);

                        if (resp.IsSuccessStatusCode && !dict.ContainsKey("code"))
                        {
                            code = 0;
                        }

                        Logger.Info("HTTP POST " + url + endpoint + " -> " + code + (code != 0 ? (" (" + msg + ")") : ""));

                        return new SiYuanResponse
                        {
                            Code = code,
                            Msg = msg,
                            Data = data,
                            RawText = text
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HTTP POST " + url + endpoint + " exception", ex);
                return new SiYuanResponse
                {
                    Code = -1,
                    Msg = "连接思源异常 (" + url + "): " + ex.Message,
                    RawText = ""
                };
            }
        }

        /// <summary>
        /// 执行 SQL 查询并返回字典列表
        /// </summary>
        public static async Task<List<Dictionary<string, object>>> SqlAsync(string sqlQuery)
        {
            var res = await PostAsync("/api/query/sql", new { stmt = sqlQuery }).ConfigureAwait(false);
            var resultList = new List<Dictionary<string, object>>();
            if (res.Success && res.Data != null)
            {
                var list = JsonHelper.GetList(new Dictionary<string, object> { { "d", res.Data } }, "d");
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        if (item is Dictionary<string, object>)
                        {
                            resultList.Add((Dictionary<string, object>)item);
                        }
                    }
                }
            }
            return resultList;
        }

        /// <summary>
        /// 列出笔记本列表
        /// </summary>
        public static async Task<List<NotebookInfo>> LsNotebooksAsync()
        {
            var res = await PostAsync("/api/notebook/lsNotebooks", new { }).ConfigureAwait(false);
            var notebooks = new List<NotebookInfo>();
            if (res.Success && res.Data != null)
            {
                var dict = res.Data as Dictionary<string, object>;
                var nbList = JsonHelper.GetList(dict, "notebooks");
                if (nbList != null)
                {
                    foreach (var item in nbList)
                    {
                        var d = item as Dictionary<string, object>;
                        if (d != null)
                        {
                            notebooks.Add(new NotebookInfo
                            {
                                Id = JsonHelper.GetString(d, "id"),
                                Name = JsonHelper.GetString(d, "name"),
                                Closed = JsonHelper.GetBool(d, "closed", false)
                            });
                        }
                    }
                }
            }
            return notebooks;
        }

        /// <summary>
        /// 极速推送思源右上角系统通知
        /// </summary>
        public static async Task<bool> PushNoticeAsync(string message, int timeoutMs = 5000)
        {
            var res = await PostAsync("/api/notification/pushNotice", new
            {
                msg = message,
                timeout = timeoutMs
            }).ConfigureAwait(false);
            return res.Success;
        }
    }
}
