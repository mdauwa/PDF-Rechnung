"use strict";

/* =========================================================
   Formatierter Text für Mailtext, Signatur und Fußzeile
   – Werkzeugleiste (Schrift, Größe, Farbe, Fett …, Link, Bild, Liste, Ausrichtung, Platzhalter)
   – Einfügen aus der Zwischenablage (Outlook, Word, Webseiten): wird bereinigt, Bilder werden hochgeladen
   – HTML-Ansicht für Profis
   Der Server bereinigt beim Speichern und Versenden noch einmal (gleiche Regeln).
   ========================================================= */

const RTE_TAGS = new Set(["P", "BR", "DIV", "SPAN", "B", "STRONG", "I", "EM", "U", "S", "A", "IMG", "UL", "OL", "LI", "TABLE", "THEAD", "TBODY", "TR", "TD", "TH",
  "H1", "H2", "H3", "H4", "HR", "BLOCKQUOTE", "SUP", "SUB", "SMALL"]);
const RTE_ATTRS = new Set(["href", "src", "alt", "title", "width", "height", "align", "valign", "colspan", "rowspan", "style", "cellpadding", "cellspacing", "border", "target", "bgcolor"]);
const RTE_CSS = new Set(["color", "background-color", "font-family", "font-size", "font-weight", "font-style", "text-decoration", "text-align", "vertical-align",
  "line-height", "letter-spacing", "margin", "margin-top", "margin-bottom", "margin-left", "margin-right", "padding", "padding-top", "padding-bottom",
  "padding-left", "padding-right", "border", "border-top", "border-bottom", "border-left", "border-right", "border-collapse", "width", "height", "max-width", "display", "white-space"]);
const RTE_DROP = new Set(["SCRIPT", "STYLE", "HEAD", "TITLE", "META", "LINK", "XML", "NOSCRIPT", "IFRAME", "OBJECT", "BASE", "TEMPLATE"]);
const RTE_FONTS = [["", "Schrift"], ["Arial, Helvetica, sans-serif", "Arial"], ["Calibri, Arial, sans-serif", "Calibri"], ["Segoe UI, Arial, sans-serif", "Segoe UI"],
  ["Verdana, Geneva, sans-serif", "Verdana"], ["Tahoma, Geneva, sans-serif", "Tahoma"], ["Georgia, serif", "Georgia"], ["Times New Roman, serif", "Times New Roman"]];
const RTE_SIZES = [["", "Größe"], ["11px", "klein"], ["13px", "etwas kleiner"], ["15px", "normal"], ["18px", "groß"], ["22px", "sehr groß"]];
const RTE_PLACEHOLDERS = ["Belegart", "Belegnummer", "Belegdatum", "Kunde", "Kundennummer", "Betrag", "Waehrung", "Dateiname", "Absender", "Datum"];

/** Enthält der Wert HTML aus dem Editor? */
const rteIsHtml = s => /<\s*\/?\s*(p|div|br|span|table|tr|td|img|b|strong|i|em|u|a|font|ul|ol|li|h[1-6]|hr|blockquote)\b/i.test(s || "");

/** Bisheriger Text (**fett**, *kursiv*, „- “-Listen, Links) → HTML, damit er im Editor richtig erscheint. */
function rteFromText(t) {
  if (!t) return "";
  if (rteIsHtml(t)) return t;
  const inline = l => esc(l).replace(/\*\*(.+?)\*\*/g, "<b>$1</b>").replace(/(^|[^*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])/g, "$1<i>$2</i>");
  return t.replace(/\r\n/g, "\n").trim().split(/\n[ \t]*\n/).map(block => {
    const lines = block.split("\n");
    if (lines.every(l => /^\s*[-•] /.test(l))) return "<ul>" + lines.map(l => `<li>${inline(l.replace(/^\s*[-•] /, ""))}</li>`).join("") + "</ul>";
    return "<p>" + lines.map(inline).join("<br>") + "</p>";
  }).join("");
}

