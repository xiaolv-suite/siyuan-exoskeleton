using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class ZoteroItemSummary
    {
        public string ItemKey { get; set; }
        public string Title { get; set; }
        public string Creators { get; set; }
        public string Year { get; set; }
        public string Publication { get; set; }
        public string Doi { get; set; }
        public string Url { get; set; }
        public string LocalPdfPath { get; set; }
        public string SelectUri { get; set; }

        public ZoteroItemSummary()
        {
            ItemKey = "";
            Title = "";
            Creators = "";
            Year = "";
            Publication = "";
            Doi = "";
            Url = "";
            LocalPdfPath = "";
            SelectUri = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · Zotero 学术文献库深度感知与双向穿透引擎 (ZoteroBridge)
    /// 零任何外部重型依赖，毫秒级实现：
    /// 1. 探测 Zotero 本地 23119 连接器服务是否在线
    /// 2. 嗅探本地 Zotero 存储目录与 zotero.sqlite 数据库
    /// 3. 生成携带 zotero://select/items/... 原位定位与 SumatraPDF / Edge 底稿穿透的思源文献卡片
    /// 4. 挂载标准 custom-type="literature-card" 与 custom-doi 原生 IAL 属性
    /// </summary>
    public static class ZoteroBridge
    {
        private const int ZOTERO_LOCAL_PORT = 23119;

        /// <summary>
        /// 检测 Zotero 本地连接器服务是否在线 (0.5ms 极速探针)
        /// </summary>
        public static bool IsZoteroConnectorOnline()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ZOTERO_LOCAL_PORT + "/connector/ping");
                req.Method = "GET";
                req.Timeout = 400;
                req.ReadWriteTimeout = 400;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return resp.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取本地 Zotero 数据存储目录
        /// </summary>
        public static string GetZoteroDataDir()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string defaultPath = Path.Combine(userProfile, "Zotero");
                if (Directory.Exists(defaultPath) && File.Exists(Path.Combine(defaultPath, "zotero.sqlite")))
                {
                    return defaultPath;
                }

                // 探测常用多盘符路径 (如 D:\Zotero 或 D:\RJ\Zotero)
                string[] probePaths = new string[]
                {
                    @"D:\Zotero",
                    @"D:\RJ\Zotero",
                    @"E:\Zotero",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zotero")
                };

                foreach (var p in probePaths)
                {
                    if (Directory.Exists(p) && File.Exists(Path.Combine(p, "zotero.sqlite")))
                    {
                        return p;
                    }
                }
            }
            catch { }

            return "";
        }

        /// <summary>
        /// 从文献元数据构建标准 Zotero 条目摘要
        /// </summary>
        public static ZoteroItemSummary CreateItemSummary(string itemKey, string title, string creators, string year, string doi = "", string publication = "", string localPdf = "")
        {
            var summary = new ZoteroItemSummary
            {
                ItemKey = itemKey ?? "",
                Title = title ?? "未命名学术文献",
                Creators = creators ?? "",
                Year = year ?? "",
                Doi = doi ?? "",
                Publication = publication ?? "",
                LocalPdfPath = localPdf ?? "",
                SelectUri = !string.IsNullOrEmpty(itemKey) ? ("zotero://select/items/0_" + itemKey) : "zotero://"
            };

            return summary;
        }

        /// <summary>
        /// 生成针对思源笔记的标准文献 Callout 卡片 Markdown
        /// </summary>
        public static string BuildLiteratureCardMarkdown(ZoteroItemSummary doc)
        {
            if (doc == null) return "";
            var sb = new StringBuilder();

            string title = !string.IsNullOrEmpty(doc.Title) ? doc.Title : "学术文献卡片";
            sb.AppendFormat("### 📖 《{0}》\n", title);

            // 作者与出版元数据
            var metaParts = new List<string>();
            if (!string.IsNullOrEmpty(doc.Creators)) metaParts.Add(string.Format("✍️ **作者**: {0}", doc.Creators));
            if (!string.IsNullOrEmpty(doc.Year)) metaParts.Add(string.Format("📅 **年份**: {0}", doc.Year));
            if (!string.IsNullOrEmpty(doc.Publication)) metaParts.Add(string.Format("🏛️ **来源**: {0}", doc.Publication));

            if (metaParts.Count > 0)
            {
                sb.AppendLine("> " + string.Join(" · ", metaParts));
            }

            // DOI 与跳转链接
            var linkParts = new List<string>();
            if (!string.IsNullOrEmpty(doc.Doi))
            {
                linkParts.Add(string.Format("🌐 **DOI**: [{0}](https://doi.org/{1})", doc.Doi, doc.Doi));
            }
            if (!string.IsNullOrEmpty(doc.SelectUri))
            {
                linkParts.Add(string.Format("[⚡ 在 Zotero 中定位]({0})", doc.SelectUri));
            }
            if (!string.IsNullOrEmpty(doc.LocalPdfPath) && File.Exists(doc.LocalPdfPath))
            {
                string openPdfUrl = "siyuan-exoskeleton://open-pdf?path=" + Uri.EscapeDataString(doc.LocalPdfPath) + "&page=1";
                linkParts.Add(string.Format("[📑 用外骨骼透视底稿 PDF]({0})", openPdfUrl));
            }

            if (linkParts.Count > 0)
            {
                sb.AppendLine("> " + string.Join(" · ", linkParts));
            }
            sb.AppendLine();

            // 研读批注区
            sb.AppendLine("#### 📝 核心论点与研读批注");
            sb.AppendLine("- [ ] 核心思想梳理与研究方法推演");
            sb.AppendLine();

            // 挂载标准 IAL 属性
            sb.AppendFormat("{{: custom-type=\"literature-card\" custom-zotero-key=\"{0}\" custom-doi=\"{1}\" }}\n",
                (doc.ItemKey ?? "").Replace("\"", "\\\""),
                (doc.Doi ?? "").Replace("\"", "\\\""));

            return sb.ToString();
        }

        /// <summary>
        /// 调起 Zotero 选中指定条目
        /// </summary>
        public static bool SelectItemInZotero(string itemKey)
        {
            if (string.IsNullOrEmpty(itemKey)) return false;
            try
            {
                string uri = itemKey.StartsWith("zotero://") ? itemKey : ("zotero://select/items/0_" + itemKey.Trim());
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
