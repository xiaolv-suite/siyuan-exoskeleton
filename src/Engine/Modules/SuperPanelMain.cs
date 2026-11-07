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
public static class SuperPanelMain
{
    // 动态路径自动探测
    private static int _lastRuntimeWriteTick = int.MinValue;

    // ---------------- Win32 窗口唤醒与前置 P/Invoke ----------------
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    public static void Exec(IStepContext context)
    {
        var existingNext = SafeContext.GetString(context, "后续动作");
        if (!string.IsNullOrEmpty(existingNext))
        {
            if (existingNext.StartsWith("runaction:", StringComparison.OrdinalIgnoreCase))
            {
                var actId = existingNext.Substring(10).Trim();
                SafeContext.Set(context, "需要调起动作", true);
                SafeContext.Set(context, "目标动作ID", actId);
                SafeContext.Set(context, "后续动作", "");
                SafeContext.Set(context, "跳过UI", true);
                return;
            }
            if (existingNext.StartsWith("openurl:", StringComparison.OrdinalIgnoreCase))
            {
                var url = existingNext.Substring(8).Trim();
                SafeContext.Set(context, "需要打开链接", true);
                SafeContext.Set(context, "目标链接", url);
                SafeContext.Set(context, "后续动作", "");
                SafeContext.Set(context, "跳过UI", true);
                return;
            }
            if (existingNext.StartsWith("install_store:", StringComparison.OrdinalIgnoreCase))
            {
                SafeContext.Set(context, "后续动作", "");
                SafeContext.Set(context, "跳过UI", true);
                return;
            }
        }
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var directCmd = SafeContext.GetString(context, "指令码");
        var pluginLaunch = SafeContext.GetString(context, "插件直达");
        var menuJsonPath = SafeContext.GetString(context, "菜单配置路径");
        var quickParam = SafeContext.GetString(context, "quicker_in_param");

        // 支持从外部动作或指令直接传参：例如 "plugin:siyuan-checkin|openCheckin" 或 "syd"
        if (string.IsNullOrEmpty(pluginLaunch) && string.IsNullOrEmpty(directCmd) && !string.IsNullOrEmpty(quickParam))
        {
            if (quickParam.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
            {
                pluginLaunch = quickParam.Substring(7).Trim();
                SafeContext.Set(context, "插件直达", pluginLaunch);
            }
            else
            {
                directCmd = quickParam.Trim();
                SafeContext.Set(context, "指令码", directCmd);
            }
        }

        // 1. 自动自愈环境探测 (利用 60s 内存缓存 + 双内核动态端口发现)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        // 2. 分支 A：跨软件唤起并打开指定思源插件/命令（WebView2 回程或直达传参）
        if (!string.IsNullOrEmpty(pluginLaunch))
        {
            SafeContext.Set(context, "跳过UI", true);
            SafeContext.Set(context, "插件直达", ""); // 清空防重入
            LaunchPluginInSiYuan(context, syUrl, token, pluginLaunch);
            return;
        }

        // 3. 分支 B：面板内置直达指令（syd / syzg / sytb / sysz / syqd 等）
        if (!string.IsNullOrEmpty(directCmd))
        {
            SafeContext.Set(context, "跳过UI", true);
            SafeContext.Set(context, "指令码", ""); // 清空防重入
            if (TryHandleBuiltinDirectCmd(context, syUrl, token, directCmd))
            {
                return;
            }

            // 全内聚 C# 调度器直跑
            if (ActionDispatcher.Dispatch(directCmd, context))
            {
                return;
            }

            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "⚠️ 未识别直达指令【" + directCmd + "】");
            return;
        }

        // 4. 分支 C：准备打开 WebView2 超级全局面板 —— 先刷新脱敏运行时快照 (.runtime-state.js)
        string panelPath = ResolvePanelHtmlPath();
        if (!string.IsNullOrEmpty(panelPath))
        {
            string panelDir = Path.GetDirectoryName(panelPath);
            RefreshRuntimeStateFile(syUrl, token, panelDir);
            SafeContext.Set(context, "panel_url", "file:///" + panelPath.Replace('\\', '/'));
        }
        else
        {
            SafeContext.Set(context, "panel_url", "file:///" + (ResolvePanelHtmlPath() ?? "").Replace('\\', '/'));
        }

        SafeContext.Set(context, "跳过UI", false);
    }

