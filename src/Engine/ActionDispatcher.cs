using Quicker.Public;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System;
using XiaoLv.Desktop.Core;

public class EntryPoint
{
    private static string GetCurrentActionId(IStepContext context)
    {
        try
        {
            var prop = context.GetType().GetProperty("ActionId");
            if (prop != null)
            {
                var val = prop.GetValue(context, null) as string;
                if (!string.IsNullOrEmpty(val)) return val;
            }
        }
        catch { }
        return "ed70abf5-45ab-430c-9e72-1f77945389da";
    }

    public static void Exec(IStepContext context)
    {
        SafeContext.Set(context, "自身动作ID", GetCurrentActionId(context));

        // 1. 优先检查外部是否传入了指令或动作代码（支持快捷键/参数化盲打）
        var inParam = SafeContext.GetString(context, "quicker_in_param");
        if (!string.IsNullOrEmpty(inParam))
        {
            var p = inParam.Trim();
            if (p.StartsWith("sub:", StringComparison.OrdinalIgnoreCase)) p = p.Substring(4).Trim();
            if (ActionDispatcher.Dispatch(p, context))
            {
                SafeContext.Set(context, "跳过UI", true);
                return;
            }
        }

        // 2. 检查面板发出的内部调度指令（用户在集市或卡片上点击直跑）
        var nextAct = SafeContext.GetString(context, "后续动作");
        if (!string.IsNullOrEmpty(nextAct))
        {
            if (nextAct.StartsWith("sub:", StringComparison.OrdinalIgnoreCase))
            {
                var subCode = nextAct.Substring(4).Trim();
                SafeContext.Set(context, "后续动作", "");
                SafeContext.Set(context, "跳过UI", true);
                if (ActionDispatcher.Dispatch(subCode, context)) return;
            }
            if (nextAct.StartsWith("dispatch:", StringComparison.OrdinalIgnoreCase))
            {
                var subCode = nextAct.Substring(9).Trim();
                SafeContext.Set(context, "后续动作", "");
                SafeContext.Set(context, "跳过UI", true);
                if (ActionDispatcher.Dispatch(subCode, context)) return;
            }
        }

        // 3. 默认运行超级面板主程序
        SuperPanelMain.Exec(context);
    }
}


