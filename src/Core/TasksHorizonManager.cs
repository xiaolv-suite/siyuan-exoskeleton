using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class HorizonTaskItem
    {
        public string BlockId { get; set; }
        public string RootId { get; set; }
        public string DocTitle { get; set; }
        public string Content { get; set; }
        public string CleanTask { get; set; }
        public string Priority { get; set; }      // "P1" | "P2" | "P3"
        public string DueDate { get; set; }       // "yyyy-MM-dd"
        public bool IsDueToday { get; set; }
        public bool IsOverdue { get; set; }
        public string UpdatedTime { get; set; }

        public HorizonTaskItem()
        {
            BlockId = "";
            RootId = "";
            DocTitle = "";
            Content = "";
            CleanTask = "";
            Priority = "P3";
            DueDate = "";
            IsDueToday = false;
            IsOverdue = false;
            UpdatedTime = "";
        }
    }

    public class TasksHorizonReport
    {
        public List<HorizonTaskItem> TodayTasks { get; set; }     // 今日必办 / 逾期
        public List<HorizonTaskItem> PriorityTasks { get; set; }  // P1 / P2 核心重点
        public List<HorizonTaskItem> NormalTasks { get; set; }    // 日常待办
        public int TotalTasks { get { return (TodayTasks != null ? TodayTasks.Count : 0) + (PriorityTasks != null ? PriorityTasks.Count : 0) + (NormalTasks != null ? NormalTasks.Count : 0); } }
        public string FormattedChecklist { get; set; }

        public TasksHorizonReport()
        {
            TodayTasks = new List<HorizonTaskItem>();
            PriorityTasks = new List<HorizonTaskItem>();
            NormalTasks = new List<HorizonTaskItem>();
            FormattedChecklist = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全景任务视界与优先级分流管理器 (TasksHorizonManager)
    /// 1. 深度对标 Task Horizon / Obsidian Tasks，毫秒级穿透全库所有未完成待办清单
    /// 2. 启发式解析优先级 (P1 紧急重要 / P2 核心不急 / P3 普通) 与到期截止日 (📅 yyyy-MM-dd)
    /// 3. 分流聚合为「今日必办 / 核心重点 / 日常收集箱」三大时序视界
    /// 4. 支持 1 键生成结构化 Markdown 待办 Checklist 导出，供早会交接或团队协同
    /// </summary>
    public static class TasksHorizonManager
    {
        public static async Task<TasksHorizonReport> GetTasksHorizonAsync(string keyword = "")
        {
            var report = new TasksHorizonReport();

            try
            {
                string safeKw = (keyword ?? "").Replace("'", "''");
                string sql = string.IsNullOrEmpty(safeKw)
                    ? "SELECT b.id, b.root_id, b.content, b.hpath, b.updated, doc.content AS doc_title FROM blocks b LEFT JOIN blocks doc ON doc.id = b.root_id WHERE b.subtype = 't' AND (b.markdown NOT LIKE '%[x]%' AND b.markdown NOT LIKE '%[X]%') ORDER BY b.updated DESC LIMIT 50"
                    : "SELECT b.id, b.root_id, b.content, b.hpath, b.updated, doc.content AS doc_title FROM blocks b LEFT JOIN blocks doc ON doc.id = b.root_id WHERE b.subtype = 't' AND (b.markdown NOT LIKE '%[x]%' AND b.markdown NOT LIKE '%[X]%') AND (b.content LIKE '%" + safeKw + "%' OR b.markdown LIKE '%" + safeKw + "%') ORDER BY b.updated DESC LIMIT 50";

                var rows = await SiYuanClient.SqlAsync(sql);
                if (rows == null || rows.Count == 0) return report;

                var todayStr = DateTime.Now.ToString("yyyy-MM-dd");
                var todayCompact = DateTime.Now.ToString("yyyyMMdd");

                foreach (var r in rows)
                {
                    string id = JsonHelper.GetString(r, "id");
                    string rootId = JsonHelper.GetString(r, "root_id");
                    string content = JsonHelper.GetString(r, "content");
                    string hpath = JsonHelper.GetString(r, "hpath");
                    string updated = JsonHelper.GetString(r, "updated");
                    string docTitle = JsonHelper.GetString(r, "doc_title");
                    if (string.IsNullOrEmpty(docTitle)) docTitle = Path.GetFileName(hpath);
                    if (string.IsNullOrEmpty(docTitle)) docTitle = "未命名笔记";

                    string raw = Regex.Replace(content, @"<[^>]+>", "").Trim();
                    string cleanTask = Regex.Replace(raw, @"^-\s*\[\s*\]\s*", "").Trim();

                    var item = new HorizonTaskItem
                    {
                        BlockId = id,
                        RootId = rootId,
                        DocTitle = docTitle,
                        Content = raw,
                        CleanTask = cleanTask,
                        UpdatedTime = updated
                    };

                    // 1. 优先级判定 (P1 / P2 / P3)
                    if (Regex.IsMatch(raw, @"(?:\bP1\b|紧急重要|重要紧急|custom-task-priority=""p1"")", RegexOptions.IgnoreCase))
                    {
                        item.Priority = "P1";
                    }
                    else if (Regex.IsMatch(raw, @"(?:\bP2\b|重要不急|重要|custom-task-priority=""p2"")", RegexOptions.IgnoreCase))
                    {
                        item.Priority = "P2";
                    }
                    else
                    {
                        item.Priority = "P3";
                    }

                    // 2. 截止日期解析 (📅 yyyy-MM-dd 或 2026-11-08)
                    var dateMatch = Regex.Match(raw, @"(?:\b📅\s*|\bdue:\s*)?(\d{4}-\d{2}-\d{2})");
                    if (dateMatch.Success)
                    {
                        item.DueDate = dateMatch.Groups[1].Value;
                        if (string.Compare(item.DueDate, todayStr, StringComparison.Ordinal) == 0)
                        {
                            item.IsDueToday = true;
                        }
                        else if (string.Compare(item.DueDate, todayStr, StringComparison.Ordinal) < 0)
                        {
                            item.IsOverdue = true;
                        }
                    }

                    // 3. 若为今日日记中创建的任务，默认纳入今日必办
                    if (!string.IsNullOrEmpty(hpath) && (hpath.Contains(todayStr) || hpath.Contains(todayCompact)))
                    {
                        item.IsDueToday = true;
                    }

                    // 4. 分流归组
                    if (item.IsDueToday || item.IsOverdue)
                    {
                        report.TodayTasks.Add(item);
                    }
                    else if (item.Priority == "P1" || item.Priority == "P2")
                    {
                        report.PriorityTasks.Add(item);
                    }
                    else
                    {
                        report.NormalTasks.Add(item);
                    }
                }

                // 5. 格式化生成结构化 Markdown Checklist
                report.FormattedChecklist = BuildChecklistMarkdown(report, todayStr);
            }
            catch { }

            return report;
        }

        private static string BuildChecklistMarkdown(TasksHorizonReport report, string todayStr)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("### 📋 全景待办清单视界 ({0} · 共 {1} 项待办)\n\n", todayStr, report.TotalTasks);

            if (report.TodayTasks.Count > 0)
            {
                sb.AppendLine("#### 🔥 今日必办 / 逾期攻坚");
                foreach (var t in report.TodayTasks)
                {
                    string tag = t.IsOverdue ? " [⚠️ 已逾期]" : " [今日]";
                    sb.AppendFormat("- [ ] {0}{1} · 《{2}》\n", t.CleanTask, tag, t.DocTitle);
                }
                sb.AppendLine();
            }

            if (report.PriorityTasks.Count > 0)
            {
                sb.AppendLine("#### ⚡ 核心重点推进 (P1 / P2)");
                foreach (var t in report.PriorityTasks)
                {
                    sb.AppendFormat("- [ ] [{0}] {1} · 《{2}》\n", t.Priority, t.CleanTask, t.DocTitle);
                }
                sb.AppendLine();
            }

            if (report.NormalTasks.Count > 0)
            {
                sb.AppendLine("#### 📌 收集箱与日常待办");
                foreach (var t in report.NormalTasks)
                {
                    sb.AppendFormat("- [ ] {0} · 《{1}》\n", t.CleanTask, t.DocTitle);
                }
                sb.AppendLine();
            }

            sb.AppendFormat("{{: custom-type=\"tasks-horizon\" custom-total=\"{0}\" custom-date=\"{1}\" }}\n",
                report.TotalTasks, todayStr);

            return sb.ToString();
        }

        /// <summary>
        /// 针对指定待办任务一键启航心流番茄钟，到期自动结绳完成任务
        /// </summary>
        public static bool StartFocusOnTask(HorizonTaskItem task, int minutes = 25)
        {
            if (task == null || string.IsNullOrEmpty(task.BlockId)) return false;
            return FlowTimerManager.StartTimer(minutes, task.CleanTask, task.BlockId, task.DocTitle, true);
        }

        /// <summary>
        /// 从当前外部前台活动上下文（浏览器论文、IDE工程、Word文档）一键萃取并创建高情境待办任务
        /// </summary>
        public static bool CreateTaskFromActiveContext(string taskTitle = "", string priority = "P2", string dueDate = "")
        {
            try
            {
                var snap = ContextPerception.CaptureForegroundContext(IntPtr.Zero);
                string cleanTitle = !string.IsNullOrEmpty(taskTitle) ? taskTitle.Trim() : (!string.IsNullOrEmpty(snap.CleanTitle) ? snap.CleanTitle : "待办任务");
                string duePart = !string.IsNullOrEmpty(dueDate) ? (" 📅 " + dueDate.Trim()) : "";
                string prioTag = !string.IsNullOrEmpty(priority) ? ("[" + priority.ToUpper() + "] ") : "[P2] ";

                string contextRef = "";
                if (!string.IsNullOrEmpty(snap.PdfPath) && snap.PdfPage > 0)
                {
                    string pdfName = Path.GetFileName(snap.PdfPath);
                    contextRef = string.Format(" · [📄 {0} P.{1}](siyuan-exoskeleton://open-pdf?path={2}&page={1})",
                        pdfName, snap.PdfPage, Uri.EscapeDataString(snap.PdfPath));
                }
                else if (snap.HasUrl)
                {
                    contextRef = string.Format(" · [{0}]({1})", !string.IsNullOrEmpty(snap.CleanTitle) ? snap.CleanTitle : "网页来源", snap.Url);
                }
                else if (snap.IsDevWorkspace)
                {
                    contextRef = string.Format(" · 💻 {0} ({1})", snap.ProjectName, snap.ActiveFileName);
                }

                string rawTask = string.Format("! {0}{1}{2}{3}", prioTag, cleanTitle, contextRef, duePart);
                var ctx = new DesktopStepContext("SYS", rawTask);
                ctx.SetVarValue("来源应用", "思源外骨骼 · 任务视界");
                SmartCaptureMain.Exec(ctx);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
