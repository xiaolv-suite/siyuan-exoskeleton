using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    public class BentoWindow : Window
    {
        private WebView2 _webView;
        private bool _isInitialized = false;
        private bool _isPageLoaded = false;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_CONTROL = 0x11;
        private const byte VK_V = 0x56;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static IntPtr LastExternalForegroundHwnd { get; set; }
        private IntPtr _previousForegroundHwnd = IntPtr.Zero;
        private static readonly Dictionary<string, object> _blockContextCache = new Dictionary<string, object>();
        private static Timer _saveSizeTimer = null;
        private string _currentInspectedDocId = "";
        private string _currentInspectedDocTitle = "";
        private bool _suppressAutoHide = false;
        private bool _isWindowPinned = false;

        public event Action<string, string> OnSubActionRequested;
        public event Action<string> OnSaveSettingsRequested;
        public event Action<string, string> OnNotificationRequested;

        public void NotifyDesktopSettingsSaved(bool success, string error = null, bool? webhookEnabled = null, string warning = null)
        {
            if (_webView == null || _webView.CoreWebView2 == null) return;
            var payload = JsonHelper.ToJson(new { success = success, error = error, webhookEnabled = webhookEnabled, warning = warning });
            _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDesktopSettingsSaved) window.onDesktopSettingsSaved(" + payload + ");");
        }

        private void NotifyOfflineQueueReadFailure(Exception ex, string operation = "get")
        {
            Logger.Error("Offline queue UI request failed; queue data was not changed", ex);
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_webView != null && _webView.CoreWebView2 != null)
                    {
                        var payload = JsonHelper.ToJson(new { code = 1, operation = operation, msg = "队列无法读取或更新；原文件已保留。请确认使用原 Windows 用户并检查数据目录权限。" });
                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOfflineQueueLoaded) window.onOfflineQueueLoaded(" + payload + ");");
                    }
                }));
            }
            catch (Exception dispatchError)
            {
                Logger.Error("Could not report offline queue read failure to the UI", dispatchError);
            }
        }

        public BentoWindow()
        {
            Title = "思源外骨骼 · 桌面穿透工作台";

            // 窗口尺寸自适应与持久化记忆 (默认舒适大屏 1180x740，自适应屏幕工作区)
            var s = AppSettings.Instance;
            double w = s.PanelWidth > 600 ? s.PanelWidth : 1180;
            double h = s.PanelHeight > 400 ? s.PanelHeight : 740;
            double maxW = SystemParameters.WorkArea.Width > 0 ? SystemParameters.WorkArea.Width * 0.96 : 1400;
            double maxH = SystemParameters.WorkArea.Height > 0 ? SystemParameters.WorkArea.Height * 0.96 : 900;
            Width = Math.Min(w, maxW);
            Height = Math.Min(h, maxH);
            MinWidth = 880;
            MinHeight = 560;

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;

            // 原生拖拽直投支持
            AllowDrop = true;
            DragEnter += (s_ev, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            };
            Drop += (s_ev, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        string targetDoc = _currentInspectedDocId;
                        string targetDocTitle = _currentInspectedDocTitle;
                        ThreadPool.QueueUserWorkItem(st =>
                        {
                            foreach (var f in files)
                            {
                                string droppedFile = f;
                                FileDispatcher.DispatchFileToDocument(droppedFile, targetDoc, targetDocTitle, (title, msg) =>
                                {
                                    if (OnNotificationRequested != null) OnNotificationRequested(title, msg);
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        if (_webView != null && _webView.CoreWebView2 != null)
                                        {
                                            var dropPayload = new Dictionary<string, object>();
                                            bool accepted = !string.IsNullOrEmpty(msg) && msg.StartsWith("✓", StringComparison.Ordinal);
                                            bool queued = accepted && (title ?? "").Contains("离岸");
                                            dropPayload["success"] = accepted && !queued;
                                            dropPayload["queued"] = queued;
                                            dropPayload["filePath"] = droppedFile ?? "";
                                            dropPayload["fileName"] = Path.GetFileName(droppedFile ?? "");
                                            dropPayload["docId"] = targetDoc ?? "";
                                            dropPayload["docTitle"] = !string.IsNullOrEmpty(targetDocTitle) ? targetDocTitle : "今日日记";
                                            dropPayload["message"] = msg ?? "";
                                            _webView.CoreWebView2.ExecuteScriptAsync("if(window.onFileDroppedToSiYuan) window.onFileDroppedToSiYuan(" + JsonHelper.ToJson(dropPayload) + ");");
                                        }
                                    }));
                                });
                            }
                        });
                    }
                }
            };

            // 外部容器边框 (暗夜基底，消除白边伪影)
            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#090d16")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };

            _webView = new WebView2();
            try { _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent; } catch { }
            border.Child = _webView;
            Content = border;

            // 极速秒开与秒关：保持 GPU 表面随时活跃，0ms 瞬时上屏与隐退，后台挂起节约内存
            IsVisibleChanged += (s_ev, e) =>
            {
                if (IsVisible)
                {
                    Opacity = 1.0;
                    ResumeWebViewMemory();
                }
                else
                {
                    SuspendWebViewMemory();
                }
            };

            // 尺寸变动防抖记忆
            SizeChanged += (s_ev, e) =>
            {
                if (WindowState == WindowState.Normal && ActualWidth >= 600 && ActualHeight >= 400)
                {
                    AppSettings.Instance.PanelWidth = ActualWidth;
                    AppSettings.Instance.PanelHeight = ActualHeight;
                    ScheduleSaveSize();
                }
            };

            PreviewKeyDown += (s_ev, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (_webView != null && _webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.handleGlobalEscapeKey){window.handleGlobalEscapeKey();}else{if(window.closeWindow)window.closeWindow();}");
                    }
                    else
                    {
                        Hide();
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.F11)
                {
                    WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                    e.Handled = true;
                }
            };

            Deactivated += (s_ev, e) =>
            {
                // 失去焦点时自动隐藏，保持轻量呼吸感 (若正在弹出原生目录选择框或已点击📌锁定窗口常驻则不自动隐藏)
                if (_suppressAutoHide || _isWindowPinned) return;
                Hide();
            };

            CodeSnippetManager.OnExternalCodeBlockSynced += (bId, updatedCode, lang, docTitle) =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                        {
                            _webView.CoreWebView2.ExecuteScriptAsync("if(window.onExternalCodeSynced) window.onExternalCodeSynced(" + JsonHelper.ToJson(new { code = 0, blockId = bId, content = updatedCode, language = lang, docTitle = docTitle }) + ");");
                        }
                    }));
                    if (OnNotificationRequested != null)
                    {
                        OnNotificationRequested("代码工作台 · 热编辑回写", string.Format("✓ 外部 IDE 保存已自动同步回思源《{0}》代码块！", docTitle));
                    }
                }
                catch { }
            };

            Loaded += BentoWindow_Loaded;
        }

        private static void ScheduleSaveSize()
        {
            if (_saveSizeTimer != null) _saveSizeTimer.Dispose();
            _saveSizeTimer = new Timer(st =>
            {
                try
                {
                    AppSettings.Instance.Save();
                }
                catch { }
            }, null, 1000, Timeout.Infinite);
        }

        private static void RestoreHotkeySetting(AppSettings settings, string configKey, string value)
        {
            if (configKey == "masterHotKey") settings.MasterHotKey = value;
            else if (configKey == "penHotKey") settings.PenHotKey = value;
            else if (configKey == "ocrHotKey") settings.OcrHotKey = value;
            else if (configKey == "quickHotKey") settings.QuickHotKey = value;
            else if (configKey == "fsrsHotKey") settings.FsrsHotKey = value;
            else if (configKey == "clipHotKey") settings.ClipHotKey = value;
        }

        private static Dictionary<string, string> _cachedNbMap = null;
        private static int _lastNbMapTick = 0;

        private static async System.Threading.Tasks.Task<Dictionary<string, string>> GetNotebookMapAsync()
        {
            int now = Environment.TickCount;
            if (_cachedNbMap != null && (now - _lastNbMapTick) < 60000)
            {
                return _cachedNbMap;
            }

            try
            {
                var res = await SiYuanClient.PostAsync("/api/notebook/lsNotebooks", new { });
                if (res != null && res.Success && res.Data != null)
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var dict = res.Data as Dictionary<string, object>;
                    if (dict != null)
                    {
                        var list = JsonHelper.GetList(dict, "notebooks");
                        if (list != null)
                        {
                            foreach (var it in list)
                            {
                                var nObj = it as Dictionary<string, object>;
                                if (nObj != null)
                                {
                                    string id = JsonHelper.GetString(nObj, "id");
                                    string name = JsonHelper.GetString(nObj, "name");
                                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                                    {
                                        map[id] = name;
                                    }
                                }
                            }
                        }
                    }
                    _cachedNbMap = map;
                    _lastNbMapTick = now;
                    return _cachedNbMap;
                }
            }
            catch { }
            return _cachedNbMap ?? new Dictionary<string, string>();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            var source = HwndSource.FromHwnd(helper.Handle);
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }

        private const int WM_NCHITTEST = 0x0084;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST && WindowState == WindowState.Normal)
            {
                int pX = (short)((int)lParam & 0xFFFF);
                int pY = (short)(((int)lParam >> 16) & 0xFFFF);
                Point pt = PointFromScreen(new Point(pX, pY));

                int borderSize = 8;

                bool isLeft = pt.X <= borderSize;
                bool isRight = pt.X >= ActualWidth - borderSize;
                bool isTop = pt.Y <= borderSize;
                bool isBottom = pt.Y >= ActualHeight - borderSize;

                if (isTop && isLeft) { handled = true; return (IntPtr)HTTOPLEFT; }
                if (isTop && isRight) { handled = true; return (IntPtr)HTTOPRIGHT; }
                if (isBottom && isLeft) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                if (isBottom && isRight) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
                if (isLeft) { handled = true; return (IntPtr)HTLEFT; }
                if (isRight) { handled = true; return (IntPtr)HTRIGHT; }
                if (isTop) { handled = true; return (IntPtr)HTTOP; }
                if (isBottom) { handled = true; return (IntPtr)HTBOTTOM; }
            }
            return IntPtr.Zero;
        }

        public async void PrewarmAsync()
        {
            if (_isInitialized) return;
            try
            {
                // 确保 HWND 句柄生成，以便 WebView2 在后台静默预热
                new WindowInteropHelper(this).EnsureHandle();

                var env = await SharedWebView2Environment.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

                _webView.NavigationCompleted += (s, ev) =>
                {
                    _isPageLoaded = true;
                    if (!IsVisible)
                    {
                        SuspendWebViewMemory();
                    }
                };
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                NavigateToPanel();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 PrewarmAsync failed: " + ex.Message);
            }
        }

        private async void BentoWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) return;
            try
            {
                var env = await SharedWebView2Environment.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

                _webView.NavigationCompleted += (s, ev) =>
                {
                    _isPageLoaded = true;
                    if (!IsVisible)
                    {
                        SuspendWebViewMemory();
                    }
                };
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                NavigateToPanel();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("初始化 WebView2 内核失败: " + ex.Message, "思源外骨骼", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = "";
                try
                {
                    raw = e.TryGetWebMessageAsString();
                }
                catch
                {
                    // 当前端发送原生 JS 字典对象时，TryGetWebMessageAsString 会抛出 ArgumentException，由 WebMessageAsJson 兜底
                }

                if (string.IsNullOrEmpty(raw))
                {
                    try { raw = e.WebMessageAsJson; } catch { }
                }

                if (string.IsNullOrEmpty(raw)) return;

                var msg = JsonHelper.ParseDict(raw);
                string op = JsonHelper.GetString(msg, "op");
                if (string.IsNullOrEmpty(op)) op = JsonHelper.GetString(msg, "action");

                // 0. 窗口拖拽直通 (先释放 WebView2 内部鼠标捕获再触发 Win32 HTCAPTION 原生丝滑拖拽)
                if (op.Equals("dragMove", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ReleaseCapture();
                        var hwnd = new WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                        }
                        else
                        {
                            DragMove();
                        }
                    }
                    catch { }
                    return;
                }

                // 0.5 窗口常驻置顶图钉锁 (togglePinWindow)
                if (op.Equals("togglePinWindow", StringComparison.OrdinalIgnoreCase))
                {
                    _isWindowPinned = JsonHelper.GetBool(msg, "pinned", !_isWindowPinned);
                    return;
                }

                // 1. 关闭总控面板
                if (op.Equals("close", StringComparison.OrdinalIgnoreCase))
                {
                    Hide();
                    return;
                }

                // 1.5 最大化 / 沉浸式 Zen 模式切换 (F11)
                if (op.Equals("toggleMaximize", StringComparison.OrdinalIgnoreCase))
                {
                    WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                    return;
                }

                // 1.8 打开本地数据存储目录
                if (op.Equals("openDataDir", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var dir = EmbeddedResourceManager.AppDataDir;
                        Process.Start("explorer.exe", "\"" + dir + "\"");
                    }
                    catch { }
                    return;
                }

                // 1.9 桌面图钉悬浮参考便签 (Sticky Pin HUD)
                if (op.Equals("pinStickyNote", StringComparison.OrdinalIgnoreCase))
                {
                    string t = JsonHelper.GetString(msg, "title");
                    string c = JsonHelper.GetString(msg, "content");
                    string bId = JsonHelper.GetString(msg, "blockId");
                    MiniInputWindow.ShowStickyNote(t, c, bId);
                    return;
                }

                // 1.95 测试 AI 连通性
                if (op.Equals("testAiConnection", StringComparison.OrdinalIgnoreCase))
                {
                    string baseUrl = JsonHelper.GetString(msg, "baseUrl");
                    string apiKey = JsonHelper.GetString(msg, "apiKey");
                    string model = JsonHelper.GetString(msg, "model");
                    ThreadPool.QueueUserWorkItem(st =>
                    {
                        var res = AiRefiner.TestConnection(baseUrl, apiKey, model);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                string jsonRes = JsonHelper.ToJson(res);
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onAiTestResult) window.onAiTestResult(" + jsonRes + ");");
                            }
                        });
                    });
                    return;
                }

                // 1.96 入库前大模型清洗提炼
                if (op.Equals("refineTextWithAi", StringComparison.OrdinalIgnoreCase))
                {
                    string inst = JsonHelper.GetString(msg, "instruction");
                    string txt = JsonHelper.GetString(msg, "text");
                    ThreadPool.QueueUserWorkItem(st =>
                    {
                        var res = AiRefiner.Refine(inst, txt);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                string jsonRes = JsonHelper.ToJson(res);
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onAiRefineResult) window.onAiRefineResult(" + jsonRes + ");");
                            }
                        });
                    });
                    return;
                }

                // 2. 保存偏好设置
                if (op.Equals("saveSettings", StringComparison.OrdinalIgnoreCase))
                {
                    if (OnSaveSettingsRequested != null)
                    {
                        OnSaveSettingsRequested(raw);
                    }
                    return;
                }

                // 3. 毫秒级原生块搜索 (b: 语法，空关键词默认呈现最近修改的 30 条正文块)
                if (op.Equals("searchBlocks", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeLike = (kw ?? "").Trim().Replace("'", "''");
                            string sql;
                            if (!string.IsNullOrEmpty(safeLike))
                            {
                                sql = "SELECT id, root_id, content, hpath, type, updated FROM blocks " +
                                      "WHERE content LIKE '%" + safeLike + "%' AND type NOT IN ('d') " +
                                      "ORDER BY (CASE " +
                                      "  WHEN content = '" + safeLike + "' THEN 100 " +
                                      "  WHEN content LIKE '" + safeLike + "%' THEN 60 " +
                                      "  WHEN type = 'h' THEN 40 " +
                                      "  WHEN type = 'p' THEN 20 " +
                                      "  ELSE 10 " +
                                      "END) DESC, updated DESC LIMIT 30";
                            }
                            else
                            {
                                sql = "SELECT id, root_id, content, hpath, type, updated FROM blocks " +
                                      "WHERE type IN ('p', 'h', 'c', 't', 'i') AND content != '' " +
                                      "ORDER BY updated DESC LIMIT 30";
                            }
                            var rows = await SiYuanClient.SqlAsync(sql);
                            Dispatcher.Invoke(() =>
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onBlockSearchResults) window.onBlockSearchResults(" + JsonHelper.ToJson(new { code = 0, data = rows }) + ");");
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3.4 全库代码片段秒搜 (code: 语法)
                if (op.Equals("searchCodeSnippets", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeLike = (kw ?? "").Replace("'", "''");
                            string sql = string.IsNullOrEmpty(safeLike)
                                ? "SELECT id, root_id, content, markdown, hpath, updated FROM blocks WHERE type='c' ORDER BY updated DESC LIMIT 30"
                                : "SELECT id, root_id, content, markdown, hpath, updated FROM blocks WHERE type='c' AND (content LIKE '%" + safeLike + "%' OR markdown LIKE '%" + safeLike + "%') ORDER BY updated DESC LIMIT 30";

                            var rows = await SiYuanClient.SqlAsync(sql);
                            var snippets = new List<CodeSnippetItem>();
                            if (rows != null)
                            {
                                foreach (var r in rows)
                                {
                                    string rawCode = JsonHelper.GetString(r, "content");
                                    string rawMd = JsonHelper.GetString(r, "markdown");
                                    string detectedLang;
                                    string cleanCode = CodeSnippetManager.ExtractCleanCode(!string.IsNullOrEmpty(rawMd) ? rawMd : rawCode, out detectedLang);

                                    string hpath = JsonHelper.GetString(r, "hpath");
                                    string docTitle = Path.GetFileName(hpath);
                                    if (string.IsNullOrEmpty(docTitle)) docTitle = "未命名笔记";

                                    snippets.Add(new CodeSnippetItem
                                    {
                                        BlockId = JsonHelper.GetString(r, "id"),
                                        RootId = JsonHelper.GetString(r, "root_id"),
                                        DocTitle = docTitle,
                                        Language = detectedLang,
                                        Code = cleanCode,
                                        LineCount = cleanCode.Split('\n').Length,
                                        FileExtension = CodeSnippetManager.ResolveExtension(detectedLang)
                                    });
                                }
                            }

                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onCodeSnippetsLoaded) window.onCodeSnippetsLoaded(" + JsonHelper.ToJson(new { code = 0, data = snippets }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3.45 纯净代码片段本地临时导出秒开 (支持外部 VS Code 热保存自动回写思源块)
                if (op.Equals("exportSourceFile", StringComparison.OrdinalIgnoreCase) ||
                    op.Equals("openCodeSnippetSourceFile", StringComparison.OrdinalIgnoreCase))
                {
                    string code = JsonHelper.GetString(msg, "code");
                    string lang = JsonHelper.GetString(msg, "lang");
                    if (string.IsNullOrEmpty(lang)) lang = JsonHelper.GetString(msg, "language", "text");
                    string title = JsonHelper.GetString(msg, "docTitle");
                    string blockId = JsonHelper.GetString(msg, "blockId");

                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        string target = CodeSnippetManager.ExportAndWatchSourceFile(code, lang, title, blockId, true);
                        if (!string.IsNullOrEmpty(target) && OnNotificationRequested != null)
                        {
                            OnNotificationRequested("思源外骨骼 · 源码热联动", "✓ 已生成 " + Path.GetFileName(target) + " 并在外部 IDE 秒开 (Ctrl+S 保存将自动回写思源)！");
                        }
                    });
                    return;
                }

                // 3.46 代码工作台原位编辑保存回思源代码块 (updateCodeBlock)
                if (op.Equals("updateCodeBlock", StringComparison.OrdinalIgnoreCase))
                {
                    string blockId = JsonHelper.GetString(msg, "blockId");
                    string code = JsonHelper.GetString(msg, "code");
                    string lang = JsonHelper.GetString(msg, "language");
                    if (string.IsNullOrEmpty(lang)) lang = JsonHelper.GetString(msg, "lang", "text");

                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        bool ok = await CodeSnippetManager.UpdateCodeBlockInSiYuanAsync(blockId, code, lang);
                        if (ok) SoundPlayerHelper.PlayFeedback();
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onCodeBlockUpdated) window.onCodeBlockUpdated(" + JsonHelper.ToJson(new { code = 0, success = ok, blockId = blockId }) + ");");
                            }
                        });
                        if (ok && OnNotificationRequested != null)
                        {
                            OnNotificationRequested("代码工作台 · 原位写回", "✓ 纯净源码已成功原位保存回思源代码块！");
                        }
                    });
                    return;
                }

	                // 3.48 全库表格与属性视图秒搜与解析 (tab: / table: 语法)
	                if (op.Equals("searchTables", StringComparison.OrdinalIgnoreCase))
	                {
	                    string kw = JsonHelper.GetString(msg, "keyword");
	                    ThreadPool.QueueUserWorkItem(async s =>
	                    {
	                        try
	                        {
	                            string safeLike = (kw ?? "").Replace("'", "''");
	                            string sql = string.IsNullOrEmpty(safeLike)
	                                ? "SELECT id, root_id, content, markdown, hpath, type, updated FROM blocks WHERE type IN ('t', 'av') ORDER BY updated DESC LIMIT 25"
	                                : "SELECT id, root_id, content, markdown, hpath, type, updated FROM blocks WHERE type IN ('t', 'av') AND (content LIKE '%" + safeLike + "%' OR markdown LIKE '%" + safeLike + "%') ORDER BY updated DESC LIMIT 25";

	                            var rows = await SiYuanClient.SqlAsync(sql);
	                            Dispatcher.Invoke(() =>
	                            {
	                                if (_webView != null && _webView.CoreWebView2 != null)
	                                {
	                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTablesLoaded) window.onTablesLoaded(" + JsonHelper.ToJson(new { code = 0, data = rows }) + ");");
	                                }
	                            });
	                        }
	                        catch { }
	                    });
	                    return;
	                }

                // 3.49 表格数据结构解析与美化转换 (parseTableData)
                if (op.Equals("parseTableData", StringComparison.OrdinalIgnoreCase))
                {
                    string rawTableText = JsonHelper.GetString(msg, "rawText");
                    string blockId = JsonHelper.GetString(msg, "blockId");
                    string title = JsonHelper.GetString(msg, "title", "数据表格");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(rawTableText))
                            {
                                string sql = !string.IsNullOrWhiteSpace(blockId)
                                    ? "SELECT id, content, markdown FROM blocks WHERE id='" + blockId.Replace("'", "''") + "' LIMIT 1"
                                    : "SELECT id, content, markdown FROM blocks WHERE type='t' ORDER BY updated DESC LIMIT 1";
                                var rows = await SiYuanClient.SqlAsync(sql);
                                if (rows != null && rows.Count > 0)
                                {
                                    rawTableText = JsonHelper.GetString(rows[0], "markdown");
                                    if (string.IsNullOrWhiteSpace(rawTableText)) rawTableText = JsonHelper.GetString(rows[0], "content");
                                    if (string.IsNullOrWhiteSpace(blockId)) blockId = JsonHelper.GetString(rows[0], "id");
                                }
                            }

                            var parsed = TableToAvConverter.ParseTable(rawTableText);
                            string beautifulMd = TableToAvConverter.FormatBeautifulMarkdownTable(parsed);
                            string foldable = TableToAvConverter.ConvertToFoldableList(parsed, title);
                            string csv = TableToAvConverter.ConvertToCsv(parsed);

                            var payload = new Dictionary<string, object>();
                            payload["blockId"] = blockId ?? "";
                            payload["structure"] = parsed;
                            payload["beautifulMarkdown"] = beautifulMd;
                            payload["foldableList"] = foldable;
                            payload["csv"] = csv;

                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTableDataParsed) window.onTableDataParsed(" + JsonHelper.ToJson(new { code = 0, data = payload }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3.495 表格矩阵原位编辑保存回思源表格块 (updateTableBlock)
                if (op.Equals("updateTableBlock", StringComparison.OrdinalIgnoreCase))
                {
                    string blockId = JsonHelper.GetString(msg, "blockId");
                    string md = JsonHelper.GetString(msg, "markdown");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        bool ok = await TableToAvConverter.UpdateTableBlockInSiYuanAsync(blockId, md);
                        if (ok) SoundPlayerHelper.PlayFeedback();
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTableBlockUpdated) window.onTableBlockUpdated(" + JsonHelper.ToJson(new { code = 0, success = ok, blockId = blockId }) + ");");
                            }
                        });
                        if (ok && OnNotificationRequested != null)
                        {
                            OnNotificationRequested("表格矩阵 · 原位写回", "✓ 表格修改已成功原位保存回思源笔记表格块！");
                        }
                    });
                    return;
                }

		                // 3.50 表格 1 键导出 Excel.xlsx 并本地秒开
		                if (op.Equals("exportTableStructureXlsx", StringComparison.OrdinalIgnoreCase))
		                {
		                    string rawTableText2 = JsonHelper.GetString(msg, "rawText");
		                    string title = JsonHelper.GetString(msg, "title", "表格导出");
		                    ThreadPool.QueueUserWorkItem(s =>
		                    {
		                        try
		                        {
		                            var parsed = TableToAvConverter.ParseTable(rawTableText2);
	                            string outPath = TableToAvConverter.ExportToXlsxAndOpen(parsed, title);
	                            if (!string.IsNullOrEmpty(outPath) && OnNotificationRequested != null)
	                            {
	                                OnNotificationRequested("思源外骨骼 · 表格导出", "✓ 已成功将表格转为标准 Excel 工作簿并在外部秒开！");
		                            }
		                        }
		                        catch { }
		                    });
		                    return;
		                }

		                // 3.51 浮墨漫游回顾与那年今日灵感唤醒 (rand: / flash: 语法)
		                if (op.Equals("getFlashbackBlock", StringComparison.OrdinalIgnoreCase))
		                {
		                    ThreadPool.QueueUserWorkItem(async s =>
		                    {
		                        try
		                        {
		                            var item = await FlashbackManager.GetRandomFlashbackAsync();
		                            Dispatcher.Invoke(() =>
		                            {
		                                if (_webView != null && _webView.CoreWebView2 != null)
		                                {
		                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onFlashbackLoaded) window.onFlashbackLoaded(" + JsonHelper.ToJson(new { code = 0, data = item }) + ");");
		                                }
		                            });
		                        }
		                        catch { }
		                    });
		                    return;
		                }

		                // 3.52 浮墨漫游追加续写思考
		                if (op.Equals("appendFlashbackThought", StringComparison.OrdinalIgnoreCase))
		                {
		                    string targetBlockId = JsonHelper.GetString(msg, "blockId");
		                    string thought = JsonHelper.GetString(msg, "thought");
		                    ThreadPool.QueueUserWorkItem(async s =>
		                    {
			                        try
			                        {
			                            bool ok = await FlashbackManager.AppendThoughtAsync(targetBlockId, thought);
			                            if (ok && OnNotificationRequested != null)
			                            {
			                                OnNotificationRequested("浮墨漫游回顾", "✓ 已成功将重温思考追加至该灵感块！");
			                            }
			                        }
			                        catch { }
			                    });
			                    return;
			                }

			                // 3.53 双向双链雷达 (Inbound Backlinks & Outbound References)
			                if (op.Equals("getBlockLinks", StringComparison.OrdinalIgnoreCase))
			                {
			                    string bId = JsonHelper.GetString(msg, "blockId");
			                    string bTitle = JsonHelper.GetString(msg, "title");
			                    ThreadPool.QueueUserWorkItem(async s =>
			                    {
				                        try
				                        {
				                            var links = await BiDirectionalGraphRadar.GetLinksForBlockAsync(bId, bTitle);
				                            string graphSvg = GraphVisualizer.RenderRadialGraphSvg(bId, bTitle, links.InboundBacklinks, links.OutboundReferences);
					                            Dispatcher.Invoke(() =>
					                            {
					                                if (_webView != null && _webView.CoreWebView2 != null)
					                                {
					                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onBlockLinksLoaded) window.onBlockLinksLoaded(" + JsonHelper.ToJson(new { code = 0, data = links, graphSvg = graphSvg }) + ");");
					                                }
					                            });
					                        }
				                        catch { }
				                    });
				                    return;
				                }

				                // 3.53B 全景星轨知识图谱大视窗 (Radial Star Graph Explorer)
				                if (op.Equals("getRadialGraphModal", StringComparison.OrdinalIgnoreCase))
				                {
				                    string bId = JsonHelper.GetString(msg, "blockId");
				                    string bTitle = JsonHelper.GetString(msg, "title");
				                    ThreadPool.QueueUserWorkItem(async s =>
				                    {
				                        try
				                        {
				                            string targetId = bId;
				                            string targetTitle = bTitle;

				                            if (string.IsNullOrEmpty(targetId))
				                            {
				                                string sql = "SELECT id, content FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 1";
				                                var rows = await SiYuanClient.SqlAsync(sql);
				                                if (rows != null && rows.Count > 0)
				                                {
				                                    targetId = JsonHelper.GetString(rows[0], "id");
				                                    targetTitle = JsonHelper.GetString(rows[0], "content");
				                                }
				                            }

					                            var links = await BiDirectionalGraphRadar.GetLinksForBlockAsync(targetId, targetTitle);
					                            string graphSvg = GraphVisualizer.RenderFullRadialGraphSvg(targetId, links.TargetTitle, links.InboundBacklinks, links.OutboundReferences, 920, 500, true);
					                            Dispatcher.Invoke(() =>
				                            {
				                                if (_webView != null && _webView.CoreWebView2 != null)
				                                {
				                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onRadialGraphLoaded) window.onRadialGraphLoaded(" + JsonHelper.ToJson(new { code = 0, centerId = targetId, centerTitle = links.TargetTitle, data = links, graphSvg = graphSvg }) + ");");
				                                }
				                            });
				                        }
				                        catch { }
				                    });
				                    return;
				                }

			                // 3.54 全景待办视界 (Tasks Horizon: P1/P2/今日分流与Checklist导出)
			                if (op.Equals("getTasksHorizon", StringComparison.OrdinalIgnoreCase))
			                {
			                    string kw = JsonHelper.GetString(msg, "keyword");
			                    ThreadPool.QueueUserWorkItem(async s =>
			                    {
			                        try
			                        {
			                            var rep = await TasksHorizonManager.GetTasksHorizonAsync(kw);
			                            Dispatcher.Invoke(() =>
			                            {
			                                if (_webView != null && _webView.CoreWebView2 != null)
			                                {
			                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTasksHorizonLoaded) window.onTasksHorizonLoaded(" + JsonHelper.ToJson(new { code = 0, data = rep }) + ");");
			                                }
			                            });
			                        }
			                        catch { }
			                    });
			                    return;
			                }

                // 3.55 离岸脱机缓冲队列透视与管理 (queue: / offline:)
                if (op.Equals("getOfflineQueue", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var items = OfflineQueueManager.GetQueueItems();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOfflineQueueLoaded) window.onOfflineQueueLoaded(" + JsonHelper.ToJson(new { code = 0, operation = "get", count = items.Count, data = items, items = items }) + ");");
                                }
                            });
                        }
                        catch (Exception ex) { NotifyOfflineQueueReadFailure(ex); }
                    });
                    return;
                }

                if (op.Equals("flushOfflineQueue", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var conn = SiYuanClient.GetConnectionInfo(false);
                            bool ok = OfflineQueueManager.FlushQueue(conn.Url, conn.Token, (t, m) =>
                            {
                                if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                            });
                            var remainingItems = OfflineQueueManager.GetQueueItems();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.updateOfflineQueueBadge) window.updateOfflineQueueBadge({{ count: {0} }});", remainingItems.Count));
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOfflineQueueLoaded) window.onOfflineQueueLoaded(" + JsonHelper.ToJson(new { code = 0, operation = "flush", count = remainingItems.Count, data = remainingItems, items = remainingItems, flushed = ok }) + ");");
                                }
                            });
                        }
                        catch (Exception ex) { NotifyOfflineQueueReadFailure(ex, "flush"); }
                    });
                    return;
                }

				                // 3.56 本地浏览器书签穿透检索 (bm: / book: 语法)
				                if (op.Equals("searchBrowserBookmarks", StringComparison.OrdinalIgnoreCase))
				                {
				                    string kw = JsonHelper.GetString(msg, "keyword");
				                    ThreadPool.QueueUserWorkItem(s =>
				                    {
				                        try
				                        {
				                            var bms = BrowserBookmarkSearcher.SearchBookmarks(kw);
				                            Dispatcher.Invoke(() =>
				                            {
				                                if (_webView != null && _webView.CoreWebView2 != null)
				                                {
				                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onBrowserBookmarksLoaded) window.onBrowserBookmarksLoaded(" + JsonHelper.ToJson(new { code = 0, data = bms }) + ");");
				                                }
				                            });
				                        }
				                        catch { }
				                    });
				                    return;
				                }

				                // 3.57 学术文献国家标准引用 (GB/T 7714 / APA / BibTeX) 1 键转译
				                if (op.Equals("formatCitationSuite", StringComparison.OrdinalIgnoreCase))
				                {
				                    string title = JsonHelper.GetString(msg, "title");
				                    string author = JsonHelper.GetString(msg, "author");
				                    string year = JsonHelper.GetString(msg, "year");
				                    string doi = JsonHelper.GetString(msg, "doi");
				                    string arxiv = JsonHelper.GetString(msg, "arxiv");

				                    ThreadPool.QueueUserWorkItem(s =>
				                    {
				                        try
				                        {
				                            var info = new CitationInfo
				                            {
				                                Title = title,
				                                Year = !string.IsNullOrEmpty(year) ? year : DateTime.Now.Year.ToString(),
				                                Doi = doi,
				                                ArxivId = arxiv
				                            };
				                            if (!string.IsNullOrEmpty(author))
				                            {
				                                foreach (var a in author.Split(new char[] { ',', '、', ';' }, StringSplitOptions.RemoveEmptyEntries))
				                                {
				                                    info.Authors.Add(a.Trim());
				                                }
				                            }
				                            string suiteMd = CitationFormatter.BuildCitationMarkdownSuite(info);
					                            Dispatcher.Invoke(() =>
					                            {
					                                if (_webView != null && _webView.CoreWebView2 != null)
					                                {
					                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onCitationFormatted) window.onCitationFormatted(" + JsonHelper.ToJson(new { code = 0, markdown = suiteMd, gbt = CitationFormatter.FormatGbT7714(info, 1), apa = CitationFormatter.FormatApa(info), bibtex = CitationFormatter.FormatBibTeX(info) }) + ");");
					                                }
					                            });
					                        }
					                        catch { }
					                    });
					                    return;
					                }

					                // 3.58 桌面轻量敏捷心流计时器 (pom: / timer:)
						                if (op.Equals("startFlowTimer", StringComparison.OrdinalIgnoreCase))
						                {
						                    int minutes = JsonHelper.GetInt(msg, "minutes", 25);
						                    string task = JsonHelper.GetString(msg, "task", "心流专注");
						                    string bId = JsonHelper.GetString(msg, "taskBlockId", "");
						                    string doc = JsonHelper.GetString(msg, "docTitle", "");
						                    bool autoCheck = JsonHelper.GetBool(msg, "autoCompleteTask", true);
						                    FlowTimerManager.StartTimer(minutes, task, bId, doc, autoCheck);
						                    return;
						                }

						                if (op.Equals("stopFlowTimer", StringComparison.OrdinalIgnoreCase))
						                {
						                    bool recordPartial = JsonHelper.GetBool(msg, "recordPartial", false);
						                    FlowTimerManager.StopTimer(recordPartial);
						                    return;
						                }

						                if (op.Equals("getFlowTimerStatus", StringComparison.OrdinalIgnoreCase))
						                {
						                    var status = FlowTimerManager.CurrentStatus;
						                    if (_webView != null && _webView.CoreWebView2 != null)
						                    {
						                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onFlowTimerStatus) window.onFlowTimerStatus(" + JsonHelper.ToJson(status) + ");");
						                    }
						                    return;
						                }

						                // 3.585 底栏番茄钟 (Dock Tomato) 生态综合专注状态感知
						                if (op.Equals("getIntegratedFocusStatus", StringComparison.OrdinalIgnoreCase))
						                {
						                    var status = DockTomatoBridge.GetIntegratedFocusStatus();
						                    if (_webView != null && _webView.CoreWebView2 != null)
						                    {
						                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onIntegratedFocusStatus) window.onIntegratedFocusStatus(" + JsonHelper.ToJson(status) + ");");
						                    }
						                    return;
						                }

						                // 3.586 从外部活动窗口前台上下文 1 键萃取高情境待办
						                if (op.Equals("createTaskFromContext", StringComparison.OrdinalIgnoreCase))
						                {
						                    string title = JsonHelper.GetString(msg, "title", "");
						                    string priority = JsonHelper.GetString(msg, "priority", "P2");
						                    string dueDate = JsonHelper.GetString(msg, "dueDate", "");
						                    bool ok = TasksHorizonManager.CreateTaskFromActiveContext(title, priority, dueDate);
						                    if (ok && OnNotificationRequested != null)
						                    {
						                        OnNotificationRequested("任务视界 · 待办萃取", "✓ 外部前台上下文待办已成功注入今日日记待办池！");
						                    }
						                    return;
						                }

					                // 3.59 剪贴板历史批量合并排版工作台 (c:merge)
					                if (op.Equals("mergeClipboardHistory", StringComparison.OrdinalIgnoreCase))
					                {
					                    string modeStr = JsonHelper.GetString(msg, "mode", "bullets");
					                    var rawList = JsonHelper.GetList(msg, "snippets");
					                    var snippets = new List<string>();

					                    if (rawList != null && rawList.Count > 0)
					                    {
					                        foreach (var it in rawList) if (it != null) snippets.Add(it.ToString());
					                    }
					                    else
					                    {
					                        var recent = ClipboardWatcher.GetRecentItemsSnapshot();
						                        for (int i = 0; i < Math.Min(5, recent.Count); i++)
						                        {
						                            string t = !string.IsNullOrEmpty(recent[i].RawText) ? recent[i].RawText : recent[i].EnrichedMarkdown;
						                            if (!string.IsNullOrEmpty(t)) snippets.Add(t);
						                        }
					                    }

						                    MergeMode mode = MergeMode.Bullets;
						                    if (modeStr.Equals("quotes", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Quotes;
						                    else if (modeStr.Equals("sections", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Sections;
						                    else if (modeStr.Equals("todo", StringComparison.OrdinalIgnoreCase) || modeStr.Equals("checklist", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Todo;
						                    else if (modeStr.Equals("table", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Table;
						                    else if (modeStr.Equals("code", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Code;
						                    else if (modeStr.Equals("plain", StringComparison.OrdinalIgnoreCase)) mode = MergeMode.Plain;

						                    string title = JsonHelper.GetString(msg, "title", "剪贴板多段合并");
						                    string merged = ClipboardBatchMerger.MergeSnippets(snippets, mode, title);
					                    if (_webView != null && _webView.CoreWebView2 != null)
					                    {
					                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onClipboardMerged) window.onClipboardMerged(" + JsonHelper.ToJson(new { code = 0, markdown = merged }) + ");");
					                    }
					                    return;
					                }

						                // 3.60 全库失效死链与空块深度诊断医生 (syh:deadlink)
						                if (op.Equals("diagnoseDeadLinks", StringComparison.OrdinalIgnoreCase))
						                {
						                    ThreadPool.QueueUserWorkItem(async s =>
						                    {
						                        try
						                        {
						                            var rep = await DeadLinkDoctor.DiagnoseAsync();
						                            Dispatcher.Invoke(() =>
						                            {
						                                if (_webView != null && _webView.CoreWebView2 != null)
						                                {
						                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDeadLinkReportLoaded) window.onDeadLinkReportLoaded(" + JsonHelper.ToJson(new { code = 0, data = rep }) + ");");
						                                }
						                            });
						                        }
						                        catch { }
						                    });
						                    return;
						                }

						                // 3.61 全库双链失效死链一键重定向自愈 (syh:heal)
						                if (op.Equals("healDeadLinks", StringComparison.OrdinalIgnoreCase))
						                {
						                    ThreadPool.QueueUserWorkItem(async s =>
						                    {
						                        try
						                        {
						                            var rep = await LinkAutoHealer.AutoHealDeadLinksAsync();
						                            Dispatcher.Invoke(() =>
						                            {
						                                if (_webView != null && _webView.CoreWebView2 != null)
						                                {
						                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDeadLinksHealed) window.onDeadLinksHealed(" + JsonHelper.ToJson(new { code = 0, data = rep }) + ");");
						                                }
						                            });
							                            if (rep.HealedCount > 0 && OnNotificationRequested != null)
							                            {
							                                OnNotificationRequested("双链智能自愈", string.Format("✓ 成功自愈重定向 {0} 处悬空死链！", rep.HealedCount));
							                            }
							                        }
							                        catch { }
							                    });
							                    return;
							                }

							                // 3.61B 系统全库综合体检与死链清道夫大视窗 (SystemComprehensiveDoctor)
							                if (op.Equals("runSystemDoctorFullAudit", StringComparison.OrdinalIgnoreCase))
							                {
							                    ThreadPool.QueueUserWorkItem(async s =>
							                    {
							                        try
							                        {
							                            var audit = await SystemComprehensiveDoctor.RunFullAuditAsync();
							                            Dispatcher.Invoke(() =>
							                            {
							                                if (_webView != null && _webView.CoreWebView2 != null)
							                                {
							                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onSystemDoctorReportLoaded) window.onSystemDoctorReportLoaded(" + JsonHelper.ToJson(new { code = 0, data = audit }) + ");");
							                                }
							                            });
							                        }
							                        catch { }
							                    });
							                    return;
							                }

							                if (op.Equals("quarantineDoctorOrphans", StringComparison.OrdinalIgnoreCase))
							                {
							                    int qCount = SystemComprehensiveDoctor.QuarantineOrphanAssets();
							                    if (_webView != null && _webView.CoreWebView2 != null)
							                    {
							                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDoctorOrphansQuarantined) window.onDoctorOrphansQuarantined(" + JsonHelper.ToJson(new { code = 0, count = qCount }) + ");");
							                    }
							                    if (qCount > 0 && OnNotificationRequested != null)
							                    {
							                        OnNotificationRequested("孤儿资产安全隔离", string.Format("✓ 已将 {0} 篇孤儿附件安全转移至隔离区！", qCount));
							                    }
							                    return;
							                }

							                if (op.Equals("restoreDoctorOrphans", StringComparison.OrdinalIgnoreCase))
							                {
							                    int rCount = SystemComprehensiveDoctor.RestoreOrphanAssets();
							                    if (_webView != null && _webView.CoreWebView2 != null)
							                    {
							                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDoctorOrphansRestored) window.onDoctorOrphansRestored(" + JsonHelper.ToJson(new { code = 0, count = rCount }) + ");");
							                    }
							                    if (rCount > 0 && OnNotificationRequested != null)
							                    {
							                        OnNotificationRequested("孤儿资产原位打捞", string.Format("✓ 已从隔离区原位打捞复原 {0} 篇附件！", rCount));
							                    }
							                    return;
							                }

							                if (op.Equals("cleanDoctorEmptyBlocks", StringComparison.OrdinalIgnoreCase))
							                {
							                    ThreadPool.QueueUserWorkItem(async s =>
							                    {
							                        int cleaned = await SystemComprehensiveDoctor.CleanEmptyBlocksAsync(50);
							                        Dispatcher.Invoke(() =>
							                        {
							                            if (_webView != null && _webView.CoreWebView2 != null)
							                            {
							                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDoctorEmptyBlocksCleaned) window.onDoctorEmptyBlocksCleaned(" + JsonHelper.ToJson(new { code = 0, count = cleaned }) + ");");
							                            }
							                            if (cleaned > 0 && OnNotificationRequested != null)
							                            {
							                                OnNotificationRequested("幽灵空块清理", string.Format("✓ 已成功物理释放 {0} 处无意义空白块！", cleaned));
							                            }
							                        });
							                    });
							                    return;
							                }

						                // 3.62 零依赖独立单文件 HTML 网页导出并在系统浏览器秒开
						                if (op.Equals("exportDocHtml", StringComparison.OrdinalIgnoreCase))
						                {
						                    string md = JsonHelper.GetString(msg, "markdown");
						                    string title = JsonHelper.GetString(msg, "title");
						                    ThreadPool.QueueUserWorkItem(s =>
						                    {
						                        string outPath = DocStandaloneHtmlExporter.ExportToHtmlAndOpen(md, title);
							                        if (!string.IsNullOrEmpty(outPath) && OnNotificationRequested != null)
							                        {
							                            OnNotificationRequested("独立HTML导出", "✓ 已成功导出单文件网页并在系统默认浏览器秒开！");
							                        }
							                    });
							                    return;
							                }

                // 3.63 外部富文本图文无损复制 (微信/飞书/Word/邮件 CF_HTML)
                if (op.Equals("copyExternalRichText", StringComparison.OrdinalIgnoreCase) ||
                    op.Equals("copyAsRichText", StringComparison.OrdinalIgnoreCase))
							                {
							                    string md = JsonHelper.GetString(msg, "markdown");
							                    string title = JsonHelper.GetString(msg, "title");
							                    ThreadPool.QueueUserWorkItem(s =>
							                    {
							                        bool ok = DocClipboardExporter.CopyAsExternalRichText(md, title);
							                        if (ok && OnNotificationRequested != null)
							                        {
							                            OnNotificationRequested("外部富文本复制", "✓ 图文富文本已复制！直接粘贴至微信/飞书/Word/邮件即可保真显示。");
							                        }
							                    });
							                    return;
							                }

							                // 3.64 阅读字数与耗时智能预估
							                if (op.Equals("estimateReadingMetrics", StringComparison.OrdinalIgnoreCase))
							                {
							                    string text = JsonHelper.GetString(msg, "text");
							                    var metrics = ReadingProgressEstimator.Estimate(text);
							                    if (_webView != null && _webView.CoreWebView2 != null)
							                    {
							                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onReadingMetricsLoaded) window.onReadingMetricsLoaded(" + JsonHelper.ToJson(new { code = 0, data = metrics }) + ");");
							                    }
							                    return;
							                }

							                // 3.65 知识卡片高保真矢量 SVG 长图生成并在系统查看器秒开
							                if (op.Equals("exportCardSvg", StringComparison.OrdinalIgnoreCase))
							                {
							                    string text = JsonHelper.GetString(msg, "text");
							                    string title = JsonHelper.GetString(msg, "title");
							                    string author = JsonHelper.GetString(msg, "author");
							                    ThreadPool.QueueUserWorkItem(s =>
							                    {
							                        string outPath = CardImageRenderer.ExportCardSvgAndOpen(text, title, author);
							                        if (!string.IsNullOrEmpty(outPath) && OnNotificationRequested != null)
							                        {
							                            OnNotificationRequested("知识卡片分享", "✓ 知识卡片长图已生成并在外部图片查看器秒开！");
							                        }
							                    });
							                    return;
							                }

							                // 3.66 外部 Markdown 库 / Obsidian 目录穿透秒查 (vault: / dir:)
							                if (op.Equals("searchVaultNotes", StringComparison.OrdinalIgnoreCase))
							                {
							                    string kw = JsonHelper.GetString(msg, "keyword");
							                    string vaultDir = JsonHelper.GetString(msg, "path");
							                    if (string.IsNullOrEmpty(vaultDir) || !Directory.Exists(vaultDir))
							                    {
							                        vaultDir = @"D:\思源插件";
							                    }

								                    ThreadPool.QueueUserWorkItem(s =>
								                    {
								                        try
								                        {
								                            var notes = VaultMirrorInspector.ScanVaultDirectory(vaultDir, kw);
								                            Dispatcher.Invoke(() =>
								                            {
								                                if (_webView != null && _webView.CoreWebView2 != null)
								                                {
								                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onVaultNotesLoaded) window.onVaultNotesLoaded(" + JsonHelper.ToJson(new { code = 0, path = vaultDir, data = notes }) + ");");
								                                }
								                            });
									                        }
									                        catch { }
									                    });
									                    return;
									                }

                // 3.66B 外部知识库笔记内容读取 (readVaultNote)
                if (op.Equals("readVaultNote", StringComparison.OrdinalIgnoreCase))
                {
                    string filePath = JsonHelper.GetString(msg, "filePath");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        string content = VaultMirrorInspector.ReadNoteContent(filePath);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onVaultNoteContentLoaded) window.onVaultNoteContentLoaded(" + JsonHelper.ToJson(new { code = 0, filePath = filePath, content = content }) + ");");
                            }
                        });
                    });
                    return;
                }

                // 3.66B2 外部知识库笔记原位写回保存 (saveVaultNote)
                if (op.Equals("saveVaultNote", StringComparison.OrdinalIgnoreCase))
                {
                    string filePath = JsonHelper.GetString(msg, "filePath");
                    string content = JsonHelper.GetString(msg, "content");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        bool ok = VaultMirrorInspector.WriteNoteContent(filePath, content);
                        if (ok) SoundPlayerHelper.PlayFeedback();
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onVaultNoteSaved) window.onVaultNoteSaved(" + JsonHelper.ToJson(new { code = 0, success = ok, filePath = filePath }) + ");");
                            }
                        });
                        if (ok && OnNotificationRequested != null)
                        {
                            OnNotificationRequested("外部知识库 · 原位保存", "✓ 已成功将修改原位保存回外部 Markdown 文件！");
                        }
                    });
                    return;
                }

                // 3.66C2 全景大纲小节原位重命名与层级升降 (renameOutlineHeading)
                if (op.Equals("renameOutlineHeading", StringComparison.OrdinalIgnoreCase))
                {
                    string blockId = JsonHelper.GetString(msg, "blockId");
                    string newTitle = JsonHelper.GetString(msg, "title");
                    string subtype = JsonHelper.GetString(msg, "subtype", "h2").ToLowerInvariant();
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            int lvl = 2;
                            if (subtype.StartsWith("h") && subtype.Length == 2)
                            {
                                int.TryParse(subtype.Substring(1), out lvl);
                            }
                            lvl = Math.Max(1, Math.Min(6, lvl));
                            string hashes = new string('#', lvl);
                            string md = hashes + " " + (newTitle ?? "").Trim();

                            var res = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                            {
                                id = blockId,
                                dataType = "markdown",
                                data = md
                            });
                            bool ok = res != null && res.Success;
                            if (ok)
                            {
                                SoundPlayerHelper.PlayFeedback();
                                OmniNotificationHub.Push("大纲导图 · 小节重构", string.Format("小节已原位重命名为【{0}】(H{1})！", newTitle, lvl), "success", "siyuan", blockId);
                            }
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOutlineHeadingRenamed) window.onOutlineHeadingRenamed(" + JsonHelper.ToJson(new { code = 0, success = ok, blockId = blockId, title = newTitle, subtype = "h" + lvl }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3.66D 零配置首启环境自检与向导探针 (runOnboardingProbe & autoHealHotkeys)
                if (op.Equals("runOnboardingProbe", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            var probe = await FirstRunEnvironmentDoctor.RunFullEnvironmentProbeAsync();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOnboardingProbeLoaded) window.onOnboardingProbeLoaded(" + JsonHelper.ToJson(new { code = 0, data = probe }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                if (op.Equals("autoHealHotkeys", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            int healed = FirstRunEnvironmentDoctor.AutoHealConflictingHotkeys();
                            var probe = await FirstRunEnvironmentDoctor.RunFullEnvironmentProbeAsync();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOnboardingProbeLoaded) window.onOnboardingProbeLoaded(" + JsonHelper.ToJson(new { code = 0, healed = healed, data = probe }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

										                // 3.66C 全景大纲与导图独立大视窗 (Outline & Mindmap Studio)
										                if (op.Equals("getDocOutlineAndMindmap", StringComparison.OrdinalIgnoreCase))
										                {
										                    string dId = JsonHelper.GetString(msg, "docId");
										                    ThreadPool.QueueUserWorkItem(async s =>
										                    {
										                        try
										                        {
										                            string targetId = dId;
										                            string targetTitle = "";

										                            if (string.IsNullOrEmpty(targetId))
										                            {
										                                string sql = "SELECT id, content FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 1";
										                                var rows = await SiYuanClient.SqlAsync(sql);
										                                if (rows != null && rows.Count > 0)
										                                {
										                                    targetId = JsonHelper.GetString(rows[0], "id");
										                                    targetTitle = JsonHelper.GetString(rows[0], "content");
										                                }
										                            }
										                            else
										                            {
										                                string sql = "SELECT id, content FROM blocks WHERE id='" + targetId.Replace("'", "''") + "' LIMIT 1";
										                                var rows = await SiYuanClient.SqlAsync(sql);
										                                if (rows != null && rows.Count > 0)
										                                {
										                                    targetTitle = JsonHelper.GetString(rows[0], "content");
										                                }
										                            }

										                            if (!string.IsNullOrEmpty(targetId))
										                            {
										                                string safeId = targetId.Replace("'", "''");
										                                string hSql = "SELECT b.id, b.content, b.subtype, b.sort FROM blocks b " +
										                                              "WHERE b.root_id = '" + safeId + "' AND b.type = 'h' " +
										                                              "ORDER BY b.sort ASC LIMIT 100";
										                                var hRows = await SiYuanClient.SqlAsync(hSql);
										                                var headings = hRows ?? new List<Dictionary<string, object>>();

										                                Dispatcher.Invoke(() =>
										                                {
										                                    if (_webView != null && _webView.CoreWebView2 != null)
										                                    {
										                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDocOutlineLoaded) window.onDocOutlineLoaded(" + JsonHelper.ToJson(new { code = 0, docId = targetId, docTitle = targetTitle, headings = headings }) + ");");
										                                    }
										                                });
										                            }
										                        }
										                        catch { }
										                    });
										                    return;
										                }

										                // 3.67 全库笔记时序热力图 (heat: / habit:)
								                if (op.Equals("getHabitHeatmap", StringComparison.OrdinalIgnoreCase))
								                {
								                    ThreadPool.QueueUserWorkItem(async s =>
								                    {
								                        try
								                        {
								                            var rep = await HabitHeatmapGenerator.GenerateHeatmapAsync();
								                            Dispatcher.Invoke(() =>
								                            {
								                                if (_webView != null && _webView.CoreWebView2 != null)
								                                {
								                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onHeatmapLoaded) window.onHeatmapLoaded(" + JsonHelper.ToJson(new { code = 0, data = rep }) + ");");
								                                }
								                            });
								                        }
								                        catch { }
								                    });
								                    return;
								                }

								                // 3.68 剪贴板隐私敏感信息脱敏 (c:mask)
								                if (op.Equals("sanitizePrivacyText", StringComparison.OrdinalIgnoreCase))
								                {
								                    string text = JsonHelper.GetString(msg, "text");
								                    int maskedCount = 0;
								                    string sanitized = PrivacySanitizer.SanitizeText(text, out maskedCount);
									                    if (_webView != null && _webView.CoreWebView2 != null)
									                    {
									                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onPrivacySanitized) window.onPrivacySanitized(" + JsonHelper.ToJson(new { code = 0, text = sanitized, count = maskedCount }) + ");");
									                    }
									                    if (maskedCount > 0 && OnNotificationRequested != null)
									                    {
									                        OnNotificationRequested("隐私脱敏盾牌", string.Format("✓ 已成功脱敏 {0} 处敏感凭据/信息！", maskedCount));
									                    }
									                    return;
									                }

									                // 3.69 网站收藏中枢检索与管理 (fav: / url:)
									                if (op.Equals("searchWebBookmarks", StringComparison.OrdinalIgnoreCase))
									                {
									                    string kw = JsonHelper.GetString(msg, "keyword");
									                    ThreadPool.QueueUserWorkItem(async s =>
									                    {
									                        try
									                        {
									                            var all = await WebBookmarkHub.LoadBookmarksAsync();
									                            var filtered = WebBookmarkHub.Search(all, kw);
									                            Dispatcher.Invoke(() =>
									                            {
									                                if (_webView != null && _webView.CoreWebView2 != null)
									                                {
									                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWebBookmarksLoaded) window.onWebBookmarksLoaded(" + JsonHelper.ToJson(new { code = 0, data = filtered }) + ");");
									                                }
									                            });
									                        }
									                        catch { }
									                    });
									                    return;
									                }

									                if (op.Equals("saveWebBookmark", StringComparison.OrdinalIgnoreCase))
									                {
									                    var entry = new WebBookmarkEntry
									                    {
									                        Title = JsonHelper.GetString(msg, "title"),
									                        Url = JsonHelper.GetString(msg, "url"),
									                        Category = JsonHelper.GetString(msg, "category", "常用"),
									                        Tags = JsonHelper.GetString(msg, "tags"),
									                        Shortcut = JsonHelper.GetString(msg, "shortcut"),
									                        Notes = JsonHelper.GetString(msg, "notes")
									                    };
									                    ThreadPool.QueueUserWorkItem(async s =>
									                    {
									                        try
									                        {
									                            bool ok = await WebBookmarkHub.SaveBookmarkAsync(entry);
									                            Dispatcher.Invoke(() =>
									                            {
										                                if (_webView != null && _webView.CoreWebView2 != null)
										                                {
										                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWebBookmarkSaved) window.onWebBookmarkSaved(" + JsonHelper.ToJson(new { code = 0, success = ok, entry = entry }) + ");");
										                                }
									                            });
									                            if (ok && OnNotificationRequested != null)
									                            {
									                                OnNotificationRequested("网站收藏中枢", "✓ 网址【" + entry.Title + "】已沉淀至思源专属文档与本地缓存！");
									                            }
									                        }
									                        catch { }
									                    });
									                    return;
									                }

									                if (op.Equals("deleteWebBookmark", StringComparison.OrdinalIgnoreCase))
									                {
									                    string bId = JsonHelper.GetString(msg, "id");
									                    ThreadPool.QueueUserWorkItem(async s =>
									                    {
									                        try
									                        {
									                            bool ok = await WebBookmarkHub.DeleteBookmarkAsync(bId);
									                            Dispatcher.Invoke(() =>
									                            {
									                                if (_webView != null && _webView.CoreWebView2 != null)
									                                {
									                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWebBookmarkDeleted) window.onWebBookmarkDeleted(" + JsonHelper.ToJson(new { code = 0, success = ok, id = bId }) + ");");
									                                }
									                            });
									                        }
									                        catch { }
									                    });
									                    return;
									                }

									                if (op.Equals("captureActiveBrowserUrl", StringComparison.OrdinalIgnoreCase))
									                {
									                    ThreadPool.QueueUserWorkItem(s =>
									                    {
									                        try
									                        {
									                            var snap = ContextPerception.CaptureForegroundContext(LastExternalForegroundHwnd);
									                            Dispatcher.Invoke(() =>
									                            {
									                                if (_webView != null && _webView.CoreWebView2 != null)
									                                {
									                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onActiveBrowserUrlCaptured) window.onActiveBrowserUrlCaptured(" + JsonHelper.ToJson(new { code = 0, title = snap.CleanTitle, url = snap.Url, app = snap.AppDisplayName }) + ");");
									                                }
									                            });
									                        }
									                        catch { }
									                    });
									                    return;
									                }

									                if (op.Equals("openWebBookmark", StringComparison.OrdinalIgnoreCase))
									                {
									                    string targetUrl = JsonHelper.GetString(msg, "url");
									                    if (!string.IsNullOrEmpty(targetUrl))
									                    {
									                        try
									                        {
									                            Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
									                            Hide();
									                        }
									                        catch { }
									                    }
									                    return;
									                }

									                // 3.70 本地文件夹收藏与对话框穿透秒跳 (fold: / dir: / f:)
									                if (op.Equals("searchFolderBookmarks", StringComparison.OrdinalIgnoreCase))
									                {
									                    string kw = JsonHelper.GetString(msg, "keyword");
									                    ThreadPool.QueueUserWorkItem(async s =>
									                    {
									                        try
									                        {
									                            var all = await FolderBookmarkHub.LoadFoldersAsync();
									                            var filtered = FolderBookmarkHub.Search(all, kw);
									                            Dispatcher.Invoke(() =>
									                            {
									                                if (_webView != null && _webView.CoreWebView2 != null)
									                                {
									                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onFolderBookmarksLoaded) window.onFolderBookmarksLoaded(" + JsonHelper.ToJson(new { code = 0, data = filtered }) + ");");
									                                }
									                            });
									                        }
									                        catch { }
									                    });
									                    return;
									                }

									                if (op.Equals("saveFolderBookmark", StringComparison.OrdinalIgnoreCase))
									                {
									                    var entry = new FolderBookmarkEntry
									                    {
									                        Path = JsonHelper.GetString(msg, "path"),
									                        Alias = JsonHelper.GetString(msg, "alias"),
									                        Shortcut = JsonHelper.GetString(msg, "shortcut"),
									                        Tags = JsonHelper.GetString(msg, "tags"),
									                        SiyuanDocId = JsonHelper.GetString(msg, "siyuanDocId"),
									                        SiyuanDocTitle = JsonHelper.GetString(msg, "siyuanDocTitle")
									                    };
									                    ThreadPool.QueueUserWorkItem(async s =>
									                    {
									                        try
									                        {
									                            bool ok = await FolderBookmarkHub.SaveFolderAsync(entry);
									                            Dispatcher.Invoke(() =>
									                            {
										                                if (_webView != null && _webView.CoreWebView2 != null)
										                                {
										                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onFolderBookmarkSaved) window.onFolderBookmarkSaved(" + JsonHelper.ToJson(new { code = 0, success = ok, entry = entry }) + ");");
										                                }
									                            });
									                            if (ok && OnNotificationRequested != null)
									                            {
									                                OnNotificationRequested("文件夹导航中枢", "✓ 目录【" + entry.Alias + "】已沉淀至思源导航文档与本地缓存！");
									                            }
									                        }
									                        catch { }
									                    });
									                    return;
									                }

									                if (op.Equals("deleteFolderBookmark", StringComparison.OrdinalIgnoreCase))
									                {
									                    string fId = JsonHelper.GetString(msg, "id");
									                    ThreadPool.QueueUserWorkItem(async s =>
									                    {
									                        try
									                        {
									                            bool ok = await FolderBookmarkHub.DeleteFolderAsync(fId);
									                            Dispatcher.Invoke(() =>
									                            {
									                                if (_webView != null && _webView.CoreWebView2 != null)
									                                {
									                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onFolderBookmarkDeleted) window.onFolderBookmarkDeleted(" + JsonHelper.ToJson(new { code = 0, success = ok, id = fId }) + ");");
									                                }
									                            });
									                        }
									                        catch { }
									                    });
									                    return;
									                }

									                if (op.Equals("captureActiveExplorerFolder", StringComparison.OrdinalIgnoreCase))
									                {
									                    ThreadPool.QueueUserWorkItem(s =>
									                    {
									                        try
									                        {
									                            string folder = ContextPerception.TryExtractExplorerPath(LastExternalForegroundHwnd);
									                            Dispatcher.Invoke(() =>
									                            {
									                                if (_webView != null && _webView.CoreWebView2 != null)
									                                {
									                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onActiveExplorerFolderCaptured) window.onActiveExplorerFolderCaptured(" + JsonHelper.ToJson(new { code = 0, path = folder, alias = Path.GetFileName(folder) }) + ");");
									                                }
									                            });
									                        }
									                        catch { }
									                    });
									                    return;
									                }

											                if (op.Equals("navigateFolderBookmark", StringComparison.OrdinalIgnoreCase))
											                {
											                    string targetPath = JsonHelper.GetString(msg, "path");
											                    if (!string.IsNullOrEmpty(targetPath))
											                    {
											                        Hide();
											                        FolderBookmarkHub.NavigateToFolder(targetPath, LastExternalForegroundHwnd);
											                    }
											                    return;
											                }

											                if (op.Equals("pickLocalFolder", StringComparison.OrdinalIgnoreCase))
											                {
											                    string initialDir = JsonHelper.GetString(msg, "initialDir", @"D:\");
											                    string targetInputId = JsonHelper.GetString(msg, "targetInputId", "");
											                    string desc = JsonHelper.GetString(msg, "description", "请选择本地文件夹目录");
											                    Dispatcher.BeginInvoke(new Action(() =>
											                    {
											                        _suppressAutoHide = true;
											                        try
											                        {
											                            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
											                            {
											                                fbd.Description = desc;
											                                fbd.ShowNewFolderButton = true;
											                                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
											                                {
											                                    fbd.SelectedPath = initialDir;
											                                }
											                                var dlgRes = fbd.ShowDialog();
											                                if (dlgRes == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(fbd.SelectedPath))
											                                {
											                                    string picked = fbd.SelectedPath;
											                                    string alias = Path.GetFileName(picked.TrimEnd('\\', '/'));
											                                    if (string.IsNullOrEmpty(alias)) alias = picked;
											                                    if (_webView != null && _webView.CoreWebView2 != null)
											                                    {
											                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onLocalFolderPicked) window.onLocalFolderPicked(" + JsonHelper.ToJson(new { code = 0, path = picked, alias = alias, targetInputId = targetInputId }) + ");");
											                                    }
											                                }
											                            }
											                        }
											                        catch { }
											                        finally
											                        {
											                            _suppressAutoHide = false;
											                            try { Activate(); } catch { }
											                        }
											                    }));
											                    return;
											                }

										                // 3.71 思源常用配置预设与大模型切换中枢 (cfg: / s3: / ai:)
										                if (op.Equals("searchProfiles", StringComparison.OrdinalIgnoreCase))
										                {
										                    string kw = JsonHelper.GetString(msg, "keyword");
										                    string type = JsonHelper.GetString(msg, "type", "all");
										                    ThreadPool.QueueUserWorkItem(async s =>
										                    {
										                        try
										                        {
										                            var all = await SiYuanProfileHub.LoadProfilesAsync(type);
										                            var filtered = SiYuanProfileHub.Search(all, kw, type);
										                            Dispatcher.Invoke(() =>
										                            {
										                                if (_webView != null && _webView.CoreWebView2 != null)
										                                {
										                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onProfilesLoaded) window.onProfilesLoaded(" + JsonHelper.ToJson(new { code = 0, data = filtered }) + ");");
										                                }
										                            });
										                        }
										                        catch { }
										                    });
										                    return;
										                }

										                if (op.Equals("saveProfile", StringComparison.OrdinalIgnoreCase))
										                {
										                    var entry = new SiYuanProfileEntry
										                    {
										                        Name = JsonHelper.GetString(msg, "name"),
										                        Type = JsonHelper.GetString(msg, "type", "s3"),
										                        Description = JsonHelper.GetString(msg, "description")
										                    };
										                    var configDict = JsonHelper.GetDict(msg, "configData");
										                    if (configDict != null) entry.ConfigData = configDict;

										                    ThreadPool.QueueUserWorkItem(async s =>
										                    {
										                        try
										                        {
										                            bool ok = await SiYuanProfileHub.SaveProfileAsync(entry);
										                            Dispatcher.Invoke(() =>
										                            {
										                                if (_webView != null && _webView.CoreWebView2 != null)
										                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onProfileSaved) window.onProfileSaved(" + JsonHelper.ToJson(new { code = 0, success = ok, localOnly = entry.IsLocalOnly }) + ");");
										                                }
										                            });
										                            if (ok && OnNotificationRequested != null)
										                            {
                                                OnNotificationRequested("配置预设中枢", entry.IsLocalOnly
                                                    ? "✓ 预设【" + entry.Name + "】仅安全保存在本机；思源暂不可达，尚未写入思源文档。"
                                                    : "✓ 预设【" + entry.Name + "】已安全脱敏沉淀至思源文档与本地缓存！");
										                            }
										                        }
										                        catch { }
										                    });
										                    return;
										                }

										                if (op.Equals("deleteProfile", StringComparison.OrdinalIgnoreCase))
										                {
										                    string pid = JsonHelper.GetString(msg, "id");
										                    ThreadPool.QueueUserWorkItem(async s =>
										                    {
										                        try
										                        {
										                            bool ok = await SiYuanProfileHub.DeleteProfileAsync(pid);
										                            Dispatcher.Invoke(() =>
										                            {
										                                if (_webView != null && _webView.CoreWebView2 != null)
										                                {
										                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onProfileDeleted) window.onProfileDeleted(" + JsonHelper.ToJson(new { code = 0, success = ok, id = pid }) + ");");
										                                }
										                            });
										                        }
										                        catch { }
										                    });
										                    return;
										                }

                if (op.Equals("captureCurrentProfile", StringComparison.OrdinalIgnoreCase))
                {
                    string cType = JsonHelper.GetString(msg, "type", "s3");
                    string cName = JsonHelper.GetString(msg, "name");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var captured = SiYuanProfileHub.CaptureCurrentLiveProfile(cType, cName);
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onProfileCaptured) window.onProfileCaptured(" + JsonHelper.ToJson(new { code = 0, data = captured }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                if (op.Equals("applyProfile", StringComparison.OrdinalIgnoreCase))
                {
                    string pid = JsonHelper.GetString(msg, "id");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            var applyRes = await SiYuanProfileHub.ApplyProfileToLiveAsync(pid);
                            if (applyRes.Success) SoundPlayerHelper.PlayFeedback();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onProfileApplied) window.onProfileApplied(" + JsonHelper.ToJson(new { code = 0, data = applyRes }) + ");");
                                }
                            });
                            if (applyRes.Success && OnNotificationRequested != null)
                            {
                                OnNotificationRequested("配置预设 · 热切换生效", "✓ " + applyRes.AppliedSummary);
                            }
                        }
                        catch { }
                    });
                    return;
                }

										                // 3.72 全景系统与应用通知事件流中枢 (notice: / notif:)
										                if (op.Equals("getNotificationHistory", StringComparison.OrdinalIgnoreCase))
										                {
										                    string kw = JsonHelper.GetString(msg, "keyword");
										                    string lvl = JsonHelper.GetString(msg, "level", "all");
										                    string src = JsonHelper.GetString(msg, "source", "all");
										                    ThreadPool.QueueUserWorkItem(s =>
										                    {
										                        try
										                        {
										                            var list = OmniNotificationHub.GetHistory(lvl, src, kw);
										                            Dispatcher.Invoke(() =>
										                            {
										                                if (_webView != null && _webView.CoreWebView2 != null)
										                                {
										                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onNotificationsLoaded) window.onNotificationsLoaded(" + JsonHelper.ToJson(new { code = 0, count = list.Count, data = list }) + ");");
										                                }
										                            });
										                        }
										                        catch { }
										                    });
										                    return;
										                }

										                if (op.Equals("clearNotificationHistory", StringComparison.OrdinalIgnoreCase))
										                {
										                    OmniNotificationHub.Clear();
										                    if (_webView != null && _webView.CoreWebView2 != null)
										                    {
										                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onNotificationsCleared) window.onNotificationsCleared();");
										                    }
										                    return;
										                }

										                if (op.Equals("dumpNotificationJournal", StringComparison.OrdinalIgnoreCase))
										                {
										                    ThreadPool.QueueUserWorkItem(s =>
										                    {
										                        try
										                        {
										                            var list = OmniNotificationHub.GetHistory("all", "all", "");
										                            string md = OmniNotificationHub.BuildJournalMarkdown(list);
										                            var ctx = new DesktopStepContext("SYS", md);
										                            ctx.SetVarValue("来源应用", "思源外骨骼 · 通知中枢");
										                            SmartCaptureMain.Exec(ctx);
										                            if (OnNotificationRequested != null)
										                            {
										                                OnNotificationRequested("通知中枢 · 日报归流", string.Format("✓ 已成功将今日 {0} 条通知事件归流至今日日记！", list.Count));
										                            }
										                        }
										                        catch { }
										                    });
										                    return;
										                }

				                // 3.5 全库文档毫秒级全局检索 (d: / doc: 语法，突破内存前20篇限制)
                if (op.Equals("searchDocs", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeLike = (kw ?? "").Replace("'", "''");
                            string sql;
                            if (!string.IsNullOrEmpty(safeLike))
                            {
                                sql = "SELECT b.id, b.content, b.hpath, b.box, b.updated FROM blocks b " +
                                      "WHERE b.type = 'd' AND b.content != '' " +
                                      "AND (b.content LIKE '%" + safeLike + "%' OR b.hpath LIKE '%" + safeLike + "%') " +
                                      "ORDER BY (CASE " +
                                      "  WHEN b.content = '" + safeLike + "' THEN 100 " +
                                      "  WHEN b.content LIKE '" + safeLike + "%' THEN 70 " +
                                      "  WHEN b.content LIKE '%" + safeLike + "%' THEN 40 " +
                                      "  ELSE 10 " +
                                      "END) DESC, b.updated DESC LIMIT 40";
                            }
                            else
                            {
                                sql = "SELECT b.id, b.content, b.hpath, b.box, b.updated FROM blocks b " +
                                      "WHERE b.type = 'd' AND b.content != '' " +
                                      "ORDER BY b.updated DESC LIMIT 30";
                            }

                            var rows = await SiYuanClient.SqlAsync(sql);
                            var nbMap = await GetNotebookMapAsync();

                            var docsList = new List<Dictionary<string, object>>();
                            if (rows != null)
                            {
                                foreach (var r in rows)
                                {
                                    string boxId = JsonHelper.GetString(r, "box");
                                    string nbName = "个人笔记本";
                                    if (!string.IsNullOrEmpty(boxId) && nbMap != null && nbMap.ContainsKey(boxId))
                                    {
                                        nbName = nbMap[boxId];
                                    }

                                    var item = new Dictionary<string, object>();
                                    item["id"] = JsonHelper.GetString(r, "id");
                                    item["content"] = JsonHelper.GetString(r, "content");
                                    item["hpath"] = JsonHelper.GetString(r, "hpath");
                                    item["box"] = boxId;
                                    item["notebookName"] = nbName;
                                    item["updated"] = JsonHelper.GetString(r, "updated");
                                    docsList.Add(item);
                                }
                            }

                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onDocSearchResults) window.onDocSearchResults(" + JsonHelper.ToJson(new { code = 0, data = docsList }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3.52 块级原位编辑保存 (Update Block Inline)
                if (op.Equals("updateBlockContent", StringComparison.OrdinalIgnoreCase))
                {
                    string bId = JsonHelper.GetString(msg, "id");
                    string newContent = JsonHelper.GetString(msg, "markdown");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            var res = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                            {
                                id = bId,
                                dataType = "markdown",
                                data = newContent
                            });
                            bool ok = res != null && res.Success;
                            if (ok) SoundPlayerHelper.PlayFeedback();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.onBlockUpdatedInline) window.onBlockUpdatedInline({{ success: {0}, id: '{1}' }});", ok ? "true" : "false", SafeContext.EscapeJson(bId)));
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3.55 全库标签穿透秒搜 (tag: / #标签 语法)
                if (op.Equals("searchTags", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeTag = (kw ?? "").Trim().TrimStart('#', '＃').Replace("'", "''");
                            string sql;
                            if (!string.IsNullOrEmpty(safeTag))
                            {
                                sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.type, b.updated FROM blocks b " +
                                      "WHERE (b.content LIKE '%#" + safeTag + "%' OR b.ial LIKE '%#" + safeTag + "%') " +
                                      "ORDER BY b.updated DESC LIMIT 40";
                            }
                            else
                            {
                                sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.type, b.updated FROM blocks b " +
                                      "WHERE (b.content LIKE '%#%#' OR b.ial LIKE '%#%#') " +
                                      "ORDER BY b.updated DESC LIMIT 30";
                            }

                            var rows = await SiYuanClient.SqlAsync(sql);
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTagSearchResults) window.onTagSearchResults(" + JsonHelper.ToJson(new { code = 0, data = rows, tag = safeTag }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 3.6 零前缀全知搜 (Zero-Prefix Omnisearch 并发穿透文档、正文块与附件资产)
                if (op.Equals("omnisearch", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(kw) || kw.Length < 2) return;
                            string safeLike = kw.Replace("'", "''");

                            // 1. 文档穿透检索 (前4篇)
                            string docSql = "SELECT b.id, b.content, b.hpath, b.box, b.updated FROM blocks b " +
                                           "WHERE b.type = 'd' AND b.content != '' " +
                                           "AND (b.content LIKE '%" + safeLike + "%' OR b.hpath LIKE '%" + safeLike + "%') " +
                                           "ORDER BY b.updated DESC LIMIT 4";
                            var docRows = await SiYuanClient.SqlAsync(docSql);

                            // 2. 正文块穿透检索 (前6块)
                            string blockSql = "SELECT b.id, b.root_id, b.content, b.hpath, b.type, b.updated, doc.content AS doc_title FROM blocks b " +
                                              "LEFT JOIN blocks doc ON doc.id = b.root_id " +
                                              "WHERE b.content LIKE '%" + safeLike + "%' AND b.type NOT IN ('d') " +
                                              "ORDER BY b.updated DESC LIMIT 6";
                            var blockRows = await SiYuanClient.SqlAsync(blockSql);

                            // 3. 关联附件穿透检索 (前3份)
                            string assetSql = "SELECT b.id, b.root_id, b.content, doc.content AS doc_title FROM blocks b " +
                                              "LEFT JOIN blocks doc ON doc.id = b.root_id " +
                                              "WHERE b.content LIKE '%assets/%' AND b.content LIKE '%" + safeLike + "%' " +
                                              "ORDER BY b.updated DESC LIMIT 3";
                            var assetRows = await SiYuanClient.SqlAsync(assetSql);

                            var nbMap = await GetNotebookMapAsync();

                            var docsList = new List<Dictionary<string, object>>();
                            if (docRows != null)
                            {
                                foreach (var r in docRows)
                                {
                                    string boxId = JsonHelper.GetString(r, "box");
                                    string nbName = (nbMap != null && nbMap.ContainsKey(boxId)) ? nbMap[boxId] : "个人笔记";
                                    var it = new Dictionary<string, object>();
                                    it["id"] = JsonHelper.GetString(r, "id");
                                    it["content"] = JsonHelper.GetString(r, "content");
                                    it["hpath"] = JsonHelper.GetString(r, "hpath");
                                    it["notebookName"] = nbName;
                                    docsList.Add(it);
                                }
                            }

                            var assetsList = new List<Dictionary<string, object>>();
                            if (assetRows != null)
                            {
                                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                string wsAssets = SiYuanClient.WorkspaceAssetsDir;
                                foreach (var ar in assetRows)
                                {
                                    string content = JsonHelper.GetString(ar, "content");
                                    var mAsset = System.Text.RegularExpressions.Regex.Matches(content, @"assets/([a-zA-Z0-9_\-\.\u4e00-\u9fa5]+\.[a-zA-Z0-9]+)");
                                    foreach (System.Text.RegularExpressions.Match m in mAsset)
                                    {
                                        string fn = m.Groups[1].Value;
                                        string rel = m.Value;
                                        if (!seen.Contains(rel))
                                        {
                                            seen.Add(rel);
                                            string full = Path.Combine(wsAssets, fn);
                                            string sz = "未知大小";
                                            if (File.Exists(full))
                                            {
                                                long len = new FileInfo(full).Length;
                                                sz = len >= 1048576 ? (len / 1048576.0).ToString("F1") + " MB" : (len / 1024.0).ToString("F0") + " KB";
                                            }
                                            var it = new Dictionary<string, object>();
                                            it["id"] = JsonHelper.GetString(ar, "id");
                                            it["fileName"] = fn;
                                            it["assetRel"] = rel;
                                            it["fullPath"] = full;
                                            it["ext"] = Path.GetExtension(fn).ToLowerInvariant();
                                            it["size"] = sz;
                                            it["docTitle"] = JsonHelper.GetString(ar, "doc_title");
                                            it["content"] = content;
                                            assetsList.Add(it);
                                        }
                                    }
                                }
                            }

                            var payload = new
                            {
                                keyword = kw,
                                docs = docsList,
                                blocks = blockRows,
                                assets = assetsList
                            };

                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOmnisearchResults) window.onOmnisearchResults(" + JsonHelper.ToJson(payload) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 4. 原生安全复制到剪贴板
                if (op.Equals("copyToClipboard", StringComparison.OrdinalIgnoreCase))
                {
                    string textToCopy = JsonHelper.GetString(msg, "text");
                    NativeClipboard.SetTextSafe(textToCopy);
                    SoundPlayerHelper.PlayFeedback();
                    return;
                }

                // 5. 原生穿透光标级快速注入双链
                if (op.Equals("pasteAtCaret", StringComparison.OrdinalIgnoreCase))
                {
                    string textToPaste = JsonHelper.GetString(msg, "text");
                    Hide();
                    NativeClipboard.PasteTextAtCaret(_previousForegroundHwnd, textToPaste);
                    SoundPlayerHelper.PlayFeedback();
                    return;
                }

                // 6. 执行核心指令
                if (op.Equals("executeCommand", StringComparison.OrdinalIgnoreCase))
                {
                    string cmd = JsonHelper.GetString(msg, "cmd").Trim();
                    string text = JsonHelper.GetString(msg, "text").Trim();
                    bool isSmartCapture = cmd.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
                                          cmd.Equals("d7f12ada-92bc-4713-9f76-d08e2ff55fce", StringComparison.OrdinalIgnoreCase);
                    bool keepOpen = JsonHelper.GetBool(msg, "keepOpen", isSmartCapture && !string.IsNullOrEmpty(text));
                    if (!keepOpen) Hide();
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        if (cmd.Equals("syfsrs", StringComparison.OrdinalIgnoreCase) || cmd.Equals("fsrs", StringComparison.OrdinalIgnoreCase))
                        {
                            XiaoLv.Desktop.Program.StartFsrsReview();
                            return;
                        }
                        if (cmd.Equals("sypen", StringComparison.OrdinalIgnoreCase))
                        {
                            XiaoLv.Desktop.Program.OpenPenCapsule(text);
                            return;
                        }
                        if (isSmartCapture && !string.IsNullOrEmpty(text) && keepOpen)
                        {
                            ThreadPool.QueueUserWorkItem(s =>
                            {
                                try
                                {
                                    var ctx = new DesktopStepContext("SYS", text);
                                    ctx.OnNotificationRequested += (t, m) =>
                                    {
                                        if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                                    };
                                    SmartCaptureMain.Exec(ctx);
                                    bool ok = SafeContext.GetBool(ctx, "是否成功", true);
                                    string fb = SafeContext.GetString(ctx, "通知反馈", "✓ 已成功分流写入思源！");
                                    if (ok) SoundPlayerHelper.PlayFeedback();
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (_webView != null && _webView.CoreWebView2 != null)
                                        {
                                            var payload = new Dictionary<string, object>();
                                            payload["success"] = ok;
                                            payload["blockId"] = "";
                                            payload["text"] = text;
                                            payload["feedback"] = fb;
                                            payload["sourceLabel"] = "多态速记与智能分流";
                                            payload["time"] = DateTime.Now.ToString("HH:mm");
                                            _webView.CoreWebView2.ExecuteScriptAsync("if(window.onQuickNoteCompleted) window.onQuickNoteCompleted(" + JsonHelper.ToJson(payload) + ");");
                                        }
                                    });
                                }
                                catch { }
                            });
                            return;
                        }
                        if (OnSubActionRequested != null)
                        {
                            OnSubActionRequested(cmd, text);
                        }
                    }
                    return;
                }

                // 6.2 专用于窗内随身划笔工作台 (pen:) 的无阻断 1~8 键动作执行与结果回执
                if (op.Equals("executePenWorkbenchAction", StringComparison.OrdinalIgnoreCase))
                {
                    string penCode = JsonHelper.GetString(msg, "code", "1").Trim();
                    string penText = JsonHelper.GetString(msg, "text").Trim();
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var ctx = new DesktopStepContext(penCode, penText);
                            ctx.OnNotificationRequested += (t, m) =>
                            {
                                if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                            };
                            ContextPenMain.Exec(ctx);
                            bool ok = SafeContext.GetBool(ctx, "是否成功", true);
                            string fb = SafeContext.GetString(ctx, "通知反馈", "✓ 随身划笔指令已执行！");
                            if (ok) SoundPlayerHelper.PlayFeedback();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    var payload = new Dictionary<string, object>();
                                    payload["success"] = ok;
                                    payload["code"] = penCode;
                                    payload["text"] = penText;
                                    payload["feedback"] = fb;
                                    payload["time"] = DateTime.Now.ToString("HH:mm");
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onPenWorkbenchActionCompleted) window.onPenWorkbenchActionCompleted(" + JsonHelper.ToJson(payload) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 6.5 专用于总控抽屉与 12 大工作台的无阻断追加日记 (支持 appendToDailyNote / recordMiniInput / quickNote 免切屏)
                if (op.Equals("appendToDailyNote", StringComparison.OrdinalIgnoreCase) ||
                    op.Equals("recordMiniInput", StringComparison.OrdinalIgnoreCase) ||
                    op.Equals("quickNote", StringComparison.OrdinalIgnoreCase))
                {
                    string text = JsonHelper.GetString(msg, "text").Trim();
                    string sourceLabel = JsonHelper.GetString(msg, "sourceLabel", "速记");
                    bool keepOpen = JsonHelper.GetBool(msg, "keepOpen", true);
                    if (!keepOpen) Hide();

                    if (!string.IsNullOrEmpty(text))
                    {
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var conn = SiYuanClient.GetConnectionInfo(false);
                                string createdBlockId;
                                bool ok = SmartCaptureMain.AppendToDailyNoteWithId(conn.Url, conn.Token, text, out createdBlockId);
                                if (ok)
                                {
                                    SoundPlayerHelper.PlayFeedback();
                                    if (OnNotificationRequested != null)
                                    {
                                        OnNotificationRequested("思源外骨骼", "✓ 已成功写入今日日记！");
                                    }
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (_webView != null && _webView.CoreWebView2 != null)
                                        {
                                            var payload = new Dictionary<string, object>();
                                            payload["success"] = true;
                                            payload["blockId"] = createdBlockId ?? "";
                                            payload["text"] = text;
                                            payload["sourceLabel"] = sourceLabel;
                                            payload["time"] = DateTime.Now.ToString("HH:mm");
                                            _webView.CoreWebView2.ExecuteScriptAsync("if(window.onQuickNoteCompleted) window.onQuickNoteCompleted(" + JsonHelper.ToJson(payload) + ");");
                                        }
                                    });
                                }
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 7. 插件直达
                if (op.Equals("launchPlugin", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = JsonHelper.GetString(msg, "spec");
                    if (string.IsNullOrEmpty(spec)) spec = JsonHelper.GetString(msg, "value");
                    if (!string.IsNullOrEmpty(spec))
                    {
                        Hide();
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var conn = SiYuanClient.GetConnectionInfo(false);
                                SuperPanelMain.LaunchPluginInSiYuan(null, conn.Url, conn.Token, spec);
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 8. 打开文档
                if (op.Equals("openDoc", StringComparison.OrdinalIgnoreCase))
                {
                    string docId = JsonHelper.GetString(msg, "docId");
                    if (!string.IsNullOrEmpty(docId))
                    {
                        Hide();
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var conn = SiYuanClient.GetConnectionInfo(false);
                                LvSendCommand.Send(conn.Url, conn.Token, "doc.open", JsonHelper.ToJson(new { id = docId }), "siyuan-speed-switch", "fast");
                                SuperPanelMain.ActivateSiYuanWindow();
                                Process.Start(new ProcessStartInfo("siyuan://blocks/" + docId) { UseShellExecute = true });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 9. 打开块
                if (op.Equals("openBlock", StringComparison.OrdinalIgnoreCase))
                {
                    string blockId = JsonHelper.GetString(msg, "blockId");
                    if (!string.IsNullOrEmpty(blockId))
                    {
                        Hide();
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var conn = SiYuanClient.GetConnectionInfo(false);
                                LvSendCommand.Send(conn.Url, conn.Token, "doc.open", JsonHelper.ToJson(new { id = blockId }), "siyuan-speed-switch", "fast");
                                SuperPanelMain.ActivateSiYuanWindow();
                                Process.Start(new ProcessStartInfo("siyuan://blocks/" + blockId) { UseShellExecute = true });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 10. 属性视图数据库直投
                if (op.Equals("insertAvRow", StringComparison.OrdinalIgnoreCase))
                {
                    string avId = JsonHelper.GetString(msg, "avId");
                    string blockId = JsonHelper.GetString(msg, "blockId");
                    string avName = JsonHelper.GetString(msg, "avName", "属性视图");
                    string rowContent = JsonHelper.GetString(msg, "title");
                    bool keepOpen = JsonHelper.GetBool(msg, "keepOpen", true);
                    if (!keepOpen) Hide();
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var conn = SiYuanClient.GetConnectionInfo(false);
                            var res = WebToAvDatabaseMain.InsertAvRow(conn.Url, conn.Token, avId, rowContent);
                            if (res.Success)
                            {
                                SoundPlayerHelper.PlayFeedback();
                                if (OnNotificationRequested != null)
                                {
                                    OnNotificationRequested("思源外骨骼 · 数据库直投", "✓ 已成功录入至属性视图【" + avName + "】！");
                                }
                            }
                            else
                            {
                                if (OnNotificationRequested != null)
                                {
                                    OnNotificationRequested("思源外骨骼", "⚠️ 数据库录入失败: " + res.Error);
                                }
                            }
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onAvRowInserted) window.onAvRowInserted(" + JsonHelper.ToJson(new { success = res.Success, avId = avId, blockId = blockId, avName = avName, title = rowContent, error = res.Error }) + ");");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            if (OnNotificationRequested != null)
                            {
                                OnNotificationRequested("思源外骨骼", "⚠️ 数据库录入异常: " + ex.Message);
                            }
                        }
                    });
                    return;
                }

                // 11. 块上下文与面包屑实时查询（双栏即时预览抽屉，带 0ms 内存高速缓存）
                if (op.Equals("getBlockContext", StringComparison.OrdinalIgnoreCase))
                {
                    string targetId = JsonHelper.GetString(msg, "id");
                    if (string.IsNullOrEmpty(targetId)) targetId = JsonHelper.GetString(msg, "blockId");
                    if (!string.IsNullOrEmpty(targetId))
                    {
                        // 优先命中 0ms 内存缓存
                        object cached;
                        lock (_blockContextCache)
                        {
                            if (_blockContextCache.TryGetValue(targetId, out cached))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onBlockContextLoaded) window.onBlockContextLoaded(" + JsonHelper.ToJson(cached) + ");");
                                    }
                                });
                                return;
                            }
                        }

                        ThreadPool.QueueUserWorkItem(async s =>
                        {
                            try
                            {
                                // A. 面包屑查询
                                var bcRes = await SiYuanClient.PostAsync("/api/block/getBlockBreadcrumb", new { id = targetId });
                                object breadcrumbs = bcRes.Success ? bcRes.Data : null;

                                // B. 块详情与上下文
                                string safeId = targetId.Replace("'", "''");
                                string sql = "SELECT b.id, b.parent_id, b.root_id, b.hash, b.content, b.markdown, b.ial, b.type, b.subtype, b.updated, b.hpath, doc.content AS doc_title " +
                                             "FROM blocks b " +
                                             "LEFT JOIN blocks doc ON doc.id = b.root_id " +
                                             "WHERE b.id = '" + safeId + "' LIMIT 1";
                                var rows = await SiYuanClient.SqlAsync(sql);
                                var blockInfo = (rows != null && rows.Count > 0) ? rows[0] : null;
                                if (blockInfo != null)
                                {
                                    _currentInspectedDocId = JsonHelper.GetString(blockInfo, "root_id");
                                    _currentInspectedDocTitle = JsonHelper.GetString(blockInfo, "doc_title");
                                }

                                // C. 邻近兄弟块上下文（前后各2块）
                                List<Dictionary<string, object>> siblings = null;
                                if (blockInfo != null)
                                {
                                    string parentId = JsonHelper.GetString(blockInfo, "parent_id");
                                    if (!string.IsNullOrEmpty(parentId))
                                    {
                                        string safeParent = parentId.Replace("'", "''");
                                        string sibSql = "SELECT id, content, type FROM blocks WHERE parent_id = '" + safeParent + "' ORDER BY sort ASC LIMIT 8";
                                        siblings = await SiYuanClient.SqlAsync(sibSql);
                                    }
                                }

                                // D. 反向引用探针 (查询引用此块的其他文档或段落)
                                List<Dictionary<string, object>> backlinks = null;
                                try
                                {
                                    string refSql = "SELECT b.id, b.root_id, b.content, doc.content AS doc_title " +
                                                    "FROM refs r " +
                                                    "JOIN blocks b ON b.id = r.block_id " +
                                                    "LEFT JOIN blocks doc ON doc.id = b.root_id " +
                                                    "WHERE r.def_block_id = '" + safeId + "' LIMIT 6";
                                    backlinks = await SiYuanClient.SqlAsync(refSql);
                                }
                                catch { }

                                // E. 关联本地文件与附件清单 (Linked Local Files & Assets Radar)
                                List<Dictionary<string, object>> linkedAssets = new List<Dictionary<string, object>>();
                                try
                                {
                                    string rootId = blockInfo != null ? JsonHelper.GetString(blockInfo, "root_id") : targetId;
                                    if (string.IsNullOrEmpty(rootId)) rootId = targetId;
                                    string safeRoot = rootId.Replace("'", "''");

                                    string assetSql = "SELECT b.id, b.content FROM blocks b " +
                                                      "WHERE (b.root_id = '" + safeRoot + "' OR b.id = '" + safeId + "') " +
                                                      "AND (b.content LIKE '%assets/%' OR b.content LIKE '%file:///%') LIMIT 15";
                                    var assetBlocks = await SiYuanClient.SqlAsync(assetSql);
                                    if (assetBlocks != null)
                                    {
                                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                        string wsAssets = SiYuanClient.WorkspaceAssetsDir;
                                        foreach (var ab in assetBlocks)
                                        {
                                            string cnt = JsonHelper.GetString(ab, "content");
                                            var mAsset = System.Text.RegularExpressions.Regex.Matches(cnt, @"assets/([a-zA-Z0-9_\-\.\u4e00-\u9fa5]+\.[a-zA-Z0-9]+)");
                                            foreach (System.Text.RegularExpressions.Match m in mAsset)
                                            {
                                                string fn = m.Groups[1].Value;
                                                string rel = m.Value;
                                                if (!seen.Contains(rel))
                                                {
                                                    seen.Add(rel);
                                                    string full = Path.Combine(wsAssets, fn);
                                                    string sz = "未知大小";
                                                    if (File.Exists(full))
                                                    {
                                                        long len = new FileInfo(full).Length;
                                                        sz = len >= 1048576 ? (len / 1048576.0).ToString("F1") + " MB" : (len / 1024.0).ToString("F0") + " KB";
                                                    }
                                                    var it = new Dictionary<string, object>();
                                                    it["fileName"] = fn;
                                                    it["assetRel"] = rel;
                                                    it["fullPath"] = full;
                                                    it["ext"] = Path.GetExtension(fn).ToLowerInvariant();
                                                    it["size"] = sz;
                                                    it["blockId"] = JsonHelper.GetString(ab, "id");
                                                    it["exists"] = File.Exists(full);
                                                    linkedAssets.Add(it);
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }

                                // F. 文档大纲目录树 (Document Headings H1~H6)
                                List<Dictionary<string, object>> headings = new List<Dictionary<string, object>>();
                                try
                                {
                                    string rootId = blockInfo != null ? JsonHelper.GetString(blockInfo, "root_id") : targetId;
                                    if (string.IsNullOrEmpty(rootId)) rootId = targetId;
                                    string safeRoot = rootId.Replace("'", "''");

                                    string hSql = "SELECT b.id, b.content, b.subtype, b.sort FROM blocks b " +
                                                  "WHERE b.root_id = '" + safeRoot + "' AND b.type = 'h' " +
                                                  "ORDER BY b.sort ASC LIMIT 16";
                                    var hRows = await SiYuanClient.SqlAsync(hSql);
                                    if (hRows != null) headings = hRows;
                                }
                                catch { }

                                var contextPayload = new
                                {
                                    id = targetId,
                                    breadcrumb = breadcrumbs,
                                    block = blockInfo,
                                    siblings = siblings,
                                    backlinks = backlinks,
                                    linkedAssets = linkedAssets,
                                    headings = headings
                                };

                                lock (_blockContextCache)
                                {
                                    _blockContextCache[targetId] = contextPayload;
                                    if (_blockContextCache.Count > 150) _blockContextCache.Clear();
                                }

                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onBlockContextLoaded) window.onBlockContextLoaded(" + JsonHelper.ToJson(contextPayload) + ");");
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 12. 今日/历史日记记事时间轴速览 (支持日期穿梭)
                if (op.Equals("getTodayTimeline", StringComparison.OrdinalIgnoreCase))
                {
                    string reqDate = JsonHelper.GetString(msg, "date");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string targetDate = !string.IsNullOrEmpty(reqDate) ? reqDate : DateTime.Now.ToString("yyyy-MM-dd");
                            string compactDate = targetDate.Replace("-", "");
                            string docSql = "SELECT id, content, hpath FROM blocks WHERE type = 'd' AND (hpath LIKE '%" + targetDate + "%' OR content LIKE '%" + targetDate + "%' OR ial LIKE '%custom-dailynote-" + compactDate + "%') ORDER BY updated DESC LIMIT 1";
                            var docRows = await SiYuanClient.SqlAsync(docSql);
                            if (docRows != null && docRows.Count > 0)
                            {
                                string targetDocId = JsonHelper.GetString(docRows[0], "id");
                                string targetDocTitle = JsonHelper.GetString(docRows[0], "content");
                                string blockSql = "SELECT id, parent_id, content, markdown, ial, type, subtype, created, updated FROM blocks WHERE root_id = '" + targetDocId.Replace("'", "''") + "' AND type NOT IN ('d') ORDER BY created ASC";
                                var blockRows = await SiYuanClient.SqlAsync(blockSql);

                                var timelinePayload = new
                                {
                                    date = targetDate,
                                    docId = targetDocId,
                                    docTitle = targetDocTitle,
                                    blocks = blockRows
                                };

                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTodayTimelineLoaded) window.onTodayTimelineLoaded(" + JsonHelper.ToJson(timelinePayload) + ");");
                                    }
                                });
                            }
                            else
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTodayTimelineLoaded) window.onTodayTimelineLoaded({ date: '" + targetDate + "', docId: '', docTitle: '', blocks: [] });");
                                    }
                                });
                            }
                        }
                        catch { }
                    });
                    return;
                }

                if (op.Equals("clearOfflineQueue", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            if (!OfflineQueueManager.ClearQueue()) throw new IOException("离线队列清空失败。");
                            var remainingItems = OfflineQueueManager.GetQueueItems();
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    var payload = JsonHelper.ToJson(new { code = 0, operation = "clear", count = remainingItems.Count, data = remainingItems, items = remainingItems });
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOfflineQueueLoaded) window.onOfflineQueueLoaded(" + payload + ");");
                                }
                            }));
                        }
                        catch (Exception ex) { NotifyOfflineQueueReadFailure(ex, "clear"); }
                    });
                    return;
                }

                // 13. 思源独立新窗口开启
                if (op.Equals("openInStandaloneWindow", StringComparison.OrdinalIgnoreCase))
                {
                    string targetId = JsonHelper.GetString(msg, "id");
                    if (string.IsNullOrEmpty(targetId)) targetId = JsonHelper.GetString(msg, "docId");
                    if (!string.IsNullOrEmpty(targetId))
                    {
                        Hide();
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var conn = SiYuanClient.GetConnectionInfo(false);
                                SiYuanClient.Post(conn.Url, conn.Token, "/api/block/openBlockWindow", new { id = targetId });
                                SuperPanelMain.ActivateSiYuanWindow();
                                Process.Start(new ProcessStartInfo("siyuan://blocks/" + targetId) { UseShellExecute = true });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 14. 全库书签与收藏秒搜 (m: 语法)
                if (op.Equals("searchBookmarks", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeLike = (kw ?? "").Replace("'", "''");
                            string sql;
                            if (!string.IsNullOrEmpty(safeLike))
                            {
                                sql = "SELECT id, root_id, content, hpath, type, updated, ial FROM blocks " +
                                      "WHERE ial LIKE '%bookmark=\"%' AND (content LIKE '%" + safeLike + "%' OR ial LIKE '%" + safeLike + "%') " +
                                      "ORDER BY updated DESC LIMIT 30";
                            }
                            else
                            {
                                sql = "SELECT id, root_id, content, hpath, type, updated, ial FROM blocks " +
                                      "WHERE ial LIKE '%bookmark=\"%' " +
                                      "ORDER BY updated DESC LIMIT 30";
                            }
                            var rows = await SiYuanClient.SqlAsync(sql);
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onBookmarkSearchResults) window.onBookmarkSearchResults(" + JsonHelper.ToJson(new { code = 0, data = rows }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 15. 全库未完成待办穿透 (t: / todo: 语法)
                if (op.Equals("searchTasks", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeLike = (kw ?? "").Replace("'", "''");
                            string sql;
	                            if (!string.IsNullOrEmpty(safeLike))
	                            {
	                                sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.updated, doc.content AS doc_title FROM blocks b " +
	                                      "LEFT JOIN blocks doc ON doc.id = b.root_id " +
	                                      "WHERE b.subtype = 't' AND (b.markdown NOT LIKE '%[x]%' AND b.markdown NOT LIKE '%[X]%') AND (b.content LIKE '%" + safeLike + "%' OR b.markdown LIKE '%" + safeLike + "%') " +
	                                      "ORDER BY b.updated DESC LIMIT 35";
	                            }
	                            else
	                            {
	                                sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.updated, doc.content AS doc_title FROM blocks b " +
	                                      "LEFT JOIN blocks doc ON doc.id = b.root_id " +
	                                      "WHERE b.subtype = 't' AND (b.markdown NOT LIKE '%[x]%' AND b.markdown NOT LIKE '%[X]%') " +
	                                      "ORDER BY b.updated DESC LIMIT 35";
	                            }
                            var rows = await SiYuanClient.SqlAsync(sql);
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTaskSearchResults) window.onTaskSearchResults(" + JsonHelper.ToJson(new { code = 0, data = rows }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 16. 待办勾选完成 (/api/block/updateBlock)
                if (op.Equals("toggleTaskDone", StringComparison.OrdinalIgnoreCase))
                {
                    string taskId = JsonHelper.GetString(msg, "id");
                    string taskContent = JsonHelper.GetString(msg, "content");
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        ThreadPool.QueueUserWorkItem(async s =>
                        {
                            try
                            {
                                if (string.IsNullOrWhiteSpace(taskContent))
                                {
                                    var rows = await SiYuanClient.SqlAsync("SELECT content, markdown FROM blocks WHERE id='" + taskId.Replace("'", "''") + "' LIMIT 1");
                                    if (rows != null && rows.Count > 0)
                                    {
                                        taskContent = JsonHelper.GetString(rows[0], "markdown");
                                        if (string.IsNullOrWhiteSpace(taskContent))
                                        {
                                            taskContent = JsonHelper.GetString(rows[0], "content");
                                        }
                                    }
                                }

                                string rawText = (taskContent ?? "").Trim();
                                string doneText;
                                if (rawText.Contains("- [ ]")) doneText = rawText.Replace("- [ ]", "- [x]");
                                else if (rawText.Contains("* [ ]")) doneText = rawText.Replace("* [ ]", "* [x]");
                                else if (rawText.Contains("[ ]")) doneText = rawText.Replace("[ ]", "[x]");
                                else doneText = "- [x] " + rawText.TrimStart('-', '*', ' ');

                                var res = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                                {
                                    id = taskId,
                                    dataType = "markdown",
                                    data = doneText
                                });

                                bool ok = res != null && res.Success;
                                if (ok)
                                {
                                    SoundPlayerHelper.PlayFeedback();
                                    if (OnNotificationRequested != null)
                                    {
                                        OnNotificationRequested("思源外骨骼 · 待办管理", "✓ 已在思源笔记中勾选完成该待办！");
                                    }
                                }
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTaskToggledDone) window.onTaskToggledDone(" + JsonHelper.ToJson(new { success = ok, id = taskId, content = doneText }) + ");");
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 17. 清扫测试靶场孤儿内核 (释放残留内存)
                if (op.Equals("cleanOrphanKernels", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        string cleanReport = KernelManager.CleanOrphanTestKernels();
                        if (OnNotificationRequested != null)
                        {
                            OnNotificationRequested("思源外骨骼 · 靶场清扫", cleanReport);
                        }
                    });
                    return;
                }

                // 18. 检索思源个人自建模板库 (v: 语法)
                if (op.Equals("searchTemplates", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeLike = (kw ?? "").Replace("'", "''");
                            string sql;
                            if (!string.IsNullOrEmpty(safeLike))
                            {
                                sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.updated FROM blocks b " +
                                      "WHERE b.type='d' AND (b.hpath LIKE '%/templates/%' OR b.hpath LIKE '%/模板/%' OR b.ial LIKE '%template%' OR b.content LIKE '%模板%') " +
                                      "AND (b.content LIKE '%" + safeLike + "%' OR b.hpath LIKE '%" + safeLike + "%') " +
                                      "ORDER BY b.updated DESC LIMIT 15";
                            }
                            else
                            {
                                sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.updated FROM blocks b " +
                                      "WHERE b.type='d' AND (b.hpath LIKE '%/templates/%' OR b.hpath LIKE '%/模板/%' OR b.ial LIKE '%template%' OR b.content LIKE '%模板%') " +
                                      "ORDER BY b.updated DESC LIMIT 15";
                            }
                            var rows = await SiYuanClient.SqlAsync(sql);
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onTemplateSearchResults) window.onTemplateSearchResults(" + JsonHelper.ToJson(new { code = 0, data = rows }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 19. 检索近 15 条剪贴板历史快照 (c: 语法)
                if (op.Equals("getRecentClips", StringComparison.OrdinalIgnoreCase))
                {
                    var list = ClipboardWatcher.GetRecentItemsSnapshot();
                    if (_webView != null && _webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onRecentClipsLoaded) window.onRecentClipsLoaded(" + JsonHelper.ToJson(new { code = 0, data = list }) + ");");
                    }
                    return;
                }

                // 20. 兼容旧版 name/value 与 setVar 嵌套 data 格式（如指令码、后续动作）
                var dataDict = JsonHelper.GetDict(msg, "data") ?? msg;
                string name = JsonHelper.GetString(dataDict, "name");
                string val = JsonHelper.GetString(dataDict, "value").Trim();
                if (op.Equals("setVar", StringComparison.OrdinalIgnoreCase))
                {
                    if (val.StartsWith("openurl:", StringComparison.OrdinalIgnoreCase))
                    {
                        string targetUrl = val.Substring("openurl:".Length).Trim();
                        if (!string.IsNullOrEmpty(targetUrl))
                        {
                            try { Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true }); } catch { }
                        }
                    }
                    return;
                }
                // 19. 全库附件与文献资产秒查坞 (att: / asset: 语法)
                if (op.Equals("searchAssets", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            string safeLike = (kw ?? "").Replace("'", "''");
                            string sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.updated, doc.content AS doc_title FROM blocks b " +
                                  "LEFT JOIN blocks doc ON doc.id = b.root_id " +
                                  "WHERE b.content LIKE '%assets/%' AND b.type NOT IN ('d') " +
                                  (!string.IsNullOrEmpty(safeLike) ? "AND (b.content LIKE '%" + safeLike + "%' OR doc.content LIKE '%" + safeLike + "%') " : "") +
                                  "ORDER BY b.updated DESC LIMIT 60";

                            var rows = await SiYuanClient.SqlAsync(sql);
                            var assetsList = new List<Dictionary<string, object>>();
                            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            string physicalAssetBase = SiYuanClient.WorkspaceAssetsDir;

                            if (rows != null)
                            {
                                foreach (var r in rows)
                                {
                                    string content = JsonHelper.GetString(r, "content");
                                    var matches = System.Text.RegularExpressions.Regex.Matches(content, @"assets/([a-zA-Z0-9_\-\.\u4e00-\u9fa5]+\.[a-zA-Z0-9]+)");
                                    foreach (System.Text.RegularExpressions.Match m in matches)
                                    {
                                        string rel = m.Value;
                                        string fn = m.Groups[1].Value;
                                        string fullPath = !string.IsNullOrEmpty(physicalAssetBase) ? Path.Combine(physicalAssetBase, fn) : "";

                                        string ext = Path.GetExtension(fn).ToLowerInvariant();
                                        string sizeStr = "未知大小";
                                        if (File.Exists(fullPath))
                                        {
                                            long bytes = new FileInfo(fullPath).Length;
                                            sizeStr = bytes >= 1048576 
                                                ? (bytes / 1048576.0).ToString("F1") + " MB" 
                                                : (bytes / 1024.0).ToString("F0") + " KB";
                                        }

                                        string key = rel + "::" + JsonHelper.GetString(r, "root_id");
                                        if (!seenPaths.Contains(key))
                                        {
                                            seenPaths.Add(key);
                                            var it = new Dictionary<string, object>();
                                            it["id"] = JsonHelper.GetString(r, "id");
                                            it["rootId"] = JsonHelper.GetString(r, "root_id");
                                            it["docTitle"] = JsonHelper.GetString(r, "doc_title");
                                            it["assetRel"] = rel;
                                            it["fileName"] = fn;
                                            it["ext"] = ext;
                                            it["size"] = sizeStr;
                                            it["fullPath"] = fullPath;
                                            it["content"] = content;
                                            it["updated"] = JsonHelper.GetString(r, "updated");
                                            assetsList.Add(it);
                                        }
                                    }
                                }
                            }

                            // 物理磁盘附件补充扫描 (支持在 data/assets/ 穿透检索全部物理文件，哪怕尚未在笔记中引用)
                            if (!string.IsNullOrEmpty(physicalAssetBase) && Directory.Exists(physicalAssetBase) && !string.IsNullOrEmpty(kw) && kw.Length >= 2)
                            {
                                try
                                {
                                    var di = new DirectoryInfo(physicalAssetBase);
                                    var matchedFiles = di.GetFiles("*" + kw + "*.*");
                                    int added = 0;
                                    foreach (var fi in matchedFiles)
                                    {
                                        if (added >= 20) break;
                                        string fn = fi.Name;
                                        string rel = "assets/" + fn;
                                        if (!seenPaths.Contains(rel + "::"))
                                        {
                                            seenPaths.Add(rel + "::");
                                            var it = new Dictionary<string, object>();
                                            it["id"] = "";
                                            it["rootId"] = "";
                                            it["docTitle"] = "未引用资产 (物理存储就绪)";
                                            it["assetRel"] = rel;
                                            it["fileName"] = fn;
                                            it["ext"] = fi.Extension.ToLowerInvariant();
                                            it["size"] = fi.Length >= 1048576 
                                                ? (fi.Length / 1048576.0).ToString("F1") + " MB" 
                                                : (fi.Length / 1024.0).ToString("F0") + " KB";
                                            it["fullPath"] = fi.FullName;
                                            it["content"] = "物理附件已存储于思源 assets 库中，按 Shift+Enter 可将其 Markdown 链接直接贴入光标处";
                                            it["updated"] = fi.LastWriteTime.ToString("yyyyMMddHHmmss");
                                            assetsList.Add(it);
                                            added++;
                                        }
                                    }
                                }
                                catch { }
                            }

                            // 外部监听的本地文档补充扫描 (包含 OfficeFileWatcher 监听的 Word/Excel/PPT/PDF 底稿)
                            try
                            {
                                var watchedFiles = OfficeFileWatcher.GetWatchedFilesSnapshot();
                                if (watchedFiles != null && watchedFiles.Count > 0)
                                {
                                    foreach (var wf in watchedFiles)
                                    {
                                        if (!File.Exists(wf)) continue;
                                        string wfn = Path.GetFileName(wf);
                                        if (string.IsNullOrEmpty(kw) || wfn.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 || wf.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            string key = wf + "::watched";
                                            if (!seenPaths.Contains(key))
                                            {
                                                seenPaths.Add(key);
                                                var fi = new FileInfo(wf);
                                                var it = new Dictionary<string, object>();
                                                it["id"] = "watched_" + Guid.NewGuid().ToString("N");
                                                it["rootId"] = "";
                                                it["docTitle"] = "本地外部文档 (热监听就绪)";
                                                it["assetRel"] = "file:///" + wf.Replace('\\', '/');
                                                it["fileName"] = wfn;
                                                it["ext"] = fi.Extension.ToLowerInvariant();
                                                it["size"] = fi.Length >= 1048576 
                                                    ? (fi.Length / 1048576.0).ToString("F1") + " MB" 
                                                    : (fi.Length / 1024.0).ToString("F0") + " KB";
                                                it["fullPath"] = wf;
                                                it["content"] = "外部文档底稿: " + wf + " (支持大纲/批注透视与深链跳转)";
                                                it["updated"] = fi.LastWriteTime.ToString("yyyyMMddHHmmss");
                                                assetsList.Add(it);
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onAssetSearchResults) window.onAssetSearchResults(" + JsonHelper.ToJson(new { code = 0, data = assetsList }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 20. 打开本地物理附件文件 (自动适配优先阅读器如 SumatraPDF / Office / 关联软件)
                if (op.Equals("openLocalFile", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "path");
                    int page = 0;
                    string pageStr = JsonHelper.GetString(msg, "page");
                    if (!string.IsNullOrEmpty(pageStr)) int.TryParse(pageStr, out page);

                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        FileDispatcher.OpenWithPreferredApp(p, page);
                    }
                    return;
                }

                // 21. 本地工具检索附件与文件 (支持 Everything / Listary / 智能感知)
                if (op.Equals("openInEverything", StringComparison.OrdinalIgnoreCase) ||
                    op.Equals("openInSearchTool", StringComparison.OrdinalIgnoreCase) ||
                    op.Equals("locateFile", StringComparison.OrdinalIgnoreCase))
                {
                    string q = JsonHelper.GetString(msg, "query");
                    if (string.IsNullOrEmpty(q)) q = JsonHelper.GetString(msg, "keyword");
                    var pref = AppSettings.Instance.PreferredSearchEngine;
                    ToolDetector.OpenInSearchTool(q ?? "", pref);
                    return;
                }

                // 21.5 全盘极速文件检索 (ev: / find: / listary: 语法直通本地检索工具)
                if (op.Equals("searchEverything", StringComparison.OrdinalIgnoreCase) ||
                    op.Equals("searchLocalFiles", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    if (!string.IsNullOrEmpty(kw))
                    {
                        var pref = AppSettings.Instance.PreferredSearchEngine;
                        ToolDetector.OpenInSearchTool(kw, pref);
                    }
                    return;
                }

                // 21.7 调动本地 Zotero 学术文献管理库 (zotero: 或 zotero:// 链接穿透)
                if (op.Equals("openInZotero", StringComparison.OrdinalIgnoreCase))
                {
                    string q = JsonHelper.GetString(msg, "query");
                    if (string.IsNullOrEmpty(q)) q = JsonHelper.GetString(msg, "keyword");
                    ToolDetector.OpenInZotero(q ?? "");
                    return;
                }

                // 21.75 一键生成 Zotero 文献卡片并沉淀入今日日记
                if (op.Equals("importZoteroCard", StringComparison.OrdinalIgnoreCase))
                {
                    string key = JsonHelper.GetString(msg, "itemKey");
                    string title = JsonHelper.GetString(msg, "title");
                    string creators = JsonHelper.GetString(msg, "creators");
                    string year = JsonHelper.GetString(msg, "year");
                    string doi = JsonHelper.GetString(msg, "doi");
                    string pub = JsonHelper.GetString(msg, "publication");
                    string pdf = JsonHelper.GetString(msg, "localPdf");

                    var item = ZoteroBridge.CreateItemSummary(key, title, creators, year, doi, pub, pdf);
                    string md = ZoteroBridge.BuildLiteratureCardMarkdown(item);

                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        var conn = SiYuanClient.GetConnectionInfo(false);
                        bool ok = SmartCaptureMain.AppendToDailyNote(conn.Url, conn.Token, md);
                        if (ok) SoundPlayerHelper.PlayFeedback();
                    });
                    return;
                }

                // 21.8 本地 Office (Word/Excel/PPT) 流式透视与抽取
                if (op.Equals("inspectOfficeFile", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "filePath");
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var summary = OfficeOpenXmlParser.InspectFile(p);
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOfficeInspectionLoaded) window.onOfficeInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 21.82 本地学术 PDF 极速流式透视 (页数/元数据/书签)
                if (op.Equals("inspectPdf", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "filePath");
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var summary = PdfLightParser.InspectPdf(p);
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onPdfInspectionLoaded) window.onPdfInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 21.83 本地 EPUB 电子书极速流式透视 (书名/作者/章节目录)
                if (op.Equals("inspectEpub", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "filePath");
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var summary = EpubLightParser.InspectEpub(p);
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onEpubInspectionLoaded) window.onEpubInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 21.84 本地思维导图 (.xmind) 极速流式透视与原生大纲生成
                if (op.Equals("inspectXMind", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "filePath");
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var summary = XMindLightParser.InspectXMind(p);
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onXMindInspectionLoaded) window.onXMindInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                // 21.845 本地多媒体 (音视频) 极速流式透视与时间戳起播
                if (op.Equals("inspectMedia", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "filePath");
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var summary = MediaLightParser.InspectMedia(p);
                                Dispatcher.Invoke(() =>
                                {
                                    if (_webView != null && _webView.CoreWebView2 != null)
                                    {
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onMediaInspectionLoaded) window.onMediaInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    return;
                }

                if (op.Equals("openMediaAtTime", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "filePath");
                    int seek = JsonHelper.GetInt(msg, "time", 0);
                    MediaLightParser.PlayMediaAtTime(p, seek);
                    return;
                }

                // 21.85 零依赖逆向导出 Word (.docx) 并本地秒开
                if (op.Equals("exportDocx", StringComparison.OrdinalIgnoreCase))
                {
                    string md = JsonHelper.GetString(msg, "markdown");
                    string title = JsonHelper.GetString(msg, "title");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        bool ok = OfficeExporter.ExportAndOpen("docx", md, title);
                        if (ok && OnNotificationRequested != null)
                        {
                            OnNotificationRequested("思源外骨骼 · 导出Word", "✓ 已成功生成标准 .docx 大纲并在本地秒开！");
                        }
                    });
                    return;
                }

                // 21.86 零依赖逆向导出 Excel (.xlsx) 并本地秒开
                if (op.Equals("exportXlsx", StringComparison.OrdinalIgnoreCase))
                {
                    string table = JsonHelper.GetString(msg, "markdown");
                    string title = JsonHelper.GetString(msg, "title");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        bool ok = OfficeExporter.ExportAndOpen("xlsx", table, title);
                        if (ok && OnNotificationRequested != null)
                        {
                            OnNotificationRequested("思源外骨骼 · 导出Excel", "✓ 已成功生成标准 .xlsx 表格并在本地秒开！");
                        }
                    });
                    return;
                }

                // 21.87 资产安全隔离区：移入 .quarantine
                if (op.Equals("quarantineAssets", StringComparison.OrdinalIgnoreCase))
                {
                    var namesObj = JsonHelper.GetList(msg, "fileNames");
                    var names = new List<string>();
                    if (namesObj != null)
                    {
                        foreach (var n in namesObj) if (n != null) names.Add(n.ToString());
                    }
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        int count = AssetCleanDoctor.QuarantineAssets(SiYuanClient.WorkspaceAssetsDir, names);
                        if (OnNotificationRequested != null)
                        {
                            OnNotificationRequested("思源外骨骼 · 资产隔离区", "✓ 已将 " + count + " 个无主孤儿附件安全移入 .quarantine 隔离区！");
                        }
                    });
                    return;
                }

                // 21.88 资产安全隔离区：彻底清空释放
                if (op.Equals("purgeQuarantine", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        long freed = AssetCleanDoctor.PurgeQuarantine(SiYuanClient.WorkspaceAssetsDir);
                        string freedStr = AssetCleanDoctor.FormatFileSize(freed);
                        if (OnNotificationRequested != null)
                        {
                            OnNotificationRequested("思源外骨骼 · 隔离区清理", "✓ 已彻底清空隔离区，累计释放 " + freedStr + " 磁盘空间！");
                        }
                    });
                    return;
                }

                // 21.9 跨软件 Office 深度锚点跳转 (Excel 单元格 / Word 标题)
                if (op.Equals("openOfficeLocation", StringComparison.OrdinalIgnoreCase))
                {
                    string p = JsonHelper.GetString(msg, "filePath");
                    string sheet = JsonHelper.GetString(msg, "sheet");
                    string cell = JsonHelper.GetString(msg, "cell");
                    string heading = JsonHelper.GetString(msg, "heading");
                    XiaoLv.Desktop.Engine.Modules.OfficeDeepLinkMain.OpenOfficeLocation(p, sheet, cell, heading);
                    return;
                }

                // 21.91 Excel 表格一键转入思源属性视图 (AV 数据库)
                if (op.Equals("importExcelToAv", StringComparison.OrdinalIgnoreCase))
                {
                    string targetAvId = JsonHelper.GetString(msg, "avId");
                    string tableMd = JsonHelper.GetString(msg, "markdown");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            var conn = SiYuanClient.GetConnectionInfo(false);
                            if (string.IsNullOrEmpty(targetAvId))
                            {
                                // 自动寻找最近更新的 AV 视图
                                var avRows = await SiYuanClient.SqlAsync("SELECT id FROM blocks WHERE type='av' ORDER BY updated DESC LIMIT 1");
                                if (avRows != null && avRows.Count > 0)
                                {
                                    targetAvId = JsonHelper.GetString(avRows[0], "id");
                                }
                            }

                            if (!string.IsNullOrEmpty(targetAvId))
                            {
                                XiaoLv.Desktop.Engine.Modules.OfficeDeepLinkMain.ImportExcelToAvDatabase(conn.Url, conn.Token, targetAvId, tableMd, (t, m) =>
                                {
                                    if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                                });
                            }
                            else
                            {
                                if (OnNotificationRequested != null) OnNotificationRequested("思源外骨骼", "⚠️ 未找到可注入的属性视图，请先在思源中创建属性视图！");
                            }
                        }
                        catch (Exception ex)
                        {
                            if (OnNotificationRequested != null) OnNotificationRequested("思源外骨骼", "⚠️ 导入属性视图失败: " + ex.Message);
                        }
                    });
                    return;
                }

                // 21.6 误删打捞与时光机安全检索 (rescue: / hist: 语法)
                if (op.Equals("searchRescue", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var items = HistoryRescueManager.SearchHistory(kw);
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onRescueSearchResults) window.onRescueSearchResults(" + JsonHelper.ToJson(new { code = 0, data = items }) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 21.7 1键打捞恢复误删内容至今日日记
                if (op.Equals("restoreRescueItem", StringComparison.OrdinalIgnoreCase))
                {
                    string docTitle = JsonHelper.GetString(msg, "title");
                    string markdown = JsonHelper.GetString(msg, "markdown");
                    string snapTime = JsonHelper.GetString(msg, "time");
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        HistoryRescueManager.RestoreToSiYuan(docTitle, markdown, snapTime, (t, m) =>
                        {
                            if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                        });
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onRescueRestored) window.onRescueRestored(" + JsonHelper.ToJson(new { success = true, title = docTitle, time = snapTime, markdown = markdown }) + ");");
                            }
                        });
                    });
                    return;
                }

                // 21.8 立即创建安全时光机快照 (AI大动作与重大操作前安全屏障)
                if (op.Equals("createTimeSnapshot", StringComparison.OrdinalIgnoreCase))
                {
                    string memo = JsonHelper.GetString(msg, "memo");
                    if (string.IsNullOrEmpty(memo)) memo = "思源外骨骼 · 安全时光机快照";
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            var res = await SiYuanClient.PostAsync("/api/history/createSnapshot", new { memo = memo });
                            bool ok = res != null && res.Success;
                            if (ok) SoundPlayerHelper.PlayFeedback();
                            if (OnNotificationRequested != null)
                            {
                                OnNotificationRequested("思源外骨骼 · 时光机", ok ? ("✓ 已成功建立时光机快照！") : "⚠️ 创建快照响应超时");
                            }
                        }
                        catch { }
                    });
                    return;
                }

                // 21.9 唤醒思源并直接打开原生「数据历史 / 回收站」大窗
                if (op.Equals("openNativeDataHistory", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            SuperPanelMain.ActivateSiYuanWindow();
                            string syUrl = "";
                            string token = "";
                            if (SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false))
                            {
                                LvSendCommand.Send(syUrl, token, "commands.run", "{\"command\":\"dataHistory\"}", "siyuan-speed-switch", "fast");
                            }
                        }
                        catch { }
                    });
                    return;
                }

                // 22.0 嗅探前台浏览器完整上下文供窗内网页剪藏工作台实时展示与编辑 (probeWebClipContext)
                if (op.Equals("probeWebClipContext", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var snap = (LastExternalForegroundHwnd != IntPtr.Zero)
                                ? ContextPerception.CaptureForegroundContext(LastExternalForegroundHwnd)
                                : ContextPerception.CaptureForegroundContext();
                            string url = snap != null ? TextCleaner.PurifyUrl(snap.Url) : "";
                            string rawTitle = snap != null ? (!string.IsNullOrEmpty(snap.CleanTitle) ? snap.CleanTitle : snap.FullTitle) : "";
                            string title, author, siteName;
                            TextCleaner.ParseSiteMetadata(url, rawTitle, out title, out author, out siteName);

                            string excerpt = "";
                            if (ClipboardWatcher.IsEnabled && ClipboardWatcher.LatestItem != null)
                            {
                                excerpt = !string.IsNullOrEmpty(ClipboardWatcher.LatestItem.EnrichedMarkdown)
                                    ? ClipboardWatcher.LatestItem.EnrichedMarkdown
                                    : ClipboardWatcher.LatestItem.RawText;
                            }

                            var payload = new Dictionary<string, object>();
                            payload["code"] = 0;
                            payload["title"] = title ?? "";
                            payload["url"] = url ?? "";
                            payload["siteName"] = siteName ?? "";
                            payload["author"] = author ?? "";
                            payload["app"] = snap != null ? (snap.AppDisplayName ?? "浏览器") : "浏览器";
                            payload["excerpt"] = excerpt ?? "";

                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWebClipContextProbed) window.onWebClipContextProbed(" + JsonHelper.ToJson(payload) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 22. 触发网页极速剪藏卡 (支持双态剪藏、自定义标题/网址/摘录、顺便收藏网址及剪藏后进一步选择回执)
                if (op.Equals("webClip", StringComparison.OrdinalIgnoreCase) || op.Equals("executeWebClip", StringComparison.OrdinalIgnoreCase))
                {
                    string mode = JsonHelper.GetString(msg, "mode");
                    if (string.IsNullOrEmpty(mode)) mode = "card";
                    string annotation = JsonHelper.GetString(msg, "annotation");
                    if (string.IsNullOrEmpty(annotation)) annotation = JsonHelper.GetString(msg, "text");
                    string customTitle = JsonHelper.GetString(msg, "customTitle");
                    string customUrl = JsonHelper.GetString(msg, "customUrl");
                    string customExcerpt = JsonHelper.GetString(msg, "customExcerpt");
                    bool alsoBookmark = JsonHelper.GetBool(msg, "alsoBookmark", false);
                    string bookmarkCategory = JsonHelper.GetString(msg, "bookmarkCategory", "常用");
                    bool keepOpen = JsonHelper.GetBool(msg, "keepOpen", true);

                    if (!keepOpen) Hide();
                    SmartCaptureMain.ExecuteWebClip((t, m) =>
                    {
                        if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                    }, annotation, mode, customTitle, customUrl, customExcerpt, alsoBookmark, bookmarkCategory, (resDict) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWebClipCompleted) window.onWebClipCompleted(" + JsonHelper.ToJson(resDict) + ");");
                            }
                        });
                    });
                    return;
                }

                // 22.5 融合前置大模型提炼的智能网页剪藏
                if (op.Equals("executeWebClipWithAi", StringComparison.OrdinalIgnoreCase))
                {
                    string mode = JsonHelper.GetString(msg, "mode");
                    if (string.IsNullOrEmpty(mode)) mode = "card";
                    string customTitle = JsonHelper.GetString(msg, "customTitle");
                    string customUrl = JsonHelper.GetString(msg, "customUrl");
                    string customExcerpt = JsonHelper.GetString(msg, "customExcerpt");
                    bool alsoBookmark = JsonHelper.GetBool(msg, "alsoBookmark", false);
                    string bookmarkCategory = JsonHelper.GetString(msg, "bookmarkCategory", "常用");
                    bool keepOpen = JsonHelper.GetBool(msg, "keepOpen", true);

                    if (!keepOpen) Hide();
                    SmartCaptureMain.ExecuteWebClipWithAi((t, m) =>
                    {
                        if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                    }, mode, customTitle, customUrl, customExcerpt, alsoBookmark, bookmarkCategory, (resDict) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWebClipCompleted) window.onWebClipCompleted(" + JsonHelper.ToJson(resDict) + ");");
                            }
                        });
                    });
                    return;
                }

                // 22.6 窗内截屏与离线 OCR 工作台联动 (截图完成后自动回到总控面板展示识别结果与下一步操作选择)
                if (op.Equals("startScreenSnipForWorkbench", StringComparison.OrdinalIgnoreCase))
                {
                    Hide();
                    SnippingManager.TriggerCapture((recognized, bmp) =>
                    {
                        string imagePath = "";
                        if (bmp != null)
                        {
                            try
                            {
                                var tmpFile = Path.Combine(Path.GetTempPath(), "xiaolv_snip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                                bmp.Save(tmpFile, System.Drawing.Imaging.ImageFormat.Png);
                                imagePath = tmpFile;
                            }
                            catch { }
                            try { bmp.Dispose(); } catch { }
                        }

                        Dispatcher.Invoke(() =>
                        {
                            ShowPanel("ocr:");
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                var payload = new Dictionary<string, object>();
                                payload["success"] = true;
                                payload["text"] = recognized ?? "";
                                payload["imagePath"] = imagePath ?? "";
                                payload["source"] = "屏幕框选截屏";
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWorkbenchOcrCaptured) window.onWorkbenchOcrCaptured(" + JsonHelper.ToJson(payload) + ");");
                            }
                        });
                    }, (title, m) =>
                    {
                        if (OnNotificationRequested != null) OnNotificationRequested(title, m);
                    });
                    return;
                }

                if (op.Equals("ocrClipboardImage", StringComparison.OrdinalIgnoreCase))
                {
                    var staThread = new Thread(() =>
                    {
                        try
                        {
                            string recognized = "";
                            string imagePath = "";
                            string source = "系统剪贴板";
                            if (System.Windows.Forms.Clipboard.ContainsImage())
                            {
                                var img = System.Windows.Forms.Clipboard.GetImage();
                                if (img != null)
                                {
                                    var bmp = new System.Drawing.Bitmap(img);
                                    try
                                    {
                                        var tmpFile = Path.Combine(Path.GetTempPath(), "xiaolv_clipimg_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                                        bmp.Save(tmpFile, System.Drawing.Imaging.ImageFormat.Png);
                                        imagePath = tmpFile;
                                    }
                                    catch { }
                                    recognized = LocalOcrEngine.RecognizeBitmap(bmp);
                                    try { bmp.Dispose(); } catch { }
                                    source = "剪贴板图片 OCR";
                                }
                            }
                            else if (System.Windows.Forms.Clipboard.ContainsText())
                            {
                                recognized = System.Windows.Forms.Clipboard.GetText();
                                source = "剪贴板已有文本";
                            }

                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    var payload = new Dictionary<string, object>();
                                    payload["success"] = !string.IsNullOrEmpty(recognized) || !string.IsNullOrEmpty(imagePath);
                                    payload["text"] = recognized ?? "";
                                    payload["imagePath"] = imagePath ?? "";
                                    payload["source"] = source;
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onWorkbenchOcrCaptured) window.onWorkbenchOcrCaptured(" + JsonHelper.ToJson(payload) + ");");
                                }
                            });
                        }
                        catch { }
                    });
                    staThread.IsBackground = true;
                    staThread.SetApartmentState(ApartmentState.STA);
                    staThread.Start();
                    return;
                }

                // 22.8 通用大模型前置智能提炼 (AI Refine) 并写入今日日记或回调前端
                if (op.Equals("refineWithAi", StringComparison.OrdinalIgnoreCase) || op.Equals("executeAiRefine", StringComparison.OrdinalIgnoreCase))
                {
                    string mode = JsonHelper.GetString(msg, "mode"); // "takeaways", "todos", "polish", "translate"
                    string inputText = JsonHelper.GetString(msg, "text");
                    string targetDoc = JsonHelper.GetString(msg, "target"); // "daily" or "return"

                    if (!string.IsNullOrEmpty(inputText))
                    {
                        string prompt = AiRefiner.PromptKeyPoints;
                        string opName = "要点提炼";
                        if (mode == "todos") { prompt = AiRefiner.PromptActionItems; opName = "待办提取"; }
                        else if (mode == "polish") { prompt = AiRefiner.PromptPolish; opName = "去噪润色"; }
                        else if (mode == "translate") { prompt = AiRefiner.PromptTranslate; opName = "学术翻译"; }

                        ThreadPool.QueueUserWorkItem(s =>
                        {
                            try
                            {
                                var refineRes = AiRefiner.Refine(prompt, inputText);
                                if (refineRes.Success && !string.IsNullOrEmpty(refineRes.Content))
                                {
                                    if (targetDoc == "daily" || string.IsNullOrEmpty(targetDoc))
                                    {
                                        var conn = SiYuanClient.GetConnectionInfo(false);
                                        string md = string.Format("### 🤖 大模型{0}\n{1}\n\n{{: custom-type=\"ai-refined\" }}\n", opName, refineRes.Content.Trim());
                                        SmartCaptureMain.AppendToDailyNote(conn.Url, conn.Token, md);
                                        if (OnNotificationRequested != null)
                                        {
                                            OnNotificationRequested("思源外骨骼 · AI提炼", "✓ 已成功完成" + opName + "并沉淀写入今日日记！");
                                        }
                                    }
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (_webView != null && _webView.CoreWebView2 != null)
                                        {
                                            _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.onAiRefined) window.onAiRefined({{ success: true, mode: '{0}', content: {1} }});", SafeContext.EscapeJson(mode), JsonHelper.ToJson(refineRes.Content)));
                                        }
                                    });
                                }
                                else
                                {
                                    if (OnNotificationRequested != null)
                                    {
                                        OnNotificationRequested("思源外骨骼 · AI提炼", "⚠️ 提炼失败: " + refineRes.ErrorMessage);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (OnNotificationRequested != null)
                                {
                                    OnNotificationRequested("思源外骨骼 · AI提炼", "⚠️ 提炼异常: " + ex.Message);
                                }
                            }
                        });
                    }
                    return;
                }

                // 23. 安全触发思源云端增量数据同步 (S3/WebDAV云同步守护)
                if (op.Equals("performSync", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            var res = await SiYuanClient.PostAsync("/api/sync/performSync", new { });
                            bool ok = res != null && res.Success;
                            if (ok) SoundPlayerHelper.PlayFeedback();
                            Dispatcher.Invoke(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onSyncCompleted) window.onSyncCompleted({ success: " + (ok ? "true" : "false") + " });");
                                }
                            });
                        }
                        catch { }
                    });
                    return;
                }

                // 24. 智能物理内存与工作集压缩指令 (Memory Trim & WorkingSet Deep Compression)
                if (op.Equals("trimMemory", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        MemoryOptimizer.PerformDeepTrim();
                        double ws = MemoryOptimizer.GetCurrentWorkingSetMb();
                        double pb = MemoryOptimizer.GetCurrentPrivateBytesMb();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                string js = string.Format("if(window.onMemoryTrimmed) window.onMemoryTrimmed({0}, {1});", ws, pb);
                                _webView.CoreWebView2.ExecuteScriptAsync(js);
                            }
                        }));
                    });
                    return;
                }

                if (op.Equals("getMemoryStatus", StringComparison.OrdinalIgnoreCase))
                {
                    double ws = MemoryOptimizer.GetCurrentWorkingSetMb();
                    double pb = MemoryOptimizer.GetCurrentPrivateBytesMb();
                    if (_webView != null && _webView.CoreWebView2 != null)
                    {
                        string js = string.Format("if(window.onMemoryStatus) window.onMemoryStatus({0}, {1});", ws, pb);
                        _webView.CoreWebView2.ExecuteScriptAsync(js);
                    }
                    return;
                }

                // 25. 工作现场快照与全域恢复 (Workspace Horizon & Session Restore)
                if (op.Equals("listWorkspaces", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        var list = WorkspaceSnapshotManager.ListWorkspaces();
                        string json = JsonHelper.ToJson(list);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                                _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.onWorkspacesLoaded) window.onWorkspacesLoaded(decodeURIComponent(escape(atob('{0}'))));", b64));
                            }
                        }));
                    });
                    return;
                }

                if (op.Equals("saveWorkspace", StringComparison.OrdinalIgnoreCase))
                {
                    string wsName = JsonHelper.GetString(msg, "name");
                    string note = JsonHelper.GetString(msg, "note");
                    string icon = JsonHelper.GetString(msg, "icon", "🚀");
                    var dIds = JsonHelper.GetList(msg, "docIds");
                    var dTitles = JsonHelper.GetList(msg, "docTitles");
                    var lFiles = JsonHelper.GetList(msg, "localFiles");
                    var wUrls = JsonHelper.GetList(msg, "webUrls");

                    var ws = new WorkspaceItem
                    {
                        Name = !string.IsNullOrEmpty(wsName) ? wsName : "我的工作现场",
                        Note = note,
                        Icon = icon
                    };
                    if (dIds != null) foreach (var it in dIds) ws.DocIds.Add(it.ToString());
                    if (dTitles != null) foreach (var it in dTitles) ws.DocTitles.Add(it.ToString());
                    if (lFiles != null) foreach (var it in lFiles) ws.LocalFiles.Add(it.ToString());
                    if (wUrls != null) foreach (var it in wUrls) ws.WebUrls.Add(it.ToString());

                    if (ws.DocIds.Count == 0 && !string.IsNullOrEmpty(_currentInspectedDocId))
                    {
                        ws.DocIds.Add(_currentInspectedDocId);
                        ws.DocTitles.Add(_currentInspectedDocTitle);
                    }

                    bool ok = WorkspaceSnapshotManager.SaveWorkspace(ws);
                    if (ok)
                    {
                        SoundPlayerHelper.PlayFeedback();
                        if (OnNotificationRequested != null) OnNotificationRequested("工作现场快照", "✓ 成功保存工作现场【" + ws.Name + "】！");
                    }
                    return;
                }

                if (op.Equals("restoreWorkspace", StringComparison.OrdinalIgnoreCase))
                {
                    string wsId = JsonHelper.GetString(msg, "id");
                    Hide();
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        WorkspaceSnapshotManager.RestoreWorkspace(wsId, (t, m) =>
                        {
                            if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                        });
                    });
                    return;
                }

                if (op.Equals("deleteWorkspace", StringComparison.OrdinalIgnoreCase))
                {
                    string wsId = JsonHelper.GetString(msg, "id");
                    WorkspaceSnapshotManager.DeleteWorkspace(wsId);
                    return;
                }

                // 26. 桌面极速语音速记与口语去噪 (Voice HUD & Speech Denoising)
                if (op.Equals("triggerVoiceTyping", StringComparison.OrdinalIgnoreCase))
                {
                    VoiceCaptureManager.TriggerWindowsVoiceTyping();
                    return;
                }

                if (op.Equals("cleanVoiceSpeech", StringComparison.OrdinalIgnoreCase))
                {
                    string rawSpeech = JsonHelper.GetString(msg, "text");
                    string cleaned = VoiceCaptureManager.CleanVoiceSpeech(rawSpeech);
                    if (_webView != null && _webView.CoreWebView2 != null)
                    {
                        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cleaned));
                        _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.onVoiceSpeechCleaned) window.onVoiceSpeechCleaned(decodeURIComponent(escape(atob('{0}'))));", b64));
                    }
                    return;
                }

                // 27. 全局快捷键冲突深度诊断与智能自愈 (Hotkey Conflict Doctor)
                if (op.Equals("diagnoseHotkeys", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        var reports = HotkeyConflictManager.DiagnoseAllHotkeys();
                        string json = JsonHelper.ToJson(reports);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                                _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.onHotkeyDiagnosticsLoaded) window.onHotkeyDiagnosticsLoaded(decodeURIComponent(escape(atob('{0}'))));", b64));
                            }
                        }));
                    });
                    return;
                }

                if (op.Equals("applyRecommendedHotkey", StringComparison.OrdinalIgnoreCase))
                {
                    string cfgKey = JsonHelper.GetString(msg, "configKey");
                    string newKey = JsonHelper.GetString(msg, "newKey");
                    if (!string.IsNullOrEmpty(cfgKey) && !string.IsNullOrEmpty(newKey))
                    {
                        var settings = AppSettings.Instance;
                        string oldKey = null;
                        if (cfgKey == "masterHotKey") { oldKey = settings.MasterHotKey; settings.MasterHotKey = newKey; }
                        else if (cfgKey == "penHotKey") { oldKey = settings.PenHotKey; settings.PenHotKey = newKey; }
                        else if (cfgKey == "ocrHotKey") { oldKey = settings.OcrHotKey; settings.OcrHotKey = newKey; }
                        else if (cfgKey == "quickHotKey") { oldKey = settings.QuickHotKey; settings.QuickHotKey = newKey; }
                        else if (cfgKey == "fsrsHotKey") { oldKey = settings.FsrsHotKey; settings.FsrsHotKey = newKey; }
                        else if (cfgKey == "clipHotKey") { oldKey = settings.ClipHotKey; settings.ClipHotKey = newKey; }
                        if (oldKey == null) return;

                        if (!settings.Save())
                        {
                            RestoreHotkeySetting(settings, cfgKey, oldKey);
                            if (OnNotificationRequested != null) OnNotificationRequested("快捷键冲突自愈", "❌ 快捷键设置保存失败，原键位保持不变。请检查磁盘权限后重试。");
                            return;
                        }
                        if (!XiaoLv.Desktop.Program.ReRegisterConfiguredHotKeys())
                        {
                            RestoreHotkeySetting(settings, cfgKey, oldKey);
                            settings.Save();
                            if (OnNotificationRequested != null) OnNotificationRequested("快捷键冲突自愈", "❌ 快捷键未能重新注册，已恢复原键位。");
                            return;
                        }
                        SoundPlayerHelper.PlayFeedback();
                        if (OnNotificationRequested != null)
                        {
                            OnNotificationRequested("快捷键冲突自愈", string.Format("✓ 已成功将【{0}】安全切换为推荐键位: {1}", cfgKey, newKey));
                        }

                        // 异步重新体检刷新前端状态
                        ThreadPool.QueueUserWorkItem(st =>
                        {
                            Thread.Sleep(200);
                            var reports = HotkeyConflictManager.DiagnoseAllHotkeys();
                            string json = JsonHelper.ToJson(reports);
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_webView != null && _webView.CoreWebView2 != null)
                                {
                                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                                    _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.onHotkeyDiagnosticsLoaded) window.onHotkeyDiagnosticsLoaded(decodeURIComponent(escape(atob('{0}'))));", b64));
                                }
                            }));
                        });
                    }
                    return;
                }

                if (name == "指令码" && !string.IsNullOrEmpty(val))
                {
                    Hide();
                    if (OnSubActionRequested != null) OnSubActionRequested(val, "");
                    return;
                }
                if (name == "后续动作" && !string.IsNullOrEmpty(val))
                {
                    if (val.StartsWith("sub:", StringComparison.OrdinalIgnoreCase))
                    {
                        Hide();
                        if (OnSubActionRequested != null) OnSubActionRequested(val.Substring(4).Trim(), "");
                        return;
                    }
                    if (val.StartsWith("runaction:", StringComparison.OrdinalIgnoreCase))
                    {
                        Hide();
                        if (OnSubActionRequested != null) OnSubActionRequested(val.Substring(10).Trim(), "");
                        return;
                    }
                }
            }
            catch { }
        }

        public void InspectExternalFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            string fullPath = Path.GetFullPath(filePath);
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();

            ShowPanel();

            ThreadPool.QueueUserWorkItem(s =>
            {
                try
                {
                    if (ext == ".pdf")
                    {
                        var summary = PdfLightParser.InspectPdf(fullPath);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onPdfInspectionLoaded) window.onPdfInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                            }
                        });
                    }
                    else if (ext == ".epub")
                    {
                        var summary = EpubLightParser.InspectEpub(fullPath);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onEpubInspectionLoaded) window.onEpubInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                            }
                        });
                    }
                    else if (ext == ".xmind")
                    {
                        var summary = XMindLightParser.InspectXMind(fullPath);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onXMindInspectionLoaded) window.onXMindInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                            }
                        });
                    }
                    else if (ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".mp4" || ext == ".mkv" || ext == ".mov")
                    {
                        var summary = MediaLightParser.InspectMedia(fullPath);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onMediaInspectionLoaded) window.onMediaInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                            }
                        });
                    }
                    else if (ext == ".docx" || ext == ".xlsx" || ext == ".pptx")
                    {
                        var summary = OfficeOpenXmlParser.InspectFile(fullPath);
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOfficeInspectionLoaded) window.onOfficeInspectionLoaded(" + JsonHelper.ToJson(summary) + ");");
                            }
                        });
                    }
                }
                catch { }
            });
        }

        public void ShowPanel(string initialQuery = null)
        {
            // 1. 0ms 瞬间上屏与聚焦（秒开核心：从挂起态迅速唤醒 WebView2）
            Opacity = 1.0;
            ResumeWebViewMemory();

            // 多显示器自适应：根据鼠标当前所在屏幕与 Per-Monitor DPI 动态居中
            try
            {
                if (WindowState == WindowState.Normal)
                {
                    WindowPositionHelper.CenterOnCursorScreen(this);
                }
            }
            catch { }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Show();
            Topmost = false;
            Topmost = true;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
            }
            catch { }
            Activate();
            Focus();
            if (_webView != null) _webView.Focus();

            if (_isInitialized && _isPageLoaded && _webView.CoreWebView2 != null)
            {
                if (!string.IsNullOrEmpty(initialQuery))
                {
                    string queryJson = JsonHelper.ToJson(initialQuery);
                    if (initialQuery == "@onboard")
                    {
                        // 托盘「新手上路」专用通道：打开面板并滑出 5 步上手清单
                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.openOnboardingTasksModal) { window.openOnboardingTasksModal(); } else if(window.insertPrefix) { window.insertPrefix('onboard:'); }");
                    }
                    else
                    {
                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.insertPrefix) window.insertPrefix(" + queryJson + ");");
                    }
                }
                else
                {
                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onPanelActivated) window.onPanelActivated();");
                }
            }
            else if (!_isInitialized || !_isPageLoaded)
            {
                NavigateToPanel();
            }

            // 2. 异步后台探针与前台感知（彻底解耦 UI 线程，0ms 极速响应）
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    var snapshot = ContextPerception.CaptureForegroundContextFast();
                    if (snapshot != null && snapshot.Hwnd != IntPtr.Zero)
                    {
                        _previousForegroundHwnd = snapshot.Hwnd;
                        LastExternalForegroundHwnd = snapshot.Hwnd;
                    }
                }
                catch { }

                try
                {
                    var htmlPath = EmbeddedResourceManager.ResolveAssetPath("panel.html");
                    var panelDir = Path.GetDirectoryName(htmlPath);
                    RefreshLocalRuntimeState(panelDir);

                    string statePath = Path.Combine(panelDir, ".runtime-state.js");
                    string jsCode = "";
                    if (File.Exists(statePath))
                    {
                        jsCode = File.ReadAllText(statePath, Encoding.UTF8);
                    }
                    var s = AppSettings.Instance;
                    var storageInfo = new { dataDir = EmbeddedResourceManager.AppDataDir, isPortable = EmbeddedResourceManager.IsPortableMode };
                    int queueCount = OfflineQueueManager.GetQueueCount();
                    var settingsJs = "window.__DESKTOP_SETTINGS__ = " + JsonHelper.ToJson(s) + ";\nwindow.__STORAGE_INFO__ = " + JsonHelper.ToJson(storageInfo) + ";\nwindow.__OFFLINE_QUEUE_COUNT__ = " + queueCount + ";";
                    var script = jsCode + "\n" + settingsJs + "\nif(window.initLiveTelemetry) window.initLiveTelemetry();";

                    Dispatcher.Invoke(() =>
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                        {
                            _webView.CoreWebView2.ExecuteScriptAsync(script);
                        }
                    });
                }
                catch { }
            });
        }

        private void NavigateToPanel()
        {
            var htmlPath = EmbeddedResourceManager.ResolveAssetPath("panel.html");

            if (File.Exists(htmlPath))
            {
                var fileUri = "file:///" + htmlPath.Replace('\\', '/');
                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.Navigate(fileUri);
                }
            }
        }

        private void RefreshLocalRuntimeState(string dir)
        {
            try
            {
                string syUrl = "";
                string token = "";
                SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
                SuperPanelMain.RefreshRuntimeStateFile(syUrl, token, dir);
            }
            catch
            {
                try
                {
                    var stateFile = Path.Combine(dir, ".runtime-state.js");
                    if (!File.Exists(stateFile))
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("window.__OMNI_RUNTIME__ = {");
                        sb.AppendLine("  kernelPort: 6806,");
                        sb.AppendLine("  mcpPort: 18765,");
                        sb.AppendLine("  docCount: 0,");
                        sb.AppendLine("  plugins: []");
                        sb.AppendLine("};");
                        File.WriteAllText(stateFile, sb.ToString(), new UTF8Encoding(false));
                    }
                }
                catch { }
            }
        }
        private async void SuspendWebViewMemory()
        {
            try
            {
                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                    try { await _webView.CoreWebView2.ExecuteScriptAsync("if(window.onPanelDeactivated) window.onPanelDeactivated();"); } catch { }
                    try
                    {
                        await _webView.CoreWebView2.TrySuspendAsync();
                    }
                    catch { }
                }
            }
            catch { }

            // 窗口隐藏 500ms 后执行深度工作集压缩与垃圾回收
            MemoryOptimizer.ScheduleTrim(500);
        }

        private void ResumeWebViewMemory()
        {
            try
            {
                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    if (_webView.CoreWebView2.IsSuspended)
                    {
                        _webView.CoreWebView2.Resume();
                    }
                    _webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                }
            }
            catch { }
        }

        public void NotifyOfficeFileChanged(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    var pdfSummary = PdfLightParser.InspectPdf(filePath);
                    Dispatcher.Invoke(() =>
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                        {
                            string js = string.Format("if(window.onPdfInspectionLoaded && window.currentPdfSummary && window.currentPdfSummary.FilePath === {0}) window.onPdfInspectionLoaded({1});", JsonHelper.ToJson(filePath), JsonHelper.ToJson(pdfSummary));
                            _webView.CoreWebView2.ExecuteScriptAsync(js);
                        }
                    });
                }
                else if (ext == ".epub")
                {
                    var epubSummary = EpubLightParser.InspectEpub(filePath);
                    Dispatcher.Invoke(() =>
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                        {
                            string js = string.Format("if(window.onEpubInspectionLoaded && window.currentEpubSummary && window.currentEpubSummary.FilePath === {0}) window.onEpubInspectionLoaded({1});", JsonHelper.ToJson(filePath), JsonHelper.ToJson(epubSummary));
                            _webView.CoreWebView2.ExecuteScriptAsync(js);
                        }
                    });
                }
                else if (ext == ".xmind")
                {
                    var xmindSummary = XMindLightParser.InspectXMind(filePath);
                    Dispatcher.Invoke(() =>
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                        {
                            string js = string.Format("if(window.onXMindInspectionLoaded && window.currentXMindSummary && window.currentXMindSummary.FilePath === {0}) window.onXMindInspectionLoaded({1});", JsonHelper.ToJson(filePath), JsonHelper.ToJson(xmindSummary));
                            _webView.CoreWebView2.ExecuteScriptAsync(js);
                        }
                    });
                }
                else
                {
                    var summary = OfficeOpenXmlParser.InspectFile(filePath);
                    Dispatcher.Invoke(() =>
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                        {
                            string js = string.Format("if(window.onOfficeInspectionLoaded && window.currentOfficeDocSummary && window.currentOfficeDocSummary.FilePath === {0}) window.onOfficeInspectionLoaded({1});", JsonHelper.ToJson(filePath), JsonHelper.ToJson(summary));
                            _webView.CoreWebView2.ExecuteScriptAsync(js);
                        }
                    });
                }
            }
            catch { }
        }
    }
}
