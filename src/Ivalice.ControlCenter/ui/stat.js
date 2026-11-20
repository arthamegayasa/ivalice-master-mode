"use strict";

// ---- Stat Tuning view -----------------------------------------------------------
// Loaded after app.js and uses its helpers (call, el, $, toast). The rules are checked and written by
// the desktop host (Ivalice.Party.StatTuningRules); this page edits a draft of them.
let stat = null;        // last answer of getStatTuning
let statDraft = null;   // { applyJobsToEnemies, jobs: { [id]: { growth, multiplier } }, characters: { [fingerprint]: { name, growthPercent, multiplierPercent } } }
let statSavedKey = "";  // the saved rules, to tell whether the draft differs
let statJob = null;     // job id shown in the job editor
let statCharacter = null; // fingerprint shown in the character editor
let statDerived = [];   // functions that refresh numbers computed from the draft

const STAT_NAMES = ["HP", "MP", "Speed", "Serangan fisik (PA)", "Serangan sihir (MA)"];
const STAT_GROUPS = [["generic", "Job umum"], ["special", "Job khusus karakter"], ["monster", "Monster"]];
const STAT_STATUS = {
  modNotInstalled: ["warning", "Mod Stat Tuning belum terpasang. Jalankan scripts/install.ps1 dari repositori."],
  modDisabled: ["warning", "Mod Stat Tuning sedang nonaktif. Aturan di sini tidak dipakai game sampai mod diaktifkan di tab Mod."],
};

const statValues = (value) => STAT_NAMES.map(() => value);
// Move and Jump are not percentages: a job rule replaces the base value, a character rule adds to it.
const MOVEMENT = [
  { name: "Move", key: "move", bonusKey: "moveBonus", min: "minMove", max: "maxMove" },
  { name: "Jump", key: "jump", bonusKey: "jumpBonus", min: "minJump", max: "maxJump" },
];
const blankJob = () => ({ growth: statValues(null), multiplier: statValues(null), move: null, jump: null });
const blankCharacter = (name) => ({ name: name || null, growthPercent: statValues(100), multiplierPercent: statValues(100), moveBonus: 0, jumpBonus: 0 });
const jobChanged = (rule) => !!rule && [...rule.growth, ...rule.multiplier, rule.move, rule.jump].some((v) => v !== null);
const characterChanged = (rule) => !!rule && ([...rule.growthPercent, ...rule.multiplierPercent].some((v) => v !== 100) || rule.moveBonus !== 0 || rule.jumpBonus !== 0);
const wholeOr = (value, fallback) => (Number.isInteger(value) ? value : fallback);
const signed = (value) => (value > 0 ? "+" + value : String(value));
const clampNumber = (value, low, high) => Math.min(high, Math.max(low, value));

function draftFrom(rules) {
  const draft = { applyJobsToEnemies: !!rules.applyJobsToEnemies, jobs: {}, characters: {} };
  for (const job of rules.jobs || []) {
    draft.jobs[job.job] = { growth: [...job.growth], multiplier: [...job.multiplier], move: wholeOr(job.move, null), jump: wholeOr(job.jump, null) };
  }
  for (const c of rules.characters || []) {
    draft.characters[c.fingerprint] = {
      name: c.name || null, growthPercent: [...c.growthPercent], multiplierPercent: [...c.multiplierPercent],
      moveBonus: wholeOr(c.moveBonus, 0), jumpBonus: wholeOr(c.jumpBonus, 0),
    };
  }
  return draft;
}

function rulesFrom(draft) {
  return {
    schema: 1,
    applyJobsToEnemies: draft.applyJobsToEnemies,
    jobs: Object.entries(draft.jobs).filter(([, rule]) => jobChanged(rule))
      .map(([job, rule]) => ({ job: Number(job), growth: rule.growth, multiplier: rule.multiplier, move: rule.move, jump: rule.jump })),
    characters: Object.entries(draft.characters).filter(([, rule]) => characterChanged(rule))
      .map(([fingerprint, rule]) => ({ fingerprint, name: rule.name, growthPercent: rule.growthPercent, multiplierPercent: rule.multiplierPercent,
        moveBonus: rule.moveBonus, jumpBonus: rule.jumpBonus })),
  };
}

const draftKey = (draft) => JSON.stringify(rulesFrom(draft));
const statDirty = () => !!statDraft && draftKey(statDraft) !== statSavedKey;

