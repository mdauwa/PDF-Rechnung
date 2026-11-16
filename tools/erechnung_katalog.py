"""Erzeugt src/PdfRechnung/Documents/ERechnungFelder.json aus der gotomaxx-Liste „Steuerzeichen eRechnung.xlsx“.

Aufruf: python3 tools/erechnung_katalog.py "<Pfad>/Steuerzeichen eRechnung.xlsx"

Übernommen werden nur technische Fakten: Parametername, Tabelle, BT-Nummer (EN 16931 bzw. BT-X = ZUGFeRD Extended),
Werttyp, Codeliste, in welchen Formaten verfügbar/Pflicht, Vorbelegung, ab Version.
Beschreibungen, Bemerkungen und Beispiele der Liste werden NICHT übernommen; die Bezeichnungen hier sind eigene Formulierungen.
Die Excel-Datei selbst liegt nicht im Repository.
"""
import json, re, sys, pathlib, openpyxl

root = pathlib.Path(__file__).resolve().parents[1]
src = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "Steuerzeichen eRechnung.xlsx")
dst = root / "src/PdfRechnung/Documents/ERechnungFelder.json"

# ---------- eigene Bezeichnungen ----------
PARTY = [  # längster Präfix zuerst
    ("ULTIMATE_SHIPPING_ADDRESS_", "Endempfänger"), ("SELLER_TAXREPRESENTATIVE_", "Steuervertreter"),
    ("SELLER_OTHER_BANKS_", "Weiteres Konto"), ("SHIPPING_ADDRESS_", "Lieferanschrift"), ("INVOICEE_ADDRESS_", "Rechnungsempfänger"),
    ("SELLER_", "Verkäufer"), ("BUYER_", "Käufer"), ("PAYEE_", "Zahlungsempfänger"),
]
PART = {
    "NAME": "Name", "TRADING_NAME": "Handelsname", "LEGAL_REGISTRATION_ID": "Registernummer", "ADDRESS_LINE": "Straße",
    "ADDRESS_LINE2": "Adresszusatz", "ADDRESS_LINE3": "Adresszeile 3", "LINE": "Straße", "LINE2": "Adresszusatz", "LINE3": "Adresszeile 3",
    "ADDRESS_ZIP": "PLZ", "ZIP": "PLZ", "ADDRESS_CITY": "Ort", "CITY": "Ort", "ADDRESS_COUNTRY": "Land", "COUNTRY": "Land",
    "ADDRESS_COUNTRY_SUBDIVISION": "Bundesland/Region", "GLN": "GLN", "ID": "Kennung/Kundennummer", "TAX_ID": "USt-IdNr.",
    "TAX_REG_ID": "Steuernummer", "TAX_REGISTRATION_ID": "Steuernummer", "ENDPOINT_ID": "elektronische Adresse",
    "ENDPOINT_ID_TYPE": "Art der elektronischen Adresse", "CONTACT_NAME": "Ansprechpartner", "CONTACT_PHONE": "Telefon",
    "CONTACT_EMAIL": "E-Mail", "PAYMENT_ACCOUNT_NAME": "Kontoinhaber", "ACCOUNT_NAME": "Kontoinhaber", "IBAN": "IBAN", "BIC": "BIC",
    "ADDITIONAL_LEGAL_INFO": "rechtliche Zusatzangaben", "REGULATORY_INFORMATIONS": "Pflichtangaben (Geschäftsführung, Register)",
    "ACCOUNTING_REFERENCE": "Buchungsreferenz", "ORDER_ID": "Bestellnummer", "ORDER_DATE": "Bestelldatum",
}
OWN = {
    "SELLER_ID": "Verkäufer – Kennung", "BUYER_ID": "Käufer – Kundennummer",
    "DOCUMENT_TYPE": "Belegart (Code)", "INVOICE_NOTE": "Belegnotiz", "VOUCHER_ID": "Rechnungsnummer", "VOUCHER_DATE": "Rechnungsdatum",
    "INVOICING_PERIOD_START_DATE": "Leistungszeitraum von", "INVOICING_PERIOD_END_DATE": "Leistungszeitraum bis",
    "DELIVERY_DATE": "Liefer-/Leistungsdatum", "CURRENCY": "Währung", "CURRENCY_VAT": "Währung der Steuer",
    "VAT_EXEPT_REASON_TEXT": "Grund der Steuerbefreiung", "DELIVERY_TYPE_CODE": "Lieferbedingung (Incoterms)",
    "VAT_POINT_DATE": "Steuerdatum", "VAT_POINT_DATE_CODE": "Steuerdatum (Code)",
    "INVOICE_NOTES_CONTENT_CODE": "Notiz – Code", "INVOICE_NOTES_CONTENT": "Notiz – Text", "INVOICE_NOTES_SUBJECT_CODE": "Notiz – Betreff (Code)",
    "ROUTING_ID": "Leitweg-ID / Käuferreferenz", "CONTRACT_REFERENCE_ID": "Vertragsnummer", "PROJECT_REFERENCE_ID": "Projektnummer",
    "PROJECT_REFERENCE_NAME": "Projektname", "SALES_ORDER_REFERENCE_ID": "Auftragsnummer", "SALES_ORDER_REFERENCE_DATE": "Auftragsdatum",
    "DESPATCH_ADVICE_REFERENCE_ID": "Versandanzeige", "TENDER_LOT_ID": "Vergabe-/Losnummer", "INVOICED_OBJECT_ID": "Objektkennung",
    "INVOICED_OBJECT_ID_SCHEME_ID": "Objektkennung – Schema", "PRECEDING_INVOICE_REFERENCE_ID": "Bezug auf Rechnung",
    "PRECEDING_INVOICE_REFERENCE_DATE": "Bezug auf Rechnung – Datum", "DELIVERY_NOTE_REFEERENCE_ID": "Lieferschein",
    "DELIVERY_NOTE_REFEERENCE_DATE": "Lieferschein – Datum", "OTHER_PRECEDING_INVOICE_REFERENCE_ID": "weitere Bezugsrechnung",
    "OTHER_PRECEDING_INVOICE_REFERENCE_DATE": "weitere Bezugsrechnung – Datum",
    "LINE_TOTAL_AMOUNT": "Summe Positionen", "NET_AMOUNT": "Nettobetrag", "TAX_AMOUNT": "Steuerbetrag",
    "TAX_AMOUNT_ACCOUNTING_CURRENCY": "Steuerbetrag in Buchungswährung", "GROSS_AMOUNT": "Bruttobetrag", "CHARGE_AMOUNT": "Summe Zuschläge",
    "ALLOWANCE_AMOUNT": "Summe Abschläge", "PREPAID_AMOUNT": "bereits bezahlt", "DUE_PAYABLE_AMOUNT": "offener Betrag",
    "PAYMENT_REFERENCE": "Verwendungszweck", "PAYMENT_TERMS_DUE_DATE": "Fälligkeitsdatum", "PAYMENT_TERMS_DESCRIPTION": "Zahlungsbedingung",
    "PAYMENT_MEANS_TYPE_CODE": "Zahlungsart (Code)", "PAYMENT_MEANS_TYPE_TEXT": "Zahlungsart (Text)",
    "PAYMENT_DIRECTDEBIT_MANDATE_ID": "Lastschrift – Mandatsreferenz", "PAYMENT_DIRECTDEBIT_REFERENCE_ID": "Lastschrift – Gläubiger-ID",
    "PAYMENT_DIRECTDEBIT_IBAN": "Lastschrift – IBAN des Käufers",
    "PAYMENT_TERMS_TABLE_DAYS": "Skonto – Tage", "PAYMENT_TERMS_TABLE_DATE": "Skonto – bis Datum", "PAYMENT_TERMS_TABLE_PERCENT": "Skonto – Prozent",
    "PAYMENT_TERMS_TABLE_BASEAMOUNT": "Skonto – Basisbetrag", "PAYMENT_TERMS_TABLE_AMOUNT": "Skonto – Betrag",
    "ALLOWANCE_CHARGE_TYPE": "Zu-/Abschlag – Art", "ALLOWANCE_CHARGE_AMOUNT": "Zu-/Abschlag – Betrag", "ALLOWANCE_CHARGE_REASON": "Zu-/Abschlag – Grund",
    "ALLOWANCE_CHARGE_REASON_CODE": "Zu-/Abschlag – Grund (Code)", "ALLOWANCE_CHARGE_CALCULATION_PERCENT": "Zu-/Abschlag – Prozent",
    "ALLOWANCE_CHARGE_BASIS_AMOUNT": "Zu-/Abschlag – Basisbetrag", "ALLOWANCE_CHARGE_VAT_PERCENT": "Zu-/Abschlag – Steuersatz",
    "ALLOWANCE_CHARGE_VAT_CATEGORY": "Zu-/Abschlag – Steuerkategorie",
    "BASIS_AMOUNT": "Steuer – Bemessungsgrundlage", "VAT_LINE_TOTAL_BASIS_AMOUNT": "Steuer – Summe Positionen", "VAT_AMOUNT": "Steuer – Betrag",
    "VAT_PERCENT": "Steuer – Satz", "VAT_CATEGORY": "Steuer – Kategorie",
    "ATTACH_ID": "Anlage – Kennung", "ATTACH_NAME": "Anlage – Bezeichnung", "ATTACH_FILE": "Anlage – Datei", "ATTACH_URI": "Anlage – Adresse (URL)",
    "ATTACH_TYPE": "Anlage – Art (Code)", "ATTACH_REFERENCETYPECODE": "Anlage – Referenzart",
    "ITEM_LINE_ID": "Positionsnummer", "ITEM_SELLER_ID": "Artikelnummer", "ITEM_BUYER_ID": "Artikelnummer des Käufers",
    "ITEM_STANDARD_IDENTIFIER": "Artikelkennung (z. B. GTIN)", "ITEM_STANDARD_IDENTIFIER_SCHEME_ID": "Artikelkennung – Schema",
    "ITEM_OBJECT_IDENTIFIER": "Objektkennung der Position", "ITEM_OBJECT_IDENTIFIER_SCHEME_ID": "Objektkennung der Position – Schema",
    "ITEM_ACCOUNTING_ID": "Kostenstelle/Konto", "ITEM_ACCOUNTING_CODE": "Kontierung (Code)", "ITEM_NAME": "Bezeichnung",
    "ITEM_DESCRIPTION": "Beschreibung", "ITEM_QUANTITY": "Menge", "ITEM_PRICE_BASE_QUANTITY": "Preiseinheit", "ITEM_UNIT": "Mengeneinheit",
    "ITEM_TOTAL_AMOUNT": "Positionsbetrag", "ITEM_VAT_PERCENT": "Steuersatz", "ITEM_VAT_CATEGORY": "Steuerkategorie",
    "ITEM_VAT_EXEPT_REASON_TEXT": "Grund der Steuerbefreiung", "ITEM_GROSS_PRICE": "Bruttopreis", "ITEM_NET_PRICE": "Nettopreis",
    "ITEM_PRICE_DISCOUNT": "Preisnachlass", "ITEM_NOTE": "Positionsnotiz", "ITEM_DELIVERY_DATE": "Lieferdatum",
    "ITEM_PERIOD_START_DATE": "Zeitraum von", "ITEM_PERIOD_END_DATE": "Zeitraum bis",
    "ITEM_ALLOWANCE_AMOUNT": "Abschlag – Betrag", "ITEM_ALLOWANCE_REASON": "Abschlag – Grund", "ITEM_ALLOWANCE_REASON_CODE": "Abschlag – Grund (Code)",
    "ITEM_ALLOWANCE_BASIS_AMOUNT": "Abschlag – Basisbetrag", "ITEM_ALLOWANCE_CALCULATION_PERCENT": "Abschlag – Prozent",
    "ITEM_CHARGE_AMOUNT": "Zuschlag – Betrag", "ITEM_CHARGE_REASON": "Zuschlag – Grund", "ITEM_CHARGE_REASON_CODE": "Zuschlag – Grund (Code)",
    "ITEM_CHARGE_BASIS_AMOUNT": "Zuschlag – Basisbetrag", "ITEM_CHARGE_CALCULATION_PERCENT": "Zuschlag – Prozent",
    "ITEM_REFERENCED_PURCHASE_ORDER_ID": "Bestellnummer", "ITEM_REFERENCED_PURCHASE_ORDER_LINE_ID": "Bestellposition",
    "ITEM_REFERENCED_PURCHASE_ORDER_DATE": "Bestelldatum", "ITEM_REFERENCED_DELIVERY_NOTE_ID": "Lieferschein",
    "ITEM_REFERENCED_DELIVERY_NOTE_LINE_ID": "Lieferscheinposition", "ITEM_REFERENCED_DELIVERY_NOTE_DATE": "Lieferscheindatum",
    "ITEM_ORIGIN": "Ursprungsland", "ITEM_ATTR_TYPE": "Merkmal – Art (Code)", "ITEM_ATTR_DESCRIPTION": "Merkmal – Name", "ITEM_ATTR_VALUE": "Merkmal – Wert",
    "ITEM_CLASSIFICATION_CODE": "Klassifizierung", "ITEM_CLASSIFICATION_SCHEME_ID": "Klassifizierung – Schema",
    "ITEM_CLASSIFICATION_SCHEME_VERSION_ID": "Klassifizierung – Version",
}
GROUP = {"SELLER_OTHER_BANKS_DETAILS": "Weitere Bankkonten", "INVOICE_NOTES": "Notizen", "OTHER_PRECEDING_INVOICES": "Bezugsrechnungen",
         "PAYMENT_TERMS_TABLE": "Skonto", "ALLOWANCE_CHARGE": "Zu- und Abschläge", "VAT": "Steueraufschlüsselung", "ATTACH": "Anlagen",
         "ITEM_DETAILS": "Positionen", "ITEM_ATTR": "Positionsmerkmale", "ITEM_CLASSIFICATION": "Positionsklassifizierung",
         "ITEM_CHARGE": "Positionszuschläge", "ITEM_ALLOWANCE": "Positionsabschläge"}

