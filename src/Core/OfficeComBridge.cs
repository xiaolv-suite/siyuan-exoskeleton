using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class OfficeSelectionResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public string DocumentPath { get; set; }
        public string DocumentTitle { get; set; }
        public string SubLocation { get; set; } // 例如 "Sheet1!B2:D10" 或 "Heading2"
        public string AppType { get; set; }     // "word", "excel", "wps", "et"
        public string AppDisplayName { get; set; }

        public OfficeSelectionResult()
        {
            Success = false;
            Text = "";
            DocumentPath = "";
            DocumentTitle = "";
            SubLocation = "";
            AppType = "";
            AppDisplayName = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 微软 Office 与金山 WPS 运行态 COM 晚绑定桥接器
    /// 1. 0 破坏剪贴板：直接通过 COM 运行态对象直读选区，不注入 Ctrl+C，保护用户剪贴板
    /// 2. 自动提取前台文档绝对物理路径与文件名
    /// 3. Excel 单元格地址深度捕获 (如 Sheet1!B2:D10)
    /// 4. 纯反射晚绑定 (Late-Binding)，无需依赖 Microsoft.Office.Interop，未装 Office 亦绝不报错
    /// </summary>
    public static class OfficeComBridge
    {
        public static OfficeSelectionResult TryGetActiveOfficeSelection(string procName)
        {
            var res = new OfficeSelectionResult();
            if (string.IsNullOrEmpty(procName)) return res;

            string pLower = procName.ToLowerInvariant();

            try
            {
                // 1. 微软 Word
                if (pLower == "winword")
                {
                    return ReadWordSelection("Word.Application", "Microsoft Word", "word");
                }

                // 2. 微软 Excel
                if (pLower == "excel")
                {
                    return ReadExcelSelection("Excel.Application", "Microsoft Excel", "excel");
                }

                // 3. 金山 WPS 文字
                if (pLower == "wps")
                {
                    var wpsRes = ReadWordSelection("wps.Application", "WPS 文字", "wps");
                    if (!wpsRes.Success) wpsRes = ReadWordSelection("kwps.Application", "WPS 文字", "wps");
                    return wpsRes;
                }

                // 4. 金山 WPS 表格
                if (pLower == "et")
                {
                    var etRes = ReadExcelSelection("et.Application", "WPS 表格", "et");
                    if (!etRes.Success) etRes = ReadExcelSelection("ket.Application", "WPS 表格", "et");
                    return etRes;
                }

                // 5. 微软 PowerPoint
                if (pLower == "powerpnt")
                {
                    return ReadPowerPointSelection("PowerPoint.Application", "Microsoft PowerPoint", "ppt");
                }

                // 6. 金山 WPS 演示
                if (pLower == "wpp")
                {
                    var wppRes = ReadPowerPointSelection("wpp.Application", "WPS 演示", "ppt");
                    if (!wppRes.Success) wppRes = ReadPowerPointSelection("kwpp.Application", "WPS 演示", "ppt");
                    return wppRes;
                }
            }
            catch { }

            return res;
        }

        private static OfficeSelectionResult ReadWordSelection(string progId, string displayName, string appType)
        {
            var res = new OfficeSelectionResult { AppType = appType, AppDisplayName = displayName };
            try
            {
                object app = null;
                try { app = Marshal.GetActiveObject(progId); } catch { return res; }
                if (app == null) return res;

                Type tApp = app.GetType();

                // 读取活动文档路径
                try
                {
                    object doc = tApp.InvokeMember("ActiveDocument", BindingFlags.GetProperty, null, app, null);
                    if (doc != null)
                    {
                        Type tDoc = doc.GetType();
                        res.DocumentPath = (string)tDoc.InvokeMember("FullName", BindingFlags.GetProperty, null, doc, null);
                        res.DocumentTitle = (string)tDoc.InvokeMember("Name", BindingFlags.GetProperty, null, doc, null);
                    }
                }
                catch { }

                // 读取当前划选文本
                try
                {
                    object sel = tApp.InvokeMember("Selection", BindingFlags.GetProperty, null, app, null);
                    if (sel != null)
                    {
                        Type tSel = sel.GetType();
                        string rawText = (string)tSel.InvokeMember("Text", BindingFlags.GetProperty, null, sel, null);
                        if (!string.IsNullOrEmpty(rawText))
                        {
                            // 规整 Word 尾部多余回车字符
                            res.Text = rawText.Trim('\r', '\n', '\a', '\0', ' ', '\t');
                            res.Success = !string.IsNullOrEmpty(res.Text);
                        }
                    }
                }
                catch { }
            }
            catch { }

            return res;
        }

        private static OfficeSelectionResult ReadExcelSelection(string progId, string displayName, string appType)
        {
            var res = new OfficeSelectionResult { AppType = appType, AppDisplayName = displayName };
            try
            {
                object app = null;
                try { app = Marshal.GetActiveObject(progId); } catch { return res; }
                if (app == null) return res;

                Type tApp = app.GetType();

                // 读取活动工作簿路径
                try
                {
                    object wb = tApp.InvokeMember("ActiveWorkbook", BindingFlags.GetProperty, null, app, null);
                    if (wb != null)
                    {
                        Type tWb = wb.GetType();
                        res.DocumentPath = (string)tWb.InvokeMember("FullName", BindingFlags.GetProperty, null, wb, null);
                        res.DocumentTitle = (string)tWb.InvokeMember("Name", BindingFlags.GetProperty, null, wb, null);
                    }
                }
                catch { }

                // 读取当前活动工作表与单元格选区地址
                try
                {
                    object sel = tApp.InvokeMember("Selection", BindingFlags.GetProperty, null, app, null);
                    if (sel != null)
                    {
                        Type tSel = sel.GetType();

                        // 读取 Address
                        try
                        {
                            string addr = (string)tSel.InvokeMember("Address", BindingFlags.GetProperty, null, sel, new object[] { false, false, 1, true });
                            res.SubLocation = addr ?? "";
                        }
                        catch { }

                        // 尝试读取选区文本 (如果是单个单元格或区域，尝试转换为 TSV/Markdown)
                        try
                        {
                            object valObj = tSel.InvokeMember("Value2", BindingFlags.GetProperty, null, sel, null);
                            if (valObj is object[,])
                            {
                                var arr = (object[,])valObj;
                                int rows = arr.GetLength(0);
                                int cols = arr.GetLength(1);
                                var sb = new System.Text.StringBuilder();

                                for (int r = 1; r <= rows; r++)
                                {
                                    for (int c = 1; c <= cols; c++)
                                    {
                                        string cellVal = arr[r, c] != null ? arr[r, c].ToString() : "";
                                        sb.Append(cellVal);
                                        if (c < cols) sb.Append("\t");
                                    }
                                    if (r < rows) sb.AppendLine();
                                }
                                res.Text = sb.ToString().Trim();
                                res.Success = !string.IsNullOrEmpty(res.Text);
                            }
                            else if (valObj != null)
                            {
                                res.Text = valObj.ToString().Trim();
                                res.Success = !string.IsNullOrEmpty(res.Text);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch { }

            return res;
        }

        private static OfficeSelectionResult ReadPowerPointSelection(string progId, string displayName, string appType)
        {
            var res = new OfficeSelectionResult { AppType = appType, AppDisplayName = displayName };
            try
            {
                object app = null;
                try { app = Marshal.GetActiveObject(progId); } catch { return res; }
                if (app == null) return res;

                Type tApp = app.GetType();

                // 读取活动演示文稿路径
                try
                {
                    object pres = tApp.InvokeMember("ActivePresentation", BindingFlags.GetProperty, null, app, null);
                    if (pres != null)
                    {
                        Type tPres = pres.GetType();
                        res.DocumentPath = (string)tPres.InvokeMember("FullName", BindingFlags.GetProperty, null, pres, null);
                        res.DocumentTitle = (string)tPres.InvokeMember("Name", BindingFlags.GetProperty, null, pres, null);
                    }
                }
                catch { }

                // 读取当前活动窗口与幻灯片索引
                try
                {
                    object actWin = tApp.InvokeMember("ActiveWindow", BindingFlags.GetProperty, null, app, null);
                    if (actWin != null)
                    {
                        Type tWin = actWin.GetType();

                        // 提取当前所在幻灯片页码
                        try
                        {
                            object view = tWin.InvokeMember("View", BindingFlags.GetProperty, null, actWin, null);
                            if (view != null)
                            {
                                object slide = view.GetType().InvokeMember("Slide", BindingFlags.GetProperty, null, view, null);
                                if (slide != null)
                                {
                                    object idx = slide.GetType().InvokeMember("SlideIndex", BindingFlags.GetProperty, null, slide, null);
                                    if (idx != null)
                                    {
                                        res.SubLocation = "Slide" + idx.ToString();
                                    }
                                }
                            }
                        }
                        catch { }

                        // 提取划选文字
                        try
                        {
                            object sel = tWin.InvokeMember("Selection", BindingFlags.GetProperty, null, actWin, null);
                            if (sel != null)
                            {
                                object tr = sel.GetType().InvokeMember("TextRange", BindingFlags.GetProperty, null, sel, null);
                                if (tr != null)
                                {
                                    string rawText = (string)tr.GetType().InvokeMember("Text", BindingFlags.GetProperty, null, tr, null);
                                    if (!string.IsNullOrEmpty(rawText))
                                    {
                                        res.Text = rawText.Trim('\r', '\n', '\a', '\0', ' ', '\t');
                                        res.Success = !string.IsNullOrEmpty(res.Text);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(res.DocumentPath) && string.IsNullOrEmpty(res.Text))
                {
                    // 若无划选文字但处于演示文稿中，依然标定文档成功
                    res.Success = true;
                }
            }
            catch { }

            return res;
        }
    }
}
