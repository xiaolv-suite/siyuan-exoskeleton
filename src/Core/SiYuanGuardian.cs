using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace XiaoLv.Desktop.Core
{
    public static class SiYuanGuardian
    {
        private static Thread _monitorThread;
        private static bool _isRunning = false;
        private static bool _lastOnlineState = false;

        public static bool IsAlive { get { return _lastOnlineState; } }

        public static Action<string, string> NotifyCallback { get; set; }

        public static void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "SiYuanGuardianThread"
            };
            _monitorThread.Start();
        }

        public static void Stop()
        {
            _isRunning = false;
        }

        private static void MonitorLoop()
        {
            while (_isRunning)
            {
                try
                {
                    string syUrl = "";
                    string token = "";
                    bool found = SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
                    bool currentOnline = false;

                    if (found && !string.IsNullOrEmpty(syUrl))
                    {
                        var res = SyKernelRequest.DoRequest(syUrl, token, "/api/system/version", "{}");
                        currentOnline = res.Success;
                    }

                    // 状态流转检测：如果从离线变成在线
                    if (currentOnline && !_lastOnlineState)
                    {
                        if (OfflineQueueManager.GetQueueCount() > 0)
                        {
                            Thread.Sleep(1200); // 稍等内核完全就绪
                            OfflineQueueManager.FlushQueue(syUrl, token, NotifyCallback);
                        }
                    }

                    _lastOnlineState = currentOnline;
                }
                catch { }

                // 每 6 秒做一次极轻量的低频心跳
                for (int i = 0; i < 60 && _isRunning; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        public static bool TryLaunchSiYuan()
        {
            try
            {
                // 1. 尝试 siyuan:// 协议唤起
                Process.Start(new ProcessStartInfo("siyuan://") { UseShellExecute = true });
                return true;
            }
            catch
            {
                // 2. 尝试常见安装路径
                string[] candidates = new string[]
                {
                    @"D:\RJ\SiYuan\SiYuan.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\SiYuan\SiYuan.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"SiYuan\SiYuan.exe")
                };

                foreach (var p in candidates)
                {
                    if (File.Exists(p))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                            return true;
                        }
                        catch { }
                    }
                }
                return false;
            }
        }
    }
}
