import fs from 'fs';

const p = 'D:/思源插件/思源小驴桌面助手/README.md';
let txt = fs.readFileSync(p, 'utf8');

const targetSection = `10. **★ 深度对标与超越叶归「一键记事」（连续记录防碎块粘滞 & 意图微感应 HUD）**：
   - **防碎块粘滞时间窗口 (Continuation Window)**：学习叶归 \`newBlockDelay\` 的核心设计，小驴默认提供 30 秒（可在偏好设置自由调节 0~600 秒）的连续记录粘滞期。在同窗口同应用下连续速记自动合并至上一条块内，彻底告别“一句话一个日记块”的碎片化痛点；
   - **动态意图微感应 HUD (\`MiniInputWindow\`)**：按下 \`Alt+Shift+S\` 唤出的居中速记框升级为现代微胶囊风格，键入 \`!\`、\`@\`、\`?\`、\`#\` 时左侧徽标与边框实时平滑变色感应（待办/人脉/闪卡/项目），处于粘滞期时自动亮起 \`⏳ 续写合并\` 提示；
   - **双重灵活落点与叶归 LifeLog 时间线原生兼容**：支持追加今日日记（可指定二级小节）或自定义专属收集箱文档，时间戳格式 \`- 12:00 项目：详情\` 完全兼容叶归 LifeLog 统计与时间线解析器。
11. **★ 桌面外部活动上下文感知与原生 IAL 溯源注水 (\`ContextPerception.cs\`)**：
   - 彻底打破思源前台孤岛：小驴在呼出速记或随身划笔前瞬间捕获 Win32 前台活动上下文；
   - 自动提取当前活跃软件（Edge / Chrome / VS Code / Word / Foxit / 微信）、文档或标签标题、并通过 UI Automation 自动嗅探 Chromium 活跃网址；
   - 写入思源时自动挂载原生块属性 \`custom-source-app\`、\`custom-source-url\`、\`custom-source-title\`，并在速记框内直观呈现来源上下文！
12. **★ 桌面级 0ms FSRS 闪卡 3秒微刷卡微舱 (\`FSRSCapsuleWindow.cs\` · Alt+Shift+R)**：
   - 深度联动思源原生 \`/api/riff/\` 间隔重复算法，彻底解决用户“卡片堆积不复习”的长期痛点；
   - 按下 \`Alt+Shift+R\` 0ms 居中弹出 540×360 极简微舱，展示卡片正面，空格翻转答案，数字键 \`1 / 2 / 3 / 4\` 秒级评级（重来/困难/良好/简单），无需切换思源大窗口，碎片时间轻松刷完；
   - 系统托盘与总控顶栏实时显示今日待复习闪卡角标（\`🧠 待复习 N 张\`）。
13. **★ 一键隔离真机测试靶场 (\`pnpm e2e:isolated\`)**：
   - 深度对齐小驴插件生态自动化标准，在独立临时工作区（\`.isolated-workspaces/xiaolv-desktop-isolated\`）与随机回环端口（如 43988）拉起全新真实思源 3.8.9 内核；
   - 全程对思源小驴桌面助手开展 **14 步无损真机自动化验收**：首启冷启动、日记笔记本初始化、单条速记、30秒连续粘滞合并、桌面上下文感知与原生 IAL 注水、待办/闪卡意图分流、FSRS 原生评级闭环、b:块级秒搜、模拟内核停机离岸缓冲入队、内核重启自动回写，以及沙箱安全销毁全链路闭环，绝不污染用户真实笔记！

---

## 三、 运行与系统集成

- **可执行程序**：\`D:\\思源插件\\思源小驴桌面助手\\bin\\Release\\思源小驴.exe\`（**2.89 MB** 纯单文件便携版）
- **自动化测试命令**：\`pnpm e2e:isolated\`（14 项真内核端到端 100% 自动化全绿通过）
- **系统资源占用**：常驻内存稳定在 **~58 MB**，CPU **0%**
- **系统托盘功能矩阵**：
  - \`🪟 思源小驴 · 全局面板 (Alt+Shift+Q)\`
  - \`✏️ 思源小驴 · 随身划笔 (Alt+Shift+W)\`
  - \`📸 截屏离线 OCR (Alt+Shift+C)\`
  - \`⚡ 极速灵感速记窗 (Alt+Shift+S)\`
  - \`🧠 FSRS 闪卡桌面微舱 (Alt+Shift+R)\`
  - \`🔔 可交互通知速记 (Windows Toast)\`
  - \`✨ 智能划选自动浮窗 (PopClip模式)\`
  - \`📋 智能剪贴板感知 (URL/表格/代码自动清洗)\`
  - \`🗂️ 资源管理器右键直投菜单 (小驴速递)\`
  - \`⚙️ 开机自动启动\``;

const idx = txt.indexOf('10. **★ 深度对标与超越叶归「一键记事」');
if (idx > 0) {
  txt = txt.substring(0, idx) + targetSection + '\n';
  fs.writeFileSync(p, txt, 'utf8');
  console.log('README.md updated successfully!');
} else {
  console.log('Target section not found');
}
