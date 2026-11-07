using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace XiaoLv.Desktop.Core
{
    public static class EmbeddedResourceManager
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static string _appDataDir;
        private static string _assetsDir;
        private static bool? _isPortableMode;

        public static bool IsPortableMode
        {
            get
            {
                if (!_isPortableMode.HasValue)
                {
                    var dir = AppDataDir;
                }
                return _isPortableMode ?? false;
            }
        }

        public static string AppDataDir
        {
            get
            {
                if (_appDataDir == null)
                {
                    _appDataDir = ResolveOptimalDataDir();
                }
                return _appDataDir;
            }
        }

        private static string ResolveOptimalDataDir()
        {
            try
            {
                // 0. 隔离靶场优先：若环境变量指定 SIYUAN_URL，为保持 E2E 隔离靶场一致性，使用 AppData 模式
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIYUAN_URL")))
                {
                    _isPortableMode = false;
                    var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoLvDesktop");
                    if (!Directory.Exists(defaultDir)) Directory.CreateDirectory(defaultDir);
                    return defaultDir;
                }

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // 1. 检查是否存在自定义重定向文件 data_path.txt
                var redirectFile = Path.Combine(baseDir, "data_path.txt");
                if (File.Exists(redirectFile))
                {
                    string customPath = File.ReadAllText(redirectFile, System.Text.Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(customPath))
                    {
                        if (!Directory.Exists(customPath)) Directory.CreateDirectory(customPath);
                        _isPortableMode = true;
                        return customPath;
                    }
                }

                // 2. 检查程序同级是否存在 data 文件夹或 portable.txt 标记 (便携模式优先)
                var localDataDir = Path.Combine(baseDir, "data");
                var portableFlag = Path.Combine(baseDir, "portable.txt");
                bool isPortableExplicit = Directory.Exists(localDataDir) || File.Exists(portableFlag);

                // 3. 若位于非 C 盘（如 D 盘），且对当前目录具有完全写权限，推荐并优先开启同级便携模式
                bool isNonCDrive = !baseDir.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase);
                if (isPortableExplicit || (isNonCDrive && HasWritePermission(baseDir)))
                {
                    if (!Directory.Exists(localDataDir)) Directory.CreateDirectory(localDataDir);
                    _isPortableMode = true;
                    return localDataDir;
                }
            }
            catch { }

            // 4. 标准系统 AppData 模式（C 盘受限环境或普通安装环境）
            _isPortableMode = false;
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoLvDesktop");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            return appData;
        }

        private static bool HasWritePermission(string dirPath)
        {
            try
            {
                string testFile = Path.Combine(dirPath, ".test_write_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string AssetsDir
        {
            get
            {
                if (_assetsDir == null)
                {
                    // 1. 优先使用程序同目录下的 assets 文件夹
                    var localAssets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
                    if (Directory.Exists(localAssets) && File.Exists(Path.Combine(localAssets, "panel.html")))
                    {
                        _assetsDir = localAssets;
                    }
                    else
                    {
                        // 2. 回退使用 AppData 中的自解压 assets
                        _assetsDir = Path.Combine(AppDataDir, "assets");
                        if (!Directory.Exists(_assetsDir)) Directory.CreateDirectory(_assetsDir);
                    }
                }
                return _assetsDir;
            }
        }

        public static void InitializeAssemblyResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    string shortName = new AssemblyName(args.Name).Name;
                    string resName = shortName + ".dll";

                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
                    {
                        if (stream == null) return null;
                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                        return Assembly.Load(bytes);
                    }
                }
                catch
                {
                    return null;
                }
            };
        }

        public static void EnsureRuntimeAssets()
        {
            try
            {
                // 1. 释放并加载原生 WebView2Loader.dll
                var runtimesDir = Path.Combine(AppDataDir, "runtimes");
                if (!Directory.Exists(runtimesDir)) Directory.CreateDirectory(runtimesDir);
                var loaderPath = Path.Combine(runtimesDir, "WebView2Loader.dll");

                if (!File.Exists(loaderPath))
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebView2Loader.dll"))
                    {
                        if (stream != null)
                        {
                            using (var fs = new FileStream(loaderPath, FileMode.Create, FileAccess.Write))
                            {
                                stream.CopyTo(fs);
                            }
                        }
                    }
                }

                // 将该路径加入 DLL 搜索目录
                if (File.Exists(loaderPath))
                {
                    SetDllDirectory(runtimesDir);
                    var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!currentPath.Contains(runtimesDir))
                    {
                        Environment.SetEnvironmentVariable("PATH", runtimesDir + ";" + currentPath);
                    }
                }

                // 2. 检查 assets 目录；若缺失或版本升级则自解压/覆盖 assets.zip
                var panelHtml = Path.Combine(AssetsDir, "panel.html");
                var versionFile = Path.Combine(AssetsDir, ".version");
                string currentVersion = AutoUpdater.CURRENT_VERSION;
                bool needExtract = !File.Exists(panelHtml) || !File.Exists(versionFile) || File.ReadAllText(versionFile).Trim() != currentVersion;

                if (needExtract)
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("assets.zip"))
                    {
                        if (stream != null)
                        {
                            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                            {
                                foreach (var entry in archive.Entries)
                                {
                                    if (string.IsNullOrEmpty(entry.Name)) continue;
                                    var fullPath = Path.Combine(AssetsDir, entry.FullName);
                                    var parent = Path.GetDirectoryName(fullPath);
                                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                                    entry.ExtractToFile(fullPath, true);
                                }
                            }
                            try { File.WriteAllText(versionFile, currentVersion, System.Text.Encoding.UTF8); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public static string ResolveAssetPath(string relativePath)
        {
            // 优先当前资产目录
            var p = Path.Combine(AssetsDir, relativePath);
            if (File.Exists(p)) return p;

            // 再次尝试程序目录
            var p2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (File.Exists(p2)) return p2;

            return p;
        }
    }
}
