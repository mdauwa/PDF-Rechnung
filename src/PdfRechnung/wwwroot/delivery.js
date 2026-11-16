"use strict";

/* =========================================================
   Einstellungen → Versandwege (global) und → Kunden
   (Kunden mit eigenen Versandwegen, Regeln und Abruf)
   ========================================================= */

const CH_TEXT = { mail: "E-Mail", ftp: "FTP", ftps: "FTPS", sftp: "SFTP", as2: "AS2", gotomaxx: "gotomaxx-Drucker", exite: "eXite (FTPS)" };
const CONTENT_TEXT = { pdf: "PDF", xml: "E-Rechnungs-XML", beide: "PDF und XML", anlagen: "Anlagen des Belegs (EDI, XML, …)", "pdf+anlagen": "PDF und Anlagen", alles: "PDF, XML und Anlagen", connect: "ERP Suite Connect XML" };

/**
 * Felder eines Versandwegs. bind(path) liefert das Attribut für die Bindung (global: data-path, Kunde: data-cu).
 * own = Kunde (leere Felder = global, Auswahl „wie global“).
 */
function channelFields(c, P, bind, own) {
  const t = c.type;
  const v = k => c[k] ?? "";
  const txt = (k, label, ph = "", help = "", type = "text") =>
    field(label, `<input type="${type}" ${bind(`${P}.${k}`)} value="${esc(v(k))}" placeholder="${esc(ph)}">`, help);
  const num = (k, label, ph = "", help = "") =>
    field(label, `<input type="number" min="0" max="65535" ${bind(`${P}.${k}`, "int")} value="${c[k] ? esc(c[k]) : ""}" placeholder="${esc(ph)}">`, help);
  const lines = (k, label, ph = "", help = "") =>
    field(label, `<textarea rows="2" ${bind(`${P}.${k}`, "lines")} placeholder="${esc(ph)}">${esc((c[k] || []).join("\n"))}</textarea>`, help);
  const sel = (k, label, opts, help = "", redraw = false) =>
    field(label, `<select ${bind(`${P}.${k}`, "", redraw)}>${opts.map(([val, tx]) => `<option value="${esc(val)}" ${String(v(k)) === val ? "selected" : ""}>${esc(tx)}</option>`).join("")}</select>`, help);
  const yesNo = (k, label, help = "") => sel(k, label, own ? [["", "wie global"], ["ja", "ja"], ["nein", "nein"]] : [["", "Standard"], ["ja", "ja"], ["nein", "nein"]], help);
  const chk = (k, label) => `<label class="check"><input type="checkbox" ${bind(`${P}.${k}`)} ${c[k] ? "checked" : ""}> ${label}</label>`;
  const pw = (hasKey, newKey, label) => field(label,
    `<input type="password" autocomplete="new-password" ${bind(`${P}.${newKey}`)} placeholder="${c[hasKey] ? "gespeichert – leer lassen" : own ? "leer = wie global" : ""}">`);
  const ownPh = own ? "wie global" : "";

  let out = "";
  // ---- Inhalt ----
  if (t !== "gotomaxx") {
    const contents = Object.entries(CONTENT_TEXT).map(([k, tx]) => [k, t === "mail" && k === "pdf" ? "wie im Beleg (PDF + Anlagen)" : tx]);
    out += `<div class="grid3">
      ${sel("content", "Inhalt", own ? [["", "wie global"], ...contents] : contents, t === "exite" ? "eXite erwartet XML (ebInterface) oder EDIFACT." : "XML erst, wenn Belegpost die E-Rechnung selbst erzeugt.", true)}
      ${txt("fileName", "Dateiname (ohne Endung)", own ? "wie global" : "{Belegart}_{Belegnummer}", "Leer = Dateiname des Belegs.")}
      ${c.content === "connect" ? `<p class="help">ERP Suite Connect XML: der Abruf legt die Dateien in den Connect-Eingangsordner, der Ausgang sendet aus dem Connect-Ausgangsordner (Einstellungen → ERP Suite Connect) – solange unten nichts anderes eingetragen ist. Belege werden über diesen Versandweg nicht verschickt.</p>` : ""}
      ${["anlagen", "pdf+anlagen", "alles"].includes(c.content) ? txt("attachmentPattern", "Welche Anlagen", "*.edi; *.xml", "Leer = alle Anlagen (@@ma@@/@@mc@@).") : ""}
    </div>`;
  }
  // ---- Versand ----
  if (["ftp", "ftps", "sftp"].includes(t)) {
    out += `<h4>Versand</h4><div class="grid3">
      ${txt("host", "Server", ownPh || "ftp.kunde.at")}${num("port", "Port", t === "sftp" ? "22" : (c.implicitTls ? "990" : "21"))}
      ${txt("user", "Benutzer", ownPh, own ? "Leer = Benutzer und Kennwort wie global." : "")}${pw("hasPassword", "newPassword", "Kennwort")}
      ${txt("folder", "Zielordner am Server", ownPh || "/eingang")}
      ${t !== "ftp" ? txt("fingerprint", t === "sftp" ? "Hostschlüssel (SHA256:…)" : "Zertifikat (SHA-256)", "optional", "Leer = " + (t === "sftp" ? "jeder Hostschlüssel (nicht empfohlen)" : "nur gültige Zertifikate")) : ""}
    </div>
    <div class="grid3">${t === "ftps" ? chk("implicitTls", "Implizites TLS (Port 990)") : ""}${t !== "sftp" ? chk("activeMode", "Aktiver Modus") : ""}${chk("uploadAsTemp", "Erst als .part hochladen, dann umbenennen")}</div>`;
  }
  if (t === "exite") {
    out += `<h4>eXite-Postfach</h4><div class="grid3">
      ${txt("user", "Benutzer (Postfach)", ownPh)}${pw("hasPassword", "newPassword", "Kennwort")}
      ${txt("host", "Server", "www.exite.net")}${num("port", "Port", "21")}${txt("folder", "Verzeichnis", "leer = Start")}
      ${sel("exiteType", "Datenformat typ()", own ? [["", "wie global"], ["X", "X – XML"], ["E", "E – EDIFACT"]] : [["", "X – XML"], ["X", "X – XML"], ["E", "E – EDIFACT"]])}
      ${txt("exiteMessageClass", "Nachrichtenklasse umc()", "optional")}
      ${yesNo("exiteAck", "Empfangsbestätigung ack()")}
    </div><p class="help">Laut eXite-FTPS-Beschreibung: explizites TLS, Daten als „S…“, Befehl als „C…“ mit <code>ser(S) typ(X|E)</code>; eXite meldet „J…“ (verarbeitet) oder „E…“ (Fehler).</p>`;
  }
  if (t === "as2") {
    out += `<h4>AS2</h4><div class="grid3">
      ${txt("url", "Adresse des Partners (URL)", ownPh || "https://as2.partner.at/as2")}
      ${txt("as2From", "Eigene AS2-ID (AS2-From)", ownPh)}${txt("as2To", "AS2-ID des Partners (AS2-To)", ownPh)}
      ${txt("ownCertificate", "Eigenes Zertifikat (.pfx, Pfad am Server)", ownPh || "C:\\ProgramData\\Belegpost\\as2\\eigen.pfx")}
      ${pw("hasCertificatePassword", "newCertificatePassword", "Kennwort des Zertifikats")}
      ${txt("partnerCertificate", "Zertifikat des Partners (.cer, Pfad am Server)", ownPh)}
      ${yesNo("sign", "Signieren (SHA-256)")}${yesNo("encrypt", "Verschlüsseln (AES-256)")}${yesNo("requestMdn", "Empfangsbestätigung (MDN)")}
    </div><p class="help">Empfang: Partner senden an <code>https://${esc(location.host)}/as2</code> – erkannt an AS2-From/AS2-To, dafür unten „Abruf“ einschalten.</p>`;
  }
  if (t === "gotomaxx") {
    const printers = ST.data?.printers || [];
    out += `<div class="grid2">${sel("printer", "gotomaxx-Drucker", [["", own ? "wie global" : "Standarddrucker (Drucker & Eingang)"], ...printers.map(p => [p, p]),
      ...(c.printer && !printers.includes(c.printer) ? [[c.printer, c.printer + " (nicht installiert)"]] : [])])}</div>`;
  }
  if (t === "mail") {
    const tpl = (ST.s?.mailTemplates?.items || []).map(x => x.name).filter(Boolean);
    out += `<h4>Versand</h4><div class="grid3">
      ${lines("to", "An", "leer = wie im Beleg", "Ersetzt die Empfänger aus dem Druck.")}${lines("cc", "CC", "")}${lines("bcc", "BCC", "")}
      ${sel("template", "Mail-Vorlage", [["", "wie ermittelt"], ...tpl.map(x => [x, x])])}
    </div>`;
  }
  // ---- Ausgangsordner ----
  if (["ftp", "ftps", "sftp", "exite", "as2"].includes(t)) {
    out += `<h4>Ausgangsordner</h4><div class="grid3">
      ${txt("outboxFolder", "Dateien aus diesem Ordner senden", ownPh || "C:\\EDI\\ausgang", "z. B. EDI- oder XML-Dateien aus dem ERP. Leer = aus. Wird im Abruf-Intervall geprüft.")}
      ${txt("outboxPattern", "Welche Dateien", "*.edi; *.xml", "Leer = alle.")}
      ${txt("outboxArchive", "Danach verschieben nach", ownPh || "Unterordner „gesendet“")}
      ${sel("outboxValidate", "Vor dem Senden prüfen", [...(own ? [["", "wie global"]] : []), ["fehler", "ja – bei Fehlern zurückhalten"], ["warnung", "ja – auch bei Warnungen zurückhalten"], ["nein", "nicht prüfen"]], "EDIFACT/EANCOM, E-Rechnungs-XML und ZUGFeRD-PDF. Zurückgehaltene Dateien kommen mit Prüfbericht in den Unterordner „fehlerhaft“.")}
    </div>`;
  }
  // ---- Abruf ----
  if (t !== "gotomaxx") {
    const on = c.inboundEnabled === "ja";
    out += `<h4>Abruf</h4><div class="grid3">
      ${sel("inboundEnabled", "Abruf", own ? [["", "wie global"], ["ja", "ein"], ["nein", "aus"]] : [["nein", "aus"], ["ja", "ein"]], "", true)}
      ${t === "mail" ? sel("mailProtocol", "Postfach", [...(own ? [["", "wie global"]] : []), ["imaps", "IMAPS (993)"], ["imap", "IMAP (143)"], ["pop3s", "POP3S (995)"], ["pop3", "POP3 (110)"]]) : ""}
    </div>`;
    if (on) {
      out += `<div class="grid3">
        ${t === "mail" ? `${txt("host", "Mail-Server", ownPh || "imap.firma.at")}${num("port", "Port", "Standard")}${txt("user", "Benutzer", ownPh)}${pw("hasPassword", "newPassword", "Kennwort")}` : ""}
        ${t !== "as2" ? txt("inboundFolder", t === "mail" ? "Postfach-Ordner" : "Abholort am Server", t === "mail" ? "INBOX" : t === "exite" ? "–" : "/ausgang", t === "exite" ? "eXite: Abruf über ser(H)." : "") : ""}
        ${t !== "as2" && t !== "exite" ? txt("inboundPattern", "Dateien bzw. Anhänge", "*.xml; *.edi; *.pdf", "Leer = alle.") : ""}
        ${txt("inboundTarget", "Zielordner bei dir", ownPh || "C:\\ProgramData\\Belegpost\\eingang", "Der Belegpost-Eingangsordner macht aus PDFs Vorgänge. Leer = daten\\abruf\\" + t)}
        ${t !== "as2" && t !== "exite" ? sel("inboundAfter", "Danach am Server", [...(own ? [["", "wie global"]] : []), ["loeschen", "löschen"], ...(c.mailProtocol?.startsWith("pop3") ? [] : [["verschieben", "in Archivordner verschieben"]]), ["behalten", t === "mail" ? "als gelesen markieren" : "behalten (merken)"]], "", true) : ""}
        ${c.inboundAfter === "verschieben" ? txt("inboundArchive", t === "mail" ? "Archiv-Ordner im Postfach" : "Archivordner am Server", "/archiv") : ""}
        ${num("inboundInterval", "alle … Minuten", own ? "wie global" : "5", "Gilt auch für den Ausgangsordner.")}
      </div>`;
    }
  }
  return out;
}

