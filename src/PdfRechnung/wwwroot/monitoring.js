"use strict";

/* =========================================================
   Einstellungen: Benachrichtigung, REST-API, Filialverbund
   Datenaustausch → Filialverbund (Übersicht der Dateien)
   ========================================================= */

const MON = { status: null, keys: null, newKey: null, clients: null, fvEdit: null };
const fmtWhen = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "–";
const lvlBadge = s => `<span class="badge ${s === "fehler" ? "missing" : s === "warnung" ? "partial" : "ok"}">${s === "fehler" ? "Fehler" : s === "warnung" ? "Warnung" : "in Ordnung"}</span>`;

/* ---------------- Tab: Benachrichtigung ---------------- */

async function loadMonStatus() {
  try { MON.status = await api("GET", "api/admin/status"); } catch (e) { MON.status = { error: e.message }; }
  if (["benachrichtigung", "api"].includes(ST.tab)) drawTab();
}

function tabAlerts() {
  ST.s.alerts ||= {};
  const a = ST.s.alerts;
  const S = MON.status;
  const checks = S?.checks || [];
  const bad = checks.filter(c => c.status !== "ok");
  return section("Benachrichtigung per E-Mail",
    `${inCheck("alerts.enabled", "Benachrichtigung ist aktiv")}
     <p class="help">Belegpost meldet per Mail, wenn ein Problem länger besteht als die Wartezeit und niemand reagiert – z. B. ein Versandweg, der nicht überträgt,
       Vorgänge mit Fehlern, die keiner bearbeitet, oder ein Filial-Client, der sich nicht mehr meldet. Versendet wird über die Einstellungen unter E-Mail.</p>
     <div class="grid3">
       ${field("Empfänger", inText("alerts.recipients", "it@firma.at; edv@firma.at"), "Mehrere mit ; getrennt.")}
       ${field("Melden nach (Minuten)", inNum("alerts.delayMinutes", 0, 10080), "So lange muss ein Problem bestehen. Standard 30.")}
       ${field("Erneut melden nach (Minuten)", inNum("alerts.repeatMinutes", 0, 10080), "Solange es besteht. 0 = nur einmal. Standard 240.")}
     </div>
     <div class="grid3">
       ${field("Nur an diesen Tagen", inText("alerts.days", "Mo-Fr"), "Leer = täglich.")}
       ${field("von (Uhrzeit)", inText("alerts.from", "07:00"), "Außerhalb wird gesammelt und danach gemeldet.")}
       ${field("bis (Uhrzeit)", inText("alerts.to", "18:00"))}
     </div>
     ${inCheck("alerts.sendRecovery", "Mail „wieder in Ordnung“, wenn ein gemeldetes Problem behoben ist")}
     <h4>Was wird überwacht</h4>
     <div class="grid2">
       <div>${inCheck("alerts.channels", "Versandwege: Abruf bzw. Übertragung schlägt fehl")}
         ${inCheck("alerts.outbox", "Ausgang: Dateien wegen Prüffehlern zurückgehalten")}
         ${inCheck("alerts.inbound", "Eingang: empfangene Dateien mit Prüffehlern, nicht angesehen")}
         ${inCheck("alerts.filialverbund", "Filialverbund: Client meldet sich nicht, Dateien nicht abgeholt, Importfehler")}</div>
       <div>${inCheck("alerts.documents", "Vorgänge mit Fehler oder Versandfehler, die niemand bearbeitet")}
         ${inCheck("alerts.documentWarnings", "auch Vorgänge mit Warnungen (warten auf Freigabe)")}
         ${field("Vorgänge gelten als unbearbeitet nach (Minuten)", inNum("alerts.documentMinutes", 1, 10080))}
         ${inCheck("alerts.connect", "ERP Suite Connect: Eingangsdateien werden nicht verarbeitet")}
         ${field("… nach (Minuten)", inNum("alerts.connectMinutes", 1, 10080))}</div>
     </div>
     <div class="actions"><button class="btn ghost" data-st="alert-test">Testmail mit aktuellem Zustand senden</button>
       <span class="meta-inline">Vorher speichern – die Testmail verwendet die gespeicherten Einstellungen.</span></div>`) +
    section("Aktueller Zustand",
      !S ? `<p class="meta">Lade …</p>` : S.error ? `<div class="notice">${esc(S.error)}</div>` :
      `<p>${lvlBadge(S.status)} Dienst ${esc(S.version)} auf <b>${esc(S.machine)}</b>, Stand ${fmtWhen(S.at)}
         ${S.last?.at ? ` · letzte Benachrichtigung ${fmtWhen(S.last.at)}${S.last.error ? ` <span class="badge missing" title="${esc(S.last.error)}">nicht gesendet</span>` : ""}` : ""}</p>
       ${bad.length ? `<div class="table-wrap"><table class="list"><thead><tr><th>Bereich</th><th>Status</th><th>Meldung</th><th>seit</th></tr></thead><tbody>
         ${bad.map(c => `<tr><td><b>${esc(c.title)}</b></td><td>${lvlBadge(c.status)}</td><td>${esc(c.message)}</td><td class="nowrap">${fmtWhen(c.since)}</td></tr>`).join("")}</tbody></table></div>`
         : `<p class="meta">Alles in Ordnung (${checks.length} Prüfungen).</p>`}`,
      `<button class="btn ghost small" data-st="mon-reload">Aktualisieren</button>`);
}

