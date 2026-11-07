using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace XiaoLv.Desktop.Core
{
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    internal class VirtualDesktopManagerClass { }

    public class WorkspaceItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        public string CreatedAt { get; set; }
        public string Note { get; set; }
        public string VirtualDesktopId { get; set; }
        public string VirtualDesktopName { get; set; }
        public List<string> DocIds { get; set; }
        public List<string> DocTitles { get; set; }
        public List<string> LocalFiles { get; set; }
        public List<string> WebUrls { get; set; }

        public WorkspaceItem()
        {
            DocIds = new List<string>();
            DocTitles = new List<string>();
            LocalFiles = new List<string>();
            WebUrls = new List<string>();
            Icon = "🚀";
            VirtualDesktopName = "Windows 桌面";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 工作现场与情境工作台管理器 (Workspace Horizon & Session Snapshot)
    /// 1. 深度联动微软 Windows 虚拟桌面体系 (通过官方 IVirtualDesktopManager COM 接口零开销查询与跨桌面窗口调度)
    /// 2. 跨越软件沙箱边界，全景捕获思源多个项目文档、关联本地文献(SumatraPDF/Office/VS Code)与参考网址
    /// 3. 支持一键现场快照保存与 0ms 全域工作现场恢复
    /// 4. 极致本地资源保护：零后台常驻轮询线程，纯按需瞬时 COM 调度，用完即释放
    /// </summary>
    public static class WorkspaceSnapshotManager
    {
        private static readonly object _lock = new object();

        private static string GetStoragePath()
        {
            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoLvDesktop");
            if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
            return Path.Combine(appDir, "workspaces.json");
        }

        /// <summary>
        /// 纯按需瞬态查询窗口所在 Windows 虚拟桌面 GUID（零常驻内存开销）
        /// </summary>
        public static Guid GetWindowVirtualDesktopId(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return Guid.Empty;
            try
            {
                var vdm = (IVirtualDesktopManager)new VirtualDesktopManagerClass();
                Guid desktopId;
                int hr = vdm.GetWindowDesktopId(hwnd, out desktopId);
                Marshal.ReleaseComObject(vdm);
                if (hr == 0) return desktopId;
            }
            catch { }
            return Guid.Empty;
        }

        /// <summary>
        /// 纯按需将目标窗口拉取至当前激活的 Windows 虚拟桌面
        /// </summary>
        public static bool EnsureWindowOnCurrentVirtualDesktop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                var vdm = (IVirtualDesktopManager)new VirtualDesktopManagerClass();
                int onCurrent = 1;
                vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out onCurrent);
                if (onCurrent == 0)
                {
                    IntPtr selfHwnd = Process.GetCurrentProcess().MainWindowHandle;
                    Guid currentDesktop;
                    if (vdm.GetWindowDesktopId(selfHwnd, out currentDesktop) == 0 && currentDesktop != Guid.Empty)
                    {
                        vdm.MoveWindowToDesktop(hwnd, ref currentDesktop);
                    }
                }
                Marshal.ReleaseComObject(vdm);
                return true;
            }
            catch { }
            return false;
        }

        public static List<WorkspaceItem> ListWorkspaces()
        {
            lock (_lock)
            {
                var list = new List<WorkspaceItem>();
                try
                {
                    string path = GetStoragePath();
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path, Encoding.UTF8);
                        var rawList = JsonHelper.ParseList(json);
                        if (rawList != null)
                        {
                            foreach (var item in rawList)
                            {
                                var dict = item as Dictionary<string, object>;
                                if (dict != null)
                                {
                                    var ws = new WorkspaceItem
                                    {
                                        Id = JsonHelper.GetString(dict, "id"),
                                        Name = JsonHelper.GetString(dict, "name"),
                                        Icon = JsonHelper.GetString(dict, "icon", "🚀"),
                                        CreatedAt = JsonHelper.GetString(dict, "createdAt"),
                                        Note = JsonHelper.GetString(dict, "note"),
                                        VirtualDesktopId = JsonHelper.GetString(dict, "virtualDesktopId"),
                                        VirtualDesktopName = JsonHelper.GetString(dict, "virtualDesktopName", "Windows 虚拟桌面")
                                    };

                                    var dIds = JsonHelper.GetList(dict, "docIds");
                                    if (dIds != null)
                                    {
                                        foreach (var d in dIds) ws.DocIds.Add(d.ToString());
                                    }

                                    var dTitles = JsonHelper.GetList(dict, "docTitles");
                                    if (dTitles != null)
                                    {
                                        foreach (var t in dTitles) ws.DocTitles.Add(t.ToString());
                                    }

                                    var lFiles = JsonHelper.GetList(dict, "localFiles");
                                    if (lFiles != null)
                                    {
                                        foreach (var f in lFiles) ws.LocalFiles.Add(f.ToString());
                                    }

                                    var wUrls = JsonHelper.GetList(dict, "webUrls");
                                    if (wUrls != null)
                                    {
                                        foreach (var u in wUrls) ws.WebUrls.Add(u.ToString());
                                    }

                                    list.Add(ws);
                                }
                            }
                        }
                    }
                }
                catch { }

                // 预置默认工作现场（若为空）
                if (list.Count == 0)
                {
                    var demo = new WorkspaceItem
                    {
                        Id = "ws-demo-default",
                        Name = "外骨骼架构与核心复盘",
                        Icon = "🚀",
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                        Note = "日常开发、架构评审与思源文献全域工作现场",
                        VirtualDesktopName = "研发桌面"
                    };
                    demo.DocTitles.Add("周度规划与架构复盘");
                    demo.WebUrls.Add("https://b3log.org/siyuan");
                    list.Add(demo);
                }

                return list;
            }
        }

        public static bool SaveWorkspace(WorkspaceItem ws)
        {
            if (ws == null) return false;
            lock (_lock)
            {
                try
                {
                    var current = ListWorkspaces();
                    if (string.IsNullOrEmpty(ws.Id))
                    {
                        ws.Id = "ws-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    }
                    if (string.IsNullOrEmpty(ws.CreatedAt))
                    {
                        ws.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    }

                    int existIdx = current.FindIndex(x => x.Id == ws.Id);
                    if (existIdx >= 0)
                    {
                        current[existIdx] = ws;
                    }
                    else
                    {
                        current.Insert(0, ws);
                    }

                    string json = JsonHelper.ToJson(current);
                    File.WriteAllText(GetStoragePath(), json, new UTF8Encoding(false));
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool DeleteWorkspace(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_lock)
            {
                try
                {
                    var current = ListWorkspaces();
                    int removed = current.RemoveAll(x => x.Id == id);
                    if (removed > 0)
                    {
                        string json = JsonHelper.ToJson(current);
                        File.WriteAllText(GetStoragePath(), json, Encoding.UTF8);
                        return true;
                    }
                }
                catch { }
                return false;
            }
        }

        public static bool RestoreWorkspace(string id, Action<string, string> notify = null)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var list = ListWorkspaces();
            var target = list.Find(x => x.Id == id);
            if (target == null) return false;

            int docsOpened = 0;
            int filesOpened = 0;
            int urlsOpened = 0;

            // 1. 恢复唤醒全部绑定的思源文档
            if (target.DocIds != null && target.DocIds.Count > 0)
            {
                foreach (var docId in target.DocIds)
                {
                    if (string.IsNullOrEmpty(docId)) continue;
                    try
                    {
                        string syUri = "siyuan://blocks/" + docId;
                        Process.Start(new ProcessStartInfo(syUri) { UseShellExecute = true });
                        docsOpened++;
                    }
                    catch { }
                }
            }

            // 2. 恢复打开本地绑定的关联文献、报表与工程 (SumatraPDF/Office/VS Code)
            if (target.LocalFiles != null && target.LocalFiles.Count > 0)
            {
                foreach (var file in target.LocalFiles)
                {
                    if (string.IsNullOrEmpty(file)) continue;
                    try
                    {
                        if (File.Exists(file) || Directory.Exists(file))
                        {
                            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                            filesOpened++;
                        }
                    }
                    catch { }
                }
            }

            // 3. 恢复打开项目关联的核心网页与参考链接
            if (target.WebUrls != null && target.WebUrls.Count > 0)
            {
                foreach (var url in target.WebUrls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    try
                    {
                        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                            urlsOpened++;
                        }
                    }
                    catch { }
                }
            }

            SoundPlayerHelper.PlayFeedback();
            if (notify != null)
            {
                string msg = string.Format("✓ 已恢复【{0}】工作现场：打开 {1} 篇思源文档、{2} 个本地资产、{3} 个参考网页！", target.Name, docsOpened, filesOpened, urlsOpened);
                notify("思源外骨骼 · 工作现场", msg);
            }
            return true;
        }
    }
}
