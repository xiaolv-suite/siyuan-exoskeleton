using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace XiaoLv.Desktop.Core
{
    public class ClipboardItem
    {
        public string RawText { get; set; }
        public string EnrichedMarkdown { get; set; }
        public string SniffedType { get; set; } // "url", "table", "code", "image", "text"
        public string Title { get; set; }
        public string Summary { get; set; }
        public string ImagePath { get; set; }
        public DateTime Timestamp { get; set; }

        public ClipboardItem()
        {
            RawText = "";
            EnrichedMarkdown = "";
            SniffedType = "text";
            Title = "";
            Summary = "";
            ImagePath = "";
            Timestamp = DateTime.Now;
        }
    }

    public static class ClipboardWatcher
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        private class MessageWindow : Form
        {
            public MessageWindow()
            {
                Text = "XiaoLvClipboardHiddenWindow";
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_CLIPBOARDUPDATE)
                {
                    OnClipboardUpdated();
                }
                base.WndProc(ref m);
            }
        }

        private static MessageWindow _window;
        private static int _lastUpdateTick = 0;
        private static string _lastProcessedText = "";

        public static bool IsEnabled { get; set; }
        public static ClipboardItem LatestItem { get; private set; }
        public static event Action<ClipboardItem> OnClipboardEnriched;

        public const int MaxRecentCount = 30;
        private static readonly object _historyLock = new object();
        public static readonly System.Collections.Generic.List<ClipboardItem> RecentItems = new System.Collections.Generic.List<ClipboardItem>();

        public static System.Collections.Generic.List<ClipboardItem> GetRecentItemsSnapshot()
        {
            lock (_historyLock)
            {
                return new System.Collections.Generic.List<ClipboardItem>(RecentItems);
            }
        }

        static ClipboardWatcher()
        {
            IsEnabled = true;
            LatestItem = new ClipboardItem();
        }

        public static void Start()
        {
            if (_window != null) return;
            try
            {
                _window = new MessageWindow();
                IntPtr handle = _window.Handle;
                AddClipboardFormatListener(handle);
            }
            catch { }
        }

        public static void Stop()
        {
            if (_window != null && !_window.IsDisposed)
            {
                try
                {
                    RemoveClipboardFormatListener(_window.Handle);
                    _window.Dispose();
                }
                catch { }
                _window = null;
            }
        }

        private static void OnClipboardUpdated()
        {
            if (!IsEnabled || SelectionHelper.IsInternalClipboardOperation) return;

            int now = Environment.TickCount;
            if (now - _lastUpdateTick < 150) return;
            _lastUpdateTick = now;

            ThreadPool.QueueUserWorkItem(state =>
            {
                Thread.Sleep(80); // 等待写入进程完全释放剪贴板
                try
                {
                    // 1. 优先检查剪贴板原生图片
                    if (Clipboard.ContainsImage())
                    {
                        var img = Clipboard.GetImage();
                        if (img != null)
                        {
                            string tmpDir = Path.Combine(Path.GetTempPath(), "XiaoLvDesktop");
                            if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);
                            string imgPath = Path.Combine(tmpDir, "clip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                            img.Save(imgPath, System.Drawing.Imaging.ImageFormat.Png);

                            var imgItem = new ClipboardItem
                            {
                                SniffedType = "image",
                                ImagePath = imgPath,
                                RawText = imgPath,
                                Title = string.Format("剪贴板图片 ({0}×{1})", img.Width, img.Height),
                                Summary = string.Format("图片 ({0}×{1})", img.Width, img.Height),
                                EnrichedMarkdown = "![" + Path.GetFileName(imgPath) + "](" + imgPath + ")",
                                Timestamp = DateTime.Now
                            };
                            LatestItem = imgItem;
                            lock (_historyLock)
                            {
                                RecentItems.Insert(0, imgItem);
                                while (RecentItems.Count > MaxRecentCount) RecentItems.RemoveAt(RecentItems.Count - 1);
                            }
                            if (OnClipboardEnriched != null) OnClipboardEnriched(imgItem);
                            return;
                        }
                    }

                    string txt = "";
                    string html = "";
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            if (Clipboard.ContainsText()) txt = Clipboard.GetText();
                            if (Clipboard.ContainsText(TextDataFormat.Html)) html = Clipboard.GetText(TextDataFormat.Html);
                            if (!string.IsNullOrEmpty(txt)) break;
                        }
                        catch
                        {
                            Thread.Sleep(40);
                        }
                    }

                    if (string.IsNullOrEmpty(txt)) return;
                    txt = txt.Trim();
                    if (txt == _lastProcessedText) return;
                    _lastProcessedText = txt;

                    var item = AnalyzeClipboardContent(txt, html);
                    LatestItem = item;
                    lock (_historyLock)
                    {
                        RecentItems.RemoveAll(x => x.RawText == item.RawText);
                        RecentItems.Insert(0, item);
                        while (RecentItems.Count > MaxRecentCount) RecentItems.RemoveAt(RecentItems.Count - 1);
                    }
                    if (OnClipboardEnriched != null)
                    {
                        OnClipboardEnriched(item);
                    }
                }
                catch { }
            });
        }

        public static ClipboardItem AnalyzeClipboardContent(string txt, string html)
        {
            var item = new ClipboardItem
            {
                RawText = txt,
                Timestamp = DateTime.Now
            };

            // 1. URL 嗅探与去追踪参数净化
            if (IsSingleUrl(txt))
            {
                string pureUrl = TextCleaner.PurifyUrl(txt);
                item.SniffedType = "url";
                item.Summary = "网页链接: " + Truncate(pureUrl, 40);
                item.EnrichedMarkdown = "[" + pureUrl + "](" + pureUrl + ")";

                // 异步抓取网页真实标题
                ThreadPool.QueueUserWorkItem(s =>
                {
                    try
                    {
                        var title = FetchWebPageTitle(pureUrl);
                        if (!string.IsNullOrEmpty(title))
                        {
                            item.Title = title;
                            item.Summary = "网页: " + title;
                            item.EnrichedMarkdown = "[" + title + "](" + pureUrl + ")";
                            if (OnClipboardEnriched != null) OnClipboardEnriched(item);
                        }
                    }
                    catch { }
                });
                return item;
            }

            // 2. 表格数据嗅探 (多行且含制表符 \t)
            string mdTable;
            int rows, cols;
            if (TryConvertTsvToMarkdown(txt, out mdTable, out rows, out cols))
            {
                item.SniffedType = "table";
                item.Title = string.Format("表格 ({0}行×{1}列)", rows, cols);
                item.Summary = string.Format("检测到数据表格 ({0}行×{1}列)", rows, cols);
                item.EnrichedMarkdown = mdTable;
                return item;
            }

            // 3. 代码片段嗅探
            string codeLang;
            if (TryDetectCodeSnippet(txt, out codeLang))
            {
                item.SniffedType = "code";
                item.Title = "代码片段 (" + codeLang + ")";
                item.Summary = "代码片段 (" + codeLang + ", " + txt.Split('\n').Length + " 行)";
                item.EnrichedMarkdown = "```" + codeLang + "\n" + txt + "\n```";
                return item;
            }

            // 4. 富文本 HTML 优先转换 (若包含超链接、加粗、图片等富文本元素)
            if (!string.IsNullOrEmpty(html) && (html.Contains("<a ") || html.Contains("<strong>") || html.Contains("<b>") || html.Contains("<img ")))
            {
                string richMd = TextCleaner.ConvertHtmlToMarkdown(html);
                if (!string.IsNullOrEmpty(richMd) && richMd.Length >= 2)
                {
                    item.SniffedType = "rich-text";
                    item.EnrichedMarkdown = richMd;
                    item.Title = Truncate(TextCleaner.CleanAll(txt).Replace("\r", "").Replace("\n", " "), 30);
                    item.Summary = string.Format("网页高保真富文本 ({0} 字，保留超链接/格式)", txt.Length);
                    return item;
                }
            }

            // 5. 普通纯文本预清洗 (去除多余断行、版权尾巴、盘古中英空格)
            string cleaned = TextCleaner.CleanAll(txt);
            item.SniffedType = "text";
            item.EnrichedMarkdown = !string.IsNullOrEmpty(cleaned) ? cleaned : txt;
            item.Title = Truncate(item.EnrichedMarkdown.Replace("\r", "").Replace("\n", " "), 30);
            if (!string.IsNullOrEmpty(cleaned) && cleaned != txt)
            {
                item.Summary = string.Format("已优化排版 ({0} 字 -> {1} 字)", txt.Length, cleaned.Length);
            }
            else
            {
                item.Summary = "普通文本 (" + txt.Length + " 字)";
            }
            return item;
        }

        private static bool IsSingleUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (s.Contains("\n") || s.Contains(" ")) return false;
            Uri uri;
            return Uri.TryCreate(s, UriKind.Absolute, out uri);
        }

        private static string FetchWebPageTitle(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 2500;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    char[] buffer = new char[8192];
                    int read = r.Read(buffer, 0, buffer.Length);
                    var html = new string(buffer, 0, read);
                    var m = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (m.Success)
                    {
                        var t = m.Groups[1].Value.Trim();
                        t = Regex.Replace(t, "\\s+", " ");
                        return WebUtility.HtmlDecode(t);
                    }
                }
            }
            catch { }
            return "";
        }

        public static bool TryConvertTsvToMarkdown(string txt, out string md, out int rowCount, out int colCount)
        {
            md = "";
            rowCount = 0;
            colCount = 0;
            if (string.IsNullOrEmpty(txt) || !txt.Contains("\t")) return false;

            var lines = txt.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return false;

            int firstCols = lines[0].Split('\t').Length;
            if (firstCols < 2) return false;

            var sb = new StringBuilder();
            rowCount = lines.Length;
            colCount = firstCols;

            // 表头
            var headers = lines[0].Split('\t');
            sb.Append("|");
            foreach (var h in headers) sb.Append(" ").Append(h.Trim()).Append(" |");
            sb.AppendLine();

            // 分割线
            sb.Append("|");
            for (int i = 0; i < headers.Length; i++) sb.Append(" --- |");
            sb.AppendLine();

            // 数据行
            for (int i = 1; i < lines.Length; i++)
            {
                var cells = lines[i].Split('\t');
                sb.Append("|");
                for (int c = 0; c < headers.Length; c++)
                {
                    var val = c < cells.Length ? cells[c].Trim() : "";
                    sb.Append(" ").Append(val).Append(" |");
                }
                sb.AppendLine();
            }

            md = sb.ToString().TrimEnd();
            return true;
        }

        private static bool TryDetectCodeSnippet(string txt, out string lang)
        {
            lang = "";
            if (string.IsNullOrEmpty(txt) || txt.Length < 20) return false;

            var lines = txt.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return false;

            // Python
            if (Regex.IsMatch(txt, "\\b(def\\s+\\w+\\(|import\\s+\\w+|from\\s+\\w+\\s+import|elif\\s+|print\\()"))
            {
                lang = "python";
                return true;
            }

            // JavaScript / TypeScript
            if (Regex.IsMatch(txt, "\\b(const\\s+\\w+\\s*=|let\\s+\\w+\\s*=|function\\s+\\w+\\(|console\\.log\\(|=>\\s*\\{)"))
            {
                lang = "javascript";
                return true;
            }

            // C# / Java
            if (Regex.IsMatch(txt, "\\b(public\\s+class\\s+|private\\s+void\\s+|using\\s+System;|namespace\\s+\\w+|Console\\.WriteLine\\()"))
            {
                lang = "csharp";
                return true;
            }

            // SQL
            if (Regex.IsMatch(txt, "^\\s*(SELECT|INSERT\\s+INTO|UPDATE|DELETE\\s+FROM|CREATE\\s+TABLE|ALTER\\s+TABLE)\\b", RegexOptions.IgnoreCase))
            {
                lang = "sql";
                return true;
            }

            // HTML / XML
            if (txt.StartsWith("<") && (txt.Contains("</div>") || txt.Contains("</p>") || txt.Contains("</html>") || txt.Contains("/>")))
            {
                lang = "html";
                return true;
            }

            // JSON
            if ((txt.StartsWith("{") && txt.EndsWith("}")) || (txt.StartsWith("[") && txt.EndsWith("]")))
            {
                if (txt.Contains("\":") || txt.Contains("\": "))
                {
                    lang = "json";
                    return true;
                }
            }

            return false;
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
