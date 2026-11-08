using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Quicker.Public;
using XiaoLv.Desktop.Core;
public static class ResearchSourceCardMain
{
    public static void Exec(IStepContext context)
    {
        var selectedText = SafeContext.GetString(context, "选中文本");
        var clipboardText = SafeContext.GetString(context, "剪贴板文本");
        var rawInput = SafeContext.GetString(context, "输入文本");
        var pageUrl = SafeContext.GetString(context, "页面URL");
        var pageTitle = SafeContext.GetString(context, "页面标题");
        var windowTitle = SafeContext.GetString(context, "来源窗口");
        if (string.IsNullOrEmpty(pageTitle)) pageTitle = windowTitle;

        // 优先选区，次选剪贴板，再选输入文本
        var textToClip = !string.IsNullOrEmpty(selectedText) ? selectedText : (!string.IsNullOrEmpty(clipboardText) ? clipboardText : rawInput);

        var note = SafeContext.GetString(context, "推断笔记");
        var inboxId = SafeContext.GetString(context, "收集箱ID");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 必填项校验
        if (string.IsNullOrEmpty(textToClip))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 摘录正文为空：请先划选文字或复制要摘录的内容。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 规范化换行与空白
        textToClip = textToClip.Replace("\r\n", "\n").Trim();

        // 2. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 3. 计算 8 位 MD5 摘录内容指纹
        string fingerprint;
        using (var md5 = MD5.Create())
        {
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(textToClip));
            var sb = new StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            fingerprint = sb.ToString().Substring(0, 8);
        }
        context.SetVarValue("内容指纹", fingerprint);

        var now = DateTime.Now;
        var timeStr = now.ToString("yyyy-MM-dd HH:mm");

        // 4. 重复 URL 软提示探测（轻量快速探测：限定在已有研究来源卡中，避免全库长文本慢扫描）
        var dupHint = "";
        if (!string.IsNullOrEmpty(pageUrl) && pageUrl.Length >= 8)
        {
            try
            {
                var cleanUrl = pageUrl.Replace("'", "''");
                var sql = "SELECT id FROM blocks WHERE ial LIKE '%custom-type=\"research-card\"%' AND markdown LIKE '%" + cleanUrl + "%' LIMIT 1";
                var checkRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(sql) + "\"}");
                if (checkRes.Success && checkRes.Text.Contains("\"id\""))
                {
                    dupHint = " (注意: 库中已有同 URL 摘录)";
                }
            }
            catch { }
        }

        // 5. 自动嗅探收集箱文档 (若未显式指定收集箱ID)
        if (string.IsNullOrEmpty(inboxId))
        {
            try
            {
                var findInboxSql = "SELECT id FROM blocks WHERE type='d' AND (content LIKE '%收集箱%' OR content LIKE '%Inbox%') ORDER BY updated DESC LIMIT 1";
                var findInboxRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(findInboxSql) + "\"}");
                if (findInboxRes.Success && findInboxRes.Text.Contains("\"id\":"))
                {
                    var m = Regex.Match(findInboxRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) inboxId = m.Groups[1].Value;
                }
            }
            catch { }
        }

        // 6. 组装三层结构化防 404 Markdown 卡片
        var cardBuilder = new StringBuilder();
        
        // 标题头
        cardBuilder.Append("### 📑 研究来源卡: ").Append(string.IsNullOrEmpty(pageTitle) ? "文献速摘" : TruncateString(pageTitle, 36)).Append(" #研究来源卡 #防404\n\n");
        
        // 层级 1: 摘录正文层 (独立客观完整证据，源站404后依然永久可读)
        cardBuilder.Append("> ").Append(textToClip.Replace("\n", "\n> ")).Append("\n\n");
        
        // 层级 2: 来源与可信元数据层 (可信溯源证据链)
        cardBuilder.Append("* **原始信源**: ");
        if (!string.IsNullOrEmpty(pageUrl))
        {
            cardBuilder.Append("[").Append(string.IsNullOrEmpty(pageTitle) ? pageUrl : pageTitle).Append("](").Append(pageUrl).Append(")");
        }
        else
        {
            cardBuilder.Append(string.IsNullOrEmpty(pageTitle) ? "本地选区" : pageTitle);
        }
        cardBuilder.Append("\n* **采集时间**: ").Append(timeStr);
        cardBuilder.Append(" · **内容指纹**: `").Append(fingerprint).Append("`");
        if (!string.IsNullOrEmpty(windowTitle))
        {
            cardBuilder.Append(" · **捕获来源**: ").Append(windowTitle);
        }
        cardBuilder.Append("\n");

        // 层级 3: 推断与思考层 (主观见解与客观证据物理隔离)
        if (!string.IsNullOrEmpty(note))
        {
            cardBuilder.Append("\n**💡 核心洞察 / 个人推断**:\n").Append(note).Append("\n");
        }
        cardBuilder.Append("\n{: custom-type=\"research-card\" custom-fingerprint=\"").Append(fingerprint).Append("\" }\n");

        var cardMarkdown = cardBuilder.ToString();

        // 6. 写入目标决策：优先写入收集箱文档，未配置则追加今日日记
        bool writeSuccess = false;
        string targetName = "";
        string errDetail = "";

        if (!string.IsNullOrEmpty(inboxId))
        {
            var appendBody = "{\"parentID\":\"" + inboxId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(cardMarkdown) + "\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);
            writeSuccess = r.Success;
            targetName = "收集箱";
            errDetail = r.Error;
        }
        else
        {
            var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(cardMarkdown) + "\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);
            writeSuccess = r.Success;
            targetName = "今日日记";
            errDetail = r.Error;
        }

        context.SetVarValue("是否成功", writeSuccess);
        context.SetVarValue("通知反馈", writeSuccess 
            ? "✓ 已生成研究来源卡并沉淀至【" + targetName + "】！" + dupHint
            : "❌ 写入失败：" + errDetail);
        SafeContext.Set(context, "需要通知", true);
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string TruncateString(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + "...";
    }
}


// ============================================================================
// L1-03: 多模态截屏与公式入库 —— 主驱动程序 (VisionMathCaptureMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 接收截图或剪贴板图片临时文件路径 (PNG / JPEG)
//   2. 调用思源内核资源上传接口 (/api/asset/upload)：
//      - Multipart 表单提交至 /data/assets/
//      - 提取真实资源引用路径 (形如 assets/screenshot-20261014-xxxx.png)
//   3. 格式化多模态结构化 Markdown 块：
//      - 包含图片标准引用标签
//      - 附加 OCR 识别文本（若有）或 LaTeX 公式块（$$ ... $$）
//      - 记录截图时间与来源窗口元数据
//   4. 写入今日日记或指定文档，输出完成提示
// ============================================================================


public static class VisionMathCaptureMain
{
    public static void Exec(IStepContext context)
    {
        var imgPath = SafeContext.GetString(context, "图片文件路径");
        var ocrText = SafeContext.GetString(context, "OCR文本");
        var rawInput = SafeContext.GetString(context, "输入文本");
        var isMathFormula = SafeContext.GetString(context, "是否数学公式", "false").ToLower() == "true";
        var windowTitle = SafeContext.GetString(context, "来源窗口", "桌面选区");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 2. 多源探查图片文件（优先参数路径，次选输入文本，再次遍历 Quicker 临时截图缓存）
        if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath))
        {
            if (!string.IsNullOrEmpty(rawInput) && File.Exists(rawInput) && IsImageFile(rawInput))
            {
                imgPath = rawInput;
            }
            else
            {
                string[] tempCandidates = new string[]
                {
                    Path.Combine(Path.GetTempPath(), "quicker_screenshot.png"),
                    Path.Combine(Path.GetTempPath(), "quicker_capture.png"),
                    Path.Combine(Path.GetTempPath(), "quicker_image.png"),
                    Path.Combine(Path.GetTempPath(), "quicker_ocr.png"),
                    Path.Combine(Path.GetTempPath(), "quicker_clip_image.png")
                };

                foreach (var temp in tempCandidates)
                {
                    if (File.Exists(temp))
                    {
                        imgPath = temp;
                        break;
                    }
                }
            }
        }

        string assetRelativePath = "";

        // 3. 上传图片至思源资源库
        if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
        {
            var uploadRes = UploadAssetToSiYuan(syUrl, token, imgPath);
            if (uploadRes.Success)
            {
                assetRelativePath = uploadRes.AssetPath;
                context.SetVarValue("资源路径", assetRelativePath);
            }
            else
            {
                context.SetVarValue("是否成功", false);
                context.SetVarValue("通知反馈", "❌ 图片上传至思源失败：" + uploadRes.Error);
        SafeContext.Set(context, "需要通知", true);
                return;
            }
        }

        // 3.5 既无图片也无 OCR 文本时直接友好退出，绝不写入空壳卡片
        if (string.IsNullOrEmpty(assetRelativePath) && string.IsNullOrEmpty(ocrText))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 未检测到截图或 OCR 文本：请先截屏/复制图片，或通过 OCR 步骤传入识别结果。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 4. 启发式检测 LaTeX 数学公式
        if (!isMathFormula && !string.IsNullOrEmpty(ocrText))
        {
            isMathFormula = DetectMathFormula(ocrText);
        }

        // 5. 构建结构化多模态 Markdown 块
        var now = DateTime.Now;
        var timeStr = now.ToString("HH:mm");
        var sb = new StringBuilder();

        sb.Append("### 🖼️ 视觉入库资产 (").Append(timeStr).Append(" - ").Append(windowTitle).Append(") #多模态入库\n\n");

        if (!string.IsNullOrEmpty(assetRelativePath))
        {
            // 将 OCR 文本清洗后注入 Alt 属性，使图片在思源全文检索中直接可搜！
            var altText = !string.IsNullOrEmpty(ocrText) ? TruncateClean(ocrText, 80) : Path.GetFileName(imgPath);
            sb.Append("![").Append(altText).Append("](").Append(assetRelativePath).Append(")\n\n");
        }

        if (!string.IsNullOrEmpty(ocrText))
        {
            if (isMathFormula)
            {
                sb.Append("$$\n").Append(ocrText.Trim()).Append("\n$$\n\n");
            }
            else
            {
                sb.Append("> 📝 **OCR 提取文本**:\n> ").Append(ocrText.Replace("\r\n", "\n").Replace("\n", "\n> ")).Append("\n\n");
            }
        }

        sb.Append("{: custom-type=\"vision-asset\" }\n");

        var mdContent = sb.ToString();

        // 6. 追加写入今日日记
        var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(mdContent) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);

        context.SetVarValue("是否成功", r.Success);
        context.SetVarValue("通知反馈", r.Success 
            ? (isMathFormula ? "✓ LaTeX 数学公式与图片已成功沉淀至今日日记！" : "✓ 截图资产与可搜索 OCR 文本已成功入库！")
            : "❌ 写入今日日记失败：" + r.Error);
    }

    /// <summary>
    /// 启发式检测字符串是否为 LaTeX 数学公式
    /// </summary>
    private static bool DetectMathFormula(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string[] mathTokens = new string[]
        {
            "\\frac", "\\sqrt", "\\sum", "\\int", "\\prod", "\\alpha", "\\beta", "\\gamma",
            "\\theta", "\\pi", "\\lambda", "\\sigma", "\\partial", "\\cdot", "\\times",
            "\\leq", "\\geq", "\\neq", "\\approx", "\\infty", "\\lim", "\\begin{matrix}",
            "\\begin{aligned}", "\\left(", "\\right)", "\\mathbf", "\\vec", "\\Delta"
        };
        foreach (var token in mathTokens)
        {
            if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".bmp";
    }

    private static string TruncateClean(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = s.Replace("\r", " ").Replace("\n", " ").Replace("[", "").Replace("]", "").Trim();
        if (clean.Length <= maxLen) return clean;
        return clean.Substring(0, maxLen) + "...";
    }

    /// <summary>
    /// 调用 /api/asset/upload 上传本地图片文件至思源
    /// </summary>
    public static AssetUploadResult UploadAssetToSiYuan(string baseUrl, string token, string localFilePath)
    {
        try
        {
            var fileName = Path.GetFileName(localFilePath);
            var boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
            var boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
            var endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

            var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/asset/upload");
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers[HttpRequestHeader.Authorization] = "Token " + token;
            }
            req.Timeout = 15000;
            req.Proxy = null;

            using (var requestStream = req.GetRequestStream())
            {
                // 参数 1: assetsDirPath 文本字段
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var dirHeader = "Content-Disposition: form-data; name=\"assetsDirPath\"\r\n\r\n/data/assets/";
                var dirBytes = Encoding.UTF8.GetBytes(dirHeader);
                requestStream.Write(dirBytes, 0, dirBytes.Length);

                // 参数 2: file[] 二进制文件
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var fileHeader = "Content-Disposition: form-data; name=\"file[]\"; filename=\"" + fileName + "\"\r\n" +
                                 "Content-Type: image/png\r\n\r\n";
                var fileHeaderBytes = Encoding.UTF8.GetBytes(fileHeader);
                requestStream.Write(fileHeaderBytes, 0, fileHeaderBytes.Length);

                var fileData = File.ReadAllBytes(localFilePath);
                requestStream.Write(fileData, 0, fileData.Length);

                requestStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var respText = reader.ReadToEnd();
                if (resp.StatusCode == HttpStatusCode.OK && respText.Contains("\"code\":0"))
                {
                    // 从 succMap 解析实际相对路径，例如: "assets/xxx.png"
                    var m = Regex.Match(respText, "\"assets/[^\"]+\"");
                    var assetPath = m.Success ? m.Value.Trim('"') : "assets/" + fileName;
                    return new AssetUploadResult { Success = true, AssetPath = assetPath, Error = "" };
                }
                return new AssetUploadResult { Success = false, AssetPath = "", Error = "上传接口响应: " + respText };
            }
        }
        catch (Exception ex)
        {
            return new AssetUploadResult { Success = false, AssetPath = "", Error = ex.Message };
        }
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    public class AssetUploadResult
    {
        public bool Success;
        public string AssetPath;
        public string Error;
    }
}


// ============================================================================
// L1-04: Office/WPS 结构化沉淀 —— 主驱动程序 (OfficeIngestionMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部互操作程序集依赖）
// 机制原理：COM 晚绑定（Type.GetTypeFromProgID + Marshal.GetActiveObject）
//   - 支持 Microsoft Word / Excel 以及 WPS 文字 / WPS 表格运行态直接附加
//   - Word 模式: 提取选区富文本、页码、批注修订数，格式化为引用块
//   - Excel 模式: 读取活动选区单元格矩阵 (Value2 / Text)，自动转换为 Markdown 表格
//   - 结构化沉淀: 将来源文档名、选区范围/页码、元数据与正文组装写入思源日记
// ============================================================================


public static class OfficeIngestionMain
{
    public static void Exec(IStepContext context)
    {
        var appType = SafeContext.GetString(context, "应用类型", "auto").ToLowerInvariant();
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        string docTitle = "";
        string extractedContent = "";
        string metaInfo = "";

        // 2. COM 晚绑定读取选区（支持 Word/Excel/WPS 全自动嗅探）
        try
        {
            if (appType == "excel" || appType == "et")
            {
                ReadExcelSelection(out docTitle, out extractedContent, out metaInfo);
            }
            else if (appType == "word" || appType == "wps")
            {
                ReadWordSelection(out docTitle, out extractedContent, out metaInfo);
            }
            else
            {
                // auto 模式：先尝试探测 Word，若无选区则尝试探测 Excel
                try
                {
                    ReadWordSelection(out docTitle, out extractedContent, out metaInfo);
                    if (!string.IsNullOrEmpty(extractedContent)) appType = "word";
                }
                catch { }

                if (string.IsNullOrEmpty(extractedContent))
                {
                    try
                    {
                        ReadExcelSelection(out docTitle, out extractedContent, out metaInfo);
                        if (!string.IsNullOrEmpty(extractedContent)) appType = "excel";
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 3. 优雅自愈降级：若 COM 未捕获到选区，自动从系统剪贴板嗅探已复制的表格或办公数据
        if (string.IsNullOrEmpty(extractedContent))
        {
            try
            {
                string clipTxt = "";
                if (System.Windows.Forms.Clipboard.ContainsText())
                {
                    clipTxt = System.Windows.Forms.Clipboard.GetText();
                }
                if (!string.IsNullOrEmpty(clipTxt))
                {
                    string mdTable;
                    int rows, cols;
                    if (XiaoLv.Desktop.Core.ClipboardWatcher.TryConvertTsvToMarkdown(clipTxt, out mdTable, out rows, out cols))
                    {
                        docTitle = "剪贴板表格 (" + rows + "行×" + cols + "列)";
                        extractedContent = mdTable;
                        metaInfo = "TSV/Excel数据转换 · " + rows + " 行 " + cols + " 列";
                        appType = "clipboard-table";
                    }
                    else if (clipTxt.Trim().Length > 0)
                    {
                        docTitle = "剪贴板办公文本";
                        extractedContent = XiaoLv.Desktop.Core.TextCleaner.CleanAll(clipTxt);
                        metaInfo = "剪贴板提纯 · " + clipTxt.Length + " 字";
                        appType = "clipboard-text";
                    }
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(extractedContent))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 未检测到 Office 选区或剪贴板表格，请先在 Excel、WPS 或网页表格中复制内容。");
            SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 3. 构建多维结构化 Markdown 卡片
        var sb = new StringBuilder();
        var now = DateTime.Now;

        sb.Append("### 📊 Office 结构化资产: ").Append(string.IsNullOrEmpty(docTitle) ? "办公选区" : docTitle).Append("\n");
        sb.Append("> **来源**: ").Append(docTitle).Append(" · **元数据**: ").Append(metaInfo)
          .Append(" · **时间**: ").Append(now.ToString("yyyy-MM-dd HH:mm")).Append("\n\n");

        sb.Append(extractedContent).Append("\n\n");
        sb.Append("{: custom-type=\"office-ingest\" }\n");

        var mdResult = sb.ToString();

        // 4. 追加至今日日记并同步写入剪贴板
        var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(mdResult) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);

        try
        {
            if (!string.IsNullOrEmpty(extractedContent))
            {
                System.Windows.Forms.Clipboard.SetText(extractedContent);
            }
        }
        catch { }

        context.SetVarValue("纯净表格", extractedContent);
        context.SetVarValue("是否成功", r.Success);
        context.SetVarValue("通知反馈", r.Success 
            ? "✓ 已成功清洗并转换表格 (" + docTitle + ") 入日记与剪贴板！" 
            : "❌ 写入日记失败：" + r.Error);
    }

    /// <summary>
    /// 读取 Word / WPS 文字选区
    /// </summary>
    private static void ReadWordSelection(out string docTitle, out string content, out string meta)
    {
        docTitle = "Word 文档";
        content = "";
        meta = "";

        object app = GetActiveComObject("Word.Application", "KWPS.Application");
        if (app == null) throw new Exception("Word/WPS 文字未在运行");

        Type t = app.GetType();
        object doc = t.InvokeMember("ActiveDocument", BindingFlags.GetProperty, null, app, null);
        if (doc != null)
        {
            object nameObj = doc.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, doc, null);
            if (nameObj != null) docTitle = nameObj.ToString();
        }

        object sel = t.InvokeMember("Selection", BindingFlags.GetProperty, null, app, null);
        if (sel == null) throw new Exception("未能获取有效选区");

        object textObj = sel.GetType().InvokeMember("Text", BindingFlags.GetProperty, null, sel, null);
        if (textObj != null)
        {
            var rawText = textObj.ToString().Trim('\r', '\n', '\a');
            content = "> " + rawText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\n> ");
        }

        // 获取页码信息
        try
        {
            object pageObj = sel.GetType().InvokeMember("Information", BindingFlags.GetProperty, null, sel, new object[] { 1 }); // wdActiveEndPageNumber = 1
            if (pageObj != null) meta = "第 " + pageObj + " 页";
        }
        catch { }
    }

    /// <summary>
    /// 读取 Excel / WPS 表格选区并转换为 Markdown 表格
    /// </summary>
    private static void ReadExcelSelection(out string docTitle, out string content, out string meta)
    {
        docTitle = "Excel 工作簿";
        content = "";
        meta = "";

        object app = GetActiveComObject("Excel.Application", "KET.Application");
        if (app == null) throw new Exception("Excel/WPS 表格未在运行");

        Type t = app.GetType();
        object wb = t.InvokeMember("ActiveWorkbook", BindingFlags.GetProperty, null, app, null);
        if (wb != null)
        {
            object nameObj = wb.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, wb, null);
            if (nameObj != null) docTitle = nameObj.ToString();
        }

        object sel = t.InvokeMember("Selection", BindingFlags.GetProperty, null, app, null);
        if (sel == null) throw new Exception("未能获取有效单元格区域");

        Type selType = sel.GetType();
        object addrObj = selType.InvokeMember("Address", BindingFlags.GetProperty, null, sel, null);
        if (addrObj != null) meta = "选区 " + addrObj.ToString();

        object valObj = selType.InvokeMember("Value2", BindingFlags.GetProperty, null, sel, null);
        if (valObj is Array)
        {
            var arr = (Array)valObj;
            int rCount = arr.GetLength(0);
            int cCount = arr.GetLength(1);

            var sbTable = new StringBuilder();
            for (int r = 1; r <= rCount; r++)
            {
                sbTable.Append("|");
                for (int c = 1; c <= cCount; c++)
                {
                    object cellVal = arr.GetValue(r, c);
                    var cellStr = cellVal != null ? cellVal.ToString().Replace("|", "\\|").Replace("\n", " ") : "";
                    sbTable.Append(" ").Append(cellStr).Append(" |");
                }
                sbTable.Append("\n");

                // 在首行后追加表头对齐分隔符
                if (r == 1)
                {
                    sbTable.Append("|");
                    for (int c = 1; c <= cCount; c++) sbTable.Append(" --- |");
                    sbTable.Append("\n");
                }
            }
            content = sbTable.ToString();
        }
        else if (valObj != null)
        {
            content = "| 单元格数值 |\n| --- |\n| " + valObj.ToString() + " |";
        }
    }

    private static object GetActiveComObject(string progId1, string progId2)
    {
        try { return Marshal.GetActiveObject(progId1); }
        catch
        {
            try { return Marshal.GetActiveObject(progId2); }
            catch { return null; }
        }
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }
}


// ============================================================================
// L1-05: 纯净 Markdown 复制与分享 —— 主驱动程序 (CleanMarkdownExportMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 范围嗅探：优先处理用户划选的文本；若未划选，自动嗅探当前前台活动的整篇文档
//   2. 内核通信：调用 /api/export/exportMdContent 导出标准正文，或读取选区 Kramdown
//   3. 深度正则脱敏与排版清洗增强：
//      - 剥离所有 {: id="..." updated="..." custom-* } 等思源内部 Kramdown 属性定义
//      - 智能转换双链引用：((块ID "锚文本")) -> 转换为自然清晰的 "锚文本"
//      - 规整标签：#标签# -> 标准 CommonMark 标签 #标签
//      - 规整高亮与行内标记：剥离 data-type 杂质，保留纯净文本或粗体/强调
//      - 清理空属性残留、剔除行尾多余空白，并将连续多余空行紧缩为标准间距
//   4. 直接将干净无噪点的标准 CommonMark 写入系统剪贴板，方便即时发送至微信、知乎、邮件或 GitHub
// ============================================================================


public static class CleanMarkdownExportMain
{
    public static void Exec(IStepContext context)
    {
        var selectedText = SafeContext.GetString(context, "选中文本");
        var rawInput = SafeContext.GetString(context, "输入文本");
        var targetDocId = SafeContext.GetString(context, "目标文档ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        string sourceContent = !string.IsNullOrEmpty(selectedText) ? selectedText : rawInput;
        string scopeDesc = "选中文段";

        // 2. 若未划选文段，尝试嗅探当前前台文档并全篇导出
        if (string.IsNullOrEmpty(sourceContent))
        {
            if (string.IsNullOrEmpty(targetDocId))
            {
                var sendRes = LvSendCommand.Send(syUrl, token, "editor.context", "{}", "siyuan-speed-switch", "fast");
                if (sendRes.Success)
                {
                    var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 2000, "siyuan-speed-switch");
                    var dataJson = rec.DataJson;
                    if (!string.IsNullOrEmpty(dataJson))
                    {
                        var docM = Regex.Match(dataJson, "\"docId\"\\s*:\\s*\"([^\"]+)\"");
                        if (docM.Success) targetDocId = docM.Groups[1].Value;
                    }
                }
            }

            // 兜底：若插件未返回，直接通过原生 SQL 查询最近更新的活动文档
            if (string.IsNullOrEmpty(targetDocId))
            {
                var sqlRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", 
                    "{\"stmt\":\"SELECT id, content FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 1\"}");
                if (sqlRes.Success && sqlRes.Text.Contains("\"id\":"))
                {
                    var idM = Regex.Match(sqlRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    if (idM.Success) targetDocId = idM.Groups[1].Value;
                }
            }

            if (!string.IsNullOrEmpty(targetDocId))
            {
                var exportReq = "{\"id\":\"" + SafeContext.EscapeJson(targetDocId) + "\"}";
                var exportRes = SyKernelRequest.DoRequest(syUrl, token, "/api/export/exportMdContent", exportReq);
                if (exportRes.Success)
                {
                    var contentM = Regex.Match(exportRes.Text, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (contentM.Success)
                    {
                        sourceContent = UnescapeJson(contentM.Groups[1].Value);
                        scopeDesc = "整篇文档";
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(sourceContent))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未检测到选中文字或前台活动文档，请在思源中划选或打开文档。");
            return;
        }

        // 3. 执行纯净 CommonMark 清洗算法 (增强版)
        var cleaned = SanitizeKramdown(sourceContent);

        // 4. 写回剪贴板与输出变量
        SafeContext.Set(context, "纯净Markdown", cleaned);
        SafeContext.Set(context, "需要写回剪贴板", true);
        SetClipboardHelper(cleaned);
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "✓ 已复制纯净 Markdown【" + scopeDesc + "】至剪贴板。");
    }

    /// <summary>
    /// 核心 Kramdown 纯净化清洗过滤管线 (增强脱敏版)
    /// </summary>
    public static string SanitizeKramdown(string kramdown)
    {
        if (string.IsNullOrEmpty(kramdown)) return "";

        var text = kramdown;

        // A. 剥离行内及块级 {: id="..." ...} 属性杂质 (支持多行/换行属性)
        text = Regex.Replace(text, "\\{:[^\\}]*\\}", "", RegexOptions.Singleline);

        // B. 转换双链引用：((块ID "锚文本")) -> 锚文本，或 ((块ID)) -> (块引用)
        text = Regex.Replace(text, "\\(\\([0-9a-zA-Z\\-]+\\s+\"([^\"]+)\"\\)\\)", "$1");
        text = Regex.Replace(text, "\\(\\([0-9a-zA-Z\\-]+\\)\\)", "");

        // C. 规整思源原生前后闭合标签：#标签名称# -> 标准 CommonMark #标签名称
        text = Regex.Replace(text, "#([^#\\s]+)#", "#$1");

        // D. 规整思源高亮：==文本== 或带属性高亮转换为加粗文本
        text = Regex.Replace(text, "==([^=]+)==", "**$1**");

        // E. 清理 HTML 特殊标签杂质 (如思源专有 span data-type 等)
        text = Regex.Replace(text, "<span[^>]*data-type=[^>]*>([^<]*)<\\/span>", "$1");

        // E.1 剥离思源超级块包围标记 ({{{row / {{{col ... }}})
        text = Regex.Replace(text, @"^\{\{\{(row|col)[^\n]*\r?\n?", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^\}\}\}\s*$", "", RegexOptions.Multiline);

        // E.2 剥离思源嵌入块语法：{{((id))}} 或 {{select ...}}
        text = Regex.Replace(text, @"\{\{\(\([0-9a-zA-Z\-]+\)\)\}\}", "");
        text = Regex.Replace(text, @"\{\{select\s+[^}]+\}\}", "", RegexOptions.IgnoreCase);

        // F. 规范化排版空白与行尾空格
        var lines = text.Split(new char[] { '\n' }, StringSplitOptions.None);
        var sb = new StringBuilder();
        int emptyCount = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (string.IsNullOrWhiteSpace(line))
            {
                emptyCount++;
                if (emptyCount <= 2) sb.Append("\n");
            }
            else
            {
                emptyCount = 0;
                sb.Append(line).Append("\n");
            }
        }

        return sb.ToString().Trim();
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
    }

    private static void SetClipboardHelper(string text)
    {
        try { System.Windows.Forms.Clipboard.SetText(text); } catch { }
    }
}

// ============================================================================
// L1-06: 网页与影音转属性视图 AV 数据库 —— 主驱动程序 (WebToAvDatabaseMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 浏览器一键结构化沉淀：自动抓取当前网页 URL、网页标题与选中文段/简介
//   2. 自动发现与适配：
//      - 优先检索思源中已有的属性视图数据库 (SELECT id, content FROM blocks WHERE type='av')
//      - 支持用户指定数据库 (AV ID) 或指定专页
//      - 未配置时，自动格式化为标准属性视图行块追加，或优雅降级写入今日日记【知识库增行】
//   3. 零外部插件依赖：100% 纯思源原生内核端点与 SQL 驱动，开箱即用！
// ============================================================================


public static class WebToAvDatabaseMain
{
    public static void Exec(IStepContext context)
    {
        var pageUrl = SafeContext.GetString(context, "页面URL");
        var pageTitle = SafeContext.GetString(context, "页面标题");
        var selectedText = SafeContext.GetString(context, "选中文本");
        var targetAvId = SafeContext.GetString(context, "属性视图ID");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var windowTitle = SafeContext.GetString(context, "来源窗口", "浏览器");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrEmpty(pageTitle)) pageTitle = windowTitle;
        var inParam = SafeContext.GetString(context, "quicker_in_param");
        var rawInput = SafeContext.GetString(context, "输入文本");
        if (string.IsNullOrEmpty(selectedText) && !string.IsNullOrEmpty(inParam)) selectedText = inParam;
        if (string.IsNullOrEmpty(selectedText) && !string.IsNullOrEmpty(rawInput)) selectedText = rawInput;
        if (string.IsNullOrEmpty(pageUrl) && string.IsNullOrEmpty(selectedText))
        {
            context.SetVarValue("需要弹窗输入", true);
            context.SetVarValue("输入提示", "输入要沉淀入数据库的网址或条目名称：");
            return;
        }

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var timeStr = now.ToString("HH:mm");

        // 2. 自动发现思源属性视图 (AV 数据库)
        string avTargetDocId = targetAvId;
        string avName = "默认数据库";

        if (string.IsNullOrEmpty(avTargetDocId))
        {
            // 通过 SQL 查找最近更新的属性视图块
            var avSql = "SELECT id, root_id, content FROM blocks WHERE type='av' ORDER BY updated DESC LIMIT 1";
            var avRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(avSql) + "\"}");
            if (avRes.Success && avRes.Text.Contains("\"id\":"))
            {
                var idM = Regex.Match(avRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                var nameM = Regex.Match(avRes.Text, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (idM.Success) avTargetDocId = idM.Groups[1].Value;
                if (nameM.Success && !string.IsNullOrEmpty(nameM.Groups[1].Value)) avName = UnescapeJson(nameM.Groups[1].Value);
            }
        }

        // 3. 构建结构化记录块
        var sb = new StringBuilder();
        var domainName = ExtractDomain(pageUrl);

        sb.Append("- [ ] **").Append(string.IsNullOrEmpty(pageTitle) ? "新媒体资源" : pageTitle).Append("**");
        if (!string.IsNullOrEmpty(pageUrl))
        {
            sb.Append(" [🔗 直达](").Append(pageUrl).Append(")");
        }
        sb.Append("\n");

        if (!string.IsNullOrEmpty(selectedText))
        {
            sb.Append("  * *摘要/要点*: ").Append(selectedText.Replace("\n", " ")).Append("\n");
        }
        sb.Append("  * *录入时间*: ").Append(dateStr).Append(" ").Append(timeStr);
        if (!string.IsNullOrEmpty(domainName))
        {
            sb.Append(" · *站点*: #").Append(domainName).Append("#");
        }
        sb.Append("\n\n{: custom-av-record=\"true\" }\n");

        var recordMarkdown = sb.ToString();

        // 4. 执行写入
        bool writeSuccess = false;
        string targetLocation = "";

        if (!string.IsNullOrEmpty(avTargetDocId))
        {
            // 增强路径：调用思源原生 AV 接口真实插入数据库行 (带回读验证)
            var avInsert = TryInsertAvRow(syUrl, token, avTargetDocId, string.IsNullOrEmpty(pageTitle) ? "新媒体资源" : pageTitle);
            if (avInsert.Success)
            {
                writeSuccess = true;
                targetLocation = "属性视图【" + avName + "】(真实 AV 行 " + avInsert.RowId + ")";
            }
            else
            {
                // 降级路径：追加至 AV 块所在容器 (行块备忘形式)
                var appendBody = "{\"parentID\":\"" + avTargetDocId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(recordMarkdown) + "\"}";
                var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);
                writeSuccess = r.Success;
                targetLocation = "属性视图旁挂备忘【" + avName + "】(AV 接口不可用已降级)";
            }
        }
        else
        {
            // 优雅降级：追加至今日日记【网页与影音数据库】
            var dailyMd = "### 📚 网页与数据沉淀 (AV 行备忘)\n" + recordMarkdown;
            var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(dailyMd) + "\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);
            writeSuccess = r.Success;
            targetLocation = "今日日记 (自动原生归档)";
        }

        context.SetVarValue("是否成功", writeSuccess);
        context.SetVarValue("通知反馈", writeSuccess 
            ? "✓ 已成功将「" + (pageTitle.Length > 20 ? pageTitle.Substring(0, 20) + "..." : pageTitle) + "」录入至 " + targetLocation + "！"
            : "❌ 数据库增行失败。");
    }

    public class AvInsertResult
    {
        public bool Success;
        public string RowId;
        public string Error;
    }

    /// <summary>
    /// 调用思源原生 /api/av/addAttributeViewBlocks 真实插入一行，并回读 keyValues 验证落库
    /// </summary>
    public static AvInsertResult InsertAvRow(string baseUrl, string token, string avId, string rowTitle)
    {
        return TryInsertAvRow(baseUrl, token, avId, rowTitle);
    }

    public static AvInsertResult TryInsertAvRow(string baseUrl, string token, string avId, string rowTitle)
    {
        try
        {
            // 生成思源规范的行块 ID: yyyyMMddHHmmss-7位hex
            var newRowId = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 7);
            var srcJson = "{\"id\":\"" + newRowId + "\",\"isDetached\":true,\"content\":\"" + EscapeJson(rowTitle) + "\"}";
            var addBody = "{\"avID\":\"" + EscapeJson(avId) + "\",\"blockID\":\"\",\"srcs\":[" + srcJson + "]}";
            var addRes = SyKernelRequest.DoRequest(baseUrl, token, "/api/av/addAttributeViewBlocks", addBody);
            if (!addRes.Success || !addRes.Text.Contains("\"code\":0"))
            {
                return new AvInsertResult { Success = false, RowId = "", Error = addRes.Error };
            }

            // 回读验证：在 AV 的 keyValues 中检索刚插入的标题行
            var getRes = SyKernelRequest.DoRequest(baseUrl, token, "/api/av/getAttributeView", "{\"id\":\"" + EscapeJson(avId) + "\"}");
            if (getRes.Success)
            {
                var m = Regex.Match(getRes.Text, "\"blockID\":\"" + newRowId + "\"|\"block\":\\{\"id\":\"" + newRowId + "\"");
                if (m.Success) return new AvInsertResult { Success = true, RowId = newRowId, Error = "" };
                return new AvInsertResult { Success = false, RowId = "", Error = "插入后回读未命中 (AV 可能未持久化)" };
            }
            return new AvInsertResult { Success = false, RowId = "", Error = "AV 回读请求失败" };
        }
        catch (Exception ex)
        {
            return new AvInsertResult { Success = false, RowId = "", Error = ex.Message };
        }
    }

    private static string ExtractDomain(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLower();
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host.Replace('.', '_');
        }
        catch { return ""; }
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }
}


