# Übergabe – Projekt Belegpost (Repo „pdf-rechnung“, Stand 13.11.2026)

Mit diesem Dokument kann ein neuer Chat nahtlos weiterarbeiten.

## Ziel

Ein eigener Ersatz für **gotomaxx PDFMAILER**, ohne Lizenzkosten.

- Das ERP (BüroWARE/erpFlow) druckt Belege mit gotomaxx-Steuerzeichen (`@@XXWert@@`) auf einen virtuellen Drucker.
- Ein Windows-Dienst am Server liest die Steuerzeichen und erzeugt das PDF mit Briefpapier.
- Zusätzlich erzeugt er die E-Rechnung im angeforderten Format (ZUGFeRD/Factur-X, XRechnung, Peppol).
- Das Portal zeigt die Vorgänge zur Prüfung an und versendet sie, manuell oder per Autoversand.

## Wo liegt was

| Ort | Inhalt |
|---|---|
| GitHub `mdauwa/pdf-rechnung` (Branch `main`) | Quellcode, maßgeblicher Stand |
| `Y:\Dev\eRechnung` | lokale Kopie des Codes (noch **kein** Git-Klon) plus Ordner `Dokumente`, `PdfDrucker` und Test-PDFs des Benutzers |
| `Y:\Dev\Firmenportal` | Vorlage für Dienst, Einrichtung, HTTPS, Anmeldung (Repo `mdauwa/Firmenportal`) |
| claude.ai-Projekt „eRechnung“ | Konzept, Feldzuordnung, diese Übergabe |
| `docs/` im Repo | Konzept, Feldzuordnung Steuerzeichen → ZUGFeRD, ERP-Vorlage `ERP-Vorlage-Drucksteuerzeichen.json` |

## Entscheidungen

- **Drucker:** Treiber **Microsoft Print to PDF** mit eigenem Anschluss statt `PORTPROMPT:`, damit kein Speichern-Dialog kommt. Bevorzugt eine Named Pipe, als Ausweichlösung mfilemon. Ein fester Dateiname als Anschluss ist ungeeignet.
- **Kein eigener Treibercode.** Keine AGPL-Bausteine (Ghostscript, PDFCreator, clawPDF), weil der Drucker eventuell weitergegeben wird.
- **E-Rechnungsformat wie angefordert** (`@@xt@@`, auch ZUGFeRD 1). Wird ein Format nicht unterstützt, bekommt der Vorgang den Status **Fehler** mit einer Meldung im Portal. Er wird dann nicht automatisch versendet.
- **Rechnungsdaten kommen aus den gedruckten Steuerzeichen.** Die ERP-Webschnittstelle mit Belegindex ist nur eine Option für später.
- **Steuerzeichen kompatibel zu gotomaxx.** Erweiterungen bekommen einen eigenen Präfix, z. B. `@@ca3@@` für PDF/A-3 oder `@@xi ERP_VOUCHER_INDEX=@@`.
- **Name (13.11.2026): „Belegpost“** – neutral, ohne Bezug zu ERP Austria/Phoenix Data Systems. Programm `Belegpost.exe`, Dienst `Belegpost` (Anzeigename „Belegpost Portal“), Daten `C:\ProgramData\Belegpost`, Programmordner `C:\Program Files\Belegpost`. Ein alter Dienst `PdfRechnung` wird bei der Installation entfernt, `C:\ProgramData\PdfRechnung` wird beim Start übernommen. Repo und C#-Namensraum heißen weiter `PdfRechnung` (nicht sichtbar).
- **Technik:** .NET 9, Aufbau wie im Firmenportal, Ports 5444/5081. Der Dienst läuft als SYSTEM.
- **Reihenfolge:** Das Portal kam zuerst, jetzt folgt der Druckdienst.
- **Erst normale PDFs, dann ZUGFeRD (13.11.2026):** Zuerst läuft der komplette Weg ohne E-Rechnung: Drucker, Steuerzeichen entfernen, Briefpapier, Versand. ZUGFeRD kommt erst dazu, wenn das stabil funktioniert.
- **ERP-Ausgabe der Steuerzeichen:** Dass die Steuerzeichen jetzt gedruckt werden, lag an einer internen ERP-Einstellung. Für das Projekt ist das nicht relevant.

## Stand (fertig, getestet, 84 Tests grün)

- **Portal:**
  - Vorgangsliste mit Filtern und Menüzählern
  - Detail mit PDF-Vorschau, Prüfmeldungen, Rechnungsdaten mit Positionen und nachgerechneten Summen/Steuer, Verlauf, gelesenen Steuerzeichen
  - Versanddaten bearbeiten, Senden, Massenversand, „Trotz Fehler senden“, Verwerfen/Wiederherstellen, erneut senden
