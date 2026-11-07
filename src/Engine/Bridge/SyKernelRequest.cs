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
public static class SyKernelRequest
{
    // 进程级内存缓存（60 秒有效期）
    private static string _cachedUrl = "";
    private static string _cachedToken = "";
    private static int _lastCacheTicks = 0;
    private const int CacheTtlMs = 60000;

    static SyKernelRequest()
    {
        // 核心性能调优：提升本地并发连接上限并彻底禁用 100-continue 握手延迟 (降低 350ms 首包等待)
        try
        {
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
        }
        catch { }
    }

    public static void Exec(IStepContext context)
    {
        var url = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var endpoint = SafeContext.GetString(context, "端点");
        var body = SafeContext.GetString(context, "请求体");

        // 1. 若入参未指定，优先使用有效的内存缓存，缓存失效才重新探测
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(url))
        {
            if (!IsCacheValid())
            {
                AutoDiscoverEnv(ref url, ref token, false);
                UpdateCache(url, token);
            }
            else
            {
                url = _cachedUrl;
                token = _cachedToken;
            }

            SafeContext.Set(context, "sy_token", token);
            SafeContext.Set(context, "sy_url", url);
        }

        // 2. 发起 HTTP POST 请求
        var r = DoRequest(url, token, endpoint, body);

        // 3. 令牌自愈重试：若遭遇 401/403，清空缓存强制重新探测并重试一次
        if ((r.Status == 401 || r.Status == 403))
        {
            InvalidateCache();
            if (AutoDiscoverEnv(ref url, ref token, true))
            {
                UpdateCache(url, token);
                SafeContext.Set(context, "sy_token", token);
                SafeContext.Set(context, "sy_url", url);
                r = DoRequest(url, token, endpoint, body);
            }
        }

        // 4. 写回标准输出变量
        SafeContext.Set(context, "状态码", r.Status);
        SafeContext.Set(context, "文本结果", r.Text);
        SafeContext.Set(context, "是否成功", r.Success);

        if (r.Success)
        {
            SafeContext.Set(context, "错误提示", "");
            return;
        }

        string errMsg;
        if (r.Status == 401 || r.Status == 403)
        {
            errMsg = "思源 API 令牌无效：请打开思源「设置→关于→复制 API 令牌」，粘贴到动作配置 sy_token。";
        }
        else if (r.Status == 0)
        {
            errMsg = "思源未运行或无法连接（" + url + "）：请先启动思源笔记。";
        }
        else
        {
            errMsg = r.Error;
        }

        SafeContext.Set(context, "错误提示", errMsg);
    }

    private static bool IsCacheValid()
    {
        if (string.IsNullOrEmpty(_cachedUrl) || string.IsNullOrEmpty(_cachedToken)) return false;
        return (Environment.TickCount - _lastCacheTicks) < CacheTtlMs;
    }

    private static void UpdateCache(string url, string token)
    {
        _cachedUrl = url;
        _cachedToken = token;
        _lastCacheTicks = Environment.TickCount;
    }

    public static void InvalidateCache()
    {
        _cachedUrl = "";
        _cachedToken = "";
        _lastCacheTicks = 0;
    }

    /// <summary>
    /// 多源自动发现环境配置（现代化委托至 SiYuanClient）
    /// </summary>
    public static bool AutoDiscoverEnv(ref string url, ref string token, bool forceRefresh)
    {
        var conn = SiYuanClient.GetConnectionInfo(forceRefresh);
        if (!string.IsNullOrEmpty(conn.Url)) url = conn.Url;
        if (!string.IsNullOrEmpty(conn.Token)) token = conn.Token;
        return !string.IsNullOrEmpty(url);
    }

    /// <summary>
    /// 底层标准 HTTP POST 请求执行器（现代化高并发连接池驱动）
    /// </summary>
    public static KernelResult DoRequest(string url, string token, string endpoint, string body)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return new KernelResult { Status = 400, Text = "", Success = false, Error = "端点不能为空" };
        }
        try
        {
            if (string.IsNullOrEmpty(url))
            {
                var conn = SiYuanClient.GetConnectionInfo(false);
                url = conn.Url;
                if (string.IsNullOrEmpty(token)) token = conn.Token;
            }
            var res = SiYuanClient.Post(url, token, endpoint, body);
            return new KernelResult
            {
                Status = res.Success ? 200 : (res.Code == 0 ? 200 : res.Code),
                Text = res.RawText ?? "",
                Success = res.Success,
                Error = res.Error ?? ""
            };
        }
        catch (Exception ex)
        {
            return new KernelResult
            {
                Status = 0,
                Text = "",
                Success = false,
                Error = "请求异常：" + ex.Message
            };
        }
    }

    public struct KernelResult
    {
        public int Status;
        public string Text;
        public bool Success;
        public string Error;
    }
}


// ============================================================================
// LV·发命令 —— 向小驴快门命令桥投递命令信封
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 协议依据：小驴外部命令桥协议 v1.1 / docs/02 §2、§6
// 功能：
//   1. 自动生成标准幂等键 ID: qk-<yyyyMMdd-HHmmss>-<4位随机> 与 UTC ISO 时间戳
//   2. 组装标准命令信封，支持 ttlMs (过期时间)、reply (回执开关) 与 device (定向设备)
//   3. 双通道自适应加速：
//      - 优先尝试【广播快路径 v1.5】（POST /api/broadcast/postMessage 频道 qg-cmd，延迟 ~10-20ms）
//      - 降级回退【NDJSON 文件桥】（Multipart 表单 putFile 追加写入 commands.ndjson）
//   4. 严格保持 args JSON 原始字面量，不经多次序列化破坏精度或中文转义
//   5. 提供强类型 Send(...) 静态方法，供动作直接调用，零反射、零伪包装
// ============================================================================

