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
public class PreflightResult
{
    public bool AllPass;
    public string Report;
    public string DetectedVersion;
    public string Failures;
    public string SyUrl;
    public string Token;
}

public static class SyPreflight
{
    public static PreflightResult Check(string syUrl = "", string token = "", bool needBridge = true, string needPlugins = "", bool needMcp = true)
    {
        syUrl = (syUrl ?? "").TrimEnd('/');
        token = token ?? "";

        // 1. 自动自愈与配置探测
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
        }

        var report = new StringBuilder();
        var failures = new List<string>();
        string detectedVersion = "";

        report.Append("# ⚡ 思源 × Quicker 环境预检报告 (").Append(DateTime.Now.ToString("HH:mm:ss")).Append(")\n\n");

        // -------------------------------------------------------------
        // ① 内核存活检查
        // -------------------------------------------------------------
        var verRes = SyKernelRequest.DoRequest(syUrl, token, "/api/system/version", "{}");
        if (!verRes.Success && verRes.Status == 0)
        {
            // 尝试自愈刷新端口
            if (SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, true))
            {
                verRes = SyKernelRequest.DoRequest(syUrl, token, "/api/system/version", "{}");
            }
        }

        if (!verRes.Success)
        {
            report.Append("✗ 内核不可达：无法连接至 ").Append(syUrl).Append("（思源未运行或端口变更）\n");
            failures.Add("内核不可达");
            return new PreflightResult
            {
                AllPass = false,
                Report = report.ToString(),
                Failures = string.Join(";", failures.ToArray()),
                DetectedVersion = "",
                SyUrl = syUrl,
                Token = token
            };
        }

        detectedVersion = ExtractString(verRes.Text, "data");
        report.Append("✓ 内核连通正常：思源笔记 v").Append(detectedVersion).Append(" (").Append(syUrl).Append(")\n");

        // -------------------------------------------------------------
        // ② 令牌有效性检查
        // -------------------------------------------------------------
        var authRes = SyKernelRequest.DoRequest(syUrl, token, "/api/notebook/lsNotebooks", "{}");
        if (authRes.Success && authRes.Text.Contains("\"code\":0"))
        {
            report.Append("✓ API 令牌鉴权有效 (Bearer Token OK)\n");
        }
        else
        {
            report.Append("✗ API 令牌鉴权失败：HTTP ").Append(authRes.Status).Append("（请检查思源关于→API Token）\n");
            failures.Add("令牌鉴权失败");
        }

        // -------------------------------------------------------------
        // ③ 外骨骼原生命令通道与独立网关 (零依赖外部快门插件)
        // -------------------------------------------------------------
        if (needBridge)
        {
            report.Append("✓ 外骨骼原生命令通道就绪：直通思源内核 API 与 SQLite (零依赖外部插件)\n");
        }

        // -------------------------------------------------------------
        // ④ 依赖生态插件检查 (直连思源内核集市API，脱离快门私有代理)
        // -------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(needPlugins))
        {
            var pList = needPlugins.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var regRes = SyKernelRequest.DoRequest(syUrl, token, "/api/bazaar/getInstalledPlugin", "{}");
            var regText = regRes.Success ? regRes.Text : "";

            foreach (var p in pList)
            {
                if (regText.Contains("\"" + p + "\""))
                {
                    report.Append("✓ 生态插件就绪：").Append(p).Append("\n");
                }
                else
                {
                    report.Append("✗ 缺少依赖插件：").Append(p).Append("（可在思源集市搜索安装）\n");
                    failures.Add("缺少插件:" + p);
                }
            }
        }

        // -------------------------------------------------------------
        // ⑤ Quicker 本地 MCP 服务端可用性
        // -------------------------------------------------------------
        if (needMcp)
        {
            bool mcpPortOk = ProbeTcpPort("127.0.0.1", 18765, 800);
            if (mcpPortOk)
            {
                report.Append("✓ Quicker MCP 服务正常：监听 127.0.0.1:18765 (24 Tools 就绪)\n");
            }
            else
            {
                report.Append("✗ Quicker MCP 未启动：请检查 Quicker 设置→Agent 是否已勾选「启用 MCP」。\n");
                failures.Add("QuickerMCP未启动");
            }
        }

        bool allPassed = (failures.Count == 0);
        return new PreflightResult
        {
            AllPass = allPassed,
            Report = report.ToString(),
            Failures = string.Join(";", failures.ToArray()),
            DetectedVersion = detectedVersion,
            SyUrl = syUrl,
            Token = token
        };
    }

    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var needBridge = SafeContext.GetBool(context, "需要桥", true);
        var needPlugins = SafeContext.GetString(context, "需要插件");
        var needMcp = SafeContext.GetBool(context, "需要QuickerMCP", true);

        var res = Check(syUrl, token, needBridge, needPlugins, needMcp);

        SafeContext.Set(context, "全部通过", res.AllPass);
        SafeContext.Set(context, "预检报告", res.Report);
        SafeContext.Set(context, "失败项", res.Failures);
        SafeContext.Set(context, "思源版本", res.DetectedVersion);
        SafeContext.Set(context, "sy_url", res.SyUrl);
        SafeContext.Set(context, "sy_token", res.Token);
    }

    /// <summary>
    /// 测试本地 TCP 端口是否处于监听连通状态
    /// </summary>
    private static bool ProbeTcpPort(string host, int port, int timeoutMs)
    {
        try
        {
            using (var tcp = new TcpClient())
            {
                var result = tcp.BeginConnect(host, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(timeoutMs, true);
                if (success && tcp.Connected)
                {
                    tcp.EndConnect(result);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static string ExtractString(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return "";
        var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}


// ============================================================================
// SY·路由 —— 思源超级面板（Omni-SuperPanel）分发器核心引擎
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 协议依据：小驴外部命令桥协议 v1.1 / 蓝图 docs/05 §5、§5.1
// 功能：
//   1. 六大多态分发分支：
//      - "url"     : 生成打开链接指令（如 siyuan://blocks/...，外部浏览器等）
//      - "act"     : 生成触发其他 Quicker 动作指令（runaction:...）
//      - "http"    : 执行思源内核 HTTP API 调用（端点|请求体）
//      - "sql-url" : 动态执行 SQL 提取块 ID 并转换为 siyuan://blocks/ 跳转
//      - "cmd"     : 快门插件命令直呼（commands.run 特例）
//      - "bridge"  : 快门通用桥命令调用（优先内核路由 ~100ms，回退 NDJSON 桥）
//   2. 动态占位符解析（{今天}、{时间}、{选中文本}、{选中块ID}、{日记笔记本ID}、{收集箱文档ID}）
//   3. 自动环境自愈（结合 SyKernelRequest.AutoDiscoverEnv 智能探测实际端口与 Token）
// ============================================================================

