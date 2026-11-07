using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 思源外骨骼 · 桌面极速语音速记舱与口语清洗引擎 (Voice HUD & Speech Denoising)
    /// 1. 深度融合桌面端语音输入生态 (微信输入法电脑版 / 讯飞 / 搜狗 / Windows 11 Win+H 语音键)
    /// 2. 智能口语去噪自愈引擎 (剥离"呃/那个/就是说"语气词，口头标点自动还原，中英术语自愈)
    /// 3. 本地音频速记直投日记 (支持 .mp3 / .wav / .m4a 资产秒传并构建播放器块)
    /// </summary>
    public static class VoiceCaptureManager
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_LWIN = 0x5B;
        private const byte VK_H = 0x48;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>
        /// 模拟按下 Win+H 瞬时呼出 Windows 10/11 原生极速语音听写栏
        /// </summary>
        public static void TriggerWindowsVoiceTyping()
        {
            try
            {
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_H, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                keybd_event(VK_H, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        /// <summary>
        /// 口语转文字深度去噪与标点排版自愈引擎
        /// 解决电脑语音输入法常见的：长句不断句、结巴口吃、口头语气助词泛滥等痛点
        /// </summary>
        public static string CleanVoiceSpeech(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return "";
            string t = rawText.Trim();

            // 1. 口述标点指令自愈还原 (如用户说"逗号"、"句号"、"换行"、"问号")
            t = Regex.Replace(t, @"(?<=[a-zA-Z0-9\u4e00-\u9fa5])\s*逗号\s*", "，");
            t = Regex.Replace(t, @"(?<=[a-zA-Z0-9\u4e00-\u9fa5])\s*句号\s*", "。");
            t = Regex.Replace(t, @"(?<=[a-zA-Z0-9\u4e00-\u9fa5])\s*问号\s*", "？");
            t = Regex.Replace(t, @"(?<=[a-zA-Z0-9\u4e00-\u9fa5])\s*感叹号\s*", "！");
            t = Regex.Replace(t, @"(?<=[a-zA-Z0-9\u4e00-\u9fa5])\s*(?:换行|另起一行)\s*", "\n");

            // 2. 消除常见结巴卡顿重复词 (如 "我我我" -> "我", "这个这个" -> "这个", "然后然后" -> "然后")
            t = Regex.Replace(t, @"(那个|就是|然后|这个)\1+", "$1");
            t = Regex.Replace(t, @"([\u4e00-\u9fa5])\1{2,}", "$1");

            // 3. 口头废话与停顿垫字清洗过滤
            var fillerWords = new string[]
            {
                "呃…", "呃...", "呃，", "呃",
                "嗯…", "嗯...", "嗯，", "嗯",
                "啊…", "啊...",
                "那个就是说", "就是说那个", "可能就是说",
                "怎么说呢，", "怎么说呢",
                "基本上来说", "大概就是"
            };

            foreach (var fw in fillerWords)
            {
                t = t.Replace(fw, "");
            }

            // 4. 清理冗余多重标点与头尾留白
            t = Regex.Replace(t, @"[，,]{2,}", "，");
            t = Regex.Replace(t, @"[。.]{2,}", "。");
            t = Regex.Replace(t, @"\s+", " ");
            t = t.Trim(' ', '，', ',', '；', ';');

            // 5. 核心专有名词自动规范自愈 (支持中英混排无空格场景)
            t = Regex.Replace(t, @"(?i)markdown", "Markdown");
            t = Regex.Replace(t, @"(?i)(?<=^|[^\w]|[\u4e00-\u9fa5])api(?=$|[^\w]|[\u4e00-\u9fa5])", "API");
            t = Regex.Replace(t, @"(?i)(?<=^|[^\w]|[\u4e00-\u9fa5])fsrs(?=$|[^\w]|[\u4e00-\u9fa5])", "FSRS");
            t = Regex.Replace(t, @"(?i)(?<=^|[^\w]|[\u4e00-\u9fa5])pdf(?=$|[^\w]|[\u4e00-\u9fa5])", "PDF");
            t = Regex.Replace(t, @"(?i)(?<=^|[^\w]|[\u4e00-\u9fa5])word(?=$|[^\w]|[\u4e00-\u9fa5])", "Word");
            t = Regex.Replace(t, @"(?i)(?<=^|[^\w]|[\u4e00-\u9fa5])excel(?=$|[^\w]|[\u4e00-\u9fa5])", "Excel");
            t = Regex.Replace(t, @"思源(?!(?:笔记))", "思源笔记");

            return t.Trim();
        }

        /// <summary>
        /// 本地音频速记文件上传思源资产库并追加至今日日记
        /// </summary>
        public static bool IngestAudioMemo(string audioPath, string noteTitle, Action<string, string> notify)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return false;

            try
            {
                string syUrl = "";
                string token = "";
                SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
                if (string.IsNullOrEmpty(syUrl)) return false;

                string relPath, err;
                if (!FileDispatcher.UploadAsset(syUrl, token, audioPath, out relPath, out err))
                {
                    if (notify != null) notify("语音速记", "❌ 上传音频至思源失败: " + err);
                    return false;
                }

                string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);
                string targetDocId = ContextPenMain.GetOrCreateDailyNote(syUrl, token, diaryNb, DateTime.Now.ToString("yyyyMMdd"), DateTime.Now.ToString("yyyy-MM-dd"));
                if (string.IsNullOrEmpty(targetDocId)) return false;

                string timeStr = DateTime.Now.ToString("HH:mm");
                string desc = !string.IsNullOrEmpty(noteTitle) ? noteTitle : "桌面语音速记录音";
                string banner = string.Format("- {0} 🎙️ **{1}**\n\n<audio controls src=\"{2}\" style=\"width: 100%; max-width: 480px;\"></audio>\n{{: custom-lifelog-type=\"voice\" }}", timeStr, desc, relPath);

                var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", JsonHelper.ToJson(new
                {
                    parentID = targetDocId,
                    dataType = "markdown",
                    data = banner
                }));

                if (r.Success)
                {
                    SoundPlayerHelper.PlayFeedback();
                    if (notify != null) notify("思源外骨骼 · 语音速记", "✓ 音频录音已成功存入今日日记！");
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
