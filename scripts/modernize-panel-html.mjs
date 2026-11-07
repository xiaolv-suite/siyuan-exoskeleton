import fs from 'fs';

const panelPath = 'D:/思源插件/思源小驴桌面助手/assets/panel.html';
let html = fs.readFileSync(panelPath, 'utf8');

// 1. Update Title and Brand Header
html = html.replace(
  /<title>[\s\S]*?<\/title>/,
  '<title>思源小驴 · 桌面穿透工作台 (XiaoLv Desktop)</title>'
);

// 2. Add quick-intent-chips CSS
const chipsCss = `
    /* Quick Intent Chips */
    .quick-intent-chips {
      display: flex;
      align-items: center;
      gap: 6px;
      margin-top: 8px;
      flex-wrap: wrap;
    }
    .intent-chip {
      font-size: 11px;
      padding: 3px 9px;
      border-radius: 6px;
      background: var(--key-badge-bg);
      border: 1px solid var(--border-hairline);
      color: var(--text-secondary);
      cursor: pointer;
      display: inline-flex;
      align-items: center;
      gap: 4px;
      transition: all 0.15s ease;
      user-select: none;
    }
    .intent-chip:hover {
      border-color: var(--indigo-light);
      color: var(--indigo-light);
      background: rgba(99, 102, 241, 0.08);
      transform: translateY(-1px);
    }
    .intent-chip b {
      color: var(--indigo-light);
      font-family: ui-monospace, monospace;
      font-weight: 700;
    }
`;

if (!html.includes('.quick-intent-chips')) {
  html = html.replace('</style>', chipsCss + '\n  </style>');
}

// 3. Modernize Header
const oldHeaderRegex = /<header class="telemetry-header">[\s\S]*?<\/header>/;
const newHeader = `<header class="telemetry-header">
    <div class="brand-wrap">
      <div class="brand-logo" title="思源小驴 · 桌面穿透工作台">
        <img src="icons/xiaolv-master-logo.svg" alt="思源小驴" class="brand-logo-img" onerror="this.outerHTML='⚡';" />
      </div>
      <div class="brand-title">
        <span>思源小驴 · 桌面穿透工作台</span>
        <span class="brand-pill">v1.6.0</span>
      </div>
    </div>
    <div class="telemetry-right">
      <div class="telemetry-pills">
        <div class="t-pill" id="telemetry-kernel" title="思源笔记本地通信内核状态">
          <span class="dot-live"></span>
          <span id="telemetry-kernel-text">思源内核已就绪</span>
        </div>
      </div>

      <button class="theme-toggle-btn" id="help-btn" onclick="toggleCheatsheet()" title="快捷心流与语法罗盘 (?)">⌨️</button>
      <button class="theme-toggle-btn" id="customize-btn" onclick="togglePreferencesModal()" title="设置中枢 (⚙)">⚙️</button>
      <button class="theme-toggle-btn" id="theme-btn" onclick="toggleTheme()" title="切换浅色/深色主题">🌓</button>
      <div class="top-nav-divider"></div>
      <button class="theme-toggle-btn window-close-btn" id="window-close-btn" onclick="closeWindow()" title="关闭退出面板 (Esc)">✕</button>
    </div>
  </header>`;
html = html.replace(oldHeaderRegex, newHeader);

// 4. Modernize Search Section with Quick Intent Chips
const oldSearchRegex = /<section class="search-section">[\s\S]*?<\/section>/;
const newSearch = `<section class="search-section">
    <div class="search-box">
      <div class="search-icon">
        <svg class="icon-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="11" cy="11" r="8"></circle>
          <line x1="21" y1="21" x2="16.65" y2="16.65"></line>
        </svg>
      </div>
      <input
        type="text"
        id="search-input"
        class="search-input"
        placeholder="键入速记内容 (!待办, @人脉, ?闪卡, #项目) 或快速检索 (b:块, d:文档, p:插件)..."
        autocomplete="off"
        spellcheck="false"
        autofocus
      />
      <div class="search-shortcuts">
        <span class="key-badge">↵ 执行</span>
        <span class="key-badge">Shift+↵ 穿透回贴</span>
        <span class="key-badge">Esc 退出</span>
      </div>
    </div>

    <!-- Quick Intent Chips below the search box -->
    <div class="quick-intent-chips" id="quick-intent-chips">
      <span class="intent-chip" onclick="insertPrefix('!')" title="输入 ! 快速将待办事项存入今日日记"><b>!</b> 待办</span>
      <span class="intent-chip" onclick="insertPrefix('@')" title="输入 @ 记录人脉互动与双链时间线"><b>@</b> 人脉</span>
      <span class="intent-chip" onclick="insertPrefix('?')" title="输入 ? 制作原生 FSRS 闪卡"><b>?</b> 闪卡</span>
      <span class="intent-chip" onclick="insertPrefix('#')" title="输入 # 归入项目专页或今日日记"><b>#</b> 项目</span>
      <span class="intent-chip" onclick="insertPrefix('b:')" title="输入 b: 毫秒级检索思源正文块并直接复制/回贴双链"><b>b:</b> 块级秒搜</span>
      <span class="intent-chip" onclick="insertPrefix('d:')" title="输入 d: 检索思源全库文档"><b>d:</b> 文档</span>
      <span class="intent-chip" onclick="insertPrefix('p:')" title="输入 p: 穿透检索全部已装社区插件"><b>p:</b> 插件</span>
      <span class="intent-chip" style="margin-left: auto; opacity: 0.7; cursor: pointer;" onclick="toggleCheatsheet()" title="查看完整快捷键心流"><b>Alt+1~9</b> 官方直达</span>
    </div>

    <div id="intent-banner" class="intent-banner" style="display: none;"></div>
  </section>`;