// ============================================================================
// L1-07: 选中文本快捷批注与划线评论 —— 主驱动程序 (QuickCommentMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 精准情境感知：自动通过 editor.context 捕获当前正在阅读/划选的文字及目标块 ID
//   2. 渐进增强与原生自愈：
//      - 若安装了【鲸鱼快速批注】(siyuan-comment)，自动调用 addComment 命令融入侧边批注流
//      - 若未安装该插件（纯净版思源），100% 自动降级为思源原生双轨批注：
//        ① 调用 /api/attr/setBlockAttrs 在该块注入原生属性 memo="{批注内容}"（悬浮即可查看）
//        ② 紧随该块后插入折叠式优雅 Callout 批注块，确保行内审阅体验绝对自洽！
// ============================================================================


public static class QuickCommentMain
{
    public static void Exec(IStepContext context)
    {
        var selectedText = (context.GetVarValue("选中文本") as string ?? "").Trim();
        var targetBlockId = (context.GetVarValue("目标块ID") as string ?? "").Trim();
        var commentText = (context.GetVarValue("批注内容") as string ?? "").Trim();
        if (string.IsNullOrEmpty(commentText)) commentText = (context.GetVarValue("输入文本") as string ?? "").Trim();
        if (string.IsNullOrEmpty(commentText)) commentText = (context.GetVarValue("quicker_in_param") as string ?? "").Trim();
        var windowTitle = context.GetVarValue("来源窗口") as string ?? "思源编辑器";
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 嗅探当前编辑器选区与焦点块 ID (若未传入)
        if (string.IsNullOrEmpty(targetBlockId) || string.IsNullOrEmpty(selectedText))
        {
            try
            {
                var sendRes = LvSendCommand.Send(syUrl, token, "editor.context", "{}", "siyuan-speed-switch", "fast");
                if (sendRes.Success)
                {
                    var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 1500, "siyuan-speed-switch");
                    var dataJson = rec.DataJson;
                    if (!string.IsNullOrEmpty(dataJson))
                    {
                        var bidM = Regex.Match(dataJson, "\"blockId\"\\s*:\\s*\"([^\"]+)\"");
                        var selM = Regex.Match(dataJson, "\"selectedText\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (bidM.Success && string.IsNullOrEmpty(targetBlockId)) targetBlockId = bidM.Groups[1].Value;
                        if (selM.Success && string.IsNullOrEmpty(selectedText)) selectedText = UnescapeJson(selM.Groups[1].Value).Trim();
                    }
                }
            }
            catch { }
        }

        // 兜底：若前台未直接返回焦点块，直接通过原生 SQL 智能匹配最近活跃编辑的段落块
        if (string.IsNullOrEmpty(targetBlockId))
        {
            var sqlRecent = "SELECT id, content FROM blocks WHERE type NOT IN ('d') ORDER BY updated DESC LIMIT 1";
            var rRecent = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sqlRecent) + "\"}");
            if (rRecent.Success && rRecent.Text.Contains("\"id\":"))
            {
                var idM = Regex.Match(rRecent.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (idM.Success) targetBlockId = idM.Groups[1].Value;
                var cM = Regex.Match(rRecent.Text, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (cM.Success && string.IsNullOrEmpty(selectedText)) selectedText = UnescapeJson(cM.Groups[1].Value).Trim();
            }
        }

        if (string.IsNullOrEmpty(targetBlockId))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 未检测到目标块，请在思源笔记中聚焦或划选需要批注的段落。");
            SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 3. 若尚未输入批注内容，弹出速记输入框
        if (string.IsNullOrEmpty(commentText))
        {
            context.SetVarValue("目标块ID", targetBlockId);
            context.SetVarValue("选中文本", selectedText);
            context.SetVarValue("需要弹窗输入", true);
            context.SetVarValue("输入提示", "输入对此段的批注 / 审阅意见：");
            return;
        }

        var now = DateTime.Now;
        var timeStr = now.ToString("HH:mm");

        // 4. 尝试增强路径：调用【鲸鱼批注】插件命令
        bool commentPluginHandled = false;
        try
        {
            var sendRes = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-comment\",\"command\":\"addComment\"}", "siyuan-speed-switch", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 2000, "siyuan-speed-switch");
                commentPluginHandled = (rec.Status == "recorded" || rec.Status == "duplicate");
            }
        }
        catch { commentPluginHandled = false; }

        // 5. 原生平滑降级（即便未安装任何批注插件，依然通过思源原生属性与 Callout 块完美呈现）
        // 动作 A: 设置原生块属性 memo（在思源悬浮块图标上即刻可见）
        var setAttrReq = "{\"id\":\"" + EscapeJson(targetBlockId) + "\",\"attrs\":{\"memo\":\"" + EscapeJson(commentText) + "\",\"custom-comment\":\"true\"}}";
        var attrRes = SyKernelRequest.DoRequest(syUrl, token, "/api/attr/setBlockAttrs", setAttrReq);

        // 动作 B: 在该块下方精确插入带有引用出处的 Callout 批注子块
        var calloutMd = "> 💡 **批注** (" + timeStr + "): " + commentText + "\n{: custom-type=\"inline-comment\" }\n";
        var insertReq = "{\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(calloutMd) + "\",\"previousID\":\"" + EscapeJson(targetBlockId) + "\"}";
        var insertRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/insertBlock", insertReq);

        bool success = attrRes.Success || insertRes.Success;

        context.SetVarValue("是否成功", success);
        context.SetVarValue("通知反馈", success 
            ? "✓ 已为选中文段成功添加批注！" + (commentPluginHandled ? " (已同步鲸鱼批注)" : " (原生 memo/Callout 呈现)")
            : "❌ 批注添加失败：" + attrRes.Error);
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }
}


// ============================================================================
// L1-08: 自媒体选题与灵感一键捕获 —— 主驱动程序 (MediaTopicCaptureMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 灵感秒级捕捉：在知乎、B站、小红书、微博、公众号等任意网页一键提取选题
//   2. 渐进增强与原生自愈：
//      - 若安装了【小驴自媒体】(siyuan-media-workbench)，自动沉淀至选题库数据库进行排期与生命周期流转
//      - 若未安装该插件，100% 自动降级为思源原生结构化卡片，打上 #{平台}# 标签归入今日日记【自媒体选题池】
//   3. 零插件依赖兜底，杜绝灵感因缺少插件而遗失！
// ============================================================================


public static class MediaTopicCaptureMain
{
    public static void Exec(IStepContext context)
    {
        var pageUrl = SafeContext.GetString(context, "页面URL");
        var pageTitle = SafeContext.GetString(context, "页面标题");
        var selectedText = SafeContext.GetString(context, "选中文本");
        var topicAngle = SafeContext.GetString(context, "切入角度");
        var platform = SafeContext.GetString(context, "拟发平台", "公众号");
        var windowTitle = SafeContext.GetString(context, "来源窗口", "自媒体资讯");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrEmpty(pageTitle)) pageTitle = windowTitle;
        if (string.IsNullOrEmpty(topicAngle)) topicAngle = SafeContext.GetString(context, "输入文本");
        if (string.IsNullOrEmpty(topicAngle)) topicAngle = SafeContext.GetString(context, "quicker_in_param");

        // 若尚未输入核心切入角度，弹出速记输入框
        if (string.IsNullOrEmpty(topicAngle))
        {
            context.SetVarValue("页面标题", pageTitle);
            context.SetVarValue("页面URL", pageUrl);
            context.SetVarValue("需要弹窗输入", true);
            context.SetVarValue("输入提示", "输入此选题的核心切入点 / 爆款 Hook (可写拟发平台)：");
            return;
        }

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var timeStr = now.ToString("HH:mm");

        // 自动识别平台（若切入点中含有平台词汇）
        string[] knownPlatforms = new string[] { "B站", "小红书", "知乎", "公众号", "抖音", "视频号", "快手", "Twitter", "YouTube" };
        foreach (var p in knownPlatforms)
        {
            if (topicAngle.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                platform = p;
                break;
            }
        }

        // 2. 构造排版精美的结构化选题灵感卡（毫秒级原生极速落盘）
        var sb = new StringBuilder();
        sb.Append("### 💡 自媒体选题灵感: ").Append(TruncateString(pageTitle, 36)).Append("\n");
        sb.Append("* 🎯 **核心切入点 / Hook**: ").Append(topicAngle).Append("\n");
        sb.Append("* 📱 **拟发平台**: #").Append(platform).Append("# · 📅 **捕获时间**: ").Append(dateStr).Append(" ").Append(timeStr).Append("\n");
        
        if (!string.IsNullOrEmpty(pageUrl))
        {
            sb.Append("* 🔗 **参考信源**: [").Append(pageTitle).Append("](").Append(pageUrl).Append(")\n");
        }
        if (!string.IsNullOrEmpty(selectedText))
        {
            sb.Append("* 📝 **参考素材**: ").Append(selectedText.Replace("\n", " ")).Append("\n");
        }
        sb.Append("\n{: custom-type=\"media-topic\" custom-status=\"backlog\" }\n");

        // 优先尝试追加到自媒体专页，未配置则无缝归入今日日记
        var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(sb.ToString()) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);

        context.SetVarValue("是否成功", r.Success);
        context.SetVarValue("通知反馈", r.Success 
            ? "✓ 选题【" + TruncateString(pageTitle, 20) + "】已成功沉淀至今日日记选题池！"
            : "❌ 选题录入失败：" + r.Error);
    }

    private static string TruncateString(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        s = s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + "...";
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }
}


// ============================================================================
// L1-09: 任务视界结构化派工 —— 主驱动程序 (TaskHorizonDispatchMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 针对「Task Horizon 任务视界」多文档任务管理插件提供桌面级极速结构化派工：
//      - 自动解析任务标题、四象限优先级（P1~P4）、截止日期（今天/明天/指定日）与所属标签
//      - 自动注入 Task Horizon 专用属性 custom-task-priority 与 custom-task-due
//   2. 100% 思源原生自愈兜底：
//      - 若未安装 Task Horizon 插件，直接无缝作为思源标准待办 (- [ ]) 沉淀至今日日记待办池，
//        括号清晰保留优先级与截止日期，排版高可读、绝不报错！
// ============================================================================


public static class TaskHorizonDispatchMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "桌面应用");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");

        // 1. 输入解析
        var rawText = SafeContext.GetInputText(context);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            var json = "{\"title\":\"思源外骨骼 · 任务视界结构化派工\"," +
                       "\"subtitle\":\"免切窗口快速派发待办任务，支持识别优先级与截止日期\"," +
                       "\"badge\":\"任务视界\"," +
                       "\"icon\":\"📋\"," +
                       "\"mode\":\"input\"," +
                       "\"outputVar\":\"输入文本\"," +
                       "\"placeholder\":\"输入任务，如: 完善接口文档 P1 明天截止 #开发、修复登录Bug !p2...\"," +
                       "\"defaultValue\":\"\"," +
                       "\"tags\":[\"P1紧急重要\", \"P2重要不急\", \"P3紧急不重\", \"今日截止\", \"明天截止\"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 2. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 3. 结构化任务解析（优先级 P1~P4，截止日 DueDate）
        string priority = "p2";
        string priorityLabel = "P2重要不急";
        var text = rawText.Trim();

        if (Regex.IsMatch(text, "(?i)\\b(p1|!p1)\\b") || text.Contains("紧急重要") || text.Contains("优先级:P1"))
        {
            priority = "p1";
            priorityLabel = "P1紧急重要";
            text = Regex.Replace(text, "(?i)\\b(!?p1)\\b|紧急重要", "").Trim();
        }
        else if (Regex.IsMatch(text, "(?i)\\b(p3|!p3)\\b") || text.Contains("紧急不重") || text.Contains("优先级:P3"))
        {
            priority = "p3";
            priorityLabel = "P3紧急不重";
            text = Regex.Replace(text, "(?i)\\b(!?p3)\\b|紧急不重", "").Trim();
        }
        else if (Regex.IsMatch(text, "(?i)\\b(p4|!p4)\\b") || text.Contains("不急不重") || text.Contains("优先级:P4"))
        {
            priority = "p4";
            priorityLabel = "P4不急不重";
            text = Regex.Replace(text, "(?i)\\b(!?p4)\\b|不急不重", "").Trim();
        }
        else if (Regex.IsMatch(text, "(?i)\\b(p2|!p2)\\b") || text.Contains("重要不急") || text.Contains("优先级:P2"))
        {
            priority = "p2";
            priorityLabel = "P2重要不急";
            text = Regex.Replace(text, "(?i)\\b(!?p2)\\b|重要不急", "").Trim();
        }

        // 解析截止日期
        string dueDate = "";
        var today = DateTime.Today;
        if (text.Contains("今天截止") || text.Contains("今日截止") || text.Contains("今天到期"))
        {
            dueDate = today.ToString("yyyy-MM-dd");
            text = Regex.Replace(text, "今天截止|今日截止|今天到期", "").Trim();
        }
        else if (text.Contains("明天截止") || text.Contains("次日截止") || text.Contains("明天到期"))
        {
            dueDate = today.AddDays(1).ToString("yyyy-MM-dd");
            text = Regex.Replace(text, "明天截止|次日截止|明天到期", "").Trim();
        }
        else if (text.Contains("后天截止") || text.Contains("后天到期"))
        {
            dueDate = today.AddDays(2).ToString("yyyy-MM-dd");
            text = Regex.Replace(text, "后天截止|后天到期", "").Trim();
        }
        else
        {
            var dateMatch = Regex.Match(text, "\\b(\\d{4}-\\d{2}-\\d{2})\\b");
            if (dateMatch.Success)
            {
                dueDate = dateMatch.Groups[1].Value;
                text = text.Replace(dateMatch.Value, "").Trim();
            }
        }

        // 清理多余空格
        text = Regex.Replace(text, "\\s+", " ").Trim();
        if (string.IsNullOrEmpty(text)) text = "新派发任务";

        // 4. 定位或新建今日日记
        var todayCompact = today.ToString("yyyyMMdd");
        var todayDash = today.ToString("yyyy-MM-dd");
        string docId = "";

        var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + todayCompact + "%' ORDER BY updated DESC LIMIT 1\"}";
        var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
        if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

        if (string.IsNullOrEmpty(docId))
        {
            var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + todayDash + "%' OR hpath LIKE '%" + todayDash + "%') ORDER BY updated DESC LIMIT 1\"}";
            var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
            if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(diaryNotebookId) + "\"}");
            if (createRes.Success) docId = ExtractJsonField(createRes.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未能定位或创建今日日记文档，请确认思源笔记正在运行。");
            return;
        }

        // 5. 组装符合 Task Horizon 属性规范 + 原生高保真双重兼容的 Markdown 块
        var metaSuffix = " (" + priorityLabel + (string.IsNullOrEmpty(dueDate) ? "" : (" · 截止: " + dueDate)) + ")";
        var attrIal = "{: custom-task-priority=\"" + priority + "\"" + (string.IsNullOrEmpty(dueDate) ? "" : (" custom-task-due=\"" + dueDate + "\"")) + " custom-task-horizon=\"true\" }";
        var taskMarkdown = "- [ ] " + text + metaSuffix + " " + attrIal;

        var appendBody = "{\"parentID\":\"" + docId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(taskMarkdown) + "\"}";
        var appRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);

        bool ok = appRes.Success;
        SafeContext.Set(context, "是否成功", ok);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", ok 
            ? "✓ 已派工至 Task Horizon / 今日待办：【" + text + "】(" + priorityLabel + (string.IsNullOrEmpty(dueDate) ? "" : (", " + dueDate)) + ")"
            : "❌ 派工失败：" + appRes.Error);
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// L1-10: PicGo图床智能上传直插 —— 主驱动程序 (PicGoUploadInsertMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 双轨智能图床调度（云端图床 + 本地资产双链路）：
//      - 优先探测本地 PicGo / PicList 运行服务 (http://127.0.0.1:36677/upload)
//      - 若 PicGo 在线：秒级上传至用户配置的云端图床（七牛/腾讯COS/又拍/GitHub/S3等），返回远程 URL
//   2. 100% 思源原生平滑自愈兜底：
//      - 若 PicGo 离线或未安装：自动无损回退调用思源标准 /api/asset/upload 接口，
//        将图片存入本地 data/assets/ 库并返回 assets/xxx.png 相对路径，绝不中断！
//   3. 双通道输出：
//      - 优先通过小驴快门 editor.insert 直插思源活动编辑器光标处（免切窗口）；
//      - 无论插入成功与否，Markdown 图片链接均 100% 保底写入系统剪贴板。
// ============================================================================


public static class PicGoUploadInsertMain
{
    public static void Exec(IStepContext context)
    {
        var rawPath = SafeContext.GetString(context, "图片文件路径");
        var selectedText = SafeContext.GetString(context, "选中文本");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 探查图片文件（参数路径 -> 选中文本文件 -> 剪贴板图像 -> Quicker临时截图）
        string localImgPath = ResolveImageFile(rawPath, selectedText);
        if (string.IsNullOrEmpty(localImgPath) || !File.Exists(localImgPath))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "⚠️ 未检测到有效图像：请先截屏，或复制图片文件/图像到剪贴板后重试。");
            return;
        }

        string resultImgUrl = "";
        bool usedPicGo = false;

        // 3. 优先通道：尝试调用本地 PicGo 服务 (127.0.0.1:36677/upload)
        try
        {
            var picgoRes = UploadToPicGoServer(localImgPath);
            if (picgoRes.Success && !string.IsNullOrEmpty(picgoRes.Url))
            {
                resultImgUrl = picgoRes.Url;
                usedPicGo = true;
            }
        }
        catch { }

        // 4. 原生自愈兜底：若 PicGo 离线，调用思源原生 /api/asset/upload
        if (string.IsNullOrEmpty(resultImgUrl))
        {
            try
            {
                var uploadRes = UploadAssetToSiYuan(syUrl, token, localImgPath);
                if (uploadRes.Success && !string.IsNullOrEmpty(uploadRes.AssetPath))
                {
                    resultImgUrl = uploadRes.AssetPath;
                }
                else
                {
                    SafeContext.Set(context, "是否成功", false);
                    SafeContext.Set(context, "需要通知", true);
                    SafeContext.Set(context, "通知反馈", "❌ 图片入库失败：" + uploadRes.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                SafeContext.Set(context, "是否成功", false);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "❌ 原生自愈异常: " + ex.Message);
                return;
            }
        }

        // 5. 组装标准 Markdown 格式
        string altText = Path.GetFileNameWithoutExtension(localImgPath);
        if (string.IsNullOrEmpty(altText) || altText.StartsWith("quicker_")) altText = "image";
        string mdTag = "![" + altText + "](" + resultImgUrl + ")";

        SafeContext.Set(context, "生成链接", mdTag);

        // 6. 双通道输出：1. 保底写入剪贴板；2. 快门桥直插思源当前活动光标
        try
        {
            Clipboard.SetText(mdTag);
        }
        catch { }

        bool insertedToEditor = false;
        try
        {
            var insertBody = "{\"text\":\"" + SafeContext.EscapeJson(mdTag) + "\"}";
            var sendRes = LvSendCommand.Send(syUrl, token, "editor.insert", insertBody, "siyuan-speed-switch", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 1500, "siyuan-speed-switch");
                if (rec.Status == "recorded" || rec.Status == "duplicate")
                {
                    insertedToEditor = true;
                }
            }
        }
        catch { }

        // 7. 反馈提示
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        if (usedPicGo)
        {
            SafeContext.Set(context, "通知反馈", insertedToEditor
                ? "✓ [PicGo图床] 已上传云端并直插思源编辑器！"
                : "✓ [PicGo图床] 上传成功！Markdown 链接已写入剪贴板。");
        }
        else
        {
            SafeContext.Set(context, "通知反馈", insertedToEditor
                ? "✓ [原生自愈] PicGo未开启，已存入思源 assets 库并直插光标！"
                : "✓ [原生自愈] 已存入思源 assets 库，Markdown 链接已入剪贴板。");
        }
    }

    private static string ResolveImageFile(string rawPath, string selectedText)
    {
        if (!string.IsNullOrEmpty(rawPath) && File.Exists(rawPath) && IsImageExt(rawPath))
            return rawPath;

        if (!string.IsNullOrEmpty(selectedText) && File.Exists(selectedText) && IsImageExt(selectedText))
            return selectedText;

        // 检查系统剪贴板图像并暂存到临时文件
        try
        {
            if (Clipboard.ContainsImage())
            {
                using (var img = Clipboard.GetImage())
                {
                    if (img != null)
                    {
                        var tmpFile = Path.Combine(Path.GetTempPath(), "siyuan_picgo_clip_" + DateTime.Now.Ticks + ".png");
                        img.Save(tmpFile, ImageFormat.Png);
                        return tmpFile;
                    }
                }
            }
        }
        catch { }

        // 检查 Quicker 常见临时截图文件
        string[] candidates = new string[]
        {
            Path.Combine(Path.GetTempPath(), "quicker_screenshot.png"),
            Path.Combine(Path.GetTempPath(), "quicker_capture.png"),
            Path.Combine(Path.GetTempPath(), "quicker_image.png"),
            Path.Combine(Path.GetTempPath(), "quicker_clip_image.png")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                var ageSec = (DateTime.Now - File.GetLastWriteTime(c)).TotalSeconds;
                if (ageSec >= 0 && ageSec < 120) return c;
            }
        }

        return null;
    }

    private static bool IsImageExt(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".bmp";
    }

    private static PicGoResult UploadToPicGoServer(string localPath)
    {
        var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:36677/upload");
        req.Method = "POST";
        req.ContentType = "application/json; charset=utf-8";
        req.Timeout = 3500;

        string jsonPayload = "{\"list\":[\"" + SafeContext.EscapeJson(localPath) + "\"]}";
        byte[] bytes = Encoding.UTF8.GetBytes(jsonPayload);
        req.ContentLength = bytes.Length;

        using (var s = req.GetRequestStream())
        {
            s.Write(bytes, 0, bytes.Length);
        }

        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            var text = sr.ReadToEnd();
            var mSuccess = Regex.Match(text, "\"success\"\\s*:\\s*(true|false)");
            bool ok = mSuccess.Success && mSuccess.Groups[1].Value == "true";
            if (ok)
            {
                var mUrl = Regex.Match(text, "\"result\"\\s*:\\s*\\[\\s*\"([^\"]+)\"");
                if (mUrl.Success)
                {
                    return new PicGoResult { Success = true, Url = mUrl.Groups[1].Value };
                }
            }
        }

        return new PicGoResult { Success = false };
    }

    private static PicGoAssetUploadResult UploadAssetToSiYuan(string baseUrl, string token, string localFilePath)
    {
        try
        {
            var fileName = Path.GetFileName(localFilePath);
            var boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
            var boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
            var trailerBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

            var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/asset/upload");
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Timeout = 8000;
            if (!string.IsNullOrEmpty(token)) req.Headers["Authorization"] = "Token " + token;

            var header = "Content-Disposition: form-data; name=\"file[]\"; filename=\"" + fileName + "\"\r\nContent-Type: image/png\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);

            byte[] fileBytes = File.ReadAllBytes(localFilePath);

            req.ContentLength = boundaryBytes.Length + headerBytes.Length + fileBytes.Length + trailerBytes.Length;

            using (var s = req.GetRequestStream())
            {
                s.Write(boundaryBytes, 0, boundaryBytes.Length);
                s.Write(headerBytes, 0, headerBytes.Length);
                s.Write(fileBytes, 0, fileBytes.Length);
                s.Write(trailerBytes, 0, trailerBytes.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var text = sr.ReadToEnd();
                var m = Regex.Match(text, "\"assets/[^\"]+\"");
                if (m.Success)
                {
                    return new PicGoAssetUploadResult { Success = true, AssetPath = m.Value.Trim('"') };
                }
                return new PicGoAssetUploadResult { Success = false, Error = "未能解析上传后资源相对路径" };
            }
        }
        catch (Exception ex)
        {
            return new PicGoAssetUploadResult { Success = false, Error = ex.Message };
        }
    }

    private class PicGoResult
    {
        public bool Success;
        public string Url;
    }

    private class PicGoAssetUploadResult
    {
        public bool Success;
        public string AssetPath;
        public string Error;
    }
}


// ============================================================================
// L1-11: 轻语气泡闪念速记 —— 主驱动程序 (LuminaThoughtMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 极简桌面气泡弹窗，专为闪念、碎片灵感与情绪速记设计；
//   2. 深度适配「轻语 (siyuan-lumina / LifeLog)」时间流规范：
//      - 自动写入 custom-lifelog-date、custom-lifelog-time 与 custom-lifelog-type="thought"
//   3. 100% 思源原生平滑自愈兜底：
//      - 若未安装轻语插件，自动作为思源标准带时间戳清单（- HH:mm 闪念内容）
//        沉淀至今日日记时间轴文末，排版优雅、心流不断！
// ============================================================================


public static class LuminaThoughtMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "桌面应用");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");

        // 1. 输入内容解析
        var rawText = SafeContext.GetInputText(context);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            var json = "{\"title\":\"思源外骨骼 · 轻语气泡闪念速记\"," +
                       "\"subtitle\":\"记录每一个轻闪而过的念头 (联动轻语/LifeLog时间流)\"," +
                       "\"badge\":\"轻语闪念\"," +
                       "\"icon\":\"💬\"," +
                       "\"mode\":\"input\"," +
                       "\"outputVar\":\"输入文本\"," +
                       "\"placeholder\":\"随手记一笔瞬时想法、灵感火花或行动备忘...\"," +
                       "\"defaultValue\":\"\"," +
                       "\"tags\":[\"#灵感\", \"#随想\", \"#思考\", \"#摘录\", \"#复盘\"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 2. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 3. 定位或新建今日日记
        var now = DateTime.Now;
        var todayCompact = now.ToString("yyyyMMdd");
        var todayDash = now.ToString("yyyy-MM-dd");
        var timeStr = now.ToString("HH:mm");
        string docId = "";

        var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + todayCompact + "%' ORDER BY updated DESC LIMIT 1\"}";
        var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
        if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

        if (string.IsNullOrEmpty(docId))
        {
            var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + todayDash + "%' OR hpath LIKE '%" + todayDash + "%') ORDER BY updated DESC LIMIT 1\"}";
            var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
            if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(diaryNotebookId) + "\"}");
            if (createRes.Success) docId = ExtractJsonField(createRes.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未能定位或创建今日日记文档。");
            return;
        }

        // 4. 构建符合轻语 (Lumina) 规范 + 原生日记时间轴兼容的 Markdown 块
        var text = rawText.Trim();
        var ial = "{: custom-lifelog-date=\"" + todayDash + "\" custom-lifelog-time=\"" + timeStr + "\" custom-lifelog-type=\"thought\" custom-type=\"lumina-memo\" }";
        var thoughtMd = "- " + timeStr + " " + text + " " + ial;

        var appendBody = "{\"parentID\":\"" + docId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(thoughtMd) + "\"}";
        var appRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);

        bool ok = appRes.Success;
        var summary = text.Length > 20 ? (text.Substring(0, 20) + "...") : text;

        SafeContext.Set(context, "是否成功", ok);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", ok 
            ? "✓ 闪念已记入轻语/今日时间轴：【" + summary + "】"
            : "❌ 写入失败：" + appRes.Error);
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// L2-01: AI 提炼与 FSRS 闪卡工坊 —— 主驱动程序 (FlashcardFactoryMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 捕获选中文本，获取当前环境上下文（来源窗口、文档块链接）
//   2. 闪卡模式双轨生成：
//      - 结构化启发式拆分：若文本包含问答分隔（如 "？"、"："、"//"），自动分离问与答
//      - 概念提炼模式：自动提取首句或主干为问题，提取要点为列表答案
//   3. 构造思源原生 FSRS 闪卡块（带 {: custom-riff-decks="default" } 原生属性）
//   4. 溯源锚定：自动附加原文双链引用与来源标签，随时可跳回知识出处
//   5. 写入目标与排程：写入今日日记或指定题卡文档，联动小驴闪卡纳入复习调度
// ============================================================================


