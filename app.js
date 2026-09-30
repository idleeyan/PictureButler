/* 证件照 / 文档 标签展示 — 交互 */

const state = {
  cat: "all",
  q: "",
  items: [],
  view: [],
  lbIndex: -1,
};

const $ = (sel) => document.querySelector(sel);

function normalize(item, kind) {
  return {
    ...item,
    kind, // id | doc
    kindLabel: kind === "id" ? "证件照" : "文档",
  };
}

function matchQuery(item, q) {
  if (!q) return true;
  const s = [item.title, item.desc, item.source, ...(item.tags || [])]
    .join(" ")
    .toLowerCase();
  return s.includes(q);
}

function applyFilter() {
  const q = state.q.trim().toLowerCase();
  state.view = state.items.filter(
    (it) =>
      (state.cat === "all" || it.kind === state.cat) &&
      matchQuery(it, q)
  );
  render();
}

function render() {
  const gallery = $("#gallery");
  const empty = $("#empty");
  gallery.innerHTML = "";

  $("#count-all").textContent = String(state.items.length);
  $("#count-id").textContent = String(state.items.filter((i) => i.kind === "id").length);
  $("#count-doc").textContent = String(state.items.filter((i) => i.kind === "doc").length);
  $("#hero-id").textContent = $("#count-id").textContent;
  $("#hero-doc").textContent = $("#count-doc").textContent;

  const titles = {
    all: ["全部标签", "证件照与文档的专门展示视图"],
    id: ["证件照", "正式证件与身份类影像"],
    doc: ["文档", "文件、通知书与纸质文献"],
  };
  const [t, s] = titles[state.cat] || titles.all;
  $("#page-title").textContent = t;
  $("#page-sub").textContent = s;

  if (state.view.length === 0) {
    empty.hidden = false;
    return;
  }
  empty.hidden = true;

  const frag = document.createDocumentFragment();
  state.view.forEach((item, idx) => {
    const card = document.createElement("article");
    card.className = `card is-${item.kind}`;
    card.tabIndex = 0;
    card.innerHTML = `
      <div class="badge ${item.kind}">${item.kindLabel}</div>
      <img class="card-img" src="${item.file}" alt="${escapeHtml(item.title)}" loading="lazy" />
      <div class="card-body">
        <h3 class="card-title" title="${escapeHtml(item.title)}">${escapeHtml(item.title)}</h3>
        <p class="card-desc">${escapeHtml(item.desc || "（无描述）")}</p>
        <div class="chips">
          ${(item.tags || [])
            .slice(0, 4)
            .map((tag) => `<span class="chip ${item.kind}">${escapeHtml(tag)}</span>`)
            .join("")}
        </div>
      </div>
    `;
    card.addEventListener("click", () => openLightbox(idx));
    card.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        openLightbox(idx);
      }
    });
    frag.appendChild(card);
  });
  gallery.appendChild(frag);
}

function escapeHtml(str) {
  return String(str ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function openLightbox(idx) {
  state.lbIndex = idx;
  const item = state.view[idx];
  if (!item) return;
  $("#lb-img").src = item.file;
  $("#lb-img").alt = item.title;
  $("#lb-title").textContent = item.title;
  $("#lb-desc").textContent = item.desc || "（无描述）";
  $("#lb-src").textContent = item.source || "";
  $("#lb-tags").innerHTML = (item.tags || [])
    .map((tag) => `<span class="chip ${item.kind}">${escapeHtml(tag)}</span>`)
    .join("");
  $("#lightbox").hidden = false;
  document.body.style.overflow = "hidden";
}

function closeLightbox() {
  $("#lightbox").hidden = true;
  document.body.style.overflow = "";
  state.lbIndex = -1;
}

function moveLightbox(delta) {
  if (state.view.length === 0) return;
  const next = (state.lbIndex + delta + state.view.length) % state.view.length;
  openLightbox(next);
}

function bindUi() {
  $("#cats").addEventListener("click", (e) => {
    const btn = e.target.closest(".cat");
    if (!btn) return;
    document.querySelectorAll(".cat").forEach((el) => el.classList.remove("is-active"));
    btn.classList.add("is-active");
    state.cat = btn.dataset.cat;
    applyFilter();
  });

  $("#search").addEventListener("input", (e) => {
    state.q = e.target.value;
    applyFilter();
  });

  $("#lb-close").addEventListener("click", closeLightbox);
  $("#lb-prev").addEventListener("click", () => moveLightbox(-1));
  $("#lb-next").addEventListener("click", () => moveLightbox(1));
  $("#lightbox").addEventListener("click", (e) => {
    if (e.target.id === "lightbox") closeLightbox();
  });
  document.addEventListener("keydown", (e) => {
    if ($("#lightbox").hidden) return;
    if (e.key === "Escape") closeLightbox();
    if (e.key === "ArrowLeft") moveLightbox(-1);
    if (e.key === "ArrowRight") moveLightbox(1);
  });
}

async function load() {
  let data = null;
  try {
    const res = await fetch("./tag-data.json", { cache: "no-store" });
    if (res.ok) data = await res.json();
  } catch {
    /* file:// 或离线时走内联兜底 */
  }
  if (!data) {
    data = window.__TAG_DATA_FALLBACK__ || { idPhotos: [], documents: [] };
  }
  state.items = [
    ...(data.idPhotos || []).map((x) => normalize(x, "id")),
    ...(data.documents || []).map((x) => normalize(x, "doc")),
  ];
  applyFilter();
}

bindUi();
load();
