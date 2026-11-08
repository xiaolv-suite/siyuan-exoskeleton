using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class LinkItem
    {
        public string BlockId { get; set; }
        public string RootId { get; set; }
        public string DocTitle { get; set; }
        public string Content { get; set; }
        public string UpdatedTime { get; set; }

        public LinkItem()
        {
            BlockId = "";
            RootId = "";
            DocTitle = "";
            Content = "";
            UpdatedTime = "";
        }
    }

    public class BiDirectionalLinksResult
    {
        public string TargetBlockId { get; set; }
        public string TargetTitle { get; set; }
        public List<LinkItem> InboundBacklinks { get; set; }   // 谁引用了我 (反向反链)
        public List<LinkItem> OutboundReferences { get; set; } // 我引用了谁 (正向出链)
        public int TotalLinks { get { return (InboundBacklinks != null ? InboundBacklinks.Count : 0) + (OutboundReferences != null ? OutboundReferences.Count : 0); } }

        public BiDirectionalLinksResult()
        {
            TargetBlockId = "";
            TargetTitle = "";
            InboundBacklinks = new List<LinkItem>();
            OutboundReferences = new List<LinkItem>();
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全局双向反链与知识图谱雷达引擎 (BiDirectionalGraphRadar)
    /// 1. 纳秒级穿透思源底层 SQLite 的 refs 关系表与 blocks 表
    /// 2. 毫秒级提取当前笔记块的双向引用网络：
    ///    - Inbound Backlinks: 反向反链（全库哪些笔记和段落引用了当前块）
    ///    - Outbound References: 正向出链（当前笔记块内部引用了全库哪些知识点）
    /// 3. 支持在 Bento 便当盒抽屉中以高保真交互图谱与卡片呈现，支持 1 键回贴双链与跳转
    /// </summary>
    public static class BiDirectionalGraphRadar
    {
        public static async Task<BiDirectionalLinksResult> GetLinksForBlockAsync(string blockId, string blockTitle = "")
        {
            var result = new BiDirectionalLinksResult
            {
                TargetBlockId = blockId ?? "",
                TargetTitle = blockTitle ?? "目标笔记块"
            };

            if (string.IsNullOrEmpty(blockId)) return result;

            try
            {
                string safeId = blockId.Replace("'", "''");

                // 1. 查询反向反链 (Inbound Backlinks: 谁引用了当前块)
                string inboundSql = string.Format(
                    "SELECT b.id, b.root_id, b.content, b.hpath, b.updated FROM refs r " +
                    "JOIN blocks b ON b.id = r.block_id " +
                    "WHERE r.def_block_id = '{0}' OR r.def_block_root_id = '{0}' " +
                    "ORDER BY b.updated DESC LIMIT 30", safeId);

                var inRows = await SiYuanClient.SqlAsync(inboundSql);
                if (inRows != null)
                {
                    var seen = new HashSet<string>();
                    foreach (var r in inRows)
                    {
                        string bId = JsonHelper.GetString(r, "id");
                        if (string.IsNullOrEmpty(bId) || bId == blockId || seen.Contains(bId)) continue;
                        seen.Add(bId);

                        string hpath = JsonHelper.GetString(r, "hpath");
                        string doc = Path.GetFileName(hpath);
                        if (string.IsNullOrEmpty(doc)) doc = "关联文档";

                        result.InboundBacklinks.Add(new LinkItem
                        {
                            BlockId = bId,
                            RootId = JsonHelper.GetString(r, "root_id"),
                            DocTitle = doc,
                            Content = TextCleaner.StripSiYuanAttributes(JsonHelper.GetString(r, "content")),
                            UpdatedTime = FormatBlockTime(JsonHelper.GetString(r, "updated"))
                        });
                    }
                }

                // 2. 查询正向出链 (Outbound References: 当前块引用了谁)
                string outboundSql = string.Format(
                    "SELECT b.id, b.root_id, b.content, b.hpath, b.updated FROM refs r " +
                    "JOIN blocks b ON b.id = r.def_block_id " +
                    "WHERE r.block_id = '{0}' OR r.root_id = '{0}' " +
                    "ORDER BY b.updated DESC LIMIT 30", safeId);

                var outRows = await SiYuanClient.SqlAsync(outboundSql);
                if (outRows != null)
                {
                    var seen = new HashSet<string>();
                    foreach (var r in outRows)
                    {
                        string bId = JsonHelper.GetString(r, "id");
                        if (string.IsNullOrEmpty(bId) || bId == blockId || seen.Contains(bId)) continue;
                        seen.Add(bId);

                        string hpath = JsonHelper.GetString(r, "hpath");
                        string doc = Path.GetFileName(hpath);
                        if (string.IsNullOrEmpty(doc)) doc = "出链文档";

                        result.OutboundReferences.Add(new LinkItem
                        {
                            BlockId = bId,
                            RootId = JsonHelper.GetString(r, "root_id"),
                            DocTitle = doc,
                            Content = TextCleaner.StripSiYuanAttributes(JsonHelper.GetString(r, "content")),
                            UpdatedTime = FormatBlockTime(JsonHelper.GetString(r, "updated"))
                        });
                    }
                }

                // 3. 保底：若 refs 索引表暂无记录，通过块引用语法 ((blockId ...)) 启发式回退扫描
                if (result.TotalLinks == 0)
                {
                    string fallbackSql = string.Format(
                        "SELECT b.id, b.root_id, b.content, b.hpath, b.updated FROM blocks b " +
                        "WHERE (b.content LIKE '%(({0} %' OR b.markdown LIKE '%(({0} %') " +
                        "ORDER BY b.updated DESC LIMIT 15", safeId);

                    var fbRows = await SiYuanClient.SqlAsync(fallbackSql);
                    if (fbRows != null)
                    {
                        foreach (var r in fbRows)
                        {
                            string bId = JsonHelper.GetString(r, "id");
                            if (string.IsNullOrEmpty(bId) || bId == blockId) continue;
                            string hpath = JsonHelper.GetString(r, "hpath");
                            string doc = Path.GetFileName(hpath);
                            if (string.IsNullOrEmpty(doc)) doc = "反链引用";

                            result.InboundBacklinks.Add(new LinkItem
                            {
                                BlockId = bId,
                                RootId = JsonHelper.GetString(r, "root_id"),
                                DocTitle = doc,
                                Content = TextCleaner.StripSiYuanAttributes(JsonHelper.GetString(r, "content")),
                                UpdatedTime = FormatBlockTime(JsonHelper.GetString(r, "updated"))
                            });
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        private static string FormatBlockTime(string updated)
        {
            if (string.IsNullOrEmpty(updated) || updated.Length < 14) return "";
            try
            {
                return string.Format("{0}-{1}-{2} {3}:{4}",
                    updated.Substring(0, 4),
                    updated.Substring(4, 2),
                    updated.Substring(6, 2),
                    updated.Substring(8, 2),
                    updated.Substring(10, 2));
            }
            catch
            {
                return "";
            }
        }
    }
}
