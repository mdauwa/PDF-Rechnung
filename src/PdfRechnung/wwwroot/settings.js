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
  ["regeln", "Regeln & Signaturen"],
  ["erechnung", "E-Rechnung"],
  ["ablage", "Ablage"],
  ["mandanten", "Mandanten"],
  ["versandwege", "Versandwege"],
  ["kunden", "Kommunikationspartner"],
  ["connect", "ERP Suite Connect"],
  ["erp", "ERP-Rückmeldung"],
  ["mapi", "MAPI-Mails"],
  ["statuslink", "Status-Link"],
  ["benutzer", "Benutzer"],
  ["anmeldung", "Anmeldung"],
  ["benachrichtigung", "Benachrichtigung"],
  ["api", "REST-API"],
  ["filialverbund", "Filialverbund"]
];

/** Einstellungen im Menü links: Gruppen mit ihren Reitern. */
const ST_GROUPS = [
  ["benutzer", "Benutzer", "users", ["benutzer"]],
  ["system", "Dienst & Zugang", "settings", ["dienst", "server", "anmeldung", "benachrichtigung", "api"]],
  ["belege", "Drucker & Belege", "doc", ["eingang", "briefpapier", "erechnung", "ablage"]],
  ["email", "E-Mail", "send", ["mail", "mailvorlagen", "regeln", "mapi", "statuslink"]],
  ["versand", "Versand & Partner", "upload", ["versandwege", "kunden", "filialverbund", "connect"]],
  ["erp", "ERP & Mandanten", "inbox", ["erp", "mandanten"]]
];
const stGroupOf = tab => ST_GROUPS.find(g => g[3].includes(tab)) || ST_GROUPS[0];
const stGroupTabs = () => TABS.filter(([k]) => stGroupOf(k)[0] === ST.group);

/** Zu einem Reiter springen (ggf. in eine andere Gruppe). */
function stGoto(tab) {
  const g = stGroupOf(tab)[0];
  if (g !== ST.group) { location.hash = `#/einstellungen?g=${g}&t=${tab}`; return; }
  ST.tab = tab; drawTab();
}

const get = (obj, path) => path.split(".").reduce((o, k) => o?.[k], obj);
function set(obj, path, val) {
  const keys = path.split("."), last = keys.pop();
  keys.reduce((o, k) => (o[k] ??= {}), obj)[last] = val;
}

/* ---------------- Rahmen ---------------- */

async function renderSettings(view) {
  const q = new URLSearchParams(location.hash.split("?")[1] || "");
  const grp = ST_GROUPS.find(g => g[0] === q.get("g")) || ST_GROUPS[0];
  ST.group = grp[0];
  // Beim Aufruf über das Menü ist immer der erste Reiter aktiv
  ST.tab = q.get("t") && grp[3].includes(q.get("t")) ? q.get("t") : (stGroupTabs()[0] || [grp[3][0]])[0];
  $("#viewTitle").textContent = grp[0] === "benutzer" ? "Benutzer" : `Einstellungen – ${grp[1]}`;
  view.innerHTML = `<div class="settings">
    <div class="notice info" id="stSetup" hidden></div>
    <div class="notice" id="stMsg" hidden></div>
    <nav class="tabs" id="stTabs" ${stGroupTabs().length < 2 ? "hidden" : ""}>${stGroupTabs().map(([k, t]) => `<button data-tab="${k}">${t}</button>`).join("")}</nav>
    <div id="stBody" class="st-body"><p class="meta">Lade Einstellungen …</p></div>
    <div class="savebar" id="stSave" hidden>
      <span id="stSaveText">Ungespeicherte Änderungen</span>
      <button class="btn ghost" data-st="discard">Verwerfen</button>
      <button class="btn" data-st="save">Speichern</button>
    </div>
  </div>`;

  view.querySelector("#stTabs").onclick = e => {
    const t = e.target.closest("[data-tab]")?.dataset.tab;
    if (t) { ST.tab = t; ST.mtEdit = null; ST.userEdit = null; ST.mdEdit = null; drawTab(); }
  };
  rteInstall(view);
  view.addEventListener("click", onAction);
  view.addEventListener("toggle", trackFold, true);
  view.addEventListener("input", onInput);
  view.addEventListener("change", onInput);

  // Ungespeicherte Änderungen bleiben beim Wechsel zwischen den Einstellungsseiten erhalten
  if (ST.s && ST.dirty) { drawTab(); updateSaveBar(); const s = $("#stSetup"); if (s) s.hidden = true; return; }
  try { await loadSettings(); }
  catch (e) { msg("Einstellungen konnten nicht geladen werden: " + e.message); $("#stBody").innerHTML = ""; }
}

async function loadSettings() {
  ST.data = await api("GET", "api/admin/settings");
  ST.s = structuredClone(ST.data.settings);
  ST.erpPassword = ""; ST.clearErpPassword = false; ST.saveWarnings = null;
  ST.dirty = false; ST.entraSecret = ""; ST.clearEntraSecret = false; ST.mailPassword = ""; ST.clearMailPassword = false; ST.graphSecret = ""; ST.clearGraphSecret = false;
  const setup = $("#stSetup");
  setup.hidden = !ST.data.setupMode;
  setup.innerHTML = "<b>Einrichtungsmodus.</b> Das Programm wurde per Doppelklick gestartet und ist nur an diesem Rechner erreichbar. " +
    "Nach dem Einrichten unter <span class=\"linkish\" data-goto=\"dienst\">Dienst</span> als Windows-Dienst installieren.";
  if (!stGroupTabs().some(([k]) => k === ST.tab)) ST.tab = stGroupTabs()[0][0];
  drawTab();
  updateSaveBar();
}

