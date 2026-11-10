# 思源外骨骼 · 软件现状全景与后续开发路线图（2026-11）

> 调研方法：仓库自上而下只读盘点 + 三路外部调研（同类竞品 20+ 款 / 思源源码与插件生态 / Obsidian 插件生态 16 款头部插件）。
> 结论先行：**官方思源正全力冲 AI/MCP/数据库/移动端，把「进程边界之外」的世界（全局热键、托盘、系统通知、截图 OCR、剪贴板、启动器联动、入站 webhook）完整留给了外骨骼**。下一阶段的主战场不是再加功能，而是①补齐社区有 issue 背书却无人占位的 4 个空白能力，②把「功能多但不会用」转化为「发现性设计」。

---

## 一、软件现状全景（自上而下，2026-11-10 实测）

### 1.1 规模数字

| 维度 | 数值 |
|---|---|
| 版本 | v3.0.0（README / package.json / IpcAndProtocol.cs 三处一致） |
| 产物 | bin\Release\思源外骨骼.exe = 3,968,512 B（≈3.79 MB），2026-11-09 23:34 |
| C# 源码 | 98 文件 / 45,034 行（Core 78 文件 22,475 行；Engine 13 文件 14,118 行；UI 7 文件 7,245 行） |
| 最大模块 | OmniDomainActions.cs 8,316 行 · BentoWindow.cs 4,412 行 · ExternalApiBridge.cs 1,698 行 |
| 前端资产 | panel.html 20,161 行 + pen-capsule.html 781 行 + quick-dialog.html 1,072 行，共 22,014 行 |
| IPC 面 | BentoWindow 142 个唯一 op 分支；前端 panel.html 发送 92 种 op（121 处调用点） |
| Webhook | 127.0.0.1:43989，70 条路径 = 34 个逻辑端点，Token 固定时间比较鉴权 + 回环 CORS |
| 功能卡 | 31 张（5 组：捕获 6 / 搜索导航 10 / 插件直达 8 / 格式扩展 4 / 运维 3） |
| 工作室 | 九宫格 12 个入口；实际注册 19 个 modal（另有 onboarding/时间轴/偏好/速查/AI 结果/离线队列/功能指南 7 个） |
| E2E | scripts/e2e-isolated.mjs 4,317 行，260 条 logStep 断言（隔离靶场 :43988） |

### 1.2 架构分层（自上而下）

```
┌─ 入口层 ─ 全局热键(Alt+Shift+S/W/G/C/O/R/X…) · 托盘菜单 · siyuan-exoskeleton:// + xiaolv:// 协议
│           命名管道单实例唤出 · Quicker/uTools 外部事件
├─ 窗体层 ─ 7 窗体 + 1 托盘（见 1.3）
├─ 前端层 ─ panel.html：搜索意图条 → 分类 tabs → 31 卡网格/列表 → 12 工作室 modal
│           Inspector 三 tab 坞 · action-followup 跟进 banner · 三支柱 Home Hub · 明暗双主题(127 处 CSS 变量)
├─ 分发层 ─ BentoWindow op.Equals 链(142 op) · ActionDispatcher.Dispatch(Engine 命令面)
├─ 领域层 ─ OmniDomainActions(8,316 行) 等领域模块：速记/剪藏/OCR/检索/闪卡/表格/代码/大纲/体检…
├─ 数据层 ─ SiYuanClient(kernel HTTP API, Token) · SiYuanGuardian · OfflineQueueManager · DPAPI 保险箱
├─ 服务层 ─ ExternalApiBridge(:43989 webhook 34 端点) · 剪贴板/选择/Office 文件监听 · SnippetExpansionEngine
└─ 守护层 ─ MemoryOptimizer · HotkeyConflictManager · FirstRunEnvironmentDoctor · SiYuanProfileHub
```

### 1.3 窗体矩阵（全部核实）