    // =========================================================================
    // 核心能力 1：跨软件唤醒前置思源窗口 + 毫秒级直开目标插件
    // =========================================================================
    public static void LaunchPluginInSiYuan(IStepContext context, string syUrl, string token, string spec)
    {
        // 编码格式：pluginId|commandId|accelerator|focusOnly|displayLabel
        var parts = spec.Split('|');
        var pluginId = parts.Length > 0 ? parts[0].Trim() : "";
        var commandId = parts.Length > 1 ? parts[1].Trim() : "";
        var hotkey = parts.Length > 2 ? parts[2].Trim() : "";
        var focusOnly = parts.Length > 3 && string.Equals(parts[3].Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var label = parts.Length > 4 && !string.IsNullOrEmpty(parts[4].Trim()) ? parts[4].Trim() : (pluginId + "/" + commandId);

        if (string.IsNullOrEmpty(pluginId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "通知反馈", "⚠️ 插件标识为空，无法唤起");
            return;
        }

        // 第一步：无论当前处于浏览器/Word/微信还是桌面，先唤醒并将思源主窗口置为前台焦点窗口
        IntPtr hwnd = ActivateSiYuanWindow();

        // 第二步：优先走免确认公开桥或快门 commands.run 精准调用插件内存回调（不受输入法与按键冲突影响）
        bool ok = false;
        string detail = "";

        if (!focusOnly && !string.IsNullOrEmpty(commandId))
        {
            string op = "commands.run";
            string argsJson = "{\"plugin\":\"" + SafeContext.EscapeJson(pluginId) + "\",\"command\":\"" + SafeContext.EscapeJson(commandId) + "\"}";

            // 小驴管家与小驴考试拥有专用零门控导航 op，优先直调
            if (pluginId == "siyuan-home" && (commandId == "openButler" || commandId == "openReminders"))
            {
                op = "home.open";
                argsJson = "{\"target\":\"" + (commandId == "openReminders" ? "reminders" : "butler") + "\"}";
            }
            else if (pluginId == "siyuan-exam" && commandId.StartsWith("command.open", StringComparison.OrdinalIgnoreCase))
            {
                var sub = commandId.Substring("command.open".Length).ToLowerInvariant();
                if (sub == "practice" || sub == "wrongbook" || sub == "mock" || sub == "report")
                {
                    op = "exam.open";
                    argsJson = "{\"target\":\"" + sub + "\"}";
                }
            }

            var sendRes = LvSendCommand.Send(syUrl, token, op, argsJson, "siyuan-speed-switch", "fast");
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 3500, "siyuan-speed-switch");
                if (rec.Status == "recorded" || rec.Status == "duplicate")
                {
                    ok = true;
                    detail = rec.Message;
                }
                else
                {
                    detail = rec.Status + ": " + rec.Message;
                }
            }
            else
            {
                detail = sendRes.Error;
            }
        }

        // 第三步：若是 focusOnly 焦点型命令（或快门桥未开启）且该插件命令配有思源快捷键，在思源已前置的前提下投递热键
        if (!ok && !string.IsNullOrEmpty(hotkey) && hwnd != IntPtr.Zero)
        {
            var sendKeysExpr = TranslateAcceleratorToSendKeys(hotkey);
            if (!string.IsNullOrEmpty(sendKeysExpr))
            {
                try
                {
                    Thread.Sleep(90);
                    if (GetForegroundWindow() == hwnd)
                    {
                        System.Windows.Forms.SendKeys.SendWait(sendKeysExpr);
                        ok = true;
                        detail = "已投递插件热键 " + hotkey;
                    }
                }
                catch (Exception ex)
                {
                    detail = "热键投递异常: " + ex.Message;
                }
            }
        }

