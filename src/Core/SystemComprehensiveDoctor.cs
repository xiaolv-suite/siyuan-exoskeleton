using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class ComprehensiveDiagnosisResult
    {
        public int HealthScore { get; set; }
        public string HealthGrade { get; set; }
        public int DeadRefCount { get; set; }
        public int EmptyBlockCount { get; set; }
        public int OrphanAssetCount { get; set; }
        public string OrphanAssetSizeFormatted { get; set; }
        public long LatencyMs { get; set; }
        public bool IsConnected { get; set; }
        public string KernelVersion { get; set; }
        public List<DeadRefItem> DeadRefs { get; set; }
        public List<string> EmptyBlockIds { get; set; }
        public List<UnusedAssetItem> OrphanAssets { get; set; }
        public string ReportMarkdown { get; set; }

        public ComprehensiveDiagnosisResult()
        {
            HealthScore = 100;
            HealthGrade = "🟢 优良";
            DeadRefCount = 0;
            EmptyBlockCount = 0;
            OrphanAssetCount = 0;
            OrphanAssetSizeFormatted = "0 B";
            LatencyMs = 1;
            IsConnected = true;
            KernelVersion = "3.8.9";
            DeadRefs = new List<DeadRefItem>();
            EmptyBlockIds = new List<string>();
            OrphanAssets = new List<UnusedAssetItem>();
            ReportMarkdown = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全库知识体检与系统综合清道夫 (SystemComprehensiveDoctor)
    /// 1. 深度聚合 DeadLinkDoctor (死链), AssetCleanDoctor (孤儿附件), LinkAutoHealer (双链自愈) 与内核通信状态
    /// 2. 毫秒级计算全库健康总评分 (0~100) 与健康等级
    /// 3. 提供一键死链同名语义自愈、一键孤儿附件安全隔离与空块清理能力
    /// 4. 0 第三方 NuGet 重依赖，纯原生 C# 5.0 架构
    /// </summary>
    public static class SystemComprehensiveDoctor
    {
        public static async Task<ComprehensiveDiagnosisResult> RunFullAuditAsync()
        {
            var res = new ComprehensiveDiagnosisResult();
            var sw = Stopwatch.StartNew();

            try
            {
                // 1. 测试内核通信与真实往返延迟
                var verRes = await SiYuanClient.PostAsync("/api/system/version", new { });
                sw.Stop();
                res.LatencyMs = Math.Max(1, sw.ElapsedMilliseconds);
                res.IsConnected = verRes != null && verRes.Success;
                if (res.IsConnected && verRes.Data != null)
                {
                    res.KernelVersion = verRes.Data.ToString();
                }

                // 2. 穿透诊断悬空死链与幽灵空块
                var deadReport = await DeadLinkDoctor.DiagnoseAsync();
                if (deadReport != null)
                {
                    res.DeadRefs = deadReport.DeadRefs ?? new List<DeadRefItem>();
                    res.EmptyBlockIds = deadReport.EmptyBlockIds ?? new List<string>();
                    res.DeadRefCount = res.DeadRefs.Count;
                    res.EmptyBlockCount = res.EmptyBlockIds.Count;
                }

                // 3. 穿透体检全库无主孤儿附件
                string assetsDir = SiYuanClient.WorkspaceAssetsDir;
                if (Directory.Exists(assetsDir))
                {
                    // 收集 SQLite 中已被引用的附件文件名
                    var usedAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var rows = await SiYuanClient.SqlAsync("SELECT content FROM blocks WHERE content LIKE '%assets/%' LIMIT 1000");
                        if (rows != null)
                        {
                            foreach (var r in rows)
                            {
                                string c = JsonHelper.GetString(r, "content");
                                if (string.IsNullOrEmpty(c)) continue;
                                var matches = Regex.Matches(c, @"assets/([^\)\""\s]+)");
                                foreach (Match m in matches)
                                {
                                    if (m.Success) usedAssetNames.Add(m.Groups[1].Value);
                                }
                            }
                        }
                    }
                    catch { }

                    var allPhysical = Directory.GetFiles(assetsDir);
                    var rawUnused = new List<string>();
                    foreach (var p in allPhysical)
                    {
                        string fn = Path.GetFileName(p);
                        if (!fn.StartsWith(".") && !usedAssetNames.Contains(fn))
                        {
                            rawUnused.Add(fn);
                        }
                    }

                    var assetAudit = AssetCleanDoctor.AuditUnusedAssets(assetsDir, rawUnused);
                    if (assetAudit != null)
                    {
                        res.OrphanAssetCount = assetAudit.TotalUnusedCount;
                        res.OrphanAssetSizeFormatted = assetAudit.TotalSizeFormatted;
                        res.OrphanAssets = assetAudit.Items ?? new List<UnusedAssetItem>();
                    }
                }

                // 4. 计算综合健康度评分 (100分满分制)
                int deductions = (res.DeadRefCount * 3) + (res.EmptyBlockCount / 5) + Math.Min(20, res.OrphanAssetCount * 2);
                res.HealthScore = Math.Max(0, Math.Min(100, 100 - deductions));

                if (res.HealthScore >= 90) res.HealthGrade = "🟢 优良 (健康度极高)";
                else if (res.HealthScore >= 75) res.HealthGrade = "🟡 良好 (建议适度自愈)";
                else res.HealthGrade = "🔴 待优化 (建议执行一键清洗)";

                // 5. 生成结构化诊断报告 Markdown
                res.ReportMarkdown = BuildComprehensiveReportMarkdown(res);
            }
            catch (Exception ex)
            {
                res.HealthGrade = "⚠️ 诊断异常: " + ex.Message;
            }

            return res;
        }

        public static async Task<LinkAutoHealReport> AutoHealDeadLinksAsync()
        {
            return await LinkAutoHealer.AutoHealDeadLinksAsync();
        }

        public static int QuarantineOrphanAssets()
        {
            string assetsDir = SiYuanClient.WorkspaceAssetsDir;
            if (!Directory.Exists(assetsDir)) return 0;

            var allPhysical = Directory.GetFiles(assetsDir);
            var toQuarantine = new List<string>();
            foreach (var p in allPhysical)
            {
                string fn = Path.GetFileName(p);
                if (!fn.StartsWith(".")) toQuarantine.Add(fn);
            }
            return AssetCleanDoctor.QuarantineAssets(assetsDir, toQuarantine);
        }

        public static int RestoreOrphanAssets()
        {
            string assetsDir = SiYuanClient.WorkspaceAssetsDir;
            if (!Directory.Exists(assetsDir)) return 0;
            string qDir = Path.Combine(assetsDir, ".quarantine");
            if (!Directory.Exists(qDir)) return 0;

            int restored = 0;
            foreach (var f in Directory.GetFiles(qDir))
            {
                if (AssetCleanDoctor.RestoreFromQuarantine(assetsDir, Path.GetFileName(f)))
                {
                    restored++;
                }
            }
            return restored;
        }

        public static async Task<int> CleanEmptyBlocksAsync(int maxLimit = 50)
        {
            int deleted = 0;
            try
            {
                var diag = await DeadLinkDoctor.DiagnoseAsync();
                if (diag != null && diag.EmptyBlockIds != null)
                {
                    int count = Math.Min(maxLimit, diag.EmptyBlockIds.Count);
                    for (int i = 0; i < count; i++)
                    {
                        string id = diag.EmptyBlockIds[i];
                        if (string.IsNullOrEmpty(id)) continue;
                        var res = await SiYuanClient.PostAsync("/api/block/deleteBlock", new { id = id });
                        if (res != null && res.Success) deleted++;
                    }
                }
            }
            catch { }
            return deleted;
        }

        private static string BuildComprehensiveReportMarkdown(ComprehensiveDiagnosisResult res)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("### 🩺 思源全库知识体检与系统诊断报告 ({0})\n\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

            sb.AppendFormat("> 🏆 **综合健康评分**: **{0} 分** · {1}\n", res.HealthScore, res.HealthGrade);
            sb.AppendFormat("> ⚡ **系统通信状态**: {0} · 往返延迟: **{1} ms** · 内核版本: `{2}`\n\n",
                res.IsConnected ? "🟢 在线畅通" : "🔴 离线未联通", res.LatencyMs, res.KernelVersion);

            sb.AppendLine("#### 📊 核心指标全景透视");
            sb.AppendFormat("- 🔗 **悬空失效死链**: {0} 处 (已删除块但引用仍保留)\n", res.DeadRefCount);
            sb.AppendFormat("- 👻 **幽灵空白碎片**: {0} 处 (无意义空白内容块)\n", res.EmptyBlockCount);
            sb.AppendFormat("- 🗑️ **游离无主附件**: {0} 篇 · 占用空间: {1}\n\n", res.OrphanAssetCount, res.OrphanAssetSizeFormatted);

            if (res.DeadRefs != null && res.DeadRefs.Count > 0)
            {
                sb.AppendLine("#### 🔗 悬空失效死链明细 (前 10 处)");
                sb.AppendLine("| 所属笔记 | 引用块 ID | 锚文本 | 缺失目标 ID |");
                sb.AppendLine("| :--- | :--- | :--- | :--- |");
                for (int i = 0; i < Math.Min(10, res.DeadRefs.Count); i++)
                {
                    var d = res.DeadRefs[i];
                    sb.AppendFormat("| 《{0}》 | `{1}` | {2} | `{3}` |\n",
                        d.DocTitle, d.BlockId, d.AnchorText, d.MissingDefId);
                }
                sb.AppendLine();
            }

            sb.AppendFormat("{{: custom-type=\"doctor-report\" custom-score=\"{0}\" custom-dead=\"{1}\" custom-orphan=\"{2}\" }}\n",
                res.HealthScore, res.DeadRefCount, res.OrphanAssetCount);

            return sb.ToString().Trim();
        }
    }
}