/** Auf- und zuklappbare Karte; Standard zugeklappt, der Zustand bleibt beim Neuzeichnen erhalten. */
function foldCard(key, title, info, body) {
  const open = (ST.dlOpen ||= new Set()).has(key);
  return `<details class="st-card fold" data-fold="${esc(key)}" ${open ? "open" : ""}>
    <summary class="st-card-head"><span class="fold-title"><h3>${title}</h3>${info ? `<span class="fold-info">${info}</span>` : ""}</span></summary>
    <div class="st-card-body">${body}</div></details>`;
}

/** Kurzinfo für die zugeklappte Zeile. */
function channelInfo(c, own) {
  const b = [];
  if (c.host || c.url || c.user) b.push(esc(c.url || [c.user, c.host].filter(Boolean).join("@")));
  else if (c.printer) b.push(esc(c.printer));
  else if (c.type === "mail" && (c.to || []).length) b.push(esc(c.to.join(", ")));
  if (c.content && c.content !== "pdf") b.push(esc(CONTENT_TEXT[c.content] || c.content));
  if (c.inboundEnabled === "ja") b.push('<span class="badge ok">Abruf</span>');
  if (c.outboxFolder) b.push('<span class="badge">Ausgangsordner</span>');
  if (!b.length) b.push(`<span class="meta-inline">${own ? "wie global" : "nicht eingerichtet"}</span>`);
  return b.join(" ");
}