async function loadStat(keepDraft) {
  const before = statDraft && draftKey(statDraft);
  let fresh;
  try {
    fresh = await call("getStatTuning");
  } catch (error) {
    toast(error.message);
    return;
  }
  // An edit made while the answer was on its way is kept.
  const editedMeanwhile = !!statDraft && draftKey(statDraft) !== before;
  // Rules that could not be read just now say nothing about what is saved: keep what is on screen.
  if (fresh.rulesBusy && stat) fresh.rules = stat.rules;
  stat = fresh;
  const saved = draftFrom(stat.rules);
  statSavedKey = draftKey(saved);
  if (!statDraft || !(keepDraft || editedMeanwhile)) statDraft = saved;
  renderStat();
}

// The values a job gives the player's units under the draft: the game's own, replaced where a rule says so.
function jobValues(jobId) {
  const job = stat.jobs.find((j) => j.job === jobId);
  if (!job) return null;
  const rule = statDraft.jobs[jobId];
  return {
    growth: job.growth.map((v, i) => (rule && rule.growth[i] !== null ? rule.growth[i] : v)),
    multiplier: job.multiplier.map((v, i) => (rule && rule.multiplier[i] !== null ? rule.multiplier[i] : v)),
    move: rule && rule.move !== null ? rule.move : job.move,
    jump: rule && rule.jump !== null ? rule.jump : job.jump,
  };
}

// Same arithmetic as StatTuningRules.Apply: a stat left at 100% keeps the job's value as it is;
// otherwise integer rounding, held within one byte.
const scaledGrowth = (growth, percent) => (percent === 100 ? growth
  : clampNumber(Math.floor((Math.max(1, growth) * 100 + Math.floor(percent / 2)) / percent), stat.limits.minValue, stat.limits.maxValue));
const scaledMultiplier = (multiplier, percent) => (percent === 100 ? multiplier
  : clampNumber(Math.floor((multiplier * percent + 50) / 100), stat.limits.minValue, stat.limits.maxValue));

function jobLabel(job) {
  const holders = stat.party.filter((u) => u.jobId === job.job && u.name).map((u) => u.name);
  const who = job.group === "special" && holders.length ? ` · ${holders.join(", ")}` : "";
  return `${jobChanged(statDraft.jobs[job.job]) ? "● " : ""}${job.name}${who} (#${job.job})`;
}

function characterChoices() {
  const seen = new Map();
  for (const unit of stat.party) {
    seen.set(unit.fingerprint, { fingerprint: unit.fingerprint, name: unit.name || `${unit.jobName || "Job " + unit.jobId} · slot ${unit.slot}`, unit });
  }
  for (const [fingerprint, rule] of Object.entries(statDraft.characters)) {
    if (!seen.has(fingerprint)) seen.set(fingerprint, { fingerprint, name: rule.name || fingerprint, unit: null });
  }
  return [...seen.values()];
}

function updateStatButtons() {
  const dirty = statDirty();
  $("stat-save").disabled = !dirty || !!pendingPreset || !!(stat && stat.rulesBusy);
  for (const button of document.querySelectorAll(".preset-quick button, .preset-form button"))
    button.disabled = !!pendingPreset;
  $("stat-discard").disabled = !dirty;
  for (const refresh of statDerived) refresh();
}

function numberInput(value, placeholder, low, high, label, onValue) {
  const input = el("input", {
    type: "number", min: String(low), max: String(high), step: "1", inputmode: "numeric", class: "stat-input",
    placeholder: placeholder === null ? "" : String(placeholder), "aria-label": label,
  });
  input.value = value === null ? "" : String(value);
  const read = () => {
    const text = input.value.trim();
    if (text === "" || Number.isNaN(Number(text))) return null;
    return clampNumber(Math.round(Number(text)), low, high);
  };
  input.addEventListener("input", () => { onValue(read(), input); updateStatButtons(); });
  // On leaving the field, show the value that was actually taken.
  input.addEventListener("change", () => { const taken = read(); input.value = taken === null ? "" : String(taken); onValue(taken, input); updateStatButtons(); });
  return input;
}

