using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace XiaoLv.Desktop.Core
{
    public class ContextSnapshot
    {
        public IntPtr Hwnd { get; set; }
        public string ProcessName { get; set; }
        public string AppDisplayName { get; set; }
        public string FullTitle { get; set; }
        public string CleanTitle { get; set; }
        public string Url { get; set; }

        public bool HasUrl
        {
            get { return !string.IsNullOrEmpty(Url) && (Url.StartsWith("http://") || Url.StartsWith("https://")); }
        }

        public string FormattedCitation
        {
            get
            {
                string title = !string.IsNullOrEmpty(CleanTitle) ? CleanTitle : FullTitle;
                if (string.IsNullOrEmpty(title)) title = AppDisplayName;

                if (HasUrl)
                {
                    return string.Format("[{0}]({1}) ({2})", title.Replace("[", "\\[").Replace("]", "\\]"), Url, AppDisplayName);
                }
                return string.Format("{0} ({1})", title, AppDisplayName);
            }
        }
    }

    /// <summary>
    /// 桌面外部活动与环境感知引擎（Context Perception）
    /// 自动嗅探前台活跃软件、窗口文档标题与浏览器 URL，为笔记提供情境注水
    /// </summary>
    public static class ContextPerception
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static readonly Dictionary<string, string> _friendlyAppNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "msedge", "Microsoft Edge" },
            { "chrome", "Google Chrome" },
            { "brave", "Brave Browser" },
            { "firefox", "Mozilla Firefox" },
            { "code", "VS Code" },
            { "cursor", "Cursor" },
            { "devenv", "Visual Studio" },
            { "idea64", "IntelliJ IDEA" },
            { "pycharm64", "PyCharm" },
            { "webstorm64", "WebStorm" },
            { "obsidian", "Obsidian" },
            { "typora", "Typora" },
            { "zotero", "Zotero" },
            { "sublime_text", "Sublime Text" },
            { "sumatrapdf", "SumatraPDF" },
            { "potplayer64", "PotPlayer" },
            { "potplayer", "PotPlayer" },
            { "cajviewer", "CAJViewer" },
            { "winword", "Microsoft Word" },
            { "excel", "Microsoft Excel" },
            { "powerpnt", "PowerPoint" },
            { "wps", "WPS 文字" },
            { "wpp", "WPS 演示" },
            { "et", "WPS 表格" },
            { "foxitreader", "福昕阅读器" },
            { "acrobat", "Adobe Acrobat" },
            { "wechat", "微信" },
            { "wemeetapp", "腾讯会议" },
            { "dingtalk", "钉钉" },
            { "feishu", "飞书" },
            { "notepad", "记事本" },
            { "siyuan", "思源笔记" }
        };

        private static readonly string[] _titleSuffixes = new string[]
        {
            " - Google Chrome",
            " - Microsoft​ Edge",
            " - Microsoft Edge",
            " - Brave",
            " - Visual Studio Code",
            " - Cursor",
            " - Obsidian",
            " - Typora",
            " - Zotero",
            " - Sublime Text",
            " - SumatraPDF",
            " - 微信",
            " - 飞书",
            " - 钉钉",
            " - WPS Office"
        };

        /// <summary>
        /// 0ms 纯原生 Win32 前台窗口上下文探测（耗时 <0.3ms，绝不阻塞 UI 线程）
        /// </summary>
        public static ContextSnapshot CaptureForegroundContextFast()
        {
            var snap = new ContextSnapshot
            {
                Hwnd = IntPtr.Zero,
                ProcessName = "",
                AppDisplayName = "桌面应用",
                FullTitle = "",
                CleanTitle = "",
                Url = ""
            };

            try
            {
                var hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return snap;
                snap.Hwnd = hWnd;

                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, 512);
                string title = sb.ToString().Trim();
                snap.FullTitle = title;
                snap.CleanTitle = CleanWindowTitle(title);

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid > 0)
                {
                    try
                    {
                        using (var p = Process.GetProcessById((int)pid))
                        {
                            snap.ProcessName = p.ProcessName;
                            string friendly;
                            if (_friendlyAppNames.TryGetValue(p.ProcessName, out friendly))
                            {
                                snap.AppDisplayName = friendly;
                            }
                            else
                            {
                                snap.AppDisplayName = p.ProcessName;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return snap;
        }

        public static ContextSnapshot CaptureForegroundContext(IntPtr explicitHwnd)
        {
            var snap = CaptureForegroundContextFast();
            if (explicitHwnd != IntPtr.Zero) snap.Hwnd = explicitHwnd;
            if (IsBrowserProcess(snap.ProcessName) && snap.Hwnd != IntPtr.Zero)
            {
                snap.Url = TryExtractBrowserUrl(snap.Hwnd);
            }
            return snap;
        }

        /// <summary>
        /// 捕获当前前台活动窗口的上下文快照（必须在弹出助手窗口前调用）
        /// </summary>
        public static ContextSnapshot CaptureForegroundContext()
        {
            var snap = CaptureForegroundContextFast();
            if (IsBrowserProcess(snap.ProcessName) && snap.Hwnd != IntPtr.Zero)
            {
                snap.Url = TryExtractBrowserUrl(snap.Hwnd);
            }
            return snap;
        }

        private static bool IsBrowserProcess(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;
            var name = procName.ToLowerInvariant();
            return name == "chrome" || name == "msedge" || name == "brave" || name == "firefox" || name == "arc" || name == "opera" || name == "vivaldi" || name.Contains("360") || name == "qqbrowser";
        }

        private static string CleanWindowTitle(string fullTitle)
        {
            if (string.IsNullOrEmpty(fullTitle)) return "";
            var title = fullTitle;
            foreach (var s in _titleSuffixes)
            {
                if (title.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                {
                    title = title.Substring(0, title.Length - s.Length).Trim();
                    break;
                }
            }
            return title;
        }

        /// <summary>
        /// 采用 Windows 原生 UI Automation 轻量快速探测 Chromium 浏览器地址栏 URL
        /// 零外部浏览器扩展依赖，超时 100ms 有界拦截，绝对不阻断主交互
        /// </summary>
        private static string TryExtractBrowserUrl(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "";
            string url = "";

            var thread = new Thread(() =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(hWnd);
                    if (root == null) return;

                    // 快速匹配 Edit 控件 (Chromium Omnibox)
                    var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                    var edits = root.FindAll(TreeScope.Descendants, cond);
                    if (edits == null || edits.Count == 0) return;

                    foreach (AutomationElement edit in edits)
                    {
                        object pattern;
                        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                        {
                            var vp = pattern as ValuePattern;
                            if (vp != null && !string.IsNullOrEmpty(vp.Current.Value))
                            {
                                var val = vp.Current.Value.Trim();
                                if (val.StartsWith("http://") || val.StartsWith("https://") || (val.Contains(".") && !val.Contains(" ") && val.Length > 4))
                                {
                                    if (!val.StartsWith("http://") && !val.StartsWith("https://"))
                                    {
                                        val = "https://" + val;
                                    }
                                    url = val;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            })
            { IsBackground = true };

            thread.Start();
            thread.Join(60); // 最多等待 60ms，防卡死

            return url;
        }
    }
}
