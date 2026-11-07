using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Quicker.Public;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.Engine.Modules
{
    public static class OfficeDeepLinkMain
    {
        /// <summary>
        /// 将 Word 大纲树导出为带思源溯源与层级大纲的标准 Markdown
        /// </summary>
        public static string BuildWordOutlineMarkdown(OfficeDocumentSummary doc)
        {
            if (doc == null || doc.Headings == null || doc.Headings.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendFormat("### 📑 《{0}》文档大纲目录\n", doc.FileName);
            sb.AppendFormat("> 📍 **源文件**: `{0}` ({1}) · **最后修改**: {2}\n\n", doc.FilePath, doc.FileSizeFormatted, doc.LastModified.ToString("yyyy-MM-dd HH:mm"));

            foreach (var h in doc.Headings)
            {
                int indent = Math.Max(0, h.Level - 1) * 2;
                string spaces = new string(' ', indent);
                sb.AppendFormat("{0}- **H{1}**: {2}\n", spaces, h.Level, h.Title);
            }

            sb.AppendLine();
            sb.AppendFormat("{{: custom-type=\"office-outline\" custom-file-path=\"{0}\" custom-file-name=\"{1}\" }}\n", 
                doc.FilePath.Replace("\"", "\\\""), doc.FileName.Replace("\"", "\\\""));

            return sb.ToString();
        }

        /// <summary>
        /// 将 Word 批注集合转化为可执行的法务/学术审阅待办任务清单 (Review Checklist)
        /// </summary>
        public static string BuildWordReviewChecklistMarkdown(OfficeDocumentSummary doc)
        {
            if (doc == null || doc.Comments == null || doc.Comments.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendFormat("### 💬 《{0}》审阅批注与修订待办清单 ({1}条)\n", doc.FileName, doc.Comments.Count);
            sb.AppendFormat("> 📍 **文档底稿**: `{0}` · 审阅跟踪流\n\n", doc.FilePath);

            foreach (var c in doc.Comments)
            {
                string author = !string.IsNullOrEmpty(c.Author) ? c.Author : "审阅人";
                string dateStr = c.Date.ToString("MM-dd HH:mm");
                string comment = c.CommentText.Replace("\r", " ").Replace("\n", " ").Trim();

                sb.AppendFormat("- [ ] **批注建议** ({0} · {1}): {2}\n", author, dateStr, comment);
                if (!string.IsNullOrEmpty(c.AnchorQuoteText))
                {
                    sb.AppendFormat("  > 原文引言: 「{0}」\n", c.AnchorQuoteText.Replace("\r\n", " ").Replace("\n", " ").Trim());
                }
            }

            sb.AppendLine();
            sb.AppendFormat("{{: custom-type=\"office-review-checklist\" custom-file-path=\"{0}\" }}\n", doc.FilePath.Replace("\"", "\\\""));

            return sb.ToString();
        }

        /// <summary>
        /// 将 PPT 幻灯片与演讲者详细口述备注转化为思源原生 FSRS 知识卡片流
        /// </summary>
        public static string BuildPptFlashcardsMarkdown(OfficeDocumentSummary doc)
        {
            if (doc == null || doc.Slides == null || doc.Slides.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendFormat("### 🎬 《{0}》幻灯片核心知识卡片库 ({1}页)\n", doc.FileName, doc.Slides.Count);
            sb.AppendFormat("> 📍 **演示原件**: `{0}` · FSRS 闪卡提炼\n\n", doc.FilePath);

            foreach (var s in doc.Slides)
            {
                string title = !string.IsNullOrEmpty(s.Title) ? s.Title : ("幻灯片 " + s.SlideIndex);
                string answer = s.HasNotes ? s.SpeakerNotes : (!string.IsNullOrEmpty(s.ContentPreview) ? s.ContentPreview : "核心要点复习");

                sb.AppendFormat("#### 🧠 Slide {0}: {1}\n\n", s.SlideIndex, title);
                if (!string.IsNullOrEmpty(s.ContentPreview) && s.HasNotes)
                {
                    sb.AppendFormat("**画面要点**:\n{0}\n\n", s.ContentPreview);
                }
                sb.AppendFormat("**深入解读与演讲备注**:\n{0}\n\n", answer);
                sb.AppendFormat("{{: custom-riff-decks=\"PPT\" custom-type=\"flashcard\" custom-slide=\"{0}\" }}\n\n", s.SlideIndex);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 将 Excel 表格数据直接批量注入思源原生属性视图 (AV 数据库)
        /// </summary>
        public static bool ImportExcelToAvDatabase(string syUrl, string token, string targetAvId, string sampleMarkdown, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(syUrl) || string.IsNullOrEmpty(targetAvId) || string.IsNullOrEmpty(sampleMarkdown)) return false;

            try
            {
                // 解析表格行
                var lines = sampleMarkdown.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 3) return false;

                // 跳过表头与分割线，逐行插入
                int succ = 0;
                for (int i = 2; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (!line.StartsWith("|") || !line.EndsWith("|")) continue;

                    var cells = line.Trim('|').Split('|');
                    string mainKey = cells.Length > 0 ? cells[0].Trim() : "";
                    if (string.IsNullOrEmpty(mainKey)) continue;

                    var res = WebToAvDatabaseMain.InsertAvRow(syUrl, token, targetAvId, mainKey);
                    if (res.Success) succ++;
                }

                if (succ > 0 && notify != null)
                {
                    notify("思源外骨骼 · AV数据库", string.Format("✓ 已成功将 Excel 数据流转追加 {0} 行至属性视图！", succ));
                }
                return succ > 0;
            }
            catch (Exception ex)
            {
                if (notify != null) notify("思源外骨骼", "❌ 注入属性视图失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 跨软件双向毫秒级锚点深层跳转 (Excel 单元格 / Word 书签)
        /// </summary>
        public static bool OpenOfficeLocation(string filePath, string sheet = "", string cell = "", string heading = "")
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                // 1. Excel 单元格穿透
                if (ext == ".xlsx" || ext == ".xls" || ext == ".csv")
                {
                    if (!string.IsNullOrEmpty(cell))
                    {
                        // 优先尝试 Excel 命令行参数
                        try
                        {
                            string target = !string.IsNullOrEmpty(sheet) ? (sheet + "!" + cell) : cell;
                            Process.Start("excel.exe", string.Format("\"{0}\" /e /select,{1}", filePath, target));
                            return true;
                        }
                        catch { }
                    }
                }

                // 2. Word 标题/书签穿透
                if (ext == ".docx" || ext == ".doc")
                {
                    // 默认调用 Word 打开
                    try
                    {
                        Process.Start("winword.exe", string.Format("\"{0}\"", filePath));
                        return true;
                    }
                    catch { }
                }

                // 3. 通用系统原生关联唤起
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }
    }
}
