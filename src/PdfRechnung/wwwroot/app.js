"use strict";

/* =========================================================
   Belegpost – Portal-Shell, Anmeldung, Routing
   Ohne externe Bibliotheken (läuft auch ohne Internet).
   ========================================================= */

const $ = (sel, root = document) => root.querySelector(sel);
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const ICONS = {
  inbox: '<path d="M3 13h5l1.5 3h5L16 13h5"/><path d="M5 5h14l2 8v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-6z"/>',
  send: '<path d="M21 3L10 14"/><path d="M21 3l-7 18-4-7-7-4z"/>',
  alert: '<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18h.01"/>',
  archive: '<rect x="3" y="4" width="18" height="5" rx="1"/><path d="M5 9v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9M10 13h4"/>',
  upload: '<path d="M12 16V4M7 9l5-5 5 5"/><path d="M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  back: '<path d="M15 6l-6 6 6 6"/>',
  doc: '<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4M9 12h6M9 16h6"/>',
  download: '<path d="M12 4v12M7 11l5 5 5-5"/><path d="M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/>',
  check: '<path d="M9 12l2 2 4-4"/><path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><path d="M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6 6 0 0 1 3.5 6"/>',
  bell: '<path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4z"/><path d="M10 20a2 2 0 0 0 4 0"/>'
};
const icon = name => `<svg viewBox="0 0 24 24">${ICONS[name] || ICONS.doc}</svg>`;
const store = {
  get(k, d) { try { const v = localStorage.getItem("pdfr." + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem("pdfr." + k, JSON.stringify(v)); } catch { /* egal */ } }
};

/** API-Aufruf mit CSRF-Header; wirft bei Fehlern eine Exception mit der Meldung des Servers. */
async function api(method, url, body, isForm) {
  const opt = { method, headers: { "X-Requested-With": "Belegpost" }, cache: "no-store" };
  if (body !== undefined) {
    if (isForm) opt.body = body;
    else { opt.body = JSON.stringify(body); opt.headers["Content-Type"] = "application/json"; }
  }
  const res = await fetch(url, opt);
  if (res.status === 401) { await loadPortal(); route(); throw new Error("Bitte anmelden."); }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) { const e = new Error(data.error || data.title || data.detail || res.statusText); e.data = data; throw e; }
  return data;
}

/* ---------------- Shell / Menü ---------------- */

let portal = { title: "Belegpost" };

const MENU = [
  { key: "vorgaenge", title: "Meine Belege", icon: "doc", filter: "meine", needsUser: true },
  { key: "vorgaenge", title: "Zu prüfen", icon: "alert", filter: "offen" },
  { key: "vorgaenge", title: "Alle Vorgänge", icon: "inbox", filter: "alle" },
  { key: "vorgaenge", title: "Versendet", icon: "send", filter: "versendet" },
  { key: "vorgaenge", title: "Verworfen", icon: "archive", filter: "verworfen" }
];

