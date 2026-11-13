# eRechnung-Drucker – Konzept Phase 1 (Druckdienst)

Stand: 13.11.2026 · Phase 2 (Versand-Portal) ist am Ende skizziert.

## Ziel

Ein eigener virtueller Drucker als Ersatz für gotomaxx PDFMAILER, ohne Lizenzkosten pro Arbeitsplatz.

- Das ERP druckt Belege wie bisher mit gotomaxx-Steuerzeichen (`@@…@@`). Die Vorlage dafür ist im ERP hinterlegt (SEBELK/SEBELP, siehe *Steuerzeichen-Referenz*).
- Der Dienst am Server erzeugt aus dem Druck:
  - das PDF mit Briefpapier,
  - die E-Rechnung (ZUGFeRD/Factur-X, XRechnung),
  - einen Versandauftrag mit Empfänger, Betreff, Text und Anlagen.
- Bestehende Druckformulare müssen dafür nicht geändert werden: Das ERP liefert die Rechnungsdaten bereits vollständig über `@@xt`/`@@xi`/`@@xr`.

## Ablauf

1. Der Benutzer druckt auf den freigegebenen Drucker `\\SERVER\eRechnung`.
2. Der Drucker verwendet den in Windows enthaltenen Treiber „Microsoft Print to PDF“, aber **nicht** mit dessen Standardanschluss `PORTPROMPT:`. Nur dieser Anschluss öffnet den Speichern-Dialog. Der Dienst legt einen eigenen lokalen Anschluss an, und der Treiber schreibt das PDF ohne Nachfrage dorthin:
   - **Bevorzugt:** Named Pipe `\\.\pipe\eRechnung`, auf der der Dienst lauscht. Jeder Druckauftrag kommt einzeln an, und es entsteht keine Zwischendatei. Im Prototyp prüfen.
   - **Ausweichlösung:** der kostenlose Port-Monitor *mfilemon* (GPL, unverändert mitgeliefert). Er schreibt jeden Auftrag als eigene Datei mit eindeutigem Namen in einen Eingangsordner, den der Dienst überwacht.
   - **Nicht geeignet** ist ein lokaler Anschluss mit festem Dateinamen: Jeder neue Auftrag überschreibt die Datei, das gibt Probleme bei mehreren Druckaufträgen hintereinander.

   Benutzer, Rechner und Dokumentname holt sich der Dienst aus der Druckwarteschlange.
3. **Steuerzeichen auslesen**: Text mit Positionen aus dem PDF lesen (PdfPig) und alle `@@XX…@@` sammeln, getrennt je Seite und Dokument (`@@nd@@` trennt Dokumente).
4. **Steuerzeichen entfernen**: Die Textbefehle werden aus dem Seiteninhalt gelöscht, nicht nur überdeckt.
5. **Briefpapier** (`@@bp…@@` oder Standard) unterlegen, mit eigener Vorlage für die erste Seite und die Folgeseiten. Danach Anhänge (`@@af`), Grafiken und QR-Codes (`@@ip`) und Metadaten (`@@di`) einfügen.
6. **E-Rechnung bauen**: `@@xt` bestimmt das Format, `@@xi` liefert die Felder, `@@xr` die Tabellen (Positionen, Zuschläge, Skonto, Steuern). Summen und Steuern werden nachgerechnet, wenn sie fehlen.
7. **Ausgabe**:
   - ZUGFeRD: PDF/A-3 mit eingebetteter `factur-x.xml`
   - XRechnung/Peppol: XML-Datei, das PDF als Sichtkopie
8. **Prüfen**:
   - Pflichtfelder nach EN 16931
   - Summen: Positionen gegen Netto, Netto plus Steuer gegen Brutto, Abgleich mit `GROSS_AMOUNT`
   - optional der KoSIT-Validator
   - Ergebnis: Status *ok*, *Warnung* oder *Fehler*
