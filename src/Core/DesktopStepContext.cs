using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Quicker.Public
{
    public interface IStepContext
    {
        object GetVarValue(string key);
        void SetVarValue(string key, object value);
    }
}

namespace XiaoLv.Desktop.Core
{
    public class DesktopStepContext : Quicker.Public.IStepContext
    {
        private readonly ConcurrentDictionary<string, object> _vars = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public string ActionId { get; set; }

        public event Action<string, string> OnNotificationRequested;
        public event Action<string> OnOpenUrlRequested;
        public event Action<string, string> OnLaunchSubActionRequested;
        public event Action<string, bool> OnDialogRequested;

        public DesktopStepContext()
        {
            ActionId = "xiaolv_desktop_master";
        }

        public DesktopStepContext(string inParam) : this(inParam, "") { }

        public DesktopStepContext(string inParam, string selectedText)
        {
            ActionId = "xiaolv_desktop_master";
            if (!string.IsNullOrEmpty(inParam))
            {
                _vars["quicker_in_param"] = inParam;
                _vars["指令码"] = inParam;
            }
            if (!string.IsNullOrEmpty(selectedText))
            {
                _vars["选中文本"] = selectedText;
                _vars["输入文本"] = selectedText;
            }
        }

        public object GetVarValue(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            object val;
            if (_vars.TryGetValue(key, out val)) return val;
            return null;
        }

        public void SetVarValue(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _vars[key] = value;

            // 监听关键副作用变量并触发宿主调度
            if ((key == "需要通知" || key == "通知反馈") && GetBool("需要通知"))
            {
                var msg = GetString("通知反馈", GetString("消息"));
                if (!string.IsNullOrEmpty(msg))
                {
                    var title = GetString("通知标题", "思源外骨骼");
                    if (OnNotificationRequested != null) OnNotificationRequested(title, msg);
                }
            }
            else if (key == "需要打开链接" && value is bool && (bool)value)
            {
                var url = GetString("目标链接");
                if (!string.IsNullOrEmpty(url) && OnOpenUrlRequested != null) OnOpenUrlRequested(url);
            }
            else if (key == "需要调起动作" && value is bool && (bool)value)
            {
                var actId = GetString("目标动作ID");
                var inputTxt = GetString("输入文本");
                if (OnLaunchSubActionRequested != null) OnLaunchSubActionRequested(actId, inputTxt);
            }
            else if ((key.StartsWith("需要弹窗选择") || key.StartsWith("需要弹窗输入")) && value is bool && (bool)value)
            {
                var url = GetString("dialog_url");
                if (string.IsNullOrEmpty(url))
                {
                    string menuText = GetString("常用项列表");
                    if (string.IsNullOrEmpty(menuText)) menuText = GetString("待分拣块列表");
                    if (string.IsNullOrEmpty(menuText)) menuText = GetString("编排菜单");
                    if (string.IsNullOrEmpty(menuText)) menuText = GetString("调色盘菜单文本");

                    if (!string.IsNullOrEmpty(menuText))
                    {
                        url = SynthesizeMenuDialogUrl(menuText);
                    }
                }

                if (!string.IsNullOrEmpty(url) && OnDialogRequested != null)
                {
                    OnDialogRequested(url, key.Contains("输入"));
                }
            }
        }

        private string SynthesizeMenuDialogUrl(string menuText)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"title\":\"思源外骨骼 · 快捷交互选择\",")
                  .Append("\"subtitle\":\"请选择一项执行\",")
                  .Append("\"mode\":\"select\",")
                  .Append("\"outputVar\":\"选中指令\",")
                  .Append("\"placeholder\":\"拼音或关键词过滤...\",")
                  .Append("\"items\":[");

                var lines = menuText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int count = 0;
                foreach (var line in lines)
                {
                    var l = line.Trim();
                    if (l.StartsWith("---") || string.IsNullOrEmpty(l)) continue;
                    var parts = l.Split('|');
                    var title = parts[0].Trim();
                    var val = parts.Length > 1 ? parts[1].Trim() : title;

                    if (count > 0) sb.Append(",");
                    sb.Append("{\"id\":\"").Append(SafeContext.EscapeJson(val)).Append("\",")
                      .Append("\"title\":\"").Append(SafeContext.EscapeJson(title)).Append("\",")
                      .Append("\"value\":\"").Append(SafeContext.EscapeJson(val)).Append("\"}");
                    count++;
                }
                sb.Append("]}");
                return SafeContext.BuildDialogUrl(sb.ToString());
            }
            catch
            {
                return "";
            }
        }

        public bool IsVarExists(string key)
        {
            return _vars.ContainsKey(key);
        }

        public string GetString(string key, string fallback)
        {
            var val = GetVarValue(key);
            if (val == null) return fallback;
            return val.ToString();
        }

        public string GetString(string key)
        {
            return GetString(key, "");
        }

        public bool GetBool(string key, bool fallback)
        {
            var val = GetVarValue(key);
            if (val == null) return fallback;
            if (val is bool) return (bool)val;
            bool parsed;
            if (bool.TryParse(val.ToString(), out parsed)) return parsed;
            return fallback;
        }

        public bool GetBool(string key)
        {
            return GetBool(key, false);
        }
    }
}
