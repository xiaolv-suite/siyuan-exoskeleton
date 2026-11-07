using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    public class DialogWindow : Window
    {
        private WebView2 _webView;
        private bool _isInitialized = false;
        private string _dialogUrl = "";
        private TaskCompletionSource<string> _tcs;

        public DialogWindow()
        {
            Title = "思源外骨骼 · 交互工作台";
            Width = 520;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;

            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f172a")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569")),
                BorderThickness = new Thickness(1.5),
                ClipToBounds = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 32,
                    ShadowDepth = 8,
                    Opacity = 0.55
                }
            };

            _webView = new WebView2();
            border.Child = _webView;
            Content = border;

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    CloseWithResult("");
                    e.Handled = true;
                }
            };

            Loaded += DialogWindow_Loaded;
        }

        private async void DialogWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) return;
            try
            {
                var env = await SharedWebView2Environment.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _isInitialized = true;

                if (!string.IsNullOrEmpty(_dialogUrl))
                {
                    _webView.CoreWebView2.Navigate(_dialogUrl);
                }
            }
            catch { }
        }

        private string _lastVarValue = "";

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
                string action = JsonHelper.GetString(msg, "action");
                if (string.IsNullOrEmpty(action)) action = JsonHelper.GetString(msg, "op");

                if (action == "setVar")
                {
                    string val = JsonHelper.GetString(msg, "value");
                    if (string.IsNullOrEmpty(val) && msg.ContainsKey("data"))
                    {
                        var dataDict = msg["data"] as System.Collections.Generic.Dictionary<string, object>;
                        if (dataDict != null)
                        {
                            val = JsonHelper.GetString(dataDict, "value");
                        }
                    }
                    _lastVarValue = val;
                }
                else if (action == "close")
                {
                    CloseWithResult(_lastVarValue);
                }
                else if (action == "dragWindow")
                {
                    try { DragMove(); } catch { }
                }
            }
            catch { }
        }

        private void CloseWithResult(string res)
        {
            Hide();
            if (_tcs != null && !_tcs.Task.IsCompleted)
            {
                _tcs.SetResult(res);
            }
            MemoryOptimizer.ScheduleTrim(500);
        }

        public Task<string> ShowDialogAsync(string url)
        {
            _lastVarValue = "";
            _dialogUrl = url;
            _tcs = new TaskCompletionSource<string>();

            try
            {
                WindowPositionHelper.CenterOnCursorScreen(this);
            }
            catch { }

            if (_isInitialized && _webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Navigate(_dialogUrl);
            }

            Show();
            Activate();
            Focus();
            _webView.Focus();

            return _tcs.Task;
        }
    }
}
