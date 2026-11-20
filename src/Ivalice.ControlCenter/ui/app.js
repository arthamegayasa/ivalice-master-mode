"use strict";

// ---- Bridge to the desktop host -------------------------------------------------
const pending = new Map();
let nextId = 1;

function call(method, args = {}) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, method, args });
  });
}

window.chrome.webview.addEventListener("message", (event) => {
  const reply = event.data;
  const waiter = pending.get(reply.id);
  if (!waiter) return;
  pending.delete(reply.id);
  if (reply.ok) waiter.resolve(reply.result);
  else waiter.reject(new Error(reply.error));
});

// ---- Small DOM helpers ----------------------------------------------------------
const $ = (id) => document.getElementById(id);

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") node.className = value;
    else if (key === "text") node.textContent = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (key in node && typeof value !== "string") node[key] = value;
    else node.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(child));
  }
  return node;
}

let toastTimer = 0;
function toast(message, good = false) {
  const box = $("toast");
  box.textContent = message;
  box.classList.toggle("good", good);
  box.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.hidden = true; }, good ? 2600 : 6500);
}

// ---- State ----------------------------------------------------------------------
let snapshot = null;
const openDetails = new Set();

const GROUPS = [
  { name: "Kenyamanan", blurb: "Mempercepat dan memperhalus cara bermain." },
  { name: "Progres", blurb: "Mengatur laju XP, JP, dan level." },
  { name: "Pertarungan", blurb: "Mengubah jalannya pertarungan." },
  { name: "Lainnya", blurb: "Mod terpasang yang belum ada di katalog." },
  { name: "Fondasi", blurb: "Dibutuhkan mod lain. Tidak mengubah permainan dengan sendirinya." },
];

function formatValue(setting, value) {
  if (setting.type === "bool") return value ? "Nyala" : "Mati";
  const decimals = setting.step && setting.step < 1 ? 1 : 0;
  return Number(value).toFixed(decimals) + (setting.unit || "");
}

async function act(promise, successMessage) {
  try {
    snapshot = await promise;
    render();
    if (successMessage) toast(successMessage, true);
  } catch (error) {
    toast(error.message);
    render();
  }
}

// ---- Rendering ------------------------------------------------------------------
function render() {
  renderHeader();
  renderNotices();
  renderMods();
}

function renderHeader() {
  const chips = $("chips");
  const play = $("play");
  chips.replaceChildren();
  $("version").textContent = snapshot ? "v" + snapshot.version : "";

  if (!snapshot || !snapshot.reloadedFound || !snapshot.state) {
    play.disabled = true;
    $("play-sub").textContent = "Reloaded-II belum terpasang";
    return;
  }

  const mods = snapshot.state.mods;
  const features = mods.filter((m) => m.role !== "foundation");
  const active = features.filter((m) => m.enabled).length;
  const errors = snapshot.state.notices.filter((n) => n.level === "error").length;

  chips.append(
    el("li", { class: "chip" }, el("b", { text: `${active}/${features.length}` }), " mod aktif"),
    el("li", { class: "chip " + (snapshot.gameRunning ? "ok" : "") },
      "Game: ", el("b", { text: snapshot.gameRunning ? "berjalan" : "tidak berjalan" })),
    errors > 0
      ? el("li", { class: "chip bad" }, el("b", { text: String(errors) }), " masalah")
      : el("li", { class: "chip ok" }, el("b", { text: "Siap" })),
  );

  play.disabled = snapshot.gameRunning || errors > 0;
  $("play-sub").textContent = snapshot.gameRunning
    ? "Game sedang berjalan"
    : errors > 0 ? "Perbaiki masalah di bawah dulu" : "Jalankan game dengan mod aktif";

  const footer = document.querySelector("footer");
  footer.classList.toggle("live", snapshot.gameRunning);
  $("footer-note").textContent = snapshot.gameRunning
    ? "Game sedang berjalan. Perubahan berlaku setelah game ditutup dan dijalankan lagi."
    : "Perubahan berlaku saat game dijalankan berikutnya.";
}

function renderNotices() {
  const box = $("notices");
  box.replaceChildren();
  if (!snapshot) return;
  if (!snapshot.reloadedFound) {
    box.append(el("div", { class: "notice error" }, el("strong", { text: "Masalah" }),
      el("span", { text: "Reloaded-II belum terpasang di komputer ini. Jalankan scripts/install.ps1 dari repositori lebih dulu." })));
    return;
  }
  const labels = { error: "Masalah", warning: "Perhatian", info: "Info" };
  for (const notice of snapshot.state.notices) {
    box.append(el("div", { class: "notice " + notice.level },
      el("strong", { text: labels[notice.level] }), el("span", { text: notice.message })));
  }
}

