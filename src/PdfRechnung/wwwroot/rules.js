"use strict";

/* =========================================================
   Signaturen und Mail-Regeln – gemeinsamer Editor für
   · Einstellungen → Regeln & Signaturen (global)
   · Einstellungen → Benutzer → Maske des Benutzers
   · Persönlich → Meine Mail-Regeln
   Steuerzeichen im Druck übersteuern Regeln immer.
   ========================================================= */

const RULE_EDITORS = {};

/**
 * Editor anlegen. set = { signatures: [], rules: [] }, choices = { templates, stationeries, globalSignatures, routes, gotomaxx, printers, outlook },
 * global = Regeln mit Benutzer-Bedingung, users = [{ id, name }], onChange(), redraw().
 */
function ruleEditor(id, opts) {
  const ed = RULE_EDITORS[id] = Object.assign(RULE_EDITORS[id] || { open: new Set() }, opts, { id });
  ed.set.signatures ||= []; ed.set.rules ||= [];
  return ed;
}

const reList = v => (v || []).join(", ");
const reParse = v => String(v || "").split(/[,;\n]+/).map(x => x.trim()).filter(Boolean);
const reAttr = (ed, kind, i, f) => `data-re="${ed.id}|${kind}|${i}|${f}"`;
const ROUTE_TEXT = { "": "wie eingestellt", direkt: "direkt (automatisch)", wennok: "automatisch, wenn alles ok", freigabe: "nach Freigabe im Portal", outlook: "über Outlook", gotomaxx: "an gotomaxx", keine: "keine Mail" };

function reText(ed, kind, i, f, value, ph = "", list) {
  return `<input ${reAttr(ed, kind, i, f)} value="${esc(value ?? "")}" placeholder="${esc(ph)}" ${list ? `list="${list}"` : ""}>`;
}
function reSelect(ed, kind, i, f, value, options) {
  return `<select ${reAttr(ed, kind, i, f)}>${options.map(([v, t]) => `<option value="${esc(v)}" ${(value || "") === v ? "selected" : ""}>${esc(t)}</option>`).join("")}</select>`;
}
const reField = (label, inner, help) => {
  const tag = String(inner).includes('class="rte') ? "div" : "label"; // Editor nie in <label> (Fokus)
  return `<${tag} class="field"><span class="lbl">${label}</span>${inner}${help ? `<small class="help">${help}</small>` : ""}</${tag}>`;
};

/** Kurzbeschreibung einer Regel für die eingeklappte Zeile. */
function ruleSummary(r, ed) {
  const cond = [];
  if (r.documentTypes?.length) cond.push(`Belegart ${reList(r.documentTypes)}`);
  if (r.stationeries?.length) cond.push(`Briefpapier ${reList(r.stationeries)}`);
  if (r.senders?.length) cond.push(`Absender ${reList(r.senders)}`);
  if (r.recipients?.length) cond.push(`an ${reList(r.recipients)}`);
  if (r.customers?.length) cond.push(`Kunde ${reList(r.customers)}`);
  if (r.sources?.length) cond.push(`aus ${reList(r.sources)}`);
  if (r.formats?.length) cond.push(`Format ${reList(r.formats)}`);
  if (r.textContains) cond.push(`Text „${r.textContains}“`);
  if (r.users?.length) cond.push(`Benutzer ${r.users.map(u => (ed.users || []).find(x => x.id === u)?.name || u).join(", ")}`);
  const act = [];
  if (r.template) act.push(`Vorlage ${r.template}`);
  if (r.fromAddress) act.push(`von ${r.fromAddress}`);
  if (r.signature) act.push(`Signatur ${r.signature}`);
  if (r.route) act.push(ROUTE_TEXT[r.route] || r.route);
  if (r.cc?.length) act.push(`CC ${reList(r.cc)}`);
  if (r.bcc?.length) act.push(`BCC ${reList(r.bcc)}`);
  if (r.stop) act.push("Stopp");
  return `${cond.length ? "Wenn " + cond.join(" · ") : "Immer"} → ${act.length ? act.join(" · ") : "(noch keine Aktion)"}`;
}