/* ---------------- Tab: REST-API ---------------- */

async function loadApiKeys() {
  try { MON.keys = await api("GET", "api/admin/apikeys"); } catch (e) { MON.keys = { error: e.message }; }
  if (ST.tab === "api") drawTab();
}

function tabApi() {
  const K = MON.keys;
  const base = location.origin + location.pathname.replace(/[^/]*$/, "");
  const standorte = ST.s.filialverbund?.standorte || [];
  return section("REST-API",
    `<p class="help">Für Überwachungssysteme (z. B. PRTG, Zabbix, Uptime Kuma) und Konnektoren. Anmeldung mit API-Schlüssel im Header
       <code>X-Api-Key: …</code> oder <code>Authorization: Bearer …</code>.</p>
     <div class="table-wrap"><table class="list"><thead><tr><th>Aufruf</th><th>Zweck</th></tr></thead><tbody>
       <tr><td class="mono">GET ${esc(base)}api/v1/ping</td><td>Lebt der Dienst? Ohne Anmeldung.</td></tr>
       <tr><td class="mono">GET ${esc(base)}api/v1/status</td><td>Gesamtstatus mit allen Prüfungen. HTTP 200 = in Ordnung/Warnung, 503 = Fehler (<code>?immer200=1</code> liefert immer 200).</td></tr>
       <tr><td class="mono">GET ${esc(base)}api/v1/status/{versandweg}</td><td>Ein Versandweg, Kommunikationspartner oder Standort (Name, Kürzel oder Schlüssel aus <code>status-liste</code>) –
         oder ein Bereich: <code>versandwege</code>, <code>ausgang</code>, <code>vorgaenge</code>, <code>eingang</code>, <code>connect</code>, <code>filialverbund</code>.</td></tr>
       <tr><td class="mono">GET ${esc(base)}api/v1/status-liste</td><td>Welche Versandwege und Standorte abgefragt werden können.</td></tr>
       <tr><td class="mono">POST ${esc(base)}api/v1/pruefen</td><td>E-Rechnung (XML/ZUGFeRD-PDF) oder EDIFACT prüfen: Datei als Inhalt der Anfrage. Antwort: Prüfbericht als JSON
         (<code>?text=1</code> als Text) mit Empfehlung annehmen/ablehnen; HTTP 200 = annehmen, 406 = ablehnen.</td></tr>
       <tr><td class="mono">… ${esc(base)}api/v1/austausch/…</td><td>Datenaustausch für Konnektoren und den Filial-Client: <code>konfiguration</code>, <code>lebenszeichen</code>,
         <code>dateien</code> (abholen, <code>{nr}/quittung</code>, hochladen).</td></tr>
     </tbody></table></div>`) +
    section("API-Schlüssel",
      `${MON.newKey ? `<div class="notice ok"><b>Neuer Schlüssel „${esc(MON.newKey.name)}“</b> – jetzt kopieren, er wird nicht noch einmal angezeigt:<br>
          <code class="mono" style="user-select:all">${esc(MON.newKey.secret)}</code></div>` : ""}
       ${!K ? `<p class="meta">Lade …</p>` : K.error ? `<div class="notice">${esc(K.error)}</div>` : K.length ? `<div class="table-wrap"><table class="list"><thead><tr>
         <th>Name</th><th>Schlüssel</th><th>Rechte</th><th>Standort</th><th>Zuletzt benutzt</th><th></th></tr></thead><tbody>
         ${K.map(k => `<tr class="${k.active ? "" : "inactive"}"><td><b>${esc(k.name)}</b>${k.active ? "" : ' <span class="badge">gesperrt</span>'}</td>
           <td class="mono">${esc(k.prefix)}…</td><td>${(k.scopes || []).map(s => `<span class="badge">${{ status: "Status", pruefen: "Prüfen", austausch: "Datenaustausch" }[s] || esc(s)}</span>`).join(" ")}</td>
           <td>${esc(k.standort || "–")}</td><td class="nowrap">${fmtWhen(k.lastUsed)}${k.lastAddress ? `<small>${esc(k.lastAddress)}</small>` : ""}</td>
           <td class="nowrap"><button class="btn ghost small" data-st="key-toggle" data-id="${esc(k.id)}">${k.active ? "Sperren" : "Freigeben"}</button>
             <button class="btn ghost small danger" data-st="key-del" data-id="${esc(k.id)}">Löschen</button></td></tr>`).join("")}</tbody></table></div>`
         : `<p class="meta">Noch keine Schlüssel.</p>`}
       <h4>Neuer Schlüssel</h4>
       <div class="grid3">
         ${field("Name", `<input type="text" id="keyName" placeholder="z. B. PRTG oder Filiale Graz">`)}
         ${field("Rechte", `<label class="check"><input type="checkbox" id="keyStatus" checked> Status abfragen</label>
            <label class="check"><input type="checkbox" id="keyCheck"> Dokumente prüfen</label>
            <label class="check"><input type="checkbox" id="keyEx"> Datenaustausch (Filial-Client)</label>`)}
         ${field("Standort (für Datenaustausch)", `<select id="keyStandort"><option value="">–</option>${standorte.map(s => `<option value="${esc(s.code)}">${esc(s.code)}${s.name ? " – " + esc(s.name) : ""}</option>`).join("")}</select>`,
           "Standorte unter Versand & Partner → Filialverbund anlegen und speichern.")}
       </div>
       <div class="actions"><button class="btn" data-st="key-new">Schlüssel erzeugen</button></div>`);
}

