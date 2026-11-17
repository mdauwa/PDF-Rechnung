"use strict";

/* =========================================================
   ERP Suite Connect → Eingang / Ausgang
   Ordner anzeigen, jede Datei geprüft; Verarbeitung macht ERP Suite Connect.
   ========================================================= */

const CN = { data: null, archive: false };
const cnBadge = (status, text) => status
  ? `<span class="badge ${status === "fehler" ? "missing" : status === "warnung" ? "partial" : "ok"}" title="${esc(text || "")}">${status === "fehler" ? "Fehler" : status === "warnung" ? "Warnungen" : "in Ordnung"}</span>`
  : '<span class="muted">–</span>';

async function renderConnect(view) {
  clearInterval(VL.timer);
  const area = new URLSearchParams(location.hash.split("?")[1] || "").get("b") === "ausgang" ? "ausgang" : "eingang";
  $("#viewTitle").textContent = `ERP Suite Connect – ${area === "eingang" ? "Eingang" : "Ausgang"}`;
  CN.archive = false;
  view.innerHTML = `
    <div class="toolbar">
      <div class="filters">
        <input type="search" id="cnSearch" placeholder="Datei oder Typ suchen" aria-label="Suchen">
        <select id="cnType" aria-label="Nachrichtentyp"><option value="">Alle Typen</option></select>
        <label class="check"><input type="checkbox" id="cnBad"> nur mit Fehlern/Warnungen</label>
      </div>
      <div class="filters">
        <div class="seg" id="cnSeg"><button class="active" data-arch="0">${area === "eingang" ? "Wartet auf Verarbeitung" : "Wartet auf Versand"}</button><button data-arch="1">Archiv</button></div>
        <button class="btn ghost" id="cnReload">Aktualisieren</button>
      </div>
    </div>
    <div class="notice" id="cnMsg" hidden></div>
    <div class="card"><div class="table-wrap"><table class="list" id="cnTable"><tbody><tr><td class="meta">Lade …</td></tr></tbody></table></div></div>
    <div id="cnReceived"></div>
    <p class="meta" id="cnFoot"></p>`;
  const showMsg = (t, ok) => { const n = $("#cnMsg"); n.hidden = !t; n.className = "notice" + (ok ? " ok" : ""); n.textContent = t || ""; };
  const load = async () => {
    try {
      CN.data = await api("GET", `api/connect/${area}?archiv=${CN.archive ? 1 : ""}`);
      showMsg(CN.data.folder ? (CN.data.exists ? "" : `Ordner nicht gefunden: ${CN.data.folder}`) : `Kein ${CN.archive ? "Archiv" : ""}ordner eingestellt (Einstellungen → ERP Suite Connect).`);
      $("#cnSeg [data-arch='1']").hidden = !CN.data.hasArchive;
      const types = [...new Set(CN.data.files.map(f => f.type).filter(Boolean))].sort();
      const cur = $("#cnType").value;
      $("#cnType").innerHTML = `<option value="">Alle Typen</option>` + types.map(t => `<option ${t === cur ? "selected" : ""}>${esc(t)}</option>`).join("");
      draw();
    } catch (e) { showMsg(e.message); }
  };
  const draw = () => {
    const d = CN.data; if (!d) return;
    const q = $("#cnSearch").value.trim().toLowerCase(), t = $("#cnType").value, bad = $("#cnBad").checked;
    const rows = d.files.filter(f => (!q || f.name.toLowerCase().includes(q) || (f.type || "").toLowerCase().includes(q)) && (!t || f.type === t) && (!bad || (f.check && f.check !== "ok")));
    const base = `api/connect/${area}/datei?archiv=${CN.archive ? 1 : ""}&name=`;
    $("#cnTable").innerHTML = rows.length ? `<thead><tr><th>Geändert</th><th>Datei</th><th>Typ</th><th>Prüfung</th><th></th></tr></thead><tbody>${rows.map(f => `<tr>
        <td class="nowrap">${fmtDT(f.changed)}</td>
        <td><b>${esc(f.name)}</b><small>${fmtSize(f.size)}</small></td>
        <td>${f.type ? `${esc(f.type)}<small>${esc(f.typeTitle || "")}</small>` : '<span class="muted">unbekannt</span>'}</td>
        <td class="nowrap"><a href="#/pruefen?connect=${area}&archiv=${CN.archive ? 1 : ""}&name=${encodeURIComponent(f.name)}" title="${esc(f.checkText || "Prüfbericht öffnen")}">${cnBadge(f.check, f.checkText)}</a></td>
        <td class="nowrap"><a href="${base}${encodeURIComponent(f.name)}&ansehen=1" target="_blank" rel="noopener">Ansehen</a> · <a href="${base}${encodeURIComponent(f.name)}" download>Herunterladen</a></td>
      </tr>`).join("")}</tbody>` : `<tbody><tr><td class="empty">${d.files.length ? "Keine passenden Dateien." : "Der Ordner ist leer."}</td></tr></tbody>`;
    $("#cnFoot").innerHTML = `${d.folder ? `Ordner: <code>${esc(d.folder)}</code> · ` : ""}${d.files.length} Datei(en) · geprüft nach Schnittstellenbeschreibung V${esc(d.version)} und dem, was ERP Suite Connect liest.`;
    $("#cnReceived").innerHTML = d.received?.length ? `<h3 class="cn-h">Empfangen über Versandwege</h3><div class="card"><div class="table-wrap"><table class="list"><thead><tr><th>Eingang</th><th>Versandweg</th><th>Datei</th><th>Typ</th><th>Prüfung</th><th></th></tr></thead><tbody>${d.received.slice(0, 100).map(x => `<tr>
        <td class="nowrap">${fmtDT(x.at)}</td><td>${esc(x.channelText)}<small>${esc(x.source || "")}</small></td>
        <td><b>${esc(x.fileName)}</b><small>${fmtSize(x.size)}</small></td><td>${esc(x.connectType || "")}</td>
        <td class="nowrap">${x.check ? `<a href="#/pruefen?eingang=${x.nr}">${cnBadge(x.check, x.checkText)}</a>` : '<span class="muted">–</span>'}</td>
        <td class="nowrap">${x.hasCopy || x.inTarget ? `<a href="api/eingang/${x.nr}/datei?ansehen=1" target="_blank" rel="noopener">Ansehen</a> · <a href="api/eingang/${x.nr}/datei" download>Herunterladen</a>` : ""}</td></tr>`).join("")}</tbody></table></div></div>`
      : d.sent?.length ? `<h3 class="cn-h">Gesendet über Versandwege</h3><div class="card"><div class="table-wrap"><table class="list"><thead><tr><th>Zeit</th><th>Versandweg</th><th>Datei</th><th>Typ</th><th>Status</th><th>Prüfung</th><th></th></tr></thead><tbody>${d.sent.slice(0, 100).map(x => `<tr>
        <td class="nowrap">${fmtDT(x.at)}</td><td>${esc(x.channelText)}<small>${esc(x.source || "")}</small></td>
        <td><b>${esc(x.fileName)}</b><small>${fmtSize(x.size)}</small></td><td>${esc(x.connectType || "")}</td>
        <td class="nowrap">${x.status === "fehlerhaft" ? '<span class="badge missing">zurückgehalten</span>' : '<span class="badge ok">gesendet</span>'}</td>
        <td class="nowrap">${agCheck(x)}</td><td class="nowrap">${agLinks(x)}</td></tr>`).join("")}</tbody></table></div></div>` : "";
  };
  let deb;
  $("#cnSearch").oninput = () => { clearTimeout(deb); deb = setTimeout(draw, 200); };
  $("#cnType").onchange = draw; $("#cnBad").onchange = draw;
  $("#cnReload").onclick = load;
  $("#cnSeg").onclick = e => {
    const b = e.target.closest("[data-arch]"); if (!b) return;
    CN.archive = b.dataset.arch === "1";
    $("#cnSeg").querySelectorAll("button").forEach(x => x.classList.toggle("active", x === b));
    load();
  };
  await load();
  VL.timer = setInterval(() => { if (document.visibilityState === "visible" && $("#cnTable")) load(); }, 30000);
}