html = html.replace(oldSearchRegex, newSearch);

// 5. Modernize Controls Bar (Category Tabs)
const oldControlsRegex = /<div class="controls-bar">[\s\S]*?<\/div>/;
const newControls = `<div class="controls-bar">
    <nav class="category-tabs" id="category-tabs">
      <button class="cat-btn active" id="cat-btn-all" onclick="setCategoryFilter('all')">全部 (24)</button>
      <button class="cat-btn" onclick="setCategoryFilter('capture')">📝 捕获速记 (4)</button>
      <button class="cat-btn" onclick="setCategoryFilter('search')">🔍 检索双链 (4)</button>
      <button class="cat-btn" onclick="setCategoryFilter('learning')">🧩 官方生态 (9 · Alt+1~9)</button>
      <button class="cat-btn" onclick="setCategoryFilter('commands')">🌐 扩展心流 (4)</button>
      <button class="cat-btn" onclick="setCategoryFilter('maintenance')">⚙️ 运维诊断 (3)</button>
    </nav>
  </div>`;
html = html.replace(oldControlsRegex, newControls);

// 6. Modernize Cheatsheet Modal
const oldCheatsheetRegex = /<div class="help-modal-overlay" id="cheatsheet-modal"[\s\S]*?<\/div>\s*<\/div>\s*<\/div>/;
const newCheatsheet = `<div class="help-modal-overlay" id="cheatsheet-modal" onclick="closeCheatsheetOnBg(event)">
    <div class="help-modal-box" onclick="event.stopPropagation()" style="max-width: 640px;">
      <div class="help-header">
        <div style="display: flex; align-items: center; gap: 8px;">
          <span style="font-size: 16px;">⌨️</span>
          <span style="font-weight: 700; font-size: 14px;">思源小驴 · 快捷键与心流罗盘</span>
        </div>
        <button class="theme-toggle-btn" style="width: 26px; height: 26px; font-size: 12px;" onclick="toggleCheatsheet()">✕</button>
      </div>
      <div class="help-body" style="gap: 14px;">
        <div>
          <div class="help-sec-title"><span>🎯 桌面四大全局热键 (任何软件随处呼出)</span></div>
          <table class="help-table">
            <tr><td><span class="hint-kbd">Alt + Shift + Q</span></td><td><b>桌面穿透工作台</b>：0ms 呼出当前 24 项全能罗盘</td></tr>
            <tr><td><span class="hint-kbd">Alt + Shift + W</span></td><td><b>随身划笔微胶囊</b>：划选文字 1~6 盲打流秒入思源</td></tr>
            <tr><td><span class="hint-kbd">Alt + Shift + C</span></td><td><b>截屏离线 OCR</b>：调动 PixPin / Snipaste / 原生硬件 OCR</td></tr>
            <tr><td><span class="hint-kbd">Alt + Shift + S</span></td><td><b>极简灵感速记窗</b>：敲字回车瞬入今日日记</td></tr>
          </table>
        </div>

        <div>
          <div class="help-sec-title"><span>🧩 官方生态插件秒开 (Alt + 1~9 跨软件直开)</span></div>
          <table class="help-table">
            <tr><td><span class="hint-kbd">Alt + 1</span></td><td>唤醒思源并打开【小驴打卡】打卡大窗</td></tr>
            <tr><td><span class="hint-kbd">Alt + 2</span></td><td>唤醒思源并打开【小驴管家】生活事务总控</td></tr>
            <tr><td><span class="hint-kbd">Alt + 3</span></td><td>唤醒思源并打开【小驴人脉】人脉档案工作台</td></tr>
            <tr><td><span class="hint-kbd">Alt + 4</span></td><td>唤醒思源并打开【小驴拾遗】读库稍后读侧栏</td></tr>
            <tr><td><span class="hint-kbd">Alt + 5</span></td><td>唤醒思源并打开【小驴常用】常用资产条目库</td></tr>
            <tr><td><span class="hint-kbd">Alt + 6</span></td><td>唤醒思源并打开【小驴闪卡】FSRS 闪卡复习中心</td></tr>
            <tr><td><span class="hint-kbd">Alt + 7</span></td><td>唤醒思源并打开【小驴学习】(原考试) 练习台</td></tr>
            <tr><td><span class="hint-kbd">Alt + 8</span></td><td>唤醒思源并打开【小驴雷切】快速标签切换器</td></tr>
            <tr><td><span class="hint-kbd">Alt + 9</span></td><td>唤醒思源并打开【小驴快门】中枢网关设置面板</td></tr>
          </table>
        </div>

        <div>
          <div class="help-sec-title"><span>⚡ 极速意图前缀语法 (直接在搜索框键入)</span></div>
          <table class="help-table">
            <tr><td><span class="hint-kbd">! 事项</span></td><td>自动将「- [ ] 事项」作为待办任务存入今日日记</td></tr>
            <tr><td><span class="hint-kbd">@ 联系人 备注</span></td><td>记录与联系人的互动档案并建立思源双链时间线</td></tr>
            <tr><td><span class="hint-kbd">? 问题 // 答案</span></td><td>自动生成问答闪卡放入今日日记与 FSRS 复习库</td></tr>
            <tr><td><span class="hint-kbd"># 项目 进展</span></td><td>精准追加归流至对应的项目专页或今日日记</td></tr>
            <tr><td><span class="hint-kbd">b: 关键词</span></td><td><b>块级毫秒级秒搜</b>：全库正文/代码/标题，回车定位</td></tr>
            <tr><td><span class="hint-kbd">d: 关键词</span></td><td><b>文档标题秒搜</b>：快速匹配思源文档并一键打开</td></tr>
            <tr><td><span class="hint-kbd">p: 关键词</span></td><td><b>社区插件穿透</b>：动态检索思源内已装的任意第三方命令</td></tr>
          </table>
        </div>

        <div>
          <div class="help-sec-title"><span>🚀 极客键盘操作</span></div>
          <table class="help-table">
            <tr><td><span class="hint-kbd">Enter</span></td><td>执行选中卡片 / 打开文档原文</td></tr>
            <tr><td><span class="hint-kbd">Shift + Enter</span></td><td><b>光标穿透直贴</b>：将思源双链 ((id)) 直接粘贴到外部应用光标处</td></tr>
            <tr><td><span class="hint-kbd">Esc</span></td><td>关闭当前弹窗或退出总控面板</td></tr>
          </table>
        </div>
      </div>
    </div>
  </div>`;