function renderStat() {
  if (!stat || !statDraft) return;
  statDerived = [];
  const notices = $("stat-status");
  const body = $("stat-body");
  notices.replaceChildren();
  body.replaceChildren();
  const notice = (level, message) => notices.append(el("div", { class: "notice " + level },
    el("strong", { text: { error: "Masalah", warning: "Perhatian", info: "Info" }[level] }), el("span", { text: message })));

  if (STAT_STATUS[stat.status]) notice(...STAT_STATUS[stat.status]);
  if (stat.rulesBusy) notice("warning", "Berkas aturan yang tersimpan sedang dipakai program lain dan tidak bisa dibaca. Menyimpan dimatikan agar aturan lama tidak tertimpa; buka tab ini lagi sebentar lagi.");
  if (stat.rulesUnreadable) notice("warning", "Berkas aturan yang tersimpan tidak bisa dibaca dan dianggap kosong. Menyimpan dari sini akan menggantinya.");
  if (stat.status === "ok" && stat.gameRunning) {
    notice("info", "Game sedang berjalan. Aturan yang disimpan langsung dipakai untuk unit yang dibuat sesudahnya: pertarungan berikutnya, atau saat layar Units dibuka lagi. Pertarungan yang sedang berlangsung tidak berubah.");
  }

  if (stat.jobs.length === 0) {
    body.append(el("div", { class: "empty" }, el("h2", { text: "Nilai bawaan job belum terbaca" }),
      el("p", { text: "Jalankan game sekali dengan mod Stat Tuning aktif (cukup sampai layar judul). Mod mencatat nilai growth dan multiplier asli tiap job, lalu daftar job muncul di sini." })));
    updateStatButtons();
    return;
  }

  if (statJob === null || !stat.jobs.some((j) => j.job === statJob)) statJob = (stat.jobs.find((j) => j.group === "generic") || stat.jobs[0]).job;
  const choices = characterChoices();
  if (!choices.some((c) => c.fingerprint === statCharacter)) statCharacter = choices.length ? choices[0].fingerprint : null;

  body.append(
    renderPreset(),
    el("div", { class: "stat-layout" }, renderJobEditor(), renderCharacterEditor(choices)),
    renderStatSummary(),
    el("article", { class: "card stat-card" },
      el("label", { class: "stat-check" },
        el("input", { type: "checkbox", checked: statDraft.applyJobsToEnemies,
          onchange: (e) => { statDraft.applyJobsToEnemies = e.target.checked; updateStatButtons(); } }),
        el("span", {}, el("b", { text: "Aturan job berlaku juga untuk musuh" }),
          el("span", { class: "stat-help", text: " Mati (bawaan): musuh memakai nilai asli game walaupun job-nya sama. Nyalakan hanya jika Anda memang ingin musuh ikut berubah. Aturan per karakter tidak pernah berlaku untuk musuh." })))),
    el("p", { class: "reinc-foot", text: "Move dan Jump: yang diatur di sini adalah nilai dasar dari job. Bonus dari perlengkapan dan ability (misalnya sepatu atau Move +1) tetap ditambahkan game di atasnya, jadi angka di game bisa lebih tinggi." }),
    el("p", { class: "reinc-foot", text: "Growth: angka lebih kecil berarti tumbuh lebih cepat. Tiap naik level, stat dasar bertambah sebesar stat dasar ÷ (growth + level). Mengubah growth hanya memengaruhi kenaikan level sesudahnya; level yang sudah didapat tidak dihitung ulang. Multiplier: persen dari stat dasar yang dipakai game, jadi perubahannya langsung terasa. Nilai Speed, PA, dan MA yang tampil di game dibulatkan ke bawah, jadi perubahan kecil bisa belum terlihat." }));
  updateStatButtons();
}

const presetForm = { scope: "generic", stats: statValues(true), growthPercent: 100, multiplierPercent: 100, moveBonus: 0, jumpBonus: 0 };
let pendingPreset = null;

