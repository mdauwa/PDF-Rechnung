# Belegpost – rechtliche Hinweise und Lizenzen

Stand 17.11.2026. Diese Zusammenstellung ist eine technische Übersicht und keine Rechtsberatung. Vor dem Verkauf von Belegpost sollte jemand mit juristischem Hintergrund sie prüfen.

## 1. E-Rechnung: was rechtlich maßgeblich ist
- Bei XRechnung, ZUGFeRD und Factur-X ist die **strukturierte XML** das Original der Rechnung. Sie muss nach den gesetzlichen Vorgaben (GoBD) archiviert werden. Belegpost archiviert deshalb immer die XML, beim ZUGFeRD-PDF das PDF samt eingebetteter XML.
- Die **Rechnungsansicht** (Knopf „Rechnung anzeigen“) ist nur eine lesbare Darstellung. Sie ist keine gültige XRechnung, nicht zur Archivierung gedacht und wird nicht gespeichert. KoSIT weist in der Beschreibung der Visualisierung ausdrücklich darauf hin. Die Ansicht trägt oben einen entsprechenden Hinweis.
- Die **Prüfung** (Dokument validieren, Eingang, vor dem Senden, `POST /api/v1/pruefen`) liefert eine Empfehlung „annehmen/ablehnen“ auf Basis der offiziellen Regeln. Sie ersetzt keine fachliche oder steuerliche Prüfung der Rechnung.
  - Werden unter Einstellungen → E-Rechnung → „Prüfregeln anpassen“ Regeln heruntergestuft oder ausgeblendet, ist das eine bewusste Entscheidung des Betreibers.
  - Die offiziellen Regeln selbst bleiben dabei unverändert. Angepasste Meldungen sind im Bericht als „Stufe angepasst“ markiert.

## 2. Mitgelieferte Prüf- und Darstellungsartefakte (unverändert)
Die folgenden Artefakte stecken in `src/PdfRechnung/Edi/Validierung.zip`. Herkunft und Stand stehen in `STAND.txt`, die Lizenztexte unter `lizenzen/`.

| Bestandteil | Herkunft | Lizenz | Pflichten |
|---|---|---|---|
| EN 16931 Validierungsartefakte (Schematron als XSLT) | CEN/TC 434, github.com/ConnectingEurope/eInvoicing-EN16931 | EUPL 1.2 | Lizenztext mitliefern; Änderungen an den Artefakten wieder unter EUPL. Wir ändern nichts. |
| XRechnung-Schematron | KoSIT, github.com/itplr-kosit/xrechnung-schematron | Apache 2.0 | Lizenztext mitliefern; Änderungen kennzeichnen. Wir haben nur mit SchXslt nach XSLT übersetzt, der Inhalt ist unverändert. |
| XRechnung-Visualisierung (Rechnungsansicht) | KoSIT, github.com/itplr-kosit/xrechnung-visualization, v2026-10-13 | Apache 2.0 | Lizenztext mitliefern; unverändert eingebunden, unter `visualisierung/` |
| FileSaver.js (Teil der Visualisierung) | Eli Grey | MIT | Lizenzhinweis (`lizenzen/FileSaver-MIT.txt`) |
| SchXslt (nur zum Übersetzen beim Bauen) | David Maus | MIT | – |
| UBL 2.1 Schemas | OASIS Open | OASIS IPR Policy (frei verwendbar) | Copyright-Hinweise in den Dateien belassen |
| UN/CEFACT CII D16B Schemas | UNECE | frei verwendbar | Hinweise in den Dateien belassen |
| EDIFACT-Verzeichnisdaten | EDIFlow | MIT | `Edi/Verzeichnisse/LICENSE-EDIFlow.txt` |

Eine NOTICE-Datei gibt es bei den KoSIT-Projekten nicht. Bekommt eine spätere Version eine, muss sie mitgeliefert werden.

## 3. Bibliotheken (NuGet)
Die Lizenzen stammen aus den Paketangaben. Vor dem Verkauf bitte noch einmal gegen die jeweilige Version prüfen.

| Paket | Lizenz |
|---|---|
| SaxonCS-HE (XSLT 3) | Mozilla Public License 2.0 – unverändert genutzt; Änderungen an Saxon selbst wären offenzulegen |
| PdfPig | Apache 2.0 |
| PDFsharp | MIT |
| bblanchon.PDFium.Win32 (PDFium) | PDFium: BSD-3-Clause/Apache 2.0, Paket: MIT |
| MailKit / MimeKit | MIT |
| FluentFTP | MIT |
| SSH.NET | MIT |
| HtmlSanitizer | MIT |
| Microsoft.* / System.* (.NET) | MIT |

## 4. Eigene Umsetzung statt Übernahme
- **gotomaxx PDFMAILER**: Belegpost wertet dieselben Steuerzeichen (`@@…@@`) aus, damit vorhandene ERP-Vorlagen weiter funktionieren. Die Beschreibungstexte aus der gotomaxx-Dokumentation bzw. der Liste „Steuerzeichen eRechnung.xlsx“ werden nicht 1:1 übernommen. Alle Bezeichnungen in Belegpost sind eigene Formulierungen. Übernommen werden nur technische Fakten, also Kürzel und Feldzuordnungen. Die gotomaxx-Liste liegt nicht im Repository.
- **KoSIT-Prüfwerkzeug** (github.com/itplr-kosit/validator, Apache 2.0): Kein Code übernommen. Nur die Konzepte (Prüfszenario, eigene Stufe je Regel, Annahmeempfehlung, Antwort 200/406) sind in eigenem Code umgesetzt.
- **FeRD/ZUGFeRD**: Die FeRD-Spezifikation und das FeRD-Paket werden nicht mitgeliefert. Eigene Artefakte kann der Betreiber unter `<Datenordner>\validierung\eigene\<profil>` ablegen.
- **ERP Suite Connect**: Der Quellcode wurde nur gelesen. Der Prüfer ist aus der eigenen Schnittstellenbeschreibung und dem gelesenen Code abgeleitet.

## 5. Namen und Marken
- „XRechnung“, „ZUGFeRD“, „Factur-X“, „KoSIT“, „Peppol“ und „gotomaxx“ werden nur beschreibend verwendet, z. B. „prüft XRechnung“ oder „Herkunft: KoSIT“. Sie dürfen nicht als Gütesiegel oder Empfehlung für Belegpost dienen.
- Belegpost soll neutral bleiben und keinen Bezug zu ERP Austria oder Phoenix Data Systems im Produkt tragen. „ERP Suite“/„erpFlow“ nur dort, wo es die Schnittstelle betrifft.

## 6. Datenschutz und Sicherheit (Kurzfassung)
- Kennwörter und Geheimnisse liegen verschlüsselt im Datenordner. API-Schlüssel werden nur als Hash gespeichert.
- Die Rechnungsansicht läuft als abgeschottete Seite (Content-Security-Policy `sandbox`). Skripte darin können nicht auf das Portal oder die Anmeldung zugreifen.
- Für die Ansicht hochgeladene Dateien liegen höchstens 2 Stunden unter `<Datenordner>\temp\ansicht` und werden dann gelöscht.