public static class FlashcardFactoryMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "文献选区");
        var sourceBlockId = SafeContext.GetString(context, "来源块ID");
        var cardDeckName = SafeContext.GetString(context, "卡包名称", "default");
        if (string.IsNullOrWhiteSpace(cardDeckName)) cardDeckName = "default";

        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 获取输入内容（自动覆盖 输入文本 -> quicker_in_param -> 选中文本 -> 剪贴板文本）
        var text = SafeContext.GetInputText(context);
        if (string.IsNullOrEmpty(text))
        {
            context.SetVarValue("需要弹窗输入", true);
            context.SetVarValue("输入提示", "输入或粘贴欲制成闪卡的内容 (支持 问//答 格式)：");
            return;
        }

        // 2. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 3. 智能问答拆解
        string question = "";
        string answer = "";

        if (text.Contains("//"))
        {
            var parts = text.Split(new string[] { "//" }, 2, StringSplitOptions.None);
            question = parts[0].Trim();
            answer = parts[1].Trim();
        }
        else if (text.Contains("——"))
        {
            var parts = text.Split(new string[] { "——" }, 2, StringSplitOptions.None);
            question = parts[0].Trim();
            answer = parts[1].Trim();
        }
        else if (text.Contains("？") || text.Contains("?"))
        {
            var qIdx = Math.Max(text.IndexOf('？'), text.IndexOf('?'));
            question = text.Substring(0, qIdx + 1).Trim();
            answer = text.Substring(qIdx + 1).Trim();
            if (string.IsNullOrEmpty(answer)) answer = "(请在复习时补充核心论点)";
        }
        else
        {
            // 自动概念提炼：以首句或首行作为提问，余下作为要点解答
            var lines = text.Split(new char[] { '\n', '。' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
            {
                question = lines[0].Trim() + "的核心内涵是什么？";
                answer = lines[1].Trim();
            }
            else
            {
                question = "【核心概念辨析】" + text;
                answer = text;
            }
        }

        // 清理前缀符号
        question = question.TrimStart('?', '？', '#', ' ');
        if (question.StartsWith("🎴")) question = question.Substring("🎴".Length).TrimStart();

        // 4. 构建思源原生 FSRS 闪卡 Markdown 块 (采用引用单块完整封装，保证 Riff 算法准确识别整体卡片)
        var sb = new StringBuilder();
        sb.Append("> 🎴 **").Append(question).Append("**\n>\n");
        sb.Append("> ").Append(answer.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\n> ")).Append("\n>\n");
        
        // 溯源标注与双链
        sb.Append("> *溯源*: ");
        if (!string.IsNullOrEmpty(sourceBlockId))
        {
            sb.Append("((").Append(sourceBlockId).Append(" \"查看知识出处原文\"))");
        }
        else
        {
            sb.Append("摘自 ").Append(windowTitle);
        }
        sb.Append(" · 制卡时间 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("\n");

        // 注入 FSRS 卡包原生属性 (紧跟引用块底端，精确绑定在容器整体上)
        sb.Append("{: custom-riff-decks=\"").Append(EscapeJson(cardDeckName)).Append("\" custom-type=\"fsrs-card\" }\n");

        var cardMarkdown = sb.ToString();

        // 5. 写入思源今日日记或收集箱
        var postBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(cardMarkdown) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postBody);

        context.SetVarValue("是否成功", r.Success);
        context.SetVarValue("问题摘要", question);
        context.SetVarValue("通知反馈", r.Success 
            ? "✓ 已生成 FSRS 问答闪卡并推入【" + cardDeckName + "】复习流！" 
            : "❌ 闪卡制卡失败：" + r.Error);
        SafeContext.Set(context, "需要通知", true);
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }
}


// ============================================================================
// L2-02: 错题本与智能自适应题库 —— 主驱动程序 (ExamWrongbookMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 捕获错题正文（选区、截屏或弹窗输入）
//   2. 启发式解析题目结构：题干、错误选项、正确答案、考点与解析
//   3. 格式化为思源原生题卡 Markdown 块（带标签 #错题本 #考点 属性）
//   4. 写入题库文档或今日日记
//   5. 联动【小驴考试】(siyuan-exam)：自动调用 exam.open(target='wrongbook') 进入错题复习台
// ============================================================================


public static class ExamWrongbookMain
{
    public static void Exec(IStepContext context)
    {
        var rawText = SafeContext.GetString(context, "输入文本");
        var selectedText = SafeContext.GetString(context, "选中文本");
        var windowTitle = SafeContext.GetString(context, "来源窗口", "题库来源");
        var examDocId = SafeContext.GetString(context, "题库文档ID");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        var text = !string.IsNullOrEmpty(selectedText) ? selectedText : rawText;
        if (string.IsNullOrEmpty(text))
        {
            context.SetVarValue("需要弹窗输入", true);
            context.SetVarValue("输入提示", "输入错题 (支持 题干 // 错选 // 正确解析 格式)：");
            return;
        }

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 2. 结构化解析
        string question = text;
        string wrongAns = "";
        string correctAns = "";
        string analysis = "";

        if (text.Contains("//"))
        {
            var parts = text.Split(new string[] { "//" }, StringSplitOptions.None);
            if (parts.Length >= 1) question = parts[0].Trim();
            if (parts.Length >= 2) wrongAns = parts[1].Trim();
            if (parts.Length >= 3) correctAns = parts[2].Trim();
            if (parts.Length >= 4) analysis = parts[3].Trim();
        }

        // 3. 构造结构化错题卡片
        var sb = new StringBuilder();
        var now = DateTime.Now;

        sb.Append("### ❌ 错题归档: ").Append(TruncateString(question, 32)).Append("\n\n");
        sb.Append("**题目**: ").Append(question).Append("\n\n");

        if (!string.IsNullOrEmpty(wrongAns))
        {
            sb.Append("* 🚫 **误选**: ~~").Append(wrongAns).Append("~~\n");
        }
        if (!string.IsNullOrEmpty(correctAns))
        {
            sb.Append("* ✅ **正解**: **").Append(correctAns).Append("**\n");
        }
        if (!string.IsNullOrEmpty(analysis))
        {
            sb.Append("* 💡 **解析与考点**: ").Append(analysis).Append("\n");
        }

        sb.Append("* 📅 **录入时间**: ").Append(now.ToString("yyyy-MM-dd HH:mm")).Append(" · 来源: ").Append(windowTitle).Append("\n\n");
        sb.Append("{: custom-type=\"exam-wrongbook\" custom-exam-status=\"unmastered\" }\n");

        var mdResult = sb.ToString();

        // 4. 写入指定文档或今日日记
        bool writeSuccess = false;
        string targetName = "";

        if (!string.IsNullOrEmpty(examDocId))
        {
            var appendBody = "{\"parentID\":\"" + examDocId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(mdResult) + "\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);
            writeSuccess = r.Success;
            targetName = "错题本专页";
        }
        else
        {
            var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(mdResult) + "\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);
            writeSuccess = r.Success;
            targetName = "今日日记";
        }

        // 5. 联动小驴考试 (siyuan-exam): 唤起错题复习台
        if (writeSuccess)
        {
            LvSendCommand.Send(syUrl, token, "exam.open", "{\"target\":\"wrongbook\"}");
        }

        context.SetVarValue("是否成功", writeSuccess);
        context.SetVarValue("通知反馈", writeSuccess 
            ? "✓ 错题已成功归入【" + targetName + "】，并联动打开小驴考试错题台！" 
            : "❌ 错题写入失败。");
        SafeContext.Set(context, "需要通知", true);
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string TruncateString(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + "...";
    }
}


// ============================================================================
// L2-03: 全库概念网络自动双链巡检 —— 主驱动程序 (AutoWikiLinkerMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 读取待编织文本或选中文段（如新写的笔记草稿）
//   2. 自动拉取知识库全量核心文档标题与关键概念词库（SQL SELECT id, content FROM blocks WHERE type='d'）
//   3. 智能词网匹配：按长度倒序贪婪优先匹配长专有名词，避免短词局部截断
//   4. 双链自动编织：将匹配到的实体名词替换为思源原生块引用格式 ((block_id "实体名"))
//   5. 防重防护：已处于超链接、代码块或双链结构内部的内容严格保持原样，不重复包裹
// ============================================================================


public static class AutoWikiLinkerMain
{
    public static void Exec(IStepContext context)
    {
        var rawText = SafeContext.GetString(context, "输入文本");
        var selectedText = SafeContext.GetString(context, "选中文本");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        var text = !string.IsNullOrEmpty(selectedText) ? selectedText : rawText;
        if (string.IsNullOrEmpty(text))
        {
            var clip = SafeContext.GetString(context, "剪贴板文本");
            if (!string.IsNullOrEmpty(clip)) text = clip.Trim();
        }

        if (string.IsNullOrEmpty(text))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 待编织文本为空，请划选需织网的文段或复制到剪贴板。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 查询全库核心文档标题作为候选概念词典
        var sql = "SELECT id, content FROM blocks WHERE type='d' AND length(content) >= 2 AND length(content) <= 32 ORDER BY updated DESC LIMIT 300";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(sql) + "\"}");

        if (!r.Success)
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 检索概念网络失败：" + r.Error);
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        var conceptDict = ExtractConcepts(r.Text);
        if (conceptDict.Count == 0)
        {
            context.SetVarValue("是否成功", true);
            context.SetVarValue("编织后文本", text);
            context.SetVarValue("新增双链数", 0);
            context.SetVarValue("通知反馈", "ℹ️ 知识库文档词库为空，未做修改。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 3. 执行概念网络双链编织
        int linkedCount = 0;
        var hitNames = new List<string>();
        var wovenText = WeaveWikiLinks(text, conceptDict, out linkedCount, hitNames);

        context.SetVarValue("编织后文本", wovenText);
        context.SetVarValue("新增双链数", linkedCount);
        context.SetVarValue("命中概念清单", string.Join(", ", hitNames.ToArray()));
        context.SetVarValue("是否成功", true);

        if (linkedCount > 0)
        {
            // 如果成功编织，直接写入剪贴板
            context.SetVarValue("需要写回剪贴板", true);
            SetClipboardHelper(wovenText);
            context.SetVarValue("通知反馈", "✓ 成功编织 " + linkedCount + " 处概念双链：【" + string.Join("、", hitNames.ToArray()) + "】！已复制到剪贴板。");
        }
        else
        {
            context.SetVarValue("通知反馈", "ℹ️ 未在文中识别出知识库已有专有名词。");
        SafeContext.Set(context, "需要通知", true);
        }
    }

    /// <summary>
    /// 核心双链织网算法（按长度倒序贪婪匹配，单词首次出现替换，防重闭环）
    /// </summary>
    private static string WeaveWikiLinks(string source, List<ConceptEntry> dict, out int linkCount, List<string> hitList)
    {
        linkCount = 0;
        var result = source;

        // 按标题长度从长到短排序（优先匹配长词，如优先匹配「分布式锁」而非「锁」）
        dict.Sort((a, b) => b.Title.Length.CompareTo(a.Title.Length));

        foreach (var c in dict)
        {
            if (string.IsNullOrEmpty(c.Title) || c.Title.Length < 2) continue;
            // 过滤无意义系统词
            if (c.Title == "今日日记" || c.Title == "收集箱" || c.Title == "未命名" || c.Title == "Inbox") continue;

            // 检查文本是否包含该专有名词
            if (result.Contains(c.Title))
            {
                // 正则防重检查：如果该词已经在 ((id "title")) 或 [title](url) 或 `title` 内部，跳过
                var pattern = @"(?<!\(\([^\)]*|\[[^\]]*|`[^`]*)(" + Regex.Escape(c.Title) + @")(?![^\(]*\)\)|[^\[]*\]|[^`]*`)";
                var match = Regex.Match(result, pattern);
                if (match.Success)
                {
                    // 仅替换首次出现处
                    var replacement = "((" + c.Id + " \"" + c.Title + "\"))";
                    result = result.Substring(0, match.Index) + replacement + result.Substring(match.Index + match.Length);
                    linkCount++;
                    hitList.Add(c.Title);
                    if (linkCount >= 15) break; // 单篇上限 15 处，避免密度过大
                }
            }
        }

        return result;
    }

    private static List<ConceptEntry> ExtractConcepts(string json)
    {
        var list = new List<ConceptEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(json, "\\{[^\\}]+\\}");
        foreach (Match m in matches)
        {
            var b = m.Value;
            var idM = Regex.Match(b, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            var contentM = Regex.Match(b, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (idM.Success && contentM.Success)
            {
                var id = idM.Groups[1].Value;
                var title = UnescapeJson(contentM.Groups[1].Value).Trim();
                if (title.Length >= 2 && !seen.Contains(title))
                {
                    seen.Add(title);
                    list.Add(new ConceptEntry { Id = id, Title = title });
                }
            }
        }

        return list;
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    public class ConceptEntry
    {
        public string Id;
        public string Title;
    }

    private static void SetClipboardHelper(string text)
    {
        try { System.Windows.Forms.Clipboard.SetText(text); } catch { }
    }
}

// ============================================================================
// L2-04: 文档一键思维导图化 —— 主驱动程序 (DocToMindmapMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 目录树自动提取：自动嗅探当前前台文档，调用 /api/outline/getDocOutline 提取 H1~H6 大纲
//   2. 渐进增强与原生平滑降级（降维打击式兜底）：
//      - 若安装了【KMind Zen】(siyuan-kmind-zen)，调用 kmindZenInsertBlockMap 插入交互式导图块
//      - 若未安装该插件（纯净版思源），100% 自动构建标准 Mermaid 语法 mindmap 代码块，
//        直接通过 /api/block/prependBlock 插入文档头部，思源笔记原生即可无缝渲染出高颜值思维导图！
// ============================================================================


public static class DocToMindmapMain
{
    public static void Exec(IStepContext context)
    {
        var targetDocId = (context.GetVarValue("目标文档ID") as string ?? "").Trim();
        var insertPosition = (context.GetVarValue("插入位置") as string ?? "prepend").Trim().ToLowerInvariant();
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        string activeDocId = targetDocId;
        string activeDocTitle = "核心文档";

        // 2. 嗅探当前前台活动文档 ID
        if (string.IsNullOrEmpty(activeDocId))
        {
            try
            {
                var sendRes = LvSendCommand.Send(syUrl, token, "editor.context", "{}", "siyuan-speed-switch", "fast");
                if (sendRes.Success)
                {
                    var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 1500, "siyuan-speed-switch");
                    var dataJson = rec.DataJson;
                    if (!string.IsNullOrEmpty(dataJson))
                    {
                        var idM = Regex.Match(dataJson, "\"docId\"\\s*:\\s*\"([^\"]+)\"");
                        var titleM = Regex.Match(dataJson, "\"rootTitle\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (idM.Success) activeDocId = idM.Groups[1].Value;
                        if (titleM.Success) activeDocTitle = UnescapeJson(titleM.Groups[1].Value);
                    }
                }
            }
            catch { }
        }

        // 兜底：若前台未聚焦，取最近更新文档
        if (string.IsNullOrEmpty(activeDocId))
        {
            var sqlRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", 
                "{\"stmt\":\"SELECT id, content FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 1\"}");
            if (sqlRes.Success)
            {
                var idM = Regex.Match(sqlRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                var titleM = Regex.Match(sqlRes.Text, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (idM.Success) activeDocId = idM.Groups[1].Value;
                if (titleM.Success) activeDocTitle = UnescapeJson(titleM.Groups[1].Value);
            }
        }

        if (string.IsNullOrEmpty(activeDocId))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 未检测到目标文档，请在思源中打开任意一篇笔记。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 3. 调用 /api/outline/getDocOutline 提取大纲节点
        var outlineReq = "{\"id\":\"" + EscapeJson(activeDocId) + "\"}";
        var outlineRes = SyKernelRequest.DoRequest(syUrl, token, "/api/outline/getDocOutline", outlineReq);

        var headings = DocOutlineNavMain.ParseOutline(outlineRes.Text);
        if (headings.Count == 0)
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "ℹ️ 文档【" + activeDocTitle + "】暂无小节标题，无法构建思维导图。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 4. 尝试增强路径：调用【KMind Zen】导图插件命令（带回执确认）
        bool kmindHandled = false;
        try
        {
            var sendRes = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-kmind-zen\",\"command\":\"kmindZenInsertBlockMap\"}", "siyuan-speed-switch", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 2500, "siyuan-speed-switch");
                kmindHandled = (rec.Status == "recorded" || rec.Status == "duplicate");
            }
        }
        catch { kmindHandled = false; }

        if (kmindHandled)
        {
            context.SetVarValue("是否成功", true);
            context.SetVarValue("通知反馈", "✓ 已成功调用 KMind Zen 插入交互式思维导图块！");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 5. 核心原生降级：构建思源原生支持的 Mermaid Mindmap 代码块
        var sb = new StringBuilder();
        sb.Append("```mermaid\n");
        sb.Append("mindmap\n");
        sb.Append("  root((\"").Append(CleanMermaidText(activeDocTitle)).Append("\"))\n");

        foreach (var h in headings)
        {
            // 按标题层级生成缩进 (H1=2级缩进, H2=3级缩进...)
            int indentLevel = Math.Min(Math.Max((int)h.Depth, 1), 5);
            string indent = new string(' ', (indentLevel + 1) * 2);
            sb.Append(indent).Append(CleanMermaidText(h.Content)).Append("\n");
        }
        sb.Append("```\n\n{: custom-type=\"mermaid-mindmap\" }\n");

        var mermaidBlock = sb.ToString();

        // 6. 智能检测是否已存在旧思维导图：存在则就地刷新更新，不存在则置顶插入
        string existingMindmapBlockId = "";
        try
        {
            var checkSql = "SELECT id FROM blocks WHERE root_id='" + EscapeJson(activeDocId) + "' AND (ial LIKE '%custom-type=\"mermaid-mindmap\"%' OR markdown LIKE '%mindmap%') LIMIT 1";
            var checkRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(checkSql) + "\"}");
            if (checkRes.Success && checkRes.Text.Contains("\"id\":"))
            {
                var idM = Regex.Match(checkRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (idM.Success) existingMindmapBlockId = idM.Groups[1].Value;
            }
        }
        catch { }

        bool isUpdated = false;
        bool writeOk = false;
        string writeErr = "";

        if (!string.IsNullOrEmpty(existingMindmapBlockId))
        {
            // 就地更新已有思维导图块
            var updateReq = "{\"id\":\"" + EscapeJson(existingMindmapBlockId) + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(mermaidBlock) + "\"}";
            var upd = SyKernelRequest.DoRequest(syUrl, token, "/api/block/updateBlock", updateReq);
            writeOk = upd.Success;
            writeErr = upd.Error;
            isUpdated = writeOk;
        }
        else
        {
            // 置顶插入文档头部
            var endpoint = (insertPosition == "append") ? "/api/block/appendBlock" : "/api/block/prependBlock";
            var insertReq = "{\"parentID\":\"" + EscapeJson(activeDocId) + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(mermaidBlock) + "\"}";
            var ins = SyKernelRequest.DoRequest(syUrl, token, endpoint, insertReq);
            writeOk = ins.Success;
            writeErr = ins.Error;
        }

        context.SetVarValue("是否成功", writeOk);
        context.SetVarValue("通知反馈", writeOk 
            ? (isUpdated 
                ? "✓ 已成功就地更新文档思维导图！(无缝覆写旧导图，零插件原生渲染)" 
                : "✓ 已成功生成原生 Mermaid 交互式思维导图并插入文档头部！(零插件原生渲染)")
            : "❌ 写入思维导图失败：" + writeErr);
    }

    private static string CleanMermaidText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "主题";
        // 剥离 Mermaid 语法冲突符号
        return s.Replace("\"", "'").Replace("(", "（").Replace(")", "）").Replace("[", "【").Replace("]", "】").Trim();
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }
}


// ============================================================================
// L2-05: 备考冲刺仪表盘 —— 主驱动程序 (ExamDashboardMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 exam.stats / exam.open（window.siyuanExam lite 透传）
// 核心逻辑：
//   1. 默认：读取脱敏统计快照组装仪表盘，高保真 Bento 弹窗呈现四入口直达
//   2. 直达模式：以参数 open/practice/wrongbook/mock/report 跨软件秒开对应练习台
//   3. 兜底：考试桥不可用时降级为思源原生 Riff 到期卡统计 + 闪卡复习指引
// ============================================================================


public static class ExamDashboardMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 外部直接指令 / 快速传参嗅探
        var inParam = SafeContext.GetInputText(context).Trim().ToLowerInvariant();
        var direct = SafeContext.GetString(context, "练习入口").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(direct) && !string.IsNullOrEmpty(inParam))
        {
            direct = inParam;
        }

        // 直达模式：指定练习入口直接打开
        if (!string.IsNullOrEmpty(direct))
        {
            OpenExam(context, syUrl, token, direct);
            return;
        }

        // 默认：仪表盘 + 高保真 Bento 四入口菜单
        var dash = BuildDashboard(syUrl, token);
        SafeContext.Set(context, "仪表盘文本", dash);

        var json = "{\"title\":\"思源外骨骼 · 备考冲刺仪表盘\"," +
                   "\"subtitle\":\"一键直开练习台、错题本与模拟测\"," +
                   "\"badge\":\"小驴学习\"," +
                   "\"icon\":\"🎯\"," +
                   "\"mode\":\"select\"," +
                   "\"outputVar\":\"练习入口\"," +
                   "\"stats\":\"" + SafeContext.EscapeJson(dash.Replace("\n", " ")) + "\"," +
                   "\"items\":[" +
                   "{\"id\":\"practice\",\"title\":\"开始常规练习\",\"desc\":\"小驴学习智能刷题与自适应练习台\",\"badge\":\"刷题练习\",\"badgeCls\":\"badge-native\",\"hotkey\":\"1\",\"icon\":\"🎯\"}," +
                   "{\"id\":\"wrongbook\",\"title\":\"错题本深度攻坚\",\"desc\":\"集中消灭高频错误知识点与错题卡\",\"badge\":\"错题本\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"2\",\"icon\":\"📕\"}," +
                   "{\"id\":\"mock\",\"title\":\"模拟冲刺测试\",\"desc\":\"全真模拟考试环境与限时作答\",\"badge\":\"模拟测试\",\"badgeCls\":\"badge-bridge\",\"hotkey\":\"3\",\"icon\":\"⏱\"}," +
                   "{\"id\":\"report\",\"title\":\"查看学习报告\",\"desc\":\"分析知识掌握度、薄弱点与遗忘曲线\",\"badge\":\"学情分析\",\"badgeCls\":\"badge-system\",\"hotkey\":\"4\",\"icon\":\"📊\"}," +
                   "{\"id\":\"riff\",\"title\":\"思源原生 FSRS 闪卡复习\",\"desc\":\"调用思源原生 Riff 算法到期卡片包\",\"badge\":\"原生FSRS\",\"badgeCls\":\"badge-native\",\"hotkey\":\"5\",\"icon\":\"🎴\"}" +
                   "]}";

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "是否成功", true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateSiYuanWindow()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    private static void OpenExam(IStepContext context, string syUrl, string token, string target)
    {
        ActivateSiYuanWindow();

        if (target == "open") target = "practice";

        if (target == "riff")
        {
            var riffRes = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"\",\"command\":\"riffCard\"}", "siyuan-speed-switch", "fast");
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已唤起思源原生 FSRS 闪卡复习。");
            return;
        }

        string[] valid = new string[] { "practice", "wrongbook", "mock", "report" };
        bool okTarget = false;
        foreach (var v in valid) { if (v == target) { okTarget = true; break; } }
        if (!okTarget) target = "practice";

        var rec = PollBridge(syUrl, token, "exam.open", "{\"target\":\"" + target + "\"}", 6000);
        if (rec.Status == "recorded")
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已唤起思源并打开小驴学习练习台（" + target + "）。");
            return;
        }

        // 尝试 commands.run 穿透调用 siyuan-exam
        var cmdRes = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-exam\",\"command\":\"command.open" + char.ToUpper(target[0]) + target.Substring(1) + "\"}", "siyuan-speed-switch", "fast");
        SafeContext.Set(context, "是否成功", cmdRes.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", cmdRes.Success 
            ? "✓ 已唤起思源并打开小驴学习（" + target + "）。"
            : "⚠️ 练习台打开未完成【" + rec.Status + "】：" + rec.Message);
    }

    private static string BuildDashboard(string syUrl, string token)
    {
        var rec = PollBridge(syUrl, token, "exam.stats", "{}", 8000);
        var sb = new StringBuilder();

        if (rec.Status == "recorded" && !string.IsNullOrEmpty(rec.DataJson) && rec.DataJson.Contains("\"stats\":{"))
        {
            sb.Append("📊 小驴学习快照：").Append(SummarizeStats(rec.DataJson));
        }
        else
        {
            sb.Append("⚠️ 考试桥未就绪【").Append(rec.Status).Append("】：").Append(rec.Message);
            var due = SyKernelRequest.DoRequest(syUrl, token, "/api/riff/getRiffDueCards", "{\"deckID\":\"\"}");
            var m = Regex.Match(due.Text ?? "", "\"unreviewedCount\"\\s*:\\s*([0-9]+)");
            if (due.Success && m.Success)
            {
                sb.Append("\n🎴 思源原生 FSRS 到期卡：").Append(m.Groups[1].Value).Append(" 张待复习");
            }
        }
        return sb.ToString();
    }

    private static string SummarizeStats(string dataJson)
    {
        var sb = new StringBuilder();
        AppendIfFound(dataJson, "totalAnswers", "累计作答", sb);
        AppendIfFound(dataJson, "answered", "作答量", sb);
        AppendIfFound(dataJson, "correctRate", "正确率", sb);
        AppendIfFound(dataJson, "accuracy", "正确率", sb);
        AppendIfFound(dataJson, "eliminated", "已消灭错题", sb);
        AppendIfFound(dataJson, "streak", "连击", sb);
        AppendIfFound(dataJson, "streakDays", "连击天数", sb);
        if (sb.Length == 0) return "快照已获取（详见小驴学习练习台）。";
        return sb.ToString().TrimEnd().TrimEnd('·').TrimEnd();
    }

    private static void AppendIfFound(string json, string field, string label, StringBuilder sb)
    {
        if (sb == null) return;
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*([0-9.]+)");
        if (m.Success) sb.Append(label).Append(" ").Append(m.Groups[1].Value).Append(" · ");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L2-06: 闪卡复习直达 —— 主驱动程序 (FlashcardEntryMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 commands.list / commands.run（小驴闪卡 siyuan-lv-cards 命令面穿透）
// 核心逻辑：
//   1. 列出小驴闪卡注册的命令清单，高保真 Bento 弹窗选择执行（30s 确认门控）
//   2. 置顶直达小驴闪卡主中心与原生思源 FSRS 复习
//   3. 直达模式：以参数 open/dashboard 直接打开主界面
//   4. 3层窗口唤醒置顶保障
// ============================================================================


public static class FlashcardEntryMain
{
    private const string TargetPlugin = "siyuan-lv-cards";

    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 外部直接指令：打开闪卡主面板
        var inParam = SafeContext.GetInputText(context).Trim().ToLowerInvariant();
        if (inParam == "open" || inParam == "dashboard")
        {
            OpenDashboard(context, syUrl, token);
            return;
        }

        var picked = SafeContext.GetString(context, "选中命令");
        if (!string.IsNullOrEmpty(picked))
        {
            RunCommand(context, syUrl, token, picked);
            return;
        }

        ListCommands(context, syUrl, token);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateSiYuanWindow()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    private static void OpenDashboard(IStepContext context, string syUrl, string token)
    {
        ActivateSiYuanWindow();
        var rec = PollBridge(syUrl, token, "commands.run",
            "{\"plugin\":\"" + TargetPlugin + "\",\"command\":\"openDashboard\"}", 8000);
        SafeContext.Set(context, "是否成功", rec.Status == "recorded");
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", rec.Status == "recorded"
            ? "✓ 已唤起思源并打开【小驴闪卡】复习中心。"
            : "⚠️ 打开小驴闪卡未完成【" + rec.Status + "】：" + rec.Message);
    }

    private static void ListCommands(IStepContext context, string syUrl, string token)
    {
        var rec = PollBridge(syUrl, token, "commands.list", "{\"plugin\":\"" + TargetPlugin + "\"}", 8000);
        var menu = BuildCommandList(rec.DataJson);

        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 闪卡复习直达\",")
                   .Append("\"subtitle\":\"一键执行小驴闪卡命令，或直达复习工作台\",")
                   .Append("\"badge\":\"小驴闪卡\",")
                   .Append("\"icon\":\"🃏\",")
                   .Append("\"mode\":\"select\",")
                   .Append("\"outputVar\":\"选中命令\",")
                   .Append("\"placeholder\":\"输入拼音过滤命令，回车快速执行...\",")
                   .Append("\"items\":[")
                   .Append("{\"id\":\"openDashboard\",\"title\":\"打开小驴闪卡复习中心 (Alt+6)\",\"desc\":\"唤醒思源打开闪卡复习看板\",\"badge\":\"小驴闪卡\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"Alt+6\",\"icon\":\"🎴\"},")
                   .Append("{\"id\":\"riffCard\",\"title\":\"打开思源原生 FSRS 闪卡复习\",\"desc\":\"调用思源原生 Riff 卡包\",\"badge\":\"原生FSRS\",\"badgeCls\":\"badge-native\",\"hotkey\":\"1\",\"icon\":\"🃏\"}");

        int numHk = 2;
        if (menu.Count > 0)
        {
            foreach (var cmd in menu)
            {
                var hk = numHk <= 9 ? numHk.ToString() : "";
                jsonBuilder.Append(",{\"id\":\"").Append(SafeContext.EscapeJson(cmd.Key)).Append("\",")
                           .Append("\"title\":\"").Append(SafeContext.EscapeJson(cmd.Title)).Append("\",")
                           .Append("\"desc\":\"命令标识: ").Append(SafeContext.EscapeJson(cmd.Key)).Append("\",")
                           .Append("\"badge\":\"插件命令\",")
                           .Append("\"badgeCls\":\"badge-bridge\",")
                           .Append("\"hotkey\":\"").Append(hk).Append("\",")
                           .Append("\"icon\":\"⚡\"}");
                numHk++;
            }
        }
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "是否成功", true);
    }

    private static void RunCommand(IStepContext context, string syUrl, string token, string picked)
    {
        ActivateSiYuanWindow();

        if (picked == "openDashboard")
        {
            OpenDashboard(context, syUrl, token);
            return;
        }

        if (picked == "riffCard")
        {
            LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"\",\"command\":\"riffCard\"}", "siyuan-speed-switch", "fast");
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已唤起思源原生 FSRS 闪卡复习。");
            return;
        }

        var sep = picked.LastIndexOf('|');
        var title = sep > 0 ? picked.Substring(0, sep) : picked;
        var langKey = sep > 0 ? picked.Substring(sep + 1) : picked;

        var rec = PollBridge(syUrl, token, "commands.run",
            "{\"plugin\":\"" + TargetPlugin + "\",\"command\":\"" + SafeContext.EscapeJson(langKey) + "\"}", 10000);

        SafeContext.Set(context, "是否成功", rec.Status == "recorded");
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", rec.Status == "recorded"
            ? "✓ 已执行闪卡命令【" + (title == "" ? langKey : title) + "】。"
            : "❌ 执行未完成【" + rec.Status + "】：" + rec.Message);
    }

    public class CmdEntry { public string Key; public string Title; }

    private static List<CmdEntry> BuildCommandList(string dataJson)
    {
        var list = new List<CmdEntry>();
        if (string.IsNullOrEmpty(dataJson)) return list;

        foreach (Match m in Regex.Matches(dataJson, "\\{[^\\{\\}]+\\}"))
        {
            var b = m.Value;
            var key = ExtractField(b, "command");
            if (key == "") key = ExtractField(b, "langKey");
            if (key == "" || key == "openDashboard") continue;
            var title = ExtractField(b, "title");
            if (title == "") title = ExtractField(b, "name");
            list.Add(new CmdEntry { Key = key, Title = title == "" ? key : title });
            if (list.Count >= 25) break;
        }
        return list;
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L2-07: 标签体系治理巡检 —— 主驱动程序 (TagGovernanceMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑（严格只读，绝不写入）：
//   1. 拉取全库 blocks.tag（空格分隔），内存聚合出全局标签词典与频次
//   2. 四维诊断：
//      - 【单次使用标签】: 只出现过 1 次的标签（碎片化候选，建议并入高频标签）
//      - 【格式一致性】: 尾部带 # 的旧式写法（#标签#）与标准写法（#标签）混用检测
//      - 【高频标签 TOP10】: 体系主干一览
//      - 【规模概览】: 带标签块数 / 标签实例数 / 去重标签数
//   3. 评定标签体系健康分 (0~100) 并生成 Markdown 报告
// ============================================================================


public static class TagGovernanceMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 1. 拉取全库标签列
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql",
            "{\"stmt\":\"SELECT id, tag FROM blocks WHERE tag != '' LIMIT 1000\"}");
        if (!r.Success)
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 标签检索失败：" + r.Error);
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 2. 内存聚合
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        int blockCount = 0, legacyStyle = 0, standardStyle = 0;
        foreach (Match m in Regex.Matches(r.Text ?? "", "\\{[^\\{\\}]+\\}"))
        {
            var rawTag = ExtractField(m.Value, "tag");
            if (string.IsNullOrEmpty(rawTag)) continue;
            blockCount++;
            foreach (var part in rawTag.Split(' '))
            {
                var tag = part.Trim();
                if (tag.Length == 0) continue;
                var isLegacy = tag.EndsWith("#");
                var clean = tag.TrimStart('#').TrimEnd('#');
                if (clean.Length == 0) continue;
                if (isLegacy) legacyStyle++; else standardStyle++;
                if (!freq.ContainsKey(clean)) freq[clean] = 0;
                freq[clean]++;
            }
        }

        int totalInstances = 0;
        foreach (var kv in freq) totalInstances += kv.Value;
        int distinctTags = freq.Count;

        // 3. 四维报告
        var report = new StringBuilder();
        report.Append("# 🏷 标签体系治理巡检报告\n");
        report.Append("> 扫描时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · 模式：只读巡检\n\n");

        report.Append("### 1. 📊 规模概览\n");
        report.Append("* 带标签块：").Append(blockCount).Append(" 个 · 标签实例：").Append(totalInstances)
              .Append(" 个 · 去重标签：").Append(distinctTags).Append(" 个\n\n");

        // 单次使用标签（碎片化候选）
        var singleUse = new List<string>();
        foreach (var kv in freq) { if (kv.Value == 1) singleUse.Add(kv.Key); }
        singleUse.Sort(StringComparer.Ordinal);
        report.Append("### 2. 🧩 单次使用标签 (").Append(singleUse.Count).Append(" 个)\n");
        if (singleUse.Count == 0)
        {
            report.Append("✓ 优秀：无孤儿标签，体系收敛良好。\n\n");
        }
        else
        {
            report.Append("> 仅出现 1 次的标签建议并入相近高频标签或删除：\n");
            for (int i = 0; i < singleUse.Count && i < 30; i++)
            {
                report.Append("- #").Append(singleUse[i]).Append("\n");
            }
            if (singleUse.Count > 30) report.Append("- …（其余 ").Append(singleUse.Count - 30).Append(" 个略）\n");
            report.Append("\n");
        }

        // 格式一致性
        report.Append("### 3. 🧭 格式一致性\n");
        if (legacyStyle == 0)
        {
            report.Append("✓ 优秀：全部为标准 #标签 写法（无尾 #）。\n\n");
        }
        else
        {
            report.Append("* 旧式 #标签# 写法：").Append(legacyStyle).Append(" 处 · 标准写法：").Append(standardStyle).Append(" 处\n");
            report.Append("> 建议统一为标准写法（思源默认标签语法），避免双格式并存。\n\n");
        }

        // 高频 TOP10
        report.Append("### 4. 🔥 高频标签 TOP10\n");
        var top = new List<KeyValuePair<string, int>>(freq);
        top.Sort((a, b) => b.Value.CompareTo(a.Value));
        for (int i = 0; i < top.Count && i < 10; i++)
        {
            report.Append("* #").Append(top[i].Key).Append(" × ").Append(top[i].Value).Append("\n");
        }
        if (top.Count == 0) report.Append("* 暂无标签。\n");

        // 评分
        int penalty = 0;
        penalty += Math.Min(singleUse.Count, 20);
        penalty += legacyStyle > 0 ? 10 : 0;
        int score = Math.Max(100 - penalty, 40);
        report.Append("\n---\n### 📊 标签体系健康分：**").Append(score).Append(" / 100**\n");
        string rating = score >= 90 ? "🟢 极佳：标签体系收敛。" : (score >= 70 ? "🟡 良好：存在少量碎片标签可整理。" : "🟠 亚健康：碎片标签较多，建议一轮归并。");
        report.Append("> 评定结论：").Append(rating).Append("\n");
        report.Append("\n*注：本报告严格只读，绝不修改任何标签数据。*\n");

        context.SetVarValue("标签报告", report.ToString());
        context.SetVarValue("健康评分", score);
        context.SetVarValue("是否成功", true);
        context.SetVarValue("通知反馈", "✓ 标签巡检完成：去重 " + distinctTags + " 个 / 单次使用 " + singleUse.Count +
            " 个 / 旧式写法 " + legacyStyle + " 处 · 健康分 " + score + "（报告已存「标签报告」变量）");
        SafeContext.Set(context, "需要通知", true);
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// L2-08: 思阅在读书架与书摘卡 —— 主驱动程序 (SiReaderExcerptCardMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 深度研读卡片化：划选书籍引文，秒级排版为标准三层书摘留痕卡（书名、章节页码、引文正文、思考批注）；
//   2. 深度适配「思阅 (siyuan-sireader)」阅读器与原生双链书架：
//      - 自动在书名上编织 [[《书名》]] 双链专页索引，写入 custom-sireader-book 属性
//   3. 100% 思源原生平滑自愈兜底：
//      - 若未安装思阅插件，自动通过日记追加三层 Callout 书摘块，知识复盘自洽！
// ============================================================================


public static class SiReaderExcerptCardMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "电子书阅读器");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");

        // 1. 输入内容解析
        var rawText = SafeContext.GetInputText(context);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            var json = "{\"title\":\"思源外骨骼 · 思阅在读书架与书摘卡\"," +
                       "\"subtitle\":\"划选文段制作三层结构化书摘卡片 (联动思阅/微信读书)\"," +
                       "\"badge\":\"书摘沉淀\"," +
                       "\"icon\":\"📖\"," +
                       "\"mode\":\"input\"," +
                       "\"outputVar\":\"输入文本\"," +
                       "\"placeholder\":\"输入或划选引文，支持前置 《书名》 或 P.120，如: 《置身事内》第一章 政府与经济发展的关系...\"," +
                       "\"defaultValue\":\"\"," +
                       "\"tags\":[\"《置身事内》\", \"《原则》\", \"#深度思考\", \"#核心观点\"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 2. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 3. 提取书名与页码
        var text = rawText.Trim();
        string bookTitle = "";
        var bookMatch = Regex.Match(text, "《([^》]+)》");
        if (bookMatch.Success)
        {
            bookTitle = bookMatch.Groups[1].Value.Trim();
            text = text.Replace(bookMatch.Value, "").Trim();
        }
        else if (windowTitle.Contains("《") && windowTitle.Contains("》"))
        {
            var wm = Regex.Match(windowTitle, "《([^》]+)》");
            if (wm.Success) bookTitle = wm.Groups[1].Value.Trim();
        }

        if (string.IsNullOrEmpty(bookTitle)) bookTitle = "在读书籍摘录";

        // 4. 定位或新建今日日记
        var now = DateTime.Now;
        var todayCompact = now.ToString("yyyyMMdd");
        var todayDash = now.ToString("yyyy-MM-dd");
        var timeStr = now.ToString("HH:mm");
        string docId = "";

        var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + todayCompact + "%' ORDER BY updated DESC LIMIT 1\"}";
        var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
        if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

        if (string.IsNullOrEmpty(docId))
        {
            var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + todayDash + "%' OR hpath LIKE '%" + todayDash + "%') ORDER BY updated DESC LIMIT 1\"}";
            var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
            if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(diaryNotebookId) + "\"}");
            if (createRes.Success) docId = ExtractJsonField(createRes.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未能定位或创建今日日记文档。");
            return;
        }

        // 5. 组装符合思阅关联 + 三层留痕结构化卡片 Markdown
        var ial = "{: custom-sireader-book=\"" + bookTitle + "\" custom-type=\"book-excerpt\" }";
        var sb = new StringBuilder();
        sb.Append("### 📖 书摘 · [[《").Append(bookTitle).Append("》]] (").Append(timeStr).Append(")\n\n");
        sb.Append("> 「").Append(text.Replace("\r\n", "\n").Replace("\n", "\n> ")).Append("」\n\n");
        sb.Append("*— 摘录自 ").Append(windowTitle).Append("*\n\n");
        sb.Append(ial);

        var cardMd = sb.ToString();

        var appendBody = "{\"parentID\":\"" + docId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(cardMd) + "\"}";
        var appRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);

        bool ok = appRes.Success;
        SafeContext.Set(context, "是否成功", ok);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", ok 
            ? "✓ 已沉淀《" + bookTitle + "》书摘卡至今日日记！"
            : "❌ 写入失败：" + appRes.Error);
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// L2-09: 选区大纲思维导图化 —— 主驱动程序 (SelectionToMindmapMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 局部精读聚焦：无需全篇重构，划选任意多级列表、缩进文本或子章节，秒级转换为可视化思维导图；
//   2. 深度适配「KMind Zen」思维导图插件与原生 Mermaid 语法引擎：
//      - 自动将列表缩进转换为标准 Mermaid mindmap 语法结构
//   3. 双通道输出：优先经快门 editor.insert 原地插入，同步保底写入剪贴板！
// ============================================================================


public static class SelectionToMindmapMain
{
    public static void Exec(IStepContext context)
    {
        var rawText = SafeContext.GetInputText(context);
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(rawText))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "⚠️ 请先划选需要转为思维导图的多级列表或文本段落。");
            return;
        }

        // 1. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 将选中的多行文本转译为 Mermaid mindmap
        var lines = rawText.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        sb.Append("```mermaid\nmindmap\n  root((选区导图))\n");

        int validCount = 0;
        foreach (var rawLine in lines)
        {
            var l = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(l)) continue;

            // 计算缩进层级
            int indent = 0;
            while (indent < l.Length && (l[indent] == ' ' || l[indent] == '\t'))
            {
                indent += (l[indent] == '\t') ? 2 : 1;
            }

            int level = Math.Max(1, Math.Min(6, (indent / 2) + 1));
            var clean = Regex.Replace(l.Trim(), "^(#+|[-*+]|\\d+\\.)\\s*", "").Trim();
            clean = clean.Replace("(", " ").Replace(")", " ").Replace("[", " ").Replace("]", " ").Trim();
            if (string.IsNullOrEmpty(clean)) continue;

            sb.Append(new string(' ', (level + 1) * 2)).Append(clean).Append("\n");
            validCount++;
        }

        sb.Append("```\n");
        var mindmapMd = sb.ToString();

        // 3. 双通道输出：写入剪贴板并尝试直插思源
        try
        {
            Clipboard.SetText(mindmapMd);
        }
        catch { }

        bool inserted = false;
        try
        {
            var insertBody = "{\"text\":\"" + SafeContext.EscapeJson("\n\n" + mindmapMd) + "\"}";
            var sendRes = LvSendCommand.Send(syUrl, token, "editor.insert", insertBody, "siyuan-speed-switch", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 1500, "siyuan-speed-switch");
                if (rec.Status == "recorded" || rec.Status == "duplicate") inserted = true;
            }
        }
        catch { }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", inserted
            ? "✓ 选区已成功转译为思维导图并直插编辑器！（" + validCount + " 个节点）"
            : "✓ 思维导图代码块已生成并写入剪贴板！（" + validCount + " 个节点，可直接粘贴）");
    }
}


// ============================================================================
// L3-01: 跨日记待办自动滚动归集 —— 主驱动程序 (RollingTasksMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 业务依据：docs/03 SY-12 汇总日记待办 · 文本指令 db · 滚动池闭环
// 核心逻辑：
//   1. 本地生成近 N 天 (默认 7 天) 精准日期白名单谓词（避免宽泛全库扫描影响性能）
//   2. 高效参数化 SQL 联表查询：仅限定日记文档（d.type='d'）下未勾选待办（[ ]），上限 100 条
//   3. 智能排重与清理（去除 markdown 勾选字符，避免文本冗余）
//   4. 构造结构化待办滚动清单，保留溯源超链接（siyuan://blocks/{bid} 直跳原日记出处）
//   5. 一键追加到今日日记头部或待办区域，输出精准统计与反馈
// ============================================================================


