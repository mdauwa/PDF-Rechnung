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
  $("#userInfo").innerHTML = portal.authenticated
    ? `<div class="user-name">${esc(portal.user)}</div>${portal.login ? `<div class="user-login">${esc(portal.login)}</div>` : ""}
       <div class="user-foot">${portal.setupMode ? "Einrichtungsmodus · " : ""}<a href="auth/logout">Abmelden</a></div>`
    : (portal.setupMode ? "Einrichtungsmodus" : "");
  renderMenu();
}

function renderMenu() {
  if (!portal.authenticated) { $("#menu").innerHTML = ""; return; }
  let html = `<div class="menu-group">Vorgänge</div>`;
  html += MENU.filter(m => !m.needsUser || portal.userId).map(m => `<a href="#/${m.key}?f=${m.filter}" data-key="${m.key}:${m.filter}">${icon(m.icon)}<span>${esc(m.title)}</span><span class="menu-count" data-count="${m.filter}"></span></a>`).join("");
  if (portal.userId) html += `<div class="menu-group">Persönlich</div><a href="#/melder" data-key="melder">${icon("bell")}<span>Melder &amp; Hinweise</span><span class="menu-dot" id="liveDot" title="Live-Verbindung"></span></a>`;
  if (portal.isAdmin) html += `<div class="menu-group">System</div><a href="#/einstellungen" data-key="einstellungen">${icon("settings")}<span>Einstellungen</span></a>`;
  $("#menu").innerHTML = html;
}

/** Zähler im Menü (wird von der Vorgangsliste aktualisiert). */
function updateMenuCounts(counts, extra) {
  const open = (counts.fehler || 0) + (counts.warnung || 0) + (counts.versandfehler || 0) + (counts.bereit || 0);
  const mine = document.querySelector('[data-count="meine"]');
  if (mine) mine.textContent = counts.mine || "";
  const el = document.querySelector('[data-count="offen"]');
  if (el) { el.textContent = open || ""; el.classList.toggle("alert", (counts.fehler || 0) + (counts.versandfehler || 0) > 0); }
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
  melder: { title: "Melder & Hinweise", render: v => renderMelder(v) }
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
  const v = VIEWS[key] || VIEWS.vorgaenge;
  $("#viewTitle").textContent = v.title;
  v.render(view);

  const q = new URLSearchParams(hash.split("?")[1] || "");
  const active = key === "vorgaenge" ? `vorgaenge:${q.get("f") || "offen"}` : key;
  document.querySelectorAll("#menu a").forEach(a => a.classList.toggle("active", a.dataset.key === active));
}

async function init() {
  // Im Firmenportal eingebettet: eigene Kopfzeile/Marke ausblenden
  try { if (window.top !== window.self) document.body.classList.add("embedded"); } catch { document.body.classList.add("embedded"); }
  await loadPortal();
  startLive();
  $("#menu").addEventListener("click", e => { if (e.target.closest("a")) toggleSidebar(false); });
  $("#menuToggle").onclick = () => toggleSidebar(true);
  $("#backdrop").onclick = () => toggleSidebar(false);
  window.addEventListener("hashchange", route);
  route();
}

/* ---------------- Live-Hinweise (neuer Beleg von mir gedruckt) ---------------- */

let live = null;
function startLive() {
  if (live || !portal.authenticated || !portal.userId || !window.EventSource) return;
  live = new EventSource("api/ereignisse");
  live.onopen = () => $("#liveDot")?.classList.add("on");
  live.onerror = () => $("#liveDot")?.classList.remove("on");
  live.addEventListener("beleg", e => {
    let ev; try { ev = JSON.parse(e.data); } catch { return; }
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
