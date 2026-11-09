using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using XiaoLv.Desktop.Engine.Modules;

namespace XiaoLv.Desktop.Core
{
    public static class FileDispatcher
    {
        public static bool DispatchFileToDocument(string filePath, string targetDocId, string targetDocTitle, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(targetDocId))
            {
                return DispatchFileToSiYuan(filePath, notify);
            }

            if (string.IsNullOrEmpty(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath)))
            {
                if (notify != null) notify("思源外骨骼 · 资产直投", "⚠️ 目标文件不存在");
                return false;
            }

            string syUrl = "";
            string token = "";
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            if (string.IsNullOrEmpty(syUrl))
            {
                bool queued = OfflineQueueManager.EnqueueFile(filePath, targetDocId, targetDocTitle);
                if (notify != null) notify("思源外骨骼 · 离岸队列", queued
                    ? "✓ 思源未启动，文件已加入直投《" + (targetDocTitle ?? "指定文档") + "》的加密队列。"
                    : "❌ 直投文件无法加入离岸队列，请检查磁盘空间和权限。");
                return queued;
            }

            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                string ext = fi.Extension.ToLowerInvariant();
                string safeName = fi.Name.Replace("[", "【").Replace("]", "】");
                string timeStr = DateTime.Now.ToString("HH:mm");

                // 结构化文档透视优先
                bool isStructured = false;
                string structuredMd = BuildStructuredFileMarkdown(filePath, out isStructured);

                string assetPath, err;
                UploadAsset(syUrl, token, filePath, out assetPath, out err);

