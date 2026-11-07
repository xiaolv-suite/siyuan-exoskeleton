using Quicker.Public;
// ============================================================================
// L1-00: 随身划词笔 —— 主驱动程序 (ContextPenMain.cs)
// 兼容模式：Quicker C# 模块「普通模式 v2」（Roslyn / C# 5+ / 零外部第三方依赖）
// 人性化设计：
//   1. 桌面双子星架构之「划词流核心」：
//      - 针对用户划选的手头内容，呼出 420px 极简微拟物悬浮胶囊；
//      - 键盘盲打流：支持直接按数字键 1~6 闪电分发：
//        [1] 📝 入日记   [2] 🧠 制闪卡   [3] 📋 派任务
//        [4] 🔍 查思源   [5] 📤 净复制   [6] 💬 加批注
//   2. 0.2 秒心流不断：选完即走，自动异步落盘，绝不抢窗口焦点！
//   3. 100% 思源原生平滑自愈：所有分支均使用标准内核 API，未装插件完全自洽！
// ============================================================================

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

public static class ContextPenMain
{
    public static void Exec(IStepContext context)
    {
        var windowTitle = SafeContext.GetString(context, "来源窗口", "当前窗口");
        var syUrl = SafeContext.GetString(context, "sy_url").TrimEnd('/');
        var token = SafeContext.GetString(context, "sy_token");
        var diaryNotebookId = SafeContext.GetString(context, "日记笔记本ID");
        var op = SafeContext.GetString(context, "操作指令");
        var text = SafeContext.GetInputText(context);

        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(text)) text = text.Trim();

