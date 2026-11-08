using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class HeatmapReport
    {
        public int TotalBlocksThisYear { get; set; }
        public int ActiveDaysThisYear { get; set; }
        public int MaxDailyBlocks { get; set; }
        public int CurrentStreak { get; set; }
        public string SvgContent { get; set; }

        public HeatmapReport()
        {
            TotalBlocksThisYear = 0;
            ActiveDaysThisYear = 0;
            MaxDailyBlocks = 0;
            CurrentStreak = 0;
            SvgContent = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全库时序心流热力图生成器 (HabitHeatmapGenerator)
    /// 1. 深度对标 GitHub 贡献热力图 / flomo MEMO 记录热力图
    /// 2. 毫秒级穿透思源 SQLite blocks 表按天聚合过去 52 周的知识沉淀频次
    /// 3. 生成自包含纯矢量 SVG 52 周渐变绿热力图，可直接在抽屉呈现或一键导出
    /// 4. 统计年累计笔记块数、活跃天数、最长连续记录天数与峰值记录日
    /// </summary>
    public static class HabitHeatmapGenerator
    {
        public static async Task<HeatmapReport> GenerateHeatmapAsync()
        {
            var report = new HeatmapReport();

            try
            {
                var today = DateTime.Today;
                var startDate = today.AddDays(-363); // 52 周 = 364 天
                string startCompact = startDate.ToString("yyyyMMdd");

                // 1. 查询过去一年每天的有效记录块数 (排除无意义的空块与大纲外壳)
                string sql = string.Format(
                    "SELECT substr(b.created, 1, 8) AS day, count(1) AS cnt " +
                    "FROM blocks b " +
                    "WHERE b.type IN ('p', 'h', 'c', 't', 'i') " +
                    "AND b.created >= '{0}' " +
                    "GROUP BY day", startCompact);

                var rows = await SiYuanClient.SqlAsync(sql);
                var dayCountMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                if (rows != null)
                {
                    foreach (var r in rows)
                    {
                        string day = JsonHelper.GetString(r, "day");
                        int cnt = JsonHelper.GetInt(r, "cnt", 0);
                        if (!string.IsNullOrEmpty(day))
                        {
                            dayCountMap[day] = cnt;
                            report.TotalBlocksThisYear += cnt;
                            if (cnt > 0) report.ActiveDaysThisYear++;
                            if (cnt > report.MaxDailyBlocks) report.MaxDailyBlocks = cnt;
                        }
                    }
                }

                // 2. 计算当前连续打卡天数 (Current Streak)
                int streak = 0;
                var cursor = today;
                while (cursor >= startDate)
                {
                    string k = cursor.ToString("yyyyMMdd");
                    if (dayCountMap.ContainsKey(k) && dayCountMap[k] > 0)
                    {
                        streak++;
                        cursor = cursor.AddDays(-1);
                    }
                    else if (cursor == today)
                    {
                        // 今天尚未记事，检查昨天
                        cursor = cursor.AddDays(-1);
                    }
                    else
                    {
                        break;
                    }
                }
                report.CurrentStreak = streak;

                // 3. 构建 52 周矢量 SVG 热力图
                report.SvgContent = RenderHeatmapSvg(startDate, today, dayCountMap, report);
            }
            catch { }

            return report;
        }

        private static string RenderHeatmapSvg(DateTime startDate, DateTime endDate, Dictionary<string, int> dayCountMap, HeatmapReport report)
        {
            int cellWidth = 11;
            int cellGap = 3;
            int step = cellWidth + cellGap; // 14px 每周
            int leftOffset = 36;
            int topOffset = 42;
            int svgWidth = leftOffset + (53 * step) + 24;
            int svgHeight = topOffset + (7 * step) + 38;

            var sb = new StringBuilder();
            sb.AppendFormat("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100%\" viewBox=\"0 0 {0} {1}\" style=\"background:#0b1120;border-radius:8px;font-family:-apple-system,sans-serif;\">\n", svgWidth, svgHeight);

            // 标题与统计摘要
            sb.AppendFormat("  <text x=\"{0}\" y=\"24\" fill=\"#f8fafc\" font-size=\"12\" font-weight=\"700\">🔥 过去一年累计沉淀 <tspan fill=\"#10b981\">{1}</tspan> 块 · 活跃 <tspan fill=\"#10b981\">{2}</tspan> 天 · 当前连续 <tspan fill=\"#f59e0b\">{3}</tspan> 天</text>\n",
                leftOffset, report.TotalBlocksThisYear, report.ActiveDaysThisYear, report.CurrentStreak);

            // 星期标签 (周一/周三/周五)
            sb.AppendFormat("  <text x=\"8\" y=\"{0}\" fill=\"#64748b\" font-size=\"9.5\">周一</text>\n", topOffset + 1 * step + 9);
            sb.AppendFormat("  <text x=\"8\" y=\"{0}\" fill=\"#64748b\" font-size=\"9.5\">周三</text>\n", topOffset + 3 * step + 9);
            sb.AppendFormat("  <text x=\"8\" y=\"{0}\" fill=\"#64748b\" font-size=\"9.5\">周五</text>\n", topOffset + 5 * step + 9);

            // 绘制 52 周的 364 个小方块
            DateTime cur = startDate;
            int startDayOfWeek = (int)cur.DayOfWeek; // 0=Sunday
            // 补齐起始偏移
            cur = cur.AddDays(-startDayOfWeek);

            int currentWeek = 0;
            string lastMonth = "";

            while (currentWeek < 53)
            {
                int x = leftOffset + (currentWeek * step);

                for (int d = 0; d < 7; d++)
                {
                    int y = topOffset + (d * step);
                    string key = cur.ToString("yyyyMMdd");
                    int count = 0;
                    dayCountMap.TryGetValue(key, out count);

                    // 月份顶标 (在每个月第一周标注)
                    string mStr = cur.ToString("M月");
                    if (d == 0 && mStr != lastMonth && cur >= startDate && cur <= endDate)
                    {
                        sb.AppendFormat("  <text x=\"{0}\" y=\"36\" fill=\"#94a3b8\" font-size=\"9\">{1}</text>\n", x, mStr);
                        lastMonth = mStr;
                    }

                    string fillHex = GetCellColor(count, cur > endDate || cur < startDate);
                    string titleTip = string.Format("{0:yyyy-MM-dd}: {1} 笔沉淀", cur, count);

                    sb.AppendFormat("  <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{2}\" rx=\"2\" fill=\"{3}\"><title>{4}</title></rect>\n",
                        x, y, cellWidth, fillHex, titleTip);

                    cur = cur.AddDays(1);
                }

                currentWeek++;
            }

            // 图例 Legend (Less -> More)
            int legendX = svgWidth - 140;
            int legendY = svgHeight - 16;
            sb.AppendFormat("  <text x=\"{0}\" y=\"{1}\" fill=\"#64748b\" font-size=\"9\">少</text>\n", legendX - 16, legendY + 9);
            sb.AppendFormat("  <rect x=\"{0}\" y=\"{1}\" width=\"10\" height=\"10\" rx=\"2\" fill=\"#1e293b\"/>\n", legendX, legendY);
            sb.AppendFormat("  <rect x=\"{0}\" y=\"{1}\" width=\"10\" height=\"10\" rx=\"2\" fill=\"#064e3b\"/>\n", legendX + 14, legendY);
            sb.AppendFormat("  <rect x=\"{0}\" y=\"{1}\" width=\"10\" height=\"10\" rx=\"2\" fill=\"#047857\"/>\n", legendX + 28, legendY);
            sb.AppendFormat("  <rect x=\"{0}\" y=\"{1}\" width=\"10\" height=\"10\" rx=\"2\" fill=\"#10b981\"/>\n", legendX + 42, legendY);
            sb.AppendFormat("  <rect x=\"{0}\" y=\"{1}\" width=\"10\" height=\"10\" rx=\"2\" fill=\"#34d399\"/>\n", legendX + 56, legendY);
            sb.AppendFormat("  <text x=\"{0}\" y=\"{1}\" fill=\"#64748b\" font-size=\"9\">多</text>\n", legendX + 72, legendY + 9);

            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        private static string GetCellColor(int count, bool isOutOfRange)
        {
            if (isOutOfRange) return "transparent";
            if (count <= 0) return "#1e293b";
            if (count <= 3) return "#064e3b";
            if (count <= 8) return "#047857";
            if (count <= 15) return "#10b981";
            return "#34d399";
        }
    }
}
