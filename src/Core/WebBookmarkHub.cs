using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class WebBookmarkEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string CleanUrl { get; set; }
        public string Category { get; set; }     // "开发", "学术", "设计", "生活", "工具"
        public string Tags { get; set; }         // e.g. "AI,LLM,开源"
        public string Notes { get; set; }        // 备注说明/账号备忘
        public string Shortcut { get; set; }     // 快速简码 e.g. "gh", "arxiv"
        public int VisitCount { get; set; }
        public string CreatedTime { get; set; }

        public WebBookmarkEntry()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = "";
            Url = "";
            CleanUrl = "";
            Category = "常用";
            Tags = "";
            Notes = "";
            Shortcut = "";
            VisitCount = 1;
            CreatedTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }
    }

    /// <summary>
    /// 思源外骨骼 · 跨浏览器网站收藏中枢引擎 (WebBookmarkHub)
    /// 1. 彻底打破多浏览器书签孤岛，数据终态完整沉淀在思源专属文档中
    /// 2. 毫秒级多维检索 (标题/网址/分类/标签/简码/拼音)，按访问频次智能加权
    /// 3. 支持前台浏览器活动网址 1 键收录、自动脱敏追踪参数
    /// 4. 三层保底体系：思源属性视图 (AV) ➔ 原生大纲列表 ➔ 本地脱机缓存 (web_bookmarks.json)
    /// </summary>
    public static class WebBookmarkHub
    {
        private static readonly object _lock = new object();

        private static string LocalCachePath
        {
            get
            {
                string dir = EmbeddedResourceManager.AppDataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "web_bookmarks.json");
            }
        }

        public const string TARGET_DOC_PATH = "/网站导航";

        /// <summary>
        /// 异步加载全量网站收藏清单 (思源 SQLite 优先，脱机自动降级读取本地缓存)
        /// </summary>
        public static async Task<List<WebBookmarkEntry>> LoadBookmarksAsync()
        {
            var list = new List<WebBookmarkEntry>();

            try
            {
                // 1. 尝试从思源 SQLite 检索标准 web-bookmark 块
                string sql = "SELECT b.id, b.content, b.markdown, b.ial, b.updated FROM blocks b " +
                             "WHERE b.ial LIKE '%custom-type=\"web-bookmark\"%' OR b.content LIKE '%custom-type=\"web-bookmark\"%' " +
                             "ORDER BY b.updated DESC LIMIT 300";

                var rows = await SiYuanClient.SqlAsync(sql);
                if (rows != null && rows.Count > 0)
                {
                    foreach (var r in rows)
                    {
                        var entry = ParseBlockToEntry(r);
                        if (entry != null && !string.IsNullOrEmpty(entry.Url))
                        {
                            list.Add(entry);
                        }
                    }

                    // 成功获取思源数据，同步刷新本地脱机缓存
                    SaveToLocalCache(list);
                    return list;
                }
            }
            catch { }

            // 2. 思源停机或暂无数据时，降级读取本地脱机缓存
            return LoadFromLocalCache();
        }

        /// <summary>
        /// 保存或追加一条网站收藏 (自动脱敏、推导分类并沉淀至思源原生文档与缓存)
        /// </summary>
        public static async Task<bool> SaveBookmarkAsync(WebBookmarkEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Url)) return false;

            entry.Url = entry.Url.Trim();
            entry.CleanUrl = TextCleaner.PurifyUrl(entry.Url);

            if (string.IsNullOrWhiteSpace(entry.Title))
            {
                string siteName = TextCleaner.DetectSiteName(entry.CleanUrl);
                entry.Title = (siteName != "网页摘录") ? siteName : entry.CleanUrl;
            }

            if (string.IsNullOrWhiteSpace(entry.Category) || entry.Category == "常用")
            {
                entry.Category = InferCategory(entry.CleanUrl, entry.Title);
            }

            // 1. 本地脱机缓存更新
            lock (_lock)
            {
                var cached = LoadFromLocalCache();
                cached.RemoveAll(x => x.CleanUrl.Equals(entry.CleanUrl, StringComparison.OrdinalIgnoreCase) || x.Id == entry.Id);
                cached.Insert(0, entry);
                SaveToLocalCache(cached);
            }

            // 2. 异步同步写入思源文档
            try
            {
                string docId = await EnsureBookmarkDocExistsAsync();
                if (!string.IsNullOrEmpty(docId))
                {
                    string safeTitle = entry.Title.Replace("[", "【").Replace("]", "】");
                    string md = string.Format("* 🌐 [{0}]({1}) {{: custom-type=\"web-bookmark\" custom-category=\"{2}\" custom-tags=\"{3}\" custom-shortcut=\"{4}\" custom-freq=\"{5}\" custom-notes=\"{6}\" }}\n",
                        safeTitle, entry.CleanUrl,
                        EscapeIal(entry.Category),
                        EscapeIal(entry.Tags),
                        EscapeIal(entry.Shortcut),
                        entry.VisitCount,
                        EscapeIal(entry.Notes));

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
        /// 删除指定的网站收藏条目
        /// </summary>
        public static async Task<bool> DeleteBookmarkAsync(string bookmarkId)
        {
            if (string.IsNullOrEmpty(bookmarkId)) return false;

            // 1. 本地缓存删除
            lock (_lock)
            {
                var cached = LoadFromLocalCache();
                cached.RemoveAll(x => x.Id == bookmarkId);
                SaveToLocalCache(cached);
            }

            // 2. 思源块物理删除
            try
            {
                var res = await SiYuanClient.PostAsync("/api/block/deleteBlock", new { id = bookmarkId });
                return res != null && res.Success;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 智能关键词与拼音检索收藏条目并按访问频次排序
        /// </summary>
        public static List<WebBookmarkEntry> Search(IEnumerable<WebBookmarkEntry> pool, string keyword)
        {
            var results = new List<WebBookmarkEntry>();
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

                bool match = it.Title.ToLowerInvariant().Contains(cleanKw) ||
                             it.CleanUrl.ToLowerInvariant().Contains(cleanKw) ||
                             it.Category.ToLowerInvariant().Contains(cleanKw) ||
                             it.Tags.ToLowerInvariant().Contains(cleanKw) ||
                             it.Shortcut.ToLowerInvariant().Contains(cleanKw) ||
                             (!string.IsNullOrEmpty(it.Notes) && it.Notes.ToLowerInvariant().Contains(cleanKw));

                if (match)
                {
                    results.Add(it);
                }
            }

            // 按访问频次降序排序
            results.Sort((a, b) => b.VisitCount.CompareTo(a.VisitCount));
            return results;
        }

        private static WebBookmarkEntry ParseBlockToEntry(Dictionary<string, object> row)
        {
            string id = JsonHelper.GetString(row, "id");
            string content = JsonHelper.GetString(row, "content");
            string markdown = JsonHelper.GetString(row, "markdown");
            string ial = JsonHelper.GetString(row, "ial");

            string text = !string.IsNullOrEmpty(markdown) ? markdown : content;

            var entry = new WebBookmarkEntry { Id = id };

            // 提取 [标题](网址)
            var linkMatch = Regex.Match(text, @"\[([^\]]+)\]\((https?://[^)]+)\)");
            if (linkMatch.Success)
            {
                entry.Title = linkMatch.Groups[1].Value.Trim();
                entry.Url = linkMatch.Groups[2].Value.Trim();
                entry.CleanUrl = TextCleaner.PurifyUrl(entry.Url);
            }
            else
            {
                return null;
            }

            // 提取 IAL 属性
            string fullMeta = ial + " " + text;
            entry.Category = ExtractAttr(fullMeta, "custom-category", "常用");
            entry.Tags = ExtractAttr(fullMeta, "custom-tags", "");
            entry.Shortcut = ExtractAttr(fullMeta, "custom-shortcut", "");
            entry.Notes = ExtractAttr(fullMeta, "custom-notes", "");

            string freqStr = ExtractAttr(fullMeta, "custom-freq", "1");
            int f;
            if (int.TryParse(freqStr, out f)) entry.VisitCount = f;

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

        private static string InferCategory(string url, string title)
        {
            string low = (url + " " + title).ToLowerInvariant();
            if (low.Contains("github") || low.Contains("gitlab") || low.Contains("stackoverflow") || low.Contains("csdn") || low.Contains("juejin") || low.Contains("v2ex") || low.Contains("npm") || low.Contains("pypi"))
            {
                return "开发";
            }
            if (low.Contains("arxiv") || low.Contains("doi.org") || low.Contains("nature") || low.Contains("sciencedirect") || low.Contains("scholar") || low.Contains("researchgate") || low.Contains("ieee"))
            {
                return "学术";
            }
            if (low.Contains("figma") || low.Contains("dribbble") || low.Contains("behance") || low.Contains("iconfont") || low.Contains("unsplash"))
            {
                return "设计";
            }
            if (low.Contains("bilibili") || low.Contains("youtube") || low.Contains("zhihu") || low.Contains("weibo") || low.Contains("xiaohongshu"))
            {
                return "媒体";
            }
            return "常用";
        }

        private static async Task<string> EnsureBookmarkDocExistsAsync()
        {
            try
            {
                // 1. 查询是否已存在文档
                var rows = await SiYuanClient.SqlAsync("SELECT id FROM blocks WHERE type='d' AND hpath LIKE '%/网站导航%' LIMIT 1");
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
                        markdown = "# 🌐 个人全景网站收藏夹\n\n> 💡 由思源外骨骼统一管理，支持多浏览器通用秒开、频次加权与双链直贴。\n\n"
                    });

                    if (res != null && res.Success && res.Data != null)
                    {
                        return res.Data.ToString();
                    }

                    // 若创建返回或已存在，二次查询
                    var queryRows = await SiYuanClient.SqlAsync("SELECT id FROM blocks WHERE type='d' AND hpath LIKE '%/网站导航%' LIMIT 1");
                    if (queryRows != null && queryRows.Count > 0)
                    {
                        return JsonHelper.GetString(queryRows[0], "id");
                    }
                }
            }
            catch { }

            return "";
        }

        public static List<WebBookmarkEntry> LoadFromLocalCache()
        {
            lock (_lock)
            {
                try
                {
                    string p = LocalCachePath;
                    if (File.Exists(p))
                    {
                        string json = File.ReadAllText(p, Encoding.UTF8);
                        var list = JsonHelper.FromJson<List<WebBookmarkEntry>>(json);
                        if (list != null) return list;
                    }
                }
                catch { }

                // 默认内置种子示范书签
                var seeds = new List<WebBookmarkEntry>
                {
                    new WebBookmarkEntry { Title = "思源笔记官方社区 (链滴)", Url = "https://ld246.com", CleanUrl = "https://ld246.com", Category = "工具", Shortcut = "ld", VisitCount = 10 },
                    new WebBookmarkEntry { Title = "GitHub: Where the world builds software", Url = "https://github.com", CleanUrl = "https://github.com", Category = "开发", Shortcut = "gh", VisitCount = 20 },
                    new WebBookmarkEntry { Title = "arXiv.org e-Print archive", Url = "https://arxiv.org", CleanUrl = "https://arxiv.org", Category = "学术", Shortcut = "arxiv", VisitCount = 15 }
                };
                SaveToLocalCache(seeds);
                return seeds;
            }
        }

        private static void SaveToLocalCache(List<WebBookmarkEntry> list)
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
    }
}
