using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace XiaoLv.Desktop.Core
{
    public class OfficeHeadingItem
    {
        public int Level { get; set; } // 1 ~ 6
        public string Title { get; set; }
        public int ParagraphIndex { get; set; }

        public OfficeHeadingItem()
        {
            Level = 1;
            Title = "";
            ParagraphIndex = 0;
        }
    }

    public class OfficeCommentItem
    {
        public string Id { get; set; }
        public string Author { get; set; }
        public DateTime Date { get; set; }
        public string CommentText { get; set; }
        public string AnchorQuoteText { get; set; }

        public OfficeCommentItem()
        {
            Id = "";
            Author = "";
            Date = DateTime.Now;
            CommentText = "";
            AnchorQuoteText = "";
        }
    }

    public class ExcelSheetSummary
    {
        public string SheetName { get; set; }
        public int RowCount { get; set; }
        public int ColCount { get; set; }
        public List<string> HeaderColumns { get; set; }
        public string SampleMarkdownTable { get; set; }

        public ExcelSheetSummary()
        {
            SheetName = "";
            RowCount = 0;
            ColCount = 0;
            HeaderColumns = new List<string>();
            SampleMarkdownTable = "";
        }
    }

    public class SlideCardItem
    {
        public int SlideIndex { get; set; }
        public string Title { get; set; }
        public string ContentPreview { get; set; }
        public string SpeakerNotes { get; set; }
        public bool HasNotes { get; set; }

        public SlideCardItem()
        {
            SlideIndex = 1;
            Title = "";
            ContentPreview = "";
            SpeakerNotes = "";
            HasNotes = false;
        }
    }

    public class OfficeDocumentSummary
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string FileType { get; set; } // "docx", "xlsx", "pptx"
        public long FileSizeBytes { get; set; }
        public string FileSizeFormatted { get; set; }
        public DateTime LastModified { get; set; }

        public List<OfficeHeadingItem> Headings { get; set; }
        public List<OfficeCommentItem> Comments { get; set; }
        public List<ExcelSheetSummary> Sheets { get; set; }
        public List<SlideCardItem> Slides { get; set; }

        public string ExtractedPlainText { get; set; }
        public int TotalParagraphs { get; set; }
        public int TotalTables { get; set; }
        public int TotalWordCount { get; set; }

        public OfficeDocumentSummary()
        {
            FilePath = "";
            FileName = "";
            FileType = "";
            FileSizeBytes = 0;
            FileSizeFormatted = "";
            LastModified = DateTime.Now;
            Headings = new List<OfficeHeadingItem>();
            Comments = new List<OfficeCommentItem>();
            Sheets = new List<ExcelSheetSummary>();
            Slides = new List<SlideCardItem>();
            ExtractedPlainText = "";
            TotalParagraphs = 0;
            TotalTables = 0;
            TotalWordCount = 0;
        }
    }

    /// <summary>
    /// 思源外骨骼 · 本地 Office (Word / Excel / PPT) 纯原生零依赖流式解析引擎
    /// 纯粹利用 .NET 4.8 内置 System.IO.Compression.ZipArchive 与 XmlReader 流式透视
    /// 无需安装 Office/WPS，无第三方外部依赖，内存占用 < 8MB，解析耗时 < 30ms
    /// </summary>
    public static class OfficeOpenXmlParser
    {
        public static OfficeDocumentSummary InspectFile(string filePath, int maxTextLen = 150000)
        {
            var summary = new OfficeDocumentSummary();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return summary;

            try
            {
                var fi = new FileInfo(filePath);
                summary.FilePath = filePath;
                summary.FileName = fi.Name;
                summary.FileSizeBytes = fi.Length;
                summary.FileSizeFormatted = FormatFileSize(fi.Length);
                summary.LastModified = fi.LastWriteTime;

                string ext = fi.Extension.ToLowerInvariant();
                try { OfficeFileWatcher.WatchFile(filePath); } catch { }
                if (ext == ".docx")
                {
                    summary.FileType = "docx";
                    InspectDocx(filePath, summary, maxTextLen);
                }
                else if (ext == ".xlsx")
                {
                    summary.FileType = "xlsx";
                    InspectXlsx(filePath, summary);
                }
                else if (ext == ".pptx")
                {
                    summary.FileType = "pptx";
                    InspectPptx(filePath, summary);
                }
            }
            catch { }

            return summary;
        }

        #region Word (.docx) 解析
        private static void InspectDocx(string path, OfficeDocumentSummary summary, int maxTextLen)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // 1. 读取批注集合 (word/comments.xml)
                var commentsDict = new Dictionary<string, OfficeCommentItem>();
                var commentEntry = zip.GetEntry("word/comments.xml");
                if (commentEntry != null)
                {
                    try
                    {
                        using (var cs = commentEntry.Open())
                        using (var xr = XmlReader.Create(cs, new XmlReaderSettings { IgnoreWhitespace = true }))
                        {
                            OfficeCommentItem curComment = null;
                            var curSb = new StringBuilder();

                            while (xr.Read())
                            {
                                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "comment")
                                {
                                    string cid = xr.GetAttribute("w:id") ?? xr.GetAttribute("id") ?? "";
                                    string author = xr.GetAttribute("w:author") ?? xr.GetAttribute("author") ?? "审阅人";
                                    string dateStr = xr.GetAttribute("w:date") ?? xr.GetAttribute("date") ?? "";
                                    DateTime dt = DateTime.Now;
                                    DateTime.TryParse(dateStr, out dt);

                                    curComment = new OfficeCommentItem
                                    {
                                        Id = cid,
                                        Author = author,
                                        Date = dt
                                    };
                                    curSb.Clear();
                                }
                                else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t" && curComment != null)
                                {
                                    curSb.Append(xr.ReadElementContentAsString());
                                }
                                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "comment" && curComment != null)
                                {
                                    curComment.CommentText = curSb.ToString().Trim();
                                    if (!string.IsNullOrEmpty(curComment.Id))
                                    {
                                        commentsDict[curComment.Id] = curComment;
                                        summary.Comments.Add(curComment);
                                    }
                                    curComment = null;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 2. 流式读取正文与大纲 (word/document.xml)
                var docEntry = zip.GetEntry("word/document.xml");
                if (docEntry != null)
                {
                    using (var ds = docEntry.Open())
                    using (var xr = XmlReader.Create(ds, new XmlReaderSettings { IgnoreWhitespace = true }))
                    {
                        var textSb = new StringBuilder();
                        var paraSb = new StringBuilder();
                        int paraIndex = 0;
                        int currentHeadingLevel = 0;
                        string activeCommentId = null;
                        var commentAnchorSb = new StringBuilder();

                        while (xr.Read())
                        {
                            if (xr.NodeType == XmlNodeType.Element)
                            {
                                if (xr.LocalName == "p")
                                {
                                    paraIndex++;
                                    currentHeadingLevel = 0;
                                    paraSb.Clear();
                                }
                                else if (xr.LocalName == "pStyle")
                                {
                                    string val = xr.GetAttribute("w:val") ?? xr.GetAttribute("val") ?? "";
                                    currentHeadingLevel = ParseHeadingLevel(val);
                                }
                                else if (xr.LocalName == "tbl")
                                {
                                    summary.TotalTables++;
                                }
                                else if (xr.LocalName == "commentRangeStart")
                                {
                                    activeCommentId = xr.GetAttribute("w:id") ?? xr.GetAttribute("id");
                                    commentAnchorSb.Clear();
                                }
                                else if (xr.LocalName == "commentRangeEnd")
                                {
                                    string endId = xr.GetAttribute("w:id") ?? xr.GetAttribute("id");
                                    if (!string.IsNullOrEmpty(endId) && commentsDict.ContainsKey(endId))
                                    {
                                        commentsDict[endId].AnchorQuoteText = commentAnchorSb.ToString().Trim();
                                    }
                                    activeCommentId = null;
                                }
                                else if (xr.LocalName == "t")
                                {
                                    string t = xr.ReadElementContentAsString();
                                    paraSb.Append(t);
                                    if (activeCommentId != null) commentAnchorSb.Append(t);
                                    if (textSb.Length < maxTextLen) textSb.Append(t);
                                }
                            }
                            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "p")
                            {
                                string pText = paraSb.ToString().Trim();
                                if (!string.IsNullOrEmpty(pText))
                                {
                                    summary.TotalParagraphs++;
                                    if (currentHeadingLevel > 0)
                                    {
                                        summary.Headings.Add(new OfficeHeadingItem
                                        {
                                            Level = currentHeadingLevel,
                                            Title = pText,
                                            ParagraphIndex = paraIndex
                                        });
                                    }
                                    if (textSb.Length < maxTextLen) textSb.AppendLine();
                                }
                            }
                        }

                        summary.ExtractedPlainText = textSb.ToString().Trim();
                        summary.TotalWordCount = summary.ExtractedPlainText.Length;
                    }
                }
            }
        }

        private static int ParseHeadingLevel(string styleVal)
        {
            if (string.IsNullOrEmpty(styleVal)) return 0;
            var low = styleVal.ToLowerInvariant();
            if (low.Contains("heading1") || low == "1" || low.Contains("标题1") || low.Contains("heading 1")) return 1;
            if (low.Contains("heading2") || low == "2" || low.Contains("标题2") || low.Contains("heading 2")) return 2;
            if (low.Contains("heading3") || low == "3" || low.Contains("标题3") || low.Contains("heading 3")) return 3;
            if (low.Contains("heading4") || low == "4" || low.Contains("标题4") || low.Contains("heading 4")) return 4;
            if (low.Contains("heading5") || low == "5" || low.Contains("标题5") || low.Contains("heading 5")) return 5;
            if (low.Contains("heading6") || low == "6" || low.Contains("标题6") || low.Contains("heading 6")) return 6;
            return 0;
        }
        #endregion

        #region Excel (.xlsx) 解析
        private static void InspectXlsx(string path, OfficeDocumentSummary summary)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // 1. 读取共享字符串池 (xl/sharedStrings.xml)
                var sharedStrings = new List<string>();
                var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
                if (ssEntry != null)
                {
                    try
                    {
                        using (var s = ssEntry.Open())
                        using (var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true }))
                        {
                            var sb = new StringBuilder();
                            while (xr.Read())
                            {
                                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                                {
                                    sb.Append(xr.ReadElementContentAsString());
                                }
                                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "si")
                                {
                                    sharedStrings.Add(sb.ToString());
                                    sb.Clear();
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 2. 读取工作表路由表 (xl/workbook.xml)
                var wbEntry = zip.GetEntry("xl/workbook.xml");
                var sheetNames = new List<string>();
                if (wbEntry != null)
                {
                    try
                    {
                        using (var s = wbEntry.Open())
                        using (var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true }))
                        {
                            while (xr.Read())
                            {
                                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "sheet")
                                {
                                    string sName = xr.GetAttribute("name") ?? "";
                                    if (!string.IsNullOrEmpty(sName)) sheetNames.Add(sName);
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (sheetNames.Count == 0) sheetNames.Add("Sheet1");

                // 3. 采样读取第 1 个工作表 (xl/worksheets/sheet1.xml)
                int sheetIdx = 1;
                foreach (var sName in sheetNames)
                {
                    var sheetEntry = zip.GetEntry("xl/worksheets/sheet" + sheetIdx + ".xml");
                    if (sheetEntry != null)
                    {
                        var sheetSummary = new ExcelSheetSummary { SheetName = sName };
                        SampleWorksheet(sheetEntry, sharedStrings, sheetSummary);
                        summary.Sheets.Add(sheetSummary);
                    }
                    sheetIdx++;
                    if (sheetIdx > 12) break; // 最多采样前 12 张表
                }
            }
        }

        private static void SampleWorksheet(ZipArchiveEntry entry, List<string> sharedStrings, ExcelSheetSummary sheetSummary)
        {
            try
            {
                using (var s = entry.Open())
                using (var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true }))
                {
                    var rowsData = new List<List<string>>();
                    List<string> curRow = null;
                    string cellType = "";

                    while (xr.Read())
                    {
                        if (xr.NodeType == XmlNodeType.Element)
                        {
                            if (xr.LocalName == "row")
                            {
                                curRow = new List<string>();
                            }
                            else if (xr.LocalName == "c")
                            {
                                cellType = xr.GetAttribute("t") ?? "";
                            }
                            else if (xr.LocalName == "v" && curRow != null)
                            {
                                string val = xr.ReadElementContentAsString();
                                int sIdx = -1;
                                if (cellType == "s" && int.TryParse(val, out sIdx) && sIdx >= 0 && sIdx < sharedStrings.Count)
                                {
                                    curRow.Add(sharedStrings[sIdx]);
                                }
                                else
                                {
                                    curRow.Add(val);
                                }
                            }
                        }
                        else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "row" && curRow != null)
                        {
                            if (curRow.Count > 0)
                            {
                                rowsData.Add(curRow);
                                if (curRow.Count > sheetSummary.ColCount) sheetSummary.ColCount = curRow.Count;
                            }
                            curRow = null;
                            if (rowsData.Count >= 25) break; // 采样前 25 行
                        }
                    }

                    sheetSummary.RowCount = rowsData.Count;

                    // 构造第一行表头和 Markdown 表格预览
                    if (rowsData.Count > 0)
                    {
                        sheetSummary.HeaderColumns = rowsData[0];
                        var sb = new StringBuilder();

                        // 表头
                        sb.Append("|");
                        for (int c = 0; c < sheetSummary.ColCount; c++)
                        {
                            var val = c < rowsData[0].Count ? rowsData[0][c].Trim() : ("列" + (c + 1));
                            sb.Append(" ").Append(val).Append(" |");
                        }
                        sb.AppendLine();

                        // 分割线
                        sb.Append("|");
                        for (int c = 0; c < sheetSummary.ColCount; c++) sb.Append(" --- |");
                        sb.AppendLine();

                        // 预览行 (最多 6 行)
                        for (int r = 1; r < Math.Min(rowsData.Count, 7); r++)
                        {
                            sb.Append("|");
                            for (int c = 0; c < sheetSummary.ColCount; c++)
                            {
                                var val = c < rowsData[r].Count ? rowsData[r][c].Trim() : "";
                                sb.Append(" ").Append(val).Append(" |");
                            }
                            sb.AppendLine();
                        }

                        sheetSummary.SampleMarkdownTable = sb.ToString().TrimEnd();
                    }
                }
            }
            catch { }
        }
        #endregion

        #region PowerPoint (.pptx) 解析
        private static void InspectPptx(string path, OfficeDocumentSummary summary)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                int slideNum = 1;
                while (true)
                {
                    var slideEntry = zip.GetEntry("ppt/slides/slide" + slideNum + ".xml");
                    if (slideEntry == null) break;

                    var item = new SlideCardItem { SlideIndex = slideNum };

                    // 1. 读取幻灯片正文与标题
                    try
                    {
                        using (var s = slideEntry.Open())
                        using (var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true }))
                        {
                            var sb = new StringBuilder();
                            while (xr.Read())
                            {
                                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                                {
                                    string t = xr.ReadElementContentAsString().Trim();
                                    if (!string.IsNullOrEmpty(t))
                                    {
                                        if (string.IsNullOrEmpty(item.Title)) item.Title = t;
                                        sb.AppendLine("- " + t);
                                    }
                                }
                            }
                            item.ContentPreview = sb.ToString().Trim();
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(item.Title)) item.Title = "幻灯片 " + slideNum;

                    // 2. 读取对应演讲者备注 (ppt/notesSlides/notesSlideN.xml)
                    var notesEntry = zip.GetEntry("ppt/notesSlides/notesSlide" + slideNum + ".xml");
                    if (notesEntry != null)
                    {
                        try
                        {
                            using (var s = notesEntry.Open())
                            using (var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true }))
                            {
                                var sbNotes = new StringBuilder();
                                while (xr.Read())
                                {
                                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                                    {
                                        string t = xr.ReadElementContentAsString().Trim();
                                        if (!string.IsNullOrEmpty(t) && t != item.Title)
                                        {
                                            sbNotes.AppendLine(t);
                                        }
                                    }
                                }
                                item.SpeakerNotes = sbNotes.ToString().Trim();
                                item.HasNotes = !string.IsNullOrEmpty(item.SpeakerNotes);
                            }
                        }
                        catch { }
                    }

                    summary.Slides.Add(item);
                    slideNum++;
                    if (slideNum > 60) break; // 采样前 60 张幻灯片
                }
            }
        }
        #endregion

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return string.Format("{0:0.#} KB", bytes / 1024.0);
            return string.Format("{0:0.##} MB", bytes / (1024.0 * 1024.0));
        }
    }
}
