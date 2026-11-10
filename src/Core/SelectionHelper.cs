using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XiaoLv.Desktop.Core
{
    public static class SelectionHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private const byte VK_CONTROL = 0x11;
        private const byte VK_C = 0x43;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelMouseProc _mouseProc;
        private static IntPtr _hookId = IntPtr.Zero;
        private static Point _mouseDownPos = Point.Empty;

        public static bool AutoFloatEnabled { get; set; }
        public static bool IsSuspended { get; set; }
        public static bool IsInternalClipboardOperation { get; set; }
        public static event Action<string> OnAutoSelectionDetected;
        public static event Action<int, int> OnGlobalMouseDown;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public static Point GetCurrentCursorPos()
        {
            POINT p;
            if (GetCursorPos(out p))
            {
                return new Point(p.X, p.Y);
            }
            return new Point(500, 300);
        }

        public static string GetForegroundWindowTitle()
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            return sb.ToString().Trim();
        }

        public static bool IsFullscreenForeground()
        {
            try
            {
                var hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return false;

                var sb = new StringBuilder(64);
                GetClassName(hWnd, sb, 64);
                var cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW") return false;

                RECT r;
                if (GetWindowRect(hWnd, out r))
                {
                    int w = Screen.PrimaryScreen.Bounds.Width;
                    int h = Screen.PrimaryScreen.Bounds.Height;
                    if (r.Left <= 0 && r.Top <= 0 && r.Right >= w && r.Bottom >= h)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void StartMouseHook()
        {
            if (_hookId != IntPtr.Zero) return;
            _mouseProc = HookCallback;
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        public static void StopMouseHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                // 软开关/挂起保护：当用户暂停划词或处于全屏独占应用(游戏/影音)时，0 开销短路放行，绝不阻滞系统输入
                if (IsSuspended || IsFullscreenForeground())
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                if (wParam == (IntPtr)WM_LBUTTONDOWN)
                {
                    try
                    {
                        var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        _mouseDownPos = new Point(info.pt.X, info.pt.Y);
                        if (OnGlobalMouseDown != null)
                        {
                            OnGlobalMouseDown(info.pt.X, info.pt.Y);
                        }
                    }
                    catch { }
                }
                else if (wParam == (IntPtr)WM_LBUTTONUP && AutoFloatEnabled)
                {
                    try
                    {
                        var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        var upPos = new Point(info.pt.X, info.pt.Y);

                        // 智能防抖：鼠标位移不足 8 像素视为单击而非划选拖拽，直接忽略
                        int dx = Math.Abs(upPos.X - _mouseDownPos.X);
                        int dy = Math.Abs(upPos.Y - _mouseDownPos.Y);
                        if (dx >= 8 || dy >= 8)
                        {
                            // 全屏免打扰检查
                            if (!IsFullscreenForeground())
                            {
                                ThreadPool.QueueUserWorkItem(state =>
                                {
                                    Thread.Sleep(160);
                                    var txt = CaptureSelectedText(200);
                                    if (!string.IsNullOrEmpty(txt) && txt.Length >= 2 && txt.Length <= 2000)
                                    {
                                        if (OnAutoSelectionDetected != null)
                                        {
                                            OnAutoSelectionDetected(txt);
                                        }
                                    }
                                });
                            }
                        }
                    }
                    catch { }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public static string CaptureSelectedText(int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = 70;

            // 0. 微软 Office 与金山 WPS COM 晚绑定优先直读 (0ms读取，100% 保护剪贴板)
            try
            {
                var snap = ContextPerception.CaptureForegroundContextFast();
                if (snap != null && !string.IsNullOrEmpty(snap.ProcessName))
                {
                    string pLower = snap.ProcessName.ToLowerInvariant();
                    if (pLower == "winword" || pLower == "excel" || pLower == "wps" || pLower == "et")
                    {
                        var officeSel = OfficeComBridge.TryGetActiveOfficeSelection(pLower);
                        if (officeSel != null && officeSel.Success && !string.IsNullOrEmpty(officeSel.Text))
                        {
                            return officeSel.Text.Trim();
                        }
                    }
                }
            }
            catch { }

            IsInternalClipboardOperation = true;
            string oldClip = null;
            try
            {
                oldClip = NativeClipboard.GetTextSafe(1, 5);

                try { Clipboard.Clear(); } catch { }

                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_C, 0, 0, UIntPtr.Zero);
                Thread.Sleep(15);
                keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                string captured = "";
                var start = Environment.TickCount;
                while (Environment.TickCount - start < timeoutMs)
                {
                    Thread.Sleep(10);
                    captured = NativeClipboard.GetTextSafe(1, 5);
                    if (!string.IsNullOrEmpty(captured)) break;
                }

                if (string.IsNullOrEmpty(captured) && !string.IsNullOrEmpty(oldClip))
                {
                    NativeClipboard.SetTextSafe(oldClip, 1, 5);
                }

                return captured != null ? captured.Trim() : "";
            }
            finally
            {
                ThreadPool.QueueUserWorkItem(state =>
                {
                    Thread.Sleep(150);
                    IsInternalClipboardOperation = false;
                });
            }
        }

        public static string CaptureSelectedText()
        {
            return CaptureSelectedText(300);
        }
    }
}
