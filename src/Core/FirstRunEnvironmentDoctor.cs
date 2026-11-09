using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class EnvironmentProbeReport
    {
        public int ReadinessScore { get; set; }
        public string ReadinessGrade { get; set; }
        public bool KernelConnected { get; set; }
        public string KernelUrl { get; set; }
        public string KernelVersion { get; set; }
        public long KernelLatencyMs { get; set; }
        public string WorkspaceDir { get; set; }
        public int NotebookCount { get; set; }
        public int DocCount { get; set; }
        public int TotalHotkeysCount { get; set; }
        public int AvailableHotkeysCount { get; set; }
        public List<HotkeyHealthReport> HotkeyReports { get; set; }
        public bool EverythingDetected { get; set; }
        public string EverythingStatus { get; set; }
        public bool ListaryDetected { get; set; }
        public string ListaryStatus { get; set; }
        public string BestSnipToolName { get; set; }
        public bool AiEnabled { get; set; }
        public string AiEndpoint { get; set; }
        public string AiModel { get; set; }
        public bool StorageIsPortable { get; set; }
        public string StorageDir { get; set; }
        public string SummaryMarkdown { get; set; }

        public EnvironmentProbeReport()
        {
            ReadinessScore = 100;
            ReadinessGrade = "🟢 完美就绪";
            KernelConnected = false;
            KernelUrl = "http://127.0.0.1:6806";
            KernelVersion = "未知";
            KernelLatencyMs = 0;
            WorkspaceDir = @"D:\SIYUAN";
            NotebookCount = 0;
            DocCount = 0;
            TotalHotkeysCount = 6;
            AvailableHotkeysCount = 6;
            HotkeyReports = new List<HotkeyHealthReport>();
            EverythingDetected = false;
            EverythingStatus = "未安装 (由内置 SQLite 兜底)";
            ListaryDetected = false;
            ListaryStatus = "未安装";
            BestSnipToolName = "内置离线硬件加速引擎";
            AiEnabled = false;
            AiEndpoint = "http://localhost:11434/v1";
            AiModel = "qwen2.5:latest";
            StorageIsPortable = true;
            StorageDir = "";
            SummaryMarkdown = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 零配置首启 4 维环境自动探针与一键自愈向导引擎 (FirstRunEnvironmentDoctor)
    /// 1. 维度一：思源内核连通性、端口、版本、工作区路径、笔记本与文档规模探测
    /// 2. 维度二：6 大全局快捷键系统级占用探针与冲突自动规避自愈 (AutoHealHotkeys)
    /// 3. 维度三：本地生产力工具生态探测 (Everything / Listary / PixPin / Snipaste)
    /// 4. 维度四：前置大模型提炼、便携存储目录与 DPAPI 凭据保险箱就绪度评估
    /// </summary>
    public static class FirstRunEnvironmentDoctor
    {
        public static async Task<EnvironmentProbeReport> RunFullEnvironmentProbeAsync()
        {
            var rep = new EnvironmentProbeReport();

            // 1. 思源内核与工作区探针
            try
            {
                var conn = SiYuanClient.GetConnectionInfo(false);
                if (conn != null && !string.IsNullOrEmpty(conn.Url))
                {
                    rep.KernelUrl = conn.Url;
                }
                rep.WorkspaceDir = SiYuanClient.DiscoveredWorkspaceDir ?? @"D:\SIYUAN";

                var sw = Stopwatch.StartNew();
                var verRes = await SiYuanClient.PostAsync("/api/system/version", new { });
                sw.Stop();
                rep.KernelLatencyMs = Math.Max(1, sw.ElapsedMilliseconds);

                if (verRes != null && verRes.Success)
                {
                    rep.KernelConnected = true;
                    rep.KernelVersion = verRes.Data != null ? verRes.Data.ToString() : "3.x";

                    var nbs = await SiYuanClient.LsNotebooksAsync();
                    if (nbs != null) rep.NotebookCount = nbs.Count;

                    var docRows = await SiYuanClient.SqlAsync("SELECT COUNT(id) AS cnt FROM blocks WHERE type='d'");
                    if (docRows != null && docRows.Count > 0)
                    {
                        rep.DocCount = JsonHelper.GetInt(docRows[0], "cnt", 0);
                    }
                }
            }
            catch { }

            // 2. 全局快捷键健康度探针
            try
            {
                var hkList = HotkeyConflictManager.DiagnoseAllHotkeys();
                if (hkList != null)
                {
                    rep.HotkeyReports = hkList;
                    rep.TotalHotkeysCount = hkList.Count;
                    int okCount = 0;
                    foreach (var hk in hkList)
                    {
                        if (hk != null && hk.IsAvailable) okCount++;
                    }
                    rep.AvailableHotkeysCount = okCount;
                }
            }
            catch { }

            // 3. 本地外部工具生态探针
            try
            {
                var ev = ToolDetector.DetectEverything();
                if (ev != null && (ev.IsInstalled || ev.IsRunning))
                {
                    rep.EverythingDetected = true;
                    rep.EverythingStatus = ev.Name;
                }

                var ls = ToolDetector.DetectListary();
                if (ls != null && (ls.IsInstalled || ls.IsRunning))
                {
                    rep.ListaryDetected = true;
                    rep.ListaryStatus = ls.Name;
                }

                var s = AppSettings.Instance;
                var snip = ToolDetector.GetBestTool(s != null ? s.PreferredSnippingTool : "auto");
                if (snip != null && !string.IsNullOrEmpty(snip.Name))
                {
                    rep.BestSnipToolName = snip.Name;
                }
            }
            catch { }

            // 4. AI 与本地便携存储探针
            try
            {
                var s = AppSettings.Instance;
                if (s != null)
                {
                    rep.AiEnabled = s.AiEnabled;
                    rep.AiEndpoint = !string.IsNullOrEmpty(s.AiBaseUrl) ? s.AiBaseUrl : "http://localhost:11434/v1";
                    rep.AiModel = !string.IsNullOrEmpty(s.AiModel) ? s.AiModel : "qwen2.5:latest";
                }
                rep.StorageIsPortable = EmbeddedResourceManager.IsPortableMode;
                rep.StorageDir = EmbeddedResourceManager.AppDataDir;
            }
            catch { }

            // 5. 计算综合就绪度评分 (0 ~ 100)
            int score = 100;
            if (!rep.KernelConnected) score -= 25;
            int conflictCount = Math.Max(0, rep.TotalHotkeysCount - rep.AvailableHotkeysCount);
            score -= (conflictCount * 5);
            rep.ReadinessScore = Math.Max(40, Math.Min(100, score));

            if (rep.ReadinessScore >= 90) rep.ReadinessGrade = "🟢 完美就绪 (零配置畅通)";
            else if (rep.ReadinessScore >= 75) rep.ReadinessGrade = "🟡 良好就绪 (部分热键可一键自愈)";
            else rep.ReadinessGrade = "🟠 离岸就绪 (思源内核未启动，离线缓冲队列已接管)";

            // 6. 构建结构化环境自检报告 Markdown
            var sb = new StringBuilder();
            sb.AppendLine("### 🧭 思源外骨骼 v3.0.0 · 本机环境自检与生态配置报告");
            sb.AppendLine();
            sb.AppendFormat("- **综合就绪评分**: **{0} / 100** ({1})\n", rep.ReadinessScore, rep.ReadinessGrade);
            sb.AppendFormat("- **思源笔记内核**: {0} (`{1}` · v{2} · 延迟 {3}ms · {4} 个笔记本 / {5} 篇文档)\n",
                rep.KernelConnected ? "🟢 在线直连" : "🟠 离线缓冲模式",
                rep.KernelUrl, rep.KernelVersion, rep.KernelLatencyMs, rep.NotebookCount, rep.DocCount);
            sb.AppendFormat("- **全局热键矩阵**: {0}/{1} 项无冲突畅通\n", rep.AvailableHotkeysCount, rep.TotalHotkeysCount);
            sb.AppendFormat("- **本地检索联动**: Everything ({0}) · Listary ({1})\n", rep.EverythingStatus, rep.ListaryStatus);
            sb.AppendFormat("- **截图 OCR 引擎**: {0}\n", rep.BestSnipToolName);
            sb.AppendFormat("- **数据存储模式**: {0} (`{1}`)\n", rep.StorageIsPortable ? "便携绿色模式" : "AppData 标准模式", rep.StorageDir);
            sb.AppendLine("{: custom-type=\"exoskeleton-onboarding-report\" }");
            rep.SummaryMarkdown = sb.ToString();

            return rep;
        }

        /// <summary>
        /// 一键自动将存在冲突的快捷键替换为实测空闲的推荐热键并持久化保存
        /// </summary>
        public static int AutoHealConflictingHotkeys()
        {
            int healed = 0;
            var s = AppSettings.Instance;
            if (s == null) return 0;

            string oldMaster = s.MasterHotKey;
            string oldPen = s.PenHotKey;
            string oldOcr = s.OcrHotKey;
            string oldQuick = s.QuickHotKey;
            string oldFsrs = s.FsrsHotKey;
            string oldClip = s.ClipHotKey;
            try
            {
                var reports = HotkeyConflictManager.DiagnoseAllHotkeys();
                if (reports == null) return 0;

                foreach (var r in reports)
                {
                    if (r == null || r.IsAvailable) continue;
                    if (r.VerifiedSuggestions != null && r.VerifiedSuggestions.Count > 0)
                    {
                        string replacement = r.VerifiedSuggestions[0];
                        if (string.IsNullOrEmpty(replacement)) continue;

                        if (r.ConfigKey == "masterHotKey") { s.MasterHotKey = replacement; healed++; }
                        else if (r.ConfigKey == "penHotKey") { s.PenHotKey = replacement; healed++; }
                        else if (r.ConfigKey == "ocrHotKey") { s.OcrHotKey = replacement; healed++; }
                        else if (r.ConfigKey == "quickHotKey") { s.QuickHotKey = replacement; healed++; }
                        else if (r.ConfigKey == "fsrsHotKey") { s.FsrsHotKey = replacement; healed++; }
                        else if (r.ConfigKey == "clipHotKey") { s.ClipHotKey = replacement; healed++; }
                    }
                }

                if (healed > 0)
                {
                    if (!s.Save())
                    {
                        RestoreHotkeys(s, oldMaster, oldPen, oldOcr, oldQuick, oldFsrs, oldClip);
                        return -1;
                    }
                    if (!XiaoLv.Desktop.Program.ReRegisterConfiguredHotKeys())
                    {
                        RestoreHotkeys(s, oldMaster, oldPen, oldOcr, oldQuick, oldFsrs, oldClip);
                        s.Save();
                        return -1;
                    }
                }
            }
            catch
            {
                RestoreHotkeys(s, oldMaster, oldPen, oldOcr, oldQuick, oldFsrs, oldClip);
                s.Save();
                XiaoLv.Desktop.Program.ReRegisterConfiguredHotKeys();
                return -1;
            }
            return healed;
        }

        private static void RestoreHotkeys(AppSettings settings, string master, string pen, string ocr, string quick, string fsrs, string clip)
        {
            settings.MasterHotKey = master;
            settings.PenHotKey = pen;
            settings.OcrHotKey = ocr;
            settings.QuickHotKey = quick;
            settings.FsrsHotKey = fsrs;
            settings.ClipHotKey = clip;
        }
    }
}