| 类 | 宿主 | 职责 |
|---|---|---|
| BentoWindow | WPF + **WebView2**（4,412 行） | 主工作台，31 卡 + 12 工作室，op 分发中枢 |
| CapsuleWindow | WPF + WebView2 | 划词微胶囊（Alt+Shift+W），1~8 数字键盲打分流 |
| DialogWindow | WPF + WebView2 | 通用 HTML 弹窗宿主（quick-dialog.html + SafeContext 状态 URL） |
| MiniInputWindow | WPF **原生**（1,289 行） | 多态速记框：!/?/¥/~/+ 前缀意图分流、30s 防碎块粘滞合并 |
| FSRSCapsuleWindow | WPF 原生（无边框透明） | 3 秒微刷卡舱：空格翻面、1~4 评分，直连 /api/riff/reviewRiffCard |
| SnippingOverlayWindow | WPF 原生全屏覆盖层 | 截屏框选 + LocalOcrEngine 离线识字 |
| DropZoneWindow | WPF 原生右缘停靠 | 屏幕右缘 10px 托放中枢，DropZoneRouter 路由（文章/票据/文件） |
| SystemTray | WinForms NotifyIcon | 托盘常驻、菜单、溢出区探测 |

跨组件底座：静态事件 8+（ExternalApiBridge.OnApiActionReceived、ClipboardWatcher.OnClipboardEnriched、SelectionHelper.OnAutoSelectionDetected 等）；通信模式统一为「前端 postMessage(JSON{op}) → op.Equals 链 → ExecuteScriptAsync 回推 window.onXxx（20+ 接收器）」。

### 1.4 Webhook 34 端点（摘要）

存活探针(/ /ping /status)、capture、action(核心分流)、show、today、tasks、search、书签(web/folder)、navigate、focus、task/complete、profile(读/萃取/切换)、notify(+journal)、launchpad、clip/merge、graph/radial、doctor/diagnose、vault(列表/读/写)、outline(tree/rename)、table(list/data/update/export)、code(snippets/open/update)、system/onboarding。

### 1.5 UX 层现状

搜索框 type-first 意图条（搜笔记/记日记/存待办三按钮 + 前缀语法零白脸路由）→ 8 分类 tabs（默认「精简推荐」22 卡合并视图）→ 卡片内联子操作 pills → split 检索视图（显式行按钮 + 防误触 hover 抑制）→ Inspector 三 tab → action-followup 引擎（每动作后 4~8 个下一步）→ 31 卡 3 步交互指南 + 首启 4 维环境自检向导 → 底栏活体遥测。

### 1.6 构建与质量门

- build-desktop.mjs：`Framework64/v4.0.30319/csc.exe` + `/target:winexe /platform:x64 /optimize+`，嵌入 WebView2 DLL 与 assets.zip，bin 侧保留松散资源目录供热更新；零 XAML、零 NuGet。
- e2e-isolated.mjs：隔离内核 3.8.9 冷启动 → 253→260 断言扩容中 → 沙箱销毁/生产实例自动还原。