9. **Ablage** als Vorgang (PDF, XML, Steuerzeichen-Rohdaten, Prüfprotokoll, Versanddaten) in `C:\ProgramData\eRechnung\vorgaenge\JJJJ\MM\<ID>\` plus Index in SQLite. Auf diesen Vorgängen setzt Phase 2 auf.

Phase 1 versendet noch nicht. Optional: Bei Status *ok* und eingeschaltetem „Autoversand“ wird per SMTP gesendet. Ob das schon in Phase 1 kommt oder erst mit dem Portal, ist zu entscheiden.

## Druckertreiber – Entscheidung

| Variante | Lizenz | Bewertung |
|---|---|---|
| **A: Microsoft Print to PDF + Named-Pipe-Anschluss** (empfohlen) | in Windows enthalten, Verarbeitung mit MIT/Apache-Bibliotheken | Kein eigener Treibercode, kein Ghostscript, keine AGPL. Am Server und an den Clients ist nichts zu installieren. **Mit Prototyp prüfen**: Freigabe als Netzwerkdrucker und Textauslesbarkeit der Steuerzeichen. |
| B: PostScript-Treiber + Ghostscript (oder clawPDF/PDFCreator) | AGPL | Bewährt. Bei Weitergabe an Kunden müsste der Quellcode offengelegt werden. Fallback, falls A scheitert. |
| C: eigener Treiber / Port-Monitor (C++) | eigen | Hoher Aufwand und Pflege (Signierung, Windows-Updates). Nicht empfohlen. |

## Technik (an *Firmenportal* angelehnt)

- **.NET 9**, eine selbstständige `eRechnung.exe` (inkl. Laufzeit), Kestrel.
- Doppelklick startet den Einrichtungsmodus unter `https://localhost:5443/#/einstellungen`. Die Installation als Windows-Dienst (`sc.exe`, Neustart bei Fehlern, Firewall) läuft per Knopfdruck. Update durch Doppelklick auf die neue EXE.
- **HTTPS**: eigenes PFX oder automatisch selbst signiert (5 Jahre, Rechnername/FQDN/IP, `.cer` zum Verteilen per GPO).
- **Einstellungen** in `C:\ProgramData\eRechnung\settings.json`, Passwörter verschlüsselt (Data Protection + DPAPI).
- **Anmeldung** wie im Firmenportal: lokales Administrator-Kennwort am Server und Microsoft Entra ID. Benutzer, Rollen und Administratoren werden übernommen.
- Übernommen werden die Bausteine `SettingsStore`, `CertificateProvider`, `ServiceManager`, `CommandLine`, `RuntimeControl`, `UserStore`, `AuthApi` und `PortalAccess`, ohne TANSS/timr.
- Bibliotheken:

  | Zweck | Bibliothek |
  |---|---|
  | Text und Positionen lesen | PdfPig |
  | Seiten bearbeiten, Briefpapier, PDF/A-3, Anhänge | PDFsharp |
  | XML für ZUGFeRD, XRechnung UBL/CII | ZUGFeRD-csharp (s2industries) |
  | Datenbank | SQLite |

  Die Lizenzen werden vor dem Einsatz geprüft.

## Einstellungen (Oberfläche, Phase 1)

| Reiter | Inhalt |
|---|---|
| Server & Zertifikat | Ports, HTTP→HTTPS-Umleitung, PFX |
| Drucker | Drucker anlegen/reparieren (Name, Freigabe, Anschluss), Testdruck, Steuerzeichen-Erkennung (Begrenzer `@@`, Mindestschriftgröße) |
| Briefpapier | Briefpapiere verwalten (Name wie in `@@bp`, Groß/Klein beachten): PDF für erste Seite + Folgeseiten, Standard-Briefpapier, „nicht bei angehängten PDFs“ |
| E-Rechnung | Formatzuordnung je `@@xt`-Wert, Standardwerte für fehlende Verkäuferfelder, Einheiten-Mapping (z. B. Stk → H87/C62), Rundung, Validierung an/aus |
| Mail | SMTP oder Microsoft 365 (Graph), Absender, Standardbetreff und -text mit Variablen, Autoversand an/aus |
| Ablage | Aufbewahrungsdauer, optional zusätzlicher Exportordner (z. B. Archiv/DMS) |
| Benutzer & Rollen | wie Firmenportal |
| Dienst | installieren, Status, Protokoll |

## Steuerzeichen – Umfang Phase 1

Syntax wie gotomaxx: `@@` + 2 Zeichen Befehl + Wert + `@@`, Courier ≥ 4 pt, sichtbar, ohne Zeilenumbruch.

| Gruppe | Befehle |
|---|---|
| E-Rechnung | `xt` (Format), `xi FELD=` (Felder), `xr table=…\|…` (Tabellen), `pe` (Peppol-Versand, erst Phase 2) |
| Mail | `ml`, `to`, `cc`, `bc`, `ed`, `sa`, `mb`, `ma`, `mc`, `md` (`mo`/`ms`/`mp` werden ignoriert, weil der Server sendet) |
| Briefpapier/Bearbeiten | `bp`, `af` (inkl. `\| no_stationery`), `ef`, `ip` (Grafik/QR), `rpc`, `di` |
| Allgemein | `fn`, `ca` (Erweiterung: `ca3` = PDF/A-3), `nd`, `cf`, `jc`/`je` (Sammeln), `od` (ignoriert) |
| Eigene Erweiterungen | `@@xi ERP_VOUCHER_INDEX=…@@` (Belegindex für eine spätere ERP-Abfrage), weitere nur mit eigenem Präfix |

