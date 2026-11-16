"use strict";

/* =========================================================
   Vorgänge → Eingang: abgerufene Dateien (FTP/SFTP/eXite/Mail/AS2)
   ========================================================= */

const EG = { items: [], timer: null };
const fmtSize = n => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1).replace(".", ",")} KB` : `${(n / 1048576).toFixed(1).replace(".", ",")} MB`;
const egViewable = name => /\.(pdf|xml|edi|txt|csv|json|x12|edifact|inh|dat|log)$/i.test(name);

async function renderEingang(view) {
  clearInterval(VL.timer);
  view.innerHTML = `
    <div class="toolbar">
      <div class="filters">
        <input type="search" id="egSearch" placeholder="Datei, Kunde, Versandweg suchen" value="${esc(store.get("eg.q", ""))}" aria-label="Suchen">
        <select id="egChannel" aria-label="Versandweg"><option value="">Alle Versandwege</option></select>
        <input type="date" id="egFrom" aria-label="Von" title="Eingang von">
        <input type="date" id="egTo" aria-label="Bis" title="Eingang bis">
        <label class="check"><input type="checkbox" id="egUnread"> nur ungelesene</label>
      </div>
      <div class="filters">
        <button class="btn ghost" id="egAllRead">Alle als gelesen markieren</button>
        <button class="btn ghost" id="egReload">Aktualisieren</button>
      </div>
    </div>
    <div class="notice" id="egMsg" hidden></div>
    <div class="card">
      <div class="table-wrap"><table class="list eglist" id="egTable"><tbody><tr><td class="meta">Lade Eingang …</td></tr></tbody></table></div>
    </div>
    <p class="meta">Hier steht jede Datei, die über einen Versandweg mit Abruf hereingekommen ist (FTP/FTPS/SFTP, eXite, E-Mail-Anhänge, AS2).
      Eine Kopie liegt im Datenordner unter <code>eingang-archiv</code>, auch wenn der Eingangsordner aus einem PDF schon einen Vorgang gemacht hat.</p>`;

  let channelsDone = false;
  const showMsg = (t, ok) => { const n = $("#egMsg"); n.hidden = !t; n.className = "notice" + (ok ? " ok" : ""); n.textContent = t || ""; };
  const load = async () => {
    const p = new URLSearchParams();
    const s = $("#egSearch").value.trim(); if (s) p.set("q", s);
    if ($("#egChannel").value) p.set("kanal", $("#egChannel").value);
    if ($("#egFrom").value) p.set("from", $("#egFrom").value);
    if ($("#egTo").value) p.set("to", $("#egTo").value);
    if ($("#egUnread").checked) p.set("ungelesen", "1");
    try {
      const r = await api("GET", "api/eingang?" + p);
      if (!channelsDone) {
        $("#egChannel").insertAdjacentHTML("beforeend", r.channels.map(c => `<option value="${esc(c.key)}">${esc(c.text)}</option>`).join(""));
        channelsDone = true;
      }
      EG.items = r.items;
      drawEingang();
      setInboundCount(r.unread);
    } catch (e) { showMsg(e.message); }
  };

  let deb;
  $("#egSearch").oninput = () => { clearTimeout(deb); deb = setTimeout(() => { store.set("eg.q", $("#egSearch").value); load(); }, 300); };
  for (const id of ["egChannel", "egFrom", "egTo", "egUnread"]) $("#" + id).onchange = load;
  $("#egReload").onclick = load;
  $("#egAllRead").onclick = async () => {
    try { const r = await api("POST", "api/eingang/gelesen", { all: true }); setInboundCount(r.unread); load(); }
    catch (e) { showMsg(e.message); }
  };
  $("#egTable").addEventListener("click", e => {
    const a = e.target.closest("a[data-nr]");
    if (!a) return;
    const it = EG.items.find(x => String(x.nr) === a.dataset.nr);
    if (it && !it.read) { it.read = true; a.closest("tr")?.classList.remove("unread"); setInboundCount(Math.max(0, (EG.unread || 1) - 1)); }
  });
  await load();
  VL.timer = setInterval(() => { if (document.visibilityState === "visible" && $("#egTable")) load(); }, 30000);
}

function drawEingang() {
  const rows = EG.items;
  if (!rows.length) { $("#egTable").innerHTML = `<tbody><tr><td class="empty">Noch keine Dateien abgerufen.</td></tr></tbody>`; return; }
  $("#egTable").innerHTML = `<thead><tr><th>Eingang</th><th>Versandweg</th><th>Kunde</th><th>Datei</th><th>Abgelegt in</th><th>Vorgang</th><th></th></tr></thead>
    <tbody>${rows.map(x => {
      const url = `api/eingang/${x.nr}/datei`;
      const dir = x.target.replace(/[\\/][^\\/]*$/, "");
      const where = x.vorgangId ? `<span class="muted" title="${esc(x.target)}">vom Eingangsordner übernommen</span>`
        : `<span title="${esc(x.target)}">${esc(dir)}</span>${x.inTarget ? "" : ' <span class="muted">(nicht mehr dort)</span>'}`;
      const can = x.hasCopy || x.inTarget;
      return `<tr class="${x.read ? "" : "unread"}">
        <td class="nowrap">${fmtDT(x.at)}</td>
        <td class="nowrap">${esc(x.channelText)}<small>${esc(x.customerId ? "" : x.source || "")}</small></td>
        <td>${esc(x.customer || "")}</td>
        <td><b>${esc(x.fileName)}</b><small>${fmtSize(x.size)}</small></td>
        <td>${where}</td>
        <td class="nowrap">${x.vorgangId ? `<a href="#/vorgang?id=${encodeURIComponent(x.vorgangId)}">Vorgang öffnen</a>` : '<span class="muted">–</span>'}</td>
        <td class="nowrap">${can ? `${egViewable(x.fileName) ? `<a href="${url}?ansehen=1" target="_blank" rel="noopener" data-nr="${x.nr}">Ansehen</a> · ` : ""}<a href="${url}" download data-nr="${x.nr}">Herunterladen</a>` : '<span class="muted">Datei fehlt</span>'}</td>
      </tr>`;
    }).join("")}</tbody>`;
}

/** Zahl ungelesener Eingänge im Menü. */
function setInboundCount(n) {
  EG.unread = n || 0;
  const el = document.querySelector('[data-count="eingang"]');
  if (el) el.textContent = n || "";
}
