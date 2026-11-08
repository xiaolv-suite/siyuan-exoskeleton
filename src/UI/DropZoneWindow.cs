using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XiaoLv.Desktop.Core;

namespace XiaoLv.Desktop.UI
{
    /// <summary>
    /// 思源外骨骼 · 桌面边缘微停靠托放中枢视窗 (DropZoneWindow)
    /// 1. 极窄边缘停靠 (屏幕右缘平时仅 10px 细长微徽章，0 遮挡)
    /// 2. 拖入任意文件或网页链接时平滑展开为 180px 磨砂玻璃抽屉
    /// 3. 与 DropZoneRouter 联动，自动路由文章/拾遗、票据/管家、文件/速递
    /// </summary>
    public class DropZoneWindow : Window
    {
        private Border _rootBorder;
        private StackPanel _expandedContent;
        private Border _dockHandle;
        private bool _isExpanded = false;

        public Action<string, string> OnNotificationRequested { get; set; }

        public DropZoneWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            AllowDrop = true;

            Width = 10;
            Height = 180;

            PositionAtRightEdge();

            BuildUI();

            MouseEnter += (s, e) => Expand();
            MouseLeave += (s, e) => Collapse();

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

        private void PositionAtRightEdge()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }

        private void BuildUI()
        {
            _rootBorder = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293be6")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38bdf888")),
                BorderThickness = new Thickness(1.5, 1, 0, 1),
                CornerRadius = new CornerRadius(8, 0, 0, 8),
                ClipToBounds = true
            };

            var grid = new Grid();

            // 停靠把手 (折叠时展现)
            _dockHandle = new Border
            {
                Width = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38bdf844")),
                CornerRadius = new CornerRadius(4, 0, 0, 4)
            };

            // 展开内容卡片
            _expandedContent = new StackPanel
            {
                Margin = new Thickness(14, 10, 8, 10),
                Visibility = Visibility.Collapsed
            };

            var header = new TextBlock
            {
                Text = "⚡ 桌面托放中枢",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38bdf8")),
                Margin = new Thickness(0, 0, 0, 8)
            };
            _expandedContent.Children.Add(header);

            _expandedContent.Children.Add(CreateDropCard("📥", "小驴拾遗", "拖入网页超链/文章自动读库分拣"));
            _expandedContent.Children.Add(CreateDropCard("💰", "小驴管家", "拖入发票/报销/账目记入Lifelog"));
            _expandedContent.Children.Add(CreateDropCard("📁", "本地速递", "拖入Office/PDF/源码结构化入库"));

            grid.Children.Add(_dockHandle);
            grid.Children.Add(_expandedContent);
            _rootBorder.Child = grid;

            Content = _rootBorder;
        }

        private Border CreateDropCard(string icon, string title, string desc)
        {
            var b = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f172a88")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var iconBlock = new TextBlock
            {
                Text = icon,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            var textSp = new StackPanel();
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            var descBlock = new TextBlock
            {
                Text = desc,
                FontSize = 9.5,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94a3b8")),
                TextWrapping = TextWrapping.Wrap,
                Width = 115
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
            Width = 180;
            Left = workArea.Right - Width;
        }

        public void Collapse()
        {
            if (!_isExpanded) return;
            _isExpanded = false;
            _expandedContent.Visibility = Visibility.Collapsed;
            _dockHandle.Visibility = Visibility.Visible;

            var workArea = SystemParameters.WorkArea;
            Width = 10;
            Left = workArea.Right - Width;
        }
    }
}
