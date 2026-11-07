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
public class ReceiptResult
{
    public bool Success;
    public string Status;
    public string Message;
    public string DataJson;
    public int ElapsedMs;
}

public static class LvGetReceipt
{
    // 回执熔断器：桥未就绪时回执轮询注定超时。60 秒内出现过一次超时，则后续轮询
    // 降为 1.5s 快速失败（动作兜底路径几乎零等待）；任一次成功命中即自动复位。
    private static int _lastTimeoutTick = int.MinValue;
    private const int CircuitBreakerWindowMs = 60000;
    private const int FastFailBudgetMs = 1500;

    public static ReceiptResult Poll(string syUrl, string token, string targetId, int maxMs = 8000, string plugin = "siyuan-speed-switch")
    {
        if (string.IsNullOrWhiteSpace(plugin)) plugin = "siyuan-speed-switch";
        syUrl = (syUrl ?? "").TrimEnd('/');

        if (string.IsNullOrWhiteSpace(targetId))
        {
            return new ReceiptResult
            {
                Success = false,
                Status = "rejected",
                Message = "取回执错误：目标命令 ID 不能为空",
                DataJson = "{}",
                ElapsedMs = 0
            };
        }

        if (maxMs <= 0) maxMs = 8000;

        // 熔断判定：窗口期内曾超时 → 压缩本轮等待预算（仍保留最小等待，容忍桥恰好复活）
        var tickNow = Environment.TickCount;
        if (_lastTimeoutTick != int.MinValue && (tickNow - _lastTimeoutTick) < CircuitBreakerWindowMs)
        {
            if (maxMs > FastFailBudgetMs) maxMs = FastFailBudgetMs;
        }

        // 1. 兜底获取 URL 与 Token (利用 SyKernelRequest 内存级缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
        }

        var resultsPath = "/storage/petal/" + plugin + "/bridge/results.ndjson";
        var deadline = Environment.TickCount + maxMs;
        var intervalMs = 150; // 轮询间隔 150ms

        ReceiptMatch match = null;
        int notFoundCount = 0;

        // 2. 轮询循环
        while (Environment.TickCount < deadline)
        {
            var text = TryGetFile(syUrl, token, resultsPath);
            if (!string.IsNullOrEmpty(text))
            {
                notFoundCount = 0;
                match = FindReceiptInNdjson(text, targetId);
                if (match != null)
                {
                    break;
                }
            }
            else
            {
                notFoundCount++;
                // 桥插件未就绪或 results.ndjson 文件不存在：连续 2 次（~200ms）未命中即快速失败，
                // 绝不耗尽 1500~8000ms 阻塞后续的原生 REST API / SQLite 降级执行链路
                if (notFoundCount >= 2)
                {
                    break;
                }
            }

            Thread.Sleep(intervalMs);
        }

        // 3. 结果返回
        if (match != null)
        {
            _lastTimeoutTick = int.MinValue; // 复位熔断器
            bool isSuccess = (match.Status == "recorded" || match.Status == "duplicate");
            return new ReceiptResult
            {
                Success = isSuccess,
                Status = match.Status,
                Message = match.Message,
                DataJson = match.DataJson,
                ElapsedMs = match.ElapsedMs
            };
        }