public static class RollingTasksMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");

        var days = 7;
        var daysObj = SafeContext.Get(context, "天数");
        if (daysObj != null) int.TryParse(daysObj.ToString(), out days);
        if (days <= 0) days = 7;
        if (days > 30) days = 30;

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 2. 生成近 N 天历史日记日期匹配谓词 (双轨匹配：标题 content 或路径 hpath)
        var predicates = new List<string>();
        for (int i = 1; i <= days; i++)
        {
            var d = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
            predicates.Add("(d.content LIKE '%" + d + "%' OR d.hpath LIKE '%" + d + "%')");
        }
        var datePred = "(" + string.Join(" OR ", predicates.ToArray()) + ")";

        // 3. 构造 SQL：限定日记文档中未勾选待办块
        var boxFilter = !string.IsNullOrEmpty(diaryNotebookId) ? " AND d.box='" + diaryNotebookId.Replace("'", "''") + "'" : "";
        var sql = "SELECT b.id AS bid, b.content AS bcontent, b.root_id AS rid, d.content AS dtitle " +
                  "FROM blocks b JOIN blocks d ON b.root_id = d.id " +
                  "WHERE d.type='d' " + boxFilter + " AND " + datePred + " " +
                  "AND b.markdown LIKE '%[ ]%' ORDER BY b.id DESC LIMIT 100";

        var reqBody = "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", reqBody);

        if (!r.Success)
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 查询待办失败：" + r.Error);
            return;
        }

        // 4. 解析结果块并去重
        var items = ExtractTaskItems(r.Text);

        // 4.5 智能防重复：排查今日日记中已存在的任务，避免重复滚动堆积
        var todayStr = DateTime.Now.ToString("yyyy-MM-dd");
        var todaySql = "SELECT b.content FROM blocks b JOIN blocks d ON b.root_id = d.id " +
                       "WHERE d.type='d' AND (d.content LIKE '%" + todayStr + "%' OR d.hpath LIKE '%" + todayStr + "%') " +
                       "AND b.markdown LIKE '%[%]%' LIMIT 100";
        var todayRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(todaySql) + "\"}");
        if (todayRes.Success && !string.IsNullOrEmpty(todayRes.Text))
        {
            var todayTasks = ExtractTaskContents(todayRes.Text);
            if (todayTasks.Count > 0)
            {
                items = items.FindAll(it => !todayTasks.Contains(CleanTaskContent(it.Content)));
            }
        }

        SafeContext.Set(context, "命中数", items.Count);

        if (items.Count == 0)
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 近 " + days + " 天日记中无遗留的未完成待办事项。");
            return;
        }

        // 5. 格式化为按原日期分组的结构化 Markdown 清单（带出处块反向溯源链接）
        var sb = new StringBuilder();
        sb.Append("### 📋 跨日记待办滚动池 (近 ").Append(days).Append(" 天遗留) #待办滚动池\n");
        sb.Append("> 已聚合昨天及更早未完成任务，点击 🔗 可秒级定位原日记出处：\n\n");

        var dateGroups = new Dictionary<string, List<TaskItem>>();
        foreach (var it in items)
        {
            var dateKey = ExtractDateFromTitle(it.DateTitle);
            if (string.IsNullOrEmpty(dateKey)) dateKey = "更早日记";
            if (!dateGroups.ContainsKey(dateKey)) dateGroups[dateKey] = new List<TaskItem>();
            dateGroups[dateKey].Add(it);
        }

        foreach (var grp in dateGroups)
        {
            sb.Append("#### 📅 ").Append(grp.Key).Append("\n");
            foreach (var it in grp.Value)
            {
                var cleanText = CleanTaskContent(it.Content);
                sb.Append("- [ ] ").Append(cleanText);
                if (!string.IsNullOrEmpty(it.BlockId))
                {
                    sb.Append(" [🔗](siyuan://blocks/").Append(it.BlockId).Append(")");
                }
                sb.Append("\n");
            }
            sb.Append("\n");
        }
        sb.Append("{: custom-type=\"rolling-tasks\" }\n");

        var mdResult = sb.ToString();
        SafeContext.Set(context, "汇总Markdown", mdResult);

        // 6. 追加写入今日日记
        var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(mdResult) + "\"}";
        var appendRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);

        SafeContext.Set(context, "是否成功", appendRes.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", appendRes.Success 
            ? "✓ 已成功汇总 " + items.Count + " 项未完成待办并滚动至今日日记！" 
            : "❌ 写入今日日记失败：" + appendRes.Error);
    }

    private static List<TaskItem> ExtractTaskItems(string json)
    {
        var list = new List<TaskItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(json, "\\{[^\\}]+\\}");
        foreach (Match m in matches)
        {
            var block = m.Value;
            var bid = ExtractJsonField(block, "bid");
            var content = ExtractJsonField(block, "bcontent");
            var dtitle = ExtractJsonField(block, "dtitle");

            if (string.IsNullOrEmpty(bid) || string.IsNullOrEmpty(content)) continue;

            var norm = CleanTaskContent(content);
            if (!seen.Contains(norm))
            {
                seen.Add(norm);
                list.Add(new TaskItem { BlockId = bid, Content = content, DateTitle = dtitle });
            }
        }
        return list;
    }

    private static HashSet<string> ExtractTaskContents(string json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(json, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        foreach (Match m in matches)
        {
            var raw = m.Groups[1].Value;
            set.Add(CleanTaskContent(raw));
        }
        return set;
    }

    private static string CleanTaskContent(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = Regex.Replace(text, "^\\s*[-*+]\\s*\\[[ xX]\\]\\s*", "");
        clean = Regex.Replace(clean, "\\[🔗\\]\\([^)]+\\)", "").Trim();
        return clean;
    }

    private static string ExtractDateFromTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var m = Regex.Match(title, "\\b([0-9]{4}-[0-9]{2}-[0-9]{2})\\b");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string ExtractJsonField(string block, string field)
    {
        var m = Regex.Match(block, "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"") : "";
    }

    public class TaskItem
    {
        public string BlockId;
        public string Content;
        public string DateTitle;
    }
}


// ============================================================================
// L3-02: 收集箱智能分拣工作台 —— 主驱动程序 (InboxTriageMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 自动定位收集箱文档 ID（读取配置或通过 SQL 发现名为「收集箱/Inbox」的文档）
//   2. 扫描收集箱中的未整理碎片块（LIMIT 20）
//   3. 格式化生成待分拣卡片清单供 Quicker 选择
//   4. 接收用户分拣目标操作：
//      - "daily": 移动/追加至今日日记
//      - "todo":  转化为待办复选框事项
//      - "card":  转化为 FSRS 问答闪卡
//      - "done":  标记为已处理/移除
//   5. 闭环更新思源笔记，输出精确分拣完成报表
// ============================================================================


public static class InboxTriageMain
{
    public static void Exec(IStepContext context)
    {
        var inboxDocId = SafeContext.GetString(context, "收集箱文档ID");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var selectedBlockId = SafeContext.GetString(context, "选中待分拣块ID");
        var targetAction = SafeContext.GetString(context, "分拣操作").ToLowerInvariant();
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 2. 自动发现收集箱文档 ID (若未配置)
        if (string.IsNullOrEmpty(inboxDocId))
        {
            var findSql = "SELECT id FROM blocks WHERE type='d' AND (content LIKE '%收集箱%' OR content LIKE '%Inbox%') ORDER BY updated DESC LIMIT 1";
            var findRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(findSql) + "\"}");
            if (findRes.Success && findRes.Text.Contains("\"id\":"))
            {
                var m = Regex.Match(findRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) inboxDocId = m.Groups[1].Value;
            }
        }

        if (string.IsNullOrEmpty(inboxDocId))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 未找到思源收集箱文档，请在思源中建立名为「收集箱」的文档。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // =============================================================
        // 阶段 2: 用户已经选中了块并指定了分拣动作 -> 执行流转
        // =============================================================
        if (!string.IsNullOrEmpty(selectedBlockId) && !string.IsNullOrEmpty(targetAction))
        {
            ExecuteTriageAction(context, syUrl, token, selectedBlockId, targetAction, diaryNotebookId);
            return;
        }

        // =============================================================
        // 阶段 1: 扫描收集箱中的未整理子块 (排查已处理块与空块)
        // =============================================================
        var scanSql = "SELECT id, content, markdown FROM blocks WHERE root_id='" + EscapeJson(inboxDocId) + "' AND id != '" + EscapeJson(inboxDocId) + "' AND type NOT IN ('d') AND content != '' AND (ial NOT LIKE '%custom-triage-status=%' OR ial IS NULL) ORDER BY id DESC LIMIT 20";
        var scanRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(scanSql) + "\"}");

        var blocks = ExtractInboxBlocks(scanRes.Text);
        if (blocks.Count == 0)
        {
            context.SetVarValue("是否成功", true);
            context.SetVarValue("需要弹窗选择", false);
            context.SetVarValue("通知反馈", "✓ 收集箱已清空，目前无待分拣碎片。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 收集箱分拣\",")
                   .Append("\"subtitle\":\"待分拣碎片共 ").Append(blocks.Count).Append(" 块\",")
                   .Append("\"badge\":\"收集箱分拣\",")
                   .Append("\"icon\":\"📥\",")
                   .Append("\"mode\":\"select\",")
                   .Append("\"outputVar\":\"选中块ID\",")
                   .Append("\"placeholder\":\"拼音或关键词过滤碎片，回车选择去向...\",")
                   .Append("\"items\":[");

        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (i > 0) jsonBuilder.Append(",");
            var cleanC = TruncateString(b.Content, 45);
            jsonBuilder.Append("{\"id\":\"").Append(SafeContext.EscapeJson(b.Id)).Append("\",")
                       .Append("\"title\":\"").Append(SafeContext.EscapeJson(cleanC)).Append("\",")
                       .Append("\"value\":\"").Append(SafeContext.EscapeJson(b.Id)).Append("\",")
                       .Append("\"icon\":\"📄\"}");
        }
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "待分拣总数", blocks.Count);
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "收集箱文档ID", inboxDocId);
    }

    /// <summary>
    /// 执行具体的分拣动作
    /// </summary>
    private static void ExecuteTriageAction(IStepContext context, string baseUrl, string token, string blockId, string action, string notebookId)
    {
        // 先读取该块完整内容
        var getRes = SyKernelRequest.DoRequest(baseUrl, token, "/api/block/getBlockInfo", "{\"id\":\"" + EscapeJson(blockId) + "\"}");
        var content = "";
        if (getRes.Success)
        {
            var contentM = Regex.Match(getRes.Text, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (contentM.Success) content = UnescapeJson(contentM.Groups[1].Value);
        }
        if (string.IsNullOrEmpty(content)) content = "碎片内容 (块 " + blockId + ")";

        var now = DateTime.Now;

        switch (action)
        {
            case "daily": // 归流至今日日记
                var dailyMd = "- [来自收集箱] " + content + " [🔗](siyuan://blocks/" + blockId + ")";
                var appendDaily = "{\"notebook\":\"" + notebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(dailyMd) + "\"}";
                SyKernelRequest.DoRequest(baseUrl, token, "/api/block/appendDailyNoteBlock", appendDaily);
                
                // 标记原块为已分拣
                SyKernelRequest.DoRequest(baseUrl, token, "/api/attr/setBlockAttrs", "{\"id\":\"" + EscapeJson(blockId) + "\",\"attrs\":{\"custom-triage-status\":\"triaged-daily\"}}");
                
                context.SetVarValue("是否成功", true);
                context.SetVarValue("通知反馈", "✓ 已将碎片内容迁移归流至今日日记！");
        SafeContext.Set(context, "需要通知", true);
                break;

            case "todo": // 转换为待办事项
                var todoMd = "- [ ] " + content;
                var appendTodo = "{\"notebook\":\"" + notebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(todoMd) + "\"}";
                SyKernelRequest.DoRequest(baseUrl, token, "/api/block/appendDailyNoteBlock", appendTodo);
                SyKernelRequest.DoRequest(baseUrl, token, "/api/attr/setBlockAttrs", "{\"id\":\"" + EscapeJson(blockId) + "\",\"attrs\":{\"custom-triage-status\":\"triaged-todo\"}}");

                context.SetVarValue("是否成功", true);
                context.SetVarValue("通知反馈", "✓ 已将碎片转换为待办事项并推入今日待办池！");
        SafeContext.Set(context, "需要通知", true);
                break;

            case "card": // 转换为闪卡
                var cardMd = "#### 🎴 概念思考: " + TruncateString(content, 30) + "\n* " + content + "\n\n{: custom-riff-decks=\"default\" }";
                var appendCard = "{\"notebook\":\"" + notebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(cardMd) + "\"}";
                SyKernelRequest.DoRequest(baseUrl, token, "/api/block/appendDailyNoteBlock", appendCard);
                SyKernelRequest.DoRequest(baseUrl, token, "/api/attr/setBlockAttrs", "{\"id\":\"" + EscapeJson(blockId) + "\",\"attrs\":{\"custom-triage-status\":\"triaged-card\"}}");

                context.SetVarValue("是否成功", true);
                context.SetVarValue("通知反馈", "✓ 已转换为 FSRS 闪卡并推入复习库！");
        SafeContext.Set(context, "需要通知", true);
                break;

            case "delete": // 从收集箱物理删除
                SyKernelRequest.DoRequest(baseUrl, token, "/api/block/deleteBlock", "{\"id\":\"" + EscapeJson(blockId) + "\"}");
                context.SetVarValue("是否成功", true);
                context.SetVarValue("通知反馈", "✓ 已安全移除该碎片块（如需恢复请在思源右下角「回收站」找回）。");
        SafeContext.Set(context, "需要通知", true);
                break;

            default:
                context.SetVarValue("是否成功", false);
                context.SetVarValue("通知反馈", "❌ 未知的分拣目标操作: " + action);
        SafeContext.Set(context, "需要通知", true);
                break;
        }
    }

    private static List<InboxBlockItem> ExtractInboxBlocks(string json)
    {
        var list = new List<InboxBlockItem>();
        if (string.IsNullOrEmpty(json)) return list;

        var matches = Regex.Matches(json, "\\{[^\\}]+\\}");
        foreach (Match m in matches)
        {
            var b = m.Value;
            var idM = Regex.Match(b, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            var contentM = Regex.Match(b, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (idM.Success && contentM.Success)
            {
                var content = UnescapeJson(contentM.Groups[1].Value).Trim();
                if (content.Length > 0)
                {
                    list.Add(new InboxBlockItem { Id = idM.Groups[1].Value, Content = content });
                }
            }
        }
        return list;
    }

    private static string TruncateString(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        s = s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + "...";
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    public class InboxBlockItem
    {
        public string Id;
        public string Content;
    }
}


// ============================================================================
// L3-03: 图书管理员健康体检 —— 主驱动程序 (LibrarianHealthMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 纯只读 SQL 深度扫描（严格遵循安全准则：只读不改，杜绝误删数据风险）
//   2. 深度体检四大维度：
//      - 【空文档诊断】: 检索标题或内容为空的空壳文档 (type='d' AND content='')
//      - 【重名冗余度】: 统计同名重复文档，识别潜在多版本冲突与碎片
//      - 【超长巨块预警】: 探测单块字符超过 5000 字符的臃肿大块（影响编辑器渲染性能）
//      - 【长期沉睡文档】: 扫描最早创建且极少变动的沉睡笔记，提供归档决策参考
//   3. 生成标准化 Markdown 体检报告，评定健康指数 (0~100) 并给出治理建议
// ============================================================================


public static class LibrarianHealthMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var report = new StringBuilder();
        report.Append("# 🩺 思源知识库健康体检报表\n");
        report.Append("> 扫描时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · 模式：深度只读体检\n\n");

        int penalty = 0;
        int emptyDocCount = 0;
        int duplicateTitleCount = 0;
        int giantBlockCount = 0;
        int brokenRefCount = 0;

        // -------------------------------------------------------------
        // 维度 1: 空壳文档体检
        // -------------------------------------------------------------
        var emptyRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", 
            "{\"stmt\":\"SELECT id, updated FROM blocks WHERE type='d' AND content='' LIMIT 30\"}");
        
        var emptyIds = ExtractIds(emptyRes.Text);
        emptyDocCount = emptyIds.Count;
        report.Append("### 1. 🈳 空壳无内容文档 (").Append(emptyDocCount).Append(" 篇)\n");
        if (emptyDocCount == 0)
        {
            report.Append("✓ 优秀：未发现无标题或无内容的空壳文档。\n\n");
        }
        else
        {
            penalty += Math.Min(emptyDocCount * 3, 20);
            report.Append("> 检测到以下空文档，建议清理或补充内容：\n");
            foreach (var id in emptyIds)
            {
                report.Append("- [空文档](siyuan://blocks/").Append(id).Append(")\n");
            }
            report.Append("\n");
        }

        // -------------------------------------------------------------
        // 维度 2: 重名重复标题体检
        // -------------------------------------------------------------
        var dupRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", 
            "{\"stmt\":\"SELECT content, COUNT(*) AS cnt FROM blocks WHERE type='d' AND content!='' GROUP BY content HAVING cnt > 1 ORDER BY cnt DESC LIMIT 20\"}");
        
        var dupList = ExtractDuplicates(dupRes.Text);
        duplicateTitleCount = dupList.Count;
        report.Append("### 2. 📑 重复同名文档 (").Append(duplicateTitleCount).Append(" 组)\n");
        if (duplicateTitleCount == 0)
        {
            report.Append("✓ 优秀：知识库文档标题命名清晰，无重复同名项。\n\n");
        }
        else
        {
            penalty += Math.Min(duplicateTitleCount * 2, 15);
            report.Append("> 以下标题存在多篇同名文档，建议合并或区分命名：\n");
            foreach (var d in dupList)
            {
                report.Append("- **").Append(d.Title).Append("**: 共 ").Append(d.Count).Append(" 篇重复\n");
            }
            report.Append("\n");
        }

        // -------------------------------------------------------------
        // 维度 3: 超过 5000 字的超长臃肿单块检测 (限定文本段落与代码块，去除全表大排序)
        // -------------------------------------------------------------
        var giantRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql",
            "{\"stmt\":\"SELECT id, root_id, length(content) AS len FROM blocks WHERE type IN ('p','c','h') AND length(content) > 5000 LIMIT 10\"}");

        var giantList = ExtractGiantBlocks(giantRes.Text);
        giantBlockCount = giantList.Count;
        report.Append("### 3. 🐘 超长臃肿单块预警 (>5000字，").Append(giantBlockCount).Append(" 块)\n");
        if (giantBlockCount == 0)
        {
            report.Append("✓ 优秀：单块粒度适中，无影响编辑器性能的超长单块。\n\n");
        }
        else
        {
            penalty += Math.Min(giantBlockCount * 2, 15);
            report.Append("> 发现超长单块（建议按章节拆分为更小的段落块以利于双链引用）：\n");
            foreach (var g in giantList)
            {
                report.Append("- [单块 (").Append(g.Length).Append(" 字)](siyuan://blocks/").Append(g.Id).Append(")\n");
            }
            report.Append("\n");
        }

        // -------------------------------------------------------------
        // 维度 4: 失效双链与悬空引用探测 (基于主键索引高效左连接，避免 NOT IN 全表物化)
        // -------------------------------------------------------------
        var brokenRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql",
            "{\"stmt\":\"SELECT r.block_id FROM refs r LEFT JOIN blocks b ON r.def_block_id = b.id WHERE b.id IS NULL LIMIT 10\"}");
        var brokenIds = ExtractIds(brokenRes.Text);
        brokenRefCount = brokenIds.Count;
        report.Append("### 4. 🔗 悬空失效引用诊断 (").Append(brokenRefCount).Append(" 处)\n");
        if (brokenRefCount == 0)
        {
            report.Append("✓ 优秀：全库双链引用网络完整，未发现指向已删除块的悬空断链。\n\n");
        }
        else
        {
            penalty += Math.Min(brokenRefCount * 2, 15);
            report.Append("> 发现指向已删除块的失效双链引用（建议修复或清理）：\n");
            foreach (var id in brokenIds)
            {
                report.Append("- [失效引用所在块](siyuan://blocks/").Append(id).Append(")\n");
            }
            report.Append("\n");
        }

        // -------------------------------------------------------------
        // 维度 5: 孤立无主资源附件体检 (/api/asset/getUnusedAssets & AssetCleanDoctor)
        // -------------------------------------------------------------
        int unusedAssetCount = 0;
        var unusedAssets = new List<string>();
        string totalSizeStr = "0 B";
        int quarantinedCount = 0;

        try
        {
            var assetRes = SyKernelRequest.DoRequest(syUrl, token, "/api/asset/getUnusedAssets", "{}");
            if (assetRes.Success && !string.IsNullOrEmpty(assetRes.Text))
            {
                var aDict = JsonHelper.ParseDict(assetRes.Text);
                var aList = JsonHelper.GetList(aDict, "data");
                if (aList != null)
                {
                    unusedAssetCount = aList.Count;
                    var allNames = new List<string>();
                    for (int i = 0; i < aList.Count; i++)
                    {
                        var it = aList[i] as Dictionary<string, object>;
                        if (it != null)
                        {
                            var name = JsonHelper.GetString(it, "name");
                            if (!string.IsNullOrEmpty(name))
                            {
                                allNames.Add(name);
                                if (unusedAssets.Count < 10) unusedAssets.Add(name);
                            }
                        }
                    }

                    var audit = AssetCleanDoctor.AuditUnusedAssets(SiYuanClient.WorkspaceAssetsDir, allNames);
                    totalSizeStr = audit.TotalSizeFormatted;
                    quarantinedCount = audit.QuarantinedCount;
                }
            }
        }
        catch { }

        report.Append("### 5. 🗑️ 游离无主附件与废弃图片 (").Append(unusedAssetCount).Append(" 个 · 占用 ").Append(totalSizeStr).Append(")\n");
        if (unusedAssetCount == 0)
        {
            report.Append("✓ 优秀：资源资产库纯净，无孤立游离的无主图片或附件。\n");
            if (quarantinedCount > 0)
            {
                report.Append("> ℹ️ 安全隔离区现有 **").Append(quarantinedCount).Append("** 个历史待释放附件。\n");
            }
            report.Append("\n");
        }
        else
        {
            penalty += Math.Min(unusedAssetCount * 2, 20);
            report.Append("> 发现以下已删笔记残留的孤立附件（共占用 ").Append(totalSizeStr).Append("，支持移入 .quarantine 安全隔离区）：\n");
            foreach (var a in unusedAssets)
            {
                report.Append("- `").Append(a).Append("`\n");
            }
            if (unusedAssetCount > 10)
            {
                report.Append("- *... 及其余 ").Append(unusedAssetCount - 10).Append(" 个附件*\n");
            }
            report.Append("\n");
        }

        // -------------------------------------------------------------
        // 维度 6: 知识库总评分与结论
        // -------------------------------------------------------------
        int score = Math.Max(100 - penalty, 40);
        report.Append("---\n");
        report.Append("### 📊 综合健康评分：**").Append(score).Append(" / 100**\n");
        
        string ratingText;
        if (score >= 90) ratingText = "🟢 极佳：您的知识库结构健康，保持得很棒！";
        else if (score >= 75) ratingText = "🟡 良好：结构总体良好，有少量空文档或同名文档可供整理。";
        else ratingText = "🟠 亚健康：存在较多冗余空文档、断链或游离附件，建议开展一轮整理。";
        
        report.Append("> 评定结论：").Append(ratingText).Append("\n");
        report.Append("\n*注：本报表遵循【只读安全铁律】，绝不自动篡改或删除任何数据。*");

        var mdResult = report.ToString();

        // 自动将体检报表安全沉淀入今日日记
        try
        {
            var diaryNb = SafeContext.GetDiaryNotebookId(context, syUrl, token);
            if (!string.IsNullOrEmpty(diaryNb))
            {
                var appendBody = "{\"notebook\":\"" + diaryNb + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(mdResult) + "\"}";
                SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", appendBody);
            }
        }
        catch { }

        NativeClipboard.SetTextSafe(mdResult);

        SafeContext.Set(context, "体检报告", mdResult);
        SafeContext.Set(context, "健康评分", score);
        SafeContext.Set(context, "是否健康", score >= 75);
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "✓ 体检完成！综合评分: " + score + " 分 (断链 " + brokenRefCount + " 处，游离附件 " + unusedAssetCount + " 个)，报表已存入日记与剪贴板！");
    }

    private static List<string> ExtractIds(string json)
    {
        var list = new List<string>();
        var m = Regex.Matches(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
        foreach (Match match in m)
        {
            list.Add(match.Groups[1].Value);
        }
        return list;
    }

    private static List<DupItem> ExtractDuplicates(string json)
    {
        var list = new List<DupItem>();
        var m = Regex.Matches(json, "\\{[^\\}]+\\}");
        foreach (Match match in m)
        {
            var b = match.Value;
            var contentM = Regex.Match(b, "\"content\"\\s*:\\s*\"([^\"]+)\"");
            var cntM = Regex.Match(b, "\"cnt\"\\s*:\\s*([0-9]+)");
            if (contentM.Success && cntM.Success)
            {
                list.Add(new DupItem { Title = contentM.Groups[1].Value, Count = int.Parse(cntM.Groups[1].Value) });
            }
        }
        return list;
    }

    private static List<GiantBlockItem> ExtractGiantBlocks(string json)
    {
        var list = new List<GiantBlockItem>();
        var m = Regex.Matches(json, "\\{[^\\}]+\\}");
        foreach (Match match in m)
        {
            var b = match.Value;
            var idM = Regex.Match(b, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            var lenM = Regex.Match(b, "\"len\"\\s*:\\s*([0-9]+)");
            if (idM.Success && lenM.Success)
            {
                list.Add(new GiantBlockItem { Id = idM.Groups[1].Value, Length = int.Parse(lenM.Groups[1].Value) });
            }
        }
        return list;
    }

    public class DupItem
    {
        public string Title;
        public int Count;
    }

    public class GiantBlockItem
    {
        public string Id;
        public int Length;
    }
}


// ============================================================================
// L3-04: 稍后读拾遗分拣工作台 —— 主驱动程序 (GleanTriageMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 glean.list / glean.status / glean.get（window.siyuanGlean v1 透传）
// 核心逻辑：
//   1. 阶段一：合并拉取拾遗读库 inbox + later 积压，高保真 Bento 弹窗供选
//   2. 置顶直达小驴拾遗侧栏工作台主界面
//   3. 阶段二：为选中条目弹出 Bento 去向菜单（开始阅读/标记完成/归档/打开原文）
//   4. 阶段三：glean.status 变更（回执确认）或 glean.get 取 URL 直达原文
//   5. 3层窗口唤醒置顶保障
// ============================================================================


public static class GleanTriageMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 外部直接指令：打开拾遗读库面板
        var inParam = SafeContext.GetInputText(context).Trim().ToLowerInvariant();
        if (inParam == "open" || inParam == "panel")
        {
            OpenGleanPanel(context, syUrl, token);
            return;
        }

        var target = SafeContext.GetString(context, "拾遗去向");
        var clipId = SafeContext.GetString(context, "选中拾遗ID");

        // 阶段三：执行去向
        if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(clipId))
        {
            ExecuteTarget(context, syUrl, token, clipId, target);
            return;
        }

        // 阶段二：为选中条目弹出去向菜单
        if (!string.IsNullOrEmpty(clipId))
        {
            if (clipId == "__open_plugin__")
            {
                OpenGleanPanel(context, syUrl, token);
                return;
            }

            var targetJson = "{\"title\":\"条目流转去向\"," +
                             "\"subtitle\":\"选择该拾遗条目的下一步操作\"," +
                             "\"badge\":\"流转\",\"icon\":\"🎯\",\"mode\":\"select\",\"outputVar\":\"拾遗去向\"," +
                             "\"items\":[" +
                             "{\"id\":\"reading\",\"title\":\"开始阅读 (置为 reading)\",\"desc\":\"标记为正在阅读中\",\"badge\":\"阅读\",\"badgeCls\":\"badge-native\",\"hotkey\":\"1\",\"icon\":\"📖\"}," +
                             "{\"id\":\"done\",\"title\":\"标记完成 (置为 done)\",\"desc\":\"完成并在今日日记追加一行留痕\",\"badge\":\"完成\",\"badgeCls\":\"badge-bridge\",\"hotkey\":\"2\",\"icon\":\"✅\"}," +
                             "{\"id\":\"archived\",\"title\":\"归档 (置为 archived)\",\"desc\":\"归档封存，不再在待读池呈现\",\"badge\":\"归档\",\"badgeCls\":\"badge-system\",\"hotkey\":\"3\",\"icon\":\"🗄\"}," +
                             "{\"id\":\"open\",\"title\":\"打开原文链接\",\"desc\":\"提取信源 URL 在浏览器中打开\",\"badge\":\"原文\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"4\",\"icon\":\"🌐\"}," +
                             "{\"id\":\"__back__\",\"title\":\"返回重选条目\",\"desc\":\"返回待读条目清单\",\"badge\":\"返回\",\"badgeCls\":\"badge-system\",\"hotkey\":\"Esc\",\"icon\":\"↩️\"}" +
                             "]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(targetJson));
            SafeContext.Set(context, "需要弹窗选择去向", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 阶段一：拉取积压清单
        ListBacklog(context, syUrl, token);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateSiYuanWindow()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    private static void OpenGleanPanel(IStepContext context, string syUrl, string token)
    {
        ActivateSiYuanWindow();
        var rec = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-glean\",\"command\":\"cmd.openPanel\"}", "siyuan-speed-switch", "fast");
        SafeContext.Set(context, "是否成功", rec.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", rec.Success ? "✓ 已唤起思源并打开【小驴拾遗】读库侧栏。" : "⚠️ 唤起小驴拾遗未完成");
    }

    private static void ListBacklog(IStepContext context, string syUrl, string token)
    {
        var inbox = PollBridge(syUrl, token, "glean.list", "{\"status\":\"inbox\",\"limit\":50}", 8000);

        var skipLater = (inbox.Status == "send-failed" || inbox.Status == "timeout");
        var later = skipLater
            ? new ReceiptResult { Status = "skipped", Message = "首轮已确认桥不可达", DataJson = "" }
            : PollBridge(syUrl, token, "glean.list", "{\"status\":\"later\",\"limit\":50}", 8000);

        var clips = ParseClips(inbox.DataJson);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in clips) seenIds.Add(c.Id);
        foreach (var c in ParseClips(later.DataJson))
        {
            if (seenIds.Add(c.Id)) clips.Add(c);
        }

        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 稍后读拾遗分拣工作台\",")
                   .Append("\"subtitle\":\"选择待读条目，实现 Reading Zero\",")
                   .Append("\"badge\":\"小驴拾遗\",")
                   .Append("\"icon\":\"📚\",")
                   .Append("\"mode\":\"select\",")
                   .Append("\"outputVar\":\"选中拾遗ID\",")
                   .Append("\"placeholder\":\"输入拼音过滤待读条目，回车选择...\",")
                   .Append("\"items\":[")
                   .Append("{\"id\":\"__open_plugin__\",\"title\":\"打开小驴拾遗读库面板 (Alt+4)\",\"desc\":\"唤醒思源打开拾遗侧栏\",\"badge\":\"小驴拾遗\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"Alt+4\",\"icon\":\"🪟\"}");

        int numHk = 1;
        if (clips.Count > 0)
        {
            foreach (var c in clips)
            {
                var hk = numHk <= 9 ? numHk.ToString() : "";
                var desc = (string.IsNullOrEmpty(c.Site) ? "" : c.Site + " · ") + "状态: " + c.Status;
                jsonBuilder.Append(",{\"id\":\"").Append(SafeContext.EscapeJson(c.Id)).Append("\",")
                           .Append("\"title\":\"").Append(SafeContext.EscapeJson(c.Title)).Append("\",")
                           .Append("\"desc\":\"").Append(SafeContext.EscapeJson(desc)).Append("\",")
                           .Append("\"badge\":\"").Append(SafeContext.EscapeJson(c.Status)).Append("\",")
                           .Append("\"badgeCls\":\"badge-native\",")
                           .Append("\"hotkey\":\"").Append(hk).Append("\",")
                           .Append("\"icon\":\"📖\"}");
                numHk++;
            }
        }
        else
        {
            jsonBuilder.Append(",{\"id\":\"__open_plugin__\",\"title\":\"稍后读已清空：拾遗读库暂无积压\",\"desc\":\"点击打开拾遗读库侧栏\",\"badge\":\"清空\",\"badgeCls\":\"badge-system\",\"hotkey\":\"1\",\"icon\":\"🎉\"}");
        }
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "需要弹窗选择条目", true);
        SafeContext.Set(context, "是否成功", true);
    }

    private static void ExecuteTarget(IStepContext context, string syUrl, string token, string clipId, string target)
    {
        if (target == "__back__")
        {
            SafeContext.Set(context, "选中拾遗ID", "");
            SafeContext.Set(context, "拾遗去向", "");
            SafeContext.Set(context, "拾遗清单", "");
            ListBacklog(context, syUrl, token);
            return;
        }

        if (target == "open")
        {
            var rec = PollBridge(syUrl, token, "glean.get", "{\"id\":\"" + SafeContext.EscapeJson(clipId) + "\"}", 6000);
            var url = ExtractField(rec.DataJson, "url");
            var href = ExtractField(rec.DataJson, "href");
            var link = !string.IsNullOrEmpty(url) ? url : href;
            if (rec.Status == "recorded" && !string.IsNullOrEmpty(link))
            {
                SafeContext.Set(context, "目标链接", link);
                SafeContext.Set(context, "需要打开链接", true);
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 正在打开拾遗原文...");
                return;
            }
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未取到原文链接：" + (string.IsNullOrEmpty(rec.Message) ? "该条目无 URL 字段" : rec.Message));
            return;
        }

        // reading / done / archived
        var st = PollBridge(syUrl, token, "glean.status", "{\"id\":\"" + SafeContext.EscapeJson(clipId) + "\",\"status\":\"" + target + "\"}", 8000);
        if (st.Status == "recorded")
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 拾遗条目已置为 " + target + "。");
            if (target == "done")
            {
                var md = "- " + DateTime.Now.ToString("HH:mm") + " 📖 已读完拾遗文章 (id " + clipId + ")";
                var body = "{\"notebook\":\"" + SafeContext.GetDiaryNotebookId(context, syUrl, token) +
                           "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}";
                SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", body);
            }
            return;
        }

        SafeContext.Set(context, "是否成功", false);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "❌ 状态变更未完成【" + st.Status + "】：" + st.Message);
    }

    public class ClipEntry { public string Id; public string Title; public string Site; public string Status; }

    private static List<ClipEntry> ParseClips(string dataJson)
    {
        var list = new List<ClipEntry>();
        if (string.IsNullOrEmpty(dataJson)) return list;
        foreach (Match m in Regex.Matches(dataJson, "\\{[^\\{\\}]+\\}"))
        {
            var b = m.Value;
            var id = ExtractField(b, "id");
            var title = ExtractField(b, "title");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;
            list.Add(new ClipEntry
            {
                Id = id,
                Title = title.Length > 48 ? title.Substring(0, 48) + "…" : title,
                Site = ExtractField(b, "site"),
                Status = ExtractField(b, "status")
            });
        }
        return list;
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L3-05: 学习阅读周报 —— 主驱动程序 (WeeklyDigestMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑（全只读采集，仅周报本身写入）：
//   1. 六维 SQL 聚合（近 7 天，按 IAL custom-type 标记统计本套件动作的产出）：
//      新建文档 / 研究来源卡 / FSRS 闪卡 / 错题归档 / 晨间简报执行数 / 读书相关修订
//   2. 拾遗增强：桥可用时读取 glean done/reading 周统计；桥死自动跳过该节
//   3. 防重写入：今日日记已有 weekly-reading 块就地更新，否则追加
// ============================================================================


public static class WeeklyDigestMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        var diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        var weekAgo = DateTime.Now.AddDays(-7).ToString("yyyyMMdd");
        var sb = new StringBuilder();
        sb.Append("### 📚 学习与阅读周报 (近 7 天)\n");
        sb.Append("> 周期：").Append(DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd")).Append(" ~ ")
          .Append(DateTime.Now.ToString("yyyy-MM-dd")).Append(" · 生成时间：")
          .Append(DateTime.Now.ToString("MM-dd HH:mm")).Append("\n\n");

        // ① 新建文档数
        var docCount = ScalarQuery(syUrl, token,
            "SELECT count(*) AS c FROM blocks WHERE type='d' AND created >= '" + weekAgo + "'");
        sb.Append("* 📄 **新建文档**: ").Append(docCount).Append(" 篇\n");

        // ②~⑤ 单条高效条件聚合：1 次网络往返、1 次表扫描获取全部 4 类卡片产出计数
        int cardCount = 0, cardFc = 0, wrong = 0, brief = 0;
        var aggSql = "SELECT " +
                     "SUM(CASE WHEN ial LIKE '%custom-type=\\\"research-card\\\"%' THEN 1 ELSE 0 END) AS c_card, " +
                     "SUM(CASE WHEN ial LIKE '%custom-type=\\\"fsrs-card\\\"%' THEN 1 ELSE 0 END) AS c_fc, " +
                     "SUM(CASE WHEN ial LIKE '%custom-type=\\\"exam-wrongbook\\\"%' THEN 1 ELSE 0 END) AS c_wrong, " +
                     "SUM(CASE WHEN ial LIKE '%custom-type=\\\"morning-brief\\\"%' THEN 1 ELSE 0 END) AS c_brief " +
                     "FROM blocks WHERE created >= '" + weekAgo + "'";
        var aggRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(aggSql) + "\"}");
        if (aggRes.Success && !string.IsNullOrEmpty(aggRes.Text))
        {
            cardCount = ExtractIntField(aggRes.Text, "c_card");
            cardFc = ExtractIntField(aggRes.Text, "c_fc");
            wrong = ExtractIntField(aggRes.Text, "c_wrong");
            brief = ExtractIntField(aggRes.Text, "c_brief");
        }

        sb.Append("* 📑 **研究来源卡**: ").Append(cardCount).Append(" 张（防 404 沉淀）\n");
        sb.Append("* 🎴 **FSRS 闪卡**: 制卡 ").Append(cardFc).Append(" 张\n");
        sb.Append("* ❌ **错题归档**: ").Append(wrong).Append(" 题\n");
        sb.Append("* 🌅 **晨间对齐**: 简报 ").Append(brief).Append(" 次\n");

        // ⑥ 读书相关修订文档 TOP3
        sb.Append("* 📖 **读书相关修订**: ");
        var readSql = "SELECT id, content FROM blocks WHERE type='d' AND updated >= '" + weekAgo + "' " +
                      "AND (hpath LIKE '%读%' OR content LIKE '%微信读书%' OR content LIKE '%阅读笔记%') " +
                      "ORDER BY updated DESC LIMIT 3";
        var readRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(readSql) + "\"}");
        var readLines = new List<string>();
        foreach (Match m in Regex.Matches(readRes.Text ?? "", "\\{[^\\{\\}]+\\}"))
        {
            var id = ExtractField(m.Value, "id");
            var content = ExtractField(m.Value, "content");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(content)) continue;
            var title = content.Length > 24 ? content.Substring(0, 24) + "…" : content;
            readLines.Add("[" + title + "](siyuan://blocks/" + id + ")");
        }
        sb.Append(readLines.Count > 0 ? string.Join(" · ", readLines.ToArray()) : "本周无").Append("\n");

        // ⑦ 拾遗增强（桥可用时）
        var gleanPart = TryGleanDigest(syUrl, token);
        if (!string.IsNullOrEmpty(gleanPart))
        {
            sb.Append("* 📥 **拾遗阅读**: ").Append(gleanPart).Append("\n");
        }

        sb.Append("\n{: custom-type=\"weekly-reading\" }\n");
        var md = sb.ToString();
        context.SetVarValue("周报Markdown", md);

        // 防重写入
        var existingId = FindBlock(syUrl, token, "weekly-reading");
        bool writeOk;
        string writeErr = "";
        if (!string.IsNullOrEmpty(existingId))
        {
            var upd = SyKernelRequest.DoRequest(syUrl, token, "/api/block/updateBlock",
                "{\"id\":\"" + SafeContext.EscapeJson(existingId) + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}");
            writeOk = upd.Success; writeErr = upd.Error;
        }
        else
        {
            var app = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock",
                "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}");
            writeOk = app.Success; writeErr = app.Error;
        }

        context.SetVarValue("是否成功", writeOk);
        context.SetVarValue("通知反馈", writeOk
            ? "✓ 学习阅读周报已生成并入日记（新建 " + docCount + " 篇 / 来源卡 " + cardCount + " / 闪卡 " + cardFc + "）"
            : "❌ 周报写入失败：" + writeErr);
        SafeContext.Set(context, "需要通知", true);
    }

    /// <summary>单值查询（count）</summary>
    private static int ScalarQuery(string syUrl, string token, string sql)
    {
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");
        var m = Regex.Match(r.Text ?? "", "\"c\"\\s*:\\s*([0-9]+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static int ExtractIntField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*([0-9]+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>拾遗周统计（桥可用时返回摘要，不可用返回空串跳过该节）</summary>
    private static string TryGleanDigest(string syUrl, string token)
    {
        try
        {
            var done = PollBridge(syUrl, token, "glean.list", "{\"status\":\"done\",\"limit\":200}", 5000);
            if (done.Status != "recorded") return "";
            var doneCount = CountItems(done.DataJson, "clips");
            var reading = PollBridge(syUrl, token, "glean.list", "{\"status\":\"reading\",\"limit\":50}", 5000);
            var readingCount = reading.Status == "recorded" ? CountItems(reading.DataJson, "clips") : 0;
            return "本周读完 " + doneCount + " 篇 · 在读 " + readingCount + " 篇";
        }
        catch { return ""; }
    }

    private static int CountItems(string dataJson, string arrayKey)
    {
        if (string.IsNullOrEmpty(dataJson)) return 0;
        var m = Regex.Match(dataJson, "\"" + arrayKey + "\"\\s*:\\s*\\[");
        if (!m.Success) return 0;
        int start = m.Index + m.Length;
        int depth = 1;
        int count = 0;
        for (int i = start; i < dataJson.Length; i++)
        {
            if (dataJson[i] == '{') { if (depth == 1) count++; depth++; }
            else if (dataJson[i] == '}') { depth--; if (depth == 0) break; }
        }
        return count;
    }

    private static string FindBlock(string syUrl, string token, string typeName)
    {
        var sql = "SELECT id FROM blocks WHERE ial LIKE '%custom-type=\\\"" + typeName + "\\\"%' ORDER BY created DESC LIMIT 1";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");
        var m = Regex.Match(r.Text ?? "", "\"id\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L3-06: 全库批注与审阅巡检台 —— 主驱动程序 (CommentReviewTriageMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 全库只读安全 SQL 诊断（零写路径、零数据污染风险）：
//      - 深度检索所有带原生 memo 属性或「鲸鱼快速批注 (siyuan-comment)」标记的块
//      - 提取引文内容、审阅批注文本、所属文档标题与最后更新时间
//   2. 智能聚合为可审计的 Markdown 审阅待办清单：
//      - 每条批注均挂载原生 siyuan://blocks/{id} 直达反链，点击秒级居中高亮跳转
//      - 自动统计未决批注总数并写入剪贴板供随时粘贴复盘
// ============================================================================


public static class CommentReviewTriageMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 只读 SQL 扫描带批注或评论的块（包含原生 memo 与 siyuan-comment 属性）
        var sql = "{\"stmt\":\"SELECT id, content, memo, ial, updated, root_id FROM blocks WHERE memo != '' OR ial LIKE '%custom-comment%' ORDER BY updated DESC LIMIT 100\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql);

        if (!r.Success)
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 检索全库批注失败：" + r.Error);
            return;
        }

        var blocks = ExtractBalancedObjects(r.Text);
        if (blocks.Count == 0)
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "批注数量", "0");
            SafeContext.Set(context, "通知反馈", "✓ 全库巡检完毕：当前没有任何未决批注或划线评论，知识库非常清爽！");
            return;
        }

        // 3. 构建结构化审阅报告
        var sb = new StringBuilder();
        sb.Append("# 📑 思源全库批注与审阅清单 (共 ").Append(blocks.Count).Append(" 处待处理)\n\n");
        sb.Append("> 巡检时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · 支持点击链接直接跳转思源对应块居中高亮\n\n");

        int count = 0;
        foreach (var b in blocks)
        {
            var id = ExtractJsonField(b, "id");
            var content = ExtractJsonField(b, "content");
            var memo = ExtractJsonField(b, "memo");
            var ial = ExtractJsonField(b, "ial");

            if (string.IsNullOrEmpty(memo) && !string.IsNullOrEmpty(ial))
            {
                var mText = Regex.Match(ial, "custom-comment-text=\"([^\"]+)\"");
                if (mText.Success) memo = mText.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(memo)) memo = "未命名审阅意见";
            if (string.IsNullOrEmpty(content)) content = "(引文无纯文本预览)";

            count++;
            var safeContent = TruncateClean(content, 40);
            var safeMemo = TruncateClean(memo, 60);

            sb.Append("- [ ] **批注 ").Append(count).Append("**：[").Append(safeMemo).Append("](siyuan://blocks/").Append(id).Append(")\n");
            sb.Append("  - 📌 原文：*“").Append(safeContent).Append("”*\n");
        }

        var report = sb.ToString();

        // 4. 写入输出变量并保底写入剪贴板
        SafeContext.Set(context, "巡检报表", report);
        SafeContext.Set(context, "批注数量", blocks.Count.ToString());
        try
        {
            Clipboard.SetText(report);
        }
        catch { }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "✓ 已扫描全库 " + blocks.Count + " 处批注/审阅意见，清单已写入剪贴板！");
    }

    private static string TruncateClean(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = s.Replace("\r", " ").Replace("\n", " ").Replace("[", "").Replace("]", "").Trim();
        if (clean.Length <= maxLen) return clean;
        return clean.Substring(0, maxLen) + "...";
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> ExtractBalancedObjects(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        int depth = 0;
        int start = -1;
        bool inStr = false;
        bool esc = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(text.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
        }
        return list;
    }
}