/* ---------------- Tab: Versandwege (global) ---------------- */

function tabDelivery() {
  const D = (ST.s.delivery ||= { channels: [] });
  const bind = (path, type = "", redraw = false) => `data-path="${path.replace(/^/, "")}"${type ? ` data-type="${type}"` : ""}${redraw ? ' data-redraw="1"' : ""}`;
  return section("Versandwege",
    `<p class="help">Hier stehen die <b>globalen</b> Einstellungen je Versandweg. Beim Kunden (Einstellungen → Kunden) wählst du die Versandwege aus
       und übersteuerst nur, was für ihn anders ist – leere Felder gelten wie hier. Ohne Kunden-Eintrag wird wie bisher per E-Mail versendet.</p>
     <div id="dlRuns"><p class="meta">Lade Abrufe …</p></div>`) +
    `<div class="actions fold-all"><button class="btn ghost small" data-st="fold-all" data-open="1">alle aufklappen</button><button class="btn ghost small" data-st="fold-all" data-open="0">alle zuklappen</button></div>` +
    D.channels.map((c, i) => foldCard("g-" + c.type, esc(CH_TEXT[c.type] || c.type), channelInfo(c, false),
      channelFields(c, `delivery.channels.${i}`, bind, false) +
      `<div class="actions"><button class="btn ghost small" data-st="dl-test" data-type="${esc(c.type)}">Verbindung testen</button>
        <span class="meta-inline">prüft die gespeicherte Einstellung</span></div>`)).join("");
}