/** Eingefügtes oder bearbeitetes HTML bereinigen (wie der Server). Liefert { html, lostImages }. */
function rteClean(html) {
  let lostImages = 0;
  html = String(html || "").replace(/<!--\[if[\s\S]*?<!\[endif\]-->/gi, "").replace(/<!--[\s\S]*?-->/g, "");
  const doc = new DOMParser().parseFromString(html, "text/html");
  const fontSize = { 1: "10px", 2: "13px", 3: "15px", 4: "18px", 5: "24px", 6: "32px", 7: "40px" };
  const walk = node => {
    for (const child of [...node.childNodes]) {
      if (child.nodeType === 8) { child.remove(); continue; }
      if (child.nodeType !== 1) continue;
      const tag = child.tagName.toUpperCase();
      if (RTE_DROP.has(tag)) { child.remove(); continue; }
      if (tag === "FONT") {
        const span = doc.createElement("span");
        if (child.getAttribute("color")) span.style.color = child.getAttribute("color");
        if (child.getAttribute("face")) span.style.fontFamily = child.getAttribute("face");
        if (fontSize[child.getAttribute("size")]) span.style.fontSize = fontSize[child.getAttribute("size")];
        span.append(...child.childNodes); child.replaceWith(span); walk(span); continue;
      }
      if (!RTE_TAGS.has(tag)) { walk(child); child.replaceWith(...child.childNodes); continue; }
      for (const a of [...child.attributes]) {
        const n = a.name.toLowerCase();
        if (!RTE_ATTRS.has(n)) { child.removeAttribute(a.name); continue; }
        if ((n === "href" || n === "src") && /^\s*(javascript|vbscript):/i.test(a.value)) child.removeAttribute(a.name);
        if (n === "href" && /^\s*data:/i.test(a.value)) child.removeAttribute(a.name);
      }
      if (child.hasAttribute("style")) {
        const keep = [];
        for (let k = 0; k < child.style.length; k++) { const prop = child.style[k];
          if (!RTE_CSS.has(prop) || /^mso-/.test(prop)) continue;
          const v = child.style.getPropertyValue(prop);
          if (/expression|url\(/i.test(v)) continue;
          keep.push(`${prop}:${v}`);
        }
        keep.length ? child.setAttribute("style", keep.join(";")) : child.removeAttribute("style");
      }
      if (tag === "IMG") {
        const src = child.getAttribute("src") || "";
        if (!/^(https:|cid:|data:image\/(png|jpe?g|gif);base64,)/i.test(src) && !src.includes("api/mailbilder/")) { lostImages++; child.remove(); continue; }
      }
      if (tag === "A" && child.hasAttribute("href")) child.setAttribute("target", "_blank");
      walk(child);
      if (tag === "SPAN" && !child.attributes.length) child.replaceWith(...child.childNodes);
    }
  };
  walk(doc.body);
  let out = doc.body.innerHTML.replace(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*[\d.]+\s*)?\)/g,
    (_, r, g, b) => "#" + [r, g, b].map(x => Math.min(255, +x).toString(16).padStart(2, "0")).join(""));
  out = out.replace(/(<p>(\s|&nbsp;|<br>)*<\/p>\s*){2,}/g, "<p>&nbsp;</p>").trim();
  return { html: out, lostImages };
}

/** Wert aus dem Editor (leer, wenn nur Leerraum). */
function rteValue(el) {
  if (el.dataset.rteSource === "1") return el.value;
  const h = el.innerHTML.trim();
  return /^(<p>(<br>|&nbsp;|\s)*<\/p>|<br>|)$/i.test(h) ? "" : h;
}

/** Ein Feld mit Werkzeugleiste. attrs = Attribute für das bearbeitbare Feld (z. B. data-path="…"). */
function rteHtml(value, attrs, opts = {}) {
  const ph = opts.placeholders || (typeof ST !== "undefined" && ST.data?.placeholders?.length ? ST.data.placeholders : RTE_PLACEHOLDERS);
  const btn = (cmd, label, title, extra = "") => `<button type="button" class="rte-btn" data-rte-cmd="${cmd}" title="${title}" ${extra}>${label}</button>`;
  return `<div class="rte${opts.small ? " small" : ""}">
    <div class="rte-bar">
      <select data-rte-cmd="fontName" title="Schriftart">${RTE_FONTS.map(([v, t]) => `<option value="${esc(v)}">${t}</option>`).join("")}</select>
      <select data-rte-cmd="fontSize" title="Schriftgröße">${RTE_SIZES.map(([v, t]) => `<option value="${v}">${t}</option>`).join("")}</select>
      <span class="rte-sep"></span>
      ${btn("bold", "<b>F</b>", "Fett (Strg+B)")}${btn("italic", "<i>K</i>", "Kursiv (Strg+I)")}${btn("underline", "<u>U</u>", "Unterstrichen (Strg+U)")}
      <label class="rte-btn rte-color" title="Schriftfarbe">A<input type="color" data-rte-cmd="foreColor" value="#1c2330"></label>
      <span class="rte-sep"></span>
      ${btn("justifyLeft", "⯇", "Links")}${btn("justifyCenter", "≡", "Mittig")}${btn("justifyRight", "⯈", "Rechts")}
      ${btn("insertUnorderedList", "•", "Aufzählung")}${btn("insertOrderedList", "1.", "Nummerierung")}${btn("insertHorizontalRule", "―", "Trennlinie")}
      <span class="rte-sep"></span>
      ${btn("link", "🔗", "Link einfügen")}
      <label class="rte-btn" title="Bild einfügen (PNG, JPG, GIF – höchstens 2 MB)">🖼<input type="file" accept="image/png,image/jpeg,image/gif" data-rte-cmd="image" hidden></label>
      ${btn("imgWidth", "↔", "Breite des gewählten Bildes")}
      <select data-rte-cmd="placeholder" title="Platzhalter einfügen"><option value="">{…}</option>${ph.map(p => `<option value="${esc(p)}">{${esc(p)}}</option>`).join("")}</select>
      <span class="rte-sep"></span>
      ${btn("removeFormat", "⌫", "Formatierung entfernen")}${btn("source", "&lt;/&gt;", "HTML anzeigen/bearbeiten")}
    </div>
    <div class="rte-area" contenteditable="true" data-rte="1" ${attrs} spellcheck="true">${rteFromText(value)}</div>
    <div class="rte-note" hidden></div>
  </div>`;
}

