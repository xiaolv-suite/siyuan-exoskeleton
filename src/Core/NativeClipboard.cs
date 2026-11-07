using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 原生级 Win32 安全剪贴板与光标穿透注入引擎
    /// 彻底解决 Quicker 时代 Clipboard.SetText 在外部程序占用时的 CLIPBRD_E_CANT_OPEN 崩溃异常
    /// </summary>
    public static class NativeClipboard
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const byte VK_CONTROL = 0x11;
        private const byte VK_V = 0x56;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>
        /// 带自动重试与避让的安全设置剪贴板文本
        /// </summary>
        public static bool SetTextSafe(string text, int retries = 5, int delayMs = 25)
        {
            if (text == null) text = "";
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true, 3, 20);
                    return true;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(delayMs);
                }
                catch (Exception)
                {
                    Thread.Sleep(delayMs);
                }
            }
            return false;
        }

        /// <summary>
        /// 带自动重试与避让的安全读取剪贴板文本
        /// </summary>
        public static string GetTextSafe(int retries = 5, int delayMs = 25)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        return Clipboard.GetText();
                    }
                    return "";
                }
                catch (ExternalException)
                {
                    Thread.Sleep(delayMs);
                }
                catch (Exception)
                {
                    Thread.Sleep(delayMs);
                }
            }
            return "";
        }

        /// <summary>
        /// 原生穿透光标级快速注入文本（自动交还焦点并模拟 Ctrl+V）
        /// </summary>
        public static void PasteTextAtCaret(IntPtr targetHwnd, string textToPaste)
        {
            if (string.IsNullOrEmpty(textToPaste)) return;

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    // 1. 安全设置剪贴板
                    SetTextSafe(textToPaste);

                    // 2. 焦点交还给前置应用
                    if (targetHwnd != IntPtr.Zero)
                    {
                        uint currentThreadId = GetCurrentThreadId();
                        uint targetProcessId;
                        uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out targetProcessId);

                        if (currentThreadId != targetThreadId)
                        {
                            AttachThreadInput(currentThreadId, targetThreadId, true);
                            SetForegroundWindow(targetHwnd);
                            AttachThreadInput(currentThreadId, targetThreadId, false);
                        }
                        else
                        {
                            SetForegroundWindow(targetHwnd);
                        }
                        Thread.Sleep(60);
                    }

                    // 3. 原生触发 Ctrl+V 粘贴
                    keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(20);
                    keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                catch { }
            });
        }
    }
}
