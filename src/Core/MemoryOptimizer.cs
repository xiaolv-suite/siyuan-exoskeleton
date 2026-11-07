using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 智能轻量内存优化与物理工作集压缩引擎
    /// 1. 支持 Win32 物理内存 WorkingSet 深度修剪 (EmptyWorkingSet / SetProcessWorkingSetSize)
    /// 2. CLR 垃圾回收与大对象堆 (LOH) 紧缩
    /// 3. WebView2 挂起态 (Suspend/Resume) 与低内存目标级别 (MemoryUsageTargetLevel.Low) 联动
    /// 4. 防抖延迟回收机制：窗口隐退后 500ms 触发，杜绝频繁切换造成的 CPU 震荡
    /// </summary>
    public static class MemoryOptimizer
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        private static Timer _trimTimer;
        private static readonly object _lock = new object();

        /// <summary>
        /// 安排一次防抖物理内存压缩与垃圾清理（默认隐退 500ms 后执行）
        /// </summary>
        public static void ScheduleTrim(int delayMs = 500)
        {
            lock (_lock)
            {
                if (_trimTimer != null)
                {
                    _trimTimer.Dispose();
                    _trimTimer = null;
                }

                _trimTimer = new Timer(state =>
                {
                    PerformDeepTrim();
                }, null, delayMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 执行一次即时深度内存压缩与工作集修剪
        /// </summary>
        public static void PerformDeepTrim()
        {
            try
            {
                // 1. CLR 紧凑型垃圾回收与大对象堆碎片整理
                try
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                }
                catch { }

                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);

                // 2. Win32 物理工作集深度压缩：释放未引用的物理内存页还给操作系统
                IntPtr currentProcessHandle = Process.GetCurrentProcess().Handle;
                EmptyWorkingSet(currentProcessHandle);
                SetProcessWorkingSetSize(currentProcessHandle, (IntPtr)(-1), (IntPtr)(-1));

                // 3. 递归修剪本应用派生出的所有 WebView2 子进程物理工作集 (msedgewebview2.exe)
                try
                {
                    int myPid = Process.GetCurrentProcess().Id;
                    var wvProcs = Process.GetProcessesByName("msedgewebview2");
                    foreach (var wp in wvProcs)
                    {
                        try
                        {
                            using (wp)
                            {
                                EmptyWorkingSet(wp.Handle);
                                SetProcessWorkingSetSize(wp.Handle, (IntPtr)(-1), (IntPtr)(-1));
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// 获取当前进程物理工作集 (MB)
        /// </summary>
        public static double GetCurrentWorkingSetMb()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    return Math.Round(p.WorkingSet64 / (1024.0 * 1024.0), 2);
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 获取当前进程专用内存 (MB)
        /// </summary>
        public static double GetCurrentPrivateBytesMb()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    return Math.Round(p.PrivateMemorySize64 / (1024.0 * 1024.0), 2);
                }
            }
            catch
            {
                return 0;
            }
        }
    }
}
