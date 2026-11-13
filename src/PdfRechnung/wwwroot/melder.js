"use strict";

/* =========================================================
   Melder & Hinweise: neue eigene Belege sofort öffnen –
   im offenen Portal-Tab oder über den „Belegpost Melder“ auf dem PC.
   ========================================================= */

async function renderMelder(view) {
  view.innerHTML = `<p class="meta">Lade …</p>`;
  let d;
  try { d = await api("GET", "api/melder/geraete"); }
  catch (e) { view.innerHTML = `<div class="notice">${esc(e.message)}</div>`; return; }
  const fmt = s => s ? new Date(s).toLocaleString("de-AT", { day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit" }) : "–";
  const notifyState = !window.Notification ? "nicht verfügbar" : Notification.permission === "granted" ? "erlaubt" : Notification.permission === "denied" ? "im Browser blockiert" : "noch nicht erlaubt";
  const isMac = /Mac/i.test(navigator.platform || navigator.userAgent);
  const localhost = /^(https?:\/\/)?(localhost|127\.0\.0\.1|\[::1\])(:|$)/i.test(d.server.replace(/^https?:\/\//, ""));

  view.innerHTML = `
  <div class="card"><div class="card-head"><h2>Wie kommt ein gedruckter Beleg zu mir?</h2></div><div class="card-body">
    <p class="help">Wenn du im ERP druckst, erkennt Belegpost dich am Windows-Benutzer des Druckauftrags oder an der Bedienernummer
      (<code>@@pu007@@</code>, Zuordnung unter Einstellungen → Benutzer). Der Beleg erscheint dann unter <b>Meine Belege</b> und kann sofort geöffnet werden:</p>
    <ul class="steps"><li><b>Belegpost Melder</b> (empfohlen): kleines Programm im Infobereich deines PCs – öffnet den Beleg automatisch im Browser, auch wenn du im ERP auf dem Terminalserver (RDP) druckst.</li>
      <li><b>E-Mail in Outlook</b>: Im Melder „Bei neuem Beleg → Outlook“ wählen – oder im Vorgang auf <b>In Outlook öffnen</b> klicken. Die Mail öffnet sich mit Empfängern, Text (CI-Vorlage) und allen Anhängen als Entwurf in deinem Outlook; du prüfst und sendest selbst. Solche Belege werden nicht automatisch versendet.</li>
      <li><b>Offener Portal-Tab</b>: Ist das Portal in einem Tab offen, springt es selbst zum neuen Beleg oder zeigt einen Hinweis.</li></ul>
  </div></div>

  <div class="card"><div class="card-head"><h2>Belegpost Melder auf diesem PC</h2></div><div class="card-body">
    ${!d.canPair ? `<div class="notice">Der Melder wird mit deinem eigenen Portal-Konto verbunden. Als Server-Administrator geht das nicht – bitte mit dem Microsoft-Konto anmelden.</div>` : `
    ${localhost ? `<div class="notice">Du bist über <b>localhost</b> angemeldet. Für einen anderen PC das Portal dort mit der Server-Adresse öffnen (z. B. https://server:${location.port || 443}/) und den Melder von dort verbinden.</div>` : ""}
    <ol class="steps">
      <li>${isMac
        ? (d.downloadMac ? `<a class="btn ghost" href="${esc(d.downloadMac)}" download>Belegpost Melder für Mac herunterladen</a>, entpacken, in „Programme“ ziehen und starten (beim ersten Mal Rechtsklick → Öffnen). Er erscheint oben in der Menüleiste.`
          : `Den Belegpost Melder für Mac (<code>Belegpost-Melder-macOS.zip</code>) vom Administrator holen – er liegt am Server noch nicht neben <code>Belegpost.exe</code>.`)
        : (d.download ? `<a class="btn ghost" href="${esc(d.download)}" download>Belegpost Melder herunterladen</a> und starten (keine Administratorrechte nötig, startet künftig mit Windows).`
          : `Den Belegpost Melder (<code>Belegpost-Melder.exe</code>) vom Administrator holen und starten – er liegt am Server noch nicht neben <code>Belegpost.exe</code>.`)}
        ${!isMac && d.downloadMac ? `<br><span class="meta-inline">Für Mac: <a href="${esc(d.downloadMac)}" download>Belegpost Melder für macOS</a></span>` : ""}
        ${isMac && d.download ? `<br><span class="meta-inline">Für Windows: <a href="${esc(d.download)}" download>Belegpost-Melder.exe</a></span>` : ""}</li>
      <li><label class="field inline"><span class="lbl">Name dieses PCs</span><input id="mlName" value="${esc(store.get("melder.name", ""))}" placeholder="z. B. Büro Martin"></label>
        <button class="btn" id="mlPair">Melder verbinden</button></li>
    </ol>
    <div id="mlPairBox"></div>`}
  </div></div>

  <div class="card"><div class="card-head"><h2>Verbundene Melder</h2></div><div class="card-body">
    ${d.devices.length ? `<table class="list mini"><thead><tr><th>PC</th><th>verbunden seit</th><th>zuletzt aktiv</th><th></th></tr></thead><tbody>
      ${d.devices.map(x => `<tr><td>${esc(x.name)}</td><td>${fmt(x.created)}</td><td>${fmt(x.lastSeen)}</td>
        <td><button class="btn ghost small" data-del="${esc(x.id)}">Trennen</button></td></tr>`).join("")}</tbody></table>`
      : `<p class="meta">Noch kein Melder verbunden.</p>`}
    ${d.canPair ? `<div class="actions"><button class="btn ghost" id="mlTest">Test: letzten Beleg an meine Melder schicken</button><span class="meta-inline" id="mlTestMsg"></span></div>` : ""}
  </div></div>

  <div class="card"><div class="card-head"><h2>Hinweise in diesem Browser</h2></div><div class="card-body">
    <label class="check"><input type="checkbox" id="mlAuto" ${store.get("live.auto", true) ? "checked" : ""}> Bei einem neuen eigenen Beleg direkt zur Ansicht springen</label>
    <label class="check"><input type="checkbox" id="mlNotify" ${store.get("live.notify", false) ? "checked" : ""}> Windows-Benachrichtigung, wenn das Portal im Hintergrund ist
      <span class="meta-inline">(${notifyState})</span></label>
    <p class="help">Live-Verbindung: <span id="mlLive">${live && live.readyState === 1 ? '<span class="badge ok">verbunden</span>' : '<span class="badge partial">getrennt</span>'}</span></p>
  </div></div>`;

  $("#mlAuto", view).onchange = e => store.set("live.auto", e.target.checked);
  $("#mlNotify", view).onchange = async e => {
    store.set("live.notify", e.target.checked);
    if (e.target.checked && window.Notification && Notification.permission === "default") await Notification.requestPermission();
  };
  view.querySelectorAll("[data-del]").forEach(b => b.onclick = async () => {
    if (!await confirmDialog("Melder trennen", "Diesen PC trennen? Der Melder dort meldet sich danach nicht mehr.", "Trennen", true)) return;
    try { await api("DELETE", "api/melder/geraete/" + encodeURIComponent(b.dataset.del)); renderMelder(view); } catch (ex) { toast(ex.message, false); }
  });
  const test = $("#mlTest", view);
  if (test) test.onclick = async () => {
    try {
      const r = await api("POST", "api/melder/test");
      $("#mlTestMsg", view).textContent = r.listeners ? `gesendet an ${r.listeners} Empfänger (Melder und offene Tabs)` : "niemand verbunden – ist der Melder gestartet?";
    } catch (ex) { $("#mlTestMsg", view).textContent = ex.message; }
  };
  const pair = $("#mlPair", view);
  if (pair) pair.onclick = async () => {
    const name = $("#mlName", view).value.trim() || "PC";
    store.set("melder.name", name);
    try {
      const r = await api("POST", "api/melder/koppeln", { name });
      $("#mlPairBox", view).innerHTML = `<div class="notice ok">
        <p><b>Fast fertig:</b> <a class="btn" href="${esc(r.link)}">Im Melder übernehmen</a> – öffnet den Melder auf diesem PC und verbindet ihn.</p>
        <p class="help">Klappt der Knopf nicht (Melder noch nie gestartet), im Melder „Verbinden …“ wählen und diesen Code einfügen:</p>
        <div class="melder-code" id="mlCode">${esc(r.code)}</div>
        <div class="actions"><button class="btn ghost small" id="mlCopy">Code kopieren</button>
          <span class="meta-inline">Der Code gilt nur für diesen PC – nicht weitergeben.</span></div></div>`;
      $("#mlCopy", view).onclick = async () => { try { await navigator.clipboard.writeText(r.code); toast("Code kopiert"); } catch { toast("Bitte den Code markieren und kopieren.", false); } };
    } catch (ex) { toast(ex.message, false); }
  };
}