/** HTML des Editors (Signaturen + Regeln). */
function ruleEditorHtml(ed) {
  const c = ed.choices || {};
  const dl = `re-${ed.id}`;
  const sigNames = [...new Set([...(ed.set.signatures || []).map(s => s.name), ...(c.globalSignatures || [])].filter(Boolean))];
  const routes = (c.routes || Object.keys(ROUTE_TEXT).map(k => ({ key: k, text: ROUTE_TEXT[k] })))
    .filter(r => r.key !== "gotomaxx" || c.gotomaxx).filter(r => r.key !== "outlook" || c.outlook !== false || ed.global);

  const sigs = ed.set.signatures.map((s, i) => `
    <div class="re-card">
      <div class="re-head"><b>${esc(s.name || "Neue Signatur")}</b>${s.default ? ' <span class="badge ok">Standard</span>' : ""}
        <span class="re-assign">${[s.stationeries?.length ? "Briefpapier " + reList(s.stationeries) : "", s.senders?.length ? "Absender " + reList(s.senders) : ""].filter(Boolean).join(" · ")}</span>
        <span class="spacer"></span><button class="icon-btn" data-rea="${ed.id}|sig-del|${i}" title="Entfernen">✕</button></div>
      <div class="grid3">
        ${reField("Name", reText(ed, "sig", i, "name", s.name, "z. B. Martin – Firma A"))}
        ${reField("Für Briefpapier", reText(ed, "sig", i, "stationeries", reList(s.stationeries), "leer = alle", dl + "-bp"), "Mehrere mit Komma.")}
        ${reField("Für Absender", reText(ed, "sig", i, "senders", reList(s.senders), "z. B. service@firma.at oder *@firma-b.at"))}
      </div>
      <label class="check"><input type="checkbox" ${reAttr(ed, "sig", i, "default")} ${s.default ? "checked" : ""}> Standard${ed.global ? " (für alle ohne eigene Signatur)" : " (wenn kein Briefpapier/Absender passt)"}</label>
      ${reField("Signatur", rteHtml(s.text, reAttr(ed, "sig", i, "text"), { small: true }),
        "Schrift, Farbe, Links und Bilder wie in Outlook. Eine bestehende Signatur aus Outlook oder Word einfach kopieren und hier einfügen – sie wird bereinigt übernommen. Platzhalter über {…}.")}
    </div>`).join("");

  const rules = ed.set.rules.map((r, i) => `
    <details class="re-card re-rule ${r.enabled === false ? "off" : ""}" data-reopen="${ed.id}|${i}" ${ed.open.has(i) ? "open" : ""}>
      <summary><span class="re-no">${i + 1}</span><b>${esc(r.name || "Neue Regel")}</b>${r.enabled === false ? ' <span class="badge">aus</span>' : ""}
        <span class="re-sum">${esc(ruleSummary(r, ed))}</span>
        <span class="re-tools"><button class="icon-btn" data-rea="${ed.id}|up|${i}" title="Nach oben" ${i === 0 ? "disabled" : ""}>↑</button>
        <button class="icon-btn" data-rea="${ed.id}|down|${i}" title="Nach unten" ${i === ed.set.rules.length - 1 ? "disabled" : ""}>↓</button>
        <button class="icon-btn" data-rea="${ed.id}|rule-del|${i}" title="Entfernen">✕</button></span></summary>
      <div class="re-body">
        <div class="grid2">${reField("Name", reText(ed, "rule", i, "name", r.name, "z. B. Angebote an Großkunden"))}
          <div class="re-checks"><label class="check"><input type="checkbox" ${reAttr(ed, "rule", i, "enabled")} ${r.enabled !== false ? "checked" : ""}> aktiv</label>
          <label class="check"><input type="checkbox" ${reAttr(ed, "rule", i, "stop")} ${r.stop ? "checked" : ""}> danach keine weiteren Regeln</label></div></div>
        <h4>Wenn … <small class="meta-inline">(leer = egal; mehrere Werte mit Komma; * und ? als Platzhalter)</small></h4>
        <div class="grid3">
          ${reField("Belegart", reText(ed, "rule", i, "documentTypes", reList(r.documentTypes), "Rechnung, Angebot …"))}
          ${reField("Briefpapier", reText(ed, "rule", i, "stationeries", reList(r.stationeries), "", dl + "-bp"))}
          ${reField("Absender", reText(ed, "rule", i, "senders", reList(r.senders), "z. B. *@firma-b.at"))}
          ${reField("Empfänger", reText(ed, "rule", i, "recipients", reList(r.recipients), "z. B. *@kunde.at"))}
          ${reField("Kunde (Nr. oder Name)", reText(ed, "rule", i, "customers", reList(r.customers), "z. B. 4711, *GmbH"))}
          ${reField("Quelle", reText(ed, "rule", i, "sources", reList(r.sources), "Drucker, MAPI, Eingang, Upload"))}
          ${reField("E-Rechnungsformat", reText(ed, "rule", i, "formats", reList(r.formats), "z. B. XRECHNUNG*"))}
          ${reField("Betreff/Dateiname enthält", reText(ed, "rule", i, "textContains", r.textContains, "z. B. Mahnung"))}
          ${ed.global ? reField("Nur für Benutzer", `<select multiple size="3" ${reAttr(ed, "rule", i, "users")}>${(ed.users || []).map(u => `<option value="${esc(u.id)}" ${(r.users || []).includes(u.id) ? "selected" : ""}>${esc(u.name)}</option>`).join("")}</select>`, "Keiner gewählt = alle. Strg-Klick für mehrere.") : ""}
        </div>
        <h4>… dann <small class="meta-inline">(leer = nicht ändern)</small></h4>
        <div class="grid3">
          ${reField("Mail-Vorlage", reSelect(ed, "rule", i, "template", r.template, [["", "–"], ...(c.templates || []).map(t => [t, t])]))}
          ${reField("Absender", reText(ed, "rule", i, "fromAddress", r.fromAddress, c.defaultSender || "name@firma.at"))}
          ${reField("Absendername", reText(ed, "rule", i, "fromName", r.fromName, "z. B. Firma A – Vertrieb"))}
          ${reField("Antwort an", reText(ed, "rule", i, "replyTo", r.replyTo, "optional"))}
          ${reField("Signatur", reSelect(ed, "rule", i, "signature", r.signature, [["", "–"], ...sigNames.map(n => [n, n])]))}
          ${reField("Versandweg", reSelect(ed, "rule", i, "route", r.route, routes.map(x => [x.key, x.key ? x.text : "–"])))}
          ${r.route === "gotomaxx" ? reField("gotomaxx-Drucker", reSelect(ed, "rule", i, "gotomaxxPrinter", r.gotomaxxPrinter, [["", "Standarddrucker"], ...(c.printers || []).map(p => [p, p])])) : ""}
          ${reField("CC", reText(ed, "rule", i, "cc", reList(r.cc), "optional"))}
          ${reField("BCC", reText(ed, "rule", i, "bcc", reList(r.bcc), "z. B. archiv@firma.at"))}
        </div>
      </div>
    </details>`).join("");

  return `<datalist id="${dl}-bp">${(c.stationeries || []).map(n => `<option value="${esc(n)}">`).join("")}</datalist>
    <div class="re-section"><div class="re-section-head"><h4>Signaturen</h4><button class="btn ghost small" data-rea="${ed.id}|sig-add|0">+ Signatur</button></div>
      ${sigs || `<p class="meta">Keine${ed.global ? " globalen" : " eigenen"} Signaturen – dann gilt ${ed.global ? "die Signatur der Mail-Vorlage" : "die globale Signatur bzw. die der Mail-Vorlage"}.</p>`}
      <p class="help">Auswahl: <code>@@psName@@</code> im Druck → Regel → ${ed.global ? "eigene Signaturen des Benutzers → " : ""}Signatur für das Briefpapier → für den Absender → Standard${ed.global ? "" : " → globale Signaturen"} → Signatur der Mail-Vorlage. <code>@@ps@@</code> = ohne Signatur.</p></div>
    <div class="re-section"><div class="re-section-head"><h4>Regeln</h4><button class="btn ghost small" data-rea="${ed.id}|rule-add|0">+ Regel</button></div>
      ${rules || `<p class="meta">Keine${ed.global ? " globalen" : " eigenen"} Regeln.</p>`}
      <p class="help">Geprüft wird von oben nach unten – ${ed.global ? "zuerst die eigenen Regeln des Benutzers, dann diese" : "zuerst deine eigenen Regeln, dann die globalen"}. Je Aktion gilt die erste Regel, die sie setzt.
        <b>Steuerzeichen im Druck haben immer Vorrang:</b> <code>@@pt@@</code> Vorlage, <code>@@pf@@</code> Absender, <code>@@ps@@</code> Signatur, <code>@@pw…@@</code> Versandweg
        (<code>direkt</code>, <code>wennok</code>, <code>freigabe</code>, <code>outlook</code>, <code>gotomaxx</code>, <code>keine</code>), <code>@@pg@@</code> gotomaxx, <code>@@cc@@</code>/<code>@@bc@@</code>, <code>@@ml0@@</code> keine Mail.</p></div>`;
}

