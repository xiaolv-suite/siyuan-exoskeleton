using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class QueuedNoteItem
    {
        public string Id { get; set; }
        public string Type { get; set; } // "note" or "file"
        public string Content { get; set; }
        public string FilePath { get; set; }
        public string SourceWindow { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public static class OfflineQueueManager
    {
        private static readonly object _lock = new object();

        private static string QueueFilePath
        {
            get
            {
                return Path.Combine(EmbeddedResourceManager.AppDataDir, "offline_queue.json");
            }
        }

        public static void EnqueueNote(string text, string sourceWindow)
        {
            if (string.IsNullOrEmpty(text)) return;
            Logger.Info("OfflineQueueManager.EnqueueNote: " + text);
            var item = new QueuedNoteItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "note",
                Content = text.Trim(),
                SourceWindow = sourceWindow ?? "",
                CreatedAt = DateTime.Now
            };
            AppendToQueue(item);
        }

        public static void EnqueueFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            var item = new QueuedNoteItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "file",
                FilePath = filePath,
                Content = Path.GetFileName(filePath),
                CreatedAt = DateTime.Now
            };
            AppendToQueue(item);
        }

        public static int GetQueueCount()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(QueueFilePath)) return 0;
                    var list = ReadAllItems();
                    return list.Count;
                }
                catch
                {
                    return 0;
                }
            }
        }

        public static List<QueuedNoteItem> GetQueueItems()
        {
            lock (_lock)
            {
                return ReadAllItems();
            }
        }

        public static void ClearQueue()
        {
            lock (_lock)
            {
                try
                {
                    WriteAllItems(new List<QueuedNoteItem>());
                }
                catch { }
            }
        }

        private static void AppendToQueue(QueuedNoteItem item)
        {
            lock (_lock)
            {
                try
                {
                    var list = ReadAllItems();
                    list.Add(item);
                    WriteAllItems(list);
                }
                catch { }
            }
        }

        public static bool FlushQueue(string syUrl, string token, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(syUrl)) return false;

            List<QueuedNoteItem> itemsToFlush;
            lock (_lock)
            {
                itemsToFlush = ReadAllItems();
                if (itemsToFlush.Count == 0) return false;
            }

            int successCount = 0;
            var remaining = new List<QueuedNoteItem>();
            string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);

            foreach (var item in itemsToFlush)
            {
                bool ok = false;
                if (item.Type == "note")
                {
                    string dateStr = (item.CreatedAt.Date == DateTime.Today)
                        ? item.CreatedAt.ToString("HH:mm")
                        : item.CreatedAt.ToString("MM-dd HH:mm");

                    string md;
                    if (item.Content.StartsWith("- [ ]") || item.Content.StartsWith("* [ ]"))
                    {
                        md = "- [ ] " + dateStr + " 📦 *(离线补录)* " + item.Content.Substring(5).Trim();
                    }
                    else if (item.Content.StartsWith("- [x]") || item.Content.StartsWith("* [x]"))
                    {
                        md = "- [x] " + dateStr + " 📦 *(离线补录)* " + item.Content.Substring(5).Trim();
                    }
                    else if (item.Content.StartsWith("!") || item.Content.StartsWith("！"))
                    {
                        md = "- [ ] " + dateStr + " 📦 *(离线补录)* " + item.Content.TrimStart('!', '！', ' ');
                    }
                    else
                    {
                        md = "- " + dateStr + " 📦 *(离线补录)* " + item.Content;
                    }

                    string postJson = "{\"notebook\":\"" + diaryNb + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}";
                    var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postJson);
                    ok = r.Success;
                }
                else if (item.Type == "file")
                {
                    if (File.Exists(item.FilePath))
                    {
                        FileDispatcher.DispatchFileToSiYuan(item.FilePath, null);
                        ok = true;
                    }
                    else
                    {
                        ok = true; // 原始文件若已删除则丢弃
                    }
                }

                if (ok)
                {
                    successCount++;
                }
                else
                {
                    remaining.Add(item);
                }
            }

            lock (_lock)
            {
                try
                {
                    WriteAllItems(remaining);
                }
                catch { }
            }

            if (successCount > 0 && notify != null)
            {
                notify("思源外骨骼 · 离岸同步", "✓ 思源已恢复连接，已自动为您补录 " + successCount + " 条离线速记至今日日记！");
            }

            return successCount > 0;
        }

        private static List<QueuedNoteItem> ReadAllItems()
        {
            var list = new List<QueuedNoteItem>();
            try
            {
                if (!File.Exists(QueueFilePath)) return list;
                var json = File.ReadAllText(QueueFilePath, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) return list;

                var items = JsonHelper.FromJson<List<QueuedNoteItem>>(json);
                if (items != null) return items;
            }
            catch { }
            return list;
        }

        private static void WriteAllItems(List<QueuedNoteItem> list)
        {
            try
            {
                var json = JsonHelper.ToJson(list);
                File.WriteAllText(QueueFilePath, json, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string ExtractField(string json, string field, string def)
        {
            var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success) return m.Groups[1].Value;
            return def;
        }
    }
}
