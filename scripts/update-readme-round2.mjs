import fs from 'fs';

const p = 'D:/思源插件/思源小驴桌面助手/README.md';
let txt = fs.readFileSync(p, 'utf8');

const targetSection = `13. **★ 全局双链认知雷达与随身划笔穿透 (\`CapsuleWindow.cs\` · Alt+Shift+W)**：
   - 划选即知知识关联：在任意软件（Word、PDF、浏览器、微信）划选词汇呼出微胶囊时，小驴以 3ms 极速反查思源 SQLite 数据库；
   - 若词汇已在思源建档收录，胶囊顶栏瞬间亮起翠绿微徽章：\`🔗 已收录: 《深度学习笔记》 (Shift+↵ 贴双链)\`，按 \`Shift+Enter\` 即可将双链 \`((id "词汇"))\` 直接注入外部光标，按 \`4\` 原地跳转思源；
14. **★ 桌面原生拖拽秒入库 (Native Drag & Drop Ingestion)**：
   - 支持将桌面或浏览器中的任意图片、PDF、Word 或代码文件直接拖入 Bento 总控窗口或胶囊窗口；
   - 自动上传至思源资产库并规范创建附件块沉淀进今日日记，完全摆脱手动切窗导入繁琐动作；
15. **★ 动态内联子标题小节路由 (\`>>小节名\`)**：
   - 在速记框输入以 \`>>小节名\` 或 \`##小节名\` 开头的内容（如 \`>>临时闪念 这是灵感\`），小驴自动精准追加至今日日记对应的二级小节下方，实现动态换轨；
16. **★ 一键隔离真机测试靶场 (\`pnpm e2e:isolated\`)**：
   - 深度对齐小驴插件生态自动化标准，在独立临时工作区（\`.isolated-workspaces/xiaolv-desktop-isolated\`）与随机回环端口（如 43988）拉起全新真实思源 3.8.9 内核；
   - 全程对思源小驴桌面助手开展 **16 步无损真机自动化验收**：首启冷启动、日记笔记本初始化、单条速记、30秒连续粘滞合并、上下文感知与 IAL 注水、动态内联小节路由 (>>)、待办/闪卡意图分流、FSRS 原生评级闭环、b:块级秒搜、全库断链与游离附件清道夫 (syh)、模拟内核停机离岸缓冲入队、内核重启自动回写，以及沙箱安全销毁全链路闭环，绝不污染用户真实笔记！

---

## 三、 运行与系统集成

- **可执行程序**：\`D:\\思源插件\\思源小驴桌面助手\\bin\\Release\\思源小驴.exe\`（**2.89 MB** 纯单文件便携版）
- **自动化测试命令**：\`pnpm e2e:isolated\`（16 项真内核端到端 100% 自动化全绿通过）
- **系统资源占用**：常驻内存稳定在 **~59 MB**，CPU **0%**
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

const idx = txt.indexOf('13. **★ 一键隔离真机测试靶场');
if (idx > 0) {
  txt = txt.substring(0, idx) + targetSection + '\n';
  fs.writeFileSync(p, txt, 'utf8');
  console.log('README.md updated with Round 2 features!');
} else {
  console.log('Target section 13 not found');
}