### 1.9 桌面边缘灵动坞 (Edge Dock) 与选区微光药丸 (Pop-Pill) 竞品对标与避雷终态（E2E 279 验收）
- **灵动坞高阶避雷**：引入 140ms 移入防误触延时 (飞速甩鼠标拖滚动条或关窗口绝不误弹)；后台全屏检测自愈 (前台游戏/PPT放映时整窗彻底隐退)；右键单击边缘弹出极速上下文菜单 (`⚙️设置`/`🔄同步`/`🧹释放内存`/`🙈隐藏`)；拖拽释放亮绿高光呼吸态；支持上下拖动把手调整垂直高度并持久化 `AppSettings.EdgeDockTopRatio`；
- **微光药丸高阶避雷**：内容智能感知变形 (URL->🌐一键剪藏 / 待办->📋存待办 / 英文学术语篇->🌐译 AI双语对照 / 文本->📝日记附字数徽标)；键盘打字输入即 0ms 瞬间消隐避雷 (绝不遮挡正常编辑)；距离衰减退避算法 (光标远离中心 >165px 自动淡出)；120ms 柔和微缩放进场。
- **灵动坞防抖与高度记忆**：引入 140ms 移入防误触延时 (飞速甩鼠标拖滚动条或关窗口绝不误弹)；支持上下拖动把手调整垂直停靠高度并自动持久化 `AppSettings.EdgeDockTopRatio`，自适应深浅主题；
- **微光药丸智能感知与距离衰减**：选区内容智能感知变形 (URL -> 🌐一键剪藏 / 待办 -> 📋存待办 / 文本 -> 📝日记附字数徽标)；增加距离衰减退避算法 (光标远离中心 >160px 自动淡出)，绝不遮挡用户阅读后文。
- **桌面最上层常驻调用**：升级 `DropZoneWindow` 为屏幕边缘灵动坞（Edge Dock），平时 5px 极细半透明发光细条贴边隐藏，触碰展开 `[⚡速记|🔍搜块|📋待办|📸识字|🤖问AI]` 动作矩阵与拖放合一，`WS_EX_NOACTIVATE` 绝对不抢焦点；
- **全局划词进一步显示**：摒弃直接弹大窗的重度打扰模式，新建 `SelectionPillWindow` 原生微光药丸（106×28px，📝日记 / 🤖问AI / ⋯展开大盘），不抢焦点、外部单击 0ms 消隐、2.5s 自动淡出。

### 1.8 体验与健壮性深化审计（2026-11-10 完成，E2E 276 封板）
- **安全加固 (Uri.TryCreate 强校验)**：BentoWindow 与 CapsuleWindow 的 `aiRouteLaunch` / `routeAiExternal` 全面升级为 `Uri.TryCreate` 绝对白名单校验，去除换行符与控制字符，杜绝外部传入畸形协议注入；
- **思源匹配升级 (真实日记历史反推)**：`SafeContext.GetDiaryNotebookId` 升级为「AppSettings.DailyNoteTargetNotebook 显式配置 -> SQL 历史反推 (按 `/daily note/` 或 `/%日记%/` 嗅探最近写过日记的真实 box) -> 启发式名称」三级探测，彻底解决笔记本改名后日记串流或误落第一本的痛点；
- **用户控制权 (系统级提醒守护开关)**：`AppSettings.ReminderGuardianEnabled` 偏好设置持久化 + UI 勾选联动 + 后台动态启停 (`Stop` / `Restart`)，满足静默免打扰诉求；
- **操作指引强化**：顶栏 `? 快捷指南` 强化为包含「快捷指南 · 动作广场 · 新手上路」，与速查表深度贯通。

### 1.7 并行开发现状（已收口，2026-11-10）

并行会话遗留的 26 文件改动（+2,236/-887：离线队列可靠性回执、SiYuanProfileHub 配置预设中枢 +626、panel UX 迭代 +522、Program.cs +205、E2E 253→258+ 扩容、托盘/截屏/构建脚本打磨）已由本会话验证并提交；其中 Step 7.67 暴露的「离线补录 Flush 后未强制提交内核事务」缺陷已双向修复（OfflineQueueManager.FlushQueueInternal 补 `/api/sqlite/flushTransaction` + E2E 轮询前 flush、窗口 1.5s→4.5s），E2E 全量 258/258 通过。第 0.2 步 rowID→itemID 审计结论：无需迁移（见 0.2 行）。

---

## 二、同类产品横向对比（20+ 款调研摘要）