/** Eingabe im Editor; liefert true, wenn sie dem Editor gehörte. */
function ruleEditorInput(el, e) {
  if (el.dataset?.rteCmd) return false;
  if (!el.dataset.re) return false;
  const [id, kind, idx, f] = el.dataset.re.split("|"), ed = RULE_EDITORS[id];
  if (!ed) return true;
  const obj = (kind === "sig" ? ed.set.signatures : ed.set.rules)[+idx];
  const lists = ["stationeries", "senders", "documentTypes", "recipients", "customers", "sources", "formats", "cc", "bcc"];
  if (el.type === "checkbox") {
    if (kind === "sig" && f === "default" && el.checked) ed.set.signatures.forEach(s => s.default = false);
    obj[f] = el.checked;
  }
  else if (f === "users") obj.users = [...el.selectedOptions].map(o => o.value);
  else if (lists.includes(f)) obj[f] = reParse(el.value);
  else obj[f] = el.dataset.rte === "1" ? fieldValue(el) : el.value;
  ed.onChange?.();
  if (e.type === "change" && (["route", "default", "enabled"].includes(f) || el.tagName === "SELECT")) ed.redraw?.();
  return true;
}

/** Klick im Editor (Hinzufügen, Entfernen, Verschieben); liefert true, wenn er dem Editor gehörte. */
async function ruleEditorClick(e) {
  const det = e.target.closest("[data-reopen]");
  const btn = e.target.closest("[data-rea]");
  if (!btn) {
    // Auf-/Zuklappen merken
    if (det && e.target.closest("summary")) {
      const [id, i] = det.dataset.reopen.split("|"), ed = RULE_EDITORS[id];
      setTimeout(() => { if (ed) det.open ? ed.open.add(+i) : ed.open.delete(+i); });
    }
    return false;
  }
  e.preventDefault();
  const [id, act, idx] = btn.dataset.rea.split("|"), ed = RULE_EDITORS[id], i = +idx;
  if (!ed) return true;
  const R = ed.set.rules, S = ed.set.signatures;
  switch (act) {
    case "sig-add": S.push({ name: "", text: "", stationeries: [], senders: [], default: S.length === 0 }); break;
    case "sig-del":
      if (!await confirmDialog("Signatur entfernen", `„${esc(S[i].name || "Neue Signatur")}“ entfernen?`, "Entfernen", true)) return true;
      S.splice(i, 1); break;
    case "rule-add": R.push({ name: "", enabled: true, documentTypes: [], stationeries: [], senders: [], recipients: [], customers: [], sources: [], formats: [], users: [], cc: [], bcc: [] }); ed.open.add(R.length - 1); break;
    case "rule-del":
      if (!await confirmDialog("Regel entfernen", `„${esc(R[i].name || "Neue Regel")}“ entfernen?`, "Entfernen", true)) return true;
      R.splice(i, 1); ed.open = new Set([...ed.open].filter(x => x !== i).map(x => x > i ? x - 1 : x)); break;
    case "up": case "down": {
      const j = act === "up" ? i - 1 : i + 1;
      if (j < 0 || j >= R.length) return true;
      [R[i], R[j]] = [R[j], R[i]];
      const oi = ed.open.has(i), oj = ed.open.has(j);
      ed.open.delete(i); ed.open.delete(j); if (oi) ed.open.add(j); if (oj) ed.open.add(i);
      break;
    }
  }
  ed.onChange?.(); ed.redraw?.();
  return true;
}

