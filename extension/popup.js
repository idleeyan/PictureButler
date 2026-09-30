// PictureButler 拾取器 - 弹窗逻辑
const API = "http://127.0.0.1:8189";
let allItems = [];

const $ = (id) => document.getElementById(id);

async function load() {
  try {
    const r = await fetch(API + "/api/starred", { signal: AbortSignal.timeout(2500) });
    if (!r.ok) throw new Error("status " + r.status);
    allItems = await r.json();
    setStatus(true);
    render();
  } catch {
    allItems = [];
    setStatus(false);
    $("list").innerHTML =
      '<div class="empty">无法连接 PictureButler 程序<br>请先启动桌面程序，再点「刷新」</div>';
  }
}

function setStatus(on) {
  const s = $("status");
  if (on) {
    s.textContent = "已连接";
    s.className = "status on";
  } else {
    s.textContent = "未连接";
    s.className = "status off";
  }
}

function render() {
  const q = $("search").value.trim().toLowerCase();
  const items = q
    ? allItems.filter(
        (i) =>
          i.title.toLowerCase().includes(q) ||
          i.content.toLowerCase().includes(q)
      )
    : allItems;

  const list = $("list");
  if (items.length === 0) {
    list.innerHTML =
      '<div class="empty">' +
      (allItems.length === 0
        ? "暂无星标提示词<br>在程序里给常用提示词点 ★ 即可出现在这里"
        : "没有匹配的提示词") +
      "</div>";
    return;
  }

  list.innerHTML = "";
  for (const item of items) {
    const div = document.createElement("div");
    div.className = "item";

    // 左侧缩略图（有图则加载 /api/image）
    const thumb = document.createElement("div");
    thumb.className = "thumb";
    if (item.hasImage) {
      const img = document.createElement("img");
      img.alt = "";
      img.loading = "lazy";
      thumb.appendChild(img);
      // fetch + blob 加载（跨源更可靠），失败显示占位
      fetch(API + "/api/image?id=" + encodeURIComponent(item.id) + "&kind=preview", { signal: AbortSignal.timeout(6000) })
        .then((r) => { if (!r.ok) throw new Error("status " + r.status); return r.blob(); })
        .then((b) => {
          if (!thumb.isConnected) return;
          img.src = URL.createObjectURL(b);
        })
        .catch(() => {
          const noimg = document.createElement("div");
          noimg.className = "noimg";
          noimg.textContent = "◇";
          if (img.isConnected) img.replaceWith(noimg); else if (thumb.isConnected) thumb.appendChild(noimg);
        });
    } else {
      const noimg = document.createElement("div");
      noimg.className = "noimg";
      noimg.textContent = "◇";
      thumb.appendChild(noimg);
    }

    const body = document.createElement("div");
    body.className = "body";
    const t = document.createElement("div");
    t.className = "t";
    t.innerHTML = '<svg class="star" width="11" height="11" viewBox="0 0 24 24" aria-hidden="true">'
    + '<path d="M12 2 L15.1 8.3 L22 9.3 L17 14.1 L18.2 21 L12 17.8 L5.8 21 L7 14.1 L2 9.3 L8.9 8.3 Z" fill="#F5B84B"/></svg>'
    + escapeHtml(item.title);
    const c = document.createElement("div");
    c.className = "c";
    c.textContent = item.content;
    body.appendChild(t);
    body.appendChild(c);

    div.appendChild(thumb);
    div.appendChild(body);
    div.addEventListener("click", () => use(item));
    list.appendChild(div);
  }
}

function use(item) {
  chrome.tabs.query({ active: true, currentWindow: true }, async (tabs) => {
    const tab = tabs && tabs[0];
    if (!tab || !tab.id) {
      toast("没有找到当前网页", true);
      return;
    }
    try {
      const resp = await chrome.tabs.sendMessage(tab.id, {
        type: "PB_INSERT",
        text: item.content,
        title: item.title,
      });
      if (resp && resp.ok) {
        toast("已写入「" + item.title + "」");
        window.close();
      } else {
        toast("当前页面没有可写入的输入框，请先点击目标输入框再试", true);
      }
    } catch {
      toast("无法写入该网页（请刷新页面后重试）", true);
    }
  });
}

function toast(msg, isErr) {
  const t = $("toast");
  t.textContent = msg;
  t.className = isErr ? "err" : "";
  t.style.display = "block";
  setTimeout(() => (t.style.display = "none"), 1800);
}

function escapeHtml(s) {
  const d = document.createElement("div");
  d.textContent = s;
  return d.innerHTML;
}

$("search").addEventListener("input", render);
$("refresh").addEventListener("click", load);
$("openApp").addEventListener("click", () => {
  toast("请先启动桌面程序 PictureButler，再点「刷新」");
});

load();
