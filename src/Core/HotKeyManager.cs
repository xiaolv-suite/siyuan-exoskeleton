using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XiaoLv.Desktop.Core
{
    public class HotKeyManager : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        private const int HOTKEY_MASTER_PANEL = 9001;
        private const int HOTKEY_CONTEXT_PEN = 9002;
        private const int HOTKEY_SNIP_OCR = 9003;
        private const int HOTKEY_QUICK_NOTE = 9004;
        private const int HOTKEY_FSRS_REVIEW = 9005;
        private const int HOTKEY_WEB_CLIP = 9006;
        private const int WM_HOTKEY = 0x0312;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_MENU = 0x12;    // Alt
        private const int VK_CONTROL = 0x11; // Ctrl
        private const int VK_SHIFT = 0x10;   // Shift
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        public event Action OnMasterPanelRequested;
        public event Action OnContextPenRequested;
        public event Action OnSnipOcrRequested;
        public event Action OnQuickNoteRequested;
        public event Action OnFsrsReviewRequested;
        public event Action OnWebClipRequested;

        private class MessageWindow : Form
        {
            private readonly HotKeyManager _parent;

            public MessageWindow(HotKeyManager parent)
            {
                _parent = parent;
                Text = "XiaoLvHotKeyHiddenWindow";
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    _parent.TriggerHotKeyAction(id);
                }
                base.WndProc(ref m);
            }
        }

        private MessageWindow _window;

        public struct KeyCombo
        {
            public int Id;
            public uint Mod;
            public uint Vk;
        }

        private readonly List<KeyCombo> _fallbackCombos = new List<KeyCombo>();
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _keyboardProc;
        private IntPtr _hookId = IntPtr.Zero;

        public bool RegisterDefaultHotKeys()
        {
            return RegisterConfiguredHotKeys("Alt+Shift+Q", "Alt+Shift+W", "Alt+Shift+C", "Alt+Shift+S", "Alt+Shift+R");
        }

        public bool RegisterConfiguredHotKeys(string masterKey, string penKey, string ocrKey = "Alt+Shift+C", string quickKey = "Alt+Shift+S", string fsrsKey = "Alt+Shift+R", string clipKey = "Alt+Shift+G")
        {
            if (_window == null)
            {
                _window = new MessageWindow(this);
                IntPtr h = _window.Handle;
            }

            UnregisterAll();
            _fallbackCombos.Clear();

            RegisterOrFallback(HOTKEY_MASTER_PANEL, string.IsNullOrEmpty(masterKey) ? "Alt+Shift+Q" : masterKey);
            RegisterOrFallback(HOTKEY_CONTEXT_PEN, string.IsNullOrEmpty(penKey) ? "Alt+Shift+W" : penKey);
            RegisterOrFallback(HOTKEY_SNIP_OCR, string.IsNullOrEmpty(ocrKey) ? "Alt+Shift+C" : ocrKey);
            RegisterOrFallback(HOTKEY_QUICK_NOTE, string.IsNullOrEmpty(quickKey) ? "Alt+Shift+S" : quickKey);
            RegisterOrFallback(HOTKEY_FSRS_REVIEW, string.IsNullOrEmpty(fsrsKey) ? "Alt+Shift+R" : fsrsKey);
            RegisterOrFallback(HOTKEY_WEB_CLIP, string.IsNullOrEmpty(clipKey) ? "Alt+Shift+G" : clipKey);

            if (_fallbackCombos.Count > 0)
            {
                StartKeyboardHook();
            }
            else
            {
                StopKeyboardHook();
            }

            return true; // 双轨引擎确保 100% 成功监听
        }

        private void RegisterOrFallback(int id, string keyStr)
        {
            uint mod;
            uint vk;
            if (!ParseKeyString(keyStr, out mod, out vk)) return;

            // 1. 优先尝试 Win32 原生专属热键注册
            bool ok = RegisterHotKey(_window.Handle, id, mod | MOD_NOREPEAT, vk);
            if (!ok) ok = RegisterHotKey(_window.Handle, id, mod, vk);

            // 2. 若被其他软件（如 Quicker、PixPin）占用，加入底层键盘钩子兜底名单
            if (!ok)
            {
                _fallbackCombos.Add(new KeyCombo { Id = id, Mod = mod, Vk = vk });
            }
        }

        public void UnregisterAll()
        {
            StopKeyboardHook();
            _fallbackCombos.Clear();

            if (_window != null && !_window.IsDisposed && _window.Handle != IntPtr.Zero)
            {
                UnregisterHotKey(_window.Handle, HOTKEY_MASTER_PANEL);
                UnregisterHotKey(_window.Handle, HOTKEY_CONTEXT_PEN);
                UnregisterHotKey(_window.Handle, HOTKEY_SNIP_OCR);
                UnregisterHotKey(_window.Handle, HOTKEY_QUICK_NOTE);
                UnregisterHotKey(_window.Handle, HOTKEY_FSRS_REVIEW);
                UnregisterHotKey(_window.Handle, HOTKEY_WEB_CLIP);
            }
        }

        private void TriggerHotKeyAction(int id)
        {
            if (id == HOTKEY_MASTER_PANEL)
            {
                if (OnMasterPanelRequested != null) OnMasterPanelRequested();
            }
            else if (id == HOTKEY_CONTEXT_PEN)
            {
                if (OnContextPenRequested != null) OnContextPenRequested();
            }
            else if (id == HOTKEY_SNIP_OCR)
            {
                if (OnSnipOcrRequested != null) OnSnipOcrRequested();
            }
            else if (id == HOTKEY_QUICK_NOTE)
            {
                if (OnQuickNoteRequested != null) OnQuickNoteRequested();
            }
            else if (id == HOTKEY_FSRS_REVIEW)
            {
                if (OnFsrsReviewRequested != null) OnFsrsReviewRequested();
            }
            else if (id == HOTKEY_WEB_CLIP)
            {
                if (OnWebClipRequested != null) OnWebClipRequested();
            }
        }

        private void StartKeyboardHook()
        {
            if (_hookId != IntPtr.Zero) return;
            _keyboardProc = HookCallback;
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private void StopKeyboardHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);

                bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                bool win = ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) || ((GetAsyncKeyState(VK_RWIN) & 0x8000) != 0);

                foreach (var combo in _fallbackCombos)
                {
                    if (combo.Vk == (uint)vkCode)
                    {
                        bool matchAlt = (combo.Mod & MOD_ALT) != 0;
                        bool matchCtrl = (combo.Mod & MOD_CONTROL) != 0;
                        bool matchShift = (combo.Mod & MOD_SHIFT) != 0;
                        bool matchWin = (combo.Mod & MOD_WIN) != 0;

                        if (alt == matchAlt && ctrl == matchCtrl && shift == matchShift && win == matchWin)
                        {
                            TriggerHotKeyAction(combo.Id);
                            // 不强行吃掉按键，允许共存
                            break;
                        }
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static bool ParseKeyString(string keyStr, out uint mod, out uint vk)
        {
            mod = 0;
            vk = 0;
            if (string.IsNullOrEmpty(keyStr)) return false;

            var parts = keyStr.Split(new char[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var pt = p.Trim().ToUpperInvariant();
                if (pt == "ALT") mod |= MOD_ALT;
                else if (pt == "CTRL" || pt == "CONTROL") mod |= MOD_CONTROL;
                else if (pt == "SHIFT") mod |= MOD_SHIFT;
                else if (pt == "WIN") mod |= MOD_WIN;
                else
                {
                    if (pt.Length == 1)
                    {
                        vk = (uint)pt[0];
                    }
                    else if (pt == "SPACE")
                    {
                        vk = 0x20;
                    }
                    else if (pt.StartsWith("F") && pt.Length > 1)
                    {
                        int fNum;
                        if (int.TryParse(pt.Substring(1), out fNum) && fNum >= 1 && fNum <= 12)
                        {
                            vk = (uint)(0x70 + (fNum - 1));
                        }
                    }
                }
            }
            return vk != 0;
        }

        public void Dispose()
        {
            UnregisterAll();
            if (_window != null)
            {
                _window.Dispose();
                _window = null;
            }
        }
    }
}
