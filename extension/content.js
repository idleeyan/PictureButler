// PictureButler 拾取器 - 内容脚本：把提示词写入当前网页输入框
(function () {
  // 注入标记（供验证/调试：页面可检测到此脚本已注入）
  try { document.documentElement.setAttribute("data-pb-picker", "v1"); } catch (e) {}
  function isEditable(el) {
    if (!el) return false;
    if (el.tagName === "TEXTAREA" || el.tagName === "INPUT" || el.isContentEditable) return true;
    if (el.getAttribute && el.getAttribute("contenteditable") === "true") return true;
    if (el.tagName === "SELECT") return true;
    if (el.closest && el.closest(".CodeMirror, .ql-editor, [contenteditable=true]")) return true;
    return false;
  }

  function findEditable() {
    // 优先焦点元素
    const focused = document.activeElement;
    if (isEditable(focused)) return focused;

    // 常见富文本编辑器容器
    const rich = document.querySelector(
      ".CodeMirror, .ql-editor, [contenteditable=true][data-placeholder], .ProseMirror, [role=textbox]"
    );
    if (rich) return rich;

    // 通用：第一个可见可编辑元素
    const all = document.querySelectorAll(
      "textarea, input:not([type=hidden]):not([type=submit]):not([type=button]):not([type=checkbox]):not([type=radio]), [contenteditable=true]"
    );
    for (const el of all) {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0 && !el.disabled && !el.readOnly) return el;
    }
    return null;
  }

  function insert(el, text) {
    el.focus();
    if (el.isContentEditable || (el.getAttribute && el.getAttribute("contenteditable") === "true")) {
      // 富文本：清空后插入
      if (el.tagName !== "TEXTAREA" && el.tagName !== "INPUT") {
        el.innerHTML = "";
      }
      document.execCommand("insertText", false, text);
      return true;
    }
    el.value = text;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    return true;
  }

  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg && msg.type === "PB_INSERT") {
      try {
        const el = findEditable();
        if (!el) {
          sendResponse({ ok: false, reason: "no-editable" });
          return;
        }
        insert(el, msg.text);
        sendResponse({ ok: true, target: el.tagName });
      } catch (e) {
        sendResponse({ ok: false, reason: String(e) });
      }
      return true;
    }
  });
})();