function rteNote(box, text) { const n = box.querySelector(".rte-note"); n.hidden = !text; n.textContent = text || ""; if (text) setTimeout(() => { n.hidden = true; }, 8000); }
function rteChanged(area) { area.dispatchEvent(new Event("input", { bubbles: true })); area.dispatchEvent(new Event("change", { bubbles: true })); }

/** Bild hochladen → URL (api/mailbilder/…). */
async function rteUpload(blob) {
  if (blob.size > 2 * 1024 * 1024) throw new Error("Das Bild ist größer als 2 MB.");
  const res = await fetch("api/mailbilder", { method: "POST", body: blob, credentials: "same-origin", headers: { "Content-Type": blob.type || "application/octet-stream" } });
  const j = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(j.error || `Hochladen fehlgeschlagen (${res.status})`);
  return j.url;
}

/** data:-Bilder im Editor hochladen und durch Verweise ersetzen. */
async function rteUploadInlineImages(area) {
  for (const img of area.querySelectorAll('img[src^="data:"]')) {
    try { const blob = await (await fetch(img.src)).blob(); img.src = await rteUpload(blob); }
    catch (e) { img.remove(); rteNote(area.closest(".rte"), "Ein Bild wurde nicht übernommen: " + e.message); }
  }
}

function rteInsertHtml(html) { document.execCommand("insertHTML", false, html); }

