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
public class SendCommandResult
{
    public bool Success;
    public string CommandId;
    public string Channel;
    public string Error;
}

public static class LvSendCommand
{
    public static SendCommandResult Send(string syUrl, string token, string op, string argsJson, string plugin = "siyuan-speed-switch", string channelPref = "auto", int ttl = 60000, bool reply = true, string device = "")
    {
        if (string.IsNullOrWhiteSpace(plugin)) plugin = "siyuan-speed-switch";
        if (string.IsNullOrWhiteSpace(argsJson)) argsJson = "{}";
        syUrl = (syUrl ?? "").TrimEnd('/');

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
        }

        var id = "qk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"v\":1,");
        sb.Append("\"id\":\"").Append(id).Append("\",");
        sb.Append("\"op\":\"").Append(op).Append("\",");
        sb.Append("\"args\":").Append(argsJson.Trim()).Append(",");
        sb.Append("\"createdAt\":\"").Append(createdAt).Append("\"");
        
        if (ttl > 0) sb.Append(",\"ttlMs\":").Append(ttl);
        if (!reply) sb.Append(",\"reply\":false");
        if (!string.IsNullOrWhiteSpace(device)) sb.Append(",\"device\":\"").Append(EscapeJsonString(device)).Append("\"");
        sb.Append("}");

        var envelopeJson = sb.ToString();
        bool sent = false;
        string usedChannel = "";
        string sendError = "";

        if ((channelPref == "auto" || channelPref == "fast") && (plugin == "siyuan-speed-switch" || plugin == "siyuan-quickgate"))
        {
            sent = TrySendBroadcast(syUrl, token, envelopeJson, out sendError);
            if (sent) usedChannel = "broadcast";
        }

        if (!sent)
        {
            sent = SendViaNdjsonBridge(syUrl, token, plugin, id, envelopeJson, out sendError);
            if (sent) usedChannel = "ndjson";
        }

        return new SendCommandResult
        {
            Success = sent,
            CommandId = id,
            Channel = usedChannel,
            Error = sent ? "" : sendError
        };
    }

    public static void Exec(IStepContext context)
    {
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var plugin = SafeContext.GetString(context, "桥插件", "siyuan-speed-switch");
        var op = SafeContext.GetString(context, "命令op");
        var argsJson = SafeContext.GetString(context, "命令参数", "{}");
        var ttl = SafeContext.GetInt(context, "可选ttl", 60000);
        var device = SafeContext.GetString(context, "目标设备");
        var reply = SafeContext.GetBool(context, "需要回执", true);
        var channelPref = SafeContext.GetString(context, "通道首选", "auto");

        var res = Send(syUrl, token, op, argsJson, plugin, channelPref, ttl, reply, device);

        SafeContext.Set(context, "命令id", res.CommandId);
        SafeContext.Set(context, "发送成功", res.Success);
        SafeContext.Set(context, "发送错误", res.Error);
        SafeContext.Set(context, "使用通道", res.Channel);
    }

    /// <summary>
    /// 尝试通过思源内核广播频道 postMessage 毫秒级投递
    /// 端点：POST /api/broadcast/postMessage { "channel": "qg-cmd", "message": "<envelopeJson>" }
    /// </summary>
    public static bool TrySendBroadcast(string baseUrl, string token, string envelopeJson, out string error)
    {
        error = "";
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/broadcast/postMessage");
            req.Method = "POST";
            req.ContentType = "application/json";
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers[HttpRequestHeader.Authorization] = "Token " + token;
            }
            req.Timeout = 4000;
            req.Proxy = null;

            var bodyJson = "{\"channel\":\"qg-cmd\",\"message\":" + JsonSerializeString(envelopeJson) + "}";
            var data = Encoding.UTF8.GetBytes(bodyJson);
            req.ContentLength = data.Length;

            using (var stream = req.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var respText = reader.ReadToEnd();
                if (resp.StatusCode == HttpStatusCode.OK && respText.Contains("\"code\":0"))
                {
                    // 防假成功关键修复：code:0 仅代表内核受理，data.channel.count 为 0 时表示
                    // 频道没有任何插件前端订阅，消息无人消费且永不产生回执——必须视为失败
                    // 并降级 NDJSON 文件桥，否则上层动作会在回执轮询上白白超时。
                    var countM = System.Text.RegularExpressions.Regex.Match(respText, "\"count\"\\s*:\\s*(\\d+)");
                    if (countM.Success)
                    {
                        int subscribers;
                        if (int.TryParse(countM.Groups[1].Value, out subscribers) && subscribers <= 0)
                        {
                            error = "广播频道无订阅者 (count=0)，雷切前端未就绪";
                            return false;
                        }
                        return true;
                    }
                    // 老版本内核响应缺 count 字段时保守放行（与历史行为一致）
                    return true;
                }
                error = "广播响应非 0: " + respText;
                return false;
            }
        }
        catch (Exception ex)
        {
            error = "广播投递异常: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 降级通过 NDJSON 文件桥投递（Multipart 表单 putFile 追加写入 commands.ndjson）
    /// 端点：POST /api/file/putFile
    /// </summary>
    public static bool SendViaNdjsonBridge(string baseUrl, string token, string plugin, string cmdId, string envelopeJson, out string error)
    {
        error = "";
        try
        {
            var path = "/storage/petal/" + plugin + "/bridge/commands.ndjson";
            var lineContent = envelopeJson.Replace("\r", "").Replace("\n", "") + "\n";
            var lineBytes = Encoding.UTF8.GetBytes(lineContent);

            var boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x");
            var boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
            var endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

            var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/file/putFile");
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers[HttpRequestHeader.Authorization] = "Token " + token;
            }
            req.Timeout = 6000;
            req.Proxy = null;

            using (var requestStream = req.GetRequestStream())
            {
                // 参数 1: path
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var pathHeader = "Content-Disposition: form-data; name=\"path\"\r\n\r\n" + path;
                var pathBytes = Encoding.UTF8.GetBytes(pathHeader);
                requestStream.Write(pathBytes, 0, pathBytes.Length);

                // 参数 2: isDir
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var isDirHeader = "Content-Disposition: form-data; name=\"isDir\"\r\n\r\nfalse";
                var isDirBytes = Encoding.UTF8.GetBytes(isDirHeader);
                requestStream.Write(isDirBytes, 0, isDirBytes.Length);

                // 参数 3: modTime
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var modTime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                var modHeader = "Content-Disposition: form-data; name=\"modTime\"\r\n\r\n" + modTime;
                var modBytes = Encoding.UTF8.GetBytes(modHeader);
                requestStream.Write(modBytes, 0, modBytes.Length);

                // 参数 4: file 二进制块
                requestStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                var fileHeader = "Content-Disposition: form-data; name=\"file\"; filename=\"commands.ndjson\"\r\nContent-Type: application/octet-stream\r\n\r\n";
                var fileHeaderBytes = Encoding.UTF8.GetBytes(fileHeader);
                requestStream.Write(fileHeaderBytes, 0, fileHeaderBytes.Length);

                requestStream.Write(lineBytes, 0, lineBytes.Length);

                // 结束边界
                requestStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var respText = reader.ReadToEnd();
                if (resp.StatusCode == HttpStatusCode.OK && respText.Contains("\"code\":0"))
                {
                    return true;
                }
                error = "putFile 写入异常：" + respText;
                return false;
            }
        }
        catch (Exception ex)
        {
            error = "文件桥写入异常: " + ex.Message;
            return false;
        }
    }

    private static string EscapeJsonString(string s)
    {
        return SafeContext.EscapeJson(s ?? "");
    }

    private static string JsonSerializeString(string s)
    {
        return "\"" + EscapeJsonString(s) + "\"";
    }
}


// ============================================================================
// LV·取回执 —— 轮询小驴快门回执文件 results.ndjson 并匹配指定命令 (尾部高效逆向扫描优化版)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 协议依据：小驴外部命令桥协议 v1.1 / docs/02 §3、§6
// 核心优化项：
//   1. 尾部窗口截取优化：直接截取 results.ndjson 尾部 32KB 文本，避免全文件海量字符串拆分
//   2. 逆向快速扫描：自底向上检索目标 ID，命中立即解析返回，平均耗时降至 <5ms
//   3. 坏行与多行格式容错：容忍空行、截断行与特殊字符转义
//   4. 严格防重复执行约束：超时返回 timeout，铁律：严禁更换新 ID 盲目重放请求
//   5. 提供强类型 Poll(...) 静态方法，供动作直接调用，零反射、零伪包装
// ============================================================================