async function applyPreset(growthPercent, multiplierPercent) {
  const stats = STAT_NAMES.flatMap((_, i) => presetForm.stats[i] ? [i] : []);
  if (!stats.length && !presetForm.moveBonus && !presetForm.jumpBonus) return toast("Pilih setidaknya satu stat, atau isi tambahan Move atau Jump.");
  if (pendingPreset) return;
  const request = {};
  pendingPreset = request;
  updateStatButtons();
  const sent = draftKey(statDraft);
  try {
    const preset = { ...presetForm, stats, growthPercent, multiplierPercent };
    const rules = await call("applyStatPreset", { rules: rulesFrom(statDraft), preset });
    if (request !== pendingPreset) return;
    if (draftKey(statDraft) !== sent) {
      toast("Draf berubah saat preset dihitung. Terapkan lagi untuk memakai draf terbaru.");
      return;
    }
    statDraft = draftFrom(rules);
    renderStat();
    const job = stat.jobs.find(j => j.job === statJob);
    const inScope = job && (job.group === preset.scope || (preset.scope === "both" && job.group !== "monster"));
    const message = draftKey(statDraft) === sent ? "Draf sudah sesuai dengan preset." : "Preset diterapkan ke draf.";
    toast(inScope ? message : message + " Job yang sedang ditampilkan berada di luar cakupan.", true);
  } catch (error) {
    if (request === pendingPreset) toast(error.message);
  } finally {
    if (request === pendingPreset) {
      pendingPreset = null;
      updateStatButtons();
    }
  }
}

function renderPreset() {
  const scope = el("select", { class: "stat-select", onchange: e => { presetForm.scope = e.target.value; } },
    [...STAT_GROUPS.filter(([id]) => id !== "monster"), ["both", "Job umum dan khusus"]].map(([id, name]) =>
      el("option", { value: id, selected: presetForm.scope === id, text: name })));
  const percent = (label, key) => el("label", {}, label, el("input", {
    class: "stat-input", type: "number", min: stat.limits.minPercent, max: stat.limits.maxPercent,
    step: 1, required: true, value: String(presetForm[key]), oninput: e => { presetForm[key] = e.target.value; },
  }));
  const checks = STAT_NAMES.map((name, i) => el("label", { class: "stat-check" },
    el("input", { type: "checkbox", checked: presetForm.stats[i], onchange: e => { presetForm.stats[i] = e.target.checked; } }), name));
  const bonus = (label, key) => el("label", {}, label, el("input", {
    class: "stat-input", type: "number", min: stat.limits.minBonus, max: stat.limits.maxBonus,
    step: 1, required: true, value: String(presetForm[key]), oninput: e => { presetForm[key] = e.target.value; },
  }));
  const quick = (label, growth, multiplier, names, moveBonus = 0) => el("button", { type: "button", class: "ghost", text: label,
    onclick: () => {
      presetForm.stats = STAT_NAMES.map(name => !names || names.includes(name));
      if (growth !== null) presetForm.growthPercent = growth;
      if (multiplier !== null) presetForm.multiplierPercent = multiplier;
      presetForm.moveBonus = moveBonus;
      presetForm.jumpBonus = 0;
      renderStat();
      applyPreset(growth, multiplier);
    } });
  return el("article", { class: "card stat-card preset-card" },
    el("div", { class: "stat-card-head" }, el("h3", { text: "Preset" }),
      el("button", { type: "button", class: "setting-reset", text: "Kembalikan semua job ke bawaan",
        onclick: () => { pendingPreset = null; statDraft.jobs = {}; renderStat(); } })),
    el("p", { class: "stat-help", text: "Preset memakai nilai bawaan tiap job. Pilih cakupan, lalu gunakan preset cepat atau atur sendiri. Tekan Simpan untuk menyimpan draf." }),
    el("label", { class: "preset-scope" }, "Cakupan", scope),
    el("div", { class: "preset-quick" }, quick("Tumbuh 25% lebih cepat", 125, null),
      quick("Tumbuh 50% lebih cepat", 150, null), quick("HP dan MP +20%", null, 120, ["HP", "MP"]),
      quick("Semua stat +10%", null, 110), quick("Move +1", null, null, null, 1)),
    el("form", { class: "preset-form", onsubmit: e => {
      e.preventDefault();
      presetForm.growthPercent = Number(presetForm.growthPercent);
      presetForm.multiplierPercent = Number(presetForm.multiplierPercent);
      presetForm.moveBonus = Number(presetForm.moveBonus);
      presetForm.jumpBonus = Number(presetForm.jumpBonus);
      applyPreset(presetForm.growthPercent, presetForm.multiplierPercent);
    } },
      el("fieldset", {}, el("legend", { text: "Stat" }), el("div", { class: "preset-checks" }, checks)),
      el("div", { class: "preset-values" }, percent("Kecepatan tumbuh (%)", "growthPercent"),
        percent("Multiplier (%)", "multiplierPercent"), bonus("Move (tambahan)", "moveBonus"), bonus("Jump (tambahan)", "jumpBonus"),
        el("button", { class: "ghost", type: "submit", text: "Terapkan ke draf" })),
      el("p", { class: "stat-help", text: "Isi 25–400%. Pada 100%, stat yang dicentang kembali ke nilai bawaan untuk kolom itu. Tambahan Move dan Jump berkisar dari -3 sampai +5. Isi 0 untuk mempertahankan draf gerak." })));
}

