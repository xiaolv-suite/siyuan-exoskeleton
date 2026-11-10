using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    /// <summary>
    /// 思源外骨骼 · 桌面边缘灵动坞与托放中枢 (Edge Dock)
    /// 1. 极致克制贴边：平时仅 5px 极细半透明钛青发光细条，0 遮挡，不挡滚动条或右上角按钮；
    /// 2. 140ms 移入防误触延时：快速甩鼠标拖动滚动条时绝对不误弹；
    /// 3. 支持垂直拖拽调整高度并记忆位置 (EdgeDockTopRatio)；
    /// 4. 上半部内嵌极速动作矩阵 (⚡速记 / 🔍搜块 / 📋待办 / 📸识字 / 🤖问AI)，点击即开；
    /// 5. 下半部保留文件/超链极速拖放路由 (拾遗/管家/速递)；
    /// 6. Win32 WS_EX_NOACTIVATE 强保护，触碰和点击绝不抢夺前台正在输入的焦点。
    /// </summary>
    public class DropZoneWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private Border _rootBorder;
        private StackPanel _expandedContent;
        private Border _dockHandle;
        private bool _isExpanded = false;
        private DispatcherTimer _collapseTimer;
        private DispatcherTimer _expandHoverTimer;

        // 垂直位置拖动记忆状态
        private bool _isDraggingVertical = false;
        private Point _dragStartMousePos;
        private double _dragStartTop;

        public Action<string, string> OnNotificationRequested { get; set; }
        public Action OnQuickCaptureClicked { get; set; }
        public Action<string> OnOpenPanelWithQuery { get; set; }
        public Action OnSnippingOcrClicked { get; set; }

        public DropZoneWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            AllowDrop = true;

            Width = 5;
            Height = 110;

            PositionAtRightEdge();

            BuildUI();

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _collapseTimer.Tick += (s, e) =>
            {
                _collapseTimer.Stop();
                if (!IsMouseOver && !_isDraggingVertical) Collapse();
            };

            // 140ms 移入防误触延时：鼠标飞速划过右边缘（如拖动滚动条）不展开
            _expandHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _expandHoverTimer.Tick += (s, e) =>
            {
                _expandHoverTimer.Stop();
                if (IsMouseOver && !SelectionHelper.IsFullscreenForeground() && !_isDraggingVertical)
                {
                    Expand();
                }
            };

            MouseEnter += (s, e) =>
            {
                _collapseTimer.Stop();
                _expandHoverTimer.Stop();
                _expandHoverTimer.Start();
            };

            MouseLeave += (s, e) =>
            {
                _expandHoverTimer.Stop();
                _collapseTimer.Stop();
                _collapseTimer.Start();
            };

            DragEnter += (s, e) =>
            {
                e.Effects = DragDropEffects.Copy;
                Expand();
                e.Handled = true;
            };

            DragOver += (s, e) =>
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };

            DragLeave += (s, e) =>
            {
                var pos = e.GetPosition(this);
                if (pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight)
                {
                    Collapse();
                }
            };

            Drop += (s, e) =>
            {
                Collapse();
                try
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    {
                        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                        if (files != null && files.Length > 0)
                        {
                            SoundPlayerHelper.PlayFeedback();
                            DropZoneRouter.RouteFiles(files, OnNotificationRequested);
                        }
                    }
                    else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                    {
                        string text = (string)e.Data.GetData(DataFormats.UnicodeText);
                        if (!string.IsNullOrEmpty(text))
                        {
                            SoundPlayerHelper.PlayFeedback();
                            DropZoneRouter.RouteTextOrUrl(text, OnNotificationRequested);
                        }
                    }
                }
                catch { }
                e.Handled = true;
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

        public void PositionAtRightEdge()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width;

            // 依据 AppSettings.EdgeDockTopRatio 动态居中或复原用户拖拽偏好的高度
            double ratio = AppSettings.Instance.EdgeDockTopRatio;
            if (ratio <= 0.05 || ratio >= 0.95) ratio = 0.5;

            double maxTop = workArea.Bottom - Height;
            double targetTop = workArea.Top + (workArea.Height - Height) * ratio;
            if (targetTop < workArea.Top) targetTop = workArea.Top;
            if (targetTop > maxTop) targetTop = maxTop;
            Top = targetTop;
        }

        private void BuildUI()
        {
            bool isLight = (AppSettings.Instance.DefaultTheme == "light");
            string bgColor = isLight ? "#f8fafce6" : "#0f172ae6";
            string borderColor = isLight ? "#0284c788" : "#38bdf888";
            string handleColor = isLight ? "#0284c766" : "#38bdf866";

            _rootBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor)),
                BorderThickness = new Thickness(1.5, 1, 0, 1),
                CornerRadius = new CornerRadius(10, 0, 0, 10),
                ClipToBounds = true
            };

            var grid = new Grid();

            // 停靠把手 (折叠时展现：5px 极细微发光细条，支持按住拖动调整垂直高度)
            _dockHandle = new Border
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(handleColor)),
                CornerRadius = new CornerRadius(3, 0, 0, 3),
                Cursor = Cursors.SizeNS,
                ToolTip = "按住可上下拖动调整贴边位置"
            };
            WireVerticalDrag(_dockHandle);

            // 展开内容容器
            _expandedContent = new StackPanel
            {
                Margin = new Thickness(10, 8, 8, 8),
                Visibility = Visibility.Collapsed
            };

            // 顶部标题栏 (亦支持上下拖拽微调位置)
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.SizeNS };
            WireVerticalDrag(headerGrid);

            var headerText = new TextBlock
            {
                Text = "⚡ 边缘灵动坞",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? "#0284c7" : "#38bdf8"))
            };
            var closeHint = new TextBlock
            {
                Text = "↕拖动 ◀移出",
                FontSize = 9,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748b")),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            headerGrid.Children.Add(headerText);
            headerGrid.Children.Add(closeHint);
            _expandedContent.Children.Add(headerGrid);

            // 极速动作矩阵 (点击即开)
            var actionGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.RowDefinitions.Add(new RowDefinition());
            actionGrid.RowDefinitions.Add(new RowDefinition());
            actionGrid.RowDefinitions.Add(new RowDefinition());

            var btnQuick = CreateActionButton("⚡ 速记灵感", "#10b981", () =>
            {
                Collapse();
                if (OnQuickCaptureClicked != null) OnQuickCaptureClicked();
            });
            Grid.SetRow(btnQuick, 0); Grid.SetColumn(btnQuick, 0);

            var btnSearch = CreateActionButton("🔍 搜全库", "#38bdf8", () =>
            {
                Collapse();
                if (OnOpenPanelWithQuery != null) OnOpenPanelWithQuery("b:");
            });
            Grid.SetRow(btnSearch, 0); Grid.SetColumn(btnSearch, 1);

            var btnTask = CreateActionButton("📋 待办看板", "#f59e0b", () =>
            {
                Collapse();
                if (OnOpenPanelWithQuery != null) OnOpenPanelWithQuery("t:");
            });
            Grid.SetRow(btnTask, 1); Grid.SetColumn(btnTask, 0);

            var btnSnip = CreateActionButton("📸 截屏OCR", "#a855f7", () =>
            {
                Collapse();
                if (OnSnippingOcrClicked != null) OnSnippingOcrClicked();
            });
            Grid.SetRow(btnSnip, 1); Grid.SetColumn(btnSnip, 1);

            var btnAi = CreateActionButton("🤖 问 AI", "#6366f1", () =>
            {
                Collapse();
                if (OnOpenPanelWithQuery != null) OnOpenPanelWithQuery("ai:");
            });
            Grid.SetRow(btnAi, 2); Grid.SetColumn(btnAi, 0);
            Grid.SetColumnSpan(btnAi, 2);

            actionGrid.Children.Add(btnQuick);
            actionGrid.Children.Add(btnSearch);
            actionGrid.Children.Add(btnTask);
            actionGrid.Children.Add(btnSnip);
            actionGrid.Children.Add(btnAi);
            _expandedContent.Children.Add(actionGrid);

            // 下半部：拖放指引微卡片
            var dropHeader = new TextBlock
            {
                Text = "📥 拖入文件/链接即入思源：",
                FontSize = 9.5,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748b")),
                Margin = new Thickness(0, 2, 0, 4)
            };
            _expandedContent.Children.Add(dropHeader);
            _expandedContent.Children.Add(CreateDropCard("🌐", "网页剪藏", "拖入URL自动提取并读库分拣"));
            _expandedContent.Children.Add(CreateDropCard("📁", "本地文件", "拖入Office/PDF/源码直接归档"));

            grid.Children.Add(_dockHandle);
            grid.Children.Add(_expandedContent);
            _rootBorder.Child = grid;

            Content = _rootBorder;
        }

        private void WireVerticalDrag(FrameworkElement element)
        {
            if (element == null) return;
            element.MouseLeftButtonDown += (s, e) =>
            {
                _isDraggingVertical = true;
                _dragStartMousePos = e.GetPosition(this);
                _dragStartTop = Top;
                element.CaptureMouse();
                e.Handled = true;
            };

            element.MouseMove += (s, e) =>
            {
                if (_isDraggingVertical)
                {
                    Point currentPos = e.GetPosition(this);
                    double deltaY = currentPos.Y - _dragStartMousePos.Y;
                    var workArea = SystemParameters.WorkArea;
                    double newTop = Top + deltaY;
                    if (newTop < workArea.Top) newTop = workArea.Top;
                    if (newTop > workArea.Bottom - Height) newTop = workArea.Bottom - Height;
                    Top = newTop;
                    e.Handled = true;
                }
            };

            element.MouseLeftButtonUp += (s, e) =>
            {
                if (_isDraggingVertical)
                {
                    _isDraggingVertical = false;
                    element.ReleaseMouseCapture();

                    // 保存新的垂直停靠比例
                    var workArea = SystemParameters.WorkArea;
                    double availableHeight = workArea.Height - Height;
                    if (availableHeight > 10)
                    {
                        double ratio = (Top - workArea.Top) / availableHeight;
                        if (ratio < 0.05) ratio = 0.05;
                        if (ratio > 0.95) ratio = 0.95;
                        AppSettings.Instance.EdgeDockTopRatio = ratio;
                        ThreadPool.QueueUserWorkItem(st => AppSettings.Instance.Save());
                    }
                    e.Handled = true;
                }
            };
        }

        private Border CreateActionButton(string label, string colorHex, Action onClick)
        {
            var b = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b88")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand
            };
            var tb = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            b.Child = tb;
            b.MouseEnter += (s, e) =>
            {
                b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                b.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            };
            b.MouseLeave += (s, e) =>
            {
                b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b88"));
                b.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
            };
            b.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (onClick != null) onClick();
            };
            return b;
        }

        private Border CreateDropCard(string icon, string title, string desc)
        {
            var b = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f172a66")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(0, 0, 0, 3)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var iconBlock = new TextBlock
            {
                Text = icon,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            var textSp = new StackPanel();
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            var descBlock = new TextBlock
            {
                Text = desc,
                FontSize = 8.5,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94a3b8")),
                TextWrapping = TextWrapping.NoWrap,
                Width = 145
            };
            textSp.Children.Add(titleBlock);
            textSp.Children.Add(descBlock);

            sp.Children.Add(iconBlock);
            sp.Children.Add(textSp);
            b.Child = sp;
            return b;
        }

        public void Expand()
        {
            if (_isExpanded) return;
            _isExpanded = true;
            _expandedContent.Visibility = Visibility.Visible;
            _dockHandle.Visibility = Visibility.Collapsed;

            var workArea = SystemParameters.WorkArea;
            Width = 190;
            Height = 220;
            Left = workArea.Right - Width;

            // 保持当前高度并做防越界安全卡位
            double maxTop = workArea.Bottom - Height;
            if (Top > maxTop) Top = maxTop;
            if (Top < workArea.Top) Top = workArea.Top;
        }

        public void Collapse()
        {
            if (!_isExpanded) return;
            _isExpanded = false;
            _expandedContent.Visibility = Visibility.Collapsed;
            _dockHandle.Visibility = Visibility.Visible;

            var workArea = SystemParameters.WorkArea;
            Width = 5;
            Height = 110;
            Left = workArea.Right - Width;

            double maxTop = workArea.Bottom - Height;
            if (Top > maxTop) Top = maxTop;
            if (Top < workArea.Top) Top = workArea.Top;
        }
    }
}
