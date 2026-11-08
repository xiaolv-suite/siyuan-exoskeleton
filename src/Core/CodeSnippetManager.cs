using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
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
    /// 思源外骨骼 · 全库代码片段秒搜与纯净源码提取引擎 (CodeSnippetManager)
    /// 1. 毫秒级从思源 SQLite blocks 表定向秒搜代码块 (type='c')
    /// 2. 剥离 Markdown 围栏符号与多余零宽字符，保证纯净代码 0 杂质
    /// 3. 根据代码语言智能推导源码扩展名 (.py/.cs/.sql/.js/.sh 等)
    /// 4. 支持纯净源码直贴外部光标 (Shift+Enter) 与 1 键生成本地临时源码文件秒开
    /// </summary>
    public static class CodeSnippetManager
    {
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
        /// 将纯净代码导出为本地临时源码文件，并调起用户 IDE / 编辑器秒开
        /// </summary>
        public static string ExportAndOpenSourceFile(string code, string lang, string docTitle = "")
        {
            if (string.IsNullOrEmpty(code)) return "";

            try
            {
                string ext = ResolveExtension(lang);
                string tempDir = Path.Combine(Path.GetTempPath(), "XiaoLvDesktop", "snippets");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

                string safeDoc = !string.IsNullOrEmpty(docTitle) ? Regex.Replace(docTitle, @"[\\/:*?""<>|]", "_") : "snippet";
                if (safeDoc.Length > 20) safeDoc = safeDoc.Substring(0, 20);

                string fn = string.Format("{0}_{1:yyyyMMdd_HHmmss}{2}", safeDoc, DateTime.Now, ext);
                string targetPath = Path.Combine(tempDir, fn);

                File.WriteAllText(targetPath, code, new UTF8Encoding(false));

                // 调动系统关联程序秒开 (VS Code / Visual Studio / PyCharm / 记事本)
                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
                return targetPath;
            }
            catch
            {
                return "";
            }
        }
    }
}
