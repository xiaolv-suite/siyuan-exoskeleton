// 思源外骨骼剪藏伴侣 · 网页多段高亮收集器 (Highlighter Collector)
// 对标 Obsidian Web Clipper 2024 / Readwise Reader 核心杀手锏：边读边画重点，一次性整合成结构化大纲入库

(function() {
  let highlights = [];
  let floatBadge = null;

  function ensureFloatBadge() {
    if (floatBadge && document.body.contains(floatBadge)) return floatBadge;
    floatBadge = document.createElement("div");
    floatBadge.id = "xlv-highlighter-float-dock";
    floatBadge.style.cssText = [
      "position: fixed",
      "bottom: 24px",
      "right: 24px",
      "z-index: 2147483647",
      "display: none",
      "align-items: center",
      "gap: 8px",
      "padding: 8px 14px",
      "border-radius: 20px",
      "background: rgba(15, 23, 42, 0.92)",
      "border: 1px solid #38bdf8",
      "box-shadow: 0 4px 16px rgba(0,0,0,0.35)",
      "color: #f8fafc",
      "font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif",
      "font-size: 12px",
      "user-select: none",
      "backdrop-filter: blur(8px)"
    ].join(";");

    const textSpan = document.createElement("span");
    textSpan.id = "xlv-highlighter-count";
    textSpan.textContent = "🖍️ 已摘录 0 段";

    const submitBtn = document.createElement("button");
    submitBtn.textContent = "📥 汇编入思源";
    submitBtn.style.cssText = "padding: 3px 10px; border-radius: 12px; border: 1px solid #10b981; background: rgba(16,185,129,0.25); color: #10b981; font-weight: 700; cursor: pointer; font-size: 11px;";
    submitBtn.onclick = () => {
      chrome.runtime.sendMessage({ action: "FLUSH_HIGHLIGHTS", highlights: highlights });
    };

    const clearBtn = document.createElement("button");
    clearBtn.textContent = "✕ 清空";
    clearBtn.style.cssText = "padding: 3px 8px; border-radius: 12px; border: 1px solid #475569; background: transparent; color: #94a3b8; cursor: pointer; font-size: 11px;";
    clearBtn.onclick = () => {
      highlights = [];
      updateBadgeUI();
      chrome.runtime.sendMessage({ action: "SYNC_HIGHLIGHTS", count: 0 });
    };

    floatBadge.appendChild(textSpan);
    floatBadge.appendChild(submitBtn);
    floatBadge.appendChild(clearBtn);
    document.body.appendChild(floatBadge);
    return floatBadge;
  }

  function updateBadgeUI() {
    ensureFloatBadge();
    const countEl = document.getElementById("xlv-highlighter-count");
    if (highlights.length > 0) {
      if (countEl) countEl.textContent = `🖍️ 已摘录 ${highlights.length} 段高亮`;
      floatBadge.style.display = "flex";
    } else {
      floatBadge.style.display = "none";
    }
  }

  // 接收来自 background 的摘录指令
  chrome.runtime.onMessage.addListener((req, sender, sendResponse) => {
    if (req.action === "ADD_PAGE_HIGHLIGHT" && req.text) {
      highlights.push(req.text.trim());
      updateBadgeUI();
      sendResponse({ success: true, count: highlights.length });
    } else if (req.action === "GET_PAGE_HIGHLIGHTS") {
      sendResponse({ highlights: highlights, title: document.title, url: window.location.href });
    } else if (req.action === "CLEAR_PAGE_HIGHLIGHTS") {
      highlights = [];
      updateBadgeUI();
      sendResponse({ success: true });
    }
    return true;
  });
})();
