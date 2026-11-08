using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class WatchedCodeSnippetMeta
    {
        public string FilePath { get; set; }
        public string BlockId { get; set; }
        public string Language { get; set; }
        public string DocTitle { get; set; }
        public string LastSyncedHash { get; set; }
    }

    public class CodeSnippetItem
    {
        public string BlockId { get; set; }
        public string RootId { get; set; }
        public string DocTitle { get; set; }
        public string Language { get; set; }
        public string Code { get; set; }
        public int LineCount { get; set; }
        public string FileExtension { get; set; }

        public CodeSnippetItem()
        {
            BlockId = "";
            RootId = "";
            DocTitle = "";
            Language = "text";
            Code = "";
            LineCount = 1;
            FileExtension = ".txt";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 全库代码片段秒搜、纯净源码提取与双向热回写引擎 (CodeSnippetManager)
    /// 1. 毫秒级从思源 SQLite blocks 表定向秒搜代码块 (type='c')
    /// 2. 剥离 Markdown 围栏符号与多余零宽字符，保证纯净代码 0 杂质
    /// 3. 根据代码语言智能推导源码扩展名 (.py/.cs/.sql/.js/.sh 等)
    /// 4. 支持纯净源码直贴外部光标 (Shift+Enter)、1 键生成本地临时源码文件秒开
    /// 5. 支持外部 VS Code / IDE Ctrl+S 热编辑自动监听并原位回写思源代码块 (/api/block/updateBlock)
    /// </summary>
    public static class CodeSnippetManager
    {
        private static readonly object _watchLock = new object();
        private static readonly Dictionary<string, WatchedCodeSnippetMeta> _watchedFiles = new Dictionary<string, WatchedCodeSnippetMeta>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Timer> _debounceTimers = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        private static FileSystemWatcher _snippetDirWatcher = null;

        /// <summary>
        /// 当外部 IDE 保存代码文件并成功同步回思源块时触发 (blockId, updatedCode, lang, docTitle)
        /// </summary>
        public static event Action<string, string, string, string> OnExternalCodeBlockSynced;

        private static readonly Dictionary<string, string> _langToExt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "python", ".py" }, { "py", ".py" },
            { "csharp", ".cs" }, { "cs", ".cs" }, { "c#", ".cs" },
            { "javascript", ".js" }, { "js", ".js" },
            { "typescript", ".ts" }, { "ts", ".ts" },
            { "sql", ".sql" },
            { "html", ".html" }, { "htm", ".html" },
            { "css", ".css" }, { "scss", ".scss" }, { "less", ".less" },
            { "json", ".json" },
            { "xml", ".xml" },
            { "yaml", ".yaml" }, { "yml", ".yml" },
            { "bash", ".sh" }, { "sh", ".sh" }, { "shell", ".sh" },
            { "powershell", ".ps1" }, { "ps1", ".ps1" },
            { "go", ".go" }, { "golang", ".go" },
            { "rust", ".rs" }, { "rs", ".rs" },
            { "cpp", ".cpp" }, { "c++", ".cpp" }, { "c", ".c" },
            { "java", ".java" },
            { "php", ".php" },
            { "markdown", ".md" }, { "md", ".md" }
        };

        /// <summary>
        /// 剥离 Markdown 围栏标记与杂质，提取 100% 纯净代码文本
        /// </summary>
        public static string ExtractCleanCode(string rawContent, out string detectedLang)
        {
            detectedLang = "text";
            if (string.IsNullOrEmpty(rawContent)) return "";

            string text = rawContent.Trim();

            // 检查并剥离 ```language 前缀
            var fenceMatch = Regex.Match(text, @"^```([a-zA-Z0-9_#\+\-]+)?\r?\n([\s\S]*?)\r?\n?```$", RegexOptions.Singleline);
            if (fenceMatch.Success)
            {
                string lang = fenceMatch.Groups[1].Value.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(lang)) detectedLang = lang;
                text = fenceMatch.Groups[2].Value;
            }

            // 剥离零宽字符与异常回车
            text = text.Replace("\u200B", "").Replace("\r\n", "\n").Trim('\r', '\n');
            return text;
        }

        /// <summary>
        /// 根据语言标识获取标准文件扩展名
        /// </summary>
        public static string ResolveExtension(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return ".txt";
            string ext;
            if (_langToExt.TryGetValue(lang.ToLowerInvariant().Trim(), out ext))
            {
                return ext;
            }
            return ".txt";
        }

        /// <summary>
        /// 将纯净代码封装为标准思源 Markdown 代码块并原位更新指定块 (/api/block/updateBlock)
        /// </summary>
        public static async Task<bool> UpdateCodeBlockInSiYuanAsync(string blockId, string cleanCode, string lang)
        {
            if (string.IsNullOrWhiteSpace(blockId)) return false;

            try
            {
                string detected;
                string pure = ExtractCleanCode(cleanCode ?? "", out detected);
                string finalLang = !string.IsNullOrWhiteSpace(lang) ? lang.Trim().ToLowerInvariant() : detected;
                if (finalLang == "text" || finalLang == "plain") finalLang = "";

                string md = "```" + finalLang + "\n" + pure + "\n```";
                var res = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                {
                    id = blockId.Trim(),
                    dataType = "markdown",
                    data = md
                });

                return res != null && res.Success;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 将纯净代码导出为本地临时源码文件，并调起用户 IDE / 编辑器秒开
        /// </summary>
        public static string ExportAndOpenSourceFile(string code, string lang, string docTitle = "")
        {
            return ExportAndWatchSourceFile(code, lang, docTitle, "", true);
        }

        /// <summary>
        /// 导出本地源码文件并自动挂载外部 IDE 热保存回写思源监听器
        /// </summary>
        public static string ExportAndWatchSourceFile(string code, string lang, string docTitle, string blockId, bool autoOpen = true)
        {
            if (string.IsNullOrEmpty(code)) return "";

            try
            {
                string ext = ResolveExtension(lang);
                string tempDir = Path.Combine(Path.GetTempPath(), "XiaoLvDesktop", "snippets");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

                string safeDoc = !string.IsNullOrEmpty(docTitle) ? Regex.Replace(docTitle, @"[\\/:*?""<>|]", "_") : "snippet";
                if (safeDoc.Length > 20) safeDoc = safeDoc.Substring(0, 20);

                string blockSuffix = !string.IsNullOrEmpty(blockId)
                    ? ("_" + Regex.Replace(blockId, @"[^a-zA-Z0-9\-]", ""))
                    : string.Format("_{0:yyyyMMdd_HHmmss}", DateTime.Now);

                string fn = string.Format("{0}{1}{2}", safeDoc, blockSuffix, ext);
                string targetPath = Path.Combine(tempDir, fn);

                File.WriteAllText(targetPath, code, new UTF8Encoding(false));

                if (!string.IsNullOrEmpty(blockId))
                {
                    RegisterCodeFileWatch(targetPath, blockId, lang, docTitle, code);
                }

                if (autoOpen)
                {
                    // 调动系统关联程序秒开 (VS Code / Visual Studio / PyCharm / 记事本)
                    Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
                }
                return targetPath;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 注册临时代码文件与思源源块 ID 的双向热同步监听
        /// </summary>
        public static void RegisterCodeFileWatch(string filePath, string blockId, string lang, string docTitle, string initialCode = "")
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(blockId)) return;

            try
            {
                string fullPath = Path.GetFullPath(filePath);
                string dir = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                string normInit = (initialCode ?? "").Replace("\r\n", "\n").Trim('\r', '\n');

                lock (_watchLock)
                {
                    _watchedFiles[fullPath] = new WatchedCodeSnippetMeta
                    {
                        FilePath = fullPath,
                        BlockId = blockId,
                        Language = lang ?? "text",
                        DocTitle = docTitle ?? "代码片段",
                        LastSyncedHash = ComputeLightweightHash(normInit)
                    };

                    if (_snippetDirWatcher == null)
                    {
                        _snippetDirWatcher = new FileSystemWatcher(dir)
                        {
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                            Filter = "*.*",
                            EnableRaisingEvents = true
                        };
                        _snippetDirWatcher.Changed += (s, e) => OnSnippetFileChanged(e.FullPath);
                        _snippetDirWatcher.Created += (s, e) => OnSnippetFileChanged(e.FullPath);
                        _snippetDirWatcher.Renamed += (s, e) => OnSnippetFileChanged(e.FullPath);
                    }
                }
            }
            catch { }
        }

        private static void OnSnippetFileChanged(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            lock (_watchLock)
            {
                if (!_watchedFiles.ContainsKey(fullPath)) return;

                Timer existing;
                if (_debounceTimers.TryGetValue(fullPath, out existing))
                {
                    try { existing.Dispose(); } catch { }
                    _debounceTimers.Remove(fullPath);
                }

                string target = fullPath;
                _debounceTimers[fullPath] = new Timer(state =>
                {
                    lock (_watchLock)
                    {
                        _debounceTimers.Remove(target);
                    }
                    var unusedTask = SyncModifiedFileToSiYuanAsync(target);
                }, null, 380, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 读取本地已修改的源码文件并静默原位回写至思源代码块
        /// </summary>
        public static async Task<bool> SyncModifiedFileToSiYuanAsync(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return false;

            WatchedCodeSnippetMeta meta = null;
            lock (_watchLock)
            {
                _watchedFiles.TryGetValue(fullPath, out meta);
            }
            if (meta == null || string.IsNullOrEmpty(meta.BlockId)) return false;

            try
            {
                string updatedCode = "";
                for (int retry = 0; retry < 3; retry++)
                {
                    bool readOk = false;
                    try
                    {
                        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            updatedCode = sr.ReadToEnd();
                        }
                        readOk = true;
                    }
                    catch { }
                    if (readOk) break;
                    await Task.Delay(80);
                }

                string normCode = (updatedCode ?? "").Replace("\r\n", "\n").Trim('\r', '\n');
                if (string.IsNullOrEmpty(normCode)) return false;

                string newHash = ComputeLightweightHash(normCode);
                if (newHash == meta.LastSyncedHash) return false;

                bool ok = await UpdateCodeBlockInSiYuanAsync(meta.BlockId, normCode, meta.Language);
                if (ok)
                {
                    lock (_watchLock)
                    {
                        meta.LastSyncedHash = newHash;
                    }

                    OmniNotificationHub.Push("外部 IDE 代码热回写",
                        string.Format("《{0}》中的 {1} 代码块已通过外部编辑器自动同步回思源！", meta.DocTitle, (meta.Language ?? "code").ToUpperInvariant()),
                        "success", "desktop", meta.BlockId);

                    if (OnExternalCodeBlockSynced != null)
                    {
                        try { OnExternalCodeBlockSynced(meta.BlockId, normCode, meta.Language, meta.DocTitle); } catch { }
                    }
                }
                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeLightweightHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "0";
            unchecked
            {
                int hash = 23;
                for (int i = 0; i < s.Length; i++)
                {
                    hash = hash * 31 + s[i];
                }
                return hash.ToString("X8") + "_" + s.Length;
            }
        }
    }
}
