"use strict";

/* =========================================================
   Einstellungen – Oberfläche zur Konfiguration des Dienstes
   (Aufbau wie im Firmenportal)
   ========================================================= */

const ST = { data: null, s: null, tab: store.get("st.tab", "dienst"), dirty: false, service: null,
  entraSecret: "", clearEntraSecret: false, mailPassword: "", clearMailPassword: false, graphSecret: "", clearGraphSecret: false, users: null, usersDirty: false, mailTest: null };

const TABS = [
  ["dienst", "Dienst"],
  ["server", "Server & Zertifikat"],
  ["eingang", "Drucker & Eingang"],
  ["briefpapier", "Briefpapier"],
  ["mail", "E-Mail & Versand"],
  ["mailvorlagen", "Mail-Vorlagen"],
  ["erechnung", "E-Rechnung"],
  ["ablage", "Ablage"],
  ["benutzer", "Benutzer"],
  ["anmeldung", "Anmeldung"]
];

const get = (obj, path) => path.split(".").reduce((o, k) => o?.[k], obj);
function set(obj, path, val) {
  const keys = path.split("."), last = keys.pop();
  keys.reduce((o, k) => (o[k] ??= {}), obj)[last] = val;
}

/* ---------------- Rahmen ---------------- */

async function renderSettings(view) {
  view.innerHTML = `<div class="settings">
    <div class="notice info" id="stSetup" hidden></div>
    <div class="notice" id="stMsg" hidden></div>
    <nav class="tabs" id="stTabs">${TABS.map(([k, t]) => `<button data-tab="${k}">${t}</button>`).join("")}</nav>
    <div id="stBody" class="st-body"><p class="meta">Lade Einstellungen …</p></div>
    <div class="savebar" id="stSave" hidden>
      <span id="stSaveText">Ungespeicherte Änderungen</span>
      <button class="btn ghost" data-st="discard">Verwerfen</button>
      <button class="btn" data-st="save">Speichern</button>
    </div>
  </div>`;

  view.querySelector("#stTabs").onclick = e => {
    const t = e.target.closest("[data-tab]")?.dataset.tab;
    if (t) { ST.tab = t; store.set("st.tab", t); drawTab(); }
  };
  view.addEventListener("click", onAction);
  view.addEventListener("input", onInput);
  view.addEventListener("change", onInput);

  try { await loadSettings(); }
  catch (e) { msg("Einstellungen konnten nicht geladen werden: " + e.message); $("#stBody").innerHTML = ""; }
}

async function loadSettings() {
  ST.data = await api("GET", "api/admin/settings");
  ST.s = structuredClone(ST.data.settings);
  ST.dirty = false; ST.entraSecret = ""; ST.clearEntraSecret = false; ST.mailPassword = ""; ST.clearMailPassword = false; ST.graphSecret = ""; ST.clearGraphSecret = false;
  const setup = $("#stSetup");
  setup.hidden = !ST.data.setupMode;
  setup.innerHTML = "<b>Einrichtungsmodus.</b> Das Programm wurde per Doppelklick gestartet und ist nur an diesem Rechner erreichbar. " +
    "Nach dem Einrichten unter <span class=\"linkish\" data-goto=\"dienst\">Dienst</span> als Windows-Dienst installieren.";
  if (!TABS.some(([k]) => k === ST.tab)) ST.tab = "dienst";
  drawTab();
  updateSaveBar();
}

function drawTab() {
  document.querySelectorAll("#stTabs button").forEach(b => b.classList.toggle("active", b.dataset.tab === ST.tab));
  const body = $("#stBody");
  if (!ST.s) return;
  const fn = { dienst: tabService, server: tabServer, eingang: tabInbox, briefpapier: tabStationery, mail: tabMail, mailvorlagen: tabTemplates, erechnung: tabInvoice, ablage: tabStorage, benutzer: tabUsers, anmeldung: tabAuth }[ST.tab] || tabService;
  body.innerHTML = fn();
  if (ST.tab === "benutzer" && !ST.users) loadUsers();
  if (ST.tab === "dienst") loadService();
}

function msg(text, ok) {
  const n = $("#stMsg");
  if (!text) { n.hidden = true; return; }
  n.textContent = text;
  n.className = "notice" + (ok ? " ok" : "");
  n.hidden = false;
  n.scrollIntoView({ block: "nearest" });
}

function markDirty() { ST.dirty = true; updateSaveBar(); }
function updateSaveBar() { $("#stSave").hidden = !ST.dirty; }

/* ---------------- Formular-Bausteine ---------------- */

const field = (label, inner, help) => `<label class="field"><span class="lbl">${label}</span>${inner}${help ? `<small class="help">${help}</small>` : ""}</label>`;
const inText = (path, ph = "", type = "text") => `<input type="${type}" data-path="${path}" value="${esc(get(ST.s, path) ?? "")}" placeholder="${esc(ph)}">`;
const inNum = (path, min = 0, max = 65535, step = 1) => `<input type="number" data-path="${path}" data-type="${step === 1 ? "int" : "dec"}" min="${min}" max="${max}" step="${step}" value="${esc(get(ST.s, path) ?? "")}">`;
const inCheck = (path, label) => `<label class="check"><input type="checkbox" data-path="${path}" ${get(ST.s, path) ? "checked" : ""}> ${label}</label>`;
const inLines = (path, ph) => `<textarea data-path="${path}" data-type="lines" rows="3" placeholder="${esc(ph)}">${esc((get(ST.s, path) || []).join("\n"))}</textarea>`;
const inArea = (path, rows = 5) => `<textarea data-path="${path}" rows="${rows}">${esc(get(ST.s, path) ?? "")}</textarea>`;
const inSelect = (path, options) => `<select data-path="${path}">${options.map(([v, t]) => `<option value="${esc(v)}" ${get(ST.s, path) === v ? "selected" : ""}>${esc(t)}</option>`).join("")}</select>`;
const section = (title, inner, extra = "") => `<section class="st-card"><div class="st-card-head"><h3>${title}</h3>${extra}</div><div class="st-card-body">${inner}</div></section>`;