async function loadDeliveryRuns() {
  const box = $("#dlRuns"); if (!box) return;
  try {
    const r = await api("GET", "api/admin/abruf");
    const dt = d => d ? new Date(d).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "–";
    const runs = Object.fromEntries((r.runs || []).map(x => [x.key, x]));
    box.innerHTML = (r.jobs || []).length ? `<h4>Abrufe</h4><div class="table-wrap"><table class="list mini"><thead><tr><th>Abruf</th><th>Art</th><th>Ziel</th><th>Intervall</th><th>Zuletzt</th><th>Ergebnis</th><th></th></tr></thead><tbody>
      ${r.jobs.map(j => { const x = runs[j.key]; return `<tr><td>${esc(j.label)}</td><td>${esc(j.type)}</td><td><small>${esc(j.target)}</small></td><td>${j.interval} Min.</td>
        <td class="nowrap">${dt(x?.at)}</td><td>${x ? (x.ok ? esc(x.message) : `<span class="bad">${esc(x.message)}</span>`) : '<span class="meta-inline">noch nicht gelaufen</span>'}</td>
        <td><button class="btn ghost small" data-st="dl-run" data-key="${esc(j.key)}">Jetzt abrufen</button></td></tr>`; }).join("")}</tbody></table></div>`
      : `<p class="meta">Kein Abruf eingeschaltet.</p>`;
  } catch (e) { box.innerHTML = `<p class="bad">${esc(e.message)}</p>`; }
}

