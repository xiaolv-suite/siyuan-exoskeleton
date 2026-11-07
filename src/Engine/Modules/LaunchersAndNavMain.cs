using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Quicker.Public;
using XiaoLv.Desktop.Core;
public static class PreflightMain
{
    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var autoLaunch = SafeContext.GetString(context, "未运行时自动启动", "true").Trim().ToLower() == "true";

        // 1. 调用底层预检子程序
        var res = SyPreflight.Check(syUrl, token, true, "siyuan-checkin,siyuan-contacts,xiaolv-common", true);

        var allPassed = res.AllPass;
        var report = res.Report;
        var failures = res.Failures;
        var version = res.DetectedVersion;
        var activeUrl = res.SyUrl;
        var activeToken = res.Token;

        // 2. 自愈分支：若检测到内核不可达，尝试自动启动思源并轮询等待内核就绪
        if (failures.Contains("内核不可达") && autoLaunch)
        {
            var launched = TryLaunchSiYuan();
            if (launched)
            {
                report += "\n> 🚀 **自愈动作**: 已尝试在后台启动思源笔记客户端，正在等待内核就绪...\n";
                if (WaitKernelReady(activeUrl, 15000))
                {
                    report += "> ✅ 内核已在自愈等待窗口内恢复响应。\n";
                }
                else
                {
                    report += "> ⏳ 内核在 15 秒等待窗口内仍未就绪（冷启动可能较慢），请稍后手动重新运行本动作确认。\n";
                }
                // 无论就绪与否都复检一次，输出最新真实状态
                res = SyPreflight.Check(activeUrl, activeToken, true, "siyuan-checkin,siyuan-contacts,xiaolv-common", true);
                allPassed = res.AllPass;
                report += "\n---\n\n" + res.Report;
                failures = res.Failures;
                version = res.DetectedVersion;
                activeUrl = res.SyUrl;
                activeToken = res.Token;
            }
            else
            {
                report += "\n> ⚠️ **自愈失败**: 未在常见安装路径找到 SiYuan.exe，请手动启动思源笔记。\n";
            }
        }

        // 3. 输出变量写回
        SafeContext.Set(context, "全部通过", allPassed);
        SafeContext.Set(context, "体检报告", report);
        SafeContext.Set(context, "失败项", failures);
        SafeContext.Set(context, "思源版本", version);
        SafeContext.Set(context, "sy_url", activeUrl);
        SafeContext.Set(context, "sy_token", activeToken);
        SafeContext.Set(context, "是否成功", allPassed);
        SafeContext.Set(context, "需要通知", true);

        SafeContext.Set(context, "通知反馈", allPassed 
            ? "✓ 思源外骨骼运行环境预检全通！内核、Token、雷切服务桥与本地 IPC 均正常在线。" 
            : "⚠️ 预检发现待处理项: " + failures);
    }

    /// <summary>
    /// 在指定时间窗内轮询等待内核 /api/system/version 恢复响应
    /// </summary>
    private static bool WaitKernelReady(string baseUrl, int maxMs)
    {
        var deadline = Environment.TickCount + maxMs;
        while (Environment.TickCount < deadline)
        {
            System.Threading.Thread.Sleep(1500);
            var probe = SyKernelRequest.DoRequest(baseUrl, "", "/api/system/version", "{}");
            if (probe.Success) return true;
        }
        return false;
    }

    /// <summary>
    /// 自动寻找本地安装路径并拉起思源笔记
    /// </summary>
    private static bool TryLaunchSiYuan()
    {
        string[] candidates = new string[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "siyuan", "SiYuan.exe")
        };

        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                try
                {
                    Process.Start(p);
                    return true;
                }
                catch { }
            }
        }
        return false;
    }
}


// ============================================================================
// L0-03: 混合语义与 SQL 快查 —— 主驱动程序 (OmniSearchMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 优先读取划选文字/剪贴板，为空时弹出高保真 Bento 弹窗交互输入
//   2. 三级降级检索链：
//      - 优先模式: /api/search/fullTextSearchBlock 原生全文高精检索
//      - 自动降级: 零命中时自动回退 SQL `content LIKE '%关键词%'` 模糊匹配
//      - 专家模式: 输入以 `sql:` 开头时，激活只读 SQL 执行引擎（带安全防注入门控）
//   3. 高保真 Bento 候选网格（实时拼音过滤，1~9快捷选），回车 3 层直达定位
//   4. 3层高可靠定位跳转：快门网关 doc.open + Win32 唤醒置顶 + siyuan:// 协议
// ============================================================================


