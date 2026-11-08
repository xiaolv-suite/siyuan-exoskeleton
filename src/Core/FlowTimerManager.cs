using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class FlowTimerStatus
    {
        public bool IsRunning { get; set; }
        public string TaskDescription { get; set; }
        public string TaskBlockId { get; set; }
        public string DocTitle { get; set; }
        public bool AutoCompleteTask { get; set; }
        public int TotalMinutes { get; set; }
        public int TotalSeconds { get; set; }
        public int RemainingSeconds { get; set; }
        public DateTime StartTime { get; set; }
        public double ProgressPercent { get; set; }
        public string TimeDisplay { get; set; }

        public FlowTimerStatus()
        {
            IsRunning = false;
            TaskDescription = "心流专注";
            TaskBlockId = "";
            DocTitle = "";
            AutoCompleteTask = true;
            TotalMinutes = 25;
            TotalSeconds = 1500;
            RemainingSeconds = 0;
            StartTime = DateTime.Now;
            ProgressPercent = 0.0;
            TimeDisplay = "00:00 / 25:00";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 桌面轻量敏捷心流计时器与行动闭环引擎 (FlowTimerManager)
    /// 1. 毫秒级启动桌面无感倒计时 (pom:25 / timer:45 任务名)，0% 待机 CPU
    /// 2. 支持与思源 Task Horizon 待办任务/块级双链深度绑定 (TaskBlockId / DocTitle)
    /// 3. 到期自动播放轻柔触觉音效，并将专注时序记录回写入今日日记 (~25m 任务 ((blockId)))
    /// 4. 自动勾销关联任务 (- [ ] ➔ - [x])，实现「专注-结绳-完成」闭环
    /// 5. 自动挂载 custom-lifelog-type="focus" 原生 IAL 属性，对标 siyuan-checkin 习惯看板与 52 周热力图
    /// </summary>
    public static class FlowTimerManager
    {
        private static Timer _timer;
        private static readonly object _lock = new object();
        private static FlowTimerStatus _currentStatus = new FlowTimerStatus();

        public static Action<FlowTimerStatus> OnTimerTick { get; set; }
        public static Action<string, string> NotificationHandler { get; set; }

        public static FlowTimerStatus CurrentStatus
        {
            get
            {
                lock (_lock)
                {
                    return _currentStatus;
                }
            }
        }

        public static bool StartTimer(int minutes, string taskDescription = "")
        {
            return StartTimer(minutes, taskDescription, "", "", true);
        }

        public static bool StartTimer(int minutes, string taskDescription, string taskBlockId, string docTitle, bool autoCompleteTask)
        {
            if (minutes <= 0) minutes = 25;
            if (string.IsNullOrEmpty(taskDescription)) taskDescription = "心流专注";

            lock (_lock)
            {
                StopTimerInternal();

                _currentStatus = new FlowTimerStatus
                {
                    IsRunning = true,
                    TaskDescription = taskDescription.Trim(),
                    TaskBlockId = (taskBlockId ?? "").Trim(),
                    DocTitle = (docTitle ?? "").Trim(),
                    AutoCompleteTask = autoCompleteTask,
                    TotalMinutes = minutes,
                    TotalSeconds = minutes * 60,
                    RemainingSeconds = minutes * 60,
                    StartTime = DateTime.Now,
                    ProgressPercent = 0.0,
                    TimeDisplay = FormatTimeDisplay(minutes * 60, minutes * 60)
                };

                _timer = new Timer(TimerTickCallback, null, 1000, 1000);
            }

            if (NotificationHandler != null)
            {
                string extra = !string.IsNullOrEmpty(taskBlockId) ? " · 已绑定待办任务" : "";
                NotificationHandler("心流番茄钟 · 已启航", string.Format("⏱️ 已开启 {0} 分钟专注心流【{1}】{2}", minutes, taskDescription, extra));
            }

            return true;
        }

        public static void StopTimer(bool recordPartial = false)
        {
            FlowTimerStatus statusSnapshot;
            lock (_lock)
            {
                statusSnapshot = _currentStatus;
                StopTimerInternal();
            }

            if (recordPartial && statusSnapshot.IsRunning)
            {
                int elapsedMinutes = (statusSnapshot.TotalSeconds - statusSnapshot.RemainingSeconds) / 60;
                if (elapsedMinutes >= 3)
                {
                    RecordFocusCompletion(statusSnapshot.StartTime, DateTime.Now, elapsedMinutes, statusSnapshot.TaskDescription + " (提前结束)", statusSnapshot.TaskBlockId, statusSnapshot.DocTitle);
                }
            }
        }

        private static void StopTimerInternal()
        {
            if (_timer != null)
            {
                try { _timer.Dispose(); } catch { }
                _timer = null;
            }
            _currentStatus.IsRunning = false;
        }

        private static void TimerTickCallback(object state)
        {
            FlowTimerStatus statusCopy;
            bool isFinished = false;

            lock (_lock)
            {
                if (!_currentStatus.IsRunning) return;

                _currentStatus.RemainingSeconds--;
                if (_currentStatus.RemainingSeconds <= 0)
                {
                    _currentStatus.RemainingSeconds = 0;
                    _currentStatus.ProgressPercent = 100.0;
                    _currentStatus.TimeDisplay = FormatTimeDisplay(0, _currentStatus.TotalSeconds);
                    _currentStatus.IsRunning = false;
                    isFinished = true;
                    if (_timer != null)
                    {
                        try { _timer.Dispose(); } catch { }
                        _timer = null;
                    }
                }
                else
                {
                    int elapsed = _currentStatus.TotalSeconds - _currentStatus.RemainingSeconds;
                    _currentStatus.ProgressPercent = Math.Round((double)elapsed / _currentStatus.TotalSeconds * 100.0, 1);
                    _currentStatus.TimeDisplay = FormatTimeDisplay(_currentStatus.RemainingSeconds, _currentStatus.TotalSeconds);
                }

                statusCopy = _currentStatus;
            }

            if (OnTimerTick != null)
            {
                try { OnTimerTick(statusCopy); } catch { }
            }

            if (isFinished)
            {
                // 到期完成处理
                SoundPlayerHelper.PlayFeedback();
                RecordFocusCompletion(statusCopy.StartTime, DateTime.Now, statusCopy.TotalMinutes, statusCopy.TaskDescription, statusCopy.TaskBlockId, statusCopy.DocTitle);

                // 自动勾销关联任务
                if (statusCopy.AutoCompleteTask && !string.IsNullOrEmpty(statusCopy.TaskBlockId))
                {
                    ThreadPool.QueueUserWorkItem(async s =>
                    {
                        await CompleteAssociatedTaskAsync(statusCopy.TaskBlockId, statusCopy.TaskDescription);
                    });
                }

                if (NotificationHandler != null)
                {
                    string taskNotice = !string.IsNullOrEmpty(statusCopy.TaskBlockId) ? "（关联任务已自动勾选完成）" : "";
                    NotificationHandler("🎉 专注心流达成！", string.Format("✓ 恭喜完成 {0} 分钟【{1}】！{2}时序已记入今日日记。", statusCopy.TotalMinutes, statusCopy.TaskDescription, taskNotice));
                }
            }
        }

        /// <summary>
        /// 异步将思源内指定的任务清单块从未完成 [- ] 翻转为已完成 [- x]
        /// </summary>
        public static async Task<bool> CompleteAssociatedTaskAsync(string blockId, string taskTitle = "")
        {
            if (string.IsNullOrEmpty(blockId)) return false;

            try
            {
                string sql = "SELECT id, content, markdown FROM blocks WHERE id = '" + blockId.Replace("'", "''") + "' LIMIT 1";
                var rows = await SiYuanClient.SqlAsync(sql);
                if (rows != null && rows.Count > 0)
                {
                    string md = JsonHelper.GetString(rows[0], "markdown");
                    string content = JsonHelper.GetString(rows[0], "content");
                    string target = !string.IsNullOrEmpty(md) ? md : content;

                    if (target.Contains("[ ]") || target.Contains("[]"))
                    {
                        string updated = Regex.Replace(target, @"-\s*\[\s*\]", "- [x]");
                        var res = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                        {
                            id = blockId,
                            dataType = "markdown",
                            data = updated
                        });
                        return res != null && res.Success;
                    }
                }
            }
            catch { }

            return false;
        }

        private static void RecordFocusCompletion(DateTime start, DateTime end, int minutes, string taskDesc, string taskBlockId = "", string docTitle = "")
        {
            ThreadPool.QueueUserWorkItem(s =>
            {
                try
                {
                    string taskRef = "";
                    if (!string.IsNullOrEmpty(taskBlockId))
                    {
                        string anchor = !string.IsNullOrEmpty(docTitle) ? docTitle : "任务";
                        taskRef = string.Format(" (({0} \"{1}\"))", taskBlockId, anchor.Replace("\"", "'"));
                    }

                    string focusNote = string.Format("~{0}m {1}{2}", minutes, taskDesc, taskRef);
                    var ctx = new DesktopStepContext("SYS", focusNote);
                    ctx.SetVarValue("来源应用", "思源外骨骼 · 心流番茄钟");
                    SmartCaptureMain.Exec(ctx);
                }
                catch { }
            });
        }

        private static string FormatTimeDisplay(int remaining, int total)
        {
            int rM = remaining / 60;
            int rS = remaining % 60;
            int tM = total / 60;
            return string.Format("{0:D2}:{1:D2} / {2:D2}:00", rM, rS, tM);
        }
    }
}