/* ---------------- Tab: Kunden ---------------- */

async function loadCustomers() {
  try { ST.cust = await api("GET", "api/admin/kunden"); }
  catch (e) { ST.cust = { items: [], error: e.message }; }
  if (ST.tab === "kunden") drawTab();
}

function tabCustomers() {
  const C = ST.cust;
  if (!C) { loadCustomers(); return `<p class="meta">Lade Kunden …</p>`; }
  if (C.error) return `<div class="notice">${esc(C.error)}</div>`;
  if (ST.custEdit) return customerMask();
  const or = v => v ? esc(v) : '<span class="meta-inline">–</span>';
  return section("Kunden",
    `<p class="help">Belegpost erkennt den Kunden an der <b>Kundennummer</b> aus dem Druck (<code>@@xi BUYER_ID=…@@</code>) oder an einer seiner <b>E-Mail-Adressen</b>
       (auch <code>*@kunde.at</code>). Für erkannte Kunden gelten ihre eigenen Versandwege – mehrere gleichzeitig möglich – und ihre Regeln.</p>
     ${C.items.length ? `<div class="table-wrap"><table class="list users-compact"><thead><tr><th>Nummer</th><th>Name</th><th>Mandant</th><th>E-Mail-Erkennung</th><th>Versandwege</th><th>Regeln</th><th></th></tr></thead><tbody>
       ${C.items.map(c => `<tr class="clickable ${c.active ? "" : "inactive"}" data-st="cu-open" data-id="${esc(c.id)}">
         <td class="mono"><b>${or(c.number)}</b></td><td>${or(c.name)}${c.active ? "" : ' <span class="badge">inaktiv</span>'}</td><td>${or(c.mandant)}</td>
         <td><small>${or((c.emails || []).join(", "))}</small></td>
         <td>${(c.channels || []).map(ch => `<span class="badge${ch.enabled ? "" : " partial"}">${esc(ch.name || CH_TEXT[ch.settings.type])}</span>`).join(" ") || '<span class="meta-inline">–</span>'}</td>
         <td>${(c.rules || []).length || "–"}</td>
         <td class="nowrap"><button class="btn ghost small" data-st="cu-open" data-id="${esc(c.id)}">Bearbeiten</button></td></tr>`).join("")}
       </tbody></table></div>` : `<p class="meta">Noch keine Kunden – alle Belege werden wie eingestellt per E-Mail versendet.</p>`}`,
    `<button class="btn ghost" data-st="cu-add">+ Kunde</button>`);
}

