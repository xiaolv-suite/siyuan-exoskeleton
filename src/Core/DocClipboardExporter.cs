using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 外部富文本图文无损复制引擎 (DocClipboardExporter)
    /// 1. 彻底解决思源笔记向外部 (微信 / 飞书 / 钉钉 / Word / 邮件) 粘贴时的排版混乱与图床失效痛点
    /// 2. 剥离思源原生 Block IAL {: id} 属性杂质，双链还原为自然标题
    /// 3. 自动扫描 assets/ 本地图片并内联转译为 Base64 Data URI，保证微信与邮件中图片 100% 正常显示
    /// 4. 自动注入优雅内联 CSS 排版，同时填充 CF_HTML 富文本与 CF_UNICODETEXT 纯文本剪贴板通道
    /// </summary>
    public static class DocClipboardExporter
    {
        public static bool CopyAsExternalRichText(string markdown, string title = "", string assetsDir = "")
        {
            if (string.IsNullOrEmpty(markdown)) return false;

            try
            {
                if (string.IsNullOrEmpty(assetsDir))
                {
                    assetsDir = SiYuanClient.WorkspaceAssetsDir;
                }

                // 1. 预清洗：剥离思源属性，对齐语法与盘古中英空格
                string cleanedMd = TextCleaner.HarmonizeMarkdownSyntax(TextCleaner.FormatPanguSpacing(TextCleaner.StripSiYuanAttributes(markdown)));

                // 2. 将本地 assets/ 图片转译为内联 Base64
                cleanedMd = InlineLocalAssetsToBase64(cleanedMd, assetsDir);

                // 3. 构建高保真内联 CSS HTML 片段
                string htmlBody = ConvertMarkdownToInlineCssHtml(cleanedMd, title);

                // 4. 写入 Windows 剪贴板 (同时提供 HTML 与纯文本)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.Html, FormatCfHtml(htmlBody));
                        dataObj.SetData(System.Windows.DataFormats.UnicodeText, TextCleaner.CleanAll(markdown));
                        System.Windows.Clipboard.SetDataObject(dataObj, true);
                        SoundPlayerHelper.PlayFeedback();
                    }
                    catch
                    {
                        // 降级使用 WinForms 纯文本剪贴板
                        System.Windows.Forms.Clipboard.SetText(TextCleaner.CleanAll(markdown));
                    }
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string InlineLocalAssetsToBase64(string md, string assetsDir)
        {
            if (string.IsNullOrEmpty(md) || string.IsNullOrEmpty(assetsDir) || !Directory.Exists(assetsDir)) return md;

            return Regex.Replace(md, @"!\[([^\]]*)\]\((?:data/)?assets/([^)]+)\)", new MatchEvaluator(m =>
            {
                string alt = m.Groups[1].Value;
                string fileName = m.Groups[2].Value.Trim();
                string fullPath = Path.Combine(assetsDir, fileName);

                if (File.Exists(fullPath))
                {
                    try
                    {
                        var fi = new FileInfo(fullPath);
                        // 仅对 <= 4MB 图片内联 Base64，防止剪贴板内存溢出
                        if (fi.Length <= 4 * 1024 * 1024)
                        {
                            byte[] bytes = File.ReadAllBytes(fullPath);
                            string mime = "image/png";
                            string ext = fi.Extension.ToLowerInvariant();
                            if (ext == ".jpg" || ext == ".jpeg") mime = "image/jpeg";
                            else if (ext == ".gif") mime = "image/gif";
                            else if (ext == ".webp") mime = "image/webp";
                            else if (ext == ".svg") mime = "image/svg+xml";

                            string b64 = Convert.ToBase64String(bytes);
                            return string.Format("<img src=\"data:{0};base64,{1}\" alt=\"{2}\" style=\"max-width:100%;border-radius:6px;margin:8px 0;display:block;\" />",
                                mime, b64, System.Net.WebUtility.HtmlEncode(alt));
                        }
                    }
                    catch { }
                }

                return m.Value;
            }));
        }

        public static string ConvertMarkdownToInlineCssHtml(string md, string title)
        {
            var lines = md.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            sb.AppendLine("<div style=\"font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,'PingFang SC',sans-serif;font-size:15px;line-height:1.75;color:#334155;max-width:720px;margin:0 auto;padding:12px;\">");

            if (!string.IsNullOrEmpty(title))
            {
                sb.AppendFormat("<h1 style=\"font-size:22px;font-weight:800;color:#0f172a;border-bottom:1.5px solid #e2e8f0;padding-bottom:8px;margin-bottom:16px;\">{0}</h1>\n",
                    System.Net.WebUtility.HtmlEncode(title));
            }

            bool inCode = false;
            var codeBuf = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.TrimStart().StartsWith("```"))
                {
                    if (!inCode)
                    {
                        inCode = true;
                        codeBuf.Clear();
                    }
                    else
                    {
                        inCode = false;
                        sb.AppendFormat("<pre style=\"background:#0f172a;color:#f8fafc;padding:12px 14px;border-radius:6px;font-family:Consolas,monospace;font-size:13px;line-height:1.5;overflow-x:auto;margin:12px 0;\"><code>{0}</code></pre>\n",
                            System.Net.WebUtility.HtmlEncode(codeBuf.ToString().TrimEnd()));
                        codeBuf.Clear();
                    }
                    continue;
                }

                if (inCode)
                {
                    codeBuf.AppendLine(line);
                    continue;
                }

                // 标题
                var hMatch = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (hMatch.Success)
                {
                    int lvl = hMatch.Groups[1].Value.Length;
                    string hText = hMatch.Groups[2].Value.Trim();
                    int fontSize = (lvl == 1) ? 20 : (lvl == 2 ? 17 : 15);
                    sb.AppendFormat("<h{0} style=\"font-size:{1}px;font-weight:700;color:#0f172a;margin-top:20px;margin-bottom:8px;\">{2}</h{0}>\n",
                        lvl, fontSize, System.Net.WebUtility.HtmlEncode(hText));
                    continue;
                }

                // 待办复选框
                var todoMatch = Regex.Match(line, @"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$");
                if (todoMatch.Success)
                {
                    bool isChecked = todoMatch.Groups[2].Value.ToLowerInvariant() == "x";
                    string tText = todoMatch.Groups[3].Value.Trim();
                    sb.AppendFormat("<div style=\"display:flex;align-items:center;gap:6px;margin-bottom:4px;\"><span style=\"font-size:13px;\">{0}</span><span style=\"{1}\">{2}</span></div>\n",
                        isChecked ? "☑" : "☐",
                        isChecked ? "text-decoration:line-through;color:#94a3b8;" : "color:#1e293b;",
                        System.Net.WebUtility.HtmlEncode(tText));
                    continue;
                }

                // 引用块
                if (line.TrimStart().StartsWith(">"))
                {
                    string qText = line.TrimStart().Substring(1).Trim();
                    sb.AppendFormat("<blockquote style=\"border-left:3.5px solid #38bdf8;background:#f0f9ff;padding:8px 12px;border-radius:0 6px 6px 0;margin:10px 0;color:#0369a1;font-size:14px;\">{0}</blockquote>\n",
                        System.Net.WebUtility.HtmlEncode(qText));
                    continue;
                }

                // 普通段落
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (line.Trim().StartsWith("<img"))
                    {
                        sb.AppendLine(line.Trim());
                    }
                    else
                    {
                        sb.AppendFormat("<p style=\"margin-bottom:10px;color:#334155;\">{0}</p>\n",
                            System.Net.WebUtility.HtmlEncode(line.Trim()));
                    }
                }
            }

            sb.AppendLine("</div>");
            return sb.ToString();
        }

        private static string FormatCfHtml(string htmlFragment)
        {
            string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            string htmlStart = "<html><body>\r\n<!--StartFragment-->";
            string htmlEnd = "<!--EndFragment-->\r\n</body></html>";

            int dummyLen = string.Format(header, 0, 0, 0, 0).Length;
            int startHtml = dummyLen;
            int startFrag = dummyLen + htmlStart.Length;
            int endFrag = startFrag + Encoding.UTF8.GetByteCount(htmlFragment);
            int endHtml = endFrag + htmlEnd.Length;

            return string.Format(header, startHtml, endHtml, startFrag, endFrag) + htmlStart + htmlFragment + htmlEnd;
        }
    }
}
