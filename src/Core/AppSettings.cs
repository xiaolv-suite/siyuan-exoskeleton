using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class AppSettings
    {
        private static AppSettings _instance;
        private static readonly object _instanceLock = new object();
        public static AppSettings Instance
        {
            get
            {
                lock (_instanceLock)
                {
                    if (_instance == null) _instance = Load();
                    return _instance;
                }
            }
        }

        public static void ReplaceInstance(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            lock (_instanceLock) _instance = settings;
        }

        public string MasterHotKey { get; set; }
        public string PenHotKey { get; set; }
        public string OcrHotKey { get; set; }
        public string QuickHotKey { get; set; }
        public string FsrsHotKey { get; set; }
        public string ClipHotKey { get; set; }
        public string PreferredSnippingTool { get; set; }
        public string PreferredSearchEngine { get; set; }
        public string DailyNoteTargetHeading { get; set; }

        /// <summary>指定日记笔记本 ID 或名称 (留空则自动按思源历史/日记名探测)</summary>
        public string DailyNoteTargetNotebook { get; set; }

        /// <summary>系统级提醒守护总开关 (到期闪卡与今日待办气泡通知)</summary>
        public bool ReminderGuardianEnabled { get; set; }
        public int QuickNoteContinuationSec { get; set; }
        public string QuickNoteTargetDoc { get; set; }
        public bool AutoFloatEnabled { get; set; }
        public bool SmartClipboardEnabled { get; set; }
        public bool AutoStart { get; set; }
        public bool SoundFeedbackEnabled { get; set; }
        public bool ShellContextMenuEnabled { get; set; }
        public string PinnedDocsJson { get; set; }
        public string DefaultTheme { get; set; }
        public double PanelWidth { get; set; }
        public double PanelHeight { get; set; }
        public bool LifelogAutoSectionEnabled { get; set; }
        public string WebClipDefaultMode { get; set; }
        public string WebClipTargetFolder { get; set; }

        /// <summary>P1-4 剪藏域名路由规则，每行一条: 域名 => /目标文件夹/</summary>
        public string ClipDomainRules { get; set; }

        /// <summary>P2-2 AI 路由目标 (JSON 数组: [{name,url}]，URL 模板用 {query} 占位)</summary>
        public string AiRouteTargetsJson { get; set; }
        public string WebClipCustomTemplate { get; set; }
        public bool AiEnabled { get; set; }
        public string AiBaseUrl { get; set; }
        public string AiModel { get; set; }
        public string AiApiKey { get; set; }
        public string CustomSnippetsJson { get; set; }
        public bool WebhookEnabled { get; set; }
        public int WebhookPort { get; set; }
        public string WebhookToken { get; set; }
        public bool DropZoneEnabled { get; set; }
        public bool DoubleCtrlActivationEnabled { get; set; }

        public AppSettings()
        {
            MasterHotKey = "Alt+Shift+Q";
            PenHotKey = "Alt+Shift+W";
            OcrHotKey = "Alt+Shift+C";
            QuickHotKey = "Alt+Shift+S";
            FsrsHotKey = "Alt+Shift+R";
            ClipHotKey = "Alt+Shift+G";
            PreferredSnippingTool = "auto";
            PreferredSearchEngine = "auto";
            DailyNoteTargetHeading = "";
            DailyNoteTargetNotebook = "";
            ReminderGuardianEnabled = true;
            QuickNoteContinuationSec = 30;
            QuickNoteTargetDoc = "";
            AutoFloatEnabled = false;
            SmartClipboardEnabled = true;
            AutoStart = false;
            SoundFeedbackEnabled = true;
            ShellContextMenuEnabled = true;
            PinnedDocsJson = "[]";
            DefaultTheme = "dark";
            PanelWidth = 1180;
            PanelHeight = 740;
            LifelogAutoSectionEnabled = true;
            WebClipDefaultMode = "card";
            WebClipTargetFolder = "/读库/";
            ClipDomainRules = "";
            AiRouteTargetsJson = "[{\"name\":\"豆包\",\"url\":\"https://www.doubao.com/chat?q={query}\"},{\"name\":\"千问\",\"url\":\"https://chat.qwen.ai/?q={query}\"},{\"name\":\"Kimi\",\"url\":\"https://www.kimi.com/\"},{\"name\":\"秘塔AI搜索\",\"url\":\"https://metaso.cn/?q={query}\"}]";
            WebClipCustomTemplate = "";
            AiEnabled = false;
            AiBaseUrl = "http://localhost:11434/v1";
            AiModel = "qwen2.5:latest";
            AiApiKey = "";
            CustomSnippetsJson = "[]";
            WebhookEnabled = false;
            WebhookPort = 43989;
            WebhookToken = "";
            DropZoneEnabled = true;
            DoubleCtrlActivationEnabled = true;
        }

        private static string SettingsFilePath
        {
            get
            {
                return Path.Combine(EmbeddedResourceManager.AppDataDir, "settings.json");
            }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                var p = SettingsFilePath;
                if (File.Exists(p))
                {
                    var json = File.ReadAllText(p, Encoding.UTF8);
                    var dict = JsonHelper.ParseDict(json);
                    s.MasterHotKey = JsonHelper.GetString(dict, "masterHotKey", s.MasterHotKey);
                    s.PenHotKey = JsonHelper.GetString(dict, "penHotKey", s.PenHotKey);
                    s.OcrHotKey = JsonHelper.GetString(dict, "ocrHotKey", s.OcrHotKey);
                    s.QuickHotKey = JsonHelper.GetString(dict, "quickHotKey", s.QuickHotKey);
                    s.FsrsHotKey = JsonHelper.GetString(dict, "fsrsHotKey", s.FsrsHotKey);
                    s.ClipHotKey = JsonHelper.GetString(dict, "clipHotKey", s.ClipHotKey);
                    s.PreferredSnippingTool = JsonHelper.GetString(dict, "preferredSnippingTool", s.PreferredSnippingTool);
                    s.PreferredSearchEngine = JsonHelper.GetString(dict, "preferredSearchEngine", s.PreferredSearchEngine);
	                    s.DailyNoteTargetHeading = JsonHelper.GetString(dict, "dailyNoteTargetHeading", s.DailyNoteTargetHeading);
	                    s.DailyNoteTargetNotebook = JsonHelper.GetString(dict, "dailyNoteTargetNotebook", s.DailyNoteTargetNotebook);
	                    s.ReminderGuardianEnabled = JsonHelper.GetBool(dict, "reminderGuardianEnabled", s.ReminderGuardianEnabled);
	                    s.QuickNoteContinuationSec = JsonHelper.GetInt(dict, "quickNoteContinuationSec", s.QuickNoteContinuationSec);
                    s.QuickNoteTargetDoc = JsonHelper.GetString(dict, "quickNoteTargetDoc", s.QuickNoteTargetDoc);
                    s.DefaultTheme = JsonHelper.GetString(dict, "defaultTheme", s.DefaultTheme);
                    s.AutoFloatEnabled = JsonHelper.GetBool(dict, "autoFloatEnabled", s.AutoFloatEnabled);
                    s.SmartClipboardEnabled = JsonHelper.GetBool(dict, "smartClipboardEnabled", s.SmartClipboardEnabled);
                    s.AutoStart = JsonHelper.GetBool(dict, "autoStart", s.AutoStart);
                    s.SoundFeedbackEnabled = JsonHelper.GetBool(dict, "soundFeedbackEnabled", s.SoundFeedbackEnabled);
                    s.ShellContextMenuEnabled = JsonHelper.GetBool(dict, "shellContextMenuEnabled", s.ShellContextMenuEnabled);
                    s.PinnedDocsJson = JsonHelper.GetString(dict, "pinnedDocsJson", s.PinnedDocsJson);
                    s.PanelWidth = JsonHelper.GetDouble(dict, "panelWidth", s.PanelWidth);
                    s.PanelHeight = JsonHelper.GetDouble(dict, "panelHeight", s.PanelHeight);
                    s.LifelogAutoSectionEnabled = JsonHelper.GetBool(dict, "lifelogAutoSectionEnabled", s.LifelogAutoSectionEnabled);
                    s.WebClipDefaultMode = JsonHelper.GetString(dict, "webClipDefaultMode", s.WebClipDefaultMode);
                    s.WebClipTargetFolder = JsonHelper.GetString(dict, "webClipTargetFolder", s.WebClipTargetFolder);
                    s.WebClipCustomTemplate = JsonHelper.GetString(dict, "webClipCustomTemplate", s.WebClipCustomTemplate);
                    s.ClipDomainRules = JsonHelper.GetString(dict, "clipDomainRules", s.ClipDomainRules);
                    s.AiRouteTargetsJson = JsonHelper.GetString(dict, "aiRouteTargetsJson", s.AiRouteTargetsJson);
                    s.AiEnabled = JsonHelper.GetBool(dict, "aiEnabled", s.AiEnabled);
                    s.AiBaseUrl = JsonHelper.GetString(dict, "aiBaseUrl", s.AiBaseUrl);
                    s.AiModel = JsonHelper.GetString(dict, "aiModel", s.AiModel);
                    s.AiApiKey = JsonHelper.GetString(dict, "aiApiKey", s.AiApiKey);
                    s.CustomSnippetsJson = JsonHelper.GetString(dict, "customSnippetsJson", s.CustomSnippetsJson);
                    SnippetExpansionEngine.ReloadCustomSnippets(s.CustomSnippetsJson);
                    s.WebhookEnabled = JsonHelper.GetBool(dict, "webhookEnabled", s.WebhookEnabled);
                    s.WebhookPort = JsonHelper.GetInt(dict, "webhookPort", s.WebhookPort);
                    if (s.WebhookPort <= 0) s.WebhookPort = 43989;
	                    s.WebhookToken = JsonHelper.GetString(dict, "webhookToken", s.WebhookToken);
		                    s.DropZoneEnabled = JsonHelper.GetBool(dict, "dropZoneEnabled", s.DropZoneEnabled);
		                    s.DoubleCtrlActivationEnabled = JsonHelper.GetBool(dict, "doubleCtrlActivationEnabled", s.DoubleCtrlActivationEnabled);
                }
                else
                {
                    s.Save();
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(s.WebhookToken)) s.WebhookEnabled = false;
            return s;
        }

        public bool Save()
        {
            bool isolated = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIYUAN_URL"));
            bool hasIsolatedDataDir = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XIAOLV_DATA_DIR"));
            if (isolated && !hasIsolatedDataDir) return false;

            string tempPath = null;
            try
            {
                var json = JsonHelper.ToJson(this);
                var targetPath = SettingsFilePath;
                var directory = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(targetPath)) File.Replace(tempPath, targetPath, null);
                else File.Move(tempPath, targetPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Saving application settings failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath))
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
        }
    }
}