function customerMask() {
  const c = ST.custEdit;
  const bind = (path, type = "", redraw = false) => `data-cu="${path}"${type ? ` data-type="${type}"` : ""}${redraw ? ' data-redraw="1"' : ""}`;
  const t = (k, label, ph = "", help = "") => field(label, `<input ${bind(k)} value="${esc(c[k] ?? "")}" placeholder="${esc(ph)}">`, help);
  const mand = (ST.s?.mandanten?.items || []).filter(m => m.number);
  const chOpts = Object.entries(CH_TEXT);
  const channels = c.channels.map((ch, j) => foldCard(`c-${c.id || "neu"}-${ch.id || j}`,
      esc(ch.name || CH_TEXT[ch.settings.type]),
      `<span class="badge">${esc(CH_TEXT[ch.settings.type])}</span>${ch.enabled ? "" : ' <span class="badge partial">aus</span>'} ${channelInfo(ch.settings, true)}`,
      `<div class="actions fold-tools">
          <label class="check"><input type="checkbox" ${bind(`channels.${j}.enabled`)} ${ch.enabled ? "checked" : ""}> aktiv</label>
          ${ch.id ? `<button class="btn ghost small" data-st="cu-test" data-ch="${esc(ch.id)}">Verbindung testen</button>` : ""}
          <span class="spacer"></span>
          <button class="btn ghost small danger" data-st="cu-ch-del" data-j="${j}">Versandweg entfernen</button></div>
        <div class="grid3">
          ${field("Versandweg", `<select ${bind(`channels.${j}.settings.type`, "", true)}>${chOpts.map(([k, tx]) => `<option value="${k}" ${ch.settings.type === k ? "selected" : ""}>${tx}</option>`).join("")}</select>`)}
          ${field("Bezeichnung", `<input ${bind(`channels.${j}.name`)} value="${esc(ch.name || "")}" placeholder="${esc(CH_TEXT[ch.settings.type])}">`, "z. B. „Buchhaltung“ oder „EDI“.")}
        </div>
        ${channelFields(ch.settings, `channels.${j}.settings`, bind, true)}`)).join("");
  const ruleRow = (r, k) => `<tr>
      <td><input ${bind(`rules.${k}.name`)} value="${esc(r.name || "")}" placeholder="Regel ${k + 1}"></td>
      <td><input ${bind(`rules.${k}.documentTypes`, "lines")} value="${esc((r.documentTypes || []).join(", "))}" placeholder="egal"></td>
      <td><input ${bind(`rules.${k}.textContains`)} value="${esc(r.textContains || "")}" placeholder="egal"></td>
      <td><input ${bind(`rules.${k}.sources`, "lines")} value="${esc((r.sources || []).join(", "))}" placeholder="egal"></td>
      <td><input ${bind(`rules.${k}.formats`, "lines")} value="${esc((r.formats || []).join(", "))}" placeholder="egal"></td>
      <td>${r.noDelivery ? '<span class="meta-inline">kein Versand</span>' : c.channels.map(ch => ch.id
          ? `<label class="check"><input type="checkbox" data-cu-rule="${k}" value="${esc(ch.id)}" ${(r.channels || []).includes(ch.id) ? "checked" : ""}> ${esc(ch.name || CH_TEXT[ch.settings.type])}</label>` : "").join("") || '<span class="meta-inline">erst speichern</span>'}</td>
      <td><input type="checkbox" ${bind(`rules.${k}.noDelivery`, "", true)} ${r.noDelivery ? "checked" : ""}></td>
      <td><input type="checkbox" ${bind(`rules.${k}.enabled`)} ${r.enabled ? "checked" : ""}></td>
      <td><button class="icon-btn" data-st="cu-rule-del" data-k="${k}">✕</button></td></tr>`;
  return `<div class="user-mask-head"><button class="btn ghost" data-st="cu-back">← Alle Kunden</button>
      <h2>${esc(c.name || c.number || "Neuer Kunde")}</h2>${c.active ? "" : '<span class="badge">inaktiv</span>'}
      <span class="spacer"></span>
      ${c.id ? `<button class="btn ghost danger" data-st="cu-del">Löschen</button>` : ""}
      <button class="btn" data-st="cu-save" ${ST.custDirty ? "" : "disabled"}>Kunde speichern</button></div>` +
    section("Kunde",
      `<div class="grid3">
        ${t("number", "Kundennummer", "z. B. 10023", "Wie im ERP (@@xi BUYER_ID=…@@); Vornullen zählen nicht.")}
        ${t("name", "Name", "Firma GmbH")}
        ${field("Mandant", `<select ${bind("mandant")}>${[["", "alle"], ...mand.map(m => [m.number, m.name ? `${m.number} – ${m.name}` : m.number])].map(([v, tx]) => `<option value="${esc(v)}" ${c.mandant === v ? "selected" : ""}>${esc(tx)}</option>`).join("")}</select>`, "Nur Belege dieses Mandanten.")}
      </div>
      <div class="grid2">
        ${field("Erkennen auch an E-Mail-Adressen", `<textarea rows="2" ${bind("emails", "lines")} placeholder="buchhaltung@kunde.at\n*@kunde.at">${esc((c.emails || []).join("\n"))}</textarea>`, "Falls der Beleg keine Kundennummer hat.")}
        ${field("Notiz", `<textarea rows="2" ${bind("note")}>${esc(c.note || "")}</textarea>`)}
      </div>
      <label class="check"><input type="checkbox" ${bind("active")} ${c.active ? "checked" : ""}> aktiv</label>`) +
    section("Versandwege", c.channels.length ? `<p class="help">Alle aktiven Versandwege werden ausgeführt – der Beleg gilt als versendet, wenn alle erfolgreich waren.
        Leere Felder gelten wie unter Einstellungen → Versandwege.</p>` : `<p class="meta">Noch keine Versandwege – Belege dieses Kunden gehen wie bisher per E-Mail.</p>`,
      `<button class="btn ghost small" data-st="cu-ch-add">+ Versandweg</button>`) + channels +
    section("Regeln",
      `<p class="help">Die erste passende Regel bestimmt, welche Versandwege für einen Beleg gelten (* und ? als Platzhalter, mehrere Werte mit Komma).
        Passt keine, gelten alle aktiven Versandwege.</p>
       ${(c.rules || []).length ? `<div class="table-wrap"><table class="list mini cu-rules"><thead><tr><th>Name</th><th>Belegart</th><th>Betreff/Datei enthält</th><th>Quelle</th><th>E-Rechnung</th><th>→ Versandwege</th><th>kein Versand</th><th>aktiv</th><th></th></tr></thead><tbody>
         ${c.rules.map(ruleRow).join("")}</tbody></table></div>` : `<p class="meta">Keine Regeln.</p>`}`,
      `<button class="btn ghost small" data-st="cu-rule-add">+ Regel</button>`);
}