def label(name):
    if name in OWN: return OWN[name]
    for pre, party in PARTY:
        if name.startswith(pre):
            rest = name[len(pre):]
            if rest in PART: return f"{party} – {PART[rest]}"
    raise SystemExit(f"Keine eigene Bezeichnung für {name} – bitte in tools/erechnung_katalog.py ergänzen.")

def group(name, table):
    if table: return GROUP.get(table, table)
    for pre, party in PARTY:
        if name.startswith(pre): return party
    if name.endswith("_AMOUNT") or name.endswith("_ACCOUNTING_CURRENCY"): return "Summen"
    if name.startswith("PAYMENT_"): return "Zahlung"
    if "REFERENCE" in name or "REFEERENCE" in name or name in ("ROUTING_ID", "TENDER_LOT_ID") or name.startswith("INVOICED_OBJECT"): return "Bezüge"
    return "Beleg"

def formats(text):
    t = (text or "").lower()
    if t.strip() in ("", "-", "*") or t.startswith("- "): return []
    out = []
    if "extended" in t: out.append("extended")
    if "comfort" in t: out.append("comfort")
    if "xrechnung (cii)" in t: out.append("xrechnung-cii")
    elif "xrechnung" in t: out.append("xrechnung")
    if "peppol" in t: out.append("peppol")
    return out

