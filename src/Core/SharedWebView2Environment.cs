using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace XiaoLv.Desktop.Core
{
    public static class SharedWebView2Environment
    {
        private static Task<CoreWebView2Environment> _initTask;
        private static readonly object _lock = new object();

        public static Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            lock (_lock)
            {
                if (_initTask == null)
                {
                    var userDataFolder = Path.Combine(EmbeddedResourceManager.AppDataDir, "WebView2");
                    Directory.CreateDirectory(userDataFolder);

                    // 极致轻量化 Chromium 启动参数调优：
                    // 1. --renderer-process-limit=1: 强制合并与限制渲染进程实例
                    // 2. --disable-background-networking: 关停 Chromium 后台网络与遥测探测
                    // 3. --disable-component-update: 关闭后台组件自检与冗余更新
                    // 4. --disable-features=Translate,OptimizationHints...: 剥离沉重且无用的浏览器内置特性
                    // 5. --js-flags="--max-old-space-size=64": 将 V8 老生代堆内存阈值收窄至 64MB，积极触发 V8 内部垃圾回收
                    var options = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = "--renderer-process-limit=1 " +
                                                     "--disable-background-networking " +
                                                     "--disable-component-update " +
                                                     "--disable-features=Translate,OptimizationHints,MediaRouter,CalculateNativeWinOcclusion,InterestFeedContentSuggestions " +
                                                     "--js-flags=\"--max-old-space-size=64\""
                    };

                    _initTask = CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                }
                return _initTask;
            }
        }
    }
}