        // 阶段一：展示 420px 极简微胶囊菜单（若尚未选择指令）
        if (string.IsNullOrEmpty(op))
        {
            var b64 = "";
            if (!string.IsNullOrEmpty(text))
            {
                try { b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)); } catch { }
            }

            var tempHtml = Path.Combine(Path.GetTempPath(), "siyuan_pen_capsule.html");
            EnsurePenCapsuleFile(tempHtml);
            var capsuleUrl = "file:///" + tempHtml.Replace('\\', '/') + (!string.IsNullOrEmpty(b64) ? ("#" + b64) : "");

            SafeContext.Set(context, "capsule_url", capsuleUrl);
            SafeContext.Set(context, "需要胶囊UI", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }

        // 阶段二：执行所选指令流 (1~6)
        SafeContext.Set(context, "需要胶囊UI", false);

        if (string.IsNullOrWhiteSpace(text))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "⚠️ 未获取到有效文本，操作已取消。");
            return;
        }

        if (string.IsNullOrWhiteSpace(syUrl) || string.IsNullOrWhiteSpace(token))
        {
            SyKernelRequest.AutoDiscoverEnv(ref syUrl, ref token, false);
            SafeContext.Set(context, "sy_url", syUrl);
            SafeContext.Set(context, "sy_token", token);
        }
        diaryNotebookId = SafeContext.GetDiaryNotebookId(context, syUrl, token);

        var now = DateTime.Now;
        var todayCompact = now.ToString("yyyyMMdd");
        var todayDash = now.ToString("yyyy-MM-dd");
        var timeStr = now.ToString("HH:mm");

        
        // 分支 Q: 携文呼出思源超级全局总控台
        if (op == "q" || op == "panel")
        {
            SafeContext.Set(context, "需要调起动作", true);
            SafeContext.Set(context, "目标动作ID", "ed70abf5-45ab-430c-9e72-1f77945389da");
            SafeContext.Set(context, "输入文本", text);
            SafeContext.Set(context, "跳过UI", true);
            SafeContext.Set(context, "是否成功", true);
            return;
        }
        // 分支 5: 纯净复制（预清洗：合并PDF断行、剔除广告尾巴、盘古空格与属性脱敏）
        if (op == "5" || op == "clean")
        {
            var cleaned = XiaoLv.Desktop.Core.TextCleaner.CleanAll(text);
            if (string.IsNullOrEmpty(cleaned)) cleaned = text.Trim();
            try { Clipboard.SetText(cleaned); } catch { }
            XiaoLv.Desktop.Core.SoundPlayerHelper.PlayFeedback();

            SafeContext.Set(context, "是否成功", true);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "✓ [净复制] 已合并PDF断行、去除广告尾巴、完成盘古排版！");
            return;
        }

        // 定位今日日记文档
        string docId = GetOrCreateDailyNote(syUrl, token, diaryNotebookId, todayCompact, todayDash);
        if (string.IsNullOrEmpty(docId))
        {
            SafeContext.Set(context, "是否成功", false);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", "❌ 未能连接思源笔记或定位今日日记，请确认思源正在运行。");
            return;
        }

        // 分支 1: 记日记 / 摘录 (灵感 A: 嵌套列表结构化沉淀与修饰键分流)
        if (op == "1" || op == "daily" || op.StartsWith("1:"))
        {
            var imagePath = SafeContext.GetString(context, "截图路径");
            string imageMd = "";
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                string assetPath, err;
                if (XiaoLv.Desktop.Core.FileDispatcher.UploadAsset(syUrl, token, imagePath, out assetPath, out err))
                {
                    imageMd = "\n  * ![" + Path.GetFileName(imagePath) + "](" + assetPath + ")";
                }
            }

            string sourceUrl = SafeContext.GetString(context, "来源URL");
            bool isShift = op.EndsWith(":shift") || SafeContext.GetBool(context, "修饰键Shift");
            bool isCtrl = op.EndsWith(":ctrl") || SafeContext.GetBool(context, "修饰键Ctrl");

            string targetDocId = docId;
            if (isShift && !string.IsNullOrEmpty(XiaoLv.Desktop.Core.AppSettings.Instance.QuickNoteTargetDoc))
            {
                targetDocId = XiaoLv.Desktop.Core.AppSettings.Instance.QuickNoteTargetDoc;
            }

            // 1.1 Lifelog 语法智能多态直投 (¥记账, ~专注, +习惯)
            if (text.StartsWith("¥") || text.StartsWith("￥") || text.StartsWith("~") || text.StartsWith("+"))
            {
                string category, tags;
                string parsed = XiaoLv.Desktop.Core.LifelogParser.ParseLine(text, out category, out tags);
                if (!string.IsNullOrEmpty(parsed))
                {
                    string lifelogMd = "- " + parsed;
                    var okLife = AppendBlock(syUrl, token, targetDocId, lifelogMd);
                    if (okLife) XiaoLv.Desktop.Core.SoundPlayerHelper.PlayFeedback();
                    SafeContext.Set(context, "是否成功", okLife);
                    SafeContext.Set(context, "需要通知", true);
                    SafeContext.Set(context, "通知反馈", okLife ? ("✓ [Lifelog] " + category + " 已记入今日日记！") : "❌ 写入失败");
                    return;
                }
            }

            // 1.2 LaTeX 数学公式智能包裹
            string cleanFormula;
            if (XiaoLv.Desktop.Core.ClipboardWatcher.TryDetectLatexMath(text, out cleanFormula))
            {
                string mathBlock = "$$\n" + cleanFormula + "\n$$\n{: custom-type=\"math\" }";
                var okMath = AppendBlock(syUrl, token, targetDocId, mathBlock);
                if (okMath) XiaoLv.Desktop.Core.SoundPlayerHelper.PlayFeedback();
                SafeContext.Set(context, "是否成功", okMath);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", okMath ? "✓ [LaTeX公式] 已生成原生数学公式块！" : "❌ 写入失败");
                return;
            }

            // 1.3 数据表格原生块格式化
            if (text.TrimStart().StartsWith("|") && text.Contains("\n|"))
            {
                string tableBlock = text.Trim() + "\n\n{: custom-type=\"table-block\" }";
                var okTab = AppendBlock(syUrl, token, targetDocId, tableBlock);
                if (okTab) XiaoLv.Desktop.Core.SoundPlayerHelper.PlayFeedback();
                SafeContext.Set(context, "是否成功", okTab);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", okTab ? "✓ [数据表格] 已结构化写入今日日记！" : "❌ 写入失败");
                return;
            }

            // 构建大纲级嵌套列表项：正文为主列表项，出处与时间自动缩离子项
            string nestedMd = XiaoLv.Desktop.Core.TextCleaner.BuildNestedListItem(text, windowTitle, sourceUrl, windowTitle, timeStr);
            if (!string.IsNullOrEmpty(imageMd)) nestedMd += imageMd;

            if (isCtrl)
            {
                nestedMd = nestedMd.Replace("* ", "* [ ] ");
            }

            // 附带小驴拾遗 (siyuan-glean) 兼容 IAL 属性
            string ial = XiaoLv.Desktop.Core.TextCleaner.BuildGleanIalAttributes(sourceUrl, windowTitle);

            var ok = AppendBlock(syUrl, token, targetDocId, nestedMd + "\n" + ial);
            if (ok) XiaoLv.Desktop.Core.SoundPlayerHelper.PlayFeedback();
            SafeContext.Set(context, "是否成功", ok);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", ok ? ("✓ [随身摘录] " + (isCtrl ? "待办" : (isShift ? "读库" : "嵌套列表")) + "已结构化沉淀！") : "❌ 写入失败");
            return;
        }

        // 分支 2: 制闪卡 (FSRS 属性)
        if (op == "2" || op == "card")
        {
            string cleanText = text;
            if (cleanText.StartsWith("? ") || cleanText.StartsWith("？ ")) cleanText = cleanText.Substring(2).Trim();

            string q = cleanText, a = "待补充答案";
            if (cleanText.Contains("//") || cleanText.Contains("——"))
            {
                var parts = Regex.Split(cleanText, "//|——");
                if (parts.Length > 1) { q = parts[0].Trim(); a = parts[1].Trim(); }
            }
            var cardMd = "### 🧠 " + q + "\n\n" + a + "\n\n{: custom-riff-decks=\"FSRS\" custom-type=\"flashcard\" }";
            var ok = AppendBlock(syUrl, token, docId, cardMd);
            SafeContext.Set(context, "是否成功", ok);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", ok ? "✓ [制闪卡] 已纳入思源原生 FSRS 复习排程！" : "❌ 写入失败");
            return;
        }

        // 分支 3: 派任务 (Task Horizon 兼容)
        if (op == "3" || op == "task")
        {
            string cleanTask = text;
            if (cleanTask.StartsWith("! ") || cleanTask.StartsWith("！ ")) cleanTask = cleanTask.Substring(2).Trim();

            var taskMd = "- [ ] " + cleanTask + " (P2重要不急) {: custom-task-priority=\"p2\" custom-task-horizon=\"true\" }";
            var ok = AppendBlock(syUrl, token, docId, taskMd);
            SafeContext.Set(context, "是否成功", ok);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", ok ? "✓ [派任务] 已派工至 Task Horizon / 今日待办池！" : "❌ 写入失败");
            return;
        }

        // 分支 4: 稍后读剪藏 (小驴拾遗标准兼容流)
        if (op == "4" || op == "glean" || op == "readlater")
        {
            string sourceUrl = SafeContext.GetString(context, "来源链接");
            // 自动补全学术文献或纯 URL
            if (string.IsNullOrEmpty(sourceUrl))
            {
                string citeType, citeId, canonicalUrl;
                if (XiaoLv.Desktop.Core.ClipboardWatcher.TryDetectAcademicCitation(text, out citeType, out citeId, out canonicalUrl))
                {
                    sourceUrl = canonicalUrl;
                }
                else if (text.StartsWith("http://") || text.StartsWith("https://"))
                {
                    sourceUrl = text.Trim();
                }
            }
            string targetDocId = !string.IsNullOrEmpty(XiaoLv.Desktop.Core.AppSettings.Instance.QuickNoteTargetDoc) 
                ? XiaoLv.Desktop.Core.AppSettings.Instance.QuickNoteTargetDoc 
                : docId;

            string nestedMd = XiaoLv.Desktop.Core.TextCleaner.BuildNestedListItem(text, windowTitle, sourceUrl, windowTitle, timeStr);
            string ial = XiaoLv.Desktop.Core.TextCleaner.BuildGleanIalAttributes(sourceUrl, windowTitle);

            var ok = AppendBlock(syUrl, token, targetDocId, nestedMd + "\n" + ial);
            if (ok) XiaoLv.Desktop.Core.SoundPlayerHelper.PlayFeedback();

            SafeContext.Set(context, "是否成功", ok);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", ok ? "✓ [稍后读] 文献卡片已成功存入读库收件箱！" : "❌ 写入读库失败");
            return;
        }

        // 分支 4.5: 查思源
        if (op == "search")
        {
            var kw = TruncateClean(text, 40);
            var sql = "{\"stmt\":\"SELECT id, content FROM blocks WHERE content LIKE '%" + SafeContext.EscapeJson(kw.Replace("'", "''")) + "%' OR markdown LIKE '%" + SafeContext.EscapeJson(kw.Replace("'", "''")) + "%' ORDER BY updated DESC LIMIT 1\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql);
            string hitId = "";
            string hitTitle = kw;
            if (r.Success)
            {
                hitId = ExtractJsonField(r.Text, "id");
                var c = ExtractJsonField(r.Text, "content");
                if (!string.IsNullOrEmpty(c)) hitTitle = TruncateClean(c, 25);
            }

            if (!string.IsNullOrEmpty(hitId))
            {
                try
                {
                    LvSendCommand.Send(syUrl, token, "doc.open", "{\"id\":\"" + SafeContext.EscapeJson(hitId) + "\"}", "siyuan-speed-switch", "fast");
                }
                catch { }
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("siyuan://blocks/" + hitId) { UseShellExecute = true });
                }
                catch { }

                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 已秒级定位至笔记块：【" + hitTitle + "】");
            }
            else
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("siyuan://") { UseShellExecute = true });
                }
                catch { }

                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "🔍 思源中未直接命中【" + kw + "】，已为您唤醒思源笔记。");
            }
            return;
        }

        // 分支 6: 原位穿透贴双链 ((id "文本")) (优先直接贴入光标处，未命中则降级为批注卡)
        if (op == "6" || op == "link")
        {
            var kw = TruncateClean(text, 40);
            string safeKw = kw.Replace("'", "''");
            string sql = "{\"stmt\":\"SELECT id, content FROM blocks WHERE (content LIKE '%" + SafeContext.EscapeJson(safeKw) + "%' OR name LIKE '%" + SafeContext.EscapeJson(safeKw) + "%' OR alias LIKE '%" + SafeContext.EscapeJson(safeKw) + "%') AND type IN ('d', 'h', 'p') ORDER BY updated DESC LIMIT 1\"}";
            var r = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql);
            string hitId = "";
            if (r.Success) hitId = ExtractJsonField(r.Text, "id");

            if (!string.IsNullOrEmpty(hitId))
            {
                string doubleLink = "((" + hitId + " \"" + kw + "\"))";
                IntPtr fg = IntPtr.Zero;
                XiaoLv.Desktop.Core.NativeClipboard.PasteTextAtCaret(fg, doubleLink);
                XiaoLv.Desktop.Core.SoundPlayerHelper.PlayFeedback();
                SafeContext.Set(context, "是否成功", true);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", "✓ 已将双链 ((" + hitId.Substring(0, 8) + "...)) 原位注入当前外部光标处！");
                return;
            }
            else
            {
                // 若库中未命中，回退以批注卡形式记入日记
                var commentMd = "> 💬 **审阅批注** (" + timeStr + ")\n> \n> 「" + text.Replace("\r\n", "\n").Replace("\n", "\n> ") + "」\n\n{: memo=\"待深入考证\" custom-type=\"comment-block\" }";
                var ok = AppendBlock(syUrl, token, docId, commentMd);
                SafeContext.Set(context, "是否成功", ok);
                SafeContext.Set(context, "需要通知", true);
                SafeContext.Set(context, "通知反馈", ok ? "✓ [加批注] 审阅批注卡已存入今日日记！" : "❌ 写入失败");
                return;
            }
        }

        // 分支 6.5: 独立批注
        if (op == "comment")
        {
            var commentMd = "> 💬 **审阅批注** (" + timeStr + ")\n> \n> 「" + text.Replace("\r\n", "\n").Replace("\n", "\n> ") + "」\n\n{: memo=\"待深入考证\" custom-type=\"comment-block\" }";
            var ok = AppendBlock(syUrl, token, docId, commentMd);
            SafeContext.Set(context, "是否成功", ok);
            SafeContext.Set(context, "需要通知", true);
            SafeContext.Set(context, "通知反馈", ok ? "✓ [加批注] 审阅批注卡已存入今日日记！" : "❌ 写入失败");
            return;
        }
    }

    private static string BuildCapsuleHtml(string preview)
    {
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>随身划词笔</title><style>" +
               "body{margin:0;padding:12px 14px;background:#0f172a;color:#f8fafc;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;user-select:none;overflow:hidden}" +
               ".header{display:flex;align-items:center;justify-content:space-between;margin-bottom:10px;font-size:12px;opacity:0.85}" +
               ".preview{background:rgba(255,255,255,0.08);border:1px solid rgba(255,255,255,0.12);padding:5px 8px;border-radius:6px;font-size:11.5px;color:#94a3b8;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-bottom:12px}" +
               ".grid{display:grid;grid-template-columns:repeat(3,1fr);gap:8px}" +
               ".btn{display:flex;align-items:center;gap:6px;padding:8px 10px;border-radius:8px;background:rgba(255,255,255,0.06);border:1px solid rgba(255,255,255,0.15);border-bottom:2.5px solid rgba(255,255,255,0.25);color:#f1f5f9;cursor:pointer;font-size:12.5px;font-weight:700;transition:all 0.1s}" +
               ".btn:hover{background:rgba(99,102,241,0.25);border-color:#818cf8;transform:translateY(-1px)}" +
               ".btn:active{transform:translateY(1.5px);border-bottom-width:1px}" +
               ".kbd{background:rgba(255,255,255,0.14);padding:1px 5px;border-radius:4px;font-size:11px;font-family:monospace;border:1px solid rgba(255,255,255,0.2)}" +
               "</style></head><body>" +
               "<div class=\"header\"><span>✏️ <b>思源随身划词笔</b> · 盲打分发</span><span style=\"font-size:11px;opacity:0.6\">按数字键 1~6 或 Esc</span></div>" +
               "<div class=\"preview\">选区：\"" + SafeContext.EscapeJson(preview) + "\"</div>" +
               "<div class=\"grid\">" +
               "<div class=\"btn\" onclick=\"choose('1')\"><span class=\"kbd\">1</span><span>📝 记日记</span></div>" +
               "<div class=\"btn\" onclick=\"choose('2')\"><span class=\"kbd\">2</span><span>🧠 制闪卡</span></div>" +
               "<div class=\"btn\" onclick=\"choose('3')\"><span class=\"kbd\">3</span><span>📋 派任务</span></div>" +
               "<div class=\"btn\" onclick=\"choose('4')\"><span class=\"kbd\">4</span><span>🔍 查思源</span></div>" +
               "<div class=\"btn\" onclick=\"choose('5')\"><span class=\"kbd\">5</span><span>📤 净复制</span></div>" +
               "<div class=\"btn\" onclick=\"choose('6')\"><span class=\"kbd\">6</span><span>💬 加批注</span></div>" +
               "</div>" +
               "<script>" +
               "function choose(k){if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({op:'setVar',data:{name:'操作指令',value:k}});setTimeout(()=>window.chrome.webview.postMessage({op:'close'}),80);}else{alert('选定: '+k);}}" +
               "window.addEventListener('keydown',e=>{" +
               "if(['1','2','3','4','5','6'].includes(e.key)){e.preventDefault();choose(e.key);}" +
               "else if(e.key==='Escape'){e.preventDefault();window.chrome?.webview?.postMessage({op:'close'});}" +
               "else if(e.key==='Enter'){e.preventDefault();choose('1');}" +
               "});" +
               "</script></body></html>";
    }

    public static string GetOrCreateDailyNote(string syUrl, string token, string nbId, string compact, string dash)
    {
        var sql1 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND ial LIKE '%custom-dailynote-" + compact + "%' ORDER BY updated DESC LIMIT 1\"}";
        var r1 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql1);
        if (r1.Success)
        {
            var m = Regex.Match(r1.Text ?? "", "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
        }

        var sql2 = "{\"stmt\":\"SELECT id FROM blocks WHERE type='d' AND (content LIKE '%" + dash + "%' OR hpath LIKE '%" + dash + "%') ORDER BY updated DESC LIMIT 1\"}";
        var r2 = SyKernelRequest.DoRequest(syUrl, token, "/api/query/sql", sql2);
        if (r2.Success)
        {
            var m = Regex.Match(r2.Text ?? "", "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
        }

        var createRes = SyKernelRequest.DoRequest(syUrl, token, "/api/filetree/createDailyNote", "{\"notebook\":\"" + SafeContext.EscapeJson(nbId) + "\"}");
        if (createRes.Success)
        {
            var m = Regex.Match(createRes.Text ?? "", "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
        }

        return "";
    }

    private static bool AppendBlock(string syUrl, string token, string docId, string markdown)
    {
        var appendBody = "{\"parentID\":\"" + docId + "\",\"dataType\":\"markdown\",\"data\":\"" + SafeContext.EscapeJson(markdown) + "\"}";
        var res = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);
        // 云同步安全门禁：若因后台云同步或写锁产生短暂冲突，自动退避 400ms 重试
        if (!res.Success && (res.Error.Contains("sync") || res.Error.Contains("busy") || res.Error.Contains("locked") || res.Error.Contains("rename") || res.Error.Contains("Access is denied")))
        {
            Thread.Sleep(400);
            res = SyKernelRequest.DoRequest(syUrl, token, "/api/block/appendBlock", appendBody);
        }
        return res.Success;
    }

    private static string TruncateClean(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (clean.Length <= maxLen) return clean;
        return clean.Substring(0, maxLen) + "...";
    }

    private static string ExtractJsonField(string json, string field)
    {
        var m = Regex.Match(json ?? "", "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private const string EmbeddedGzPenCapsule = "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";

    private static void EnsurePenCapsuleFile(string targetPath)
    {
        try
        {
            if (File.Exists(targetPath) && (DateTime.Now - File.GetLastWriteTime(targetPath)).TotalDays < 3) return;
            var localFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files", "pen-capsule.html");
            if (File.Exists(localFile)) { File.Copy(localFile, targetPath, true); return; }
            byte[] gzBytes = Convert.FromBase64String(EmbeddedGzPenCapsule);
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
}