async function loadPortal() {
  try { portal = await (await fetch("api/portal", { cache: "no-store" })).json(); }
  catch { portal = { title: "Belegpost", authenticated: false }; }
  document.title = portal.title;
  $("#portalTitle").textContent = portal.title;
  if (portal.authenticated && portal.userId && typeof portal.theme === "string") applyTheme(portal.theme, false);
  $("#userInfo").innerHTML = portal.authenticated
    ? `<button class="user-btn" id="userBtn" aria-haspopup="true" aria-expanded="false" title="Persönliche Einstellungen">
         <span class="user-name">${esc(portal.user)}${portal.userId ? `<span class="menu-dot live-dot${liveOn ? " on" : ""}" title="Live-Verbindung zum Melder"></span>` : ""}</span>${portal.login ? `<span class="user-login">${esc(portal.login)}</span>` : ""}
         <span class="user-caret" aria-hidden="true">▾</span></button>
       <div class="user-pop" id="userPop" hidden>
         <div class="user-pop-h">Darstellung</div>
         ${[["", "Wie das System"], ["hell", "Hell"], ["dunkel", "Dunkel"]].map(([k, t]) => `<button class="user-pop-i" data-theme-set="${k}">${themeCurrent() === k ? "✓ " : ""}${t}</button>`).join("")}
         <div class="user-pop-sep"></div>
         <a class="user-pop-i" href="auth/logout">Abmelden</a>
       </div>
       ${portal.setupMode ? `<div class="user-foot">Einrichtungsmodus</div>` : ""}`
    : (portal.setupMode ? "Einrichtungsmodus" : "");
  const btn = $("#userBtn");
  if (btn) {
    btn.onclick = e => { e.stopPropagation(); const p = $("#userPop"); p.hidden = !p.hidden; btn.setAttribute("aria-expanded", String(!p.hidden)); };
    $("#userPop").onclick = async e => {
      const t = e.target.closest("[data-theme-set]"); if (!t) return;
      applyTheme(t.dataset.themeSet, true);
      $("#userPop").querySelectorAll("[data-theme-set]").forEach(b => b.textContent = (b.dataset.themeSet === themeCurrent() ? "✓ " : "") + b.textContent.replace(/^✓ /, ""));
      if (portal.userId) try { await api("PUT", "api/mein-design", { theme: t.dataset.themeSet }); } catch { /* lokal gespeichert */ }
    };
  }
  renderMenu();
}

/* ---------------- Darstellung hell/dunkel ---------------- */

const themeCurrent = () => { try { return localStorage.getItem("belegpost.theme") || ""; } catch { return ""; } };
function applyTheme(t, save) {
  const map = { hell: "light", dunkel: "dark" };
  if (map[t]) document.documentElement.dataset.theme = map[t]; else delete document.documentElement.dataset.theme;
  try { if (t) localStorage.setItem("belegpost.theme", t); else localStorage.removeItem("belegpost.theme"); } catch { }
}
document.addEventListener("click", e => {
  const p = document.getElementById("userPop");
  if (p && !p.hidden && !e.target.closest("#userPop") && !e.target.closest("#userBtn")) { p.hidden = true; $("#userBtn")?.setAttribute("aria-expanded", "false"); }
});

