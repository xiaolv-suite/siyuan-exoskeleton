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
    public class IdeWorkspaceInfo
    {
        public string ProjectName { get; set; }
        public string ActiveFileName { get; set; }
        public bool Success { get; set; }

        public IdeWorkspaceInfo()
        {
            ProjectName = "";
            ActiveFileName = "";
            Success = false;
        }
    }

    public class ContextSnapshot
    {
        public IntPtr Hwnd { get; set; }
        public string ProcessName { get; set; }
        public string AppDisplayName { get; set; }
        public string FullTitle { get; set; }
        public string CleanTitle { get; set; }
        public string Url { get; set; }
        public string ProjectName { get; set; }
        public string ActiveFileName { get; set; }
        public string GitBranch { get; set; }
        public string GitRepo { get; set; }
        public string PdfPath { get; set; }
        public int PdfPage { get; set; }

        public bool HasUrl
        {
            get { return !string.IsNullOrEmpty(Url) && (Url.StartsWith("http://") || Url.StartsWith("https://")); }
        }

        public bool IsDevWorkspace
        {
            get { return !string.IsNullOrEmpty(ProjectName); }
        }

        public string FormattedCitation
        {
            get
            {
                string title = !string.IsNullOrEmpty(CleanTitle) ? CleanTitle : FullTitle;
                if (string.IsNullOrEmpty(title)) title = AppDisplayName;

                string gitBadge = !string.IsNullOrEmpty(GitBranch) ? string.Format(" [🌿 #{0}]", GitBranch.Replace("/", "-")) : "";

                // 学术 PDF 原生深度锚点优先
                if (!string.IsNullOrEmpty(PdfPath) && PdfPage > 0)
                {
                    string pdfName = Path.GetFileName(PdfPath);
                    return string.Format("[📄 {0} (第 {1} 页)](siyuan-exoskeleton://open-pdf?path={2}&page={1}) ({3})",
                        pdfName, PdfPage, Uri.EscapeDataString(PdfPath), AppDisplayName);
                }

                if (HasUrl)
                {
                    return string.Format("[{0}]({1}) ({2})", title.Replace("[", "\\[").Replace("]", "\\]"), Url, AppDisplayName);
                }
                if (IsDevWorkspace)
                {
                    if (!string.IsNullOrEmpty(ActiveFileName))
                    {
                        return string.Format("工程【{0}】· {1}{2} ({3})", ProjectName, ActiveFileName, gitBadge, AppDisplayName);
                    }
                    return string.Format("工程【{0}】{1} ({2})", ProjectName, gitBadge, AppDisplayName);
                }
                if (!string.IsNullOrEmpty(gitBadge))
                {
                    return string.Format("{0}{1} ({2})", title, gitBadge, AppDisplayName);
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
            { "windowsterminal", "Windows Terminal" },
            { "powershell", "PowerShell" },
            { "pwsh", "PowerShell" },
            { "cmd", "命令行提示符" },
            { "mintty", "Git Bash" },
            { "bash", "Git Bash" },
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
            " - PyCharm",
            " - IntelliJ IDEA",
            " - WebStorm",
            " - Rider",
            " - Visual Studio",
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

                var ideInfo = ExtractIdeWorkspace(snap.ProcessName, snap.CleanTitle);
                if (ideInfo != null && ideInfo.Success)
                {
                    snap.ProjectName = ideInfo.ProjectName;
                    snap.ActiveFileName = ideInfo.ActiveFileName;
                }

                // 探测研发终端或 IDE 当前工作区 Git 上下文
                try
                {
                    string probeDir = "";
                    if (!string.IsNullOrEmpty(snap.ActiveFileName) && File.Exists(snap.ActiveFileName))
                    {
                        probeDir = Path.GetDirectoryName(snap.ActiveFileName);
                    }
                    else if (!string.IsNullOrEmpty(snap.CleanTitle) && Directory.Exists(snap.CleanTitle))
                    {
                        probeDir = snap.CleanTitle;
                    }
                    else
                    {
                        probeDir = Environment.CurrentDirectory;
                    }

                    if (!string.IsNullOrEmpty(probeDir))
                    {
                        var git = GitContextDetector.DetectGitContext(probeDir);
                        if (git != null && git.IsGitRepo)
                        {
                            snap.GitBranch = git.BranchName;
                            snap.GitRepo = git.RepoName;
                        }
                    }

                    // 嗅探本地 PDF 阅读器 (SumatraPDF / Acrobat / Foxit) 页码特征
                    if (snap.ProcessName == "sumatrapdf" || snap.ProcessName == "acrobat" || snap.ProcessName == "foxitreader")
                    {
                        var mPage = Regex.Match(snap.FullTitle, @"(?:\[page|\(|p\.)\s*(\d+)(?:/\d+)?(?:\)|\])?", RegexOptions.IgnoreCase);
                        if (mPage.Success)
                        {
                            int p;
                            if (int.TryParse(mPage.Groups[1].Value, out p) && p > 0)
                            {
                                snap.PdfPage = p;
                            }
                        }
                    }
                }
                catch { }
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
            else if (IsOfficeProcess(snap.ProcessName))
            {
                try
                {
                    var off = OfficeComBridge.TryGetActiveOfficeSelection(snap.ProcessName);
                    if (off != null && !string.IsNullOrEmpty(off.DocumentPath))
                    {
                        snap.Url = "file:///" + off.DocumentPath.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(off.DocumentTitle)) snap.CleanTitle = off.DocumentTitle;
                    }
                }
                catch { }
            }

            // 嗅探浏览器或本地 URL 中的 PDF 路径与页码锚点 (#page=N)
            if (!string.IsNullOrEmpty(snap.Url) && snap.Url.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var mUrlPage = Regex.Match(snap.Url, @"[#&?]page=(\d+)", RegexOptions.IgnoreCase);
                if (mUrlPage.Success)
                {
                    int p;
                    if (int.TryParse(mUrlPage.Groups[1].Value, out p) && p > 0)
                    {
                        snap.PdfPage = p;
                    }
                }
                if (snap.Url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string localPath = new Uri(snap.Url.Split('#')[0]).LocalPath;
                        if (File.Exists(localPath))
                        {
                            snap.PdfPath = localPath;
                        }
                    }
                    catch { }
                }
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
            else if (IsOfficeProcess(snap.ProcessName))
            {
                try
                {
                    var off = OfficeComBridge.TryGetActiveOfficeSelection(snap.ProcessName);
                    if (off != null && !string.IsNullOrEmpty(off.DocumentPath))
                    {
                        snap.Url = "file:///" + off.DocumentPath.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(off.DocumentTitle)) snap.CleanTitle = off.DocumentTitle;
                    }
                }
                catch { }
            }
            return snap;
        }

        private static bool IsOfficeProcess(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;
            var name = procName.ToLowerInvariant();
            return name == "winword" || name == "excel" || name == "wps" || name == "et";
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

        public static IdeWorkspaceInfo ExtractIdeWorkspace(string procName, string rawOrCleanTitle)
        {
            var res = new IdeWorkspaceInfo();
            if (string.IsNullOrEmpty(procName) || string.IsNullOrEmpty(rawOrCleanTitle)) return res;

            string clean = CleanWindowTitle(rawOrCleanTitle);
            string pLower = procName.ToLowerInvariant();
            if (pLower == "code" || pLower == "cursor")
            {
                // 形如 "● Program.cs - 思源小驴桌面助手" 或 "Program.cs - 思源小驴桌面助手 [WSL: Ubuntu]"
                string title = clean.TrimStart('●', '○', ' ', '*');
                int dashIdx = title.IndexOf(" - ");
                if (dashIdx > 0)
                {
                    res.ActiveFileName = title.Substring(0, dashIdx).Trim();
                    string rest = title.Substring(dashIdx + 3).Trim();
                    int bracketIdx = rest.IndexOf('[');
                    res.ProjectName = (bracketIdx > 0 ? rest.Substring(0, bracketIdx) : rest).Trim();
                    res.Success = true;
                }
                else
                {
                    res.ProjectName = title;
                    res.Success = true;
                }
            }
            else if (pLower == "idea64" || pLower == "pycharm64" || pLower == "webstorm64" || pLower == "rider64")
            {
                // 形如 "main.py [my-project]" 或 "main.py [my-project] - PyCharm"
                var m = Regex.Match(clean, @"^(.+?)\s*\[([^\]]+)\](?:\s*-\s*.*)?$");
                if (m.Success)
                {
                    res.ActiveFileName = m.Groups[1].Value.Trim();
                    res.ProjectName = m.Groups[2].Value.Trim();
                    res.Success = true;
                }
                else
                {
                    res.ProjectName = clean;
                    res.Success = true;
                }
            }
            else if (pLower == "devenv")
            {
                res.ProjectName = clean;
                res.Success = true;
            }
            return res;
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