        _lastTimeoutTick = Environment.TickCount; // 记录本次超时，触发熔断窗口
        return new ReceiptResult
        {
            Success = false,
            Status = "timeout",
            Message = "等待思源回执超时（已等待 " + (maxMs / 1000) + " 秒）。注意：思源可能仍在后台执行，请勿换新 ID 盲目重试。",
            DataJson = "{}",
            ElapsedMs = maxMs
        };
    }

    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var plugin = SafeContext.GetString(context, "桥插件", "siyuan-speed-switch");
        var targetId = SafeContext.GetString(context, "命令id");
        var maxMs = SafeContext.GetInt(context, "等待毫秒", 8000);

        var res = Poll(syUrl, token, targetId, maxMs, plugin);

        SafeContext.Set(context, "回执状态", res.Status);
        SafeContext.Set(context, "回执消息", res.Message);
        SafeContext.Set(context, "回挂数据", res.DataJson);
        SafeContext.Set(context, "执行耗时", res.ElapsedMs);
        SafeContext.Set(context, "是否成功", res.Success);
    }

    /// <summary>
    /// 读取指定 results.ndjson 文件文本
    /// </summary>
    private static string TryGetFile(string baseUrl, string token, string path)
    {
        try
        {
            var body = "{\"path\":\"" + path + "\"}";
            var r = SyKernelRequest.DoRequest(baseUrl, token, "/api/file/getFile", body);
            if (r.Success && !string.IsNullOrWhiteSpace(r.Text) && !r.Text.Contains("\"code\":"))
            {
                return r.Text;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 在 NDJSON 文本中截取尾部活跃窗口并逆向精准扫描目标 ID 的回执行
    /// </summary>
    public static ReceiptMatch FindReceiptInNdjson(string ndjsonText, string targetId)
    {
        if (string.IsNullOrEmpty(ndjsonText) || string.IsNullOrEmpty(targetId)) return null;

        // 优化：仅截取尾部 32KB（对于每行约 200 字节的回执，涵盖约 160 行活跃记录）
        const int tailWindowBytes = 32768;
        string searchWindow = ndjsonText;
        if (ndjsonText.Length > tailWindowBytes)
        {
            searchWindow = ndjsonText.Substring(ndjsonText.Length - tailWindowBytes);
        }

        // 按行分割并自底向上逆向搜索
        var lines = searchWindow.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var targetKey = "\"id\":\"" + targetId + "\"";

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            // 极速子串探测，不匹配跳过
            if (!line.Contains(targetKey)) continue;

            // 命中目标行，解析核心字段
            return ParseReceiptLine(line);
        }

        return null;
    }

    /// <summary>
    /// 解析单行 NDJSON 回执对象
    /// 典型格式：{"id":"qk-xxx","op":"xxx","status":"recorded","message":"xxx","elapsedMs":12,"data":{...}}
    /// </summary>
    public static ReceiptMatch ParseReceiptLine(string line)
    {
        try
        {
            var status = ExtractStringField(line, "status") ?? "unknown";
            var message = ExtractStringField(line, "message") ?? "";
            var elapsedStr = ExtractRawField(line, "elapsedMs");
            int elapsed = 0;
            if (!string.IsNullOrEmpty(elapsedStr)) int.TryParse(elapsedStr, out elapsed);

            var dataJson = ExtractObjectField(line, "data") ?? "{}";

            return new ReceiptMatch
            {
                Status = status,
                Message = message,
                DataJson = dataJson,
                ElapsedMs = elapsed
            };
        }
        catch
        {
            return new ReceiptMatch
            {
                Status = "recorded",
                Message = "回执命中（轻量解析模式）",
                DataJson = "{}",
                ElapsedMs = 0
            };
        }
    }

    private static string ExtractStringField(string json, string field)
    {
        var pattern = "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"";
        var m = Regex.Match(json, pattern);
        if (m.Success)
        {
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r");
        }
        return null;
    }

    private static string ExtractRawField(string json, string field)
    {
        var pattern = "\"" + field + "\"\\s*:\\s*([^,\\}\\s]+)";
        var m = Regex.Match(json, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string ExtractObjectField(string json, string field)
    {
        var prefix = "\"" + field + "\"\\s*:\\s*";
        var m = Regex.Match(json, prefix);
        if (!m.Success) return null;

        int start = m.Index + m.Length;
        if (start >= json.Length) return null;

        char firstChar = json[start];
        if (firstChar == '{' || firstChar == '[')
        {
            int depth = 0;
            char open = firstChar;
            char close = (firstChar == '{') ? '}' : ']';

            for (int i = start; i < json.Length; i++)
            {
                if (json[i] == open) depth++;
                else if (json[i] == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return json.Substring(start, i - start + 1);
                    }
                }
            }
        }
        else
        {
            // 简单标量或 null
            var endM = Regex.Match(json.Substring(start), "^(null|true|false|\\d+)");
            if (endM.Success) return endM.Value;
        }

        return "{}";
    }

    public class ReceiptMatch
    {
        public string Status;
        public string Message;
        public string DataJson;
        public int ElapsedMs;
    }
}


// ============================================================================
// SY·预检 —— 动作运行前能力探测、依赖检查与环境自动自愈
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 协议依据：小驴外部命令桥协议 v1.1 / 蓝图 docs/21 R151-06
// 核心功能：
//   1. 自动环境自愈（自动寻觅思源活动端口 2542/6806/6808 与 Token）
//   2. 检项 ①：思源内核存活与网络可达（/api/system/version）
//   3. 检项 ②：API 令牌鉴权有效性（/api/notebook/lsNotebooks）
//   4. 检项 ③：小驴雷切外部命令桥可用性（/plugin/private/siyuan-speed-switch/exec -> bridge.ping）
//   5. 检项 ④：生态依赖插件在装状态（registry.list 查询）
//   6. 检项 ⑤：Quicker 本地 MCP 服务端可用性（TCP 端口 18765 探针）
//   7. 提供强类型 Check(...) 静态方法，供动作直接调用，零反射、零伪包装
// ============================================================================

