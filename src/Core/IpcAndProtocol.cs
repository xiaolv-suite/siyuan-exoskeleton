using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XiaoLv.Desktop.Core
{
    public static class ProtocolRegistrar
    {
        /// <summary>
        /// 将 siyuan-exoskeleton:// 与 xiaolv:// 协议注册到当前用户注册表
        /// </summary>
        public static void RegisterProtocol()
        {
            RegisterProtocolNamed("siyuan-exoskeleton");
            RegisterProtocolNamed("xiaolv");
        }

        private static void RegisterProtocolNamed(string protocolName)
        {
            try
            {
                var exePath = Application.ExecutablePath;
                string regRoot = @"Software\Classes\" + protocolName;
                using (var key = Registry.CurrentUser.CreateSubKey(regRoot))
                {
                    if (key == null) return;
                    key.SetValue("", "URL:SiYuan Exoskeleton Protocol");
                    key.SetValue("URL Protocol", "");

                    using (var defIcon = key.CreateSubKey("DefaultIcon"))
                    {
                        if (defIcon != null) defIcon.SetValue("", "\"" + exePath + "\",0");
                    }

                    using (var shellKey = key.CreateSubKey(@"shell\open\command"))
                    {
                        if (shellKey != null) shellKey.SetValue("", "\"" + exePath + "\" \"%1\"");
                    }
                }
            }
            catch { }
        }

        public static bool IsProtocolRegistered()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\siyuan-exoskeleton", false))
                {
                    if (key != null) return true;
                }
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\xiaolv", false))
                {
                    return key != null;
                }
            }
            catch { return false; }
        }
    }

    public static class IpcManager
    {
        private const string PIPE_NAME = "XiaoLvDesktop_IPC_Pipe";

        /// <summary>
        /// 尝试向已在运行的实例发送指令，若发送成功则返回 true，说明已有实例在跑
        /// </summary>
        public static bool SendToRunningInstance(string message, int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = 1200;
            try
            {
                using (var client = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out))
                {
                    client.Connect(timeoutMs);
                    using (var writer = new StreamWriter(client, Encoding.UTF8))
                    {
                        // 统一单行信封传输，避免多行截断
                        var singleLine = message.Replace("\r", " ").Replace("\n", "\\n");
                        writer.WriteLine(singleLine);
                        writer.Flush();
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SendToRunningInstance(string message)
        {
            return SendToRunningInstance(message, 1200);
        }

        /// <summary>
        /// 后台命名管道监听服务端
        /// </summary>
        public class Server : IDisposable
        {
            private bool _running = true;
            private Thread _listenThread;

            public event Action<string> OnMessageReceived;

            public void Start()
            {
                _running = true;
                _listenThread = new Thread(ListenLoop);
                _listenThread.IsBackground = true;
                _listenThread.Name = "XiaoLv_IpcServerThread";
                _listenThread.Start();
            }

            private void ListenLoop()
            {
                while (_running)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(PIPE_NAME, PipeDirection.In, 10, PipeTransmissionMode.Byte))
                        {
                            pipe.WaitForConnection();
                            using (var reader = new StreamReader(pipe, Encoding.UTF8))
                            {
                                var line = reader.ReadLine();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    var msg = line.Trim();
                                    if (!msg.StartsWith("{") && msg.Contains("\\n"))
                                    {
                                        msg = msg.Replace("\\n", "\n");
                                    }
                                    if (OnMessageReceived != null)
                                    {
                                        OnMessageReceived(msg);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        if (!_running) break;
                        Thread.Sleep(80);
                    }
                }
            }

            public void Dispose()
            {
                _running = false;
                try
                {
                    if (_listenThread != null && _listenThread.IsAlive)
                    {
                        _listenThread.Abort();
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Gitee 免服务器云端版本检查探针
    /// </summary>
    public static class AutoUpdater
    {
        public const string CURRENT_VERSION = "2.9.0";
        private const string GITEE_VERSION_URL = "https://gitee.com/xiaolv-desktop/releases/raw/master/version.json";
        private const string GITHUB_PROXY_URL = "https://ghproxy.net/https://raw.githubusercontent.com/xiaolv-desktop/releases/master/version.json";

        public class UpdateInfo
        {
            public bool HasUpdate;
            public string LatestVersion;
            public string Changelog;
            public string DownloadUrl;
        }

        public static UpdateInfo CheckForUpdate()
        {
            var info = new UpdateInfo();
            info.HasUpdate = false;
            info.LatestVersion = CURRENT_VERSION;

            string json = "";
            try
            {
                json = FetchUrlWithTimeout(GITEE_VERSION_URL, 3000);
            }
            catch
            {
                try { json = FetchUrlWithTimeout(GITHUB_PROXY_URL, 3000); } catch { }
            }

            if (string.IsNullOrEmpty(json)) return info;

            try
            {
                var vMatch = Regex.Match(json, "\"latestVersion\"\\s*:\\s*\"([^\"]+)\"");
                var cMatch = Regex.Match(json, "\"changelog\"\\s*:\\s*\"([^\"]+)\"");
                var dMatch = Regex.Match(json, "\"downloadUrl\"\\s*:\\s*\"([^\"]+)\"");

                if (vMatch.Success)
                {
                    var remoteVer = vMatch.Groups[1].Value.Trim();
                    info.LatestVersion = remoteVer;
                    info.Changelog = cMatch.Success ? cMatch.Groups[1].Value.Replace("\\n", "\n") : "";
                    info.DownloadUrl = dMatch.Success ? dMatch.Groups[1].Value : "";

                    if (CompareVersion(remoteVer, CURRENT_VERSION) > 0)
                    {
                        info.HasUpdate = true;
                    }
                }
            }
            catch { }

            return info;
        }

        private static string FetchUrlWithTimeout(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.UserAgent = "XiaoLvDesktop/" + CURRENT_VERSION;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        public static int CompareVersion(string v1, string v2)
        {
            try
            {
                var p1 = v1.Trim('v', 'V').Split('.');
                var p2 = v2.Trim('v', 'V').Split('.');
                for (int i = 0; i < Math.Max(p1.Length, p2.Length); i++)
                {
                    int n1 = i < p1.Length ? int.Parse(p1[i]) : 0;
                    int n2 = i < p2.Length ? int.Parse(p2[i]) : 0;
                    if (n1 > n2) return 1;
                    if (n1 < n2) return -1;
                }
                return 0;
            }
            catch { return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
