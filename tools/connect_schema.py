"""Erzeugt src/PdfRechnung/Connect/ErpSuiteConnect.json aus der XML-Beschreibung von ERP Suite Connect
(„XML Beschreibung Schnittstellenversion_V…xlsx“) und ergänzt, was der Quellcode (FWebshop, frmFWebshop.vb) zusätzlich liest.

Aufruf: python3 tools/connect_schema.py "<Pfad>/XML Beschreibung Schnittstellenversion_V5.76_12.11.2026.xlsx"
Pflicht laut Zellfarbe der Excel-Liste: rot = zwingend, hellorange = erforderlich, sonst optional.
"""
import json, re, sys, pathlib, openpyxl

src = pathlib.Path(sys.argv[1])
dst = pathlib.Path(__file__).resolve().parents[1] / "src/PdfRechnung/Connect/ErpSuiteConnect.json"
version = re.search(r"V(\d+\.\d+[a-z]?)", src.name).group(1)

# Nachrichtentypen: Blatt, Richtung, Dateinamen-Muster (Großschreibung, „enthält“)
TYPES = [
    ("ORDER", "ORDER", "eingang", ["ORDER_"], "Bestellung vom Webshop"),
    ("USERS", "USERS", "eingang", ["USERS"], "Neue Adressen vom Webshop"),
    ("PRODUCTS", "PRODUCTS", "eingang", ["PRODUCTS"], "Rückmeldung der Webshop-ID zu Artikeln"),
    ("ORDERSTATUS", "ORDERSTATUS", "ausgang", ["ORDERSTATUS"], "Bestellstatus an den Webshop"),
    ("ARTIKELSTAMM", "ARTIKELSTAMM", "ausgang", ["ARTIKELSTAMM", "ARTIKEL_"], "Artikelstamm an den Webshop"),
    ("LAGERBESTAND", "LAGERBESTAND", "ausgang", ["LAGERBESTAND"], "Lagerbestand an den Webshop"),
    ("ADRESSEN", "ADRESSEN", "ausgang", ["ADRESSEN"], "Adressen an den Webshop"),
    ("KATEGORIE", "KATEGORIE", "ausgang", ["KATEGORIE"], "Kategorien an den Webshop"),
    ("GUTSCHEINE", "GUTSCHEINE", "ausgang", ["GUTSCHEIN"], "Gutscheine an den Webshop"),
]

def level(cell):
    f = cell.fill
    if f is None or not f.fill_type: return "optional"
    c = f.fgColor
    if c.type == "rgb":
        if c.rgb.upper().endswith("FF0000"): return "zwingend"
        if c.rgb.upper().endswith("FFE598"): return "erforderlich"
    return "optional"

def tag(text):
    t = text.strip()
    m = re.match(r"<\s*([A-Za-zÄÖÜäöüß_][\wÄÖÜäöüß.\-]*)([^>]*)>(.*)$", t)
    if not m or t.startswith("</") or t.startswith("<?"): return None
    name, attrs, rest = m.group(1), m.group(2), m.group(3)
    selfclose = attrs.strip().endswith("/")
    leaf = selfclose or ("</" in rest) or (rest.strip() != "" and not rest.strip().startswith("<"))
    if name.upper() == "TEXT" and rest.strip(): leaf = True
    sample = None
    if leaf and not selfclose:
        sample = re.sub(r"<!\[CDATA\[|\]\]>", "", re.split(r"</|<", rest)[0]).strip() or None
    attr = re.findall(r'(\w+)\s*=', attrs)
    return name, leaf, sample, attr

ORDER = {"zwingend": 3, "erforderlich": 2, "optional": 1}

