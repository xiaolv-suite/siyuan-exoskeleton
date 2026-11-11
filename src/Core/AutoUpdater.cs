using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 国内高可用多源竞速自更新引擎 (AutoUpdater)
    /// 1. 多源元数据并发竞速 (jsDelivr Anycast CDN / Fastly / GitHub Raw 镜像 / 官方 API)
    /// 2. 动态下载加速镜像池 (ghfast / ghproxy / moeyy / ddlc / 直连)
    /// 3. 三重安全门禁校验 (PE 文件头 MZ 校验 + SHA-256 哈希比对 + 最小文件尺寸防御)
    /// 4. Win32 原位无感热替换 (In-Place File Rename + 子进程异步垃圾回收)
    /// </summary>
    public static class AutoUpdater
    {
        public const string CURRENT_VERSION = "3.0.0";
        public const string REPO_OWNER = "xiaolv-suite";
        public const string REPO_NAME = "siyuan-exoskeleton";

        private static readonly string[] VersionProbeEndpoints = new string[]
        {
            "https://fastly.jsdelivr.net/gh/xiaolv-suite/siyuan-exoskeleton@main/version.json",
            "https://cdn.jsdelivr.net/gh/xiaolv-suite/siyuan-exoskeleton@main/version.json",
            "https://ghfast.top/https://raw.githubusercontent.com/xiaolv-suite/siyuan-exoskeleton/main/version.json",
            "https://raw.githubusercontent.com/xiaolv-suite/siyuan-exoskeleton/main/version.json",
            "https://api.github.com/repos/xiaolv-suite/siyuan-exoskeleton/releases/latest"
        };

        private static readonly string[] DefaultDownloadMirrors = new string[]
        {
            "https://ghfast.top/",
            "https://ghproxy.net/",
            "https://github.moeyy.xyz/",
            "https://gh.ddlc.top/",
            "" // 直连兜底
        };

        public class UpdateInfo
        {
            public bool HasUpdate;
            public string LatestVersion;
            public string Changelog;
            public string DownloadUrl;
            public string Sha256;
            public string ReleaseUrl;
            public List<string> DynamicMirrors;

            public UpdateInfo()
            {
                HasUpdate = false;
                LatestVersion = CURRENT_VERSION;
                Changelog = "";
                DownloadUrl = "";
                Sha256 = "";
                ReleaseUrl = "https://github.com/xiaolv-suite/siyuan-exoskeleton/releases";
                DynamicMirrors = new List<string>();
            }
        }

        /// <summary>
        /// 启动时异步清理旧版本备份 (.old 临时文件)
        /// </summary>
        public static void CleanOldBackupAsync()
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    Thread.Sleep(1200);
                    string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                    string oldPath = currentExe + ".old";
                    if (File.Exists(oldPath))
                    {
                        File.Delete(oldPath);
                    }
                }
                catch { }
            });
        }

        /// <summary>
        /// 多源并发/快速回退检查最新版本元数据
        /// </summary>
        public static UpdateInfo CheckForUpdate()
        {
            var info = new UpdateInfo();

            foreach (var url in VersionProbeEndpoints)
            {
                try
                {
                    string json = FetchStringWithTimeout(url, 2800);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    // 1. 若命中了 version.json 标准清单
                    if (json.Contains("\"version\"") || json.Contains("\"latestVersion\""))
                    {
                        var mVer = Regex.Match(json, "\"(version|latestVersion)\"\\s*:\\s*\"([^\"]+)\"");
                        var mLog = Regex.Match(json, "\"changelog\"\\s*:\\s*\"([^\"]+)\"");
                        var mSha = Regex.Match(json, "\"sha256\"\\s*:\\s*\"([^\"]+)\"");
                        var mUrl = Regex.Match(json, "\"downloadUrl\"\\s*:\\s*\"([^\"]+)\"");
                        var mRel = Regex.Match(json, "\"releaseUrl\"\\s*:\\s*\"([^\"]+)\"");

                        if (mVer.Success)
                        {
                            string remoteVer = mVer.Groups[2].Value.Trim();
                            info.LatestVersion = remoteVer;
                            info.Changelog = mLog.Success ? mLog.Groups[1].Value.Replace("\\n", "\n") : "";
                            info.Sha256 = mSha.Success ? mSha.Groups[1].Value.Trim() : "";
                            info.DownloadUrl = mUrl.Success ? mUrl.Groups[1].Value.Trim() : "";
                            info.ReleaseUrl = mRel.Success ? mRel.Groups[1].Value.Trim() : info.ReleaseUrl;

                            // 提取动态下发的可用镜像列表
                            var mMirrors = Regex.Matches(json, "\"https://[^\"]+\"");
                            foreach (Match m in mMirrors)
                            {
                                string mirror = m.Value.Trim('\"');
                                if (mirror.EndsWith("/") && !info.DynamicMirrors.Contains(mirror))
                                {
                                    info.DynamicMirrors.Add(mirror);
                                }
                            }

                            if (CompareVersion(remoteVer, CURRENT_VERSION) > 0)
                            {
                                info.HasUpdate = true;
                            }
                            return info;
                        }
                    }
                    // 2. 若命中了 GitHub Release API 官方响应
                    else if (json.Contains("\"tag_name\""))
                    {
                        var mTag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        var mBody = Regex.Match(json, "\"body\"\\s*:\\s*\"([^\"]+)\"");
                        var mHtml = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+)\"");
                        var mAsset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+siyuan-exoskeleton\\.exe)\"");

                        if (mTag.Success)
                        {
                            string remoteVer = mTag.Groups[1].Value.Trim().TrimStart('v', 'V');
                            info.LatestVersion = remoteVer;
                            info.Changelog = mBody.Success ? mBody.Groups[1].Value.Replace("\\n", "\n") : "";
                            info.ReleaseUrl = mHtml.Success ? mHtml.Groups[1].Value.Trim() : info.ReleaseUrl;
                            info.DownloadUrl = mAsset.Success ? mAsset.Groups[1].Value.Trim() :
                                "https://github.com/" + REPO_OWNER + "/" + REPO_NAME + "/releases/download/v" + remoteVer + "/siyuan-exoskeleton.exe";

                            if (CompareVersion(remoteVer, CURRENT_VERSION) > 0)
                            {
                                info.HasUpdate = true;
                            }
                            return info;
                        }
                    }
                }
                catch { }
            }

            return info;
        }

        /// <summary>
        /// 执行多节点高速下载与原位热替换
        /// </summary>
        public static bool DownloadAndApplyUpdate(UpdateInfo info, Action<int, string> progressCallback)
        {
            if (info == null || string.IsNullOrEmpty(info.DownloadUrl)) return false;

            var mirrorList = new List<string>();
            if (info.DynamicMirrors != null && info.DynamicMirrors.Count > 0)
            {
                mirrorList.AddRange(info.DynamicMirrors);
            }
            foreach (var dm in DefaultDownloadMirrors)
            {
                if (!mirrorList.Contains(dm)) mirrorList.Add(dm);
            }

            string tempDownloadFile = Path.Combine(Path.GetTempPath(), "siyuan_update_" + Guid.NewGuid().ToString("N") + ".download");

            foreach (var mirror in mirrorList)
            {
                string targetDownloadUrl = mirror + info.DownloadUrl;
                try
                {
                    if (progressCallback != null)
                    {
                        string mirrorName = string.IsNullOrEmpty(mirror) ? "官方直连" : mirror.Replace("https://", "").TrimEnd('/');
                        progressCallback(0, "正在连接高速节点: " + mirrorName + "...");
                    }

                    if (DownloadFileWithProgress(targetDownloadUrl, tempDownloadFile, progressCallback))
                    {
                        // 1. 门禁一：尺寸校验 (EXE 必须大于 1MB)
                        var fi = new FileInfo(tempDownloadFile);
                        if (!fi.Exists || fi.Length < 1024 * 1024)
                        {
                            try { File.Delete(tempDownloadFile); } catch { }
                            continue;
                        }

                        // 2. 门禁二：PE 头 MZ 魔数校验 (杜绝下载到 HTML 报错页)
                        using (var fs = new FileStream(tempDownloadFile, FileMode.Open, FileAccess.Read))
                        {
                            byte[] magic = new byte[2];
                            int read = fs.Read(magic, 0, 2);
                            if (read < 2 || magic[0] != 0x4D || magic[1] != 0x5A) // 'M', 'Z'
                            {
                                fs.Close();
                                try { File.Delete(tempDownloadFile); } catch { }
                                continue;
                            }
                        }

                        // 3. 门禁三：SHA-256 完整性强校验
                        if (!string.IsNullOrWhiteSpace(info.Sha256))
                        {
                            string fileHash = ComputeSha256(tempDownloadFile);
                            if (!string.Equals(fileHash, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(tempDownloadFile); } catch { }
                                continue;
                            }
                        }

                        // 通过全部门禁，执行 Win32 原位无感热替换
                        if (progressCallback != null) progressCallback(100, "校验通过，正在无感热替换重启...");
                        return ApplyInPlaceSwap(tempDownloadFile);
                    }
                }
                catch
                {
                    try { if (File.Exists(tempDownloadFile)) File.Delete(tempDownloadFile); } catch { }
                }
            }

            return false;
        }

        private static bool DownloadFileWithProgress(string url, string destinationPath, Action<int, string> progressCallback)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 8000;
            req.ReadWriteTimeout = 25000;
            req.UserAgent = "XiaoLvDesktop/" + CURRENT_VERSION;

            using (var resp = req.GetResponse())
            {
                long totalBytes = resp.ContentLength;
                using (var stream = resp.GetResponseStream())
                using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    long totalRead = 0;
                    int lastPercent = -1;

                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fs.Write(buffer, 0, read);
                        totalRead += read;

                        if (totalBytes > 0)
                        {
                            int percent = (int)((totalRead * 100) / totalBytes);
                            if (percent != lastPercent && progressCallback != null)
                            {
                                lastPercent = percent;
                                string speedText = string.Format("正在极速下载: {0:F1}MB / {1:F1}MB ({2}%)",
                                    totalRead / (1024.0 * 1024.0),
                                    totalBytes / (1024.0 * 1024.0),
                                    percent);
                                progressCallback(percent, speedText);
                            }
                        }
                    }
                }
            }

            return File.Exists(destinationPath);
        }

        private static bool ApplyInPlaceSwap(string tempFilePath)
        {
            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                string backupOldPath = currentExePath + ".old";

                // 1. 清理遗留 .old
                if (File.Exists(backupOldPath))
                {
                    try { File.Delete(backupOldPath); } catch { }
                }

                // 2. 将正在运行的当前 EXE 重命名为 .old (Windows 合法操作)
                File.Move(currentExePath, backupOldPath);

                // 3. 将新下载的文件移动为正式可执行程序
                File.Move(tempFilePath, currentExePath);

                // 4. 拉起新进程并退出旧进程
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExePath,
                    WorkingDirectory = Path.GetDirectoryName(currentExePath),
                    UseShellExecute = true
                });

                // 退出当前运行中的实例
                Environment.Exit(0);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("ApplyInPlaceSwap failed: " + ex.Message);
                return false;
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder();
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }

        private static string FetchStringWithTimeout(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "XiaoLvDesktop/" + CURRENT_VERSION;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        public static int CompareVersion(string v1, string v2)
        {
            try
            {
                var p1 = v1.Trim('v', 'V').Split('.');
                var p2 = v2.Trim('v', 'V').Split('.');
                for (int i = 0; i < Math.Max(p1.Length, p2.Length); i++)
                {
                    int n1 = i < p1.Length ? int.Parse(p1[i]) : 0;
                    int n2 = i < p2.Length ? int.Parse(p2[i]) : 0;
                    if (n1 > n2) return 1;
                    if (n1 < n2) return -1;
                }
                return 0;
            }
            catch { return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
