using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace XiaoLv.Desktop.Core
{
    public class SyncStatusSnapshot
    {
        public bool IsOnline { get; set; }
        public bool IsSyncing { get; set; }
        public long SyncedEpochMs { get; set; }
        public string RelativeTimeDesc { get; set; }
        public string StatSummary { get; set; }
    }

    /// <summary>
    /// 轻量同步安全守护 (SafeSyncGuardian)：
    /// 严守「零破坏、纯辅助、极度克制」三原则，排除思源同步的 7 大雷点：
    /// 1. 并发重入防御：进程级互斥锁 + 内核 task 探针，杜绝重复调用引发内核死锁；
    /// 2. 离线静默防护：基于 NetworkInterface.GetIsNetworkAvailable()，断网环境静默退避，绝不弹窗轰炸；
    /// 3. 只读感知优先：通过 getSyncInfo / getSyncCheckout 获取状态，不污染任何业务文档；
    /// 4. 休眠唤醒保护：监听 SystemEvents.PowerModeChanged，恢复唤醒后强制静默等待 8 秒（避开网卡假连窗口）；
    /// 5. 官方规则遵从：performSync 传 {}，由内核按官方合并算法运行，绝不强行指定单向覆盖。
    /// </summary>
    public static class SafeSyncGuardian
    {
        private static int _syncingFlag = 0;
        private static int _lastSyncTick = 0;
        private const int DebounceMs = 10000; // 最低 10 秒防抖，防止用户误触狂点
        private static Timer _wakeDelayTimer;
        private static bool _powerRegistered = false;
        private static readonly object _lock = new object();

        public static long LastKnownSyncedEpochMs { get; private set; }
        public static string LastKnownRelativeTime { get; private set; }

        static SafeSyncGuardian()
        {
            LastKnownSyncedEpochMs = 0;
            LastKnownRelativeTime = "未同步";
        }

        public static void RegisterPowerWatcher(Action<string, string> notifyCallback)
        {
            lock (_lock)
            {
                if (_powerRegistered) return;
                try
                {
                    SystemEvents.PowerModeChanged += (s, e) =>
                    {
                        if (e.Mode == PowerModes.Resume)
                        {
                            // 计算机从睡眠/休眠唤醒：绝不立刻请求网络！
                            // 启动 8 秒安全延迟定时器，等待网络适配器完全 DHCP 联机
                            if (_wakeDelayTimer != null)
                            {
                                try { _wakeDelayTimer.Dispose(); } catch { }
                            }
                            _wakeDelayTimer = new Timer(state =>
                            {
                                try
                                {
                                    if (NetworkInterface.GetIsNetworkAvailable())
                                    {
                                        // 唤醒仅做纯只读状态探测，更新灵动坞显示
                                        RefreshStatusAsync().Wait();
                                    }
                                }
                                catch { }
                            }, null, 8000, Timeout.Infinite);
                        }
                    };
                    _powerRegistered = true;
                }
                catch { }
            }
        }

        private static Timer _warmupTimer;

        /// <summary>
        /// 启动后延迟 12 秒轻量探针预热（纯只读，避开应用启动高峰）
        /// </summary>
        public static void StartBackgroundWarmup()
        {
            if (_warmupTimer != null) return;
            _warmupTimer = new Timer(st =>
            {
                try
                {
                    if (NetworkInterface.GetIsNetworkAvailable())
                    {
                        var t = RefreshStatusAsync();
                        t.Wait(3000);
                    }
                }
                catch { }
                finally
                {
                    if (_warmupTimer != null)
                    {
                        try { _warmupTimer.Dispose(); } catch { }
                        _warmupTimer = null;
                    }
                }
            }, null, 12000, Timeout.Infinite);
        }

        /// <summary>
        /// 获取当前同步状态摘要（纯只读，零污染）
        /// </summary>
        public static async Task<SyncStatusSnapshot> RefreshStatusAsync()
        {
            var snap = new SyncStatusSnapshot
            {
                IsOnline = NetworkInterface.GetIsNetworkAvailable(),
                IsSyncing = _syncingFlag == 1,
                SyncedEpochMs = LastKnownSyncedEpochMs,
                RelativeTimeDesc = LastKnownRelativeTime,
                StatSummary = ""
            };

            if (!snap.IsOnline)
            {
                snap.RelativeTimeDesc = "离线";
                LastKnownRelativeTime = "离线";
                return snap;
            }

            try
            {
                // 1. 查询同步信息
                var res = await SiYuanClient.PostAsync("/api/sync/getSyncInfo", new { });
                if (res != null && res.Success && !string.IsNullOrEmpty(res.RawText))
                {
                    // 解析 synced: 1794281074166
                    var m = Regex.Match(res.RawText, @"""synced""\s*:\s*(\d+)");
                    if (m.Success)
                    {
                        long ms;
                        if (long.TryParse(m.Groups[1].Value, out ms))
                        {
                            LastKnownSyncedEpochMs = ms;
                            snap.SyncedEpochMs = ms;
                            snap.RelativeTimeDesc = FormatRelativeTime(ms);
                            LastKnownRelativeTime = snap.RelativeTimeDesc;
                        }
                    }
                }

                // 2. 查询是否有运行中的任务
                var chk = await SiYuanClient.PostAsync("/api/sync/getSyncCheckout", new { });
                if (chk != null && chk.Success && !string.IsNullOrEmpty(chk.RawText))
                {
                    // 若 task 字段存在且不为 null 则正在同步
                    var mTask = Regex.Match(chk.RawText, @"""task""\s*:\s*([^,}]+)");
                    if (mTask.Success && mTask.Groups[1].Value.Trim() != "null")
                    {
                        snap.IsSyncing = true;
                    }
                }
            }
            catch { }

            return snap;
        }

        /// <summary>
        /// 执行安全增量同步（带 3 级安全门禁：断网防御、并发重入防御、防抖保护）
        /// </summary>
        public static void TriggerSafeSync(bool isManual, Action<string, string> notifyCallback)
        {
            ThreadPool.QueueUserWorkItem(async s =>
            {
                // 门禁 1：网络可用性检查（排雷 6：断网骚扰）
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    if (isManual && notifyCallback != null)
                    {
                        notifyCallback("思源外骨骼 · 云同步", "当前处于离线模式，已安全跳过同步。");
                    }
                    return;
                }

                // 门禁 2：进程内部互斥锁（排雷 2：重入并发）
                if (Interlocked.CompareExchange(ref _syncingFlag, 1, 0) != 0)
                {
                    if (isManual && notifyCallback != null)
                    {
                        notifyCallback("思源外骨骼 · 云同步", "云端增量同步正在进行中，请稍候...");
                    }
                    return;
                }

                try
                {
                    // 门禁 3：内核态任务探针检查
                    try
                    {
                        var chk = await SiYuanClient.PostAsync("/api/sync/getSyncCheckout", new { });
                        if (chk != null && chk.Success && !string.IsNullOrEmpty(chk.RawText))
                        {
                            var mTask = Regex.Match(chk.RawText, @"""task""\s*:\s*([^,}]+)");
                            if (mTask.Success && mTask.Groups[1].Value.Trim() != "null")
                            {
                                if (isManual && notifyCallback != null)
                                {
                                    notifyCallback("思源外骨骼 · 云同步", "思源内核后台同步尚未就绪，请稍后重试。");
                                }
                                return;
                            }
                        }
                    }
                    catch { }

                    // 门禁 4：防抖时间检查（最低间隔）
                    int nowTick = Environment.TickCount;
                    if (isManual && (nowTick - _lastSyncTick) < DebounceMs && _lastSyncTick != 0)
                    {
                        if (notifyCallback != null)
                        {
                            notifyCallback("思源外骨骼 · 云同步", "刚刚已完成同步，无需频繁触发。");
                        }
                        return;
                    }

                    // 调用思源内核原生 API（排雷 5：传空参数遵从内核官方规则，绝不强制覆盖）
                    var res = await SiYuanClient.PostAsync("/api/sync/performSync", new { });
                    bool ok = res != null && res.Success;

                    if (ok)
                    {
                        _lastSyncTick = Environment.TickCount;
                        try { SoundPlayerHelper.PlayFeedback(); } catch { }
                        await RefreshStatusAsync();
                        if (isManual && notifyCallback != null)
                        {
                            notifyCallback("思源外骨骼 · 云同步", "✓ 思源云端增量同步完成！(" + LastKnownRelativeTime + ")");
                        }
                    }
                    else
                    {
                        if (isManual && notifyCallback != null)
                        {
                            string reason = res != null ? res.Error : "内核无响应";
                            notifyCallback("思源外骨骼 · 云同步", "⚠️ 同步未完成: " + (string.IsNullOrEmpty(reason) ? "超时或繁忙" : reason));
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _syncingFlag, 0);
                }
            });
        }

        public static string FormatRelativeTime(long epochMs)
        {
            if (epochMs <= 0) return "未同步";
            var now = DateTime.UtcNow;
            var dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(epochMs);
            var span = now - dt;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;

            if (span.TotalSeconds < 45) return "刚刚";
            if (span.TotalMinutes < 60) return string.Format("{0}分钟前", Math.Max(1, (int)span.TotalMinutes));
            if (span.TotalHours < 24) return string.Format("{0}小时前", (int)span.TotalHours);
            if (span.TotalDays < 30) return string.Format("{0}天前", (int)span.TotalDays);
            return dt.ToLocalTime().ToString("MM-dd HH:mm");
        }
    }
}