def merge(a, b):
    """Gleichnamige Knoten zusammenführen (Blöcke kommen in der Liste mehrfach vor)."""
    if ORDER[b["req"]] > ORDER[a["req"]]: a["req"] = b["req"]
    for k in ("type", "len", "desc", "sample"):
        if not a.get(k) and b.get(k): a[k] = b[k]
    if a.get("type") and b.get("type") and a["type"] != b["type"]:
        a["type"] = "String" if "String" in (a["type"], b["type"]) else a["type"]
    for c in b.get("children", []):
        same = next((x for x in a.setdefault("children", []) if x["name"].lower() == c["name"].lower()), None)
        if same: merge(same, c)
        else: a["children"].append(c)
    if b.get("count", 1) > 1 or b is not a: pass

wb = openpyxl.load_workbook(src)
out = {"version": version, "types": []}
for key, sheet, direction, names, title in TYPES:
    ws = wb[sheet]
    hdr = [str(c.value or "").strip() for c in ws[1]]
    ti = hdr.index("Datentyp"); di = hdr.index("Beschreibung")
    root = {"name": "#doc", "children": [], "req": "zwingend"}
    stack = [(-1, root)]
    for row in ws.iter_rows(min_row=2):
        cell = next((c for c in row[:6] if isinstance(c.value, str) and c.value.strip().startswith("<")), None)
        if cell is None: continue
        depth = cell.column - 1
        t = tag(cell.value)
        if t is None: continue
        name, leaf, sample, attrs = t
        node = {"name": name, "req": level(cell)}
        typ = row[ti].value; ln = row[ti + 1].value; desc = row[di].value
        if typ: node["type"] = str(typ).strip()
        if ln is not None:
            m = re.match(r"\s*(\d+)", str(ln)); node["len"] = int(m.group(1)) if m else None
            if " oder " in str(ln): node["len"] = max(int(x) for x in re.findall(r"\d+", str(ln)))
        if desc: node["desc"] = re.sub(r"\s+", " ", str(desc)).strip()
        if sample: node["sample"] = sample[:60]
        if attrs: node["attrs"] = attrs
        while stack and stack[-1][0] >= depth: stack.pop()
        parent = stack[-1][1]
        same = next((x for x in parent.setdefault("children", []) if x["name"].lower() == name.lower()), None)
        if same:
            if not leaf: same["repeat"] = True
            merge(same, node); node = same
        else:
            parent["children"].append(node)
        if not leaf:
            node.setdefault("children", [])
            stack.append((depth, node))
    rootnode = root["children"][0] if root["children"] else {"name": "ROOT", "children": []}
    out["types"].append({"key": key, "title": title, "direction": direction, "names": names, "sheet": sheet, "root": rootnode})

# ---- Korrekturen aus dem Quellcode: was das Programm ausdrücklich ohne den Wert verarbeitet ----
def find(node, path):
    for part in path:
        node = next((c for c in node.get("children", []) if c["name"].lower() == part.lower()), None)
        if node is None: return None
    return node
for key, path, req, why in [
    ("ORDER", ["ORDER", "ADRESS", "LIEFER", "ShopID"], "optional", "Lieferadressen ohne ShopID bekommen einen eigenen Nummernkreis"),
]:
    t = next(x for x in out["types"] if x["key"] == key)
    n = find(t["root"], path)
    if n: n["req"] = req; n["note"] = why