/* ---------------- Persönlich → Meine Mail-Regeln ---------------- */

async function renderMyRules(view) {
  view.innerHTML = `<div class="settings"><div class="notice" id="mrMsg" hidden></div><div id="mrBody"><p class="meta">Lade …</p></div></div>`;
  const say = (t, ok) => { const m = view.querySelector("#mrMsg"); m.hidden = !t; m.className = "notice" + (ok ? " ok" : ""); m.textContent = t || ""; };
  let data;
  try { data = await api("GET", "api/meine-regeln"); }
  catch (e) { view.querySelector("#mrBody").innerHTML = `<div class="notice">${esc(e.message)}</div>`; return; }
  let dirty = false;
  const ed = ruleEditor("mine", {
    set: data.set, choices: data.choices, global: false,
    onChange: () => { dirty = true; const b = view.querySelector("#mrSave"); if (b) b.disabled = false; },
    redraw: () => draw()
  });
  function draw() {
    view.querySelector("#mrBody").innerHTML = `<section class="st-card"><div class="st-card-head"><h3>Meine Signaturen und Mail-Regeln</h3>
        <button class="btn" id="mrSave" ${dirty ? "" : "disabled"}>Speichern</button></div>
      <div class="st-card-body"><p class="help">Hier legst du fest, mit welcher Signatur, welchem Absender und auf welchem Weg <b>deine</b> Belege verschickt werden –
        zum Beispiel eine eigene Signatur je Briefpapier oder Absenderadresse. Deine Regeln gelten vor den globalen.</p>
        ${ruleEditorHtml(ed)}</div></section>`;
  }
  draw();
  rteInstall(view);
  view.addEventListener("input", e => ruleEditorInput(e.target, e));
  view.addEventListener("change", e => ruleEditorInput(e.target, e));
  view.addEventListener("click", async e => {
    if (await ruleEditorClick(e)) return;
    if (e.target.closest("#mrSave")) {
      try { const r = await api("PUT", "api/meine-regeln", { set: ed.set }); dirty = false; draw(); say(r.message, true); }
      catch (x) { say(x.message); }
    }
  });
}
