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
        public string TargetDocId { get; set; }
        public string TargetDocTitle { get; set; }
        public string SourceWindow { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public static class OfflineQueueManager
    {
        private static readonly object _lock = new object();
        private static readonly object _flushLock = new object();

        private static string QueueFilePath
        {
            get
            {
                return Path.Combine(EmbeddedResourceManager.AppDataDir, "offline_queue.json");
            }
        }

        public static bool EnqueueNote(string text, string sourceWindow)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var item = new QueuedNoteItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "note",
                Content = text.Trim(),
                SourceWindow = sourceWindow ?? "",
                CreatedAt = DateTime.Now
            };
            return AppendToQueue(item);
        }

        public static bool EnqueueFile(string filePath)
        {
            return EnqueueFile(filePath, "", "");
        }

        public static bool EnqueueFile(string filePath, string targetDocId, string targetDocTitle)
        {
            if (string.IsNullOrEmpty(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath))) return false;
            var paths = new List<string>();
            if (File.Exists(filePath))
            {
                paths.Add(filePath);
            }
            else
            {
                try
                {
                    paths.AddRange(Directory.GetFiles(filePath, "*.*", SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex)
                {
                    Logger.Error("Offline queue could not enumerate directory " + filePath, ex);
                    return false;
                }
            }

            return AppendFilesToQueue(paths, DateTime.Now, "", targetDocId, targetDocTitle);
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
                    Logger.Error("Offline queue count could not be read. The queue file was left untouched.");
                    return -1;
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

        public static bool ClearQueue()
        {
            lock (_flushLock)
            lock (_lock)
            {
                try
                {
                    WriteAllItems(new List<QueuedNoteItem>());
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error("Offline queue clear failed; queue was left untouched", ex);
                    return false;
                }
            }
        }

        private static bool AppendToQueue(QueuedNoteItem item)
        {
            return AppendToQueue(new List<QueuedNoteItem> { item });
        }

        private static bool AppendFilesToQueue(IEnumerable<string> paths, DateTime createdAt, string sourceWindow, string targetDocId, string targetDocTitle)
        {
            var items = new List<QueuedNoteItem>();
            foreach (var path in paths)
            {
                items.Add(new QueuedNoteItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = "file",
                    FilePath = path,
                    Content = Path.GetFileName(path),
                    SourceWindow = sourceWindow ?? "",
                    TargetDocId = targetDocId ?? "",
                    TargetDocTitle = targetDocTitle ?? "",
                    CreatedAt = createdAt
                });
            }
            return items.Count == 0 || AppendToQueue(items);
        }

        private static bool AppendToQueue(List<QueuedNoteItem> items)
        {
            lock (_lock)
            {
                try
                {
                    var list = ReadAllItems();
                    list.AddRange(items);
                    WriteAllItems(list);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error("Offline queue append failed", ex);
                    return false;
                }
            }
        }

        public static bool FlushQueue(string syUrl, string token, Action<string, string> notify)
        {
            lock (_flushLock)
            {
                return FlushQueueInternal(syUrl, token, notify);
            }
        }

        private static bool FlushQueueInternal(string syUrl, string token, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(syUrl)) return false;

            List<QueuedNoteItem> itemsToFlush;
            lock (_lock)
            {
                itemsToFlush = ReadAllItems();
                if (itemsToFlush.Count == 0) return false;
            }

            int successCount = 0;
            int droppedMissingFiles = 0;
            var succeededIds = new HashSet<string>(StringComparer.Ordinal);
            string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);

            foreach (var item in itemsToFlush)
            {
                bool ok = false;
                bool missingFile = false;
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
                    if (File.Exists(item.FilePath) || Directory.Exists(item.FilePath))
                    {
                        ok = string.IsNullOrEmpty(item.TargetDocId)
                            ? FileDispatcher.DispatchFileToSiYuan(item.FilePath, null)
                            : FileDispatcher.DispatchFileToDocument(item.FilePath, item.TargetDocId, item.TargetDocTitle, null);
                    }
                    else
                    {
                        ok = true;
                        missingFile = true;
                    }
                }

                if (ok)
                {
                    if (missingFile) droppedMissingFiles++;
                    else successCount++;
                    if (!string.IsNullOrEmpty(item.Id)) succeededIds.Add(item.Id);
                }
            }

            // 可靠性加固：离线补录成功后立即强制提交思源内核待写事务，
            // 避免内核在自动提交窗口内退出导致已补录数据丢失（官方 /api/sqlite/flushTransaction）
            if (successCount > 0)
            {
                try
                {
                    SyKernelRequest.DoRequest(syUrl, token, "/api/sqlite/flushTransaction", "{}");
                }
                catch { }
            }

            bool queueCommitted = false;
            int remainingCount = 0;
            lock (_lock)
            {
                try
                {
                    var currentItems = ReadAllItems();
                    var remaining = new List<QueuedNoteItem>();
                    foreach (var currentItem in currentItems)
                    {
                        if (string.IsNullOrEmpty(currentItem.Id) || !succeededIds.Contains(currentItem.Id))
                        {
                            remaining.Add(currentItem);
                        }
                    }
                    WriteAllItems(remaining);
                    remainingCount = remaining.Count;
                    queueCommitted = true;
                }
                catch (Exception ex)
                {
                    Logger.Error("Offline queue flush could not commit delivered items", ex);
                }
            }

            if ((successCount > 0 || droppedMissingFiles > 0) && notify != null)
            {
                if (queueCommitted)
                {
                    string message = "✓ 思源已恢复连接，已自动补录 " + successCount + " 条离线记录；队列剩余 " + remainingCount + " 条。";
                    if (droppedMissingFiles > 0) message += " 已移除 " + droppedMissingFiles + " 条原文件已删除的队列记录。";
                    notify("思源外骨骼 · 离岸同步", message);
                }
                else
                {
                    notify("思源外骨骼 · 离岸同步", "⚠️ 已发送 " + successCount + " 条离线记录，但无法确认本地队列是否已更新；若仍保留，下次重试可能产生重复，请检查磁盘空间或权限。");
                }
            }

            return successCount > 0 || droppedMissingFiles > 0;
        }

        private static List<QueuedNoteItem> ReadAllItems()
        {
            var list = new List<QueuedNoteItem>();
            if (!File.Exists(QueueFilePath)) return list;

            var stored = File.ReadAllText(QueueFilePath, Encoding.UTF8).TrimStart('\uFEFF');
            bool migrateLegacy = !stored.StartsWith("DPAPI:", StringComparison.Ordinal);
            string json = migrateLegacy ? stored : SiYuanProfileHub.UnprotectSecretDpapi(stored);
            if (!migrateLegacy && string.IsNullOrEmpty(json))
            {
                throw new InvalidDataException("Offline queue cannot be decrypted for the current Windows user.");
            }

            if (!string.IsNullOrWhiteSpace(json))
            {
                var items = JsonHelper.FromJson<List<QueuedNoteItem>>(json);
                if (items == null) throw new InvalidDataException("Offline queue JSON is invalid.");
                list = items;
            }

            bool itemsChanged = false;
            var normalized = new List<QueuedNoteItem>();
            foreach (var item in list)
            {
                if (item == null) continue;
                if (string.IsNullOrEmpty(item.Id))
                {
                    item.Id = Guid.NewGuid().ToString("N");
                    itemsChanged = true;
                }

                if (string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase) && Directory.Exists(item.FilePath))
                {
                    try
                    {
                        var files = Directory.GetFiles(item.FilePath, "*.*", SearchOption.TopDirectoryOnly);
                        foreach (var file in files)
                        {
                            normalized.Add(new QueuedNoteItem
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Type = "file",
                                FilePath = file,
                                Content = Path.GetFileName(file),
                                SourceWindow = item.SourceWindow,
                                TargetDocId = item.TargetDocId,
                                TargetDocTitle = item.TargetDocTitle,
                                CreatedAt = item.CreatedAt
                            });
                        }
                        itemsChanged = true;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Offline queue could not split legacy directory item " + item.FilePath, ex);
                    }
                }
                normalized.Add(item);
            }

            if (migrateLegacy || itemsChanged) WriteAllItems(normalized);
            return normalized;
        }

        private static void WriteAllItems(List<QueuedNoteItem> list)
        {
            var json = JsonHelper.ToJson(list);
            var encrypted = SiYuanProfileHub.ProtectSecretDpapi(json);
            if (string.IsNullOrEmpty(encrypted) || !encrypted.StartsWith("DPAPI:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Windows DPAPI could not protect the offline queue.");
            }

            var queuePath = QueueFilePath;
            var queueDir = Path.GetDirectoryName(queuePath);
            if (!Directory.Exists(queueDir)) Directory.CreateDirectory(queueDir);
            var tempPath = queuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, encrypted, new UTF8Encoding(false));
                if (File.Exists(queuePath)) File.Replace(tempPath, queuePath, null);
                else File.Move(tempPath, queuePath);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static string ExtractField(string json, string field, string def)
        {
            var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success) return m.Groups[1].Value;
            return def;
        }
    }
}
