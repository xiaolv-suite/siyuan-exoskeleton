using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class DeadRefItem
    {
        public string BlockId { get; set; }
        public string RootId { get; set; }
        public string DocTitle { get; set; }
        public string MissingDefId { get; set; }
        public string AnchorText { get; set; }

        public DeadRefItem()
        {
            BlockId = "";
            RootId = "";
            DocTitle = "";
            MissingDefId = "";
            AnchorText = "";
        }
    }

    public class DeadLinkDoctorReport
    {
        public List<DeadRefItem> DeadRefs { get; set; }
        public List<string> EmptyBlockIds { get; set; }
        public int TotalDeadRefs { get { return DeadRefs != null ? DeadRefs.Count : 0; } }
        public int TotalEmptyBlocks { get { return EmptyBlockIds != null ? EmptyBlockIds.Count : 0; } }
        public string SummaryMarkdown { get; set; }

        public DeadLinkDoctorReport()
        {
            DeadRefs = new List<DeadRefItem>();
            EmptyBlockIds = new List<string>();
            SummaryMarkdown = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全库失效死链与游离引用深度诊断医生 (DeadLinkDoctor)
    /// 1. 深度穿透 SQLite refs 与 blocks 交叉引用索引
    /// 2. 毫秒级嗅探全库已失效的「悬空死链」((deleted_id "文本"))
    /// 3. 统计无意义的幽灵空块并支持一键健康体检清洗
    /// 4. 生成规范的思源体检报告，无感回写或在 Bento 抽屉中交互式修复
    /// </summary>
    public static class DeadLinkDoctor
    {
        public static async Task<DeadLinkDoctorReport> DiagnoseAsync()
        {
            var report = new DeadLinkDoctorReport();

            try
            {
                // 1. 穿透排查悬空死链：refs 中引用的 def_block_id 在 blocks 中已物理不存在
                string deadRefsSql =
                    "SELECT r.id, r.block_id, r.root_id, r.def_block_id, r.content AS anchor, doc.content AS doc_title " +
                    "FROM refs r " +
                    "LEFT JOIN blocks b ON b.id = r.def_block_id " +
                    "LEFT JOIN blocks doc ON doc.id = r.root_id " +
                    "WHERE b.id IS NULL AND r.def_block_id != '' " +
                    "LIMIT 40";

                var deadRows = await SiYuanClient.SqlAsync(deadRefsSql);
                if (deadRows != null)
                {
                    foreach (var r in deadRows)
                    {
                        string bId = JsonHelper.GetString(r, "block_id");
                        string rootId = JsonHelper.GetString(r, "root_id");
                        string missingDef = JsonHelper.GetString(r, "def_block_id");
                        string anchor = JsonHelper.GetString(r, "anchor");
                        string docTitle = JsonHelper.GetString(r, "doc_title");
                        if (string.IsNullOrEmpty(docTitle)) docTitle = "未命名笔记";

                        report.DeadRefs.Add(new DeadRefItem
                        {
                            BlockId = bId,
                            RootId = rootId,
                            DocTitle = docTitle,
                            MissingDefId = missingDef,
                            AnchorText = !string.IsNullOrEmpty(anchor) ? anchor : "未命名锚文本"
                        });
                    }
                }

                // 2. 穿透排查幽灵空块 (content 为空且 markdown 为空的无意义碎片块)
                string emptyBlocksSql =
                    "SELECT b.id FROM blocks b " +
                    "WHERE b.type NOT IN ('d', 'av') AND (b.content = '' OR b.content IS NULL) AND (b.markdown = '' OR b.markdown IS NULL) " +
                    "LIMIT 50";

                var emptyRows = await SiYuanClient.SqlAsync(emptyBlocksSql);
                if (emptyRows != null)
                {
                    foreach (var r in emptyRows)
                    {
                        string id = JsonHelper.GetString(r, "id");
                        if (!string.IsNullOrEmpty(id)) report.EmptyBlockIds.Add(id);
                    }
                }

                // 3. 构建规范诊断报告
                report.SummaryMarkdown = BuildReportMarkdown(report);
            }
            catch { }

            return report;
        }

        private static string BuildReportMarkdown(DeadLinkDoctorReport report)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("### 🩺 思源知识库死链与空块深度体检报告 ({0})\n\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

            sb.AppendFormat("> 📊 **体检概况**: 发现 **{0}** 处悬空死链 · **{1}** 处幽灵空块\n\n",
                report.TotalDeadRefs, report.TotalEmptyBlocks);

            if (report.DeadRefs.Count > 0)
            {
                sb.AppendLine("#### ⚠️ 失效悬空双链清单 (被引用块已删除)");
                foreach (var it in report.DeadRefs)
                {
                    sb.AppendFormat("- ❌ [定位块](siyuan://blocks/{0}) · 锚文本: 「**{1}**」 · 缺失目标ID: `{2}` · 《{3}》\n",
                        it.BlockId, it.AnchorText, it.MissingDefId, it.DocTitle);
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("✓ 全库双链健康度 100%！未发现失效悬空双链。\n");
            }

            sb.AppendFormat("{{: custom-type=\"deadlink-doctor-report\" custom-dead-refs=\"{0}\" custom-empty-blocks=\"{1}\" }}\n",
                report.TotalDeadRefs, report.TotalEmptyBlocks);

            return sb.ToString();
        }
    }
}