/* ---------------- Tab: Filialverbund ---------------- */

async function loadFvClients() {
  try { MON.clients = await api("GET", "api/admin/filialverbund/clients"); } catch (e) { MON.clients = []; }
  if (ST.tab === "filialverbund") drawTab();
}

const fvP = (...p) => "filialverbund." + p.join(".");
const fvText = (path, ph = "") => inText(path, ph);
const fvList = (path, ph = "") => `<input type="text" data-fvlist="${path}" value="${esc((get(ST.s, path) || []).join(", "))}" placeholder="${esc(ph)}">`;

function tabFilialverbund() {
  ST.s.filialverbund ||= {};
  const F = ST.s.filialverbund;
  F.standorte ||= []; F.gruppen ||= []; F.regeln ||= [];
  if (MON.fvEdit != null && F.standorte[MON.fvEdit]) return fvStandort(MON.fvEdit);
  MON.fvEdit = null;
  const cl = Object.fromEntries((MON.clients || []).map(c => [c.code.toLowerCase(), c]));
  return section("Versandweg Filialverbund",
    `${inCheck("filialverbund.enabled", "Filialverbund ist aktiv")}
     <p class="help">Das ERP der Zentrale legt Import- und Exportdateien (.dta) in den Ausgangsordner. Belegpost verteilt sie an die Standorte:
       ein Unterordner mit dem Kürzel eines Standorts oder dem Namen einer Gruppe geht an diese(n), Dateien direkt im Ordner nach den Verteilregeln.
       Am Standort holt der <b>Belegpost Filial-Client</b> die Dateien ab, importiert sie ins ERP, ersetzt Sonderdateien, startet Workflows und lädt Rückläufer hoch.
       Alles, was der Client tun soll, wird hier eingestellt – am Client nur Portal-Adresse und API-Schlüssel.</p>
     <div class="grid2">
       ${field("Ausgangsordner der Zentrale", fvText(fvP("outFolder"), "D:\\ERP\\Filialverbund\\Ausgang"), "Unterordner F01, F02 … bzw. Gruppen; Dateien direkt darin nach Regeln.")}
       ${field("Verteilte Dateien (Archiv)", fvText(fvP("outArchive"), "leer = Unterordner „verteilt“"))}
       ${field("Welche Dateien", fvText(fvP("pattern"), "*.dta"), "Leer = *.dta. Mehrere mit ; getrennt, z. B. *.dta; *.bwp")}
       ${field("Rückläufer der Standorte ablegen in", fvText(fvP("inFolder"), "leer = Datenordner\\filialverbund\\eingang"))}
     </div>
     ${inCheck(fvP("inSubfolders"), "Rückläufer in Unterordner je Standort")}
     <div class="grid2">
       ${field("Client gilt als offline nach (Minuten)", inNum(fvP("offlineMinutes"), 5, 10080))}
       ${field("Datei gilt als hängengeblieben nach (Minuten)", inNum(fvP("pendingMinutes"), 5, 10080), "Nicht abgeholt bzw. nicht quittiert → Benachrichtigung.")}
     </div>
     <div class="actions"><button class="btn ghost" data-st="fv-run">Jetzt verteilen</button></div>`) +
    section("Standorte",
      `${F.standorte.length ? `<div class="table-wrap"><table class="list"><thead><tr><th>Kürzel</th><th>Name</th><th>Aufgaben</th><th>Client</th><th>Offen</th><th></th></tr></thead><tbody>
        ${F.standorte.map((s, i) => { const c = cl[(s.code || "").toLowerCase()]; const b = c?.beat;
          return `<tr class="clickable ${s.active === false ? "inactive" : ""}" data-st="fv-open" data-i="${i}">
          <td class="mono"><b>${esc(s.code || "(neu)")}</b></td><td>${esc(s.name || "")}${s.active === false ? ' <span class="badge">inaktiv</span>' : ""}</td>
          <td>${(s.client?.tasks || []).filter(t => t.enabled !== false).map(t => esc(t.name || "Aufgabe")).join(", ") || '<span class="muted">keine</span>'}</td>
          <td class="nowrap">${b ? `${fmtWhen(b.lastSeen)}<small>${esc(b.machine || "")} ${esc(b.version || "")}</small>` : '<span class="muted">noch nie</span>'}</td>
          <td>${c?.pending ?? "–"}</td><td><button class="btn ghost small danger" data-st="fv-del" data-i="${i}">Entfernen</button></td></tr>`; }).join("")}</tbody></table></div>`
        : `<p class="meta">Noch keine Standorte.</p>`}
       <div class="actions"><button class="btn ghost" data-st="fv-add">Standort hinzufügen</button>
         <span class="meta-inline">Den API-Schlüssel für den Client unter Dienst & Zugang → REST-API erzeugen (Recht „Datenaustausch“, Standort wählen).</span></div>`) +
    section("Gruppen",
      `${F.gruppen.map((g, i) => `<div class="grid3">
          ${field("Name", fvText(fvP("gruppen", i, "name"), "ALLE"))}
          ${field("Standorte", fvList(fvP("gruppen", i, "members"), "F01, F02, F03"), "Kürzel mit Komma getrennt.")}
          <div class="field"><span class="lbl">&nbsp;</span><button class="btn ghost small danger" data-st="fv-del-list" data-list="gruppen" data-i="${i}">Entfernen</button></div></div>`).join("")}
       <div class="actions"><button class="btn ghost" data-st="fv-add-list" data-list="gruppen">Gruppe hinzufügen</button>
         <span class="meta-inline">Eine Datei im Unterordner „ALLE“ geht an alle Mitglieder – z. B. an 4 Filialen.</span></div>`) +
    section("Verteilregeln (Dateien direkt im Ausgangsordner)",
      `${F.regeln.map((r, i) => `<div class="grid3">
          ${field("Dateiname", fvText(fvP("regeln", i, "pattern"), "ARTIKEL*.dta"))}
          ${field("an Standorte/Gruppen", fvText(fvP("regeln", i, "targets"), "ALLE oder F01, F03"), "* = alle aktiven Standorte.")}
          <div class="field"><span class="lbl">&nbsp;</span><button class="btn ghost small danger" data-st="fv-del-list" data-list="regeln" data-i="${i}">Entfernen</button></div></div>`).join("")}
       <div class="actions"><button class="btn ghost" data-st="fv-add-list" data-list="regeln">Regel hinzufügen</button>
         <span class="meta-inline">Passen mehrere Regeln, geht die Datei an alle genannten Empfänger. Ohne passende Regel bleibt sie liegen und wird gemeldet.</span></div>`);
}

