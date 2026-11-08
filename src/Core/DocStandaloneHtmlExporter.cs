using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 零依赖独立单文件 HTML 网页导出引擎 (DocStandaloneHtmlExporter)
    /// 1. 0 外部重依赖，无需 Pandoc / Node.js，纯原生生成现代优雅自包含 HTML 独立专页
    /// 2. 内嵌自适应暗黑黑曜主题与响应式流式布局，支持移动端自适应
    /// 3. 内置原生 H1~H6 目录大纲树导航边栏 (TOC)
    /// 4. 优化 @media print 打印规则，支持浏览器中 Ctrl+P 一键打印生成无损 PDF
    /// 5. 生成后直接通过系统默认浏览器秒开预览，方便非思源用户分享协同
    /// </summary>
    public static class DocStandaloneHtmlExporter
    {
        public static string ExportToHtmlAndOpen(string markdown, string title = "", string outputPath = "")
        {
            if (string.IsNullOrEmpty(markdown)) markdown = "# 欢迎使用思源外骨骼\n\n*(当前文档无正文内容)*";
            if (string.IsNullOrEmpty(title)) title = "思源笔记导出文档";

            try
            {
                if (string.IsNullOrEmpty(outputPath))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "XiaoLvExport");
                    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

                    string safeName = Regex.Replace(title, @"[\\/:*?""<>|]", "_");
                    if (safeName.Length > 24) safeName = safeName.Substring(0, 24);

                    outputPath = Path.Combine(tempDir, string.Format("{0}_{1:yyyyMMdd_HHmmss}.html", safeName, DateTime.Now));
                }

                string html = BuildCompleteHtml(markdown, title);
                File.WriteAllText(outputPath, html, new UTF8Encoding(false));

                Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
                return outputPath;
            }
            catch
            {
                return "";
            }
        }

        public static string BuildCompleteHtml(string markdown, string title)
        {
            var tocItems = new List<string>();
            string bodyHtml = ConvertMarkdownToHtmlBody(markdown, tocItems);

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"utf-8\" />");
            sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
            sb.AppendFormat("  <title>{0} - 思源外骨骼导出</title>\n", System.Net.WebUtility.HtmlEncode(title));
            sb.AppendLine("  <style>");
            sb.AppendLine(GetEmbeddedCss());
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("  <div class=\"layout-container\">");

            // 侧栏 TOC 目录导航
            sb.AppendLine("    <aside class=\"sidebar-toc\">");
            sb.AppendLine("      <div class=\"toc-header\">📑 目录导航</div>");
            sb.AppendLine("      <nav class=\"toc-nav\">");
            if (tocItems.Count > 0)
            {
                foreach (var t in tocItems) sb.AppendLine(t);
            }
            else
            {
                sb.AppendLine("        <div style=\"font-size:12px;color:#64748b;padding:8px;\">无标题大纲</div>");
            }
            sb.AppendLine("      </nav>");
            sb.AppendLine("    </aside>");

            // 核心正文容器
            sb.AppendLine("    <main class=\"main-content\">");
            sb.AppendFormat("      <header class=\"doc-header\"><h1 class=\"doc-title\">{0}</h1>", System.Net.WebUtility.HtmlEncode(title));
            sb.AppendFormat("      <div class=\"doc-meta\">导出时间: {0:yyyy-MM-dd HH:mm} · 思源外骨骼原生单文件呈现</div></header>\n", DateTime.Now);
            sb.AppendLine("      <article class=\"markdown-body\">");
            sb.AppendLine(bodyHtml);
            sb.AppendLine("      </article>");
            sb.AppendLine("    </main>");

            sb.AppendLine("  </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private static string ConvertMarkdownToHtmlBody(string md, List<string> tocItems)
        {
            if (string.IsNullOrEmpty(md)) return "";

            var lines = md.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            bool inCode = false;
            string codeLang = "";
            var codeBuffer = new StringBuilder();
            int headingIdx = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // 1. 代码块检测 (```lang ... ```)
                if (line.TrimStart().StartsWith("```"))
                {
                    if (!inCode)
                    {
                        inCode = true;
                        codeLang = line.Trim().Substring(3).Trim();
                        codeBuffer.Clear();
                    }
                    else
                    {
                        inCode = false;
                        sb.AppendFormat("<pre><code class=\"language-{0}\">{1}</code></pre>\n",
                            System.Net.WebUtility.HtmlEncode(codeLang),
                            System.Net.WebUtility.HtmlEncode(codeBuffer.ToString()));
                        codeBuffer.Clear();
                    }
                    continue;
                }

                if (inCode)
                {
                    codeBuffer.AppendLine(line);
                    continue;
                }

                // 忽略思源 Block IAL 属性 {: ...}
                if (Regex.IsMatch(line, @"^\{:\s*[^}]*\}\s*$")) continue;

                // 2. 标题 (H1~H6)
                var hMatch = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (hMatch.Success)
                {
                    int lvl = hMatch.Groups[1].Value.Length;
                    string hText = hMatch.Groups[2].Value.Trim();
                    string anchorId = "heading-" + (++headingIdx);

                    tocItems.Add(string.Format("        <a class=\"toc-item toc-lvl-{0}\" href=\"#{1}\">{2}</a>",
                        lvl, anchorId, System.Net.WebUtility.HtmlEncode(hText)));

                    sb.AppendFormat("<h{0} id=\"{1}\">{2}</h{0}>\n",
                        lvl, anchorId, InlineFormat(hText));
                    continue;
                }

                // 3. 待办复选框清单 (- [ ] 或 - [x])
                var todoMatch = Regex.Match(line, @"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$");
                if (todoMatch.Success)
                {
                    bool isChecked = todoMatch.Groups[2].Value.ToLowerInvariant() == "x";
                    string tText = todoMatch.Groups[3].Value.Trim();
                    sb.AppendFormat("<div class=\"task-item\"><input type=\"checkbox\" {0} disabled /> <span class=\"{1}\">{2}</span></div>\n",
                        isChecked ? "checked" : "",
                        isChecked ? "task-done" : "task-todo",
                        InlineFormat(tText));
                    continue;
                }

                // 4. 无序列表 (- / * / +)
                var listMatch = Regex.Match(line, @"^(\s*)[-*+]\s+(.*)$");
                if (listMatch.Success)
                {
                    sb.AppendFormat("<ul><li>{0}</li></ul>\n", InlineFormat(listMatch.Groups[2].Value.Trim()));
                    continue;
                }

                // 5. 引用块 (> ...)
                if (line.TrimStart().StartsWith(">"))
                {
                    string quoteText = line.TrimStart().Substring(1).Trim();
                    sb.AppendFormat("<blockquote><p>{0}</p></blockquote>\n", InlineFormat(quoteText));
                    continue;
                }

                // 6. 普通段落
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendFormat("<p>{0}</p>\n", InlineFormat(line.Trim()));
                }
            }

            return sb.ToString();
        }

        private static string InlineFormat(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = System.Net.WebUtility.HtmlEncode(text);
            // 加粗
            s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
            // 斜体
            s = Regex.Replace(s, @"\*([^*]+)\*", "<em>$1</em>");
            // 行内代码
            s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");
            // 高亮
            s = Regex.Replace(s, @"==([^=]+)==", "<mark>$1</mark>");
            // 超链接 [text](url)
            s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "<a href=\"$2\" target=\"_blank\" rel=\"noopener\">$1</a>");
            // 思源块引 ((id "title"))
            s = Regex.Replace(s, @"\(\([a-zA-Z0-9\-_]+\s+&quot;([^&]+)&quot;\)\)", "<span class=\"sy-ref\">$1</span>");
            return s;
        }

        private static string GetEmbeddedCss()
        {
            return @"
:root {
  --bg-main: #090d16;
  --bg-sidebar: #0f172a;
  --bg-card: #1e293b;
  --text-main: #f8fafc;
  --text-sub: #94a3b8;
  --accent: #38bdf8;
  --border: rgba(148, 163, 184, 0.15);
}
* { box-sizing: border-box; margin: 0; padding: 0; }
body {
  background: var(--bg-main);
  color: var(--text-main);
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'PingFang SC', sans-serif;
  line-height: 1.7;
  font-size: 15px;
}
.layout-container {
  display: flex;
  min-height: 100vh;
}
.sidebar-toc {
  width: 260px;
  background: var(--bg-sidebar);
  border-right: 1px solid var(--border);
  padding: 24px 16px;
  position: sticky;
  top: 0;
  height: 100vh;
  overflow-y: auto;
  flex-shrink: 0;
}
.toc-header {
  font-size: 13px;
  font-weight: 700;
  color: var(--accent);
  margin-bottom: 16px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}
.toc-item {
  display: block;
  font-size: 12.5px;
  color: var(--text-sub);
  text-decoration: none;
  padding: 4px 8px;
  border-radius: 4px;
  margin-bottom: 2px;
  transition: all 0.1s;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.toc-item:hover {
  color: var(--text-main);
  background: rgba(56, 189, 248, 0.1);
}
.toc-lvl-2 { padding-left: 18px; }
.toc-lvl-3 { padding-left: 28px; }
.toc-lvl-4 { padding-left: 38px; }
.main-content {
  flex: 1;
  max-width: 860px;
  margin: 0 auto;
  padding: 40px 48px;
}
.doc-header {
  border-bottom: 1px solid var(--border);
  padding-bottom: 20px;
  margin-bottom: 32px;
}
.doc-title { font-size: 28px; font-weight: 800; color: #fff; margin-bottom: 8px; }
.doc-meta { font-size: 12px; color: var(--text-sub); }
.markdown-body h1, .markdown-body h2, .markdown-body h3, .markdown-body h4 {
  color: #fff;
  margin-top: 28px;
  margin-bottom: 12px;
  font-weight: 750;
}
.markdown-body h1 { font-size: 24px; border-bottom: 1px solid var(--border); padding-bottom: 8px; }
.markdown-body h2 { font-size: 20px; }
.markdown-body h3 { font-size: 16.5px; }
.markdown-body p { margin-bottom: 16px; }
.markdown-body ul { padding-left: 24px; margin-bottom: 12px; }
.markdown-body li { margin-bottom: 4px; }
.markdown-body pre {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 16px;
  overflow-x: auto;
  margin: 18px 0;
  font-family: Consolas, 'Cascadia Code', monospace;
  font-size: 13px;
  line-height: 1.5;
}
.markdown-body code {
  background: rgba(148, 163, 184, 0.15);
  color: var(--accent);
  padding: 2px 6px;
  border-radius: 4px;
  font-family: Consolas, monospace;
  font-size: 13px;
}
.markdown-body blockquote {
  border-left: 3.5px solid var(--accent);
  background: rgba(56, 189, 248, 0.05);
  padding: 10px 16px;
  border-radius: 0 6px 6px 0;
  margin: 16px 0;
  color: var(--text-main);
}
.task-item { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
.task-done { text-decoration: line-through; color: var(--text-sub); }
.sy-ref { color: var(--accent); background: rgba(56, 189, 248, 0.12); padding: 1px 5px; border-radius: 3px; }
mark { background: #fef08a; color: #1e293b; padding: 1px 4px; border-radius: 3px; }
@media (max-width: 768px) {
  .sidebar-toc { display: none; }
  .main-content { padding: 24px 16px; }
}
@media print {
  .sidebar-toc { display: none; }
  body { background: #fff; color: #000; }
  .main-content { max-width: 100%; padding: 0; }
  .markdown-body pre { background: #f8fafc; border: 1px solid #cbd5e1; color: #0f172a; }
  .markdown-body blockquote { background: #f8fafc; border-left-color: #0284c7; }
  h1, h2, h3 { page-break-after: avoid; color: #0f172a; }
}
";
        }
    }
}