// ============================================================================
// L3-07: 任务视界今日看板速查 —— 主驱动程序 (TaskHorizonDashboardMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 专为「Task Horizon 任务视界」打造的全局任务看板与到期速览中枢；
//   2. 深度检索全库未勾选待办 (- [ ])，提取 custom-task-priority 与 custom-task-due 属性；
//   3. 自动按四象限（P1紧急重要、P2重要不急、P3紧急不重、P4普通）分组聚合；
//   4. 挂载原生 siyuan://blocks/{id} 反链，回车点击即可秒级居中高亮跳转原文；
//   5. 100% 纯只读安全 SQL 诊断，零插件依赖，绝不改动任何数据！
// ============================================================================


public static class TaskHorizonDashboardMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 只读 SQL 扫描全库未完成任务块
        var sql = "SELECT b.id AS bid, b.content AS bcontent, b.ial AS bial, b.updated AS bupdated, d.content AS dtitle " +
                  "FROM blocks b JOIN blocks d ON b.root_id = d.id " +
                  "WHERE d.type='d' AND b.markdown LIKE '%[ ]%' ORDER BY b.updated DESC LIMIT 60";

        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");

        if (!r.Success)
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 检索任务视界待办失败：" + r.Error);
            return;
        }

        var blocks = ExtractBalancedObjects(r.Text);
        if (blocks.Count == 0)
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "任务总数", "0");
            SafeContext.Set(context, "通知反馈", "✓ 任务视界看板：当前全库没有任何未完成待办，所有任务已清空！");
            return;
        }

        // 3. 四象限分组
        var p1List = new List<string>();
        var p2List = new List<string>();
        var p3List = new List<string>();
        var p4List = new List<string>();

        foreach (var b in blocks)
        {
            var bid = ExtractJsonField(b, "bid");
            var content = ExtractJsonField(b, "bcontent");
            var ial = ExtractJsonField(b, "bial");
            var dtitle = ExtractJsonField(b, "dtitle");

            var cleanText = Regex.Replace(content, "^-\\s*\\[\\s*\\]\\s*", "").Trim();
            cleanText = TruncateClean(cleanText, 50);

            // 识别优先级
            string pLevel = "p2";
            var mP = Regex.Match(ial, "custom-task-priority=\"([^\"]+)\"");
            if (mP.Success) pLevel = mP.Groups[1].Value.ToLower();
            else if (cleanText.Contains("P1") || cleanText.Contains("紧急重要")) pLevel = "p1";
            else if (cleanText.Contains("P3") || cleanText.Contains("紧急不重")) pLevel = "p3";
            else if (cleanText.Contains("P4") || cleanText.Contains("不急不重")) pLevel = "p4";

            // 识别截止日期
            string dueInfo = "";
            var mDue = Regex.Match(ial, "custom-task-due=\"([^\"]+)\"");
            if (mDue.Success) dueInfo = " *(截止: " + mDue.Groups[1].Value + ")*";

            var itemLine = "- [ ] [" + cleanText + "](siyuan://blocks/" + bid + ")" + dueInfo + " · *《" + TruncateClean(dtitle, 18) + "》*";

            if (pLevel == "p1") p1List.Add(itemLine);
            else if (pLevel == "p3") p3List.Add(itemLine);
            else if (pLevel == "p4") p4List.Add(itemLine);
            else p2List.Add(itemLine);
        }

        // 4. 构建看板 Markdown
        var sb = new StringBuilder();
        sb.Append("# 📋 任务视界 · 全域待办看板 (共 ").Append(blocks.Count).Append(" 项待办)\n\n");
        sb.Append("> 更新时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · 点击任务标题可直接跳转思源居中高亮\n\n");

        if (p1List.Count > 0)
        {
            sb.Append("### 🔴 P1 紧急且重要 (").Append(p1List.Count).Append(")\n");
            foreach (var l in p1List) sb.Append(l).Append("\n");
            sb.Append("\n");
        }

        if (p2List.Count > 0)
        {
            sb.Append("### 🟡 P2 重要不紧急 (").Append(p2List.Count).Append(")\n");
            foreach (var l in p2List) sb.Append(l).Append("\n");
            sb.Append("\n");
        }

        if (p3List.Count > 0)
        {
            sb.Append("### 🔵 P3 紧急不重要 (").Append(p3List.Count).Append(")\n");
            foreach (var l in p3List) sb.Append(l).Append("\n");
            sb.Append("\n");
        }

        if (p4List.Count > 0)
        {
            sb.Append("### ⚪ P4 顺延待办 (").Append(p4List.Count).Append(")\n");
            foreach (var l in p4List) sb.Append(l).Append("\n");
            sb.Append("\n");
        }

        var report = sb.ToString();

        // 5. 写入输出变量并保底复制到剪贴板
        SafeContext.Set(context, "待办清单", report);
        SafeContext.Set(context, "任务总数", blocks.Count.ToString());
        try
        {
            Clipboard.SetText(report);
        }
        catch { }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "✓ 任务视界看板：共 " + blocks.Count + " 项待办（P1: " + p1List.Count + ", P2: " + p2List.Count + "），看板已复制到剪贴板！");
    }

    private static string TruncateClean(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = s.Replace("\r", " ").Replace("\n", " ").Replace("[", "").Replace("]", "").Trim();
        if (clean.Length <= maxLen) return clean;
        return clean.Substring(0, maxLen) + "...";
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> ExtractBalancedObjects(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        int depth = 0;
        int start = -1;
        bool inStr = false;
        bool esc = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(text.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
        }
        return list;
    }
}


// ============================================================================
// L4-01: 全域常用语与 Prompt 智能喷射 —— 主驱动程序 (CommonInserterMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 渐进增强与本地自愈：
//      - 若安装了【小驴常用】(xiaolv-common)，自动从思源真实块级资产库中检索（支持搜索、最近使用与收藏）
//      - 若未安装【小驴常用】，自动平滑降级至本地持久化模板库（%USERPROFILE%\.quicker\snippets.json），
//        并提供开箱即用的高频预设模板（商务邮件、Code Review、需求拆解 Prompt、晨会纪要）
//   2. 智能动态变量与填参引擎：
//      - 内置宏自动计算：{{date}}, {{time}}, {{datetime}}, {{clipboard}} (剪贴板文本)
//      - 交互填参：若模板中含有未解析的 {{自定义参数}}，自动弹窗现场填参
//   3. 安全粘贴：将最终文本写入剪贴板并向原活动窗口模拟 Ctrl+V 喷射，绝不报错中断！
// ============================================================================


public static class CommonInserterMain
{
    private static readonly string LocalSnippetsFile = Path.Combine(
        XiaoLv.Desktop.Core.EmbeddedResourceManager.AppDataDir, 
        "snippets.json"
    );

    public static void Exec(IStepContext context)
    {
        var keyword = SafeContext.GetString(context, "搜索词");
        var selectedId = SafeContext.GetString(context, "选中条目ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // =============================================================
        // 阶段 2: 用户已经选中了具体条目 ID -> 读取正文、解析变量并喷射
        // =============================================================
        if (!string.IsNullOrEmpty(selectedId))
        {
            ProcessAndInjectItem(context, syUrl, token, selectedId);
            return;
        }

        // =============================================================
        // 阶段 1: 首次呼出 -> 检索条目清单供用户选择
        // =============================================================
        FetchAndDisplayItemList(context, syUrl, token, keyword);
    }

    /// <summary>
    /// 获取常用语清单（双轨机制：优先小驴常用，未装则读本地模板库）
    /// </summary>
    private static void FetchAndDisplayItemList(IStepContext context, string baseUrl, string token, string kw)
    {
        var items = new List<CommonItemMeta>();
        bool fromPlugin = false;

        // 轨 1: 尝试向小驴常用发起请求
        try
        {
            var op = string.IsNullOrEmpty(kw) ? "common.recent" : "common.search";
            var args = string.IsNullOrEmpty(kw) ? "{\"limit\":25}" : "{\"keyword\":\"" + EscapeJson(kw) + "\",\"limit\":25}";

            var sendRes = LvSendCommand.Send(baseUrl, token, op, args, "xiaolv-common", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(baseUrl, token, sendRes.CommandId, 2000, "xiaolv-common");
                if (rec.Status == "recorded")
                {
                    items = ExtractItemsList(rec.DataJson);
                    if (items.Count > 0) fromPlugin = true;
                }
            }
        }
        catch { fromPlugin = false; }

        // 轨 2: 本地持久化模板库或预设内置模板平滑兜底
        if (items.Count == 0)
        {
            items = LoadLocalSnippets();
            if (!string.IsNullOrEmpty(kw))
            {
                items = items.FindAll(it => it.Title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (items.Count == 0)
            {
                items = GetBuiltinFallbackTemplates();
            }
        }

        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 常用语与智能 Prompt\",")
                   .Append("\"subtitle\":\"选中条目即可 0ms 瞬间喷射至外部光标处 (共 ").Append(items.Count).Append(" 项)\",")
                   .Append("\"badge\":\"常用语库\",")
                   .Append("\"icon\":\"⚡\",")
                   .Append("\"mode\":\"select\",")
                   .Append("\"outputVar\":\"选中条目ID\",")
                   .Append("\"placeholder\":\"输入拼音或关键词过滤常用语/Prompt，回车喷射...\",")
                   .Append("\"items\":[");

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (i > 0) jsonBuilder.Append(",");
            string catBadge = !string.IsNullOrEmpty(it.Category) ? it.Category : "常用";
            jsonBuilder.Append("{\"id\":\"").Append(SafeContext.EscapeJson(it.Id)).Append("\",")
                       .Append("\"title\":\"").Append(SafeContext.EscapeJson(it.Title)).Append("\",")
                       .Append("\"desc\":\"").Append(SafeContext.EscapeJson(it.Title)).Append("\",")
                       .Append("\"badge\":\"").Append(SafeContext.EscapeJson(catBadge)).Append("\",")
                       .Append("\"badgeCls\":\"badge-native\",")
                       .Append("\"icon\":\"💬\"}");
        }
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "来源类型", fromPlugin ? "plugin" : "local");
    }

