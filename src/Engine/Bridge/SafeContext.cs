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
public static class SafeContext
{
    public static object Get(IStepContext ctx, string key)
    {
        if (ctx == null || string.IsNullOrEmpty(key)) return null;
        try
        {
            return ctx.GetVarValue(key);
        }
        catch
        {
            return null;
        }
    }

    public static string GetString(IStepContext ctx, string key, string def = "")
    {
        var val = Get(ctx, key);
        return val != null ? val.ToString().Trim() : def;
    }

    public static bool GetBool(IStepContext ctx, string key, bool def = false)
    {
        var val = Get(ctx, key);
        if (val == null) return def;
        if (val is bool) return (bool)val;
        bool b;
        if (bool.TryParse(val.ToString().Trim(), out b)) return b;
        return def;
    }

    public static int GetInt(IStepContext ctx, string key, int def = 0)
    {
        var val = Get(ctx, key);
        if (val == null) return def;
        if (val is int) return (int)val;
        int i;
        if (int.TryParse(val.ToString().Trim(), out i)) return i;
        return def;
    }

    public static void Set(IStepContext ctx, string key, object val)
    {
        if (ctx == null || string.IsNullOrEmpty(key)) return;
        try
        {
            ctx.SetVarValue(key, val);
        }
        catch { }
    }

    /// <summary>
    /// 智能解析输入文本（输入文本 -> quicker_in_param 传入参数 -> 选中文本 -> 剪贴板文本）
    /// </summary>
    public static string GetInputText(IStepContext ctx)
    {
        var t1 = GetString(ctx, "输入文本");
        if (!string.IsNullOrEmpty(t1)) return t1;
        var t2 = GetString(ctx, "quicker_in_param");
        if (!string.IsNullOrEmpty(t2)) return t2;
        var t3 = GetString(ctx, "选中文本");
        if (!string.IsNullOrEmpty(t3)) return t3;
        var t4 = GetString(ctx, "剪贴板文本");
        if (!string.IsNullOrEmpty(t4)) return t4;
        return "";
    }

    /// <summary>
    /// 统一 JSON 字符串转义（全动作共享）：覆盖引号、反斜杠与全部 ASCII 控制字符，
    /// 保证含制表符/特殊空白的多行 Markdown 不会被内核以非法 JSON 拒收
    /// </summary>
    public static string EscapeJson(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 统一高保真 HTML 交互弹窗 URL 构造器（传递 Base64 UTF-8 状态参数至 quick-dialog.html）
    /// </summary>
    private const string EmbeddedGzQuickDialog = "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";

    private static void EnsureQuickDialogFile(string targetPath)
    {
        try
        {
            if (File.Exists(targetPath) && (DateTime.Now - File.GetLastWriteTime(targetPath)).TotalDays < 3) return;
            var localFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files", "quick-dialog.html");
            if (File.Exists(localFile)) { File.Copy(localFile, targetPath, true); return; }
            byte[] gzBytes = Convert.FromBase64String(EmbeddedGzQuickDialog);
            using (var ms = new MemoryStream(gzBytes))
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[4096];
                int read;
                while ((read = gz.Read(buffer, 0, buffer.Length)) > 0) fs.Write(buffer, 0, read);
            }
        }
        catch { }
    }

    /// <summary>
    /// HTML 交互弹窗 URL 构造器（自包含释放至 Temp，零本地绝对路径依赖）
    /// </summary>
    public static string BuildDialogUrl(string jsonState)
    {
        try
        {
            var tempHtml = Path.Combine(Path.GetTempPath(), "siyuan_quick_dialog.html");
            EnsureQuickDialogFile(tempHtml);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonState ?? "{}"));
            return "file:///" + tempHtml.Replace('\\', '/') + "#" + b64;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 日记笔记本自动发现（60 秒缓存）：动作未配置 日记笔记本ID 时按
    /// 「名称含 日记/daily/journal 优先 → 首个未关闭笔记本 → 首个笔记本」解析，
    /// 兜住此前空笔记本写入被内核拒绝却报成功的静默假成功问题。
    /// </summary>
    private static string _cachedDiaryNb = "";
    private static int _diaryNbTick = int.MinValue;

    public static string GetDiaryNotebookId(IStepContext ctx, string syUrl, string token)
    {
        var declared = GetString(ctx, "日记笔记本ID");
        if (!string.IsNullOrEmpty(declared)) return declared;

        var now = Environment.TickCount;
        if (_diaryNbTick != int.MinValue && (now - _diaryNbTick) < 60000 && !string.IsNullOrEmpty(_cachedDiaryNb))
        {
            return _cachedDiaryNb;
        }

        var found = "";
        try
        {
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/notebook/lsNotebooks", "{}");
            if (r.Success && !string.IsNullOrEmpty(r.Text))
            {
                var dict = JsonHelper.ParseDict(r.Text);
                var data = JsonHelper.GetDict(dict, "data");
                var nbList = JsonHelper.GetList(data, "notebooks");
                if (nbList != null)
                {
                    string firstOpen = "", firstAny = "";
                    foreach (var item in nbList)
                    {
                        var nb = item as Dictionary<string, object>;
                        if (nb == null) continue;
                        bool closed = JsonHelper.GetBool(nb, "closed", false);
                        string id = JsonHelper.GetString(nb, "id");
                        string name = JsonHelper.GetString(nb, "name");
                        if (string.IsNullOrEmpty(id)) continue;
                        if (string.IsNullOrEmpty(firstAny)) firstAny = id;
                        if (!closed && string.IsNullOrEmpty(firstOpen)) firstOpen = id;
                        if (closed) continue;

                        var lowerName = name.ToLowerInvariant();
                        if (string.IsNullOrEmpty(found) && !string.IsNullOrEmpty(name) &&
                            (name.Contains("日记") || lowerName.Contains("dailynote") || lowerName.Contains("daily") || lowerName.Contains("diary") || lowerName.Contains("journal")))
                        {
                            found = id;
                        }
                    }
                    if (string.IsNullOrEmpty(found)) found = string.IsNullOrEmpty(firstOpen) ? firstAny : firstOpen;
                }
            }
        }
        catch { }

        _cachedDiaryNb = found;
        _diaryNbTick = now;
        return found;
    }

    private static string ExtractJsonString(string json, string field)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return "";
        try
        {
            var dict = JsonHelper.ParseDict(json);
            var val = JsonHelper.GetString(dict, field);
            if (!string.IsNullOrEmpty(val)) return val;
        }
        catch { }
        var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}
