// 思源外骨骼剪藏伴侣 · MV3 Service Worker
// 剪藏通道：浏览器 → 本地思源外骨骼 webhook (/api/write, X-Token 鉴权) → 思源今日日记
// 数据全程只经过本机 127.0.0.1，无任何云端中转。

const DEFAULTS = { token: "", heading: "🌐 网页剪藏", port: 43989 };

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: "xlv-clip-selection", title: "📑 存入选中文字到思源", contexts: ["selection"] });
  chrome.contextMenus.create({ id: "xlv-clip-page", title: "🌐 收藏此网页到思源", contexts: ["page"] });
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
  const url = info.pageUrl || "";
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