let menuSettingsOpen = false;
function renderMenu() {
  if (!portal.authenticated) { $("#menu").innerHTML = ""; return; }
  let html = `<div class="menu-group">Vorgänge</div>`;
  html += MENU.filter(m => !m.needsUser || portal.userId).map(m => `<a href="#/${m.key}?f=${m.filter}" data-key="${m.key}:${m.filter}">${icon(m.icon)}<span>${esc(m.title)}</span><span class="menu-count" data-count="${m.filter}"></span></a>`).join("");
  if (portal.seeInbound || portal.seeOutbound) html += `<div class="menu-group">Datenaustausch</div>`
    + (portal.seeInbound ? `<a href="#/eingang" data-key="eingang">${icon("download")}<span>Eingang</span><span class="menu-count" data-count="eingang"></span></a>` : "")
    + (portal.seeOutbound ? `<a href="#/ausgang" data-key="ausgang">${icon("upload")}<span>Ausgang</span></a>` : "")
    + (portal.filialverbund ? `<a href="#/filialverbund" data-key="filialverbund">${icon("users")}<span>Filialverbund</span></a>` : "");
  if (portal.gotomaxxLog) html += `<div class="menu-group">Gotomaxx</div>`
    + (portal.gotomaxxLog.length ? portal.gotomaxxLog.map(p => `<a href="#/gotomaxx?d=${encodeURIComponent(p)}" data-key="gotomaxx:${esc(p)}">${icon("send")}<span>${esc(p)}</span></a>`).join("")
      : `<a href="#/gotomaxx" data-key="gotomaxx:">${icon("send")}<span>noch keine Einträge</span></a>`);
  if (portal.connect) html += `<div class="menu-group">ERP Suite Connect</div>`
    + (portal.seeInbound ? `<a href="#/connect?b=eingang" data-key="connect:eingang">${icon("download")}<span>Eingang</span></a>` : "")
    + (portal.seeOutbound ? `<a href="#/connect?b=ausgang" data-key="connect:ausgang">${icon("upload")}<span>Ausgang</span></a>` : "");
  html += `<div class="menu-group">Tools</div><a href="#/pruefen" data-key="pruefen">${icon("check")}<span>Dokument validieren</span></a>`;
  // Einstellungen: persönliche Einstellungen, Benutzer und (für Admins) die Systemeinstellungen
  const sub = [];
  if (portal.userId) sub.push(`<a href="#/melder" data-key="melder">${icon("bell")}<span>Melder &amp; Hinweise</span><span class="menu-dot live-dot${liveOn ? " on" : ""}" title="Live-Verbindung"></span></a>`,
    `<a href="#/regeln" data-key="regeln">${icon("send")}<span>Meine Mail-Regeln</span></a>`);
  if (portal.isAdmin) {
    if (sub.length) sub.push(`<div class="menu-sub-sep"></div>`);
    sub.push(...ST_GROUPS.map(([g, title, ic]) => `<a href="#/einstellungen?g=${g}" data-key="einstellungen:${g}">${icon(ic)}<span>${esc(title)}</span></a>`));
  }
  if (sub.length) {
    // Beim Öffnen des Portals zugeklappt; nur aufgeklappt, wenn gerade eine Einstellungsseite offen ist
    const inSettings = /^#\/(einstellungen|melder|regeln)/.test(location.hash);
    const open = inSettings || menuSettingsOpen;
    html += `<div class="menu-group">System</div>
      <a href="#" class="menu-parent" data-menu-toggle="einstellungen" aria-expanded="${open}">${icon("settings")}<span>Einstellungen</span><span class="menu-caret" aria-hidden="true">▸</span></a>
      <div class="menu-sub" id="menuSubEinstellungen" ${open ? "" : "hidden"}>${sub.join("")}</div>`;
  }
  $("#menu").innerHTML = html;
  if (portal.seeInbound) api("GET", "api/eingang?anzahl=1").then(r => setInboundCount(r.unread)).catch(() => { });
}

/** Zähler im Menü (wird von der Vorgangsliste aktualisiert). */
function updateMenuCounts(counts, extra) {
  const open = (counts.fehler || 0) + (counts.warnung || 0) + (counts.versandfehler || 0) + (counts.bereit || 0);
  const mine = document.querySelector('[data-count="meine"]');
  if (mine) mine.textContent = counts.mine || "";
  const el = document.querySelector('[data-count="offen"]');
  if (el) { el.textContent = open || ""; el.classList.toggle("alert", (counts.fehler || 0) + (counts.versandfehler || 0) > 0); }
  if (extra && typeof extra.inbound === "number" && portal.seeInbound) setInboundCount(extra.inbound);
}

function toggleSidebar(open) {
  $("#sidebar").classList.toggle("open", open);
  $("#backdrop").classList.toggle("open", open);
}

const VIEWS = {
  anmeldung: { title: "Anmeldung", render: v => renderLogin(v) },
  vorgaenge: { title: "Vorgänge", render: v => renderVorgaenge(v) },
  vorgang: { title: "Vorgang", render: v => renderVorgang(v) },
  einstellungen: { title: "Einstellungen", render: v => renderSettings(v) },
  melder: { title: "Melder & Hinweise", render: v => renderMelder(v) },
  regeln: { title: "Meine Mail-Regeln", render: v => renderMyRules(v) },
  eingang: { title: "Eingang", render: v => renderEingang(v) },
  ausgang: { title: "Ausgang", render: v => renderAusgang(v) },
  filialverbund: { title: "Filialverbund", render: v => renderFilialverbund(v) },
  gotomaxx: { title: "gotomaxx", render: v => renderGotomaxx(v) },
  pruefen: { title: "Dokument validieren", render: v => renderPruefen(v) },
  rechnungsdaten: { title: "Rechnungsdaten", render: v => renderRechnungsdaten(v) },
  connect: { title: "ERP Suite Connect", render: v => renderConnect(v) }
};