| 类别 | 代表产品 | 对外骨骼最有价值的点 |
|---|---|---|
| 启动器 | uTools / Quicker / PowerToys CmdPal / Raycast(2025-11 Windows 公测) / Flow.Launcher / Listary 6 / Fluent Search | uTools **MCP 无 UI 能力注册**；Quicker **动作广场「网页复制→面板右键粘贴即装」**；CmdPal **fallback 兜底 + Home 钉选 + 快捷架 shelf**；Listary **双击 Ctrl**；Raycast **AI Quicklinks 路由** |
| 便签/剪藏 | Sticky Notes / 简悦 / Cubox / Anybox / myMind / Omnivore(已死教训) | Cubox **收藏压缩成一个快捷键**；简悦「剪藏→合辑→回顾」闭环；myMind **取消分类决策**；Omnivore 关停 → **剪藏直接落本地笔记才是稳态架构**（外骨骼差异化依据） |
| 剪贴板 | Ditto / PasteBar / Win+V | Ditto **粘贴为纯文本/多格式/合并**是标准答案；Win+V 25 条重启即清是及格线 |
| AI 桌面助手 | Raycast AI / uTools AI / BoltAI / ChatBox / 豆包 / 千问 / Kimi | 国内三家已训练出「**划词→小窗**」肌肉记忆；四种 AI 集成模式中「**外部 Agent 反向调用(MCP)**」是 2025 生态级趋势 |
| 笔记自带快捕 | Obsidian Web Clipper / Logseq(无原生) / Heptabase(2025-06 才排期) / Notion | **原生笔记软件普遍不做全局捕捉**——正是伴侣应用的存在空间 |

**上手路径模式对比**：Quicker=广场装动作（生态即引导）· Raycast=Walkthrough 任务清单 · CmdPal=Home 钉选+最近+空状态 · uTools=教程视频兜底 · myMind/Cubox=取消选择成本。外骨骼组合拳 = **Quicker 式动作广场 + Raycast 式任务清单 + CmdPal 式兜底与最近使用**。

---

## 三、思源生态洞察

- **版本基线**：最新稳定 v3.8.9（2026-10-29），rc v3.8.10-rc.3（2026-11-09）；47.1k stars，发版活跃。外骨骼无需升级基线，但需做 3.8.10 兼容回归。
- **siyuan:// 深链**：仅 `siyuan://blocks/{id}?focus=1` 一种动作；无 create/append/write 参数化（issue #14841）。v3.8.4 前存在 XSS→RCE（GHSA-6gx2-8gcr-x83f）——**外骨骼的深链/webhook 入参必须白名单化**。
- **插件做 vs 外骨骼做**：插件无 OS 全局热键、无后台常驻/托盘、无跨进程 exe 调用、无剪贴板主动写、无 OCR API、无 OS toast、仅 petal 私有存储。两者调的是**同一个 kernel API**——差异全在进程边界与系统层，这正是官方架构留出的位置。
- **kernel API 宝藏**：17 个未文档化 `/api/riff/*`（外骨骼 FSRS 已在用）；`/api/clipboard/*` 富文本/资产粘贴；`/api/template/render`（3.8.10 起模板支持 HTTP 请求）；`/api/network/forwardProxy`。
- **⚠ 硬兼容警报**：属性视图 `setAttributeViewBlockAttr` 的 **rowID 参数 2026-12-01 后移除，改 itemID**——外骨骼直写 AV 的代码必须在此之前迁移。
- **集市生态位**：645 个插件；任务管理类最拥挤（Task Note Management ⭐227 等 6+ 款各自为战）、AI 类拥挤；**「外部伴侣/quick capture/全局热键/截图 OCR 伴侣」类目完全空白**；jsrunner(⭐5) 证明外部程序桥接需求真实存在但方案原始。
- **官方缺口（有 issue 背书）**：桌面端全局热键速记 **#20319**（开放中）· 系统级任务提醒 **#15002**（「官方只有微信提醒」）· OCR 文本不可写回 **#13714** · 深链粒度不足 **#14841** · 全局命令中心 **#21332** · 大库搜索性能 **#6655**。
- **官方主攻方向（不撞车区）**：AI Agent/MCP/长期记忆/向量语义搜索（v3.8.0~3.8.10）、数据库视图增强、内核插件系统、移动端闪念速记（v3.7.0 已做）。