public static class OmniSearchMain
{
    public static void Exec(IStepContext context)
    {
        var keyword = SafeContext.GetString(context, "关键词");
        var selectedBlockId = SafeContext.GetString(context, "选中块ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 阶段二：用户已在 Bento 候选列表中选中了目标块 ID -> 3层唤起定位
        if (!string.IsNullOrEmpty(selectedBlockId))
        {
            NavigateToBlock(context, syUrl, token, selectedBlockId);
            return;
        }

        // 阶段一：关键词嗅探（输入框 -> 外部输入参数 -> 选中文本 -> 剪贴板文本）
        if (string.IsNullOrEmpty(keyword))
        {
            keyword = SafeContext.GetInputText(context);
        }

        if (string.IsNullOrEmpty(keyword))
        {
            var json = "{\"title\":\"思源外骨骼 · 双轨高精全局快查\",\"subtitle\":\"普通词走全文与模糊检索，sql: 前缀走安全只读 SQL 直跑\",\"badge\":\"双轨快查\",\"icon\":\"🔍\",\"mode\":\"input\",\"outputVar\":\"关键词\",\"placeholder\":\"输入关键词，或 sql:SELECT... 专家模式\",\"defaultValue\":\"\",\"tags\":[\"sql:SELECT id, content FROM blocks LIMIT 10\",\"sql:\",\"今日日记\",\"待办\"]}";
            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // -------------------------------------------------------------
        // 模式 A: 专家模式 —— `sql:` / `sql：` (兼容全角中文冒号) 强安全检查直跑
        // -------------------------------------------------------------
        if (keyword.StartsWith("sql:", StringComparison.OrdinalIgnoreCase) || keyword.StartsWith("sql：", StringComparison.OrdinalIgnoreCase))
        {
            var rawSql = keyword.Substring(4).Trim();
            var guardErr = ValidateSqlSecurity(rawSql);
            if (!string.IsNullOrEmpty(guardErr))
            {
                SafeContext.Set(context, "是否成功", false);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "❌ SQL 安全拦截：" + guardErr + "（仅允许只读 SELECT 语句）");
                return;
            }

            var sqlReq = "{\"stmt\":\"" + SafeContext.EscapeJson(rawSql) + "\"}";
            var sqlRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sqlReq);
            EmitCandidateList(context, sqlRes.Text, "SQL 查询完成");
            return;
        }

        // -------------------------------------------------------------
        // 模式 B: 全文搜索主路径 (/api/search/fullTextSearchBlock)
        // -------------------------------------------------------------
        var searchBody = "{\"query\":\"" + SafeContext.EscapeJson(keyword) + "\",\"method\":0,\"orderBy\":0,\"types\":{\"paragraph\":true,\"heading\":true,\"list\":true,\"document\":true},\"page\":1}";
        var fr = SyKernelRequest.DoRequest(syUrl, token, "/api/search/fullTextSearchBlock", searchBody);
        
        var blocks = ExtractSearchResultBlocks(fr.Text);
        if (blocks.Count > 0)
        {
            FormatOutputCandidates(context, blocks, "✓ 全文检索命中 " + blocks.Count + " 条");
            return;
        }

        // -------------------------------------------------------------
        // 模式 C: 自动降级至 SQL LIKE 模糊匹配
        // -------------------------------------------------------------
        var safeLike = keyword.Replace("'", "''");
        var fallbackSql = "SELECT id, content FROM blocks WHERE content LIKE '%" + safeLike + "%' AND type IN ('p','h','d') ORDER BY updated DESC LIMIT 15";
        var fbRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(fallbackSql) + "\"}");

