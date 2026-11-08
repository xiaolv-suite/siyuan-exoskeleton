using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public enum MergeMode
    {
        Bullets,    // 无序清单列表
        Quotes,     // 引用摘录块
        Sections,   // 小节大纲流
        Plain       // 空行隔开自然段
    }

    /// <summary>
    /// 思源外骨骼 · 剪贴板历史批量合并与排版工作台 (ClipboardBatchMerger)
    /// 1. 解决跨网页、跨文档多段碎片资料搜集时的零散杂乱痛点
    /// 2. 支持批量提取剪贴板最近 N 条记录，自动去重、去噪、盘古排版
    /// 3. 支持转译为无序列表项 (* ...)、Callout 引用块或标准小节大纲
    /// 4. 1 键将结构化合并结果注入外部活动光标，或直投今日日记指定小节
    /// </summary>
    public static class ClipboardBatchMerger
    {
        public static string MergeSnippets(System.Collections.IEnumerable snippets, MergeMode mode = MergeMode.Bullets, string title = "多源摘录汇总")
        {
            if (snippets == null) return "";

            var cleanList = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawObj in snippets)
            {
                if (rawObj == null) continue;
                string raw = rawObj.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string clean = TextCleaner.CleanAll(raw.Trim());
                if (string.IsNullOrEmpty(clean)) clean = raw.Trim();

                if (!seen.Contains(clean))
                {
                    seen.Add(clean);
                    cleanList.Add(clean);
                }
            }

            if (cleanList.Count == 0) return "";

            var sb = new StringBuilder();

            switch (mode)
            {
                case MergeMode.Bullets:
                    sb.AppendFormat("### 📋 《{0}》 (共 {1} 项要点)\n", title, cleanList.Count);
                    foreach (var it in cleanList)
                    {
                        var lines = it.Split('\n');
                        sb.AppendFormat("* {0}\n", lines[0].Trim());
                        for (int i = 1; i < lines.Length; i++)
                        {
                            if (!string.IsNullOrEmpty(lines[i].Trim()))
                            {
                                sb.AppendFormat("  {0}\n", lines[i].Trim());
                            }
                        }
                    }
                    sb.AppendFormat("{{: custom-type=\"merged-bullets\" custom-count=\"{0}\" }}\n", cleanList.Count);
                    break;

                case MergeMode.Quotes:
                    sb.AppendFormat("### 💬 《{0}》 研读摘录汇编\n", title);
                    foreach (var it in cleanList)
                    {
                        sb.AppendLine(">");
                        foreach (var l in it.Split('\n'))
                        {
                            sb.AppendFormat("> {0}\n", l.Trim());
                        }
                    }
                    sb.AppendFormat("{{: custom-type=\"merged-quotes\" custom-count=\"{0}\" }}\n", cleanList.Count);
                    break;

                case MergeMode.Sections:
                    sb.AppendFormat("### 📑 《{0}》 结构化汇编\n\n", title);
                    for (int i = 0; i < cleanList.Count; i++)
                    {
                        string firstLine = cleanList[i].Split('\n')[0].Trim();
                        if (firstLine.Length > 20) firstLine = firstLine.Substring(0, 20) + "...";
                        sb.AppendFormat("#### {0}. {1}\n", i + 1, firstLine);
                        sb.AppendLine(cleanList[i]);
                        sb.AppendLine();
                    }
                    sb.AppendFormat("{{: custom-type=\"merged-sections\" custom-count=\"{0}\" }}\n", cleanList.Count);
                    break;

                case MergeMode.Plain:
                default:
                    foreach (var it in cleanList)
                    {
                        sb.AppendLine(it);
                        sb.AppendLine();
                    }
                    break;
            }

            return sb.ToString().Trim();
        }
    }
}