## 四、Obsidian 生态十大设计模式 → 外骨骼落地

| # | 模式 | 来源 | 外骨骼落地 |
|---|---|---|---|
| 1 | 深链动作参数化（create/append/overwrite + 锚点 + daily） | Advanced URI | webhook 封装 appendBlock/appendDailyNoteBlock/createDocByParent，暴露「向日记某标题下追加」语义动作，回吐 siyuan:// 跳转 |
| 2 | 外部接入安全模型（Bearer key + 最小端点 + OpenAPI） | Local REST API | 34 webhook 已有 token 鉴权，补 OpenAPI 描述文档与参数白名单 |
| 3 | Capture 宏管道（菜单→表单→宏串行→回执） | QuickAdd | 热键→胶囊五路由→模板→kernel API→toast 回执；可串「截图→OCR→upload→建块→addRiffCards」 |
| 4 | 占位符模板引擎（按目标路由选模板） | Templater | {{date}}/{{clipboard}}/{{selection}}/{{ocr_text}}/{{url}}，Sprig 走 renderSprig |
| 5 | 入口再分配（命令挂到任意系统入口） | Commander | 热键/托盘/胶囊/右键发送到/Quicker/uTools/浏览器扩展 → 全部收敛到同一命令集 |
| 6 | 跨库聚合查询 | Dataview/Tasks | /api/query/sql（记得 flushTransaction）聚合待办+到期卡+日记索引做「今日面板」 |
| 7 | 模板化剪藏（Reader 提取 + 变量 + 资产本地化） | 官方 Web Clipper | 剪藏按域名规则路由笔记本/模板；划词高亮存为带回链引用块 |
| 8 | OCR 索引管线 | Text Extractor | 截图→OCR→图片 upload→OCR 文本写 custom- 属性 → SQL 可检索（呼应 #13714） |
| 9 | 系统级提醒触达 | Reminder | 常驻轮询待办 deadline + getRiffDueCards → Windows toast → 点击深链跳块 → 完成/延后回写 |
| 10 | AI RAG 本地化的边界 | Copilot/Smart Connections | **不重复造向量库**：官方已上 embeddings/MCP/Agent；外骨骼做「采集入口 + 上下文喂送」 |

---

## 五、后续开发计划（2026 Q4 → 2027 Q1）

### 第 0 步 · 立即卫生项（硬截止约束）

| # | 事项 | 理由 |
|---|---|---|
| 0.1 | 收口并行会话未提交改动 → 跑 e2e → 落 commit | 工作区 +2,236/-887 行悬空，E2E 253→260 待验证 |
| 0.2 | **AV rowID→itemID 迁移审查** ✅ 已审计（2026-11-10） | 全源码零命中 `setAttributeViewBlockAttr`/`rowID`/`itemID`；AV 写入仅走 `/api/av/addAttributeViewBlocks`（avID/blockID/srcs[].id 当前 schema）——**无需迁移**；今后新增 AV 单元格写值功能时须直接用 itemID |
| 0.3 | 3.8.10-rc 兼容性回归（token/模板 HTTP/riff） | rc.3 已出，正式版发布前完成回归 |

### P0 · 发现性战役（对齐「功能多但不会用」主线）

