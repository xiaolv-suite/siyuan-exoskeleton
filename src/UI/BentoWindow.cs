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

        public event Action<string, string> OnSubActionRequested;
        public event Action<string> OnSaveSettingsRequested;
        public event Action<string, string> OnNotificationRequested;

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
                        Hide();
                        string targetDoc = _currentInspectedDocId;
                        string targetDocTitle = _currentInspectedDocTitle;
                        ThreadPool.QueueUserWorkItem(st =>
                        {
                            foreach (var f in files)
                            {
                                FileDispatcher.DispatchFileToDocument(f, targetDoc, targetDocTitle, (title, msg) =>
                                {
                                    if (OnNotificationRequested != null) OnNotificationRequested(title, msg);
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
                    Hide();
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
                // 失去焦点时自动隐藏，保持轻量呼吸感
                Hide();
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

                // 0. 窗口拖拽直通
                if (op.Equals("dragMove", StringComparison.OrdinalIgnoreCase))
                {
                    try { DragMove(); } catch { }
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

                // 3. 毫秒级原生块搜索 (b: 语法)
                if (op.Equals("searchBlocks", StringComparison.OrdinalIgnoreCase))
                {
                    string kw = JsonHelper.GetString(msg, "keyword");
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(kw))
                            {
                                string safeLike = kw.Replace("'", "''");
                                string sql = "SELECT id, root_id, content, hpath, type, updated FROM blocks " +
                                             "WHERE content LIKE '%" + safeLike + "%' AND type NOT IN ('d') " +
                                             "ORDER BY (CASE " +
                                             "  WHEN content = '" + safeLike + "' THEN 100 " +
                                             "  WHEN content LIKE '" + safeLike + "%' THEN 60 " +
                                             "  WHEN type = 'h' THEN 40 " +
                                             "  WHEN type = 'p' THEN 20 " +
                                             "  ELSE 10 " +
                                             "END) DESC, updated DESC LIMIT 30";
                                var rows = await SiYuanClient.SqlAsync(sql);
                                Dispatcher.Invoke(() =>
                                {
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onBlockSearchResults) window.onBlockSearchResults(" + JsonHelper.ToJson(new { code = 0, data = rows }) + ");");
                                });
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
                    Hide();
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        if (cmd.Equals("syfsrs", StringComparison.OrdinalIgnoreCase) || cmd.Equals("fsrs", StringComparison.OrdinalIgnoreCase))
                        {
                            XiaoLv.Desktop.Program.StartFsrsReview();
                            return;
                        }
                        if (OnSubActionRequested != null)
                        {
                            OnSubActionRequested(cmd, text);
                        }
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
                    string avName = JsonHelper.GetString(msg, "avName", "属性视图");
                    string rowContent = JsonHelper.GetString(msg, "title");
                    Hide();
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try
                        {
                            var conn = SiYuanClient.GetConnectionInfo(false);
                            var res = WebToAvDatabaseMain.InsertAvRow(conn.Url, conn.Token, avId, rowContent);
                            if (res.Success)
                            {
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

                // 12.5 离岸队列详情检视与手动回写
                if (op.Equals("getOfflineQueue", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        var items = OfflineQueueManager.GetQueueItems();
                        var payload = new
                        {
                            count = items.Count,
                            items = items
                        };
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync("if(window.onOfflineQueueLoaded) window.onOfflineQueueLoaded(" + JsonHelper.ToJson(payload) + ");");
                            }
                        });
                    });
                    return;
                }

                if (op.Equals("flushOfflineQueue", StringComparison.OrdinalIgnoreCase))
                {
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        string syUrl = "";
                        string token = "";
                        SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
                        bool ok = OfflineQueueManager.FlushQueue(syUrl, token, (title, text) =>
                        {
                            if (OnNotificationRequested != null) OnNotificationRequested(title, text);
                        });
                        int remaining = OfflineQueueManager.GetQueueCount();
                        Dispatcher.Invoke(() =>
                        {
                            if (_webView != null && _webView.CoreWebView2 != null)
                            {
                                _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.updateOfflineQueueBadge) window.updateOfflineQueueBadge({{ count: {0} }});", remaining));
                            }
                        });
                    });
                    return;
                }

                if (op.Equals("clearOfflineQueue", StringComparison.OrdinalIgnoreCase))
                {
                    OfflineQueueManager.ClearQueue();
                    if (_webView != null && _webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.updateOfflineQueueBadge) window.updateOfflineQueueBadge({ count: 0 });");
                    }
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
                                      "WHERE b.subtype = 't' AND b.content LIKE '%[ ]%' AND b.content LIKE '%" + safeLike + "%' " +
                                      "ORDER BY b.updated DESC LIMIT 35";
                            }
                            else
                            {
                                sql = "SELECT b.id, b.root_id, b.content, b.hpath, b.updated, doc.content AS doc_title FROM blocks b " +
                                      "LEFT JOIN blocks doc ON doc.id = b.root_id " +
                                      "WHERE b.subtype = 't' AND b.content LIKE '%[ ]%' " +
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
                                string doneText;
                                if (taskContent.Contains("- [ ]")) doneText = taskContent.Replace("- [ ]", "- [x]");
                                else if (taskContent.Contains("[ ]")) doneText = taskContent.Replace("[ ]", "[x]");
                                else doneText = "- [x] " + taskContent.TrimStart('-', '*', ' ');

                                var res = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                                {
                                    id = taskId,
                                    dataType = "markdown",
                                    data = doneText
                                });

                                if (res.Success)
                                {
                                    SoundPlayerHelper.PlayFeedback();
                                    if (OnNotificationRequested != null)
                                    {
                                        OnNotificationRequested("思源外骨骼 · 待办管理", "✓ 已在思源笔记中勾选完成该待办！");
                                    }
                                }
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

                // 20. 兼容旧版 name/value 格式（如指令码、后续动作）
                string name = JsonHelper.GetString(msg, "name");
                string val = JsonHelper.GetString(msg, "value").Trim();
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
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        FileDispatcher.OpenWithPreferredApp(p);
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
                    if (!string.IsNullOrEmpty(q))
                    {
                        var pref = AppSettings.Instance.PreferredSearchEngine;
                        ToolDetector.OpenInSearchTool(q, pref);
                    }
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

                // 22. 触发网页极速剪藏卡 (支持双态剪藏与即时思考批注)
                if (op.Equals("webClip", StringComparison.OrdinalIgnoreCase) || op.Equals("executeWebClip", StringComparison.OrdinalIgnoreCase))
                {
                    string mode = JsonHelper.GetString(msg, "mode");
                    if (string.IsNullOrEmpty(mode)) mode = "card";
                    string annotation = JsonHelper.GetString(msg, "annotation");
                    if (string.IsNullOrEmpty(annotation)) annotation = JsonHelper.GetString(msg, "text");

                    Hide();
                    SmartCaptureMain.ExecuteWebClip((t, m) =>
                    {
                        if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                    }, annotation, mode);
                    return;
                }

                // 22.5 融合前置大模型提炼的智能网页剪藏
                if (op.Equals("executeWebClipWithAi", StringComparison.OrdinalIgnoreCase))
                {
                    string mode = JsonHelper.GetString(msg, "mode");
                    if (string.IsNullOrEmpty(mode)) mode = "card";
                    Hide();
                    SmartCaptureMain.ExecuteWebClipWithAi((t, m) =>
                    {
                        if (OnNotificationRequested != null) OnNotificationRequested(t, m);
                    }, mode);
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
                        if (cfgKey == "masterHotKey") settings.MasterHotKey = newKey;
                        else if (cfgKey == "penHotKey") settings.PenHotKey = newKey;
                        else if (cfgKey == "ocrHotKey") settings.OcrHotKey = newKey;
                        else if (cfgKey == "quickHotKey") settings.QuickHotKey = newKey;
                        else if (cfgKey == "fsrsHotKey") settings.FsrsHotKey = newKey;
                        else if (cfgKey == "clipHotKey") settings.ClipHotKey = newKey;

                        settings.Save();
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

            Show();
            Activate();
            Focus();
            if (_webView != null) _webView.Focus();

            if (_isInitialized && _isPageLoaded && _webView.CoreWebView2 != null)
            {
                if (!string.IsNullOrEmpty(initialQuery))
                {
                    _webView.CoreWebView2.ExecuteScriptAsync(string.Format("if(window.insertPrefix) window.insertPrefix('{0}');", initialQuery.Replace("'", "\\'")));
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
    }
}
