using System;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class ReadingMetrics
    {
        public int ChineseCharCount { get; set; }
        public int EnglishWordCount { get; set; }
        public int CodeLineCount { get; set; }
        public int TotalCount { get { return ChineseCharCount + EnglishWordCount; } }
        public int EstimatedMinutes { get; set; }
        public string FormattedBadge { get; set; }

        public ReadingMetrics()
        {
            ChineseCharCount = 0;
            EnglishWordCount = 0;
            CodeLineCount = 0;
            EstimatedMinutes = 1;
            FormattedBadge = "📖 0 字 · < 1分钟";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 阅读字数与耗时智能预估引擎 (ReadingProgressEstimator)
    /// 1. 深度对标 Medium / Readwise / 少数派的「阅读耗时预估」心流
    /// 2. 毫秒级统计长文中英文、代码行数与技术密度
    /// 3. 基于标准认知阅读速率算法 (中文 380字/分，英文 200词/分，代码 25行/分) 科学推导耗时
    /// 4. 0 外部依赖，为 Bento 检查器抽屉、网页剪藏与 Office 透视提供结构化徽章
    /// </summary>
    public static class ReadingProgressEstimator
    {
        public static ReadingMetrics Estimate(string text)
        {
            var metrics = new ReadingMetrics();
            if (string.IsNullOrEmpty(text)) return metrics;

            // 1. 统计代码行数
            int codeLines = 0;
            var codeMatches = Regex.Matches(text, @"```[\s\S]*?```");
            foreach (Match m in codeMatches)
            {
                codeLines += m.Value.Split('\n').Length;
            }
            metrics.CodeLineCount = codeLines;

            // 剔除代码块以精确统计正文
            string prose = Regex.Replace(text, @"```[\s\S]*?```", "");

            // 2. 统计中文字数
            var cjkMatches = Regex.Matches(prose, @"[\u4e00-\u9fa5\u3040-\u30ff]");
            metrics.ChineseCharCount = cjkMatches.Count;

            // 3. 统计英文单词数
            var engMatches = Regex.Matches(prose, @"\b[a-zA-Z0-9_\-]{2,}\b");
            metrics.EnglishWordCount = engMatches.Count;

            // 4. 科学推导预估时长
            double cjkMins = metrics.ChineseCharCount / 380.0;
            double engMins = metrics.EnglishWordCount / 200.0;
            double codeMins = metrics.CodeLineCount / 25.0;

            int totalMins = (int)Math.Ceiling(cjkMins + engMins + codeMins);
            if (totalMins <= 0) totalMins = 1;
            metrics.EstimatedMinutes = totalMins;

            // 5. 格式化生成自然语言徽章
            if (metrics.TotalCount >= 1000)
            {
                metrics.FormattedBadge = string.Format("📖 {0:0.#}k 字 · 约 {1} 分钟", metrics.TotalCount / 1000.0, totalMins);
            }
            else
            {
                metrics.FormattedBadge = string.Format("📖 {0} 字 · 约 {1} 分钟", metrics.TotalCount, totalMins);
            }

            return metrics;
        }
    }
}