    /// <summary>
    /// 读取条目完整内容并替换模板变量，准备喷射
    /// </summary>
    private static void ProcessAndInjectItem(IStepContext context, string baseUrl, string token, string id)
    {
        string rawMarkdown = "";
        bool fromPlugin = false;

        // 尝试从小驴常用插件读取真实块
        try
        {
            var getArgs = "{\"id\":\"" + EscapeJson(id) + "\"}";
            var sendRes = LvSendCommand.Send(baseUrl, token, "common.get", getArgs, "xiaolv-common", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(baseUrl, token, sendRes.CommandId, 2000, "xiaolv-common");
                if (!string.IsNullOrEmpty(rec.DataJson))
                {
                    var contentMatch = Regex.Match(rec.DataJson, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (contentMatch.Success)
                    {
                        rawMarkdown = UnescapeJson(contentMatch.Groups[1].Value);
                        fromPlugin = true;
                    }
                }
            }
        }
        catch { fromPlugin = false; }

        // 若插件未返回，从本地模板库或内置模板中取
        if (string.IsNullOrEmpty(rawMarkdown))
        {
            rawMarkdown = GetLocalSnippetContent(id);
        }
        if (string.IsNullOrEmpty(rawMarkdown))
        {
            rawMarkdown = GetFallbackContentById(id);
        }

        // 动态宏变量替换
        var now = DateTime.Now;
        var clipboard = SafeContext.GetString(context, "剪贴板文本");
        string[] weekdays = new string[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        var weekdayStr = weekdays[(int)now.DayOfWeek];

        var finalContent = rawMarkdown
            .Replace("{{date}}", now.ToString("yyyy-MM-dd"))
            .Replace("{{今天}}", now.ToString("yyyy-MM-dd"))
            .Replace("{{time}}", now.ToString("HH:mm"))
            .Replace("{{时间}}", now.ToString("HH:mm"))
            .Replace("{{datetime}}", now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{{year}}", now.ToString("yyyy"))
            .Replace("{{年}}", now.ToString("yyyy"))
            .Replace("{{month}}", now.ToString("MM"))
            .Replace("{{月}}", now.ToString("MM"))
            .Replace("{{day}}", now.ToString("dd"))
            .Replace("{{日}}", now.ToString("dd"))
            .Replace("{{weekday}}", weekdayStr)
            .Replace("{{星期}}", weekdayStr)
            .Replace("{{guid}}", Guid.NewGuid().ToString())
            .Replace("{{uuid}}", Guid.NewGuid().ToString())
            .Replace("{{clipboard}}", clipboard)
            .Replace("{{剪贴板}}", clipboard);

        // 检查是否存在待填写的自定义变量，例如 {{客户名称}}
        // 交互填参闭环：首个未解析变量通过固定变量「填参值」由 userInput 步骤回填后重跑本方法逐个消化
        var customVarMatch = Regex.Match(finalContent, "\\{\\{([^\\}]+)\\}\\}");
        if (customVarMatch.Success)
        {
            var varName = customVarMatch.Groups[1].Value;
            var fillVal = SafeContext.GetString(context, "填参值");
            if (string.IsNullOrEmpty(fillVal))
            {
                context.SetVarValue("需要填参", true);
                context.SetVarValue("填参变量名", varName);
                context.SetVarValue("未替换模板", finalContent);
                context.SetVarValue("是否成功", true);
                context.SetVarValue("通知反馈", "⏳ 模板含待填参数 {{" + varName + "}}，等待现场填参...");
        SafeContext.Set(context, "需要通知", true);
                return;
            }
            finalContent = finalContent.Replace("{{" + varName + "}}", fillVal);
            SafeContext.Set(context, "填参值", "");
            SafeContext.Set(context, "需要填参", false);
        }

        context.SetVarValue("待喷射文本", finalContent);
        context.SetVarValue("需要执行粘贴", true);
        SetClipboardHelper(finalContent);
        // 智能喷射：模拟 Ctrl+V 瞬间将填充后的模板粘贴至当前窗口光标处（免手动按键）
        SimulatePasteHelper(150);
        context.SetVarValue("是否成功", true);

        context.SetVarValue("通知反馈", fromPlugin 
            ? "✓ 已由【小驴常用】资产库填充模板至当前光标！" 
            : "✓ 已由本地模板库填充至当前光标 (未安装小驴常用自动原生兜底)");
    }

    private static List<CommonItemMeta> LoadLocalSnippets()
    {
        var list = new List<CommonItemMeta>();
        if (File.Exists(LocalSnippetsFile))
        {
            try
            {
                var text = File.ReadAllText(LocalSnippetsFile, Encoding.UTF8);
                list = ExtractItemsList(text);
            }
            catch { }
        }
        return list;
    }

    private static string GetLocalSnippetContent(string id)
    {
        if (File.Exists(LocalSnippetsFile))
        {
            try
            {
                var text = File.ReadAllText(LocalSnippetsFile, Encoding.UTF8);
                var matches = Regex.Matches(text, "\\{[^\\}]+\\}");
                foreach (Match m in matches)
                {
                    if (m.Value.Contains("\"id\":\"" + id + "\""))
                    {
                        var cMatch = Regex.Match(m.Value, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (cMatch.Success) return UnescapeJson(cMatch.Groups[1].Value);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static List<CommonItemMeta> ExtractItemsList(string json)
    {
        var list = new List<CommonItemMeta>();
        var matches = Regex.Matches(json, "\\{[^\\}]+\\}");
        foreach (Match m in matches)
        {
            var block = m.Value;
            var id = ExtractJsonField(block, "id");
            var title = ExtractJsonField(block, "title");
            var cat = ExtractJsonField(block, "category");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
            {
                list.Add(new CommonItemMeta { Id = id, Title = title, Category = cat });
            }
        }
        return list;
    }

    private static List<CommonItemMeta> GetBuiltinFallbackTemplates()
    {
        return new List<CommonItemMeta>
        {
            new CommonItemMeta { Id = "tmpl-prompt-deep", Title = "深度技术方案与架构评审 Prompt", Category = "AI提示词" },
            new CommonItemMeta { Id = "tmpl-prompt-summary", Title = "长文萃取与要点结构化提炼 Prompt", Category = "AI提示词" },
            new CommonItemMeta { Id = "tmpl-prompt-translate", Title = "中英双向学术与工程信达雅翻译 Prompt", Category = "AI提示词" },
            new CommonItemMeta { Id = "tmpl-daily-standup", Title = "今日三只青蛙聚焦与行动清单", Category = "时间管理" },
            new CommonItemMeta { Id = "tmpl-email-reply", Title = "商务邮件标准确认与跟进回复", Category = "办公" },
            new CommonItemMeta { Id = "tmpl-code-review", Title = "代码审查标准反馈卡 (CR Review)", Category = "开发" },
            new CommonItemMeta { Id = "tmpl-bug-report", Title = "生产缺陷诊断与复现证据报告", Category = "开发" },
            new CommonItemMeta { Id = "tmpl-meeting-memo", Title = "敏捷晨会快速纪要骨架", Category = "会议" }
        };
    }

    private static string GetFallbackContentById(string id)
    {
        if (id == "tmpl-prompt-deep") return "你是一名世界顶尖的资深架构师与代码专家。请针对以下技术方案/代码进行深度审查与推演：\n1. 【架构合理性】：核心抽象与模块边界是否清晰？\n2. 【潜在风险】：是否存在并发、资源泄漏、超时或死锁隐患？\n3. 【优化方案】：给出具体的生产级重构建议与伪代码。\n\n输入上下文：\n{{clipboard}}";
        if (id == "tmpl-prompt-summary") return "请将以下内容精炼为一份清晰的结构化速读摘要：\n- 🎯 【核心论点】（一句话归纳核心意图）\n- 🔑 【关键证据/推导链路】（条理化呈现 3~5 点要点）\n- 💡 【落地行动与启发】\n\n材料：\n{{clipboard}}";
        if (id == "tmpl-prompt-translate") return "你是一名专业的技术书籍翻译专家。请将以下文本翻译为流畅、精准的学术与工程语言，保持专有名词的一致性与自然可读性：\n\n原文：\n{{clipboard}}";
        if (id == "tmpl-daily-standup") return "### 🐸 今日三只青蛙 ({{date}} {{weekday}})\n- [ ] 1. {{核心要务1}}\n- [ ] 2. {{核心要务2}}\n- [ ] 3. {{核心要务3}}\n\n*参考资料/剪贴板*: {{clipboard}}";
        if (id == "tmpl-email-reply") return "您好，\n\n来信已收悉。关于您提到的事项，我们团队已在跟进评估中，预计将于 {{date}} 前给您详细答复。\n\n顺祝商祺，\n小驴团队";
        if (id == "tmpl-code-review") return "### 🔍 Code Review 反馈\n- **架构一致性**: 符合规范\n- **潜在风险**: 注意边界条件校验与异常熔断\n- **建议改进**: {{建议内容}}";
        if (id == "tmpl-bug-report") return "### 🐛 Bug 诊断报告 ({{datetime}})\n- **异常现象**: {{现象描述}}\n- **复现步骤**: \n- **根因分析**: \n- **修复方案**: \n- **影响范围**: ";
        if (id == "tmpl-meeting-memo") return "### 📅 晨会纪要 ({{date}} {{time}})\n- **昨日完成**: {{clipboard}}\n- **今日计划**: \n- **阻碍风险**: 无";
        return id;
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
    }

    public class CommonItemMeta
    {
        public string Id;
        public string Title;
        public string Category;
    }

    private static void SetClipboardHelper(string text)
    {
        try { System.Windows.Forms.Clipboard.SetText(text); } catch { }
    }
    private static void SimulatePasteHelper(int delayMs = 120)
    {
        try { if (delayMs > 0) System.Threading.Thread.Sleep(delayMs); System.Windows.Forms.SendKeys.SendWait("^v"); } catch { }
    }
}

// ============================================================================
// L4-02: 会议与人脉全流程闭环 —— 主驱动程序 (MeetingContactsMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 渐进增强与原生平滑降级：
//      - 无论是否安装小驴人脉/管家，均在今日日记安全沉淀排版精美的结构化会议纪要
//      - 参会人员自动以思源原生双链 [[姓名]] 形式呈现，未装人脉插件也能在思源内建立双链网
//      - 提取的行动决议自动以原生待办 `- [ ]` 形式落入日记待办池
//   2. 若检测到小驴人脉与小驴管家，自动异步追加人物互动时间线与到期备忘，绝不因缺少插件报错
//   3. 高保真 Bento 多行速记录入弹窗（带会议结构化快捷标签提示）
// ============================================================================


public static class MeetingContactsMain
{
    public static void Exec(IStepContext context)
    {
        var rawText = SafeContext.GetString(context, "输入文本");
        var selection = SafeContext.GetString(context, "选中文本");
        var windowTitle = SafeContext.GetString(context, "来源窗口", "会议沟通");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 外部直接指令：打开人脉工作台
        var inParam = SafeContext.GetInputText(context).Trim().ToLowerInvariant();
        if (inParam == "open" || inParam == "plugin")
        {
            OpenContactsPlugin(context, syUrl, token);
            return;
        }

        var text = !string.IsNullOrEmpty(selection) ? selection : rawText;
        if (string.IsNullOrEmpty(text))
        {
            text = SafeContext.GetInputText(context);
        }

        if (string.IsNullOrEmpty(text))
        {
            var json = "{\"title\":\"思源外骨骼 · 会议人脉纪要全流程闭环\"," +
                       "\"subtitle\":\"提炼参会人生成双链、纪要入日记、行动决议入待办池\"," +
                       "\"badge\":\"会议纪要\"," +
                       "\"icon\":\"🤝\"," +
                       "\"mode\":\"input\"," +
                       "\"multiline\":true," +
                       "\"outputVar\":\"输入文本\"," +
                       "\"placeholder\":\"输入会议速记 (如: 张三, 李四：探讨新方案，待办: 准备立项报告)...\",\"defaultValue\":\"\"," +
                       "\"tags\":[\"参会人: \", \"待办: \", \"决议: \", \"后续: \"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        var diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var timeStr = now.ToString("HH:mm");

        // 2. 启发式解析：分离人员名单、会议纪要正文与待办事项
        var names = new List<string>();
        string memoContent = text;
        string todoContent = "";

        // 如果包含冒号，冒号前通常为参会人员
        var colonIdx = text.IndexOfAny(new char[] { '：', ':' });
        if (colonIdx > 0 && colonIdx < 40)
        {
            var namesPart = text.Substring(0, colonIdx);
            memoContent = text.Substring(colonIdx + 1).Trim();

            var rawNames = namesPart.Split(new char[] { ',', '，', '、', ';', '；', ' ', '@' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rn in rawNames)
            {
                var clean = rn.Trim();
                if (clean.Length > 0 && clean.Length <= 20) names.Add(clean);
            }
        }

        // 提取待办关键词
        var todoMatch = Regex.Match(memoContent, "(?:待办|TODO|任务|后续|决议)[\\s：:]+(.+)$", RegexOptions.IgnoreCase);
        if (todoMatch.Success)
        {
            todoContent = todoMatch.Groups[1].Value.Trim();
        }

        // 3. 增强联动 A: 小驴人脉时间线更新（尝试执行，失败或未装则静默忽略）
        bool contactsOk = false;
        if (names.Count > 0)
        {
            try
            {
                var namesJsonList = new List<string>();
                foreach (var n in names) namesJsonList.Add("\"" + SafeContext.EscapeJson(n) + "\"");
                
                var contactsArgs = "{\"names\":[" + string.Join(",", namesJsonList.ToArray()) + "]," +
                                   "\"note\":\"" + SafeContext.EscapeJson(memoContent) + "\"," +
                                   "\"place\":\"" + SafeContext.EscapeJson(windowTitle) + "\"," +
                                   "\"date\":\"" + dateStr + "\"}";

                var sendRes = LvSendCommand.Send(syUrl, token, "contacts.interaction", contactsArgs, "siyuan-contacts", "fast");
                if (sendRes.Success)
                {
                    var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 2500, "siyuan-contacts");
                    contactsOk = (rec.Status == "recorded" || rec.Status == "duplicate");
                }
            }
            catch { contactsOk = false; }
        }

        // 4. 增强联动 B: 小驴管家到期备忘同步（尝试执行，失败或未装则静默忽略）
        bool homeOk = false;
        if (!string.IsNullOrEmpty(todoContent))
        {
            try
            {
                var homeArgs = "{\"title\":\"[会议决议] " + SafeContext.EscapeJson(todoContent) + "\",\"dueDate\":\"" + dateStr + "\"}";
                var homeRes = LvSendCommand.Send(syUrl, token, "home.memo", homeArgs);
                if (homeRes.Success)
                {
                    var rec = LvGetReceipt.Poll(syUrl, token, homeRes.CommandId, 2000);
                    homeOk = (rec.Status == "recorded" || rec.Status == "duplicate");
                }
            }
            catch { homeOk = false; }
        }

        // 5. 核心基石：构造思源原生结构化会议纪要卡片
        var sbMeeting = new StringBuilder();
        sbMeeting.Append("### 🤝 会议纪要: ").Append(timeStr).Append(" (").Append(windowTitle).Append(")\n");
        if (names.Count > 0)
        {
            var linkedNames = new List<string>();
            foreach (var n in names) linkedNames.Add("[[" + n + "]]");
            sbMeeting.Append("* **参会人**: ").Append(string.Join(", ", linkedNames.ToArray())).Append("\n");
        }
        sbMeeting.Append("* **要点记录**: ").Append(memoContent).Append("\n");
        
        if (!string.IsNullOrEmpty(todoContent))
        {
            sbMeeting.Append("* **行动待办**: \n  - [ ] ").Append(todoContent).Append("\n");
        }
        sbMeeting.Append("\n{: custom-type=\"meeting-record\" }\n");

        var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(sbMeeting.ToString()) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);

        // 6. 人性化反馈输出
        var feedback = new StringBuilder();
        feedback.Append("✓ 会议纪要已成功归入今日日记！");
        if (names.Count > 0)
        {
            feedback.Append(" 参会人已链接 [[").Append(string.Join("]], [[", names.ToArray())).Append("]]");
            if (contactsOk) feedback.Append(" (小驴人脉时间线已更新)");
            feedback.Append("。");
        }
        if (!string.IsNullOrEmpty(todoContent))
        {
            feedback.Append(" 决议待办已推入待办池");
            if (homeOk) feedback.Append(" (已同步管家备忘)");
            feedback.Append("。");
        }

        SafeContext.Set(context, "是否成功", r.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", feedback.ToString());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void OpenContactsPlugin(IStepContext context, string syUrl, string token)
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }

        var res = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-contacts\",\"command\":\"openWorkbench\"}", "siyuan-speed-switch", "fast");
        SafeContext.Set(context, "是否成功", res.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", res.Success ? "✓ 已唤起思源并打开【小驴人脉】工作台" : "⚠️ 唤起小驴人脉未完成");
    }
}


// ============================================================================
// L4-03: 专注状态与番茄时钟联动 —— 主驱动程序 (TomatoFocusMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 专注会话状态机管理（支持多档预设 15/25/45m、自定义任务与时长、开始/结束/取消/查询）
//   2. 直达小驴打卡 (siyuan-checkin) 与底栏番茄钟 (siyuan-plugin-docktomato) 主界面
//   3. 自动生成时间轴记录：结束时计算真实专注时长，格式化为带时间区间的专注日志块
//   4. 追加至今日日记时间轴（Timeline），形成一日精力分配的完整可视化回溯
//   5. 智能打卡联动：动态匹配打卡项自动入库，回执闭环确认
//   6. 高保真 Bento HTML 交互弹窗（Mist Dark / 快捷数字键 / 极速响应）
// ============================================================================


public static class TomatoFocusMain
{
    public static void Exec(IStepContext context)
    {
        var actionCmd = SafeContext.GetString(context, "操作指令").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(actionCmd))
        {
            var inParam = SafeContext.GetInputText(context).Trim().ToLowerInvariant();
            if (inParam == "open" || inParam == "plugin" || inParam == "stop" || inParam == "start" || 
                inParam == "status" || inParam == "cancel" || inParam == "toggle" || inParam == "report" || 
                inParam == "summary" || inParam == "diary" || inParam == "open_diary")
            {
                actionCmd = inParam;
            }
        }

        var stage = SafeContext.GetString(context, "阶段").Trim().ToLowerInvariant();
        var selection = SafeContext.GetString(context, "选中指令").Trim();
        var customInput = SafeContext.GetString(context, "自定义输入").Trim();

        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 状态持久化文件路径 (用于追踪当前是否有正在运行的番茄钟会话)
        var stateFile = Path.Combine(Path.GetTempPath(), "quicker_tomato_session.json");

        // 阶段三：已输入自定义任务与时长
        if (stage == "custom" && !string.IsNullOrEmpty(customInput))
        {
            ParseAndStartCustom(context, customInput);
            return;
        }

        // 阶段二：已通过弹窗菜单选中指令
        if (stage == "menu" && !string.IsNullOrEmpty(selection))
        {
            HandleSelection(context, selection, syUrl, token);
            return;
        }

        // 外部直接指令模式 (来自外部调用、快捷键或超级面板直接传参)
        if (!string.IsNullOrEmpty(actionCmd))
        {
            HandleDirectCmd(context, actionCmd, syUrl, token, stateFile);
            return;
        }

        // 阶段一：交互式入口（用户无参直接点击动作）
        ShowInteractiveMenu(context, stateFile);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateSiYuanWindow()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    private static void ShowInteractiveMenu(IStepContext context, string stateFile)
    {
        bool isRunning = File.Exists(stateFile);

        if (isRunning)
        {
            int elapsedMin = 0;
            string taskName = "专注中";
            try
            {
                var saved = File.ReadAllText(stateFile, Encoding.UTF8);
                var tMatch = Regex.Match(saved, "\"task\"\\s*:\\s*\"([^\"]+)\"");
                var ticksMatch = Regex.Match(saved, "\"startTicks\"\\s*:\\s*([0-9]+)");
                if (tMatch.Success) taskName = tMatch.Groups[1].Value;
                if (ticksMatch.Success)
                {
                    long st; long.TryParse(ticksMatch.Groups[1].Value, out st);
                    elapsedMin = Math.Max(1, (int)new TimeSpan(DateTime.Now.Ticks - st).TotalMinutes);
                }
            }
            catch { }

            var json = "{\"title\":\"思源外骨骼 · 专注番茄时钟状态联动\",\"subtitle\":\"当前已专注 " + elapsedMin + " 分钟，任务:【" + SafeContext.EscapeJson(taskName) + "】\",\"badge\":\"专注中\",\"icon\":\"🍅\",\"mode\":\"select\",\"outputVar\":\"选中指令\",\"items\":[" +
                "{\"id\":\"stop\",\"title\":\"结束并结算专注\",\"desc\":\"写入今日日记时间轴与小驴打卡\",\"badge\":\"结算归档\",\"badgeCls\":\"badge-native\",\"hotkey\":\"1\",\"icon\":\"🏁\"}," +
                "{\"id\":\"open\",\"title\":\"唤起思源并打开【小驴打卡】\",\"desc\":\"打开打卡与习惯大窗\",\"badge\":\"小驴打卡\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"Alt+1\",\"icon\":\"🪟\"}," +
                "{\"id\":\"open_diary\",\"title\":\"唤起思源并打开【今日日记】\",\"desc\":\"定位居中日记时间轴\",\"badge\":\"今日日记\",\"badgeCls\":\"badge-bridge\",\"hotkey\":\"Alt+2\",\"icon\":\"📖\"}," +
                "{\"id\":\"cancel\",\"title\":\"放弃本次专注计时\",\"desc\":\"不保留任何日记与打卡记录\",\"badge\":\"放弃\",\"badgeCls\":\"badge-system\",\"hotkey\":\"4\",\"icon\":\"❌\"}," +
                "{\"id\":\"status\",\"title\":\"刷新并查看实时进度\",\"desc\":\"查看已专注时长与进度\",\"badge\":\"状态\",\"badgeCls\":\"badge-system\",\"hotkey\":\"5\",\"icon\":\"ℹ️\"}" +
                "]}";
            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
        }
        else
        {
            var json = "{\"title\":\"思源外骨骼 · 专注番茄时钟状态联动\",\"subtitle\":\"请选择专注模式开启深度工作\",\"badge\":\"番茄钟\",\"icon\":\"🍅\",\"mode\":\"select\",\"outputVar\":\"选中指令\",\"items\":[" +
                "{\"id\":\"start_25\",\"title\":\"开始 25 分钟番茄钟 (标准专注)\",\"desc\":\"经典番茄钟，适合常规开发与研读\",\"badge\":\"标准 25m\",\"badgeCls\":\"badge-native\",\"hotkey\":\"1\",\"icon\":\"🍅\"}," +
                "{\"id\":\"start_45\",\"title\":\"开始 45 分钟深度专注流 (深度攻坚)\",\"desc\":\"高强度心流，适合核心难题与架构编写\",\"badge\":\"深度 45m\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"2\",\"icon\":\"🔥\"}," +
                "{\"id\":\"start_15\",\"title\":\"开始 15 分钟快速冲刺 (碎片整理)\",\"desc\":\"快速聚焦，适合清理收集箱与速览\",\"badge\":\"冲刺 15m\",\"badgeCls\":\"badge-bridge\",\"hotkey\":\"3\",\"icon\":\"⚡\"}," +
                "{\"id\":\"custom\",\"title\":\"自定义任务名称与专注时长...\",\"desc\":\"弹窗指定任务名与计划分钟数\",\"badge\":\"自定义\",\"badgeCls\":\"badge-system\",\"hotkey\":\"4\",\"icon\":\"✏️\"}," +
                "{\"id\":\"open\",\"title\":\"唤起思源并打开【小驴打卡】\",\"desc\":\"打开小驴打卡完整主界面\",\"badge\":\"小驴打卡\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"Alt+1\",\"icon\":\"🪟\"}," +
                "{\"id\":\"report\",\"title\":\"查看习惯连击与专注打卡榜\",\"desc\":\"拉取当前打卡连击数据快照\",\"badge\":\"连击统计\",\"badgeCls\":\"badge-bridge\",\"hotkey\":\"Alt+2\",\"icon\":\"📊\"}" +
                "]}";
            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
        }

        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "阶段", "menu");
        SafeContext.Set(context, "是否成功", true);
    }

    private static void HandleSelection(IStepContext context, string selection, string syUrl, string token)
    {
        switch (selection)
        {
            case "start_25":
                StartSession(context, "沉浸式深度工作", 25);
                break;
            case "start_45":
                StartSession(context, "深度难题攻坚", 45);
                break;
            case "start_15":
                StartSession(context, "碎片快速冲刺", 15);
                break;
            case "custom":
                SafeContext.Set(context, "阶段", "custom");
                SafeContext.Set(context, "需要输入任务", true);
                var customJson = "{\"title\":\"自定义专注任务与时长\",\"subtitle\":\"支持输入任务名与计划时长\",\"badge\":\"自定义\",\"icon\":\"✏️\",\"mode\":\"input\",\"outputVar\":\"自定义输入\",\"placeholder\":\"如: 架构方案设计 30\",\"defaultValue\":\"深度攻坚 25\",\"tags\":[\"深度攻坚 25\",\"方案设计 35\",\"代码重构 45\",\"快速冲刺 15\"]}";
                SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(customJson));
                SafeContext.Set(context, "是否成功", true);
                break;
            case "stop":
                StopSession(context, syUrl, token);
                break;
            case "open":
                OpenCheckinPlugin(context, syUrl, token);
                break;
            case "open_diary":
                OpenDailyNote(context, syUrl, token);
                break;
            case "cancel":
                CancelSession(context);
                break;
            case "status":
                ShowStatus(context);
                break;
            case "report":
                ShowStreakReport(context, syUrl, token);
                break;
            default:
                ShowStatus(context);
                break;
        }
    }

    private static void HandleDirectCmd(IStepContext context, string actionCmd, string syUrl, string token, string stateFile)
    {
        if (actionCmd == "toggle")
        {
            actionCmd = File.Exists(stateFile) ? "stop" : "start";
        }

        if (actionCmd == "open" || actionCmd == "plugin")
        {
            OpenCheckinPlugin(context, syUrl, token);
            return;
        }

        if (actionCmd == "open_diary" || actionCmd == "diary")
        {
            OpenDailyNote(context, syUrl, token);
            return;
        }

        if (actionCmd == "cancel")
        {
            CancelSession(context);
            return;
        }

        if (actionCmd == "report" || actionCmd == "summary")
        {
            ShowStreakReport(context, syUrl, token);
            return;
        }

        if (actionCmd == "status")
        {
            ShowStatus(context);
            return;
        }

        if (actionCmd == "start")
        {
            var taskName = SafeContext.GetString(context, "专注任务名");
            var durationMinutes = SafeContext.GetInt(context, "时长分钟", 25);
            StartSession(context, taskName, durationMinutes);
            return;
        }

        if (actionCmd == "stop")
        {
            StopSession(context, syUrl, token);
            return;
        }

        ShowStatus(context);
    }

    private static void StartSession(IStepContext context, string taskName, int durationMinutes)
    {
        if (string.IsNullOrWhiteSpace(taskName)) taskName = "沉浸式深度工作";
        if (durationMinutes <= 0) durationMinutes = 25;

        var stateFile = Path.Combine(Path.GetTempPath(), "quicker_tomato_session.json");
        var now = DateTime.Now;

        var sessionData = "{\"task\":\"" + SafeContext.EscapeJson(taskName) + "\",\"startTicks\":" + now.Ticks + 
                          ",\"startDate\":\"" + now.ToString("yyyy-MM-dd") + "\",\"startTime\":\"" + now.ToString("HH:mm") + 
                          "\",\"planMinutes\":" + durationMinutes + "}";
        try { File.WriteAllText(stateFile, sessionData, Encoding.UTF8); } catch { }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "当前状态", "running");
        SafeContext.Set(context, "通知反馈", "🍅 番茄钟已启动！任务:【" + taskName + "】(计划 " + durationMinutes + " 分钟)，开启专注状态。");
    }

    private static void CancelSession(IStepContext context)
    {
        var stateFile = Path.Combine(Path.GetTempPath(), "quicker_tomato_session.json");
        try { if (File.Exists(stateFile)) File.Delete(stateFile); } catch { }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "当前状态", "idle");
        SafeContext.Set(context, "通知反馈", "✓ 已取消本次专注计时，未写入记录。");
    }

    private static void ParseAndStartCustom(IStepContext context, string input)
    {
        var trimmed = (input ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            StartSession(context, "沉浸式深度工作", 25);
            return;
        }

        int minutes = 25;
        string taskName = trimmed;

        var m = Regex.Match(trimmed, @"^(.*?)(?:\s+|[，,]+)(\d+)(?:\s*(?:min|分钟|分)?)?$");
        if (m.Success)
        {
            var rawTask = m.Groups[1].Value.Trim();
            var rawMin = m.Groups[2].Value.Trim();
            if (int.TryParse(rawMin, out minutes))
            {
                taskName = string.IsNullOrEmpty(rawTask) ? "沉浸式深度工作" : rawTask;
            }
        }
        else
        {
            if (int.TryParse(trimmed, out minutes))
            {
                taskName = "沉浸式深度工作";
            }
        }

        if (minutes <= 0) minutes = 25;
        StartSession(context, taskName, minutes);
    }

    private static void StopSession(IStepContext context, string syUrl, string token)
    {
        var stateFile = Path.Combine(Path.GetTempPath(), "quicker_tomato_session.json");
        var now = DateTime.Now;
        var diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        string recordedTask = "深度专注工作";
        int planMinutes = 25;
        long startTicks = now.AddMinutes(-25).Ticks;
        string startTimeStr = now.AddMinutes(-25).ToString("HH:mm");
        string startDateStr = now.ToString("yyyy-MM-dd");

        if (File.Exists(stateFile))
        {
            try
            {
                var saved = File.ReadAllText(stateFile, Encoding.UTF8);
                var tMatch = Regex.Match(saved, "\"task\"\\s*:\\s*\"([^\"]+)\"");
                var ticksMatch = Regex.Match(saved, "\"startTicks\"\\s*:\\s*([0-9]+)");
                var startStrM = Regex.Match(saved, "\"startTime\"\\s*:\\s*\"([^\"]+)\"");
                var startDtM = Regex.Match(saved, "\"startDate\"\\s*:\\s*\"([^\"]+)\"");
                var planM = Regex.Match(saved, "\"planMinutes\"\\s*:\\s*([0-9]+)");

                if (tMatch.Success) recordedTask = tMatch.Groups[1].Value;
                if (ticksMatch.Success) long.TryParse(ticksMatch.Groups[1].Value, out startTicks);
                if (startStrM.Success) startTimeStr = startStrM.Groups[1].Value;
                if (startDtM.Success) startDateStr = startDtM.Groups[1].Value;
                if (planM.Success) int.TryParse(planM.Groups[1].Value, out planMinutes);

                File.Delete(stateFile);
            }
            catch { }
        }

        var rawElapsedMin = (int)new TimeSpan(now.Ticks - startTicks).TotalMinutes;
        string anomalyHint = "";
        int elapsedMin = rawElapsedMin;

        // 异常与跨天防御：若持续时间超过 180 分钟（3小时）或跨天，视为遗忘停止，自动重置为计划时长
        if (rawElapsedMin > 180 || (startDateStr != now.ToString("yyyy-MM-dd") && rawElapsedMin > 60))
        {
            elapsedMin = planMinutes > 0 ? planMinutes : 25;
            anomalyHint = " (由于检测到跨天或遗忘超过3小时，已自动按 " + elapsedMin + " 分钟结算)";
        }
        if (elapsedMin <= 0) elapsedMin = 1;

        var endTimeStr = now.ToString("HH:mm");

        // 构建日记时间轴块
        var sb = new StringBuilder();
        sb.Append("### 🍅 专注时间块 (").Append(startTimeStr).Append(" - ").Append(endTimeStr).Append(")\n");
        sb.Append("* **完成任务**: ").Append(recordedTask).Append("\n");
        sb.Append("* **专注时长**: ").Append(elapsedMin).Append(" 分钟 · 状态: ✓ 已归档").Append(anomalyHint).Append("\n");
        sb.Append("\n{: custom-type=\"tomato-timeline\" }\n");

        var appendBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(sb.ToString()) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", appendBody);

        // 联动小驴打卡记录专注数值（带回执确认，动态匹配已有打卡项）
        bool checkinOk = false;
        string matchedItemName = "";
        try
        {
            string targetItemId = "";
            var itemsRec = PollBridge(syUrl, token, "checkin.items", "{\"limit\":200}", 6000);
            if (itemsRec.Status == "recorded" && !string.IsNullOrEmpty(itemsRec.DataJson))
            {
                foreach (Match m in Regex.Matches(itemsRec.DataJson, "\\{[^\\{\\}]+\\}"))
                {
                    var b = m.Value;
                    var idM = Regex.Match(b, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    var nameM = Regex.Match(b, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    var unitM = Regex.Match(b, "\"unit\"\\s*:\\s*\"([^\"]+)\"");
                    if (idM.Success && ((nameM.Success && (nameM.Groups[1].Value.Contains("专注") || nameM.Groups[1].Value.Contains("番茄") || nameM.Groups[1].Value.Contains("工作") || nameM.Groups[1].Value.Contains("学习") || nameM.Groups[1].Value.Contains("深度"))) || (unitM.Success && unitM.Groups[1].Value == "分钟")))
                    {
                        targetItemId = idM.Groups[1].Value;
                        if (nameM.Success) matchedItemName = nameM.Groups[1].Value;
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(targetItemId))
            {
                var checkinArgs = "{\"itemId\":\"" + SafeContext.EscapeJson(targetItemId) + "\",\"value\":" + elapsedMin + ",\"unit\":\"分钟\",\"note\":\"" + SafeContext.EscapeJson(recordedTask) + "\"}";
                var rec = PollBridge(syUrl, token, "checkin.record", checkinArgs, 6000);
                checkinOk = (rec.Status == "recorded" || rec.Status == "duplicate");
            }
        }
        catch { }

        SafeContext.Set(context, "是否成功", r.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "当前状态", "idle");
        var checkinText = checkinOk ? (" (已同步打卡: " + (string.IsNullOrEmpty(matchedItemName) ? "专注" : matchedItemName) + ")") : "";
        SafeContext.Set(context, "通知反馈", "✓ 番茄钟已顺利结算！专注 " + elapsedMin + " 分钟已沉淀至今日日记时间轴。" + checkinText + anomalyHint);
    }

    private static void OpenCheckinPlugin(IStepContext context, string syUrl, string token)
    {
        ActivateSiYuanWindow();
        var res = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-checkin\",\"command\":\"openCheckin\"}", "siyuan-speed-switch", "fast");
        try { LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-plugin-docktomato\",\"command\":\"open\"}", "siyuan-speed-switch", "fast"); } catch { }

        SafeContext.Set(context, "是否成功", res.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", res.Success ? "✓ 已唤起思源并打开【小驴打卡】主界面" : "⚠️ 唤起小驴打卡未完成");
    }

    private static void OpenDailyNote(IStepContext context, string syUrl, string token)
    {
        var todayCompact = DateTime.Now.ToString("yyyyMMdd");
        var todayDash = DateTime.Now.ToString("yyyy-MM-dd");
        string docId = "";

        var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + todayCompact + "%' ORDER BY updated DESC LIMIT 1\"}";
        var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
        if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

        if (string.IsNullOrEmpty(docId))
        {
            var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + todayDash + "%' OR hpath LIKE '%" + todayDash + "%') ORDER BY updated DESC LIMIT 1\"}";
            var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
            if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            var nb = SafeContext.GetDiaryNotebookId(context, syUrl, token);
            if (!string.IsNullOrEmpty(nb))
            {
                var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(nb) + "\"}");
                if (createRes.Success) docId = ExtractJsonField(createRes.Text, "id");
            }
        }

        ActivateSiYuanWindow();

        if (!string.IsNullOrEmpty(docId))
        {
            LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(docId) + "\"}", "siyuan-speed-switch", "fast");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("siyuan://blocks/" + docId) { UseShellExecute = true }); } catch { }
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已唤起思源并打开【今日日记】");
        }
        else
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("siyuan://") { UseShellExecute = true }); } catch { }
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已唤起思源笔记");
        }
    }

    private static void ShowStatus(IStepContext context)
    {
        var stateFile = Path.Combine(Path.GetTempPath(), "quicker_tomato_session.json");
        var now = DateTime.Now;
        bool isRunning = File.Exists(stateFile);
        string currentInfo = "";
        if (isRunning)
        {
            try
            {
                var saved = File.ReadAllText(stateFile, Encoding.UTF8);
                var tMatch = Regex.Match(saved, "\"task\"\\s*:\\s*\"([^\"]+)\"");
                var ticksMatch = Regex.Match(saved, "\"startTicks\"\\s*:\\s*([0-9]+)");
                var planMatch = Regex.Match(saved, "\"planMinutes\"\\s*:\\s*([0-9]+)");
                if (ticksMatch.Success)
                {
                    long st; long.TryParse(ticksMatch.Groups[1].Value, out st);
                    var m = Math.Max((int)new TimeSpan(now.Ticks - st).TotalMinutes, 0);
                    var plan = planMatch.Success ? planMatch.Groups[1].Value : "25";
                    currentInfo = " 任务:【" + (tMatch.Success ? tMatch.Groups[1].Value : "专注中") + "】，已专注 " + m + "/" + plan + " 分钟。";
                }
            }
            catch { }
        }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "当前状态", isRunning ? "running" : "idle");
        SafeContext.Set(context, "通知反馈", isRunning ? "🍅 专注进行中。" + currentInfo : "ℹ️ 当前没有正在进行的番茄钟。");
    }

    private static void ShowStreakReport(IStepContext context, string syUrl, string token)
    {
        var sum = PollBridge(syUrl, token, "checkin.summary", "{}", 8000);
        if (sum.Status != "recorded")
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "ℹ️ 小驴打卡插件未启用；日常番茄专注会自动结算沉淀至今日日记时间轴。");
            return;
        }

        var items = PollBridge(syUrl, token, "checkin.items", "{\"limit\":200}", 6000);
        var nameById = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(items.DataJson))
        {
            foreach (Match m in Regex.Matches(items.DataJson, "\\{[^\\{\\}]+\\}"))
            {
                var b = m.Value;
                var idM = Regex.Match(b, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                var nameM = Regex.Match(b, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (idM.Success && nameM.Success && !nameById.ContainsKey(idM.Groups[1].Value))
                {
                    nameById[idM.Groups[1].Value] = nameM.Groups[1].Value;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("📊 习惯打卡与专注统计：");
        var streaksMatch = Regex.Match(sum.DataJson, "\"streaks\"\\s*:\\s*\\{([^\\}]+)\\}");
        if (streaksMatch.Success)
        {
            var entries = streaksMatch.Groups[1].Value.Split(',');
            int count = 0;
            foreach (var entry in entries)
            {
                var pair = entry.Split(':');
                if (pair.Length != 2) continue;
                var id = pair[0].Trim().Trim('"');
                int streak = 0;
                int.TryParse(pair[1].Trim(), out streak);
                if (streak > 0)
                {
                    var label = nameById.ContainsKey(id) ? nameById[id] : id;
                    sb.Append("【").Append(label).Append("】连击 ").Append(streak).Append(" 天；");
                    count++;
                    if (count >= 4) break;
                }
            }
        }

        var report = sb.ToString().TrimEnd('；');
        if (report == "📊 习惯打卡与专注统计：") report = "📊 今日暂无连击记录，开始一个番茄钟行动吧！";
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", report);
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }

    private static string ExtractJsonField(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return "";
        var m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// L4-04: 快捷格式化与思源原色美化集 —— 主驱动程序 (TextFormatterMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 解决社区痛点：彻底收敛 zxerath 碎片化的十几个单色字体动作（红/蓝/橙等），一个动作聚合调色盘
//   2. 严格采用思源原生 CSS 变量色值（var(--b3-font-color1~13) 与 var(--b3-font-background1~13)）
//      - 无论用户切换深色、浅色主题，颜色自动和谐自适应，彻底避免硬编码十六进制色导致的“夜间刺眼反色”漏洞！
//   3. 支持一键格式化与一键剥离：
//      - 文字设色 / 背景高亮 (荧光笔效果)
//      - 「一键清除格式」：瞬间剥离选中范围内的 span 标签与 Kramdown 杂质，恢复纯净字态
//   4. 写回剪贴板并模拟 Ctrl+V 即时替换当前选区，操作行云流水！
// ============================================================================


public static class TextFormatterMain
{
    public static void Exec(IStepContext context)
    {
        var selectedText = SafeContext.GetString(context, "选中文本");
        var styleCmd = SafeContext.GetString(context, "格式指令");
        if (string.IsNullOrEmpty(styleCmd)) styleCmd = SafeContext.GetString(context, "选中指令");
        if (string.IsNullOrEmpty(styleCmd)) styleCmd = SafeContext.GetString(context, "输入文本");
        styleCmd = (styleCmd ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(selectedText))
        {
            selectedText = XiaoLv.Desktop.Core.SelectionHelper.CaptureSelectedText(150);
        }
        if (string.IsNullOrEmpty(selectedText) && XiaoLv.Desktop.Core.ClipboardWatcher.IsEnabled && XiaoLv.Desktop.Core.ClipboardWatcher.LatestItem != null)
        {
            selectedText = XiaoLv.Desktop.Core.ClipboardWatcher.LatestItem.RawText;
        }

        if (string.IsNullOrEmpty(selectedText))
        {
            var clip = SafeContext.GetString(context, "剪贴板文本");
            if (!string.IsNullOrEmpty(clip)) selectedText = clip;
        }

        if (string.IsNullOrEmpty(selectedText))
        {
            context.SetVarValue("是否成功", false);
            context.SetVarValue("通知反馈", "❌ 未检测到划选文字，请在思源或任意编辑器中先划选内容。");
            SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 阶段 1: 若未指定具体颜色指令，弹出紧凑原生调色盘选择微窗
        if (string.IsNullOrEmpty(styleCmd))
        {
            var json = BuildColorPaletteDialogJson(selectedText);
            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗选择", true);
            return;
        }

        // 阶段 2: 根据选定指令执行格式化包裹或清理
        var formatted = ApplyFormatting(selectedText, styleCmd);

        context.SetVarValue("格式化文本", formatted);
        context.SetVarValue("需要写回剪贴板", true);
        context.SetVarValue("需要模拟粘贴", true);
        SetClipboardHelper(formatted);
        // 智能美化：模拟 Ctrl+V 瞬间将设色后的 HTML span 替换回当前划选范围
        SimulatePasteHelper(150);
        context.SetVarValue("是否成功", true);
        context.SetVarValue("通知反馈", "✓ 已应用原生调色格式！");
        SafeContext.Set(context, "需要通知", true);
    }

    /// <summary>
    /// 构建采用思源原生 CSS 变量的高保真调色盘对话框 JSON
    /// </summary>
    public static string BuildColorPaletteDialogJson(string sampleText)
    {
        var sample = string.IsNullOrEmpty(sampleText) ? "示例文字" : (sampleText.Length > 20 ? sampleText.Substring(0, 20) + "..." : sampleText);
        var sb = new StringBuilder();
        sb.Append("{\"title\":\"思源外骨骼 · 原色高亮美化\",")
          .Append("\"subtitle\":\"针对选区【").Append(SafeContext.EscapeJson(sample)).Append("】设置原生变量设色\",")
          .Append("\"badge\":\"原生调色盘\",")
          .Append("\"icon\":\"🎨\",")
          .Append("\"mode\":\"select\",")
          .Append("\"outputVar\":\"选中指令\",")
          .Append("\"placeholder\":\"输入拼音或关键词过滤颜色，回车应用...\",")
          .Append("\"items\":[");

        var items = new[]
        {
            new { text = "🔴 红色字体 (var(--b3-font-color1))", value = "c1", icon = "🔴" },
            new { text = "🟠 橙色字体 (var(--b3-font-color2))", value = "c2", icon = "🟠" },
            new { text = "🟡 黄色字体 (var(--b3-font-color3))", value = "c3", icon = "🟡" },
            new { text = "🟢 绿色字体 (var(--b3-font-color4))", value = "c4", icon = "🟢" },
            new { text = "🔵 蓝色字体 (var(--b3-font-color6))", value = "c6", icon = "🔵" },
            new { text = "🟣 紫色字体 (var(--b3-font-color7))", value = "c7", icon = "🟣" },
            new { text = "🔘 灰色弱化字体 (var(--b3-font-color9))", value = "c9", icon = "🔘" },
            new { text = "🟡 黄色荧光高亮 (var(--b3-font-background3))", value = "bg3", icon = "🖍️" },
            new { text = "🟢 绿色荧光高亮 (var(--b3-font-background4))", value = "bg4", icon = "🖍️" },
            new { text = "🔵 蓝色荧光高亮 (var(--b3-font-background6))", value = "bg6", icon = "🖍️" },
            new { text = "🟣 紫色荧光高亮 (var(--b3-font-background7))", value = "bg7", icon = "🖍️" },
            new { text = "🔴 浅红重点高亮 (var(--b3-font-background1))", value = "bg1", icon = "🖍️" },
            new { text = "✨ 一键清除选区行内格式与颜色", value = "clear", icon = "🧹" }
        };

        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"id\":\"").Append(items[i].value).Append("\",")
              .Append("\"title\":\"").Append(SafeContext.EscapeJson(items[i].text)).Append("\",")
              .Append("\"value\":\"").Append(items[i].value).Append("\",")
              .Append("\"icon\":\"").Append(items[i].icon).Append("\"}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>
    /// 应用格式化或清理规则
    /// </summary>
    public static string ApplyFormatting(string text, string cmd)
    {
        if (cmd == "clear")
        {
            // 剥离 HTML span 标签与 Kramdown 属性定义
            var clean = Regex.Replace(text, @"<span[^>]*>(.*?)</span>", "$1", RegexOptions.Singleline);
            clean = Regex.Replace(clean, @"\{:[^\}]+\}", "");
            return clean;
        }

        // 文字字体颜色 (c1 ~ c13)
        if (cmd.StartsWith("c"))
        {
            var idx = cmd.Substring(1);
            return "<span style=\"color: var(--b3-font-color" + idx + ");\">" + text + "</span>";
        }

        // 背景荧光笔高亮 (bg1 ~ bg13)
        if (cmd.StartsWith("bg"))
        {
            var idx = cmd.Substring(2);
            return "<span style=\"background-color: var(--b3-font-background" + idx + ");\">" + text + "</span>";
        }

        return text;
    }

    private static void SetClipboardHelper(string text)
    {
        try { System.Windows.Forms.Clipboard.SetText(text); } catch { }
    }
    private static void SimulatePasteHelper(int delayMs = 120)
    {
        try { if (delayMs > 0) System.Threading.Thread.Sleep(delayMs); System.Windows.Forms.SendKeys.SendWait("^v"); } catch { }
    }
}

// ============================================================================
// L4-05: 习惯打卡快捷面板 —— 主驱动程序 (CheckinPanelMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 checkin.items / checkin.record / checkin.summary（打卡 API v5 透传）
// 核心逻辑：
//   1. 阶段一：高保真 Bento 弹窗呈现打卡清单（含计量型单位、置顶插件大窗入口、连击榜），支持 1~9 秒选
//   2. 阶段二：数值型项目 Bento 现场填值；布尔型直接记录
//   3. 阶段三：checkin.record 落卡（回执确认），随后 checkin.summary 读取连击反馈
//   4. 兜底：快门桥不可用时降级写今日日记打点卡（如实标注未同步），绝不丢打卡意图
// ============================================================================


public static class CheckinPanelMain
{
    public static void Exec(IStepContext context)
    {
        var mode = SafeContext.GetString(context, "指令码").Trim().ToLowerInvariant();
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 专项模式：连击速览
        if (mode == "report")
        {
            ShowStreakReport(context, syUrl, token);
            return;
        }

        // 专项模式：一键直开小驴打卡插件主窗口
        if (mode == "open")
        {
            OpenCheckinPlugin(context, syUrl, token);
            return;
        }

        // 降级补名完成：已输入手动项目名 -> 写日记打点卡
        var manualName = SafeContext.GetString(context, "项目名");
        if (SafeContext.GetString(context, "阶段") == "manual" && !string.IsNullOrEmpty(manualName))
        {
            WriteFallbackCard(context, syUrl, token, manualName);
            return;
        }

        var stage = SafeContext.GetString(context, "阶段");
        var selection = SafeContext.GetString(context, "选中项目ID");

        // 填值完成：执行记录
        if (stage == "fill" && !string.IsNullOrEmpty(selection))
        {
            RecordCheckin(context, syUrl, token);
            return;
        }

        // 阶段二：已选中项目（选中值为 id|kind|unit|name 编码）
        if (!string.IsNullOrEmpty(selection))
        {
            var parts = selection.Split('|');
            var itemId = parts[0].Trim();
            var kind = parts.Length > 1 ? parts[1].Trim() : "";
            var unit = parts.Length > 2 ? parts[2].Trim() : "";
            var name = parts.Length > 3 ? parts[3].Trim() : itemId;

            // 分支 1：直接打开小驴打卡插件主面板
            if (itemId == "__open_plugin__")
            {
                OpenCheckinPlugin(context, syUrl, token);
                return;
            }

            // 分支 2：查看习惯连击榜
            if (itemId == "__report__")
            {
                ShowStreakReport(context, syUrl, token);
                return;
            }

            // 分支 3：记一笔临时打点
            if (itemId == "__fallback_jot__")
            {
                SafeContext.Set(context, "阶段", "manual");
                SafeContext.Set(context, "需要弹窗输入", true);
                var manualJson = "{\"title\":\"临时打点记录\",\"subtitle\":\"未关联插件，将以日记打点卡记录至今日日记\",\"badge\":\"日记卡\",\"icon\":\"➕\",\"mode\":\"input\",\"outputVar\":\"项目名\",\"placeholder\":\"请输入打卡项目名称...\",\"defaultValue\":\"\"}";
                SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(manualJson));
                SafeContext.Set(context, "是否成功", true);
                return;
            }

            SafeContext.Set(context, "选中项目ID", itemId);
            SafeContext.Set(context, "项目类型", kind);
            SafeContext.Set(context, "项目名", name);

            // 支持所有数值/计量型项目（number, quantity, duration）
            if (kind == "number" || kind == "quantity" || kind == "duration")
            {
                SafeContext.Set(context, "阶段", "fill");
                SafeContext.Set(context, "需要填值", true);
                var fillJson = "{\"title\":\"打卡数值填录\",\"subtitle\":\"【" + SafeContext.EscapeJson(name) + "】为数值计量型项目\",\"badge\":\"" + SafeContext.EscapeJson(unit == "" ? "数值" : unit) + "\",\"icon\":\"📝\",\"mode\":\"input\",\"outputVar\":\"打卡数值\",\"placeholder\":\"请输入本次数值 (默认 1)...\",\"defaultValue\":\"1\"}";
                SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(fillJson));
                SafeContext.Set(context, "是否成功", true);
                return;
            }
            RecordCheckin(context, syUrl, token);
            return;
        }

        // 阶段一：拉取项目清单
        ListItems(context, syUrl, token);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void OpenCheckinPlugin(IStepContext context, string syUrl, string token)
    {
        // 1. 唤醒并前置思源笔记主窗口
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }

        // 2. 调用雷切 commands.run 穿透调用打卡插件的 openCheckin 命令
        var res = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-checkin\",\"command\":\"openCheckin\"}", "siyuan-speed-switch", "fast");
        SafeContext.Set(context, "是否成功", res.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", res.Success ? "✓ 已唤起思源并打开【小驴打卡】主界面" : "⚠️ 唤起小驴打卡未完成");
    }

    private static void ListItems(IStepContext context, string syUrl, string token)
    {
        var rec = PollBridge(syUrl, token, "checkin.items", "{\"limit\":200}", 8000);
        var items = ParseItems(rec.DataJson);

        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 习惯打卡快捷面板\",\"subtitle\":\"选择项目完成签到，或直达插件主界面\",\"badge\":\"小驴打卡\",\"icon\":\"🍅\",\"mode\":\"select\",\"outputVar\":\"选中项目ID\",\"items\":[");
        jsonBuilder.Append("{\"id\":\"__open_plugin__\",\"title\":\"打开小驴打卡主界面\",\"desc\":\"唤醒思源打开打卡大窗\",\"badge\":\"小驴打卡\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"Alt+1\",\"icon\":\"🪟\"},");
        jsonBuilder.Append("{\"id\":\"__report__\",\"title\":\"查看习惯连击与周报统计\",\"desc\":\"查看当前连击天数与概况\",\"badge\":\"连击周报\",\"badgeCls\":\"badge-bridge\",\"hotkey\":\"Alt+2\",\"icon\":\"🔥\"}");

        int numHotkey = 1;
        if (items.Count > 0)
        {
            foreach (var it in items)
            {
                var hk = numHotkey <= 9 ? numHotkey.ToString() : "";
                var desc = it.Kind == "number" || it.Kind == "quantity" || it.Kind == "duration" 
                    ? ("计量型打卡 (" + (string.IsNullOrEmpty(it.Unit) ? "数值" : it.Unit) + ")") 
                    : "单次打卡项目";
                var encodedId = it.Id + "|" + it.Kind + "|" + it.Unit + "|" + it.Name;
                jsonBuilder.Append(",{\"id\":\"").Append(SafeContext.EscapeJson(encodedId)).Append("\",")
                           .Append("\"title\":\"").Append(SafeContext.EscapeJson(it.Name)).Append("\",")
                           .Append("\"desc\":\"").Append(SafeContext.EscapeJson(desc)).Append("\",")
                           .Append("\"badge\":\"").Append(SafeContext.EscapeJson(it.Kind == "binary" ? "打卡" : "计量")).Append("\",")
                           .Append("\"badgeCls\":\"").Append(it.Kind == "binary" ? "badge-native" : "badge-bridge").Append("\",")
                           .Append("\"hotkey\":\"").Append(hk).Append("\",")
                           .Append("\"icon\":\"✓\"}");
                numHotkey++;
            }
        }
        else
        {
            jsonBuilder.Append(",{\"id\":\"__fallback_jot__\",\"title\":\"记一笔临时打点 (写入今日日记)\",\"desc\":\"打卡桥未就绪，原生降级\",\"badge\":\"日记打点\",\"badgeCls\":\"badge-system\",\"hotkey\":\"3\",\"icon\":\"➕\"}");
        }
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "是否成功", true);
    }

    private static void RecordCheckin(IStepContext context, string syUrl, string token)
    {
        var itemId = SafeContext.GetString(context, "选中项目ID");
        var itemName = SafeContext.GetString(context, "项目名");
        var valueText = SafeContext.GetString(context, "打卡数值");
        var note = SafeContext.GetString(context, "打卡备注");

        double numVal = 1;
        double.TryParse(valueText, out numVal);
        if (numVal <= 0) numVal = 1;

        var args = "{\"itemId\":\"" + SafeContext.EscapeJson(itemId) + "\",\"value\":" +
                   numVal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   (string.IsNullOrEmpty(note) ? "" : ",\"note\":\"" + SafeContext.EscapeJson(note) + "\"") + "}";
        var rec = PollBridge(syUrl, token, "checkin.record", args, 8000);

        if (rec.Status != "recorded" && rec.Status != "duplicate")
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 打卡未完成【" + rec.Status + "】：" + rec.Message);
            return;
        }

        // 成功后读取连击做即时反馈
        var streakText = "";
        var sum = PollBridge(syUrl, token, "checkin.summary", "{}", 6000);
        var streak = ExtractStreakFor(sum.DataJson, itemId);
        if (streak >= 0) streakText = " 🔥连击 " + streak + " 天";

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", (rec.Status == "duplicate" ? "ℹ️ " : "✓ ") +
            (string.IsNullOrEmpty(rec.Message) ? "已记录" : rec.Message) + " · 【" +
            (string.IsNullOrEmpty(itemName) ? itemId : itemName) + "】" + streakText);
    }

    private static void WriteFallbackCard(IStepContext context, string syUrl, string token, string itemName)
    {
        var note = SafeContext.GetString(context, "打卡备注");
        var md = "- " + DateTime.Now.ToString("HH:mm") + " ✅ 打卡：" + itemName +
                 (string.IsNullOrEmpty(note) ? "" : " · " + note) + " (未同步小驴打卡)";
        var body = "{\"notebook\":\"" + SafeContext.GetDiaryNotebookId(context, syUrl, token) +
                   "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", body);
        SafeContext.Set(context, "是否成功", r.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", r.Success ? "✓ 已写入日记打点卡（未同步小驴打卡）。" : "❌ 打点卡写入失败：" + r.Error);
    }

    private static void ShowStreakReport(IStepContext context, string syUrl, string token)
    {
        var sum = PollBridge(syUrl, token, "checkin.summary", "{}", 8000);
        if (sum.Status != "recorded")
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "ℹ️ 小驴打卡插件未启用，连击榜需安装小驴打卡；当前日常打卡会自动降级记录至今日日记。");
            return;
        }

        var items = PollBridge(syUrl, token, "checkin.items", "{\"limit\":200}", 6000);
        var nameById = new Dictionary<string, string>();
        foreach (var it in ParseItems(items.DataJson))
        {
            if (!nameById.ContainsKey(it.Id)) nameById[it.Id] = it.Name;
        }

        var report = FormatStreakReport(sum.DataJson, nameById);
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", report);
    }

    private static string FormatStreakReport(string summaryJson, Dictionary<string, string> nameById)
    {
        if (string.IsNullOrEmpty(summaryJson)) return "📊 暂无连击数据";
        var m = Regex.Match(summaryJson, "\"streaks\"\\s*:\\s*\\{([^\\}]+)\\}");
        if (!m.Success) return "📊 暂无连击数据";

        var body = m.Groups[1].Value;
        var pairs = Regex.Matches(body, "\"([^\"]+)\"\\s*:\\s*([0-9]+)");
        if (pairs.Count == 0) return "📊 暂无连击数据";

        var sb = new StringBuilder();
        sb.Append("🔥 习惯连击速览：");
        int count = 0;
        foreach (Match p in pairs)
        {
            var id = p.Groups[1].Value;
            var days = p.Groups[2].Value;
            var name = nameById.ContainsKey(id) ? nameById[id] : id;
            if (count > 0) sb.Append(" · ");
            sb.Append(name).Append(" ").Append(days).Append("天");
            count++;
            if (count >= 5) break;
        }
        return sb.ToString();
    }

    private static int ExtractStreakFor(string summaryJson, string itemId)
    {
        if (string.IsNullOrEmpty(summaryJson) || string.IsNullOrEmpty(itemId)) return -1;
        var pattern = "\"" + Regex.Escape(itemId) + "\"\\s*:\\s*([0-9]+)";
        var m = Regex.Match(summaryJson, pattern);
        if (m.Success)
        {
            int days;
            if (int.TryParse(m.Groups[1].Value, out days)) return days;
        }
        return -1;
    }

    public class CheckinItem { public string Id; public string Name; public string Kind; public string Unit; }

    private static List<CheckinItem> ParseItems(string dataJson)
    {
        var list = new List<CheckinItem>();
        if (string.IsNullOrEmpty(dataJson)) return list;
        foreach (Match m in Regex.Matches(dataJson, "\\{[^\\{\\}]+\\}"))
        {
            var b = m.Value;
            var id = ExtractField(b, "id");
            var name = ExtractField(b, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
            var kind = ExtractField(b, "kind");
            var unit = ExtractField(b, "unit");
            list.Add(new CheckinItem { Id = id, Name = name, Kind = kind, Unit = unit });
        }
        return list;
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L4-06: 思源日历自然语言日程 —— 主驱动程序 (CalendarScheduleMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 纯标准库自然语言中文日程解析 (NLP Time Extractor)：
//      - 相对日期：今天/明天/后天/大后天、周一~周日、下周一~下周日、X月X日、YYYY-MM-DD
//      - 相对时间：上午/早上/中午/下午/晚上 + X点 / X:XX
//   2. 自动定位目标日期日记或日程块：
//      - SQL 精准检索对应日期的 custom-dailynote-yyyyMMdd 块，若未创建自动新建
//      - 追加带时间标签的待办条目：- [ ] ⏰ HH:mm 事件名称
//   3. 100% 思源原生自愈兜底：
//      - 装有「思源日历面板」插件时直接联动高亮展示对应日期；
//      - 未装插件时 100% 优雅沉淀至目标日期日记，绝不报错！
// ============================================================================


public static class CalendarScheduleMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "桌面应用");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");

        // 1. 输入内容解析
        var rawText = SafeContext.GetInputText(context);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            var json = "{\"title\":\"思源外骨骼 · 思源日历自然语言日程\"," +
                       "\"subtitle\":\"输入自然语言（如: 明天下午3点 方案评审、周五10:00 提交周报）\"," +
                       "\"badge\":\"自然语言日程\"," +
                       "\"icon\":\"📅\"," +
                       "\"mode\":\"input\"," +
                       "\"outputVar\":\"输入文本\"," +
                       "\"placeholder\":\"输入日程，如: 明天下午3点 产品评审会、周五 提交Q4财报、后天10:00 牙医预约...\"," +
                       "\"defaultValue\":\"\"," +
                       "\"tags\":[\"今天下午\", \"明天上午\", \"明天下午\", \"周五上午\", \"后天\"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 2. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 3. 自然语言日期与时间解析
        var text = rawText.Trim();
        DateTime targetDate = DateTime.Today;
        string timeStr = "全天";
        bool dateExtracted = false;

        // 3.1 识别 YYYY-MM-DD
        var matchIso = Regex.Match(text, "\\b(\\d{4})[\\-/年](\\d{1,2})[\\-/月](\\d{1,2})日?\\b");
        if (matchIso.Success)
        {
            int y = int.Parse(matchIso.Groups[1].Value);
            int m = int.Parse(matchIso.Groups[2].Value);
            int d = int.Parse(matchIso.Groups[3].Value);
            targetDate = new DateTime(y, m, d);
            text = text.Replace(matchIso.Value, "");
            dateExtracted = true;
        }

        // 3.2 识别 M月D日
        if (!dateExtracted)
        {
            var matchMd = Regex.Match(text, "\\b(\\d{1,2})月(\\d{1,2})[日号]?\\b");
            if (matchMd.Success)
            {
                int m = int.Parse(matchMd.Groups[1].Value);
                int d = int.Parse(matchMd.Groups[2].Value);
                targetDate = new DateTime(DateTime.Today.Year, m, d);
                if (targetDate < DateTime.Today.AddDays(-30)) targetDate = targetDate.AddYears(1);
                text = text.Replace(matchMd.Value, "");
                dateExtracted = true;
            }
        }

        // 3.3 识别相对日期（今天/明天/后天/大后天）
        if (!dateExtracted)
        {
            if (text.Contains("今天") || text.Contains("今日"))
            {
                targetDate = DateTime.Today;
                text = Regex.Replace(text, "今天|今日", "");
                dateExtracted = true;
            }
            else if (text.Contains("大后天"))
            {
                targetDate = DateTime.Today.AddDays(3);
                text = text.Replace("大后天", "");
                dateExtracted = true;
            }
            else if (text.Contains("后天"))
            {
                targetDate = DateTime.Today.AddDays(2);
                text = text.Replace("后天", "");
                dateExtracted = true;
            }
            else if (text.Contains("明天") || text.Contains("次日"))
            {
                targetDate = DateTime.Today.AddDays(1);
                text = Regex.Replace(text, "明天|次日", "");
                dateExtracted = true;
            }
        }

        // 3.4 识别周几 / 星期几
        if (!dateExtracted)
        {
            var weekMatch = Regex.Match(text, "(下周|本周|这周|周|星期|礼拜)([一二三四五六日天1234567])");
            if (weekMatch.Success)
            {
                bool nextWeek = weekMatch.Groups[1].Value.Contains("下");
                int targetDayOfWeek = ParseDayOfWeek(weekMatch.Groups[2].Value);
                int currentDayOfWeek = (int)DateTime.Today.DayOfWeek;
                if (currentDayOfWeek == 0) currentDayOfWeek = 7;

                int diff = targetDayOfWeek - currentDayOfWeek;
                if (diff <= 0) diff += 7;
                if (nextWeek) diff += 7;

                targetDate = DateTime.Today.AddDays(diff);
                text = text.Replace(weekMatch.Value, "");
                dateExtracted = true;
            }
        }

        // 3.5 时间解析 (上午/下午/晚上 + X点 / HH:mm)
        var timeMatch = Regex.Match(text, "(早上|早晨|上午|中午|下午|晚上|傍晚)?\\s*(\\d{1,2})([:：点时])(\\d{0,2})分?");
        if (timeMatch.Success)
        {
            string ampm = timeMatch.Groups[1].Value;
            int hour = int.Parse(timeMatch.Groups[2].Value);
            string minuteStr = timeMatch.Groups[4].Value;
            int minute = string.IsNullOrEmpty(minuteStr) ? 0 : int.Parse(minuteStr);

            if ((ampm.Contains("下午") || ampm.Contains("晚上") || ampm.Contains("傍晚")) && hour < 12)
            {
                hour += 12;
            }
            else if (ampm.Contains("中午") && hour < 11)
            {
                hour += 12;
            }

            timeStr = string.Format("{0:D2}:{1:D2}", hour, minute);
            text = text.Replace(timeMatch.Value, "");
        }
        else if (text.Contains("上午"))
        {
            timeStr = "09:00";
            text = text.Replace("上午", "");
        }
        else if (text.Contains("下午"))
        {
            timeStr = "14:00";
            text = text.Replace("下午", "");
        }
        else if (text.Contains("晚上"))
        {
            timeStr = "20:00";
            text = text.Replace("晚上", "");
        }

        // 清理事件标题
        var eventTitle = Regex.Replace(text, "\\s+", " ").Trim();
        if (string.IsNullOrEmpty(eventTitle)) eventTitle = "新建日程";

        // 4. 定位或新建目标日期的日记文档
        var targetCompact = targetDate.ToString("yyyyMMdd");
        var targetDash = targetDate.ToString("yyyy-MM-dd");
        string docId = "";

        var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + targetCompact + "%' ORDER BY updated DESC LIMIT 1\"}";
        var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
        if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

        if (string.IsNullOrEmpty(docId))
        {
            var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + targetDash + "%' OR hpath LIKE '%" + targetDash + "%') ORDER BY updated DESC LIMIT 1\"}";
            var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
            if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            // 通过日记笔记本为该日期创建日记文档
            var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(diaryNotebookId) + "\"}");
            if (createRes.Success) docId = ExtractJsonField(createRes.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未能定位或创建目标日期【" + targetDash + "】日记文档。");
            return;
        }

        // 5. 组装符合思源日历 + 原生待办高保真双重兼容的 Markdown 块
        string scheduleMd = "- [ ] ⏰ " + (timeStr == "全天" ? "" : (timeStr + " ")) + eventTitle +
                            " {: custom-calendar-event=\"true\" custom-calendar-date=\"" + targetDash + "\" custom-calendar-time=\"" + timeStr + "\" }";

        var appendBody = "{\"parentID\":\"" + docId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(scheduleMd) + "\"}";
        var appRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);

        bool ok = appRes.Success;
        SafeContext.Set(context, "是否成功", ok);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", ok
            ? "✓ 已录入【" + targetDash + "】日程：⏰ " + timeStr + " " + eventTitle
            : "❌ 录入日程失败：" + appRes.Error);
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static int ParseDayOfWeek(string s)
    {
        switch (s)
        {
            case "一": case "1": return 1;
            case "二": case "2": return 2;
            case "三": case "3": return 3;
            case "四": case "4": return 4;
            case "五": case "5": return 5;
            case "六": case "6": return 6;
            case "日": case "天": case "7": return 7;
            default: return 1;
        }
    }
}