function fvStandort(i) {
  const s = ST.s.filialverbund.standorte[i];
  s.client ||= {};
  const c = s.client;
  c.specialFiles ||= []; c.exports ||= []; c.tasks ||= [];
  const P = (...p) => fvP("standorte", i, ...p);
  const C = (...p) => P("client", ...p);
  const sel = (path, opts) => `<select data-path="${path}" data-redraw="1">${opts.map(([v, t]) => `<option value="${v}" ${(get(ST.s, path) ?? "") === v ? "selected" : ""}>${esc(t)}</option>`).join("")}</select>`;
  const task = (t, j) => {
    t.schedule ||= { mode: "intervall", minutes: 120 }; t.workflows ||= [];
    const T = (...p) => C("tasks", j, ...p);
    const m = t.schedule.mode || "intervall";
    return `<div class="fv-task">
      <div class="grid3">
        ${field("Aufgabe", fvText(T("name"), "z. B. Import alle 2 Stunden"))}
        ${field("Zeitplan", sel(T("schedule", "mode"), [["intervall", "alle … Minuten"], ["zeiten", "zu festen Uhrzeiten"], ["aus", "aus (nur von Hand)"]]))}
        ${m === "intervall" ? field("alle … Minuten", inNum(T("schedule", "minutes"), 1, 10080)) : m === "zeiten" ? field("Uhrzeiten", fvText(T("schedule", "times"), "19:00"), "Mehrere mit ; getrennt.") : "<div></div>"}
      </div>
      ${m !== "aus" ? `<div class="grid3">
        ${field("Wochentage", fvText(T("schedule", "days"), "Mo-Sa"), "Leer = täglich.")}
        ${m === "intervall" ? field("von", fvText(T("schedule", "from"), "07:00")) + field("bis", fvText(T("schedule", "to"), "20:00")) : ""}
      </div>` : ""}
      <div class="grid2"><div>
        ${inCheck(T("enabled"), "Aufgabe ist aktiv")}
        ${inCheck(T("import"), "Bereitliegende Dateien abholen und ins ERP importieren")}
        ${inCheck(T("specialFiles"), "Sonderdateien im ERP-Ordner ersetzen")}
        ${inCheck(T("export"), "Exporte des Standorts hochladen")}
        ${inCheck(T("closeErp"), "ERP am Client vorher beenden")}
      </div><div class="grid2">
        ${field("Nur diese Dateien importieren", fvText(T("importPattern"), "leer = wie oben"))}
        ${field("Schnittstelle", sel(T("interface"), [["", "wie oben"], ["wawi", "Warenwirtschaft (IMPAUTO)"], ["fibu", "Finanzbuchhaltung (FIBAUTO)"]]))}
      </div></div>
      <h5>Workflows und Auswertungen</h5>
      ${t.workflows.map((w, k) => { const W = (...p) => T("workflows", k, ...p); return `<div class="grid3 fv-wf">
          ${field("Name", fvText(W("name"), "Tagesabschluss"))}
          ${field("Wann", sel(W("when"), [["vorher", "vor Import/Export"], ["nachher", "nach dem Import"], ["nachErfolg", "nur nach fehlerfreiem Import"]]))}
          ${field("Programmbereich", sel(W("program"), [["01", "01 Warenwirtschaft"], ["02", "02 ReportCenter"], ["03", "03 Datenimport"], ["04", "04 Datenexport"],
            ["05", "05 Beleggenerator"], ["06", "06 Kassensystem"], ["07", "07 Finanzbuchhaltung"], ["08", "08 Anlagenbuchhaltung"], ["00", "00 CRM/OfficePlaner"]]))}
          ${field("Modul / Workflow / Auswertung", fvText(W("workflow"), "Modulnummer bzw. Name im ERP"))}
          ${field("Sonderparameter", fvText(W("arguments"), "optional"))}
          ${field("Höchstlaufzeit (Minuten)", inNum(W("maxMinutes"), 0, 1440), "0 = 60 Minuten.")}
          <div class="field"><span class="lbl">&nbsp;</span><span>${inCheck(W("enabled"), "aktiv")} ${inCheck(W("close"), "ERP danach schließen (/CLOSE)")}
            <button class="btn ghost small danger" data-st="fv-del-wf" data-j="${j}" data-k="${k}">Entfernen</button></span></div></div>`; }).join("")}
      <div class="actions"><button class="btn ghost small" data-st="fv-add-wf" data-j="${j}">Workflow/Auswertung hinzufügen</button>
        <button class="btn ghost small danger" data-st="fv-del-task" data-j="${j}">Aufgabe entfernen</button></div>
    </div>`;
  };
  return `<div class="actions"><button class="btn ghost" data-st="fv-back">← Alle Standorte</button></div>` +
    section(`Standort ${esc(s.code || "(neu)")}`,
      `<div class="grid3">
         ${field("Kürzel", fvText(P("code"), "F01"), "Auch Name des Unterordners im Ausgangsordner.")}
         ${field("Name", fvText(P("name"), "Filiale Graz"))}
         ${field("Rückläufer zusätzlich an", fvText(P("forwardTo"), "leer = nur Zentrale"), "Standorte/Gruppen, z. B. F02.")}
       </div>${inCheck(P("active"), "Standort ist aktiv")}`) +
    section("ERP am Standort",
      `<div class="grid3">
         ${field("ERP-Programmordner", fvText(C("erpPath"), "C:\\BueroWARE"), "Am Rechner des Clients.")}
         ${field("Importprogramm", sel(C("importProgram"), [["auto", "automatisch (WWWIN64 bzw. BW000632)"], ["wwwin", "WWWIN64/WWWIN32"], ["bw000632", "BW000632"], ["bwwin", "BWWIN64/BWWIN32"]]))}
         ${field("Schnittstelle", sel(C("interface"), [["wawi", "Warenwirtschaft (IMPAUTO)"], ["fibu", "Finanzbuchhaltung (FIBAUTO)"]]))}
         ${field("ERP-Benutzer", fvText(C("erpUser"), "000"))}
         ${field("Kennwort", `<input type="password" data-path="${C("erpNewPassword")}" value="" placeholder="${c.erpHasPassword ? "gespeichert – leer lassen" : "Standard !CW"}" autocomplete="new-password">`)}
         ${field("Mandant", fvText(C("mandant"), "leer = ohne /M:"))}
         ${field("Belegzeitraum", fvText(C("period"), "leer = ohne"))}
         ${field("Importdateien", fvText(C("importPattern"), "*.dta"))}
         ${field("Abfrage alle … Sekunden", inNum(C("pollSeconds"), 15, 3600), "Einstellungen holen und Lebenszeichen.")}
       </div>
       ${inCheck(C("checkImport"), "Nach dem Import prüfen, ob Sätze nicht übernommen wurden (Kennzeichen UEBER N) – sonst Fehler melden")}`) +
    section("Aufgaben und Zeiten", c.tasks.map(task).join("") +
      `<div class="actions"><button class="btn ghost" data-st="fv-add-task">Aufgabe hinzufügen</button>
        <span class="meta-inline">z. B. „Import alle 2 h“ und „Import mit ERP beenden + Workflow um 19:00“.</span></div>`) +
    section("Sonderdateien (werden nicht importiert, sondern im ERP ersetzt)",
      `${c.specialFiles.map((r, k) => `<div class="grid3">
          ${field("Dateiname", fvText(C("specialFiles", k, "pattern"), "*.bwp oder DESIGNER.DAT"))}
          ${field("Zielordner", fvText(C("specialFiles", k, "target"), "leer = ERP-Ordner, z. B. VORLAGEN"), "Relativ zum ERP-Ordner oder absolut.")}
          <div class="field"><span class="lbl">&nbsp;</span><span>${inCheck(C("specialFiles", k, "closeErp"), "ERP vorher beenden")}
            ${inCheck(C("specialFiles", k, "backup"), "alte Datei sichern")}
            <button class="btn ghost small danger" data-st="fv-del-c" data-list="specialFiles" data-k="${k}">Entfernen</button></span></div></div>`).join("")}
       <div class="actions"><button class="btn ghost small" data-st="fv-add-c" data-list="specialFiles">Sonderdatei hinzufügen</button></div>`) +
    section("Exporte (Rückläufer an die Zentrale)",
      `${c.exports.map((r, k) => `<div class="grid3">
          ${field("Ordner am Standort", fvText(C("exports", k, "folder"), "C:\\BueroWARE\\EXPORT"))}
          ${field("Dateien", fvText(C("exports", k, "pattern"), "*.dta"))}
          ${field("zusätzlich an", fvText(C("exports", k, "to"), "leer = nur Zentrale"))}
          <div class="field"><span class="lbl">&nbsp;</span><button class="btn ghost small danger" data-st="fv-del-c" data-list="exports" data-k="${k}">Entfernen</button></div></div>`).join("")}
       <div class="actions"><button class="btn ghost small" data-st="fv-add-c" data-list="exports">Exportordner hinzufügen</button></div>`);
}

