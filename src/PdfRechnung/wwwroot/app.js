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
  doc: '<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4M9 12h6M9 16h6"/>'
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
  html += MENU.map(m => `<a href="#/${m.key}?f=${m.filter}" data-key="${m.key}:${m.filter}">${icon(m.icon)}<span>${esc(m.title)}</span><span class="menu-count" data-count="${m.filter}"></span></a>`).join("");
  if (portal.isAdmin) html += `<div class="menu-group">System</div><a href="#/einstellungen" data-key="einstellungen">${icon("settings")}<span>Einstellungen</span></a>`;
  $("#menu").innerHTML = html;
}

/** Zähler im Menü (wird von der Vorgangsliste aktualisiert). */
function updateMenuCounts(counts) {
  const open = (counts.fehler || 0) + (counts.warnung || 0) + (counts.versandfehler || 0) + (counts.bereit || 0);
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
  einstellungen: { title: "Einstellungen", render: v => renderSettings(v) }
};

function route() {
  const hash = location.hash.replace(/^#\/?/, "");
  const old = $("#view"), view = old.cloneNode(false);
  old.replaceWith(view);
  $("#topbarActions").innerHTML = "";
  view.className = "view";

  let key = hash.split("?")[0] || "vorgaenge";
  if (!portal.authenticated) key = "anmeldung";
  else if (key === "anmeldung") key = "vorgaenge";
  if (key === "einstellungen" && !portal.isAdmin) key = "vorgaenge";
  const v = VIEWS[key] || VIEWS.vorgaenge;
  $("#viewTitle").textContent = v.title;
  v.render(view);

  const q = new URLSearchParams(hash.split("?")[1] || "");
  const active = key === "vorgaenge" ? `vorgaenge:${q.get("f") || "offen"}` : key;
  document.querySelectorAll("#menu a").forEach(a => a.classList.toggle("active", a.dataset.key === active));
}

async function init() {
  await loadPortal();
  $("#menu").addEventListener("click", e => { if (e.target.closest("a")) toggleSidebar(false); });
  $("#menuToggle").onclick = () => toggleSidebar(true);
  $("#backdrop").onclick = () => toggleSidebar(false);
  window.addEventListener("hashchange", route);
  route();
}

/* ---------------- Anmeldung ---------------- */

async function renderLogin(view) {
  const q = new URLSearchParams(location.hash.split("?")[1] || "");
  let st = { isLocal: false, needsSetup: false, entraEnabled: false };
  try { st = await (await fetch("api/auth/status", { cache: "no-store" })).json(); } catch { /* Server nicht erreichbar */ }
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
      ${st.entraEnabled ? `<a class="btn ms-btn" href="auth/login?returnUrl=%2F"><span class="ms-logo" aria-hidden="true"><i></i><i></i><i></i><i></i></span>Mit Microsoft anmelden</a>`
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
      location.hash = "#/";
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
