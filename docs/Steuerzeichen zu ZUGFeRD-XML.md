# Steuerzeichen → ZUGFeRD-XML (Feldzuordnung)

Quelle: ERP-Vorlage `Drucksteuerzeichen.json` (SEBELK = Kopf, SEBELP = Positionen, Stand 02.02.2026).

So funktioniert es:
- Das ERP druckt die Werte als gotomaxx-Steuerzeichen in den Beleg. Die Bedingungen der Vorlage entscheiden nur, *ob* ein Steuerzeichen gedruckt wird; für uns sind sie ohne Bedeutung.
- Der eRechnung-Dienst wertet die gedruckten Steuerzeichen aus und erzeugt daraus die XML.
- BT-Nummern beziehen sich auf EN 16931. ★ = Feld steht nicht in der gotomaxx-Doku zu ZUGFeRD 2 Comfort, wird vom ERP aber gedruckt.

## Format (`@@xt …@@`)

| `@@xt`-Wert | Ausgabe des Dienstes |
|---|---|
| `gotomaxx.invoice.zugferd2.comfort` | ZUGFeRD 2.3 Profil EN 16931 (PDF/A-3 + factur-x.xml) |
| `gotomaxx.invoice.zugferd2.extended` | ZUGFeRD 2.3 Profil Extended |
| `gotomaxx.invoice.zugferd.basic` / `.comfort` / `.extended` | ZUGFeRD 1.0 Basic / Comfort / Extended, wie angefordert |
| `gotomaxx.invoice.xrechnung.ubl` | XRechnung 3.0 UBL |
| `gotomaxx.invoice.xrechnung2.cii` / `xrechnung3.cii` | XRechnung 3.0 CII |
| `gotomaxx.invoice.peppol.bis3.ubl` | Peppol BIS Billing 3.0 (UBL). `@@pe1@@` = Peppol-Versand (Phase 2) |

## Kopfdaten (`@@xi FELD=Wert@@`)

| Feld | BT | Bedeutung / Hinweis |
|---|---|---|
| `DOCUMENT_TYPE` | BT-3 | 380 Rechnung, 381 Gutschrift |
| `VOUCHER_ID` | BT-1 | Rechnungsnummer |
| `VOUCHER_DATE` | BT-2 | Rechnungsdatum (TT.MM.JJJJ) |
| `DELIVERY_DATE` | BT-72 | Liefer-/Leistungsdatum (ERP: Lieferdatum, sonst Belegdatum) |
| `CURRENCY` | BT-5 | Währung (ISO 4217) |
| `PAYMENT_TERMS_DUE_DATE` | BT-9 | Fälligkeitsdatum |
| `PAYMENT_TERMS_DESCRIPTION` | BT-20 | Text der Zahlungskondition |
| `ROUTING_ID` | BT-10 | Leitweg-ID / Käuferreferenz |
| `INVOICE_NOTE` | BT-22 | Belegnotiz |
| `PAYMENT_MEANS_TYPE_CODE` | BT-81 | 58 SEPA-Überweisung, 59 SEPA-Lastschrift |
| ★ `PAYMENT_DIRECTDEBIT_MANDATE_ID` | BT-89 | Mandatsreferenz |
| ★ `PAYMENT_DIRECTDEBIT_REFERENCE_ID` | BT-90 | Gläubiger-ID |
| ★ `PAYMENT_DIRECTDEBIT_IBAN` | BT-91 | IBAN des Käufers (Lastschrift) |
| `GROSS_AMOUNT` | BT-112 | Brutto. Wird **nachgerechnet und abgeglichen**; bei Abweichung Status *Fehler* |
| `PREPAID_AMOUNT` | BT-113 | bereits bezahlt (Kasse) |
| `DUE_PAYABLE_AMOUNT` | BT-115 | offener Betrag (Kasse) |
| `SELLER_NAME` | BT-27 | Verkäufer Name |
| `SELLER_ADDRESS_LINE` | BT-35 | Straße |
| `SELLER_ADDRESS_ZIP` | BT-38 | PLZ |
| `SELLER_ADDRESS_CITY` | BT-37 | Ort |
| `SELLER_ADDRESS_COUNTRY` | BT-40 | Land (ISO, z. B. AT) |
| `SELLER_TAX_ID` | BT-31 | USt-IdNr. |
| `SELLER_TAX_REG_ID` | BT-32 | Steuernummer |
| `SELLER_IBAN` | BT-84 | IBAN |
| `SELLER_BIC` | BT-86 | BIC |
| `SELLER_CONTACT_NAME` | BT-41 | Ansprechpartner |
| `SELLER_CONTACT_PHONE` | BT-42 | Telefon |
| `SELLER_CONTACT_EMAIL` | BT-43 | E-Mail |
| ★ `SELLER_ENDPOINT_ID` / `_TYPE` | BT-34 / BT-34-1 | elektronische Adresse (ERP: E-Mail, Typ `EM`) |
| `BUYER_ID` | BT-46 | Kundennummer |
| `BUYER_NAME` | BT-44 | Käufer Name |
| `BUYER_ADDRESS_LINE` | BT-50 | Straße oder Postfach |
| `BUYER_ADDRESS_ZIP` | BT-53 | PLZ |
| `BUYER_ADDRESS_CITY` | BT-52 | Ort |
| `BUYER_ADDRESS_COUNTRY` | BT-55 | Land |
| `BUYER_CONTACT_EMAIL` | BT-58 | E-Mail (Kasse) |
| ★ `BUYER_ENDPOINT_ID` / `_TYPE` | BT-49 / BT-49-1 | elektronische Adresse / Peppol-ID |
| `SHIPPING_ADDRESS_NAME` | BT-70 | Lieferadresse Name |
| `SHIPPING_ADDRESS_LINE` | BT-75 | Straße |
| `SHIPPING_ADDRESS_ZIP` | BT-78 | PLZ |
| `SHIPPING_ADDRESS_CITY` | BT-77 | Ort |
| `SHIPPING_ADDRESS_COUNTRY` | BT-80 | Land |

