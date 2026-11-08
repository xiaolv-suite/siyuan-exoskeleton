using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class SiteMetadata
    {
        public string CleanTitle { get; set; }
        public string Author { get; set; }
        public string SiteName { get; set; }
    }

    /// <summary>
    /// 思源外置文本预清洗与排版管道
    /// 专为外置助手场景打造：3D PDF 多栏断行自愈、6大主流网站特征脱敏、盘古中英空格、规范列表与小驴拾遗 IAL 生态标准联动
    /// </summary>
    public static class TextCleaner
    {
        /// <summary>
        /// 去除知乎、微信公众号、链滴、微博、小红书、CSDN 等主流平台的引流与版权尾巴
        /// </summary>
        public static string RemoveWebGarbageTail(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // 1. 知乎版权声明与互动尾巴
            text = Regex.Replace(text, @"\r?\n\s*作者：.*?\r?\n\s*链接：https?://.*?\r?\n\s*来源：知乎.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\r?\n\s*商业转载请联系作者获得授权.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*(?:编辑于|发布于)\s*\d{4}[-/]\d{1,2}[-/]\d{1,2}.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*(?:赞同|喜欢|收藏|分享|收起|添加评论).*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);

            // 2. 微信公众号排版尾巴与零宽干扰字符
            text = text.Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "").Replace("\uFEFF", "");
            text = Regex.Replace(text, @"\r?\n\s*阅读原文\s*.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*收录于合集.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*喜欢此内容的人还喜欢.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*微信扫一扫关注该公众号.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*免责声明：.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*预览时标签不可点.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);

            // 3. 微博展开与网页链接尾巴
            text = Regex.Replace(text, @"\s*展开全文c?", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*收起d?", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*O网页链接", " 🔗网页链接", RegexOptions.IgnoreCase);

            // 4. 小红书互动与话题格式
            text = Regex.Replace(text, @"#([^#\s]+)\[话题\]#", "#$1 ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\r?\n\s*(?:小红书App|发布于\s*\d{2}-\d{2}|说点什么).*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);

            // 5. CSDN / 掘金 / 简书版权声明
            text = Regex.Replace(text, @"\r?\n\s*—+\s*\r?\n\s*版权声明：本文为CSDN博主.*?\r?\n\s*原文链接：.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*来源：掘金\s*\r?\n\s*原文链接：.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);
            text = Regex.Replace(text, @"\r?\n\s*作者：.*?\r?\n\s*链接：https?://www\.jianshu\.com/.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);

            // 6. 链滴社区回帖/浏览注水
            text = Regex.Replace(text, @"\r?\n\s*\d+\s*次浏览\s*•\s*\d+\s*个回帖.*?(?:\r?\n|$)", "\n", RegexOptions.Singleline);

            return text.Trim();
        }

        /// <summary>
        /// 针对特定来源站点进行深度定制化内容纯净化
        /// </summary>
        public static string CleanSiteSpecific(string text, string url, out string siteName)
        {
            siteName = DetectSiteName(url);
            text = RemoveWebGarbageTail(text);

            if (siteName == "微信公众号")
            {
                // 去除公众号开头的引言提示
                text = Regex.Replace(text, @"^[\s\r\n]*点击上方.*?关注我们[\s\r\n]*", "", RegexOptions.IgnoreCase);
            }
            else if (siteName == "知乎")
            {
                // 去除知乎问答前缀
                text = Regex.Replace(text, @"^[\s\r\n]*知乎用户\s*\r?\n", "");
            }
            else if (siteName == "微博")
            {
                // 规范转发链格式
                text = text.Replace("//@", " 🔁 @");
            }

            return text.Trim();
        }

        /// <summary>
        /// 根据 URL 特征识别站点品牌
        /// </summary>
        public static string DetectSiteName(string url)
        {
            if (string.IsNullOrEmpty(url)) return "网页文档";
            var low = url.ToLowerInvariant();
            if (low.Contains("mp.weixin.qq.com")) return "微信公众号";
            if (low.Contains("zhihu.com")) return "知乎";
            if (low.Contains("ld246.com")) return "链滴";
            if (low.Contains("weibo.com") || low.Contains("weibo.cn")) return "微博";
            if (low.Contains("xiaohongshu.com")) return "小红书";
            if (low.Contains("bilibili.com")) return "哔哩哔哩";
            if (low.Contains("douyin.com")) return "抖音";
            if (low.Contains("github.com")) return "GitHub";
            if (low.Contains("sspai.com")) return "少数派";
            if (low.Contains("csdn.net")) return "CSDN";
            if (low.Contains("juejin.cn")) return "掘金";
            if (low.Contains("arxiv.org")) return "arXiv";
            if (low.Contains("yuque.com")) return "语雀";
            if (low.Contains("feishu.cn")) return "飞书文档";
            return "网页摘录";
        }

        /// <summary>
        /// 全网 URL 智能脱敏与去追踪参数净化引擎 (URL Tracking Parameter Purification)
        /// 剥离 utm_*, spm_id_from, vd_source, xsec_token, fbclid 等冗长营销与隐私追踪参数
        /// 还原简洁纯净的 Canonical 规范网址，避免笔记正文被冗长 URL 乱码污染
        /// </summary>
        public static string PurifyUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string clean = url.Trim();

            try
            {
                int qIdx = clean.IndexOf('?');
                if (qIdx < 0) return clean;

                string baseUrl = clean.Substring(0, qIdx);
                string queryString = clean.Substring(qIdx + 1);

                // 拆分参数
                var parts = queryString.Split('&');
                var keptParams = new List<string>();

                var trackingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
                    "spm_id_from", "spm_id", "from_source", "vd_source", "broadcast_type", "is_story_h5",
                    "xsec_token", "xsec_source", "app_id", "app_platform",
                    "fbclid", "gclid", "dclid", "msclkid", "track_id", "trace_id",
                    "isappinstalled", "scene", "clicktime", "enterid", "session_id",
                    "sharer_id", "mid", "from", "ref", "feature", "si", "scm", "spm"
                };

                foreach (var p in parts)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    int eqIdx = p.IndexOf('=');
                    string key = eqIdx > 0 ? p.Substring(0, eqIdx).Trim() : p.Trim();

                    // 剥离营销追踪参数，保留核心业务参数 (如 id, v, page, p, q, tab 等)
                    if (trackingKeys.Contains(key) || key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("spm", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    keptParams.Add(p);
                }

                if (keptParams.Count > 0)
                {
                    return baseUrl + "?" + string.Join("&", keptParams);
                }
                return baseUrl;
            }
            catch
            {
                return clean;
            }
        }

        /// <summary>
        /// 深度解析各大网站标题与作者特征（提取纯正文章标题、真实作者/UP主与站点属性）
        /// 兼容微信公众号、知乎专栏、B站视频、链滴论坛、小红书、微博、arXiv 等
        /// </summary>
        public static SiteMetadata ParseSiteMetadata(string url, string rawTitle)
        {
            string cleanTitle, author, siteName;
            ParseSiteMetadata(url, rawTitle, out cleanTitle, out author, out siteName);
            return new SiteMetadata { CleanTitle = cleanTitle, Author = author, SiteName = siteName };
        }

        public static void ParseSiteMetadata(string url, string rawTitle, out string cleanTitle, out string author, out string siteName)
        {
            siteName = DetectSiteName(url);
            author = "";
            cleanTitle = (rawTitle ?? "").Trim();

            if (string.IsNullOrEmpty(cleanTitle))
            {
                cleanTitle = siteName;
                return;
            }

            if (siteName == "微信公众号")
            {
                // 常见模式: "文章标题 - 微信公众平台" 或 "文章标题 | 公众号名称"
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*微信公众平台.*$", "", RegexOptions.IgnoreCase).Trim();
                var m = Regex.Match(cleanTitle, @"^(.*?)\s*[-_—|]\s*([^\-_—|]+)$");
                if (m.Success)
                {
                    cleanTitle = m.Groups[1].Value.Trim();
                    author = m.Groups[2].Value.Trim();
                }
            }
            else if (siteName == "知乎")
            {
                // 常见模式: "问题标题 - 知乎" 或 "文章标题 - 知乎专栏" 或 "用户名的回答 - 知乎"
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*知乎(?:专栏)?.*$", "", RegexOptions.IgnoreCase).Trim();
                var mAns = Regex.Match(cleanTitle, @"^(.*?)\s*[-_—|]\s*(.*?的回答)$");
                if (mAns.Success)
                {
                    cleanTitle = mAns.Groups[1].Value.Trim();
                    author = mAns.Groups[2].Value.Trim();
                }
            }
            else if (siteName == "哔哩哔哩")
            {
                // 常见模式: "视频标题_哔哩哔哩_bilibili" 或 "视频标题-哔哩哔哩"
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*哔哩哔哩.*$", "", RegexOptions.IgnoreCase).Trim();
                cleanTitle = Regex.Replace(cleanTitle, @"_哔哩哔哩_bilibili$", "", RegexOptions.IgnoreCase).Trim();
                var mUp = Regex.Match(cleanTitle, @"^(.*?)\s*[-_—|]\s*(?:UP主[:：]|UP[:：])?\s*(.*?)$");
                if (mUp.Success && !cleanTitle.Equals(mUp.Groups[1].Value.Trim()))
                {
                    cleanTitle = mUp.Groups[1].Value.Trim();
                    author = mUp.Groups[2].Value.Trim();
                }
            }
            else if (siteName == "链滴")
            {
                // 常见模式: "帖子标题 - 链滴"
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*链滴.*$", "", RegexOptions.IgnoreCase).Trim();
            }
            else if (siteName == "小红书")
            {
                // 常见模式: "笔记标题 - 小红书"
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*小红书.*$", "", RegexOptions.IgnoreCase).Trim();
            }
            else if (siteName == "微博")
            {
                // 常见模式: "微博正文 - 微博"
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*微博.*$", "", RegexOptions.IgnoreCase).Trim();
            }
            else if (siteName == "arXiv")
            {
                // 常见模式: "[2304.12345] Paper Title"
                var mArxiv = Regex.Match(cleanTitle, @"^\[\d{4}\.\d{4,5}\]\s*(.*)$");
                if (mArxiv.Success)
                {
                    cleanTitle = mArxiv.Groups[1].Value.Trim();
                }
            }
            else if (siteName == "CSDN")
            {
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*CSDN博客.*$", "", RegexOptions.IgnoreCase).Trim();
            }
            else if (siteName == "掘金")
            {
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*稀土掘金.*$", "", RegexOptions.IgnoreCase).Trim();
            }
            else if (siteName == "少数派")
            {
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*少数派.*$", "", RegexOptions.IgnoreCase).Trim();
            }
            else if (siteName == "GitHub")
            {
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—|]\s*GitHub.*$", "", RegexOptions.IgnoreCase).Trim();
                var mGit = Regex.Match(cleanTitle, @"^([^/]+)/([^\s:]+)");
                if (mGit.Success)
                {
                    author = mGit.Groups[1].Value.Trim();
                }
            }
            else if (siteName == "语雀")
            {
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—·|]\s*语雀.*$", "", RegexOptions.IgnoreCase).Trim();
            }
            else if (siteName == "飞书文档")
            {
                cleanTitle = Regex.Replace(cleanTitle, @"\s*[-_—·|]\s*飞书(?:云文档)?.*$", "", RegexOptions.IgnoreCase).Trim();
            }

            cleanTitle = cleanTitle.Replace("[", "【").Replace("]", "】");
        }

        public static string GetSiteSmartTag(string siteName)
        {
            if (string.IsNullOrEmpty(siteName)) return "#网页速读";
            switch (siteName)
            {
                case "微信公众号": return "#微信精选";
                case "知乎": return "#知乎精选";
                case "哔哩哔哩": return "#B站视频";
                case "GitHub": return "#开源项目";
                case "arXiv": return "#学术论文";
                case "少数派": return "#少数派精选";
                case "掘金":
                case "CSDN": return "#技术博文";
                case "小红书": return "#小红书笔记";
                case "链滴": return "#思源社区";
                case "语雀":
                case "飞书文档": return "#知识文档";
                default: return "#网页速读";
            }
        }

        /// <summary>
        /// 针对主流知识站点自动映射思源文档原生专属色彩 Emoji 图标
        /// </summary>
        public static string GetSiteEmoji(string siteName)
        {
            if (string.IsNullOrEmpty(siteName)) return "1f4d1"; // 📑
            switch (siteName)
            {
                case "微信公众号": return "1f4f0"; // 📰
                case "知乎": return "1f4a1";      // 💡
                case "哔哩哔哩": return "1f3ac";  // 🎬
                case "GitHub": return "2b50";     // ⭐
                case "arXiv": return "1f4da";      // 📚
                case "少数派": return "26a1";     // ⚡
                case "掘金":
                case "CSDN": return "1f4bb";      // 💻
                case "小红书": return "1f4d5";    // 📕
                case "链滴": return "1f4ac";      // 💬
                case "语雀":
                case "飞书文档": return "1f4dd";  // 📝
                default: return "1f4d1";          // 📑
            }
        }

        /// <summary>
        /// 智能探测网页未标注语言的代码块，自动为其注入标准语言语法高亮标签
        /// </summary>
        public static string EnrichCodeBlocks(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            return Regex.Replace(content, @"```[ \t]*(\r?\n[\s\S]*?)```", new MatchEvaluator(delegate(Match m)
            {
                string codeBody = m.Groups[1].Value;
                string lang;
                if (ClipboardWatcher.TryDetectCodeSnippet(codeBody, out lang))
                {
                    return "```" + lang + codeBody + "```";
                }
                return m.Value;
            }));
        }

        /// <summary>
        /// 构建独立研读专页 Markdown 结构 (带来源看板、作者、发布时间、智能标签及思源原生 Callout 块)
        /// 对齐 Obsidian Web Clipper / Cubox / Readwise 独立研读专页规范，支持用户自定义模板与占位符展开
        /// </summary>
        public static string BuildFullArticleMarkdown(string title, string url, string content, string siteName, string author, string userAnnotation = "", string template = "")
        {
            if (string.IsNullOrEmpty(template))
            {
                try { template = AppSettings.Instance.WebClipCustomTemplate; } catch { }
            }

            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            string smartTag = GetSiteSmartTag(siteName);

            // 若配置了自定义模板，则按占位符求值展开
            if (!string.IsNullOrEmpty(template) && template.Trim().Length > 0)
            {
                var dt = DateTime.Now;
                string weekdayStr = "";
                switch (dt.DayOfWeek)
                {
                    case DayOfWeek.Monday: weekdayStr = "周一"; break;
                    case DayOfWeek.Tuesday: weekdayStr = "周二"; break;
                    case DayOfWeek.Wednesday: weekdayStr = "周三"; break;
                    case DayOfWeek.Thursday: weekdayStr = "周四"; break;
                    case DayOfWeek.Friday: weekdayStr = "周五"; break;
                    case DayOfWeek.Saturday: weekdayStr = "周六"; break;
                    case DayOfWeek.Sunday: weekdayStr = "周日"; break;
                }

                string rendered = template;
                rendered = rendered.Replace("{{title}}", title ?? "")
                                   .Replace("{{url}}", url ?? "")
                                   .Replace("{{siteName}}", siteName ?? "")
                                   .Replace("{{author}}", author ?? "")
                                   .Replace("{{date}}", dateStr)
                                   .Replace("{{time}}", dt.ToString("HH:mm"))
                                   .Replace("{{datetime}}", timeStr)
                                   .Replace("{{year}}", dt.ToString("yyyy"))
                                   .Replace("{{month}}", dt.ToString("MM"))
                                   .Replace("{{day}}", dt.ToString("dd"))
                                   .Replace("{{weekday}}", weekdayStr)
                                   .Replace("{{smartTag}}", smartTag ?? "")
                                   .Replace("{{annotation}}", !string.IsNullOrEmpty(userAnnotation) ? userAnnotation.Trim() : "")
                                   .Replace("{{content}}", !string.IsNullOrEmpty(content) ? content.Trim() : "*(未捕获到选区文本)*");

                return rendered;
            }

            var sb = new StringBuilder();

            // 1. 文献来源看板 (思源原生 Callout 块)
            sb.Append("> 📑 **文献研读看板**\n");
            if (!string.IsNullOrEmpty(url))
            {
                sb.AppendFormat("> - 🔗 **原文出处**: [{0}]({1})\n", title, url);
            }
            sb.AppendFormat("> - 🏛️ **来源站点**: {0}", siteName);
            if (!string.IsNullOrEmpty(author))
            {
                sb.AppendFormat(" · 👤 **作者/UP主**: {0}", author);
            }
            sb.Append("\n");
            sb.AppendFormat("> - ⏱️ **剪藏时间**: {0}\n", timeStr);
            sb.AppendFormat("> - 🏷️ **智能标签**: {0}\n", smartTag);
            sb.Append("{: custom-type=\"literature-card\" }\n\n");

            // 2. 用户即时思考/批注
            if (!string.IsNullOrEmpty(userAnnotation))
            {
                sb.Append("### 💡 核心洞见与研读批注\n");
                sb.Append("> ").Append(userAnnotation.Replace("\n", "\n> ")).Append("\n\n");
            }

            // 3. 摘录正文
            sb.Append("### 📖 摘录正文\n");
            if (!string.IsNullOrEmpty(content))
            {
                sb.Append(content.Trim()).Append("\n");
            }
            else
            {
                sb.Append("*(未捕获到选区文本，已完成网页元数据建档)*\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 3D PDF 断行自愈与连字符智能缝合引擎 (3D PDF Unbreaking)
        /// 维度 1: 单词连字符还原 (trans-\n former -> transformer)
        /// 维度 2: 中英文标点智能停顿与补空
        /// 维度 3: 几何文字形状与代码块自适应豁免
        /// </summary>
        public static string CleanPdfNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // 1. 英文单词跨行破折号截断自愈：word-\r\ning -> wording (排除数字与符号运算)
            text = Regex.Replace(text, @"([a-zA-Z]{2,})-\r?\n\s*([a-zA-Z]{2,})", "$1$2");

            // 2. 检查是否为代码段或公式段，若明显存在代码阶梯缩进则豁免断行合并
            if (IsLikelyCodeBlock(text))
            {
                return text.Trim();
            }

            // 3. 按行切分，执行三维栅格自愈
            var lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.None);
            var sb = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var cur = lines[i].Trim();
                if (string.IsNullOrEmpty(cur))
                {
                    sb.AppendLine();
                    continue;
                }

                sb.Append(cur);

                if (i < lines.Length - 1)
                {
                    var next = lines[i + 1].Trim();
                    if (!string.IsNullOrEmpty(next))
                    {
                        char lastChar = cur[cur.Length - 1];
                        // 句号、问号、感叹号、冒号、分号，或下一行以标题/列表项开始 -> 视为真换行
                        bool isSentenceEnd = (lastChar == '。' || lastChar == '！' || lastChar == '？' || lastChar == '.' || lastChar == '!' || lastChar == '?' || lastChar == '；' || lastChar == ';' || lastChar == '：' || lastChar == ':');
                        // 增强学术文献与英文缩写启发式：若以点结尾但下一行首字母为小写，或当前行以常见缩写 (et al., e.g., i.e., Fig., vs., etc.) 结尾，豁免假断行
                        if (lastChar == '.' && (char.IsLower(next[0]) || cur.EndsWith("et al.", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("e.g.", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("i.e.", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("fig.", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("vs.", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("etc.", StringComparison.OrdinalIgnoreCase)))
                        {
                            isSentenceEnd = false;
                        }
                        bool nextIsHeadingOrList = next.StartsWith("#") || next.StartsWith("- ") || next.StartsWith("* ") || next.StartsWith("+ ") || Regex.IsMatch(next, @"^\d+\.\s");

                        if (isSentenceEnd || nextIsHeadingOrList)
                        {
                            sb.AppendLine();
                        }
                        else
                        {
                            // 否则判定为 PDF 栅格硬折行
                            bool isCjkCur = IsCjkChar(lastChar);
                            bool isCjkNext = IsCjkChar(next[0]);
                            if (isCjkCur && isCjkNext)
                            {
                                // 中文直接相连，不加多余空格
                            }
                            else
                            {
                                // 英文或中英交界，补充单个舒适半角空格
                                sb.Append(" ");
                            }
                        }
                    }
                    else
                    {
                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 判定文本是否为代码块（具有显著层级缩进或编程语言特征，豁免断行合并）
        /// </summary>
        private static bool IsLikelyCodeBlock(string text)
        {
            if (text.Contains("```") || text.Contains("function ") || text.Contains("class ") || text.Contains("public static ") || text.Contains("#include <") || text.Contains("import "))
            {
                return true;
            }
            int indentedCount = 0;
            var lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 3) return false;

            foreach (var l in lines)
            {
                if (l.StartsWith("    ") || l.StartsWith("\t") || l.TrimEnd().EndsWith("{") || l.TrimEnd().EndsWith("};"))
                {
                    indentedCount++;
                }
            }
            return indentedCount >= 2;
        }

        /// <summary>
        /// 盘古之白 (Pangu Spacing): 在中文与英文/数字之间插入舒适阅读空格
        /// 具备代码块、内联代码与 URL 链接占位保护，绝不破坏语法标记
        /// </summary>
        public static string FormatPanguSpacing(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // 1. 保护代码块与行内代码
            var codeBlocks = new List<string>();
            string sanitized = Regex.Replace(text, @"(```[\s\S]*?```|`[^`\r\n]+`)", m =>
            {
                string ph = " __PANGU_CODE_" + codeBlocks.Count + "__ ";
                codeBlocks.Add(m.Value);
                return ph;
            });

            // 2. 保护 URL 链接免受内部插入空格破坏
            var urls = new List<string>();
            sanitized = Regex.Replace(sanitized, @"(https?://[^\s\u4e00-\u9fa5<>""']+)", m =>
            {
                string ph = " __PANGU_URL_" + urls.Count + "__ ";
                urls.Add(m.Value);
                return ph;
            });

            // 2.5 保护 LaTeX 数学公式 ($$...$$ 与 $...$)
            var mathBlocks = new List<string>();
            sanitized = Regex.Replace(sanitized, @"(\$\$[\s\S]*?\$\$|\$[^$\r\n]+\$)", m =>
            {
                string ph = " __PANGU_MATH_" + mathBlocks.Count + "__ ";
                mathBlocks.Add(m.Value);
                return ph;
            });

            // 2.6 保护思源原生块引用与动态模板占位符
            var syBlocks = new List<string>();
            sanitized = Regex.Replace(sanitized, @"(\(\([a-z0-9\-]{14,24}(?:\s+""[^""]*"")?\)\)|\{\{[\s\S]*?\}\})", m =>
            {
                string ph = " __PANGU_SY_" + syBlocks.Count + "__ ";
                syBlocks.Add(m.Value);
                return ph;
            }, RegexOptions.IgnoreCase);

            // 3. 中文与英文/数字之间插入空格
            sanitized = Regex.Replace(sanitized, @"([\u4e00-\u9fa5\u3040-\u30ff])([a-zA-Z0-9$#@%&*])", "$1 $2");
            sanitized = Regex.Replace(sanitized, @"([a-zA-Z0-9$#@%&*])([\u4e00-\u9fa5\u3040-\u30ff])", "$1 $2");
            sanitized = Regex.Replace(sanitized, @"([\u4e00-\u9fa5\u3040-\u30ff])([(])", "$1 $2");
            sanitized = Regex.Replace(sanitized, @"([)])([\u4e00-\u9fa5\u3040-\u30ff])", "$1 $2");

            // 4. 清理冗余的连续空格（保留换行）
            sanitized = Regex.Replace(sanitized, @"[ \t]{2,}", " ");

            // 5. 还原思源块引用与模板占位符
            sanitized = Regex.Replace(sanitized, @"\s*__PANGU_SY_(\d+)__\s*", m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return idx < syBlocks.Count ? (" " + syBlocks[idx] + " ") : "";
            });

            // 5.5 还原 LaTeX 数学公式
            sanitized = Regex.Replace(sanitized, @"\s*__PANGU_MATH_(\d+)__\s*", m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return idx < mathBlocks.Count ? (" " + mathBlocks[idx] + " ") : "";
            });

            // 5.8 还原 URL
            sanitized = Regex.Replace(sanitized, @"\s*__PANGU_URL_(\d+)__\s*", m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return idx < urls.Count ? urls[idx] : "";
            });

            // 6. 还原代码块
            sanitized = Regex.Replace(sanitized, @"\s*__PANGU_CODE_(\d+)__\s*", m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return idx < codeBlocks.Count ? codeBlocks[idx] : "";
            });

            return sanitized.Trim();
        }

        /// <summary>
        /// 标准化 Markdown 列表项与无序标记
        /// </summary>
        public static string NormalizeMarkdownLists(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // 将 • / ◆ / ➢ / ▶ 替换为标准 Markdown -
            text = Regex.Replace(text, @"^[\s\t]*[•◆➢▶]\s*", "- ", RegexOptions.Multiline);

            // 将 (1) / 1) 替换为标准有序列表 1.
            text = Regex.Replace(text, @"^[\s\t]*\(?(\d+)\)[\s\t]+", "$1. ", RegexOptions.Multiline);

            return text;
        }

        /// <summary>
        /// 剥离思源 IAL 属性杂质 {: ...} 并将 [[id|标题]] 还原为自然标题
        /// </summary>
        public static string StripSiYuanAttributes(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = Regex.Replace(text, @"\{:\s*[^}]*\}", "");
            text = Regex.Replace(text, @"\[\[([^\s\]|]+)(?:\|([^\]]+))?\]\]", new MatchEvaluator(m =>
            {
                return !string.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[2].Value : m.Groups[1].Value;
            }));
            return text.Trim();
        }

        /// <summary>
        /// 构建符合思源原生规范的大纲级「嵌套列表结构 (Nested List)」，正文为主列表项，来源缩进为子列表项
        /// 彻底取代扁平引用块，使得单条摘录可在思源中独立折叠、拖拽重排与反向引用
        /// </summary>
        public static string BuildNestedListItem(string content, string title, string url, string appName, string timeStr = "")
        {
            if (string.IsNullOrEmpty(content)) return "";
            if (string.IsNullOrEmpty(timeStr)) timeStr = DateTime.Now.ToString("HH:mm");

            // 1. 正文纯文本转义，保留内部换行但不破坏列表大纲
            string cleanedContent = CleanAll(content);
            var contentLines = cleanedContent.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var mainContent = string.Join("\n  ", contentLines);

            // 2. 来源子列表项
            string metaLine;
            string site = DetectSiteName(url);
            if (!string.IsNullOrEmpty(url))
            {
                string cleanTitle = string.IsNullOrEmpty(title) ? site : title.Trim().Replace("[", "【").Replace("]", "】");
                metaLine = string.Format("🔗 [{0}]({1}) · {2} · {3}", cleanTitle, url, site, timeStr);
            }
            else
            {
                metaLine = string.Format("💻 摘录于 {0} · {1}", string.IsNullOrEmpty(appName) ? "桌面应用" : appName, timeStr);
            }

            return string.Format("* {0}\n  * {1}", mainContent, metaLine);
        }

        /// <summary>
        /// 生成与「小驴拾遗 (siyuan-glean)」深度联动的标准文档属性 IAL
        /// 确保外部桌面剪藏与思源读库无缝互通，自动进入小驴拾遗未读分拣与每日重浮队列
        /// </summary>
        public static string BuildGleanIalAttributes(string url, string title, string site = "", string author = "")
        {
            var sb = new StringBuilder();
            sb.Append("{: ");
            if (!string.IsNullOrEmpty(url)) sb.AppendFormat("custom-clip-url=\"{0}\" ", url.Replace("\"", "\\\""));
            if (string.IsNullOrEmpty(site)) site = DetectSiteName(url);
            sb.AppendFormat("custom-clip-site=\"{0}\" ", site.Replace("\"", "\\\""));
            if (!string.IsNullOrEmpty(author)) sb.AppendFormat("custom-clip-author=\"{0}\" ", author.Replace("\"", "\\\""));
            sb.AppendFormat("custom-clip-time=\"{0}\" ", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append("custom-clip-status=\"unread\" ");
            sb.Append("custom-triage-status=\"unread\" ");
            sb.Append("custom-clip-content-type=\"article\" ");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// 全套清洗工序综合执行 (CleanAll)
        /// </summary>
        public static string CleanAll(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string res = RemoveWebGarbageTail(text);
            res = StripSiYuanAttributes(res);
            res = CleanPdfNewlines(res);
            res = NormalizeMarkdownLists(res);
            res = FormatPanguSpacing(res);
            return res.Trim();
        }

        /// <summary>
        /// 将剪贴板富文本 HTML (CF_HTML) 智能转换为高保真 Markdown
        /// 完整保留超链接 [text](url)、加粗 **text**、斜体 *text*、代码 `code`、图片 ![alt](src) 与引用 > quote
        /// 对标 Obsidian Web Clipper 2024 / Readwise Reader，解决普通纯文本丢失超链接与格式坍塌痛点
        /// </summary>
        public static string ConvertHtmlToMarkdown(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            // 1. 截取 CF_HTML 的核心 Fragment 区域（若存在 StartFragment 和 EndFragment）
            string fragment = html;
            int startIdx = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
            int endIdx = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (startIdx >= 0 && endIdx > startIdx)
            {
                fragment = html.Substring(startIdx + 20, endIdx - (startIdx + 20));
            }

            // 2. 剥离脚本与样式 (script, style, head, meta, noscript)
            fragment = Regex.Replace(fragment, @"<(script|style|head|meta|noscript)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);

            // 3. 转换标题 h1~h6
            for (int h = 1; h <= 6; h++)
            {
                string hashes = new string('#', h);
                fragment = Regex.Replace(fragment, @"<h" + h + @"[^>]*>([\s\S]*?)</h" + h + @">", "\n\n" + hashes + " $1\n\n", RegexOptions.IgnoreCase);
            }

            // 4. 转换多行代码块 <pre><code ...>...</code></pre> 或 <pre>...</pre>
            fragment = Regex.Replace(fragment, @"<pre[^>]*><code[^>]*>([\s\S]*?)</code></pre>", "\n\n```\n$1\n```\n\n", RegexOptions.IgnoreCase);
            fragment = Regex.Replace(fragment, @"<pre[^>]*>([\s\S]*?)</pre>", "\n\n```\n$1\n```\n\n", RegexOptions.IgnoreCase);

            // 5. 转换行内代码 <code>...</code>
            fragment = Regex.Replace(fragment, @"<code[^>]*>([\s\S]*?)</code>", "`$1`", RegexOptions.IgnoreCase);

            // 6. 转换超链接 <a href="url"...>text</a>
            fragment = Regex.Replace(fragment, @"<a\s+[^>]*?href=[""']([^""']+)[""'][^>]*>([\s\S]*?)</a>", delegate(Match m)
            {
                string u = m.Groups[1].Value.Trim();
                string t = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();
                if (string.IsNullOrEmpty(t)) t = u;
                if (string.IsNullOrEmpty(u) || u.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return t;
                string pureU = PurifyUrl(u);
                return "[" + t + "](" + pureU + ")";
            }, RegexOptions.IgnoreCase);

            // 7. 转换图片 <img src="url" alt="alt" ...>
            fragment = Regex.Replace(fragment, @"<img\s+[^>]*?src=[""']([^""']+)[""'][^>]*>", delegate(Match m)
            {
                string tag = m.Value;
                string src = m.Groups[1].Value.Trim();
                var altM = Regex.Match(tag, @"alt=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                string alt = altM.Success ? altM.Groups[1].Value.Trim() : "";
                if (string.IsNullOrEmpty(src)) return "";
                string pureSrc = PurifyUrl(src);
                return "\n\n![" + alt + "](" + pureSrc + ")\n\n";
            }, RegexOptions.IgnoreCase);

            // 8. 转换加粗与强调 <strong> / <b> / <em> / <i>
            fragment = Regex.Replace(fragment, @"<(strong|b)[^>]*>([\s\S]*?)</\1>", "**$2**", RegexOptions.IgnoreCase);
            fragment = Regex.Replace(fragment, @"<(em|i)[^>]*>([\s\S]*?)</\1>", "*$2*", RegexOptions.IgnoreCase);

            // 9. 转换引用块 <blockquote>...</blockquote>
            fragment = Regex.Replace(fragment, @"<blockquote[^>]*>([\s\S]*?)</blockquote>", delegate(Match m)
            {
                string inner = Regex.Replace(m.Groups[1].Value, @"<[^>]+>", "").Trim();
                return "\n\n> " + inner.Replace("\n", "\n> ") + "\n\n";
            }, RegexOptions.IgnoreCase);

            // 10. 转换列表项 <li>...</li>
            fragment = Regex.Replace(fragment, @"<li[^>]*>([\s\S]*?)</li>", "\n* $1", RegexOptions.IgnoreCase);

            // 11. 换行与段落 <br> / <p> / <div>
            fragment = Regex.Replace(fragment, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            fragment = Regex.Replace(fragment, @"</?(p|div|tr|table|ul|ol)[^>]*>", "\n", RegexOptions.IgnoreCase);

            // 12. 剥离剩余全部 HTML 标签
            fragment = Regex.Replace(fragment, @"<[^>]+>", "");

            // 13. 解码常见 HTML 实体
            fragment = System.Net.WebUtility.HtmlDecode(fragment);

            // 14. 清理冗余连续空行与首尾空白
            fragment = Regex.Replace(fragment, @"\n{3,}", "\n\n");
            return fragment.Trim();
        }

	        /// <summary>
	        /// 异构笔记语法智能对齐与转译引擎 (Obsidian Callout / Frontmatter / Logseq 语法对齐)
	        /// 使得来自 Obsidian、Notion 或 Logseq 的 Markdown 文本无感融入思源原生块标准
	        /// </summary>
	        public static string HarmonizeMarkdownSyntax(string text)
	        {
	            if (string.IsNullOrEmpty(text)) return "";

	            // 1. Obsidian Callout 语法转译为思源原生强调引用块
	            // 例如 > [!NOTE] 提示内容 -> > 📌 **NOTE**: 提示内容
	            text = Regex.Replace(text, @"^>\s*\[!([a-zA-Z]+)\](?:[ \t]+([^\r\n]*))?", new MatchEvaluator(m =>
	            {
	                string kind = m.Groups[1].Value.ToLowerInvariant();
	                string customTitle = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";

	                string icon = "📌";
	                string label = kind.ToUpperInvariant();

	                switch (kind)
	                {
	                    case "note": icon = "📌"; break;
	                    case "tip":
	                    case "hint": icon = "💡"; label = "TIP"; break;
	                    case "warning":
	                    case "caution":
	                    case "attention": icon = "⚠️"; label = "WARNING"; break;
	                    case "info": icon = "ℹ️"; label = "INFO"; break;
	                    case "danger":
	                    case "error": icon = "❌"; label = "ERROR"; break;
	                    case "success":
	                    case "check":
	                    case "done": icon = "✅"; label = "SUCCESS"; break;
	                    case "question":
	                    case "help":
	                    case "faq": icon = "❓"; label = "FAQ"; break;
	                    case "quote":
	                    case "cite": icon = "💬"; label = "QUOTE"; break;
	                    case "bug": icon = "🐛"; label = "BUG"; break;
	                    case "example": icon = "📋"; label = "EXAMPLE"; break;
	                    default: icon = "📌"; break;
	                }

	                if (!string.IsNullOrEmpty(customTitle))
	                {
	                    return string.Format("> {0} **{1}**: {2}", icon, label, customTitle);
	                }
	                return string.Format("> {0} **{1}**", icon, label);
	            }), RegexOptions.Multiline);

	            // 2. Obsidian / Jekyll YAML Frontmatter 转译为思源标题与标签
	            var fmMatch = Regex.Match(text, @"^---\r?\n([\s\S]*?)\r?\n---\r?\n");
	            if (fmMatch.Success)
	            {
	                string fmContent = fmMatch.Groups[1].Value;
	                string restText = text.Substring(fmMatch.Length);

	                string docTitle = "";
	                var tags = new List<string>();

	                var titleM = Regex.Match(fmContent, @"^title:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
	                if (titleM.Success) docTitle = titleM.Groups[1].Value.Trim().Trim('"', '\'');

	                // 提取 tags: [a, b] 或列表
	                var tagArrM = Regex.Match(fmContent, @"^tags:\s*\[([^\]]*)\]", RegexOptions.Multiline | RegexOptions.IgnoreCase);
	                if (tagArrM.Success)
	                {
	                    foreach (var rawTag in tagArrM.Groups[1].Value.Split(','))
	                    {
	                        string cleanTag = rawTag.Trim().Trim('"', '\'');
	                        if (!string.IsNullOrEmpty(cleanTag)) tags.Add("#" + cleanTag);
	                    }
	                }
	                else
	                {
	                    var tagLinesM = Regex.Matches(fmContent, @"^tags:\s*\r?\n((?:\s*-\s*.+\r?\n)+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
	                    if (tagLinesM.Count > 0)
	                    {
	                        var lines = tagLinesM[0].Groups[1].Value.Split('\n');
	                        foreach (var l in lines)
	                        {
	                            var item = Regex.Replace(l.Trim(), @"^-\s*", "").Trim().Trim('"', '\'');
	                            if (!string.IsNullOrEmpty(item)) tags.Add("#" + item);
	                        }
	                    }
	                }

	                var sb = new StringBuilder();
	                if (!string.IsNullOrEmpty(docTitle))
	                {
	                    sb.AppendFormat("### 📑 《{0}》\n", docTitle);
	                }
	                if (tags.Count > 0)
	                {
	                    sb.AppendFormat("> 🏷️ {0}\n\n", string.Join(" ", tags));
	                }
	                sb.Append(restText);
	                text = sb.ToString();
	            }

	            // 3. Logseq 挖空 Cloze 转为高亮 ==text==
	            text = Regex.Replace(text, @"\{\{cloze\s+([^\}]+)\}\}", "==$1==");

	            return text.Trim();
	        }

	        /// <summary>
	        /// 智能嗅探学术文献标识符 (DOI 与 arXiv) 并生成标准思源文献卡片
	        /// </summary>
	        public static bool TryExtractAcademicCitation(string text, out string formattedCard)
	        {
	            formattedCard = "";
	            if (string.IsNullOrEmpty(text)) return false;

	            // 1. 嗅探 arXiv: 1706.03762 或 arXiv:2304.12345
	            var mArxiv = Regex.Match(text, @"(?:arxiv[:\s/]+)?(\d{4}\.\d{4,5}(?:v\d+)?)", RegexOptions.IgnoreCase);
	            if (mArxiv.Success)
	            {
	                string id = mArxiv.Groups[1].Value;
	                var sb = new StringBuilder();
	                sb.AppendLine("> 📚 **学术论文预印本 (arXiv)**");
	                sb.AppendFormat("> - 🔗 **arXiv 原文**: [arXiv:{0}](https://arxiv.org/abs/{0}) · [📥 PDF 直达](https://arxiv.org/pdf/{0}.pdf)\n", id);
	                sb.AppendLine("> - ⏱️ **捕获时间**: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
	                sb.AppendLine("> - 🏷️ **标签**: #学术论文 #arXiv");
	                sb.AppendFormat("{{: custom-type=\"literature-card\" custom-arxiv=\"{0}\" custom-clip-status=\"unread\" }}\n", id);
	                formattedCard = sb.ToString();
	                return true;
	            }

	            // 2. 嗅探标准 DOI: 10.1145/... 或 10.1038/...
	            var mDoi = Regex.Match(text, @"\b(10\.\d{4,9}/[-._;()/:A-Za-z0-9]+)\b");
	            if (mDoi.Success)
	            {
	                string doi = mDoi.Groups[1].Value.TrimEnd('.', ';', ')');
	                var sb = new StringBuilder();
	                sb.AppendLine("> 🎓 **学术文献引用 (DOI)**");
	                sb.AppendFormat("> - 🔗 **DOI 解析出处**: [doi:{0}](https://doi.org/{0})\n", doi);
	                sb.AppendLine("> - ⏱️ **捕获时间**: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
	                sb.AppendLine("> - 🏷️ **标签**: #学术文献 #DOI");
	                sb.AppendFormat("{{: custom-type=\"literature-card\" custom-doi=\"{0}\" custom-clip-status=\"unread\" }}\n", doi.Replace("\"", "\\\""));
	                formattedCard = sb.ToString();
	                return true;
	            }

	            return false;
	        }

	        /// <summary>
	        /// 智能超链与纯净网址自愈 (Smart Paste URL)
	        /// 剥离营销参数并将裸 URL 转化为规范的 Markdown 超链接 [标题](纯净URL)
	        /// </summary>
	        public static string PurifyAndResolveUrlLink(string rawUrl, string fallbackTitle = "")
	        {
	            if (string.IsNullOrEmpty(rawUrl)) return "";
	            string cleanUrl = PurifyUrl(rawUrl);

	            string title = fallbackTitle;
	            if (string.IsNullOrEmpty(title))
	            {
	                string site = DetectSiteName(cleanUrl);
	                title = (site != "网页摘录") ? (site + "内容") : cleanUrl;
	            }

	            string safeTitle = title.Replace("[", "【").Replace("]", "】").Trim();
	            return string.Format("[{0}]({1})", safeTitle, cleanUrl);
	        }

	        private static bool IsCjkChar(char c)
	        {
	            return (c >= 0x4E00 && c <= 0x9FA5) || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF);
	        }
	    }
	}
