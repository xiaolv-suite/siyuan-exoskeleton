using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 知识卡片美化分享与高保真矢量 SVG 长图生成器 (CardImageRenderer)
    /// 1. 深度对标 flomo 卡片长图 / Readwise 读书格言卡，将笔记一键转为优雅分享卡
    /// 2. 纯原生动态矢量 SVG 渲染 (零外部图片处理重型库依赖，生成耗时 < 2ms)
    /// 3. 内嵌暗夜黑曜渐变背景、发光边框、优雅字体排印与来源溯源徽章
    /// 4. 自动拆行与自适应高度计算，支持 1 键生成 .svg 并在系统图片查看器中秒开
    /// </summary>
    public static class CardImageRenderer
    {
        public static string RenderKnowledgeCardSvg(string text, string title = "", string author = "", string dateStr = "")
        {
            if (string.IsNullOrEmpty(text)) text = "未捕获到有效笔记内容";
            if (string.IsNullOrEmpty(title)) title = "思源笔记灵感";
            if (string.IsNullOrEmpty(dateStr)) dateStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            string clean = TextCleaner.CleanAll(text);
            var lines = WrapTextToLines(clean, 28); // 约 28 个汉字宽

            int cardWidth = 640;
            int lineHeight = 32;
            int textBlockHeight = lines.Count * lineHeight;
            int cardHeight = Math.Max(320, 160 + textBlockHeight + 110);

            var sb = new StringBuilder();
            sb.AppendFormat("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0}\" height=\"{1}\" viewBox=\"0 0 {0} {1}\">\n", cardWidth, cardHeight);
            sb.AppendLine("  <defs>");
            sb.AppendLine("    <linearGradient id=\"cardBg\" x1=\"0%\" y1=\"0%\" x2=\"100%\" y2=\"100%\">");
            sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#0a0f1d\"/>");
            sb.AppendLine("      <stop offset=\"50%\" stop-color=\"#0f172a\"/>");
            sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#1e293b\"/>");
            sb.AppendLine("    </linearGradient>");
            sb.AppendLine("    <linearGradient id=\"accentGlow\" x1=\"0%\" y1=\"0%\" x2=\"100%\" y2=\"0%\">");
            sb.AppendLine("      <stop offset=\"0%\" stop-color=\"#38bdf8\"/>");
            sb.AppendLine("      <stop offset=\"100%\" stop-color=\"#818cf8\"/>");
            sb.AppendLine("    </linearGradient>");
            sb.AppendLine("    <filter id=\"glow\" x=\"-10%\" y=\"-10%\" width=\"120%\" height=\"120%\">");
            sb.AppendLine("      <feGaussianBlur stdDeviation=\"6\" result=\"blur\"/>");
            sb.AppendLine("      <feComposite in=\"SourceGraphic\" in2=\"blur\" operator=\"over\"/>");
            sb.AppendLine("    </filter>");
            sb.AppendLine("  </defs>");

            // 1. 卡片底板
            sb.AppendFormat("  <rect x=\"16\" y=\"16\" width=\"{0}\" height=\"{1}\" rx=\"16\" fill=\"url(#cardBg)\" stroke=\"#334155\" stroke-width=\"1.5\"/>\n",
                cardWidth - 32, cardHeight - 32);

            // 2. 顶部微光饰条
            sb.AppendFormat("  <rect x=\"16\" y=\"16\" width=\"{0}\" height=\"4\" rx=\"2\" fill=\"url(#accentGlow)\"/>\n", cardWidth - 32);

            // 3. 巨幅装饰性开引号 “
            sb.AppendLine("  <text x=\"48\" y=\"88\" font-family=\"Georgia, serif\" font-size=\"64\" fill=\"#38bdf8\" opacity=\"0.28\" font-weight=\"bold\">“</text>");

            // 4. 文档来源标签
            string docTag = !string.IsNullOrEmpty(title) ? ("《" + title + "》") : "深度洞见";
            sb.AppendFormat("  <text x=\"88\" y=\"72\" font-family=\"-apple-system, sans-serif\" font-size=\"14\" font-weight=\"700\" fill=\"#38bdf8\">{0}</text>\n",
                System.Net.WebUtility.HtmlEncode(docTag));

            if (!string.IsNullOrEmpty(author))
            {
                sb.AppendFormat("  <text x=\"88\" y=\"92\" font-family=\"-apple-system, sans-serif\" font-size=\"11.5\" fill=\"#94a3b8\">👤 {0}</text>\n",
                    System.Net.WebUtility.HtmlEncode(author));
            }

            // 5. 核心正文段落
            int startY = 140;
            for (int i = 0; i < lines.Count; i++)
            {
                int curY = startY + (i * lineHeight);
                sb.AppendFormat("  <text x=\"48\" y=\"{0}\" font-family=\"-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'PingFang SC', sans-serif\" font-size=\"16\" fill=\"#f8fafc\" font-weight=\"500\" letter-spacing=\"0.3\">{1}</text>\n",
                    curY, System.Net.WebUtility.HtmlEncode(lines[i]));
            }

            // 6. 底部分割线与品牌徽标
            int footerY = cardHeight - 52;
            sb.AppendFormat("  <line x1=\"48\" y1=\"{0}\" x2=\"{1}\" y2=\"{0}\" stroke=\"#334155\" stroke-width=\"1\" opacity=\"0.6\"/>\n",
                footerY - 18, cardWidth - 48);

            sb.AppendFormat("  <text x=\"48\" y=\"{0}\" font-family=\"-apple-system, sans-serif\" font-size=\"11.5\" fill=\"#64748b\">⏱️ {1}</text>\n",
                footerY + 4, dateStr);

            sb.AppendFormat("  <text x=\"{0}\" y=\"{1}\" text-anchor=\"end\" font-family=\"-apple-system, sans-serif\" font-size=\"11.5\" font-weight=\"700\" fill=\"#38bdf8\" opacity=\"0.85\">思源外骨骼 · 灵感沉淀</text>\n",
                cardWidth - 48, footerY + 4);

            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        public static string ExportCardSvgAndOpen(string text, string title = "", string author = "")
        {
            try
            {
                string svg = RenderKnowledgeCardSvg(text, title, author);
                string tempDir = Path.Combine(Path.GetTempPath(), "XiaoLvExport");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

                string safeTitle = !string.IsNullOrEmpty(title) ? Regex.Replace(title, @"[\\/:*?""<>|]", "_") : "card";
                if (safeTitle.Length > 20) safeTitle = safeTitle.Substring(0, 20);

                string filePath = Path.Combine(tempDir, string.Format("card_{0}_{1:yyyyMMdd_HHmmss}.svg", safeTitle, DateTime.Now));
                File.WriteAllText(filePath, svg, new UTF8Encoding(false));

                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                return filePath;
            }
            catch
            {
                return "";
            }
        }

        private static List<string> WrapTextToLines(string text, int maxCjkPerLine)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            var rawParagraphs = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in rawParagraphs)
            {
                string cleanP = p.Trim();
                if (string.IsNullOrEmpty(cleanP)) continue;

                var sb = new StringBuilder();
                int currentWidth = 0;

                for (int i = 0; i < cleanP.Length; i++)
                {
                    char c = cleanP[i];
                    int charWeight = (c >= 0x4E00 && c <= 0x9FA5) ? 2 : 1;

                    if (currentWidth + charWeight > maxCjkPerLine * 2)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear();
                        currentWidth = 0;
                    }

                    sb.Append(c);
                    currentWidth += charWeight;
                }

                if (sb.Length > 0)
                {
                    lines.Add(sb.ToString());
                }

                if (lines.Count > 18)
                {
                    lines.Add("...");
                    break;
                }
            }

            return lines;
        }
    }
}