/** Ereignisse für alle Editoren (einmal pro Seite registrieren). */
function rteInstall(root = document) {
  if (root.__rte) return; root.__rte = true;
  // Auswahl behalten, wenn Knöpfe gedrückt werden
  root.addEventListener("mousedown", e => { if (e.target.closest("button.rte-btn")) e.preventDefault(); });

  root.addEventListener("click", async e => {
    const b = e.target.closest("button.rte-btn"); if (!b) return;
    e.preventDefault();
    const box = b.closest(".rte"), area = box.querySelector(".rte-area, textarea.rte-source");
    const cmd = b.dataset.rteCmd;
    if (cmd === "source") { rteToggleSource(box); return; }
    if (area.tagName === "TEXTAREA") return;
    area.focus();
    if (cmd === "link") {
      const url = prompt("Adresse des Links (https://…, mailto:… oder tel:…):", "https://");
      if (!url || url === "https://") return;
      const href = /^(https?:|mailto:|tel:)/i.test(url) ? url : (url.includes("@") ? "mailto:" + url : "https://" + url);
      if (document.getSelection().isCollapsed) rteInsertHtml(`<a href="${esc(href)}" target="_blank">${esc(url)}</a>`);
      else document.execCommand("createLink", false, href);
    } else if (cmd === "imgWidth") {
      const img = box.__selImg;
      if (!img || !area.contains(img)) { rteNote(box, "Erst auf ein Bild klicken, dann die Breite ändern."); return; }
      const w = prompt("Breite in Pixel (höchstens 560):", img.getAttribute("width") || img.naturalWidth || 200);
      if (w && +w > 0) { img.setAttribute("width", Math.min(560, +w)); img.removeAttribute("height"); img.style.height = "auto"; }
    } else {
      document.execCommand("styleWithCSS", false, true);
      document.execCommand(cmd, false, null);
    }
    rteChanged(area);
  });

  root.addEventListener("change", async e => {
    const el = e.target; const cmd = el.dataset?.rteCmd; if (!cmd) return;
    const box = el.closest(".rte"), area = box.querySelector(".rte-area");
    if (!area) return;
    area.focus();
    if (cmd === "image") {
      const f = el.files[0]; el.value = ""; if (!f) return;
      try { const url = await rteUpload(f); rteInsertHtml(`<img src="${esc(url)}" alt="" style="max-width:100%;height:auto">`); }
      catch (x) { rteNote(box, x.message); }
    } else if (cmd === "placeholder") {
      if (el.value) rteInsertHtml(esc(`{${el.value}}`)); el.value = "";
    } else if (cmd === "fontName") {
      if (el.value) { document.execCommand("styleWithCSS", false, true); document.execCommand("fontName", false, el.value); } el.value = "";
    } else if (cmd === "fontSize") {
      if (el.value) {
        document.execCommand("styleWithCSS", false, false);
        document.execCommand("fontSize", false, "7");
        area.querySelectorAll('font[size="7"]').forEach(f => { const s = document.createElement("span"); s.style.fontSize = el.value; s.append(...f.childNodes); f.replaceWith(s); });
      }
      el.value = "";
    } else if (cmd === "foreColor") {
      document.execCommand("styleWithCSS", false, true); document.execCommand("foreColor", false, el.value);
    }
    rteChanged(area);
  }, true);

  // Bild anklicken = auswählen (für die Breite)
  root.addEventListener("click", e => {
    const img = e.target.closest(".rte-area img"); if (!img) return;
    const box = img.closest(".rte"); box.querySelectorAll("img.sel").forEach(x => x.classList.remove("sel"));
    img.classList.add("sel"); box.__selImg = img;
  });

  // Einfügen: bereinigen, Bilder aus der Zwischenablage hochladen
  root.addEventListener("paste", async e => {
    const area = e.target.closest?.(".rte-area"); if (!area) return;
    e.preventDefault();
    const box = area.closest(".rte"), cd = e.clipboardData;
    const html = cd.getData("text/html"), text = cd.getData("text/plain");
    const files = [...cd.items].filter(i => i.kind === "file" && /^image\/(png|jpeg|gif)$/.test(i.type)).map(i => i.getAsFile());
    if (html) {
      const { html: clean, lostImages } = rteClean(html);
      rteInsertHtml(clean);
      if (lostImages) rteNote(box, `${lostImages} Bild(er) aus der Zwischenablage liegen nur lokal (z. B. Outlook) – bitte über 🖼 einfügen.`);
    } else if (files.length) {
      for (const f of files) {
        try { rteInsertHtml(`<img src="${esc(await rteUpload(f))}" alt="" style="max-width:100%;height:auto">`); }
        catch (x) { rteNote(box, x.message); }
      }
    } else if (text) rteInsertHtml(esc(text).replace(/\r?\n/g, "<br>"));
    await rteUploadInlineImages(area);
    rteChanged(area);
  });
  // Bilder per Ziehen & Ablegen
  root.addEventListener("drop", async e => {
    const area = e.target.closest?.(".rte-area"); if (!area || !e.dataTransfer?.files?.length) return;
    e.preventDefault();
    for (const f of e.dataTransfer.files) {
      if (!/^image\/(png|jpeg|gif)$/.test(f.type)) continue;
      try { area.focus(); rteInsertHtml(`<img src="${esc(await rteUpload(f))}" alt="" style="max-width:100%;height:auto">`); }
      catch (x) { rteNote(area.closest(".rte"), x.message); }
    }
    rteChanged(area);
  });
}

/** Zwischen Editor und HTML-Quelltext umschalten (Attribute wie data-path bleiben am jeweils aktiven Feld). */
function rteToggleSource(box) {
  const cur = box.querySelector(".rte-area, textarea.rte-source");
  const attrs = [...cur.attributes].filter(a => (a.name.startsWith("data-") && !a.name.startsWith("data-rte")) || a.name === "id");
  if (cur.tagName !== "TEXTAREA") {
    const ta = document.createElement("textarea");
    ta.className = "rte-source mono"; ta.rows = 10; ta.dataset.rte = "1"; ta.dataset.rteSource = "1";
    ta.value = rteValue(cur).replace(/<\/(p|div|li|tr|table|ul|ol|h\d)>/g, "</$1>\n").replace(/<br>/g, "<br>\n");
    attrs.forEach(a => ta.setAttribute(a.name, a.value));
    cur.replaceWith(ta); box.classList.add("source"); ta.focus();
  } else {
    const div = document.createElement("div");
    div.className = "rte-area"; div.contentEditable = "true"; div.dataset.rte = "1";
    attrs.forEach(a => div.setAttribute(a.name, a.value));
    div.innerHTML = rteClean(cur.value).html;
    cur.replaceWith(div); box.classList.remove("source"); rteChanged(div);
  }
}

/** Wert eines Formularfelds – Editor, Quelltext oder normales Eingabefeld. */
function fieldValue(el) {
  if (!el) return "";
  if (el.dataset?.rte === "1") return el.tagName === "TEXTAREA" ? rteClean(el.value).html : rteValue(el);
  return el.type === "checkbox" ? el.checked : el.value;
}