public static class ActionDispatcher
{
    public static bool Dispatch(string target, IStepContext context)
    {
        if (string.IsNullOrEmpty(target)) return false;
        var t = target.Trim().ToUpperInvariant();
        switch (t)
        {
            case "L0-01":
            case "SYQ":
            case "PANEL":
                SuperPanelMain.Exec(context); return true;
            case "L0-02":
            case "SYCHECK":
                PreflightMain.Exec(context); return true;
            case "L0-03":
            case "SYF":
                OmniSearchMain.Exec(context); return true;
            case "L0-04":
            case "SYO":
                DocOutlineNavMain.Exec(context); return true;
            case "L0-05":
            case "SYWR":
                WeReadShelfShuttleMain.Exec(context); return true;
            case "L0-07":
            case "SYRAI":
                SpeedSwitchLauncherMain.Exec(context); return true;
            case "L0-08":
            case "SYDATE":
                CalendarShuttleMain.Exec(context); return true;
            case "SYD":
            case "D":
            case "SYZG":
            case "ZG":
            case "SYTB":
            case "TB":
            case "SYSZ":
            case "SZ":
            case "SYQD":
            case "QD":
            {
                string syUrl = "";
                string token = "";
                SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
                return SuperPanelMain.TryHandleBuiltinDirectCmd(context, syUrl, token, t.ToLowerInvariant());
            }
            case "SYC":
                QuickCommentMain.Exec(context); return true;
            case "SYT":
                MediaTopicCaptureMain.Exec(context); return true;
            case "SYIMG":
                XiaoLv.Desktop.Program.StartSnippingAndOcr(); return true;
            case "SYDB":
                RollingTasksMain.Exec(context); return true;
            case "SYR":
                WeReadShelfShuttleMain.Exec(context); return true;
            case "SYTRIAGE":
                InboxTriageMain.Exec(context); return true;
            case "SYSG_PLUGIN":
                GleanTriageMain.Exec(context); return true;
            case "SYFK":
            case "SYFSRS":
            case "FSRS":
                XiaoLv.Desktop.Program.StartFsrsReview(); return true;
            case "SYBK":
            {
                var bkText = SafeContext.GetString(context, "选中文本");
                if (string.IsNullOrEmpty(bkText)) bkText = SafeContext.GetString(context, "输入文本");
                if (!string.IsNullOrEmpty(bkText))
                {
                    ExamWrongbookMain.Exec(context);
                }
                else
                {
                    ExamDashboardMain.Exec(context);
                }
                return true;
            }
            case "SYLQ":
                SpeedSwitchLauncherMain.Exec(context); return true;
            case "SYYJ":
                PreflightMain.Exec(context); return true;
            case "L1-00":
            case "SYPEN":
                SafeContext.Set(context, "需要调起动作", true);
                SafeContext.Set(context, "目标动作ID", "af91cd6a-ce44-4bbd-942b-e04a0783d8d5");
                SafeContext.Set(context, "跳过UI", true);
                return true;
            case "L1-01":
            case "SYS":
                SmartCaptureMain.Exec(context); return true;
            case "L1-02":
            case "SYRC":
                ResearchSourceCardMain.Exec(context); return true;
            case "L1-03":
            case "SYOCR":
            case "OCR":
                XiaoLv.Desktop.Program.StartSnippingAndOcr(); return true;
            case "L1-04":
            case "SYOFF":
                OfficeIngestionMain.Exec(context); return true;
            case "L1-05":
            case "SYX":
                CleanMarkdownExportMain.Exec(context); return true;
            case "L1-06":
            case "SYAV":
                WebToAvDatabaseMain.Exec(context); return true;
            case "L1-07":
            case "SYNOTE":
                QuickCommentMain.Exec(context); return true;
            case "L1-08":
            case "SYMED":
                MediaTopicCaptureMain.Exec(context); return true;
            case "L1-09":
            case "SYTH":
                TaskHorizonDispatchMain.Exec(context); return true;
            case "L1-10":
            case "SYPIC":
                PicGoUploadInsertMain.Exec(context); return true;
            case "L1-11":
            case "SYLU":
                LuminaThoughtMain.Exec(context); return true;
            case "L2-01":
                XiaoLv.Desktop.Program.StartFsrsReview(); return true;
            case "L2-02":
            case "SYWRONG":
                ExamWrongbookMain.Exec(context); return true;
            case "L2-03":
            case "SYWIKI":
                AutoWikiLinkerMain.Exec(context); return true;
            case "L2-04":
            case "SYMM":
            case "SYM":
                DocToMindmapMain.Exec(context); return true;
            case "L2-05":
            case "SYEXAM":
                ExamDashboardMain.Exec(context); return true;
            case "L2-06":
            case "SYCARD":
                FlashcardEntryMain.Exec(context); return true;
            case "L2-07":
            case "SYTAG":
                TagGovernanceMain.Exec(context); return true;
            case "L2-08":
            case "SYREAD":
                SiReaderExcerptCardMain.Exec(context); return true;
            case "L2-09":
            case "SYMMSEL":
                SelectionToMindmapMain.Exec(context); return true;
            case "L3-01":
            case "SYTODO":
                RollingTasksMain.Exec(context); return true;
            case "L3-02":
            case "SYIB":
                InboxTriageMain.Exec(context); return true;
            case "L3-03":
            case "SYLIB":
            case "SYH":
                LibrarianHealthMain.Exec(context); return true;
            case "L3-04":
            case "SYSG":
            case "SYG":
                XiaoLv.Desktop.Program.StartWebClip(); return true;
            case "L3-05":
            case "SYREP":
                WeeklyDigestMain.Exec(context); return true;
            case "L3-06":
            case "SYCM":
                CommentReviewTriageMain.Exec(context); return true;
            case "L3-07":
            case "SYTHK":
                TaskHorizonDashboardMain.Exec(context); return true;
            case "L4-01":
            case "SYV":
                CommonInserterMain.Exec(context); return true;
            case "L4-02":
            case "SYMTG":
                MeetingContactsMain.Exec(context); return true;
            case "L4-03":
            case "SYPOMO":
                TomatoFocusMain.Exec(context); return true;
            case "L4-04":
            case "SYFMT":
            case "SYP":
                TextFormatterMain.Exec(context); return true;
            case "L4-05":
            case "SYDA":
                CheckinPanelMain.Exec(context); return true;
            case "L4-06":
            case "SYCAL":
                CalendarScheduleMain.Exec(context); return true;
            case "L4-07":
            case "SYZJ":
                CursorInsertMain.Exec(context); return true;
            case "L4-08":
            case "SYRM":
                ContactsLookupMain.Exec(context); return true;
            case "L4-09":
            case "SYHOME":
                HomeButlerMain.Exec(context); return true;
            case "L4-10":
            case "SYMP":
                SiMediaTimestampMain.Exec(context); return true;
            case "L4-11":
            case "SYSNP":
                CodeSnippetsInserterMain.Exec(context); return true;
            case "L5-01":
            case "SYWF":
                WorkflowOrchestratorMain.Exec(context); return true;
            case "L5-02":
            case "SYAI":
                ActionAuthorStudioMain.Exec(context); return true;
            case "L5-03":
            case "SYCU":
                ComputerUseAuditMain.Exec(context); return true;
            case "L5-04":
            case "SYEVT":
                EventTimelineMain.Exec(context); return true;
            case "SYVOICE":
            case "VOICE":
                XiaoLv.Desktop.Core.VoiceCaptureManager.TriggerWindowsVoiceTyping(); return true;
            case "SYATT":
            case "ATT":
                XiaoLv.Desktop.Program.ShowMasterPanel("att:"); return true;
            case "SYWS":
            case "WS":
                XiaoLv.Desktop.Program.ShowMasterPanel("ws:"); return true;
            case "SYTRASH":
            case "TRASH":
                XiaoLv.Desktop.Program.ShowMasterPanel("trash:"); return true;
            case "SYCMD":
                XiaoLv.Desktop.Program.ShowMasterPanel("p:"); return true;
            default:
                return false;
        }
    }
}

// ============================================================================
// SY·内核请求 —— 所有思源内核 API 调用的唯一底层入口 (高性能缓存与自愈优化版)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心优化项：
//   1. 进程内静态环境缓存（TTL 60s）：高频调用免重复读盘与反复端口探测，耗时降低至 <10ms
//   2. 401/403 令牌失效自动使缓存失效，强制重读并自动重试一次
//   3. 网络参数调优：强制 Proxy=null（防本地环回绕路代理）、KeepAlive 复用、超时有界控制
//   4. 结构化错误代码与用户可解释指引（未启动、鉴权失败、端点不存在、超时）
// ============================================================================


// ============================================================================
// SafeContext: 安全上下文访问门控助手，杜绝 Quicker 运行时 "变量不存在" 异常
// ============================================================================