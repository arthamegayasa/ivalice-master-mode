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
function toast(message, good = false, milliseconds = good ? 2600 : 6500) {
  const box = $("toast");
  box.textContent = message;
  box.classList.toggle("good", good);
  box.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.hidden = true; }, milliseconds);
}

// ---- State ----------------------------------------------------------------------
let snapshot = null;
let launchingUntil = 0; // The game takes a few seconds to appear as a process; block a second launch meanwhile.
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

  const launching = !snapshot.gameRunning && Date.now() < launchingUntil;
  play.disabled = snapshot.gameRunning || launching || errors > 0;
  $("play-sub").textContent = snapshot.gameRunning
    ? "Game sedang berjalan"
    : launching ? "Sedang menjalankan game…"
    : errors > 0 ? "Perbaiki masalah di bawah dulu" : "Jalankan game dengan mod aktif";

  const footer = document.querySelector("footer");
  const onReincarnation = currentTab === "reinc";
  footer.classList.toggle("live", snapshot.gameRunning && !onReincarnation);
  $("footer-note").textContent = onReincarnation
    ? "Reinkarnasi berlaku langsung di game yang sedang berjalan. Simpan game agar permanen."
    : snapshot.gameRunning
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
      summary.textContent = "Belum ada log baru. Log muncul setelah game dijalankan lewat tombol Main; log lama diarsipkan Reloaded-II ke OldLogs.zip.";
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

// ---- Reincarnation view ---------------------------------------------------------
// Every number shown here comes from the mod and the desktop host (Ivalice.Party); this page only lays it out.
let reinc = null;       // last answer of getRoster
let reincShown = "";    // what is on screen, to skip rebuilding the cards when nothing changed
let reincBusy = null;   // { commandId, title, startedAt } while a command waits for the game
let reincChoice = null; // the unit the confirmation dialog is about

const REINC_STATUS = {
  modNotInstalled: ["warning", "Mod Reinkarnasi belum terpasang. Jalankan scripts/install.ps1 dari repositori."],
  modDisabled: ["warning", "Mod Reinkarnasi sedang nonaktif. Aktifkan di tab Mod, lalu jalankan game."],
  gameNotRunning: ["info", "Game tidak sedang berjalan. Jalankan game dengan tombol Main dan muat save Anda; daftar karakter akan muncul di sini."],
  noData: ["info", "Menunggu data dari game. Jika game baru saja dijalankan, tunggu beberapa detik. Jika game sudah berjalan sebelum mod diaktifkan, tutup game lalu jalankan lagi."],
  unreadable: ["error", "Data dari game tidak bisa dibaca. Versi mod dan aplikasi mungkin berbeda; jalankan scripts/install.ps1 lagi."],
};

const REINC_REASON = {
  emptySlot: () => "Slot ini kosong.",
  badSlot: () => "Slot ini tidak ada.",
  guest: () => "Tamu tidak bisa bereinkarnasi.",
  monster: () => "Monster tidak bisa bereinkarnasi.",
  unknownKind: () => "Jenis unit ini tidak dikenali, jadi stat awalnya tidak diketahui.",
  onErrand: () => "Sedang pergi menjalankan errand.",
  levelTooLow: (unit) => `Butuh level ${reinc.rules.minimumLevel}` + (unit ? ` (sekarang level ${unit.level}).` : "."),
  spareBytesInUse: () => "Tempat penyimpanan catatan reinkarnasi unit ini berisi data lain; mod tidak akan menimpanya.",
  inBattle: () => "Sedang dalam pertarungan atau adegan cerita. Lakukan di peta dunia.",
  boundToBattleUnit: () => "Game masih memegang salinan pertarungan unit ini yang bisa menimpa perubahan.",
  gameStateUnknown: () => "Keadaan game tidak bisa dibaca, jadi mod tidak mau mengambil risiko.",
  fingerprintMismatch: () => "Slot ini sekarang berisi unit lain. Muat ulang daftar lalu coba lagi.",
  levelChanged: () => "Level unit berubah sejak Anda mengonfirmasi. Muat ulang daftar lalu coba lagi.",
  expired: () => "Perintah kedaluwarsa sebelum game sempat menjalankannya.",
  malformed: () => "Perintah tidak dipahami mod. Versi mod dan aplikasi mungkin berbeda.",
  unknownAction: () => "Perintah tidak dikenal mod. Versi mod dan aplikasi mungkin berbeda.",
  journalFailed: () => "Jurnal tidak bisa ditulis, jadi tidak ada yang diubah.",
};