1. ✅ **首启 30 秒演示动作回路**（b6b7bc1）：Bento 首次打开空状态展示 3 张预填示例卡（日记/待办/闪卡），「▶ 试试看」一键真实写入 + toast 回执，完成后自动换成真数据。（学 Raycast Walkthrough + CmdPal Home）
2. ✅ **Fallback 兜底命令**（8d88e16，含 🤖 问 AI）：搜索无匹配时底部固定 3 条兜底——发送『输入』到便签 / 全文搜索『输入』 / 问 AI『输入』。任何输入必然有用。（学 CmdPal，兜底可管理排序）
3. ✅ **动作广场**（a046780，前缀/功能卡/Webhook 三域可搜索一键执行）：把 142 op + 34 webhook 包装成可浏览、可搜索、一键试装的动作库；每个动作配演示 GIF + `siyuan-exoskeleton://add-action?id=...` 一键导入。（学 Quicker 广场把学习成本降到近零）
4. ✅ **5 任务新手指引**（a046780，托盘首项 @onboard 通道 + 五动作自动打勾）：托盘第一项「新手上路」：唤面板→存便签→剪网页→全文搜索→AI 路由，逐项打勾，全通解锁主题彩蛋；B 站/图文只作页脚兜底。
5. ✅ **逐模块空状态**（P0-2 泛空状态兜底 + b:/d: 专属空状态，其余前缀此前已有专属空态）：每模块空态 = 一句「这能干嘛」+ 真实可点示例 + 热键徽章；检索空结果推荐「试试：全文搜索『外骨骼』」。
6. ✅ **三段式唤起矩阵**：组合热键（现有）+ 双击 Ctrl（DoubleTapHotkeyActivator 已实现）+ 选区上下文唤起（热键/双击 Ctrl 打开面板时自动带入选区，URL 一键收藏） **选中内容上下文模式**（选中文本/文件后唤起 → 自动带入选区，面板只显示类型匹配动作）。

### P1 · 能力补位（社区有 issue 背书、集市空白的 4+1 个空白）

