"use strict";

/* =========================================================
   Einstellungen – Oberfläche zur Konfiguration des Dienstes
   (Aufbau wie im Firmenportal)
   ========================================================= */

const ST = { data: null, s: null, tab: store.get("st.tab", "dienst"), dirty: false, service: null,
  entraSecret: "", clearEntraSecret: false, mailPassword: "", clearMailPassword: false, users: null, usersDirty: false, mailTest: null };

const TABS = [
  ["dienst", "Dienst"],
  ["server", "Server & Zertifikat"],
  ["eingang", "Drucker & Eingang"],
  ["mail", "E-Mail & Versand"],
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
  ST.dirty = false; ST.entraSecret = ""; ST.clearEntraSecret = false; ST.mailPassword = ""; ST.clearMailPassword = false;
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
  const fn = { dienst: tabService, server: tabServer, eingang: tabInbox, mail: tabMail, erechnung: tabInvoice, ablage: tabStorage, benutzer: tabUsers, anmeldung: tabAuth }[ST.tab] || tabService;
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
  if (el.dataset.unit !== undefined) {
    if (e.type !== "change") return;
    const units = ST.s.invoice.units, old = el.dataset.unit, row = el.closest(".unit");
    const key = row.querySelector(".key").value.trim(), code = row.querySelector(".code").value.trim().toUpperCase();
    if (old && old !== key) delete units[old];
    if (key) units[key] = code;
    markDirty(); drawTab(); return;
  }
  if (el.dataset.path) {
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
      case "cert-self":
        if (await confirmDialog("Zertifikat", "Eigenes Zertifikat entfernen und wieder das selbst signierte verwenden?", "Zurücksetzen")) {
          await api("POST", "api/admin/certificate/selfsigned"); restartHint("Zertifikat zurückgesetzt."); await loadSettings();
        }
        break;
      case "entra-clear": ST.clearEntraSecret = !ST.clearEntraSecret; markDirty(); drawTab(); break;
      case "mail-clear": ST.clearMailPassword = !ST.clearMailPassword; markDirty(); drawTab(); break;
      case "mail-test": {
        const to = $("#mailTestTo").value.trim();
        ST.mailTest = { loading: true, to }; drawTab();
        try { ST.mailTest = { ...(await api("POST", "api/admin/mail-test", { mail: ST.s.mail, password: ST.mailPassword || null, to })), to }; }
        catch (err) { ST.mailTest = { ok: false, message: err.message, to }; }
        drawTab();
        break;
      }
      case "unit-add": ST.s.invoice.units[""] = ""; drawTab(); document.querySelector('.unit .key[value=""]')?.focus(); break;
      case "unit-del": delete ST.s.invoice.units[btn.dataset.key]; markDirty(); drawTab(); break;
      case "user-add":
        ST.users.users.push({ id: "", name: "", email: "", logins: [], canSend: true, isAdmin: false, active: true });
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
    mailPassword: ST.mailPassword || null, clearMailPassword: ST.clearMailPassword });
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
  const { status: s, setupMode, installLog } = ST.service;
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
    </table>
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
    ${field("Titel des Portals", inText("portal.title", "PDF Rechnung"))}
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
      `${field("Name des Druckers", inText("inbox.printerName", "PDF Rechnung"))}
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

/* ---------------- Tab: E-Mail & Versand ---------------- */

function tabMail() {
  const pwPh = ST.data.hasMailPassword && !ST.clearMailPassword ? "•••••••• (gespeichert – leer lassen = unverändert)" : "Kennwort des Postfachs";
  const t = ST.mailTest;
  const ph = (ST.data.placeholders || []).map(p => `<code>${esc(p)}</code>`).join(" ");
  return section("SMTP-Server",
    `${inCheck("mail.enabled", "E-Mail-Versand aktivieren")}
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
     <div class="grid2">
       ${field("Absenderadresse", inText("mail.fromAddress", "rechnung@firma.at"))}
       ${field("Absendername", inText("mail.fromName", "Firma GmbH – Buchhaltung"))}
       ${field("Antwort an (optional)", inText("mail.replyTo", "buchhaltung@firma.at"))}
       ${field("Kopie jeder Mail an (BCC, optional)", inText("mail.copyTo", "archiv@firma.at"))}
     </div>
     <p class="help">Microsoft 365: Server <code>smtp.office365.com</code>, Port 587, STARTTLS; für das Postfach muss „Authentifiziertes SMTP“ erlaubt sein.</p>
     <div class="actions">
       <input type="email" id="mailTestTo" placeholder="Testmail an …" value="${esc(t?.to || "")}" style="max-width:260px">
       <button class="btn ghost" data-st="mail-test">Testmail senden</button>
       ${t?.loading ? '<span class="meta-inline">sende …</span>' : t ? `<span class="badge ${t.ok ? "ok" : "missing"}">${esc(t.message)}</span>` : ""}
     </div>
     <p class="help">Die Testmail verwendet die Werte oben – auch wenn sie noch nicht gespeichert sind.</p>`) +
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
       ${field("Dateiname des Anhangs (wenn kein @@fn@@ gedruckt wurde)", inText("mail.defaultFileName"))}
       <p class="help">Platzhalter: ${ph}</p>`);
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
      <b>Admin</b> = zusätzlich Einstellungen. <b>Aktiv</b> abgewählt = keine Anmeldung.</p>
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
        <td><button class="icon-btn" data-st="user-del" data-i="${i}" title="Entfernen">✕</button></td></tr>`).join("")}
      </tbody></table></div>` : `<p class="meta">Noch keine Benutzer. Bis dahin ist nur die Anmeldung als Administrator direkt am Server möglich.</p>`}
    <div class="actions"><button class="btn" data-st="users-save" ${ST.usersDirty ? "" : "disabled"}>Benutzer speichern</button></div>`,
    `<button class="btn ghost" data-st="user-add">+ Benutzer</button>`);
}

function onUsersInput(el, e) {
  if (!el.dataset.u) return false;
  const [i, k] = el.dataset.u.split("|"), u = ST.users.users[+i];
  if (k === "logins") u.logins = el.value.split(/[,;\s]+/).map(x => x.trim()).filter(Boolean);
  else if (el.type === "checkbox") u[k] = el.checked;
  else u[k] = el.value;
  ST.usersDirty = true;
  const b = document.querySelector('[data-st="users-save"]');
  if (b) b.disabled = false;
  return true;
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
         <li><b>Neue Registrierung</b> – Name „PDF Rechnung“, Kontotypen: <i>Nur Konten in diesem Organisationsverzeichnis</i>.</li>
         <li><b>Umleitungs-URI</b> – Plattform <i>Web</i>, eine oder mehrere dieser Adressen eintragen:
           <ul>${uris.map(u => `<li><code>${esc(u)}</code></li>`).join("")}</ul></li>
         <li><b>Übersicht</b> – <i>Anwendungs-ID (Client)</i> und <i>Verzeichnis-ID (Mandant)</i> oben eintragen.</li>
         <li><b>Zertifikate &amp; Geheimnisse</b> → Neuer geheimer Clientschlüssel → den <i>Wert</i> (nicht die ID) oben eintragen.</li>
         <li>Speichern und Dienst neu starten. Danach können sich alle unter „Benutzer“ angelegten Personen anmelden.</li>
       </ol>
     </div>`) +
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
           Vergessen? Auf dem Server <code>PdfRechnung.exe --reset-admin-password</code> ausführen.</p>`);
}
