"use strict";

/* =========================================================
   Gotomaxx → je Drucker (gotomaxx-Profil): Protokoll mit Belegdaten und ERP-Rückmeldung
   ========================================================= */

const GX = { items: [], offset: 0, limit: 100, total: 0 };
try { GX.limit = +localStorage.getItem("gx.limit") || 100; } catch { }

async function renderGotomaxx(view) {
  clearInterval(VL.timer);
  const printer = new URLSearchParams(location.hash.split("?")[1] || "").get("d") || "";
  $("#viewTitle").textContent = printer ? `gotomaxx – ${printer}` : "gotomaxx";
  GX.offset = 0;
  view.innerHTML = `
    <div class="toolbar">
      <div class="filters">
        <input type="search" id="gxSearch" placeholder="Dokument, Belegnummer, Mailadresse, Benutzer, Fehler" aria-label="Suchen">
        <select id="gxStatus" aria-label="Status"><option value="">Alle</option><option value="versendet">versendet</option><option value="fehler">Fehler</option>
          <option value="offen">in Arbeit</option><option value="erp-offen">ERP noch offen</option></select>
        <input type="date" id="gxFrom" aria-label="Von"><input type="date" id="gxTo" aria-label="Bis">
      </div>
      <div class="filters"><button class="btn ghost" id="gxReload">Aktualisieren</button></div>
    </div>
    <div class="notice" id="gxMsg" hidden></div>
    <div class="card" id="gxCard"><div id="gxPagerTop"></div><div class="table-wrap"><table class="list eglist" id="gxTable"><tbody><tr><td class="meta">Lade …</td></tr></tbody></table></div><div id="gxPager"></div></div>
    <p class="meta">Aus dem Protokoll von gotomaxx PDFMAILER: ob der Beleg verarbeitet (gespeichert, gedruckt, gemailt) wurde oder welcher Fehler auftrat,
      und ob das Ergebnis ans ERP zurückgemeldet wurde. Belege, die Belegpost an gotomaxx übergeben hat, werden über den Vorgang gemeldet;
      direkt aus dem ERP gedruckte über den erratenen Belegindex.</p>`;
  const show = (t, ok) => { const n = $("#gxMsg"); n.hidden = !t; n.className = "notice" + (ok ? " ok" : ""); n.textContent = t || ""; };
  const load = async () => {
    const p = new URLSearchParams(); if (printer) p.set("drucker", printer);
    const q = $("#gxSearch").value.trim(); if (q) p.set("q", q);
    for (const [id, k] of [["gxStatus", "status"], ["gxFrom", "from"], ["gxTo", "to"]]) if ($("#" + id).value) p.set(k, $("#" + id).value);
    p.set("offset", GX.offset); p.set("limit", GX.limit);
    try { const r = await api("GET", "api/gotomaxx-protokoll?" + p); if (!Array.isArray(r.items)) throw new Error("Das Protokoll konnte nicht geladen werden. Läuft schon die neue Version des Dienstes (Dienst neu starten)?");
      GX.items = r.items; GX.canChange = r.canChange; GX.total = r.total ?? r.items.length; show(""); drawGotomaxx();
      const pg = GX.total > r.items.length || GX.offset > 0 ? pagerHtml(GX.offset, GX.limit, GX.total, r.items.length) : "";
      $("#gxPager").innerHTML = pg; $("#gxPagerTop").innerHTML = pg; }
    catch (e) { show(e.message); if (!GX.items.length) $("#gxTable").innerHTML = `<tbody><tr><td class="empty">Keine Daten.</td></tr></tbody>`; }
  };
  let deb;
  $("#gxSearch").oninput = () => { clearTimeout(deb); deb = setTimeout(() => { GX.offset = 0; load(); }, 300); };
  for (const id of ["gxStatus", "gxFrom", "gxTo"]) $("#" + id).onchange = () => { GX.offset = 0; load(); };
  bindPager($("#gxCard"), GX, load, "gx.limit");
  $("#gxReload").onclick = load;
  $("#gxTable").addEventListener("click", async e => {
    const b = e.target.closest("[data-gx-retry]"); if (!b) return;
    b.disabled = true;
    try { await api("POST", `api/gotomaxx-protokoll/${b.dataset.gxRetry}/erneut`); await load(); show("Zuordnung erneut ausgeführt.", true); } catch (err) { show(err.message); }
  });
  await load();
  VL.timer = setInterval(() => { if (document.visibilityState === "visible" && $("#gxTable")) load(); }, 30000);
}

function gxErp(x) {
  if (x.erpJob) {
    const s = x.erpJob.status;
    const cls = s === "erledigt" ? "ok" : s === "fehler" || s === "aufgegeben" ? "missing" : "partial";
    return `<span class="badge ${cls}">${s === "erledigt" ? "ans ERP gemeldet" : esc(s)}</span>${x.erpIndex ? `<small class="mono">${esc(x.erpIndex)}${x.mandant ? ` · Mandant ${esc(x.mandant)}` : ""}</small>` : ""}${x.erpJob.message ? `<small>${esc(x.erpJob.message)}</small>` : ""}`;
  }
  if (x.erpStatus === "vorgang") return `<a href="#/vorgang?id=${encodeURIComponent(x.vorgangId)}"><span class="badge">über Vorgang</span></a>${x.erpIndex ? `<small class="mono">${esc(x.erpIndex)}</small>` : ""}${x.erpMessage ? `<small>${esc(x.erpMessage)}</small>` : ""}`;
  if (!x.erpStatus) return x.state === "offen" ? '<span class="muted">–</span>' : `<span class="badge partial">wird zugeordnet</span>${x.erpMessage ? `<small>${esc(x.erpMessage)}</small>` : ""}`;
  return `<span class="badge ${x.erpStatus === "aus" || String(x.erpStatus).startsWith("nicht gemeldet") ? "" : "missing"}">${esc(x.erpStatus)}</span>${x.erpMessage ? `<small>${esc(x.erpMessage)}</small>` : ""}`;
}

function drawGotomaxx() {
  const rows = GX.items;
  if (!rows.length) { $("#gxTable").innerHTML = `<tbody><tr><td class="empty">Keine Einträge.</td></tr></tbody>`; return; }
  $("#gxTable").innerHTML = `<thead><tr><th>Zeit</th><th>Dokument</th><th>Beleg</th><th>Benutzer</th><th>Ergebnis</th><th>ERP-Rückmeldung</th><th></th></tr></thead>
    <tbody>${rows.map(x => `<tr>
      <td class="nowrap">${fmtDT(x.at)}</td>
      <td><b>${esc(x.source || "")}</b><small>${esc(x.actions || "")}</small></td>
      <td class="nowrap">${esc(x.docType || "")}${x.docNumber ? `<small class="mono">${esc(x.docNumber)}</small>` : '<span class="muted">–</span>'}</td>
      <td class="nowrap">${esc(x.user || "")}<small>${esc(x.computer || "")}</small></td>
      <td>${x.state === "versendet" ? '<span class="badge ok">versendet</span>' : x.state === "fehler" ? '<span class="badge missing">Fehler</span>' : '<span class="badge partial">in Arbeit</span>'}${x.error ? `<small>${esc(x.error)}</small>` : ""}</td>
      <td>${gxErp(x)}</td>
      <td class="nowrap">${GX.canChange && x.state !== "offen" && x.erpStatus !== "vorgang" && !(x.erpJob && x.erpJob.status === "erledigt") ? `<button class="btn ghost small" data-gx-retry="${x.nr}">Erneut zuordnen</button>` : ""}</td>
    </tr>`).join("")}</tbody>`;
}
