using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class RescueItem
    {
        public string SnapshotDirName { get; set; }
        public string SnapshotTime { get; set; }
        public string SnapshotType { get; set; } // "编辑快照", "云同步快照"
        public string DocId { get; set; }
        public string DocTitle { get; set; }
        public string FilePath { get; set; }
        public string MatchedSnippet { get; set; }
        public string FullRecoveredMarkdown { get; set; }
    }

    /// <summary>
    /// 思源外骨骼 · 误删打捞与时光机安全黑匣子 (SiYuan Note Rescue & Time Machine)
    /// 解决社区高频痛点：当笔记被 AI 智能体/MCP/外部误操作删除或覆盖时，
    /// 毫秒级穿透扫描底层 history 快照库中的全部历史 .sy 文件，提取原文并支持 1 键安全打捞复原
    /// </summary>
    public static class HistoryRescueManager
    {
        public static List<RescueItem> SearchHistory(string keyword, int maxFolders = 30)
        {
            var results = new List<RescueItem>();

            string ws = SiYuanClient.DiscoveredWorkspaceDir;
            string historyDir = Path.Combine(ws, "history");
            if (!Directory.Exists(historyDir)) return results;

            // 若无搜索关键词，直接展示回收站最近删除与修改的 25 条快照文档
            if (string.IsNullOrEmpty(keyword) || keyword.Trim().Length == 0)
            {
                return GetRecentHistory(25);
            }

            try
            {
                var di = new DirectoryInfo(historyDir);
                var dirs = di.GetDirectories();
                Array.Sort(dirs, (a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase));

                int checkedFolders = 0;
                string kwLower = keyword.Trim().ToLowerInvariant();

                foreach (var folder in dirs)
                {
                    if (checkedFolders >= maxFolders || results.Count >= 25) break;
                    checkedFolders++;

                    var syFiles = folder.GetFiles("*.sy", SearchOption.AllDirectories);
                    foreach (var file in syFiles)
                    {
                        try
                        {
                            // 快速过滤大于 10MB 的超大快照文件，保护 LOH 堆内存
                            if (file.Length > 10 * 1024 * 1024) continue;

                            string text = File.ReadAllText(file.FullName, Encoding.UTF8);
                            if (text.ToLowerInvariant().Contains(kwLower))
                            {
                                var item = ParseSyHistoryFile(file.FullName, folder.Name, text, keyword.Trim());
                                if (item != null)
                                {
                                    results.Add(item);
                                    if (results.Count >= 25) break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 搜索完成后排期深度内存垃圾回收
            MemoryOptimizer.ScheduleTrim(800);
            return results;
        }

        public static List<RescueItem> GetRecentHistory(int maxItems = 25)
        {
            var results = new List<RescueItem>();
            string ws = SiYuanClient.DiscoveredWorkspaceDir;
            string historyDir = Path.Combine(ws, "history");
            if (!Directory.Exists(historyDir)) return results;

            try
            {
                var di = new DirectoryInfo(historyDir);
                var dirs = di.GetDirectories();
                Array.Sort(dirs, (a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase));

                var seenDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var folder in dirs)
                {
                    if (results.Count >= maxItems) break;

                    var syFiles = folder.GetFiles("*.sy", SearchOption.AllDirectories);
                    foreach (var file in syFiles)
                    {
                        try
                        {
                            if (file.Length > 10 * 1024 * 1024) continue;
                            string text = File.ReadAllText(file.FullName, Encoding.UTF8);
                            var item = ParseSyHistoryFile(file.FullName, folder.Name, text, "");
                            if (item != null && !seenDocs.Contains(item.DocId))
                            {
                                seenDocs.Add(item.DocId);
                                results.Add(item);
                                if (results.Count >= maxItems) break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            MemoryOptimizer.ScheduleTrim(800);
            return results;
        }

        public static bool RestoreToSiYuan(string docTitle, string markdown, string snapTime, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(markdown)) return false;

            try
            {
                string syUrl = "";
                string token = "";
                SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
                if (string.IsNullOrEmpty(syUrl)) return false;

                string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);
                string targetDocId = ContextPenMain.GetOrCreateDailyNote(syUrl, token, diaryNb, DateTime.Now.ToString("yyyyMMdd"), DateTime.Now.ToString("yyyy-MM-dd"));

                if (string.IsNullOrEmpty(targetDocId)) return false;

                string banner = string.Format("### 🕒 时光机打捞恢复: 《{0}》 (来自快照 {1})\n\n{2}", docTitle, snapTime, markdown);
                var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", JsonHelper.ToJson(new
                {
                    parentID = targetDocId,
                    dataType = "markdown",
                    data = banner
                }));

                if (r.Success)
                {
                    SoundPlayerHelper.PlayFeedback();
                    if (notify != null) notify("思源外骨骼 · 时光机", "✓ 成功打捞恢复【" + docTitle + "】至今日日记！");
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static RescueItem ParseSyHistoryFile(string filePath, string folderName, string rawJson, string keyword)
        {
            try
            {
                var dict = JsonHelper.ParseDict(rawJson);
                if (dict == null) return null;

                string docId = JsonHelper.GetString(dict, "ID");
                var props = JsonHelper.GetDict(dict, "Properties");
                string title = props != null ? JsonHelper.GetString(props, "title") : docId;
                if (string.IsNullOrEmpty(title)) title = docId;

                string snapTime = folderName;
                string snapType = "自动快照";
                var m = Regex.Match(folderName, @"^(\d{4}-\d{2}-\d{2})-(\d{2})(\d{2})(\d{2})-([a-zA-Z]+)");
                if (m.Success)
                {
                    snapTime = m.Groups[1].Value + " " + m.Groups[2].Value + ":" + m.Groups[3].Value;
                    string t = m.Groups[5].Value.ToLowerInvariant();
                    if (t == "update") snapType = "编辑快照";
                    else if (t == "sync") snapType = "云同步快照";
                }

                var sb = new StringBuilder();
                string matchedSnippet = "";
                int previewBlockCount = 0;
                var children = JsonHelper.GetList(dict, "Children");
                if (children != null)
                {
                    foreach (var c in children)
                    {
                        var cDict = c as Dictionary<string, object>;
                        if (cDict == null) continue;
                        var subChildren = JsonHelper.GetList(cDict, "Children");
                        if (subChildren != null)
                        {
                            foreach (var sc in subChildren)
                            {
                                var scDict = sc as Dictionary<string, object>;
                                if (scDict != null)
                                {
                                    string data = JsonHelper.GetString(scDict, "Data");
                                    if (!string.IsNullOrEmpty(data))
                                    {
                                        if (previewBlockCount < 160)
                                        {
                                            sb.AppendLine(data);
                                            previewBlockCount++;
                                        }
                                        if (string.IsNullOrEmpty(matchedSnippet) && data.ToLowerInvariant().Contains(keyword.ToLowerInvariant()))
                                        {
                                            matchedSnippet = data.Trim();
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(matchedSnippet))
                {
                    int idx = rawJson.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int start = Math.Max(0, idx - 40);
                        int len = Math.Min(rawJson.Length - start, 120);
                        matchedSnippet = rawJson.Substring(start, len).Replace("\"", " ");
                    }
                }

                return new RescueItem
                {
                    SnapshotDirName = folderName,
                    SnapshotTime = snapTime,
                    SnapshotType = snapType,
                    DocId = docId,
                    DocTitle = title,
                    FilePath = filePath,
                    MatchedSnippet = matchedSnippet,
                    FullRecoveredMarkdown = sb.ToString().Trim()
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
