const $ = (id) => document.getElementById(id);
let currentTab = null;
let currentHighlights = [];

chrome.tabs.query({ active: true, currentWindow: true }).then((tabs) => {
  currentTab = tabs[0];
  $("title").textContent = currentTab ? (currentTab.title || currentTab.url || "无题") : "未获取到网页";

  // 探测当前页面是否有高亮收集
  if (currentTab && currentTab.id) {
    try {
      chrome.tabs.sendMessage(currentTab.id, { action: "GET_PAGE_HIGHLIGHTS" }, (resp) => {
        if (resp && Array.isArray(resp.highlights) && resp.highlights.length > 0) {
          currentHighlights = resp.highlights;
          $("highlight-section").style.display = "block";
          $("hl-count").textContent = `🖍️ 当前页已收集 ${currentHighlights.length} 段高亮`;
          $("hl-list").innerHTML = currentHighlights.map((h, i) => `<div>${i + 1}. ${escapeHtml(h)}</div>`).join("");
        }
      });
    } catch (e) {}
  }
});

function escapeHtml(str) {
  return (str || "").replace(/[&<>"']/g, (m) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[m]));
}

$("flush-hl")?.addEventListener("click", async () => {
  if (!currentTab || currentHighlights.length === 0) return;
  const cfg = { ...(await chrome.storage.local.get(["token", "heading", "port"])) };
  if (!cfg.token) { $("status").textContent = "✗ 请先在扩展选项里填 Token"; return; }

  let md = `- 📖 [${currentTab.title || "无题"}](${currentTab.url || ""})`;
  currentHighlights.forEach((h, i) => {
    md += `\n  * 💡 摘录 ${i + 1}：${h.replace(/\s+/g, " ").substring(0, 1500)}`;
  });

  try {
    const res = await fetch(`http://127.0.0.1:${cfg.port || 43989}/api/write`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Token": cfg.token },
      body: JSON.stringify({ target: "daily", heading: cfg.heading || "🌐 网页剪藏", data: md })
    });
    const j = await res.json();
    if (j.code === 0) {
      $("status").textContent = `✓ 已汇编 ${currentHighlights.length} 条高亮入日记！`;
      $("highlight-section").style.display = "none";
      try { chrome.tabs.sendMessage(currentTab.id, { action: "CLEAR_PAGE_HIGHLIGHTS" }); } catch (e) {}
    } else {
      $("status").textContent = "✗ " + (j.msg || "汇编失败");
    }
  } catch (e) {
    $("status").textContent = "✗ 无法连接外骨骼服务";
  }
});

$("clear-hl")?.addEventListener("click", () => {
  if (!currentTab) return;
  currentHighlights = [];
  $("highlight-section").style.display = "none";
  try { chrome.tabs.sendMessage(currentTab.id, { action: "CLEAR_PAGE_HIGHLIGHTS" }); } catch (e) {}
});

$("save").addEventListener("click", async () => {
  const cfg = { ...(await chrome.storage.local.get(["token", "heading", "port"])) };
  if (!cfg.token) { $("status").textContent = "✗ 请先在扩展选项里填 Token"; return; }
  if (!currentTab) { $("status").textContent = "✗ 未获取到当前网页"; return; }
  const note = $("note").value.trim();
  let data = `- 🌐 [${currentTab.title || "无题"}](${currentTab.url || ""})`;
  if (note) data += `\n  * 💡 ${note.replace(/\s+/g, " ").substring(0, 1000)}`;
  try {
    const res = await fetch(`http://127.0.0.1:${cfg.port || 43989}/api/write`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Token": cfg.token },
      body: JSON.stringify({ target: "daily", heading: cfg.heading || "🌐 网页剪藏", data: data })
    });
    const j = await res.json();
    $("status").textContent = j.code === 0 ? "✓ 已存入今日日记" : "✗ " + (j.msg || "失败");
  } catch (e) {
    $("status").textContent = "✗ 无法连接外骨骼（桌面端未运行？）";
  }
});

$("copy").addEventListener("click", async () => {
  if (!currentTab) return;
  const md = `[${currentTab.title || "无题"}](${currentTab.url || ""})`;
  try { await navigator.clipboard.writeText(md); $("status").textContent = "✓ 已复制 Markdown 链接"; } catch (e) { $("status").textContent = "✗ 复制失败"; }
});
