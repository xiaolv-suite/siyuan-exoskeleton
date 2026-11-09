using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Input;

namespace XiaoLv.Desktop.Core
{
    public class HotkeyHealthReport
    {
        public string ActionName { get; set; }       // 例如："主总控面板"
        public string ConfigKey { get; set; }        // 例如："masterHotKey"
        public string CurrentShortcut { get; set; }  // 例如："Alt+Shift+Q"
        public bool IsAvailable { get; set; }        // true = 空闲可用; false = 被占用冲突
        public string ConflictCulprit { get; set; }  // 疑似占用软件（如 "WeChat" 或 "外部软件占用"）
        public List<string> VerifiedSuggestions { get; set; } // 经过真实探针实测 100% 可用的替代键

        public HotkeyHealthReport()
        {
            VerifiedSuggestions = new List<string>();
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全局快捷键冲突深度诊断与智能自愈引擎 (Hotkey Doctor & Conflict Resolver)
    /// 解决 Windows 环境下各软件（QQ/微信/PowerToys/Snipaste/PixPin/IDE）热键重叠抢占的普遍痛点
    /// 1. 毫秒级探测当前配置热键在 Windows 系统的真实占用状态 (Win32 RegisterHotKey 探针)
    /// 2. 智能交叉比对正在运行的潜在热键冲突进程
    /// 3. 全自动生成候选备用热键，并在本机真实探针试探，只推荐 100% 空闲可用的安全键组合
    /// </summary>
    public static class HotkeyConflictManager
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private static readonly Random _rand = new Random();

        /// <summary>
        /// 全量体检已配置的 6 大核心快捷键状态
        /// </summary>
        public static List<HotkeyHealthReport> DiagnoseAllHotkeys()
        {
            var s = AppSettings.Instance;
            var reports = new List<HotkeyHealthReport>();

            reports.Add(DiagnoseSingleHotkey("主总控面板", "masterHotKey", s.MasterHotKey));
            reports.Add(DiagnoseSingleHotkey("随身划笔", "penHotKey", s.PenHotKey));
            reports.Add(DiagnoseSingleHotkey("离线 OCR", "ocrHotKey", s.OcrHotKey));
            reports.Add(DiagnoseSingleHotkey("多态速记", "quickHotKey", s.QuickHotKey));
            reports.Add(DiagnoseSingleHotkey("FSRS 闪卡", "fsrsHotKey", s.FsrsHotKey));
            reports.Add(DiagnoseSingleHotkey("网页极速剪藏", "clipHotKey", s.ClipHotKey));

            return reports;
        }

        /// <summary>
        /// 针对单项快捷键进行探针探测与智能推荐
        /// </summary>
        public static HotkeyHealthReport DiagnoseSingleHotkey(string actionName, string configKey, string hotkeyStr)
        {
            var report = new HotkeyHealthReport
            {
                ActionName = actionName,
                ConfigKey = configKey,
                CurrentShortcut = hotkeyStr
            };

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIYUAN_URL")))
            {
                report.IsAvailable = false;
                report.ConflictCulprit = "隔离验证未执行全局快捷键探测";
                return report;
            }

            uint mods, vk;
            if (!ParseHotkey(hotkeyStr, out mods, out vk))
            {
                report.IsAvailable = false;
                report.ConflictCulprit = "快捷键语法无效";
                report.VerifiedSuggestions = FindVerifiedAvailableKeys(mods, vk, 3);
                return report;
            }

            // 执行临时探针测试注册
            bool probeSuccess = TestProbeRegister(mods, vk);
            report.IsAvailable = probeSuccess;

            if (!probeSuccess)
            {
                report.ConflictCulprit = DetectSuspectProcess(hotkeyStr);
                // 自动试探出 3 个当前系统实测绝对空闲的替代键
                report.VerifiedSuggestions = FindVerifiedAvailableKeys(mods, vk, 3);
            }

            return report;
        }

        /// <summary>
        /// 探针测试某热键是否空闲可用
        /// </summary>
        public static bool TestProbeRegister(uint mods, uint vk)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIYUAN_URL"))) return false;
            int testId = 98000 + _rand.Next(1, 1000);
            IntPtr dummyHwnd = IntPtr.Zero;

            // 临时注册探针
            bool ok = RegisterHotKey(dummyHwnd, testId, mods | MOD_NOREPEAT, vk);
            if (ok)
            {
                // 立即注销，避免影响后续真实注册
                UnregisterHotKey(dummyHwnd, testId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 自动搜寻实测空闲且手感舒适的替代快捷键
        /// </summary>
        public static List<string> FindVerifiedAvailableKeys(uint baseMods, uint baseVk, int count = 3)
        {
            var verified = new List<string>();

            // 候选字母表（按右手盲打舒适度排序：D, E, F, G, X, Z, C, V, B, N, M, K, L）
            char[] candidateChars = new char[] { 'D', 'E', 'X', 'Z', 'C', 'V', 'B', 'N', 'M', 'K', 'J' };

            // 优先候选：保持原有修饰键 (例如 Alt+Shift)，仅换末尾字母
            foreach (var ch in candidateChars)
            {
                if (verified.Count >= count) break;
                uint candidateVk = (uint)KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), ch.ToString()));
                if (candidateVk == baseVk) continue;

                if (TestProbeRegister(baseMods, candidateVk))
                {
                    string comboStr = FormatComboString(baseMods, ch.ToString());
                    if (!verified.Contains(comboStr)) verified.Add(comboStr);
                }
            }

            // 次级候选：换用 Ctrl+Alt 或 Alt+Win
            if (verified.Count < count)
            {
                uint altCtrl = MOD_ALT | MOD_CONTROL;
                foreach (var ch in candidateChars)
                {
                    if (verified.Count >= count) break;
                    uint candidateVk = (uint)KeyInterop.VirtualKeyFromKey((Key)Enum.Parse(typeof(Key), ch.ToString()));
                    if (TestProbeRegister(altCtrl, candidateVk))
                    {
                        string comboStr = FormatComboString(altCtrl, ch.ToString());
                        if (!verified.Contains(comboStr)) verified.Add(comboStr);
                    }
                }
            }

            return verified;
        }