/* ---------------- Ereignisse ---------------- */

function monitorInput(el, e) {
  if (el.dataset?.fvlist) {
    set(ST.s, el.dataset.fvlist, el.value.split(/[,;\s]+/).map(x => x.trim()).filter(Boolean));
    markDirty(); return true;
  }
  return false;
}

async function monitorClick(a, btn, e) {
  const F = ST.s?.filialverbund;
  const d = e.target.closest("[data-st]")?.dataset || {};
  switch (a) {
    case "gx-read": {
      if (ST.dirty) { msg("Bitte zuerst speichern."); return true; }
      const r = await api("POST", "api/admin/gotomaxx-protokoll/lesen"); msg(r.message, r.ok); return true;
    }
    case "rl-add": ST.s.validation.ruleLevels.push({ rule: "", level: "warnung", note: "" }); markDirty(); drawTab(); return true;
    case "rl-del": ST.s.validation.ruleLevels.splice(+d.i, 1); markDirty(); drawTab(); return true;
    case "mon-reload": MON.status = null; drawTab(); await loadMonStatus(); return true;
    case "alert-test": {
      if (ST.dirty) { msg("Bitte zuerst speichern."); return true; }
      const r = await api("POST", "api/admin/benachrichtigung/test"); msg(r.message, r.ok); return true;
    }
    case "key-new": {
      const scopes = [$("#keyStatus").checked ? "status" : null, $("#keyCheck").checked ? "pruefen" : null, $("#keyEx").checked ? "austausch" : null].filter(Boolean);
      const name = $("#keyName").value.trim();
      const r = await api("POST", "api/admin/apikeys", { name, scopes, standort: $("#keyStandort").value });
      MON.newKey = { name, secret: r.secret }; await loadApiKeys(); return true;
    }
    case "key-toggle": {
      const k = MON.keys.find(x => x.id === d.id);
      await api("PUT", `api/admin/apikeys/${encodeURIComponent(k.id)}`, { name: k.name, scopes: k.scopes, standort: k.standort, active: !k.active });
      await loadApiKeys(); return true;
    }
    case "key-del":
      if (await confirmDialog("API-Schlüssel löschen", "Wer diesen Schlüssel verwendet, hat danach keinen Zugriff mehr.", "Löschen", true)) {
        await api("DELETE", `api/admin/apikeys/${encodeURIComponent(d.id)}`); MON.newKey = null; await loadApiKeys();
      }
      return true;
    case "fv-run": {
      if (ST.dirty) { msg("Bitte zuerst speichern."); return true; }
      const r = await api("POST", "api/admin/filialverbund/verteilen"); msg(r.message + (r.unassigned?.length ? " " + r.unassigned.join("; ") : ""), r.ok); loadFvClients(); return true;
    }
    case "fv-add":
      F.standorte.push({ code: "", name: "", active: true, client: { importProgram: "auto", interface: "wawi", erpUser: "000", importPattern: "*.dta", checkImport: true, pollSeconds: 60,
        specialFiles: [], exports: [], tasks: [{ name: "Import alle 2 Stunden", enabled: true, import: true, specialFiles: true, export: true, closeErp: false, schedule: { mode: "intervall", minutes: 120 }, workflows: [] }] } });
      MON.fvEdit = F.standorte.length - 1; markDirty(); drawTab(); return true;
    case "fv-open": if (e.target.closest("button")) return false; MON.fvEdit = +d.i; drawTab(); return true;
    case "fv-back": MON.fvEdit = null; drawTab(); return true;
    case "fv-del":
      if (await confirmDialog("Standort entfernen", `Standort ${F.standorte[+d.i].code} entfernen? Wirksam mit „Speichern“.`, "Entfernen", true)) { F.standorte.splice(+d.i, 1); markDirty(); drawTab(); }
      return true;
    case "fv-add-list": F[d.list].push(d.list === "gruppen" ? { name: "", members: [] } : { pattern: "", targets: "" }); markDirty(); drawTab(); return true;
    case "fv-del-list": F[d.list].splice(+d.i, 1); markDirty(); drawTab(); return true;
  }
  const s = MON.fvEdit != null ? F?.standorte[MON.fvEdit] : null;
  if (!s) return false;
  const c = s.client;
  switch (a) {
    case "fv-add-task": c.tasks.push({ name: "", enabled: true, import: true, specialFiles: true, export: true, closeErp: false, schedule: { mode: "zeiten", times: "19:00" }, workflows: [] }); break;
    case "fv-del-task": c.tasks.splice(+d.j, 1); break;
    case "fv-add-wf": c.tasks[+d.j].workflows.push({ enabled: true, name: "", when: "nachher", program: "01", workflow: "", arguments: "", close: true, maxMinutes: 0 }); break;
    case "fv-del-wf": c.tasks[+d.j].workflows.splice(+d.k, 1); break;
    case "fv-add-c": c[d.list].push(d.list === "exports" ? { folder: "", pattern: "*.dta", to: "" } : { pattern: "", target: "", closeErp: false, backup: true }); break;
    case "fv-del-c": c[d.list].splice(+d.k, 1); break;
    default: return false;
  }
  markDirty(); drawTab(); return true;
}

