using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace XiaoLv.Desktop.Core
{
    public class DropZoneRouteResult
    {
        public bool Success { get; set; }
        public string Category { get; set; } // "article", "expense", "file", "text"
        public string TargetModule { get; set; }
        public string Summary { get; set; }

        public DropZoneRouteResult()
        {
            Success = false;
            Category = "text";
            TargetModule = "SmartCapture";
            Summary = "";
        }
    }

    /// <summary>
    /// 思源外骨骼 · 系统级托放中枢智能路由引擎 (DropZoneRouter)
    /// 1. 拖入任意文件、网页超链或文本，秒级启发式智能分流：
    ///    - 网页书签/文章 -> 自动提纯并推送至「小驴拾遗 (siyuan-glean)」/读库
    ///    - 票据/发票/账目 -> 自动打标并推送至「小驴管家」/今日记账流 (custom-lifelog-type="expense")
    ///    - 本地文档/代码 -> 自动结构化解析并挂载实时热监听 (FileDispatcher)
    ///    - 纯文本碎片 -> 自动盘古空格排版、去噪并沉淀入今日日记
    /// </summary>
    public static class DropZoneRouter
    {
        public static DropZoneRouteResult RouteFiles(string[] files, Action<string, string> notify)
        {
            var res = new DropZoneRouteResult { Success = true, Category = "file", TargetModule = "FileDispatcher" };
            if (files == null || files.Length == 0) return res;

            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f) || !File.Exists(f)) continue;

                string fn = Path.GetFileName(f);

                // 智能判定：票据/发票图片或文档 -> 小驴管家 / 记账流
                if (Regex.IsMatch(fn, @"(?:\b发票\b|\b账单\b|\b报销\b|\b收据\b|\binvoice\b|\breceipt\b)", RegexOptions.IgnoreCase))
                {
                    res.Category = "expense";
                    res.TargetModule = "siyuan-home";
                    res.Summary = "发票报销票据: " + fn;

                    string expenseText = string.Format("¥0.0 票据附件: {0}", fn);
                    var ctx = new DesktopStepContext("SYS", expenseText);
                    ctx.SetVarValue("来源应用", "思源外骨骼 · 托放中枢");
                    SmartCaptureMain.Exec(ctx);

                    if (notify != null) notify("托放中枢 · 小驴管家", "✓ 票据【" + fn + "】已自动送至记账报销流！");
                    continue;
                }

                // 常规 Office / PDF / Markdown / 源码文档 -> FileDispatcher 结构化透视
                FileDispatcher.DispatchFileToSiYuan(f, notify);
            }

            res.Summary = "已成功分流 " + files.Length + " 份文件";
            return res;
        }

        public static DropZoneRouteResult RouteTextOrUrl(string rawText, Action<string, string> notify)
        {
            var res = new DropZoneRouteResult();
            if (string.IsNullOrWhiteSpace(rawText)) return res;

            string text = rawText.Trim();

            // 1. 网址 / 网页超链 -> 推送至「小驴拾遗 (siyuan-glean)」读库
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                res.Success = true;
                res.Category = "article";
                res.TargetModule = "siyuan-glean";
                res.Summary = "网页文章剪藏";

                SmartCaptureMain.ExecuteWebClip(notify, "", "card");
                if (notify != null) notify("托放中枢 · 小驴拾遗", "✓ 网页已自动推送入读库未读分拣流！");
                return res;
            }

            // 2. 票据 / 账目 / 花销文本 (¥38.5 瑞幸 或 餐饮 50元) -> 推送至「小驴管家」记账流
            if (text.StartsWith("¥") || text.StartsWith("￥") || Regex.IsMatch(text, @"(?:\b报销\b|\b支出\b|\b花销\b|\b发票\b)"))
            {
                res.Success = true;
                res.Category = "expense";
                res.TargetModule = "siyuan-home";
                res.Summary = "记账账目流";

                var ctx = new DesktopStepContext("SYS", text);
                ctx.SetVarValue("来源应用", "思源外骨骼 · 托放中枢");
                SmartCaptureMain.Exec(ctx);

                if (notify != null) notify("托放中枢 · 小驴管家", "✓ 记账账目已自动归入管家 Lifelog 流！");
                return res;
            }

            // 3. 纯文本 / 代码碎片 -> 盘古排版、去噪并沉淀入今日日记
            res.Success = true;
            res.Category = "text";
            res.TargetModule = "SmartCapture";
            res.Summary = "纯文本清洗速记";

            string cleaned = TextCleaner.CleanAll(text);
            var noteCtx = new DesktopStepContext("SYS", cleaned);
            noteCtx.SetVarValue("来源应用", "思源外骨骼 · 托放中枢");
            SmartCaptureMain.Exec(noteCtx);

            if (notify != null) notify("托放中枢 · 极速速记", "✓ 文本已自动清洗并注入今日日记！");
            return res;
        }
    }
}
