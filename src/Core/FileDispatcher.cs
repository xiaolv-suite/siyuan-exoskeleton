using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public static class FileDispatcher
    {
        public static void DispatchFileToDocument(string filePath, string targetDocId, string targetDocTitle, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(targetDocId))
            {
                DispatchFileToSiYuan(filePath, notify);
                return;
            }

            if (string.IsNullOrEmpty(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath)))
            {
                if (notify != null) notify("思源外骨骼 · 资产直投", "⚠️ 目标文件不存在");
                return;
            }

            string syUrl = "";
            string token = "";
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            if (string.IsNullOrEmpty(syUrl))
            {
                OfflineQueueManager.EnqueueFile(filePath);
                if (notify != null) notify("思源外骨骼 · 离岸队列", "✓ 思源未启动，已将文件存入离岸队列");
                return;
            }

            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                string assetPath, err;
                if (!UploadAsset(syUrl, token, filePath, out assetPath, out err))
                {
                    if (notify != null) notify("思源外骨骼", "❌ 资产上传失败: " + err);
                    return;
                }

                string ext = fi.Extension.ToLowerInvariant();
                bool isImg = (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".svg" || ext == ".bmp");
                string safeName = fi.Name.Replace("[", "【").Replace("]", "】");
                string md = isImg ? ("![" + safeName + "](" + assetPath + ")") : ("📎 [" + safeName + " (" + FormatFileSize(fi.Length) + ")](" + assetPath + ")");

                string timeStr = DateTime.Now.ToString("HH:mm");
                string appendData = "* " + md + "\n  * ⏱️ 上传于 " + timeStr + " · 外部直投";
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
                }
                else
                {
                    if (notify != null) notify("思源外骨骼", "⚠️ 资产已入库但追加文档失败: " + r.Error);
                }
            }
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

        public static void DispatchFileToSiYuan(string filePath, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath)))
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "⚠️ 目标文件或文件夹不存在");
                return;
            }

            string syUrl = "";
            string token = "";
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);

            if (string.IsNullOrEmpty(syUrl))
            {
                OfflineQueueManager.EnqueueFile(filePath);
                if (notify != null) notify("思源外骨骼 · 离岸文件速递", "✓ 思源未启动，已将【" + Path.GetFileName(filePath) + "】安全加入离岸队列，思源启动后自动补录入库！");
                return;
            }

            if (File.Exists(filePath))
            {
                ProcessSingleFile(syUrl, token, filePath, notify);
            }
            else if (Directory.Exists(filePath))
            {
                ProcessDirectory(syUrl, token, filePath, notify);
            }
        }

        private static void ProcessSingleFile(string syUrl, string token, string filePath, Action<string, string> notify)
        {
            var fi = new FileInfo(filePath);
            string fileName = fi.Name;
            string ext = fi.Extension.ToLowerInvariant();
            string sizeStr = FormatFileSize(fi.Length);

            string assetPath;
            string err;
            bool ok = UploadAsset(syUrl, token, filePath, out assetPath, out err);

            if (!ok || string.IsNullOrEmpty(assetPath))
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "❌ 文件上传至思源资产库失败: " + err);
                return;
            }

            // 构造 Markdown 嵌入代码
            string safeName = fileName.Replace("[", "【").Replace("]", "】");
            string md;
            bool isImg = (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".svg" || ext == ".bmp");
            bool isMedia = (ext == ".mp3" || ext == ".wav" || ext == ".mp4" || ext == ".mkv" || ext == ".mov");

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

            // 写入今日日记
            string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);
            string appendData = "- " + DateTime.Now.ToString("HH:mm") + " 📦 " + md;
            string postJson = "{\"notebook\":\"" + diaryNb + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(appendData) + "\"}";

            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postJson);
            if (r.Success)
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "✓ 已将【" + fileName + "】成功归档入今日日记！");
            }
            else
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "⚠️ 资源已入库但追加日记失败: " + r.Error);
            }
        }

        private static void ProcessDirectory(string syUrl, string token, string dirPath, Action<string, string> notify)
        {
            var di = new DirectoryInfo(dirPath);
            var files = di.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "ℹ️ 所选目录为空，无待归档文件");
                return;
            }

            int succ = 0;
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
            }

            if (succ > 0)
            {
                string postJson = "{\"notebook\":\"" + diaryNb + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(sb.ToString().TrimEnd()) + "\"}";
                SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postJson);
                if (notify != null) notify("思源外骨骼 · 文件速递", "✓ 已成功批量归档 " + succ + " 个文件入今日日记！");
            }
            else
            {
                if (notify != null) notify("思源外骨骼 · 文件速递", "❌ 文件夹批量上传失败");
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
                // 伪造 Referer 绕过微信/知乎反爬防盗链限制
                if (imageUrl.Contains("mmbiz.qpic.cn") || imageUrl.Contains("weixin.qq.com"))
                {
                    req.Referer = "https://mp.weixin.qq.com/";
                }
                else if (imageUrl.Contains("zhimg.com") || imageUrl.Contains("zhihu.com"))
                {
                    req.Referer = "https://www.zhihu.com/";
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

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.1") + " MB";
        }
    }
}
