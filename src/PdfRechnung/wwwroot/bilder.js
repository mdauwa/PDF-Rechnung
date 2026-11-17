"use strict";

/* =========================================================
   Einstellungen → E-Mail → Bilder
   Globale Bilder (Logo, Zertifikat, Banner …) hochladen und benennen.
   In Mailtexten, Vorlagen und Signaturen eingebunden über {Bild:Name} bzw. {Bild:Name|Breite}.
   Änderungen wirken sofort (ohne „Speichern“) – ein ausgetauschtes Bild gilt für alle Texte.
   ========================================================= */

const IMG = { list: null, error: "" };

async function loadImages() {
  try { IMG.list = await api("GET", "api/mailbilder-katalog"); IMG.error = ""; }
  catch (e) { IMG.list = []; IMG.error = e.message; }
  if (typeof rteLoadImages === "function") rteLoadImages(true); // Auswahl in den Editoren aktualisieren
  if (ST.tab === "bilder") drawTab();
}

const imgKb = b => b >= 1024 * 1024 ? (b / 1024 / 1024).toFixed(1) + " MB" : Math.max(1, Math.round(b / 1024)) + " KB";

function tabImages() {
  if (!IMG.list) { loadImages(); return section("Bilder", `<p class="meta">Lade …</p>`); }
  const rows = IMG.list.map((x, i) => `<tr data-i="${i}">
      <td class="img-thumb">${x.missing ? `<span class="badge missing">Datei fehlt</span>` : `<img src="${esc(x.url)}" alt="">`}</td>
      <td><input type="text" data-img="name" value="${esc(x.name)}" maxlength="40"></td>
      <td><code class="img-var">${esc(x.variable)}</code> <button type="button" class="btn ghost small" data-st="img-copy" data-i="${i}" title="Variable kopieren">⧉</button></td>
      <td class="meta">${x.pixelWidth ? `${x.pixelWidth} × ${x.pixelHeight} px` : "–"}${x.bytes ? "<br>" + imgKb(x.bytes) : ""}</td>
      <td><input type="number" data-img="width" min="0" max="1200" value="${x.width ?? ""}" placeholder="auto"></td>
      <td class="img-act">
        <button type="button" class="btn ghost small" data-st="img-save" data-i="${i}">Übernehmen</button>
        <label class="btn ghost small" title="Neues Bild unter demselben Namen – gilt sofort in allen Texten">Austauschen<input type="file" accept="image/png,image/jpeg,image/gif" data-img="replace" data-i="${i}" hidden></label>
        <button type="button" class="btn ghost small danger" data-st="img-del" data-i="${i}">Entfernen</button>
      </td>
    </tr>`).join("");
  return section("Bilder für Mailtexte und Signaturen", `
      <p class="meta">Bilder hier einmal hochladen und in Mailtexten, Mail-Vorlagen und Signaturen über die Variable
        <code>{Bild:Name}</code> einbinden (im Editor unter <b>{…}</b> → „Bilder“). Mit <code>{Bild:Name|150}</code> wird das Bild 150 Pixel breit.
        Wird ein Bild ausgetauscht, gilt das sofort für alle Texte. Beim Versand werden die Bilder eingebettet – der Empfänger muss nichts nachladen.</p>
      ${IMG.error ? `<p class="notice">${esc(IMG.error)}</p>` : ""}
      <div class="img-upload">
        ${field("Name", `<input type="text" id="imgName" maxlength="40" placeholder="z. B. Logo, Zertifikat, Banner">`, "Buchstaben, Ziffern, _ und -, ohne Leerzeichen.")}
        ${field("Breite (Pixel)", `<input type="number" id="imgWidth" min="0" max="1200" placeholder="original">`, "Leer = Originalgröße (höchstens Mailbreite).")}
        ${field("Bild", `<input type="file" id="imgFile" accept="image/png,image/jpeg,image/gif">`, "PNG, JPG oder GIF, höchstens 2 MB.")}
        <div class="field"><span class="lbl">&nbsp;</span><button type="button" class="btn" data-st="img-add">Hochladen</button></div>
      </div>
      ${IMG.list.length ? `<table class="list img-list">
        <colgroup><col style="width:90px"><col style="width:180px"><col><col style="width:120px"><col style="width:100px"><col style="width:340px"></colgroup>
        <thead><tr><th>Bild</th><th>Name</th><th>Variable</th><th>Größe</th><th>Breite</th><th></th></tr></thead>
        <tbody>${rows}</tbody></table>` : `<p class="meta">Noch keine Bilder.</p>`}`);
}

