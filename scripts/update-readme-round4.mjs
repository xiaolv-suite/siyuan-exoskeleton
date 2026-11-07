import fs from 'fs';

const p = 'D:/思源插件/思源小驴桌面助手/README.md';
let txt = fs.readFileSync(p, 'utf8');

const targetSection = `17. **★ 截图与离线 OCR 图文双通道落盘 (Screenshot + OCR Dual Ingestion)**：
   - 彻底解决“截图识别完文字就把原图丢弃”的遗憾：按下 \`Alt+Shift+C\` 选区截屏后，不仅通过 WinRT 原生硬件加速瞬间提取文字，同时自动保留高保真原图；
   - 随身划笔胶囊弹出后敲击 \`1\` 键，自动将截图上传至思源资产库并嵌入图文块：\`![- 截图](assets/...) > 提取文本\`，图文并茂沉淀今日日记；
18. **★ 一键隔离真机测试靶场 (\`pnpm e2e:isolated\`)**：
   - 深度对齐小驴插件生态自动化标准，在独立临时工作区（\`.isolated-workspaces/xiaolv-desktop-isolated\`）与随机回环端口（如 43988）拉起全新真实思源 3.8.9 内核；
   - 全程对思源小驴桌面助手开展 **18 步无损真机自动化验收**：首启冷启动、日记笔记本初始化、单条速记、30秒连续粘滞合并、上下文感知与 IAL 注水、动态内联小节路由 (>>)、待办/闪卡意图分流、FSRS 原生评级闭环、b:块级秒搜、全库断链与游离附件清道夫 (syh)、全局双链认知雷达嗅探与反查断言、偏好设置持久化验证、模拟内核停机离岸缓冲入队、内核重启自动回写，以及沙箱安全销毁全链路闭环，绝不污染用户真实笔记！

---

## 三、 运行与系统集成

- **可执行程序**：\`D:\\思源插件\\思源小驴桌面助手\\bin\\Release\\思源小驴.exe\`（**2.90 MB** 纯单文件便携版）
- **自动化测试命令**：\`pnpm e2e:isolated\`（18 项真内核端到端 100% 自动化全绿通过）
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

const idx = txt.indexOf('17. **★ 一键隔离真机测试靶场');
if (idx > 0) {
  txt = txt.substring(0, idx) + targetSection + '\n';
  fs.writeFileSync(p, txt, 'utf8');
  console.log('README.md updated with Round 4 features!');
} else {
  console.log('Target section 17 not found');
}
