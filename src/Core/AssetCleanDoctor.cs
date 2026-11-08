using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XiaoLv.Desktop.Core
{
    public class UnusedAssetItem
    {
        public string Name { get; set; }
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public long SizeBytes { get; set; }
        public string SizeFormatted { get; set; }

        public UnusedAssetItem()
        {
            Name = "";
            RelativePath = "";
            FullPath = "";
            SizeBytes = 0;
            SizeFormatted = "";
        }
    }

    public class AssetAuditResult
    {
        public int TotalUnusedCount { get; set; }
        public long TotalSizeBytes { get; set; }
        public string TotalSizeFormatted { get; set; }
        public List<UnusedAssetItem> Items { get; set; }
        public int QuarantinedCount { get; set; }

        public AssetAuditResult()
        {
            TotalUnusedCount = 0;
            TotalSizeBytes = 0;
            TotalSizeFormatted = "0 B";
            Items = new List<UnusedAssetItem>();
            QuarantinedCount = 0;
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全库无主孤儿资产深度体检与安全隔离区清道夫 (AssetCleanDoctor)
    /// 遵循【安全第一铁律】：
    /// 1. 绝不盲目粗暴执行物理删除，提供 .quarantine 安全隔离区保护
    /// 2. 毫秒级探测无主附件并精确统计累计占用磁盘体积
    /// 3. 支持一键安全隔离、一键原位打捞复原与终极安全释放
    /// </summary>
    public static class AssetCleanDoctor
    {
        private const string QUARANTINE_DIR_NAME = ".quarantine";

        /// <summary>
        /// 审计并获取当前资产库中的无主孤儿文件详情
        /// </summary>
        public static AssetAuditResult AuditUnusedAssets(string workspaceAssetsDir, List<string> rawUnusedNames)
        {
            var res = new AssetAuditResult();
            if (string.IsNullOrEmpty(workspaceAssetsDir) || !Directory.Exists(workspaceAssetsDir)) return res;

            string quarantineDir = Path.Combine(workspaceAssetsDir, QUARANTINE_DIR_NAME);
            if (Directory.Exists(quarantineDir))
            {
                try
                {
                    res.QuarantinedCount = Directory.GetFiles(quarantineDir).Length;
                }
                catch { }
            }

            if (rawUnusedNames == null || rawUnusedNames.Count == 0) return res;

            foreach (var name in rawUnusedNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                string cleanName = Path.GetFileName(name);
                string fullPath = Path.Combine(workspaceAssetsDir, cleanName);

                var item = new UnusedAssetItem
                {
                    Name = cleanName,
                    RelativePath = "data/assets/" + cleanName,
                    FullPath = fullPath
                };

                if (File.Exists(fullPath))
                {
                    try
                    {
                        var fi = new FileInfo(fullPath);
                        item.SizeBytes = fi.Length;
                        item.SizeFormatted = FormatFileSize(fi.Length);
                        res.TotalSizeBytes += fi.Length;
                    }
                    catch { }
                }

                res.Items.Add(item);
            }

            res.TotalUnusedCount = res.Items.Count;
            res.TotalSizeFormatted = FormatFileSize(res.TotalSizeBytes);
            return res;
        }

        /// <summary>
        /// 将无主孤儿文件安全移动到 .quarantine 隔离区 (零风险防误删)
        /// </summary>
        public static int QuarantineAssets(string workspaceAssetsDir, List<string> fileNames)
        {
            if (string.IsNullOrEmpty(workspaceAssetsDir) || !Directory.Exists(workspaceAssetsDir)) return 0;
            if (fileNames == null || fileNames.Count == 0) return 0;

            string quarantineDir = Path.Combine(workspaceAssetsDir, QUARANTINE_DIR_NAME);
            if (!Directory.Exists(quarantineDir))
            {
                try { Directory.CreateDirectory(quarantineDir); } catch { return 0; }
            }

            int successCount = 0;
            foreach (var fn in fileNames)
            {
                try
                {
                    string clean = Path.GetFileName(fn);
                    string src = Path.Combine(workspaceAssetsDir, clean);
                    string dest = Path.Combine(quarantineDir, clean);

                    if (File.Exists(src))
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(src, dest);
                        successCount++;
                    }
                }
                catch { }
            }

            return successCount;
        }

        /// <summary>
        /// 将隔离区文件原位打捞复原至 assets 目录
        /// </summary>
        public static bool RestoreFromQuarantine(string workspaceAssetsDir, string fileName)
        {
            if (string.IsNullOrEmpty(workspaceAssetsDir) || string.IsNullOrEmpty(fileName)) return false;
            try
            {
                string clean = Path.GetFileName(fileName);
                string src = Path.Combine(workspaceAssetsDir, QUARANTINE_DIR_NAME, clean);
                string dest = Path.Combine(workspaceAssetsDir, clean);

                if (File.Exists(src))
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(src, dest);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 彻底清空并永久释放隔离区
        /// </summary>
        public static long PurgeQuarantine(string workspaceAssetsDir)
        {
            if (string.IsNullOrEmpty(workspaceAssetsDir)) return 0;
            long freedBytes = 0;
            try
            {
                string quarantineDir = Path.Combine(workspaceAssetsDir, QUARANTINE_DIR_NAME);
                if (Directory.Exists(quarantineDir))
                {
                    var di = new DirectoryInfo(quarantineDir);
                    foreach (var fi in di.GetFiles())
                    {
                        try
                        {
                            freedBytes += fi.Length;
                            fi.Delete();
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return freedBytes;
        }

        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1048576) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / 1048576.0).ToString("F1") + " MB";
        }
    }
}