function drawTab() {
  document.querySelectorAll("#stTabs button").forEach(b => b.classList.toggle("active", b.dataset.tab === ST.tab));
  const body = $("#stBody");
  if (!ST.s) return;
  const fn = { dienst: tabService, server: tabServer, eingang: tabInbox, briefpapier: tabStationery, mail: tabMail, mailvorlagen: tabTemplates, erechnung: tabInvoice, ablage: tabStorage, benutzer: tabUsers, anmeldung: tabAuth,
    erp: tabErp, mapi: tabMapi, statuslink: tabStatusLink, regeln: tabRules, mandanten: tabMandanten, versandwege: tabDelivery, kunden: tabCustomers, connect: tabConnect,
    benachrichtigung: tabAlerts, api: tabApi, filialverbund: tabFilialverbund }[ST.tab] || tabService;
  body.innerHTML = fn();
  if (ST.tab === "benutzer" && !ST.users) loadUsers();
  if (ST.tab === "benutzer" && !ST.devices) loadDevices();
  if (ST.tab === "erp") loadErpStatus();
  if (ST.tab === "mapi") loadMapiStatus();
  if (ST.tab === "eingang") loadPrinterStatus();
  if (ST.tab === "versandwege") loadDeliveryRuns();
  if (ST.tab === "dienst") loadService();
  if (ST.tab === "benachrichtigung" && !MON.status) loadMonStatus();
  if (ST.tab === "api" && !MON.keys) loadApiKeys();
  if (ST.tab === "filialverbund" && !MON.clients) loadFvClients();
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

const field = (label, inner, help) => {
  const tag = String(inner).includes('class="rte') ? "div" : "label"; // Editor nie in <label>: Klick im Text würde sonst die Werkzeugleiste fokussieren
  return `<${tag} class="field"><span class="lbl">${label}</span>${inner}${help ? `<small class="help">${help}</small>` : ""}</${tag}>`;
};
const inText = (path, ph = "", type = "text") => `<input type="${type}" data-path="${path}" value="${esc(get(ST.s, path) ?? "")}" placeholder="${esc(ph)}">`;
const inNum = (path, min = 0, max = 65535, step = 1) => `<input type="number" data-path="${path}" data-type="${step === 1 ? "int" : "dec"}" min="${min}" max="${max}" step="${step}" value="${esc(get(ST.s, path) ?? "")}">`;
const inCheck = (path, label) => `<label class="check"><input type="checkbox" data-path="${path}" ${get(ST.s, path) ? "checked" : ""}> ${label}</label>`;
const inLines = (path, ph) => `<textarea data-path="${path}" data-type="lines" rows="3" placeholder="${esc(ph)}">${esc((get(ST.s, path) || []).join("\n"))}</textarea>`;
const inArea = (path, rows = 5) => `<textarea data-path="${path}" rows="${rows}">${esc(get(ST.s, path) ?? "")}</textarea>`;
const inSelect = (path, options) => `<select data-path="${path}">${options.map(([v, t]) => `<option value="${esc(v)}" ${get(ST.s, path) === v ? "selected" : ""}>${esc(t)}</option>`).join("")}</select>`;
const section = (title, inner, extra = "") => `<section class="st-card"><div class="st-card-head"><h3>${title}</h3>${extra}</div><div class="st-card-body">${inner}</div></section>`;

function onInput(e) {
  const el = e.target;
  if (deliveryInput(el, e)) return;
  if (monitorInput(el, e)) return;
  if (el.dataset?.mdErp !== undefined) {
    const m = ST.s.mandanten.items[+el.dataset.mdErp];
    m.ownErp = el.value === "eigen"; m.erpOff = el.value === "aus";
    markDirty(); if (e.type === "change") drawTab(); return;
  }
  if (el.dataset?.rteCmd) return; // Werkzeugleiste des Editors
  if (ruleEditorInput(el, e)) return;
  if (el.dataset?.rte === "1" && el.dataset.path) { set(ST.s, el.dataset.path, fieldValue(el)); markDirty(); return; }
  if (ST.tab === "benutzer" && onUsersInput(el, e)) return;
  if (el.dataset.entrasecret !== undefined) { ST.entraSecret = el.value; markDirty(); return; }
  if (el.dataset.mailpw !== undefined) { ST.mailPassword = el.value; markDirty(); return; }
  if (el.dataset.graphsecret !== undefined) { ST.graphSecret = el.value; markDirty(); return; }
  if (el.dataset.erppw !== undefined) { ST.erpPassword = el.value; markDirty(); return; }
  if (onErpInput(el, e)) return;
  if (el.dataset.devmapi !== undefined && e.type === "change") { setDeviceMapi(el.dataset.devmapi, el.value); return; }
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
  if (await ruleEditorClick(e)) return;
  const goto = e.target.closest("[data-goto]")?.dataset.goto;
  if (goto) { stGoto(goto); return; }
  const a = e.target.closest("[data-st]")?.dataset.st;
  if (!a) return;
  const btn = e.target.closest("button") || e.target.closest("input");
  try {
    if (btn && btn.tagName === "BUTTON") btn.disabled = true;
    if (await deliveryClick(a, btn, e)) return;
    if (await monitorClick(a, btn, e)) return;
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
        ST.mtEdit = T.items.length - 1; markDirty(); drawTab(); window.scrollTo(0, 0); break;
      }
      case "mt-copy": {
        const T = ST.s.mailTemplates, src = T.items[+btn.dataset.i];
        T.items.push({ ...structuredClone(src), name: (src.name || "Vorlage") + " (Kopie)", stationeries: [], senders: [] });
        ST.mtEdit = T.items.length - 1; markDirty(); drawTab(); window.scrollTo(0, 0); break;
      }
      case "mt-open": ST.mtEdit = +e.target.closest("[data-st]").dataset.i; drawTab(); window.scrollTo(0, 0); break;
      case "mt-back": ST.mtEdit = null; drawTab(); break;
      case "mt-del": {
        const T = ST.s.mailTemplates, it = T.items[+btn.dataset.i];
        if (await confirmDialog("Mail-Vorlage entfernen", `„${esc(it.name || "ohne Namen")}“ entfernen?`, "Entfernen", true)) {
          if (T.default === it.name) T.default = "";
          T.items.splice(+btn.dataset.i, 1); ST.mtEdit = null; markDirty(); drawTab();
        }
        break;
      }
      case "mt-logo-del": ST.s.mailTemplates.items[+btn.dataset.i].logoFile = ""; markDirty(); drawTab(); break;
      case "mt-preview": await previewTemplate(+btn.dataset.i); break;
      case "mt-html": {
        const t = ST.s.mailTemplates.items[+btn.dataset.i];
        t.customHtml = t.customHtml || DEFAULT_HTML; markDirty(); drawTab(); break;
      }
      case "erp-clear": ST.clearErpPassword = !ST.clearErpPassword; markDirty(); drawTab(); break;
      case "erp-field-add": (ST.s.erp.fields ||= []).push({ field: "", value: "statuscode", format: "", length: 0, onlyCodes: [], onlyIfEmpty: false }); markDirty(); drawTab(); break;
      case "erp-field-del": ST.s.erp.fields.splice(+btn.dataset.i, 1); markDirty(); drawTab(); break;
      case "erp-check": await erpCall("api/admin/erp/pruefen", "Verbindung wird geprüft …"); break;
      case "md-add": (ST.s.mandanten ||= { items: [] }).items.push({ number: "", name: "", ownErp: false, erpOff: false, erpEndpoint: "", erpVendor: "", erpApplication: "",
          erpSecureAppId: "", erpRevision: 0, erpUsername: "", inboxFolder: "", pathMappings: [], gotomaxxPrinter: "", exportFolder: "" });
        ST.mdEdit = ST.s.mandanten.items.length - 1; markDirty(); drawTab(); window.scrollTo(0, 0); break;
      case "md-open": ST.mdEdit = +e.target.closest("[data-st]").dataset.i; drawTab(); window.scrollTo(0, 0); break;
      case "md-back": ST.mdEdit = null; drawTab(); break;
      case "md-del": {
        const m = ST.s.mandanten.items[+btn.dataset.i];
        if (!await confirmDialog("Mandant entfernen", `Mandant ${esc(m.number || "(ohne Nummer)")} aus den Einstellungen entfernen? Belege dieses Mandanten gelten danach wie ohne Mandant.`, "Entfernen", true)) break;
        ST.s.mandanten.items.splice(+btn.dataset.i, 1); ST.mdEdit = null; markDirty(); drawTab(); break;
      }
      case "md-pw-clear": { const m = ST.s.mandanten.items[+btn.dataset.i]; m.erpClearPassword = !m.erpClearPassword; markDirty(); drawTab(); break; }
      case "md-erp-check": {
        if (ST.dirty) { msg("Bitte zuerst speichern – geprüft wird die gespeicherte Einstellung."); break; }
        const r = await api("POST", "api/admin/erp/pruefen?mandant=" + encodeURIComponent(btn.dataset.m));
        msg(`Mandant ${btn.dataset.m}: ${r.message || "Erledigt."}`, r.ok !== false); break;
      }
      case "erp-confirm-reg":
        if (await confirmDialog("Registrierung zurücksetzen", "Bitte vorher im ERP prüfen, ob für Belegpost bereits ein Servicepass angelegt wurde (ggf. dort entfernen). Danach registriert Belegpost beim nächsten Zugriff neu.", "Zurücksetzen", true))
          await erpCall("api/admin/erp/registrierung-bestaetigen", "…");
        break;
      case "erp-retry-all": await api("POST", "api/admin/erp/wiederholen"); loadErpStatus(); break;
      case "erp-job-retry": await api("POST", `api/admin/erp/auftraege/${btn.dataset.id}/wiederholen`); loadErpStatus(); break;
      case "erp-job-discard": await api("POST", `api/admin/erp/auftraege/${btn.dataset.id}/verwerfen`); loadErpStatus(); break;
      case "erp-preview": {
        const r = await api("POST", "api/admin/erp/vorschau", { vorgangId: $("#erpPvId").value.trim() || null, code: $("#erpPvCode").value || null, erp: ST.s.erp });
        ST.erpPreview = r; drawTab(); break;
      }
      case "erp-test": {
        const idx = $("#erpTestIdx").value.trim();
        if (!idx) throw new Error("Bitte einen Belegindex eingeben.");
        if (ST.dirty) throw new Error("Bitte zuerst die Einstellungen speichern.");
        if (!await confirmDialog("Testschreiben", `Jetzt wirklich in Beleg ${esc(idx)} im ERP schreiben?`, "Schreiben", true)) break;
        const r = await api("POST", "api/admin/erp/testschreiben", { erpIndex: idx, vorgangId: $("#erpPvId").value.trim() || null, code: $("#erpPvCode").value || null, confirm: true });
        msg(r.message, r.ok); break;
      }
      case "printer-setup": {
        const r = await api("POST", "api/admin/drucker/einrichten");
        msg((r.ok ? "" : (r.message || "Fehler") + "\n") + (r.log || []).join("\n"), r.ok);
        loadPrinterStatus(); break;
      }
      case "printer-refresh": loadPrinterStatus(); break;
      case "xp-add": (ST.s.inbox.printers ||= []).push({ id: "", name: "", share: true, mandant: "", stationery: "" }); markDirty(); drawTab(); break;
      case "xp-del": {
        const x = ST.s.inbox.printers[+btn.dataset.i];
        if (x.id && !await confirmDialog("Drucker aus der Liste löschen", `„${esc(x.name)}“ aus der Liste löschen? Der Windows-Drucker bleibt bestehen – vorher „Aus Windows entfernen“, wenn er weg soll.`, "Löschen", true)) break;
        ST.s.inbox.printers.splice(+btn.dataset.i, 1); markDirty(); drawTab(); break;
      }
      case "xp-setup": {
        if (ST.dirty) throw new Error("Bitte zuerst speichern.");
        const r = await api("POST", "api/admin/drucker/einrichten?id=" + encodeURIComponent(btn.dataset.id));
        msg((r.ok ? "" : (r.message || "Fehler") + "\n") + (r.log || []).join("\n"), r.ok); loadPrinterStatus(); break;
      }
      case "xp-remove": {
        if (!await confirmDialog("Drucker aus Windows entfernen", `Drucker „${esc(btn.dataset.name)}“ und seinen Anschluss aus Windows entfernen?`, "Entfernen", true)) break;
        const r = await api("POST", `api/admin/drucker/entfernen?id=${encodeURIComponent(btn.dataset.id)}&name=${encodeURIComponent(btn.dataset.name)}`);
        msg((r.ok ? "" : (r.message || "Fehler") + "\n") + (r.log || []).join("\n"), r.ok); loadPrinterStatus(); break;
      }
      case "mapi-install": await mapiCall("installieren"); break;
      case "mapi-default":
        if (await confirmDialog("Standard-Mailprogramm", "Belegpost für alle Benutzer dieses Servers als Standard-Mailprogramm setzen – für MAPI-Mails und als Windows-Standard-App „E-Mail“? Das bisherige wird gemerkt und kann wiederhergestellt werden.", "Als Standard setzen"))
          await mapiCall("standard");
        break;
      case "mapi-restore": await mapiCall("wiederherstellen"); break;
      case "mapi-users":
        if (await confirmDialog("Standard je Benutzer", "Belegpost als eigenes Standard-Mailprogramm aller Windows-Benutzer dieses Servers setzen? Angemeldete Benutzer sofort, alle anderen bei ihrer nächsten Anmeldung. Der bisherige Wert wird gemerkt.", "Für alle Benutzer setzen"))
          await mapiCall("benutzer");
        break;
      case "user-mapi-push": {
        const u = ST.users.users[+btn.dataset.i];
        if (!u.id) throw new Error("Benutzer zuerst speichern.");
        if (!await confirmDialog("Einstellung überrollen", `Die MAPI-Vorgabe von ${esc(u.name)} („${esc(mapiText(u.mapiMode))}“) jetzt auf alle seine PCs übertragen? Der Benutzer kann sie danach im Melder wieder ändern.`, "Überrollen")) break;
        await api("POST", `api/melder/benutzer/${encodeURIComponent(u.id)}/mapi?alleGeraete=true`, { mode: u.mapiMode || "" });
        await loadDevices(); msg("Einstellung auf alle PCs übertragen.", true); break;
      }
      case "unit-add": ST.s.invoice.units[""] = ""; drawTab(); document.querySelector('.unit .key[value=""]')?.focus(); break;
      case "unit-del": delete ST.s.invoice.units[btn.dataset.key]; markDirty(); drawTab(); break;
      case "user-add":
        ST.users.users.push({ id: "", name: "", email: "", logins: [], printNames: [], operatorNumbers: [], visibility: "eigene", alsoSees: [], sendViaOutlook: false, canSend: true, isAdmin: false, active: true });
        ST.usersDirty = true; ST.userEdit = ST.users.users.length - 1; drawTab(); break;
      case "user-open": ST.userEdit = +e.target.closest("[data-st]").dataset.i; drawTab(); window.scrollTo(0, 0); break;
      case "user-back":
        if (ST.userRules?.dirty && !await confirmDialog("Ungespeicherte Regeln", "Die Änderungen an Signaturen/Regeln dieses Benutzers sind noch nicht gespeichert. Trotzdem zurück?", "Zurück")) break;
        if (ST.userRules?.dirty) ST.userRules = null;
        ST.userEdit = null; drawTab(); break;
      case "user-del": {
        const u = ST.users.users[+btn.dataset.i];
        if (await confirmDialog("Benutzer entfernen", `„${esc(u.name || u.email || "neuer Benutzer")}“ entfernen? Gespeichert wird mit „Speichern“.`, "Entfernen", true)) {
          ST.users.users.splice(+btn.dataset.i, 1); ST.usersDirty = true; ST.userEdit = null; drawTab();
        }
        break;
      }
      case "users-save": {
        const editedId = ST.userEdit != null ? ST.users.users[ST.userEdit]?.id : null, editedNew = ST.userEdit != null && !editedId;
        await api("PUT", "api/admin/users", { users: ST.users.users });
        for (const u of ST.users.users.filter(x => x.id && x._mapiChanged)) await api("POST", `api/melder/benutzer/${encodeURIComponent(u.id)}/mapi`, { mode: u.mapiMode || "" });
        if (ST.userRules?.dirty && !ST.userRules.error) {
          await api("PUT", `api/meine-regeln?user=${encodeURIComponent(ST.userRules.userId)}`, { set: ST.userRules.set });
          ST.userRules.dirty = false;
        }
        await loadUsers();
        if (editedNew) ST.userEdit = ST.users.users.length - 1; // neuer Benutzer: in der Maske bleiben
        else if (editedId) { const k = ST.users.users.findIndex(x => x.id === editedId); ST.userEdit = k >= 0 ? k : null; }
        drawTab();
        msg("Gespeichert – Änderungen gelten sofort.", true);
        break;
      }
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
    graphSecret: ST.graphSecret || null, clearGraphSecret: ST.clearGraphSecret,
    erpPassword: ST.erpPassword || null, clearErpPassword: ST.clearErpPassword });
  await loadSettings();
  if (r.restartRequired) restartHint("Gespeichert.");
  else msg("Gespeichert – die Änderungen sind sofort aktiv." + (r.warnings?.length ? "\nHinweise: " + r.warnings.join(" ") : ""), true);
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
      `<div class="grid2">${field("Name des Druckers", inText("inbox.printerName", "Belegpost"), "So heißt der Drucker am Server (und die Freigabe).")}
         <div>${inCheck("inbox.sharePrinter", "Im Netzwerk freigeben (für Druck von Arbeitsplätzen)")}</div></div>
       <div id="printerStatus"><p class="meta">Lade Status …</p></div>
       <p class="help">Der Drucker verwendet den Windows-Treiber <i>Microsoft Print To PDF</i> mit dem Anschluss <code>\\\\.\\pipe\\belegpost-druck</code> statt <code>PORTPROMPT:</code> –
         es erscheint kein Speichern-Dialog. Jeder Druck kommt als PDF beim Dienst an und wird zum Vorgang. Benutzer, Rechner und Dokumentname kommen aus der Druckwarteschlange.
         Nach Änderungen an Name oder Freigabe erst speichern, dann „Drucker einrichten“.</p>`) +
    section("Weitere Drucker", extraPrintersSection(), `<button class="btn ghost small" data-st="xp-add">+ Drucker</button>`) +
    section("Benutzer aus dem druckenden Prozess (WebWARE)",
      `${inCheck("inbox.processUserDetection", "ERP-Benutzer aus der Befehlszeile des druckenden Programms erkennen")}
       <div class="grid2">${field("Programme", inText("inbox.processNames", "wwa.exe"), "Mehrere mit Semikolon trennen.")}
         ${field("Benutzernummer ist Parameter Nr.", inNum("inbox.processUserArgument", 1, 20), "1 = erster Parameter nach dem Programm, z. B. <code>WWA.EXE <b>000</b> /M:10000 …</code>")}</div>
       ${inCheck("inbox.processFilterByName", "Nur in diesen Programmen mitschreiben (empfohlen; ausschalten, falls keine Aufrufe erkannt werden)")}
       <p class="help">Die WebWARE startet pro Benutzer eine <code>wwa.exe</code> – alle laufen als SYSTEM, der Druckauftrag sagt also nicht, wer gedruckt hat.
         Belegpost schreibt deshalb mit, wann welche <code>wwa.exe</code> die Druckwarteschlange aufruft (Windows-Ereignisablaufverfolgung), und ordnet den Druck
         der Benutzernummer aus deren Befehlszeile zu – wie <code>@@pb000@@</code>. <b>Steuerzeichen im Druck (<code>@@pb@@</code>, <code>@@pu@@</code>) haben Vorrang.</b>
         Drucken zwei Benutzer im selben Moment, bekommt der Vorgang eine Warnung.</p>
       <div id="printerOrigin"></div>`) +
    section("Weiterleitung an gotomaxx PDFMAILER", gotomaxxSection()) +
    section("gotomaxx-Protokoll einlesen und ans ERP melden", gotomaxxLogSection());
}