Unbekannte Befehle oder Felder werden protokolliert und im Prüfprotokoll als Warnung angezeigt, aber nicht verworfen.

### Formate (`@@xt`), wie sie das ERP je Kunde (ADR_6434_2) setzt

| ERP-Wert | `@@xt` | Ausgabe |
|---|---|---|
| 00 | zugferd2.comfort | ZUGFeRD 2.3 EN 16931 (PDF/A-3) |
| 01 | zugferd2.extended | ZUGFeRD 2.3 Extended |
| 02–04 | zugferd.basic/comfort/extended (V1) | ZUGFeRD 1.0 Basic/Comfort/Extended, **wie angefordert** |
| 05–07 | xrechnung.ubl / xrechnung2.cii / xrechnung3.cii | XRechnung 3.0 (UBL bzw. CII) |
| 08 | peppol.bis3.ubl (+ `pe1`) | Peppol BIS Billing 3.0 (UBL). Versand über einen Access Point erst in Phase 2 |

**Grundsatz:** Jedes Format wird so geliefert, wie es angefordert ist. Kann der Dienst ein angefordertes Format nicht erzeugen, bekommt der Vorgang den Status *Fehler* mit einer Meldung im Portal. Er wird dann nicht versendet. Bis das Portal steht, erscheint die Meldung im Protokoll.

## Entscheidungen

| Datum | Entscheidung |
|---|---|
| 13.11.2026 | E-Rechnungsformat wie angefordert. Nicht unterstützte Formate erzeugen eine Meldung im Portal. |
| 13.11.2026 | Quellcode unter `Y:\Dev\eRechnung` |
| 13.11.2026 | Kein Speichern-Dialog. Der Drucker bekommt einen eigenen Anschluss (Named Pipe oder mfilemon) statt `PORTPROMPT:`. |
| 13.11.2026 | Der Drucker verwendet den Treiber **Microsoft Print to PDF**. |
| 13.11.2026 | Produktname **PDF Rechnung**, Repository `mdauwa/PDF-Rechnung`. Das Portal wird zuerst gebaut, der Druckdienst folgt. |

## Offene Punkte / Prototyp zuerst

1. **Beispielbeleg** (wird nachgeliefert): eine Rechnung aus dem ERP mit sichtbaren Steuerzeichen. Damit wird geprüft:
   - wie die `<<|`-Zeilen gedruckt werden (vermutlich eine lange `@@xr`-Zeile je Position),
   - ob alle Zeichen sauber auslesbar sind.
2. **Prototyp Variante A**: Named-Pipe-Anschluss, Freigabe, Clientdruck von Windows 10/11, Steuerzeichen lesen und entfernen.
3. Autoversand schon in Phase 1 oder erst mit dem Portal.
4. Validierung mit dem KoSIT-Validator (braucht Java am Server) oder nur eigene Prüfungen.

## Phase 2 – Versand-Portal (Skizze)

- **Liste der Vorgänge**: Filter nach Status, Datum, Kunde, Belegnummer, Drucker-Benutzer.
- **Detail**: PDF-Vorschau im Browser, XML-Ansicht, Prüfprotokoll, Empfänger, Betreff und Text bearbeiten, Anlagen.
- **Aktionen**: prüfen, freigeben, senden (einzeln/mehrere), erneut senden, verwerfen. Versandprotokoll mit Zeitpunkt und Benutzer.
- **Autoversand** als Einstellung: nur Status *ok*, optional je Kunde oder Format.
- Später: Peppol-Versand, Rückmeldungen und Unzustellbarkeit.

## Stand 13.11.2026 – Portal umgesetzt

Fertig sind:
- Vorgangsliste mit PDF-Vorschau und Prüfung
- Versanddaten bearbeiten, Senden, Massenversand, Verwerfen, Verlauf
- Autoversand (Einstellung, mit Wartezeit)
- SMTP-Versand
- Eingangsordner und Upload
- Steuerzeichen-Auswertung inkl. ERP-Vorlage
- Pflichtfeld-, Summen- und Einheitenprüfung

Details stehen in der README.

**Hinweis zur XML-Erzeugung:** Die Bibliothek ZUGFeRD-csharp (Apache-2.0) wird laut NuGet ab 1.12.2026 nicht mehr gepflegt. Zur Wahl stehen:
- sie trotzdem einsetzen (Lizenz bleibt gültig),
- einen eigenen CII-/UBL-Generator schreiben,
- auf einen Fork wechseln.

Die Entscheidung fällt mit dem Druckdienst.