function renderJobEditor() {
  const job = stat.jobs.find((j) => j.job === statJob);
  const select = el("select", { class: "stat-select", "aria-label": "Pilih job",
    onchange: (e) => { statJob = Number(e.target.value); renderStat(); } });
  for (const [group, title] of STAT_GROUPS) {
    const options = stat.jobs.filter((j) => j.group === group);
    if (!options.length) continue;
    select.append(el("optgroup", { label: title }, options.map((j) => el("option", { value: String(j.job), text: jobLabel(j), selected: j.job === statJob }))));
  }

  const rule = () => statDraft.jobs[statJob] || (statDraft.jobs[statJob] = blankJob());
  const current = statDraft.jobs[statJob] || blankJob();
  const rows = STAT_NAMES.map((name, i) => {
    const mark = (input, value) => input.classList.toggle("changed", value !== null);
    const growth = numberInput(current.growth[i], job.growth[i], stat.limits.minValue, stat.limits.maxValue, `Growth ${name}`,
      (value, input) => { rule().growth[i] = value === job.growth[i] ? null : value; mark(input, rule().growth[i]); });
    const multiplier = numberInput(current.multiplier[i], job.multiplier[i], stat.limits.minValue, stat.limits.maxValue, `Multiplier ${name}`,
      (value, input) => { rule().multiplier[i] = value === job.multiplier[i] ? null : value; mark(input, rule().multiplier[i]); });
    mark(growth, current.growth[i]);
    mark(multiplier, current.multiplier[i]);
    return el("tr", {}, el("th", { scope: "row", text: name }),
      el("td", { class: "num muted", text: String(job.growth[i]) }), el("td", {}, growth),
      el("td", { class: "num muted", text: job.multiplier[i] + "%" }), el("td", {}, multiplier));
  });

  // A catalog written before the mod recorded Move and Jump has 0 for both: nothing to show or edit yet.
  const movementRows = MOVEMENT.filter((m) => job[m.key] > 0).map((m) => {
    const mark = (input, value) => input.classList.toggle("changed", value !== null);
    const input = numberInput(current[m.key], job[m.key], stat.limits[m.min], stat.limits[m.max], `${m.name} dasar`,
      (value, field) => { rule()[m.key] = value === job[m.key] ? null : value; mark(field, rule()[m.key]); });
    mark(input, current[m.key]);
    return el("tr", {}, el("th", { scope: "row", text: m.name }), el("td", { class: "num muted", text: String(job[m.key]) }), el("td", {}, input));
  });

  return el("article", { class: "card stat-card" },
    el("div", { class: "stat-card-head" }, el("h3", { text: "Per job" }),
      el("button", { class: "setting-reset", type: "button", text: "Kembalikan job ini ke bawaan",
        onclick: () => { delete statDraft.jobs[statJob]; renderStat(); } })),
    el("p", { class: "stat-help", text: "Bawaan adalah angka asli game. Kolom draf menunjukkan hasil preset dan bisa diedit. Angka growth yang lebih kecil membuat stat tumbuh lebih cepat. Kosongkan isian untuk memakai nilai bawaan." }),
    select,
    el("table", { class: "stat-table" },
      el("thead", {},
        el("tr", {}, el("th", { rowspan: "2", text: "Stat" }), el("th", { colspan: "2", class: "group", text: "Growth" }),
          el("th", { colspan: "2", class: "group", text: "Multiplier" })),
        el("tr", {}, el("th", { class: "num", text: "bawaan" }), el("th", { text: "draf" }),
          el("th", { class: "num", text: "bawaan" }), el("th", { text: "draf (%)" }))),
      el("tbody", {}, rows)),
    movementRows.length
      ? el("table", { class: "stat-table stat-movement" },
        el("thead", {}, el("tr", {}, el("th", { text: "Gerak (nilai dasar)" }), el("th", { class: "num", text: "bawaan" }), el("th", { text: "baru" }))),
        el("tbody", {}, movementRows))
      : el("p", { class: "stat-help", text: "Move dan Jump job ini belum terbaca. Jalankan game sekali dengan mod versi terbaru." }));
}

