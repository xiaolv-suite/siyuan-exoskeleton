using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class FlashbackItem
    {
        public string BlockId { get; set; }
        public string RootId { get; set; }
        public string DocTitle { get; set; }
        public string Content { get; set; }
        public string CreatedTime { get; set; }
        public string TimeAgoDescription { get; set; }
        public bool IsAnniversary { get; set; }

        public FlashbackItem()
        {
            BlockId = "";
            RootId = "";
            DocTitle = "";
            Content = "";
            CreatedTime = "";
            TimeAgoDescription = "";
            IsAnniversary = false;
        }
    }

    /// <summary>
    /// 思源外骨骼 · 浮墨漫游回顾与那年今日灵感唤醒引擎 (FlashbackManager)
    /// 1. 深度对标 flomo「浮墨漫游」与 Readwise「每日回顾」，让尘封的重点笔记自然复现
    /// 2. 毫秒级直接穿透思源底层 SQLite 随机打捞有价值的思考块 (15~300 字)
    /// 3. 自动匹配「那年今日」(历史同月同日) 优先唤醒回忆与反思
    /// 4. 支持 1 键原位追加灵感续写、思源大窗原位定位与一键纯净复制
    /// </summary>
    public static class FlashbackManager
    {
        public static async Task<FlashbackItem> GetRandomFlashbackAsync()
        {
            try
            {
                var now = DateTime.Now;
                string monthDay = now.ToString("MMdd");

                // 1. 优先尝试探测「那年今日」(过去某年的今天)
                string anniversarySql = string.Format(
                    "SELECT b.id, b.root_id, b.content, b.hpath, b.created, b.updated FROM blocks b WHERE b.type IN ('p', 'h') AND length(b.content) >= 15 AND length(b.content) <= 300 AND b.created LIKE '_____{0}%' AND b.created NOT LIKE '{1}%' ORDER BY RANDOM() LIMIT 1",
                    monthDay, now.ToString("yyyyMMdd"));

                var anniRows = await SiYuanClient.SqlAsync(anniversarySql);
                if (anniRows != null && anniRows.Count > 0)
                {
                    var item = MapRowToFlashback(anniRows[0], true);
                    if (item != null) return item;
                }

                // 2. 随机漫游任意历史优质洞见块
                string randomSql = "SELECT b.id, b.root_id, b.content, b.hpath, b.created, b.updated FROM blocks b WHERE b.type IN ('p', 'h') AND length(b.content) >= 15 AND length(b.content) <= 300 AND b.content NOT LIKE '%http%' ORDER BY RANDOM() LIMIT 1";
                var randRows = await SiYuanClient.SqlAsync(randomSql);
                if (randRows != null && randRows.Count > 0)
                {
                    return MapRowToFlashback(randRows[0], false);
                }
            }
            catch { }

            return null;
        }

        private static FlashbackItem MapRowToFlashback(Dictionary<string, object> row, bool isAnniversary)
        {
            if (row == null) return null;

            string id = JsonHelper.GetString(row, "id");
            string rootId = JsonHelper.GetString(row, "root_id");
            string rawContent = JsonHelper.GetString(row, "content");
            string hpath = JsonHelper.GetString(row, "hpath");
            string created = JsonHelper.GetString(row, "created");

            string docTitle = Path.GetFileName(hpath);
            if (string.IsNullOrEmpty(docTitle)) docTitle = "未命名笔记";

            string cleanContent = TextCleaner.StripSiYuanAttributes(rawContent);

            var item = new FlashbackItem
            {
                BlockId = id,
                RootId = rootId,
                DocTitle = docTitle,
                Content = cleanContent,
                IsAnniversary = isAnniversary
            };

            // 解析时间距离
            if (!string.IsNullOrEmpty(created) && created.Length >= 14)
            {
                try
                {
                    int y = int.Parse(created.Substring(0, 4));
                    int m = int.Parse(created.Substring(4, 2));
                    int d = int.Parse(created.Substring(6, 2));
                    int hh = int.Parse(created.Substring(8, 2));
                    int mm = int.Parse(created.Substring(10, 2));
                    var createdDt = new DateTime(y, m, d, hh, mm, 0);

                    item.CreatedTime = createdDt.ToString("yyyy-MM-dd HH:mm");
                    var span = DateTime.Now - createdDt;

                    if (isAnniversary)
                    {
                        int yearsAgo = Math.Max(1, DateTime.Now.Year - y);
                        item.TimeAgoDescription = string.Format("{0} 年前今日 · 那年今日", yearsAgo);
                    }
                    else if (span.TotalDays >= 365)
                    {
                        item.TimeAgoDescription = string.Format("{0} 年前", Math.Round(span.TotalDays / 365.0, 1));
                    }
                    else if (span.TotalDays >= 30)
                    {
                        item.TimeAgoDescription = string.Format("{0} 个月前", Math.Round(span.TotalDays / 30.0, 0));
                    }
                    else if (span.TotalDays >= 1)
                    {
                        item.TimeAgoDescription = string.Format("{0} 天前", (int)span.TotalDays);
                    }
                    else
                    {
                        item.TimeAgoDescription = "今天早些时候";
                    }
                }
                catch
                {
                    item.TimeAgoDescription = "历史记录";
                }
            }
            else
            {
                item.TimeAgoDescription = "历史记录";
            }

            return item;
        }

        /// <summary>
        /// 原位为漫游块追加重温思考与续写内容
        /// </summary>
        public static async Task<bool> AppendThoughtAsync(string blockId, string thought)
        {
            if (string.IsNullOrEmpty(blockId) || string.IsNullOrEmpty(thought)) return false;
            try
            {
                string appendMd = string.Format("\n> 💭 **重温思考** ({0}): {1}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm"), thought.Trim());

                var res = await SiYuanClient.PostAsync("/api/block/insertBlock", new
                {
                    dataType = "markdown",
                    data = appendMd,
                    previousID = blockId
                });

                return res != null && res.Success;
            }
            catch
            {
                return false;
            }
        }
    }
}