def kind(fmt):
    f = (fmt or "").lower()
    return "betrag" if f == "betrag" else "datum" if f.startswith("datum") else "prozent" if f.startswith("prozent") else \
        "menge" if f == "menge" else "code" if "code" in f else "text"

ws = openpyxl.load_workbook(src).worksheets[0]
rows = list(ws.iter_rows(values_only=True))[1:]
table, out = None, []
for r in rows:
    name = (r[4] or "").strip()
    if not name: continue
    code = (r[5] or "")
    m = re.search(r"table=([A-Z_]+)", code)
    if m: table = m.group(1)
    elif code.strip().startswith("@@xi"): table = None
    note = (r[10] or "")
    d = re.search(r"(?:[Vv]or|[Vv]er)belegt mit ([A-Z0-9]+)", note)
    mand = (r[8] or "").strip()
    bt = (r[2] or "").replace(" ", "")
    out.append({
        "name": name, "table": table, "group": group(name, table), "bt": bt if bt not in ("", "-") else None, "text": label(name),
        "type": kind(r[3]), "formats": formats(r[7]), "required": formats(mand),
        "conditional": mand.startswith("*") or "siehe" in mand, "ublOnly": "nur in ubl" in note.lower(),
        "codelist": (r[9] or "").strip() or None, "default": d.group(1) if d else None, "since": str(r[11] or "").strip() or None,
    })
# Felder aus der Vorlage "@@xr table=…|FELD=|…@@" ergänzen (z. B. ITEM_LINE_ID in ITEM_CLASSIFICATION)
for r in rows:
    code = r[5] or ""
    m = re.search(r"table=([A-Z_]+)", code)
    if not m: continue
    for f in re.findall(r"\|([A-Z0-9_]+)=", code):
        if not any(x["name"] == f and x["table"] == m.group(1) for x in out):
            base = next((x for x in out if x["name"] == f), None)
            if base: out.append(dict(base, table=m.group(1), group=group(f, m.group(1))))
# doppelte (Skonto XRechnung/ZUGFeRD) zusammenführen
merged = {}
for x in out:
    k = (x["table"], x["name"])
    if k in merged:
        y = merged[k]
        y["formats"] = sorted(set(y["formats"]) | set(x["formats"]))
        y["required"] = sorted(set(y["required"]) & set(x["required"]))
    else: merged[k] = x
out = list(merged.values())
dst.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
print(len(out), "Felder ->", dst)
