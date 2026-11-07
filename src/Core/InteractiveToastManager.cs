using System;
using System.Reflection;

namespace XiaoLv.Desktop.Core
{
    public static class InteractiveToastManager
    {
        public static bool ShowQuickCaptureToast(Action<string, string> onInputSubmitted)
        {
            try
            {
                var toastManagerType = Type.GetType("Windows.UI.Notifications.ToastNotificationManager, Windows.UI, ContentType=WindowsRuntime");
                var xmlDocType = Type.GetType("Windows.Data.Xml.Dom.XmlDocument, Windows.Data, ContentType=WindowsRuntime");
                var toastNotificationType = Type.GetType("Windows.UI.Notifications.ToastNotification, Windows.UI, ContentType=WindowsRuntime");

                if (toastManagerType == null || xmlDocType == null || toastNotificationType == null) return false;

                string xmlTemplate = @"
<toast scenario=""reminder"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>思源外骨骼 · 极速灵感速记</text>
      <text>输入任何闪念或待办，敲击回车后台静默归档入思源</text>
    </binding>
  </visual>
  <actions>
    <input id=""quickNoteText"" type=""text"" placeHolderContent=""例如: 明天下午2点和老张开会 !待办..."" />
    <action content=""📝 入日记"" arguments=""action=diary"" activationType=""background"" />
    <action content=""🧠 制闪卡"" arguments=""action=card"" activationType=""background"" />
  </actions>
</toast>";

                var xml = Activator.CreateInstance(xmlDocType);
                var loadXmlMethod = xmlDocType.GetMethod("LoadXml", new Type[] { typeof(string) });
                loadXmlMethod.Invoke(xml, new object[] { xmlTemplate.Trim() });

                var toast = Activator.CreateInstance(toastNotificationType, new object[] { xml });

                // 绑定 Activated 事件监听输入提交
                var activatedEvent = toastNotificationType.GetEvent("Activated");
                if (activatedEvent != null && onInputSubmitted != null)
                {
                    EventHandler<object> handler = (sender, args) =>
                    {
                        try
                        {
                            var argsType = args.GetType();
                            var userInputProp = argsType.GetProperty("UserInput");
                            var argumentsProp = argsType.GetProperty("Arguments");

                            string actionArg = argumentsProp != null ? (string)argumentsProp.GetValue(args, null) : "";
                            string inputText = "";

                            if (userInputProp != null)
                            {
                                dynamic userInputDict = userInputProp.GetValue(args, null);
                                if (userInputDict != null && userInputDict.ContainsKey("quickNoteText"))
                                {
                                    inputText = (string)userInputDict["quickNoteText"];
                                }
                            }

                            if (!string.IsNullOrEmpty(inputText))
                            {
                                onInputSubmitted(inputText.Trim(), actionArg);
                            }
                        }
                        catch { }
                    };

                    activatedEvent.AddEventHandler(toast, handler);
                }

                var createNotifierMethod = toastManagerType.GetMethod("CreateToastNotifier", new Type[] { typeof(string) });
                var notifier = createNotifierMethod.Invoke(null, new object[] { "XiaoLvDesktop" });

                var showMethod = notifier.GetType().GetMethod("Show", new Type[] { toastNotificationType });
                showMethod.Invoke(notifier, new object[] { toast });

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
