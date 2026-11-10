using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    public class CapsuleWindow : Window
    {
        private WebView2 _webView;
        private bool _isInitialized = false;
        private bool _isPageLoaded = false;
        private string _pendingText = "";
        private IntPtr _previousForegroundHwnd = IntPtr.Zero;
        private string _pendingImagePath = "";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;

        public event Action<string, string> OnCapsuleCommand;
        public event Action<string, string, string> OnCapsuleCommandWithImage;
        public event Action<string> OnOpenMasterRequested;
        public event Action<string, string> OnNotificationRequested;

        public CapsuleWindow()
        {
            Title = "思源外骨骼 · 随身划笔微胶囊";
            Width = 440;
            Height = 204;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;

            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f172a")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10b981")),
                BorderThickness = new Thickness(1.2),
                ClipToBounds = true
            };

            _webView = new WebView2();
            try { _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent; } catch { }
            border.Child = _webView;
            Content = border;

            // 原生拖拽直投支持
            AllowDrop = true;
            DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
            };
            Drop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        Hide();
                        ThreadPool.QueueUserWorkItem(st =>
                        {
                            foreach (var f in files)
                            {
                                FileDispatcher.DispatchFileToSiYuan(f, (title, msg) =>
                                {
                                    if (OnNotificationRequested != null) OnNotificationRequested(title, msg);
                                });
                            }
                        });
                    }
                }
            };

            // 极速秒开秒关：0ms 呈现，后台低功耗挂起
            IsVisibleChanged += (s, e) =>
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

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Hide();
                    e.Handled = true;
                }
            };

            Deactivated += (s, e) =>
            {
                Hide();
            };

            Loaded += CapsuleWindow_Loaded;
        }

        private async void CapsuleWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) return;
            try
            {
                var env = await SharedWebView2Environment.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                _webView.NavigationCompleted += (s, ev) => { _isPageLoaded = true; };
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _isInitialized = true;

                NavigateToCapsule(_pendingText);
            }
            catch { }
        }

        /// <summary>P2-2: 解析 AI 路由目标 JSON ([{name,url}])，取前 N 个有效目标</summary>
        private static System.Collections.Generic.List<Tuple<string, string>> ParseAiRouteTargets(string json)
        {
            var list = new System.Collections.Generic.List<Tuple<string, string>>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                var rows = JsonHelper.ParseList(json);
                if (rows == null) return list;
                foreach (var rowObj in rows)
                {
                    var d = rowObj as System.Collections.Generic.IDictionary<string, object>;
                    if (d == null) continue;
                    string n = JsonHelper.GetString(d, "name", "");
                    string u = JsonHelper.GetString(d, "url", "");
                    if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(u)) list.Add(Tuple.Create(n, u));
                }
            }
            catch { }
            return list;
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = "";
                try { raw = e.TryGetWebMessageAsString(); } catch { }
                if (string.IsNullOrEmpty(raw))
                {
                    try { raw = e.WebMessageAsJson; } catch { }
                }
                if (string.IsNullOrEmpty(raw)) return;

                var msg = JsonHelper.ParseDict(raw);
                string op = JsonHelper.GetString(msg, "op");
                if (op.Equals("close", StringComparison.OrdinalIgnoreCase))
                {
                    Hide();
                    return;
                }

                if (op.Equals("dragWindow", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ReleaseCapture();
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
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

                // 光标穿透直贴双链
                if (op.Equals("pasteAtCaret", StringComparison.OrdinalIgnoreCase))
                {
                    string textToPaste = JsonHelper.GetString(msg, "text");
                    Hide();
                    NativeClipboard.PasteTextAtCaret(_previousForegroundHwnd, textToPaste);
                    return;
                }

                // 携带参数唤起总控面板
                if (op.Equals("openPanelWithQuery", StringComparison.OrdinalIgnoreCase))
                {
                    string q = JsonHelper.GetString(msg, "query");
                    Hide();
                    if (OnOpenMasterRequested != null) OnOpenMasterRequested(q);
                    return;
                }

                // P2-2 划词场景 AI 路由：A 键把选中文本复制为提问并打开默认 AI 客户端 (豆包/千问/Kimi…)
                if (op.Equals("routeAiExternal", StringComparison.OrdinalIgnoreCase))
                {
                    string routeText = JsonHelper.GetString(msg, "text", "").Trim();
                    if (routeText.Length > 800) routeText = routeText.Substring(0, 800);
                    bool launched = false;
                    string targetName = "";
                    try
                    {
                        if (!string.IsNullOrEmpty(routeText))
                        {
                            try { System.Windows.Clipboard.SetText(routeText); } catch { }
                            var targets = ParseAiRouteTargets(AppSettings.Instance.AiRouteTargetsJson);
                            if (targets.Count > 0)
                            {
                                string url = targets[0].Item2;
                                if (url.Contains("{query}")) url = url.Replace("{query}", Uri.EscapeDataString(routeText));
                                url = url.Trim().Replace("\r", "").Replace("\n", "");
                                Uri safeUri;
                                if (Uri.TryCreate(url, UriKind.Absolute, out safeUri) && safeUri != null &&
                                    (safeUri.Scheme == Uri.UriSchemeHttp || safeUri.Scheme == Uri.UriSchemeHttps))
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(safeUri.AbsoluteUri) { UseShellExecute = true });
                                    targetName = targets[0].Item1;
                                    launched = true;
                                }
                            }
                        }
                    }
                    catch { }
                    try
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                        {
                            _webView.CoreWebView2.ExecuteScriptAsync("if(window.onRouteAiDone) window.onRouteAiDone(" + JsonHelper.ToJson(new { ok = launched, name = targetName }) + ");");
                        }
                    }
                    catch { }
                    if (launched)
                    {
                        if (OnNotificationRequested != null) OnNotificationRequested("思源外骨骼 · AI 路由", "🚀 提问已复制，已打开 " + targetName + "，粘贴即问");
                        var closeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                        closeTimer.Tick += (s2, e2) => { closeTimer.Stop(); Hide(); };
                        closeTimer.Start();
                    }
                    return;
                }

                // 提取操作指令 (1~8 或 q，同时兼容顶层 cmd/text 与 setVar 嵌套 data.name / data.value)
                var dataObj = JsonHelper.GetDict(msg, "data") ?? msg;
                string cmd = JsonHelper.GetString(msg, "cmd");
                if (string.IsNullOrEmpty(cmd))
                {
                    string name = JsonHelper.GetString(dataObj, "name");
                    if (name == "输入文本")
                    {
                        string vText = JsonHelper.GetString(dataObj, "value");
                        if (!string.IsNullOrEmpty(vText)) _pendingText = vText;
                        return;
                    }
                    if (name == "操作指令") cmd = JsonHelper.GetString(dataObj, "value");
                }

                string inputTxt = JsonHelper.GetString(msg, "text");
                if (string.IsNullOrEmpty(inputTxt))
                {
                    string name = JsonHelper.GetString(dataObj, "name");
                    if (name == "输入文本") inputTxt = JsonHelper.GetString(dataObj, "value");
                }
                if (string.IsNullOrEmpty(inputTxt)) inputTxt = _pendingText;

                if (cmd.Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    Hide();
                    if (OnOpenMasterRequested != null) OnOpenMasterRequested(inputTxt);
                    return;
                }

                if (!string.IsNullOrEmpty(cmd))
                {
                    bool mustHideForPaste = cmd.StartsWith("6", StringComparison.OrdinalIgnoreCase) || cmd.Equals("case", StringComparison.OrdinalIgnoreCase);
                    bool keepOpen = !mustHideForPaste && JsonHelper.GetBool(msg, "keepOpen", true);
                    if (!keepOpen) Hide();
                    if (OnCapsuleCommandWithImage != null && !string.IsNullOrEmpty(_pendingImagePath))
                    {
                        OnCapsuleCommandWithImage(cmd, inputTxt, _pendingImagePath);
                    }
                    else if (OnCapsuleCommand != null)
                    {
                        OnCapsuleCommand(cmd, inputTxt);
                    }
                    if (keepOpen && _webView != null && _webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onCapsuleActionCompleted) window.onCapsuleActionCompleted(" + JsonHelper.ToJson(new { cmd = cmd, text = inputTxt }) + ");");
                    }
                }
            }
            catch { }
        }

        public void ShowNearCursor(string selectedText)
        {
            ShowNearCursorWithImage(selectedText, "");
        }

        public void ShowNearCursorWithImage(string selectedText, string imagePath)
        {
            _pendingText = selectedText ?? "";
            _pendingImagePath = imagePath ?? "";
            try
            {
                var fg = GetForegroundWindow();
                if (fg != IntPtr.Zero) _previousForegroundHwnd = fg;
            }
            catch { }

            // 认知雷达：异步毫秒级嗅探当前划选词汇在思源知识库中的收录状态
            if (!string.IsNullOrEmpty(_pendingText) && _pendingText.Length >= 2 && _pendingText.Length <= 40)
            {
                string term = _pendingText.Trim();
                ThreadPool.QueueUserWorkItem(async s =>
                {
                    try
                    {
                        string safeTerm = term.Replace("'", "''");
                        string sql = "SELECT b.id, b.content, b.hpath, b.type FROM blocks b WHERE (b.content LIKE '%" + safeTerm + "%' OR b.name LIKE '%" + safeTerm + "%' OR b.alias LIKE '%" + safeTerm + "%') AND b.type IN ('d', 'h', 'p') ORDER BY b.updated DESC LIMIT 1";
                        var rows = await SiYuanClient.SqlAsync(sql);
                        if (rows != null && rows.Count > 0)
                        {
                            var r = rows[0];
                            string hitId = JsonHelper.GetString(r, "id");
                            string hpath = JsonHelper.GetString(r, "hpath");
                            string docTitle = Path.GetFileName(hpath);
                            if (string.IsNullOrEmpty(docTitle)) docTitle = "相关笔记";

                            Dispatcher.Invoke(() =>
                            {
                                if (_isPageLoaded && _webView != null && _webView.CoreWebView2 != null)
                                {
                                    string payload = JsonHelper.ToJson(new { hitId = hitId, docTitle = docTitle, term = term });
                                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.onRadarMatch) window.onRadarMatch(" + payload + ");");
                                }
                            });
                        }

                        // 人脉生态雷达：嗅探是否属于人脉库联系人 (一人一文档)
                        if (term.Length >= 2 && term.Length <= 8 && !term.Contains(" "))
                        {
                            try
                            {
                                string cSql = "SELECT b.id, b.content FROM blocks b WHERE b.type='d' AND (b.hpath LIKE '%/人脉/%' OR b.hpath LIKE '%/联系人/%' OR b.ial LIKE '%custom-type=\"contact\"%' OR b.ial LIKE '%custom-contact%') AND b.content LIKE '%" + safeTerm + "%' LIMIT 1";
                                var cRows = await SiYuanClient.SqlAsync(cSql);
                                if (cRows != null && cRows.Count > 0)
                                {
                                    string contactId = JsonHelper.GetString(cRows[0], "id");
                                    string contactName = JsonHelper.GetString(cRows[0], "content");
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (_isPageLoaded && _webView != null && _webView.CoreWebView2 != null)
                                        {
                                            string payload = JsonHelper.ToJson(new { contactId = contactId, name = contactName });
                                            _webView.CoreWebView2.ExecuteScriptAsync("if(window.onContactMatch) window.onContactMatch(" + payload + ");");
                                        }
                                    });
                                }
                            }
                            catch { }
                        }

                        // 智能超链嗅探 (Smart Paste URL): 嗅探剪贴板是否恰好存有有效网址
                        try
                        {
                            string clipUrl = "";
                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    if (System.Windows.Forms.Clipboard.ContainsText())
                                    {
                                        string clip = System.Windows.Forms.Clipboard.GetText().Trim();
                                        if (clip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                        {
                                            clipUrl = clip;
                                        }
                                    }
                                }
                                catch { }
                            });

                            if (!string.IsNullOrEmpty(clipUrl))
                            {
                                string cleanUrl = TextCleaner.PurifyUrl(clipUrl);
                                Dispatcher.Invoke(() =>
                                {
                                    if (_isPageLoaded && _webView != null && _webView.CoreWebView2 != null)
                                    {
                                        string payload = JsonHelper.ToJson(new { url = cleanUrl, text = term });
                                        _webView.CoreWebView2.ExecuteScriptAsync("if(window.onSmartUrlDetected) window.onSmartUrlDetected(" + payload + ");");
                                    }
                                });
                            }
                        }
                        catch { }
                    }
                    catch { }
                });
            }

            // 获取当前鼠标坐标并自适应多屏幕边缘与 Per-Monitor DPI
            WindowPositionHelper.PlaceNearCursor(this, 15, 20);

            if (_isInitialized && _webView != null && _webView.CoreWebView2 != null)
            {
                if (_isPageLoaded)
                {
                    string b64 = "";
                    try
                    {
                        if (!string.IsNullOrEmpty(_pendingText))
                        {
                            b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(_pendingText));
                        }
                    }
                    catch { }
                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.updateSelectedTextFromBase64) window.updateSelectedTextFromBase64('" + b64 + "');");
                }
                else
                {
                    NavigateToCapsule(_pendingText);
                }
            }

            Show();
            Topmost = false;
            Topmost = true;
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
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
            _webView.Focus();
        }

        private void NavigateToCapsule(string text)
        {
            var htmlPath = EmbeddedResourceManager.ResolveAssetPath("pen-capsule.html");

            if (File.Exists(htmlPath))
            {
                string b64 = "";
                try
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                    }
                }
                catch { }

                var fileUri = "file:///" + htmlPath.Replace('\\', '/') + (!string.IsNullOrEmpty(b64) ? ("#" + b64) : "");
                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.Navigate(fileUri);
                }
            }
        }

        private async void SuspendWebViewMemory()
        {
            try
            {
                if (_isInitialized && _webView != null && _webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                    try
                    {
                        await _webView.CoreWebView2.TrySuspendAsync();
                    }
                    catch { }
                }
            }
            catch { }

            MemoryOptimizer.ScheduleTrim(500);
        }

        private void ResumeWebViewMemory()
        {
            try
            {
                if (_isInitialized && _webView != null && _webView.CoreWebView2 != null)
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