# ---- Ergänzungen aus dem Quellcode (frmFWebshop.vb: ReadOrder, ReadKunden, ReadArtikel, ReadCategories, ReadCHARGEN) ----
out["aliases"] = {
    # Blocknamen (der Code liest alles klein geschrieben)
    "blocks": {"product": "ARTIKEL", "listlineitem": "ARTIKEL", "address": "ADRESS", "orderingparty": "ADRESS", "invoicerecipient": "ADRESS",
               "deliveryaddress": "LIEFER", "remark": "NOTIZ", "ordercomments": "NOTIZ"},
    # Feldnamen je Block: Alias -> Feld der Beschreibung (oder "" = vom Code gelesen, nicht beschrieben)
    "ORDER": {"orderstatus": "", "rechnungsbetrag": "InvoiceTotal", "ordertotal": "InvoiceTotal", "gezahlterbetrag": "PaidAmount",
              "paymenttransactionid": "PaymentTranactionID", "zreferenz": "", "zempfaenger": "", "ustid": "", "uid": "",
              "orderdate": "Datum", "currency": "Währung", "ordercurrency": "Währung", "language": "Sprache", "ordersource": "Quelle",
              "notiz": "", "remark": "", "created": "Zeitstempel", "text": "TEXT"},
    "ADRESS": {"addressnummer": "Adressnummer", "firstname": "Vorname", "name": "", "companyname": "Firma", "firma1": "Firma",
               "adresszusatz1": "", "adresszusatz2": "", "street": "Strasse", "zip": "PLZ", "zip-code": "PLZ", "city": "Ort",
               "country": "Land", "mail": "Email", "phone": "Telefon", "telephone": "Telefon", "ustid": "UID", "name2": "Firma2", "name3": "Firma3"},
    "LIEFER": {"addressnummer": "Adressnummer", "name": "", "name1": "", "companyname": "Firma", "firma1": "Firma", "adresstyp": "",
               "firstname": "Vorname", "adresszusatz1": "", "sdresszusatz": "", "adresszusatz2": "", "street": "Strasse", "zip": "PLZ",
               "zip-code": "PLZ", "city": "Ort", "country": "Land", "mail": "Email", "phone": "Telefon", "telephone": "Telefon", "status": "",
               "name2": "Firma2", "name3": "Firma3"},
    "ARTIKEL": {"name": "Bezeichnung", "rabattbrutto": "RabattBrutto", "bruttorabatt": "RabattBrutto", "rabattwertbrutto": "RabattBrutto",
                "nettorabatt": "RabattNetto", "rabattwertnetto": "RabattNetto", "serialnumber": "Seriennummer", "loaner": "Leihgerät",
                "order": "", "text": "Text"},
    "VERSAND": {"artikelnummer": "", "typenumber": "", "info": ""},
    "RABATTE": {"gesamtbetragrabatt": "Brutto", "rabattsteuer": "Steuer", "typenumber": "", "info": ""},
    "GUTSCHEIN": {"info": "", "gutscheinnummer": "Gutscheinnummer"},
}
# Felder, die die Beschreibung anders schreibt, als der Code sie liest (werden sonst still ignoriert)
out["misspelled"] = {
    "ORDER": {"PaymentTranactionID": "PaymentTransactionID"},
    "USERS": {"Anrede": "Andrede"},          # ReadKunden liest „andrede“
    "PRODUCTS": {"Kontenzurodnung": "Kontenzuordnung"},
}
# Weitere Nachrichtentypen, die der Code verarbeitet, die Beschreibung aber nicht enthält
out["extra"] = [
    {"key": "CATEGORIES", "title": "Kategorien vom Webshop", "direction": "eingang", "names": ["CATEGORIES"], "block": "KATEGORIE",
     "fields": ["Status", "ID", "Bezeichnung", "ShopID", "Parent"]},
    {"key": "REPAIRREQUEST", "title": "Reparaturauftrag vom Webshop", "direction": "eingang", "names": ["REPAIRREQUEST"], "block": "REPAIRREQUEST",
     "fields": ["voegltin", "Belegnummer", "FormID"]},
    {"key": "PRODUCTREGISTRATION", "title": "Produktregistrierung vom Webshop", "direction": "eingang", "names": ["PRODUCTREGISTRATION"], "block": None, "fields": []},
    {"key": "CHARGEN", "title": "Chargen vom Webshop", "direction": "eingang", "names": ["CHARGEN"], "block": "CHARGE",
     "fields": ["Chargenname", "Feuchtigkeit", "AW-Wert"]},
]
dst.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
print(version, len(out["types"]), "Typen ->", dst)
for t in out["types"]:
    def count(n): return 1 + sum(count(c) for c in n.get("children", []))
    print(" ", t["key"], t["root"]["name"], [c["name"] for c in t["root"].get("children", [])], count(t["root"]))
