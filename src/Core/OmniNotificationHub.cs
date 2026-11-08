using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class NotificationItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public string Level { get; set; }            // "info" | "success" | "warn" | "error"
        public string Source { get; set; }           // "siyuan" | "desktop" | "quicker" | "system" | "focus" | "security"
        public long Timestamp { get; set; }          // Unix ms
        public string TimeFormatted { get; set; }    // "yyyy-MM-dd HH:mm:ss"
        public int RepeatCount { get; set; }         // 自动折叠去重
        public string PayloadJson { get; set; }

        public NotificationItem()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = "";
            Message = "";
            Level = "info";
            Source = "desktop";
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            RepeatCount = 1;
            PayloadJson = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全景系统与应用通知中枢 (OmniNotificationHub)
    /// 1. 深度对标 siyuan-notification-history 并大幅系统化扩展至操作系统与本地生态
    /// 2. 覆盖四大通知维度：思源内核弹窗、Windows 系统与应用、开发脚本构建事件、专注与习惯打卡事件
    /// 3. 四级严重度标识 (info 蓝色 / success 绿色 / warn 黄色 / error 红色)
    /// 4. 15 秒滑动窗口智能折叠去重 (x2, x3)，防止风暴刷屏
    /// 5. 一键沉淀入日记 (BuildJournalMarkdown) 与对外开放 Webhook (POST /api/notify)
    /// </summary>
    public static class OmniNotificationHub
    {
        private static readonly object _lock = new object();
        private static List<NotificationItem> _memoryRing = null;
        private const int MAX_CAPACITY = 500;
        private const int DEDUP_WINDOW_MS = 15000;

        private static string LocalCachePath
        {
            get
            {
                string dir = EmbeddedResourceManager.AppDataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "notification_history.json");
            }
        }

        /// <summary>
        /// 推送并记录一条全局通知或事件
        /// </summary>
        public static NotificationItem Push(string title, string message, string level = "info", string source = "desktop", string payloadJson = "")
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(message)) return null;

            string cleanTitle = (title ?? "通知").Trim();
            string cleanMsg = (message ?? "").Trim();
            string cleanLvl = NormalizeLevel(level);
            string cleanSrc = (source ?? "desktop").Trim().ToLowerInvariant();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_lock)
            {
                EnsureLoaded();

                // 1. 15 秒滑动窗口去重折叠
                if (_memoryRing.Count > 0)
                {
                    var top = _memoryRing[0];
                    if (top.Title == cleanTitle && top.Message == cleanMsg && (nowMs - top.Timestamp) < DEDUP_WINDOW_MS)
                    {
                        top.RepeatCount++;
                        top.Timestamp = nowMs;
                        top.TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        SaveToDisk();
                        return top;
                    }
                }

                // 2. 插入新记录
                var item = new NotificationItem
                {
                    Title = cleanTitle,
                    Message = cleanMsg,
                    Level = cleanLvl,
                    Source = cleanSrc,
                    Timestamp = nowMs,
                    TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    RepeatCount = 1,
                    PayloadJson = payloadJson ?? ""
                };

                _memoryRing.Insert(0, item);
                if (_memoryRing.Count > MAX_CAPACITY)
                {
                    _memoryRing.RemoveRange(MAX_CAPACITY, _memoryRing.Count - MAX_CAPACITY);
                }

                SaveToDisk();
                return item;
            }
        }

        /// <summary>
        /// 多维检索通知历史清单
        /// </summary>
        public static List<NotificationItem> GetHistory(string levelFilter = "", string sourceFilter = "", string keyword = "")
        {
            lock (_lock)
            {
                EnsureLoaded();
                SyncSiYuanPluginHistoryInternal();

                var results = new List<NotificationItem>();
                string cleanKw = (keyword ?? "").Trim().ToLowerInvariant();
                string lvl = (levelFilter ?? "").Trim().ToLowerInvariant();
                string src = (sourceFilter ?? "").Trim().ToLowerInvariant();

                foreach (var it in _memoryRing)
                {
                    if (it == null) continue;

                    // 级别筛选
                    if (!string.IsNullOrEmpty(lvl) && lvl != "all" && it.Level != lvl) continue;
                    // 来源筛选
                    if (!string.IsNullOrEmpty(src) && src != "all" && it.Source != src) continue;

                    // 关键词匹配
                    if (!string.IsNullOrEmpty(cleanKw))
                    {
                        bool match = it.Title.ToLowerInvariant().Contains(cleanKw) ||
                                     it.Message.ToLowerInvariant().Contains(cleanKw) ||
                                     it.Source.ToLowerInvariant().Contains(cleanKw);
                        if (!match) continue;
                    }

                    results.Add(it);
                }

                return results;
            }
        }

        /// <summary>
        /// 格式化为结构化日记 Markdown，支持一键沉淀入今日日记
        /// </summary>
        public static string BuildJournalMarkdown(IEnumerable<NotificationItem> items, string heading = "今日通知与事件流")
        {
            var sb = new StringBuilder();
            string todayStr = DateTime.Now.ToString("yyyy-MM-dd");

            var list = new List<NotificationItem>();
            if (items != null) list.AddRange(items);

            sb.AppendFormat("### 📢 {0} ({1} · 共 {2} 笔记录)\n\n", heading, todayStr, list.Count);

            if (list.Count == 0)
            {
                sb.AppendLine("> 💡 今日暂无未读系统与应用通知。");
            }
            else
            {
                foreach (var it in list)
                {
                    string icon = "🔵";
                    if (it.Level == "error") icon = "🔴";
                    else if (it.Level == "warn") icon = "🟡";
                    else if (it.Level == "success") icon = "🟢";

                    string repeatBadge = it.RepeatCount > 1 ? string.Format(" `(×{0})`", it.RepeatCount) : "";
                    string timeShort = it.TimeFormatted.Length >= 16 ? it.TimeFormatted.Substring(11, 5) : it.TimeFormatted;

                    sb.AppendFormat("- {0} `{1}` **[{2}]** {3}{4}", icon, timeShort, it.Title, it.Message, repeatBadge);
                    if (!string.IsNullOrEmpty(it.Source) && it.Source != "desktop")
                    {
                        sb.AppendFormat(" · _(来自: {0})_", it.Source);
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendFormat("{{: custom-type=\"notification-journal\" custom-date=\"{0}\" custom-count=\"{1}\" }}\n", todayStr, list.Count);
            return sb.ToString();
        }

        /// <summary>
        /// 清空所有历史通知
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _memoryRing = new List<NotificationItem>();
                SaveToDisk();
            }
        }

        /// <summary>
        /// 尝试从思源已装插件 siyuan-notification-history 的 history.json 中读取思源弹窗并合流
        /// </summary>
        private static void SyncSiYuanPluginHistoryInternal()
        {
            try
            {
                string ws = SiYuanClient.DiscoveredWorkspaceDir;
                if (string.IsNullOrEmpty(ws)) return;

                string pluginHistoryPath = Path.Combine(ws, "data", "storage", "petal", "notification-history", "history.json");
                if (File.Exists(pluginHistoryPath))
                {
                    string json = File.ReadAllText(pluginHistoryPath, Encoding.UTF8);
                    var dict = JsonHelper.ParseDict(json);
                    var rawList = JsonHelper.GetList(dict, "notifications");
                    if (rawList != null && rawList.Count > 0)
                    {
                        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var m in _memoryRing) seenIds.Add(m.Id);

                        foreach (var it in rawList)
                        {
                            var d = it as Dictionary<string, object>;
                            if (d != null)
                            {
                                string msg = JsonHelper.GetString(d, "message");
                                if (string.IsNullOrEmpty(msg)) continue;

                                string pId = JsonHelper.GetString(d, "id");
                                if (!string.IsNullOrEmpty(pId) && seenIds.Contains(pId)) continue;

                                bool isError = JsonHelper.GetBool(d, "error", false);
                                long ts = 0;
                                object oTs;
                                if (d.TryGetValue("time", out oTs) && oTs != null) long.TryParse(oTs.ToString(), out ts);
                                if (ts <= 0) ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                var synced = new NotificationItem
                                {
                                    Id = !string.IsNullOrEmpty(pId) ? pId : Guid.NewGuid().ToString("N"),
                                    Title = isError ? "思源异常提示" : "思源弹窗消息",
                                    Message = msg,
                                    Level = isError ? "error" : "info",
                                    Source = "siyuan",
                                    Timestamp = ts,
                                    TimeFormatted = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                    RepeatCount = JsonHelper.GetInt(d, "count", 1)
                                };

                                _memoryRing.Add(synced);
                            }
                        }

                        // 按时间降序重新排序
                        _memoryRing.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                    }
                }
            }
            catch { }
        }

        private static void EnsureLoaded()
        {
            if (_memoryRing != null) return;

            try
            {
                string p = LocalCachePath;
                if (File.Exists(p))
                {
                    string json = File.ReadAllText(p, Encoding.UTF8);
                    _memoryRing = JsonHelper.FromJson<List<NotificationItem>>(json);
                    if (_memoryRing != null) return;
                }
            }
            catch { }

            _memoryRing = new List<NotificationItem>();
        }

        private static void SaveToDisk()
        {
            try
            {
                if (_memoryRing == null) return;
                string json = JsonHelper.ToJson(_memoryRing);
                File.WriteAllText(LocalCachePath, json, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string NormalizeLevel(string level)
        {
            if (string.IsNullOrEmpty(level)) return "info";
            string low = level.Trim().ToLowerInvariant();
            if (low == "error" || low == "err" || low == "danger") return "error";
            if (low == "warn" || low == "warning") return "warn";
            if (low == "success" || low == "ok") return "success";
            return "info";
        }
    }
}
