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
public static class SmartCaptureMain
{
    private static string _lastContinuationBlockId = "";
    private static string _lastContinuationContent = "";
    private static string _lastContinuationSource = "";
    private static int _lastContinuationTick = int.MinValue;

    /// <summary>
    /// 检查当前是否处于连续记录粘滞合并窗口中（借鉴并升级叶归一键记事的核心长处）
    /// </summary>
    public static bool IsWithinContinuationWindow(out int remainingSec)
    {
        return IsWithinContinuationWindow("", out remainingSec);
    }

    public static bool IsWithinContinuationWindow(string currentSource, out int remainingSec)
    {
        remainingSec = 0;
        int winSec = XiaoLv.Desktop.Core.AppSettings.Instance.QuickNoteContinuationSec;
        if (winSec <= 0 || string.IsNullOrEmpty(_lastContinuationBlockId) || _lastContinuationTick == int.MinValue)
            return false;

        // 如果用户切换了完全不同的前台外部应用或文档，视为新上下文，不盲目合并前块
        if (!string.IsNullOrEmpty(currentSource) && !string.IsNullOrEmpty(_lastContinuationSource) && currentSource != _lastContinuationSource)
            return false;

        int elapsed = (Environment.TickCount - _lastContinuationTick) / 1000;
        if (elapsed >= 0 && elapsed < winSec)
        {
            remainingSec = winSec - elapsed;
            return true;
        }
        return false;
    }

    private static string ExtractCreatedBlockId(string json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        try
        {
            var dict = JsonHelper.ParseDict(json);
            var dataList = JsonHelper.GetList(dict, "data");
            if (dataList != null && dataList.Count > 0)
            {
                var fObj = dataList[0] as Dictionary<string, object>;
                if (fObj != null)
                {
                    var doOps = JsonHelper.GetList(fObj, "doOperations");
                    if (doOps != null && doOps.Count > 0)
                    {
                        var op0 = doOps[0] as Dictionary<string, object>;
                        if (op0 != null)
                        {
                            var id = JsonHelper.GetString(op0, "id");
                            if (!string.IsNullOrEmpty(id)) return id;
                        }
                    }
                    var directId = JsonHelper.GetString(fObj, "id");
                    if (!string.IsNullOrEmpty(directId)) return directId;
                }
            }
        }
        catch { }
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "桌面应用");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");

        // 1. 智能输入内容解析（输入文本 -> quicker_in_param -> 选中文本 -> 剪贴板文本）
        var text = SafeContext.GetInputText(context);
        if (string.IsNullOrWhiteSpace(text))
        {
            var json = "{\"title\":\"思源外骨骼 · 极速前缀智能捕获\"," +
                       "\"subtitle\":\"智能识别前缀分流：@人名、#项目、!待办、?闪卡 (未装插件原生兜底)\"," +
                       "\"badge\":\"智能速记\"," +
                       "\"icon\":\"⚡\"," +
                       "\"mode\":\"input\"," +
                       "\"outputVar\":\"输入文本\"," +
                       "\"placeholder\":\"输入内容，如: @张三 方案评审、!周五前发报告、?何谓闭包//函数与环境绑定、#Q4规划...\"," +
                       "\"defaultValue\":\"\"," +
                       "\"tags\":[\"@人名\", \"!待办\", \"?闪卡\", \"#项目\"]}";

            SafeContext.Set(context, "dialog_url", SafeContext.BuildDialogUrl(json));
            SafeContext.Set(context, "需要弹窗输入", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 2. 自动自愈环境探测 (利用 60s 内存缓存)
        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }

        Logger.Info("SmartCaptureMain text='" + text + "', syUrl='" + syUrl + "', token='" + (string.IsNullOrEmpty(token) ? "empty" : "present") + "'");

        if (string.IsNullOrWhiteSpace(syUrl))
        {
            XiaoLv.Desktop.Core.OfflineQueueManager.EnqueueNote(text, windowTitle);
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 思源未启动，速记已安全进入离岸队列，思源启动后自动补录入库！");
            return;
        }

        // 快速离线探针：若内核无法连通，瞬时降级存入离岸队列 (零等待快速通道)
        var kernelProbe = SyKernelRequest.DoRequest(syUrl, token, "/api/system/version", "{}");
        if (!kernelProbe.Success)
        {
            XiaoLv.Desktop.Core.OfflineQueueManager.EnqueueNote(text, windowTitle);
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 思源未就绪或暂时离线，笔记已自动存入离岸安全队列，上线后自动同步！");
            return;
        }

        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);
        if (string.IsNullOrEmpty(diaryNotebookId))
        {
            XiaoLv.Desktop.Core.OfflineQueueManager.EnqueueNote(text, windowTitle);
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 思源未就绪或暂时不可达，笔记已自动存入离线安全队列，恢复后自动同步！");
            return;
        }

        var now = DateTime.Now;
        var timeStr = now.ToString("HH:mm");
        var dateStr = now.ToString("yyyy-MM-dd");

        // 支持反斜杠转义换行字符还原
        if (text.Contains("\\n") && !text.Contains("\n"))
        {
            text = text.Replace("\\n", "\n");
        }

        // 简码扩词与动态占位符变量自动求值展开 (如 :sig, :date, :time, {{now}}, {{clipboard}})
        if (text.StartsWith(":") || text.StartsWith("："))
        {
            text = SnippetExpansionEngine.Expand(text);
        }
        else if (text.Contains("{{"))
        {
            text = SnippetExpansionEngine.ResolveVariables(text);
        }

        // 智能前缀自适应归一化 (兼容中文关键字与英文缩写)
        string rawTrim = text.Trim();
        if (rawTrim.StartsWith("待办：") || rawTrim.StartsWith("待办:") || rawTrim.StartsWith("todo:", StringComparison.OrdinalIgnoreCase))
        {
            text = "!" + rawTrim.Substring(rawTrim.IndexOfAny(new char[] { '：', ':' }) + 1).Trim();
        }
        else if (rawTrim.StartsWith("闪卡：") || rawTrim.StartsWith("闪卡:") || rawTrim.StartsWith("card:", StringComparison.OrdinalIgnoreCase))
        {
            text = "?" + rawTrim.Substring(rawTrim.IndexOfAny(new char[] { '：', ':' }) + 1).Trim();
        }
        else if (rawTrim.StartsWith("人脉：") || rawTrim.StartsWith("人脉:"))
        {
            text = "@" + rawTrim.Substring(rawTrim.IndexOfAny(new char[] { '：', ':' }) + 1).Trim();
        }
        else if (rawTrim.StartsWith("项目：") || rawTrim.StartsWith("项目:"))
        {
            text = "#" + rawTrim.Substring(rawTrim.IndexOfAny(new char[] { '：', ':' }) + 1).Trim();
        }

