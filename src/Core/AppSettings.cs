using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class AppSettings
    {
        private static AppSettings _instance;
        public static AppSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Load();
                }
                return _instance;
            }
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
        public int QuickNoteContinuationSec { get; set; }
        public string QuickNoteTargetDoc { get; set; }
        public bool AutoFloatEnabled { get; set; }
        public bool SmartClipboardEnabled { get; set; }
        public bool AutoStart { get; set; }
        public bool SoundFeedbackEnabled { get; set; }
        public string PinnedDocsJson { get; set; }
        public string DefaultTheme { get; set; }
        public double PanelWidth { get; set; }
        public double PanelHeight { get; set; }
        public bool LifelogAutoSectionEnabled { get; set; }
        public string WebClipDefaultMode { get; set; }
        public string WebClipTargetFolder { get; set; }
        public string WebClipCustomTemplate { get; set; }

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
            QuickNoteContinuationSec = 30;
            QuickNoteTargetDoc = "";
            AutoFloatEnabled = false;
            SmartClipboardEnabled = true;
            AutoStart = false;
            SoundFeedbackEnabled = true;
            PinnedDocsJson = "[]";
            DefaultTheme = "dark";
            PanelWidth = 1180;
            PanelHeight = 740;
            LifelogAutoSectionEnabled = true;
            WebClipDefaultMode = "card";
            WebClipTargetFolder = "/读库/";
            WebClipCustomTemplate = "";
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
                    s.QuickNoteContinuationSec = JsonHelper.GetInt(dict, "quickNoteContinuationSec", s.QuickNoteContinuationSec);
                    s.QuickNoteTargetDoc = JsonHelper.GetString(dict, "quickNoteTargetDoc", s.QuickNoteTargetDoc);
                    s.DefaultTheme = JsonHelper.GetString(dict, "defaultTheme", s.DefaultTheme);
                    s.AutoFloatEnabled = JsonHelper.GetBool(dict, "autoFloatEnabled", s.AutoFloatEnabled);
                    s.SmartClipboardEnabled = JsonHelper.GetBool(dict, "smartClipboardEnabled", s.SmartClipboardEnabled);
                    s.AutoStart = JsonHelper.GetBool(dict, "autoStart", s.AutoStart);
                    s.PanelWidth = JsonHelper.GetDouble(dict, "panelWidth", s.PanelWidth);
                    s.PanelHeight = JsonHelper.GetDouble(dict, "panelHeight", s.PanelHeight);
                    s.LifelogAutoSectionEnabled = JsonHelper.GetBool(dict, "lifelogAutoSectionEnabled", s.LifelogAutoSectionEnabled);
                    s.WebClipDefaultMode = JsonHelper.GetString(dict, "webClipDefaultMode", s.WebClipDefaultMode);
                    s.WebClipTargetFolder = JsonHelper.GetString(dict, "webClipTargetFolder", s.WebClipTargetFolder);
                    s.WebClipCustomTemplate = JsonHelper.GetString(dict, "webClipCustomTemplate", s.WebClipCustomTemplate);
                }
                else
                {
                    s.Save();
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var json = JsonHelper.ToJson(this);
                File.WriteAllText(SettingsFilePath, json, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
