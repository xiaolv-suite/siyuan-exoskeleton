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
    /// 思源外骨骼 · 选区微光药丸 (Selection Pop-Pill)
    /// 对齐 PopClip / 豆包千问桌面端的两阶段克制划词交互：
    /// 1. 划选文字后，在光标右上角浮现 106x26px 极简毛玻璃微胶囊；
    /// 2. WS_EX_NOACTIVATE 强保护：绝不抢占宿主窗口的键盘与输入焦点；
    /// 3. 内置三键：📝 记日记 / 🤖 问 AI / ⋯ 展开完整划笔大盘；
    /// 4. 极致退避：2.5 秒无操作自动淡出，点击屏幕任意外部区域 0ms 瞬间消隐。
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
        private Border _rootBorder;

        public Action<string> OnSaveToDailyRequested { get; set; }
        public Action<string> OnRouteToAiRequested { get; set; }
        public Action<string> OnExpandToCapsuleRequested { get; set; }

        public SelectionPillWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;

            Width = 106;
            Height = 28;

            BuildUI();

            _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            _autoHideTimer.Tick += (s, e) =>
            {
                _autoHideTimer.Stop();
                if (!IsMouseOver) FadeOutAndHide();
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
            _rootBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f172af2")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38bdf888")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                ClipToBounds = true,
                Padding = new Thickness(4, 2, 4, 2)
            };

            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var btnNote = CreatePillButton("📝", "将选中文本直接存入思源今日日记", "#10b981", () =>
            {
                string text = _capturedText;
                Hide();
                if (OnSaveToDailyRequested != null) OnSaveToDailyRequested(text);
            });

            var btnAi = CreatePillButton("🤖", "复制选中文本并路由至 AI 客户端 (豆包/千问/Kimi)", "#38bdf8", () =>
            {
                string text = _capturedText;
                Hide();
                if (OnRouteToAiRequested != null) OnRouteToAiRequested(text);
            });

            var btnMore = CreatePillButton("⋯", "展开完整随身划笔胶囊 (1~8 盲打分发与双链雷达)", "#94a3b8", () =>
            {
                string text = _capturedText;
                Hide();
                if (OnExpandToCapsuleRequested != null) OnExpandToCapsuleRequested(text);
            });

            sp.Children.Add(btnNote);
            sp.Children.Add(CreateDivider());
            sp.Children.Add(btnAi);
            sp.Children.Add(CreateDivider());
            sp.Children.Add(btnMore);

            _rootBorder.Child = sp;
            Content = _rootBorder;
        }

        private Border CreatePillButton(string icon, string tooltip, string hoverColor, Action onClick)
        {
            var b = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 1, 6, 1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            var tb = new TextBlock
            {
                Text = icon,
                FontSize = 11,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            b.Child = tb;

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
                Height = 12,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                Margin = new Thickness(2, 0, 2, 0)
            };
        }

        public void ShowNearCursor(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _capturedText = text.Trim();

            POINT pt;
            if (!GetCursorPos(out pt)) return;

            // 定位在鼠标指针右上角偏移 10px，避免遮挡光标手势与原选区
            var workArea = SystemParameters.WorkArea;
            double targetX = pt.X + 12;
            double targetY = pt.Y - Height - 10;

            // 屏幕边缘自动翻转保护
            if (targetX + Width > workArea.Right) targetX = pt.X - Width - 10;
            if (targetX < workArea.Left) targetX = workArea.Left + 8;
            if (targetY < workArea.Top) targetY = pt.Y + 20;
            if (targetY + Height > workArea.Bottom) targetY = workArea.Bottom - Height - 8;

            Left = targetX;
            Top = targetY;

            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
            Show();

            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(2500);
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        public void DismissIfOutside(int screenX, int screenY)
        {
            if (!IsVisible) return;
            // 如果鼠标点击点在当前药丸边界内，不关闭
            if (screenX >= Left && screenX <= Left + ActualWidth &&
                screenY >= Top && screenY <= Top + ActualHeight)
            {
                return;
            }
            Hide();
        }

        private void FadeOutAndHide()
        {
            var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(220));
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
