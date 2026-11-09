import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const pageUrl = pathToFileURL(path.join(root, "assets", "panel.html")).href;
const chromeCandidates = [
  process.env.CHROME_PATH,
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe"
].filter(Boolean);
const chromePath = chromeCandidates.find((candidate) => fs.existsSync(candidate));
if (!chromePath) throw new Error("Google Chrome executable was not found. Set CHROME_PATH to use the headless UI smoke test.");

async function reservePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => server.once("error", reject).listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return port;
}

const cdpPort = await reservePort();
const profileDir = fs.mkdtempSync(path.join(os.tmpdir(), "xiaolv-ui-smoke-"));
const browser = spawn(chromePath, [
  "--headless=new",
  "--disable-gpu",
  "--disable-extensions",
  "--disable-background-networking",
  "--disable-component-update",
  "--no-first-run",
  "--no-default-browser-check",
  "--allow-file-access-from-files",
  `--remote-debugging-port=${cdpPort}`,
  `--user-data-dir=${profileDir}`,
  "--window-size=1280,900",
  pageUrl
], { windowsHide: true, stdio: "ignore" });

let socket;
const pending = new Map();
let nextId = 0;

function send(method, params = {}) {
  const id = ++nextId;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Headless Chrome timed out on ${method}`));
    }, 10000);
    timer.unref();
    pending.set(id, { resolve, reject, timer });
    socket.send(JSON.stringify({ id, method, params }));
  });
}

async function waitForPage() {
  const endpoint = `http://127.0.0.1:${cdpPort}/json/list`;
  for (let attempt = 0; attempt < 60; attempt++) {
    if (browser.exitCode !== null) throw new Error(`Chrome exited early with code ${browser.exitCode}`);
    try {
      const targets = await (await fetch(endpoint)).json();
      const target = targets.find((item) => item.type === "page" && item.url === pageUrl && item.webSocketDebuggerUrl);
      if (target) return target;
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
  throw new Error("Timed out waiting for the headless Chrome page.");
}

async function evaluate(expression) {
  const response = await send("Runtime.evaluate", {
    expression,
    awaitPromise: true,
    returnByValue: true
  });
  if (response.result.exceptionDetails) {
    throw new Error(JSON.stringify(response.result.exceptionDetails));
  }
  return response.result.result.value;
}

try {
  const target = await waitForPage();
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  socket.addEventListener("message", (event) => {
    const message = JSON.parse(event.data);
    if (message.method === "Runtime.exceptionThrown") {
      const details = message.params?.exceptionDetails;
      console.error("Headless page exception:", details?.exception?.description || details?.text || details);
    }
    if (message.method === "Runtime.consoleAPICalled" && message.params?.type === "error") {
      console.error("Headless page console error:", message.params.args?.map(arg => arg.value || arg.description).join(" "));
    }
    if (!message.id) return;
    const request = pending.get(message.id);
    if (!request) return;
    pending.delete(message.id);
    clearTimeout(request.timer);
    if (message.error) request.reject(new Error(message.error.message));
    else request.resolve(message);
  });

  await send("Page.enable");
  await send("Runtime.enable");
  let uiReady = false;
  for (let attempt = 0; attempt < 50; attempt++) {
    uiReady = await evaluate(`document.readyState === 'complete' &&
      !!document.querySelector('.panel-footer') &&
      document.querySelectorAll('.card-item').length === 22`);
    if (uiReady) break;
    await new Promise((resolve) => setTimeout(resolve, 200));
  }
  assert.ok(uiReady, "Headless Chrome did not finish rendering the panel within 10 seconds.");

  const desktop = await evaluate(`(() => {
    const footer = document.querySelector('.panel-footer').getBoundingClientRect();
    const content = document.querySelector('.view-container').getBoundingClientRect();
    return {
      ready: document.readyState,
      width: innerWidth,
      documentWidth: document.documentElement.scrollWidth,
      contentBottom: content.bottom,
      footerTop: footer.top,
      activeCategory: document.querySelector('.category-tabs .cat-btn.active')?.id,
      visibleCards: document.querySelectorAll('.card-item').length
    };
  })()`);
  assert.equal(desktop.ready, "complete");
  assert.ok(desktop.documentWidth <= desktop.width, `Desktop page overflows horizontally: ${JSON.stringify(desktop)}`);
  assert.ok(desktop.contentBottom <= desktop.footerTop + 1, `Footer overlaps the scrollable content: ${JSON.stringify(desktop)}`);

  await send("Page.setDeviceMetricsOverride", { width: 920, height: 760, deviceScaleFactor: 1, mobile: false });
  const compact = await evaluate(`(() => {
    const tabs = document.querySelector('.category-tabs');
    const footer = document.querySelector('.panel-footer').getBoundingClientRect();
    const content = document.querySelector('.view-container').getBoundingClientRect();
    const chips = [...document.querySelectorAll('.intent-chip[onclick]')];
    return {
      width: innerWidth,
      documentWidth: document.documentElement.scrollWidth,
      categoryClientWidth: tabs.clientWidth,
      categoryScrollWidth: tabs.scrollWidth,
      categoryScrollbar: getComputedStyle(tabs).scrollbarWidth,
      searchActions: [...document.querySelectorAll('.search-shortcuts button:not(#search-clear-btn)')].map(button => {
        const r = button.getBoundingClientRect();
        return { left: r.left, right: r.right, visible: r.width > 0 && r.height > 0 };
      }),
      missingChipSemantics: chips.filter(chip => chip.getAttribute('role') !== 'button' || chip.tabIndex < 0).length,
      missingScopeSemantics: [...document.querySelectorAll('.scope-switch-pill[onclick]')]
        .filter(action => action.getAttribute('role') !== 'button' || action.tabIndex < 0).length,
      contentBottom: content.bottom,
      footerTop: footer.top,
      cardColumns: getComputedStyle(document.querySelector('.view-grid')).gridTemplateColumns
    };
  })()`);
  assert.equal(compact.width, 920);
  assert.ok(compact.documentWidth <= compact.width, `Compact page overflows horizontally: ${JSON.stringify(compact)}`);
  assert.ok(compact.categoryScrollWidth > compact.categoryClientWidth, "Compact category tabs should retain their horizontal overflow.");
  assert.equal(compact.categoryScrollbar, "thin", "Compact category overflow must have a visible scrollbar affordance.");
  assert.ok(compact.searchActions.every(action => action.visible && action.right <= compact.width),
    `Compact search action buttons must stay visible: ${JSON.stringify(compact.searchActions)}`);
  assert.equal(compact.missingChipSemantics, 0, "Every quick intent chip must be keyboard reachable and announced as a button.");
  assert.equal(compact.missingScopeSemantics, 0, "Rendered search scope switches must be keyboard reachable buttons.");
  assert.ok(compact.contentBottom <= compact.footerTop + 1, `Compact footer overlaps the scrollable content: ${JSON.stringify(compact)}`);

  const dynamicChipSemantics = await evaluate(`(async () => {
    const chip = document.createElement('span');
    chip.className = 'scope-switch-pill';
    chip.setAttribute('onclick', 'window.__headlessScopeActivated = true');
    chip.textContent = '动态检索域';
    document.body.appendChild(chip);
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const result = { role: chip.getAttribute('role'), tabIndex: chip.tabIndex };
    chip.remove();
    return result;
  })()`);
  assert.equal(dynamicChipSemantics.role, "button", "Dynamically rendered scope actions must receive button semantics.");
  assert.ok(dynamicChipSemantics.tabIndex >= 0, "Dynamically rendered scope actions must be keyboard reachable.");

  await send("Page.setDeviceMetricsOverride", { width: 880, height: 560, deviceScaleFactor: 1, mobile: false });
  const minimumWindow = await evaluate(`(() => {
    const rect = selector => {
      const el = document.querySelector(selector);
      const r = el.getBoundingClientRect();
      return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, visible: r.width > 0 && r.height > 0 };
    };
    return {
      width: innerWidth,
      documentWidth: document.documentElement.scrollWidth,
      search: rect('.search-box'),
      searchActions: [...document.querySelectorAll('.search-shortcuts button:not(#search-clear-btn)')].map(button => {
        const r = button.getBoundingClientRect();
        return { left: r.left, right: r.right, visible: r.width > 0 && r.height > 0 };
      }),
      category: rect('.category-tabs'),
      layout: rect('.layout-switcher'),
      footer: rect('.panel-footer'),
      content: rect('.view-container')
    };
  })()`);
  assert.equal(minimumWindow.width, 880);
  assert.ok(minimumWindow.documentWidth <= minimumWindow.width, `Minimum supported window overflows: ${JSON.stringify(minimumWindow)}`);
  assert.ok(minimumWindow.search.visible && minimumWindow.category.visible && minimumWindow.layout.visible,
    `Minimum window must keep primary search and navigation visible: ${JSON.stringify(minimumWindow)}`);
  assert.ok(minimumWindow.searchActions.every(action => action.visible && action.right <= minimumWindow.width),
    `Minimum window search actions must remain visible: ${JSON.stringify(minimumWindow.searchActions)}`);
  assert.ok(minimumWindow.search.right <= minimumWindow.width && minimumWindow.category.right <= minimumWindow.width && minimumWindow.layout.right <= minimumWindow.width,
    `Minimum window primary controls must stay in bounds: ${JSON.stringify(minimumWindow)}`);
  assert.ok(minimumWindow.content.bottom <= minimumWindow.footer.top + 1,
    `Minimum window footer must not overlap main content: ${JSON.stringify(minimumWindow)}`);
  await send("Page.setDeviceMetricsOverride", { width: 920, height: 760, deviceScaleFactor: 1, mobile: false });

  const categoryReachability = await evaluate(`(() => {
    const tabs = document.querySelector('.category-tabs');
    const last = tabs.querySelector('.cat-btn:last-child');
    tabs.scrollLeft = tabs.scrollWidth;
    last.click();
    return { active: last.classList.contains('active'), id: last.id, scrollLeft: tabs.scrollLeft };
  })()`);
  assert.ok(categoryReachability.active && categoryReachability.scrollLeft > 0,
    `The final category must remain reachable by horizontal scrolling: ${JSON.stringify(categoryReachability)}`);

  const keyboardActivation = await evaluate(`(() => {
    const search = document.getElementById('search-input');
    const chip = document.querySelector('.quick-intent-chips .intent-chip[data-prefix="!"]');
    chip.focus();
    chip.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true }));
    return { active: document.activeElement === search, value: search.value };
  })()`);
  assert.ok(keyboardActivation.active && keyboardActivation.value.startsWith("!"), "Space must activate a focused intent chip and route focus to search.");

  const failedSaveFeedback = await evaluate(`(() => {
    desktopSettingsSavePending = true;
    onDesktopSettingsSaved({ success: false, error: 'headless smoke' });
    return {
      toast: document.getElementById('toast-notify').textContent,
      pending: desktopSettingsSavePending,
      saveDisabled: document.getElementById('save-desktop-preferences-btn').disabled
    };
  })()`);
  assert.ok(failedSaveFeedback.toast.includes("headless smoke") && !failedSaveFeedback.pending && !failedSaveFeedback.saveDisabled,
    `Settings failure must show feedback and release the save control: ${JSON.stringify(failedSaveFeedback)}`);

  const queueFeedback = await evaluate(`(() => {
    const savedBridge = window.chrome.webview;
    const sent = [];
    window.chrome.webview = { postMessage: text => sent.push(JSON.parse(text)) };
    const item = {
      Id: 'queue-fixture', Type: 'file', Content: '很长的文件名'.repeat(40),
      FilePath: 'D:/' + 'long-folder/'.repeat(40) + 'note.md',
      TargetDocTitle: '指定目标文档', CreatedAt: '/Date(1794233935018)/'
    };
    try {
      showOfflineQueueModal();
      const reading = offlineQueuePendingOperation === 'get';
      const savedExecute = executeSelected;
      let modalShortcutExecutions = 0;
      executeSelected = () => { modalShortcutExecutions++; };
      try {
        window.dispatchEvent(new KeyboardEvent('keydown', { key: '1', ctrlKey: true, cancelable: true }));
      } finally { executeSelected = savedExecute; }
      window.onOfflineQueueLoaded({ code: 0, operation: 'get', items: [item] });
      flushOfflineQueue();
      flushOfflineQueue();
      const pending = offlineQueuePendingOperation === 'flush' && document.getElementById('offline-queue-flush-btn').disabled;
      const stillOpen = document.getElementById('offline-queue-modal').classList.contains('open');
      window.onOfflineQueueLoaded({ code: 0, operation: 'flush', flushed: true, items: [item] });
      const partialStatus = document.getElementById('offline-queue-status').textContent;
      const rows = document.getElementById('offline-queue-modal-body');
      const validDate = !rows.textContent.includes('Invalid Date') && formatOfflineQueueDate(item.CreatedAt) !== '时间未知';
      const targetVisible = rows.textContent.includes('指定目标文档');
      const noOverflow = rows.scrollWidth <= rows.clientWidth;
      requestOfflineQueueOperation('clear');
      const beforeAck = document.getElementById('offline-queue-status').textContent;
      window.onOfflineQueueLoaded({ code: 1, operation: 'clear', msg: '模拟磁盘写入失败' });
      const errorVisible = rows.textContent.includes('模拟磁盘写入失败');
      const errorBadge = document.getElementById('telemetry-queue-text').textContent;
      const released = !document.getElementById('offline-queue-clear-btn').disabled;
      requestOfflineQueueOperation('get');
      window.onOfflineQueueLoaded({ code: 0, operation: 'get', items: [] });
      return {
        reading, pending, stillOpen, partialStatus, validDate, targetVisible, noOverflow, modalShortcutExecutions,
        flushRequests: sent.filter(x => x.op === 'flushOfflineQueue').length,
        beforeAck, errorVisible, errorBadge, released,
        emptyText: rows.textContent
      };
    } finally {
      window.chrome.webview = savedBridge;
      closeOfflineQueueModal();
    }
  })()`);
  assert.ok(queueFeedback.reading && queueFeedback.pending && queueFeedback.stillOpen);
  assert.equal(queueFeedback.modalShortcutExecutions, 0, 'Modal keyboard input must not execute commands behind the dialog.');
  assert.equal(queueFeedback.flushRequests, 1, "Queue flush must reject repeated submissions while awaiting a result.");
  assert.ok(queueFeedback.partialStatus.includes('1 条') && !queueFeedback.partialStatus.includes('全部'));
  assert.ok(queueFeedback.validDate && queueFeedback.targetVisible && queueFeedback.noOverflow);
  assert.ok(queueFeedback.beforeAck.includes('正在') && queueFeedback.errorVisible && queueFeedback.released);
  assert.equal(queueFeedback.errorBadge, '队列异常');
  assert.ok(!queueFeedback.emptyText.includes('已成功落盘'), "An empty queue does not prove every item was delivered.");

  await evaluate(`document.getElementById('search-input').focus()`);
  await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
  const tabFocus = await evaluate(`document.activeElement.matches('.search-shortcuts button')`);
  assert.ok(tabFocus, "Tab from search must reach a real control, not be consumed by category cycling.");

  const enterAction = await evaluate(`(() => {
    const savedExecute = executeSelected;
    let unexpected = 0;
    executeSelected = () => { unexpected++; };
    try {
      const chip = document.querySelector('.intent-chip[data-prefix="d:"]');
      chip.focus();
      chip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
      return { unexpected, query: document.getElementById('search-input').value };
    } finally { executeSelected = savedExecute; }
  })()`);
  assert.equal(enterAction.unexpected, 0, "Activating a scope chip must not also execute the underlying selected result.");
  assert.ok(enterAction.query.startsWith('d:'));

  console.log(JSON.stringify({ desktop, compact, minimumWindow, categoryReachability, dynamicChipSemantics, keyboardActivation, failedSaveFeedback, queueFeedback, tabFocus, enterAction }, null, 2));
} finally {
  for (const request of pending.values()) {
    clearTimeout(request.timer);
    request.reject(new Error('Headless test connection closed.'));
  }
  pending.clear();
  if (socket && socket.readyState === WebSocket.OPEN) {
    try { await send('Browser.close'); } catch {}
  }
  if (socket && socket.readyState === WebSocket.OPEN) socket.close();
  if (browser.exitCode === null && browser.signalCode === null) {
    await Promise.race([new Promise(resolve => browser.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 2000))]);
  }
  if (browser.exitCode === null && browser.signalCode === null) browser.kill();
  for (let attempt = 0; attempt < 8; attempt++) {
    try { fs.rmSync(profileDir, { recursive: true, force: true }); break; }
    catch { await new Promise(resolve => setTimeout(resolve, 250)); }
  }
}