/* =========================================================
   Datenaustausch → Filialverbund
   ========================================================= */

const FX = { items: [] };

async function renderFilialverbund(view) {
  clearInterval(VL.timer);
  view.innerHTML = `
    <div class="toolbar">
      <div class="filters">
        <input type="search" id="fxSearch" placeholder="Datei oder Meldung suchen" aria-label="Suchen">
        <select id="fxStandort" aria-label="Standort"><option value="">Alle Standorte</option></select>
        <select id="fxDir" aria-label="Richtung"><option value="">Beide Richtungen</option><option value="aus">an Standorte</option><option value="ein">von Standorten</option></select>
        <select id="fxStatus" aria-label="Status"><option value="">Alle</option><option value="offen">offen / Fehler</option><option value="wartet">wartet</option><option value="abgeholt">abgeholt</option>
          <option value="importiert">importiert</option><option value="fehler">Fehler</option><option value="erledigt">erledigt</option><option value="abgelegt">abgelegt</option></select>
      </div>
      <div class="filters"><button class="btn ghost" id="fxReload">Aktualisieren</button></div>
    </div>
    <div class="notice" id="fxMsg" hidden></div>
    <div class="card"><div class="table-wrap"><table class="list eglist" id="fxTable"><tbody><tr><td class="meta">Lade …</td></tr></tbody></table></div></div>
    <p class="meta">Dateien, die Belegpost an die Standorte verteilt hat, und Rückläufer der Standorte. Der Filial-Client quittiert jeden Import;
      „fehler“ heißt, dass Sätze nicht übernommen wurden oder der Import nicht lief.</p>`;
  let first = true;
  const show = (t, ok) => { const n = $("#fxMsg"); n.hidden = !t; n.className = "notice" + (ok ? " ok" : ""); n.textContent = t || ""; };
  const load = async () => {
    const p = new URLSearchParams();
    const q = $("#fxSearch").value.trim(); if (q) p.set("q", q);
    for (const [id, k] of [["fxStandort", "standort"], ["fxDir", "richtung"], ["fxStatus", "status"]]) if ($("#" + id).value) p.set(k, $("#" + id).value);
    try {
      const r = await api("GET", "api/austausch?" + p);
      if (first) { $("#fxStandort").insertAdjacentHTML("beforeend", r.standorte.map(s => `<option value="${esc(s.code)}">${esc(s.code)}${s.name ? " – " + esc(s.name) : ""}</option>`).join("")); first = false; }
      FX.items = r.items; FX.canChange = r.canChange;
      show(r.enabled ? "" : "Der Filialverbund ist nicht eingeschaltet (Einstellungen → Versand & Partner → Filialverbund).");
      drawFx();
    } catch (e) { show(e.message); }
  };
  let deb;
  $("#fxSearch").oninput = () => { clearTimeout(deb); deb = setTimeout(load, 300); };
  for (const id of ["fxStandort", "fxDir", "fxStatus"]) $("#" + id).onchange = load;
  $("#fxReload").onclick = load;
  $("#fxTable").addEventListener("click", async e => {
    const b = e.target.closest("[data-fx]"); if (!b) return;
    try { await api("POST", "api/austausch/status", { nrs: [+b.dataset.nr], aktion: b.dataset.fx }); load(); } catch (err) { show(err.message); }
  });
  await load();
  VL.timer = setInterval(() => { if (document.visibilityState === "visible" && $("#fxTable")) load(); }, 30000);
}