function gotomaxxLogSection() {
  ST.s.gotomaxxLog ||= {};
  const L = ST.s.gotomaxxLog;
  L.typeMap ||= [{ text: "Rechnung", code: "R" }, { text: "Lieferschein", code: "L" }, { text: "Auftrag", code: "A" }, { text: "Angebot", code: "N" }, { text: "Bestellung", code: "b" }, { text: "Gutschrift", code: "G" }];
  return `${inCheck("gotomaxxLog.enabled", "Protokolle von gotomaxx PDFMAILER laufend einlesen – Menügruppe „Gotomaxx“ mit jedem Drucker")}
    <p class="help">Belegpost liest alle <code>usage_*.dat</code> (SQLite) aus dem Protokollordner – immer aus einer Kopie, gotomaxx wird nicht verändert.
      Je Eintrag: Dokument, Windows-Benutzer, Rechner, ob gespeichert/gedruckt/gemailt wurde oder welcher Fehler auftrat.
      Belege, die Belegpost an gotomaxx übergeben hat, bekommen ihr Ergebnis im Vorgang (versendet bzw. Fehler 840) und melden über die ERP-Rückmeldung.
      Direkt aus dem ERP gedruckte Belege: Belegpost errät den Belegindex aus dem Dokumentnamen und prüft ihn im ERP (BELEG.GET).</p>
    <div class="grid3">
      ${field("Protokollordner", inText("gotomaxxLog.folder", "C:\\ProgramData\\gotomaxx\\pdfmailer6"), "Leer = C:\\ProgramData\\gotomaxx\\pdfmailer6")}
      ${field("Lesen alle … Sekunden", inNum("gotomaxxLog.intervalSeconds", 15, 3600))}
      ${field("Beim ersten Lesen … Tage zurück", inNum("gotomaxxLog.importDays", 1, 365))}
    </div>
    <h4>Benutzer informieren</h4>
    ${inCheck("gotomaxxLog.notifyUsers", "Nicht versendete Belege dem Benutzer melden, der gedruckt hat (Melder und Portal)")}
    ${inCheck("gotomaxxLog.notifyUserMail", "zusätzlich per E-Mail an den Benutzer")}
    <p class="help">Der Windows-Benutzer aus dem Protokoll wird über „Zuordnung gedruckter Belege“ beim Benutzer zugeordnet (Windows-/ERP-Benutzer bzw. Anmeldename).
      Benutzer ohne Recht „Ausgang sehen“ sehen unter Gotomaxx nur ihre eigenen (und freigegebenen) Drucke.</p>
    <h4>ERP-Rückmeldung für direkt gedruckte Belege</h4>
    ${inCheck("gotomaxxLog.erpFeedback", "Ergebnis ans ERP melden (versendet bzw. gotomaxx-Fehler, Felder wie in der ERP-Rückmeldung)")}
    ${inCheck("gotomaxxLog.lookupErp", "Belegindex aus dem Dokumentnamen erraten und im ERP prüfen")}
    <div class="grid3">
      ${field("Anfang des Belegindex", inText("gotomaxxLog.indexPrefix", "0N"), "Wenn er nicht im Dokumentnamen steht, z. B. „(0NA)“.")}
      ${field("Suchreihenfolge der Belegart", inText("gotomaxxLog.searchOrder", "R, L, A, N, b"), "Wenn die Belegart nicht im Namen steht: Rechnung, Lieferschein, Auftrag, Angebot, Bestellung.")}
      ${field("Stellen der Belegnummer", inNum("gotomaxxLog.numberWidth", 0, 20), "Kürzere Nummern rechtsbündig mit Leerzeichen, z. B. „0NR 1234567“ (Standard 8).")}
      ${field("Mandant", inText("gotomaxxLog.mandant", "leer = allgemeine ERP-Verbindung"))}
    </div>
    ${field("Belegart und -nummer im Dokumentnamen (regulärer Ausdruck, Gruppen art und nr)", inText("gotomaxxLog.titlePattern", "Standard"), "Leer = Standard, z. B. „WA-Auftrag (alle Belege) - 20241001 (0NA)“ → Nummer 20241001, Index 0NA20241001.")}
    <div class="grid3">${L.typeMap.map((m, i) => `${field(i ? "" : "Wort im Namen", inText(`gotomaxxLog.typeMap.${i}.text`, "Rechnung"))}${field(i ? "" : "Belegart", inText(`gotomaxxLog.typeMap.${i}.code`, "R"))}<div></div>`).join("")}</div>
    <div class="actions"><button class="btn ghost" data-st="gx-read">Jetzt lesen</button></div>`;
}

function extraPrintersSection() {
  const X = (ST.s.inbox.printers ||= []);
  const mand = (ST.s.mandanten?.items || []).filter(m => m.number);
  const bps = (ST.s.stationery?.items || []).map(b => b.name).filter(Boolean);
  const sel = (path, cur, opts) => `<select data-path="${path}">${opts.map(([v, t]) => `<option value="${esc(v)}" ${cur === v ? "selected" : ""}>${esc(t)}</option>`).join("")}</select>`;
  return `<p class="help">Jeder weitere Drucker bekommt einen eigenen Anschluss. Optional legst du je Drucker einen <b>Mandanten</b> und ein <b>Briefpapier</b> fest –
      z. B. „Belegpost Firma B“ mit Mandant 2 und Briefpapier „Firma B“. Vorrang hat immer, was im Druck steht: <code>@@pm@@</code> und
      <code>/M:</code> aus wwa.exe vor dem Mandanten des Druckers, <code>@@bp@@</code> vor seinem Briefpapier. Benutzer- und Prozesserkennung gelten wie beim Hauptdrucker.</p>
    ${X.length ? `<div class="table-wrap"><table class="list mini xprinters"><thead><tr><th>Name des Druckers</th><th>Freigeben</th><th>Mandant</th><th>Briefpapier</th><th>Status</th><th></th></tr></thead><tbody>
      ${X.map((x, i) => { const P = `inbox.printers.${i}`;
        const mOpts = [["", "–"], ...mand.map(m => [m.number, m.name ? `${m.number} – ${m.name}` : m.number])];
        if (x.mandant && !mand.some(m => m.number === x.mandant)) mOpts.push([x.mandant, x.mandant + " (nicht angelegt)"]);
        const bOpts = [["", "Standard"], ["-", "ohne Briefpapier"], ...bps.map(b => [b, b])];
        if (x.stationery && x.stationery !== "-" && !bps.includes(x.stationery)) bOpts.push([x.stationery, x.stationery + " (nicht vorhanden)"]);
        return `<tr><td>${inText(`${P}.name`, "Belegpost Firma B")}</td>
          <td><input type="checkbox" data-path="${P}.share" ${x.share !== false ? "checked" : ""}></td>
          <td>${sel(`${P}.mandant`, x.mandant || "", mOpts)}</td>
          <td>${sel(`${P}.stationery`, x.stationery || "", bOpts)}</td>
          <td class="xp-status" data-xp="${esc(x.id || "")}">${x.id ? '<span class="meta-inline">…</span>' : '<span class="meta-inline">neu – erst speichern</span>'}</td>
          <td class="nowrap">${x.id ? `<button class="btn ghost small" data-st="xp-setup" data-id="${esc(x.id)}">Einrichten</button>
              <button class="btn ghost small" data-st="xp-remove" data-id="${esc(x.id)}" data-name="${esc(x.name || "")}" title="Drucker und Anschluss aus Windows entfernen">Aus Windows entfernen</button>` : ""}
            <button class="icon-btn" data-st="xp-del" data-i="${i}" title="Aus der Liste löschen">✕</button></td></tr>`; }).join("")}
      </tbody></table></div>` : `<p class="meta">Keine weiteren Drucker – alle Drucke laufen über den Hauptdrucker.</p>`}`;
}

function gotomaxxSection() {
  const printers = ST.data.printers || [];
  const current = get(ST.s, "gotomaxx.printer") || "";
  const opts = [["", printers.length ? "(bitte wählen)" : "(keine Drucker gefunden)"], ...printers.map(p => [p, p])];
  if (current && !printers.includes(current)) opts.push([current, current + " (nicht installiert)"]);
  return `${inCheck("gotomaxx.enabled", "Belege an einen gotomaxx-PDFMAILER-Drucker weiterleiten")}
    <div class="grid2">
      ${field("Standarddrucker", inSelect("gotomaxx.printer", opts), "Für <code>@@pg@@</code> und „jeden Beleg weiterleiten“. <code>@@pgDruckername@@</code> wählt je Beleg einen anderen gotomaxx-Drucker.")}
      ${field("Höchstdauer eines Drucks (Sekunden)", inNum("gotomaxx.timeoutSeconds", 10, 900))}
    </div>
    ${inCheck("gotomaxx.always", "Jeden Beleg weiterleiten (außer mit <code>@@pg0@@</code>) – sonst nur Belege mit <code>@@pg@@</code>")}
    ${inCheck("gotomaxx.userSession", "In der Windows-Sitzung des Benutzers am Server drucken (empfohlen – gotomaxx verarbeitet den Auftrag dann wie einen Druck aus dem ERP)")}
    ${inCheck("gotomaxx.serviceFallback", "Ist der Benutzer nicht angemeldet, druckt der Dienst selbst")}
    <div class="grid2">
      ${field("Schriftgröße der Steuerzeichen (pt)", inNum("gotomaxx.codeFontSize", 0.5, 24, 0.1), "Wie im ERP-Formular, z. B. 2.")}
      <div>${inCheck("gotomaxx.codesVisible", "Steuerzeichen schwarz drucken (zum Testen, sonst weiß)")}</div>
    </div>
    <div class="help-box">
      <b>So funktioniert die Weiterleitung:</b> Der Beleg wird als echter Druckauftrag an den gotomaxx-Drucker geschickt.
      Die Steuerzeichen werden dafür auf derselben Seite neu als Text gedruckt –
      <b>ohne</b> die Belegpost-eigenen <code>@@pb@@ @@pi@@ @@pu@@ @@pv@@ @@pt@@ @@pf@@ @@pg@@</code> und <code>@@xi ERP_VOUCHER_INDEX=…@@</code>,
      alle anderen unverändert (Pfade wie <code>K:\…</code> ohne Pfadzuordnung). Briefpapier, Prüfung und Versand übernimmt dann gotomaxx.
      Status 140 = an gotomaxx übergeben, 840 = Übergabe fehlgeschlagen. Im Vorgang gibt es zusätzlich den Knopf „An gotomaxx übergeben“.
    </div>`;
}