        SafeContext.Set(context, "是否成功", ok);
        SafeContext.Set(context, "执行结果", ok ? "ok" : "fail");
        SafeContext.Set(context, "通知反馈", ok
            ? "✓ 已唤起思源并打开【" + label + "】"
            : "⚠️ 打开【" + label + "】未完成（" + (string.IsNullOrEmpty(detail) ? "请确认思源与雷切服务桥已开启" : detail) + "）");
    }

    /// <summary>
    /// 查找并前置思源桌面主窗口；若最小化则还原，若隐藏于系统托盘则经 siyuan:// 协议唤醒后前置
    /// </summary>
    public static IntPtr ActivateSiYuanWindow()
    {
        IntPtr hwnd = FindSiYuanMainWindow();
        if (hwnd == IntPtr.Zero)
        {
            try
            {
                Process.Start(new ProcessStartInfo("siyuan://") { UseShellExecute = true });
            }
            catch { }

            var deadline = Environment.TickCount + 2500;
            while (Environment.TickCount < deadline)
            {
                Thread.Sleep(120);
                hwnd = FindSiYuanMainWindow();
                if (hwnd != IntPtr.Zero) break;
            }
        }

        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(hwnd, SW_SHOW);
            }

            IntPtr fg = GetForegroundWindow();
            if (fg != hwnd)
            {
                uint fgPid;
                uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out fgPid) : 0;
                uint curThread = GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != curThread)
                {
                    attached = AttachThreadInput(curThread, fgThread, true);
                }
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(curThread, fgThread, false);
                }
            }
            Thread.Sleep(60);
        }
        catch { }

        return hwnd;
    }

    private static IntPtr FindSiYuanMainWindow()
    {
        try
        {
            var procs = Process.GetProcessesByName("SiYuan");
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))
                    {
                        return p.MainWindowHandle;
                    }
                }
                catch { }
            }
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        return p.MainWindowHandle;
                    }
                }
                catch { }
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 将思源快捷键表示（如 ⌥⇧C / ⌥⌘G / Ctrl+Alt+P）转换为 Windows SendKeys 表达式
    /// </summary>
    public static string TranslateAcceleratorToSendKeys(string acc)
    {
        if (string.IsNullOrWhiteSpace(acc)) return "";
        var first = acc.Split(',')[0].Trim();
        bool ctrl = false, alt = false, shift = false;

        if (first.Contains("⌃") || first.Contains("⌘") || first.IndexOf("Ctrl", StringComparison.OrdinalIgnoreCase) >= 0 || first.IndexOf("Cmd", StringComparison.OrdinalIgnoreCase) >= 0)
            ctrl = true;
        if (first.Contains("⌥") || first.IndexOf("Alt", StringComparison.OrdinalIgnoreCase) >= 0)
            alt = true;
        if (first.Contains("⇧") || first.IndexOf("Shift", StringComparison.OrdinalIgnoreCase) >= 0)
            shift = true;

        var key = Regex.Replace(first, "(?i)(Ctrl|Control|Alt|Shift|Cmd|Meta|\\+|\\s|⌃|⌘|⌥|⇧)", "");
        if (string.IsNullOrEmpty(key)) return "";

        string sendKey = "";
        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
        {
            sendKey = key.ToLowerInvariant();
        }
        else if (key == "↑" || key.Equals("Up", StringComparison.OrdinalIgnoreCase)) sendKey = "{UP}";
        else if (key == "↓" || key.Equals("Down", StringComparison.OrdinalIgnoreCase)) sendKey = "{DOWN}";
        else if (key == "←" || key.Equals("Left", StringComparison.OrdinalIgnoreCase)) sendKey = "{LEFT}";
        else if (key == "→" || key.Equals("Right", StringComparison.OrdinalIgnoreCase)) sendKey = "{RIGHT}";
        else if (Regex.IsMatch(key, "^F([1-9]|1[0-2])$", RegexOptions.IgnoreCase)) sendKey = "{" + key.ToUpperInvariant() + "}";
        else return "";

        return (ctrl ? "^" : "") + (alt ? "%" : "") + (shift ? "+" : "") + sendKey;
    }

    // =========================================================================
    // 核心能力 2：内置直达指令全闭环（修复旧版 direct_* 断链）
    // =========================================================================
    public static bool TryHandleBuiltinDirectCmd(IStepContext context, string syUrl, string token, string cmd)
    {
        var key = cmd.Trim().ToLowerInvariant();

        // 1. 打开今日日记 (syd / enter)
        if (key == "syd" || key == "enter")
        {
            var todayCompact = DateTime.Now.ToString("yyyyMMdd");
            var todayDash = DateTime.Now.ToString("yyyy-MM-dd");
            string docId = "";

            // 优先查思源原生 custom-dailynote-yyyyMMdd 属性
            var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + todayCompact + "%' ORDER BY updated DESC LIMIT 1\"}";
            var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
            if (r1.Success) docId = ExtractJsonField(r1.Text, "id");

            if (string.IsNullOrEmpty(docId))
            {
                var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + todayDash + "%' OR hpath LIKE '%" + todayDash + "%') ORDER BY updated DESC LIMIT 1\"}";
                var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
                if (r2.Success) docId = ExtractJsonField(r2.Text, "id");
            }

            if (string.IsNullOrEmpty(docId))
            {
                // 今日日记尚未创建：自动按日记笔记本新建今日日记后直达
                var nb = SafeContext.GetDiaryNotebookId(context, syUrl, token);
                if (!string.IsNullOrEmpty(nb))
                {
                    var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(nb) + "\"}");
                    if (createRes.Success) docId = ExtractJsonField(createRes.Text, "id");
                }
            }

            if (!string.IsNullOrEmpty(docId))
            {
                // 双轨调度：1. 雷切网关 doc.open 强制思源前端切到该标签页；2. Win32 唤醒并置顶；3. siyuan:// 协议兜底
                LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(docId) + "\"}", "siyuan-speed-switch", "fast");
                ActivateSiYuanWindow();
                try { Process.Start(new ProcessStartInfo("siyuan://blocks/" + docId) { UseShellExecute = true }); } catch { }
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 已唤起思源并打开今日日记");
            }
            else
            {
                ActivateSiYuanWindow();
                try { Process.Start(new ProcessStartInfo("siyuan://") { UseShellExecute = true }); } catch { }
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 已唤起思源笔记");
            }
            return true;
        }

        // 2. 打开最近修改的文档 (syzg / zg)
        if (key == "syzg" || key == "zg")
        {
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"SELECT id, content FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 1\"}");
            var docId = r.Success ? ExtractJsonField(r.Text, "id") : "";
            var title = r.Success ? ExtractJsonField(r.Text, "content") : "";
            if (!string.IsNullOrEmpty(docId))
            {
                LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(docId) + "\"}", "siyuan-speed-switch", "fast");
                ActivateSiYuanWindow();
                try { Process.Start(new ProcessStartInfo("siyuan://blocks/" + docId) { UseShellExecute = true }); } catch { }
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 已打开最近修改文档：" + title);
            }
            else
            {
                ActivateSiYuanWindow();
                SafeContext.Set(context, "是否成功", false);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "⚠️ 未查询到最近修改文档");
            }
            return true;
        }

        // 3. 一键触发增量云端同步 (sytb / tb)
        if (key == "sytb" || key == "tb")
        {
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/sync/performSync", "{\"mobileSwitch\":false,\"upload\":true}");
            SafeContext.Set(context, "是否成功", r.Success);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", r.Success ? "✓ 已触发思源多端增量同步" : "⚠️ 同步触发失败：" + r.Error);
            return true;
        }

        // 4. 打开思源全局设置窗口 (sysz / sz)
        if (key == "sysz" || key == "sz")
        {
            ActivateSiYuanWindow();
            var sendRes = LvSendCommand.Send(syUrl, token, "setting.open", "{}", "siyuan-speed-switch", "fast");
            bool ok = false;
            if (sendRes.Success)
            {
                var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 3000, "siyuan-speed-switch");
                ok = (rec.Status == "recorded" || rec.Status == "duplicate");
            }
            SafeContext.Set(context, "是否成功", ok);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", ok ? "✓ 已唤起思源并打开系统设置" : "⚠️ 打开设置未响应，请确认生态桥已开启");
            return true;
        }

        // 5. 查看已装插件与生态就绪度 (syqd / qd)
        if (key == "syqd" || key == "qd")
        {
            ExecuteRegistryProbe(context);
            return true;
        }

        return false;
    }

    // =========================================================================
    // 核心能力 3：面板脱敏运行时快照 (.runtime-state.js) 预生成（30s 缓存，零 Token 落盘）
    // =========================================================================
    public static void RefreshRuntimeStateFile(string syUrl, string token, string panelDir)
    {
        if (string.IsNullOrEmpty(panelDir) || !Directory.Exists(panelDir)) return;
        string statePath = Path.Combine(panelDir, ".runtime-state.js");

        try
        {
            int now = Environment.TickCount;
            if (_lastRuntimeWriteTick != int.MinValue && (now - _lastRuntimeWriteTick) < 30000 && File.Exists(statePath))
            {
                return;
            }
            if (File.Exists(statePath))
            {
                var ageSec = (DateTime.UtcNow - File.GetLastWriteTimeUtc(statePath)).TotalSeconds;
                if (ageSec >= 0 && ageSec < 25)
                {
                    _lastRuntimeWriteTick = now;
                    return;
                }
            }
        }
        catch { }

        bool syOnline = false;
        string syVersion = "";
        int docCount = -1;
        bool bridgeOnline = false;
        string activeDocId = "";
        string activeDocTitle = "";
        int dueCardCount = 0;
        int todayBlockCount = 0;
        int uncompletedTasks = 0;
        string recentDocsJson = "[]";
        string avTablesJson = "[]";
        string pluginsArrayJson = "[]";

        try
        {
            var verRes = SyKernelRequest.DoRequest(syUrl, token, "/api/system/version", "{}");
            if (verRes.Success)
            {
                syOnline = true;
                syVersion = ExtractJsonField(verRes.Text, "data");
            }

            if (syOnline)
            {
                // 1. 查询待复习 FSRS 闪卡总数
                try
                {
                    var riffRes = SyKernelRequest.DoRequest(syUrl, token, "/api/riff/getRiffDueCards", "{\"deckID\":\"\"}");
                    if (riffRes.Success && !string.IsNullOrEmpty(riffRes.Text))
                    {
                        var rDict = JsonHelper.ParseDict(riffRes.Text);
                        var rData = JsonHelper.GetDict(rDict, "data");
                        if (rData != null)
                        {
                            dueCardCount = JsonHelper.GetInt(rData, "unreviewedCount", 0);
                        }
                    }
                }
                catch { }

                // 2. 统计全库未完成待办事项总数 (Task Horizon 哨兵)
                try
                {
                    var taskSql = "SELECT count(1) as total FROM blocks WHERE content LIKE '- [ ]%'";
                    var taskRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(taskSql) + "\"}");
                    if (taskRes.Success && !string.IsNullOrEmpty(taskRes.Text))
                    {
                        var mTask = Regex.Match(taskRes.Text, "\"total\"\\s*:\\s*([0-9]+)");
                        if (mTask.Success) int.TryParse(mTask.Groups[1].Value, out uncompletedTasks);
                    }
                }
                catch { }

                var cntRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"SELECT count(1) as total FROM blocks WHERE type='d'\"}");
                if (cntRes.Success)
                {
                    var m = Regex.Match(cntRes.Text ?? "", "\"total\"\\s*:\\s*([0-9]+)");
                    if (m.Success) int.TryParse(m.Groups[1].Value, out docCount);
                }

                var docRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"SELECT id, content, hpath, updated FROM blocks WHERE type='d' ORDER BY updated DESC LIMIT 60\"}");
                if (docRes.Success && !string.IsNullOrEmpty(docRes.Text))
                {
                    var extractedDocs = ExtractJsonArrayField(docRes.Text, "data");
                    if (!string.IsNullOrEmpty(extractedDocs)) recentDocsJson = extractedDocs;

                    var idM = Regex.Match(docRes.Text ?? "", "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    var titleM = Regex.Match(docRes.Text ?? "", "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (idM.Success) activeDocId = idM.Groups[1].Value;
                    if (titleM.Success) activeDocTitle = titleM.Groups[1].Value;

                    // 查询当前活跃日记文档已收录的块总数
                    if (!string.IsNullOrEmpty(activeDocId))
                    {
                        try
                        {
                            var tbRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"SELECT count(1) as cnt FROM blocks WHERE root_id = '" + SafeContext.EscapeJson(activeDocId) + "'\"}");
                            if (tbRes.Success && !string.IsNullOrEmpty(tbRes.Text))
                            {
                                var tbDict = JsonHelper.ParseDict(tbRes.Text);
                                var tbData = JsonHelper.GetList(tbDict, "data");
                                if (tbData != null && tbData.Count > 0)
                                {
                                    var fRow = tbData[0] as Dictionary<string, object>;
                                    if (fRow != null) todayBlockCount = JsonHelper.GetInt(fRow, "cnt", 0);
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 检索全库属性视图 (AV 数据库表格)
                avTablesJson = "[]";
                try
                {
                    var avSearchRes = SyKernelRequest.DoRequest(syUrl, token, "/api/av/searchAttributeView", "{\"keyword\":\"\"}");
                    if (avSearchRes.Success && !string.IsNullOrEmpty(avSearchRes.Text))
                    {
                        var avDict = JsonHelper.ParseDict(avSearchRes.Text);
                        var avData = JsonHelper.GetDict(avDict, "data");
                        if (avData != null)
                        {
                            var results = JsonHelper.GetList(avData, "results");
                            if (results != null && results.Count > 0)
                            {
                                var simplifiedList = new List<Dictionary<string, object>>();
                                foreach (var item in results)
                                {
                                    var it = item as Dictionary<string, object>;
                                    if (it == null) continue;
                                    string avId = JsonHelper.GetString(it, "avID");
                                    string blockId = JsonHelper.GetString(it, "blockID");
                                    string avName = JsonHelper.GetString(it, "avName");
                                    string hPath = JsonHelper.GetString(it, "hPath");
                                    if (string.IsNullOrEmpty(avName))
                                    {
                                        avName = !string.IsNullOrEmpty(hPath) ? Path.GetFileName(hPath.Replace('\\', '/')) : "属性视图";
                                    }
                                    simplifiedList.Add(new Dictionary<string, object>
                                    {
                                        { "avId", avId },
                                        { "blockId", blockId },
                                        { "name", avName },
                                        { "hPath", hPath }
                                    });
                                }
                                avTablesJson = JsonHelper.ToJson(simplifiedList);
                            }
                        }
                    }
                }
                catch { }

                // 拉取实时已装插件与全部命令（小驴全家桶 + 任意第三方社区插件）
                var cmdSend = LvSendCommand.Send(syUrl, token, "commands.list", "{}", "siyuan-speed-switch", "fast");
                if (cmdSend.Success)
                {
                    var cmdRec = LvGetReceipt.Poll(syUrl, token, cmdSend.CommandId, 1500, "siyuan-speed-switch");
                    if ((cmdRec.Status == "recorded" || cmdRec.Status == "duplicate") && !string.IsNullOrEmpty(cmdRec.DataJson))
                    {
                        bridgeOnline = true;
                        var extracted = ExtractJsonArrayField(cmdRec.DataJson, "plugins");
                        if (!string.IsNullOrEmpty(extracted)) pluginsArrayJson = extracted;
                    }
                }
            }

            // 本地自愈兜底：若雷切桥未返回命令清单，直接扫描工作空间 data/plugins/*/plugin.json
            if (pluginsArrayJson == "[]")
            {
                try
                {
                    string ws = XiaoLv.Desktop.Core.SiYuanClient.DiscoveredWorkspaceDir;
                    string pluginsDir = Path.Combine(ws, "data", "plugins");
                    if (Directory.Exists(pluginsDir))
                    {
                        var pList = new List<Dictionary<string, object>>();
                        foreach (var d in Directory.GetDirectories(pluginsDir))
                        {
                            var pj = Path.Combine(d, "plugin.json");
                            if (File.Exists(pj))
                            {
                                try
                                {
                                    var pjText = File.ReadAllText(pj, Encoding.UTF8);
                                    var dict = JsonHelper.ParseDict(pjText);
                                    string name = JsonHelper.GetString(dict, "name");
                                    if (string.IsNullOrEmpty(name)) name = Path.GetFileName(d);
                                    string disp = name;
                                    if (dict.ContainsKey("displayName"))
                                    {
                                        var dispObj = dict["displayName"];
                                        var dispDict = dispObj as Dictionary<string, object>;
                                        if (dispDict != null)
                                        {
                                            disp = JsonHelper.GetString(dispDict, "zh_CN");
                                            if (string.IsNullOrEmpty(disp)) disp = JsonHelper.GetString(dispDict, "zh-CN");
                                            if (string.IsNullOrEmpty(disp)) disp = JsonHelper.GetString(dispDict, "default");
                                            if (string.IsNullOrEmpty(disp)) disp = JsonHelper.GetString(dispDict, "en_US");
                                        }
                                        else if (dispObj is string)
                                        {
                                            disp = (string)dispObj;
                                        }
                                    }
                                    string desc = "";
                                    if (dict.ContainsKey("description"))
                                    {
                                        var descObj = dict["description"];
                                        var descDict = descObj as Dictionary<string, object>;
                                        if (descDict != null)
                                        {
                                            desc = JsonHelper.GetString(descDict, "zh_CN");
                                            if (string.IsNullOrEmpty(desc)) desc = JsonHelper.GetString(descDict, "default");
                                        }
                                        else if (descObj is string)
                                        {
                                            desc = (string)descObj;
                                        }
                                    }

                                    pList.Add(new Dictionary<string, object>
                                    {
                                        { "name", name },
                                        { "displayName", string.IsNullOrEmpty(disp) ? name : disp },
                                        { "desc", desc },
                                        { "commands", new List<object>() }
                                    });
                                }
                                catch { }
                            }
                        }
                        if (pList.Count > 0)
                        {
                            pluginsArrayJson = JsonHelper.ToJson(pList);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            string clipboardJson = "null";
            if (ClipboardWatcher.IsEnabled && ClipboardWatcher.LatestItem != null && !string.IsNullOrEmpty(ClipboardWatcher.LatestItem.Summary))
            {
                clipboardJson = JsonHelper.ToJson(new
                {
                    type = ClipboardWatcher.LatestItem.SniffedType,
                    summary = ClipboardWatcher.LatestItem.Summary,
                    markdown = ClipboardWatcher.LatestItem.EnrichedMarkdown
                });
            }

            var sb = new StringBuilder();
            sb.Append("window.__OMNI_RUNTIME__ = {");
            sb.Append("\"updatedAt\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("\",");
            sb.Append("\"syOnline\":").Append(syOnline ? "true" : "false").Append(",");
            sb.Append("\"syVersion\":\"").Append(SafeContext.EscapeJson(syVersion)).Append("\",");
            sb.Append("\"docCount\":").Append(docCount).Append(",");
            sb.Append("\"dueCardCount\":").Append(dueCardCount).Append(",");
            sb.Append("\"uncompletedTasks\":").Append(uncompletedTasks).Append(",");
            sb.Append("\"bridgeOnline\":").Append(bridgeOnline ? "true" : "false").Append(",");
            sb.Append("\"mcpOnline\":true,");
            sb.Append("\"kernelUrl\":\"").Append(SafeContext.EscapeJson(syUrl)).Append("\",");
            sb.Append("\"activeDocId\":\"").Append(SafeContext.EscapeJson(activeDocId)).Append("\",");
            sb.Append("\"activeDocTitle\":\"").Append(SafeContext.EscapeJson(activeDocTitle)).Append("\",");
            sb.Append("\"recentDocs\":").Append(recentDocsJson).Append(",");
            sb.Append("\"avTables\":").Append(avTablesJson).Append(",");
            sb.Append("\"latestClipboard\":").Append(clipboardJson).Append(",");
            var evTool = XiaoLv.Desktop.Core.ToolDetector.DetectEverything();
            var listaryTool = XiaoLv.Desktop.Core.ToolDetector.DetectListary();
            sb.Append("\"everythingOnline\":").Append((evTool != null && evTool.IsRunning) ? "true" : "false").Append(",");
            sb.Append("\"everythingInstalled\":").Append((evTool != null && evTool.IsInstalled) ? "true" : "false").Append(",");
            sb.Append("\"listaryOnline\":").Append((listaryTool != null && listaryTool.IsRunning) ? "true" : "false").Append(",");
            sb.Append("\"listaryInstalled\":").Append((listaryTool != null && listaryTool.IsInstalled) ? "true" : "false").Append(",");
            sb.Append("\"plugins\":").Append(pluginsArrayJson);
            sb.Append("};\n");
            File.WriteAllText(statePath, sb.ToString(), Encoding.UTF8);
            _lastRuntimeWriteTick = Environment.TickCount;
        }
        catch { }
    }

    private static string ExtractJsonArrayField(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return "";
        var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\\[");
        if (!m.Success) return "";
        int start = m.Index + m.Length - 1; // '[' 位置
        int depth = 0;
        bool inStr = false;
        bool esc = false;
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, i - start + 1);
                }
            }
        }
        return "";
    }

    private static string ResolvePanelHtmlPath()
    {
        string fixedPath = "";
        if (File.Exists(fixedPath)) return fixedPath;
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string p1 = Path.Combine(appDir, "files", "panel.html");
        if (File.Exists(p1)) return p1;
        string p2 = Path.Combine(appDir, "panel.html");
        if (File.Exists(p2)) return p2;
        try
        {
            string curDir = Directory.GetCurrentDirectory();
            string p3 = Path.Combine(curDir, "files", "panel.html");
            if (File.Exists(p3)) return p3;
            string p4 = Path.Combine(curDir, "panel.html");
            if (File.Exists(p4)) return p4;
        }
        catch { }
        return fixedPath;
    }
    public static string ExtractJsonField(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return "";
        var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"");
        if (m.Success) return m.Groups[1].Value;
        var m2 = Regex.Match(json, "\"" + field + "\"\\s*:\\s*([0-9]+)");
        if (m2.Success) return m2.Groups[1].Value;
        return "";
    }

    private static void ExecuteRegistryProbe(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url");
        var token = SafeContext.GetString(context, "sy_token");
        var sendRes = LvSendCommand.Send(syUrl, token, "registry.list", "{}", "siyuan-speed-switch", "fast");
        string rawData = "";
        string msg = "已完成探测";
        if (sendRes.Success)
        {
            var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 2500, "siyuan-speed-switch");
            rawData = rec.DataJson;
            msg = rec.Message;
        }
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "通知反馈", "✓ 思源生态插件就绪状态: " + msg);
        SafeContext.Set(context, "结果数据", rawData);
    }
}


// ============================================================================
// L0-02: 环境预检与自动自愈 —— 主驱动程序 (PreflightMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 核心逻辑：
//   1. 执行多级环境探针（调用底层通用子程序 SyPreflight.Exec）
//   2. 检查：思源内核可达性、API 令牌有效性、小驴快门中枢就绪度、依赖插件在装状态、Quicker MCP 状态
//   3. 自动自愈：若思源未运行，自动探测可执行文件并提供一键拉起启动能力；若 Token 变动自动刷新
//   4. 输出结构化 Markdown 预检报告，标明 ✓/✗ 并给出明确的可解释修复建议
// ============================================================================