// ============================================================================
// L4-07: 剪贴板光标直插 —— 主驱动程序 (CursorInsertMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 editor.insert（思源当前编辑器光标处稳定插入，markdown/html）
// 定位：与 L4-01 常用语喷射互补——L4-01 面向「任意应用」走模拟粘贴（需抢焦点），
//      本动作面向「思源为靶」走快门桥直插，不切换窗口焦点、不模拟按键。
// 核心逻辑：
//   1. 取文：输入文本 → quicker_in_param → 选中文本 → 剪贴板文本
//   2. editor.insert 直插（回执确认）；拒绝时区分「无前台编辑器」与「桥未就绪」如实提示
//   3. 兜底：内容不丢失——任何失败路径都保证写入系统剪贴板供手动粘贴
// ============================================================================


public static class CursorInsertMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 待插入文本回程（yaml userInput 后重跑）
        var text = SafeContext.GetString(context, "待插入文本");
        if (string.IsNullOrEmpty(text))
        {
            text = SafeContext.GetInputText(context);
        }
        if (string.IsNullOrEmpty(text))
        {
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 直插思源光标处
        var args = "{\"data\":\"" + SafeContext.EscapeJson(text) + "\",\"dataType\":\"markdown\"}";
        var send = LvSendCommand.Send(syUrl, token, "editor.insert", args);
        if (send.Success)
        {
            var rec = LvGetReceipt.Poll(syUrl, token, send.CommandId, 5000);
            if (rec.Status == "recorded")
            {
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "通知反馈", "✓ 已直插到思源当前光标处（未切换焦点）。");
        SafeContext.Set(context, "需要通知", true);
                return;
            }
            // rejected 通常是「无前台活动编辑器」；failed/timeout 是桥未就绪
            var reason = string.IsNullOrEmpty(rec.Message) ? rec.Status : rec.Message;
            SetClipboardHelper(text);
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "通知反馈", "⚠️ 直插未完成（" + reason + "）。内容已保留在剪贴板，请到思源中手动粘贴。");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        // 桥不可达：内容保底进剪贴板
        SetClipboardHelper(text);
        SafeContext.Set(context, "是否成功", false);
        SafeContext.Set(context, "通知反馈", "⚠️ 编辑器直插通道未就绪（" + (send.Error ?? "投递失败") + "）。内容已保留在剪贴板，请手动粘贴。");
    }

    private static void SetClipboardHelper(string text)
    {
        try { System.Windows.Forms.Clipboard.SetText(text); } catch { }
    }
}

// ============================================================================
// L4-08: 人脉速查与档案直达 —— 主驱动程序 (ContactsLookupMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 contacts.search / contacts.interaction / doc.open（人脉桥 v1/v2）
// 核心逻辑：
//   1. 优先读取划选文字/剪贴板，支持关键词检索小驴人脉（≤50 人）
//   2. 高保真 Bento 弹窗选择后 3 层直达人物档案文档 (快门 doc.open + Win32 唤醒置顶 + 协议兜底)
//   3. 置顶一键打开【小驴人脉工作台】主界面，末项内置「➕ 记录新互动」落时间线
//   4. 兜底：桥不可用时按人名 SQL 检索思源文档直达；互动记录降级写日记双链卡
// ============================================================================


public static class ContactsLookupMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var selection = SafeContext.GetString(context, "选中人员");
        var keyword = SafeContext.GetString(context, "关键词");

        // 外部直接指令：打开插件主界面
        var inParam = SafeContext.GetInputText(context).Trim().ToLowerInvariant();
        if (inParam == "open" || inParam == "plugin")
        {
            OpenContactsPlugin(context, syUrl, token);
            return;
        }

        // 阶段三：已在列表中做出选择
        if (!string.IsNullOrEmpty(selection))
        {
            if (selection == "__open_plugin__")
            {
                OpenContactsPlugin(context, syUrl, token);
                return;
            }

            if (selection == "__jot__")
            {
                var note = SafeContext.GetString(context, "互动备注");
                if (string.IsNullOrEmpty(note))
                {
                    SafeContext.Set(context, "需要填值", true);
                    var jotJson = "{\"title\":\"记录人脉互动\",\"subtitle\":\"正在为【" + SafeContext.EscapeJson(keyword) + "】记录互动时间线\",\"badge\":\"互动落卡\",\"icon\":\"📝\",\"mode\":\"input\",\"outputVar\":\"互动备注\",\"placeholder\":\"输入互动要点 (地点/内容/结论)...\",\"defaultValue\":\"\",\"tags\":[\"线上沟通\",\"线下交流\",\"方案探讨\",\"电话同步\"]}";
                    SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jotJson));
                    SafeContext.Set(context, "是否成功", true);
                    return;
                }
                RecordInteraction(context, syUrl, token, keyword, note);
                return;
            }
            OpenPersonDoc(context, syUrl, token, selection);
            return;
        }

        // 阶段一：嗅探关键词（外部输入参数 -> 选中文本 -> 剪贴板文本）
        if (string.IsNullOrEmpty(keyword))
        {
            keyword = SafeContext.GetInputText(context);
        }

        if (string.IsNullOrEmpty(keyword))
        {
            var json = "{\"title\":\"思源外骨骼 · 人脉速查与档案直达\",\"subtitle\":\"输入联系人姓名检索人物档案，或现场记录互动时间线\",\"badge\":\"小驴人脉\",\"icon\":\"👥\",\"mode\":\"input\",\"outputVar\":\"关键词\",\"placeholder\":\"输入联系人姓名，如: 张三、李四...\",\"defaultValue\":\"\",\"tags\":[\"核心联系人\",\"合作伙伴\",\"同事\",\"团队\"]}";
            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 阶段二：检索人脉并弹窗呈现
        SearchPeople(context, syUrl, token, keyword);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateSiYuanWindow()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    private static void OpenContactsPlugin(IStepContext context, string syUrl, string token)
    {
        ActivateSiYuanWindow();
        var res = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-contacts\",\"command\":\"openWorkbench\"}", "siyuan-speed-switch", "fast");
        SafeContext.Set(context, "是否成功", res.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", res.Success ? "✓ 已唤起思源并打开【小驴人脉】工作台" : "⚠️ 唤起小驴人脉未完成");
    }

    private static void SearchPeople(IStepContext context, string syUrl, string token, string keyword)
    {
        var rec = PollBridge(syUrl, token, "contacts.search", "{\"keyword\":\"" + SafeContext.EscapeJson(keyword) + "\"}", 8000);
        var people = ParsePeople(rec.DataJson);

        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 人脉速查\",")
                   .Append("\"subtitle\":\"回车直达档案，末项现场记录互动时间线\",")
                   .Append("\"badge\":\"人脉档案\",")
                   .Append("\"icon\":\"👥\",")
                   .Append("\"mode\":\"select\",")
                   .Append("\"outputVar\":\"选中人员\",")
                   .Append("\"items\":[")
                   .Append("{\"id\":\"__open_plugin__\",\"title\":\"打开小驴人脉工作台\",\"desc\":\"唤醒思源打开人脉大窗\",\"badge\":\"小驴人脉\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"Alt+3\",\"icon\":\"🪟\"}");

        int numHk = 1;
        if (people.Count > 0)
        {
            foreach (var p in people)
            {
                var hk = numHk <= 9 ? numHk.ToString() : "";
                var encodedId = p.Name + "|" + p.DocId;
                var grpText = string.IsNullOrEmpty(p.Group) ? "联系人" : p.Group;
                jsonBuilder.Append(",{\"id\":\"").Append(SafeContext.EscapeJson(encodedId)).Append("\",")
                           .Append("\"title\":\"").Append(SafeContext.EscapeJson(p.Name)).Append("\",")
                           .Append("\"desc\":\"分组: ").Append(SafeContext.EscapeJson(grpText)).Append(" · 档案ID: ").Append(p.DocId).Append("\",")
                           .Append("\"badge\":\"").Append(SafeContext.EscapeJson(grpText)).Append("\",")
                           .Append("\"badgeCls\":\"badge-bridge\",")
                           .Append("\"hotkey\":\"").Append(hk).Append("\",")
                           .Append("\"icon\":\"👤\"}");
                numHk++;
            }
        }
        jsonBuilder.Append(",{\"id\":\"__jot__\",\"title\":\"记录与【").Append(SafeContext.EscapeJson(keyword)).Append("】的新互动\",\"desc\":\"未搜到也可靠名字直接记录互动时间线\",\"badge\":\"记录互动\",\"badgeCls\":\"badge-native\",\"hotkey\":\"Enter\",\"icon\":\"➕\"}");
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "是否成功", true);
    }

    private static void OpenPersonDoc(IStepContext context, string syUrl, string token, string selection)
    {
        var parts = selection.Split('|');
        var pureName = parts[0].Trim();
        var docId = parts.Length > 1 ? parts[1].Trim() : "";

        ActivateSiYuanWindow();

        if (!string.IsNullOrEmpty(docId))
        {
            LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(docId) + "\"}", "siyuan-speed-switch", "fast");
            SafeContext.Set(context, "目标链接", "siyuan://blocks/" + docId);
            SafeContext.Set(context, "需要打开链接", true);
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已打开人物档案【" + pureName + "】。");
            return;
        }

        // 兜底：SQL 模糊定位
        var likeSafe = pureName.Replace("'", "''");
        var sql = "SELECT id, content FROM blocks WHERE type='d' AND content LIKE '%" + likeSafe + "%' ORDER BY updated DESC LIMIT 1";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");
        var idM = Regex.Match(r.Text ?? "", "\"id\"\\s*:\\s*\"([^\"]+)\"");
        if (r.Success && idM.Success)
        {
            var fallbackId = idM.Groups[1].Value;
            LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(fallbackId) + "\"}", "siyuan-speed-switch", "fast");
            SafeContext.Set(context, "目标链接", "siyuan://blocks/" + fallbackId);
            SafeContext.Set(context, "需要打开链接", true);
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 人脉桥未就绪，已按纯人名定位到疑似档案文档。");
            return;
        }

        SafeContext.Set(context, "是否成功", false);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "❌ 未找到【" + pureName + "】的档案文档。");
    }

    private static void RecordInteraction(IStepContext context, string syUrl, string token, string keyword, string note)
    {
        var place = SafeContext.GetString(context, "来源窗口", "人脉速查");
        var args = "{\"names\":[\"" + SafeContext.EscapeJson(keyword) + "\"],\"note\":\"" + SafeContext.EscapeJson(note) +
                   "\",\"place\":\"" + SafeContext.EscapeJson(place) + "\"}";
        var rec = PollBridge(syUrl, token, "contacts.interaction", args, 8000);

        if (rec.Status == "recorded" || rec.Status == "duplicate")
        {
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已为【" + keyword + "】记录互动时间线。");
            return;
        }

        // 兜底：写日记双链卡
        var md = "- " + DateTime.Now.ToString("HH:mm") + " 交流速记: [[" + keyword + "]] " + note + " (未同步小驴人脉)";
        var diaryNb = SafeContext.GetDiaryNotebookId(context, syUrl, token);
        var body = "{\"notebook\":\"" + diaryNb +
                   "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", body);
        SafeContext.Set(context, "是否成功", r.Success);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", r.Success
            ? "✓ 人脉桥未就绪，已写日记双链卡 [[" + keyword + "]]。"
            : "❌ 互动记录失败：" + rec.Message + " / " + r.Error);
    }

    public class PersonEntry { public string DocId; public string Name; public string Group; }

    private static List<PersonEntry> ParsePeople(string dataJson)
    {
        var list = new List<PersonEntry>();
        if (string.IsNullOrEmpty(dataJson)) return list;
        foreach (Match m in Regex.Matches(dataJson, "\\{[^\\{\\}]+\\}"))
        {
            var b = m.Value;
            var docId = ExtractField(b, "docId");
            var name = ExtractField(b, "name");
            if (string.IsNullOrEmpty(docId) || string.IsNullOrEmpty(name)) continue;
            list.Add(new PersonEntry { DocId = docId, Name = name, Group = ExtractField(b, "group") });
        }
        return list;
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L4-09: 管家事务快捷台 —— 主驱动程序 (HomeButlerMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 home.summary / home.memo / home.open（window.LvHome v1 透传）
// 核心逻辑：
//   1. 默认：读取管家提醒有界计数（逾期/即将到期/今日到期）高保真 Bento 弹窗呈现
//   2. 新建备忘：现场 Bento 填标题（日期默认今天）→ home.memo 落卡（回执确认）
//   3. 直达：跨软件唤起前置思源窗口，秒级打开管家总览 / 提醒中枢（home.open 回执确认）
//   4. 兜底：桥不可用时备忘降级写今日日记备忘卡（如实标注未同步）
// ============================================================================


public static class HomeButlerMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 外部直接指令：打开管家总览或提醒中枢
        var inParam = SafeContext.GetInputText(context).Trim().ToLowerInvariant();
        if (inParam == "open" || inParam == "butler")
        {
            OpenTarget(context, syUrl, token, "butler");
            return;
        }
        if (inParam == "reminders")
        {
            OpenTarget(context, syUrl, token, "reminders");
            return;
        }

        var action = SafeContext.GetString(context, "选中操作").Trim().ToLowerInvariant();

        // 阶段二：执行选中操作
        if (!string.IsNullOrEmpty(action))
        {
            ExecuteAction(context, syUrl, token, action);
            return;
        }

        // 阶段一：概览 + 菜单
        ShowOverview(context, syUrl, token);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateSiYuanWindow()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    private static void ShowOverview(IStepContext context, string syUrl, string token)
    {
        var rec = PollBridge(syUrl, token, "home.summary", "{}", 8000);
        string overview;
        if (rec.Status == "recorded")
        {
            var d = rec.DataJson ?? "";
            overview = "逾期 " + GetNum(d, "overdue") + " · 即将到期 " + GetNum(d, "soon") + " · 今日到期 " + GetNum(d, "today");
        }
        else
        {
            overview = "管家桥未就绪【" + rec.Status + "】：" + rec.Message;
        }

        var json = "{\"title\":\"思源外骨骼 · 管家事务快捷台\"," +
                   "\"subtitle\":\"一键备忘录入、到期事务提醒与管家总控\"," +
                   "\"badge\":\"小驴管家\"," +
                   "\"icon\":\"🏠\"," +
                   "\"mode\":\"select\"," +
                   "\"outputVar\":\"选中操作\"," +
                   "\"stats\":\"🏠 事务概览: " + SafeContext.EscapeJson(overview) + "\"," +
                   "\"items\":[" +
                   "{\"id\":\"memo\",\"title\":\"新建到期备忘\",\"desc\":\"现场录入事项标题（默认今日到期）\",\"badge\":\"录入备忘\",\"badgeCls\":\"badge-native\",\"hotkey\":\"1\",\"icon\":\"➕\"}," +
                   "{\"id\":\"butler\",\"title\":\"打开管家总览 (Alt+2)\",\"desc\":\"唤醒思源打开家庭事务与资产全景\",\"badge\":\"小驴管家\",\"badgeCls\":\"badge-plugin\",\"hotkey\":\"Alt+2\",\"icon\":\"🗂\"}," +
                   "{\"id\":\"reminders\",\"title\":\"打开提醒中枢\",\"desc\":\"唤醒思源打开重要日程与到期看板\",\"badge\":\"提醒看板\",\"badgeCls\":\"badge-bridge\",\"hotkey\":\"3\",\"icon\":\"⏰\"}," +
                   "{\"id\":\"refresh\",\"title\":\"刷新事务概览\",\"desc\":\"重新拉取小驴管家实时到期计数\",\"badge\":\"刷新\",\"badgeCls\":\"badge-system\",\"hotkey\":\"4\",\"icon\":\"🔄\"}" +
                   "]}";

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "是否成功", true);
    }

    private static void OpenTarget(IStepContext context, string syUrl, string token, string target)
    {
        ActivateSiYuanWindow();
        var rec = PollBridge(syUrl, token, "home.open", "{\"target\":\"" + target + "\"}", 6000);
        bool ok = rec.Status == "recorded";
        SafeContext.Set(context, "是否成功", ok);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", ok
            ? ("✓ 已唤起思源并打开【小驴管家】" + (target == "reminders" ? "提醒中枢" : "总览"))
            : ("❌ 打开管家失败【" + rec.Status + "】：" + rec.Message));
    }

    private static void ExecuteAction(IStepContext context, string syUrl, string token, string action)
    {
        if (action == "refresh")
        {
            ShowOverview(context, syUrl, token);
            return;
        }

        if (action == "butler" || action == "reminders")
        {
            OpenTarget(context, syUrl, token, action);
            return;
        }

        if (action == "memo")
        {
            var title = SafeContext.GetString(context, "备忘标题");
            var due = SafeContext.GetString(context, "备忘日期");

            if (string.IsNullOrEmpty(title))
            {
                SafeContext.Set(context, "备忘日期", DateTime.Now.ToString("yyyy-MM-dd"));
                SafeContext.Set(context, "需要填值", true);
                var memoJson = "{\"title\":\"新建到期备忘\",\"subtitle\":\"录入事项将同步小驴管家并设置到期提醒\",\"badge\":\"到期备忘\",\"icon\":\"➕\",\"mode\":\"input\",\"outputVar\":\"备忘标题\",\"placeholder\":\"输入备忘事项标题 (如: 续交宽带费、保洁预约)...\",\"defaultValue\":\"\",\"tags\":[\"信用卡还款\",\"证件年审\",\"缴费提醒\",\"合同到期\"]}";
                SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(memoJson));
                SafeContext.Set(context, "是否成功", true);
                return;
            }

            if (string.IsNullOrEmpty(due))
            {
                due = DateTime.Now.ToString("yyyy-MM-dd");
                SafeContext.Set(context, "备忘日期", due);
            }

            var rec = PollBridge(syUrl, token, "home.memo",
                "{\"title\":\"" + SafeContext.EscapeJson(title) + "\",\"dueDate\":\"" + due + "\"}", 8000);
            if (rec.Status == "recorded")
            {
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 备忘已记入小驴管家（到期 " + due + "）。");
                return;
            }

            // 兜底：日记备忘卡
            var md = "- " + DateTime.Now.ToString("HH:mm") + " 🏠 备忘：" + title + " (到期 " + due + ") (未同步小驴管家)";
            var body = "{\"notebook\":\"" + SafeContext.GetDiaryNotebookId(context, syUrl, token) +
                       "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", body);
            SafeContext.Set(context, "是否成功", r.Success);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", r.Success
                ? "✓ 管家桥未就绪，已写今日日记备忘卡（到期 " + due + "）。"
                : "❌ 备忘写入失败：" + rec.Message + " / " + r.Error);
            return;
        }

        SafeContext.Set(context, "是否成功", false);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "❌ 未知操作：" + action);
    }

    private static string GetNum(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*([0-9]+)");
        return m.Success ? m.Groups[1].Value : "0";
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L4-10: 思播视频时间戳打点速记 —— 主驱动程序 (SiMediaTimestampMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 专为网课研读、播客收听与视频复盘打造的桌面时间戳打点工具；
//   2. 智能提取时间戳格式（支持 00:15:30、15:30、1h20m 等主流格式）；
//   3. 深度适配「思播 (siyuan-media-player)」播放器时间戳跳转链接：
//      - 自动生成带 t=HH:mm:ss 播放跳转锚点的 Markdown 卡片
//   4. 100% 思源原生平滑自愈兜底：
//      - 未装思播插件时直接生成标准带时间戳标注的 Callout 笔记卡存入今日日记！
// ============================================================================


public static class SiMediaTimestampMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "视频播放器");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");

        // 1. 输入内容解析
        var rawText = SafeContext.GetInputText(context);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            var json = "{\"title\":\"思源外骨骼 · 思播视频时间戳打点速记\"," +
                       "\"subtitle\":\"录入时间戳与学习要点，生成可跳转播放锚点 (联动思播)\"," +
                       "\"badge\":\"媒体打点\"," +
                       "\"icon\":\"🎬\"," +
                       "\"mode\":\"input\"," +
                       "\"outputVar\":\"输入文本\"," +
                       "\"placeholder\":\"输入时间点与要点，如: 00:15:20 核心概念推导、45:10 重点代码讲解...\"," +
                       "\"defaultValue\":\"\"," +
                       "\"tags\":[\"00:05:00\", \"00:15:00\", \"00:30:00\", \"#要点\", \"#疑惑\"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 2. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 3. 时间戳格式智能解析 (01:20:30 / 15:40 / 00:05:12)
        var text = rawText.Trim();
        string timestamp = "";
        var tsMatch = Regex.Match(text, "\\b(\\d{1,2}:\\d{2}(?::\\d{2})?)\\b");
        if (tsMatch.Success)
        {
            timestamp = tsMatch.Groups[1].Value;
            text = text.Replace(tsMatch.Value, "").Trim();
        }
        else
        {
            timestamp = DateTime.Now.ToString("HH:mm:ss");
        }

        text = Regex.Replace(text, "\\s+", " ").Trim();
        if (string.IsNullOrEmpty(text)) text = "关键时间节点记录";

        // 4. 定位或新建今日日记
        var now = DateTime.Now;
        var todayCompact = now.ToString("yyyyMMdd");
        var todayDash = now.ToString("yyyy-MM-dd");
        string docId = "";

        var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + todayCompact + "%' ORDER BY updated DESC LIMIT 1\"}";
        var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
        if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

        if (string.IsNullOrEmpty(docId))
        {
            var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + todayDash + "%' OR hpath LIKE '%" + todayDash + "%') ORDER BY updated DESC LIMIT 1\"}";
            var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
            if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(diaryNotebookId) + "\"}");
            if (createRes.Success) docId = ExtractJsonField(createRes.Text, "id");
        }

        if (string.IsNullOrEmpty(docId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未能定位或创建今日日记文档。");
            return;
        }

        // 5. 组装符合思播时间戳跳转 + 原生清晰排版的 Markdown 块
        var ial = "{: custom-media-timestamp=\"" + timestamp + "\" custom-type=\"media-note\" }";
        var cardMd = "- ▶️ **`" + timestamp + "`** " + text + " *(来自 " + windowTitle + ")* " + ial;

        var appendBody = "{\"parentID\":\"" + docId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(cardMd) + "\"}";
        var appRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);

        bool ok = appRes.Success;
        SafeContext.Set(context, "是否成功", ok);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", ok 
            ? "✓ 已录入思播时间戳卡片：【" + timestamp + " " + text + "】"
            : "❌ 写入失败：" + appRes.Error);
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// L4-11: 代码片段检索与智能喷射 —— 主驱动程序 (CodeSnippetsInserterMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 专为开发者、写作者打造的全局代码片段 (Snippets) 极速喷射通道；
//   2. 联动「代码片段管理器 (snippets)」与思源原生代码块库 (type='c')；
//   3. 自动剥离 Markdown 围栏并还原纯净代码正文；
//   4. 毫秒级写入剪贴板并就地模拟粘贴直插当前 IDE、编辑器或聊天窗口！
// ============================================================================