function renderMods() {
  const view = $("view-mods");
  const scroll = document.querySelector("main").scrollTop;
  view.replaceChildren();

  if (!snapshot || !snapshot.state) return;
  const mods = snapshot.state.mods;
  if (mods.length === 0) {
    view.append(el("div", { class: "empty" }, el("h2", { text: "Belum ada mod" }),
      el("p", { text: "Jalankan scripts/install.ps1 untuk memasang mod." })));
    return;
  }

  const names = [...GROUPS.map((g) => g.name)];
  for (const mod of mods) if (!names.includes(mod.category)) names.splice(names.length - 1, 0, mod.category);

  for (const name of names) {
    const members = mods.filter((m) => m.category === name);
    if (members.length === 0) continue;
    const group = GROUPS.find((g) => g.name === name);
    view.append(el("section", { class: "group" },
      el("div", { class: "group-head" }, el("h2", { text: name }), group && el("p", { text: group.blurb })),
      el("div", { class: "cards" }, members.map(renderCard))));
  }
  document.querySelector("main").scrollTop = scroll;
}

function renderCard(mod) {
  const byId = (id) => snapshot.state.mods.find((m) => m.id.toLowerCase() === id.toLowerCase());
  const nameOf = (id) => (byId(id) || { name: id }).name;
  const locked = mod.enabled && mod.requiredBy.length > 0;
  const toggleId = "toggle-" + mod.id;

  const toggle = el("label", {
    class: "switch",
    title: !mod.installed ? "Belum terpasang"
      : locked ? "Dibutuhkan oleh " + mod.requiredBy.map(nameOf).join(", ") : "",
  },
    el("input", {
      type: "checkbox", id: toggleId, role: "switch", checked: mod.enabled,
      disabled: !mod.installed || locked,
      "aria-label": (mod.enabled ? "Nonaktifkan " : "Aktifkan ") + mod.name,
      onchange: (e) => act(call("setEnabled", { modId: mod.id, enabled: e.target.checked }),
        `${mod.name} ${e.target.checked ? "diaktifkan" : "dinonaktifkan"}.`),
    }),
    el("span"));

  const basic = mod.settings.filter((s) => !s.advanced);
  const advanced = mod.settings.filter((s) => s.advanced);

  const detailKey = mod.id;
  const details = el("details", {
    open: openDetails.has(detailKey),
    ontoggle: (e) => { e.target.open ? openDetails.add(detailKey) : openDetails.delete(detailKey); },
  },
    el("summary", { text: advanced.length ? "Detail dan pengaturan lanjutan" : "Detail" }),
    el("div", { class: "detail-body" },
      el("p", { text: mod.details }),
      el("dl", {},
        el("dt", { text: "Pembuat" }), el("dd", { text: mod.author || "Tidak diketahui" }),
        el("dt", { text: "Lisensi" }), el("dd", { text: mod.license }),
        mod.requires.length > 0 && [el("dt", { text: "Membutuhkan" }), el("dd", { text: mod.requires.map(nameOf).join(", ") })],
        mod.requiredBy.length > 0 && [el("dt", { text: "Dipakai oleh" }), el("dd", { text: mod.requiredBy.map(nameOf).join(", ") })],
        mod.sourceUrl && [el("dt", { text: "Sumber" }), el("dd", {}, mod.inCatalog
          ? el("button", { class: "url", type: "button", text: mod.sourceUrl, onclick: () => call("open", { target: "url", value: mod.sourceUrl }).catch((e) => toast(e.message)) })
          : mod.sourceUrl)],
        el("dt", { text: "ID" }), el("dd", { text: mod.id }))),
    advanced.length > 0 && el("div", { class: "settings" }, advanced.map((s) => renderSetting(mod, s))));

  return el("article", { class: "card" + (mod.enabled ? "" : " off") },
    el("div", { class: "card-top" },
      el("div", { class: "card-main" },
        el("div", { class: "card-title" },
          el("h3", { text: mod.name }),
          mod.version && el("span", { class: "meta", text: "v" + mod.version }),
          !mod.installed && el("span", { class: "badge missing", text: "Belum terpasang" }),
          locked && el("span", { class: "badge lock", text: "Dibutuhkan" }),
          !mod.inCatalog && el("span", { class: "badge", text: "Di luar katalog" })),
        el("p", { class: "summary", text: mod.summary })),
      toggle),
    basic.length > 0 && mod.installed && el("div", { class: "settings" }, basic.map((s) => renderSetting(mod, s))),
    details);
}

