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
public static class SyRouter
{
    // 内核同步路由白名单操作（与快门 R78-P0 KERNEL_OPS 严格一致：仅纯只读发现面，template.new 等写操作严禁旁路前端桥确认与审计）
    public static readonly HashSet<string> KernelOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bridge.ping", "registry.list", "diagnostics.report",
        "events.list", "events.pull", "config.discover"
    };

    public static void Exec(IStepContext context)
    {
        var t = SafeContext.GetString(context, "载荷t").ToLower();
        var v = SafeContext.GetString(context, "载荷v");

        // 1. 占位符解析
        try
        {
            v = ResolvePlaceholders(v, context);
        }
        catch (Exception ex)
        {
            Fail(context, "占位符解析失败：" + ex.Message);
            return;
        }

        // 2. 六大分支分发
        switch (t)
        {
            case "url":
                SafeContext.Set(context, "后续动作", "openurl:" + v);
                Ok(context, "打开链接指令已就绪", "");
                break;

            case "act":
                SafeContext.Set(context, "后续动作", "runaction:" + v);
                Ok(context, "转跳动作指令已就绪", "");
                break;

            case "http":
                HttpBranch(context, v);
                break;

            case "sql-url":
                SqlUrlBranch(context, v);
                break;

            case "bridge":
            case "cmd":
                BridgeBranch(context, v, t == "cmd");
                break;

            default:
                Fail(context, "未知载荷类型：" + t);
                break;
        }
    }

    /// <summary>
    /// http 分支：调用思源内核 API（支持 端点|请求体 管道格式）
    /// </summary>
    private static void HttpBranch(IStepContext context, string payloadV)
    {
        var sep = payloadV.IndexOf('|');
        var endpoint = (sep < 0 ? payloadV : payloadV.Substring(0, sep)).Trim();
        var body = sep < 0 ? "" : payloadV.Substring(sep + 1).Trim();

        var syUrl = SafeContext.GetString(context, "sy_url");
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var result = SyKernelRequest.DoRequest(syUrl, token, endpoint, body);
        if (!result.Success)
        {
            Fail(context, result.Error);
            return;
        }

        Ok(context, "内核调用成功 (" + endpoint + ")", result.Text);
    }

    /// <summary>
    /// sql-url 分支：执行 SQL 查询获取块 ID，并生成跳转链接
    /// </summary>
    private static void SqlUrlBranch(IStepContext context, string payloadV)
    {
        var sql = payloadV.Length > 0 ? payloadV : "SELECT root_id FROM blocks WHERE id='{选中块ID}' LIMIT 1";
        var syUrl = SafeContext.GetString(context, "sy_url");
        var token = SafeContext.GetString(context, "sy_token");

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var reqBody = "{\"stmt\":\"" + EscapeJson(sql) + "\"}";
        var result = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", reqBody);

        if (!result.Success)
        {
            Fail(context, "SQL 查询失败：" + result.Error);
            return;
        }

        var rootId = ExtractJsonStringField(result.Text, "root_id");
        if (string.IsNullOrEmpty(rootId))
        {
            // 尝试从 id 字段提取
            rootId = ExtractJsonStringField(result.Text, "id");
        }

        if (string.IsNullOrEmpty(rootId))
        {
            // 兜底查看变量今日日记ID
            var today = SafeContext.GetString(context, "今日日记ID");
            if (today.Length > 0) rootId = today;
            else
            {
                Fail(context, "未找到目标块或文档记录");
                return;
            }
        }

        context.SetVarValue("后续动作", "openurl:siyuan://blocks/" + rootId);
        Ok(context, "已定位文档块: " + rootId, result.Text);
    }

    /// <summary>
    /// bridge / cmd 分支：快门命令桥分发
    /// </summary>
    private static void BridgeBranch(IStepContext context, string argsJson, bool isCmd)
    {
        var syUrl = SafeContext.GetString(context, "sy_url");
        var token = SafeContext.GetString(context, "sy_token");
        var plugin = SafeContext.GetString(context, "桥插件");
        if (string.IsNullOrWhiteSpace(plugin)) plugin = "siyuan-speed-switch";

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        var op = SafeContext.GetString(context, "载荷op");
        if (string.IsNullOrWhiteSpace(op))
        {
            op = isCmd ? "commands.run" : "bridge.ping";
        }

        if (string.IsNullOrWhiteSpace(argsJson)) argsJson = "{}";

        // 策略 1: 内核同步路由快路径（针对 KernelOps）
        if (KernelOps.Contains(op))
        {
            var kBody = "{\"op\":\"" + op + "\",\"args\":" + argsJson.Trim() + "}";
            var kResp = SyKernelRequest.DoRequest(syUrl, token, "/plugin/private/" + plugin + "/exec", kBody);
            if (kResp.Success)
            {
                var kStatus = ExtractJsonStringField(kResp.Text, "status");
                if (kStatus == "recorded" || kStatus == "duplicate")
                {
                    Ok(context, ExtractJsonStringField(kResp.Text, "message") ?? "完成", kResp.Text);
                    return;
                }
            }
        }

        // 策略 2: 调用标准的 LV·发命令 与 LV·取回执 子程序逻辑
        var cmdRes = LvSendCommand.Send(syUrl, token, op, argsJson, plugin);

        if (!cmdRes.Success)
        {
            Fail(context, "发送命令失败: " + cmdRes.Error);
            return;
        }

        // 动态等待预算
        var budgetMs = (op == "commands.run") ? 35000 : 8000;
        var rec = LvGetReceipt.Poll(syUrl, token, cmdRes.CommandId, budgetMs, plugin);

        if (rec.Status == "recorded" || rec.Status == "duplicate")
        {
            Ok(context, string.IsNullOrEmpty(rec.Message) ? "操作成功完成" : rec.Message, rec.DataJson);
        }
        else
        {
            Fail(context, "命令 [" + rec.Status + "]：" + rec.Message);
        }
    }

    /// <summary>
    /// 占位符解析字典
    /// </summary>
    public static string ResolvePlaceholders(string text, IStepContext context)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var now = DateTime.Now;

        return text
            .Replace("{日记笔记本ID}", SafeContext.GetString(context, "日记笔记本ID"))
            .Replace("{收集箱文档ID}", SafeContext.GetString(context, "收集箱文档ID"))
            .Replace("{日期时间}", now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{今天}", now.ToString("yyyy-MM-dd"))
            .Replace("{时间}", now.ToString("HH:mm"))
            .Replace("{选中文本}", SafeContext.GetString(context, "选中文本"))
            .Replace("{来源窗口}", SafeContext.GetString(context, "来源窗口"))
            .Replace("{选中块ID}", SafeContext.GetString(context, "选中块ID"))
            .Replace("{clipboard}", SafeContext.GetString(context, "剪贴板文本"));
    }

    private static void Ok(IStepContext context, string msg, string data)
    {
        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "消息", msg);
        SafeContext.Set(context, "结果数据", data ?? "");
    }

    private static void Fail(IStepContext context, string msg)
    {
        SafeContext.Set(context, "是否成功", false);
        SafeContext.Set(context, "消息", msg);
        SafeContext.Set(context, "结果数据", "");
    }

    private static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string ExtractJsonStringField(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
}



// ============================================================================
// L0-01: 思源超级全局面板 v3.0 —— 主驱动程序 (SuperPanelMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5 / 零外部第三方依赖）
// 核心升级：
//   1. 跨软件一键唤起任意插件（小驴全家桶 9 大插件 + 动态发现全部第三方已装插件）：
//      - Win32 user32.dll 精准唤醒并前置思源窗口（支持后台/最小化/托盘唤醒）
//      - 毫秒级快门 commands.run / home.open / exam.open 直调插件面板（免 30s 确认框）
//      - focusOnly 焦点型命令在思源前置后自动转译并投递思源快捷键
//   2. 面板预热快照 (.runtime-state.js，零 Token 落盘)：
//      - 彻底消除 file:// 页面裸调内核 HTTP 401 与「统计中...」挂死
//      - 动态向 panel.html 注入实时已装插件与全部命令目录，新装插件自动出现在面板中
//   3. 修复全部保留直达指令（syd/syzg/sytb/sysz/syqd）与两阶段 YAML 执行链路
// ============================================================================

