"use strict";

/* =========================================================
   Vorgang → Rechnungsdaten bearbeiten
   Stammdaten und technische Angaben der E-Rechnung korrigieren, ohne im XML zu editieren.
   Mengen, Preise, Steuersätze und Summen bleiben wie gedruckt (müssen zur Sichtkopie passen).
   ========================================================= */

const RD_UNITS = [["H87", "Stück"], ["C62", "Einheit"], ["HUR", "Stunde"], ["MIN", "Minute"], ["DAY", "Tag"], ["WEE", "Woche"], ["MON", "Monat"], ["ANN", "Jahr"],
  ["KGM", "Kilogramm"], ["GRM", "Gramm"], ["TNE", "Tonne"], ["MTR", "Meter"], ["KMT", "Kilometer"], ["MTK", "Quadratmeter"], ["MTQ", "Kubikmeter"], ["LTR", "Liter"],
  ["SET", "Satz"], ["PR", "Paar"], ["XPK", "Packung"], ["XBX", "Karton"], ["XPX", "Palette"], ["LS", "Pauschale"], ["P1", "Prozent"]];
const RD_TYPES = [["380", "Rechnung"], ["381", "Gutschrift"], ["384", "Korrekturrechnung"], ["326", "Teilrechnung"], ["875", "Teilschlussrechnung"], ["389", "Gutschrift (Selbstfakturierung)"]];
const RD_MEANS = [["58", "SEPA-Überweisung"], ["59", "SEPA-Lastschrift"], ["30", "Überweisung (nicht SEPA)"], ["31", "Auslandsüberweisung"], ["42", "Zahlung auf Bankkonto"],
  ["48", "Bankkarte"], ["49", "Lastschrift (nicht SEPA)"], ["57", "Dauerauftrag"], ["10", "Bar"], ["1", "Nicht definiert"]];
const RD_ENDPOINTS = [["", "automatisch"], ["EM", "E-Mail (EM)"], ["0088", "GLN (0088)"], ["0204", "Leitweg-ID (0204)"], ["9930", "USt-IdNr. Deutschland (9930)"],
  ["9914", "USt-IdNr. Österreich (9914)"], ["0007", "Organisationsnummer SE (0007)"], ["0208", "Unternehmensnummer BE (0208)"]];
const RD_CATS = [["S", "S – Normal"], ["Z", "Z – Nullsatz"], ["E", "E – Steuerbefreit"], ["AE", "AE – Reverse Charge"], ["K", "K – Innergemeinschaftlich"], ["G", "G – Ausfuhr"], ["O", "O – Nicht steuerbar"]];

let RD = null;

async function renderRechnungsdaten(view) {
  const id = new URLSearchParams(location.hash.split("?")[1] || "").get("id");
  $("#topbarActions").innerHTML = `<a class="btn ghost" href="#/vorgang?id=${encodeURIComponent(id)}">${icon("back")} Zurück zum Vorgang</a>`;
  view.innerHTML = `<p class="meta">Lade Rechnungsdaten …</p>`;
  try { RD = await api("GET", `api/vorgaenge/${encodeURIComponent(id)}/rechnungsdaten`); }
  catch (e) { view.innerHTML = `<div class="notice">${esc(e.message)}</div>`; return; }
  drawRechnungsdaten(view);
}

function rdSelect(name, value, opts, dis) {
  const list = [...opts];
  if (value && !list.some(([k]) => k === value)) list.unshift([value, value]);
  return `<select data-rd="${esc(name)}" ${dis}>${list.map(([k, t]) => `<option value="${esc(k)}" ${k === (value || "") ? "selected" : ""}>${esc(t)}</option>`).join("")}</select>`;
}

