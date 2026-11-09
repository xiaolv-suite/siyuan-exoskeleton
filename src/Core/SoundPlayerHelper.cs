using System;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 免切屏桌面操作触觉/轻音效反馈引擎
    /// 在速记入库、双链复制或任务勾选时提供微妙瞬发反馈，无阻断感
    /// </summary>
    public static class SoundPlayerHelper
    {
        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        public static void PlayFeedback()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIYUAN_URL"))) return;
            if (!AppSettings.Instance.SoundFeedbackEnabled) return;

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    SystemSounds.Asterisk.Play();
                }
                catch
                {
                    try { MessageBeep(0x00000040); } catch { }
                }
            });
        }
    }
}
