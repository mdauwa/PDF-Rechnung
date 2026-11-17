"use strict";

/* =========================================================
   Tools → Prüfen: EDIFACT/EANCOM, E-Rechnungs-XML (CII/UBL), ZUGFeRD-PDF
   ========================================================= */

const PR = { reports: [] };
const prLevel = { fehler: ["missing", "Fehler"], warnung: ["partial", "Warnung"], info: ["", "Hinweis"] };
const prStatus = r => r.errors > 0 ? `<span class="badge missing">${r.errors} Fehler</span>` : r.warnings > 0 ? `<span class="badge partial">${r.warnings} Warnung${r.warnings > 1 ? "en" : ""}</span>` : `<span class="badge ok">in Ordnung</span>`;

async function renderPruefen(view) {
  const q = new URLSearchParams(location.hash.split("?")[1] || "");
  view.innerHTML = `
    <div class="card pr-drop" id="prDrop">
      <div class="pr-drop-inner">
        <b>Datei hierher ziehen</b> oder <button class="btn" id="prPick">Dateien auswählen</button>
        <input type="file" id="prFile" multiple hidden>
        <button class="btn ghost" id="prPasteToggle">Text einfügen</button>
        <p class="meta">EDIFACT/EANCOM (D.96A, D.01B, D.12A, D.20B, EANCOM 2002), E-Rechnungs-XML (ZUGFeRD/Factur-X CII, XRechnung CII/UBL, UBL) und ZUGFeRD-PDF.
          Die Dateien werden nur geprüft, nicht gespeichert.</p>
      </div>
      <div id="prPaste" hidden>
        <textarea id="prText" rows="8" placeholder="EDIFACT-Nachricht oder XML hier einfügen" spellcheck="false"></textarea>
        <div class="filters"><button class="btn" id="prTextGo">Prüfen</button></div>
      </div>
    </div>
    <div class="notice" id="prMsg" hidden></div>
    <div id="prResults"></div>`;
  const msg = (t, ok) => { const n = $("#prMsg"); n.hidden = !t; n.className = "notice" + (ok ? " ok" : ""); n.textContent = t || ""; };
  const run = async (promise) => {
    msg(""); $("#prResults").innerHTML = `<div class="card"><p class="meta">Prüfe …</p></div>`;
    try { PR.reports = await promise; drawReports(); }
    catch (e) { $("#prResults").innerHTML = ""; msg(e.message); }
  };
  const send = files => {
    if (!files.length) return;
    const fd = new FormData(); [...files].forEach(f => fd.append("files", f));
    run(api("POST", "api/tools/pruefen", fd, true));
  };
  $("#prPick").onclick = () => $("#prFile").click();
  $("#prFile").onchange = e => { send(e.target.files); e.target.value = ""; };
  $("#prPasteToggle").onclick = () => { $("#prPaste").hidden = !$("#prPaste").hidden; if (!$("#prPaste").hidden) $("#prText").focus(); };
  $("#prTextGo").onclick = () => { const t = $("#prText").value; if (t.trim()) run(api("POST", "api/tools/pruefen-text", { text: t })); };
  const drop = $("#prDrop");
  drop.addEventListener("dragover", e => { e.preventDefault(); drop.classList.add("over"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("over"));
  drop.addEventListener("drop", e => { e.preventDefault(); drop.classList.remove("over"); send(e.dataTransfer.files); });
  $("#prResults").addEventListener("click", prClick);

  if (q.get("ausgang")) run(api("GET", `api/ausgang/${encodeURIComponent(q.get("ausgang"))}/pruefung`).then(r => [r]));
  if (q.get("eingang")) run(api("GET", `api/eingang/${encodeURIComponent(q.get("eingang"))}/pruefung`).then(r => [r]));
  if (q.get("connect")) run(api("GET", `api/connect/${encodeURIComponent(q.get("connect"))}/pruefung?name=${encodeURIComponent(q.get("name") || "")}&archiv=${q.get("archiv") || ""}`).then(r => [r]));
}

function drawReports() {
  $("#prResults").innerHTML = PR.reports.map((r, i) => reportCard(r, i)).join("");
}

function reportCard(r, i) {
  const env = r.sender ? `<div class="kv-inline"><span>Absender <b>${esc(r.sender)}</b></span><span>Empfänger <b>${esc(r.recipient || "")}</b></span><span>Referenz <b>${esc(r.controlRef || "")}</b></span><span>Zeichensatz <b>${esc(r.syntax || "")}</b></span>${r.separators ? `<span>Trennzeichen <code>${esc(r.separators)}</code></span>` : ""}</div>` : "";
  const msgs = r.messages?.length ? `<table class="list"><thead><tr><th>Nachricht</th><th>Typ</th><th>Version</th><th>Verzeichnis</th><th class="num">Segmente</th></tr></thead><tbody>${r.messages.map(m =>
    `<tr><td>${esc(m.ref)}</td><td><b>${esc(m.type)}</b>${m.name ? `<small>${esc(m.name)}</small>` : ""}</td><td>${esc(m.version)} ${esc(m.agency || "")} ${esc(m.association || "")}</td><td>${esc(m.directory || "nicht hinterlegt")}</td><td class="num">${m.segments}</td></tr>`).join("")}</tbody></table>` : "";
  const counts = { fehler: 0, warnung: 0, info: 0 }; r.issues.forEach(x => counts[x.level] = (counts[x.level] || 0) + 1);
  const issues = r.issues.length ? `<table class="list pr-issues"><thead><tr><th>Art</th><th>Stelle</th><th>Meldung</th>${r.issues.some(x => x.rule) ? "<th>Regel</th>" : ""}</tr></thead><tbody>${r.issues.map((x, k) => {
    const [cls, txt] = prLevel[x.level] || ["", x.level];
    const where = [x.segment ? `Segment ${x.segment}` : "", x.line && !x.segment ? `Zeile ${x.line}` : "", x.tag || "", x.position || ""].filter(Boolean).join(" · ");
    const jump = x.segment || x.line;
    return `<tr class="lvl-${esc(x.level)}"><td class="nowrap"><span class="badge ${cls}">${txt}</span></td><td class="nowrap">${jump ? `<a href="#" data-pr-jump="${i}:${x.segment ? "s" : "l"}${jump}">${esc(where)}</a>` : esc(where)}</td><td>${esc(x.text)}</td>${r.issues.some(y => y.rule) ? `<td class="nowrap mono">${esc(x.rule || "")}</td>` : ""}</tr>`;
  }).join("")}</tbody></table>` : `<p class="meta">Keine Meldungen.</p>`;
  return `<div class="card pr-report">
    <div class="pr-head"><div><h3>${esc(r.fileName || "Datei")}</h3><div class="meta">${esc(r.title || r.format)} · ${(r.size / 1024).toFixed(1).replace(".", ",")} KB</div></div>
      <div class="pr-head-right">${r.acceptance ? `<span class="badge ${r.acceptance === "annehmen" ? "ok" : "missing"}" title="Empfehlung nach Schema- und Regelprüfung">${r.acceptance === "annehmen" ? "✓ annehmen" : "✗ ablehnen"}</span> ` : ""}${prStatus(r)} <button class="btn ghost" data-pr-text="${i}">Bericht als Text</button></div></div>
    ${r.scenario || r.acceptance ? `<p class="meta">Prüfszenario: <b>${esc(r.scenario || "keines passend")}</b>${r.ruleSet ? ` · ${esc(r.ruleSet)}` : ""}</p>` : ""}
    <p class="meta">Geprüft: ${esc((r.checks || []).join(" · "))}</p>
    ${env}${msgs}
    <div class="pr-tabs"><span>${counts.fehler} Fehler · ${counts.warnung} Warnungen · ${counts.info} Hinweise</span>
      <label class="check"><input type="checkbox" data-pr-hideinfo="${i}"> Hinweise ausblenden</label></div>
    ${issues}
    ${r.content ? `<details class="pr-content" id="prContent${i}"><summary>Inhalt anzeigen</summary>${contentView(r, i)}</details>` : ""}
  </div>`;
}

function contentView(r, i) {
  const lines = r.content.split("\n").slice(0, 8000);
  const mark = new Map();
  for (const x of r.issues) {
    const key = r.format === "EDIFACT" ? x.segment : x.line;
    if (!key) continue;
    if (mark.get(key) !== "fehler") mark.set(key, x.level);
  }
  return `<div class="pr-code"><table>${lines.map((l, k) => {
    const lv = mark.get(k + 1);
    return `<tr id="pr${i}-${k + 1}" class="${lv ? "lvl-" + lv : ""}"><td class="ln">${k + 1}</td><td>${esc(l)}</td></tr>`;
  }).join("")}</table></div>`;
}

async function prClick(e) {
  const j = e.target.closest("[data-pr-jump]");
  if (j) {
    e.preventDefault();
    const [i, ref] = j.dataset.prJump.split(":");
    const det = document.getElementById("prContent" + i); if (det) det.open = true;
    const row = document.getElementById(`pr${i}-${ref.slice(1)}`);
    if (row) { row.scrollIntoView({ block: "center" }); row.classList.add("flash"); setTimeout(() => row.classList.remove("flash"), 1500); }
    return;
  }
  const t = e.target.closest("[data-pr-text]");
  if (t) {
    const r = PR.reports[+t.dataset.prText];
    const res = await fetch("api/tools/bericht", { method: "POST", headers: { "Content-Type": "application/json", "X-Requested-With": "Belegpost" }, body: JSON.stringify(r) });
    const blob = new Blob([await res.text()], { type: "text/plain" });
    const a = document.createElement("a"); a.href = URL.createObjectURL(blob); a.download = (r.fileName || "pruefung") + ".pruefung.txt"; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
    return;
  }
  const h = e.target.closest("[data-pr-hideinfo]");
  if (h) h.closest(".pr-report").classList.toggle("hide-info", h.checked);
}
