using System;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// 参数化写入桥 (P1-1 对标 Obsidian Advanced URI)：
    /// siyuan-exoskeleton://write?... 深链与 REST POST /api/write 双入口共用。
    /// 参数：target=daily|doc · heading=日记小节 · doc=目标文档名 · action=task|flash · data=内容(URL编码)
    /// 统一复用 SYS 智能分流管线 (SmartCaptureMain)——与速记框/executeCommand 完全同源，
    /// 天然支持 &gt;&gt;小节路由、&gt;@跨文档直投、- [ ] 待办、? 闪卡与 ¥/~/+ Lifelog 分流。
    /// </summary>
    public static class ParameterizedWriteBridge
    {
        /// <summary>
        /// 执行参数化写入，返回 "OK" 或错误说明。
        /// </summary>
        public static string ExecuteFromParams(string target, string heading, string doc, string action, string data, Action<string, string> notify)
        {
            if (string.IsNullOrWhiteSpace(data)) return "缺少 data 参数";

            string payload = data.Trim();
            string a = (action ?? "").Trim().ToLowerInvariant();
            if (a == "task" || a == "todo")
            {
                payload = "- [ ] " + payload;
            }
            else if (a == "flash" || a == "card")
            {
                payload = "?" + payload;
            }

            if (!string.IsNullOrWhiteSpace(doc))
            {
                payload = ">@" + doc.Trim() + " " + payload;
            }
            else if (!string.IsNullOrWhiteSpace(heading))
            {
                payload = ">>" + heading.Trim() + " " + payload;
            }

            try
            {
                var ctx = new DesktopStepContext("SYS", payload);
                if (notify != null) ctx.OnNotificationRequested += (t, m) => { try { notify(t, m); } catch { } };
                SmartCaptureMain.Exec(ctx);
                bool ok = SafeContext.GetBool(ctx, "是否成功", true);
                string feedback = SafeContext.GetString(ctx, "通知反馈", "");
                if (ok)
                {
                    // P1-1 收尾：回传写入块 ID，REST/深链/MCP 三通道都能给出 siyuan:// 跳转
                    string writtenBlockId = SafeContext.GetString(ctx, "写入块ID", "");
                    return string.IsNullOrEmpty(writtenBlockId) ? "OK" : ("OK|" + writtenBlockId);
                }
                return string.IsNullOrEmpty(feedback) ? "分流管线未成功 (未写库)" : "失败: " + feedback;
            }
            catch (Exception ex)
            {
                Logger.Error("ParameterizedWriteBridge execute failed", ex);
                return "执行异常: " + ex.Message;
            }
        }
    }
}