const reasonText = (code, unit) => (REINC_REASON[code] ? REINC_REASON[code](unit) : `Ditolak game (${code}).`);
const percent = (permille) => (permille / 10).toLocaleString("id-ID", { maximumFractionDigits: 1 }) + "%";
const unitTitle = (unit) => unit.name || `${unit.jobName || "Job " + unit.jobId} · slot ${unit.slot}`;
const statLine = (stats) =>
  `HP ${stats.hp} · MP ${stats.mp} · Speed ${stats.speed} · PA ${stats.physicalAttack} · MA ${stats.magickAttack}`;

async function loadRoster(force) {
  try {
    reinc = await call("getRoster");
  } catch (error) {
    toast(error.message);
    return;
  }
  renderReinc(force);
}

function renderReinc(force) {
  if (!reinc) return;
  // The age is left out so a quiet game does not rebuild the cards on every poll.
  const stale = reinc.status === "stale";
  const key = JSON.stringify([reinc.status, reinc.commandPending, stale && Math.round(reinc.ageSeconds / 10),
    reinc.roster && [reinc.roster.safeMode, reinc.roster.game, reinc.roster.units], reincBusy && reincBusy.commandId]);
  renderWaiting();
  if (!force && key === reincShown) return;
  reincShown = key;

  const notices = $("reinc-status");
  const list = $("reinc-units");
  const scroll = document.querySelector("main").scrollTop;
  notices.replaceChildren();
  list.replaceChildren();
  const notice = (level, message) => notices.append(el("div", { class: "notice " + level },
    el("strong", { text: { error: "Masalah", warning: "Perhatian", info: "Info" }[level] }), el("span", { text: message })));

  if (REINC_STATUS[reinc.status]) notice(...REINC_STATUS[reinc.status]);
  const roster = reinc.roster;
  $("reinc-foot").hidden = !roster || roster.units.length === 0;
  if (!roster) return;

  if (stale) {
    notice("warning", `Game belum memperbarui data selama ${Math.round(reinc.ageSeconds)} detik. Game mungkin sedang dijeda karena jendelanya tidak aktif; perintah baru dijalankan saat game aktif lagi.`);
  }
  if (roster.safeMode) notice("info", "Mode aman menyala: mod memeriksa perintah dan menjawab apa yang akan terjadi, tetapi tidak mengubah apa pun di game.");
  if (roster.game.inBattle) notice("info", "Game sedang dalam pertarungan atau adegan cerita. Reinkarnasi bisa dilakukan setelah kembali ke peta dunia.");
  if (roster.units.length === 0) {
    list.append(el("div", { class: "empty" }, el("h2", { text: "Belum ada karakter" }),
      el("p", { text: "Muat save Anda di game; daftar karakter muncul di sini." })));
    return;
  }

  list.append(...roster.units.map(renderUnit));
  document.querySelector("main").scrollTop = scroll;
}

