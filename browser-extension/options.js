const $ = (id) => document.getElementById(id);

chrome.storage.local.get(["token", "heading", "port"]).then((cfg) => {
  $("token").value = cfg.token || "";
  $("heading").value = cfg.heading || "";
  $("port").value = cfg.port || 43989;
});

$("save").addEventListener("click", async () => {
  await chrome.storage.local.set({
    token: $("token").value.trim(),
    heading: $("heading").value.trim() || "🌐 网页剪藏",
    port: parseInt($("port").value, 10) || 43989
  });
  $("status").textContent = "✓ 已保存";
  setTimeout(() => { $("status").textContent = ""; }, 2000);
});

$("test").addEventListener("click", async () => {
  const token = $("token").value.trim();
  const port = parseInt($("port").value, 10) || 43989;
  $("status").textContent = "正在探测…";
  try {
    const res = await fetch(`http://127.0.0.1:${port}/status`, { headers: { "X-Token": token } });
    const j = await res.json();
    $("status").textContent = j.code === 0
      ? `✓ 外骨骼在线 v${j.version} · 思源${j.siyuan_connected ? "已连接" : "未连接"}`
      : "✗ 鉴权失败：Token 不正确";
  } catch (e) {
    $("status").textContent = "✗ 无法连接——请确认思源外骨骼桌面端正在运行";
  }
});