function newChannel(type = "mail") {
  return { id: "", name: "", enabled: true, settings: { type, content: "", fileName: "", to: [], cc: [], bcc: [], uploadAsTemp: true, inboundEnabled: "" } };
}

/** Klicks der Seiten Versandwege und Kunden. true = erledigt. */
async function deliveryClick(a, btn, e) {
  const c = ST.custEdit;
  switch (a) {
    case "dl-test": {
      if (ST.dirty) throw new Error("Bitte zuerst speichern.");
      const r = await api("POST", "api/admin/versandwege/test", { type: btn.dataset.type });
      msg(r.message, r.ok); return true;
    }
    case "dl-run": {
      const r = await api("POST", "api/admin/abruf/jetzt?key=" + encodeURIComponent(btn.dataset.key));
      msg(r.message, r.ok); loadDeliveryRuns(); return true;
    }
    case "cu-add":
      ST.custEdit = { id: "", number: "", name: "", mandant: "", emails: [], active: true, note: "", channels: [newChannel()], rules: [] };
      ST.custDirty = true; drawTab(); window.scrollTo(0, 0); return true;
    case "cu-open": {
      const id = e.target.closest("[data-st]").dataset.id;
      ST.custEdit = structuredClone(ST.cust.items.find(x => x.id === id)); ST.custDirty = false; drawTab(); window.scrollTo(0, 0); return true;
    }
    case "cu-back":
      if (ST.custDirty && !await confirmDialog("Ungespeicherte Änderungen", "Die Änderungen an diesem Kunden sind noch nicht gespeichert. Trotzdem zurück?", "Zurück")) return true;
      ST.custEdit = null; ST.custDirty = false; drawTab(); return true;
    case "cu-save": {
      const r = await api("PUT", "api/admin/kunden", c);
      ST.custEdit = r.customer; ST.custDirty = false;
      const i = ST.cust.items.findIndex(x => x.id === r.customer.id);
      if (i >= 0) ST.cust.items[i] = structuredClone(r.customer); else ST.cust.items.push(structuredClone(r.customer));
      msg("Kunde gespeichert.", true); drawTab(); return true;
    }
    case "cu-del":
      if (!await confirmDialog("Kunde löschen", `Kunde „${esc(c.name || c.number)}“ mit seinen Versandwegen und Regeln löschen?`, "Löschen", true)) return true;
      await api("DELETE", "api/admin/kunden/" + encodeURIComponent(c.id));
      ST.cust.items = ST.cust.items.filter(x => x.id !== c.id); ST.custEdit = null; ST.custDirty = false; drawTab(); return true;
    case "cu-ch-add": c.channels.push(newChannel()); (ST.dlOpen ||= new Set()).add(`c-${c.id || "neu"}-${c.channels.length - 1}`); ST.custDirty = true; drawTab(); return true;
    case "fold-all": {
      const open = btn.dataset.open === "1";
      document.querySelectorAll("details.fold").forEach(d => { d.open = open; (ST.dlOpen ||= new Set())[open ? "add" : "delete"](d.dataset.fold); });
      return true;
    }
    case "cu-ch-del": c.channels.splice(+btn.dataset.j, 1); ST.custDirty = true; drawTab(); return true;
    case "cu-rule-add": (c.rules ||= []).push({ name: "", enabled: true, documentTypes: [], formats: [], sources: [], stationeries: [], recipients: [], textContains: "", channels: [], noDelivery: false }); ST.custDirty = true; drawTab(); return true;
    case "cu-rule-del": c.rules.splice(+btn.dataset.k, 1); ST.custDirty = true; drawTab(); return true;
    case "cu-test": {
      if (ST.custDirty) throw new Error("Bitte zuerst den Kunden speichern.");
      const r = await api("POST", "api/admin/versandwege/test", { customerId: c.id, channelId: btn.dataset.ch });
      msg(r.message, r.ok); return true;
    }
  }
  return false;
}