## Tabellen (`@@xr table=NAME|FELD=Wert|…@@`, eine Zeile je Eintrag)

### ALLOWANCE_CHARGE – Zu-/Abschläge auf Belegebene (ERP: Nebenkosten 1–5)

| Feld | BT (Zuschlag / Abschlag) | Hinweis |
|---|---|---|
| `ALLOWANCE_CHARGE_TYPE` | ChargeIndicator | `CHARGE` = Zuschlag, `ALLOWANCE` = Abschlag |
| `ALLOWANCE_CHARGE_AMOUNT` | BT-99 / BT-92 | Betrag |
| `ALLOWANCE_CHARGE_REASON` | BT-104 / BT-97 | Grund |
| `ALLOWANCE_CHARGE_VAT_CATEGORY` | BT-102 / BT-95 | S oder Z |
| `ALLOWANCE_CHARGE_VAT_PERCENT` | BT-103 / BT-96 | Steuersatz |

### PAYMENT_TERMS_TABLE – Skonto ★ (Spalte DATE)

| Feld | Hinweis |
|---|---|
| `PAYMENT_TERMS_TABLE_DAYS` | Skonto-Tage |
| `PAYMENT_TERMS_TABLE_PERCENT` | Skonto-Prozent |
| `PAYMENT_TERMS_TABLE_BASEAMOUNT` | Basisbetrag |
| ★ `PAYMENT_TERMS_TABLE_DATE` | Skonto-Datum |

Ausgabe je Format:
- **XRechnung**: als Text in BT-20 (`#SKONTO#TAGE=14#PROZENT=2.00#BASISBETRAG=…#`)
- **ZUGFeRD Extended**: strukturiert (ApplicableTradePaymentDiscountTerms)
- **EN 16931**: nur als Text

### ITEM_DETAILS – Positionen (SEBELP)

| Feld | BT | Hinweis |
|---|---|---|
| `ITEM_LINE_ID` | BT-126 | Positionsnummer |
| `ITEM_SELLER_ID` | BT-155 | Artikelnummer |
| `ITEM_NAME` | BT-153 | Bezeichnung |
| ★ `ITEM_DESCRIPTION` | BT-154 | Positionsnotiz |
| `ITEM_QUANTITY` | BT-129 | Menge (ERP druckt sie bei negativem Preis negativ) |
| `ITEM_UNIT` | BT-130 | **ERP liefert den Einheitentext** → Zuordnung zu UN/ECE-Code über die Einstellung „Einheiten“ (z. B. Stk → H87, h → HUR, kg → KGM) |
| `ITEM_PRICE_BASE_QUANTITY` | BT-149 | Preiseinheit |
| `ITEM_GROSS_PRICE` | BT-146 | Einzelpreis netto (immer ≥ 0) |
| `ITEM_PRICE_DISCOUNT` | BT-147 | im ERP abgeschaltet (Rundungsprobleme) |
| ★ `ITEM_ALLOWANCE_AMOUNT` | BT-136 | Positionsrabatt als Abschlag |
| ★ `ITEM_ALLOWANCE_REASON` | BT-139 | Rabattgrund |
| `ITEM_VAT_CATEGORY` | BT-151 | S oder Z |
| `ITEM_VAT_PERCENT` | BT-152 | Steuersatz |
| `ITEM_TOTAL_AMOUNT` | BT-131 | Positionsbetrag netto |

## Vom Dienst berechnet (nicht gedruckt)

- **Summen**: BT-106 Positionen, BT-107/108 Ab-/Zuschläge, BT-109 Netto, BT-110 Steuer, BT-112 Brutto, BT-115 Zahlbetrag.
- **Steueraufschlüsselung** je Kategorie und Satz: BT-116 bis BT-119.
- **Befreiungsgrund**: Bei Kategorie Z/E/AE/K ist BT-120 Pflicht. Der Standardtext kommt aus den Einstellungen je Kategorie.

## Prüfungen

- **Summenkontrolle**:
  - Summe der Positionen (BT-131) − Abschläge + Zuschläge = Netto
  - Netto + Steuer = gedrucktes `GROSS_AMOUNT` (Toleranz 0,01)
- **Pflichtfelder** je Profil.
- **Gutschrift (381)**: Vorzeichen der Beträge prüfen. EN 16931 erwartet positive Beträge.
- **Unbekannte Felder**: werden als Warnung ins Prüfprotokoll übernommen, nicht verworfen.