1. ✅ **参数化写入深链**（ParameterizedWriteBridge）：`siyuan-exoskeleton://write` + REST `/api/write` 双入口，复用 SYS 分流管线（>>小节/>@直投/task/flash），动作广场可复制模板；**回执已闭环**：六分支写库点回传块 ID，/api/write 响应 blockId+jumpUrl(siyuan://blocks/{id})、MCP siyuan_write 同步带跳转、E2E 按 ID 真查块验证
2. ✅ **系统级提醒守护**（ReminderGuardian）：10 分钟轮询到期闪卡(getRiffDueCards) + 📅/due: 截止日任务 → 托盘系统气泡（思源不在前台也能收）→ 点击智能跳转（fsrs→三秒微刷卡 / tasks→t: 待办视界）；数量指纹防轰炸、每日每状态一次、隔离靶场禁启
3. ✅ **OCR 写回闭环**：胶囊图文入日记时把识别文字挂为块 IAL 属性 `custom-ocr-text`（正文本身即可编辑，补 #13714 编辑诉求）；新增 `ocr: 关键词` 秒搜域（BentoWindow searchOcr op 经 attributes.block_id JOIN 结构化检索，E2E 真机验证）；动作广场收录 ocr: 前缀
4. ✅ **剪藏模板路由**：域名路由已落地（AppSettings.ClipDomainRules 每行「域名 => /目标目录/」，SmartCaptureMain.ResolveClipDomainFolder 精确+子域匹配覆盖研读专页目录，偏好 UI 可编辑，反射真机验证）；模板引擎此前已有（callout/yaml/minimal/custom 四预设 + {{smartTag}} 变量）；片段/批注模式已有（g:）；图片防盗链本地化已有（CacheAndUploadRemoteImage）；新增「3 天未读回顾」托盘气泡（ReminderGuardian 第三检查 → d:读库）
5. ✅ **剪贴板胶囊多格式与高保真富文本转MD**：Pin 置顶已落地（ClipboardItem.IsPinned/ClipKey、TrimRecentToCapacity 置顶清除保护、DPAPI 加密 pinned_clips.json 重启还原、重复复制继承置顶态、c: 列表行内一键 📌）+ 🧹 纯文本直贴（clipToPlainText 剥离 MD/HTML）+ **📝 转标准MD（TextCleaner.ConvertHtmlToMarkdown 完整支持表格 `<table>` 转标准 Markdown 矩阵、代码块语言 ` ```lang `、有序列表 `1. 2.`、任务清单 `- [x]`、删除线 `~~` 与高亮 `==`；c: 列表行内「📝 复MD」一键清洗写入剪贴板；ClipboardWatcher 自动语义嗅探）**；历史搜索（c: + 6 类筛选）与多条合并（拼版工作台）此前已有
6. ✅ **轻量同步安全守护**（SafeSyncGuardian）：深度审计 `muhanstudio/siyuan-sync-aware` 缺陷避坑落地；严守「纯辅助、零破坏、极度克制」三原则，坚决不篡改官方设置、不走第三方公网服务器、不替用户强行覆盖；实现 4 级安全门禁：断网离线防御（NetworkInterface.GetIsNetworkAvailable 静默跳过）、进程级互斥重入锁（Interlocked 互斥）、内核任务探针（getSyncCheckout task 空闲就绪才允许调用）、10 秒防抖保护；Win32 休眠唤醒保护（PowerModes.Resume 8 秒静默延迟等待网卡就绪）；**灵动坞展开态底部嵌入「同步护航微状态条」（绿/灰微光点 + 30秒节流异步只读感知刷新 + 一键安全同步）** + 右键动态相对时间微感知（`🔄 立即增量同步思源 (上次: 刚刚 / 5分钟前)`）消除盲点；**选区微光药丸补齐 Per-Monitor DPI 与上边缘防截断智能翻转，支持鼠标中键秒搜思源全库笔记**，E2E 281/281 验证通过
7. ✅ **全盘三大隐患彻底化解**：① **认知负荷降维**：搜索框前缀胶囊直铺全量高频秒搜域（`ocr:`/`ai:`/`c:`/`b:`/`d:`/`att:` 直点免记）+ 首页新增「⚡ 极简聚焦 (6 核心)」一键切换与偏好持久化记忆，心智负担降至最低；② **Win32 钩子摩擦消除**：`SelectionHelper` 增补 `IsSuspended` 软开关与全屏独占应用（Direct3D 游戏/影音）0 开销短路放行，托盘右键与偏好设置联动 `UnhookWindowsHookEx` 物理热卸载；③ **版本演进摩擦消除**：`SiYuanClient` 新增 `ProbeKernelCapabilitiesAsync` 异步嗅探思源内核版本（3.8.9/3.8.10）与连通健康，`ExternalApiBridge` `/status` 回传 `kernel_version` 与 `is_v3810_ready`，E2E 282/282 验证通过
8. ✅ **易用性与认知减负纵深落地**：搜索框输入态升级「直白动作分流雷达」（实时高亮呈现 `[↵ Enter] 📥 记日记`、`[⇧↵ Shift+Enter] 🔍 搜全库`、`[⌃↵ Ctrl+Enter] 📋 存待办`、`[Tab] 🤖 问AI` 4 大笃定去向，彻底消除黑盒不确定感）；搜索框直配「💡 怎么用」按钮 + 键入单个「?」或「help」秒唤「外骨骼常见使用场景与速查手册」模态视窗（以随手速记/搜笔记贴双链/划词盲打/网页截屏/问AI 5 大真实生活场景组织，去黑话大白话）；占位文案人性化动作引导升级，E2E 283/283 验证通过

### P2 · 生态与 AI 接入

1. ✅ **MCP server 封装**：`思源外骨骼.exe --mcp` 无头 stdio NDJSON JSON-RPC（零 UI 零互斥），7 工具（siyuan_write/search_blocks/search_docs/today_entries/unfinished_tasks/due_cards_count/status）复用参数化写入桥与 kernel API；E2E 真机 spawn 验证握手+工具表+写入落库；坑：winexe 下 Console.InputEncoding 不可靠（GBK 乱码），必须 OpenStandardInput+UTF8 裸流。新增 GET /api/openapi OpenAPI 3.0 自描述端点（9 路径）+ docs/MCP-README.md 接入指南（Claude Desktop/ChatBox/Cursor 配置）
2. ✅ **AI 路由器**：`ai: 问题` 前缀 + 搜索框 Tab 一键路由 + 意图条「📤 AI 路由」；剪贴板交接范式（先复制提问再打开目标，粘贴即问——URL 预填不确定也不误事）；默认 4 目标（豆包/千问/Kimi/秘塔）偏好可编辑（{query} 占位模板）；后端 aiRouteLaunch op 仅 http(s) 白名单；E2E 回调对账抓漏 onAiRouteLaunched 已补
3. ✅ **Quicker/AHK/uTools 上架素材**（docs/QUICKER-README.md）：5 套 Quicker HTTP 动作配方（速记/剪藏/待办/闪卡/远程控制）+ AHK 片段 + Quicklink 指引 + 5 镜 30 秒演示 GIF 分镜 + 上架检查清单
4. ✅ **轻量浏览器扩展**（browser-extension/，MV3）：划词右键/整页收藏/批注弹窗 → 本地 /api/write (X-Token) 直写今日日记「🌐 网页剪藏」小节；选项页 Token/端口/小节可配 + 测试连接探针；数据全程 127.0.0.1 零云端——对标 Obsidian Clipper 片段+整页双模式

### 决策红线（明确不做）

- ❌ AI 聊天客户端 / 本地向量库（官方 embeddings+MCP+Agent 已覆盖，撞车）
- ❌ 数据库视图（思源 av 本身是强项）
- ❌ 移动端（官方 v3.7.0 已做闪念速记）
- ❌ 云同步/账号体系（本地优先是差异化根基）
- ❌ 破坏单文件零依赖标准（新能力一律 kernel API + Win32 原生实现）

### 工程护栏（持续）

单文件零依赖（csc 直编）· E2E 隔离靶场扩容至 300+ 断言 · 安全四件套（127.0.0.1 绑定 + token 固定时间比较 + 参数白名单 + 深链 payload 净化，记取 GHSA-6gx2-8gcr-x83f 教训）· 每发版跑 3.8.x 兼容回归 · 待机内存 ≤ 22 MB、0.0% 空闲 CPU 不回退（2026-11-10 复测：WorkingSet 12.1MB · 空闲 CPU 0.0% · 45 线程/894 句柄——新增提醒/剪贴板/MCP 三轮询后仍达标）。

---

## 关键来源

- 仓库盘点：D:\思源插件\思源小驴桌面助手（只读，2026-11-10）
- 思源：github.com/siyuan-note/siyuan（47.1k★）· docs/API.md · kernel/api/router.go · petal/siyuan.d.ts · releases v3.7.0/v3.8.0/v3.8.9/v3.8.10-rc.3 · GHSA-6gx2-8gcr-x83f · bazaar（645 插件）· issues #20319 #15002 #14414 #1266 #13714 #14841 #9214 #6655 #17080 #21332
- 竞品：u-tools.cn（MCP 专题）· getquicker.net（动作广场）· learn.microsoft.com（PowerToys CmdPal）· raycast.com（Windows 公测/AI）· listary.com · fluentsearch.net · sabrogden.github.io/Ditto · pastebar.app · cubox.pro · simpread.pro · mymind.com · obsidian.md/clipper · 豆包/千问/Kimi 桌面端
- Obsidian 插件：quickadd.obsidian.guide · silentvoid13.github.io/Templater · Advanced URI 文档 · community.obsidian.md/plugins/cmdr · coddingtonbear/obsidian-local-rest-api · uphy/obsidian-reminder · scambier/obsidian-omnisearch · scambier/obsidian-text-extractor · obsidianmd/obsidian-clipper · logancyang/obsidian-copilot · brianpetro/obsidian-smart-connections · obsidian.md/help/bases