        var fbBlocks = ExtractSqlBlocks(fbRes.Text);
        if (fbBlocks.Count > 0)
        {
            FormatOutputCandidates(context, fbBlocks, "✓ 模糊匹配命中 " + fbBlocks.Count + " 条");
        }
        else
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "ℹ️ 未找到与「" + keyword + "」相关的笔记内容。");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void NavigateToBlock(IStepContext context, string syUrl, string token, string blockId)
    {
        try { LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(blockId) + "\"}", "siyuan-speed-switch", "fast"); } catch { }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("siyuan://blocks/" + blockId) { UseShellExecute = true }); } catch { }

        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }

        SafeContext.Set(context, "目标链接", "siyuan://blocks/" + blockId);
        SafeContext.Set(context, "需要打开链接", true);
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "✓ 已定位并居中高亮跳转至目标块");
    }

    private static string ValidateSqlSecurity(string sql)
    {
        var lower = sql.ToLower().Trim();
        if (!lower.StartsWith("select") && !lower.StartsWith("explain"))
        {
            return "只支持执行 SELECT 或 EXPLAIN 查询";
        }

        string[] dangerWords = new string[] { "delete", "update", "insert", "drop", "alter", "attach", "detach", "pragma", "vacuum" };
        foreach (var dw in dangerWords)
        {
            if (Regex.IsMatch(lower, "\\b" + dw + "\\b"))
            {
                return "禁止使用高危关键字: " + dw.ToUpper();
            }
        }

        return "";
    }

    private static void EmitCandidateList(IStepContext context, string json, string msg)
    {
        var blocks = ExtractSqlBlocks(json);
        FormatOutputCandidates(context, blocks, msg);
    }

    private static void FormatOutputCandidates(IStepContext context, List<SearchBlockItem> items, string msg)
    {
        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 快查结果\",")
                   .Append("\"subtitle\":\"").Append(SafeContext.EscapeJson(msg)).Append("\",")
                   .Append("\"badge\":\"双轨检索\",")
                   .Append("\"icon\":\"🔍\",")
                   .Append("\"mode\":\"select\",")
                   .Append("\"outputVar\":\"选中块ID\",")
                   .Append("\"placeholder\":\"输入拼音过滤结果，回车精准定位居中高亮...\",")
                   .Append("\"items\":[");

        int numHk = 1;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var hk = numHk <= 9 ? numHk.ToString() : "";
            if (i > 0) jsonBuilder.Append(",");
            jsonBuilder.Append("{\"id\":\"").Append(SafeContext.EscapeJson(it.Id)).Append("\",")
                       .Append("\"title\":\"").Append(SafeContext.EscapeJson(CleanSummary(it.Content))).Append("\",")
                       .Append("\"desc\":\"块ID: ").Append(it.Id).Append("\",")
                       .Append("\"badge\":\"检索命中\",")
                       .Append("\"badgeCls\":\"badge-native\",")
                       .Append("\"hotkey\":\"").Append(hk).Append("\",")
                       .Append("\"icon\":\"📄\"}");
            numHk++;
        }
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "命中总数", items.Count);
        SafeContext.Set(context, "需要弹窗选择", items.Count > 0);
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", msg);
    }

    private static List<SearchBlockItem> ExtractSearchResultBlocks(string json)
    {
        var list = new List<SearchBlockItem>();
        if (string.IsNullOrEmpty(json)) return list;

        var matches = Regex.Matches(json, "\\{[^\\}]+\"id\"\\s*:\\s*\"([^\"]+)\"[^\\}]+\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"[^\\}]*\\}");
        foreach (Match m in matches)
        {
            var id = m.Groups[1].Value;
            var content = UnescapeJson(m.Groups[2].Value);
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(content))
            {
                list.Add(new SearchBlockItem { Id = id, Content = content });
                if (list.Count >= 15) break;
            }
        }
        return list;
    }

    private static List<SearchBlockItem> ExtractSqlBlocks(string json)
    {
        var list = new List<SearchBlockItem>();
        if (string.IsNullOrEmpty(json)) return list;

        var matches = Regex.Matches(json, "\\{[^\\}]+\\}");
        foreach (Match m in matches)
        {
            var b = m.Value;
            var idMatch = Regex.Match(b, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            var contentMatch = Regex.Match(b, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (idMatch.Success && contentMatch.Success)
            {
                list.Add(new SearchBlockItem { Id = idMatch.Groups[1].Value, Content = UnescapeJson(contentMatch.Groups[1].Value) });
                if (list.Count >= 15) break;
            }
        }
        return list;
    }

    private static string CleanSummary(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空白内容)";
        var clean = Regex.Replace(text, "<[^>]+>", " ");
        clean = clean.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (clean.Length > 60) clean = clean.Substring(0, 60) + "...";
        return clean;
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    public class SearchBlockItem
    {
        public string Id;
        public string Content;
    }
}


// ============================================================================
// L0-04: 当前文档大纲悬浮导航 —— 主驱动程序 (DocOutlineNavMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 实时精准嗅探思源当前前台活动文档 ID (通过 editor.context 或最近更新文档)
//   2. 调用思源内核 /api/outline/getDocOutline 提取完整的 H1-H6 目录层级树
//   3. 高保真 Bento 弹窗快速检索与拼音即搜，回车秒级平滑滚动定位
//   4. 3层高可靠定位跳转：快门网关 doc.open + Win32 唤醒置顶 + siyuan:// 协议
// ============================================================================


public static class DocOutlineNavMain
{
    public static void Exec(IStepContext context)
    {
        var targetHeadingId = SafeContext.GetString(context, "选中标题ID");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // =============================================================
        // 阶段 2: 用户已在弹窗中选择某小节 -> 立即跳转定位
        // =============================================================
        if (!string.IsNullOrEmpty(targetHeadingId))
        {
            NavigateToHeading(context, syUrl, token, targetHeadingId);
            return;
        }

        // =============================================================
        // 阶段 1: 提取大纲并生成菜单
        // =============================================================

        // 2. 嗅探前台活动文档 ID
        string activeDocId = "";
        string activeDocTitle = "当前文档";

        try
        {
            var sendRes = LvSendCommand.Send(syUrl, token, "editor.context", "{}", "siyuan-speed-switch", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 1500, "siyuan-speed-switch");
                var dataJson = rec.DataJson;
                if (!string.IsNullOrEmpty(dataJson))
                {
                    var idM = Regex.Match(dataJson, "\"docId\"\\s*:\\s*\"([^\"]+)\"");
                    var titleM = Regex.Match(dataJson, "\"rootTitle\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (idM.Success) activeDocId = idM.Groups[1].Value;
                    if (titleM.Success) activeDocTitle = UnescapeJson(titleM.Groups[1].Value);
                }
            }
        }
        catch { }

        // 兜底：若前台未聚焦，取最近更新的一篇文档
        if (string.IsNullOrEmpty(activeDocId))
        {
            var sqlRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", 
                "{\"stmt\":\"SELECT id, content FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 1\"}");
            if (sqlRes.Success)
            {
                var idM = Regex.Match(sqlRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                var titleM = Regex.Match(sqlRes.Text, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (idM.Success) activeDocId = idM.Groups[1].Value;
                if (titleM.Success) activeDocTitle = UnescapeJson(titleM.Groups[1].Value);
            }
        }

        if (string.IsNullOrEmpty(activeDocId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未检测到当前正打开的文档，请在思源中打开任意一篇笔记。");
            return;
        }

        // 3. 调用思源内核大纲接口 /api/outline/getDocOutline
        var outlineReq = "{\"id\":\"" + SafeContext.EscapeJson(activeDocId) + "\"}";
        var outlineRes = SyKernelRequest.DoRequest(syUrl, token, "/api/outline/getDocOutline", outlineReq);

        if (!outlineRes.Success)
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 提取文档大纲失败：" + outlineRes.Error);
            return;
        }

        // 4. 解析大纲项并生成高保真 Bento 弹窗状态 JSON
        var outlineItems = ParseOutline(outlineRes.Text);
        var jsonBuilder = new StringBuilder();
        jsonBuilder.Append("{\"title\":\"思源外骨骼 · 长文大纲悬浮导航\",")
                   .Append("\"subtitle\":\"【").Append(SafeContext.EscapeJson(activeDocTitle)).Append("】(共 ").Append(outlineItems.Count).Append(" 节)\",")
                   .Append("\"badge\":\"H1~H6 大纲\",")
                   .Append("\"icon\":\"📌\",")
                   .Append("\"mode\":\"select\",")
                   .Append("\"outputVar\":\"选中标题ID\",")
                   .Append("\"placeholder\":\"拼音或关键词过滤小节，回车定位...\",")
                   .Append("\"items\":[");

        if (outlineItems.Count == 0)
        {
            jsonBuilder.Append("{\"id\":\"").Append(SafeContext.EscapeJson(activeDocId)).Append("\",")
                       .Append("\"title\":\"[文档根块] ").Append(SafeContext.EscapeJson(activeDocTitle)).Append("\",")
                       .Append("\"desc\":\"当前文档暂无标题小节，回车定位至文档根节点\",")
                       .Append("\"badge\":\"根文档\",")
                       .Append("\"badgeCls\":\"badge-native\",")
                       .Append("\"hotkey\":\"Enter\",")
                       .Append("\"icon\":\"📄\"}");
        }
        else
        {
            int numHk = 1;
            for (int i = 0; i < outlineItems.Count; i++)
            {
                var item = outlineItems[i];
                var hk = numHk <= 9 ? numHk.ToString() : "";
                var indent = new string(' ', Math.Max(0, (item.Depth - 1) * 2));
                var prefix = GetDepthPrefix(item.Depth);
                if (i > 0) jsonBuilder.Append(",");
                jsonBuilder.Append("{\"id\":\"").Append(SafeContext.EscapeJson(item.Id)).Append("\",")
                           .Append("\"title\":\"").Append(SafeContext.EscapeJson(indent + prefix + " " + item.Content)).Append("\",")
                           .Append("\"desc\":\"H").Append(item.Depth).Append(" 层级小节\",")
                           .Append("\"badge\":\"H").Append(item.Depth).Append("\",")
                           .Append("\"badgeCls\":\"badge-bridge\",")
                           .Append("\"hotkey\":\"").Append(hk).Append("\",")
                           .Append("\"icon\":\"📌\"}");
                numHk++;
            }
        }
        jsonBuilder.Append("]}");

        SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(jsonBuilder.ToString()));
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "是否成功", true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void NavigateToHeading(IStepContext context, string syUrl, string token, string headingId)
    {
        // 1. 雷切网关 doc.open 与原生协议跳转
        try { LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(headingId) + "\"}", "siyuan-speed-switch", "fast"); } catch { }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("siyuan://blocks/" + headingId) { UseShellExecute = true }); } catch { }

        // 2. Win32 唤起置顶
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }

        // 3. siyuan:// 协议定位
        SafeContext.Set(context, "目标链接", "siyuan://blocks/" + headingId);
        SafeContext.Set(context, "需要打开链接", true);
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        SafeContext.Set(context, "通知反馈", "✓ 已跳转并定位至所选小节");
    }

    private static string GetDepthPrefix(int depth)
    {
        switch (depth)
        {
            case 1: return "📌";
            case 2: return "  ▫️";
            case 3: return "    ▪️";
            case 4: return "      •";
            case 5: return "        -";
            default: return "          ·";
        }
    }

    public static List<OutlineItem> ParseOutline(string json)
    {
        var list = new List<OutlineItem>();
        if (string.IsNullOrEmpty(json)) return list;

        var matches = Regex.Matches(json, "\\{[^\\{\\}]+\"id\"\\s*:\\s*\"([^\"]+)\"[^\\{\\}]*\\}");
        foreach (Match m in matches)
        {
            var b = m.Value;
            var idM = Regex.Match(b, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            var contentM = Regex.Match(b, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            var subTypeM = Regex.Match(b, "\"subType\"\\s*:\\s*\"h([1-6])\"");
            var depthM = Regex.Match(b, "\"depth\"\\s*:\\s*([0-9]+)");

            if (idM.Success && contentM.Success)
            {
                int d = 1;
                if (subTypeM.Success) int.TryParse(subTypeM.Groups[1].Value, out d);
                else if (depthM.Success) int.TryParse(depthM.Groups[1].Value, out d);

                var title = UnescapeJson(contentM.Groups[1].Value);
                title = Regex.Replace(title, "<[^>]+>", " ").Trim();
                if (string.IsNullOrEmpty(title)) title = "(无题小节)";

                list.Add(new OutlineItem { Id = idM.Groups[1].Value, Content = title, Depth = d });
            }
        }
        return list;
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    public class OutlineItem
    {
        public string Id;
        public string Content;
        public int Depth;
    }
}


// ============================================================================
// L0-05: 微信读书与电子书架直达 —— 主驱动程序 (WeReadShelfShuttleMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 渐进增强与原生平滑降级：
//      - 若安装了【SiReader】(siyuan-sireader)，一键调用 openWeread 唤起专业阅读器与划线同步
//      - 若未安装该插件，智能双轨降级：
//        ① 自动通过 SQL 定位思源中的【读书笔记/阅读书架】文档一键直达
//        ② 或自动在浏览器打开微信读书网页版 (weread.qq.com)，绝不报错中断！
//   2. 选项聚合菜单：支持微信读书阅读器、本地书架文档、网页版直达
// ============================================================================


public static class WeReadShelfShuttleMain
{
    public static void Exec(IStepContext context)
    {
        // 运行时空值防波堤：后续动作 立即初始化为空串，杜绝 yaml 条件 {后续动作}.StartsWith 在变量为 null 时的 NullReferenceException
        SafeContext.Set(context, "后续动作", "");
        var cmd = (context.GetVarValue("操作指令") as string ?? "").Trim().ToLowerInvariant();
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        // 1. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 阶段 1: 若未指定指令，弹出阅读管理快捷菜单
        if (string.IsNullOrEmpty(cmd))
        {
            var menuText = "📖 唤起微信读书阅读器 (SiReader)|weread\n" +
                           "📚 打开思源阅读书架 / 读书笔记|shelf\n" +
                           "🌐 打开微信读书网页版 (weread.qq.com)|web";
            context.SetVarValue("阅读菜单文本", menuText);
            context.SetVarValue("需要弹窗选择", true);
            return;
        }

        // 阶段 2: 根据指令分发执行
        switch (cmd)
        {
            case "weread":
                // 尝试调用 SiReader 插件 openWeread 命令
                bool siReaderOk = false;
                try
                {
                    var sendRes = LvSendCommand.Send(syUrl, token, "commands.run", "{\"plugin\":\"siyuan-sireader\",\"command\":\"openWeread\"}", "siyuan-speed-switch", "fast");
                    if (sendRes.Success)
                    {
                        var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 2500, "siyuan-speed-switch");
                        siReaderOk = (rec.Status == "recorded" || rec.Status == "duplicate");
                    }
                }
                catch { siReaderOk = false; }

                if (siReaderOk)
                {
                    context.SetVarValue("是否成功", true);
                    context.SetVarValue("通知反馈", "✓ 已成功唤起 SiReader 微信读书阅读器！");
        SafeContext.Set(context, "需要通知", true);
                }
                else
                {
                    // 未装插件时自动打开网页版微信读书
                    context.SetVarValue("后续动作", "openurl:https://weread.qq.com");
                    context.SetVarValue("是否成功", true);
                    context.SetVarValue("通知反馈", "✓ 未检测到 SiReader 插件，已为您自动打开微信读书网页版！");
        SafeContext.Set(context, "需要通知", true);
                }
                break;

            case "shelf":
                // 通过原生 SQL 查找思源中的读书笔记或书架文档
                var shelfSql = "SELECT id, content FROM blocks WHERE type='d' AND (content LIKE '%读书%' OR content LIKE '%阅读%' OR content LIKE '%书架%') ORDER BY updated DESC LIMIT 1";
                var shelfRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + EscapeJson(shelfSql) + "\"}");

                string targetDocId = "";
                string docTitle = "读书笔记";

                if (shelfRes.Success)
                {
                    var idM = Regex.Match(shelfRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    var nameM = Regex.Match(shelfRes.Text, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (idM.Success) targetDocId = idM.Groups[1].Value;
                    if (nameM.Success) docTitle = UnescapeJson(nameM.Groups[1].Value);
                }

                if (!string.IsNullOrEmpty(targetDocId))
                {
                    context.SetVarValue("后续动作", "openurl:siyuan://blocks/" + targetDocId);
                    context.SetVarValue("是否成功", true);
                    context.SetVarValue("通知反馈", "✓ 已定位并打开【" + docTitle + "】！");
        SafeContext.Set(context, "需要通知", true);
                }
                else
                {
                    context.SetVarValue("后续动作", "openurl:siyuan://");
                    context.SetVarValue("是否成功", true);
                    context.SetVarValue("通知反馈", "ℹ️ 未在库中找到专用的「读书/书架」文档，已激活思源主窗口。");
        SafeContext.Set(context, "需要通知", true);
                }
                break;

            case "web":
                context.SetVarValue("后续动作", "openurl:https://weread.qq.com");
                context.SetVarValue("是否成功", true);
                context.SetVarValue("通知反馈", "✓ 正在打开微信读书网页版...");
        SafeContext.Set(context, "需要通知", true);
                break;

            default:
                context.SetVarValue("是否成功", false);
                context.SetVarValue("通知反馈", "❌ 未知指令: " + cmd);
        SafeContext.Set(context, "需要通知", true);
                break;
        }
    }

    private static string EscapeJson(string s)
    {
        return SafeContext.EscapeJson(s);
    }

    private static string UnescapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }
}


// ============================================================================
// L0-07: 雷切快速切换直达 —— 主驱动程序 (SpeedSwitchLauncherMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 数据契约：小驴快门 commands.list / commands.run（小驴雷切 siyuan-speed-switch 命令面穿透）
// 定位差异化：雷切管「思源内」极速切换（文档/面板跳转），本动作是把雷切面板变成
//             桌面级一键直达——无需先进思源再找侧栏图标。
// 核心逻辑：
//   1. 列出小驴雷切注册的命令清单，弹窗选择执行（30s 确认门控）
//   2. 直达模式：选中命令 变量直接给 langKey 时跳过菜单
//   3. 兜底：插件未装或桥未就绪时如实指引
// ============================================================================


public static class SpeedSwitchLauncherMain
{
    private const string TargetPlugin = "siyuan-speed-switch";

    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var picked = SafeContext.GetString(context, "选中命令");
        if (!string.IsNullOrEmpty(picked))
        {
            RunCommand(context, syUrl, token, picked);
            return;
        }

        // 核心快速路径：直接激活思源并秒开雷切标签快速切换器
        SuperPanelMain.LaunchPluginInSiYuan(context, syUrl, token, "siyuan-speed-switch|switchTabs||false|标签快速切换");
    }

    private static void ListCommands(IStepContext context, string syUrl, string token)
    {
        var rec = PollBridge(syUrl, token, "commands.list", "{\"plugin\":\"" + TargetPlugin + "\"}", 8000);
        var menu = BuildCommandMenu(rec.DataJson);

        if (menu.Length == 0)
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "通知反馈", "❌ 未获取到小驴雷切命令面【" + rec.Status + "】：" + rec.Message +
                " （请确认插件已安装启用，且雷切服务桥与思源窗口在线）");
        SafeContext.Set(context, "需要通知", true);
            return;
        }

        SafeContext.Set(context, "命令清单", menu);
        SafeContext.Set(context, "需要弹窗选择", true);
        SafeContext.Set(context, "是否成功", true);
    }

    private static void RunCommand(IStepContext context, string syUrl, string token, string picked)
    {
        var sep = picked.LastIndexOf('|');
        var title = sep > 0 ? picked.Substring(0, sep) : picked;
        var langKey = sep > 0 ? picked.Substring(sep + 1) : picked;

        var rec = PollBridge(syUrl, token, "commands.run",
            "{\"plugin\":\"" + TargetPlugin + "\",\"command\":\"" + SafeContext.EscapeJson(langKey) + "\"}", 10000);

        SafeContext.Set(context, "是否成功", rec.Status == "recorded");
        SafeContext.Set(context, "通知反馈", rec.Status == "recorded"
            ? "✓ 已唤起雷切【" + (title == "" ? langKey : title) + "】。"
            : "❌ 执行未完成【" + rec.Status + "】：" + rec.Message);
    }

    /// <summary>commands.list 返回 plugins[].commands（形状随上游演进，防御式解析）</summary>
    private static StringBuilder BuildCommandMenu(string dataJson)
    {
        var sb = new StringBuilder();
        if (string.IsNullOrEmpty(dataJson)) return sb;

        // 形态 A：commands 为对象数组 [{"command"/"langKey": "...", "title": "..."}]
        foreach (Match m in Regex.Matches(dataJson, "\\{[^\\{\\}]+\\}"))
        {
            var b = m.Value;
            var key = ExtractField(b, "command");
            if (key == "") key = ExtractField(b, "langKey");
            if (key == "") continue;
            var title = ExtractField(b, "title");
            if (title == "") title = ExtractField(b, "name");
            sb.Append(title == "" ? key : title).Append("|").Append(key).Append("\n");
            if (sb.ToString().Split('\n').Length > 40) break;
        }

        // 形态 B：commands 为字符串数组
        if (sb.Length == 0)
        {
            foreach (Match m in Regex.Matches(dataJson, "\"command\"\\s*:\\s*\"([^\"]+)\""))
            {
                sb.Append(m.Groups[1].Value).Append("|").Append(m.Groups[1].Value).Append("\n");
            }
        }
        return sb;
    }

    private static string ExtractField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unescape(m.Groups[1].Value) : "";
    }

    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", "");
    }

    private static ReceiptResult PollBridge(string syUrl, string token, string op, string argsJson, int maxMs)
    {
        var send = LvSendCommand.Send(syUrl, token, op, argsJson);
        if (!send.Success)
        {
            return new ReceiptResult { Status = "send-failed", Message = send.Error ?? "命令投递失败", DataJson = "" };
        }
        return LvGetReceipt.Poll(syUrl, token, send.CommandId, maxMs);
    }
}


