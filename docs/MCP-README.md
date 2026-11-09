# 思源外骨骼 · MCP 接入指南 (P2-1)

`思源外骨骼.exe --mcp` 即是一个标准 **MCP Server**（Model Context Protocol，stdio + NDJSON JSON-RPC 2.0），把思源的写入/检索/待办/闪卡能力无 UI 注册为 Agent 可调用工具（学 uTools `registerTool` 生态位）。任何支持 MCP 的 AI 客户端（Claude Desktop、ChatBox、Cursor、Claude Code、千问/豆包 Agent 等）都能自动调用。

## 接入配置

### Claude Desktop / Cursor / Claude Code（claude_desktop_config.json / mcp.json）

```json
{
  "mcpServers": {
    "siyuan-exoskeleton": {
      "command": "D:\\思源插件\\思源小驴桌面助手\\bin\\Release\\思源外骨骼.exe",
      "args": ["--mcp"]
    }
  }
}
```

### ChatBox（MCP 服务设置）

- 命令：`D:\思源插件\思源小驴桌面助手\bin\Release\思源外骨骼.exe`
- 参数：`--mcp`

### 注意事项

- `--mcp` 模式**零 UI、零托盘、零热键、零互斥**：与正在运行的 GUI 实例互不干扰，可同时运行。
- 思源连接自动发现（环境变量 `SIYUAN_URL` / `SIYUAN_TOKEN` 优先，其次工作区 conf 嗅探）。
- stdin 关闭（客户端退出）即自动结束进程。

## 工具清单（tools/list）

| 工具 | 参数 | 说明 |
|---|---|---|
| `siyuan_write` | `data`(必填), `target`, `heading`, `doc`, `action` | 写入思源：默认今日日记；`heading` 小节路由；`doc` 跨文档直投；`action=task` 转待办 / `flash` 转闪卡 |
| `siyuan_search_blocks` | `keyword`(必填) | 全文检索正文块，返回 20 条（含块 ID） |
| `siyuan_search_docs` | `keyword`(必填) | 按标题检索文档 |
| `siyuan_today_entries` | — | 今日日记/今日新增条目时间流 |
| `siyuan_unfinished_tasks` | — | 全库未完成待办 `- [ ]` |
| `siyuan_due_cards_count` | — | FSRS 到期闪卡数量 |
| `exoskeleton_status` | — | 版本与内核连接探针 |

## REST 双入口

不想用 stdio 的场景（Quicker/uTools/浏览器脚本）走本地 HTTP：`http://127.0.0.1:43989/`，鉴权 `X-Token`。完整机器可读规范：**`GET /api/openapi`**（OpenAPI 3.0，可导入 Apifox/Postman 或生成客户端）。

## 手动冒烟测试

```powershell
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' | & "D:\思源插件\思源小驴桌面助手\bin\Release\思源外骨骼.exe" --mcp
```

返回含 `"serverInfo":{"name":"siyuan-exoskeleton"}` 即接入成功。
