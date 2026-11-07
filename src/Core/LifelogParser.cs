using System;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public enum LifelogCategory
    {
        Note,        // 灵感与通用闪念 (💡)
        Expense,     // 账目花销 (💰)
        Focus,       // 专注与时序倒推 (⏱️)
        Mood,        // 心情与状态能量 (🎭)
        Habit,       // 习惯与打卡 (🏃)
        Diet,        // 饮食记录 (🥗)
        Todo,        // 待办任务清单 (📋)
        Flashcard,   // 问答闪卡 (🎴)
        Contact,     // 人脉互动 (👥)
        Project      // 项目直投 (📁)
    }

    public class LifelogEntry
    {
        public LifelogCategory Category { get; set; }
        public string RawText { get; set; }
        public string CoreContent { get; set; }
        public string DisplayIcon { get; set; }
        public string CategoryLabel { get; set; }
        public string FormattedMarkdown { get; set; }
        public string IalAttributes { get; set; }
        public string SuggestedHeading { get; set; }
        public string ThemePillBg { get; set; }
        public string ThemeBorderHex { get; set; }
        public string ThemeTextHex { get; set; }
        public string DueDate { get; set; }
        public string HabitItem { get; set; }
        public string HabitVal { get; set; }
        public string HabitUnit { get; set; }
    }

    /// <summary>
    /// 思源外骨骼 · Lifelog 智能时序生活流解析器
    /// 将自然语言碎片极速转换为具有丰富语义、时间推导与思源原生 IAL 属性的结构化记录
    /// </summary>
    public static class LifelogParser
    {
        public static LifelogEntry Parse(string rawInput, string sourceCitation = "", DateTime? entryTime = null)
        {
            var now = entryTime.HasValue ? entryTime.Value : DateTime.Now;
            var timeStr = now.ToString("HH:mm");
            var dateStr = now.ToString("yyyy-MM-dd");
            var raw = (rawInput ?? "").Trim();

            var entry = new LifelogEntry
            {
                RawText = raw,
                CoreContent = raw,
                DisplayIcon = "💡",
                CategoryLabel = "闪念",
                Category = LifelogCategory.Note,
                SuggestedHeading = "💡 灵感与闪念",
                ThemePillBg = "#1e1b4b",
                ThemeBorderHex = "#6366f1",
                ThemeTextHex = "#a5b4fc"
            };

            if (string.IsNullOrEmpty(raw))
            {
                entry.FormattedMarkdown = "- " + timeStr + " (空记事)";
                return entry;
            }

            string sourceTag = (!string.IsNullOrEmpty(sourceCitation) && sourceCitation != "桌面应用") 
                ? " *(来自 " + sourceCitation + ")*" 
                : "";

            // 1. 待办任务清单 (Todo: '!' / '待办:' / 'todo:'，排除 Markdown 图片 ![] )
            if (((raw.StartsWith("!") || raw.StartsWith("！")) && !raw.StartsWith("![") && !raw.StartsWith("！[")) || 
                raw.StartsWith("待办:", StringComparison.OrdinalIgnoreCase) || 
                raw.StartsWith("待办：", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("todo:", StringComparison.OrdinalIgnoreCase))
            {
                string content = raw.TrimStart('!', '！', ' ');
                if (content.StartsWith("待办", StringComparison.OrdinalIgnoreCase) || content.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                {
                    content = content.Substring(content.IndexOfAny(new char[] { ':', '：' }) + 1).Trim();
                }

                // 穿透联动小驴管家：自然语言相对到期日解析 (今天/明天/后天/大后天)
                string parsedDueDate = "";
                if (content.StartsWith("今天")) { parsedDueDate = now.ToString("yyyy-MM-dd"); content = content.Substring(2).Trim(); }
                else if (content.StartsWith("明天")) { parsedDueDate = now.AddDays(1).ToString("yyyy-MM-dd"); content = content.Substring(2).Trim(); }
                else if (content.StartsWith("后天")) { parsedDueDate = now.AddDays(2).ToString("yyyy-MM-dd"); content = content.Substring(2).Trim(); }
                else if (content.StartsWith("大后天")) { parsedDueDate = now.AddDays(3).ToString("yyyy-MM-dd"); content = content.Substring(3).Trim(); }

                entry.Category = LifelogCategory.Todo;
                entry.DisplayIcon = "📋";
                entry.CategoryLabel = "待办";
                entry.CoreContent = content;
                entry.SuggestedHeading = "📋 今日待办";
                entry.ThemePillBg = "#064e3b";
                entry.ThemeBorderHex = "#10b981";
                entry.ThemeTextHex = "#6ee7b7";
                entry.DueDate = parsedDueDate;
                entry.IalAttributes = "{: custom-lifelog-type=\"todo\" custom-lifelog-time=\"" + timeStr + "\"" + (!string.IsNullOrEmpty(parsedDueDate) ? " custom-completion-time=\"" + parsedDueDate + "\"" : "") + " }";
                entry.FormattedMarkdown = "- [ ] " + timeStr + " " + content + (!string.IsNullOrEmpty(parsedDueDate) ? " *(到期: " + parsedDueDate + ")*" : "") + sourceTag;
                return entry;
            }

            // 2. 问答闪卡 (Flashcard: '?' / '闪卡:' / 'card:')
            if (raw.StartsWith("?") || raw.StartsWith("？") || 
                raw.StartsWith("闪卡:", StringComparison.OrdinalIgnoreCase) || 
                raw.StartsWith("闪卡：", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("card:", StringComparison.OrdinalIgnoreCase))
            {
                string content = raw.TrimStart('?', '？', ' ');
                if (content.StartsWith("闪卡", StringComparison.OrdinalIgnoreCase) || content.StartsWith("card", StringComparison.OrdinalIgnoreCase))
                {
                    content = content.Substring(content.IndexOfAny(new char[] { ':', '：' }) + 1).Trim();
                }
                entry.Category = LifelogCategory.Flashcard;
                entry.DisplayIcon = "🎴";
                entry.CategoryLabel = "闪卡";
                entry.CoreContent = content;
                entry.SuggestedHeading = "🧠 闪卡复习";
                entry.ThemePillBg = "#500724";
                entry.ThemeBorderHex = "#ec4899";
                entry.ThemeTextHex = "#fbcfe8";
                
                var splitIdx = content.IndexOf("//");
                if (splitIdx < 0) splitIdx = content.IndexOf("——");
                string q = splitIdx > 0 ? content.Substring(0, splitIdx).Trim() : content;
                string a = splitIdx > 0 ? content.Substring(splitIdx + 2).Trim() : "(待补充答案)";

                entry.IalAttributes = "{: custom-riff-decks=\"default\" custom-type=\"fsrs-card\" custom-lifelog-type=\"card\" }";
                entry.FormattedMarkdown = "> 🎴 **" + q + "**\n>\n> " + a.Replace("\n", "\n> ") + "\n> *溯源*: " + (string.IsNullOrEmpty(sourceCitation) ? "桌面速记" : sourceCitation) + " · " + dateStr + "\n" + entry.IalAttributes;
                return entry;
            }

            // 3. 人脉互动 (Contact: '@' / '人脉:')
            if (raw.StartsWith("@") || raw.StartsWith("＠") || 
                raw.StartsWith("人脉:", StringComparison.OrdinalIgnoreCase) || 
                raw.StartsWith("人脉：", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(raw, @"^[@＠]?(?:人脉[：:])?\s*([^\s：:,，]+)[\s：:,，]*(.*)$");
                string person = m.Success ? m.Groups[1].Value.Trim() : "";
                string note = m.Success ? m.Groups[2].Value.Trim() : "";
                entry.Category = LifelogCategory.Contact;
                entry.DisplayIcon = "👥";
                entry.CategoryLabel = "人脉";
                entry.CoreContent = person + " " + note;
                entry.SuggestedHeading = "👥 人脉互动";
                entry.ThemePillBg = "#3b0764";
                entry.ThemeBorderHex = "#a855f7";
                entry.ThemeTextHex = "#d8b4fe";
                entry.IalAttributes = "{: custom-lifelog-type=\"contact\" custom-lifelog-person=\"" + person.Replace("\"", "") + "\" }";
                entry.FormattedMarkdown = "- " + timeStr + " 👥 [[" + person + "]] " + note + sourceTag;
                return entry;
            }

            // 4. 账目与开销记录 (Expense: '¥' / '$' / '花销' / '消费' / '买' / '支出' / '记账')
            var expMatch = Regex.Match(raw, @"^(?:[¥￥$]|花销[：:]|消费[：:]|支出[：:]|买[：:]|记账[：:])\s*(\d+(?:\.\d+)?)\s*(.*)$", RegexOptions.IgnoreCase);
            if (!expMatch.Success)
            {
                expMatch = Regex.Match(raw, @"^(\d+(?:\.\d+)?)\s*(?:元|块|rmb|usd)\s*(.*)$", RegexOptions.IgnoreCase);
            }
            if (expMatch.Success)
            {
                string amount = expMatch.Groups[1].Value;
                string item = expMatch.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(item)) item = "日常支出";

                entry.Category = LifelogCategory.Expense;
                entry.DisplayIcon = "💰";
                entry.CategoryLabel = "账目";
                entry.CoreContent = "¥" + amount + " " + item;
                entry.SuggestedHeading = "💰 账目花销";
                entry.ThemePillBg = "#451a03";
                entry.ThemeBorderHex = "#f59e0b";
                entry.ThemeTextHex = "#fde68a";
                entry.IalAttributes = "{: custom-lifelog-type=\"expense\" custom-lifelog-amount=\"" + amount + "\" custom-lifelog-item=\"" + item.Replace("\"", "") + "\" custom-lifelog-time=\"" + timeStr + "\" }";
                entry.FormattedMarkdown = "- " + timeStr + " 💰 **¥" + amount + "** · " + item + sourceTag;
                return entry;
            }

            // 5. 专注时序与时光胶囊 (Focus: '~' / '～' / '专注:' / '番茄:' / '45m' / '1h')
            var focusMatch = Regex.Match(raw, @"^(?:[~～]|专注[：:]|番茄[：:]|pomodoro[：:])\s*(\d+(?:\.\d+)?)\s*([mh分时]|min|hour|小时|分钟)?\s*(.*)$", RegexOptions.IgnoreCase);
            if (!focusMatch.Success)
            {
                focusMatch = Regex.Match(raw, @"^(\d+(?:\.\d+)?)\s*(m|min|分|分钟|h|hour|小时)\s*(?:专注|番茄)?\s*(.+)$", RegexOptions.IgnoreCase);
            }
            if (focusMatch.Success)
            {
                double val = double.Parse(focusMatch.Groups[1].Value);
                string unit = focusMatch.Groups[2].Value.ToLowerInvariant();
                string task = focusMatch.Groups[3].Value.Trim();
                if (string.IsNullOrEmpty(task)) task = "深度专注时刻";

                int totalMinutes = (int)val;
                if (unit == "h" || unit == "hour" || unit == "时" || unit == "小时")
                {
                    totalMinutes = (int)(val * 60);
                }

                var startDt = now.AddMinutes(-totalMinutes);
                string startStr = startDt.ToString("HH:mm");

                entry.Category = LifelogCategory.Focus;
                entry.DisplayIcon = "⏱️";
                entry.CategoryLabel = "专注";
                entry.CoreContent = totalMinutes + "m " + task;
                entry.SuggestedHeading = "⏱️ 专注时序";
                entry.ThemePillBg = "#082f49";
                entry.ThemeBorderHex = "#0284c7";
                entry.ThemeTextHex = "#7dd3fc";
                entry.IalAttributes = "{: custom-lifelog-type=\"focus\" custom-lifelog-start=\"" + startStr + "\" custom-lifelog-end=\"" + timeStr + "\" custom-lifelog-dur=\"" + totalMinutes + "\" }";
                entry.FormattedMarkdown = "- " + startStr + "~" + timeStr + " ⏱️ **" + task + "** (" + totalMinutes + "min)" + sourceTag;
                return entry;
            }

            // 6. 习惯打卡与身体能量 (Habit: '+' / '＋' / '打卡:' / 'habit:')
            if (raw.StartsWith("+") || raw.StartsWith("＋") || 
                raw.StartsWith("打卡:", StringComparison.OrdinalIgnoreCase) || 
                raw.StartsWith("打卡：", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("habit:", StringComparison.OrdinalIgnoreCase))
            {
                string hRaw = raw.TrimStart('+', '＋', ' ');
                if (hRaw.StartsWith("打卡", StringComparison.OrdinalIgnoreCase) || hRaw.StartsWith("habit", StringComparison.OrdinalIgnoreCase))
                {
                    hRaw = hRaw.Substring(hRaw.IndexOfAny(new char[] { ':', '：' }) + 1).Trim();
                }

                string habitIcon = "🏃";
                string lower = hRaw.ToLowerInvariant();
                if (lower.Contains("水") || lower.Contains("water") || lower.Contains("drink")) habitIcon = "💧";
                else if (lower.Contains("跑") || lower.Contains("run") || lower.Contains("步")) habitIcon = "👟";
                else if (lower.Contains("读") || lower.Contains("read") || lower.Contains("书")) habitIcon = "📖";
                else if (lower.Contains("重") || lower.Contains("weight")) habitIcon = "⚖️";
                else if (lower.Contains("睡") || lower.Contains("sleep")) habitIcon = "🌙";

                string habitItem = hRaw;
                string habitVal = "1";
                string habitUnit = "";

                var mH = Regex.Match(hRaw, @"^([a-zA-Z\u4e00-\u9fa5]+)\s*(\d+(?:\.\d+)?)\s*([a-zA-Z\u4e00-\u9fa5]*)");
                if (mH.Success)
                {
                    habitItem = mH.Groups[1].Value.Trim();
                    habitVal = mH.Groups[2].Value.Trim();
                    habitUnit = mH.Groups[3].Value.Trim();
                }

                entry.Category = LifelogCategory.Habit;
                entry.DisplayIcon = habitIcon;
                entry.CategoryLabel = "打卡";
                entry.CoreContent = hRaw;
                entry.HabitItem = habitItem;
                entry.HabitVal = habitVal;
                entry.HabitUnit = habitUnit;
                entry.SuggestedHeading = "🏃 习惯打卡";
                entry.ThemePillBg = "#064e3b";
                entry.ThemeBorderHex = "#059669";
                entry.ThemeTextHex = "#6ee7b7";
                entry.IalAttributes = "{: custom-lifelog-type=\"habit\" custom-lifelog-val=\"" + hRaw.Replace("\"", "") + "\" custom-lifelog-time=\"" + timeStr + "\" }";
                entry.FormattedMarkdown = "- " + timeStr + " " + habitIcon + " 习惯打卡 · **" + hRaw + "**" + sourceTag;
                return entry;
            }

            // 7. 饮食与摄入 (Diet: 'eat:' / '饮食:' / '早餐:' / '午餐:' / '晚餐:' / '夜宵:')
            var dietMatch = Regex.Match(raw, @"^(?:eat[：:]|饮食[：:]|早餐[：:]|午餐[：:]|晚餐[：:]|夜宵[：:])\s*(.*)$", RegexOptions.IgnoreCase);
            if (dietMatch.Success)
            {
                string meal = dietMatch.Groups[1].Value.Trim();
                entry.Category = LifelogCategory.Diet;
                entry.DisplayIcon = "🥗";
                entry.CategoryLabel = "饮食";
                entry.CoreContent = meal;
                entry.SuggestedHeading = "🥗 饮食与作息";
                entry.ThemePillBg = "#451a03";
                entry.ThemeBorderHex = "#f97316";
                entry.ThemeTextHex = "#fed7aa";
                entry.IalAttributes = "{: custom-lifelog-type=\"diet\" custom-lifelog-meal=\"" + meal.Replace("\"", "") + "\" custom-lifelog-time=\"" + timeStr + "\" }";
                entry.FormattedMarkdown = "- " + timeStr + " 🥗 饮食 · " + meal + sourceTag;
                return entry;
            }

            // 8. 心情与状态能量 (Mood: 'mood:' / '情绪:' / '心情:' / Emoji开头)
            var moodMatch = Regex.Match(raw, @"^(?:mood[：:]|情绪[：:]|心情[：:])\s*(.*)$", RegexOptions.IgnoreCase);
            string moodContent = moodMatch.Success ? moodMatch.Groups[1].Value.Trim() : "";
            bool isMoodEmoji = false;
            string moodEmoji = "🎭";

            if (string.IsNullOrEmpty(moodContent))
            {
                string[] emojis = new string[] { "😄", "😊", "🔥", "☕", "😴", "😭", "🌧️", "✨", "💪", "🧘", "🎉", "⚡" };
                foreach (var em in emojis)
                {
                    if (raw.StartsWith(em))
                    {
                        isMoodEmoji = true;
                        moodEmoji = em;
                        moodContent = raw.Substring(em.Length).Trim();
                        break;
                    }
                }
            }

            if (moodMatch.Success || isMoodEmoji)
            {
                entry.Category = LifelogCategory.Mood;
                entry.DisplayIcon = moodEmoji;
                entry.CategoryLabel = "心情";
                entry.CoreContent = moodContent;
                entry.SuggestedHeading = "🌟 灵感与心流";
                entry.ThemePillBg = "#4c0519";
                entry.ThemeBorderHex = "#e11d48";
                entry.ThemeTextHex = "#fda4af";
                entry.IalAttributes = "{: custom-lifelog-type=\"mood\" custom-lifelog-val=\"" + moodEmoji + "\" custom-lifelog-time=\"" + timeStr + "\" }";
                entry.FormattedMarkdown = "- " + timeStr + " " + moodEmoji + " 心情状态: " + moodContent + sourceTag;
                return entry;
            }

            // 9. 项目专属标记 (Project: '#' / '项目:')
            if ((raw.StartsWith("#") || raw.StartsWith("＃")) && !raw.StartsWith("##") && !raw.StartsWith("# "))
            {
                var match = Regex.Match(raw, "^[#＃]([^\\s：:,，]+)[\\s：:,，]*(.*)$");
                string tag = match.Success ? match.Groups[1].Value.Trim() : "";
                string pContent = match.Success ? match.Groups[2].Value.Trim() : "";
                entry.Category = LifelogCategory.Project;
                entry.DisplayIcon = "📁";
                entry.CategoryLabel = "项目";
                entry.CoreContent = tag + " " + pContent;
                entry.SuggestedHeading = "📁 项目直投";
                entry.ThemePillBg = "#451a03";
                entry.ThemeBorderHex = "#d97706";
                entry.ThemeTextHex = "#fde68a";
                entry.IalAttributes = "{: custom-lifelog-type=\"project\" custom-lifelog-tag=\"" + tag.Replace("\"", "") + "\" }";
                entry.FormattedMarkdown = "- " + timeStr + " #" + tag + " " + pContent + sourceTag;
                return entry;
            }

            // 10. 默认灵感与通用记事 (Note / Spark)
            string displayIcon = "💡";
            if (raw.StartsWith("思考") || raw.StartsWith("反思")) displayIcon = "🤔";
            else if (raw.StartsWith("灵感") || raw.StartsWith("idea")) displayIcon = "✨";

            entry.Category = LifelogCategory.Note;
            entry.DisplayIcon = displayIcon;
            entry.CategoryLabel = "闪念";
            entry.CoreContent = raw;
            entry.SuggestedHeading = "💡 灵感与闪念";
            entry.ThemePillBg = "#1e1b4b";
            entry.ThemeBorderHex = "#6366f1";
            entry.ThemeTextHex = "#a5b4fc";
            entry.IalAttributes = "{: custom-lifelog-type=\"spark\" custom-lifelog-time=\"" + timeStr + "\" }";
            string displayPrefix = raw.StartsWith("💡") ? "" : displayIcon + " ";
            entry.FormattedMarkdown = "- " + timeStr + " " + displayPrefix + raw + sourceTag;
            return entry;
        }
    }
}
