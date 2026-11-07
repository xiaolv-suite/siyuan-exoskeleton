using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XiaoLv.Desktop.Core
{
    public class SystemTray : IDisposable
    {
        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _menu;

        public event Action OnOpenPanelClicked;
        public event Action OnOpenPenClicked;
        public event Action OnSnippingOcrClicked;
        public event Action OnQuickCaptureClicked;
        public event Action OnFsrsReviewClicked;
        public event Action OnWebClipClicked;
        public event Action OnManualSyncClicked;
        public event Action OnInteractiveToastClicked;
        public event Action OnCheckConnectionClicked;
        public event Action OnCheckUpdateClicked;
        public event Action OnExitClicked;

        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "XiaoLvDesktop";

        public void Initialize(Icon customIcon)
        {
            // 启动时自动清扫历史异常退出残留的托盘幽灵图标
            RefreshNotificationArea();

            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei", 9f);

            var itemPanel = new ToolStripMenuItem("🪟 思源外骨骼 · 全局面板 (Alt+Shift+Q)");
            itemPanel.Font = new Font("Microsoft YaHei", 9f, FontStyle.Bold);
            itemPanel.Click += (s, e) => { if (OnOpenPanelClicked != null) OnOpenPanelClicked(); };

            var itemPen = new ToolStripMenuItem("✏️ 思源外骨骼 · 随身划笔 (Alt+Shift+W)");
            itemPen.Click += (s, e) => { if (OnOpenPenClicked != null) OnOpenPenClicked(); };

            var itemOcr = new ToolStripMenuItem("📸 截屏离线 OCR (Windows 原生引擎)");
            itemOcr.Click += (s, e) => { if (OnSnippingOcrClicked != null) OnSnippingOcrClicked(); };

            var itemQuick = new ToolStripMenuItem("⚡ 极速灵感速记窗 (Alt+Shift+S)");
            itemQuick.Click += (s, e) => { if (OnQuickCaptureClicked != null) OnQuickCaptureClicked(); };

            var itemVoice = new ToolStripMenuItem("🎙️ 桌面语音速记微舱 (Ctrl+M / 微信输入法)");
            itemVoice.Click += (s, e) =>
            {
                if (OnQuickCaptureClicked != null) OnQuickCaptureClicked();
                ThreadPool.QueueUserWorkItem(st =>
                {
                    Thread.Sleep(300);
                    VoiceCaptureManager.TriggerWindowsVoiceTyping();
                });
            };

            var itemWorkspaces = new ToolStripMenuItem("🏢 工作现场快照与恢复 (虚拟桌面联动)");
            itemWorkspaces.DropDownOpening += (s, e) =>
            {
                itemWorkspaces.DropDownItems.Clear();
                var wsList = WorkspaceSnapshotManager.ListWorkspaces();
                foreach (var ws in wsList)
                {
                    var it = new ToolStripMenuItem(string.Format("{0} {1}", ws.Icon, ws.Name));
                    it.ToolTipText = ws.Note;
                    string targetId = ws.Id;
                    it.Click += (s_ws, e_ws) =>
                    {
                        WorkspaceSnapshotManager.RestoreWorkspace(targetId, (t, m) => ShowNotice(t, m));
                    };
                    itemWorkspaces.DropDownItems.Add(it);
                }
                itemWorkspaces.DropDownItems.Add(new ToolStripSeparator());
                var itCapture = new ToolStripMenuItem("📸 捕获当前现场并管理 (ws:)");
                itCapture.Click += (s_c, e_c) =>
                {
                    if (OnOpenPanelClicked != null) OnOpenPanelClicked();
                };
                itemWorkspaces.DropDownItems.Add(itCapture);
            };

            var itemFsrs = new ToolStripMenuItem("🧠 FSRS 闪卡桌面微舱 (Alt+Shift+R)");
            itemFsrs.Click += (s, e) => { if (OnFsrsReviewClicked != null) OnFsrsReviewClicked(); };

            var itemWebClip = new ToolStripMenuItem("🌐 网页免扩展极速剪藏卡 (Alt+Shift+G)");
            itemWebClip.Click += (s, e) => { if (OnWebClipClicked != null) OnWebClipClicked(); };

            var itemSearchTool = new ToolStripMenuItem("🔍 唤起本地检索工具 (Listary / Everything)");
            itemSearchTool.Click += (s, e) =>
            {
                var pref = AppSettings.Instance.PreferredSearchEngine;
                ToolDetector.OpenInSearchTool("", pref);
            };

            var itemSync = new ToolStripMenuItem("☁️ 立即触发云同步 (安全增量数据)");
            itemSync.Click += (s, e) => { if (OnManualSyncClicked != null) OnManualSyncClicked(); };

            var itemTrim = new ToolStripMenuItem("🧹 深度压缩物理工作集内存");
            itemTrim.Click += (s, e) =>
            {
                MemoryOptimizer.PerformDeepTrim();
                double ws = MemoryOptimizer.GetCurrentWorkingSetMb();
                ShowNotice("思源外骨骼 · 内存优化", "✓ 物理内存已深度压缩！当前工作集: " + ws + " MB");
            };

            var itemToast = new ToolStripMenuItem("🔔 可交互通知速记 (Windows Toast)");
            itemToast.Click += (s, e) => { if (OnInteractiveToastClicked != null) OnInteractiveToastClicked(); };

            var itemPopClip = new ToolStripMenuItem("✨ 智能划选自动浮窗 (PopClip模式)");
            itemPopClip.Checked = SelectionHelper.AutoFloatEnabled;
            itemPopClip.Click += (s, e) =>
            {
                bool newState = !itemPopClip.Checked;
                SelectionHelper.AutoFloatEnabled = newState;
                itemPopClip.Checked = newState;
                AppSettings.Instance.AutoFloatEnabled = newState;
                AppSettings.Instance.Save();
                ShowNotice("思源外骨骼", newState ? "已开启 PopClip 模式：任意软件划选文字自动浮现胶囊！" : "已关闭 PopClip 模式：仅在按快捷键时呼出。");
            };

            var itemSmartClip = new ToolStripMenuItem("📋 智能剪贴板感知 (URL/表格/代码自动清洗)");
            itemSmartClip.Checked = ClipboardWatcher.IsEnabled;
            itemSmartClip.Click += (s, e) =>
            {
                bool newState = !itemSmartClip.Checked;
                ClipboardWatcher.IsEnabled = newState;
                itemSmartClip.Checked = newState;
                AppSettings.Instance.SmartClipboardEnabled = newState;
                AppSettings.Instance.Save();
                ShowNotice("思源外骨骼", newState ? "已开启智能剪贴板感知：复制链接/表格/代码自动提纯与结构化！" : "已关闭智能剪贴板感知。");
            };

            var itemShellMenu = new ToolStripMenuItem("🗂️ 资源管理器右键直投菜单 (小驴速递)");
            itemShellMenu.Checked = ShellContextMenuManager.IsRegistered();
            itemShellMenu.Click += (s, e) =>
            {
                bool newState = !itemShellMenu.Checked;
                if (newState)
                {
                    bool ok = ShellContextMenuManager.Register();
                    itemShellMenu.Checked = ok;
                    ShowNotice("思源外骨骼", ok ? "已启用文件右键菜单：在任意文件或图片上右键即可一键速递入思源！" : "⚠️ 注册右键菜单失败");
                }
                else
                {
                    ShellContextMenuManager.Unregister();
                    itemShellMenu.Checked = false;
                    ShowNotice("思源外骨骼", "已移除文件右键菜单。");
                }
            };

            var sep1 = new ToolStripSeparator();

            var itemProbe = new ToolStripMenuItem("📡 检查思源连接状态");
            itemProbe.Click += (s, e) => { if (OnCheckConnectionClicked != null) OnCheckConnectionClicked(); };

            var itemCleanOrphans = new ToolStripMenuItem("🧹 清扫测试靶场孤儿内核");
            itemCleanOrphans.Click += (s, e) =>
            {
                string msg = KernelManager.CleanOrphanTestKernels();
                ShowNotice("思源外骨骼 · 靶场清扫", msg);
            };

            var itemAutoStart = new ToolStripMenuItem("⚙️ 开机自动启动");
            itemAutoStart.Checked = IsAutoStartEnabled();
            itemAutoStart.Click += (s, e) =>
            {
                bool newState = !itemAutoStart.Checked;
                SetAutoStart(newState);
                itemAutoStart.Checked = newState;
                AppSettings.Instance.AutoStart = newState;
                AppSettings.Instance.Save();
                ShowNotice("思源外骨骼", newState ? "已开启开机自启！" : "已关闭开机自启。");
            };

            var itemUpdate = new ToolStripMenuItem("🔄 检查版本更新 (Gitee 源)");
            itemUpdate.Click += (s, e) => { if (OnCheckUpdateClicked != null) OnCheckUpdateClicked(); };

            var sep2 = new ToolStripSeparator();

            var itemExit = new ToolStripMenuItem("❌ 退出思源外骨骼");
            itemExit.Click += (s, e) => { if (OnExitClicked != null) OnExitClicked(); };

            _menu.Items.Add(itemPanel);
            _menu.Items.Add(itemPen);
            _menu.Items.Add(itemOcr);
            _menu.Items.Add(itemQuick);
            _menu.Items.Add(itemVoice);
            _menu.Items.Add(itemWorkspaces);
            _menu.Items.Add(itemFsrs);
            _menu.Items.Add(itemWebClip);
            _menu.Items.Add(itemSearchTool);
            _menu.Items.Add(itemSync);
            _menu.Items.Add(itemTrim);
            _menu.Items.Add(itemToast);
            _menu.Items.Add(itemPopClip);
            _menu.Items.Add(itemSmartClip);
            _menu.Items.Add(itemShellMenu);
            _menu.Items.Add(sep1);
            _menu.Items.Add(itemProbe);
            _menu.Items.Add(itemCleanOrphans);
            _menu.Items.Add(itemAutoStart);
            _menu.Items.Add(itemUpdate);
            _menu.Items.Add(sep2);
            _menu.Items.Add(itemExit);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "思源外骨骼 · 桌面穿透工作台 v2.9.0";
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Visible = true;

            if (customIcon != null)
            {
                _notifyIcon.Icon = customIcon;
            }
            else
            {
                _notifyIcon.Icon = GenerateDefaultBadgeIcon();
            }

            // 双击托盘打开主面板
            _notifyIcon.DoubleClick += (s, e) =>
            {
                if (OnOpenPanelClicked != null) OnOpenPanelClicked();
            };
        }

        public void ShowNotice(string title, string message)
        {
            ShowNotice(title, message, ToolTipIcon.Info);
        }

        public void ShowNotice(string title, string message, ToolTipIcon icon)
        {
            try
            {
                if (_notifyIcon != null && _notifyIcon.Visible)
                {
                    _notifyIcon.ShowBalloonTip(3000, title ?? "思源外骨骼", message ?? "", icon);
                }
            }
            catch { }
        }

        private static Icon GenerateDefaultBadgeIcon()
        {
            try
            {
                // 1. 尝试从 assets/icons 加载已渲染的高清外骨骼装甲图标
                string pngPath = EmbeddedResourceManager.ResolveAssetPath("icons/exoskeleton-armor.png");
                if (File.Exists(pngPath))
                {
                    using (var bmpFromFile = new Bitmap(pngPath))
                    {
                        return Icon.FromHandle(bmpFromFile.GetHicon());
                    }
                }

                // 2. 向量路径自绘制兜底：呈现方案 A【外骨骼钛青机甲底座 + 思源笔记 4 柱经典几何剪影】
                var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                    // 1. 深空曜石六边形装甲底座
                    using (var bgBrush = new SolidBrush(Color.FromArgb(15, 23, 42)))
                    using (var borderPen = new Pen(Color.FromArgb(56, 189, 248), 1.6f))
                    {
                        var pts = new PointF[]
                        {
                            new PointF(16f, 2f),
                            new PointF(28f, 8f),
                            new PointF(28f, 23f),
                            new PointF(16f, 29f),
                            new PointF(4f, 23f),
                            new PointF(4f, 8f)
                        };
                        g.FillPolygon(bgBrush, pts);
                        g.DrawPolygon(borderPen, pts);
                    }

                    // 2. 思源笔记 4 柱经典几何剪影 (电磁青与冰蓝分面交错)
                    using (var b1 = new SolidBrush(Color.FromArgb(255, 255, 255)))
                    using (var b2 = new SolidBrush(Color.FromArgb(56, 189, 248)))
                    using (var b3 = new SolidBrush(Color.FromArgb(2, 132, 199)))
                    using (var b4 = new SolidBrush(Color.FromArgb(56, 189, 248)))
                    using (var edgePen = new Pen(Color.FromArgb(15, 23, 42), 0.8f))
                    {
                        var col1 = new PointF[] { new PointF(11.5f, 14.5f), new PointF(14f, 12f), new PointF(14f, 17.5f), new PointF(11.5f, 20f) };
                        var col2 = new PointF[] { new PointF(14f, 12f), new PointF(16f, 14f), new PointF(16f, 19.5f), new PointF(14f, 17.5f) };
                        var col3 = new PointF[] { new PointF(16f, 14f), new PointF(18f, 12f), new PointF(18f, 17.5f), new PointF(16f, 19.5f) };
                        var col4 = new PointF[] { new PointF(18f, 12f), new PointF(20.5f, 14.5f), new PointF(20.5f, 20f), new PointF(18f, 17.5f) };

                        g.FillPolygon(b1, col1); g.DrawPolygon(edgePen, col1);
                        g.FillPolygon(b2, col2); g.DrawPolygon(edgePen, col2);
                        g.FillPolygon(b3, col3); g.DrawPolygon(edgePen, col3);
                        g.FillPolygon(b4, col4); g.DrawPolygon(edgePen, col4);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (key == null) return false;
                    var val = key.GetValue(APP_NAME);
                    return val != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        var exe = Application.ExecutablePath;
                        key.SetValue(APP_NAME, "\"" + exe + "\" --autostart");
                    }
                    else
                    {
                        key.DeleteValue(APP_NAME, false);
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
            RefreshNotificationArea();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint WM_MOUSEMOVE = 0x0200;

        /// <summary>
        /// 扫描并清理 Windows 系统托盘与溢出菜单中的残留“幽灵”图标 (Ghost Tray Icons)
        /// 当进程异常终止或被强制结束时，模拟鼠标微划过操作，触发 Explorer 自动注销死图标
        /// </summary>
        public static void RefreshNotificationArea()
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    // 1. 任务栏主通知区域
                    IntPtr hTray = FindWindow("Shell_TrayWnd", null);
                    if (hTray != IntPtr.Zero)
                    {
                        IntPtr hNotify = FindWindowEx(hTray, IntPtr.Zero, "TrayNotifyWnd", null);
                        if (hNotify != IntPtr.Zero)
                        {
                            IntPtr hSysPager = FindWindowEx(hNotify, IntPtr.Zero, "SysPager", null);
                            IntPtr hToolbar = FindWindowEx(hSysPager != IntPtr.Zero ? hSysPager : hNotify, IntPtr.Zero, "ToolbarWindow32", null);
                            if (hToolbar != IntPtr.Zero) SweepToolbar(hToolbar);
                        }
                    }

                    // 2. 托盘溢出浮窗区域 (用户点击向上箭头展开的网格，正是截图中的一堆图标所在处)
                    IntPtr hOverflow = FindWindow("NotifyIconOverflowWindow", null);
                    if (hOverflow != IntPtr.Zero)
                    {
                        IntPtr hToolbar = FindWindowEx(hOverflow, IntPtr.Zero, "ToolbarWindow32", null);
                        if (hToolbar != IntPtr.Zero) SweepToolbar(hToolbar);
                    }
                }
                catch { }
            });
        }

        private static void SweepToolbar(IntPtr hToolbar)
        {
            RECT r;
            if (GetClientRect(hToolbar, out r))
            {
                for (int x = 2; x < r.Right; x += 6)
                {
                    for (int y = 2; y < r.Bottom; y += 6)
                    {
                        IntPtr lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
                        SendMessage(hToolbar, WM_MOUSEMOVE, IntPtr.Zero, lParam);
                    }
                }
            }
        }
    }
}