html = html.replace(oldCheatsheetRegex, newCheatsheet);

// 7. Update handleSearch to show/hide quick-intent-chips
if (!html.includes('quick-intent-chips')) {
  html = html.replace(
    'function handleSearch(query) {',
    `function handleSearch(query) {
      const chips = document.getElementById("quick-intent-chips");
      if (chips) chips.style.display = query.trim().length > 0 ? "none" : "flex";`
  );
}

// 8. Update Footer Hints
const oldFooterRegex = /<footer class="panel-footer">[\s\S]*?<\/footer>/;
const newFooter = `<footer class="panel-footer">
    <div class="footer-hints">
      <div class="hint-item"><span class="hint-kbd">Alt+Shift+Q</span> 呼出</div>
      <div class="hint-item"><span class="hint-kbd">Alt+1~9</span> 官方直达</div>
      <div class="hint-item"><span class="hint-kbd">b:</span> 块级秒搜</div>
      <div class="hint-item"><span class="hint-kbd">Shift+Enter</span> 穿透回贴双链</div>
      <div class="hint-item"><span class="hint-kbd">Esc</span> 退出</div>
    </div>
    <div id="footer-count" style="font-family: monospace; font-weight: 600;">共 24 项能力 · 就绪</div>
  </footer>`;
html = html.replace(oldFooterRegex, newFooter);

fs.writeFileSync(panelPath, html, 'utf8');
console.log('Modernized panel.html written successfully! Size:', html.length, 'bytes');