// ============================================================================
// L0-08: 日历周期笔记极速跳转 —— 主驱动程序 (CalendarShuttleMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 专为日期跳转与周期笔记研读打造的穿梭总控；
//   2. 智能解析自然语言日期（昨天、明天、上周五、2026-10-15、本周周记、本月月记）；
//   3. 深度联动「思源日历面板 (siyuan-plugin-calendar)」的 custom-calendar-weekly/monthly 属性；
//   4. 100% 思源原生自愈：通过标准 SQL 定位日记块，一键唤醒前置并通过 siyuan:// 协议秒级直达！
// ============================================================================


public static class CalendarShuttleMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "桌面应用");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var targetDateStr = SafeContext.GetString(context, "目标日期");
        var rawInput = SafeContext.GetInputText(context);

        if (string.IsNullOrEmpty(targetDateStr) && !string.IsNullOrEmpty(rawInput))
        {
            targetDateStr = rawInput.Trim();
        }

        // 1. 输入内容解析
        if (string.IsNullOrWhiteSpace(targetDateStr))
        {
            var json = "{\"title\":\"思源外骨骼 · 日历周期笔记极速跳转\"," +
                       "\"subtitle\":\"输入日期或周期类型直达笔记 (联动思源日历面板)\"," +
                       "\"badge\":\"日历穿梭\"," +
                       "\"icon\":\"📅\"," +
                       "\"mode\":\"input\"," +
                       "\"outputVar\":\"目标日期\"," +
                       "\"placeholder\":\"输入日期，如: 昨天、前天、上周五、本周周记、本月月记、2026-10-15...\"," +
                       "\"defaultValue\":\"\"," +
                       "\"tags\":[\"昨天\", \"前天\", \"上周五\", \"本周周记\", \"本月月记\"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 2. 环境自愈探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 3. 周期笔记分支 (周记 / 月记 / 年记)
        string docId = "";
        string noteLabel = targetDateStr;

        if (targetDateStr.Contains("周记"))
        {
            var sqlW = "{\"stmt\":\"SELECT id, content FROM blocks WHERE type='d' AND (ial LIKE '%custom-calendar-weekly%' OR content LIKE '%周记%' OR content LIKE '%W%') ORDER BY updated DESC LIMIT 1\"}";
            var rw = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sqlW);
            if (rw.Success) docId = ExtractJsonField(rw.Text, "id");
            noteLabel = "本周周记";
        }
        else if (targetDateStr.Contains("月记"))
        {
            var sqlM = "{\"stmt\":\"SELECT id, content FROM blocks WHERE type='d' AND (ial LIKE '%custom-calendar-monthly%' OR content LIKE '%月记%') ORDER BY updated DESC LIMIT 1\"}";
            var rm = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sqlM);
            if (rm.Success) docId = ExtractJsonField(rm.Text, "id");
            noteLabel = "本月月记";
        }
        else
        {
            // 日期推算
            DateTime dt = DateTime.Today;
            if (targetDateStr.Contains("昨天") || targetDateStr.Contains("昨日")) dt = DateTime.Today.AddDays(-1);
            else if (targetDateStr.Contains("前天")) dt = DateTime.Today.AddDays(-2);
            else if (targetDateStr.Contains("大前天")) dt = DateTime.Today.AddDays(-3);
            else if (targetDateStr.Contains("明天")) dt = DateTime.Today.AddDays(1);
            else if (targetDateStr.Contains("后天")) dt = DateTime.Today.AddDays(2);
            else
            {
                var m = Regex.Match(targetDateStr, "\\b(\\d{4})[\\-/年](\\d{1,2})[\\-/月](\\d{1,2})日?\\b");
                if (m.Success)
                {
                    int y = int.Parse(m.Groups[1].Value);
                    int mon = int.Parse(m.Groups[2].Value);
                    int d = int.Parse(m.Groups[3].Value);
                    dt = new DateTime(y, mon, d);
                }
            }

            var compact = dt.ToString("yyyyMMdd");
            var dash = dt.ToString("yyyy-MM-dd");
            noteLabel = dash + " 日记";

            var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + compact + "%' ORDER BY updated DESC LIMIT 1\"}";
            var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
            if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

            if (string.IsNullOrEmpty(docId))
            {
                var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + dash + "%' OR hpath LIKE '%" + dash + "%') ORDER BY updated DESC LIMIT 1\"}";
                var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
                if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
            }
        }

        // 4. 打开目标文档并激活前置
        if (!string.IsNullOrEmpty(docId))
        {
            try
            {
                LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(docId) + "\"}", "siyuan-speed-switch", "fast");
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo("siyuan://blocks/" + docId) { UseShellExecute = true });
            }
            catch { }

            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 已秒级直达【" + noteLabel + "】！");
        }
        else
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "⚠️ 未检索到【" + noteLabel + "】对应的笔记文档。");
        }
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// L1-01: 语义前缀智能捕获 —— 主驱动程序 (SmartCaptureMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 渐进增强与原生自愈：若安装了对应小驴插件，享受高级结构化体验；
//      若未安装插件（或纯净版思源），100% 自动降级为思源原生双链、标签与待办，绝不报错阻断！
//   2. 多目标前缀路由机制：
//      - '@人名'  : 优先小驴人脉建立档案；未装插件则自动以思源原生双链 [[人名]] 记入日记
//      - '#项目'  : 自动检索对应项目专页；未找到则自动附带 #{项目} 标签安全归入日记
//      - '!'/'!待办' : 格式化为 '- [ ]' 待办写入今日日记；若有小驴管家则同步备忘
//      - '?'/'?闪卡' : 写入带 custom-riff-decks 原生属性的问答块，思源原生即可复习
//      - 普通文本 : 兜底带时间戳追加至今日日记文末
//   3. 高保真 Bento HTML 交互弹窗（带前缀标签快捷点选与即时回车确认）
// ============================================================================

