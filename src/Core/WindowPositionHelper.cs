using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 多显示器与 Per-Monitor DPI 感知的窗口定位器
    /// 自动换算物理像素与 WPF DIPs，确保多屏及异构 DPI 环境下 100% 像素级居中与边缘夹紧
    /// </summary>
    public static class WindowPositionHelper
    {
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private enum MonitorDpiType
        {
            MDT_EFFECTIVE_DPI = 0,
            MDT_ANGULAR_DPI = 1,
            MDT_RAW_DPI = 2,
            MDT_DEFAULT = MDT_EFFECTIVE_DPI
        }

        /// <summary>
        /// 获取指定物理坐标屏幕的 DPI 缩放比率 (默认 1.0)
        /// </summary>
        public static double GetDpiScaleForPoint(int x, int y)
        {
            try
            {
                var pt = new POINT { X = x, Y = y };
                IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                if (hMon != IntPtr.Zero)
                {
                    uint dpiX, dpiY;
                    int hr = GetDpiForMonitor(hMon, MonitorDpiType.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                    if (hr == 0 && dpiX > 0)
                    {
                        return dpiX / 96.0;
                    }
                }
            }
            catch { }
            return 1.0;
        }

        /// <summary>
        /// 将 WPF 窗口精准居中放置在当前光标所在的屏幕，自动处理多显示器与 Per-Monitor DPI 缩放
        /// </summary>
        public static void CenterOnCursorScreen(Window window, double verticalOffset = 0)
        {
            if (window == null) return;
            try
            {
                var curPos = SelectionHelper.GetCurrentCursorPos();
                var screen = Screen.FromPoint(new System.Drawing.Point((int)curPos.X, (int)curPos.Y));
                var workArea = screen.WorkingArea;

                double scale = GetDpiScaleForPoint((int)curPos.X, (int)curPos.Y);
                if (scale <= 0.1) scale = 1.0;

                // 转换工作区物理坐标为 WPF DIPs
                double dipLeft = workArea.Left / scale;
                double dipTop = workArea.Top / scale;
                double dipWidth = workArea.Width / scale;
                double dipHeight = workArea.Height / scale;

                double winWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                double winHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

                // 居中计算
                double targetLeft = dipLeft + (dipWidth - winWidth) / 2.0;
                double targetTop = dipTop + (dipHeight - winHeight) / 2.0 + verticalOffset;

                // 确保在屏幕可见工作区内 (安全夹紧)
                if (targetLeft < dipLeft) targetLeft = dipLeft;
                if (targetTop < dipTop) targetTop = dipTop;
                if (targetLeft + winWidth > dipLeft + dipWidth) targetLeft = dipLeft + dipWidth - winWidth;
                if (targetTop + winHeight > dipTop + dipHeight) targetTop = dipTop + dipHeight - winHeight;

                window.Left = targetLeft;
                window.Top = targetTop;
            }
            catch
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        /// <summary>
        /// 将随身划笔或微型浮窗安全放置在当前光标右下方，自动处理多屏幕边界翻转与 Per-Monitor DPI 缩放
        /// </summary>
        public static void PlaceNearCursor(Window window, double offsetX = 15, double offsetY = 20)
        {
            if (window == null) return;
            try
            {
                var curPos = SelectionHelper.GetCurrentCursorPos();
                var screen = Screen.FromPoint(new System.Drawing.Point((int)curPos.X, (int)curPos.Y));
                var workArea = screen.WorkingArea;

                double scale = GetDpiScaleForPoint((int)curPos.X, (int)curPos.Y);
                if (scale <= 0.1) scale = 1.0;

                // 转换工作区与光标的物理坐标为 WPF DIPs
                double dipWorkLeft = workArea.Left / scale;
                double dipWorkTop = workArea.Top / scale;
                double dipWorkWidth = workArea.Width / scale;
                double dipWorkHeight = workArea.Height / scale;

                double curDipX = curPos.X / scale;
                double curDipY = curPos.Y / scale;

                double winWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                double winHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

                // 默认置于光标右下方
                double targetLeft = curDipX + offsetX;
                double targetTop = curDipY + offsetY;

                // 1. 右侧越界：若右侧空间不足，翻转至光标左侧
                if (targetLeft + winWidth > dipWorkLeft + dipWorkWidth - 8)
                {
                    targetLeft = curDipX - winWidth - offsetX;
                }

                // 2. 下方越界：若下方空间不足，翻转至光标上方
                if (targetTop + winHeight > dipWorkTop + dipWorkHeight - 8)
                {
                    targetTop = curDipY - winHeight - offsetY;
                }

                // 3. 安全夹紧在当前屏幕工作区范围内
                if (targetLeft < dipWorkLeft + 8) targetLeft = dipWorkLeft + 8;
                if (targetTop < dipWorkTop + 8) targetTop = dipWorkTop + 8;
                if (targetLeft + winWidth > dipWorkLeft + dipWorkWidth - 8)
                {
                    targetLeft = dipWorkLeft + dipWorkWidth - winWidth - 8;
                }
                if (targetTop + winHeight > dipWorkTop + dipWorkHeight - 8)
                {
                    targetTop = dipWorkTop + dipWorkHeight - winHeight - 8;
                }

                window.Left = targetLeft;
                window.Top = targetTop;
            }
            catch
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
    }
}
