"use strict";

/* =========================================================
   Vorgänge: Liste, Prüfen, PDF-Vorschau, Versand
   ========================================================= */

const STATUS = {
  fehler: ["Fehler", "missing"],
  warnung: ["Prüfen", "partial"],
  bereit: ["Bereit", "ok"],
  ohneVersand: ["Ohne Versand", "free"],
  inVersand: ["Wird gesendet", "absent"],
  versendet: ["Versendet", "sent"],
  versandfehler: ["Versandfehler", "missing"],
  verworfen: ["Verworfen", "free"]
};
const FILTERS = {
  offen: { title: "Zu prüfen", status: "fehler,warnung,bereit,versandfehler,inVersand" },
  meine: { title: "Meine Belege", status: "", mine: true },
  alle: { title: "Alle Vorgänge", status: "" },
  versendet: { title: "Versendet", status: "versendet" },
  verworfen: { title: "Verworfen", status: "verworfen" }
};
const badge = s => { const [t, c] = STATUS[s] || [s, ""]; return `<span class="badge ${c}">${esc(t)}</span>`; };
const fmtMoney = (v, cur) => v === null || v === undefined ? "" : Number(v).toLocaleString("de-AT", { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + (cur && cur !== "EUR" ? " " + cur : " €");
const fmtDT = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "";
const listText = l => (l || []).join(", ");

/* ---------------- Liste ---------------- */

const VL = { timer: null, selected: new Set(), items: [] };

async function renderVorgaenge(view) {
  clearInterval(VL.timer);
  const q = new URLSearchParams(location.hash.split("?")[1] || "");
  const fkey = FILTERS[q.get("f")] ? q.get("f") : "offen";
  const f = FILTERS[fkey];
  $("#viewTitle").textContent = f.title;
  VL.selected.clear();
  const search = store.get("vl.q", "");

  view.innerHTML = `
    <div class="toolbar">
      <div class="filters">
        <input type="search" id="vlSearch" placeholder="Beleg, Kunde, Empfänger suchen" value="${esc(search)}" aria-label="Suchen">
        ${fkey === "alle" ? `<select id="vlStatus" aria-label="Status"><option value="">Alle Status</option>
          ${Object.entries(STATUS).map(([k, [t]]) => `<option value="${k}">${esc(t)}</option>`).join("")}</select>` : ""}
        <input type="date" id="vlFrom" aria-label="Von" title="Eingang von">
        <input type="date" id="vlTo" aria-label="Bis" title="Eingang bis">
      </div>
      <div class="filters">
        ${portal.canSend ? `<button class="btn ghost" id="vlUpload">${icon("upload")} PDF hochladen</button>
          <input type="file" id="vlFile" accept="application/pdf,.pdf" multiple hidden>
          <button class="btn" id="vlSendSel" disabled>${icon("send")} Ausgewählte senden</button>` : ""}
        <button class="btn ghost" id="vlReload">Aktualisieren</button>
      </div>
    </div>
    <div class="notice" id="vlMsg" hidden></div>
    <div class="kpis" id="vlKpis"></div>
    <div class="card">
      <div class="table-wrap"><table class="list vlist" id="vlTable"><tbody><tr><td class="meta">Lade Vorgänge …</td></tr></tbody></table></div>
    </div>`;

  const load = async () => {
    const p = new URLSearchParams();
    const st = $("#vlStatus")?.value || f.status;
    if (st) p.set("status", st);
    if (f.mine) p.set("mine", "1");
    const s = $("#vlSearch").value.trim(); if (s) p.set("q", s);
    if ($("#vlFrom").value) p.set("from", $("#vlFrom").value);
    if ($("#vlTo").value) p.set("to", $("#vlTo").value);
    try {
      const r = await api("GET", "api/vorgaenge?" + p);
      VL.items = r.items;
      drawKpis(r);
      drawTable();
      updateMenuCounts(r.counts);
    } catch (e) { showMsg(e.message); }
  };
  const showMsg = (t, ok) => { const n = $("#vlMsg"); n.hidden = !t; n.className = "notice" + (ok ? " ok" : ""); n.textContent = t || ""; };

  let deb;
  $("#vlSearch").oninput = () => { clearTimeout(deb); deb = setTimeout(() => { store.set("vl.q", $("#vlSearch").value); load(); }, 300); };
  for (const id of ["vlStatus", "vlFrom", "vlTo"]) { const el = $("#" + id); if (el) el.onchange = load; }
  $("#vlReload").onclick = load;
  if (portal.canSend) {
    $("#vlUpload").onclick = () => $("#vlFile").click();
    $("#vlFile").onchange = async e => {
      const files = [...e.target.files]; if (!files.length) return;
      const fd = new FormData(); files.forEach(x => fd.append("files", x));
      try {
        const r = await api("POST", "api/vorgaenge/upload", fd, true);
        showMsg(`${r.created.length} Vorgang/Vorgänge angelegt.`, true);
        if (r.created.length === 1) { location.hash = "#/vorgang?id=" + encodeURIComponent(r.created[0].id); return; }
        load();
      } catch (ex) { showMsg(ex.message); }
      e.target.value = "";
    };
    $("#vlSendSel").onclick = async () => {
      const ids = [...VL.selected];
      if (!ids.length) return;
      if (!await confirmDialog("Ausgewählte senden", `${ids.length} Vorgang/Vorgänge jetzt per E-Mail versenden? Vorgänge mit Fehlern werden übersprungen.`, "Senden")) return;
      try {
        const r = await api("POST", "api/vorgaenge/senden", { ids });
        const ok = r.results.filter(x => x.ok).length, bad = r.results.filter(x => !x.ok);
        showMsg(`${ok} versendet${bad.length ? `, ${bad.length} nicht versendet: ` + bad.map(b => b.message).join(" · ") : "."}`, bad.length === 0);
        VL.selected.clear();
        load();
      } catch (ex) { showMsg(ex.message); }
    };
  }

  $("#vlTable").addEventListener("click", e => {
    const cb = e.target.closest("input[type=checkbox]");
    if (cb) {
      if (cb.dataset.all !== undefined) {
        VL.items.filter(selectable).forEach(v => cb.checked ? VL.selected.add(v.id) : VL.selected.delete(v.id));
        drawTable();
      } else { cb.checked ? VL.selected.add(cb.dataset.id) : VL.selected.delete(cb.dataset.id); updateSel(); }
      return;
    }
    const tr = e.target.closest("tr[data-id]");
    if (tr) location.hash = "#/vorgang?id=" + encodeURIComponent(tr.dataset.id);
  });

  await load();
  VL.timer = setInterval(() => { if (!document.hidden && document.contains(view)) load(); else if (!document.contains(view)) clearInterval(VL.timer); }, 20000);
}

const selectable = v => ["bereit", "warnung", "versandfehler"].includes(v.status) && v.mailEnabled && v.to?.length;

function updateSel() {
  const b = $("#vlSendSel");
  if (b) { b.disabled = VL.selected.size === 0; b.innerHTML = `${icon("send")} ${VL.selected.size ? VL.selected.size + " " : "Ausgewählte "}senden`; }
}

function drawKpis(r) {
  const c = r.counts;
  const k = (label, value, sub, cls = "") => `<div class="kpi ${cls}"><span class="kpi-label">${label}</span><span class="kpi-value">${value}</span><span class="kpi-sub">${sub}</span></div>`;
  $("#vlKpis").innerHTML =
    k("Fehler", c.fehler || 0, "E-Rechnung/Versand nicht möglich", c.fehler ? "alert" : "") +
    k("Zu prüfen", c.warnung || 0, "mit Warnungen") +
    k("Bereit", c.bereit || 0, portal.autoSend ? "Autoversand aktiv" : "manuell versenden") +
    k("Versandfehler", c.versandfehler || 0, "erneut versuchen", c.versandfehler ? "alert" : "") +
    k("Heute versendet", r.sentToday || 0, `${c.versendet || 0} insgesamt`);
}

function drawTable() {
  const rows = VL.items;
  const canSel = portal.canSend;
  if (!rows.length) { $("#vlTable").innerHTML = `<tbody><tr><td class="empty">Keine Vorgänge.</td></tr></tbody>`; updateSel(); return; }
  const allSel = rows.filter(selectable).length > 0 && rows.filter(selectable).every(v => VL.selected.has(v.id));
  $("#vlTable").innerHTML = `<thead><tr>
      ${canSel ? `<th class="cb"><input type="checkbox" data-all ${allSel ? "checked" : ""} title="Alle versendbaren auswählen"></th>` : ""}
      <th>Eingang</th><th>Beleg</th><th>Kunde</th><th class="num">Betrag</th><th>Empfänger</th><th>E-Rechnung</th><th>Status</th></tr></thead>
    <tbody>${rows.map(v => `<tr data-id="${esc(v.id)}" class="st-${esc(v.status)}">
      ${canSel ? `<td class="cb">${selectable(v) ? `<input type="checkbox" data-id="${esc(v.id)}" ${VL.selected.has(v.id) ? "checked" : ""}>` : ""}</td>` : ""}
      <td class="nowrap">${fmtDT(v.createdAt)}<small>${esc(v.ownerName || v.printUser || v.source)}</small></td>
      <td><b>${esc(v.documentType)} ${esc(v.voucherId || "")}</b><small>${esc(v.voucherDate || "")}</small></td>
      <td>${esc(v.buyerName || "")}<small>${esc(v.buyerId || "")}</small></td>
      <td class="num nowrap">${fmtMoney(v.grossAmount, v.currency)}</td>
      <td>${v.mailEnabled ? esc(listText(v.to)) || '<span class="muted">–</span>' : '<span class="muted">kein Versand</span>'}</td>
      <td>${esc(v.formatName || "–")}</td>
      <td>${badge(v.status)}${v.firstMessage && !["versendet", "verworfen"].includes(v.status) ? `<small class="msg">${esc(v.firstMessage)}</small>` : ""}
        ${v.sentAt ? `<small>${fmtDT(v.sentAt)} · ${esc(v.sentBy || "")}</small>` : ""}</td>
    </tr>`).join("")}</tbody>`;
  updateSel();
}

/* ---------------- Detail ---------------- */

const HEAD_FIELDS = [
  ["Beleg", [["VOUCHER_ID", "Nummer"], ["VOUCHER_DATE", "Datum"], ["DELIVERY_DATE", "Lieferdatum"], ["CURRENCY", "Währung"],
    ["PAYMENT_TERMS_DUE_DATE", "Fällig"], ["PAYMENT_TERMS_DESCRIPTION", "Zahlungskondition"], ["PAYMENT_MEANS_TYPE_CODE", "Zahlungsart"],
    ["ROUTING_ID", "Leitweg-ID"], ["BUYER_ORDER_ID", "Bestellnummer"], ["INVOICE_NOTE", "Notiz"]]],
  ["Verkäufer", [["SELLER_NAME", "Name"], ["SELLER_ADDRESS_LINE", "Straße"], ["SELLER_ADDRESS_ZIP", "PLZ"], ["SELLER_ADDRESS_CITY", "Ort"],
    ["SELLER_ADDRESS_COUNTRY", "Land"], ["SELLER_TAX_ID", "USt-IdNr."], ["SELLER_TAX_REG_ID", "Steuernummer"], ["SELLER_IBAN", "IBAN"], ["SELLER_BIC", "BIC"],
    ["SELLER_CONTACT_NAME", "Kontakt"], ["SELLER_CONTACT_PHONE", "Telefon"], ["SELLER_CONTACT_EMAIL", "E-Mail"], ["SELLER_ENDPOINT_ID", "Elektr. Adresse"]]],
  ["Käufer", [["BUYER_ID", "Kundennummer"], ["BUYER_NAME", "Name"], ["BUYER_ADDRESS_LINE", "Straße"], ["BUYER_ADDRESS_ZIP", "PLZ"], ["BUYER_ADDRESS_CITY", "Ort"],
    ["BUYER_ADDRESS_COUNTRY", "Land"], ["BUYER_TAX_ID", "USt-IdNr."], ["BUYER_CONTACT_EMAIL", "E-Mail"], ["BUYER_ENDPOINT_ID", "Elektr. Adresse"],
    ["SHIPPING_ADDRESS_NAME", "Lieferadresse"], ["SHIPPING_ADDRESS_LINE", "Lief. Straße"], ["SHIPPING_ADDRESS_CITY", "Lief. Ort"]]]
];
const PAYMENT_MEANS = { "58": "SEPA-Überweisung", "59": "SEPA-Lastschrift", "30": "Überweisung", "10": "Bar", "48": "Karte" };

async function renderVorgang(view) {
  const id = new URLSearchParams(location.hash.split("?")[1] || "").get("id");
  $("#topbarActions").innerHTML = `<a class="btn ghost" href="#/vorgaenge" id="vdBack">${icon("back")} Zurück zur Liste</a>`;
  $("#vdBack").onclick = e => { e.preventDefault(); history.length > 1 ? history.back() : (location.hash = "#/vorgaenge"); };
  view.classList.add("vd-view");
  view.innerHTML = `<p class="meta">Lade Vorgang …</p>`;
  let data;
  try { data = await api("GET", "api/vorgaenge/" + encodeURIComponent(id)); }
  catch (e) { view.innerHTML = `<div class="notice">${esc(e.message)}</div>`; return; }
  drawVorgang(view, data);
}

function drawVorgang(view, data) {
  const v = data.vorgang, inv = v.invoice || {}, f = inv.fields || {};
  const editable = portal.canSend && !["versendet", "inVersand", "verworfen"].includes(v.status);
  $("#viewTitle").textContent = v.voucherId ? `${v.documentType} ${v.voucherId}` : (v.mail.fileName || v.originalFileName || v.documentType);
  const msgs = v.messages || [];
  const lvl = { fehler: ["missing", "Fehler"], warnung: ["partial", "Warnung"], info: ["free", "Hinweis"] };

  const actions = [];
  if (portal.canSend) {
    if (["bereit", "warnung", "versandfehler"].includes(v.status) && v.mail.enabled) actions.push(`<button class="btn" data-a="senden">${icon("send")} Jetzt senden</button>`);
    if (v.status === "fehler" && v.mail.enabled) actions.push(`<button class="btn danger-ghost" data-a="senden-force">Trotz Fehler senden …</button>`);
    if (v.status === "versendet") actions.push(`<button class="btn ghost" data-a="erneut">Erneut senden …</button>`);
    if (!["versendet", "inVersand", "verworfen"].includes(v.status)) actions.push(`<button class="btn ghost" data-a="pruefen">Neu prüfen</button>`, `<button class="btn ghost" data-a="verwerfen">Verwerfen</button>`);
    if (v.status === "verworfen") actions.push(`<button class="btn ghost" data-a="wiederherstellen">Wiederherstellen</button>`);
  }
  actions.push(`<a class="btn ghost" href="api/vorgaenge/${encodeURIComponent(v.id)}/pdf?download=true">PDF herunterladen</a>`);
  if (portal.isAdmin && ["verworfen", "versendet", "ohneVersand"].includes(v.status)) actions.push(`<button class="btn ghost" data-a="loeschen">Löschen</button>`);

  const autoInfo = data.autoSendAt && ["bereit", "warnung"].includes(v.status)
    ? `<div class="notice info">Autoversand: wird ${new Date(data.autoSendAt) > new Date() ? "um " + fmtDT(data.autoSendAt) : "in Kürze"} automatisch versendet.</div>` : "";

  const kv = rows => rows.filter(([k]) => f[k]).map(([k, l]) => `<tr><th>${esc(l)}</th><td>${esc(k === "PAYMENT_MEANS_TYPE_CODE" ? (PAYMENT_MEANS[f[k]] || f[k]) : f[k])}</td></tr>`).join("");
  const lines = inv.lines || [];
  const n = x => x === null || x === undefined ? "" : Number(x).toLocaleString("de-AT", { maximumFractionDigits: 4 });
  const m2 = x => x === null || x === undefined ? "" : Number(x).toLocaleString("de-AT", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  view.innerHTML = `
    <div class="vd">
      <div class="vd-pdf card">
        <div class="card-head"><h2>Vorschau</h2>
          ${data.hasSendPdf ? `<div class="pdf-switch"><button data-pdf="versand" class="active">Versand-PDF</button><button data-pdf="original">Original</button></div>` : ""}
          <span class="meta-inline">${v.pages} Seite(n) · ${esc(v.source)}${v.originalFileName ? " · " + esc(v.originalFileName) : ""}
            ${v.printUser || v.ownerName ? ` · gedruckt von ${esc(v.ownerName || v.printUser)}${v.ownerName && v.printUser && v.printUser !== v.ownerName ? ` (${esc(v.printUser)})` : ""}${v.printUser && !v.ownerUserId ? ' <span class="badge partial" title="Diesem Druck-Benutzer ist kein Portal-Benutzer zugeordnet (Einstellungen → Benutzer → Druck-Benutzer)">nicht zugeordnet</span>' : ""}` : ""}</span></div>
        <iframe id="vdFrame" src="api/vorgaenge/${encodeURIComponent(v.id)}/pdf#view=FitH" title="PDF-Vorschau"></iframe>
        <p class="help" id="vdPdfHelp">${data.hasSendPdf
          ? `Versand-PDF: so geht der Beleg hinaus – ${v.removedCodes ? `${v.removedCodes} Steuerzeichen entfernt` : "keine Steuerzeichen"}${v.stationery ? `, Briefpapier „${esc(v.stationery)}“` : ", ohne Briefpapier"}.`
          : "Kein Versand-PDF – bitte die Meldungen prüfen und „Neu prüfen“."}</p>
      </div>
      <div class="vd-side">
        <div class="card">
          <div class="card-head"><h2>Status</h2>${badge(v.status)}</div>
          ${autoInfo}
          ${v.sendError && v.status === "versandfehler" ? `<div class="notice">Versandfehler: ${esc(v.sendError)}</div>` : ""}
          ${v.sentAt ? `<p class="meta">Versendet ${fmtDT(v.sentAt)} von ${esc(v.sentBy || "")}</p>` : ""}
          <ul class="checks">${msgs.length ? msgs.map(m => `<li class="lv-${m.level}"><span class="badge ${lvl[m.level]?.[0] || ""}">${lvl[m.level]?.[1] || m.level}</span> ${esc(m.text)}</li>`).join("")
            : `<li class="lv-ok"><span class="badge ok">OK</span> Keine Beanstandungen.</li>`}</ul>
          <div class="actions">${actions.join("")}</div>
          <div class="notice" id="vdMsg" hidden></div>
        </div>

        <div class="card">
          <div class="card-head"><h2>Versand</h2>${editable ? `<label class="check"><input type="checkbox" id="mEnabled" ${v.mail.enabled ? "checked" : ""}> per E-Mail versenden</label>` : (v.mail.enabled ? "" : '<span class="badge free">kein Versand</span>')}</div>
          <div class="mailform">
            <label class="field"><span class="lbl">An</span><input id="mTo" value="${esc(listText(v.mail.to))}" ${editable ? "" : "readonly"} placeholder="empfaenger@kunde.at; weitere@kunde.at"></label>
            <div class="grid2">
              <label class="field"><span class="lbl">CC</span><input id="mCc" value="${esc(listText(v.mail.cc))}" ${editable ? "" : "readonly"}></label>
              <label class="field"><span class="lbl">BCC</span><input id="mBcc" value="${esc(listText(v.mail.bcc))}" ${editable ? "" : "readonly"}></label>
            </div>
            <label class="field"><span class="lbl">Betreff</span><input id="mSubject" value="${esc(v.mail.subject)}" ${editable ? "" : "readonly"}></label>
            <label class="field"><span class="lbl">Text</span><textarea id="mBody" rows="7" ${editable ? "" : "readonly"}>${esc(v.mail.body)}</textarea></label>
            <label class="field"><span class="lbl">Dateiname des Anhangs</span><div class="suffix"><input id="mFile" value="${esc(v.mail.fileName)}" ${editable ? "" : "readonly"}><span>.pdf</span></div></label>
            ${v.mail.attachments?.length ? `<p class="meta">Weitere Anlagen: ${v.mail.attachments.map(esc).join(", ")}</p>` : ""}
            ${v.mail.noDocument ? `<p class="meta">Ohne Beleg als Anhang (@@md@@).</p>` : ""}
            <p class="meta">${v.mail.template ? `Mail-Vorlage <b>${esc(v.mail.template)}</b> (${esc(v.mail.templateReason || "")})` : "Ohne Mail-Vorlage (schlichte Mail)"}${v.mail.fromAddress ? ` · Absender ${esc(v.mail.fromName ? v.mail.fromName + " <" + v.mail.fromAddress + ">" : v.mail.fromAddress)}` : ""}
              · <a href="#" id="mPreviewBtn">So sieht die Mail aus</a></p>
            <div id="mPreview"></div>
            ${editable ? `<div class="actions"><button class="btn" data-a="mail-save" disabled>Versanddaten speichern</button><span class="meta-inline">Änderungen werden geprüft und bleiben bei „Neu prüfen“ erhalten.</span></div>` : ""}
          </div>
        </div>

        <div class="card">
          <div class="card-head"><h2>Rechnungsdaten</h2><span class="meta-inline">${esc(v.formatName || "keine E-Rechnung angefordert")}</span></div>
          ${Object.keys(f).length ? `<div class="vd-fields">${HEAD_FIELDS.map(([t, rows]) => { const body = kv(rows); return body ? `<div><h3>${t}</h3><table class="kv">${body}</table></div>` : ""; }).join("")}</div>` : `<p class="meta">Keine Rechnungsdaten (@@xi@@) im Dokument.</p>`}
          ${lines.length ? `<h3>Positionen</h3><div class="table-wrap"><table class="list mini"><thead><tr><th>Pos</th><th>Artikel</th><th class="num">Menge</th><th>Einheit</th><th class="num">Preis</th><th class="num">Rabatt</th><th class="num">USt</th><th class="num">Netto</th></tr></thead><tbody>
            ${lines.map(l => `<tr><td>${esc(l.lineId || "")}</td><td><b>${esc(l.name || "")}</b><small>${esc([l.articleNo, l.description].filter(Boolean).join(" · "))}</small></td>
              <td class="num">${n(l.quantity)}</td><td>${esc(l.unitText || "")}${l.unitCode ? ` <small>${esc(l.unitCode)}</small>` : l.unitText ? ' <small class="bad">?</small>' : ""}</td>
              <td class="num">${m2(l.price)}${l.baseQuantity && l.baseQuantity != 1 ? `<small>/ ${n(l.baseQuantity)}</small>` : ""}</td><td class="num">${l.allowance ? m2(l.allowance) : ""}</td>
              <td class="num">${n(l.vatPercent)} %<small>${esc(l.vatCategory || "")}</small></td><td class="num">${m2(l.total)}</td></tr>`).join("")}
          </tbody></table></div>` : ""}
          ${(inv.charges || []).length ? `<h3>Zu-/Abschläge</h3><table class="kv">${inv.charges.map(c => `<tr><th>${c.isCharge ? "Zuschlag" : "Abschlag"}: ${esc(c.reason || "")}</th><td class="num">${m2(c.amount)} (${n(c.vatPercent)} %)</td></tr>`).join("")}</table>` : ""}
          ${lines.length ? `<h3>Summen (nachgerechnet)</h3><table class="kv totals">
            <tr><th>Positionen</th><td class="num">${m2(inv.lineTotal)}</td></tr>
            ${inv.chargeTotal ? `<tr><th>+ Zuschläge</th><td class="num">${m2(inv.chargeTotal)}</td></tr>` : ""}
            ${inv.allowanceTotal ? `<tr><th>− Abschläge</th><td class="num">${m2(inv.allowanceTotal)}</td></tr>` : ""}
            <tr><th>Netto</th><td class="num">${m2(inv.netAmount)}</td></tr>
            ${(inv.vat || []).map(x => `<tr><th>USt ${n(x.percent)} % (${esc(x.category)}) auf ${m2(x.basis)}</th><td class="num">${m2(x.tax)}</td></tr>`).join("")}
            <tr class="sum"><th>Brutto</th><td class="num">${m2(inv.grossAmount)}</td></tr>
            ${inv.printedGrossAmount !== null && inv.printedGrossAmount !== undefined ? `<tr><th>Brutto laut Beleg</th><td class="num ${Math.abs(inv.printedGrossAmount - inv.grossAmount) > 0.02 ? "bad" : "good"}">${m2(inv.printedGrossAmount)}</td></tr>` : ""}
            ${inv.prepaidAmount ? `<tr><th>Bereits bezahlt</th><td class="num">${m2(inv.prepaidAmount)}</td></tr>` : ""}
            ${inv.duePayableAmount !== null && inv.duePayableAmount !== undefined ? `<tr><th>Offen</th><td class="num">${m2(inv.duePayableAmount)}</td></tr>` : ""}
          </table>` : ""}
        </div>

        <div class="card">
          <details><summary><b>Verlauf</b> (${(v.history || []).length})</summary>
            <table class="list mini">${(v.history || []).slice().reverse().map(h => `<tr><td class="nowrap">${fmtDT(h.at)}</td><td>${esc(h.by)}</td><td>${esc(h.action)}</td><td>${esc(h.text || "")}</td></tr>`).join("")}</table>
          </details>
          <details><summary><b>Steuerzeichen</b> (${(v.codes || []).length})</summary>
            <div class="codes">${(v.codes || []).map(c => `<div><span class="pg">S.${c.page}</span><code>@@${esc(c.code)}${esc(c.value)}@@</code></div>`).join("") || '<p class="meta">Keine Steuerzeichen gefunden.</p>'}</div>
            <p><a href="api/vorgaenge/${encodeURIComponent(v.id)}/text" target="_blank" rel="noopener">Ausgelesenen Text anzeigen</a></p>
          </details>
        </div>
      </div>
    </div>`;

  $("#mPreviewBtn")?.addEventListener("click", async e => {
    e.preventDefault();
    const box = $("#mPreview");
    if (box.innerHTML) { box.innerHTML = ""; return; }
    try {
      const r = await api("GET", `api/vorgaenge/${encodeURIComponent(v.id)}/mailvorschau`);
      box.innerHTML = `<div class="mail-preview"><div class="mail-preview-head">
          <span><b>Von:</b> ${esc(r.fromName ? `${r.fromName} <${r.from}>` : r.from || "–")}</span><span><b>Betreff:</b> ${esc(r.subject)}</span>
          <span class="meta-inline">gespeicherter Stand – nach Änderungen zuerst speichern</span></div>
        <iframe sandbox="" title="Vorschau Mail"></iframe></div>`;
      box.querySelector("iframe").srcdoc = r.html;
    } catch (err) { box.innerHTML = `<p class="bad">${esc(err.message)}</p>`; }
  });

  view.querySelector(".pdf-switch")?.addEventListener("click", e => {
    const b = e.target.closest("[data-pdf]"); if (!b) return;
    view.querySelectorAll(".pdf-switch button").forEach(x => x.classList.toggle("active", x === b));
    $("#vdFrame").src = `api/vorgaenge/${encodeURIComponent(v.id)}/pdf${b.dataset.pdf === "original" ? "?variant=original" : ""}#view=FitH`;
  });

  const msg = (t, ok) => { const el = $("#vdMsg"); el.hidden = !t; el.className = "notice" + (ok ? " ok" : ""); el.textContent = t || ""; };
  const split = s => s.split(/[;,\s]+/).map(x => x.trim()).filter(Boolean);

  if (editable) {
    const saveBtn = view.querySelector('[data-a="mail-save"]');
    view.querySelector(".mailform").addEventListener("input", () => { saveBtn.disabled = false; });
    $("#mEnabled")?.addEventListener("change", () => { saveBtn.disabled = false; });
  }

  view.querySelector(".vd-side").addEventListener("click", async e => {
    const b = e.target.closest("[data-a]"); if (!b) return;
    const a = b.dataset.a;
    b.disabled = true;
    try {
      let r;
      if (a === "mail-save") {
        r = await api("PUT", `api/vorgaenge/${encodeURIComponent(v.id)}/mail`, {
          to: split($("#mTo").value), cc: split($("#mCc").value), bcc: split($("#mBcc").value),
          subject: $("#mSubject").value, body: $("#mBody").value, fileName: $("#mFile").value, enabled: $("#mEnabled")?.checked ?? v.mail.enabled
        });
        drawVorgang(view, { ...data, vorgang: r.vorgang });
        toast("Versanddaten gespeichert.");
        return;
      }
      // ungespeicherte Versanddaten vor dem Senden speichern
      const dirty = view.querySelector('[data-a="mail-save"]') && !view.querySelector('[data-a="mail-save"]').disabled;
      if (dirty && a.startsWith("senden")) {
        await api("PUT", `api/vorgaenge/${encodeURIComponent(v.id)}/mail`, {
          to: split($("#mTo").value), cc: split($("#mCc").value), bcc: split($("#mBcc").value),
          subject: $("#mSubject").value, body: $("#mBody").value, fileName: $("#mFile").value, enabled: $("#mEnabled")?.checked ?? v.mail.enabled
        });
      }
      let body = { action: a };
      if (a === "senden-force") {
        if (!await confirmDialog("Trotz Fehler senden", "Der Vorgang hat Fehler (z. B. kann die E-Rechnung nicht erzeugt werden). Trotzdem nur das PDF per E-Mail senden?", "Trotzdem senden", true)) return;
        body = { action: "senden", force: true };
      }
      if (a === "erneut" && !await confirmDialog("Erneut senden", `Den Beleg nochmals an ${esc(listText(v.mail.to))} senden?`, "Erneut senden")) return;
      if (a === "verwerfen" && !await confirmDialog("Verwerfen", "Den Vorgang verwerfen? Er wird nicht versendet und kann wiederhergestellt werden.", "Verwerfen", true)) return;
      if (a === "loeschen") {
        if (!await confirmDialog("Löschen", "Den Vorgang samt PDF endgültig löschen?", "Löschen", true)) return;
        await api("POST", `api/vorgaenge/${encodeURIComponent(v.id)}/aktion`, { action: "loeschen" });
        toast("Gelöscht."); location.hash = "#/vorgaenge"; return;
      }
      if (a.startsWith("senden") || a === "erneut") { b.textContent = "Wird gesendet …"; }
      r = await api("POST", `api/vorgaenge/${encodeURIComponent(v.id)}/aktion`, body);
      const fresh = await api("GET", "api/vorgaenge/" + encodeURIComponent(v.id));
      drawVorgang(view, fresh);
      toast(r.message || "Erledigt.");
    } catch (ex) {
      if (ex.data?.vorgang) { const fresh = await api("GET", "api/vorgaenge/" + encodeURIComponent(v.id)).catch(() => null); if (fresh) drawVorgang(view, fresh); }
      const el = $("#vdMsg"); if (el) { el.hidden = false; el.className = "notice"; el.textContent = ex.message; }
      else toast(ex.message, false);
    } finally { if (document.contains(b)) b.disabled = false; }
  });
}
