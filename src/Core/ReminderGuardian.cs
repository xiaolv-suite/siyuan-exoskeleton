using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 系统级提醒守护 (P1-2)：周期轮询思源到期闪卡与待办截止日，
    /// 经托盘气泡 (Shell_NotifyIcon) 触达——主窗口隐藏、甚至思源未在前台也能收到系统级通知。
    /// 补位官方缺口：思源任务提醒此前只有微信渠道 (siyuan-note #15002)。
    /// 通知点击即达：闪卡类跳三秒微刷卡，任务类跳全景待办视界 (t:)。
    /// </summary>
    public static class ReminderGuardian
    {
        private static Timer _timer;
        private static Action<string, string, string> _onNotify;

        /// <summary>最近一次守护通知的跳转意图 ("fsrs" | "tasks" | "")，由托盘气泡点击消费后立即清零</summary>
        public static string LastJumpKind = "";

        private static int _lastDueCards = -1;
        private static string _lastTaskStamp = "";
        private static int _checking = 0;

        /// <param name="onNotify">(标题, 正文, 跳转意图)</param>
        public static void Start(Action<string, string, string> onNotify)
        {
            if (_timer != null) return;
            _onNotify = onNotify;
            // 开机后 90 秒首次检查（避开启动高峰），之后每 10 分钟一轮
            _timer = new Timer(Check, null, 90000, 600000);
        }

        public static void CheckNow()
        {
            ThreadPool.QueueUserWorkItem(s => Check(null));
        }

        private static void Check(object state)
        {
            if (Interlocked.Exchange(ref _checking, 1) == 1) return;
            try
            {
                int dueCards = CountDueCards();
                if (dueCards >= 0)
                {
                    // 数量变化才提醒（新卡到期 / 复习完毕归零后再次到期），不重复轰炸
                    if (dueCards > 0 && dueCards != _lastDueCards)
                    {
                        Notify("🧠 闪卡到期提醒", "有 " + dueCards + " 张闪卡到了复习时间。点击通知 → 三秒微刷卡开刷", "fsrs");
                    }
                    _lastDueCards = dueCards;
                }
            }
            catch { }
            try
            {
                int[] dueInfo = CountTaskDeadlines();
                int dueToday = dueInfo[0];
                int overdue = dueInfo[1];
                if (dueToday > 0 || overdue > 0)
                {
                    // 每天每状态最多提醒一次（日期+数量做指纹，数量变化会再提醒一次）
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd") + "|" + dueToday + "|" + overdue;
                    if (stamp != _lastTaskStamp)
                    {
                        _lastTaskStamp = stamp;
                        string msg = "今日到期 " + dueToday + " 条" + (overdue > 0 ? " · 逾期未完成 " + overdue + " 条" : "") + "。点击通知 → 全景待办视界";
                        Notify("📋 任务截止提醒", msg, "tasks");
                    }
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _checking, 0); }
        }

        private static int CountDueCards()
        {
            try
            {
                var res = SiYuanClient.PostAsync("/api/riff/getRiffDueCards", new { deckID = "" }).GetAwaiter().GetResult();
                if (!res.Success || res.Data == null) return -1;
                var dict = res.Data as Dictionary<string, object>;
                var cards = JsonHelper.GetList(dict, "cards");
                return cards != null ? cards.Count : 0;
            }
            catch { return -1; }
        }

        /// <summary>返回 [今日到期数, 逾期数]。严格识别 📅/due: 前缀日期，避免把正文普通日期误判为截止日</summary>
        private static int[] CountTaskDeadlines()
        {
            int dueToday = 0;
            int overdue = 0;
            string todayStr = DateTime.Now.ToString("yyyy-MM-dd");
            var rows = SiYuanClient.SqlAsync("SELECT content FROM blocks WHERE content LIKE '%- [ ]%' LIMIT 600").GetAwaiter().GetResult();
            if (rows == null) return new int[] { 0, 0 };
            foreach (var row in rows)
            {
                var dictRow = row as Dictionary<string, object>;
                if (dictRow == null) continue;
                string content = JsonHelper.GetString(dictRow, "content", "");
                if (string.IsNullOrEmpty(content) || !content.Contains("- [ ]")) continue;
                var m = Regex.Match(content, @"(?:📅\s*|due:\s*)(\d{4}-\d{2}-\d{2})");
                if (!m.Success) continue;
                string d = m.Groups[1].Value;
                int cmp = string.Compare(d, todayStr, StringComparison.Ordinal);
                if (cmp == 0) dueToday++;
                else if (cmp < 0) overdue++;
            }
            return new int[] { dueToday, overdue };
        }

        private static void Notify(string title, string message, string jumpKind)
        {
            LastJumpKind = jumpKind ?? "";
            if (_onNotify != null)
            {
                try { _onNotify(title, message, jumpKind); } catch { }
            }
        }
    }
}
