using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    /// <summary>
    /// 思源外骨骼 · 选区微光药丸 (Selection Pop-Pill · Content-Aware 智能感知版)
    /// 1. 划选文字后，在光标右上角浮现 118x28px 极简毛玻璃微胶囊；
    /// 2. 内容智能感知变形：
    ///    - URL 链接 -> 自动变形为 [🌐 剪藏网址]；
    ///    - 待办意图 (!/todo) -> 自动变形为 [📋 存为待办]；
    ///    - 外文学术语篇 -> 第2键自动变形为 [🌐 译] (AI学术翻译此段)；
    ///    - 普通文本 -> 保持 [📝 记入日记]，带字数徽标；
    /// 3. WS_EX_NOACTIVATE 强保护：绝不抢占宿主窗口的键盘与输入焦点；
    /// 4. 距离衰减与外部单击退避：光标远离 >165px 自动淡出，外部单击 0ms 瞬间消隐，键盘打字 0ms 瞬间消隐。
    /// </summary>
    public class SelectionPillWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private string _capturedText = "";
        private DispatcherTimer _autoHideTimer;
        private DispatcherTimer _proximityTimer;
        private Border _rootBorder;
        private ScaleTransform _scaleTransform;

        // 动态变形的按钮引用
        private TextBlock _firstBtnIcon;
        private Border _firstBtnBorder;
        private TextBlock _secondBtnIcon;
        private Border _secondBtnBorder;
        private Border _moreBtnBorder;
        private TextBlock _badgeText;

        public Action<string> OnSaveToDailyRequested { get; set; }
        public Action<string> OnSaveUrlRequested { get; set; }
        public Action<string> OnSaveTaskRequested { get; set; }
        public Action<string> OnRouteToAiRequested { get; set; }
        public Action<string> OnTranslateRequested { get; set; }
        public Action<string> OnExpandToCapsuleRequested { get; set; }
        public Action<string> OnSearchNoteRequested { get; set; }

        public SelectionPillWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;

            Width = 118;
            Height = 28;

            BuildUI();

            _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            _autoHideTimer.Tick += (s, e) =>
            {
                _autoHideTimer.Stop();
                if (!IsMouseOver) FadeOutAndHide();
            };

            // 距离衰减检测定时器：鼠标远离药丸中心 >165px 时自动平滑淡出退避，不遮挡阅读后文
            _proximityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _proximityTimer.Tick += (s, e) =>
            {
                if (!IsVisible) { _proximityTimer.Stop(); return; }
                if (IsMouseOver) return;

                POINT pt;
                if (GetCursorPos(out pt))
                {
                    double scale = WindowPositionHelper.GetDpiScaleForPoint(pt.X, pt.Y);
                    if (scale <= 0.1) scale = 1.0;
                    double curDipX = pt.X / scale;
                    double curDipY = pt.Y / scale;

                    double cx = Left + Width / 2.0;
                    double cy = Top + Height / 2.0;
                    double dx = curDipX - cx;
                    double dy = curDipY - cy;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist > 165)
                    {
                        _proximityTimer.Stop();
                        FadeOutAndHide();
                    }
                }
            };

            MouseEnter += (s, e) =>
            {
                _autoHideTimer.Stop();
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;
            };

            MouseLeave += (s, e) =>
            {
                _autoHideTimer.Interval = TimeSpan.FromMilliseconds(1000);
                _autoHideTimer.Start();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }
            catch { }
        }

        private void BuildUI()
        {
            bool isLight = (AppSettings.Instance.DefaultTheme == "light");
            string bgColor = isLight ? "#f8fafcf2" : "#0f172af2";
            string borderColor = isLight ? "#0284c788" : "#38bdf888";

            _scaleTransform = new ScaleTransform(1.0, 1.0);

            _rootBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                ClipToBounds = true,
                Padding = new Thickness(4, 2, 4, 2),
                RenderTransform = _scaleTransform,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            // 快捷特性：中键点击微光药丸任意处，直接在思源全库秒搜选中词 (查词/反查笔记)
            _rootBorder.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    string text = _capturedText;
                    Hide();
                    if (!string.IsNullOrWhiteSpace(text) && OnSearchNoteRequested != null)
                    {
                        OnSearchNoteRequested(text);
                    }
                    e.Handled = true;
                }
            };

            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 第 1 键：动态内容感知按钮
            _firstBtnBorder = CreatePillButton("📝", "将选中文本直接存入思源今日日记", "#10b981", () =>
            {
                string text = _capturedText;
                Hide();
                if (IsUrlIntent(text))
                {
                    if (OnSaveUrlRequested != null) OnSaveUrlRequested(text);
                    else if (OnSaveToDailyRequested != null) OnSaveToDailyRequested(text);
                }
                else if (IsTodoIntent(text))
                {
                    if (OnSaveTaskRequested != null) OnSaveTaskRequested(text);
                    else if (OnSaveToDailyRequested != null) OnSaveToDailyRequested(text);
                }
                else
                {
                    if (OnSaveToDailyRequested != null) OnSaveToDailyRequested(text);
                }
            }, out _firstBtnIcon);

            // 第 2 键：AI 问答 / 翻译按钮 (智能外文感知)
            _secondBtnBorder = CreatePillButton("🤖", "复制选中文本并路由至 AI 客户端 (豆包/千问/Kimi)", "#38bdf8", () =>
            {
                string text = _capturedText;
                Hide();
                if (IsEnglishIntent(text) && OnTranslateRequested != null)
                {
                    OnTranslateRequested(text);
                }
                else
                {
                    if (OnRouteToAiRequested != null) OnRouteToAiRequested(text);
                }
            }, out _secondBtnIcon);

            _moreBtnBorder = CreatePillButton("⋯", "展开完整随身划笔胶囊 (1~8 盲打分发与双链雷达 · 中键秒搜库内笔记)", "#94a3b8", () =>
            {
                string text = _capturedText;
                Hide();
                if (OnExpandToCapsuleRequested != null) OnExpandToCapsuleRequested(text);
            });

            // 微型字数徽标
            _badgeText = new TextBlock
            {
                Text = "",
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#0284c7" : "#38bdf8")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 3, 0)
            };

            sp.Children.Add(_firstBtnBorder);
            sp.Children.Add(CreateDivider());
            sp.Children.Add(_secondBtnBorder);
            sp.Children.Add(CreateDivider());
            sp.Children.Add(_moreBtnBorder);
            sp.Children.Add(_badgeText);

            _rootBorder.Child = sp;
            Content = _rootBorder;
        }

        private Border CreatePillButton(string icon, string tooltip, string hoverColor, Action onClick)
        {
            TextBlock tb;
            return CreatePillButton(icon, tooltip, hoverColor, onClick, out tb);
        }

        private Border CreatePillButton(string icon, string tooltip, string hoverColor, Action onClick, out TextBlock outTb)
        {
            var b = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(5, 1, 5, 1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            ToolTipService.SetInitialShowDelay(b, 80);
            ToolTipService.SetBetweenShowDelay(b, 50);
            ToolTipService.SetShowDuration(b, 10000);
            var tb = new TextBlock
            {
                Text = icon,
                FontSize = 11,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            b.Child = tb;
            outTb = tb;

            b.MouseEnter += (s, e) =>
            {
                b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
            };
            b.MouseLeave += (s, e) =>
            {
                b.Background = Brushes.Transparent;
            };
            b.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (onClick != null) onClick();
            };
            return b;
        }

        private Border CreateDivider()
        {
            return new Border
            {
                Width = 1,
                Height = 11,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                Margin = new Thickness(1, 0, 1, 0)
            };
        }

        private static bool IsUrlIntent(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTodoIntent(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.StartsWith("!") || text.StartsWith("！") ||
                   text.StartsWith("todo:", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("- [ ]") || text.StartsWith("* [ ]");
        }

        private static bool IsEnglishIntent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.Length < 3 || IsUrlIntent(t) || IsTodoIntent(t)) return false;
            int letters = 0, asciiLetters = 0;
            foreach (char c in t)
            {
                if (char.IsLetter(c))
                {
                    letters++;
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) asciiLetters++;
                }
            }
            return letters >= 3 && ((double)asciiLetters / letters) >= 0.85;
        }

        public void ShowNearCursor(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _capturedText = text.Trim();

            // 智能内容感知变形第 1 键
            if (IsUrlIntent(_capturedText))
            {
                _firstBtnIcon.Text = "🌐";
                _firstBtnBorder.ToolTip = "一键收藏/剪藏此网址到思源";
                _badgeText.Text = "URL";
            }
            else if (IsTodoIntent(_capturedText))
            {
                _firstBtnIcon.Text = "📋";
                _firstBtnBorder.ToolTip = "将此条作为待办任务存入今日日记";
                _badgeText.Text = "待办";
            }
            else
            {
                _firstBtnIcon.Text = "📝";
                _firstBtnBorder.ToolTip = string.Format("将选区 ({0} 字) 存入思源今日日记", _capturedText.Length);
                _badgeText.Text = _capturedText.Length > 99 ? "99+" : (_capturedText.Length + "字");
            }

            // 智能感知第 2 键 (外文语篇变形为翻译)
            if (IsEnglishIntent(_capturedText))
            {
                _secondBtnIcon.Text = "🌐 译";
                _secondBtnBorder.ToolTip = "AI 学术翻译此段 (中英双语对照)";
            }
            else
            {
                _secondBtnIcon.Text = "🤖";
                _secondBtnBorder.ToolTip = "复制选中文本并路由至 AI 客户端 (豆包/千问/Kimi)";
            }

            POINT pt;
            if (!GetCursorPos(out pt)) return;

            // 基于多显示器与 Per-Monitor DPI 精确换算，彻底杜绝高分屏/副屏坐标错乱
            try
            {
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
                var workArea = screen.WorkingArea;

                double scale = WindowPositionHelper.GetDpiScaleForPoint(pt.X, pt.Y);
                if (scale <= 0.1) scale = 1.0;

                double dipWorkLeft = workArea.Left / scale;
                double dipWorkTop = workArea.Top / scale;
                double dipWorkWidth = workArea.Width / scale;
                double dipWorkHeight = workArea.Height / scale;

                double curDipX = pt.X / scale;
                double curDipY = pt.Y / scale;

                // 默认定位在光标右上角 (curDipX + 12, curDipY - Height - 8)
                double targetX = curDipX + 12;
                double targetY = curDipY - Height - 8;

                // 1. 顶边防截断智能翻转：若光标上方空间不足，平滑翻转至光标正下方
                if (targetY < dipWorkTop + 6)
                {
                    targetY = curDipY + 18;
                }

                // 2. 右侧防截断翻转：若右侧空间不足，翻转至光标左侧
                if (targetX + Width > dipWorkLeft + dipWorkWidth - 6)
                {
                    targetX = curDipX - Width - 10;
                }

                // 3. 安全夹紧在当前屏幕工作区可见范围内
                if (targetX < dipWorkLeft + 6) targetX = dipWorkLeft + 6;
                if (targetY < dipWorkTop + 6) targetY = dipWorkTop + 6;
                if (targetX + Width > dipWorkLeft + dipWorkWidth - 6) targetX = dipWorkLeft + dipWorkWidth - Width - 6;
                if (targetY + Height > dipWorkTop + dipWorkHeight - 6) targetY = dipWorkTop + dipWorkHeight - Height - 6;

                Left = targetX;
                Top = targetY;
            }
            catch
            {
                Left = pt.X + 12;
                Top = pt.Y - Height - 10;
            }

            // 柔和微缩放淡入动效 (从 0.94 平滑长出)
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scaleTransform.ScaleX = 0.94;
            _scaleTransform.ScaleY = 0.94;

            var scaleAnim = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            Show();

            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(2600);
            _autoHideTimer.Stop();
            _autoHideTimer.Start();

            _proximityTimer.Stop();
            _proximityTimer.Start();
        }

        public void DismissIfOutside(int screenX, int screenY)
        {
            if (!IsVisible) return;
            try
            {
                double scale = WindowPositionHelper.GetDpiScaleForPoint(screenX, screenY);
                if (scale <= 0.1) scale = 1.0;
                double dipX = screenX / scale;
                double dipY = screenY / scale;

                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : Height;
                if (dipX >= Left && dipX <= Left + w &&
                    dipY >= Top && dipY <= Top + h)
                {
                    return;
                }
            }
            catch { }
            Hide();
        }

        public void DismissIfTyping(int vkCode)
        {
            if (!IsVisible) return;
            // 过滤修饰键：Shift(0x10), Ctrl(0x11), Alt(0x12), Win(0x5B/5C), CapsLock(0x14)
            if (vkCode == 0x10 || vkCode == 0x11 || vkCode == 0x12 || vkCode == 0x5B || vkCode == 0x5C || vkCode == 0x14)
            {
                return;
            }
            Hide();
        }

        private void FadeOutAndHide()
        {
            _proximityTimer.Stop();
            var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180));
            anim.Completed += (s, e) =>
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;
                Hide();
            };
            BeginAnimation(OpacityProperty, anim);
        }
    }
}
