using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class SnippetDefinition
    {
        public string Shortcode { get; set; }
        public string Label { get; set; }
        public string Template { get; set; }
        public string Source { get; set; } // "内置" | "小驴常用"
    }

    /// <summary>
    /// 思源外骨骼 · 简码扩词与动态占位符求值引擎 (Espanso / Templater 对标)
    /// 1. 支持以 :简码 极速触发文本展开，如 :date, :time, :now, :meeting, :thx, :daily
    /// 2. 深度兼容并穿透检索「小驴常用」(xiaolv-common) 中的 custom-xlc-shortcode 资产
    /// 3. 支持动态占位符求值：{{date}}, {{time}}, {{now}}, {{clipboard}}, {{uuid}}
    /// </summary>
    public static class SnippetExpansionEngine
    {
        private static readonly Dictionary<string, SnippetDefinition> _builtinSnippets = new Dictionary<string, SnippetDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SnippetDefinition> _customSnippets = new Dictionary<string, SnippetDefinition>(StringComparer.OrdinalIgnoreCase);

        static SnippetExpansionEngine()
        {
            RegisterBuiltin(":date", "当前日期", "{{date}}");
            RegisterBuiltin(":time", "当前时间", "{{time}}");
            RegisterBuiltin(":now", "当前日期时间", "{{now}}");
            RegisterBuiltin(":todo", "待办清单项", "- [ ] ");
            RegisterBuiltin(":done", "已完成事项", "- [x] ");
            RegisterBuiltin(":clip", "贴入剪贴板纯文本", "{{clipboard}}");
            RegisterBuiltin(":thx", "感谢致意常用语", "非常感谢您的支持与宝贵反馈！");
            RegisterBuiltin(":meeting", "标准会议纪要骨架", "### 👥 会议纪要\n\n- 🕒 时间: {{now}}\n- 📌 参会人: \n- 🎯 核心议题: \n- 📋 行动项: \n  - [ ] ");
            RegisterBuiltin(":daily", "日记结构化骨架", "### 📅 今日概览 ({{date}})\n\n- 🎯 核心目标: \n- 📝 关键进展: \n- 💡 灵感反思: ");
            RegisterBuiltin(":code", "代码块模板", "```csharp\n// \n```");
        }

        public static void ReloadCustomSnippets(string json)
        {
            lock (_customSnippets)
            {
                _customSnippets.Clear();
                if (string.IsNullOrEmpty(json)) return;
                try
                {
                    var items = JsonHelper.FromJson<List<SnippetDefinition>>(json);
                    if (items != null)
                    {
                        foreach (var it in items)
                        {
                            if (!string.IsNullOrEmpty(it.Shortcode))
                            {
                                it.Source = "自定义";
                                _customSnippets[it.Shortcode] = it;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static void RegisterBuiltin(string code, string label, string template)
        {
            _builtinSnippets[code] = new SnippetDefinition
            {
                Shortcode = code,
                Label = label,
                Template = template,
                Source = "内置"
            };
        }

        public static List<SnippetDefinition> GetMatchingSnippets(string prefix)
        {
            var results = new List<SnippetDefinition>();
            if (string.IsNullOrEmpty(prefix)) return results;

            string pLower = prefix.ToLowerInvariant();

            // 1. 自定义简码优先匹配
            lock (_customSnippets)
            {
                foreach (var kvp in _customSnippets)
                {
                    if (kvp.Key.ToLowerInvariant().StartsWith(pLower))
                    {
                        results.Add(kvp.Value);
                    }
                }
            }

            // 2. 内置标准简码
            foreach (var kvp in _builtinSnippets)
            {
                if (kvp.Key.ToLowerInvariant().StartsWith(pLower))
                {
                    results.Add(kvp.Value);
                }
            }

            return results;
        }

        /// <summary>
        /// 解析模板中的全部动态宏变量
        /// </summary>
        public static string ResolveVariables(string template)
        {
            if (string.IsNullOrEmpty(template)) return "";
            var now = DateTime.Now;

            string res = template;
            res = Regex.Replace(res, @"\{\{date\}\}", now.ToString("yyyy-MM-dd"), RegexOptions.IgnoreCase);
            res = Regex.Replace(res, @"\{\{time\}\}", now.ToString("HH:mm"), RegexOptions.IgnoreCase);
            res = Regex.Replace(res, @"\{\{now\}\}", now.ToString("yyyy-MM-dd HH:mm"), RegexOptions.IgnoreCase);
            res = Regex.Replace(res, @"\{\{year\}\}", now.ToString("yyyy"), RegexOptions.IgnoreCase);
            res = Regex.Replace(res, @"\{\{month\}\}", now.ToString("MM"), RegexOptions.IgnoreCase);
            res = Regex.Replace(res, @"\{\{day\}\}", now.ToString("dd"), RegexOptions.IgnoreCase);
            res = Regex.Replace(res, @"\{\{weekday\}\}", GetWeekdayChinese(now), RegexOptions.IgnoreCase);
            res = Regex.Replace(res, @"\{\{uuid\}\}", Guid.NewGuid().ToString("N").Substring(0, 8), RegexOptions.IgnoreCase);

            if (res.IndexOf("{{clipboard}}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string clip = NativeClipboard.GetTextSafe() ?? "";
                res = Regex.Replace(res, @"\{\{clipboard\}\}", clip, RegexOptions.IgnoreCase);
            }

            return res;
        }

        private static string GetWeekdayChinese(DateTime dt)
        {
            switch (dt.DayOfWeek)
            {
                case DayOfWeek.Monday: return "周一";
                case DayOfWeek.Tuesday: return "周二";
                case DayOfWeek.Wednesday: return "周三";
                case DayOfWeek.Thursday: return "周四";
                case DayOfWeek.Friday: return "周五";
                case DayOfWeek.Saturday: return "周六";
                case DayOfWeek.Sunday: return "周日";
                default: return "";
            }
        }

        /// <summary>
        /// 尝试按简码展开文本内容
        /// </summary>
        public static string Expand(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            string key = input.Trim();

            SnippetDefinition def;
            lock (_customSnippets)
            {
                if (_customSnippets.TryGetValue(key, out def))
                {
                    return ResolveVariables(def.Template);
                }
            }

            if (_builtinSnippets.TryGetValue(key, out def))
            {
                return ResolveVariables(def.Template);
            }

            return input;
        }
    }
}