function renderCharacterEditor(choices) {
  const card = el("article", { class: "card stat-card" },
    el("div", { class: "stat-card-head" }, el("h3", { text: "Per karakter" }),
      statCharacter && el("button", { class: "setting-reset", type: "button", text: "Kembalikan karakter ini ke bawaan",
        onclick: () => { delete statDraft.characters[statCharacter]; renderStat(); } })),
    el("p", { class: "stat-help", text: "Persentase di atas nilai job yang sedang dipakai karakter, dan ikut ke job apa pun yang ia pakai nanti. 100% berarti tanpa perubahan. Kecepatan tumbuh 200% membagi dua angka growth job: efeknya besar di level rendah dan makin kecil di level tinggi (lihat kolom Hasil)." }));

  if (!choices.length) {
    card.append(el("p", { class: "stat-help", text: stat.gameRunning
      ? "Daftar karakter belum terbaca. Muat save Anda di game; daftar diambil lewat mod Reincarnation, jadi mod itu perlu aktif."
      : "Jalankan game dan muat save Anda untuk memilih karakter. Daftar diambil lewat mod Reincarnation, jadi mod itu perlu aktif." }));
    return card;
  }

  const choice = choices.find((c) => c.fingerprint === statCharacter);
  card.append(el("select", { class: "stat-select", "aria-label": "Pilih karakter",
    onchange: (e) => { statCharacter = e.target.value; renderStat(); } },
    choices.map((c) => el("option", { value: c.fingerprint, selected: c.fingerprint === statCharacter,
      text: `${characterChanged(statDraft.characters[c.fingerprint]) ? "● " : ""}${c.name}${c.unit ? ` · ${c.unit.jobName || "Job " + c.unit.jobId} Lv ${c.unit.level}` : " · tidak ada di party sekarang"}` }))));

  const rule = () => statDraft.characters[statCharacter] || (statDraft.characters[statCharacter] = blankCharacter(choice.unit ? choice.unit.name : choice.name));
  const current = statDraft.characters[statCharacter] || blankCharacter();
  const low = stat.limits.minPercent, high = stat.limits.maxPercent;
  const rows = STAT_NAMES.map((name, i) => {
    const mark = (input, value) => input.classList.toggle("changed", value !== 100);
    const growth = numberInput(current.growthPercent[i] === 100 ? null : current.growthPercent[i], 100, low, high, `Kecepatan tumbuh ${name} (persen)`,
      (value, input) => { rule().growthPercent[i] = value === null ? 100 : value; mark(input, rule().growthPercent[i]); });
    const multiplier = numberInput(current.multiplierPercent[i] === 100 ? null : current.multiplierPercent[i], 100, low, high, `Multiplier ${name} (persen)`,
      (value, input) => { rule().multiplierPercent[i] = value === null ? 100 : value; mark(input, rule().multiplierPercent[i]); });
    mark(growth, current.growthPercent[i]);
    mark(multiplier, current.multiplierPercent[i]);

    const result = el("td", { class: "num muted" });
    statDerived.push(() => {
      const values = choice.unit ? jobValues(choice.unit.jobId) : null;
      const now = statDraft.characters[statCharacter] || blankCharacter();
      result.textContent = values
        ? `growth ${scaledGrowth(values.growth[i], now.growthPercent[i])} · ×${scaledMultiplier(values.multiplier[i], now.multiplierPercent[i])}%`
        : "—";
    });
    return el("tr", {}, el("th", { scope: "row", text: name }), el("td", {}, growth), el("td", {}, multiplier), result);
  });

  card.append(el("table", { class: "stat-table" },
    el("thead", {}, el("tr", {}, el("th", { text: "Stat" }), el("th", { text: "Kecepatan tumbuh %" }), el("th", { text: "Multiplier %" }),
      el("th", { class: "num", text: "Hasil di job sekarang" }))),
    el("tbody", {}, rows)));

  const movementRows = MOVEMENT.map((m) => {
    const mark = (input, value) => input.classList.toggle("changed", value !== 0);
    const input = numberInput(current[m.bonusKey] === 0 ? null : current[m.bonusKey], 0, stat.limits.minBonus, stat.limits.maxBonus, `${m.name} tambahan`,
      (value, field) => { rule()[m.bonusKey] = value === null ? 0 : value; mark(field, rule()[m.bonusKey]); });
    mark(input, current[m.bonusKey]);
    const result = el("td", { class: "num muted" });
    statDerived.push(() => {
      const values = choice.unit ? jobValues(choice.unit.jobId) : null;
      const now = statDraft.characters[statCharacter] || blankCharacter();
      const base = values ? values[m.key] : 0;
      // Same rule as StatTuningRules.ApplyMovement: a bonus of 0 keeps the job's value as it is.
      result.textContent = base > 0
        ? `${m.name} dasar ${now[m.bonusKey] === 0 ? base : clampNumber(base + now[m.bonusKey], stat.limits[m.min], stat.limits[m.max])}`
        : "—";
    });
    return el("tr", {}, el("th", { scope: "row", text: m.name }), el("td", {}, input), result);
  });
  card.append(el("table", { class: "stat-table stat-movement" },
    el("thead", {}, el("tr", {}, el("th", { text: "Gerak" }), el("th", { text: "Tambahan (-3 sampai +5)" }), el("th", { class: "num", text: "Hasil di job sekarang" }))),
    el("tbody", {}, movementRows)));
  return card;
}

