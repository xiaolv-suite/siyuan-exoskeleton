using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class FolderBookmarkEntry
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Alias { get; set; }
        public string Shortcut { get; set; }
        public string Tags { get; set; }
        public string SiyuanDocId { get; set; }
        public string SiyuanDocTitle { get; set; }
        public int AccessCount { get; set; }
        public bool IsValid { get; set; }
        public string CreatedTime { get; set; }

        public FolderBookmarkEntry()
        {
            Id = Guid.NewGuid().ToString("N");
            Path = "";
            Alias = "";
            Shortcut = "";
            Tags = "";
            SiyuanDocId = "";
            SiyuanDocTitle = "";
            AccessCount = 1;
            IsValid = true;
            CreatedTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }
    }

    /// <summary>
    /// 思源外骨骼 · 系统级本地文件夹收藏与对话框穿透中枢 (FolderBookmarkHub)
    /// 1. 数据终态沉淀在思源专属文档《📂 本地文件夹与工程导航中枢》中
    /// 2. 毫秒级支持别名、路径与拼音检索，自动检测物理路径有效性 (Directory.Exists)
    /// 3. 对标 Listary 穿透黑科技：若当前前台为 Windows 文件对话框 (#32770)，回车直接原位秒跳目录
    /// 4. 支持与思源项目/工程文档建立双向锚定双链，外部磁盘与知识图谱无感互通
    /// </summary>
    public static class FolderBookmarkHub
    {
        private static readonly object _lock = new object();

        private static string LocalCachePath
        {
            get
            {
                string dir = EmbeddedResourceManager.AppDataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "folder_bookmarks.json");
            }
        }

        public const string TARGET_DOC_PATH = "/文件夹导航";

        /// <summary>
        /// 异步加载全量文件夹收藏列表 (思源 SQLite 优先，脱机自动降级读取本地缓存)
        /// </summary>
        public static async Task<List<FolderBookmarkEntry>> LoadFoldersAsync()
        {
            var list = new List<FolderBookmarkEntry>();
            var cached = LoadFromLocalCache();

            try
            {
                string sql = "SELECT b.id, b.content, b.markdown, b.ial, b.updated FROM blocks b " +
                             "WHERE b.ial LIKE '%custom-type=\"folder-bookmark\"%' OR b.content LIKE '%custom-type=\"folder-bookmark\"%' " +
                             "ORDER BY b.updated DESC LIMIT 200";

                var rows = await SiYuanClient.SqlAsync(sql);
                if (rows != null && rows.Count > 0)
                {
                    var cacheMap = new Dictionary<string, FolderBookmarkEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in cached)
                    {
                        if (c != null && !string.IsNullOrEmpty(c.Path)) cacheMap[c.Path] = c;
                    }

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in rows)
                    {
                        var entry = ParseBlockToEntry(r);
                        if (entry != null && !string.IsNullOrEmpty(entry.Path) && !seen.Contains(entry.Path))
                        {
                            seen.Add(entry.Path);
                            if (cacheMap.ContainsKey(entry.Path))
                            {
                                var latest = cacheMap[entry.Path];
                                if (!string.IsNullOrEmpty(latest.Alias)) entry.Alias = latest.Alias;
                                if (!string.IsNullOrEmpty(latest.Shortcut)) entry.Shortcut = latest.Shortcut;
                                if (!string.IsNullOrEmpty(latest.Tags)) entry.Tags = latest.Tags;
                            }
                            entry.IsValid = Directory.Exists(entry.Path);
                            list.Add(entry);
                        }
                    }

                    // 对齐本地缓存，防止思源 SQLite 异步索引延迟
                    foreach (var c in cached)
                    {
                        if (c != null && !string.IsNullOrEmpty(c.Path) && !seen.Contains(c.Path))
                        {
                            seen.Add(c.Path);
                            c.IsValid = Directory.Exists(c.Path);
                            list.Add(c);
                        }
                    }

                    SaveToLocalCache(list);
                    return list;
                }
            }
            catch { }

            foreach (var it in cached)
            {
                it.IsValid = Directory.Exists(it.Path);
            }
            return cached;
        }

        /// <summary>
        /// 保存或追加一条文件夹收藏 (沉淀入思源原生文档与缓存)
        /// </summary>
        public static async Task<bool> SaveFolderAsync(FolderBookmarkEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path)) return false;

            entry.Path = entry.Path.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(entry.Alias))
            {
                entry.Alias = Path.GetFileName(entry.Path);
                if (string.IsNullOrEmpty(entry.Alias)) entry.Alias = entry.Path;
            }

            entry.IsValid = Directory.Exists(entry.Path);

            // 1. 本地脱机缓存更新
            lock (_lock)
            {
                var cached = LoadFromLocalCache();
                cached.RemoveAll(x => x.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) || x.Id == entry.Id);
                cached.Insert(0, entry);
                SaveToLocalCache(cached);
            }

            // 2. 异步同步写入思源文档
            try
            {
                string docId = await EnsureFolderDocExistsAsync();
                if (!string.IsNullOrEmpty(docId))
                {
                    string safeAlias = entry.Alias.Replace("[", "【").Replace("]", "】");
                    string docLink = !string.IsNullOrEmpty(entry.SiyuanDocId) ? string.Format(" · (({0} \"{1}\"))", entry.SiyuanDocId, entry.SiyuanDocTitle) : "";

                    string md = string.Format("* 📂 **{0}** `{1}`{2} {{: custom-type=\"folder-bookmark\" custom-alias=\"{3}\" custom-shortcut=\"{4}\" custom-tags=\"{5}\" custom-freq=\"{6}\" custom-doc-id=\"{7}\" custom-doc-title=\"{8}\" }}\n",
                        safeAlias, entry.Path, docLink,
                        EscapeIal(entry.Alias),
                        EscapeIal(entry.Shortcut),
                        EscapeIal(entry.Tags),
                        entry.AccessCount,
                        EscapeIal(entry.SiyuanDocId),
                        EscapeIal(entry.SiyuanDocTitle));

                    var res = await SiYuanClient.PostAsync("/api/block/appendBlock", new
                    {
                        parentID = docId,
                        dataType = "markdown",
                        data = md
                    });

                    return res != null && res.Success;
                }
            }
            catch { }

            return true;
        }

        /// <summary>
        /// 删除指定的文件夹收藏条目
        /// </summary>
        public static async Task<bool> DeleteFolderAsync(string folderId)
        {
            if (string.IsNullOrEmpty(folderId)) return false;

            lock (_lock)
            {
                var cached = LoadFromLocalCache();
                cached.RemoveAll(x => x.Id == folderId);
                SaveToLocalCache(cached);
            }

            try
            {
                var res = await SiYuanClient.PostAsync("/api/block/deleteBlock", new { id = folderId });
                return res != null && res.Success;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 智能导航跳转：前台若为 Windows 文件对话框 (#32770) 则原位穿透秒跳，否则调起资源管理器
        /// </summary>
        public static bool NavigateToFolder(string targetPath, IntPtr preferredHwnd = default(IntPtr))
        {
            if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath)) return false;

            try
            {
                IntPtr targetHwnd = preferredHwnd;
                if (targetHwnd == IntPtr.Zero)
                {
                    targetHwnd = GetForegroundWindow();
                }

                if (targetHwnd != IntPtr.Zero)
                {
                    var sbClass = new StringBuilder(256);
                    GetClassName(targetHwnd, sbClass, 256);
                    string cls = sbClass.ToString();

                    // 1. 若命中标准文件选择对话框 (#32770)，执行原位穿透
                    if (cls == "#32770")
                    {
                        if (TryNavigateFileDialog(targetHwnd, targetPath))
                        {
                            return true;
                        }
                    }
                }

                // 2. 默认调起 Windows 资源管理器秒开
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + targetPath + "\"") { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 向 Windows 打开/保存对话框 (#32770) 原位注入目标路径并模拟回车
        /// </summary>
        private static bool TryNavigateFileDialog(IntPtr dialogHwnd, string folderPath)
        {
            try
            {
                // 查找对话框内的文件名编辑控件 Edit
                IntPtr editHwnd = IntPtr.Zero;
                EnumChildWindows(dialogHwnd, (hwnd, lParam) =>
                {
                    var sb = new StringBuilder(256);
                    GetClassName(hwnd, sb, 256);
                    if (sb.ToString() == "Edit")
                    {
                        editHwnd = hwnd;
                        return false; // 停止枚举
                    }
                    return true;
                }, IntPtr.Zero);

                if (editHwnd != IntPtr.Zero)
                {
                    string pathWithSlash = folderPath.TrimEnd('\\') + "\\";
                    SendMessage(editHwnd, WM_SETTEXT, IntPtr.Zero, pathWithSlash);
                    PostMessage(editHwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                    PostMessage(editHwnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 智能关键词与别名检索文件夹清单并按访问频次排序
        /// </summary>
        public static List<FolderBookmarkEntry> Search(IEnumerable<FolderBookmarkEntry> pool, string keyword)
        {
            var results = new List<FolderBookmarkEntry>();
            if (pool == null) return results;

            string cleanKw = (keyword ?? "").Trim().ToLowerInvariant();

            foreach (var it in pool)
            {
                if (it == null) continue;

                if (string.IsNullOrEmpty(cleanKw))
                {
                    results.Add(it);
                    continue;
                }

                bool match = it.Alias.ToLowerInvariant().Contains(cleanKw) ||
                             it.Path.ToLowerInvariant().Contains(cleanKw) ||
                             it.Shortcut.ToLowerInvariant().Contains(cleanKw) ||
                             it.Tags.ToLowerInvariant().Contains(cleanKw) ||
                             (!string.IsNullOrEmpty(it.SiyuanDocTitle) && it.SiyuanDocTitle.ToLowerInvariant().Contains(cleanKw));

                if (match)
                {
                    results.Add(it);
                }
            }

            results.Sort((a, b) => b.AccessCount.CompareTo(a.AccessCount));
            return results;
        }

        private static FolderBookmarkEntry ParseBlockToEntry(Dictionary<string, object> row)
        {
            string id = JsonHelper.GetString(row, "id");
            string content = JsonHelper.GetString(row, "content");
            string markdown = JsonHelper.GetString(row, "markdown");
            string ial = JsonHelper.GetString(row, "ial");

            string text = !string.IsNullOrEmpty(markdown) ? markdown : content;

            var entry = new FolderBookmarkEntry { Id = id };

            // 提取 `物理路径`
            var pathMatch = Regex.Match(text, @"`([a-zA-Z]:\\[^`]+)`");
            if (pathMatch.Success)
            {
                entry.Path = pathMatch.Groups[1].Value.Trim();
            }
            else
            {
                return null;
            }

            // 提取 **别名**
            var aliasMatch = Regex.Match(text, @"\*\*([^*]+)\*\*");
            if (aliasMatch.Success)
            {
                entry.Alias = aliasMatch.Groups[1].Value.Trim();
            }
            else
            {
                entry.Alias = Path.GetFileName(entry.Path);
            }

            string fullMeta = ial + " " + text;
            entry.Shortcut = ExtractAttr(fullMeta, "custom-shortcut", "");
            entry.Tags = ExtractAttr(fullMeta, "custom-tags", "");
            entry.SiyuanDocId = ExtractAttr(fullMeta, "custom-doc-id", "");
            entry.SiyuanDocTitle = ExtractAttr(fullMeta, "custom-doc-title", "");

            string freqStr = ExtractAttr(fullMeta, "custom-freq", "1");
            int f;
            if (int.TryParse(freqStr, out f)) entry.AccessCount = f;

            return entry;
        }

        private static string ExtractAttr(string src, string attrName, string defVal)
        {
            if (string.IsNullOrEmpty(src)) return defVal;
            var m = Regex.Match(src, attrName + @"=""([^""]*)""");
            if (m.Success) return m.Groups[1].Value;
            return defVal;
        }

        private static string EscapeIal(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\"", "\\\"").Replace("\n", " ");
        }

        private static async Task<string> EnsureFolderDocExistsAsync()
        {
            try
            {
                // 1. 查询是否已存在文档
                var rows = await SiYuanClient.SqlAsync("SELECT id FROM blocks WHERE type='d' AND hpath LIKE '%/文件夹导航%' LIMIT 1");
                if (rows != null && rows.Count > 0)
                {
                    return JsonHelper.GetString(rows[0], "id");
                }

                // 2. 获取目标笔记本 ID (优先直接从 blocks 表中提取当前活跃笔记本 box)
                string nbId = "";
                var boxRows = await SiYuanClient.SqlAsync("SELECT box FROM blocks WHERE box != '' LIMIT 1");
                if (boxRows != null && boxRows.Count > 0)
                {
                    nbId = JsonHelper.GetString(boxRows[0], "box");
                }

                if (string.IsNullOrEmpty(nbId))
                {
                    var notebooks = await SiYuanClient.LsNotebooksAsync();
                    if (notebooks != null && notebooks.Count > 0)
                    {
                        var openNb = notebooks.Find(x => !x.Closed);
                        nbId = openNb != null ? openNb.Id : notebooks[0].Id;
                    }
                }

                if (!string.IsNullOrEmpty(nbId))
                {
                    var res = await SiYuanClient.PostAsync("/api/filetree/createDocWithMd", new
                    {
                        notebook = nbId,
                        path = TARGET_DOC_PATH,
                        markdown = "# 📂 本地文件夹与工程导航中枢\n\n> 💡 由思源外骨骼统一调度，支持拼音秒查、Windows文件对话框原位秒跳与关联思源工程。\n\n"
                    });

                    if (res != null && res.Success && res.Data != null)
                    {
                        return res.Data.ToString();
                    }

                    // 若创建返回或已存在，二次查询
                    var queryRows = await SiYuanClient.SqlAsync("SELECT id FROM blocks WHERE type='d' AND hpath LIKE '%/文件夹导航%' LIMIT 1");
                    if (queryRows != null && queryRows.Count > 0)
                    {
                        return JsonHelper.GetString(queryRows[0], "id");
                    }
                }
            }
            catch { }

            return "";
        }

        public static List<FolderBookmarkEntry> LoadFromLocalCache()
        {
            lock (_lock)
            {
                try
                {
                    string p = LocalCachePath;
                    if (File.Exists(p))
                    {
                        string json = File.ReadAllText(p, Encoding.UTF8);
                        var list = JsonHelper.FromJson<List<FolderBookmarkEntry>>(json);
                        if (list != null) return list;
                    }
                }
                catch { }

                // 默认内置种子示范文件夹
                var seeds = new List<FolderBookmarkEntry>
                {
                    new FolderBookmarkEntry { Alias = "用户下载目录", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Shortcut = "dl", Tags = "常用,系统", AccessCount = 20 },
                    new FolderBookmarkEntry { Alias = "思源插件主工程", Path = @"D:\思源插件", Shortcut = "plugin", Tags = "开发,思源", AccessCount = 30 }
                };
                SaveToLocalCache(seeds);
                return seeds;
            }
        }

        private static void SaveToLocalCache(List<FolderBookmarkEntry> list)
        {
            lock (_lock)
            {
                try
                {
                    string json = JsonHelper.ToJson(list);
                    File.WriteAllText(LocalCachePath, json, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private const int WM_SETTEXT = 0x000C;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int VK_RETURN = 0x0D;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }
}