function renderUnit(unit) {
  const stat = (label, value) => el("div", {}, el("dt", { text: label }), el("dd", { text: value === null ? "?" : String(value) }));
  const shown = unit.shown || { hp: null, mp: null, speed: null, physicalAttack: null, magickAttack: null };
  const levelOk = unit.level >= reinc.rules.minimumLevel;
  const next = unit.next;

  return el("article", { class: "card unit" + (unit.eligible ? "" : " off") },
    el("div", { class: "card-top" },
      el("div", { class: "card-main" },
        el("div", { class: "card-title" },
          el("h3", { text: unitTitle(unit) }),
          el("span", { class: "meta", text: `${unit.jobName || "Job " + unit.jobId} · Lv ${unit.level}` }),
          unit.reincarnations > 0 && el("span", { class: "badge lock", text: `Reinkarnasi ×${unit.reincarnations}` }),
          unit.powerPermille > 0 && el("span", { class: "badge lock", text: `Power +${percent(unit.powerPermille)}` })),
        el("dl", { class: "unit-stats" },
          stat("HP", shown.hp), stat("MP", shown.mp), stat("Speed", shown.speed),
          stat("PA", shown.physicalAttack), stat("MA", shown.magickAttack),
          stat("Brave", unit.brave), stat("Faith", unit.faith)))),
    next && levelOk && el("div", { class: "unit-next" },
      el("span", {}, "Reinkarnasi sekarang: ", el("b", { text: `Power +${percent(next.gainPermille)}` }),
        ` (total ${percent(next.totalPermille)})`),
      next.shown && el("span", { class: "muted", text: `Stat level 1 sesudahnya: ${statLine(next.shown)}` })),
    el("div", { class: "unit-action" },
      unit.eligible
        ? el("p", { class: "unit-reason", text: `Level ${unit.level} kembali ke 1.` })
        : el("p", { class: "unit-reason", text: reasonText(unit.reason, unit) }),
      unit.eligible && el("button", {
        class: "confirm", type: "button", text: "Reinkarnasi", disabled: !!reincBusy || reinc.commandPending,
        "aria-label": "Reinkarnasi " + unitTitle(unit), onclick: () => askReincarnation(unit),
      })));
}

function askReincarnation(unit) {
  reincChoice = unit;
  const next = unit.next;
  $("reinc-dialog-title").textContent = `Reinkarnasi ${unitTitle(unit)}?`;
  $("reinc-dialog-points").replaceChildren(...[
    `Level ${unit.level} kembali ke 1 dan EXP kembali ke 0.`,
    `Power bertambah ${percent(next.gainPermille)} menjadi total ${percent(next.totalPermille)}: stat awalnya naik sebesar itu, permanen.`,
    next.shown && `Stat level 1 sesudahnya: ${statLine(next.shown)} (job sekarang, tanpa perlengkapan).`,
    "Job, JP, ability, perlengkapan, Brave, dan Faith tidak berubah.",
    reinc.roster.safeMode
      ? "Mode aman menyala: game hanya memeriksa dan menjawab, tidak ada yang diubah."
      : "Sebelum perintah dikirim, semua berkas save dicadangkan ke folder cadangan save.",
    "Perubahan baru permanen setelah game menyimpan, lewat simpan manual maupun otomatis. Selama belum tersimpan, memuat save lama membatalkannya.",
  ].filter(Boolean).map((text) => el("li", { text })));
  $("reinc-dialog").showModal();
}

async function sendReincarnation(unit) {
  const title = unitTitle(unit);
  try {
    // The dialog told the player whether this is a simulation; the command carries that promise.
    const sent = await call("reincarnate", {
      slot: unit.slot, fingerprint: unit.fingerprint, level: unit.level, simulate: reinc.roster.safeMode === true,
    });
    reincBusy = { commandId: sent.commandId, title, startedAt: Date.now() };
    renderReinc(true);
    pollReincarnation();
  } catch (error) {
    toast(error.message);
    loadRoster(true);
  }
}

function renderWaiting() {
  const box = $("reinc-waiting");
  box.hidden = !reincBusy;
  if (!reincBusy) return;
  const seconds = (Date.now() - reincBusy.startedAt) / 1000;
  $("reinc-waiting-text").textContent = seconds < 5
    ? `Menunggu game menjalankan reinkarnasi ${reincBusy.title}…`
    : `Game belum menjawab (${Math.round(seconds)} detik). Jika jendela game tidak aktif, kembali ke game. Perintah kedaluwarsa setelah ${Math.round(reinc.rules.commandMaxAgeSeconds / 60)} menit.`;
}

function showReincarnationResult(busy, result) {
  const power = result.unit ? percent(result.unit.powerPermille) : "";
  if (result.ok && result.applied) {
    toast(`${busy.title} bereinkarnasi: sekarang level 1 dengan Power total ${power}. Simpan game agar permanen.`, true, 9000);
  } else if (result.ok) {
    toast(`Mode aman: tidak ada yang diubah. Tanpa mode aman, ${busy.title} menjadi level 1 dengan Power total ${power}.`, true, 9000);
  } else {
    toast(`Reinkarnasi ${busy.title} ditolak: ${reasonText(result.reason, result.unit)}`, false, 9000);
  }
}

