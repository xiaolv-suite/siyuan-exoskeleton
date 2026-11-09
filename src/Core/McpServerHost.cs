using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XiaoLv.Desktop.Core
{
    /// <summary>
    /// P2-1 MCP 无头服务模式 (Model Context Protocol · stdio + NDJSON JSON-RPC 2.0)：
    /// 思源外骨骼.exe --mcp → 作为 MCP Server 暴露思源写入/检索/时间轴/待办/到期卡等工具，
    /// 供 Claude Desktop / ChatBox / Cursor / 豆包 Agent 等外部 AI 客户端自动调用
    /// (学 uTools registerTool 生态位：把本地能力无 UI 注册为 Agent 可调用工具)。
    /// 零 UI、零托盘、零热键、零互斥；stdin EOF 即退出。
    /// 接入配置见 docs/MCP-README.md。
    /// </summary>
    public static class McpServerHost
    {
        private static StreamWriter _writer;

        public static void Run()
        {
            // winexe 无控制台时 Console.InputEncoding 设置不可靠 (中文 Windows 默认 GBK 会把 UTF-8 stdin 解成乱码)，
            // 必须绕过文本控制台，直接以 UTF-8 裸流读写 stdio (MCP NDJSON)
            try
            {
                using (var stdin = Console.OpenStandardInput())
                using (var stdout = Console.OpenStandardOutput())
                using (var reader = new StreamReader(stdin, new UTF8Encoding(false)))
                using (_writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        try { HandleMessage(line); }
                        catch { }
                    }
                }
            }
            catch { }
            finally { _writer = null; }
        }

        private static void HandleMessage(string line)
        {
            var msg = JsonHelper.ParseDict(line);
            if (msg == null || msg.Count == 0) return;

            object idObj = null;
            msg.TryGetValue("id", out idObj);
            bool isRequest = msg.ContainsKey("id");
            string method = JsonHelper.GetString(msg, "method", "");

            if (method == "initialize")
            {
                SendResult(idObj, new Dictionary<string, object>
                {
                    { "protocolVersion", "2024-11-05" },
                    { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                    { "serverInfo", new Dictionary<string, object> { { "name", "siyuan-exoskeleton" }, { "version", "3.0.0" } } }
                });
                return;
            }
            if (method == "ping")
            {
                SendResult(idObj, new Dictionary<string, object>());
                return;
            }
            if (method == "tools/list")
            {
                SendResult(idObj, new Dictionary<string, object> { { "tools", BuildToolCatalog() } });
                return;
            }
            if (method == "tools/call")
            {
                var pr = JsonHelper.GetDict(msg, "params");
                string toolName = JsonHelper.GetString(pr, "name", "");
                var argsDict = JsonHelper.GetDict(pr, "arguments");
                string text = ExecuteTool(toolName, argsDict);
                SendResult(idObj, new Dictionary<string, object>
                {
                    { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } }
                });
                return;
            }

            if (isRequest)
            {
                SendError(idObj, -32601, "Method not found: " + method);
            }
            // 无 id 的通知 (notifications/initialized 等) 不回应
        }

        // ==================== 工具目录 ====================

        private static Dictionary<string, object> SchemaObj(Dictionary<string, object> props, string[] required)
        {
            var req = new List<object>();
            if (required != null) foreach (var r in required) req.Add(r);
            return new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", props ?? new Dictionary<string, object>() },
                { "required", req }
            };
        }

        private static Dictionary<string, object> Prop(string type, string desc)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", desc } };
        }

        private static List<object> BuildToolCatalog()
        {
            var tools = new List<object>();

            tools.Add(new Dictionary<string, object>
            {
                { "name", "siyuan_write" },
                { "description", "向思源笔记写入内容：默认追加到今日日记；支持小节路由(heading)、跨文档直投(doc)、转待办(action=task)、转闪卡(action=flash)。对应参数化写入管线。" },
                { "inputSchema", SchemaObj(new Dictionary<string, object>
                  {
                      { "data", Prop("string", "要写入的内容 (必填)") },
                      { "target", Prop("string", "daily=今日日记(默认) | doc=指定文档") },
                      { "heading", Prop("string", "写入今日日记的指定小节名 (如: 今日摘录)") },
                      { "doc", Prop("string", "目标思源文档名 (target=doc 时生效)") },
                      { "action", Prop("string", "task=转待办 | flash=转闪卡 | 留空=普通笔记") }
                  }, new[] { "data" }) }
            });

            tools.Add(new Dictionary<string, object>
            {
                { "name", "siyuan_search_blocks" },
                { "description", "全文检索思源笔记正文块，返回最相关的 20 条（含块 ID 可用于后续跳转/引用）。" },
                { "inputSchema", SchemaObj(new Dictionary<string, object>
                  {
                      { "keyword", Prop("string", "检索关键词") }
                  }, new[] { "keyword" }) }
            });

            tools.Add(new Dictionary<string, object>
            {
                { "name", "siyuan_search_docs" },
                { "description", "按标题检索思源文档，返回最相关的 20 篇。" },
                { "inputSchema", SchemaObj(new Dictionary<string, object>
                  {
                      { "keyword", Prop("string", "文档标题关键词") }
                  }, new[] { "keyword" }) }
            });

            tools.Add(new Dictionary<string, object>
            {
                { "name", "siyuan_today_entries" },
                { "description", "读取今日日记/今日新增的笔记条目时间流 (最近 30 条)。" },
                { "inputSchema", SchemaObj(null, null) }
            });

            tools.Add(new Dictionary<string, object>
            {
                { "name", "siyuan_unfinished_tasks" },
                { "description", "读取全库未完成待办任务 (- [ ])，最多 30 条。" },
                { "inputSchema", SchemaObj(null, null) }
            });

            tools.Add(new Dictionary<string, object>
            {
                { "name", "siyuan_due_cards_count" },
                { "description", "查询 FSRS 间隔重复当前到期待复习的闪卡数量。" },
                { "inputSchema", SchemaObj(null, null) }
            });

            tools.Add(new Dictionary<string, object>
            {
                { "name", "exoskeleton_status" },
                { "description", "探针：外骨骼版本与思源内核连接状态。" },
                { "inputSchema", SchemaObj(null, null) }
            });

            return tools;
        }

        // ==================== 工具执行 ====================

        private static string ExecuteTool(string tool, Dictionary<string, object> args)
        {
            try
            {
                switch (tool ?? "")
                {
                    case "siyuan_write":
                    {
                        string data = JsonHelper.GetString(args, "data", "");
                        if (string.IsNullOrWhiteSpace(data)) return "错误: 缺少 data 参数";
                        string result = ParameterizedWriteBridge.ExecuteFromParams(
                            JsonHelper.GetString(args, "target", ""),
                            JsonHelper.GetString(args, "heading", ""),
                            JsonHelper.GetString(args, "doc", ""),
                            JsonHelper.GetString(args, "action", ""),
                            data,
                            null);
                        return result == "OK" ? "OK: 已写入思源 (" + data.Trim().Replace("\r", " ").Replace("\n", " ") + ")" : "错误: " + result;
                    }
                    case "siyuan_search_blocks":
                    {
                        string kw = (JsonHelper.GetString(args, "keyword", "") ?? "").Trim().Replace("'", "''");
                        if (string.IsNullOrEmpty(kw)) return "错误: 缺少 keyword";
                        var rows = SiYuanClient.SqlAsync("SELECT id, content FROM blocks WHERE content LIKE '%" + kw + "%' AND type NOT IN ('d') ORDER BY updated DESC LIMIT 20").GetAwaiter().GetResult();
                        return FormatRows(rows, "id", "content");
                    }
                    case "siyuan_search_docs":
                    {
                        string kw = (JsonHelper.GetString(args, "keyword", "") ?? "").Trim().Replace("'", "''");
                        if (string.IsNullOrEmpty(kw)) return "错误: 缺少 keyword";
                        var rows = SiYuanClient.SqlAsync("SELECT id, content FROM blocks WHERE type = 'd' AND content LIKE '%" + kw + "%' ORDER BY updated DESC LIMIT 20").GetAwaiter().GetResult();
                        return FormatRows(rows, "id", "content");
                    }
                    case "siyuan_today_entries":
                    {
                        string today = DateTime.Now.ToString("yyyyMMdd");
                        var rows = SiYuanClient.SqlAsync("SELECT id, content FROM blocks WHERE type IN ('p') AND created LIKE '" + today + "%' ORDER BY created DESC LIMIT 30").GetAwaiter().GetResult();
                        return FormatRows(rows, "id", "content");
                    }
                    case "siyuan_unfinished_tasks":
                    {
                        var rows = SiYuanClient.SqlAsync("SELECT id, content FROM blocks WHERE content LIKE '%- [ ]%' ORDER BY updated DESC LIMIT 30").GetAwaiter().GetResult();
                        return FormatRows(rows, "id", "content");
                    }
                    case "siyuan_due_cards_count":
                    {
                        var res = SiYuanClient.PostAsync("/api/riff/getRiffDueCards", new { deckID = "" }).GetAwaiter().GetResult();
                        if (!res.Success || res.Data == null) return "思源未连接或闪卡查询失败";
                        var dict = res.Data as Dictionary<string, object>;
                        var cards = JsonHelper.GetList(dict, "cards");
                        return "当前到期闪卡: " + (cards != null ? cards.Count : 0) + " 张";
                    }
                    case "exoskeleton_status":
                    {
                        var res = SiYuanClient.PostAsync("/api/system/version", new { }).GetAwaiter().GetResult();
                        return "思源外骨骼 v3.0.0 · 思源内核: " + (res.Success ? ("在线 " + (res.RawText ?? "")) : "未连接");
                    }
                    default:
                        return "错误: 未知工具 " + tool;
                }
            }
            catch (Exception ex)
            {
                return "工具执行异常: " + ex.Message;
            }
        }

        private static string FormatRows(List<Dictionary<string, object>> rows, string idKey, string textKey)
        {
            if (rows == null || rows.Count == 0) return "(无匹配结果)";
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                string id = JsonHelper.GetString(r, idKey, "");
                string text = JsonHelper.GetString(r, textKey, "").Replace("\r", " ").Replace("\n", " ");
                if (text.Length > 120) text = text.Substring(0, 120) + "...";
                sb.Append("[").Append(id).Append("] ").AppendLine(text);
            }
            return sb.ToString().TrimEnd();
        }

        // ==================== JSON-RPC 传输 ====================

        private static void SendResult(object id, object result)
        {
            var resp = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            };
            WriteResponse(JsonHelper.ToJson(resp));
        }

        private static void SendError(object id, int code, string message)
        {
            var resp = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
            };
            WriteResponse(JsonHelper.ToJson(resp));
        }

        private static void WriteResponse(string json)
        {
            try { if (_writer != null) _writer.WriteLine(json); } catch { }
        }
    }
}