function rdInput(f, dis) {
  const v = f.value ?? "";
  switch (f.type) {
    case "textarea": return `<textarea data-rd="${esc(f.key)}" rows="2" ${dis}>${esc(v)}</textarea>`;
    case "documentType": return rdSelect(f.key, v, RD_TYPES, dis);
    case "paymentMeans": return rdSelect(f.key, v, RD_MEANS, dis);
    case "endpointType": return rdSelect(f.key, v, RD_ENDPOINTS, dis);
    case "country": return `<input data-rd="${esc(f.key)}" value="${esc(v)}" maxlength="2" style="width:5em;text-transform:uppercase" ${dis}>`;
    case "date": return `<input data-rd="${esc(f.key)}" value="${esc(v)}" placeholder="TT.MM.JJJJ" style="width:9em" ${dis}>`;
    case "email": return `<input data-rd="${esc(f.key)}" type="email" value="${esc(v)}" ${dis}>`;
    default: return `<input data-rd="${esc(f.key)}" value="${esc(v)}" ${dis}>`;
  }
}

function drawRechnungsdaten(view) {
  const r = RD, dis = r.editable ? "" : "disabled";
  $("#viewTitle").textContent = `Rechnungsdaten ${r.documentType || ""} ${r.voucherId || ""}`.trim();
  const groups = [...new Set(r.fields.map(f => f.group))];
  const num = (x, d = 2) => x == null ? "" : Number(x).toLocaleString("de-AT", { minimumFractionDigits: d, maximumFractionDigits: 4 });
  const field = f => `<div class="rd-field${f.corrected ? " rd-corr" : ""}">
      <label>${esc(f.label)}${f.corrected ? ` <span class="badge partial" title="Gedruckt: ${esc(f.printed ?? "(leer)")}">korrigiert</span>` : ""}</label>
      ${rdInput(f, dis)}
      ${f.corrected ? `<small class="meta">gedruckt: ${esc(f.printed ?? "(leer)")} ${r.editable ? `<a href="#" data-rd-reset="${esc(f.key)}">zurücksetzen</a>` : ""}</small>` : f.help ? `<small class="meta">${esc(f.help)}</small>` : ""}
    </div>`;
  const msgs = (r.messages || []).filter(m => m.level !== "info" || /E-Rechnung|erzeugt/.test(m.text));
  view.innerHTML = `
    <div class="notice ok">Hier korrigierst du <b>Stammdaten und technische Angaben</b> der E-Rechnung – Belegpost erzeugt und prüft die XML danach neu.
      <b>Mengen, Preise, Steuersätze und Summen</b> bleiben wie gedruckt, damit E-Rechnung und PDF übereinstimmen; solche Fehler bitte im ERP korrigieren und neu drucken.</div>
    ${r.why ? `<div class="notice">${esc(r.why)}</div>` : ""}
    <div class="card"><div class="card-head"><h2>${esc(r.format || "E-Rechnung")}</h2>
      <span class="meta-inline">${r.eInvoice ? `erzeugt: ${esc(r.eInvoice)}` : "noch keine E-Rechnung erzeugt"}${r.changedAt ? ` · zuletzt korrigiert ${fmtDT(r.changedAt)} von ${esc(r.changedBy || "")}` : ""}</span></div>
      ${msgs.length ? `<ul class="rd-msgs">${msgs.map(m => `<li><span class="badge ${m.level === "fehler" ? "missing" : m.level === "warnung" ? "partial" : ""}">${m.level === "fehler" ? "Fehler" : m.level === "warnung" ? "Warnung" : "Hinweis"}</span> ${esc(m.text)}</li>`).join("")}</ul>` : '<p class="meta">Keine Meldungen.</p>'}
    </div>
    <div class="rd-groups">${groups.map(g => `<div class="card"><h3>${esc(g)}</h3>${r.fields.filter(f => f.group === g).map(field).join("")}</div>`).join("")}</div>
    <div class="card"><h3>Positionen</h3>
      <div class="table-wrap"><table class="list rd-lines"><thead><tr><th>Pos</th><th>Art.-Nr.</th><th>Bezeichnung / Beschreibung</th><th class="num">Menge</th><th>Einheit</th><th class="num">Preis</th><th class="num">USt</th><th>Kategorie</th><th class="num">Betrag</th></tr></thead>
      <tbody>${r.lines.map(l => `<tr class="${l.corrected ? "rd-corr" : ""}" data-rd-line="${l.index}">
        <td>${esc(l.lineId || l.index + 1)}</td>
        <td><input data-rl="sellerId" value="${esc(l.sellerId || "")}" style="width:8em" ${dis}></td>
        <td><input data-rl="name" value="${esc(l.name || "")}" ${dis}><textarea data-rl="description" rows="1" placeholder="Beschreibung" ${dis}>${esc(l.description || "")}</textarea></td>
        <td class="num">${num(l.quantity, 0)}</td>
        <td>${rdSelect("unit", l.unitCode || "", [["", "– wählen –"], ...RD_UNITS.map(([k, t]) => [k, `${k} – ${t}`])], dis).replace('data-rd="unit"', 'data-rl="unitCode"')}
          <small class="meta">gedruckt: ${esc(l.unitText || "–")}</small></td>
        <td class="num">${num(l.price)}${l.baseQuantity && l.baseQuantity != 1 ? `<small>je ${num(l.baseQuantity, 0)}</small>` : ""}</td>
        <td class="num">${num(l.vatPercent, 0)} %</td>
        <td>${Number(l.vatPercent || 0) === 0 ? rdSelect("cat", l.vatCategory, RD_CATS.filter(([k]) => k !== "S"), dis).replace('data-rd="cat"', 'data-rl="vatCategory"') : esc(l.vatCategory)}</td>
        <td class="num">${num(l.total)}</td></tr>`).join("")}</tbody></table></div>
      <p class="meta">Netto ${num(r.totals.net)} · Steuer ${num(r.totals.tax)} · Brutto ${num(r.totals.gross)} ${esc(r.totals.currency)}${r.totals.printedGross != null ? ` (gedruckt ${num(r.totals.printedGross)})` : ""} – gesperrt.</p>
    </div>
    ${r.editable ? `<div class="filters rd-actions"><button class="btn" id="rdSave">Speichern, E-Rechnung neu erzeugen und prüfen</button>
      <button class="btn ghost" id="rdReset">Alle Korrekturen verwerfen</button>
      <a class="btn ghost" href="#/pruefen?vorgang=${encodeURIComponent(r.id)}">E-Rechnung prüfen</a></div>` : ""}
    <div class="notice" id="rdMsg" hidden></div>`;

  const msg = (t, ok) => { const n = $("#rdMsg"); n.hidden = !t; n.className = "notice" + (ok ? " ok" : ""); n.textContent = t || ""; };
  const collect = () => {
    const fields = {};
    view.querySelectorAll("[data-rd]").forEach(el => fields[el.dataset.rd] = el.dataset.rd.endsWith("_COUNTRY") ? el.value.trim().toUpperCase() : el.value);
    const lines = [...view.querySelectorAll("[data-rd-line]")].map(tr => {
      const o = { index: +tr.dataset.rdLine };
      tr.querySelectorAll("[data-rl]").forEach(el => o[el.dataset.rl] = el.value);
      return o;
    });
    return { fields, lines };
  };
  const save = async body => {
    msg("");
    try { const res = await api("PUT", `api/vorgaenge/${encodeURIComponent(r.id)}/rechnungsdaten`, body); toast(res.message); RD = await api("GET", `api/vorgaenge/${encodeURIComponent(r.id)}/rechnungsdaten`); drawRechnungsdaten(view); }
    catch (e) { msg(e.message); }
  };
  $("#rdSave")?.addEventListener("click", e => { e.target.disabled = true; e.target.textContent = "Wird erzeugt …"; save(collect()); });
  $("#rdReset")?.addEventListener("click", async () => {
    if (await confirmDialog("Korrekturen verwerfen", "Alle Korrekturen an den Rechnungsdaten entfernen? Es gelten wieder die gedruckten Werte.", "Verwerfen", true)) save({ fields: {}, lines: [] });
  });
  view.querySelectorAll("[data-rd-reset]").forEach(a => a.addEventListener("click", e => {
    e.preventDefault();
    const f = r.fields.find(x => x.key === a.dataset.rdReset);
    const el = view.querySelector(`[data-rd="${CSS.escape(f.key)}"]`);
    if (el) el.value = f.printed ?? "";
    a.closest(".rd-field")?.classList.remove("rd-corr");
  }));
}
