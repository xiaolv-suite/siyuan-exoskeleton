// 思源外骨骼剪藏伴侣 · MV3 Service Worker
// 剪藏通道：浏览器 → 本地思源外骨骼 webhook (/api/write, X-Token 鉴权) → 思源今日日记
// 数据全程只经过本机 127.0.0.1，无任何云端中转。

const DEFAULTS = { token: "", heading: "🌐 网页剪藏", port: 43989 };

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: "xlv-clip-selection", title: "📑 存入选中文字到思源", contexts: ["selection"] });
  chrome.contextMenus.create({ id: "xlv-clip-page", title: "🌐 收藏此网页到思源", contexts: ["page"] });
  chrome.contextMenus.create({ id: "xlv-collect-highlight", title: "🖍️ 摘录加入高亮收集栈", contexts: ["selection"] });
  chrome.contextMenus.create({ id: "xlv-flush-highlights", title: "📥 汇编当前页面全部高亮入日记", contexts: ["page", "selection"] });
});

async function flushHighlightsToDaily(highlights, title, url, cfg) {
  if (!highlights || highlights.length === 0) {
    notify("思源外骨骼剪藏伴侣", "当前页面尚未收集任何高亮片段");
    return;
  }
  let md = `- 📖 [${title || "无题网页"}](${url || ""})`;
  highlights.forEach((h, i) => {
    md += `\n  * 💡 摘录 ${i + 1}：${h.replace(/\s+/g, " ").substring(0, 1500)}`;
  });

  try {
    const j = await sendToExoskeleton({ target: "daily", heading: cfg.heading, data: md }, cfg);
    if (j && j.code === 0) {
      notify("✓ 汇编入库成功", `《${title}》共 ${highlights.length} 段精选高亮已结构化存入今日日记！`);
      chrome.action.setBadgeText({ text: "" });
      return true;
    } else {
      notify("汇编入库失败", (j && j.msg) || "外骨骼返回了错误");
    }
  } catch (e) {
    notify("剪藏失败", "无法连接本地思源外骨骼（端口 " + cfg.port + "）");
  }
  return false;
}

chrome.runtime.onMessage.addListener(async (req, sender) => {
  if (req.action === "SYNC_HIGHLIGHTS") {
    const text = req.count > 0 ? String(req.count) : "";
    chrome.action.setBadgeText({ text: text, tabId: sender.tab?.id });
    chrome.action.setBadgeBackgroundColor({ color: "#10b981", tabId: sender.tab?.id });
  } else if (req.action === "FLUSH_HIGHLIGHTS" && sender.tab) {
    const cfg = await getConfig();
    if (!cfg.token) {
      notify("思源外骨骼剪藏伴侣", "请先在扩展选项里填入 Webhook Token");
      return;
    }
    const ok = await flushHighlightsToDaily(req.highlights, sender.tab.title, sender.tab.url, cfg);
    if (ok) {
      try {
        chrome.tabs.sendMessage(sender.tab.id, { action: "CLEAR_PAGE_HIGHLIGHTS" });
      } catch (e) {}
    }
  }
});

async function getConfig() {
  const stored = await chrome.storage.local.get(["token", "heading", "port"]);
  return { ...DEFAULTS, ...stored };
}

async function sendToExoskeleton(payload, cfg) {
  const res = await fetch(`http://127.0.0.1:${cfg.port}/api/write`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Token": cfg.token },
    body: JSON.stringify(payload)
  });
  return res.json();
}

function notify(title, message) {
  try {
    chrome.notifications.create({ type: "basic", iconUrl: "icon128.png", title: title, message: message });
  } catch (e) {}
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const cfg = await getConfig();
  if (!cfg.token) {
    notify("思源外骨骼剪藏伴侣", "请先在扩展选项里填入 Webhook Token（外骨骼偏好设置可查看）");
    return;
  }
  const title = (tab && tab.title) || "无题网页";
  const url = info.pageUrl || (tab && tab.url) || "";

  // 1. 多段高亮摘录收集
  if (info.menuItemId === "xlv-collect-highlight" && info.selectionText) {
    const sel = info.selectionText.replace(/\s+/g, " ").trim().substring(0, 2000);
    try {
      chrome.tabs.sendMessage(tab.id, { action: "ADD_PAGE_HIGHLIGHT", text: sel }, (resp) => {
        const count = (resp && resp.count) || 1;
        chrome.action.setBadgeText({ text: String(count), tabId: tab.id });
        chrome.action.setBadgeBackgroundColor({ color: "#10b981", tabId: tab.id });
        notify("🖍️ 已加入高亮收集栈", `当前网页已累积 ${count} 段高亮摘录，读完可一次性汇编入库！`);
      });
    } catch (e) {
      notify("摘录失败", "无法向当前页面注入高亮标记");
    }
    return;
  }

  // 2. 汇编当前页面全部高亮入日记
  if (info.menuItemId === "xlv-flush-highlights") {
    try {
      chrome.tabs.sendMessage(tab.id, { action: "GET_PAGE_HIGHLIGHTS" }, async (resp) => {
        if (!resp || !resp.highlights || resp.highlights.length === 0) {
          notify("思源外骨骼剪藏伴侣", "当前网页尚未摘录任何高亮内容");
          return;
        }
        const ok = await flushHighlightsToDaily(resp.highlights, resp.title || title, resp.url || url, cfg);
        if (ok) {
          try { chrome.tabs.sendMessage(tab.id, { action: "CLEAR_PAGE_HIGHLIGHTS" }); } catch (e) {}
        }
      });
    } catch (e) {
      notify("汇编失败", "请刷新当前网页后重试");
    }
    return;
  }

  // 3. 经典单次划词与单次整页收藏
  let data;
  if (info.menuItemId === "xlv-clip-selection" && info.selectionText) {
    const sel = info.selectionText.replace(/\s+/g, " ").trim().substring(0, 2000);
    data = `- 📑 [${title}](${url})\n  * ${sel}`;
  } else {
    data = `- 🌐 [${title}](${url})`;
  }
  try {
    const j = await sendToExoskeleton({ target: "daily", heading: cfg.heading, data: data }, cfg);
    if (j && j.code === 0) {
      notify("✓ 已存入思源", `《${title}》已进入今日日记「${cfg.heading}」小节`);
    } else {
      notify("剪藏失败", (j && j.msg) || "外骨骼返回了未知错误");
    }
  } catch (e) {
    notify("剪藏失败", "无法连接本地思源外骨骼——请确认桌面端正在运行 (端口 " + cfg.port + ")");
  }
});
