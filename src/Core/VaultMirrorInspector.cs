using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class VaultNoteItem
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string RelativePath { get; set; }
        public string Title { get; set; }
        public long FileSizeBytes { get; set; }
        public string FileSizeFormatted { get; set; }
        public DateTime LastModified { get; set; }

        public VaultNoteItem()
        {
            FileName = "";
            FilePath = "";
            RelativePath = "";
            Title = "";
            FileSizeBytes = 0;
            FileSizeFormatted = "";
            LastModified = DateTime.Now;
        }
    }

    /// <summary>
    /// 思源外骨骼 · 外部知识库/Obsidian Vault 镜像透视引擎 (VaultMirrorInspector)
    /// 1. 支持指定外部 Markdown 目录（如本地 Obsidian 库、文档工程），无需物理拷贝导入
    /// 2. 毫秒级流式遍历并提取文档首个 H1 标题、相对路径与更新时间
    /// 3. 支持输入 vault: 或 dir: 极速检索外部知识库，支持 1 键结构化并入思源或外部原位秒开
    /// </summary>
    public static class VaultMirrorInspector
    {
        public static List<VaultNoteItem> ScanVaultDirectory(string dirPath, string keyword = "", int maxResults = 40)
        {
            var results = new List<VaultNoteItem>();
            if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return results;

            string cleanKw = (keyword ?? "").Trim().ToLowerInvariant();

            try
            {
                var dirInfo = new DirectoryInfo(dirPath);
                var files = dirInfo.GetFiles("*.md", SearchOption.AllDirectories);

                foreach (var f in files)
                {
                    // 忽略 .git, .obsidian, node_modules 隐藏与技术目录
                    if (f.FullName.Contains("\\.") || f.FullName.Contains("/.") || f.FullName.Contains("node_modules"))
                    {
                        continue;
                    }

                    string relPath = f.FullName.Substring(dirInfo.FullName.Length).TrimStart('\\', '/');
                    string title = Path.GetFileNameWithoutExtension(f.Name);

                    // 快速流式读取前 5 行提取首个一级标题
                    try
                    {
                        using (var sr = new StreamReader(f.FullName, Encoding.UTF8))
                        {
                            for (int i = 0; i < 5 && !sr.EndOfStream; i++)
                            {
                                string line = sr.ReadLine();
                                if (!string.IsNullOrEmpty(line) && line.StartsWith("# "))
                                {
                                    title = line.Substring(2).Trim();
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    bool isMatch = string.IsNullOrEmpty(cleanKw) ||
                                   title.ToLowerInvariant().Contains(cleanKw) ||
                                   f.Name.ToLowerInvariant().Contains(cleanKw) ||
                                   relPath.ToLowerInvariant().Contains(cleanKw);

                    if (isMatch)
                    {
                        results.Add(new VaultNoteItem
                        {
                            FileName = f.Name,
                            FilePath = f.FullName,
                            RelativePath = relPath,
                            Title = title,
                            FileSizeBytes = f.Length,
                            FileSizeFormatted = FormatFileSize(f.Length),
                            LastModified = f.LastWriteTime
                        });

                        if (results.Count >= maxResults) break;
                    }
                }
            }
            catch { }

            return results;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.1") + " MB";
        }
    }
}