function route() {
  const hash = location.hash.replace(/^#\/?/, "");
  const old = $("#view"), view = old.cloneNode(false);
  old.replaceWith(view);
  $("#topbarActions").innerHTML = "";
  view.className = "view";

  let key = hash.split("?")[0] || "vorgaenge";
  if (!portal.authenticated) {
    // Direktlink (z. B. #/vorgang?id=…) über die Anmeldung hinweg merken
    if (key !== "anmeldung" && hash) try { sessionStorage.setItem("belegpost.ziel", "#/" + hash); } catch { }
    key = "anmeldung";
  } else {
    let ziel = null;
    try { ziel = sessionStorage.getItem("belegpost.ziel"); sessionStorage.removeItem("belegpost.ziel"); } catch { }
    if (ziel && (key === "anmeldung" || !hash)) { location.replace(ziel); return; }
    if (key === "anmeldung") key = "vorgaenge";
  }
  if (key === "einstellungen" && !portal.isAdmin) key = "vorgaenge";
  if (key === "eingang" && !portal.seeInbound) key = "vorgaenge";
  if (key === "ausgang" && !portal.seeOutbound) key = "vorgaenge";
  if (key === "filialverbund" && !portal.filialverbund) key = "vorgaenge";
  if (key === "gotomaxx" && !portal.gotomaxxLog) key = "vorgaenge";
  if (key === "connect" && !portal.connect) key = "vorgaenge";
  const v = VIEWS[key] || VIEWS.vorgaenge;
  $("#viewTitle").textContent = v.title;
  v.render(view);

  const q = new URLSearchParams(hash.split("?")[1] || "");
  const active = key === "vorgaenge" ? `vorgaenge:${q.get("f") || "offen"}` : key === "connect" ? `connect:${q.get("b") || "eingang"}` : key === "gotomaxx" ? `gotomaxx:${q.get("d") || ""}` : key === "einstellungen" ? `einstellungen:${q.get("g") || "system"}` : key;
  document.querySelectorAll("#menu a").forEach(a => a.classList.toggle("active", a.dataset.key === active));
  // Untermenü „Einstellungen“ aufklappen, wenn eine Einstellungsseite offen ist
  const sub = document.getElementById("menuSubEinstellungen");
  if (sub && sub.querySelector("a.active")) { sub.hidden = false; document.querySelector("[data-menu-toggle]")?.setAttribute("aria-expanded", "true"); }
  document.querySelector("[data-menu-toggle]")?.classList.toggle("has-active", !!sub?.querySelector("a.active"));
}

async function init() {
  // Im Firmenportal eingebettet: eigene Kopfzeile/Marke ausblenden
  try { if (window.top !== window.self) document.body.classList.add("embedded"); } catch { document.body.classList.add("embedded"); }
  await loadPortal();
  startLive();
  $("#menu").addEventListener("click", e => {
    const t = e.target.closest("[data-menu-toggle]");
    if (t) {
      e.preventDefault();
      const sub = $("#menuSubEinstellungen"), open = sub.hidden;
      sub.hidden = !open; t.setAttribute("aria-expanded", String(open)); menuSettingsOpen = open;
      return;
    }
    const a = e.target.closest("a");
    if (!a) return;
    // Erneuter Klick auf die gerade offene Einstellungsseite: wieder mit dem ersten Reiter beginnen
    if (a.getAttribute("href") === location.hash && location.hash.startsWith("#/einstellungen")) { e.preventDefault(); route(); }
    toggleSidebar(false);
  });
  $("#menuToggle").onclick = () => toggleSidebar(true);
  $("#backdrop").onclick = () => toggleSidebar(false);
  window.addEventListener("hashchange", route);
  route();
}

/* ---------------- Live-Hinweise (neuer Beleg von mir gedruckt) ---------------- */

let live = null, liveOn = false;
/** Live-Punkt (Menü „Melder & Hinweise“ und Benutzername) aktualisieren. */
function setLive(on) { liveOn = on; document.querySelectorAll(".live-dot").forEach(d => d.classList.toggle("on", on)); }
function startLive() {
  if (live || !portal.authenticated || !portal.userId || !window.EventSource) return;
  live = new EventSource("api/ereignisse");
  live.onopen = () => setLive(true);
  live.onerror = () => setLive(false);
  live.addEventListener("beleg", e => {
    let ev; try { ev = JSON.parse(e.data); } catch { return; }
    if (ev.kind === "hinweis") { toast(`${ev.title}${ev.text ? " – " + ev.text : ""}`); return; }
    if (ev.kind && !["neu", "test"].includes(ev.kind)) return; // Aufträge/Einstellungen sind nur für den Melder
    const target = "#/vorgang?id=" + encodeURIComponent(ev.id);
    const auto = store.get("live.auto", true);
    if (auto && !document.hidden) { location.hash = target; toast(ev.title + " – geöffnet"); return; }
    if (store.get("live.notify", false) && window.Notification && Notification.permission === "granted" && document.hidden) {
      const n = new Notification(ev.title, { body: ev.text || "Neuer Beleg – zum Öffnen klicken", tag: ev.id });
      n.onclick = () => { window.focus(); location.hash = target; n.close(); };
    }
    if (auto && document.hidden) { location.hash = target; return; }
    liveToast(ev, target);
  });
}

function liveToast(ev, target) {
  const t = document.createElement("div");
  t.className = "toast ok live";
  t.innerHTML = `<b>${esc(ev.title)}</b>${ev.text ? `<div>${esc(ev.text)}</div>` : ""}<button class="btn small">Öffnen</button>`;
  t.querySelector("button").onclick = () => { location.hash = target; t.remove(); };
  document.body.appendChild(t);
  setTimeout(() => t.classList.add("out"), 12000);
  setTimeout(() => t.remove(), 12800);
}

/* ---------------- Anmeldung ---------------- */

async function renderLogin(view) {
  const q = new URLSearchParams(location.hash.split("?")[1] || "");
  let st = { isLocal: false, needsSetup: false, entraEnabled: false };
  try { st = await (await fetch("api/auth/status", { cache: "no-store" })).json(); } catch { /* Server nicht erreichbar */ }
  let ziel = null;
  try { ziel = sessionStorage.getItem("belegpost.ziel"); } catch { }
  const loginUrl = "auth/login?returnUrl=" + encodeURIComponent("/" + (ziel || ""));
  // Direktlink von außen (z. B. nach dem Druck): gleich zur Microsoft-Anmeldung – meist ohne Eingabe (SSO)
  const embedded = document.body.classList.contains("embedded");
  if (ziel && st.entraEnabled && !st.isLocal && !embedded && !q.get("fehler") && !q.get("abgemeldet")) { location.href = loginUrl; return; }
  const err = q.get("fehler");
  const errText = err === "nicht-freigeschaltet"
    ? `Das Konto <b>${esc(q.get("konto") || "")}</b> ist im Portal nicht freigeschaltet. Bitte an einen Administrator wenden.`
    : err ? esc(err) : "";

  let body;
  if (st.isLocal && st.needsSetup) {
    body = `<h2>Willkommen bei Belegpost</h2>
      <p>Lege zuerst ein <b>Administrator-Kennwort</b> fest. Damit meldest du dich künftig direkt am Server an
      (Einstellungen, Benutzer). Mindestens 10 Zeichen.</p>
      <form id="loginForm" class="login-form">
        <label class="field"><span class="lbl">Neues Kennwort</span><input type="password" name="pw" autocomplete="new-password" required minlength="10"></label>
        <label class="field"><span class="lbl">Kennwort wiederholen</span><input type="password" name="pw2" autocomplete="new-password" required minlength="10"></label>
        <button class="btn" type="submit">Kennwort festlegen</button>
      </form>`;
  } else {
    body = `<h2>Anmeldung</h2>
      ${q.get("abgemeldet") ? `<p class="meta">Du wurdest abgemeldet.</p>` : ""}
      ${st.entraEnabled ? `${embedded ? `<p class="meta">Die Anmeldung ist abgelaufen – bitte Belegpost im Firmenportal neu öffnen.</p>` : ""}<a class="btn ms-btn" href="${esc(loginUrl)}" ${embedded ? 'target="_top"' : ""}><span class="ms-logo" aria-hidden="true"><i></i><i></i><i></i><i></i></span>Mit Microsoft anmelden</a>`
        : `<p class="meta">Die Anmeldung mit Microsoft ist noch nicht eingerichtet${st.isLocal ? " (Einstellungen → Anmeldung)" : " – bitte an einen Administrator wenden"}.</p>`}
      ${st.isLocal ? `<div class="login-sep"><span>oder direkt am Server</span></div>
      <form id="loginForm" class="login-form">
        <label class="field"><span class="lbl">Administrator-Kennwort</span><input type="password" name="pw" autocomplete="current-password" required></label>
        <button class="btn ghost" type="submit">Als Administrator anmelden</button>
      </form>` : ""}`;
  }
  view.innerHTML = `<div class="login-wrap"><div class="login-card">
    <div class="login-brand"><span class="brand-mark"></span>${esc(portal.title || "Belegpost")}</div>
    ${errText ? `<div class="notice">${errText}</div>` : ""}${body}<div class="notice" id="loginMsg" hidden></div></div></div>`;

  const form = view.querySelector("#loginForm");
  if (form) form.onsubmit = async e => {
    e.preventDefault();
    const m = view.querySelector("#loginMsg");
    m.hidden = true;
    const pw = form.pw.value;
    try {
      if (st.needsSetup && st.isLocal) {
        if (pw !== form.pw2.value) throw new Error("Die Kennwörter stimmen nicht überein.");
        await api("POST", "api/auth/setup", { password: pw });
      } else {
        await api("POST", "api/auth/local", { password: pw });
      }
      location.hash = ziel || "#/";
      try { sessionStorage.removeItem("belegpost.ziel"); } catch { }
      await loadPortal();
      route();
    } catch (ex) { m.textContent = ex.message; m.hidden = false; }
  };
}

/** Kleiner Bestätigungsdialog. Liefert true/false. */
function confirmDialog(title, text, okLabel = "OK", danger = false) {
  return new Promise(resolve => {
    const d = document.createElement("div");
    d.className = "modal-backdrop";
    d.innerHTML = `<div class="modal" role="dialog" aria-modal="true" aria-label="${esc(title)}">
      <h3>${esc(title)}</h3><p>${text}</p>
      <div class="modal-actions"><button class="btn ghost" data-m="cancel">Abbrechen</button><button class="btn ${danger ? "danger" : ""}" data-m="ok">${esc(okLabel)}</button></div></div>`;
    document.body.appendChild(d);
    d.querySelector('[data-m="ok"]').focus();
    const close = v => { d.remove(); resolve(v); };
    d.addEventListener("click", e => {
      if (e.target === d || e.target.closest('[data-m="cancel"]')) close(false);
      else if (e.target.closest('[data-m="ok"]')) close(true);
    });
    d.addEventListener("keydown", e => { if (e.key === "Escape") close(false); });
  });
}

/** Kurze Meldung unten rechts. */
function toast(text, ok = true) {
  const t = document.createElement("div");
  t.className = "toast " + (ok ? "ok" : "bad");
  t.textContent = text;
  document.body.appendChild(t);
  setTimeout(() => t.classList.add("out"), 3500);
  setTimeout(() => t.remove(), 4200);
}

document.addEventListener("DOMContentLoaded", init);