        // 支持 >>小节名 或 》》小节名 动态内联指定日记追加标题
        string dynamicHeading = "";
        if (rawTrim.StartsWith(">>") || rawTrim.StartsWith("》》"))
        {
            var mH = Regex.Match(rawTrim, "^(>>|》》)([^\\s：:]+)[\\s：:]*(.*)$");
            if (mH.Success)
            {
                dynamicHeading = mH.Groups[2].Value.Trim();
                text = mH.Groups[3].Value.Trim();
                rawTrim = text;
            }
        }

        // 支持 >@文档名 或 doc:文档名 动态跨文档精准直投 (无需改全局设置)
        string explicitTargetDoc = "";
        if (rawTrim.StartsWith(">@") || rawTrim.StartsWith("》@") || rawTrim.StartsWith(">>@") || rawTrim.StartsWith("》》@") || rawTrim.StartsWith("doc:", StringComparison.OrdinalIgnoreCase))
        {
            var mDoc = Regex.Match(rawTrim, @"^(?:>>@|》》@|>@|》@|doc:)\s*([^\s：:]+)[\s：:]*(.*)$", RegexOptions.IgnoreCase);
            if (mDoc.Success)
            {
                explicitTargetDoc = mDoc.Groups[1].Value.Trim();
                text = mDoc.Groups[2].Value.Trim();
                rawTrim = text;
            }
        }