function renderSetting(mod, setting) {
  const id = `set-${mod.id}-${setting.key}`;
  const save = (value) => act(call("setSetting", { modId: mod.id, key: setting.key, value }),
    `${setting.label} disimpan.`);

  if (setting.type === "bool") {
    return el("div", { class: "setting" },
      el("label", { for: id, text: setting.label }),
      el("label", { class: "switch" },
        el("input", { type: "checkbox", id, role: "switch", checked: setting.value === true, onchange: (e) => save(e.target.checked) }),
        el("span")),
      el("p", { class: "help", text: setting.help }));
  }

  const output = el("span", { class: "value", text: formatValue(setting, setting.value) });
  const isDefault = Number(setting.value) === Number(setting.default);
  return el("div", { class: "setting" },
    el("label", { for: id, text: setting.label }),
    output,
    el("input", {
      type: "range", id, min: String(setting.min), max: String(setting.max), step: String(setting.step || 1),
      value: String(setting.value),
      oninput: (e) => { output.textContent = formatValue(setting, e.target.value); },
      onchange: (e) => save(Number(e.target.value)),
    }),
    el("p", { class: "help", text: `${setting.help} Rentang ${formatValue(setting, setting.min)} sampai ${formatValue(setting, setting.max)}.` }),
    !isDefault && el("button", {
      class: "reset", type: "button", text: `Kembalikan ke bawaan (${formatValue(setting, setting.default)})`,
      onclick: () => save(Number(setting.default)),
    }));
}

// ---- Log view -------------------------------------------------------------------
async function loadLog() {
  const summary = $("log-summary");
  const problems = $("log-problems");
  const lines = $("log-lines");
  try {
    const log = await call("getLog");
    problems.replaceChildren();
    if (!log.found) {
      summary.textContent = "Belum ada log. Log muncul setelah game dijalankan lewat tombol Main.";
      lines.textContent = "";
      return;
    }
    const names = log.loadedModIds.map((id) => {
      const mod = snapshot && snapshot.state && snapshot.state.mods.find((m) => m.id.toLowerCase() === id.toLowerCase());
      return mod ? mod.name : id;
    });
    summary.replaceChildren(
      "Sesi terakhir: ", el("b", { text: log.writtenAt }), " · ",
      el("b", { text: String(names.length) }), " mod termuat",
      names.length ? ": " + names.join(", ") : "", ".");
    if (log.problems.length > 0) {
      problems.append(el("div", { class: "problems" },
        el("h3", { text: `${log.problems.length} baris perlu diperiksa` }),
        el("ul", {}, log.problems.map((p) => el("li", { text: p })))));
    }
    lines.textContent = (log.totalLines > log.lines.length ? `… ${log.totalLines - log.lines.length} baris sebelumnya disembunyikan …\n` : "")
      + log.lines.join("\n");
  } catch (error) {
    summary.textContent = error.message;
  }
}

// ---- Wiring ---------------------------------------------------------------------
function selectTab(name) {
  for (const tab of ["mods", "log"]) {
    $("tab-" + tab).setAttribute("aria-selected", String(tab === name));
    $("view-" + tab).hidden = tab !== name;
  }
  $("notices").hidden = name !== "mods";
  if (name === "log") loadLog();
}

$("tab-mods").addEventListener("click", () => selectTab("mods"));
$("tab-log").addEventListener("click", () => selectTab("log"));
$("log-refresh").addEventListener("click", loadLog);
for (const [id, target] of [["open-mods", "mods"], ["open-logs", "logs"], ["open-reloaded", "reloaded"]]) {
  $(id).addEventListener("click", () => call("open", { target }).catch((e) => toast(e.message)));
}
$("play").addEventListener("click", () => act(call("launch"), "Game dijalankan. Tunggu beberapa detik."));

async function refresh(full) {
  try {
    const fresh = await call("getState");
    const before = snapshot && JSON.stringify(snapshot.state);
    snapshot = fresh;
    // Avoid rebuilding the cards under the user's cursor unless something actually changed on disk.
    if (full || before !== JSON.stringify(fresh.state)) render();
    else { renderHeader(); }
  } catch (error) {
    toast(error.message);
  }
}

refresh(true);
setInterval(() => { if (!document.querySelector("input[type=range]:active")) refresh(false); }, 3000);