- **Autoversand** als Einstellung: Wartezeit, optional auch mit Warnungen, nie bei Fehlern.
- **Versand:** Microsoft 365 über die Graph-API (App-Registrierung mit `Mail.Send`; Anlagen über 3 MB per Upload-Sitzung) oder SMTP mit MailKit, jeweils mit Testmail.
- **Versand-PDF:** Steuerzeichen werden aus dem PDF gelöscht. Briefpapier je Firma per `@@bpName@@` oder als Standard, mit eigener erster Seite und Folgeseiten. Druckbereich mit Rändern: Warnung, wenn der Inhalt hineinragt, oder automatisches Einpassen. Dazu Versatz, Skalierung und eine Vorschau in den Einstellungen. Das Portal zeigt wahlweise Versand-PDF oder Original.
- **HTML-Mails im CI (Einstellungen → Mail-Vorlagen):**
  - Baukasten: Logo (PNG/JPG, eingebettet per cid), Hauptfarbe, Text-/Hintergrundfarbe, Schrift, Kopfzeile, Infokasten (Beleg, Datum, Betrag, Anhang), Signatur, Fußzeile; optional eigener Betreff/Text und Absender je Vorlage. Profis: eigenes HTML mit `{Inhalt}` `{Logo}` `{Belegbox}` `{Signatur}` `{Fusszeile}`.
  - Auswahl: `@@ptName@@` → Briefpapier → Absender (`@@pf@@`/Einstellungen) → Standard → schlichte Mail. Vorschau in den Einstellungen und im Vorgang („So sieht die Mail aus“).
  - Versand als multipart (HTML + Text) über SMTP und Graph (Inline-Anlage mit `contentId`, abweichender Absender).
- **Platzhalter:** `{Name}` und `{Name|Ersatztext}`, eingebaute plus eigene per `@@pvName=Wert@@`. Fehlende → Warnung.
- **Textkorrektur (`TextFixer`):** UTF-8-als-ANSI („Ã¼“), Ersatzzeichen in bekannten Wörtern, NFC, Ligaturen, weiche Trennstriche, Steuerzeichen; `@@mb@@`-Dateien in ANSI/UTF-8. Korrekturen erscheinen als Hinweis.
- **IP-Bindung:** Einstellungen → Server & Zertifikat → „Gebunden an“; leer = alle Adressen, 127.0.0.1 immer dabei, fehlende Adressen werden beim Start übersprungen (`NetworkBinding`).
- **Benutzer erkennen und Sicht (13.11.2026):**
  - **`@@pb007@@` = ERP-Bedienernummer** (3-stellig, numerisch; Benutzer → Spalte „ERP-Nr.“). Vorrang vor `@@pu…@@` (Windows-/ERP-Name) und dem Benutzer des Druckauftrags (liefert später der Drucker). Unbekannte/ungültige Nummer → Warnung im Vorgang.
  - Zuordnung unter Benutzer: Bediener-Nr., Druck-Benutzer, E-Mail ohne Domäne.
  - `Vorgang.OwnerUserId`, Menü „Meine Belege“.
  - Sicht je Benutzer: `eigene`, `auswahl` (+ `AlsoSees`) oder `alle`. Admins sehen alles.
  - Durchgesetzt per Endpoint-Filter in `VorgangApi`. Belege ohne Zuordnung sehen nur „alle“/Admins.
- **Live und Melder:**
  - `EventHub` (SSE mit Nachlieferung für 10 Minuten).
  - `/api/ereignisse` für offene Tabs: Sprung zum Beleg oder Windows-Benachrichtigung.
  - `/melder/api/*` mit Bearer-Schlüssel für den **Belegpost Melder** (`src/BelegpostMelder`, WinForms-Tray). Kopplung per Link/Code mit Zertifikats-Fingerabdruck, Autostart, Protokoll `belegpost-melder:`.
  - Ende-zu-Ende geprüft: Eingangs-PDF mit `@@pu007@@` → Ereignis am Melder-Endpunkt.
  - Die WinForms-Oberfläche ist unter Windows noch nicht getestet.
- **Firmenportal:**
  - Übergabe der Anmeldung per HS256-JWT an `/auth/firmenportal` (`HandoverLogin`, einmalige `jti`, höchstens 5 Minuten).
  - Einbettung über `frame-ancestors`; Cookie `SameSite=None`, wenn aktiv.
  - Spezifikation und Beispielcode: `docs/Firmenportal-Anbindung.md`.
  - Das Firmenportal baut der Benutzer selbst um: **dort nichts ändern**.