async function loadPrinterStatus() {
  const box = $("#printerStatus"); if (!box) return;
  const dt = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" }) : "";
  try {
    const r = await api("GET", "api/admin/drucker");
    if (!r.available) { box.innerHTML = `<p class="meta">Nur unter Windows verfügbar.</p>`; return; }
    const s = r.status || {};
    const ok = s.exists && s.port === r.port && /print to pdf/i.test(s.driver || "");
    box.innerHTML = `<table class="kv">
        <tr><th>Drucker „${esc(r.name)}“</th><td>${s.exists ? (ok ? "<b>eingerichtet</b>" : `<span class="bad">vorhanden, aber Treiber/Anschluss passen nicht (${esc(s.driver || "?")} / ${esc(s.port || "?")})</span>`) : "nicht vorhanden"}${s.problem ? ` <span class="bad">${esc(s.problem)}</span>` : ""}</td></tr>
        <tr><th>Freigabe</th><td>${s.shared ? `\\\\${esc(location.hostname)}\\${esc(s.shareName || r.name)}` : "nicht freigegeben"}</td></tr>
        <tr><th>Treiber „Microsoft Print To PDF“</th><td>${s.driverInstalled ? "installiert" : '<span class="bad">fehlt</span>'}</td></tr>
        <tr><th>Annahme (${esc(r.port)})</th><td>${r.listening ? "bereit" : '<span class="bad">nicht bereit</span>'}</td></tr></table>
      <div class="actions"><button class="btn" data-st="printer-setup">${s.exists ? "Drucker reparieren" : "Drucker einrichten"}</button>
        <button class="btn ghost" data-st="printer-refresh">Aktualisieren</button></div>
      ${(r.recent || []).length ? `<h4>Letzte Drucke</h4><div class="table-wrap"><table class="list mini"><thead><tr><th>Zeit</th><th>Auftrag</th><th>Dokument</th><th>Benutzer / Rechner</th><th>Benutzer erkannt</th><th>Vorgang</th></tr></thead><tbody>
        ${r.recent.map(e => `<tr><td class="nowrap">${dt(e.at)}</td><td>${esc(e.jobId ?? "")}</td><td>${esc(e.document || "")}</td><td>${esc(e.user || "")}${e.machine ? " @ " + esc(e.machine) : ""}</td>
          <td>${esc(e.origin || "")}</td><td>${e.vorgangId ? `<a href="#/vorgang?id=${encodeURIComponent(e.vorgangId)}">öffnen</a>` : `<span class="bad">${esc(e.problem || "")}</span>`}</td></tr>`).join("")}</tbody></table></div>` : ""}`;
    for (const x of r.printers || []) {
      const cell = document.querySelector(`.xp-status[data-xp="${CSS.escape(x.id)}"]`); if (!cell) continue;
      const st = x.status || {};
      const good = st.exists && st.port === x.port && /print to pdf/i.test(st.driver || "");
      cell.innerHTML = !st.exists ? '<span class="meta-inline">nicht eingerichtet</span>'
        : good ? `eingerichtet${st.shared ? ", freigegeben" : ""}${x.listening ? "" : ' · <span class="bad">Annahme nicht bereit</span>'}`
        : `<span class="bad">Treiber/Anschluss passen nicht</span>`;
    }
    const t = r.tracker || {}, ob = $("#printerOrigin");
    if (ob) ob.innerHTML = `<p class="meta">Prozesserkennung: ${t.running ? "<b>läuft</b>" : `<span class="bad">${esc(t.problem || "aus")}</span>`}</p>
      ${(t.calls || []).length ? `<div class="table-wrap"><table class="list mini"><thead><tr><th>Zeit</th><th>Prozess</th><th>Benutzer-Nr.</th><th>Aufruf</th><th>Befehlszeile</th></tr></thead><tbody>
        ${t.calls.map(c => `<tr><td class="nowrap">${dt(c.at)}</td><td>${esc(c.exe)} (${c.pid})</td><td>${esc(c.operator ?? "–")}</td><td><small>${esc(c.endpoint || c.iface)}</small></td><td><small>${esc(c.commandLine || "")}</small></td></tr>`).join("")}
        </tbody></table></div>` : `<p class="meta">Noch keine Druckaufrufe der überwachten Programme gesehen.</p>`}
      ${(t.interfaces || []).length ? `<details><summary>Erkannte Aufrufe (Diagnose)</summary><table class="list mini"><tbody>${t.interfaces.map(x => `<tr><td><small>${esc(x.iface)}</small></td><td><small>${esc(x.endpoint)}</small></td><td>${x.count}</td></tr>`).join("")}</tbody></table></details>` : ""}`;
  } catch (e) { box.innerHTML = `<p class="bad">${esc(e.message)}</p>`; }
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
       ${field("Mailtext (wenn kein @@sa@@ gedruckt wurde)", rteHtml(get(ST.s, "mail.defaultBody"), `data-path="mail.defaultBody"`))}
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
  if (ST.mtEdit != null && T.items[ST.mtEdit]) return templateMask(ST.mtEdit);
  ST.mtEdit = null;
  const ph = (ST.data.placeholders || []).map(p => `<code>${esc(p)}</code>`).join(" ");
  const rules = ST.s.mailRules?.rules || [];
  const usedBy = name => rules.filter(r => r.template && name && r.template.toLowerCase() === name.toLowerCase()).length;
  const rows = T.items.map((t, i) => `<tr class="clickable" data-st="mt-open" data-i="${i}">
      <td class="mt-thumb">${t.logoFile ? `<img src="api/admin/mailtemplates/logo/${encodeURIComponent(t.logoFile)}" alt="">` : `<span class="mt-swatch" style="background:${esc(t.primaryColor || "#1f5fbf")}"></span>`}</td>
      <td><b>${esc(t.name || "(ohne Namen)")}</b>${T.default && T.default === t.name ? ' <span class="badge ok">Standard</span>' : ""}</td>
      <td>${t.kind === "html" ? "eigenes HTML" : "Baukasten"}</td>
      <td>${esc((t.stationeries || []).join(", ") || "–")}</td>
      <td>${esc([...(t.senders || []), ...(t.fromAddress ? [t.fromAddress] : [])].filter((x, k, a) => a.indexOf(x) === k).join(", ") || "–")}</td>
      <td class="nowrap">${t.subject || t.body ? '<span class="badge">eigener Text</span>' : ""}${usedBy(t.name) ? ` <span class="badge remote" title="Wird von Regeln gewählt">${usedBy(t.name)} Regel${usedBy(t.name) > 1 ? "n" : ""}</span>` : ""}</td>
      <td class="nowrap"><button class="btn ghost small" data-st="mt-open" data-i="${i}">Bearbeiten</button></td></tr>`).join("");
  return section("Mail-Vorlagen",
    `${T.items.length ? `<div class="table-wrap"><table class="list users-compact"><thead><tr><th></th><th>Name</th><th>Art</th><th>Briefpapier</th><th>Absender</th><th></th><th></th></tr></thead>
       <tbody>${rows}</tbody></table></div>` : `<p class="meta">Noch keine Vorlage – Mails gehen als schlichte Mail ohne Gestaltung hinaus.</p>`}`,
    `<button class="btn ghost" data-st="mt-add">+ Vorlage</button>`) +
  section("So wird die Vorlage gewählt",
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
    "");
}

/** Eigene Maske für eine Mail-Vorlage. */
function templateMask(i) {
  const T = ST.s.mailTemplates;
  const t = T.items[i];
  const bps = (ST.s.stationery?.items || []).map(b => b.name).filter(Boolean);
  const ph = (ST.data.placeholders || []).map(p => `<code>${esc(p)}</code>`).join(" ");
  const P = `mailTemplates.items.${i}`;
  const logo = t.logoFile
    ? `<img class="mt-logo" src="api/admin/mailtemplates/logo/${encodeURIComponent(t.logoFile)}" alt="Logo">`
    : '<span class="muted">kein Logo</span>';
  const bpChecks = bps.length
    ? bps.map(n => `<label class="check"><input type="checkbox" data-mtbp="${i}" value="${esc(n)}" ${(t.stationeries || []).some(x => x.toLowerCase() === n.toLowerCase()) ? "checked" : ""}> ${esc(n)}</label>`).join("")
    : '<span class="muted">Noch keine Briefpapiere angelegt.</span>';
  const html = t.kind === "html";
  return `<div class="user-mask-head"><button class="btn ghost" data-st="mt-back">← Alle Vorlagen</button>
      <h2>${esc(t.name || "Neue Vorlage")}</h2>${T.default && T.default === t.name ? '<span class="badge ok">Standard</span>' : ""}
      <span class="spacer"></span>
      <button class="btn ghost" data-st="mt-copy" data-i="${i}">Kopieren</button>
      <button class="btn ghost danger" data-st="mt-del" data-i="${i}">Entfernen</button>
      <button class="btn" data-st="mt-preview" data-i="${i}">Vorschau</button></div>` + section("Mail-Vorlage",
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
     ${field("Signatur", rteHtml(t.signature, `data-path="${P}.signature"`, { small: true }),
        "Formatiert wie in Outlook: Schrift, Farbe, Links und Bilder. Aus Outlook/Word einfach kopieren und einfügen – überflüssige Formatierungen werden entfernt. Eigene Signaturen je Benutzer, Briefpapier oder Absender unter „Regeln &amp; Signaturen“.")}
     ${field("Fußzeile (Impressum, klein und grau)", rteHtml(t.footer, `data-path="${P}.footer"`, { small: true }))}
     ${html ? `${field("Eigenes HTML", `<textarea class="mono" data-path="${P}.customHtml" rows="14">${esc(t.customHtml || "")}</textarea>`,
          "Pflicht: <code>{Inhalt}</code>. Außerdem: <code>{Logo}</code> <code>{Belegbox}</code> <code>{Signatur}</code> <code>{Fusszeile}</code> <code>{Kopfzeile}</code> <code>{Farbe}</code> und alle Platzhalter unten. Inline-Styles und Tabellen verwenden – Outlook ignoriert &lt;style&gt; und CSS-Layouts weitgehend.")}
        ${t.customHtml ? "" : `<div class="actions"><button class="btn ghost" data-st="mt-html" data-i="${i}">Beispiel-HTML einsetzen</button></div>`}` : ""}

     <details ${t.subject || t.body ? "open" : ""}><summary>Eigener Betreff und Mailtext für diese Vorlage</summary>
       <p class="help">Leer = Vorlagen aus „E-Mail &amp; Versand“. Was das ERP mit <code>@@ed@@</code>, <code>@@sa@@</code> oder <code>@@mb@@</code> druckt, hat immer Vorrang.</p>
       ${field("Betreff", inText(`${P}.subject`, "z. B. {Belegart} {Belegnummer} – Firma GmbH"))}
       ${field("Mailtext", rteHtml(t.body, `data-path="${P}.body"`))}
     </details>

     <div class="actions"><button class="btn" data-st="mt-preview" data-i="${i}">Vorschau</button>
       <span class="meta-inline">mit Beispieldaten – auch vor dem Speichern</span></div>
     <div id="mtPreview${i}"></div>`,
    "");
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
  box.scrollIntoView({ behavior: "smooth", block: "start" });
}

/* ---------------- Tab: E-Rechnung ---------------- */

/** Eigene Stufe je Prüfregel (E-Rechnung) – z. B. eine Regel, die ein Empfänger nicht verlangt, als Warnung führen. */
function ruleLevelsSection() {
  ST.s.validation ||= {}; ST.s.validation.ruleLevels ||= [];
  const L = ST.s.validation.ruleLevels;
  return section("Prüfregeln anpassen (E-Rechnung)",
    `<p class="help">Gilt für „Dokument validieren“, den Eingang und die Prüfung vor dem Senden. Die Regelkennung steht im Prüfbericht in der Spalte „Regel“ (z. B. BR-DE-21, BR-CO-15).
       Fehler führen zur Empfehlung „ablehnen“ – eine auf Warnung gesetzte Regel nicht mehr. Bitte nur bewusst einsetzen: die Regeln selbst bleiben unverändert.</p>
     ${L.map((x, i) => `<div class="grid3">
        ${field("Regel", inText(`validation.ruleLevels.${i}.rule`, "BR-DE-21"))}
        ${field("Stufe", inSelect(`validation.ruleLevels.${i}.level`, [["warnung", "Warnung"], ["info", "Hinweis"], ["fehler", "Fehler"], ["aus", "aus (nicht anzeigen)"]]))}
        <div class="field"><span class="lbl">Notiz</span><span style="display:flex;gap:8px">${inText(`validation.ruleLevels.${i}.note`, "warum?")}
          <button class="btn ghost small danger" data-st="rl-del" data-i="${i}">Entfernen</button></span></div></div>`).join("")}
     <div class="actions"><button class="btn ghost" data-st="rl-add">Regel hinzufügen</button></div>`);
}

