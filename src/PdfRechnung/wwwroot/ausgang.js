"use strict";

/* =========================================================
   Datenaustausch → Ausgang: aus Ausgangsordnern gesendete Dateien
   ========================================================= */

const AG = { items: [] };

async function renderAusgang(view) {
  clearInterval(VL.timer);
  view.innerHTML = `
    <div class="toolbar">
      <div class="filters">
        <input type="search" id="agSearch" placeholder="Datei, Kunde, Versandweg suchen" value="${esc(store.get("ag.q", ""))}" aria-label="Suchen">
        <select id="agChannel" aria-label="Versandweg"><option value="">Alle Versandwege</option></select>
        <select id="agStatus" aria-label="Status"><option value="">Alle</option><option value="gesendet">gesendet</option><option value="fehlerhaft">zurückgehalten</option></select>
        <input type="date" id="agFrom" aria-label="Von" title="Gesendet von">
        <input type="date" id="agTo" aria-label="Bis" title="Gesendet bis">
      </div>
      <div class="filters"><button class="btn ghost" id="agReload">Aktualisieren</button></div>
    </div>
    <div class="notice" id="agMsg" hidden></div>
    <div class="card">
      <div class="table-wrap"><table class="list eglist" id="agTable"><tbody><tr><td class="meta">Lade Ausgang …</td></tr></tbody></table></div>
    </div>
    <p class="meta">Hier steht jede Datei, die der Dienst aus dem Ausgangsordner eines Versandwegs gesendet hat (FTP/FTPS/SFTP, AS2, eXite),
      und jede, die er wegen Prüffehlern in den Unterordner <code>fehlerhaft</code> gelegt hat. Belege aus dem Druck stehen weiter unter Vorgänge.</p>`;

  let channelsDone = false;
  const showMsg = t => { const n = $("#agMsg"); n.hidden = !t; n.textContent = t || ""; };
  const load = async () => {
    const p = new URLSearchParams();
    const s = $("#agSearch").value.trim(); if (s) p.set("q", s);
    for (const [id, k] of [["agChannel", "kanal"], ["agStatus", "status"], ["agFrom", "from"], ["agTo", "to"]]) if ($("#" + id).value) p.set(k, $("#" + id).value);
    try {
      const r = await api("GET", "api/ausgang?" + p);
      if (!channelsDone) { $("#agChannel").insertAdjacentHTML("beforeend", r.channels.map(c => `<option value="${esc(c.key)}">${esc(c.text)}</option>`).join("")); channelsDone = true; }
      AG.items = r.items; showMsg(""); drawAusgang();
    } catch (e) { showMsg(e.message); }
  };
  let deb;
  $("#agSearch").oninput = () => { clearTimeout(deb); deb = setTimeout(() => { store.set("ag.q", $("#agSearch").value); load(); }, 300); };
  for (const id of ["agChannel", "agStatus", "agFrom", "agTo"]) $("#" + id).onchange = load;
  $("#agReload").onclick = load;
  await load();
  VL.timer = setInterval(() => { if (document.visibilityState === "visible" && $("#agTable")) load(); }, 30000);
}

function agCheck(x) {
  return x.check ? `<a href="#/pruefen?ausgang=${x.nr}" title="${esc(x.checkText || "")}"><span class="badge ${x.check === "fehler" ? "missing" : x.check === "warnung" ? "partial" : "ok"}">${x.check === "fehler" ? "Fehler" : x.check === "warnung" ? "Warnungen" : "in Ordnung"}</span></a>` : '<span class="muted">–</span>';
}
function agLinks(x) {
  const url = `api/ausgang/${x.nr}/datei`;
  return x.exists ? `${egViewable(x.fileName) ? `<a href="${url}?ansehen=1" target="_blank" rel="noopener">Ansehen</a> · ` : ""}<a href="${url}" download>Herunterladen</a>` : '<span class="muted">Datei fehlt</span>';
}

function drawAusgang() {
  const rows = AG.items;
  if (!rows.length) { $("#agTable").innerHTML = `<tbody><tr><td class="empty">Noch keine Dateien gesendet.</td></tr></tbody>`; return; }
  $("#agTable").innerHTML = `<thead><tr><th>Zeit</th><th>Versandweg</th><th>Kunde</th><th>Datei</th><th>Status</th><th>Prüfung</th><th>Abgelegt in</th><th></th></tr></thead>
    <tbody>${rows.map(x => `<tr>
        <td class="nowrap">${fmtDT(x.at)}</td>
        <td class="nowrap">${esc(x.channelText)}<small>${esc(x.customerId ? "" : x.source || "")}</small></td>
        <td>${esc(x.customer || "")}</td>
        <td><b>${esc(x.fileName)}</b><small>${fmtSize(x.size)}</small></td>
        <td class="nowrap">${x.status === "fehlerhaft" ? '<span class="badge missing">zurückgehalten</span>' : '<span class="badge ok">gesendet</span>'}</td>
        <td class="nowrap">${agCheck(x)}</td>
        <td><span title="${esc(x.stored)}">${esc(x.stored.replace(/[\\/][^\\/]*$/, ""))}</span></td>
        <td class="nowrap">${agLinks(x)}</td>
      </tr>`).join("")}</tbody>`;
}