- **Symbol:** eigenes Symbol für EXE, Portal und README (`docs/branding`).
- **Eingang:** Eingangsordner `C:\ProgramData\Belegpost\eingang` und Upload im Portal.
- **Steuerzeichen-Auswertung:**
  - Zeilen werden aus den Buchstaben gebildet. Dadurch werden Steuerzeichen an jeder Position erkannt: winzig, neben anderem Text, gedreht, auf jeder Seite.
  - Ausgewertet werden `xt`/`xi`/`xr`/`<<|`, die Mail-Steuerzeichen inkl. `aa` und `mf` sowie `bp`, `fn` usw.
- **Pfadzuordnung** (Einstellungen → Drucker & Eingang), z. B. `K:\=\\server\freigabe\`.
- **Prüfung:** Pflichtfelder EN 16931/XRechnung, Summen gegen `GROSS_AMOUNT`, Einheiten (ERP-Text → UN/ECE), Empfänger, Anlagen.
- **Einstellungsoberfläche:** Dienst, Server & Zertifikat, Drucker & Eingang, E-Mail & Versand, Mail-Vorlagen, E-Rechnung (Einheiten), Ablage, Benutzer, Anmeldung.

## Erkenntnisse aus echten Ausdrucken

1. **Der Treiber verändert nichts.** Microsoft Print to PDF übernimmt die Steuerzeichen unverändert. Geprüft mit WordPad: `@@fnTest123@@` und `@@toinfo@test.at@@` wurden erkannt.
2. **Frühere ERP-Ausdrucke** (Rechnung 41260945, Angebote 11260051/11260056) enthielten statt der Steuerzeichen ein Platzhalterfeld rechts oben: 2,2 pt, 100 Leerzeichen und 20 `*`. Das ERP hat den Inhalt also schon vor dem Druck ersetzt.
3. **Nach einer Änderung im ERP** (`erechnung.pdf`, Angebot 11260051) druckt das ERP den PDFMAILER-Block rechts oben:
   ```
   @@mfAngebot_N11260051_13.11.2026@@
   @@aa<empfänger>@@
   @@edAngebot 11260051 13.11.2026@@
   @@mbK:\WebWARE5\APP\PDFMailer\DAT00004\Angebot.txt@@
   @@od0@@
   @@maK:\WebWARE5\APP\PDFMailer\DAT00004\ERP Austria Group Dienstleistungspreise 2027.pdf@@
   ```
   - Die Pfade auf `K:\` brauchen am Server die Pfadzuordnung.
4. **Noch nicht gesehen:** ein Ausdruck mit **ZUGFeRD-Steuerzeichen** (`@@xt`/`@@xi`/`@@xr`). Dafür wird eine Rechnung an einen Kunden mit hinterlegtem E-Rechnungsformat benötigt (ADR_6434_2).

## Nächste Schritte (normale PDFs, ohne ZUGFeRD)

1. **Drucker einrichten:** Drucker „Belegpost“ mit Microsoft Print to PDF und eigenem Anschluss aus der Oberfläche anlegen und freigeben (bevorzugt Named Pipe, sonst mfilemon). Den Druckauftrag mit Benutzer und Rechner übernehmen.
2. **Weitere Steuerzeichen:** `@@af@@` (PDF anhängen, `no_stationery`), `@@nd@@` (Druckauftrag teilen), `@@rpc@@` (Seite entfernen).
3. **Ende-zu-Ende-Test** mit echten Briefpapieren je Firma.

Danach kommt ZUGFeRD: `@@xt@@`/`@@xi@@`/`@@xr@@` werden zu CII/UBL, dazu **PDF/A-3** mit `factur-x.xml`. PDF/A-3 ist für ZUGFeRD Pflicht, und das Versand-PDF wird dafür entsprechend erzeugt.
- Hinweis: ZUGFeRD-csharp (Apache-2.0) wird laut NuGet ab 1.12.2026 nicht mehr gepflegt. Zur Wahl stehen: trotzdem einsetzen, ein eigener Generator oder ein Fork.

## Arbeitsweise

- **Test-PDFs:** Der Benutzer legt sie in `Y:\Dev\eRechnung` ab oder hängt sie im Chat an.
- **Steuerzeichen schnell prüfen:** Kleines Konsolenprogramm, das `PdfText.Extract` und `ControlCodeParser.Parse` aufruft. Alternativ `pdftotext -raw <pdf> - | grep @@`.
- **Bauen und testen:** `dotnet test` im Repo-Stamm. Release: `dotnet publish src/PdfRechnung/PdfRechnung.csproj -c Release -r win-x64` → `Belegpost.exe`.
- **Lokaler Ordner:** Nach jeder Änderung auf GitHub pushen und `Y:\Dev\eRechnung` mitziehen. Besser: den Ordner einmal als Git-Klon neu anlegen. Die Benutzerordner `Dokumente` und `PdfDrucker` dabei erhalten.
- **Sprache:** Antworten auf Deutsch, Ansprache mit „du“.
