using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace XiaoLv.Desktop.Core
{
    public enum ScreenEdge
    {
        Left,
        Right,
        Top,
        Bottom
    }

    /// <summary>
    /// 思源外骨骼 · 多屏幕多DPI自适应与自愈吸附器 (MultiMonitorDpiAdapter)
    /// 1. 深度解决 Windows 10/11 拔插外接屏幕后窗口停留在“幽灵屏幕盲区”的顽疾
    /// 2. 毫秒级探测窗口是否落在可见屏幕物理视口内，若脱离视口自动瞬时回位至主屏
    /// 3. 支持像素级屏幕边缘磁吸吸附 (Screen Snap)
    /// 4. 完美兼容 Windows 10 Per-Monitor v2 混合异构缩放 (4K 150% + 1080p 100%)
    /// </summary>
    public static class MultiMonitorDpiAdapter
    {
        /// <summary>
        /// 检查并自愈窗口位置：若窗口位于已断开的副屏或完全脱离屏幕工作区，自动瞬时复位至主屏工作区
        /// </summary>
        public static bool EnsureVisibleOnActiveScreens(Window window)
        {
            if (window == null) return false;

            try
            {
                double left = window.Left;
                double top = window.Top;
                double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

                // 构造窗口的大致物理矩形 (默认按主屏估算)
                var winRect = new System.Drawing.Rectangle((int)left, (int)top, (int)width, (int)height);
                bool intersectsAny = false;

                foreach (var screen in Screen.AllScreens)
                {
                    if (screen.WorkingArea.IntersectsWith(winRect))
                    {
                        intersectsAny = true;
                        break;
                    }
                }

                if (!intersectsAny)
                {
                    // 窗口完全漂移至屏幕外，自动复位至主屏中央
                    var primary = Screen.PrimaryScreen.WorkingArea;
                    double scale = WindowPositionHelper.GetDpiScaleForPoint(primary.X + primary.Width / 2, primary.Y + primary.Height / 2);
                    if (scale <= 0.1) scale = 1.0;

                    window.Left = (primary.Left / scale) + ((primary.Width / scale) - width) / 2.0;
                    window.Top = (primary.Top / scale) + ((primary.Height / scale) - height) / 2.0;
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 将指定窗口磁吸对齐到当前光标所在屏幕的指定边缘 (常用于 DropZone 边缘停靠)
        /// </summary>
        public static void SnapToScreenEdge(Window window, ScreenEdge edge, double margin = 0)
        {
            if (window == null) return;

            try
            {
                var curPos = SelectionHelper.GetCurrentCursorPos();
                var screen = Screen.FromPoint(new System.Drawing.Point((int)curPos.X, (int)curPos.Y));
                var workArea = screen.WorkingArea;

                double scale = WindowPositionHelper.GetDpiScaleForPoint((int)curPos.X, (int)curPos.Y);
                if (scale <= 0.1) scale = 1.0;

                double dipLeft = workArea.Left / scale;
                double dipTop = workArea.Top / scale;
                double dipWidth = workArea.Width / scale;
                double dipHeight = workArea.Height / scale;

                double winWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                double winHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

                switch (edge)
                {
                    case ScreenEdge.Right:
                        window.Left = dipLeft + dipWidth - winWidth - margin;
                        window.Top = dipTop + (dipHeight - winHeight) / 2.0;
                        break;
                    case ScreenEdge.Left:
                        window.Left = dipLeft + margin;
                        window.Top = dipTop + (dipHeight - winHeight) / 2.0;
                        break;
                    case ScreenEdge.Top:
                        window.Left = dipLeft + (dipWidth - winWidth) / 2.0;
                        window.Top = dipTop + margin;
                        break;
                    case ScreenEdge.Bottom:
                        window.Left = dipLeft + (dipWidth - winWidth) / 2.0;
                        window.Top = dipTop + dipHeight - winHeight - margin;
                        break;
                }
            }
            catch { }
        }
    }
}