function tabInvoice() {
  const units = Object.entries(ST.s.invoice.units || {}).sort((a, b) => a[0].localeCompare(b[0], "de"));
  return section("Prüfung",
    `<div class="grid2">${field("Toleranz der Summenprüfung", inNum("invoice.sumTolerance", 0, 10, 0.01), "Abweichung in Belegwährung, ab der die Summenprüfung einen Fehler meldet (Standard 0,02).")}</div>`) +
    ruleLevelsSection() +
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

/* ---------------- Tab: ERP Suite Connect ---------------- */

function tabConnect() {
  ST.s.connect ||= {};
  return section("ERP Suite Connect (Webshop-Schnittstelle)",
    `${inCheck("connect.enabled", "Schnittstelle ist aktiv – Menüpunkt „ERP Suite Connect“ anzeigen")}
     <p class="help">Die Verarbeitung der Dateien macht ERP Suite Connect selbst. Belegpost zeigt die Ordner an und prüft jede Datei gegen die Schnittstellenbeschreibung.
       Abgerufene Connect-Dateien (z. B. über einen Versandweg mit Abruf in den Eingangsordner) erscheinen unter ERP Suite Connect; im allgemeinen Eingang nur die Bestellungen (ORDER_…).</p>
     <div class="grid2">
       ${field("Eingang: Ordner, aus dem ERP Suite Connect liest", inText("connect.inFolder", "C:\\ERPSuiteConnect\\Import"), "Dateien vom Webshop: ORDER_, USERS_, PRODUCTS_ …")}
       ${field("Eingang: verarbeitete Dateien (Archiv)", inText("connect.inArchive", "optional"), "Archivordner von ERP Suite Connect – leer = nicht anzeigen.")}
       ${field("Ausgang: Ordner, in den das ERP schreibt", inText("connect.outFolder", "C:\\ERPSuiteConnect\\Export"), "Dateien für den Webshop: Artikelstamm_, Lagerbestand_, Orderstatus_, Adressen_, Kategorie_, Gutscheine_.")}
       ${field("Ausgang: gesendete Dateien (Archiv)", inText("connect.outArchive", "optional"))}
     </div>
     <p class="help">Werden die Dateien über einen Versandweg aus dem Ausgangsordner gesendet, prüft Belegpost sie vorher („Vor dem Senden prüfen“) und hält fehlerhafte zurück.</p>`);
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
  if (ST.userEdit != null && U.users[ST.userEdit]) return userMask(ST.userEdit);
  ST.userEdit = null;
  const fmt = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "–";
  const vis = u => u.isAdmin ? "alle" : ({ eigene: "eigene", auswahl: "eigene + Auswahl", alle: "alle" }[u.visibility || "eigene"]);
  const devs = u => (ST.devices || []).filter(d => d.userId === u.id);
  return section("Benutzer",
    `${U.users.length ? `<div class="table-wrap"><table class="list users-compact"><thead><tr>
        <th>Name</th><th title="ERP-Bedienernummer">ERP-Nr.</th><th>E-Mail</th><th>Rechte</th><th>Sieht</th><th>Melder</th><th>Letzte Anmeldung</th><th></th></tr></thead><tbody>
      ${U.users.map((u, i) => `<tr class="clickable ${u.active ? "" : "inactive"}" data-st="user-open" data-i="${i}">
        <td><b>${esc(u.name || "(ohne Namen)")}</b>${u.active ? "" : ' <span class="badge">inaktiv</span>'}${u.id ? "" : ' <span class="badge partial">neu</span>'}</td>
        <td class="mono">${esc((u.operatorNumbers || []).join(", ") || "–")}</td>
        <td>${esc(u.email || "–")}</td>
        <td class="nowrap">${[u.isAdmin ? '<span class="badge ok">Admin</span>' : "", u.canSend || u.isAdmin ? '<span class="badge">Versenden</span>' : '<span class="badge">nur ansehen</span>', u.sendViaOutlook ? '<span class="badge remote">Outlook</span>' : ""].join(" ")}</td>
        <td>${esc(vis(u))}</td>
        <td class="nowrap">${devs(u).length ? `${devs(u).length} PC${devs(u).length > 1 ? "s" : ""}` : '<span class="meta-inline">–</span>'}</td>
        <td class="nowrap meta-inline">${fmt(u.lastLogin)}</td>
        <td class="nowrap"><button class="btn ghost small" data-st="user-open" data-i="${i}">Bearbeiten</button></td></tr>`).join("")}
      </tbody></table></div>` : `<p class="meta">Noch keine Benutzer. Bis dahin ist nur die Anmeldung als Administrator direkt am Server möglich.</p>`}
     <div class="actions"><button class="btn" data-st="users-save" ${ST.usersDirty ? "" : "disabled"}>Benutzer speichern</button></div>`,
    `<button class="btn ghost" data-st="user-add">+ Benutzer</button>`);
}

/** Eigene Maske für einen Benutzer: Person, Rechte, Zuordnung, Sicht, MAPI/Melder, Signaturen und Regeln. */
function userMask(i) {
  const U = ST.users, u = U.users[i];
  const fmt = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "–";
  const inp = (k, val, ph = "", extra = "") => `<input data-u="${i}|${k}" value="${esc(val ?? "")}" placeholder="${esc(ph)}" ${extra}>`;
  const chk = (k, label, help) => `<label class="check"><input type="checkbox" data-u="${i}|${k}" ${u[k] ? "checked" : ""}> ${label}${help ? ` <small class="meta-inline">– ${help}</small>` : ""}</label>`;
  const rules = ST.userRules?.userId === u.id ? ST.userRules : null;
  let rulesHtml;
  if (!u.id) rulesHtml = `<p class="meta">Signaturen und Regeln gibt es, sobald der Benutzer gespeichert ist.</p>`;
  else if (!rules) { loadUserRules(u.id); rulesHtml = `<p class="meta">Lade …</p>`; }
  else if (rules.error) rulesHtml = `<div class="notice">${esc(rules.error)}</div>`;
  else rulesHtml = ruleEditorHtml(ruleEditor("user", {
    set: rules.set, choices: { ...rules.choices, outlook: u.sendViaOutlook }, global: false,
    onChange: () => { rules.dirty = true; markUsersDirty(); }, redraw: () => drawTab()
  }));

  return `<div class="user-mask-head"><button class="btn ghost" data-st="user-back">← Alle Benutzer</button>
      <h2>${esc(u.name || "Neuer Benutzer")}</h2>${u.active ? "" : '<span class="badge">inaktiv</span>'}
      <span class="spacer"></span>
      <button class="btn ghost danger" data-st="user-del" data-i="${i}">Entfernen</button>
      <button class="btn" data-st="users-save" ${ST.usersDirty ? "" : "disabled"}>Speichern</button></div>` +
    section("Person und Anmeldung",
      `<div class="grid2">
         ${field("Name", inp("name", u.name, "Vorname Nachname"))}
         ${field("E-Mail (Anmeldename)", inp("email", u.email, "name@firma.at"), "Anmeldung mit dem Microsoft-365-Konto.")}
         ${field("Weitere Anmeldenamen", inp("logins", (u.logins || []).join(", "), "optional"), "Falls die Anmeldung (UPN) von der E-Mail abweicht. Mehrere mit Komma.")}
         <div class="plain-field"><span class="lbl">Letzte Anmeldung</span><span>${fmt(u.lastLogin)}</span></div>
       </div>
       ${chk("active", "Aktiv", "abgewählt = keine Anmeldung")}`) +
    section("Rechte",
      `${chk("canSend", "Versenden", "darf Versanddaten ändern, senden und verwerfen; sonst nur ansehen")}
       ${chk("sendViaOutlook", "Über Outlook senden", "„Freigeben & in Outlook senden“ öffnet die fertige Mail über den Melder; seine Belege werden nicht automatisch versendet")}
       ${chk("isAdmin", "Administrator", "zusätzlich Einstellungen; sieht alle Belege")}`) +
    section("Versand über den Server – Absender und Antwortadresse",
      `<div class="table-wrap"><table class="list sender-table"><thead><tr><th>ERP-Nr.</th><th>Absenderadresse</th><th>Absendername</th><th>Antwort an</th></tr></thead><tbody>
         <tr><td><b>Vorgabe</b><br><small class="meta-inline">alle Nummern</small></td>
           <td><input data-us="${i}||fromAddress" value="${esc(u.sendFromAddress || "")}" placeholder="${esc(ST.s.mail?.fromAddress || "aus Vorlage")}"></td>
           <td><input data-us="${i}||fromName" value="${esc(u.sendFromName || "")}" placeholder="aus Vorlage"></td>
           <td><input data-us="${i}||replyTo" value="${esc(u.replyTo || "")}" placeholder="aus Vorlage"></td></tr>
         ${(u.operatorNumbers || []).map(no => { const x = (u.operatorSenders || []).find(o => o.operator === no) || {};
           return `<tr><td class="mono"><b>${esc(no)}</b></td>
             <td><input data-us="${i}|${esc(no)}|fromAddress" value="${esc(x.fromAddress || "")}" placeholder="${esc(u.sendFromAddress || "wie Vorgabe")}"></td>
             <td><input data-us="${i}|${esc(no)}|fromName" value="${esc(x.fromName || "")}" placeholder="${esc(u.sendFromName || "wie Vorgabe")}"></td>
             <td><input data-us="${i}|${esc(no)}|replyTo" value="${esc(x.replyTo || "")}" placeholder="${esc(u.replyTo || "wie Vorgabe")}"></td></tr>`; }).join("")}
       </tbody></table></div>
       <p class="help">Gilt beim Autoversand und bei „Senden“ im Portal für Belege dieses Benutzers – je nach ERP-Nummer, mit der gedruckt wurde (<code>@@pb…@@</code> bzw. wwa.exe);
         leere Felder übernehmen die Vorgabe, diese die Mail-Vorlage bzw. den Standard. Vorrang haben <code>@@pf@@</code> im Druck und passende Mail-Regeln.
         Weitere ERP-Nummern unter „Zuordnung gedruckter Belege“ eintragen – sie erscheinen dann hier.
         Bei Microsoft 365 muss die App für jede Adresse senden dürfen („Senden als“ bzw. Application Access Policy); bei SMTP muss der Server den Absender erlauben.</p>`) +
    section("Zuordnung gedruckter Belege",
      `<div class="grid2">
         ${field("ERP-Bedienernummer(n)", inp("operatorNumbers", (u.operatorNumbers || []).join(", "), "007", 'inputmode="numeric" class="erp-no"'),
           "Dreistellig; das ERP druckt sie als <code>@@pb007@@</code>, bei der WebWARE kommt sie aus dem Prozess (<code>wwa.exe 007 …</code>). Mehrere mit Komma.")}
         ${field("Windows-/ERP-Benutzer (Druckauftrag)", inp("printNames", (u.printNames || []).join(", "), "z. B. FIRMA\\mmuster"),
           "Aus dem Druckauftrag oder <code>@@pu…@@</code>. Ohne Eintrag wird der Anmeldename ohne Domäne verglichen.")}
       </div>`) +
    section("Sichtbarkeit",
      `${field("Sieht Belege", `<select data-u="${i}|visibility" ${u.isAdmin ? "disabled" : ""}>
          ${[["eigene", "nur eigene"], ["auswahl", "eigene + ausgewählte Benutzer"], ["alle", "alle"]].map(([k, t]) =>
            `<option value="${k}" ${(u.isAdmin ? "alle" : (u.visibility || "eigene")) === k ? "selected" : ""}>${t}</option>`).join("")}</select>`,
          "Admins sehen immer alle; Belege ohne zugeordneten Benutzer sehen nur „alle“ und Admins.")}
       <div class="user-also"><span>Datenaustausch (Dateien, die nicht der Benutzer selbst gedruckt hat):</span>
         ${[["seeInbound", "Eingang sehen", "abgerufene und empfangene Dateien, ERP Suite Connect Eingang"], ["seeOutbound", "Ausgang sehen", "aus Ausgangsordnern gesendete Dateien, ERP Suite Connect Ausgang"]].map(([k, t, h]) =>
           `<label class="check" title="${h}"><input type="checkbox" data-u="${i}|${k}" ${u.isAdmin || (u[k] ?? u.visibility === "alle") ? "checked" : ""} ${u.isAdmin ? "disabled" : ""}> ${t} <small class="meta-inline">${h}</small></label>`).join("")}
       </div>
       ${!u.isAdmin && u.visibility === "auswahl" ? `<div class="user-also"><span>zusätzlich die Belege von:</span>
          ${U.users.filter(o => o !== u && o.id).map(o => `<label class="check"><input type="checkbox" data-also="${i}" value="${esc(o.id)}" ${(u.alsoSees || []).includes(o.id) ? "checked" : ""}> ${esc(o.name || o.email)}</label>`).join("") || '<span class="meta-inline">(neue Benutzer erst speichern)</span>'}</div>` : ""}`) +
    section("MAPI-Mails und Melder",
      `${field("MAPI-Mails (Vorgabe)", `<select data-u="${i}|mapiMode">
          <option value="" ${!u.mapiMode ? "selected" : ""}>Standard (${esc(mapiText(ST.s.mapi?.defaultMode || "outlook"))})</option>
          ${(ST.data.mapiModes || []).map(m => `<option value="${m.key}" ${u.mapiMode === m.key ? "selected" : ""}>${esc(m.text)}</option>`).join("")}</select>`)}
       <div class="user-devices">${userDevices(u, i)}</div>`) +
    section("Signaturen und Mail-Regeln", rulesHtml);
}

async function loadUserRules(id) {
  try { const r = await api("GET", `api/meine-regeln?user=${encodeURIComponent(id)}`); ST.userRules = { userId: id, set: r.set, choices: r.choices, dirty: false }; }
  catch (e) { ST.userRules = { userId: id, error: e.message }; }
  if (ST.tab === "benutzer") drawTab();
}

/* ---------------- Tab: Regeln & Signaturen (global) ---------------- */

function tabRules() {
  ST.s.mailRules ||= { signatures: [], rules: [] };
  if (!ST.users) loadUsers();
  const ed = ruleEditor("global", {
    set: ST.s.mailRules, global: true,
    users: (ST.users?.users || []).filter(u => u.id).map(u => ({ id: u.id, name: u.name || u.email })),
    choices: {
      templates: (ST.s.mailTemplates?.items || []).map(t => t.name).filter(Boolean),
      stationeries: (ST.s.stationery?.items || []).map(t => t.name).filter(Boolean),
      globalSignatures: [], gotomaxx: !!ST.s.gotomaxx?.enabled, printers: ST.data.printers || [], defaultSender: ST.s.mail?.fromAddress
    },
    onChange: () => markDirty(), redraw: () => drawTab()
  });
  return section("Regeln und Signaturen für alle",
    `<p class="help">Regeln legen fest, welche Mail-Vorlage, welcher Absender, welche Signatur und welcher Versandweg für einen Beleg gelten – abhängig von Belegart, Briefpapier, Absender, Empfänger, Kunde usw.
      Jeder Benutzer kann zusätzlich eigene Signaturen und Regeln anlegen (Persönlich → Meine Mail-Regeln; als Admin auch unter Benutzer). Eigene Regeln gelten vor diesen.</p>
     ${ruleEditorHtml(ed)}`);
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
  if (el.dataset.us !== undefined) {
    const [i, no, f] = el.dataset.us.split("|"), u = ST.users.users[+i];
    if (!no) u[{ fromAddress: "sendFromAddress", fromName: "sendFromName", replyTo: "replyTo" }[f]] = el.value;
    else {
      u.operatorSenders ||= [];
      let x = u.operatorSenders.find(o => o.operator === no);
      if (!x) { x = { operator: no, fromAddress: "", fromName: "", replyTo: "" }; u.operatorSenders.push(x); }
      x[f] = el.value;
    }
    markUsersDirty(); return true;
  }
  if (!el.dataset.u) return false;
  const [i, k] = el.dataset.u.split("|"), u = ST.users.users[+i];
  if (k === "operatorNumbers") {
    u.operatorNumbers = el.value.split(/[,;\s]+/).map(x => x.trim()).filter(Boolean);
    const bad = u.operatorNumbers.some(x => !/^\d+$/.test(x) || +x > 999);
    el.classList.toggle("invalid", bad);
    el.title = bad ? "Bitte 3 Ziffern, z. B. 007" : "ERP-Bedienernummer (3 Ziffern). Mehrere mit Komma.";
    if (!bad) u.operatorNumbers = u.operatorNumbers.map(x => x.padStart(3, "0"));
    if (e.type === "change" && !bad) { markUsersDirty(); drawTab(); return true; } // Zeilen für Absender je ERP-Nr. aktualisieren
  }
  else if (k === "visibility") { u.visibility = el.value; markUsersDirty(); drawTab(); return true; }
  else if (k === "mapiMode") { u.mapiMode = el.value; u._mapiChanged = true; }
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


/* ---------------- Benutzer: Melder-Geräte und MAPI-Einstellung je PC ---------------- */

const mapiText = m => (ST.data?.mapiModes || []).find(x => x.key === m)?.text || "Standard";

async function loadDevices() {
  try { ST.devices = (await api("GET", "api/melder/alle-geraete")).devices; } catch { ST.devices = []; }
  if (ST.tab === "benutzer") drawTab();
}

function userDevices(u, i) {
  const list = (ST.devices || []).filter(d => d.userId === u.id);
  if (!u.id) return "";
  if (!list.length) return `<span class="user-melder">kein Melder gekoppelt</span>`;
  const fmt = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "–";
  return `<table class="list mini"><thead><tr><th>Melder (PC)</th><th>Computer</th><th>Zuletzt</th><th>MAPI-Mails auf diesem PC</th></tr></thead><tbody>
    ${list.map(d => `<tr><td>${esc(d.name)}</td><td>${esc(d.computerName || "–")}</td><td class="meta-inline">${fmt(d.lastSeen)}</td>
      <td><select data-devmapi="${esc(d.id)}"><option value="" ${!d.mapiMode ? "selected" : ""}>wie Vorgabe</option>
        ${(ST.data.mapiModes || []).map(m => `<option value="${m.key}" ${d.mapiMode === m.key ? "selected" : ""}>${esc(m.text)}</option>`).join("")}</select>
        ${d.mapiSource ? `<span class="mapi-src">${d.mapiSource === "admin" ? "vom Administrator" : "vom Benutzer"} · ${esc(d.mapiChangedBy || "")} · ${fmt(d.mapiChangedAt)}</span>` : ""}</td></tr>`).join("")}
    </tbody></table>
    <button class="btn ghost small" data-st="user-mapi-push" data-i="${i}" title="Vorgabe sofort auf alle PCs dieses Benutzers übertragen (er kann sie danach im Melder wieder ändern)">Vorgabe auf alle PCs überrollen</button>`;
}

async function setDeviceMapi(id, mode) {
  try {
    await api("POST", `api/melder/geraete/${encodeURIComponent(id)}/mapi`, { mode });
    await loadDevices();
    msg("Einstellung für diesen PC gesetzt – der Melder übernimmt sie sofort.", true);
  } catch (e) { msg(e.message); }
}

/* ---------------- Tab: ERP-Rückmeldung ---------------- */

function onErpInput(el, e) {
  if (el.dataset.erpf !== undefined) {
    const [i, k] = el.dataset.erpf.split("|"), f = ST.s.erp.fields[+i];
    if (k === "onlyIfEmpty") f.onlyIfEmpty = el.checked;
    else if (k === "length") f.length = parseInt(el.value || "0", 10) || 0;
    else if (k === "onlyCodes") f.onlyCodes = el.value.split(/[,;\s]+/).map(x => x.trim()).filter(Boolean);
    else f[k] = k === "field" ? el.value.toUpperCase() : el.value;
    markDirty();
    if ((k === "value" || k === "field") && e.type === "change") drawTab();
    return true;
  }
  if (el.dataset.erpc !== undefined) {
    const [i, k] = el.dataset.erpc.split("|"), c = ST.s.erp.codes[+i];
    if (k === "write") c.write = el.checked; else if (k === "bitmap") c.bitmap = el.value.trim(); else c.erpValue = el.value.trim();
    markDirty(); return true;
  }
  return false;
}

function erpLength(f) {
  if (f.length > 0) return f.length;
  const m = /_(\d{1,4})$/.exec(f.field || "");
  return m ? +m[1] : null;
}

function tabErp() {
  const E = ST.s.erp ||= { enabled: false, fields: [], codes: [] };
  E.fields ||= []; E.codes ||= [];
  const pwPh = ST.data.hasErpPassword && !ST.clearErpPassword ? "•••••••• (gespeichert – leer lassen = unverändert)" : "Kennwort";
  const vals = ST.data.erpValues || [];
  const needsFormat = v => ["datum", "uhrzeit", "zeitstempel", "versanddatum", "versandzeit", "fest", "vorlage"].includes(v);
  const fmtPh = v => v === "fest" ? "Text" : v === "vorlage" ? "{Statustext} am {Datum}" : ["uhrzeit", "versandzeit"].includes(v) ? "HH:mm" : v === "zeitstempel" ? "TT.MM.JJJJ HH:mm" : "TT.MM.JJJJ";
  const pv = ST.erpPreview;
  return section("ERP-Anbindung (WEBWARE-Webservices)",
    `${inCheck("erp.enabled", "Status versendeter Belege ins ERP zurückschreiben (BELEG.PUT)")}
     <p class="help">Nur Belege mit Belegindex <code>@@pi0NR41261010@@</code> werden zurückgeschrieben. Jeder Statuswechsel landet sofort in einer Warteschlange;
       ist das ERP nicht erreichbar, wird später erneut versucht – der Versand wird nie aufgehalten.</p>
     <div class="grid2">
       ${field("Adresse (HTTPS)", inText("erp.endpoint", "https://erp.firma.at/WWSVC/"), "Mit abschließendem /. Den Servernamen verwenden, der zum Zertifikat passt (nicht localhost).")}
       ${field("SecuredApp-Zugriffs-ID", inText("erp.secureAppId", "z. B. 12"), "Eigene SecuredApp für Belegpost – nicht die eines anderen Dienstes.")}
       ${field("Hersteller-Hash", inText("erp.vendor"))}
       ${field("Anwendungs-Hash", inText("erp.application"))}
       ${field("Revision", inNum("erp.revision", 0, 9999))}
       ${field("Technischer ERP-Benutzer (optional)", inText("erp.username"))}
       ${field("Kennwort", `<input type="password" data-erppw autocomplete="new-password" placeholder="${pwPh}" ${ST.clearErpPassword ? "disabled" : ""}>`)}
       ${field("Aufgeben nach (Tagen)", inNum("erp.giveUpDays", 1, 60), "Danach gilt ein Auftrag als fehlgeschlagen und kann im Portal erneut gesendet werden.")}
     </div>
     ${ST.data.hasErpPassword ? `<label class="check"><input type="checkbox" data-st="erp-clear" ${ST.clearErpPassword ? "checked" : ""}> gespeichertes Kennwort entfernen</label>` : ""}
     ${inCheck("erp.allowRegistration", "Belegpost darf selbst einen Servicepass registrieren (danach im ERP freigeben)")}
     ${inCheck("erp.checkBeforeWrite", "Vor dem Schreiben mit BELEG.GET prüfen (Beleg vorhanden, „nur wenn leer“, schon aktuell)")}
     <div id="erpStatus"><p class="meta">Lade Status …</p></div>`) +
    section("Statuscodes",
      `<p class="help">Der Status wird als 3-stellige Zahl geschrieben. Gruppen: 0xx in Arbeit · 1xx versendet · 2xx ohne Versand · 8xx Fehler · 9xx abgebrochen.
        Den Wert im ERP kannst du anpassen und einzelne Stati abschalten.
        Die <b>Statusbitmap</b> ist die Bildnummer im ERP (Datei <code>bw000950.png</code> usw., 16×16) – als Wert „Statusbitmap“ in ein Rückgabefeld schreibbar.
        <a href="img/status/statusbitmaps.zip" download>Alle Bilder herunterladen (ZIP)</a></p>
       <table class="list mini"><thead><tr><th>Code</th><th>Status</th><th>Wert im ERP</th><th title="Bildnummer im ERP – Datei bw000…png">Statusbitmap</th><th>Schreiben</th></tr></thead><tbody>
       ${E.codes.map((c, i) => `<tr><td><code>${esc(c.code)}</code></td><td>${esc((ST.data.statusCodes || []).find(x => x.code === c.code)?.text || "")}</td>
         <td><input class="erp-no" data-erpc="${i}|erpValue" value="${esc(c.erpValue || c.code)}" maxlength="3" inputmode="numeric"></td>
         <td class="nowrap">${(() => { const def = (ST.data.statusCodes || []).find(x => x.code === c.code)?.bitmap || ""; const b = c.bitmap || def;
           return `${def ? `<img class="st-bmp" src="img/status/bw${def.padStart(6, "0")}.png" alt="" title="Standardbild bw${def.padStart(6, "0")}.png">` : ""}<input class="erp-no" data-erpc="${i}|bitmap" value="${esc(b)}" maxlength="6" inputmode="numeric" title="Bildnummer im ERP (bw000${esc(b)}.png)">`; })()}</td>
         <td><input type="checkbox" data-erpc="${i}|write" ${c.write ? "checked" : ""}></td></tr>`).join("")}</tbody></table>`) +
    section("Rückgabefelder",
      `<p class="help">Eine Zeile je Feld im Belegkopf. Die Länge ergibt sich aus der Zahl hinter dem letzten „_“ (<code>BEL_3_8</code> = 8 Zeichen); längere Werte werden gekürzt.
        Genau ein Feld bekommt den <b>Statuscode</b>. Datumsformate z. B. <code>TT.MM.JJJJ</code>, <code>JJJJMMTT</code>; Vorlagen mit Platzhaltern
        <code>{Statuscode} {Statustext} {Datum} {Zeit} {Versanddatum} {Versandzeit} {Kanal} {Empfaenger} {Bediener} {Benutzer} {Belegnummer} {StatusLink}</code>.</p>
       ${E.fields.length ? `<table class="list mini erp-fields"><thead><tr><th>ERP-Feld</th><th>Länge</th><th>Wert</th><th>Format / Text</th><th>nur bei Codes</th><th>nur wenn leer</th><th></th></tr></thead><tbody>
       ${E.fields.map((f, i) => { const len = erpLength(f); const d = vals.find(v => v.key === f.value);
         return `<tr><td><input data-erpf="${i}|field" value="${esc(f.field)}" placeholder="BEL_3_8"></td>
         <td class="len">${len ?? `<input data-erpf="${i}|length" value="${f.length || ""}" placeholder="?" title="Länge – im Feldnamen nicht erkennbar" style="width:46px">`}</td>
         <td><select data-erpf="${i}|value">${vals.map(v => `<option value="${v.key}" ${f.value === v.key ? "selected" : ""}>${esc(v.label)}</option>`).join("")}</select></td>
         <td>${needsFormat(f.value) ? `<input data-erpf="${i}|format" value="${esc(f.format)}" placeholder="${esc(fmtPh(f.value))}">` : `<span class="meta-inline">z. B. ${esc(d?.example || "")}</span>`}</td>
         <td><input data-erpf="${i}|onlyCodes" value="${esc((f.onlyCodes || []).join(", "))}" placeholder="alle"></td>
         <td><input type="checkbox" data-erpf="${i}|onlyIfEmpty" ${f.onlyIfEmpty ? "checked" : ""}></td>
         <td><button class="icon-btn" data-st="erp-field-del" data-i="${i}" title="Entfernen">✕</button></td></tr>`; }).join("")}</tbody></table>`
         : `<p class="meta">Noch keine Felder – mit „+ Feld“ das Statusfeld und weitere Felder zuordnen.</p>`}`,
      `<button class="btn ghost" data-st="erp-field-add">+ Feld</button>`) +
    section("Vorschau und Testschreiben",
      `<div class="grid3">
        ${field("Vorgang (leer = neuester)", `<input id="erpPvId" placeholder="Vorgangsnummer" value="${esc(pv?.vorgang || "")}">`)}
        ${field("Statuscode", `<select id="erpPvCode"><option value="">aktueller Status</option>${(ST.data.statusCodes || []).map(c => `<option value="${c.code}" ${pv?.code === c.code ? "selected" : ""}>${c.code} – ${esc(c.text)}</option>`).join("")}</select>`)}
        ${field("Belegindex für Testschreiben", `<input id="erpTestIdx" placeholder="z. B. 0NR41261010" maxlength="11" spellcheck="false">`)}
       </div>
       <div class="actions"><button class="btn ghost" data-st="erp-preview">Vorschau</button><button class="btn ghost" data-st="erp-test">Testschreiben …</button></div>
       ${pv ? `<p class="meta">${esc(pv.beleg)} · ${esc(pv.code)} ${esc(pv.codeText)}${pv.write ? "" : " · <b>dieser Status wird nicht geschrieben</b>"}</p>
         <div class="erp-preview">BELEG.PUT\n${Object.entries(pv.parameters).map(([k, v]) => `${esc(k)} = ${esc(v)}`).join("\n")}</div>
         ${[...(pv.errors || []), ...(pv.warnings || [])].map(x => `<p class="help bad">${esc(x)}</p>`).join("")}` : ""}`) +
    section("Warteschlange", `<div id="erpQueue"><p class="meta">Lade …</p></div>`, `<button class="btn ghost" data-st="erp-retry-all">Offene jetzt versuchen</button>`);
}

async function erpCall(url) {
  const r = await api("POST", url);
  msg(r.message || "Erledigt.", r.ok !== false);
  loadErpStatus();
}

async function loadErpStatus() {
  try {
    const [st, q] = await Promise.all([api("GET", "api/admin/erp/status"), api("GET", "api/admin/erp/auftraege")]);
    const box = $("#erpStatus"); if (!box) return;
    const c = st.counts || {};
    box.innerHTML = `<div class="plain-field"><span class="lbl">Servicepass</span>
      <p class="meta">${esc(st.configured ? st.passText : "Noch nicht vollständig eingerichtet.")}${st.lastValidated ? ` · zuletzt geprüft ${fmtDT(st.lastValidated)}` : ""}
        ${st.lastError ? `<br><span class="bad">Letzter Fehler: ${esc(st.lastError)}</span>` : ""}${st.lastSuccess ? `<br>Zuletzt erfolgreich ${fmtDT(st.lastSuccess)}` : ""}
        ${st.problem ? `<br><span class="bad">${esc(st.problem)}</span>` : ""}</p>
      <p class="meta">Warteschlange: ${c.offen || 0} offen · ${c.fehler || 0} Fehler · ${c.erledigt || 0} erledigt</p>
      <div class="actions"><button class="btn ghost" data-st="erp-check" ${st.configured ? "" : "disabled"}>Verbindung prüfen</button>
        ${st.pass === "RegistrationOutcomeUnknown" ? `<button class="btn danger-ghost" data-st="erp-confirm-reg">Registrierung zurücksetzen …</button>` : ""}</div></div>`;
    const qb = $("#erpQueue"); if (!qb) return;
    const label = { offen: ["partial", "offen"], erledigt: ["ok", "erledigt"], fehler: ["missing", "Fehler"], ersetzt: ["free", "ersetzt"], verworfen: ["free", "verworfen"] };
    qb.innerHTML = q.items.length ? `<div class="table-wrap"><table class="list mini"><thead><tr><th>Zeit</th><th>Belegindex</th><th>Code</th><th>Status</th><th>Meldung / Felder</th><th></th></tr></thead><tbody>
      ${q.items.map(j => `<tr><td class="nowrap">${fmtDT(j.done || j.created)}</td><td><a href="#/vorgang?id=${encodeURIComponent(j.vorgangId)}">${esc(j.erpIndex)}</a></td>
        <td><span class="badge code">${esc(j.code)}</span></td><td><span class="badge ${label[j.status]?.[0] || ""}">${label[j.status]?.[1] || esc(j.status)}</span>${j.status === "offen" && j.attempts ? `<small> Versuch ${j.attempts + 1} um ${fmtDT(j.nextAttempt)}</small>` : ""}</td>
        <td>${esc(j.message || (j.fields ? Object.entries(j.fields).map(([k, v]) => k + "=" + v).join(", ") : ""))}</td>
        <td class="nowrap">${["fehler", "offen", "verworfen"].includes(j.status) ? `<button class="btn ghost small" data-st="erp-job-retry" data-id="${j.id}">Erneut</button>` : ""}
          ${["fehler", "offen"].includes(j.status) ? `<button class="btn ghost small" data-st="erp-job-discard" data-id="${j.id}">Verwerfen</button>` : ""}</td></tr>`).join("")}
      </tbody></table></div>` : `<p class="meta">Noch keine Aufträge.</p>`;
  } catch (e) { const box = $("#erpStatus"); if (box) box.innerHTML = `<p class="bad">${esc(e.message)}</p>`; }
}

/* ---------------- Tab: MAPI-Mails ---------------- */

function tabMapi() {
  ST.s.mapi ||= { defaultMode: "outlook", deliveryTimeoutHours: 8, outlookConfirmHours: 24 };
  return section("Mailclient „Belegpost“ am ERP-/RDP-Server",
    `<p class="help">Das ERP verschickt Mails über MAPI. Mit dem Mailclient „Belegpost“ (BelegpostMapi.dll, 32 und 64 Bit) landen diese Mails bei Belegpost:
       je nach Einstellung öffnet der <b>Melder</b> sie als fertigen Outlook-Entwurf auf dem PC des Benutzers, sie warten im Portal auf Prüfung oder gehen direkt über den Dienst hinaus.
       Der Benutzer wird am Windows-Konto erkannt (Benutzer → Windows-Benutzer), der PC am Computernamen der RDP-Sitzung.</p>
     <div id="mapiStatus"><p class="meta">Lade Status …</p></div>`) +
    section("Was passiert mit MAPI-Mails?",
      `<div class="grid2">${field("Standard für alle Benutzer", inSelect("mapi.defaultMode", (ST.data.mapiModes || []).map(m => [m.key, m.text])),
          "Pro Benutzer (Benutzer → MAPI-Mails) und pro PC (Melder) änderbar. Der Benutzer kann die Einstellung seines PCs im Melder selbst ändern; der Admin kann sie überrollen.")}
        ${field("Nicht abgeholte Outlook-Entwürfe nach (Stunden)", inNum("mapi.deliveryTimeoutHours", 1, 720), "Holt kein Melder den Entwurf ab, bleibt die Mail im Portal (Status 820).")}
        ${field("Versandbestätigung aus Outlook erwarten (Stunden)", inNum("mapi.outlookConfirmHours", 1, 720), "Ohne Bestätigung Status 830 – eine spätere Bestätigung setzt 110.")}</div>`);
}

async function mapiCall(action) {
  const r = await api("POST", "api/admin/mapi/" + action);
  msg((r.ok ? "" : (r.message || "Fehler") + "\n") + (r.log || []).join("\n"), r.ok);
  loadMapiStatus();
}

async function loadMapiStatus() {
  const box = $("#mapiStatus"); if (!box) return;
  try {
    const r = await api("GET", "api/admin/mapi");
    const s = r.status;
    box.innerHTML = s ? `<table class="kv">
        <tr><th>DLLs neben Belegpost.exe</th><td>${s.available ? "vorhanden" : '<span class="bad">fehlen (BelegpostMapi-x64.dll / -x86.dll)</span>'}</td></tr>
        <tr><th>Installiert</th><td>${s.installed ? "ja (64 und 32 Bit)" : "nein"}${s.problem ? ` <span class="bad">${esc(s.problem)}</span>` : ""}</td></tr>
        <tr><th>Standard-Mailprogramm (alle Benutzer)</th><td>${s.defaultForAll ? "<b>Belegpost</b>" : esc(s.currentDefault || "–")}</td></tr>
        <tr><th>Bei Anmeldung je Benutzer setzen</th><td>${s.perUserLogon ? "ja – jeder Benutzer bekommt Belegpost bei der nächsten Anmeldung" : "nein"}</td></tr>
        <tr><th>Windows-Standard-App „E-Mail“</th><td>${s.appDefaultPolicy ? (/Belegpost/i.test(s.appDefaultPolicy) ? "<b>Belegpost</b> für alle Benutzer (Richtlinie, ab der nächsten Anmeldung)" : `andere Richtlinie aktiv: ${esc(s.appDefaultPolicy)}`) : "wählt jeder Benutzer selbst (Einstellungen → Apps → Standard-Apps → E-Mail)"}</td></tr></table>
      <div class="actions"><button class="btn ghost" data-st="mapi-install" ${s.available ? "" : "disabled"}>${s.installed ? "Neu installieren" : "Installieren"}</button>
        <button class="btn" data-st="mapi-default" ${s.available ? "" : "disabled"}>Für alle Benutzer als Standard setzen</button>
        <button class="btn ghost" data-st="mapi-users" ${s.available ? "" : "disabled"} title="Eigenen Standard jedes Windows-Benutzers setzen (z. B. wenn Outlook sich dort wieder eingetragen hat)">Bei allen Windows-Benutzern setzen</button>
        ${s.defaultForAll || s.perUserLogon || /Belegpost/i.test(s.appDefaultPolicy || "") ? `<button class="btn ghost" data-st="mapi-restore">Vorheriges wiederherstellen</button>` : ""}</div>
      ${mapiUsersTable(s.users || [])}
      <p class="help">Windows nimmt zuerst den <b>eigenen Standard des Benutzers</b> (HKCU) – Outlook trägt sich dort oft selbst ein. „Für alle Benutzer als Standard setzen“ setzt deshalb beides:
        den Standard für alle und den eigenen Standard jedes Benutzers (angemeldete sofort, alle anderen bei der nächsten Anmeldung). Danach das ERP bzw. das Programm neu starten.
        Zusätzlich wird Belegpost per Richtlinie als <b>Windows-Standard-App „E-Mail“</b> (mailto-Links) festgelegt – wirkt bei der nächsten Anmeldung.
        Von Hand für einen Benutzer: <code>${esc(r.perUserCommand)}</code></p>`
      : `<p class="meta">Nur unter Windows verfügbar.</p>`;
  } catch (e) { box.innerHTML = `<p class="bad">${esc(e.message)}</p>`; }
}

function mapiUsersTable(users) {
  if (!users.length) return `<p class="meta">Gerade ist kein Windows-Benutzer am Server angemeldet.</p>`;
  return `<table class="list mini"><thead><tr><th>Angemeldeter Windows-Benutzer</th><th>Eigener Standard</th><th>MAPI-Mails gehen an</th></tr></thead><tbody>
    ${users.map(u => `<tr><td>${esc(u.account)}</td><td>${u.value ? esc(u.value) : '<span class="meta">– (Standard für alle)</span>'}</td>
      <td>${u.belegpost ? "<b>Belegpost</b>" : '<span class="bad">nicht Belegpost</span>'}</td></tr>`).join("")}</tbody></table>`;
}

/* ---------------- Tab: Status-Link ---------------- */

function tabStatusLink() {
  ST.s.statusLink ||= { allowedNetworks: [], trustedProxies: [], expiryDays: 180, showVoucherNumber: true };
  return section("Status-Link",
    `<p class="help">Jeder Beleg bekommt einen Link <code>/s/…</code>, der ohne Anmeldung zeigt, was mit dem Beleg passiert ist – Belegart, Belegnummer,
       Statuscodes mit Datum und Uhrzeit (z. B. versendet, über Outlook bestätigt, im ERP bestätigt). Empfänger, Kunden, Beträge oder Inhalte werden nie angezeigt.
       Den Link kann die ERP-Rückmeldung in ein Belegfeld schreiben (Wert „Status-Link“).</p>
     <div class="grid2">
       ${field("Freigegebene IP-Bereiche", inLines("statusLink.allowedNetworks", "192.168.10.0/24\n10.0.0.0/8"), "Nur aus diesen Netzen abrufbar (CIDR oder einzelne Adressen). Leer = Status-Link aus.")}
       ${field("Vertrauenswürdige Reverse-Proxys (optional)", inLines("statusLink.trustedProxies", "192.168.10.5"), "Nur von diesen Adressen wird X-Forwarded-For übernommen.")}
       ${field("Gültig nach dem letzten Ereignis (Tage)", inNum("statusLink.expiryDays", 0, 3650), "0 = unbegrenzt.")}
       ${field("Portal-Adresse für Links", inText("portal.publicUrl", `https://${(ST.data.hostNames || ["server"])[0]}:${ST.s.server.httpsPort}`), "Leer = erster Servername.")}
     </div>
     ${inCheck("statusLink.showVoucherNumber", "Belegart und Belegnummer anzeigen")}`);
}

/* ---------------- Tab: Mandanten ---------------- */

function tabMandanten() {
  const M = (ST.s.mandanten ||= { items: [] });
  M.items ||= [];
  if (ST.mdEdit != null && M.items[ST.mdEdit]) return mandantMask(ST.mdEdit);
  ST.mdEdit = null;
  const erpText = m => m.erpOff ? '<span class="badge">aus</span>' : m.ownErp ? `<span class="badge ok">eigene SecuredApp ${esc(m.erpSecureAppId || "")}</span>` : '<span class="meta-inline">wie allgemein</span>';
  const or = v => v ? esc(v) : '<span class="meta-inline">–</span>';
  return section("Mandanten",
    `<p class="help">Belegpost erkennt den Mandanten am Beleg – in dieser Reihenfolge: Steuerzeichen <code>@@pm10000@@</code> im Druck
      (bei MAPI auch im Mailtext) → Parameter <code>/M:</code> des druckenden Programms (WebWARE <code>wwa.exe</code>, Prozesserkennung unter Drucker &amp; Eingang)
      → eigener Eingangsordner des Mandanten. Je Mandant stellst du nur ein, was anders ist; alles andere (E-Mail, Versand, Benutzer, Regeln, Vorlagen, MAPI) gilt für alle.</p>
     ${M.items.length ? `<div class="table-wrap"><table class="list users-compact"><thead><tr>
        <th>Nummer</th><th>Bezeichnung</th><th>ERP-Rückmeldung</th><th>Eingangsordner</th><th>gotomaxx-Drucker</th><th>Ablage</th><th></th></tr></thead><tbody>
      ${M.items.map((m, i) => `<tr class="clickable" data-st="md-open" data-i="${i}">
        <td class="mono"><b>${esc(m.number || "(neu)")}</b></td>
        <td>${or(m.name)}</td>
        <td class="nowrap">${erpText(m)}</td>
        <td>${or(m.inboxFolder)}</td>
        <td>${or(m.gotomaxxPrinter)}</td>
        <td>${or(m.exportFolder)}</td>
        <td class="nowrap"><button class="btn ghost small" data-st="md-open" data-i="${i}">Bearbeiten</button></td></tr>`).join("")}
      </tbody></table></div>`
      : `<p class="meta">Noch keine Mandanten – ohne Eintrag gelten für alle Belege die allgemeinen Einstellungen. Der erkannte Mandant wird trotzdem beim Vorgang angezeigt.</p>`}`,
    `<button class="btn ghost" data-st="md-add">+ Mandant</button>`);
}

function mandantMask(i) {
  const m = ST.s.mandanten.items[i];
  const P = `mandanten.items.${i}`;
  const mode = m.erpOff ? "aus" : m.ownErp ? "eigen" : "";
  return `<div class="user-mask-head"><button class="btn ghost" data-st="md-back">← Alle Mandanten</button>
      <h2>Mandant ${esc(m.number || "(neu)")}${m.name ? ` – ${esc(m.name)}` : ""}</h2>
      <span class="spacer"></span>
      <button class="btn ghost danger" data-st="md-del" data-i="${i}">Entfernen</button></div>` +
    section("Mandant",
      `<div class="grid2">
        ${field("Mandantennummer", inText(`${P}.number`, "10000"), "Wie im ERP – bei der WebWARE der Wert von <code>/M:</code> (z. B. <code>WWA.EXE 000 /M:10000</code>).")}
        ${field("Bezeichnung", inText(`${P}.name`, "Firma GmbH"))}
      </div>`) +
    section("ERP-Rückmeldung",
      `<div class="grid2">
        ${field("Anbindung", `<select data-md-erp="${i}">
            <option value="" ${mode === "" ? "selected" : ""}>wie allgemein eingestellt</option>
            <option value="eigen" ${mode === "eigen" ? "selected" : ""}>eigene SecuredApp für diesen Mandanten</option>
            <option value="aus" ${mode === "aus" ? "selected" : ""}>für diesen Mandanten nicht zurückschreiben</option></select>`,
          "Ein WEBWARE-Servicepass greift immer nur auf <b>einen</b> Mandanten zu – für jeden weiteren Mandanten braucht es im ERP eine eigene SecuredApp. Feldzuordnung und Statuscodes gelten für alle Mandanten.")}
      </div>
      ${mode === "eigen" ? `<div class="grid3">
        ${field("SecuredApp-Zugriffs-ID", inText(`${P}.erpSecureAppId`, ST.s.erp?.secureAppId || ""), "Pflicht – die SecuredApp dieses Mandanten.")}
        ${field("Webservice-Adresse", inText(`${P}.erpEndpoint`, ST.s.erp?.endpoint || "wie allgemein"), "Leer = wie allgemein.")}
        ${field("Revision", inNum(`${P}.erpRevision`, 0, 9999), "0 = wie allgemein.")}
        ${field("Hersteller-Hash", inText(`${P}.erpVendor`, "wie allgemein"))}
        ${field("Anwendungs-Hash", inText(`${P}.erpApplication`, "wie allgemein"))}
        ${field("Technischer ERP-Benutzer", inText(`${P}.erpUsername`, ST.s.erp?.username || "wie allgemein"), "Leer = Benutzer und Kennwort wie allgemein.")}
        ${field("Kennwort", `<input type="password" data-path="${P}.erpNewPassword" autocomplete="new-password" placeholder="${m.erpHasPassword ? "gespeichert – leer lassen" : ""}" ${m.erpClearPassword ? "disabled" : ""}>`)}
      </div>
      <div class="actions">${m.erpHasPassword ? `<label class="check"><input type="checkbox" data-st="md-pw-clear" data-i="${i}" ${m.erpClearPassword ? "checked" : ""}> gespeichertes Kennwort entfernen</label>` : ""}
        ${m.number ? `<button class="btn ghost small" data-st="md-erp-check" data-m="${esc(m.number)}">Verbindung prüfen</button>` : ""}</div>` : ""}`) +
    section("Drucker &amp; Eingang",
      `<div class="grid2">
        ${field("Eigener Eingangsordner (optional)", inText(`${P}.inboxFolder`, "\\\\server\\belegpost\\mandant2"), "PDFs aus diesem Ordner gehören zu diesem Mandanten. Leer = keiner.")}
        ${field("gotomaxx-Standarddrucker", inText(`${P}.gotomaxxPrinter`, ST.s.gotomaxx?.printer || "wie allgemein"), "Für <code>@@pg@@</code> ohne Druckernamen. Leer = wie allgemein.")}
        ${field("Pfade aus den Steuerzeichen umsetzen (zusätzlich)", inLines(`${P}.pathMappings`, "K:\\=\\\\server\\WebWARE5\\M2\\"), "Werden vor den allgemeinen Umsetzungen angewendet.")}
      </div>`) +
    section("Ablage",
      `<div class="grid2">
        ${field("Versendete Belege ablegen in", inText(`${P}.exportFolder`, ST.s.storage?.exportFolder || "wie allgemein"), "Leer = wie allgemein (Einstellungen → Ablage).")}
      </div>`) +
    `<p class="meta">Gespeichert wird mit „Speichern“ unten.</p>`;
}