function renderStatSummary() {
  const list = el("ul", { class: "stat-summary" });
  const card = el("article", { class: "card stat-card" }, el("div", { class: "stat-card-head" }, el("h3", { text: "Yang sedang diubah" })), list);
  const refresh = () => {
    const items = [];
    for (const [id, rule] of Object.entries(statDraft.jobs)) {
      if (!jobChanged(rule)) continue;
      const job = stat.jobs.find((j) => j.job === Number(id));
      const parts = STAT_NAMES.map((name, i) => {
        const bits = [];
        if (rule.growth[i] !== null) bits.push(`growth ${job ? job.growth[i] + " → " : ""}${rule.growth[i]}`);
        if (rule.multiplier[i] !== null) bits.push(`multiplier ${job ? job.multiplier[i] + "% → " : ""}${rule.multiplier[i]}%`);
        return bits.length ? `${name}: ${bits.join(", ")}` : null;
      }).filter(Boolean);
      for (const m of MOVEMENT) {
        if (rule[m.key] !== null) parts.push(`${m.name} dasar ${job && job[m.key] > 0 ? job[m.key] + " → " : ""}${rule[m.key]}`);
      }
      items.push(el("li", {}, el("b", { text: `Job ${job ? job.name : ""} (#${id})` }), " — " + parts.join("; ")));
    }
    for (const [fingerprint, rule] of Object.entries(statDraft.characters)) {
      if (!characterChanged(rule)) continue;
      const parts = STAT_NAMES.map((name, i) => {
        const bits = [];
        if (rule.growthPercent[i] !== 100) bits.push(`tumbuh ${rule.growthPercent[i]}%`);
        if (rule.multiplierPercent[i] !== 100) bits.push(`multiplier ${rule.multiplierPercent[i]}%`);
        return bits.length ? `${name}: ${bits.join(", ")}` : null;
      }).filter(Boolean);
      for (const m of MOVEMENT) {
        if (rule[m.bonusKey] !== 0) parts.push(`${m.name} ${signed(rule[m.bonusKey])}`);
      }
      items.push(el("li", {}, el("b", { text: rule.name || fingerprint }), " — " + parts.join("; ")));
    }
    if (!items.length) items.push(el("li", { class: "muted", text: "Belum ada perubahan. Semua unit memakai nilai bawaan game." }));
    list.replaceChildren(...items);
  };
  statDerived.push(refresh);
  return card;
}

async function saveStat() {
  if (!statDraft) return;
  $("stat-save").disabled = true;
  const sent = draftKey(statDraft);
  try {
    stat = await call("saveStatTuning", { rules: rulesFrom(statDraft) });
    const saved = draftFrom(stat.rules);
    statSavedKey = draftKey(saved);
    // An edit made while the save was on its way stays on screen, still unsaved.
    if (draftKey(statDraft) === sent) statDraft = saved;
    toast(stat.gameRunning ? "Tersimpan. Berlaku untuk unit yang dibuat sesudah ini." : "Tersimpan. Berlaku saat game dijalankan.", true);
  } catch (error) {
    toast(error.message);
  }
  renderStat();
}

$("stat-save").addEventListener("click", saveStat);
$("stat-discard").addEventListener("click", () => { pendingPreset = null; statDraft = draftFrom(stat.rules); renderStat(); });
