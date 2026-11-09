const $ = (id) => document.getElementById(id);
let currentTab = null;

chrome.tabs.query({ active: true, currentWindow: true }).then((tabs) => {
  currentTab = tabs[0];
  $("title").textContent = currentTab ? (currentTab.title || currentTab.url || "无题") : "未获取到网页";
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
