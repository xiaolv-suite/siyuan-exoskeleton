using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class ChatMessageItem
    {
        public string Sender { get; set; }
        public string TimeStr { get; set; }
        public string Content { get; set; }
        public bool HasActionItem { get; set; }

        public ChatMessageItem()
        {
            Sender = "";
            TimeStr = "";
            Content = "";
            HasActionItem = false;
        }
    }

    public class ChatLogSummary
    {
        public bool IsChatLog { get; set; }
        public List<string> Senders { get; set; }
        public List<ChatMessageItem> Messages { get; set; }
        public List<string> ActionTodos { get; set; }

        public ChatLogSummary()
        {
            IsChatLog = false;
            Senders = new List<string>();
            Messages = new List<ChatMessageItem>();
            ActionTodos = new List<string>();
        }
    }

    /// <summary>
    /// 思源外骨骼 · 桌面即时通讯 (微信/企业微信/飞书/钉钉) 对话记录智能清洗器 (ChatLogCleaner)
    /// 1. 自动识别多人群聊/单聊复制文本中的时间戳与发言人头信息
    /// 2. 剥离无意义撤回、拍一拍、表情乱码等噪声杂质
    /// 3. 合并同一发言人在连续多行中的碎片回复
    /// 4. 智能嗅探行动指令 (请/务必/待办/截止)，萃取生成思源标准任务待办清单 (- [ ])
    /// 5. 生成优雅的思源原生对话块 (Callout 样式) 与标准元数据 IAL
    /// </summary>
    public static class ChatLogCleaner
    {
        // 匹配常见 IM 发言头正则：
        // 1. 张三 14:32 或 张三 14:32:15
        // 2. 张三 2026/11/07 14:32 或 张三 2026-11-07 14:32:05
        // 3. [2026-11-07 14:32] 张三: 或 张三 (14:32):
        // 4. 张三：消息内容
        private static readonly Regex _headerRegex = new Regex(
            @"^(?:\[?(\d{4}[-/年]\d{1,2}[-/月]\d{1,2}[日]?\s+)?(\d{1,2}:\d{2}(?::\d{2})?)\]?\s+)?([^\s：:]{1,20})(?:\s+|\s*[(（]\s*(\d{1,2}:\d{2}(?::\d{2})?)\s*[)）]\s*|\s*[:：]\s*)(.*)$",
            RegexOptions.Compiled);

        private static readonly Regex _simpleHeaderRegex = new Regex(
            @"^([^\s：:]{1,16})\s+(\d{1,2}:\d{2}(?::\d{2})?)$",
            RegexOptions.Compiled);

        private static readonly Regex _wechatStyleRegex = new Regex(
            @"^([^\s：:]{1,16})\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2}(?::\d{2})?)$",
            RegexOptions.Compiled);

        // 噪声过滤正则
        private static readonly Regex _noiseRegex = new Regex(
            @"^(?:撤回了一条消息|拍了拍我|加入了群聊|移出了群聊|\[动画表情\]|\[微信红包\]|\[语音\]|\[视频号\]|\[图片\])$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 行动待办嗅探正则
        private static readonly Regex _actionItemRegex = new Regex(
            @"(?:待办|TODO|请.+?(?:在|于|前|尽快)|务必|安排|落实|核对|提交|截止|负责|跟进)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ChatLogSummary Analyze(string rawText)
        {
            var summary = new ChatLogSummary();
            if (string.IsNullOrWhiteSpace(rawText)) return summary;

            var lines = rawText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            if (lines.Length < 2) return summary;

            var senderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ChatMessageItem currentMsg = null;
            int matchedHeaderCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (_noiseRegex.IsMatch(line)) continue;

                // 尝试匹配格式 A: "张三 14:32" 或 "张三 2026/11/07 14:32"
                var matchSimple = _simpleHeaderRegex.Match(line);
                if (!matchSimple.Success) matchSimple = _wechatStyleRegex.Match(line);

                if (matchSimple.Success)
                {
                    matchedHeaderCount++;
                    string sender = matchSimple.Groups[1].Value.Trim();
                    string timeStr = matchSimple.Groups[2].Value.Trim();

                    currentMsg = new ChatMessageItem
                    {
                        Sender = sender,
                        TimeStr = timeStr,
                        Content = ""
                    };
                    summary.Messages.Add(currentMsg);
                    senderSet.Add(sender);
                    continue;
                }

                // 尝试匹配格式 B: "张三: 消息内容"
                int colonIdx = line.IndexOfAny(new char[] { '：', ':' });
                if (colonIdx > 0 && colonIdx <= 12 && colonIdx < line.Length - 1 && !line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    string possibleSender = line.Substring(0, colonIdx).Trim();
                    string possibleContent = line.Substring(colonIdx + 1).Trim();

                    if (!possibleSender.Contains(" ") && possibleSender.Length >= 1)
                    {
                        matchedHeaderCount++;
                        currentMsg = new ChatMessageItem
                        {
                            Sender = possibleSender,
                            TimeStr = DateTime.Now.ToString("HH:mm"),
                            Content = possibleContent
                        };
                        summary.Messages.Add(currentMsg);
                        senderSet.Add(possibleSender);
                        continue;
                    }
                }

                // 属于上一条消息的正文多行追加
                if (currentMsg != null)
                {
                    if (string.IsNullOrEmpty(currentMsg.Content))
                    {
                        currentMsg.Content = line;
                    }
                    else
                    {
                        currentMsg.Content += "\n" + line;
                    }
                }
            }

            // 若匹配到至少 2 处发言头，判定为有效对话记录
            if (matchedHeaderCount >= 2 && summary.Messages.Count >= 2)
            {
                summary.IsChatLog = true;
                summary.Senders = new List<string>(senderSet);

                // 提取行动待办
                foreach (var msg in summary.Messages)
                {
                    if (string.IsNullOrEmpty(msg.Content)) continue;
                    if (_actionItemRegex.IsMatch(msg.Content))
                    {
                        msg.HasActionItem = true;
                        string cleanSnippet = msg.Content.Replace("\n", " ").Trim();
                        if (cleanSnippet.Length > 80) cleanSnippet = cleanSnippet.Substring(0, 80) + "...";
                        summary.ActionTodos.Add(string.Format("**待办** ({0}): {1}", msg.Sender, cleanSnippet));
                    }
                }
            }

            return summary;
        }

        /// <summary>
        /// 生成针对思源笔记的标准对话流 Callout 块与行动待办 Markdown
        /// </summary>
        public static string BuildMarkdownSummary(ChatLogSummary summary, string title = "群聊纪要与决议摘录")
        {
            if (summary == null || !summary.IsChatLog || summary.Messages == null || summary.Messages.Count == 0)
            {
                return "";
            }

            int senderCount = summary.Senders != null ? summary.Senders.Count : 0;
            int msgCount = summary.Messages != null ? summary.Messages.Count : 0;
            var sb = new StringBuilder();
            sb.AppendFormat("### 💬 《{0}》 (共 {1} 位发言人 · {2} 条发言)\n", 
                title, senderCount, msgCount);
            sb.AppendFormat("> ⏱️ **清洗整理于**: {0} · 桌面即时通讯对话流\n\n", 
                DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

            // 1. 对话流引用块 (Callout 样式)
            sb.AppendLine("#### 📝 对话纪要流");
            foreach (var msg in summary.Messages)
            {
                if (string.IsNullOrEmpty(msg.Content)) continue;
                string timeTag = !string.IsNullOrEmpty(msg.TimeStr) ? " (" + msg.TimeStr + ")" : "";
                sb.AppendFormat("> 💬 **{0}**{1}:\n", msg.Sender, timeTag);

                var contentLines = msg.Content.Split('\n');
                foreach (var cl in contentLines)
                {
                    sb.AppendFormat("> {0}\n", cl.Trim());
                }
                sb.AppendLine(">");
            }
            sb.AppendLine();

            // 2. 提取行动待办
            if (summary.ActionTodos != null && summary.ActionTodos.Count > 0)
            {
                sb.AppendLine("#### 📋 自动提取行动待办");
                foreach (var todo in summary.ActionTodos)
                {
                    sb.AppendFormat("- [ ] {0}\n", todo);
                }
                sb.AppendLine();
            }

            // 3. 挂载标准 IAL 属性
            sb.AppendFormat("{{: custom-type=\"chat-meeting-log\" custom-sender-count=\"{0}\" custom-message-count=\"{1}\" }}\n",
                senderCount, msgCount);

            return sb.ToString();
        }
    }
}
