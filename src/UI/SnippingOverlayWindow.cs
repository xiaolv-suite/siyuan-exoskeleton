using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using XiaoLv.Desktop.Core;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace XiaoLv.Desktop.UI
{
    public class SnippingOverlayWindow : Window
    {
        private System.Windows.Shapes.Rectangle _selectionRect;
        private Canvas _canvas;
        private Point _startPoint;
        private bool _isDragging = false;
        private TextBlock _sizeTip;

        public event Action<Bitmap> OnRegionCaptured;

        public SnippingOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 0, 0, 0));
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Cross;

            // 跨多屏虚拟桌面全屏覆盖
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            // 截屏遮罩关闭即时释放多屏巨大 Bitmap 并深度修剪工作集
            Closed += (s, e) => MemoryOptimizer.ScheduleTrim(300);

            _canvas = new Canvas();
            Content = _canvas;

            _selectionRect = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)),
                StrokeThickness = 1.8,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 16, 185, 129)),
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_selectionRect);

            _sizeTip = new TextBlock
            {
                Foreground = Brushes.White,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 15, 23, 42)),
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"),
                Padding = new Thickness(6, 2, 6, 2),
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_sizeTip);

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };

            MouseRightButtonDown += (s, e) =>
            {
                Close();
            };

            MouseDown += SnippingOverlayWindow_MouseDown;
            MouseMove += SnippingOverlayWindow_MouseMove;
            MouseUp += SnippingOverlayWindow_MouseUp;
        }

        private void SnippingOverlayWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDragging = true;
                _startPoint = e.GetPosition(this);
                _selectionRect.Width = 0;
                _selectionRect.Height = 0;
                _selectionRect.Visibility = Visibility.Visible;
                _sizeTip.Visibility = Visibility.Visible;
                CaptureMouse();
            }
        }

        private void SnippingOverlayWindow_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                var cur = e.GetPosition(this);
                double x = Math.Min(_startPoint.X, cur.X);
                double y = Math.Min(_startPoint.Y, cur.Y);
                double w = Math.Abs(cur.X - _startPoint.X);
                double h = Math.Abs(cur.Y - _startPoint.Y);

                Canvas.SetLeft(_selectionRect, x);
                Canvas.SetTop(_selectionRect, y);
                _selectionRect.Width = w;
                _selectionRect.Height = h;

                _sizeTip.Text = string.Format("{0} × {1} px", (int)w, (int)h);
                Canvas.SetLeft(_sizeTip, x);
                Canvas.SetTop(_sizeTip, Math.Max(0, y - 24));
            }
        }

        private void SnippingOverlayWindow_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();

                var endPoint = e.GetPosition(this);
                int screenX = (int)(Left + Math.Min(_startPoint.X, endPoint.X));
                int screenY = (int)(Top + Math.Min(_startPoint.Y, endPoint.Y));
                int width = (int)Math.Abs(endPoint.X - _startPoint.X);
                int height = (int)Math.Abs(endPoint.Y - _startPoint.Y);

                Close();

                if (width > 8 && height > 8)
                {
                    try
                    {
                        var bmp = new Bitmap(width, height);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.CopyFromScreen(screenX, screenY, 0, 0, new System.Drawing.Size(width, height));
                        }
                        if (OnRegionCaptured != null)
                        {
                            OnRegionCaptured(bmp);
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