function onInput(e) {
  const el = e.target;
  if (ST.tab === "benutzer" && onUsersInput(el, e)) return;
  if (el.dataset.entrasecret !== undefined) { ST.entraSecret = el.value; markDirty(); return; }
  if (el.dataset.mailpw !== undefined) { ST.mailPassword = el.value; markDirty(); return; }
  if (el.dataset.graphsecret !== undefined) { ST.graphSecret = el.value; markDirty(); return; }
  if (el.dataset.bpdefault !== undefined) { ST.s.stationery.default = el.checked ? el.value : ""; markDirty(); return; }
  if (el.dataset.bpup !== undefined && e.type === "change") { uploadStationery(el); return; }
  if (el.dataset.bindall !== undefined) {
    ST.s.server.bindAddresses = el.checked ? [] : (ST.data.localAddresses || []).slice(0, 1);
    markDirty(); drawTab(); return;
  }
  if (el.dataset.bind !== undefined) {
    const list = (ST.s.server.bindAddresses || []).filter(x => x !== el.value);
    if (el.checked) list.push(el.value);
    if (list.length === 0) { el.checked = true; msg("Mindestens eine Adresse wählen – oder „alle IP-Adressen“."); return; }
    ST.s.server.bindAddresses = list; markDirty(); return;
  }
  if (el.dataset.mtbp !== undefined) {
    const t = ST.s.mailTemplates.items[+el.dataset.mtbp];
    t.stationeries = (t.stationeries || []).filter(x => x.toLowerCase() !== el.value.toLowerCase());
    if (el.checked) t.stationeries.push(el.value);
    markDirty(); return;
  }
  if (el.dataset.mtdefault !== undefined) { ST.s.mailTemplates.default = el.checked ? el.value : ""; markDirty(); return; }
  if (el.dataset.mtlogo !== undefined && e.type === "change") { uploadLogo(el); return; }
  if (el.dataset.colortext !== undefined) {
    // Farbwähler und Textfeld synchron halten
    const other = el.parentElement.querySelector(el.type === "color" ? "input[type=text]" : "input[type=color]");
    if (/^#[0-9a-fA-F]{6}$/.test(el.value) && other) other.value = el.value;
  }
  if (el.dataset.unit !== undefined) {
    if (e.type !== "change") return;
    const units = ST.s.invoice.units, old = el.dataset.unit, row = el.closest(".unit");
    const key = row.querySelector(".key").value.trim(), code = row.querySelector(".code").value.trim().toUpperCase();
    if (old && old !== key) delete units[old];
    if (key) units[key] = code;
    markDirty(); drawTab(); return;
  }
  if (el.dataset.path) {
    // Standard-Briefpapier mitziehen, wenn sein Name geändert wird
    const bp = el.dataset.path.match(/^stationery\.items\.(\d+)\.name$/);
    if (bp && ST.s.stationery.default === ST.s.stationery.items[+bp[1]].name) ST.s.stationery.default = el.value;
    const mt = el.dataset.path.match(/^mailTemplates\.items\.(\d+)\.name$/);
    if (mt && ST.s.mailTemplates.default === ST.s.mailTemplates.items[+mt[1]].name) ST.s.mailTemplates.default = el.value;
    let v = el.type === "checkbox" ? el.checked : el.value;
    if (el.dataset.type === "int") v = v === "" ? 0 : parseInt(v, 10);
    if (el.dataset.type === "dec") v = v === "" ? 0 : parseFloat(String(v).replace(",", "."));
    if (el.dataset.type === "lines") v = v.split(/[\n,;]/).map(x => x.trim()).filter(Boolean);
    set(ST.s, el.dataset.path, v);
    markDirty();
    if (el.dataset.redraw === "1" && e.type === "change") drawTab();
  }
}

async function onAction(e) {
  const goto = e.target.closest("[data-goto]")?.dataset.goto;
  if (goto) { ST.tab = goto; drawTab(); return; }
  const a = e.target.closest("[data-st]")?.dataset.st;
  if (!a) return;
  const btn = e.target.closest("button") || e.target.closest("input");
  try {
    if (btn && btn.tagName === "BUTTON") btn.disabled = true;
    switch (a) {
      case "save": await save(); break;
      case "discard": await loadSettings(); msg(""); break;
      case "restart": await restart(); break;
      case "install": await serviceAction("install"); break;
      case "uninstall":
        if (await confirmDialog("Dienst entfernen", "Den Windows-Dienst wirklich entfernen? Einstellungen und Vorgänge bleiben erhalten.", "Entfernen", true)) await serviceAction("uninstall");
        break;
      case "cert-upload": await uploadCert(); break;
      case "fp-key": {
        if (ST.data.hasFirmenportalSecret && !await confirmDialog("Neuen Schlüssel erzeugen",
          "Der bisherige Schlüssel gilt danach nicht mehr – das Firmenportal braucht dann den neuen.", "Neu erzeugen", true)) break;
        const r = await api("POST", "api/admin/firmenportal/schluessel");
        ST.data.hasFirmenportalSecret = true;
        ST.fpKey = r.key; drawTab(); break;
      }
      case "cert-self":
        if (await confirmDialog("Zertifikat", "Eigenes Zertifikat entfernen und wieder das selbst signierte verwenden?", "Zurücksetzen")) {
          await api("POST", "api/admin/certificate/selfsigned"); restartHint("Zertifikat zurückgesetzt."); await loadSettings();
        }
        break;
      case "entra-clear": ST.clearEntraSecret = !ST.clearEntraSecret; markDirty(); drawTab(); break;
      case "mail-clear": ST.clearMailPassword = !ST.clearMailPassword; markDirty(); drawTab(); break;
      case "graph-clear": ST.clearGraphSecret = !ST.clearGraphSecret; markDirty(); drawTab(); break;
      case "graph-from-login":
        ST.s.mail.graphTenantId = ST.s.auth.entra?.tenantId || ""; ST.s.mail.graphClientId = ST.s.auth.entra?.clientId || "";
        markDirty(); drawTab(); break;
      case "mail-test": {
        const to = $("#mailTestTo").value.trim();
        ST.mailTest = { loading: true, to }; drawTab();
        try { ST.mailTest = { ...(await api("POST", "api/admin/mail-test", { mail: ST.s.mail, password: ST.mailPassword || null, graphSecret: ST.graphSecret || null, to })), to }; }
        catch (err) { ST.mailTest = { ok: false, message: err.message, to }; }
        drawTab();
        break;
      }
      case "bp-add": {
        const items = (ST.s.stationery ||= { items: [], default: "" }).items;
        items.push({ name: items.length ? "" : "Standard", firstPageFile: "", followingMode: "same", followingPageFile: "",
          marginTop: 0, marginBottom: 0, marginLeft: 0, marginRight: 0, followingOwnMargins: false, followingMarginTop: 0, followingMarginBottom: 0,
          fitContent: false, offsetX: 0, offsetY: 0, scale: 100 });
        if (!ST.s.stationery.default && items.length === 1) ST.s.stationery.default = "Standard";
        markDirty(); drawTab(); break;
      }
      case "bp-del": {
        const items = ST.s.stationery.items, it = items[+btn.dataset.i];
        if (await confirmDialog("Briefpapier entfernen", `„${esc(it.name || "ohne Namen")}“ entfernen? Belege mit @@bp${esc(it.name)}@@ werden danach als Fehler gemeldet.`, "Entfernen", true)) {
          if (ST.s.stationery.default === it.name) ST.s.stationery.default = "";
          items.splice(+btn.dataset.i, 1); markDirty(); drawTab();
        }
        break;
      }
      case "bp-preview": await previewStationery(+btn.dataset.i); break;
      case "mt-add": {
        const T = (ST.s.mailTemplates ||= { items: [], default: "" });
        T.items.push({ name: T.items.length ? "" : "Standard", kind: "simple", stationeries: [], senders: [], fromAddress: "", fromName: "", replyTo: "",
          subject: "", body: "", logoFile: "", logoWidth: 180, logoAlign: "left", primaryColor: "#1f5fbf", backgroundColor: "#f4f5f7", textColor: "#1c2330",
          fontFamily: "Segoe UI, Arial, Helvetica, sans-serif", headerText: "", showDocumentBox: true,
          signature: "Mit freundlichen Grüßen\n{Absender}", footer: "", customHtml: "" });
        if (!T.default && T.items.length === 1) T.default = "Standard";
        markDirty(); drawTab(); break;
      }
      case "mt-copy": {
        const T = ST.s.mailTemplates, src = T.items[+btn.dataset.i];
        T.items.push({ ...structuredClone(src), name: (src.name || "Vorlage") + " (Kopie)", stationeries: [], senders: [] });
        markDirty(); drawTab(); break;
      }
      case "mt-del": {
        const T = ST.s.mailTemplates, it = T.items[+btn.dataset.i];
        if (await confirmDialog("Mail-Vorlage entfernen", `„${esc(it.name || "ohne Namen")}“ entfernen?`, "Entfernen", true)) {
          if (T.default === it.name) T.default = "";
          T.items.splice(+btn.dataset.i, 1); markDirty(); drawTab();
        }
        break;
      }
      case "mt-logo-del": ST.s.mailTemplates.items[+btn.dataset.i].logoFile = ""; markDirty(); drawTab(); break;
      case "mt-preview": await previewTemplate(+btn.dataset.i); break;
      case "mt-html": {
        const t = ST.s.mailTemplates.items[+btn.dataset.i];
        t.customHtml = t.customHtml || DEFAULT_HTML; markDirty(); drawTab(); break;
      }
      case "unit-add": ST.s.invoice.units[""] = ""; drawTab(); document.querySelector('.unit .key[value=""]')?.focus(); break;
      case "unit-del": delete ST.s.invoice.units[btn.dataset.key]; markDirty(); drawTab(); break;
      case "user-add":
        ST.users.users.push({ id: "", name: "", email: "", logins: [], printNames: [], operatorNumbers: [], visibility: "eigene", alsoSees: [], canSend: true, isAdmin: false, active: true });
        ST.usersDirty = true; drawTab(); break;
      case "user-del": {
        const u = ST.users.users[+btn.dataset.i];
        if (await confirmDialog("Benutzer entfernen", `„${esc(u.name || u.email || "neuer Benutzer")}“ entfernen?`, "Entfernen", true)) {
          ST.users.users.splice(+btn.dataset.i, 1); ST.usersDirty = true; drawTab();
        }
        break;
      }
      case "users-save":
        await api("PUT", "api/admin/users", { users: ST.users.users });
        await loadUsers();
        msg("Benutzer gespeichert – Änderungen gelten sofort.", true);
        break;
      case "local-pw": {
        const cur = $("#lpCur").value, n1 = $("#lpNew").value, n2 = $("#lpNew2").value;
        if (n1 !== n2) throw new Error("Die neuen Kennwörter stimmen nicht überein.");
        await api("POST", "api/admin/local-password", { current: cur, password: n1 });
        $("#lpCur").value = $("#lpNew").value = $("#lpNew2").value = "";
        msg("Administrator-Kennwort geändert.", true);
        break;
      }
    }
  } catch (err) {
    msg(err.message);
  } finally {
    if (btn && btn.tagName === "BUTTON" && document.contains(btn)) btn.disabled = false;
  }
}

async function save() {
  const r = await api("PUT", "api/admin/settings", { settings: ST.s,
    entraSecret: ST.entraSecret || null, clearEntraSecret: ST.clearEntraSecret,
    mailPassword: ST.mailPassword || null, clearMailPassword: ST.clearMailPassword,
    graphSecret: ST.graphSecret || null, clearGraphSecret: ST.clearGraphSecret });
  await loadSettings();
  if (r.restartRequired) restartHint("Gespeichert.");
  else msg("Gespeichert – die Änderungen sind sofort aktiv.", true);
  await loadPortal();
}

function restartHint(prefix) {
  const n = $("#stMsg");
  n.className = "notice warn";
  n.innerHTML = `${esc(prefix)} Server-, Zertifikats- und Anmeldeeinstellungen werden erst nach einem Neustart aktiv. <button class="btn" data-st="restart">Jetzt neu starten</button>`;
  n.hidden = false;
}

async function restart() {
  const port = ST.s.server.httpsPort;
  await api("POST", "api/admin/service/restart");
  msg("Neustart läuft … die Seite lädt automatisch neu.", true);
  const target = `https://${location.hostname}:${port}/`;
  await new Promise(r => setTimeout(r, 2500));
  for (let i = 0; i < 40; i++) {
    try {
      if (location.port == port) {
        const res = await fetch("api/health", { cache: "no-store" });
        if (res.ok) { location.reload(); return; }
      } else { location.href = target + "#/einstellungen"; return; }
    } catch { /* noch nicht erreichbar */ }
    await new Promise(r => setTimeout(r, 1500));
  }
  msg("Der Server antwortet noch nicht. Bitte die Seite in einigen Sekunden neu laden.");
}

/* ---------------- Tab: Dienst ---------------- */

function tabService() {
  return section("Windows-Dienst", `<div id="svcBox"><p class="meta">Lade Status …</p></div>`) +
    section("Speicherort", `<p class="meta">Einstellungen, Schlüssel und Zertifikate liegen in <code>${esc(ST.data.dataDirectory)}</code>,
      die Vorgänge in <code>${esc(ST.data.documentsFolder)}</code>. Passwörter werden verschlüsselt gespeichert.
      Bei einem Update der Programmdateien bleiben diese Ordner unverändert.</p>`);
}

async function loadService() {
  try { ST.service = await api("GET", "api/admin/service"); }
  catch (e) { $("#svcBox").innerHTML = `<p class="meta">${esc(e.message)}</p>`; return; }
  const { status: s, setupMode, installLog, listening, diagnostics } = ST.service;
  const row = (k, v) => `<tr><th>${k}</th><td>${v}</td></tr>`;
  const state = s.installed ? (s.state === "Running" ? '<span class="badge ok">läuft</span>' : `<span class="badge partial">${esc(s.state || "installiert")}</span>`) : '<span class="badge missing">nicht installiert</span>';
  let actions = "";
  if (!s.isWindows) actions = `<p class="meta">Die Dienst-Installation ist nur unter Windows verfügbar.</p>`;
  else if (setupMode) {
    actions = `<button class="btn" data-st="install">${s.installed ? "Dienst aktualisieren &amp; starten" : "Als Windows-Dienst installieren &amp; starten"}</button>
      ${s.installed ? '<button class="btn ghost" data-st="uninstall">Dienst entfernen</button>' : ""}
      <p class="help">Dafür sind Administratorrechte nötig – Windows fragt ggf. nach (UAC). Diese Einrichtung beendet sich danach,
      der Dienst übernimmt und der Browser öffnet sich automatisch neu.</p>`;
  } else {
    actions = `<button class="btn" data-st="restart">Dienst neu starten</button>
      <button class="btn ghost" data-st="uninstall">Dienst entfernen</button>`;
  }
  const box = $("#svcBox"); if (!box) return;
  box.innerHTML = `<table class="kv">
      ${row("Betriebsart", setupMode ? "Einrichtungsmodus (per Doppelklick gestartet, nur localhost)" : "Windows-Dienst")}
      ${row("Dienst", state)}
      ${row("Rechner", esc(s.machineName))}
      ${row("Programm", `<code>${esc(s.executable)}</code>`)}
      ${row("Version", esc(s.version))}
      ${setupMode && s.isWindows ? row("Administratorrechte", s.elevated ? "ja" : "nein – Abfrage bei Installation") : ""}
      ${row("Lauscht auf", (listening || []).map(a => `<code>${esc(a)}</code>`).join("<br>") || "–")}
    </table>
    ${setupMode && (listening || []).every(a => /localhost|127\.0\.0\.1|\[::1\]/.test(a)) ? `<div class="notice">Im Einrichtungsmodus ist das Portal ohne eingestellte IP-Adressen <b>nur über localhost</b> erreichbar.
      Entweder unter „Server &amp; Zertifikat“ die IP-Adressen wählen und die Einrichtung neu starten – oder es als Windows-Dienst betreiben
      ${s.installed && s.state === "Running" ? "– der Dienst läuft bereits; bitte diese Einrichtung schließen und das Portal über den Dienst öffnen." : "– unten „Als Windows-Dienst installieren &amp; starten“."}</div>` : ""}
    ${diagnostics ? `<details class="log"><summary>Netzwerk-Diagnose (Firewall, Netzwerkprofil)</summary>
      <p class="help">Die Firewall-Regel gilt standardmäßig nur für die Profile <b>Domäne</b> und <b>Privat</b>. Steht die Netzwerkkarte auf
        <b>Public/Öffentlich</b>, blockt Windows den Zugriff – dann unter „Server &amp; Zertifikat“ die Firewall auch für öffentliche Netze freigeben
        oder das Netzwerkprofil auf Privat/Domäne stellen.</p>
      <pre>${esc(diagnostics.networkProfiles || "")}</pre><pre>${esc(diagnostics.firewallRule || "")}</pre></details>` : ""}
    <div class="actions">${actions}</div>
    ${installLog ? `<details class="log"><summary>Protokoll der letzten Installation</summary><pre>${esc(installLog)}</pre></details>` : ""}`;
}

async function serviceAction(kind) {
  const r = await api("POST", `api/admin/service/${kind}`);
  msg(r.message, true);
  if (kind === "install") {
    $("#stBody").innerHTML = section("Installation läuft", `<p>Der Dienst wird eingerichtet und gestartet. Diese Seite wartet auf den Dienst …</p>`);
    const port = ST.s.server.httpsPort;
    for (let i = 0; i < 60; i++) {
      await new Promise(r => setTimeout(r, 2000));
      try { const res = await fetch(`https://${location.hostname}:${port}/api/health`, { cache: "no-store" }); if (res.ok) { location.reload(); return; } } catch { }
    }
    msg("Der Dienst antwortet noch nicht. Falls die Installation fehlgeschlagen ist, öffnet sich die Einrichtung erneut mit dem Protokoll.");
  } else setTimeout(loadService, 2500);
}

/* ---------------- Tab: Server & Zertifikat ---------------- */

function tabServer() {
  const c = ST.data.certificate;
  const custom = !!ST.s.server.certificatePath;
  const d = s => s ? new Date(s).toLocaleDateString("de-AT") : "–";
  const certInfo = c ? `<table class="kv">
      <tr><th>Quelle</th><td>${esc(c.source)}</td></tr>
      <tr><th>Ausgestellt für</th><td>${esc(c.subject)}</td></tr>
      <tr><th>Aussteller</th><td>${esc(c.issuer)}${c.selfSigned ? ' <span class="badge partial">selbst signiert</span>' : ""}</td></tr>
      <tr><th>Gültig</th><td>${d(c.notBefore)} – ${d(c.notAfter)}</td></tr>
      <tr><th>Namen</th><td>${(c.dnsNames || []).map(esc).join(", ")}</td></tr>
      <tr><th>Fingerabdruck</th><td><code>${esc(c.thumbprint)}</code></td></tr>
    </table>
    ${c.warning ? `<div class="notice">${esc(c.warning)}</div>` : ""}
    <div class="actions"><a class="btn ghost" href="api/admin/certificate/download">Zertifikat herunterladen (.cer)</a>
      ${custom ? '<button class="btn ghost" data-st="cert-self">Wieder selbst signiertes verwenden</button>' : ""}</div>
    ${c.selfSigned ? `<p class="help">Damit Browser im LAN keine Warnung zeigen, die .cer-Datei per Gruppenrichtlinie unter
      „Vertrauenswürdige Stammzertifizierungsstellen“ verteilen – oder ein Zertifikat der eigenen CA als PFX hinterlegen.</p>` : ""}` : "";

  return section("Webserver",
    `<div class="grid2">
      ${field("HTTPS-Port", inNum("server.httpsPort", 1))}
      ${field("HTTP-Port", inNum("server.httpPort", 1))}
    </div>
    ${inCheck("server.enableHttp", "Zusätzlich HTTP anbieten")}
    ${inCheck("server.redirectHttpToHttps", "HTTP automatisch auf HTTPS umleiten")}
    ${bindField()}
    ${inCheck("server.firewallPublic", "Firewall auch für öffentliche Netzwerke öffnen (nötig, wenn die Netzwerkkarte des Servers als „Öffentlich“ eingestuft ist)")}
    ${field("Titel des Portals", inText("portal.title", "Belegpost"))}
    <p class="help">Die Firewall-Regel (Domänen- und privates Netz) wird beim Dienststart automatisch an die Ports angepasst.</p>`) +
    section("Aktuelles Zertifikat", certInfo || '<p class="meta">Kein Zertifikat geladen.</p>') +
    section("Eigenes Zertifikat (PFX) hinterlegen",
      `<div class="grid2">
        ${field("PFX-Datei hochladen", `<input type="file" id="certFile" accept=".pfx,.p12">`)}
        ${field("… oder Pfad am Server", `<input type="text" id="certPath" placeholder="C:\\Zertifikate\\portal.pfx">`)}
        ${field("Passwort der PFX-Datei", `<input type="password" id="certPw" autocomplete="new-password">`)}
      </div>
      <div class="actions"><button class="btn" data-st="cert-upload">Prüfen &amp; übernehmen</button></div>
      <p class="help">Ohne eigenes Zertifikat erzeugt der Dienst automatisch ein selbst signiertes (5 Jahre gültig) für:
      ${(ST.data.hostNames || []).map(esc).join(", ")} und die IP-Adressen des Servers.</p>`) +
    section("Selbst signiertes Zertifikat",
      field("Weitere DNS-Namen", inLines("server.additionalHostNames", "rechnung.firma.local"),
        "Ein Name pro Zeile. Das Zertifikat wird beim Neustart passend erneuert."));
}

function bindField() {
  const sel = ST.s.server.bindAddresses ||= [];
  const known = ST.data.localAddresses || [];
  const extra = sel.filter(x => !known.includes(x));
  const all = sel.length === 0;
  return `<div class="plain-field"><span class="lbl">Gebunden an (IP-Adressen)</span>
      <label class="check"><input type="checkbox" data-bindall ${all ? "checked" : ""}> alle IP-Adressen des Servers</label>
      ${all ? "" : `<div class="check-list">${known.map(ip => `<label class="check"><input type="checkbox" data-bind value="${esc(ip)}" ${sel.includes(ip) ? "checked" : ""}> <code>${esc(ip)}</code></label>`).join("")}
        ${extra.map(ip => `<label class="check"><input type="checkbox" data-bind value="${esc(ip)}" checked> <code>${esc(ip)}</code> <span class="badge missing">nicht vorhanden</span></label>`).join("")}</div>`}
      <p class="help">Nur an die gewählten Adressen binden, z. B. nur das interne Netz. <code>127.0.0.1</code> (localhost) ist immer dabei –
        für die Einrichtung und die Administrator-Anmeldung am Server. Gilt für HTTPS und HTTP und erst nach einem Neustart (Dienst neu starten bzw. Einrichtung schließen und neu öffnen).
        Bei „alle IP-Adressen“ ist die Einrichtung per Doppelklick nur über localhost erreichbar, erst der Dienst lauscht dann auf allen Adressen.
        Fehlt eine Adresse später (andere Netzwerkkarte), startet der Dienst trotzdem und meldet es im Protokoll.</p></div>`;
}

async function uploadCert() {
  const f = $("#certFile").files[0], path = $("#certPath").value.trim(), pw = $("#certPw").value;
  if (!f && !path) throw new Error("Bitte eine PFX-Datei auswählen oder einen Pfad angeben.");
  const fd = new FormData();
  if (f) fd.append("file", f);
  fd.append("path", path);
  fd.append("password", pw);
  const r = await api("POST", "api/admin/certificate", fd, true);
  await loadSettings();
  restartHint(`Zertifikat „${r.info.subject}“ (gültig bis ${new Date(r.info.notAfter).toLocaleDateString("de-AT")}) übernommen.`);
}

/* ---------------- Tab: Drucker & Eingang ---------------- */

function tabInbox() {
  return section("Eingang",
    `${inCheck("inbox.enabled", "Eingangsordner überwachen")}
     ${field("Eingangsordner", inText("inbox.folder", ST.data.inboxFolder), `Leer = <code>${esc(ST.data.inboxFolder)}</code>. Jede PDF-Datei, die hier abgelegt wird, wird zu einem Vorgang verarbeitet und danach aus dem Ordner entfernt. Nicht lesbare Dateien landen im Unterordner <code>fehler</code>.`)}`) +
    section("Pfadzuordnung",
      field("Pfade aus den Steuerzeichen umsetzen", inLines("inbox.pathMappings", "K:\\=\\\\server\\WebWARE5\\"),
        "Das ERP druckt Pfade für Mailtext (@@mb@@) und Anlagen (@@ma@@) so, wie sie am Arbeitsplatz gelten, z. B. <code>K:\\WebWARE5\\…</code>. " +
        "Der Dienst läuft als SYSTEM und sieht keine Netzlaufwerke. Eine Zuordnung pro Zeile: <code>K:\\=\\\\server\\freigabe\\</code>. " +
        "Das Computerkonto des Servers braucht Leserechte auf die Freigabe.")) +
    section("Drucker",
      `${field("Name des Druckers", inText("inbox.printerName", "Belegpost"))}
       <div class="help-box">
         <b>So kommt der Druck in den Eingang</b> (wird im nächsten Schritt hier automatisiert):
         <ol>
           <li>Am Server einen Drucker mit dem Treiber <i>Microsoft Print to PDF</i> anlegen und unter dem Namen oben freigeben.</li>
           <li>Statt des Standardanschlusses <code>PORTPROMPT:</code> (der den Speichern-Dialog öffnet) bekommt der Drucker einen eigenen Anschluss, der jeden Druckauftrag als eigene PDF-Datei in den Eingangsordner schreibt – ohne Nachfrage.</li>
           <li>Im ERP auf diesen Drucker drucken. Die gotomaxx-Steuerzeichen (<code>@@…@@</code>) im Formular bleiben unverändert.</li>
         </ol>
         Zum Testen: PDF-Dateien in den Eingangsordner kopieren oder in der Vorgangsliste über „PDF hochladen“ einspielen.
       </div>`);
}

/* ---------------- Tab: Briefpapier ---------------- */

function tabStationery() {
  const S = ST.s.stationery ||= { items: [], default: "" };
  const fileLink = f => f ? `<a href="api/admin/stationery/file/${encodeURIComponent(f)}" target="_blank" rel="noopener">${esc(f)}</a>` : '<span class="muted">noch keine Datei</span>';
  const mm = (path, label) => field(label, inNum(path, 0, 150, 0.5));
  const items = S.items.map((b, i) => {
    const P = `stationery.items.${i}`;
    return section(`Briefpapier ${esc(b.name || "(ohne Namen)")}`,
      `<div class="grid2">
         ${field("Name", inText(`${P}.name`, "z. B. Firma GmbH"), `Steuerzeichen: <code>@@bp${esc(b.name || "Name")}@@</code> – Groß-/Kleinschreibung wird beachtet.`)}
         <div class="plain-field"><span class="lbl">Verwendung</span>
           <label class="check"><input type="checkbox" data-bpdefault value="${esc(b.name)}" ${S.default && S.default === b.name ? "checked" : ""}> Standard-Briefpapier (ohne @@bp@@ im Beleg)</label></div>
       </div>
       <div class="grid2">
         <div class="field"><span class="lbl">Erste Seite (PDF)</span><div class="bp-file">${fileLink(b.firstPageFile)}
           <label class="btn ghost small">PDF hochladen<input type="file" accept="application/pdf,.pdf" data-bpup="${i}|firstPageFile" hidden></label></div></div>
         <div class="field"><span class="lbl">Folgeseiten</span>
           <select data-path="${P}.followingMode" data-redraw="1">
             <option value="same" ${b.followingMode === "same" ? "selected" : ""}>wie erste Seite</option>
             <option value="file" ${b.followingMode === "file" ? "selected" : ""}>eigenes PDF</option>
             <option value="none" ${b.followingMode === "none" ? "selected" : ""}>kein Briefpapier</option>
           </select>
           ${b.followingMode === "file" ? `<div class="bp-file">${fileLink(b.followingPageFile)}
             <label class="btn ghost small">PDF hochladen<input type="file" accept="application/pdf,.pdf" data-bpup="${i}|followingPageFile" hidden></label></div>` : ""}</div>
       </div>
       <h4>Druckbereich – Ränder in mm</h4>
       <p class="help">Bereiche des Briefpapiers (Logo/Kopf, Fußzeile, seitliche Leiste), in die der gedruckte Beleg nicht ragen soll.
         Ragt er hinein, meldet das Portal eine Warnung – oder der Inhalt wird automatisch eingepasst.</p>
       <div class="grid4">${mm(`${P}.marginTop`, "oben")}${mm(`${P}.marginBottom`, "unten")}${mm(`${P}.marginLeft`, "links")}${mm(`${P}.marginRight`, "rechts")}</div>
       ${inCheck(`${P}.followingOwnMargins`, "Folgeseiten mit eigenen Rändern oben/unten (z. B. kleinerer Kopf)")}
       ${b.followingOwnMargins ? `<div class="grid4">${mm(`${P}.followingMarginTop`, "Folgeseiten oben")}${mm(`${P}.followingMarginBottom`, "Folgeseiten unten")}</div>` : ""}
       ${inCheck(`${P}.fitContent`, "Inhalt in den Druckbereich einpassen (verkleinern und verschieben, damit Kopf und Fuß frei bleiben)")}
       <details><summary>Feinjustierung des Briefpapiers</summary>
         <div class="grid3">${field("Versatz waagrecht (mm, + = rechts)", inNum(`${P}.offsetX`, -50, 50, 0.5))}
           ${field("Versatz senkrecht (mm, + = oben)", inNum(`${P}.offsetY`, -50, 50, 0.5))}
           ${field("Skalierung (%)", inNum(`${P}.scale`, 10, 200, 1))}</div>
       </details>
       <div class="actions"><button class="btn ghost" data-st="bp-preview" data-i="${i}">Vorschau</button>
         <span class="meta-inline">zeigt erste Seite und Folgeseite mit dem Druckbereich (rot) – auch vor dem Speichern</span></div>
       <div id="bpPreview${i}"></div>`,
      `<button class="icon-btn" data-st="bp-del" data-i="${i}" title="Briefpapier entfernen">✕</button>`);
  }).join("");
  return section("Briefpapiere",
    `<p class="help">Das Briefpapier wird beim Erzeugen des Versand-PDFs unter den gedruckten Beleg gelegt. Mehrere Briefpapiere, z. B. je Firma:
      Im Formular wählt das Steuerzeichen <code>@@bpName@@</code> das passende aus; ohne Steuerzeichen gilt das Standard-Briefpapier,
      <code>@@bp@@</code> ohne Namen bedeutet „kein Briefpapier“. Ist ein angefordertes Briefpapier nicht angelegt, wird der Vorgang als Fehler gemeldet und nicht automatisch versendet.</p>
     <p class="help">Das PDF sollte A4 sein (eine Seite für die erste Seite; für Folgeseiten optional ein zweites PDF). Druckt das ERP Logo und Fußzeile selbst, braucht es kein Briefpapier.</p>`,
    `<button class="btn ghost" data-st="bp-add">+ Briefpapier</button>`) + items;
}

async function uploadStationery(el) {
  const [i, key] = el.dataset.bpup.split("|");
  const f = el.files[0]; if (!f) return;
  const fd = new FormData(); fd.append("file", f);
  try {
    const r = await api("POST", "api/admin/stationery/file", fd, true);
    ST.s.stationery.items[+i][key] = r.file;
    markDirty(); drawTab();
    msg(`„${f.name}“ hochgeladen (${r.pages} Seite(n), ${r.width} × ${r.height} mm).${r.hint ? " " + r.hint : ""} Zum Übernehmen speichern.`, !r.hint);
  } catch (e) { msg(e.message); }
}

async function previewStationery(i) {
  const b = ST.s.stationery.items[i];
  const res = await fetch("api/admin/stationery/preview", { method: "POST", headers: { "Content-Type": "application/json", "X-Requested-With": "Belegpost" }, body: JSON.stringify(b) });
  if (!res.ok) throw new Error("Vorschau fehlgeschlagen.");
  const url = URL.createObjectURL(await res.blob());
  $(`#bpPreview${i}`).innerHTML = `<iframe class="bp-preview" src="${url}#view=FitH" title="Vorschau Briefpapier"></iframe>`;
}

/* ---------------- Tab: E-Mail & Versand ---------------- */

function tabMail() {
  const m = ST.s.mail;
  const graph = m.transport === "Graph";
  const pwPh = ST.data.hasMailPassword && !ST.clearMailPassword ? "•••••••• (gespeichert – leer lassen = unverändert)" : "Kennwort des Postfachs";
  const gsPh = ST.data.hasGraphSecret && !ST.clearGraphSecret ? "•••••••• (gespeichert – leer lassen = unverändert)" : "Wert des geheimen Clientschlüssels";
  const t = ST.mailTest;
  const ph = (ST.data.placeholders || []).map(p => `<code>${esc(p)}</code>`).join(" ");
  const sender = `<div class="grid2">
       ${field("Absenderadresse", inText("mail.fromAddress", "rechnung@firma.at"), graph ? "Postfach in Microsoft 365, in dessen Namen gesendet wird." : "")}
       ${field("Absendername", inText("mail.fromName", "Firma GmbH – Buchhaltung"))}
       ${field("Antwort an (optional)", inText("mail.replyTo", "buchhaltung@firma.at"))}
       ${field("Kopie jeder Mail an (BCC, optional)", inText("mail.copyTo", "archiv@firma.at"))}
     </div>`;
  const test = `<div class="actions">
       <input type="email" id="mailTestTo" placeholder="Testmail an …" value="${esc(t?.to || "")}" style="max-width:260px">
       <button class="btn ghost" data-st="mail-test">Testmail senden</button>
       ${t?.loading ? '<span class="meta-inline">sende …</span>' : t ? `<span class="badge ${t.ok ? "ok" : "missing"}">${esc(t.message)}</span>` : ""}
     </div>
     <p class="help">Die Testmail verwendet die Werte oben – auch wenn sie noch nicht gespeichert sind.</p>`;

  const graphBox = `
     <div class="grid2">
       ${field("Verzeichnis-ID (Mandanten-ID)", inText("mail.graphTenantId", "z. B. 0a1b2c3d-… oder firma.at"))}
       ${field("Anwendungs-ID (Client-ID)", inText("mail.graphClientId", "z. B. 9f8e7d6c-…"))}
     </div>
     ${field("Geheimer Clientschlüssel", `<input type="password" data-graphsecret autocomplete="new-password" placeholder="${gsPh}" ${ST.clearGraphSecret ? "disabled" : ""}>`,
       "Wird verschlüsselt gespeichert. Clientschlüssel laufen ab (max. 24 Monate) – rechtzeitig erneuern.")}
     ${ST.data.hasGraphSecret ? `<label class="check"><input type="checkbox" data-st="graph-clear" ${ST.clearGraphSecret ? "checked" : ""}> gespeicherten Schlüssel entfernen</label>` : ""}
     ${ST.data.hasEntraLogin ? `<div class="actions"><button class="btn ghost" data-st="graph-from-login">IDs aus „Anmeldung“ übernehmen</button><span class="meta-inline">dieselbe App-Registrierung verwenden; der Schlüssel muss hier extra eingetragen werden</span></div>` : ""}
     ${inCheck("mail.graphSaveToSentItems", "Versendete Mails im Postfach unter „Gesendete Elemente“ ablegen")}
     <div class="help-box">
       <b>Einrichtung in Microsoft Entra</b> (entra.microsoft.com → Identität → Anwendungen → App-Registrierungen):
       <ol>
         <li><b>App-Registrierung</b> anlegen (oder die der Anmeldung verwenden) – Kontotyp <i>Nur dieses Organisationsverzeichnis</i>.</li>
         <li><b>API-Berechtigungen</b> → Berechtigung hinzufügen → Microsoft Graph → <b>Anwendungsberechtigungen</b> → <code>Mail.Send</code> → anschließend <b>Administratorzustimmung erteilen</b>.</li>
         <li><b>Zertifikate &amp; Geheimnisse</b> → Neuer geheimer Clientschlüssel → den <i>Wert</i> oben eintragen; Mandanten- und Client-ID aus der Übersicht.</li>
         <li><b>Empfohlen:</b> Die App nur für das Absenderpostfach freigeben (Exchange Online: <i>RBAC für Anwendungen</i> bzw. Application Access Policy) – sonst dürfte sie im Namen jedes Postfachs senden.</li>
       </ol>
       Mails über Graph brauchen kein SMTP und kein Postfach-Kennwort; große Anlagen (über 3 MB) werden automatisch in Teilen hochgeladen.
     </div>`;
  const smtpBox = `
     <div class="grid3">
       ${field("Server", inText("mail.host", "smtp.office365.com"))}
       ${field("Port", inNum("mail.port", 1))}
       ${field("Verschlüsselung", inSelect("mail.security", [["Auto", "Automatisch"], ["StartTls", "STARTTLS (587)"], ["SslOnConnect", "SSL/TLS (465)"], ["None", "Keine"]]))}
     </div>
     <div class="grid2">
       ${field("Benutzer", inText("mail.user", "rechnung@firma.at"))}
       ${field("Kennwort", `<input type="password" data-mailpw autocomplete="new-password" placeholder="${pwPh}" ${ST.clearMailPassword ? "disabled" : ""}>`)}
     </div>
     ${ST.data.hasMailPassword ? `<label class="check"><input type="checkbox" data-st="mail-clear" ${ST.clearMailPassword ? "checked" : ""}> gespeichertes Kennwort entfernen</label>` : ""}
     <p class="help">Für Microsoft 365 ist die Versandart „Microsoft 365 (Graph)“ empfohlen – SMTP mit Kennwort (SMTP AUTH) wird von Microsoft schrittweise abgeschaltet.</p>`;

  return section("Versand",
    `${inCheck("mail.enabled", "E-Mail-Versand aktivieren")}
     <div class="grid2">${field("Versandart", `<select data-path="mail.transport" data-redraw="1">
        <option value="Graph" ${graph ? "selected" : ""}>Microsoft 365 / Exchange Online (Graph-API)</option>
        <option value="Smtp" ${!graph ? "selected" : ""}>SMTP-Server</option></select>`)}</div>
     ${graph ? graphBox : smtpBox}
     ${sender}
     ${test}`) +
    section("Autoversand",
      `${inCheck("mail.autoSend", "Korrekte Vorgänge automatisch versenden")}
       <div class="grid2">
         ${field("Wartezeit vor dem Versand (Minuten)", inNum("mail.autoSendDelayMinutes", 0, 1440), "Zeit, um einen Vorgang noch zu prüfen oder zu verwerfen.")}
       </div>
       ${inCheck("mail.autoSendWithWarnings", "Auch Vorgänge mit Warnungen automatisch versenden")}
       <p class="help">Vorgänge mit Fehlern werden nie automatisch versendet. Ein fehlgeschlagener Versand wird nicht automatisch wiederholt, sondern im Portal als „Versandfehler“ angezeigt.</p>`) +
    section("Vorlagen",
      `${field("Betreff (wenn kein @@ed@@ gedruckt wurde)", inText("mail.defaultSubject"))}
       ${field("Mailtext (wenn kein @@sa@@ gedruckt wurde)", inArea("mail.defaultBody", 6))}
       ${field("Dateiname des Anhangs (wenn kein @@fn@@/@@mf@@ gedruckt wurde)", inText("mail.defaultFileName"))}
       <p class="help">Platzhalter: ${ph}</p>`);
}

/* ---------------- Tab: Mail-Vorlagen ---------------- */

const FONTS = [
  ["Segoe UI, Arial, Helvetica, sans-serif", "Segoe UI (Windows-Standard)"],
  ["Arial, Helvetica, sans-serif", "Arial"],
  ["Calibri, Arial, sans-serif", "Calibri"],
  ["Verdana, Geneva, sans-serif", "Verdana"],
  ["Tahoma, Verdana, sans-serif", "Tahoma"],
  ["Georgia, 'Times New Roman', serif", "Georgia (Serifen)"],
  ["'Times New Roman', Times, serif", "Times New Roman"]
];

const DEFAULT_HTML = `<!DOCTYPE html>
<html lang="de"><head><meta charset="utf-8"></head>
<body style="margin:0;background:#f4f5f7;">
<table width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px;">
  <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;font-family:Arial,sans-serif;font-size:15px;color:#1c2330;">
    <tr><td style="padding:24px 32px;">{Logo}</td></tr>
    <tr><td style="padding:0 32px;border-top:3px solid {Farbe};">&nbsp;</td></tr>
    <tr><td style="padding:8px 32px;">{Inhalt}</td></tr>
    <tr><td style="padding:8px 32px;">{Belegbox}</td></tr>
    <tr><td style="padding:8px 32px 24px 32px;">{Signatur}</td></tr>
  </table>
  <div style="font-family:Arial,sans-serif;font-size:12px;color:#6b7280;padding:12px;">{Fusszeile}</div>
</td></tr></table>
</body></html>`;

function colorField(path, label) {
  const v = get(ST.s, path) || "";
  return `<div class="field"><span class="lbl">${label}</span><div class="color-field">
    <input type="color" data-path="${path}" data-colortext value="${/^#[0-9a-fA-F]{6}$/.test(v) ? esc(v) : "#000000"}">
    <input type="text" data-path="${path}" data-colortext value="${esc(v)}" placeholder="#RRGGBB" maxlength="7"></div></div>`;
}

function tabTemplates() {
  const T = ST.s.mailTemplates ||= { items: [], default: "" };
  const bps = (ST.s.stationery?.items || []).map(b => b.name).filter(Boolean);
  const ph = (ST.data.placeholders || []).map(p => `<code>${esc(p)}</code>`).join(" ");
  const items = T.items.map((t, i) => {
    const P = `mailTemplates.items.${i}`;
    const logo = t.logoFile
      ? `<img class="mt-logo" src="api/admin/mailtemplates/logo/${encodeURIComponent(t.logoFile)}" alt="Logo">`
      : '<span class="muted">kein Logo</span>';
    const bpChecks = bps.length
      ? bps.map(n => `<label class="check"><input type="checkbox" data-mtbp="${i}" value="${esc(n)}" ${(t.stationeries || []).some(x => x.toLowerCase() === n.toLowerCase()) ? "checked" : ""}> ${esc(n)}</label>`).join("")
      : '<span class="muted">Noch keine Briefpapiere angelegt.</span>';
    const html = t.kind === "html";
    return section(`Vorlage ${esc(t.name || "(ohne Namen)")}`,
      `<div class="grid2">
         ${field("Name", inText(`${P}.name`, "z. B. Firma GmbH"), `Steuerzeichen: <code>@@pt${esc(t.name || "Name")}@@</code>`)}
         <div class="plain-field"><span class="lbl">Verwendung</span>
           <label class="check"><input type="checkbox" data-mtdefault value="${esc(t.name)}" ${T.default && T.default === t.name ? "checked" : ""}> Standardvorlage (wenn keine andere passt)</label></div>
       </div>
       <h4>Wann wird diese Vorlage verwendet?</h4>
       <div class="grid2">
         <div class="plain-field"><span class="lbl">bei Briefpapier</span><div class="check-list">${bpChecks}</div></div>
         ${field("bei Absender (eine Adresse pro Zeile)", inLines(`${P}.senders`, "rechnung@firma.at"), "Absender aus <code>@@pf…@@</code> oder aus „E-Mail &amp; Versand“.")}
       </div>
       <h4>Absender</h4>
       <div class="grid3">
         ${field("Absenderadresse", inText(`${P}.fromAddress`, "leer = aus „E-Mail & Versand“"))}
         ${field("Absendername", inText(`${P}.fromName`, "Firma GmbH – Buchhaltung"))}
         ${field("Antwort an", inText(`${P}.replyTo`, "optional"))}
       </div>
       ${ST.s.mail.transport === "Graph" ? '<p class="help">Bei Microsoft 365 muss die App-Registrierung auch für diese Absenderadresse senden dürfen.</p>' : ""}

       <h4>Gestaltung</h4>
       <div class="grid2">
         ${field("Art", `<select data-path="${P}.kind" data-redraw="1">
             <option value="simple" ${!html ? "selected" : ""}>Baukasten (Logo, Farben, Texte)</option>
             <option value="html" ${html ? "selected" : ""}>Eigenes HTML (für Profis)</option></select>`)}
         ${field("Schriftart", inSelect(`${P}.fontFamily`, FONTS))}
       </div>
       <div class="grid3">
         <div class="field"><span class="lbl">Logo (PNG/JPG)</span><div class="bp-file">${logo}
           <label class="btn ghost small">Logo hochladen<input type="file" accept="image/png,image/jpeg,image/gif" data-mtlogo="${i}" hidden></label>
           ${t.logoFile ? `<button class="btn ghost small" data-st="mt-logo-del" data-i="${i}">entfernen</button>` : ""}</div></div>
         ${field("Logobreite (Pixel)", inNum(`${P}.logoWidth`, 40, 560))}
         ${field("Logo-Position", inSelect(`${P}.logoAlign`, [["left", "links"], ["center", "mittig"], ["right", "rechts"]]))}
       </div>
       <div class="grid3">${colorField(`${P}.primaryColor`, "Hauptfarbe (CI)")}${colorField(`${P}.textColor`, "Textfarbe")}${colorField(`${P}.backgroundColor`, "Hintergrund")}</div>
       ${!html ? field("Kopfzeile (Balken in der Hauptfarbe, optional)", inText(`${P}.headerText`, "z. B. Ihre {Belegart} {Belegnummer}")) : ""}
       ${inCheck(`${P}.showDocumentBox`, "Infokasten mit Belegnummer, Datum, Betrag und Anhang anzeigen")}
       ${field("Signatur", inArea(`${P}.signature`, 4))}
       ${field("Fußzeile (Impressum, klein und grau)", inArea(`${P}.footer`, 3))}
       ${html ? `${field("Eigenes HTML", `<textarea class="mono" data-path="${P}.customHtml" rows="14">${esc(t.customHtml || "")}</textarea>`,
            "Pflicht: <code>{Inhalt}</code>. Außerdem: <code>{Logo}</code> <code>{Belegbox}</code> <code>{Signatur}</code> <code>{Fusszeile}</code> <code>{Kopfzeile}</code> <code>{Farbe}</code> und alle Platzhalter unten. Inline-Styles und Tabellen verwenden – Outlook ignoriert &lt;style&gt; und CSS-Layouts weitgehend.")}
          ${t.customHtml ? "" : `<div class="actions"><button class="btn ghost" data-st="mt-html" data-i="${i}">Beispiel-HTML einsetzen</button></div>`}` : ""}

       <details ${t.subject || t.body ? "open" : ""}><summary>Eigener Betreff und Mailtext für diese Vorlage</summary>
         <p class="help">Leer = Vorlagen aus „E-Mail &amp; Versand“. Was das ERP mit <code>@@ed@@</code>, <code>@@sa@@</code> oder <code>@@mb@@</code> druckt, hat immer Vorrang.</p>
         ${field("Betreff", inText(`${P}.subject`, "z. B. {Belegart} {Belegnummer} – Firma GmbH"))}
         ${field("Mailtext", inArea(`${P}.body`, 7))}
       </details>

       <div class="actions"><button class="btn" data-st="mt-preview" data-i="${i}">Vorschau</button>
         <button class="btn ghost" data-st="mt-copy" data-i="${i}">Kopieren</button>
         <span class="meta-inline">mit Beispieldaten – auch vor dem Speichern</span></div>
       <div id="mtPreview${i}"></div>`,
      `<button class="icon-btn" data-st="mt-del" data-i="${i}" title="Vorlage entfernen">✕</button>`);
  }).join("");

  return section("HTML-Mails im CI",
    `<p class="help">Jede Mail wird als HTML-Mail mit Logo, Farben und Signatur der Firma versendet (mit Textfassung für Programme ohne HTML).
       Die Vorlage wird so gewählt: <b>1.</b> Steuerzeichen <code>@@ptName@@</code> → <b>2.</b> Vorlage, der das verwendete Briefpapier zugeordnet ist →
       <b>3.</b> Vorlage des Absenders → <b>4.</b> Standardvorlage. Ohne Vorlage geht eine schlichte Mail ohne Gestaltung hinaus.</p>
     <h4>Platzhalter</h4>
     <p class="help">In Betreff, Mailtext, Kopfzeile, Signatur, Fußzeile und Dateiname: ${ph}</p>
     <p class="help">Eigene Platzhalter übergibt das ERP mit <code>@@pvName=Wert@@</code>, z. B. <code>@@pvAnrede=Sehr geehrter Herr Maier@@</code> → im Text <code>{Anrede}</code>.
       Ersatztext, wenn nichts übergeben wurde: <code>{Anrede|Sehr geehrte Damen und Herren}</code>. Fehlt ein Wert ohne Ersatztext, meldet das Portal eine Warnung.</p>
     <p class="help">Weitere Steuerzeichen: <code>@@ptName@@</code> wählt die Vorlage, <code>@@pfadresse@firma.at@@</code> den Absender.
       Im Text: <code>**fett**</code>, <code>*kursiv*</code>, Zeilen mit „- “ werden zur Aufzählung, Adressen und Links werden klickbar.
       Falsch kodierte Umlaute (z. B. „Ã¼“ statt „ü“) werden automatisch korrigiert und im Vorgang als Hinweis angezeigt.</p>`,
    `<button class="btn ghost" data-st="mt-add">+ Vorlage</button>`) + items;
}

async function uploadLogo(el) {
  const i = +el.dataset.mtlogo, f = el.files[0]; if (!f) return;
  const fd = new FormData(); fd.append("file", f);
  try {
    const r = await api("POST", "api/admin/mailtemplates/logo", fd, true);
    ST.s.mailTemplates.items[i].logoFile = r.file;
    markDirty(); drawTab();
    msg(`Logo „${f.name}“ hochgeladen. Zum Übernehmen speichern.`, true);
  } catch (e) { msg(e.message); }
}

async function previewTemplate(i) {
  const t = ST.s.mailTemplates.items[i];
  const r = await api("POST", "api/admin/mailtemplates/preview", t);
  const box = $(`#mtPreview${i}`);
  box.innerHTML = `<div class="mail-preview">
      <div class="mail-preview-head"><b>Betreff:</b> ${esc(r.subject)}
        ${r.missing?.length ? `<span class="badge missing">ohne Wert: ${r.missing.map(x => "{" + esc(x) + "}").join(", ")}</span>` : ""}</div>
      <iframe sandbox="" title="Vorschau Mail"></iframe></div>`;
  box.querySelector("iframe").srcdoc = r.html;
}

/* ---------------- Tab: E-Rechnung ---------------- */

function tabInvoice() {
  const units = Object.entries(ST.s.invoice.units || {}).sort((a, b) => a[0].localeCompare(b[0], "de"));
  return section("Prüfung",
    `<div class="grid2">${field("Toleranz der Summenprüfung", inNum("invoice.sumTolerance", 0, 10, 0.01), "Abweichung in Belegwährung, ab der die Summenprüfung einen Fehler meldet (Standard 0,02).")}</div>`) +
    section("Einheiten",
      `<p class="help">Das ERP druckt die Mengeneinheit als Text (z. B. „Stk“). Die E-Rechnung braucht den UN/ECE-Code (z. B. H87 = Stück, HUR = Stunde, C62 = Einheit).
        Nicht zugeordnete Einheiten erscheinen beim Vorgang als Warnung.</p>
       <div class="units">${units.map(([k, v]) => `<div class="unit"><input class="key" data-unit="${esc(k)}" value="${esc(k)}" placeholder="ERP-Text">→<input class="code" data-unit="${esc(k)}" value="${esc(v)}" placeholder="Code" maxlength="3"><button class="icon-btn" data-st="unit-del" data-key="${esc(k)}" title="Entfernen">✕</button></div>`).join("")}</div>`,
      `<button class="btn ghost" data-st="unit-add">+ Einheit</button>`) +
    section("Formate",
      `<p class="help">Das ERP fordert das Format je Kunde per <code>@@xt …@@</code> an. Die Erzeugung der E-Rechnung (XML, PDF/A-3) folgt mit dem Druckdienst;
        bis dahin wird ein angefordertes Format im Vorgang als Fehler gemeldet, damit nichts ohne E-Rechnung automatisch hinausgeht.</p>`);
}

/* ---------------- Tab: Ablage ---------------- */

function tabStorage() {
  return section("Aufbewahrung",
    `<div class="grid2">${field("Abgeschlossene Vorgänge löschen nach (Tagen)", inNum("storage.retentionDays", 0, 36500), "0 = nie löschen. Betrifft nur versendete, verworfene und Vorgänge ohne Versand.")}</div>
     <p class="help">Das Portal ist kein Archiv im Sinne der Aufbewahrungspflichten – dafür den Exportordner oder das DMS verwenden.</p>`) +
    section("Export",
      field("Versendete Belege zusätzlich ablegen in", inText("storage.exportFolder", "\\\\server\\archiv\\rechnungen"), "Leer = aus. Der Dienst braucht Schreibrechte (läuft als SYSTEM → Computerkonto des Servers berechtigen)."));
}

/* ---------------- Tab: Benutzer ---------------- */

async function loadUsers() {
  try { ST.users = await api("GET", "api/admin/users"); ST.usersDirty = false; }
  catch (e) { ST.users = { users: [], error: e.message }; }
  if (ST.tab === "benutzer") drawTab();
}

function tabUsers() {
  const U = ST.users;
  if (!U) return `<p class="meta">Lade Benutzer …</p>`;
  if (U.error) return `<div class="notice">${esc(U.error)}</div>`;
  const fmtLogin = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "–";
  return section("Benutzer",
    `<p class="help">Anmeldung mit dem Microsoft-365-Konto (E-Mail-Adresse). <b>Versenden</b> = darf Versanddaten ändern, senden und verwerfen; ohne dieses Recht nur ansehen.
      <b>Admin</b> = zusätzlich Einstellungen. <b>Aktiv</b> abgewählt = keine Anmeldung.
      <b>ERP-Bediener-Nr.</b> = dreistellige Bedienernummer aus dem ERP; im Druck <code>@@pu007@@</code> (führende Nullen egal).
      <b>Druck-Benutzer</b> = Windows- oder ERP-Benutzer dieser Person (aus dem Druckauftrag oder <code>@@pu…@@</code>). Ohne Eintrag wird der Anmeldename ohne Domäne verglichen.
      So landet ein gedruckter Beleg bei „Meine Belege“ und öffnet sich über den Melder auf dem PC dieser Person.
      <b>Sieht Belege</b>: nur eigene, eigene plus die ausgewählter Kollegen (z. B. Vertretung, Sekretariat) oder alle. Admins sehen immer alle; Belege ohne zugeordneten Benutzer sehen nur „alle“ und Admins.</p>
    ${U.users.length ? `<div class="table-wrap"><table class="list mini users"><thead><tr><th>Name</th><th>E-Mail (Anmeldename)</th><th>Weitere Anmeldenamen</th>
      <th>Versenden</th><th>Admin</th><th>Aktiv</th><th>Letzte Anmeldung</th><th></th></tr></thead><tbody>
      ${U.users.map((u, i) => `<tr class="${u.active ? "" : "inactive"}">
        <td><input data-u="${i}|name" value="${esc(u.name)}" placeholder="Vorname Nachname"></td>
        <td><input data-u="${i}|email" value="${esc(u.email || "")}" placeholder="name@firma.at"></td>
        <td><input data-u="${i}|logins" value="${esc((u.logins || []).join(", "))}" placeholder="optional"></td>
        <td><input type="checkbox" data-u="${i}|canSend" ${u.canSend ? "checked" : ""}></td>
        <td><input type="checkbox" data-u="${i}|isAdmin" ${u.isAdmin ? "checked" : ""}></td>
        <td><input type="checkbox" data-u="${i}|active" ${u.active ? "checked" : ""}></td>
        <td class="meta-inline">${fmtLogin(u.lastLogin)}</td>
        <td><button class="icon-btn" data-st="user-del" data-i="${i}" title="Entfernen">✕</button></td></tr>
        <tr class="user-sub ${u.active ? "" : "inactive"}"><td colspan="8"><div class="user-sub-grid">
          <label>ERP-Bediener-Nr.<input data-u="${i}|operatorNumbers" value="${esc((u.operatorNumbers || []).join(", "))}" placeholder="z. B. 007" inputmode="numeric"></label>
          <label>Druck-Benutzer (Windows/ERP)<input data-u="${i}|printNames" value="${esc((u.printNames || []).join(", "))}" placeholder="z. B. FIRMA\\mmuster"></label>
          <label>Sieht Belege<select data-u="${i}|visibility" ${u.isAdmin ? "disabled" : ""}>
            ${[["eigene", "nur eigene"], ["auswahl", "eigene + ausgewählte Benutzer"], ["alle", "alle"]].map(([k, t]) =>
              `<option value="${k}" ${(u.isAdmin ? "alle" : (u.visibility || "eigene")) === k ? "selected" : ""}>${t}</option>`).join("")}</select></label>
          ${!u.isAdmin && u.visibility === "auswahl" ? `<div class="user-also"><span>zusätzlich die Belege von:</span>
            ${U.users.filter(o => o !== u && o.id).map(o => `<label class="check"><input type="checkbox" data-also="${i}" value="${esc(o.id)}" ${(u.alsoSees || []).includes(o.id) ? "checked" : ""}> ${esc(o.name || o.email)}</label>`).join("") || '<span class="meta-inline">(neue Benutzer erst speichern)</span>'}</div>` : ""}
        </div></td></tr>`).join("")}
      </tbody></table></div>` : `<p class="meta">Noch keine Benutzer. Bis dahin ist nur die Anmeldung als Administrator direkt am Server möglich.</p>`}
    <div class="actions"><button class="btn" data-st="users-save" ${ST.usersDirty ? "" : "disabled"}>Benutzer speichern</button></div>`,
    `<button class="btn ghost" data-st="user-add">+ Benutzer</button>`);
}

function markUsersDirty() {
  ST.usersDirty = true;
  const b = document.querySelector('[data-st="users-save"]');
  if (b) b.disabled = false;
}

function onUsersInput(el, e) {
  if (el.dataset.also !== undefined) {
    const u = ST.users.users[+el.dataset.also];
    u.alsoSees = (u.alsoSees || []).filter(x => x !== el.value);
    if (el.checked) u.alsoSees.push(el.value);
    markUsersDirty(); return true;
  }
  if (!el.dataset.u) return false;
  const [i, k] = el.dataset.u.split("|"), u = ST.users.users[+i];
  if (k === "operatorNumbers") u.operatorNumbers = el.value.split(/[,;\s]+/).map(x => x.trim()).filter(Boolean);
  else if (k === "visibility") { u.visibility = el.value; markUsersDirty(); drawTab(); return true; }
  else if (k === "printNames") u.printNames = el.value.split(/[,;\s]+/).map(x => x.trim()).filter(Boolean);
  else if (k === "logins") u.logins = el.value.split(/[,;\s]+/).map(x => x.trim()).filter(Boolean);
  else if (el.type === "checkbox") { u[k] = el.checked; if (k === "isAdmin" || k === "active") { markUsersDirty(); drawTab(); return true; } }
  else u[k] = el.value;
  ST.usersDirty = true;
  const b = document.querySelector('[data-st="users-save"]');
  if (b) b.disabled = false;
  return true;
}

/* ---------------- Tab: Anmeldung → Firmenportal ---------------- */

function tabFirmenportal() {
  const f = ST.s.auth.firmenportal ||= { enabled: false, url: "", createUsers: false };
  const base = `https://${(ST.data.hostNames || ["SERVER"])[0]}:${ST.s.server.httpsPort}`;
  return section("Einbindung ins Firmenportal",
    `${inCheck("auth.firmenportal.enabled", "Anmeldung aus dem Firmenportal übernehmen und Einbettung erlauben")}
     ${field("Adresse des Firmenportals", inText("auth.firmenportal.url", "https://firmenportal.firma.at:5443"),
       "Nur diese Seite darf Belegpost in einem Rahmen (iframe) anzeigen.")}
     ${inCheck("auth.firmenportal.createUsers", "Unbekannte Benutzer beim ersten Aufruf automatisch anlegen (sehen nur eigene Belege)")}
     <div class="plain-field"><span class="lbl">Gemeinsamer Schlüssel</span>
       ${ST.fpKey ? `<div class="melder-code">${esc(ST.fpKey)}</div>
           <p class="help"><b>Jetzt kopieren</b> und im Firmenportal hinterlegen – er wird nur dieses eine Mal angezeigt.</p>`
         : `<p class="meta">${ST.data.hasFirmenportalSecret ? "Schlüssel ist hinterlegt." : "Noch kein Schlüssel."}</p>`}
       <div class="actions"><button class="btn ghost" data-st="fp-key">${ST.data.hasFirmenportalSecret ? "Neuen Schlüssel erzeugen" : "Schlüssel erzeugen"}</button></div></div>
     <details><summary>So bindet das Firmenportal Belegpost ein</summary>
       <ol class="steps">
         <li>Das Firmenportal erzeugt für den angemeldeten Benutzer ein JWT (HS256, obiger Schlüssel) mit
           <code>iss = "Firmenportal"</code>, <code>aud = "Belegpost"</code>, <code>email</code> (bzw. <code>upn</code>), <code>name</code>,
           <code>jti</code> (einmalig), <code>iat</code> und <code>exp</code> (höchstens 5 Minuten).</li>
         <li>Aufruf oder iframe-Quelle: <code>${esc(base)}/auth/firmenportal?token=…&amp;ziel=%2F%23%2Fvorgaenge%3Ff%3Dmeine</code></li>
         <li>Belegpost meldet den Benutzer an (Zuordnung über E-Mail/Anmeldename unter „Benutzer“) und zeigt das Ziel an, z. B. einen Vorgang:
           <code>ziel=/#/vorgang?id=…</code>.</li>
       </ol>
       <p class="help">Details und Beispielcode: <code>docs/Firmenportal-Anbindung.md</code>. Nach dem Ändern den Dienst neu starten (Cookie-Einstellung für die Einbettung).</p>
     </details>`);
}

/* ---------------- Tab: Anmeldung ---------------- */

function tabAuth() {
  const e = ST.s.auth.entra ||= { enabled: false, tenantId: "", clientId: "" };
  const uris = ST.data.entraRedirectUris || [];
  const secretPh = ST.data.hasEntraSecret && !ST.clearEntraSecret ? "•••••••• (gespeichert – leer lassen = unverändert)" : "Wert des geheimen Clientschlüssels";
  return section("Microsoft Entra ID (Anmeldung der Benutzer)",
    `${inCheck("auth.entra.enabled", "Anmeldung mit Microsoft-Konto aktivieren")}
     <div class="grid2">
       ${field("Verzeichnis-ID (Mandanten-ID)", inText("auth.entra.tenantId", "z. B. 0a1b2c3d-… oder firma.at"))}
       ${field("Anwendungs-ID (Client-ID)", inText("auth.entra.clientId", "z. B. 9f8e7d6c-…"))}
     </div>
     ${field("Geheimer Clientschlüssel", `<input type="password" data-entrasecret autocomplete="new-password" placeholder="${secretPh}" ${ST.clearEntraSecret ? "disabled" : ""}>`,
       "Wird verschlüsselt gespeichert. Achtung: Clientschlüssel laufen ab (max. 24 Monate) – rechtzeitig erneuern.")}
     ${ST.data.hasEntraSecret ? `<label class="check"><input type="checkbox" data-st="entra-clear" ${ST.clearEntraSecret ? "checked" : ""}> gespeicherten Schlüssel entfernen</label>` : ""}
     <div class="help-box">
       <b>Einrichtung im Microsoft-Entra-Admin-Center</b> (entra.microsoft.com → Identität → Anwendungen → App-Registrierungen):
       <ol>
         <li><b>Neue Registrierung</b> – Name „Belegpost“, Kontotypen: <i>Nur Konten in diesem Organisationsverzeichnis</i>.</li>
         <li><b>Umleitungs-URI</b> – Plattform <i>Web</i>, eine oder mehrere dieser Adressen eintragen:
           <ul>${uris.map(u => `<li><code>${esc(u)}</code></li>`).join("")}</ul></li>
         <li><b>Übersicht</b> – <i>Anwendungs-ID (Client)</i> und <i>Verzeichnis-ID (Mandant)</i> oben eintragen.</li>
         <li><b>Zertifikate &amp; Geheimnisse</b> → Neuer geheimer Clientschlüssel → den <i>Wert</i> (nicht die ID) oben eintragen.</li>
         <li>Speichern und Dienst neu starten. Danach können sich alle unter „Benutzer“ angelegten Personen anmelden.</li>
       </ol>
     </div>`) +
    tabFirmenportal() +
    section("Administrator direkt am Server",
      portal.isLocalAdmin
        ? `<p class="help">Mit diesem Kennwort meldest du dich direkt am Server (<code>https://localhost:${ST.s.server.httpsPort}</code>) an – auch wenn die Microsoft-Anmeldung einmal nicht funktioniert.</p>
           <div class="grid3">
             ${field("Bisheriges Kennwort", `<input type="password" id="lpCur" autocomplete="current-password">`)}
             ${field("Neues Kennwort", `<input type="password" id="lpNew" autocomplete="new-password" minlength="10">`)}
             ${field("Wiederholen", `<input type="password" id="lpNew2" autocomplete="new-password" minlength="10">`)}
           </div>
           <div class="actions"><button class="btn ghost" data-st="local-pw">Kennwort ändern</button></div>`
        : `<p class="help">Das Administrator-Kennwort kann nur angemeldet als Administrator direkt am Server geändert werden.
           Vergessen? Auf dem Server <code>Belegpost.exe --reset-admin-password</code> ausführen.</p>`);
}