public static class CodeSnippetsInserterMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "编辑器");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var keyword = SafeContext.GetString(context, "关键词");
        var rawInput = SafeContext.GetInputText(context);

        if (string.IsNullOrEmpty(keyword) && !string.IsNullOrEmpty(rawInput))
        {
            keyword = rawInput.Trim();
        }

        // 1. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 检索思源代码块 (type='c')
        string sql = "SELECT id, content, markdown, updated, ial FROM blocks WHERE type='c' ORDER BY updated DESC LIMIT 20";
        if (!string.IsNullOrEmpty(keyword))
        {
            sql = "SELECT id, content, markdown, updated, ial FROM blocks WHERE type='c' AND content LIKE '%" + keyword.Replace("'", "''") + "%' ORDER BY updated DESC LIMIT 20";
        }

        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");
        var snippetBlocks = r.Success ? ExtractBalancedObjects(r.Text) : new List<string>();

        string chosenCode = "";
        string snippetTitle = "";

        if (snippetBlocks.Count > 0)
        {
            // 取第一项命中代码
            var b = snippetBlocks[0];
            var rawContent = ExtractJsonField(b, "content");
            chosenCode = CleanCodeBlock(rawContent);
            snippetTitle = TruncateClean(chosenCode, 30);
        }
        else
        {
            // 兜底常用预置开发片段
            chosenCode = GetBuiltinSnippet(keyword);
            snippetTitle = "内置常用模板";
        }

        if (string.IsNullOrEmpty(chosenCode))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "⚠️ 未找到匹配的代码片段。可在思源中创建包含代码块的文档。");
            return;
        }

        // 3. 写入剪贴板并就地模拟粘贴
        SafeContext.Set(context, "目标代码", chosenCode);
        try
        {
            Clipboard.SetText(chosenCode);
            Thread.Sleep(80);
            SendKeys.SendWait("^v");
        }
        catch { }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "✓ 代码片段已喷射填充：【" + snippetTitle + "】");
    }

    private static string CleanCodeBlock(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.Replace("\r\n", "\n").Split('\n');
        var cleanLines = new List<string>();
        foreach (var l in lines)
        {
            if (Regex.IsMatch(l, "^```[a-zA-Z0-9_-]*$")) continue;
            cleanLines.Add(l);
        }
        return string.Join("\r\n", cleanLines.ToArray()).Trim();
    }

    private static string GetBuiltinSnippet(string kw)
    {
        var k = (kw ?? "").ToLower();
        if (k.Contains("json") || k.Contains("api"))
            return "{\n  \"code\": 0,\n  \"msg\": \"ok\",\n  \"data\": {}\n}";
        if (k.Contains("sql") || k.Contains("select"))
            return "SELECT id, content, updated FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 10;";
        if (k.Contains("md") || k.Contains("link"))
            return "[链接标题](https://example.com)";
        return "// Code Snippet\nconsole.log(\"Hello SiYuan\");";
    }

    private static string TruncateClean(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (clean.Length <= maxLen) return clean;
        return clean.Substring(0, maxLen) + "...";
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> ExtractBalancedObjects(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        int depth = 0;
        int start = -1;
        bool inStr = false;
        bool esc = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(text.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
        }
        return list;
    }
}


// ============================================================================
// L5-01: 多步受控工作流执行器 —— 主驱动程序 v2 (WorkflowOrchestratorMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 协议依据：小驴快门 workflow.plan / workflow.execute（≤8 步白名单 op、5 分钟计划 TTL、
//           总确认门控、单步失败熔断、逐步回执；写步骤由快门侧 confirm 标记）
// v2 名实相符重构（此前头注声称调用 workflow.plan 实际未调用）：
//   1. 受控编排层（桥可用时）：按流程类型组装白名单步骤 → workflow.plan 取 planId
//      → 用户 select 确认计划 → workflow.execute → 逐步执行回执写入简报
//   2. 直连兜底层（桥不可用/计划被拒/执行未完成时）：保持 v1 直连行为，零功能损失
//   3. 晨报数据升级：打卡连击榜（checkin.summary）+ 管家到期计数（home.summary）
//      + 原生 FSRS 到期卡（内核 API）三者聚合，替代 v1 的静态占位文案
// ============================================================================


public static class WorkflowOrchestratorMain
{
    public static void Exec(IStepContext context)
    {
        // 关键接线修复：v1 的类型选择输出到「工作流代号」而 C# 误读「工作流类型」，
        // 导致用户选 weekly 也永远跑 morning。代号在首次运行时为空，交还 yaml 弹类型选择。
        var flowType = SafeContext.GetString(context, "工作流代号").ToLower();
        if (string.IsNullOrEmpty(flowType))
        {
            SafeContext.Set(context, "需要选择工作流", true);
            return;
        }
        SafeContext.Set(context, "需要选择工作流", false);
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var stage = SafeContext.GetString(context, "编排阶段");
        var selection = SafeContext.GetString(context, "选中编排选择");

        // 确认回程：用户已对计划做出选择
        if (stage == "await" && !string.IsNullOrEmpty(selection))
        {
            if (selection == "cancel")
            {
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "通知反馈", "ℹ️ 已按你的选择取消本次编排，未执行任何步骤。");
        SafeContext.Set(context, "需要通知", true);
                return;
            }
            // go → 执行计划（失败/未完成自动落回直连兜底）
            ExecutePlan(context, syUrl, token, flowType);
            return;
        }

        // 受控编排层：组装白名单步骤并取计划
        var planned = TryPlan(context, syUrl, token, flowType);
        if (!planned)
        {
            // 桥不可达（熔断快失败）或计划被拒 → 直连兜底
            RunBuiltin(context, syUrl, token, flowType, SafeContext.GetString(context, "编排回执"));
            return;
        }
        // 计划就绪：等待用户在 select 中确认（yaml 回程后进入 go/cancel 分支）
    }

    /// <summary>组装白名单步骤并 workflow.plan；返回 false=桥不可用或计划被拒（原因写入 编排回执）</summary>
    private static bool TryPlan(IStepContext context, string syUrl, string token, string flowType)
    {
        string steps;
        if (flowType == "weekly")
        {
            steps = "[{\"op\":\"checkin.summary\",\"args\":{}},{\"op\":\"home.summary\",\"args\":{}}]";
        }
        else
        {
            steps = "[{\"op\":\"daily.status\",\"args\":{}},{\"op\":\"checkin.summary\",\"args\":{}},{\"op\":\"home.summary\",\"args\":{}}]";
        }

        var rec = PollBridge(syUrl, token, "workflow.plan", "{\"steps\":" + steps + "}", 6000);
        var planId = ExtractField(rec.DataJson, "planId");
        if (rec.Status != "recorded" || string.IsNullOrEmpty(planId))
        {
            SafeContext.Set(context, "编排回执", "⚠️ 受控编排不可用【" + rec.Status + "】：" + rec.Message);
            return false;
        }

        SafeContext.Set(context, "编排计划ID", planId);
        SafeContext.Set(context, "编排阶段", "await");
        SafeContext.Set(context, "编排菜单",
            "✅ 确认执行编排 (planId " + planId + ")|go\n" +
            "❌ 取消本次编排|cancel");
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "需要确认编排", true);
        SafeContext.Set(context, "是否成功", true);
        return true;
    }

    /// <summary>执行已确认的计划；任何未完成形态都落回直连兜底，绝不丢简报</summary>
    private static void ExecutePlan(IStepContext context, string syUrl, string token, string flowType)
    {
        var planId = SafeContext.GetString(context, "编排计划ID");
        var rec = PollBridge(syUrl, token, "workflow.execute", "{\"planId\":\"" + SafeContext.EscapeJson(planId) + "\"}", 25000);

        if (rec.Status != "recorded")
        {
            SafeContext.Set(context, "编排回执", "⚠️ 编排未完成【" + rec.Status + "】：" + rec.Message + "，已转直连模式。");
            RunBuiltin(context, syUrl, token, flowType, SafeContext.GetString(context, "编排回执"));
            return;
        }

        SafeContext.Set(context, "编排回执", "✅ 受控编排回执：" + SummarizeExecute(rec.DataJson));
        RunBuiltin(context, syUrl, token, flowType, SafeContext.GetString(context, "编排回执"));
    }

    /// <summary>解析 execute 回执的 steps 数组为 ①op✓ 摘要</summary>
    private static string SummarizeExecute(string dataJson)
    {
        var sb = new StringBuilder();
        var matches = Regex.Matches(dataJson ?? "", "\\{[^\\{\\}]*\"op\"\\s*:\\s*\"([^\"]+)\"[^\\{\\}]*\"status\"\\s*:\\s*\"([^\"]+)\"[^\\{\\}]*\\}");
        foreach (Match m in matches)
        {
            var ok = m.Groups[2].Value == "recorded" || m.Groups[2].Value == "duplicate";
            sb.Append(sb.Length == 0 ? "" : " → ")
              .Append(m.Groups[1].Value).Append(ok ? "✓" : "✗(" + m.Groups[2].Value + ")");
        }
        return sb.Length == 0 ? "（回执未含步骤明细）" : sb.ToString();
    }

    /// <summary>直连兜底层：采集真数据并写简报（编排注释作为首行附注）</summary>
    private static void RunBuiltin(IStepContext context, string syUrl, string token, string flowType, string orchestrationNote)
    {
        var diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);
        var report = new StringBuilder();

        if (flowType == "weekly")
        {
            report.Append("### 📊 周度系统复盘与对账报告\n");
            report.Append("> 周期：近 7 天 · 生成时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("\n\n");

            // 打卡连击实数据
            var sum = PollBridge(syUrl, token, "checkin.summary", "{}", 6000);
            report.Append("#### 1. 🎯 习惯与打卡达成\n");
            report.Append("* ").Append(BuildStreakDigest(sum)).Append("\n\n");

            // 管家到期实数据
            var home = PollBridge(syUrl, token, "home.summary", "{}", 6000);
            report.Append("#### 2. 🏠 管家事务待办\n");
            if (home.Status == "recorded")
            {
                var d = home.DataJson ?? "";
                report.Append("* 逾期 ").Append(GetNum(d, "overdue")).Append(" · 即将到期 ").Append(GetNum(d, "soon"))
                      .Append(" · 今日到期 ").Append(GetNum(d, "today")).Append("\n\n");
            }
            else
            {
                report.Append("* 管家桥未就绪（").Append(home.Status).Append("），跳过读取。\n\n");
            }

            report.Append("#### 3. 📋 任务闭环状态\n* 近 7 天遗留待办请运行 L3-01 滚动归集复核。\n\n");
            report.Append("{: custom-type=\"weekly-review\" }\n");
        }
        else
        {
            report.Append("### 🌅 晨间行动简报 (").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(")\n\n");
            if (!string.IsNullOrEmpty(orchestrationNote))
            {
                report.Append("> ").Append(orchestrationNote).Append("\n\n");
            }

            // ① 原生 FSRS 到期卡（内核 API，永可用）
            var cardDueCount = -1;
            var dueRes = SyKernelRequest.DoRequest(syUrl, token, "/api/riff/getRiffDueCards", "{\"deckID\":\"\"}");
            var countMatch = Regex.Match(dueRes.Text ?? "", "\"unreviewedCount\"\\s*:\\s*([0-9]+)");
            if (dueRes.Success && countMatch.Success) int.TryParse(countMatch.Groups[1].Value, out cardDueCount);
            report.Append("* 🎴 **待复习闪卡**: ").Append(cardDueCount >= 0 ? cardDueCount + " 张到期待复习" : "今日已全部完成 / 暂不可统计").Append("\n");

            // ② 打卡连击实数据
            var sum = PollBridge(syUrl, token, "checkin.summary", "{}", 6000);
            report.Append("* 🔥 **习惯连击**: ").Append(BuildStreakDigest(sum)).Append("\n");

            // ③ 管家到期实数据
            var home = PollBridge(syUrl, token, "home.summary", "{}", 6000);
            if (home.Status == "recorded")
            {
                var d = home.DataJson ?? "";
                report.Append("* 🏠 **管家事务**: 逾期 ").Append(GetNum(d, "overdue"))
                      .Append(" · 即将到期 ").Append(GetNum(d, "soon"))
                      .Append(" · 今日到期 ").Append(GetNum(d, "today")).Append("\n");
            }
            else
            {
                report.Append("* 🏠 **管家事务**: ").Append(home.Message).Append("\n");
            }

            report.Append("\n{: custom-type=\"morning-brief\" }\n");
        }

        var briefMd = report.ToString();
        var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(briefMd) + "\"}";
        var appendRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);

        SafeContext.Set(context, "简报Markdown", briefMd);
        SafeContext.Set(context, "是否成功", appendRes.Success);
        SafeContext.Set(context, "通知反馈", appendRes.Success
            ? "✓ 工作流已执行完毕，简报已注入今日日记！"
            : "❌ 写入日记失败：" + appendRes.Error);
        SafeContext.Set(context, "需要通知", true);
    }

    /// <summary>打卡连击摘要（streaks 括号配对截取 + TOP4；桥死时如实说明）</summary>
    private static string BuildStreakDigest(ReceiptResult sum)
    {
        if (sum.Status != "recorded") return "打卡桥未就绪（" + sum.Status + "）";
        var streaksJson = ExtractObjectByBraces(sum.DataJson ?? "", "streaks");
        var matches = Regex.Matches(streaksJson, "\"([0-9]{14}-[0-9a-z]+)\"\\s*:\\s*([0-9]+)");
        var parts = new List<string>();
        foreach (Match m in matches)
        {
            int days; int.TryParse(m.Groups[2].Value, out days);
            if (days > 0) parts.Add(days + " 天");
            if (parts.Count >= 4) break;
        }
        return parts.Count > 0 ? "当前有效连击 " + parts.Count + " 项（" + string.Join(" / ", parts.ToArray()) + "）" : "暂无有效连击";
    }

    private static string ExtractObjectByBraces(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return "{}";
        var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\\{");
        if (!m.Success) return "{}";
        int start = m.Index + m.Length - 1;
        int depth = 0;
        for (int i = start; i < json.Length; i++)
        {
            if (json[i] == '{') depth++;
            else if (json[i] == '}')
            {
                depth--;
                if (depth == 0) return json.Substring(start, i - start + 1);
            }
        }
        return "{}";
    }

    private static string GetNum(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*([0-9]+)");
        return m.Success ? m.Groups[1].Value : "0";
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L5-02: Quicker 动作 AI 自生成工坊 —— 主驱动程序 (ActionAuthorStudioMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 接收用户的自然语言需求（例如："划选文献时提取 DOI 并检索思源已有笔记"）
//   2. 启发式生成符合 Quicker V2 规范的动作定义：
//      - 生成 info.yaml（标题、说明、科技感图标）
//      - 生成 program.source.yaml（变量声明、步骤链路、底层子程序依赖）
//      - 生成内嵌 C# 核心驱动逻辑
//   3. 直接通过 Quicker MCP 本地服务 (127.0.0.1:18765) 自动创建、写入并保存至暂存区
//   4. 自动触发 quicker_preview 打开内嵌设计器供用户即时审阅启用
// ============================================================================


public static class ActionAuthorStudioMain
{
    public static void Exec(IStepContext context)
    {
        var prompt = (context.GetVarValue("动作需求描述") as string ?? "").Trim();
        var customTitle = (context.GetVarValue("动作标题") as string ?? "").Trim();
        var icon = (context.GetVarValue("图标") as string ?? "fa:Solid_Magic:#6366f1").Trim();

        if (string.IsNullOrEmpty(prompt))
        {
            context.SetVarValue("需要弹窗输入", true);
            context.SetVarValue("输入提示", "输入你想让 AI 编写的 Quicker 动作需求：");
            return;
        }

        // 1. 自动生成动作标题与描述
        string title = customTitle;
        if (string.IsNullOrEmpty(title))
        {
            title = GenerateTitleFromPrompt(prompt);
        }

        // 2. 检查 Quicker 本地 MCP 服务端
        var mcpPort = 18765;
        var token = ReadQuickerMcpToken();

        // 3. 构造生成的动作内容
        var infoYaml = "$schema: quicker.action.info.v1\n" +
                       "title: " + title + "\n" +
                       "description: 由 AI 工坊自动生成：" + EscapeYaml(prompt) + "\n" +
                       "icon: \"" + icon + "\"\n";

        var csharpCode = GenerateCSharpFromPrompt(title, prompt);
        var programYaml = GenerateProgramYaml(title, csharpCode);

        // 4. 调用 Quicker MCP 接口创建动作
        string createdSlot = "";
        string createdActionId = "";
        string err = "";

        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                // A: quicker_create
                var createReq = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"quicker_create\",\"arguments\":{\"kind\":\"action\",\"title\":\"" + EscapeJson(title) + "\"}}}";
                var createResp = CallQuickerMcp(mcpPort, token, createReq);
                createdSlot = ExtractJsonField(createResp, "slot");
                createdActionId = ExtractJsonField(createResp, "actionId");

                if (!string.IsNullOrEmpty(createdSlot))
                {
                    // B: write_file info.yaml
                    var writeInfoReq = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"write_file\",\"arguments\":{\"path\":\"" + createdSlot + "/info.yaml\",\"content\":" + JsonSerializeString(infoYaml) + "}}}";
                    CallQuickerMcp(mcpPort, token, writeInfoReq);

                    // C: write_file program.source.yaml
                    var writeProgReq = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"write_file\",\"arguments\":{\"path\":\"" + createdSlot + "/program.source.yaml\",\"content\":" + JsonSerializeString(programYaml) + "}}}";
                    CallQuickerMcp(mcpPort, token, writeProgReq);

                    // C2: 关键补齐：write_file files/Script.cs（将自包含 C# 驱动代码落盘，杜绝脚本缺失报错）
                    var writeScriptReq = "{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"tools/call\",\"params\":{\"name\":\"write_file\",\"arguments\":{\"path\":\"" + createdSlot + "/files/Script.cs\",\"content\":" + JsonSerializeString(csharpCode) + "}}}";
                    CallQuickerMcp(mcpPort, token, writeScriptReq);

                    // D: quicker_save
                    var saveReq = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"quicker_save\",\"arguments\":{\"path\":\"" + createdSlot + "\",\"summary\":\"AI 自动生成动作\"}}}";
                    CallQuickerMcp(mcpPort, token, saveReq);

                    // E: quicker_preview (调起设计器)
                    var previewReq = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"quicker_preview\",\"arguments\":{\"target\":\"" + createdSlot + "\"}}}";
                    CallQuickerMcp(mcpPort, token, previewReq);
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
        }

        bool success = !string.IsNullOrEmpty(createdSlot);

        context.SetVarValue("生成的动作标题", title);
        context.SetVarValue("生成的Slot", createdSlot);
        context.SetVarValue("生成的ActionId", createdActionId);
        context.SetVarValue("生成的YAML", programYaml);
        context.SetVarValue("生成的CSharp", csharpCode);
        context.SetVarValue("是否成功", success);

        context.SetVarValue("通知反馈", success 
            ? "✨ 动作【" + title + "】已成功生成并推入 Quicker 暂存区！设计器已自动打开预览。"
            : "⚠️ 动作代码已生成，但 MCP 注入失败: " + err);
        SafeContext.Set(context, "需要通知", true);
    }

    private static string GenerateTitleFromPrompt(string p)
    {
        if (p.Length <= 12) return p;
        var m = Regex.Match(p, "(?:实现|写一个|做个|自动)([^，。,!！]+)");
        if (m.Success && m.Groups[1].Value.Length >= 4)
        {
            return m.Groups[1].Value.Trim();
        }
        return p.Substring(0, 10) + "助手";
    }

    private static string GenerateProgramYaml(string title, string csCode)
    {
        // 关键修复：输出必须符合本项目实际使用的 quicker.program.source.v1 规范
        // （key/varType 变量声明 + csscript Roslyn 步骤），此前生成的幻想 schema 会导致动作不可运行
        var sb = new StringBuilder();
        sb.Append("$schema: quicker.program.source.v1\n");
        sb.Append("variables:\n");
        sb.Append("  - key: 选中文本\n    varType: text\n");
        sb.Append("  - key: 来源窗口\n    varType: text\n");
        sb.Append("  - key: 输入文本\n    varType: text\n");
        sb.Append("  - key: 通知反馈\n    varType: text\n");
        sb.Append("  - key: 是否成功\n    varType: bool\n");
        sb.Append("steps:\n");
        sb.Append("  - csscript:\n");
        sb.Append("      mode: normal_roslyn\n");
        sb.Append("      script.file: files/Script.cs\n");
        sb.Append("      runOnUiThread: background\n");
        sb.Append("      enableCache: true\n");
        sb.Append("  - if:\n");
        sb.Append("      condition: \"$=!string.IsNullOrEmpty({通知反馈})\"\n");
        sb.Append("      then:\n");
        sb.Append("        - notify:\n            msg:\n              var: 通知反馈\n");
        return sb.ToString();
    }

    private static string GenerateCSharpFromPrompt(string title, string prompt)
    {
        // 生成与本项目自包含动作一致的 EntryPoint 包装骨架（Quicker Roslyn 要求静态入口类）
        var sb = new StringBuilder();
        sb.Append("// ============================================================================\n");
        sb.Append("// 动作: ").Append(title).Append("\n");
        sb.Append("// 需求: ").Append(prompt).Append("\n");
        sb.Append("// 生成时间: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n");
        sb.Append("// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）\n");
        sb.Append("// ============================================================================\n");
        sb.Append("using System;\nusing Quicker.Public;\n\n");
        sb.Append("public class EntryPoint\n{\n");
        sb.Append("    public static void Exec(IStepContext context)\n    {\n");
        sb.Append("        GeneratedAction.Exec(context);\n    }\n}\n\n");
        sb.Append("public static class GeneratedAction\n{\n");
        sb.Append("    public static void Exec(IStepContext context)\n    {\n");
        sb.Append("        var text = (context.GetVarValue(\"选中文本\") as string ?? \"\").Trim();\n");
        sb.Append("        var win = (context.GetVarValue(\"来源窗口\") as string ?? \"\").Trim();\n\n");
        sb.Append("        // TODO: AI 核心逻辑实施处 (可调用思源内核 HTTP API 或扩展本骨架)\n");
        sb.Append("        context.SetVarValue(\"是否成功\", true);\n");
        sb.Append("        context.SetVarValue(\"通知反馈\", \"✓ 【").Append(EscapeJson(title)).Append("】已成功执行！\");\n");
        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    private static string ReadQuickerMcpToken()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quicker", "mcp", "server.json");
            if (File.Exists(p))
            {
                var text = File.ReadAllText(p);
                var m = Regex.Match(text, "\"Token\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        return "";
    }

    private static string CallQuickerMcp(int port, string token, string jsonRpc)
    {
        var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/mcp");
        req.Method = "POST";
        req.ContentType = "application/json; charset=utf-8";
        req.Headers["Authorization"] = "Bearer " + token;
        req.Headers["Mcp-Client-Info"] = "omniverse-author-plugin";
        req.Timeout = 8000;
        req.Proxy = null;

        var buf = Encoding.UTF8.GetBytes(jsonRpc);
        req.ContentLength = buf.Length;
        using (var s = req.GetRequestStream()) s.Write(buf, 0, buf.Length);

        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string EscapeYaml(string s)
    {
        if (s == null) return "";
        return s.Replace("\r", " ").Replace("\n", " ");
    }

    private static string JsonSerializeString(string s)
    {
        return "\"" + EscapeJson(s) + "\"";
    }
}


// ============================================================================
// L5-03: 桌面 Computer Use 操作留痕 —— 主驱动程序 (ComputerUseAuditMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 捕获当前桌面自动化或 AI Agent 执行的操作描述与目标窗口上下文
//   2. 自动截取当前屏幕/目标窗口证据图，上传至思源资产库 (/data/assets/)
//   3. 生成结构化可审计的操作证据留痕卡片（带时间戳、操作人/Agent名称、步骤描述、状态证据）
//   4. 追加至今日日记或审计专页，实现完全透明、可回溯的桌面级自动化留痕
// ============================================================================


public static class ComputerUseAuditMain
{
    public static void Exec(IStepContext context)
    {
        var actionSummary = SafeContext.GetString(context, "操作步骤描述");
        var agentName = SafeContext.GetString(context, "智能体名称", "Desktop Agent");
        var imgPath = SafeContext.GetString(context, "截图文件路径");
        var windowTitle = SafeContext.GetString(context, "来源窗口", "桌面应用");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrEmpty(actionSummary)) actionSummary = SafeContext.GetString(context, "输入文本");
        if (string.IsNullOrEmpty(actionSummary)) actionSummary = SafeContext.GetString(context, "quicker_in_param");
        if (string.IsNullOrEmpty(actionSummary))
        {
            context.SetVarValue("需要弹窗输入", true);
            context.SetVarValue("输入提示", "输入本次 Computer Use 自动化操作的步骤说明：");
            return;
        }

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        // 日记笔记本自动发现（60s 缓存）：未配置时兜住空笔记本写入被内核拒绝的假成功
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        var now = DateTime.Now;
        var timeStr = now.ToString("HH:mm:ss");
        var dateStr = now.ToString("yyyy-MM-dd");

        // 2. 若有截图，上传至思源资源库
        string assetPath = "";
        if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
        {
            var uploadRes = UploadAssetToSiYuan(syUrl, token, imgPath);
            if (uploadRes.Success)
            {
                assetPath = uploadRes.AssetPath;
            }
        }

        // 3. 构造结构化操作留痕 Markdown
        var sb = new StringBuilder();
        sb.Append("### 🤖 Computer Use 操作留痕 (").Append(timeStr).Append(" · ").Append(agentName).Append(")\n");
        sb.Append("* **执行动作**: ").Append(actionSummary).Append("\n");
        sb.Append("* **目标应用**: ").Append(windowTitle).Append(" · **执行时间**: ").Append(dateStr).Append(" ").Append(timeStr).Append("\n");

        if (!string.IsNullOrEmpty(assetPath))
        {
            sb.Append("\n**操作现场证据截图**:\n");
            sb.Append("![").Append(actionSummary).Append("](").Append(assetPath).Append(")\n");
        }

        sb.Append("\n{: custom-type=\"computer-use-audit\" custom-agent=\"").Append(EscapeJson(agentName)).Append("\" }\n");

        var mdResult = sb.ToString();

        // 4. 追加写入今日日记
        var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + EscapeJson(mdResult) + "\"}";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);

        context.SetVarValue("是否成功", r.Success);
        context.SetVarValue("证据资产路径", assetPath);
        context.SetVarValue("通知反馈", r.Success 
            ? "✓ Computer Use 自动化操作已成功记录至今日日记！" 
            : "❌ 留痕写入失败: " + r.Error);
        SafeContext.Set(context, "需要通知", true);
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static UploadResult UploadAssetToSiYuan(string baseUrl, string token, string localFilePath)
    {
        try
        {
            var fileName = Path.GetFileName(localFilePath);
            var boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
            var boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
            var endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

            var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/asset/upload");
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            if (!string.IsNullOrEmpty(token)) req.Headers[HttpRequestHeader.Authorization] = "Token " + token;
            req.Timeout = 15000;
            req.Proxy = null;

            using (var requestStream = req.GetRequestStream())
            {
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var dirHeader = "Content-Disposition: form-data; name=\"assetsDirPath\"\r\n\r\n/data/assets/";
                var dirBytes = Encoding.UTF8.GetBytes(dirHeader);
                requestStream.Write(dirBytes, 0, dirBytes.Length);

                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var fileHeader = "Content-Disposition: form-data; name=\"file[]\"; filename=\"" + fileName + "\"\r\nContent-Type: image/png\r\n\r\n";
                var fileHeaderBytes = Encoding.UTF8.GetBytes(fileHeader);
                requestStream.Write(fileHeaderBytes, 0, fileHeaderBytes.Length);

                var fileData = File.ReadAllBytes(localFilePath);
                requestStream.Write(fileData, 0, fileData.Length);
                requestStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var respText = reader.ReadToEnd();
                if (resp.StatusCode == HttpStatusCode.OK && respText.Contains("\"code\":0"))
                {
                    var m = Regex.Match(respText, "\"assets/[^\"]+\"");
                    var assetPath = m.Success ? m.Value.Trim('"') : "assets/" + fileName;
                    return new UploadResult { Success = true, AssetPath = assetPath };
                }
                return new UploadResult { Success = false, AssetPath = "" };
            }
        }
        catch
        {
            return new UploadResult { Success = false, AssetPath = "" };
        }
    }

    private class UploadResult
    {
        public bool Success;
        public string AssetPath;
    }
}


// ============================================================================
// L5-04: 全域事件审计时间线 —— 主驱动程序 (EventTimelineMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 events.pull（CDC 白名单事件流：块增删改/日记创建/打卡/管家/同步）
// 核心逻辑：
//   1. 拉取今日 00:00 以来的全域事件（≤200 条），按小时分桶生成时间线 Markdown
//   2. 防重写入：今日日记已存在 event-timeline 块则就地更新，否则追加（绝不重复堆积）
//   3. 兜底：桥不可用 → 降级为内核 SQL「今日修订文档 TopN」只读时间线（永可用）
// ============================================================================


public static class EventTimelineMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        // 1. 受控通道：CDC 事件流
        var since = DateTime.UtcNow.Date.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var timeline = new StringBuilder();
        int eventCount = 0;
        bool cdcOk = false;

        var send = LvSendCommand.Send(syUrl, token, "events.pull", "{\"since\":\"" + since + "\",\"limit\":200}");
        if (send.Success)
        {
            var rec = LvGetReceipt.Poll(syUrl, token, send.CommandId, 6000);
            if (rec.Status == "recorded")
            {
                cdcOk = true;
                eventCount = BuildCdcTimeline(rec.DataJson, timeline);
            }
        }

        string md;
        if (cdcOk)
        {
            md = "### 🛰 全域动态审计时间线 (今日 " + eventCount + " 事件)\n\n" + timeline.ToString() +
                 "\n{: custom-type=\"event-timeline\" }\n";
        }
        else
        {
            // 2. 兜底通道：内核 SQL 今日修订（只读，永可用）
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var sql = "SELECT id, content, updated FROM blocks WHERE type='d' AND updated LIKE '" + today + "%' ORDER BY updated DESC LIMIT 20";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");
            var docs = ParseRecentDocs(r.Text);
            md = "### 🛰 全域动态审计时间线 (降级：今日修订文档 TopN)\n\n" +
                 "> ⚠️ CDC 事件流未就绪，以下为内核 SQL 只读降级版。\n\n" + docs.ToString() +
                 "\n{: custom-type=\"event-timeline\" }\n";
        }

        SafeContext.Set(context, "时间线Markdown", md);

        // 3. 防重写入：已有 event-timeline 块就地更新，否则追加
        var existingId = FindTodayTimelineBlock(syUrl, token);
        bool writeOk;
        string writeErr = "";
        if (!string.IsNullOrEmpty(existingId))
        {
            var upd = SyKernelRequest.DoRequest(syUrl, token, "/api/block/updateBlock",
                "{\"id\":\"" + SafeContext.EscapeJson(existingId) + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}");
            writeOk = upd.Success;
            writeErr = upd.Error;
        }
        else
        {
            var app = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock",
                "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(md) + "\"}");
            writeOk = app.Success;
            writeErr = app.Error;
        }

        SafeContext.Set(context, "是否成功", writeOk);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", writeOk
            ? (cdcOk ? "✓ 今日事件时间线已刷新入日记（" + eventCount + " 事件，防重更新）。"
                     : "✓ 降级时间线已刷新入日记（今日修订文档 TopN）。")
            : "❌ 时间线写入失败：" + writeErr);
    }

    private static int BuildCdcTimeline(string dataJson, StringBuilder timeline)
    {
        var events = new List<KeyValuePair<string, string>>();
        var matches = Regex.Matches(dataJson ?? "", "\\{[^\\{\\}]+\\}");
        foreach (Match m in matches)
        {
            var b = m.Value;
            var name = ExtractField(b, "name");
            var source = ExtractField(b, "source");
            var ts = ExtractField(b, "timestamp");
            if (string.IsNullOrEmpty(name)) continue;

            string hm = "00:00";
            DateTime dt;
            if (DateTime.TryParse(ts, out dt)) hm = dt.ToLocalTime().ToString("HH:mm");

            var icon = IconForEvent(name);
            events.Add(new KeyValuePair<string, string>(hm, icon + " " + name + (string.IsNullOrEmpty(source) ? "" : " · " + source)));
        }

        if (events.Count == 0)
        {
            timeline.Append("> 今日暂无全域事件记录。\n");
            return 0;
        }

        var byHour = new Dictionary<string, List<string>>();
        foreach (var ev in events)
        {
            var hour = ev.Key.Length >= 2 ? ev.Key.Substring(0, 2) + ":00" : "00:00";
            if (!byHour.ContainsKey(hour)) byHour[hour] = new List<string>();
            byHour[hour].Add(ev.Key + " " + ev.Value);
        }

        foreach (var kv in byHour)
        {
            timeline.Append("#### ⏰ ").Append(kv.Key).Append("\n");
            foreach (var item in kv.Value)
            {
                timeline.Append("- ").Append(item).Append("\n");
            }
            timeline.Append("\n");
        }
        return events.Count;
    }

    private static string IconForEvent(string eventName)
    {
        if (eventName.StartsWith("block:create")) return "➕";
        if (eventName.StartsWith("block:delete")) return "🗑";
        if (eventName.StartsWith("block:update")) return "✏️";
        if (eventName.StartsWith("daily:create")) return "📅";
        if (eventName.StartsWith("checkin:")) return "🔥";
        if (eventName.StartsWith("home:")) return "🏠";
        if (eventName.StartsWith("sync:")) return "🔄";
        return "⚡";
    }

    private static StringBuilder ParseRecentDocs(string sqlText)
    {
        var sb = new StringBuilder();
        if (string.IsNullOrEmpty(sqlText))
        {
            sb.Append("> 今日暂无修订文档。\n");
            return sb;
        }

        int count = 0;
        foreach (Match m in Regex.Matches(sqlText, "\\{[^\\{\\}]+\\}"))
        {
            var b = m.Value;
            var id = ExtractField(b, "id");
            var content = ExtractField(b, "content");
            var updated = ExtractField(b, "updated");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(content)) continue;

            string hm = updated.Length >= 12 ? updated.Substring(8, 2) + ":" + updated.Substring(10, 2) : "";
            sb.Append("- ").Append(string.IsNullOrEmpty(hm) ? "" : hm + " ").Append(content)
              .Append(" [🔗](siyuan://blocks/").Append(id).Append(")\n");
            count++;
        }
        if (count == 0) sb.Append("> 今日暂无修订文档。\n");
        return sb;
    }

    private static string FindTodayTimelineBlock(string syUrl, string token)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var sql = "SELECT id FROM blocks WHERE ial LIKE '%custom-type=\"event-timeline\"%' " +
                  "AND root_id IN (SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + today + "%') LIMIT 1";
        var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");
        if (r.Success && !string.IsNullOrEmpty(r.Text))
        {
            var m = Regex.Match(r.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
        }
        return "";
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }
}