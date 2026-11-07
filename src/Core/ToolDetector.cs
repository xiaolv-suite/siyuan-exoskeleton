using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace XiaoLv.Desktop.Core
{
    public class LocalToolInfo
    {
        public string Id { get; set; }       // "builtin", "pixpin", "snipaste", "sharex", "windows"
        public string Name { get; set; }     // "PixPin", "Snipaste", "Windows 自带截图", "思源外骨骼内置离线"
        public string ExePath { get; set; }
        public bool IsRunning { get; set; }
        public bool IsInstalled { get; set; }
    }

    public static class ToolDetector
    {
        public static List<LocalToolInfo> DetectSnippingTools()
        {
            var list = new List<LocalToolInfo>();

            // 1. 思源外骨骼内置硬件加速引擎
            list.Add(new LocalToolInfo
            {
                Id = "builtin",
                Name = "思源外骨骼内置离线引擎 (零依赖硬件加速)",
                ExePath = "",
                IsRunning = true,
                IsInstalled = true
            });

            // 2. PixPin
            var pixpin = DetectPixPin();
            if (pixpin != null) list.Add(pixpin);

            // 3. Snipaste
            var snipaste = DetectSnipaste();
            if (snipaste != null) list.Add(snipaste);

            // 4. ShareX
            var sharex = DetectShareX();
            if (sharex != null) list.Add(sharex);

            // 5. Windows 10/11 系统自带截图 (ms-screenclip:)
            list.Add(new LocalToolInfo
            {
                Id = "windows",
                Name = "Windows 系统自带截图 (Win+Shift+S)",
                ExePath = "ms-screenclip:",
                IsRunning = true,
                IsInstalled = true
            });

            return list;
        }

        public static LocalToolInfo GetBestTool(string preferredId)
        {
            var all = DetectSnippingTools();

            // 如果用户指定了且不是 "auto"
            if (!string.IsNullOrEmpty(preferredId) && !preferredId.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var matched = all.Find(t => t.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
                if (matched != null && (matched.IsRunning || matched.IsInstalled))
                {
                    return matched;
                }
            }

            // "auto" 模式：优先使用用户当前正在运行的第三方截图软件（尊重用户已有习惯）
            var runningThirdParty = all.Find(t => t.IsRunning && t.Id != "builtin" && t.Id != "windows");
            if (runningThirdParty != null) return runningThirdParty;

            var installedThirdParty = all.Find(t => t.IsInstalled && t.Id != "builtin" && t.Id != "windows");
            if (installedThirdParty != null) return installedThirdParty;

            // 兜底使用小驴内置引擎
            return all[0];
        }

        private static LocalToolInfo DetectPixPin()
        {
            try
            {
                var procs = Process.GetProcessesByName("PixPin");
                bool isRunning = procs.Length > 0;
                string exePath = "";

                if (isRunning)
                {
                    try { exePath = procs[0].MainModule.FileName; } catch { }
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    string[] probePaths = new string[]
                    {
                        @"D:\RJ\PixPin\PixPin.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\PixPin\PixPin.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PixPin\PixPin.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"PixPin\PixPin.exe")
                    };

                    foreach (var p in probePaths)
                    {
                        if (File.Exists(p)) { exePath = p; break; }
                    }
                }

                if (isRunning || !string.IsNullOrEmpty(exePath))
                {
                    return new LocalToolInfo
                    {
                        Id = "pixpin",
                        Name = "PixPin (本地已装" + (isRunning ? "·运行中" : "") + ")",
                        ExePath = exePath,
                        IsRunning = isRunning,
                        IsInstalled = !string.IsNullOrEmpty(exePath)
                    };
                }
            }
            catch { }
            return null;
        }

        private static LocalToolInfo DetectSnipaste()
        {
            try
            {
                var procs = Process.GetProcessesByName("Snipaste");
                bool isRunning = procs.Length > 0;
                string exePath = "";

                if (isRunning)
                {
                    try { exePath = procs[0].MainModule.FileName; } catch { }
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    string[] probePaths = new string[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Snipaste\Snipaste.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Snipaste\Snipaste.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Snipaste\Snipaste.exe")
                    };

                    foreach (var p in probePaths)
                    {
                        if (File.Exists(p)) { exePath = p; break; }
                    }
                }

                if (isRunning || !string.IsNullOrEmpty(exePath))
                {
                    return new LocalToolInfo
                    {
                        Id = "snipaste",
                        Name = "Snipaste (本地已装" + (isRunning ? "·运行中" : "") + ")",
                        ExePath = exePath,
                        IsRunning = isRunning,
                        IsInstalled = !string.IsNullOrEmpty(exePath)
                    };
                }
            }
            catch { }
            return null;
        }

        private static LocalToolInfo DetectShareX()
        {
            try
            {
                var procs = Process.GetProcessesByName("ShareX");
                bool isRunning = procs.Length > 0;
                string exePath = "";
                if (isRunning)
                {
                    try { exePath = procs[0].MainModule.FileName; } catch { }
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"ShareX\ShareX.exe");
                    if (File.Exists(p)) exePath = p;
                }

                if (isRunning || !string.IsNullOrEmpty(exePath))
                {
                    return new LocalToolInfo
                    {
                        Id = "sharex",
                        Name = "ShareX (本地已装" + (isRunning ? "·运行中" : "") + ")",
                        ExePath = exePath,
                        IsRunning = isRunning,
                        IsInstalled = !string.IsNullOrEmpty(exePath)
                    };
                }
            }
            catch { }
            return null;
        }

        public static LocalToolInfo DetectEverything()
        {
            try
            {
                var procs = Process.GetProcessesByName("Everything");
                bool isRunning = procs.Length > 0;
                string exePath = "";
                if (isRunning)
                {
                    try { exePath = procs[0].MainModule.FileName; } catch { }
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    string[] probePaths = new string[]
                    {
                        @"D:\Program Files\Everything\Everything.exe",
                        @"C:\Program Files\Everything\Everything.exe",
                        @"C:\Program Files (x86)\Everything\Everything.exe",
                        @"D:\Everything\Everything.exe"
                    };

                    foreach (var p in probePaths)
                    {
                        if (File.Exists(p)) { exePath = p; break; }
                    }
                }

                if (isRunning || !string.IsNullOrEmpty(exePath))
                {
                    return new LocalToolInfo
                    {
                        Id = "everything",
                        Name = "Everything (本地已装" + (isRunning ? "·运行中" : "") + ")",
                        ExePath = exePath,
                        IsRunning = isRunning,
                        IsInstalled = !string.IsNullOrEmpty(exePath)
                    };
                }
            }
            catch { }
            return null;
        }

        public static LocalToolInfo DetectListary()
        {
            try
            {
                var procs = Process.GetProcessesByName("Listary");
                bool isRunning = procs.Length > 0;
                string exePath = "";
                if (isRunning)
                {
                    try { exePath = procs[0].MainModule.FileName; } catch { }
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    string[] probePaths = new string[]
                    {
                        @"C:\Program Files\Listary\Listary.exe",
                        @"C:\Program Files (x86)\Listary\Listary.exe",
                        @"D:\Program Files\Listary\Listary.exe"
                    };

                    foreach (var p in probePaths)
                    {
                        if (File.Exists(p)) { exePath = p; break; }
                    }
                }

                if (isRunning || !string.IsNullOrEmpty(exePath))
                {
                    return new LocalToolInfo
                    {
                        Id = "listary",
                        Name = "Listary (本地已装" + (isRunning ? "·运行中" : "") + ")",
                        ExePath = exePath,
                        IsRunning = isRunning,
                        IsInstalled = !string.IsNullOrEmpty(exePath)
                    };
                }
            }
            catch { }
            return null;
        }

        public static LocalToolInfo DetectSumatraPdf()
        {
            try
            {
                var procs = Process.GetProcessesByName("SumatraPDF");
                bool isRunning = procs.Length > 0;
                string exePath = "";
                if (isRunning)
                {
                    try { exePath = procs[0].MainModule.FileName; } catch { }
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    string[] probePaths = new string[]
                    {
                        @"C:\Program Files\SumatraPDF\SumatraPDF.exe",
                        @"C:\Program Files (x86)\SumatraPDF\SumatraPDF.exe",
                        @"D:\Program Files\SumatraPDF\SumatraPDF.exe",
                        @"D:\RJ\SumatraPDF\SumatraPDF.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"SumatraPDF\SumatraPDF.exe")
                    };

                    foreach (var p in probePaths)
                    {
                        if (File.Exists(p)) { exePath = p; break; }
                    }
                }

                if (isRunning || !string.IsNullOrEmpty(exePath))
                {
                    return new LocalToolInfo
                    {
                        Id = "sumatrapdf",
                        Name = "SumatraPDF (极速轻量PDF阅读器)",
                        ExePath = exePath,
                        IsRunning = isRunning,
                        IsInstalled = !string.IsNullOrEmpty(exePath)
                    };
                }
            }
            catch { }
            return null;
        }

        public static bool OpenInEverything(string query)
        {
            var ev = DetectEverything();
            if (ev != null && (!string.IsNullOrEmpty(ev.ExePath) || ev.IsRunning))
            {
                try
                {
                    string exe = !string.IsNullOrEmpty(ev.ExePath) ? ev.ExePath : "Everything.exe";
                    Process.Start(new ProcessStartInfo(exe, "-s \"" + query + "\"") { UseShellExecute = true });
                    return true;
                }
                catch { }
            }
            return false;
        }

        public static bool OpenInListary(string query)
        {
            var listary = DetectListary();
            if (listary != null && (!string.IsNullOrEmpty(listary.ExePath) || listary.IsRunning))
            {
                try
                {
                    if (!string.IsNullOrEmpty(query))
                    {
                        try { System.Windows.Forms.Clipboard.SetText(query); } catch { }
                    }
                    string exe = !string.IsNullOrEmpty(listary.ExePath) ? listary.ExePath : "Listary.exe";
                    Process.Start(new ProcessStartInfo(exe, "\"" + query + "\"") { UseShellExecute = true });
                    return true;
                }
                catch { }
            }
            return false;
        }

        public static bool FallbackOpenInExplorer(string query)
        {
            try
            {
                if (File.Exists(query))
                {
                    Process.Start("explorer.exe", "/select,\"" + query + "\"");
                    return true;
                }
                else if (Directory.Exists(query))
                {
                    Process.Start("explorer.exe", "\"" + query + "\"");
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool OpenInSearchTool(string query, string preferredTool = "auto")
        {
            preferredTool = (preferredTool ?? "auto").ToLowerInvariant();
            var listary = DetectListary();
            var ev = DetectEverything();

            // 1. 用户显式指定 Listary
            if (preferredTool == "listary")
            {
                if (OpenInListary(query)) return true;
                if (OpenInEverything(query)) return true;
                return FallbackOpenInExplorer(query);
            }

            // 2. 用户显式指定 Everything
            if (preferredTool == "everything")
            {
                if (OpenInEverything(query)) return true;
                if (OpenInListary(query)) return true;
                return FallbackOpenInExplorer(query);
            }

            // 3. 智能感知 (auto)：优先调动当前正在运行的本地检索工具
            if (listary != null && listary.IsRunning)
            {
                if (OpenInListary(query)) return true;
            }
            if (ev != null && ev.IsRunning)
            {
                if (OpenInEverything(query)) return true;
            }

            // 4. 若两者均未运行，优先调动已安装的工具
            if (listary != null && listary.IsInstalled)
            {
                if (OpenInListary(query)) return true;
            }
            if (ev != null && ev.IsInstalled)
            {
                if (OpenInEverything(query)) return true;
            }

            // 5. 兜底回退：Windows 资源管理器
            return FallbackOpenInExplorer(query);
        }
    }
}