        private static string FormatComboString(uint mods, string keyName)
        {
            var sb = new StringBuilder();
            if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(keyName);
            return sb.ToString();
        }

        /// <summary>
        /// 比对系统活跃进程，推断可能造成冲突的外部知名软件
        /// </summary>
        private static string DetectSuspectProcess(string hotkey)
        {
            string hkUpper = (hotkey ?? "").ToUpperInvariant();

            try
            {
                var processes = Process.GetProcesses();
                var procNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in processes)
                {
                    try { procNames.Add(p.ProcessName); } catch { }
                }

                if (hkUpper.Contains("ALT+SPACE") && procNames.Contains("PowerToys")) return "PowerToys (快捷启动器重叠)";
                if (hkUpper.Contains("ALT+A") && procNames.Contains("WeChat")) return "微信电脑版 (截屏快捷键)";
                if (hkUpper.Contains("CTRL+ALT+A") && procNames.Contains("QQ")) return "QQ (截屏快捷键)";
                if (procNames.Contains("Snipaste") && (hkUpper.Contains("F1") || hkUpper.Contains("F3"))) return "Snipaste (截图贴图热键)";
                if (procNames.Contains("PixPin") && (hkUpper.Contains("CTRL+1") || hkUpper.Contains("CTRL+2"))) return "PixPin (截图/离线OCR热键)";
                if (procNames.Contains("Listary") && hkUpper.Contains("CTRL")) return "Listary (快速呼出键)";
                if (procNames.Contains("Everything")) return "Everything (全盘搜索热键)";
            }
            catch { }

            return "其他后台软件已注册独占";
        }

        private static bool ParseHotkey(string text, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;

            var parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var m = parts[i].Trim().ToLowerInvariant();
                if (m == "ctrl" || m == "control") modifiers |= MOD_CONTROL;
                else if (m == "alt") modifiers |= MOD_ALT;
                else if (m == "shift") modifiers |= MOD_SHIFT;
                else if (m == "win" || m == "windows") modifiers |= MOD_WIN;
            }

            var keyStr = parts[parts.Length - 1].Trim();
            if (string.IsNullOrEmpty(keyStr)) return false;

            try
            {
                Key k;
                if (Enum.TryParse<Key>(keyStr, true, out k))
                {
                    vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                    return vk != 0;
                }
            }
            catch { }

            return false;
        }
    }
}
