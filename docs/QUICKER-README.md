# 思源外骨骼 · Quicker / AHK / uTools 动作上架素材（P2-3）

外骨骼的本地 Webhook（`http://127.0.0.1:43989`，鉴权头 `X-Token`，完整机器可读规范 `GET /api/openapi`）就是为启动器生态准备的标准件。以下每条都是「复制即配」的成品配方——在 Quicker 动作广场发布时，把对应配置写进步骤说明即可。

> 前置：思源外骨骼桌面端运行中；Token 在「偏好设置 → Webhook」查看。下文 `<TOKEN>` 均指该令牌。

---

## 一、Quicker 动作配方（HTTP 请求步骤）

Quicker 新建动作 → 添加步骤类型「HTTP 请求」，按下表填：

### 配方 1：速记到思源（选中文字一键入日记）
- 方法：`POST` · URL：`http://127.0.0.1:43989/api/capture`
- 请求头：`X-Token: <TOKEN>`
- Body (application/json)：`{"text": "{{选中文本}}"}`

> 建议动作图标 📝、说明文案「选中即入思源日记，免切窗」。可绑定 Quicker 中键/悬浮球。

### 配方 2：网页剪藏到思源（浏览器当前页）
- 方法：`POST` · URL：`http://127.0.0.1:43989/api/write`
- 请求头：`X-Token: <TOKEN>`
- Body：`{"target":"daily","heading":"🌐 网页剪藏","data":"- 🌐 [{{窗口标题}}]({{剪贴板URL}})"}`

> 配合 Quicker「获取浏览器 URL」步骤把当前页填入 `{{剪贴板URL}}`。

### 配方 3：一键建待办
- 方法：`POST` · URL：`http://127.0.0.1:43989/api/write`
- Body：`{"action":"task","data":"{{选中文本或输入}}"}`

### 配方 4：闪卡速制（问题 // 答案）
- 方法：`POST` · URL：`http://127.0.0.1:43989/api/write`
- Body：`{"action":"flash","data":"{{选中文本}}"}`

### 配方 5：任务勾销 / 专注启航（远程控制）
- 勾销：`POST /api/task/complete` · Body：`{"blockId":"{{思源块ID}}"}`
- 专注：`GET /api/focus`（状态探针）· `POST /api/focus`（启航）

---

## 二、AutoHotkey 片段（双击 Ctrl 之外的自定义热键）

```autohotkey
; Win+Shift+S：选中文本 → 思源速记
#+s::
{
    A_Clipboard := ""
    Send "^c"
    ClipWait 0.6
    req := ComObject("WinHttp.WinHttpRequest.5.1")
    req.Open("POST", "http://127.0.0.1:43989/api/capture", false)
    req.SetRequestHeader("X-Token", "<TOKEN>")
    req.SetRequestHeader("Content-Type", "application/json")
    body := '{"text":"' A_Clipboard '"}'
    req.Send(body)
    TrayTip "思源外骨骼", "✓ 已速记入日记"
}
```

---

## 三、uTools / Raycast Quicklink

- uTools 自定义命令 / Raycast Quicklink：URL 模板 `http://127.0.0.1:43989/api/write`（需带 header 的场景建议用 Quicker/AHK；纯打开场景可用 `ai:` 路由或 `siyuan-exoskeleton://write?...` 深链，data 需 URL 编码）。

---

## 四、动作广场演示 GIF 分镜脚本（5 镜 30 秒）

| 镜 | 时长 | 画面 | 强调点 |
|---|---|---|---|
| 1 | 4s | 任意软件选中文本 → Alt+Shift+W 胶囊弹出 → 按 1 | 「免切窗，1 键入日记」 |
| 2 | 6s | 浏览器按 Alt+Shift+G → 剪藏卡片流落库 → 思源侧自动出现 | 「0 扩展剪藏 + 防盗链图片本地化」 |
| 3 | 6s | Alt+Shift+C 截图 → OCR 识别 → 按 1 图文入日记 → 面板 `ocr:` 秒搜图中文字 | 「识字可检索 (#13714)」 |
| 4 | 7s | 搜索框打字 → Tab → 浏览器弹出豆包且提问已在剪贴板 | 「AI 路由，粘贴即问」 |
| 5 | 7s | Claude Desktop/ChatBox 里 Agent 调用 siyuan_write → 思源日记出现新待办 | 「MCP：Agent 自动调用本地能力」 |

---

## 五、上架检查清单

- [ ] 动作说明首行写明前置：「需运行思源外骨骼桌面助手（本地 127.0.0.1:43989）」
- [ ] Token 提示用户从「偏好设置 → Webhook」复制，不要硬编码在公开动作里
- [ ] 附本文件第一节对应配方的截图
- [ ] 动作详情页挂第四节 GIF
- [ ] 版本号与外骨骼 v3.x 对齐，CHANGELOG 注明依赖端点（/api/capture、/api/write…）
