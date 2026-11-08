using System;
using System.Threading;

namespace XiaoLv.Desktop.Core
{
    public class FlowTimerStatus
    {
        public bool IsRunning { get; set; }
        public string TaskDescription { get; set; }
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
            TotalMinutes = 25;
            TotalSeconds = 1500;
            RemainingSeconds = 0;
            StartTime = DateTime.Now;
            ProgressPercent = 0.0;
            TimeDisplay = "00:00 / 25:00";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 桌面轻量敏捷心流计时器与时序自动沉淀引擎 (FlowTimerManager)
    /// 1. 毫秒级启动桌面无感倒计时 (pom:25 / timer:45 任务名)，0% 待机 CPU
    /// 2. 计时器运转中通过托盘微徽章与轻提示呈现专注进度
    /// 3. 到期自动播放轻柔触觉音效，并自动将专注时序记录回写入思源今日日记
    /// 4. 自动挂载 custom-lifelog-type="focus" 原生 IAL 属性，对标 siyuan-checkin 习惯看板
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
            if (minutes <= 0) minutes = 25;
            if (string.IsNullOrEmpty(taskDescription)) taskDescription = "心流专注";

            lock (_lock)
            {
                StopTimerInternal();

                _currentStatus = new FlowTimerStatus
                {
                    IsRunning = true,
                    TaskDescription = taskDescription.Trim(),
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
                NotificationHandler("心流番茄钟 · 已启航", string.Format("⏱️ 已开启 {0} 分钟专注心流【{1}】", minutes, taskDescription));
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
                    RecordFocusCompletion(statusSnapshot.StartTime, DateTime.Now, elapsedMinutes, statusSnapshot.TaskDescription + " (提前结束)");
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
                RecordFocusCompletion(statusCopy.StartTime, DateTime.Now, statusCopy.TotalMinutes, statusCopy.TaskDescription);

                if (NotificationHandler != null)
                {
                    NotificationHandler("🎉 专注心流达成！", string.Format("✓ 恭喜完成 {0} 分钟【{1}】！时序已自动记入今日日记。", statusCopy.TotalMinutes, statusCopy.TaskDescription));
                }
            }
        }

        private static void RecordFocusCompletion(DateTime start, DateTime end, int minutes, string taskDesc)
        {
            ThreadPool.QueueUserWorkItem(s =>
            {
                try
                {
                    string focusNote = string.Format("~{0}m {1}", minutes, taskDesc);
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