function drawFx() {
  const rows = FX.items;
  if (!rows.length) { $("#fxTable").innerHTML = `<tbody><tr><td class="empty">Keine Einträge.</td></tr></tbody>`; return; }
  const badge = s => `<span class="badge ${s === "fehler" ? "missing" : s === "wartet" || s === "abgeholt" ? "partial" : "ok"}">${esc(s)}</span>`;
  $("#fxTable").innerHTML = `<thead><tr><th>Zeit</th><th>Richtung</th><th>Standort</th><th>Datei</th><th>Status</th><th>Meldung</th><th></th></tr></thead>
    <tbody>${rows.map(x => `<tr>
      <td class="nowrap">${fmtDT(x.at)}</td>
      <td class="nowrap">${x.direction === "aus" ? "→ an Standort" : "← vom Standort"}</td>
      <td class="mono">${esc(x.standort)}</td>
      <td><b>${esc(x.fileName)}</b><small>${fmtSize(x.size)}${x.source ? " · " + esc(x.source) : ""}</small></td>
      <td class="nowrap">${badge(x.status)}<small>${x.statusAt ? fmtDT(x.statusAt) : ""}${x.attempts > 1 ? ` · ${x.attempts}× abgeholt` : ""}</small></td>
      <td>${esc(x.message || "")}</td>
      <td class="nowrap">${x.exists ? `<a href="api/austausch/${x.nr}/datei?ansehen=1" target="_blank" rel="noopener">Ansehen</a> · <a href="api/austausch/${x.nr}/datei" download>Herunterladen</a>` : '<span class="muted">Datei fehlt</span>'}
        ${FX.canChange && x.direction === "aus" && x.status === "fehler" ? ` · <button class="btn ghost small" data-fx="erledigt" data-nr="${x.nr}">Erledigt</button>` : ""}
        ${FX.canChange && x.direction === "aus" && x.status !== "wartet" ? ` · <button class="btn ghost small" data-fx="neu" data-nr="${x.nr}">Neu senden</button>` : ""}</td>
    </tr>`).join("")}</tbody>`;
}
