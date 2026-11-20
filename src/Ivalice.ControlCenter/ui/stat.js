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

const blankJob = () => ({ growth: [null, null, null, null, null], multiplier: [null, null, null, null, null] });
const blankCharacter = (name) => ({ name: name || null, growthPercent: [100, 100, 100, 100, 100], multiplierPercent: [100, 100, 100, 100, 100] });
const jobChanged = (rule) => !!rule && [...rule.growth, ...rule.multiplier].some((v) => v !== null);
const characterChanged = (rule) => !!rule && [...rule.growthPercent, ...rule.multiplierPercent].some((v) => v !== 100);
const clampNumber = (value, low, high) => Math.min(high, Math.max(low, value));

function draftFrom(rules) {
  const draft = { applyJobsToEnemies: !!rules.applyJobsToEnemies, jobs: {}, characters: {} };
  for (const job of rules.jobs || []) draft.jobs[job.job] = { growth: [...job.growth], multiplier: [...job.multiplier] };
  for (const c of rules.characters || []) {
    draft.characters[c.fingerprint] = { name: c.name || null, growthPercent: [...c.growthPercent], multiplierPercent: [...c.multiplierPercent] };
  }
  return draft;
}

function rulesFrom(draft) {
  return {
    schema: 1,
    applyJobsToEnemies: draft.applyJobsToEnemies,
    jobs: Object.entries(draft.jobs).filter(([, rule]) => jobChanged(rule))
      .map(([job, rule]) => ({ job: Number(job), growth: rule.growth, multiplier: rule.multiplier })),
    characters: Object.entries(draft.characters).filter(([, rule]) => characterChanged(rule))
      .map(([fingerprint, rule]) => ({ fingerprint, name: rule.name, growthPercent: rule.growthPercent, multiplierPercent: rule.multiplierPercent })),
  };
}

const draftKey = (draft) => JSON.stringify(rulesFrom(draft));
const statDirty = () => !!statDraft && draftKey(statDraft) !== statSavedKey;

async function loadStat(keepDraft) {
  let fresh;
  try {
    fresh = await call("getStatTuning");
  } catch (error) {
    toast(error.message);
    return;
  }
  stat = fresh;
  const saved = draftFrom(stat.rules);
  statSavedKey = draftKey(saved);
  if (!keepDraft || !statDraft) statDraft = saved;
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
  };
}

// Same arithmetic as StatTuningRules.Apply: integer rounding, held within one byte.
const scaledGrowth = (growth, percent) => clampNumber(Math.floor((Math.max(1, growth) * 100 + Math.floor(percent / 2)) / percent), stat.limits.minValue, stat.limits.maxValue);
const scaledMultiplier = (multiplier, percent) => clampNumber(Math.floor((multiplier * percent + 50) / 100), stat.limits.minValue, stat.limits.maxValue);

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
  $("stat-save").disabled = !dirty;
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
    el("div", { class: "stat-layout" }, renderJobEditor(), renderCharacterEditor(choices)),
    renderStatSummary(),
    el("article", { class: "card stat-card" },
      el("label", { class: "stat-check" },
        el("input", { type: "checkbox", checked: statDraft.applyJobsToEnemies,
          onchange: (e) => { statDraft.applyJobsToEnemies = e.target.checked; updateStatButtons(); } }),
        el("span", {}, el("b", { text: "Aturan job berlaku juga untuk musuh" }),
          el("span", { class: "stat-help", text: " Mati (bawaan): musuh memakai nilai asli game walaupun job-nya sama. Nyalakan hanya jika Anda memang ingin musuh ikut berubah. Aturan per karakter tidak pernah berlaku untuk musuh." })))),
    el("p", { class: "reinc-foot", text: "Growth: angka lebih kecil berarti tumbuh lebih cepat. Tiap naik level, stat dasar bertambah sebesar stat dasar ÷ (growth + level). Mengubah growth hanya memengaruhi kenaikan level sesudahnya; level yang sudah didapat tidak dihitung ulang. Multiplier: persen dari stat dasar yang dipakai game, jadi perubahannya langsung terasa. Nilai Speed, PA, dan MA yang tampil di game dibulatkan ke bawah, jadi perubahan kecil bisa belum terlihat." }));
  updateStatButtons();
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

  return el("article", { class: "card stat-card" },
    el("div", { class: "stat-card-head" }, el("h3", { text: "Per job" }),
      el("button", { class: "setting-reset", type: "button", text: "Kembalikan job ini ke bawaan",
        onclick: () => { delete statDraft.jobs[statJob]; renderStat(); } })),
    el("p", { class: "stat-help", text: "Berlaku untuk semua unit Anda yang sedang memakai job ini. Kosongkan kolom untuk memakai nilai bawaan." }),
    select,
    el("table", { class: "stat-table" },
      el("thead", {},
        el("tr", {}, el("th", { rowspan: "2", text: "Stat" }), el("th", { colspan: "2", class: "group", text: "Growth" }),
          el("th", { colspan: "2", class: "group", text: "Multiplier" })),
        el("tr", {}, el("th", { class: "num", text: "bawaan" }), el("th", { text: "baru" }),
          el("th", { class: "num", text: "bawaan" }), el("th", { text: "baru" }))),
      el("tbody", {}, rows)));
}

function renderCharacterEditor(choices) {
  const card = el("article", { class: "card stat-card" },
    el("div", { class: "stat-card-head" }, el("h3", { text: "Per karakter" }),
      statCharacter && el("button", { class: "setting-reset", type: "button", text: "Kembalikan karakter ini ke 100%",
        onclick: () => { delete statDraft.characters[statCharacter]; renderStat(); } })),
    el("p", { class: "stat-help", text: "Persentase di atas nilai job yang sedang dipakai karakter, dan ikut ke job apa pun yang ia pakai nanti. 100% berarti tanpa perubahan; growth 200% berarti tumbuh kira-kira dua kali lebih cepat." }));

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
  try {
    stat = await call("saveStatTuning", { rules: rulesFrom(statDraft) });
    statDraft = draftFrom(stat.rules);
    statSavedKey = draftKey(statDraft);
    toast(stat.gameRunning ? "Tersimpan. Berlaku untuk unit yang dibuat sesudah ini." : "Tersimpan. Berlaku saat game dijalankan.", true);
  } catch (error) {
    toast(error.message);
  }
  renderStat();
}

$("stat-save").addEventListener("click", saveStat);
$("stat-discard").addEventListener("click", () => { statDraft = draftFrom(stat.rules); renderStat(); });