        // 多行草稿微舱 (Quick Scratchpad) 批量意图智能解包独立分流归档
        if (text.Contains("\n") &&
            !rawTrim.StartsWith("```") &&
            !rawTrim.StartsWith("$$") &&
            !(rawTrim.StartsWith("|") && rawTrim.Contains("\n|")) &&
            !rawTrim.StartsWith("---") &&
            !Regex.IsMatch(rawTrim, @"^#{1,6}\s") &&
            string.IsNullOrEmpty(dynamicHeading) &&
            string.IsNullOrEmpty(explicitTargetDoc))
        {
            var rawLines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawLines.Length > 1)
            {
                bool hasIntent = false;
                foreach (var l in rawLines)
                {
                    var lt = l.TrimStart();
                    if (lt.StartsWith("!") || lt.StartsWith("！") || lt.StartsWith("?") || lt.StartsWith("？") ||
                        lt.StartsWith("¥") || lt.StartsWith("￥") || lt.StartsWith("~") || lt.StartsWith("+") ||
                        lt.StartsWith("@") || lt.StartsWith("＠") ||
                        ((lt.StartsWith("#") || lt.StartsWith("＃")) && !Regex.IsMatch(lt, @"^#{1,6}\s")))
                    {
                        hasIntent = true;
                        break;
                    }
                }

                if (hasIntent)
                {
                    int successLines = 0;
                    foreach (var l in rawLines)
                    {
                        var lineText = l.Trim();
                        if (string.IsNullOrEmpty(lineText)) continue;
                        var subCtx = new DesktopStepContext("SYS", lineText);
                        subCtx.SetVarValue("来源窗口", windowTitle);
                        Exec(subCtx);
                        if (SafeContext.GetBool(subCtx, "是否成功")) successLines++;
                    }

                    SafeContext.Set(context, "是否成功", successLines > 0);
                    SafeContext.Set(context, "需要通知", true);
                    SafeContext.Set(context, "通知反馈", "✓ 草稿微舱多行意图解包完成：已将 " + successLines + " 条记录独立分流归档！");
                    return;
                }
            }
        }

        // 检测并自动将本地图片上传至思源资产库并转换为 assets/ 相对路径
        try
        {
            var imgMatch = Regex.Match(text, @"!\[([^\]]*)\]\((([a-zA-Z]:[\\/][^)]+\.(png|jpg|jpeg|gif|webp|bmp)))\)", RegexOptions.IgnoreCase);
            if (imgMatch.Success)
            {
                var localPath = imgMatch.Groups[2].Value;
                if (File.Exists(localPath))
                {
                    string assetRelPath, err;
                    if (XiaoLv.Desktop.Core.FileDispatcher.UploadAsset(syUrl, token, localPath, out assetRelPath, out err))
                    {
                        text = text.Replace(localPath, assetRelPath);
                    }
                }
            }
            else if (text.Length < 260 && Regex.IsMatch(text.Trim(), @"^[a-zA-Z]:[\\/].*\.(png|jpg|jpeg|gif|webp|bmp)$", RegexOptions.IgnoreCase))
            {
                var localPath = text.Trim();
                if (File.Exists(localPath))
                {
                    string assetRelPath, err;
                    if (XiaoLv.Desktop.Core.FileDispatcher.UploadAsset(syUrl, token, localPath, out assetRelPath, out err))
                    {
                        text = "![" + Path.GetFileName(localPath) + "](" + assetRelPath + ")";
                    }
                }
            }

            // 外链防盗链图片自动本地落盘与上传 (支持微信公众号/知乎等易失效图床)
            var remoteImgMatches = Regex.Matches(text, @"!\[([^\]]*)\]\((https?://[^)\s]+)\)");
            foreach (Match rm in remoteImgMatches)
            {
                string rUrl = rm.Groups[2].Value;
                if (rUrl.Contains("mmbiz.qpic.cn") || rUrl.Contains("zhimg.com") || rUrl.Contains("biliimg.com") || rUrl.Contains("sinaimg.cn") || Regex.IsMatch(rUrl, @"\.(png|jpg|jpeg|webp|gif)", RegexOptions.IgnoreCase))
                {
                    string assetRelPath, err;
                    if (XiaoLv.Desktop.Core.FileDispatcher.CacheAndUploadRemoteImage(syUrl, token, rUrl, out assetRelPath, out err))
                    {
                        text = text.Replace(rUrl, assetRelPath);
                    }
                }
            }
        }
        catch { }

        // =============================================================
        // 动态跨文档精准直投 (>@文档名 或 doc:文档名，无需手动切窗找文档)
        // =============================================================
        if (!string.IsNullOrEmpty(explicitTargetDoc))
        {
            var safeDocName = explicitTargetDoc.Replace("'", "''");
            var docSql = "SELECT id, content FROM blocks WHERE type='d' AND content LIKE '%" + safeDocName + "%' ORDER BY updated DESC LIMIT 1";
            var docRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(docSql) + "\"}");
            string targetDocId = "";
            string targetDocTitle = explicitTargetDoc;
            if (docRes.Success && docRes.Text.Contains("\"id\":"))
            {
                var idMatch = Regex.Match(docRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (idMatch.Success) targetDocId = idMatch.Groups[1].Value;
                var tMatch = Regex.Match(docRes.Text, "\"content\"\\s*:\\s*\"([^\"]+)\"");
                if (tMatch.Success) targetDocTitle = tMatch.Groups[1].Value;
            }

            if (!string.IsNullOrEmpty(targetDocId))
            {
                string docSourceCitation = SafeContext.GetString(context, "来源窗口", windowTitle);
                string noteMd = "- " + timeStr + " " + text + (!string.IsNullOrEmpty(docSourceCitation) && docSourceCitation != "桌面应用" ? " *(来自 " + docSourceCitation + ")*" : "");
                var appendBody = "{\"parentID\":\"" + targetDocId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(noteMd) + "\"}";
                var appRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);
                if (appRes.Success) SoundPlayerHelper.PlayFeedback();

                SafeContext.Set(context, "是否成功", appRes.Success);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", appRes.Success ? ("✓ 已精准追加至文档【" + targetDocTitle + "】！") : ("❌ 写入文档失败: " + appRes.Error));
                return;
            }
            else
            {
                SafeContext.Set(context, "是否成功", false);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "⚠️ 库中未找到名称匹配【" + explicitTargetDoc + "】的文档");
                return;
            }
        }

        // =============================================================
        // 分支 A: '@人名' / '＠人名' 人脉互动分流 (增强: 小驴人脉 | 兜底: 原生双链 [[人名]])
        // =============================================================
        if (text.StartsWith("@") || text.StartsWith("＠"))
        {
            var match = Regex.Match(text, "^[@＠]([^\\s：:,，]+)[\\s：:,，]*(.*)$");
            var personName = match.Success ? match.Groups[1].Value.Trim() : "";
            var noteContent = match.Success ? match.Groups[2].Value.Trim() : "";

            if (!string.IsNullOrEmpty(personName))
            {
                bool pluginHandled = false;

                try
                {
                    var cmdArgs = "{\"names\":[\"" + SafeContext.EscapeJson(personName) + "\"],\"note\":\"" + SafeContext.EscapeJson(noteContent) + "\",\"place\":\"" + SafeContext.EscapeJson(windowTitle) + "\"}";
                    var sendRes = LvSendCommand.Send(syUrl, token, "contacts.interaction", cmdArgs, "siyuan-contacts", "fast");
                    if (sendRes.Success)
                    {
                        var rec = LvGetReceipt.Poll(syUrl, token, sendRes.CommandId, 2500, "siyuan-contacts");
                        pluginHandled = (rec.Status == "recorded" || rec.Status == "duplicate");
                    }
                }
                catch { pluginHandled = false; }

                if (pluginHandled)
                {
                    SafeContext.Set(context, "是否成功", true);
                    SafeContext.Set(context, "需要通知", true);
                    SafeContext.Set(context, "通知反馈", "✓ 已为人脉【" + personName + "】记录互动交集 (小驴人脉时间线)！");
                    return;
                }

                // 原生平滑降级：在今日日记以双链 [[人名]] 形式归流
                var fallbackDailyMd = "- " + timeStr + " 交流速记: [[" + personName + "]] " + noteContent + " (来自 " + windowTitle + ")";
                var fallbackBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(fallbackDailyMd) + "\"}";
                var rFallback = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", fallbackBody);

                SafeContext.Set(context, "是否成功", rFallback.Success);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", rFallback.Success 
                    ? "✓ 已归流至今日日记并生成 [[" + personName + "]] 双链 (未安装人脉插件自动原生兜底)" 
                    : "❌ 写入日记失败: " + rFallback.Error);
                return;
            }
        }

        // =============================================================
        // 分支 B: '!' 或 '!待办' 任务待办分流 (思源原生 - [ ] 待办 + 可选管家同步)
        // 排除 Markdown 图片语法 (![图片描述](路径))
        // =============================================================
        if ((text.StartsWith("!") || text.StartsWith("！")) && !text.StartsWith("![") && !text.StartsWith("！["))
        {
            var todoContent = text.TrimStart('!', '！', ' ');
            if (todoContent.StartsWith("待办")) todoContent = todoContent.Substring(2).TrimStart('：', ':', ' ');

            if (string.IsNullOrWhiteSpace(todoContent))
            {
                SafeContext.Set(context, "是否成功", false);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "❌ 待办内容为空，未写入日记。");
                return;
            }

            var mdTask = "- [ ] " + timeStr + " " + todoContent + " (来自 " + windowTitle + ")";
            var postBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(mdTask) + "\"}";

            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postBody);
            if (!r.Success && !string.IsNullOrEmpty(r.Error) && (r.Error.Contains("Access is denied") || r.Error.Contains("rename")))
            {
                Thread.Sleep(300);
                r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postBody);
            }

            if (r.Success)
            {
                try
                {
                    var memoArgs = "{\"title\":\"" + SafeContext.EscapeJson(todoContent) + "\",\"dueDate\":\"" + dateStr + "\"}";
                    LvSendCommand.Send(syUrl, token, "home.memo", memoArgs);
                }
                catch { }

                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 已加入今日日记待办清单！");
                return;
            }
            else
            {
                XiaoLv.Desktop.Core.OfflineQueueManager.EnqueueNote(text, windowTitle);
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 思源文件锁冲突，待办已安全转入离线队列自动补录！");
                return;
            }
        }

        // =============================================================
        // 分支 C: '?' 或 '?闪卡' 问答闪卡分流 (思源原生 FSRS custom-riff-decks Callout)
        // =============================================================
        if (text.StartsWith("?") || text.StartsWith("？"))
        {
            var cardRaw = text.TrimStart('?', '？', ' ');
            if (string.IsNullOrWhiteSpace(cardRaw))
            {
                SafeContext.Set(context, "是否成功", false);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "❌ 闪卡内容为空，未写入复习库。");
                return;
            }

            var splitIdx = cardRaw.IndexOf("//");
            if (splitIdx < 0) splitIdx = cardRaw.IndexOf("——");

            var question = splitIdx > 0 ? cardRaw.Substring(0, splitIdx).Trim() : cardRaw;
            var answer = splitIdx > 0 ? cardRaw.Substring(splitIdx + 2).Trim() : "(待补充答案)";

            question = question.TrimStart('?', '？', '#', ' ');
            if (question.StartsWith("🎴")) question = question.Substring("🎴".Length).TrimStart();

            var cardMd = "> 🎴 **" + question + "**\n>\n> " + answer.Replace("\n", "\n> ") + "\n> *溯源*: 摘自 " + windowTitle + " · " + dateStr + "\n{: custom-riff-decks=\"default\" custom-type=\"fsrs-card\" }\n";
            var postBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(cardMd) + "\"}";

            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postBody);
            if (!r.Success && !string.IsNullOrEmpty(r.Error) && (r.Error.Contains("Access is denied") || r.Error.Contains("rename")))
            {
                Thread.Sleep(300);
                r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", postBody);
            }

            SafeContext.Set(context, "是否成功", r.Success);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", r.Success ? "✓ 已生成原生 FSRS 问答闪卡并推入思源复习库！" : "❌ 闪卡入库失败: " + r.Error);
            return;
        }

        // =============================================================
        // 分支 D: '#项目' / '＃项目' 项目专属文档分流 (找到文档入专页，未找到则带 #{标签} 入日记)
        // 排除 Markdown 标题语法 (# 标题、## 标题、### 标题)
        // =============================================================
        if ((text.StartsWith("#") || text.StartsWith("＃")) && !text.StartsWith("##") && !text.StartsWith("# "))
        {
            var match = Regex.Match(text, "^[#＃]([^\\s：:,，]+)[\\s：:,，]*(.*)$");
            var projTag = match.Success ? match.Groups[1].Value.Trim() : "";
            var projContent = match.Success ? match.Groups[2].Value.Trim() : "";

            if (string.IsNullOrWhiteSpace(projTag))
            {
                SafeContext.Set(context, "是否成功", false);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "❌ 项目标签为空，未执行归流。");
                return;
            }

            var likeSafe = projTag.Replace("'", "''");
            var sql = "SELECT id FROM blocks WHERE type='d' AND content LIKE '%" + likeSafe + "%' ORDER BY updated DESC LIMIT 1";
            var sqlRes = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sql) + "\"}");

            var targetDocId = "";
            if (sqlRes.Success && sqlRes.Text.Contains("\"id\":"))
            {
                var idMatch = Regex.Match(sqlRes.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (idMatch.Success) targetDocId = idMatch.Groups[1].Value;
            }

            if (!string.IsNullOrEmpty(targetDocId))
            {
                var blockMd = "- " + dateStr + " " + timeStr + " " + projContent + " (摘自 " + windowTitle + ")";
                var appendBody = "{\"parentID\":\"" + targetDocId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(blockMd) + "\"}";
                var appRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);

                SafeContext.Set(context, "是否成功", appRes.Success);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", appRes.Success ? "✓ 已精准追加至项目文档【" + projTag + "】！" : "❌ 写入项目失败: " + appRes.Error);
                return;
            }
            else
            {
                var tagDailyMd = "- " + timeStr + " #" + projTag + " " + projContent + " (来自 " + windowTitle + ")";
                var tagBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(tagDailyMd) + "\"}";
                var tagRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", tagBody);

                SafeContext.Set(context, "是否成功", tagRes.Success);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", tagRes.Success 
                    ? "✓ 未找到「" + projTag + "」专页，已附带标签 #" + projTag + " 安全归入今日日记！" 
                    : "❌ 写入日记失败: " + tagRes.Error);
                return;
            }
        }

        // =============================================================
        // 分支 E: 'av:表名 内容' 或 '+表名 内容' 属性视图数据库单行直投
        // =============================================================
        if (text.StartsWith("av:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("av：", StringComparison.OrdinalIgnoreCase) || text.StartsWith("+") || text.StartsWith("＋"))
        {
            var avRaw = text.StartsWith("av:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("av：", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(3).Trim()
                : text.Substring(1).Trim();

            var spaceIdx = avRaw.IndexOfAny(new char[] { ' ', '：', ':' });
            string targetTableName = spaceIdx > 0 ? avRaw.Substring(0, spaceIdx).Trim() : avRaw;
            string rowContent = spaceIdx > 0 ? avRaw.Substring(spaceIdx + 1).Trim() : "";

            if (!string.IsNullOrEmpty(targetTableName))
            {
                string targetAvId = "";
                string resolvedName = targetTableName;

                try
                {
                    var searchRes = SyKernelRequest.DoRequest(syUrl, token, "/api/av/searchAttributeView", "{\"keyword\":\"" + SafeContext.EscapeJson(targetTableName) + "\"}");
                    if (searchRes.Success && !string.IsNullOrEmpty(searchRes.Text))
                    {
                        var sDict = JsonHelper.ParseDict(searchRes.Text);
                        var sData = JsonHelper.GetDict(sDict, "data");
                        if (sData != null)
                        {
                            var rList = JsonHelper.GetList(sData, "results");
                            if (rList != null && rList.Count > 0)
                            {
                                var firstTbl = rList[0] as Dictionary<string, object>;
                                if (firstTbl != null)
                                {
                                    targetAvId = JsonHelper.GetString(firstTbl, "avID");
                                    var n = JsonHelper.GetString(firstTbl, "avName");
                                    if (!string.IsNullOrEmpty(n)) resolvedName = n;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(targetAvId))
                {
                    string insertTitle = !string.IsNullOrEmpty(rowContent) ? rowContent : "新数据库记录 (" + timeStr + ")";
                    var insertRes = WebToAvDatabaseMain.InsertAvRow(syUrl, token, targetAvId, insertTitle);
                    if (insertRes.Success)
                    {
                        SafeContext.Set(context, "是否成功", true);
                        SafeContext.Set(context, "需要通知", true);
                        SafeContext.Set(context, "通知反馈", "✓ 已成功录入至属性视图【" + resolvedName + "】！");
                        return;
                    }
                }
            }
        }

        // =============================================================
        // 分支 F: 普通无前缀文本 -> 智能归流到今日日记（支持用户自定义小节与防碎块粘滞合并）
        // =============================================================
        var sourceCitation = SafeContext.GetString(context, "来源窗口", windowTitle);
        var sourceApp = SafeContext.GetString(context, "来源应用");
        var sourceUrl = SafeContext.GetString(context, "来源链接");
        var sourceTitle = SafeContext.GetString(context, "来源标题");

        var logEntry = LifelogParser.Parse(text, sourceCitation, now);

        // 1. 防碎块粘滞窗口续记合并（仅限通用灵感闪念、未显式指定 >>小节名、非结构化标题/模板且非独立 Lifelog 分流条目时才合并）
        int remSec;
        bool isImageArtifact = text.Contains("![") || text.Contains("assets/");
        bool isCategorizedLifelog = logEntry.Category != LifelogCategory.Note;
        bool isHeadingOrTemplate = text.TrimStart().StartsWith("#") || text.TrimStart().StartsWith("```") || text.TrimStart().StartsWith("---");
        if (string.IsNullOrEmpty(dynamicHeading) && !isCategorizedLifelog && !isImageArtifact && !isHeadingOrTemplate && IsWithinContinuationWindow(sourceCitation, out remSec) && !string.IsNullOrEmpty(_lastContinuationBlockId))
        {
            var updatedContent = _lastContinuationContent + "\n  - " + text;
            var updateBody = "{\"id\":\"" + _lastContinuationBlockId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(updatedContent) + "\"}";
            var updateRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/updateBlock", updateBody);
            if (!updateRes.Success && !string.IsNullOrEmpty(updateRes.Error) && (updateRes.Error.Contains("Access is denied") || updateRes.Error.Contains("rename")))
            {
                Thread.Sleep(300);
                updateRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/updateBlock", updateBody);
            }
            if (updateRes.Success)
            {
                SoundPlayerHelper.PlayFeedback();
                _lastContinuationContent = updatedContent;
                _lastContinuationTick = Environment.TickCount;
                _lastContinuationSource = sourceCitation;
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 已连续续记至前块 (粘滞合并 · 剩余 " + remSec + "s)");
                return;
            }
        }

        string dailyMd;
        if (text.StartsWith("#") || text.StartsWith("```") || text.StartsWith(">") || text.StartsWith("!["))
        {
            dailyMd = text + (!string.IsNullOrEmpty(sourceCitation) && sourceCitation != "桌面应用" ? "\n*-- 来自: " + sourceCitation + "*" : "");
        }
        else
        {
            dailyMd = logEntry.FormattedMarkdown;
        }

        var targetHeading = !string.IsNullOrEmpty(dynamicHeading) 
            ? dynamicHeading 
            : XiaoLv.Desktop.Core.AppSettings.Instance.DailyNoteTargetHeading;

        // 若用户未指定特定小节，且启用了 Lifelog 自动分流，则按语法自动导流至对应时序大纲小节
        if (string.IsNullOrEmpty(targetHeading) && XiaoLv.Desktop.Core.AppSettings.Instance.LifelogAutoSectionEnabled)
        {
            targetHeading = logEntry.SuggestedHeading;
        }

        string headingBlockId = "";

        if (!string.IsNullOrEmpty(targetHeading))
        {
            var todayStr = now.ToString("yyyyMMdd");
            var todayDash = now.ToString("yyyy-MM-dd");
            var docFilter = "(ial LIKE '%custom-dailynote-" + todayStr + "%' OR hpath LIKE '%" + todayDash + "%' OR content LIKE '%" + todayDash + "%')";
            var sqlH = "SELECT b.id FROM blocks b WHERE b.root_id IN (SELECT id FROM blocks WHERE type='d' AND " + docFilter + ") AND b.type='h' AND b.content LIKE '%" + targetHeading.Replace("'", "''") + "%' LIMIT 1";
            var rH = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sqlH) + "\"}");
            if (rH.Success && rH.Text.Contains("\"id\":"))
            {
                var mId = Regex.Match(rH.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (mId.Success) headingBlockId = mId.Groups[1].Value;
            }

            // 智能自愈：若指定小节在今日日记中尚不存在，自动为用户创建该标题，确保大纲层级结构永不丢失
            if (string.IsNullOrEmpty(headingBlockId))
            {
                var sqlDoc = "SELECT id FROM blocks WHERE type='d' AND " + docFilter + " ORDER BY updated DESC LIMIT 1";
                var rDoc = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sqlDoc) + "\"}");
                if (rDoc.Success && rDoc.Text.Contains("\"id\":"))
                {
                    var mDoc = Regex.Match(rDoc.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                    if (mDoc.Success)
                    {
                        string dailyDocId = mDoc.Groups[1].Value;
                        string headingData = "### " + targetHeading;
                        var rCreate = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", "{\"parentID\":\"" + dailyDocId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(headingData) + "\"}");
                        if (rCreate.Success)
                        {
                            var rH2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(sqlH) + "\"}");
                            if (rH2.Success && rH2.Text.Contains("\"id\":"))
                            {
                                var mId2 = Regex.Match(rH2.Text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                                if (mId2.Success) headingBlockId = mId2.Groups[1].Value;
                            }
                        }
                    }
                }
            }
        }

        bool appOk;
        string createdId = "";
        if (!string.IsNullOrEmpty(headingBlockId))
        {
            // 思源原生规则：标题块属于叶子节点，内容需以 previousID 通过 /api/block/insertBlock 挂载于标题正下方
            var insertBody = "{\"previousID\":\"" + headingBlockId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(dailyMd) + "\"}";
            var rApp = SyKernelRequest.DoRequest(syUrl, token, "/api/block/insertBlock", insertBody);
            if (!rApp.Success && !string.IsNullOrEmpty(rApp.Error) && (rApp.Error.Contains("sync") || rApp.Error.Contains("busy") || rApp.Error.Contains("locked") || rApp.Error.Contains("rename") || rApp.Error.Contains("Access is denied")))
            {
                Thread.Sleep(400);
                rApp = SyKernelRequest.DoRequest(syUrl, token, "/api/block/insertBlock", insertBody);
            }
            appOk = rApp.Success;
            if (appOk) createdId = ExtractCreatedBlockId(rApp.Text);
        }
        else
        {
            var dailyBody = "{\"notebook\":\"" + diaryNotebookId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(dailyMd) + "\"}";
            var defaultRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);
            if (!defaultRes.Success && !string.IsNullOrEmpty(defaultRes.Error) && (defaultRes.Error.Contains("Access is denied") || defaultRes.Error.Contains("rename")))
            {
                Thread.Sleep(300);
                defaultRes = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);
            }
            appOk = defaultRes.Success;
            if (appOk)
            {
                SoundPlayerHelper.PlayFeedback();
                createdId = ExtractCreatedBlockId(defaultRes.Text);
                if (string.IsNullOrEmpty(createdId) && !string.IsNullOrEmpty(text))
                {
                    try
                    {
                        var probeSql = "SELECT id FROM blocks WHERE content LIKE '%" + text.Substring(0, Math.Min(text.Length, 15)).Replace("'", "''") + "%' ORDER BY updated DESC LIMIT 1";
                        var rProbe = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", "{\"stmt\":\"" + SafeContext.EscapeJson(probeSql) + "\"}");
                        if (rProbe.Success) createdId = ExtractCreatedBlockId(rProbe.Text);
                    }
                    catch { }
                }
            }
        }

        if (!appOk)
        {
            XiaoLv.Desktop.Core.OfflineQueueManager.EnqueueNote(text, windowTitle);
            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ 思源未就绪或暂时不可达，笔记已自动存入离线安全队列，恢复后自动同步！");
            return;
        }

        if (!string.IsNullOrEmpty(createdId))
        {
            _lastContinuationBlockId = createdId;
            _lastContinuationContent = dailyMd;
            _lastContinuationSource = sourceCitation;
            _lastContinuationTick = Environment.TickCount;

            // 外部上下文深度注水与 Lifelog / 小驴舰队生态属性注入 (小驴管家 / 小驴打卡 / 小驴拾遗)
            try
            {
                var attrs = new Dictionary<string, string>();
                if (logEntry != null)
                {
                    attrs["custom-lifelog-type"] = logEntry.Category.ToString().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(logEntry.DueDate))
                    {
                        attrs["custom-completion-time"] = logEntry.DueDate;
                        attrs["custom-status"] = "todo";
                    }
                    if (!string.IsNullOrEmpty(logEntry.HabitItem))
                    {
                        attrs["custom-lv-checkin-item"] = logEntry.HabitItem;
                        attrs["custom-lv-checkin-date"] = now.ToString("yyyy-MM-dd");
                        attrs["custom-lv-checkin-val"] = !string.IsNullOrEmpty(logEntry.HabitVal) ? logEntry.HabitVal : "1";
                        if (!string.IsNullOrEmpty(logEntry.HabitUnit)) attrs["custom-lv-checkin-unit"] = logEntry.HabitUnit;
                    }
                }
                if (!string.IsNullOrEmpty(sourceApp)) attrs["custom-source-app"] = sourceApp;
                if (!string.IsNullOrEmpty(sourceUrl)) attrs["custom-source-url"] = sourceUrl;
                if (!string.IsNullOrEmpty(sourceTitle)) attrs["custom-source-title"] = sourceTitle;
                string projName = SafeContext.GetString(context, "项目名称");
                string activeFile = SafeContext.GetString(context, "活动文件");
                if (!string.IsNullOrEmpty(projName)) attrs["custom-project-name"] = projName;
                if (!string.IsNullOrEmpty(activeFile)) attrs["custom-active-file"] = activeFile;
                if (attrs.Count > 0)
                {
                    SyKernelRequest.DoRequest(syUrl, token, "/api/attr/setBlockAttrs", JsonHelper.ToJson(new { id = createdId, attrs = attrs }));
                }
            }
            catch { }
        }

        SafeContext.Set(context, "是否成功", true);
        SafeContext.Set(context, "需要通知", true);
        string catName = logEntry != null ? logEntry.CategoryLabel : "速记";
        string headingName = !string.IsNullOrEmpty(targetHeading) ? targetHeading : "时序流";
        SafeContext.Set(context, "通知反馈", "✓ 已记录【" + catName + "】至今日日记 (" + timeStr + " · " + headingName + ")");
    }

    /// <summary>
    /// 灵感 1 核心能力：0ms 网页极速剪藏卡 (Alt+Shift+G / g:)
    /// 免浏览器扩展，毫秒抓取前台网页标题与 URL，网站定制去噪，生成嵌套列表文献卡并联动小驴拾遗未读分拣
    /// </summary>
    public static void ExecuteWebClip(Action<string, string> notify)
    {
        ExecuteWebClip(notify, "", "card");
    }

    /// <summary>
    /// 灵感 1 核心能力：0ms 网页双态剪藏 (Alt+Shift+G / g: / 独立专页)
    /// 模式 A: 文献卡片流 (card) - 嵌套大纲存入日记/读库，自动防盗链图片落盘
    /// 模式 B: 独立研读专页 (doc) - 在读库中生成整篇独立研读笔记，生成文献看板
    /// 支持追加用户即时思考批注 (userAnnotation)
    /// </summary>
    public static void ExecuteWebClip(Action<string, string> notify, string userAnnotation, string clipMode)
    {
        if (string.IsNullOrEmpty(clipMode)) clipMode = "card";
        ThreadPool.QueueUserWorkItem(state =>
        {
            try
            {
                // 1. 毫秒级探测前台浏览器上下文 (优先使用呼出面板前的原始前台窗口句柄，杜绝自身窗口干扰)
                IntPtr prevHwnd = XiaoLv.Desktop.UI.BentoWindow.LastExternalForegroundHwnd;
                var snap = (prevHwnd != IntPtr.Zero)
                    ? ContextPerception.CaptureForegroundContext(prevHwnd)
                    : ContextPerception.CaptureForegroundContext();

                if (snap == null)
                {
                    if (notify != null) notify("思源外骨骼", "⚠️ 未能探测到活动窗口上下文");
                    return;
                }

                string url = TextCleaner.PurifyUrl(snap.Url);
                string rawTitle = !string.IsNullOrEmpty(snap.CleanTitle) ? snap.CleanTitle : snap.FullTitle;
                string title, author, siteName;
                TextCleaner.ParseSiteMetadata(url, rawTitle, out title, out author, out siteName);

                // 2. 抓取选中文本
                string selected = SelectionHelper.CaptureSelectedText(150);
                if (string.IsNullOrEmpty(selected) && ClipboardWatcher.IsEnabled && ClipboardWatcher.LatestItem != null)
                {
                    var clip = ClipboardWatcher.LatestItem;
                    if (!string.IsNullOrEmpty(clip.EnrichedMarkdown))
                    {
                        selected = clip.EnrichedMarkdown;
                    }
                }
                else if (!string.IsNullOrEmpty(selected))
                {
                    // 若选区包含超链接/加粗等富文本，优先通过 HTML 提取高保真 Markdown (对标 Obsidian Web Clipper)
                    try
                    {
                        if (System.Windows.Forms.Clipboard.ContainsText(System.Windows.Forms.TextDataFormat.Html))
                        {
                            string rawHtml = System.Windows.Forms.Clipboard.GetText(System.Windows.Forms.TextDataFormat.Html);
                            if (!string.IsNullOrEmpty(rawHtml) && (rawHtml.Contains("<a ") || rawHtml.Contains("<strong>") || rawHtml.Contains("<b>") || rawHtml.Contains("<img ")))
                            {
                                string richMd = TextCleaner.ConvertHtmlToMarkdown(rawHtml);
                                if (!string.IsNullOrEmpty(richMd) && richMd.Length >= 2)
                                {
                                    selected = richMd;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 3. 网站定制化去噪与清洗 (微信/知乎/链滴/微博/小红书/B站)
                if (!string.IsNullOrEmpty(selected))
                {
                    selected = TextCleaner.CleanSiteSpecific(selected, url, out siteName);
                    selected = TextCleaner.RemoveWebGarbageTail(selected);
                    selected = TextCleaner.EnrichCodeBlocks(selected);
                }

                // 4. 环境连接自愈
                string syUrl = "";
                string token = "";
                SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
                if (string.IsNullOrEmpty(syUrl))
                {
                    if (notify != null) notify("思源外骨骼", "❌ 未能连接思源内核");
                    return;
                }

                // 外链防盗链图片自动本地落盘与上传至 /data/assets/ (防微信/知乎图床失效)
                if (!string.IsNullOrEmpty(selected) && selected.Contains("http"))
                {
                    var remoteImgMatches = Regex.Matches(selected, @"!\[([^\]]*)\]\((https?://[^)\s]+)\)");
                    foreach (Match rm in remoteImgMatches)
                    {
                        string rUrl = rm.Groups[2].Value;
                        if (rUrl.Contains("mmbiz.qpic.cn") || rUrl.Contains("zhimg.com") || rUrl.Contains("biliimg.com") || rUrl.Contains("sinaimg.cn") || Regex.IsMatch(rUrl, @"\.(png|jpg|jpeg|webp|gif)", RegexOptions.IgnoreCase))
                        {
                            string assetRelPath, err;
                            if (XiaoLv.Desktop.Core.FileDispatcher.CacheAndUploadRemoteImage(syUrl, token, rUrl, out assetRelPath, out err))
                            {
                                selected = selected.Replace(rUrl, assetRelPath);
                            }
                        }
                    }
                }

                string timeStr = DateTime.Now.ToString("HH:mm");
                string shortTitle = title.Length > 24 ? title.Substring(0, 24) + "..." : title;

                // =============================================================
                // 模式分支 B: 另存为独立研读专页 (Full Article Doc Mode)
                // 对齐 Obsidian Web Clipper / Cubox / Readwise 独立研读专页模式
                // =============================================================
                if (clipMode.Equals("doc", StringComparison.OrdinalIgnoreCase))
                {
                    string safeDocTitle = title.Replace("/", "／").Replace("\\", "＼").Replace(":", "：").Replace("*", "").Replace("?", "？").Trim();
                    if (safeDocTitle.Length > 50) safeDocTitle = safeDocTitle.Substring(0, 50);
                    if (string.IsNullOrEmpty(safeDocTitle)) safeDocTitle = "研读笔记_" + DateTime.Now.ToString("yyyyMMdd_HHmm");

                    string fullMd = TextCleaner.BuildFullArticleMarkdown(title, url, selected, siteName, author, userAnnotation);
                    string targetNbId = SafeContext.GetDiaryNotebookId(null, syUrl, token);

                    string rawFolder = AppSettings.Instance.WebClipTargetFolder;
                    if (string.IsNullOrEmpty(rawFolder)) rawFolder = "/读库/";
                    rawFolder = rawFolder.Trim().Replace("\\", "/");
                    if (!rawFolder.StartsWith("/")) rawFolder = "/" + rawFolder;
                    if (!rawFolder.EndsWith("/")) rawFolder = rawFolder + "/";

                    string docPath = rawFolder + safeDocTitle;
                    var createReq = "{\"notebook\":\"" + SafeContext.EscapeJson(targetNbId) + "\",\"path\":\"" + SafeContext.EscapeJson(docPath) + "\",\"markdown\":\"" + SafeContext.EscapeJson(fullMd) + "\"}";
                    var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDocWithMd", createReq);
                    if (!createRes.Success)
                    {
                        // 冲突自愈 1: 尝试在标题后追加时间戳防重名覆盖
                        string dedupDocTitle = safeDocTitle + "_" + DateTime.Now.ToString("HHmmss");
                        docPath = rawFolder + dedupDocTitle;
                        createReq = "{\"notebook\":\"" + SafeContext.EscapeJson(targetNbId) + "\",\"path\":\"" + SafeContext.EscapeJson(docPath) + "\",\"markdown\":\"" + SafeContext.EscapeJson(fullMd) + "\"}";
                        createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDocWithMd", createReq);
                    }
                    if (!createRes.Success)
                    {
                        // 冲突自愈 2: 降级到根目录创建
                        docPath = "/" + safeDocTitle;
                        createReq = "{\"notebook\":\"" + SafeContext.EscapeJson(targetNbId) + "\",\"path\":\"" + SafeContext.EscapeJson(docPath) + "\",\"markdown\":\"" + SafeContext.EscapeJson(fullMd) + "\"}";
                        createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDocWithMd", createReq);
                    }

                    if (createRes.Success)
                    {
                        string createdDocId = ExtractCreatedBlockId(createRes.Text);
                        if (!string.IsNullOrEmpty(createdDocId))
                        {
                            var attrs = new Dictionary<string, string>
                            {
                                { "custom-source-url", url ?? "" },
                                { "custom-source-title", title ?? "" },
                                { "custom-source-author", author ?? "" },
                                { "custom-source-site", siteName ?? "" },
                                { "custom-clip-status", "unread" },
                                { "custom-clip-time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
                            };
                            string docEmoji = TextCleaner.GetSiteEmoji(siteName);
                            if (!string.IsNullOrEmpty(docEmoji)) attrs["icon"] = docEmoji;

                            SyKernelRequest.DoRequest(syUrl, token, "/api/attr/setBlockAttrs", JsonHelper.ToJson(new { id = createdDocId, attrs = attrs }));
                        }

                        SoundPlayerHelper.PlayFeedback();
                        if (notify != null) notify("思源外骨骼 · 研读专页", "✓ 已新建独立研读专页: 《" + shortTitle + "》");
                        return;
                    }
                    else
                    {
                        if (notify != null) notify("思源外骨骼", "❌ 研读专页创建失败: " + createRes.Error);
                        return;
                    }
                }

                // =============================================================
                // 模式分支 A: 文献卡片流 (Insight Card Mode - 默认)
                // =============================================================
                string cardMd;
                if (string.IsNullOrEmpty(selected))
                {
                    string safeTitle = title.Replace("[", "【").Replace("]", "】");
                    if (!string.IsNullOrEmpty(url))
                    {
                        string meta = string.Format("⏱️ 摘录于 {0} · {1}{2}", timeStr, siteName, !string.IsNullOrEmpty(author) ? " · 作者: " + author : "");
                        cardMd = string.Format("* 📑 [{0}]({1})\n  * {2}", safeTitle, url, meta);
                    }
                    else
                    {
                        cardMd = string.Format("* 📑 {0}\n  * ⏱️ 记录于 {1} · {2}", safeTitle, timeStr, snap.AppDisplayName);
                    }
                }
                else
                {
                    cardMd = TextCleaner.BuildNestedListItem(selected, title, url, snap.AppDisplayName, timeStr);
                }

                // 若用户附带即时思考/批注，以缩进子列表挂载
                if (!string.IsNullOrEmpty(userAnnotation))
                {
                    cardMd += "\n  * 💡 思考批注: " + userAnnotation.Trim().Replace("\n", " ");
                }

                // 附带小驴拾遗兼容 IAL
                string ial = TextCleaner.BuildGleanIalAttributes(url, title, siteName, author);

                string targetDoc = AppSettings.Instance.QuickNoteTargetDoc;
                if (string.IsNullOrEmpty(targetDoc))
                {
                    string diaryNb = SafeContext.GetDiaryNotebookId(null, syUrl, token);
                    targetDoc = ContextPenMain.GetOrCreateDailyNote(syUrl, token, diaryNb, DateTime.Now.ToString("yyyyMMdd"), DateTime.Now.ToString("yyyy-MM-dd"));
                }

                if (string.IsNullOrEmpty(targetDoc))
                {
                    if (notify != null) notify("思源外骨骼", "❌ 未能定位今日日记或读库目标文档");
                    return;
                }

                var r = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", JsonHelper.ToJson(new
                {
                    parentID = targetDoc,
                    dataType = "markdown",
                    data = cardMd + "\n" + ial
                }));

                if (r.Success)
                {
                    SoundPlayerHelper.PlayFeedback();
                    if (notify != null) notify("思源外骨骼 · 极速剪藏", "✓ 已收录文献卡: 《" + shortTitle + "》");
                }
                else
                {
                    if (notify != null) notify("思源外骨骼", "❌ 剪藏写入失败: " + r.Error);
                }
            }
            catch (Exception ex)
            {
                if (notify != null) notify("思源外骨骼", "⚠️ 剪藏异常: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// 融合前置大模型提炼的智能网页剪藏 (1-Click AI Summarize & Clip)
    /// 自动对当前网页划选正文提取 3 条核心要点，注入批注区并写入文献卡或专页
    /// </summary>
    public static void ExecuteWebClipWithAi(Action<string, string> notify, string clipMode)
    {
        ThreadPool.QueueUserWorkItem(state =>
        {
            try
            {
                IntPtr prevHwnd = XiaoLv.Desktop.UI.BentoWindow.LastExternalForegroundHwnd;
                var snap = (prevHwnd != IntPtr.Zero)
                    ? ContextPerception.CaptureForegroundContext(prevHwnd)
                    : ContextPerception.CaptureForegroundContext();

                string selected = SelectionHelper.CaptureSelectedText(150);
                if (string.IsNullOrEmpty(selected) && ClipboardWatcher.IsEnabled && ClipboardWatcher.LatestItem != null)
                {
                    selected = ClipboardWatcher.LatestItem.RawText;
                }

                if (string.IsNullOrEmpty(selected))
                {
                    if (notify != null) notify("思源外骨骼 · AI 剪藏", "⚠️ 请先在网页中划选需要 AI 提炼的核心正文段落");
                    return;
                }

                if (notify != null) notify("思源外骨骼 · AI 剪藏", "🤖 正在调用大模型提炼核心要点...");
                var aiRes = AiRefiner.Refine(AiRefiner.PromptKeyPoints, selected);
                string annotation = "";
                if (aiRes != null && aiRes.Success && !string.IsNullOrEmpty(aiRes.Content))
                {
                    annotation = "【AI 核心结论】\n" + aiRes.Content;
                }

                ExecuteWebClip(notify, annotation, clipMode);
            }
            catch (Exception ex)
            {
                if (notify != null) notify("思源外骨骼", "❌ AI 提炼剪藏异常: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// 原生将一段 Markdown 追加至今日日记，自动发现日记笔记本并处理文件并发重试
    /// </summary>
    public static bool AppendToDailyNote(string syUrl, string token, string markdown)
    {
        if (string.IsNullOrEmpty(syUrl) || string.IsNullOrEmpty(markdown)) return false;
        string diaryNotebookId = SafeContext.GetDiaryNotebookId(null, syUrl, token);
        var dailyBody = "{\"notebook\":\"" + SafeContext.EscapeJson(diaryNotebookId) + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(markdown) + "\"}";
        var res = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);
        if (!res.Success && !string.IsNullOrEmpty(res.Error) && (res.Error.Contains("Access is denied") || res.Error.Contains("rename")))
        {
            Thread.Sleep(300);
            res = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendDailyNoteBlock", dailyBody);
        }
        return res.Success;
    }
}


// ============================================================================
// L1-02: 多源深度研究来源卡 —— 主驱动程序 (ResearchSourceCardMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 协议依据：docs/contracts/evidence-envelope · R152-04 研究来源卡契约
// 核心逻辑：
//   1. 浏览器/文献选区抓取（选区正文 + 页面 URL + 页面标题/窗口名）
//   2. 计算 8 位 MD5 内容指纹，确保摘录唯一性与版本追踪
//   3. 重复 URL 软提示（查库检测是否曾摘录过此链接，提示已有版本，不阻断本次写入）
//   4. 三层物理结构化分流卡片构造：
//      - 【摘录层】: > 引用全文（独立完整，保证源站 404 后摘录内容永久可读）
//      - 【来源层】: 来源链接、标题、抓取时间、MD5 指纹、来源应用
//      - 【推断层】: 用户个人理解或 AI 提炼，与客观摘录物理隔离
//   5. 写入目标智能兜底：优先写入指定收集箱文档，未配置则无缝归流至今日日记
// ============================================================================