/** Bild an den Server schicken (neu oder austauschen). */
async function uploadImage(name, width, file) {
  if (!file) throw new Error("Bitte ein Bild wählen.");
  if (file.size > 2 * 1024 * 1024) throw new Error("Das Bild ist größer als 2 MB.");
  const q = new URLSearchParams({ name }); if (width) q.set("width", width);
  const res = await fetch("api/mailbilder-katalog?" + q, { method: "POST", body: file, credentials: "same-origin",
    headers: { "Content-Type": file.type || "application/octet-stream", "X-Requested-With": "Belegpost" } });
  const j = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(j.error || `Hochladen fehlgeschlagen (${res.status})`);
  return j;
}

/** Klicks im Reiter „Bilder“. true = behandelt. */
async function imagesClick(a, btn, e) {
  if (!a.startsWith("img-")) return false;
  const i = +(btn?.dataset.i ?? -1), x = IMG.list?.[i], row = btn?.closest("tr");
  switch (a) {
    case "img-add": {
      const name = $("#imgName").value.trim();
      if (!name) { msg("Bitte einen Namen angeben."); return true; }
      if (IMG.list.some(y => y.name.toLowerCase() === name.toLowerCase())
        && !await confirmDialog("Bild ersetzen", `Ein Bild „${esc(name)}“ gibt es schon. Ersetzen? Das gilt sofort für alle Texte.`, "Ersetzen")) return true;
      await uploadImage(name, $("#imgWidth").value, $("#imgFile").files[0]);
      msg(`Bild „${name}“ gespeichert – Variable {Bild:${name}}.`, true);
      await loadImages(); break;
    }
    case "img-save": {
      const name = row.querySelector('[data-img="name"]').value.trim(), width = row.querySelector('[data-img="width"]').value;
      await api("PUT", `api/mailbilder-katalog/${encodeURIComponent(x.name)}`, { name, width: width ? +width : null });
      msg(name.toLowerCase() !== x.name.toLowerCase()
        ? `Umbenannt in „${name}“. Texte mit {Bild:${x.name}} bitte anpassen.` : "Gespeichert.", true);
      await loadImages(); break;
    }
    case "img-del":
      if (!await confirmDialog("Bild entfernen", `„${esc(x.name)}“ aus der Bildverwaltung entfernen? Texte mit <code>{Bild:${esc(x.name)}}</code> zeigen dann kein Bild mehr.`, "Entfernen", true)) return true;
      await api("DELETE", `api/mailbilder-katalog/${encodeURIComponent(x.name)}`);
      await loadImages(); break;
    case "img-copy":
      try { await navigator.clipboard.writeText(x.variable); toast("Kopiert: " + x.variable); }
      catch { msg("Kopieren nicht möglich – bitte markieren: " + x.variable); }
      break;
  }
  return true;
}

/** Eingaben im Reiter „Bilder“ (nicht Teil der Einstellungen). true = behandelt. */
function imagesInput(el, e) {
  if (el.dataset?.img === undefined && !["imgName", "imgWidth", "imgFile"].includes(el.id)) return false;
  if (el.dataset.img === "replace" && e.type === "change" && el.files[0]) {
    const x = IMG.list[+el.dataset.i];
    uploadImage(x.name, x.width, el.files[0])
      .then(() => { msg(`Bild „${x.name}“ ausgetauscht.`, true); return loadImages(); })
      .catch(err => msg(err.message));
  }
  return true;
}