/** Eingaben in der Kunden-Maske. true = erledigt. */
function deliveryInput(el, e) {
  if (el.dataset?.cuRule !== undefined) {
    const r = ST.custEdit.rules[+el.dataset.cuRule];
    r.channels = [...document.querySelectorAll(`[data-cu-rule="${el.dataset.cuRule}"]:checked`)].map(x => x.value);
    ST.custDirty = true; updateCustSave(); return true;
  }
  if (el.dataset?.cu === undefined) return false;
  let v = el.type === "checkbox" ? el.checked : el.value;
  if (el.dataset.type === "int") v = v === "" ? 0 : parseInt(v, 10) || 0;
  if (el.dataset.type === "lines") v = String(v).split(/[\n,;]/).map(x => x.trim()).filter(Boolean);
  set(ST.custEdit, el.dataset.cu, v);
  ST.custDirty = true; updateCustSave();
  if (el.dataset.redraw === "1" && e.type === "change") drawTab();
  return true;
}

function updateCustSave() {
  const b = document.querySelector('[data-st="cu-save"]'); if (b) b.disabled = !ST.custDirty;
}

/** Auf-/Zuklappen merken (Ereignis „toggle“ steigt nicht auf – daher im Erfassungsmodus). */
function trackFold(e) {
  const d = e.target;
  if (!(d instanceof HTMLDetailsElement) || !d.classList.contains("fold")) return;
  (ST.dlOpen ||= new Set())[d.open ? "add" : "delete"](d.dataset.fold);
}