                string appendData;
                if (isStructured && !string.IsNullOrEmpty(structuredMd))
                {
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        structuredMd += string.Format("\n> 📦 **思源资产库备份**: [{0}]({1}) (更新于 {2})\n", safeName, assetPath, timeStr);
                    }
                    appendData = structuredMd.TrimEnd();
                }
                else
                {
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        if (notify != null) notify("思源外骨骼", "❌ 资产上传失败: " + err);
                        return false;
                    }
                    bool isImg = (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".svg" || ext == ".bmp");
                    string md = isImg ? ("![" + safeName + "](" + assetPath + ")") : ("📎 [" + safeName + " (" + FormatFileSize(fi.Length) + ")](" + assetPath + ")");
                    appendData = "* " + md + "\n  * ⏱️ 上传于 " + timeStr + " · 外部直投";
                }

                var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", JsonHelper.ToJson(new
                {
                    parentID = targetDocId,
                    dataType = "markdown",
                    data = appendData
                }));

                if (r.Success)
                {
                    SoundPlayerHelper.PlayFeedback();
                    if (notify != null) notify("思源外骨骼 · 资产直投", "✓ 已将【" + fi.Name + "】嵌入至《" + (targetDocTitle ?? "指定文档") + "》！");
                    return true;
                }
                else
                {
                    if (notify != null) notify("思源外骨骼", "⚠️ 资产已上传但追加目标文档失败，队列会保留；重试可能重复上传资产: " + r.Error);
                    return false;
                }
            }
            if (notify != null) notify("思源外骨骼 · 资产直投", "❌ 目前仅支持将单个文件直投至指定文档。");
            return false;
        }

        public static bool OpenWithPreferredApp(string filePath, int targetPage = 0)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            // 智能解析文件名中自带的页码锚点 (如 paper.pdf#page=12 或 paper.pdf#12)
            string cleanPath = filePath;
            int page = targetPage;
            var pageMatch = Regex.Match(filePath, @"#(?:page=)?(\d+)$", RegexOptions.IgnoreCase);
            if (pageMatch.Success)
            {
                page = int.Parse(pageMatch.Groups[1].Value);
                cleanPath = filePath.Substring(0, pageMatch.Index);
            }

            if (!File.Exists(cleanPath)) return false;

            try
            {
                string ext = Path.GetExtension(cleanPath).ToLowerInvariant();

                // 1. 若为 PDF 文献，优先调用轻量学术级 SumatraPDF 并携带页码穿透跳转
                if (ext == ".pdf")
                {
                    var sumatra = ToolDetector.DetectSumatraPdf();
                    if (sumatra != null && !string.IsNullOrEmpty(sumatra.ExePath))
                    {
                        string args = page > 0 
                            ? string.Format("-page {0} \"{1}\"", page, cleanPath) 
                            : string.Format("\"{0}\"", cleanPath);

                        Process.Start(new ProcessStartInfo(sumatra.ExePath, args) { UseShellExecute = true });
                        return true;
                    }
                    else if (page > 0)
                    {
                        // 若未装 SumatraPDF 但附带页码，优先通过 Edge 内置 PDF 引擎携带 #page= 深度跳转
                        try
                        {
                            string edgeUri = "file:///" + cleanPath.Replace('\\', '/') + "#page=" + page;
                            Process.Start("msedge.exe", "\"" + edgeUri + "\"");
                            return true;
                        }
                        catch { }
                    }
                }

                // 2. 默认调用系统原生关联程序打开
                Process.Start(new ProcessStartInfo(cleanPath) { UseShellExecute = true });
                return true;
            }
            catch { }
            return false;
        }

        public static bool DispatchFileToSiYuan(string filePath, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath)))
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "⚠️ 目标文件或文件夹不存在");
                return false;
            }

            if (Directory.Exists(filePath))
            {
                try
                {
                    if (Directory.GetFiles(filePath, "*.*", SearchOption.TopDirectoryOnly).Length == 0)
                    {
                        if (notify != null) notify("思源外骨骼 · 文件速递", "ℹ️ 所选目录为空，无需加入离岸队列。");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    if (notify != null) notify("思源外骨骼 · 文件速递", "❌ 无法读取所选目录：" + ex.Message);
                    return false;
                }
            }

            string syUrl = "";
            string token = "";
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);

            if (string.IsNullOrEmpty(syUrl))
            {
                bool queued = OfflineQueueManager.EnqueueFile(filePath);
                if (notify != null)
                {
                    notify("思源外骨骼 · 离岸文件速递", queued
                        ? "✓ 思源未启动，已将【" + Path.GetFileName(filePath) + "】加入离岸队列。"
                        : "❌ 无法将【" + Path.GetFileName(filePath) + "】写入离岸队列，请检查磁盘空间或权限。");
                }
                return queued;
            }

            try
            {
                if (File.Exists(filePath)) return ProcessSingleFile(syUrl, token, filePath, notify);
                if (Directory.Exists(filePath)) return ProcessDirectory(syUrl, token, filePath, notify);
            }
            catch (Exception ex)
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "❌ 文件处理失败，记录仍保留：" + ex.Message);
            }
            return false;
        }

        private static bool ProcessSingleFile(string syUrl, string token, string filePath, Action<string, string> notify)
        {
            var fi = new FileInfo(filePath);
            string fileName = fi.Name;
            string ext = fi.Extension.ToLowerInvariant();
            string sizeStr = FormatFileSize(fi.Length);
            string safeName = fileName.Replace("[", "【").Replace("]", "】");
            string timeStr = DateTime.Now.ToString("HH:mm");

            // 1. 若为本地 Office / PDF / Markdown / CSV / 源码结构化文档，执行高精度零依赖流式透视
            bool isStructured = false;
            string structuredMd = BuildStructuredFileMarkdown(filePath, out isStructured);

            // 2. 尝试上传至思源资产库作为备份
            string assetPath = null;
            string err = null;
            bool assetUploaded = UploadAsset(syUrl, token, filePath, out assetPath, out err);
            if ((!assetUploaded || string.IsNullOrEmpty(assetPath)) && (!isStructured || string.IsNullOrEmpty(structuredMd)))
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "❌ 文件上传至思源资产库失败: " + err);
                return false;
            }

            // 3. 构建写入 Markdown
            string appendData;
            string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);

            if (isStructured && !string.IsNullOrEmpty(structuredMd))
            {
                if (assetUploaded && !string.IsNullOrEmpty(assetPath))
                {
                    structuredMd += string.Format("\n> 📦 **思源资产库备份**: [{0}]({1}) (更新于 {2})\n", safeName, assetPath, timeStr);
                }
                appendData = structuredMd.TrimEnd();
            }
            else
            {
                bool isImg = (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".svg" || ext == ".bmp");
                bool isMedia = (ext == ".mp3" || ext == ".wav" || ext == ".mp4" || ext == ".mkv" || ext == ".mov");

                string md;
                if (isImg)
                {
                    md = "![" + safeName + "](" + assetPath + ")";
                }
                else if (isMedia)
                {
                    md = "🎬 [" + safeName + " (" + sizeStr + ")](" + assetPath + ")";
                }
                else
                {
                    md = "📄 [" + safeName + " (" + sizeStr + ")](" + assetPath + ")";
                }

                appendData = "- " + timeStr + " 📦 " + md;
            }

            string postJson = "{\"notebook\":\"" + diaryNb + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(appendData) + "\"}";

            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postJson);
            if (r.Success)
            {
                SoundPlayerHelper.PlayFeedback();
                if (notify != null)
                {
                    if (isStructured)
                    {
                        notify("思源外骨骼 · 结构化透视入库", "✓ 已将《" + fileName + "》结构大纲存入今日日记并激活外部监听！");
                    }
                    else
                    {
                        notify("思源外骨骼 · 文件速递", "✓ 已将【" + fileName + "】成功归档入今日日记！");
                    }
                }
                return true;
            }
            else
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "⚠️ 资源已上传但追加日记失败，队列记录会保留；重试可能再次上传资源: " + r.Error);
                return false;
            }
        }

        private static bool ProcessDirectory(string syUrl, string token, string dirPath, Action<string, string> notify)
        {
            var di = new DirectoryInfo(dirPath);
            var files = di.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "ℹ️ 所选目录为空，无待归档文件");
                return true;
            }

            int succ = 0;
            int failed = 0;
            string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);
            var sb = new StringBuilder();
            sb.AppendLine("📁 **目录归档: " + di.Name + "** (" + files.Length + " 个文件)");

            foreach (var f in files)
            {
                string assetPath;
                string err;
                if (UploadAsset(syUrl, token, f.FullName, out assetPath, out err) && !string.IsNullOrEmpty(assetPath))
                {
                    succ++;
                    sb.AppendLine("  - 📄 [" + f.Name + " (" + FormatFileSize(f.Length) + ")](" + assetPath + ")");
                }
                else
                {
                    failed++;
                }
            }

            if (succ > 0)
            {
                string postJson = "{\"notebook\":\"" + diaryNb + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(sb.ToString().TrimEnd()) + "\"}";
                var appendResult = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postJson);
                if (!appendResult.Success)
                {
                    if (notify != null) notify("思源外骨骼 · 文件速递", "⚠️ 已上传 " + succ + " 个文件，但日记写入失败；队列会保留，重试可能重复上传资源。");
                    return false;
                }
                if (failed > 0)
                {
                    if (notify != null) notify("思源外骨骼 · 文件速递", "⚠️ 已归档 " + succ + " 个文件，另有 " + failed + " 个失败；目录记录会保留，重试可能重复上传已成功文件。");
                    return false;
                }
                if (notify != null) notify("思源外骨骼 · 文件速递", "✓ 已成功批量归档 " + succ + " 个文件入今日日记！");
                return true;
            }
            else
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "❌ 文件夹批量上传失败");
                return false;
            }
        }

        public static bool UploadAsset(string syUrl, string token, string filePath, out string assetPath, out string error)
        {
            assetPath = "";
            error = "";

            try
            {
                var boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
                var boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
                var endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

                var req = (HttpWebRequest)WebRequest.Create(syUrl.TrimEnd('/') + "/api/asset/upload");
                req.Method = "POST";
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                if (!string.IsNullOrEmpty(token))
                {
                    req.Headers[HttpRequestHeader.Authorization] = "Token " + token;
                }
                req.Timeout = 15000;
                req.Proxy = null;

                var fileName = Path.GetFileName(filePath);

                using (var reqStream = req.GetRequestStream())
                {
                    // 参数 1: assetsDirPath 字段
                    reqStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                    var dirHeader = "Content-Disposition: form-data; name=\"assetsDirPath\"\r\n\r\n/data/assets/";
                    var dirBytes = Encoding.UTF8.GetBytes(dirHeader);
                    reqStream.Write(dirBytes, 0, dirBytes.Length);

                    // 参数 2: file[] 字段 (思源标准资源上传表单名)
                    reqStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                    var header = "Content-Disposition: form-data; name=\"file[]\"; filename=\"" + fileName + "\"\r\nContent-Type: application/octet-stream\r\n\r\n";
                    var headerBytes = Encoding.UTF8.GetBytes(header);
                    reqStream.Write(headerBytes, 0, headerBytes.Length);

                    using (var fs = File.OpenRead(filePath))
                    {
                        fs.CopyTo(reqStream);
                    }

                    reqStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var txt = reader.ReadToEnd();
                    var dict = JsonHelper.ParseDict(txt);
                    if (resp.StatusCode == HttpStatusCode.OK && JsonHelper.GetInt(dict, "code") == 0)
                    {
                        var data = JsonHelper.GetDict(dict, "data");
                        var succMap = JsonHelper.GetDict(data, "succMap");
                        if (succMap != null)
                        {
                            assetPath = JsonHelper.GetString(succMap, fileName);
                            if (!string.IsNullOrEmpty(assetPath)) return true;
                        }

                        var m2 = Regex.Match(txt, "\"assets/[^\"]+\"");
                        if (m2.Success)
                        {
                            assetPath = m2.Value.Trim('"');
                            return true;
                        }
                    }
                    error = txt;
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 自动下载微信、知乎、B站等防盗链网页图片并上传至思源资产库 (/data/assets/)
        /// 防止外链图床过期或 403 访问被拒导致笔记图片失效
        /// </summary>
        public static bool CacheAndUploadRemoteImage(string syUrl, string token, string imageUrl, out string assetPath, out string error)
        {
            assetPath = "";
            error = "";
            if (string.IsNullOrEmpty(imageUrl) || (!imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                error = "无效的图片 URL";
                return false;
            }

            string tmpFile = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(imageUrl);
                req.Method = "GET";
                req.Timeout = 5000;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                // 伪造 Referer 绕过各大平台反爬与防盗链限制 (微信/知乎/B站/微博/小红书/CSDN/掘金)
                if (imageUrl.Contains("mmbiz.qpic.cn") || imageUrl.Contains("weixin.qq.com"))
                {
                    req.Referer = "https://mp.weixin.qq.com/";
                }
                else if (imageUrl.Contains("zhimg.com") || imageUrl.Contains("zhihu.com"))
                {
                    req.Referer = "https://www.zhihu.com/";
                }
                else if (imageUrl.Contains("biliimg.com") || imageUrl.Contains("hdslb.com") || imageUrl.Contains("bilibili.com"))
                {
                    req.Referer = "https://www.bilibili.com/";
                }
                else if (imageUrl.Contains("sinaimg.cn") || imageUrl.Contains("weibo.com"))
                {
                    req.Referer = "https://weibo.com/";
                }
                else if (imageUrl.Contains("csdnimg.cn") || imageUrl.Contains("csdn.net"))
                {
                    req.Referer = "https://blog.csdn.net/";
                }
                else if (imageUrl.Contains("xhscdn.com") || imageUrl.Contains("xiaohongshu.com"))
                {
                    req.Referer = "https://www.xiaohongshu.com/";
                }
                else if (imageUrl.Contains("juejin.cn") || imageUrl.Contains("byteimg.com"))
                {
                    req.Referer = "https://juejin.cn/";
                }

                string ext = ".png";
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK)
                    {
                        error = "下载失败: HTTP " + (int)resp.StatusCode;
                        return false;
                    }

                    string ctype = resp.ContentType != null ? resp.ContentType.ToLowerInvariant() : "";
                    if (ctype.Contains("jpeg") || ctype.Contains("jpg")) ext = ".jpg";
                    else if (ctype.Contains("webp")) ext = ".webp";
                    else if (ctype.Contains("gif")) ext = ".gif";
                    else if (ctype.Contains("png")) ext = ".png";
                    else
                    {
                        var urlLow = imageUrl.ToLowerInvariant();
                        if (urlLow.Contains(".jpg") || urlLow.Contains(".jpeg")) ext = ".jpg";
                        else if (urlLow.Contains(".webp")) ext = ".webp";
                        else if (urlLow.Contains(".gif")) ext = ".gif";
                    }

                    var tempDir = Path.Combine(Path.GetTempPath(), "XiaoLvDesktop");
                    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                    tmpFile = Path.Combine(tempDir, "clip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ext);

                    using (var fs = File.Create(tmpFile))
                    using (var stream = resp.GetResponseStream())
                    {
                        stream.CopyTo(fs);
                    }
                }

                if (File.Exists(tmpFile) && new FileInfo(tmpFile).Length > 0)
                {
                    return UploadAsset(syUrl, token, tmpFile, out assetPath, out error);
                }
                error = "下载文件为空";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmpFile) && File.Exists(tmpFile))
                {
                    try { File.Delete(tmpFile); } catch { }
                }
            }
        }

        public static string BuildStructuredFileMarkdown(string filePath, out bool isStructured)
        {
            isStructured = false;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return "";

            try
            {
                var fi = new FileInfo(filePath);
                string ext = fi.Extension.ToLowerInvariant();

                if (ext == ".docx")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    var doc = OfficeOpenXmlParser.InspectFile(filePath);
                    if (doc != null && doc.Headings != null && doc.Headings.Count > 0)
                    {
                        isStructured = true;
                        return OfficeDeepLinkMain.BuildWordOutlineMarkdown(doc);
                    }
                }
                else if (ext == ".xlsx")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    var doc = OfficeOpenXmlParser.InspectFile(filePath);
                    if (doc != null && doc.Sheets != null && doc.Sheets.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendFormat("### 📊 《{0}》 表格结构透视 (共 {1} 个工作表)\n", doc.FileName, doc.Sheets.Count);
                        string docOpenUrl = "siyuan-exoskeleton://open-office?path=" + Uri.EscapeDataString(doc.FilePath);
                        sb.AppendFormat("> 📍 **本地底稿**: `{0}` · [⚡ 本地秒开]({1})\n\n", doc.FilePath, docOpenUrl);
                        sb.AppendLine("#### 📑 工作表清单与单元格速览");
                        foreach (var sh in doc.Sheets)
                        {
                            string shUrl = string.Format("siyuan-exoskeleton://open-office?path={0}&sheet={1}", Uri.EscapeDataString(doc.FilePath), Uri.EscapeDataString(sh.SheetName));
                            sb.AppendFormat("- 📄 **{0}** ({1} 行) · [定位 ↗]({2})\n", sh.SheetName, sh.RowCount, shUrl);
                        }
                        sb.AppendLine();
                        sb.AppendFormat("{{: custom-type=\"office-excel-outline\" custom-file-path=\"{0}\" }}\n", doc.FilePath.Replace("\"", "\\\""));
                        isStructured = true;
                        return sb.ToString();
                    }
                }
                else if (ext == ".pptx")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    var doc = OfficeOpenXmlParser.InspectFile(filePath);
                    if (doc != null && doc.Slides != null && doc.Slides.Count > 0)
                    {
                        isStructured = true;
                        return OfficeDeepLinkMain.BuildPptFlashcardsMarkdown(doc);
                    }
                }
                else if (ext == ".pdf")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    var pdf = PdfLightParser.InspectPdf(filePath);
                    if (pdf != null)
                    {
                        isStructured = true;
                        return PdfLightParser.BuildPdfReadingNotesMarkdown(pdf);
                    }
                }
                else if (ext == ".epub")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    var epub = EpubLightParser.InspectEpub(filePath);
                    if (epub != null)
                    {
                        isStructured = true;
                        return EpubLightParser.BuildEpubReadingNotesMarkdown(epub);
                    }
                }
                else if (ext == ".xmind")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    var xmind = XMindLightParser.InspectXMind(filePath);
                    if (xmind != null)
                    {
                        isStructured = true;
                        return XMindLightParser.BuildXMindOutlineMarkdown(xmind);
                    }
                }
                else if (ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".mp4" || ext == ".mkv" || ext == ".mov")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    var media = MediaLightParser.InspectMedia(filePath);
                    if (media != null)
                    {
                        isStructured = true;
                        return MediaLightParser.BuildMediaNotesMarkdown(media);
                    }
                }
                else if (ext == ".md" || ext == ".markdown")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    string rawMd = File.ReadAllText(filePath, Encoding.UTF8);
                    string cleanMd = TextCleaner.HarmonizeMarkdownSyntax(rawMd);
                    var sb = new StringBuilder();
                    sb.AppendFormat("### 📝 《{0}》 导入文档 ({1})\n", Path.GetFileNameWithoutExtension(fi.Name), FormatFileSize(fi.Length));
                    sb.AppendFormat("> 📍 **本地底稿**: `{0}` · [⚡ 原位打开](siyuan-exoskeleton://open-file?path={1})\n\n",
                        filePath, Uri.EscapeDataString(filePath));
                    sb.AppendLine(cleanMd);
                    sb.AppendFormat("\n{{: custom-type=\"imported-markdown\" custom-file-path=\"{0}\" }}\n", filePath.Replace("\"", "\\\""));
                    isStructured = true;
                    return sb.ToString();
                }
                else if (ext == ".csv")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    string rawCsv = File.ReadAllText(filePath, Encoding.UTF8);
                    var tbl = TableToAvConverter.ParseTable(rawCsv);
                    string prettyTable = TableToAvConverter.FormatBeautifulMarkdownTable(tbl);
                    var sb = new StringBuilder();
                    sb.AppendFormat("### 📊 《{0}》 数据表格 (共 {1} 行 · {2} 列)\n", fi.Name, tbl.RowCount, tbl.ColCount);
                    sb.AppendFormat("> 📍 **本地底稿**: `{0}` · [⚡ 原位打开](siyuan-exoskeleton://open-file?path={1})\n\n",
                        filePath, Uri.EscapeDataString(filePath));
                    sb.AppendLine(prettyTable);
                    sb.AppendFormat("{{: custom-type=\"imported-csv-table\" custom-file-path=\"{0}\" }}\n", filePath.Replace("\"", "\\\""));
                    isStructured = true;
                    return sb.ToString();
                }
                else if (ext == ".py" || ext == ".cs" || ext == ".js" || ext == ".ts" || ext == ".sql" || ext == ".sh" ||
                         ext == ".json" || ext == ".html" || ext == ".css" || ext == ".rs" || ext == ".go" || ext == ".cpp" ||
                         ext == ".c" || ext == ".java" || ext == ".php" || ext == ".yaml" || ext == ".yml")
                {
                    OfficeFileWatcher.WatchFile(filePath);
                    string codeText = File.ReadAllText(filePath, Encoding.UTF8);
                    string lang = ext.TrimStart('.').ToLowerInvariant();
                    if (lang == "py") lang = "python";
                    else if (lang == "cs") lang = "csharp";
                    else if (lang == "js") lang = "javascript";
                    else if (lang == "ts") lang = "typescript";
                    else if (lang == "yml") lang = "yaml";
                    string safeCode = codeText.Replace("\r\n", "\n").Trim();
                    int lineCount = safeCode.Split('\n').Length;
                    var sb = new StringBuilder();
                    sb.AppendFormat("### ⌨️ 《{0}》 源码档案 ({1} 行 · {2})\n", fi.Name, lineCount, FormatFileSize(fi.Length));
                    sb.AppendFormat("> 📍 **本地源文件**: `{0}` · [⚡ 外部IDE秒开](siyuan-exoskeleton://open-file?path={1})\n\n",
                        filePath, Uri.EscapeDataString(filePath));
                    sb.AppendFormat("```{0}\n{1}\n```\n\n", lang, safeCode);
                    sb.AppendFormat("{{: custom-type=\"imported-source-code\" custom-file-path=\"{0}\" custom-language=\"{1}\" }}\n",
                        filePath.Replace("\"", "\\\""), lang);
                    isStructured = true;
                    return sb.ToString();
                }
            }
            catch { }

            return "";
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.1") + " MB";
        }
    }
}