async function pollReincarnation() {
  const busy = reincBusy;
  if (!busy) return;
  const finish = (message, good) => {
    reincBusy = null;
    if (message) toast(message, good, 9000);
    loadRoster(true);
  };

  try {
    const answer = await call("getReincarnationResult", { commandId: busy.commandId });
    if (reincBusy !== busy) return;
    if (answer.done) {
      reincBusy = null;
      showReincarnationResult(busy, answer.result);
      loadRoster(true);
      return;
    }

    // The mod refuses a command older than this, so there is nothing left to wait for.
    if (Date.now() - busy.startedAt > (reinc.rules.commandMaxAgeSeconds + 5) * 1000) {
      const taken = await call("cancelReincarnation", { commandId: busy.commandId });
      const late = taken.cancelled ? { done: false } : await call("getReincarnationResult", { commandId: busy.commandId });
      if (late.done) { reincBusy = null; showReincarnationResult(busy, late.result); loadRoster(true); }
      else if (taken.cancelled) finish(`Game tidak menjawab; perintah untuk ${busy.title} ditarik kembali. Tidak ada yang diubah.`, false);
      else finish(`Game sudah mengambil perintah untuk ${busy.title} tetapi jawabannya tidak sampai. Periksa level karakter itu di daftar ini atau di game sebelum mencoba lagi.`, false);
      return;
    }
  } catch (error) {
    if (reincBusy === busy) finish(error.message, false);
    return;
  }

  renderWaiting();
  setTimeout(pollReincarnation, 400);
}

async function cancelReincarnation() {
  const busy = reincBusy;
  if (!busy) return;
  try {
    const answer = await call("cancelReincarnation", { commandId: busy.commandId });
    if (reincBusy !== busy) return;
    if (answer.cancelled) {
      reincBusy = null;
      toast(`Perintah untuk ${busy.title} dibatalkan. Tidak ada yang diubah.`, true, 5000);
      loadRoster(true);
    } else {
      toast("Game sudah mengambil perintah itu; menunggu jawabannya.");
    }
  } catch (error) {
    toast(error.message);
  }
}

// ---- Wiring ---------------------------------------------------------------------
let currentTab = "mods";
function selectTab(name) {
  currentTab = name;
  for (const tab of ["mods", "reinc", "log"]) {
    $("tab-" + tab).setAttribute("aria-selected", String(tab === name));
    $("view-" + tab).hidden = tab !== name;
  }
  $("notices").hidden = name !== "mods";
  renderHeader();
  if (name === "log") loadLog();
  if (name === "reinc") loadRoster(true);
}

$("tab-mods").addEventListener("click", () => selectTab("mods"));
$("tab-reinc").addEventListener("click", () => selectTab("reinc"));
$("tab-log").addEventListener("click", () => selectTab("log"));
$("log-refresh").addEventListener("click", loadLog);
$("reinc-refresh").addEventListener("click", () => loadRoster(true));
$("reinc-cancel").addEventListener("click", cancelReincarnation);
$("open-backups").addEventListener("click", () => call("open", { target: "saveBackups" }).catch((e) => toast(e.message)));
$("reinc-dialog-cancel").addEventListener("click", () => $("reinc-dialog").close());
$("reinc-dialog-confirm").addEventListener("click", () => {
  $("reinc-dialog").close();
  if (reincChoice && !reincBusy) sendReincarnation(reincChoice);
  reincChoice = null;
});
for (const [id, target] of [["open-mods", "mods"], ["open-logs", "logs"], ["open-reloaded", "reloaded"]]) {
  $(id).addEventListener("click", () => call("open", { target }).catch((e) => toast(e.message)));
}
$("play").addEventListener("click", async () => {
  $("play").disabled = true;
  try {
    snapshot = await call("launch");
    launchingUntil = Date.now() + 30000;
    toast("Game dijalankan. Tunggu beberapa detik.", true);
  } catch (error) {
    toast(error.message);
  }
  render();
});

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
setInterval(() => { if (currentTab === "reinc" && !$("reinc-dialog").open) loadRoster(false); }, 2000);
