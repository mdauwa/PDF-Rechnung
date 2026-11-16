# Übergabe – Projekt Belegpost (Repo „pdf-rechnung“, Stand 15.11.2026)

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
| `Y:\Dev\eRechnung` | Arbeitsdateien auf Stand 7a03c1b, **`.git` fehlt noch** – der Benutzer richtet ihn per PowerShell ein: `git init -b main; git remote add origin https://github.com/mdauwa/PDF-Rechnung.git; git fetch origin; git reset origin/main; git branch --set-upstream-to=origin/main; Add-Content .git\info\exclude "/Dokumente/`n/PdfDrucker/`n/*.pdf"`, danach `git restore .` (setzt die alten Arbeitsdateien auf den GitHub-Stand; `git pull` allein reicht nach `git reset` nicht), weiter mit `git pull`. Befehle wurden dem Benutzer am 15.11. gegeben, inkl. `/_to_delete/` im Exclude. Alte Dateien `docs\branding\PdfRechnung-*` und `src\PdfRechnung\PdfRechnung.ico` sollen nach `_to_delete` (Benutzer hat zugestimmt) plus Ordner `Dokumente`, `PdfDrucker`, Test-PDFs (per `.git\info\exclude` ignoriert) und `Ausgabe` (fertige Programme, flach: `Belegpost.exe`, `Belegpost-Melder.exe`, `BelegpostMapi-x64/x86.dll`, `wwwroot` – Pflicht neben der EXE) |
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

## Stand (fertig, getestet, 136 Tests grün – Commit d8d46ba; `Y:\Dev\eRechnung\Ausgabe` auf diesem Stand, ohne Mac-ZIP)

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
- **Outlook über den Melder (13.11.2026):**
  - Im Vorgang gibt es „In Outlook öffnen“ (Knopf und Link beim Versand). Das schickt ein Ereignis `outlook` an den eigenen Melder.
  - Alternativ im Melder einstellen: „Bei neuem Beleg → Outlook“.
  - Der Melder holt `/melder/api/vorgang/{id}/mail` (Empfänger, Betreff, HTML der CI-Vorlage, Logo inline, Beleg-PDF und Anlagen).
  - Er legt den Entwurf per COM im klassischen Outlook an und zeigt ihn an (`Display`). Es werden nur Eigenschaften gesetzt, deshalb gibt es keine Sicherheitsabfrage.
  - Ohne klassisches Outlook: `.eml` mit `X-Unsent` (`/melder/api/vorgang/{id}/eml`).
  - Danach steht der Vorgang auf Status `outlook` („In Outlook“).
  - **Freigabe je Benutzer:** Spalte „Outlook“ (`PortalUser.SendViaOutlook`). Nur dann gibt es „Freigeben & in Outlook senden“ und die Melder-Einstellung „Outlook“; die Endpunkte prüfen das ebenfalls.
  - Die Belege solcher Benutzer werden nicht automatisch versendet.
  - Ablauf: Im ERP drucken → Melder meldet und öffnet das Portal mit dem Vorgang → „Freigeben & in Outlook senden“ → Outlook öffnet die fertige Mail.
  - Absender ist das Standardkonto in Outlook.
- **Melder für macOS (13.11.2026):**
  - `src/BelegpostMelderMac`: SwiftUI-Menüleisten-App (macOS 13+), gleiche Melder-API wie Windows.
  - Outlook für Mac per AppleScript, Rückfall `.eml` bzw. Apple Mail.
  - Bauen auf einem Mac mit `build.sh` (Universal-Binary, ad-hoc oder Developer-ID-Signatur, optional Notarisierung).
  - Die ZIP liegt neben `Belegpost.exe`; das Portal bietet sie Mac-Benutzern an.
  - Auf dem Mac (Apple Silicon, macOS 27) am 14.11.2026 fehlerfrei gebaut (Universal-Binary, ad-hoc signiert). Ein Funktionstest gegen das Portal steht noch aus.
- **Firmenportal:**
  - Übergabe der Anmeldung per HS256-JWT an `/auth/firmenportal` (`HandoverLogin`, einmalige `jti`, höchstens 5 Minuten).
  - Einbettung über `frame-ancestors`; Cookie `SameSite=None`, wenn aktiv.
  - Spezifikation und Beispielcode: `docs/Firmenportal-Anbindung.md`.
  - Das Firmenportal baut der Benutzer selbst um: **dort nichts ändern**.
- **Update per Doppelklick:** Startet man eine neue `Belegpost.exe`, während der Dienst installiert ist, aktualisiert sie ihn automatisch (Adminabfrage, `--install --open-browser`) und beendet sich. Das passiert, wenn die Datei abweicht, der alte Dienst `PdfRechnung` noch existiert oder der Dienst nicht läuft. Läuft der Dienst schon mit derselben Datei, öffnet sich nur der Browser.
- **Symbol:** eigenes Symbol für EXE, Portal und README (`docs/branding`).
- **Eingang:** Eingangsordner `C:\ProgramData\Belegpost\eingang` und Upload im Portal.
- **Steuerzeichen-Auswertung:**
  - Zeilen werden aus den Buchstaben gebildet. Dadurch werden Steuerzeichen an jeder Position erkannt: winzig, neben anderem Text, gedreht, auf jeder Seite.
  - Ausgewertet werden `xt`/`xi`/`xr`/`<<|`, die Mail-Steuerzeichen inkl. `aa` und `mf` sowie `bp`, `fn` usw.
- **Pfadzuordnung** (Einstellungen → Drucker & Eingang), z. B. `K:\=\\server\freigabe\`.
- **Prüfung:** Pflichtfelder EN 16931/XRechnung, Summen gegen `GROSS_AMOUNT`, Einheiten (ERP-Text → UN/ECE), Empfänger, Anlagen.
- **Einstellungsoberfläche:** Dienst, Server & Zertifikat, Drucker & Eingang, E-Mail & Versand, Mail-Vorlagen, E-Rechnung (Einheiten), Ablage, ERP-Rückmeldung, MAPI-Mails, Status-Link, Benutzer, Anmeldung.
- **Datenbank SQLite (15.11.2026):** `C:\ProgramData\Belegpost\belegpost.db` (WAL) mit Vorgängen, Zeitstrahl, Benutzern, Melder-Geräten, Melder-Aufträgen, ERP-Warteschlange, Status-Links und Audit (`Data/BelegpostDb.cs`, Schema über `PRAGMA user_version`).
  - PDFs, Anlagen, Einstellungen und der ERP-Servicepass bleiben Dateien.
  - Alte `meta.json`/`users.json` werden beim ersten Start übernommen und in `*.migriert` umbenannt.
  - Listen und Zähler laufen per SQL (Sichtbarkeit als Filter), nicht mehr im Speicher.
  - Tägliche Sicherung nach `daten\sicherung` (Anzahl: `Storage:DatabaseBackups`, Standard 14), Prüfung beim Start, wöchentlich VACUUM.
- **Statuscodes und Zeitstrahl (15.11.2026):** Jeder Statuswechsel wird als 3-stelliger Code in `vorgang_ereignis` festgehalten (`Documents/StatusCodes.cs`):
  - 001 erfasst · 010 wartet auf Freigabe · 020 freigegeben · 030 an Outlook übergeben
  - 100 versendet · 110 versendet über Outlook (bestätigt) · 120 erneut versendet · 200 ohne Versand
  - 800 Fehler im Beleg · 810 Versandfehler · 820 Outlook-Übergabe fehlgeschlagen · 830 Outlook-Versand nicht bestätigt · 900 verworfen
  - Hinweis „ERP“ = Status im ERP bestätigt.
  - Im Vorgang zeigt die Karte „Belegstatus“ Zeitstrahl, Status-Link und ERP-Rückmeldung.
- **Status-Link (15.11.2026):** `/s/<token>` (128-Bit-Zufall, je Belegindex bzw. Vorgang), ohne Anmeldung, nur aus freigegebenen IP-Bereichen (CIDR; Proxys nur wenn eingetragen).
  - Zeigt nur Belegart, Belegnummer und den Zeitstrahl, keine Empfänger, Beträge oder Inhalte.
  - `.json`-Variante; Rate-Limit, `noindex`/`no-referrer`/`no-store`, Ablauf, widerrufbar.
- **ERP-Rückmeldung (15.11.2026):** `@@pi<BELNDX>@@` → bei jedem Statuswechsel ein Auftrag in `erp_auftrag` (Millisekunden, Versand wird nie aufgehalten).
  - `ErpFeedbackService` schreibt nacheinander per `BELEG.PUT` (`KEINE_VERBUCHUNG=1`, `BERECHNE_SUMMEN=0`), optional mit `BELEG.GET` vorher.
  - Wartezeiten 30 s/2/10/30 min/stündlich; nach `GiveUpDays` Fehler. Pro Belegindex zählt der neueste Status.
  - Feldtabelle: Feld frei (`BEL_3_8` → Länge 8), Wert aus Liste (Statuscode, Statustext, Datum, Zeit, Versanddatum, Kanal, Empfänger, Bediener, Benutzer, Status-Link, Betreff, fester Text, Vorlage). Dazu Format, „nur bei Codes“, „nur wenn leer“.
  - Statuscodes je ERP-Wert anpassbar und abschaltbar. Vorschau und Testschreiben in den Einstellungen.
  - **WWSVC-Anbindung** aus ERP-Austria-MCP-Server übernommen (`Erp/Wwsvc.cs`):
    - REGISTER nur wenn erlaubt, mit vorher gespeicherter Absicht (`RegistrationOutcomeUnknown`, Admin bestätigt).
    - VALIDATE vor jeder Operation (202 = Freigabe ausstehend, 404/50200 = neu registrieren).
    - CONNECT mit einer Wiederholung bei 401; Anfragenummer vor jedem Aufruf gespeichert; ein Gate.
    - **Eigene SecuredApp und eigener Pass** (verschlüsselt in `daten\erp\pass-*.bin`). Nur `BELEG.GET`/`BELEG.PUT` erlaubt.
- **MAPI-Umleitung (15.11.2026):**
  - `src/BelegpostMapi`: Simple-MAPI-Mailclient in C (mingw-w64, `build.sh`) → `BelegpostMapi-x64.dll` und `BelegpostMapi-x86.dll` ohne Abhängigkeiten.
  - Installation unter Einstellungen → MAPI-Mails: 64 Bit nach System32, 32 Bit nach SysWOW64, ein gemeinsamer Pfad unter `HKLM\Software\Clients\Mail\Belegpost`. „Für alle als Standard“ merkt sich den vorherigen Wert; pro Benutzer per `reg add HKCU\Software\Clients\Mail /ve /d Belegpost /f`.
  - **Windows-Standard-App „E-Mail“ (15.11.2026):** Die Installation registriert Belegpost als Windows-App mit E-Mail-Fähigkeit (`Clients\Mail\Belegpost\Capabilities`, `RegisteredApplications`, ProgID `Belegpost.mailto`). mailto-Links laufen über `rundll32 BelegpostMapi.dll,Mailto "%1"` (Einstieg `MailtoW` in der DLL, RFC 6068) und damit über dieselbe Pipe wie MAPI. „Für alle als Standard“ setzt zusätzlich die Richtlinie `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\DefaultAssociationsConfiguration` auf `C:\Program Files\Belegpost\Standard-Apps-Belegpost.xml` (mailto → Belegpost, wirkt bei jeder Anmeldung; eine fremde Richtlinie wird nicht überschrieben). „Wiederherstellen“ entfernt sie.
  - **Standard je Windows-Benutzer (15.11.2026):** HKCU hat Vorrang vor HKLM. „Für alle als Standard“ setzt deshalb zusätzlich `HKU\<SID>\Software\Clients\Mail` aller geladenen Profile (vorheriger Wert in `Software\Belegpost\MapiVorher`) und einen Active-Setup-Eintrag `BelegpostMapi` (StubPath `reg add …`), der alle anderen Benutzer bei der nächsten Anmeldung umstellt. Knopf „Bei allen Windows-Benutzern setzen“ wiederholt das (z. B. wenn Outlook sich wieder einträgt); die Seite zeigt je angemeldetem Benutzer, ob MAPI bei Belegpost landet. „Wiederherstellen“ stellt die gemerkten Werte zurück und entfernt Belegpost per Active Setup bei der nächsten Anmeldung.
  - Die DLL liest Anhänge sofort, sendet über die Named Pipe `\\.\pipe\belegpost-mapi` (nur lokal; Windows-Benutzer aus dem Prozess-Token des Aufrufers, ohne Impersonation) samt RDP-Client-Computername. Ist der Dienst nicht erreichbar, legt sie in `%LOCALAPPDATA%\Belegpost\mapi-ausgang` ab und liefert nach.
  - Der Dienst (`MapiIntake`) legt einen Vorgang „MAPI“ an: MAPI-Empfänger/Betreff/Text haben Vorrang, PDF-Steuerzeichen gelten weiter, weitere Anhänge liegen im Vorgang.
  - Einstellung **global → Benutzer → PC**: Outlook (Standard) / Portal / direkt. Ziel-PC = Melder, dessen Computername dem RDP-Client entspricht, sonst der zuletzt aktive.
  - Aufträge an den Melder sind persistent (`melder_auftrag`, Übernahme atomar). Nicht abgeholt nach `DeliveryTimeoutHours` → 820.
  - Der Benutzer ändert die Einstellung seines PCs im Melder oder Portal. Der Admin setzt die Vorgabe und kann sie auf alle PCs „überrollen“ (Ereignis `einstellung`); danach darf der Benutzer wieder ändern.
- **Normale Mail oder Beleg (15.11.2026):** Eine MAPI-Mail ist ein **Beleg**, wenn ein PDF-Anhang oder der Mailtext Belegpost-Steuerzeichen enthält. Alles andere (z. B. „Senden“ aus WordPad/Word/Explorer) ist eine **normale E-Mail** (`MailData.Plain`): keine Prüfung, kein Briefpapier, keine CI-Vorlage, alle Anhänge bleiben Anhänge, nie Autoversand. Sie wird immer als Entwurf in Outlook auf dem PC des Benutzers geöffnet (auch ohne Outlook-Freigabe); ohne zugeordneten Benutzer oder Melder bleibt sie im Portal. Landet eine Mail nicht in Outlook, zeigt die DLL den Grund als Hinweis an. PC-Zuordnung toleriert gekürzte RDP-Clientnamen (`UserStore.SameComputer`); der Mac-Melder meldet den lokalen Hostnamen.
- **Mail-Regeln und Signaturen (16.11.2026, am Server noch nicht getestet):**
  - Global: Einstellungen → „Regeln & Signaturen“ (`AppSettings.MailRules`). Je Benutzer: Datei `daten\regeln\<id>.json` (`MailRuleStore`), bearbeitbar unter Persönlich → „Meine Mail-Regeln“ und vom Admin in der Benutzer-Maske (API `/api/meine-regeln[?user=]`).
  - Regel = Bedingungen (Belegart, Briefpapier, Absender, Empfänger, Kunde, Quelle, Format, Text, global zusätzlich Benutzer; * ?) → Aktionen (Vorlage, Absender, Absendername, Antwort-an, Signatur, Versandweg, gotomaxx-Drucker, CC, BCC, Stopp). Eigene vor globalen, je Aktion erste Regel gewinnt (`MailRuleEngine`). Steuerzeichen übersteuern immer; neue `@@ps…@@` (Signatur, `@@ps@@` = keine) und `@@pw…@@` (Versandweg direkt/wennok/freigabe/outlook/gotomaxx/keine).
  - Signatur: `@@ps@@` → Regel → Benutzer (Briefpapier → Absender → Standard) → global → Vorlage; gespeichert in `MailData.SignatureText`, `MailDesigner.Render(…, signatureOverride)`.
  - Versandweg: direkt = Autoversand auch wenn global aus; wennok = Autoversand nur ohne Warnungen/Fehler (auch bei MAPI), sonst Freigabe; freigabe/outlook = nie automatisch; outlook = Melder-Auftrag beim Eingang (nur mit Outlook-Freigabe); gotomaxx = Weiterleitung; keine = Mail aus.
- **Benutzerübersicht (16.11.2026):** einzeilige Tabelle (Name, ERP-Nr., E-Mail, Rechte, Sicht, Melder, Anmeldung), Klick öffnet eigene Maske (Person, Rechte, Zuordnung, Sichtbarkeit, MAPI/Melder, Signaturen & Regeln).
- **Outlook-Versandbestätigung (15.11.2026):**
  - Windows-Melder: Benutzereigenschaft `BelegpostId` am Entwurf; minütliche Suche (DASL) in „Gesendete Elemente“ aller Postfächer, nur wenn Outlook läuft → `/melder/api/vorgang/{id}/gesendet` → 110.
  - Mac: Betreff und Zeit in „Gesendet“ (AppleScript).
  - Ohne Bestätigung nach `OutlookConfirmHours` → 830.

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

0. **Servertest (läuft, Stand 15.11.2026 14:20):**
   - Erledigt: Dienst aktualisiert (SQLite-Übernahme ok), MAPI-Client installiert, „für alle als Standard“ gesetzt, MAPI kommt im Dienst an. Mac-Melder mit neuem Code gebaut (ZIP 14:03, enthält Hostname + Text-Mails).
   - Behoben: „Zugriff verweigert auf `vorgaenge`“ (594cbbc); WordPad-„Senden“ als Fehler im Portal (28c1175: normale Mail vs. Beleg).
   - **Neu, am Server noch nicht getestet:** Standard je Windows-Benutzer (2732db2) und Belegpost als Windows-Standard-App „E-Mail“ mit mailto-Handler (b383788).
   - **Ausgabe:** Die aktuelle Ausgabe (b383788, inkl. `Belegpost-Melder-macOS.zip`) wurde dem Benutzer als `Belegpost-Ausgabe.zip` im Chat geschickt. `Y:\Dev\eRechnung\Ausgabe` ist noch auf 28c1175 (ohne Mac-ZIP) – beim nächsten Zugriff auf die VM neu bauen und dorthin kopieren (`dotnet publish src/PdfRechnung -c Release -r win-x64`, DLLs per `build.sh`, Mac-ZIP liegt am Mac unter `~/Claude/BelegpostMelderMac/build/`).
   - **Nächste Schritte am Server:** Ausgabe nach `C:\Install\Belegpost`, `Belegpost.exe` doppelklicken → Einstellungen → MAPI-Mails → „Neu installieren“ (ERP/WordPad vorher schließen) → „Für alle Benutzer als Standard setzen“ → ab-/anmelden → prüfen: Standard-Apps → E-Mail = Belegpost, Tabelle „Angemeldeter Windows-Benutzer“ zeigt Belegpost. Testen: WordPad → Senden und `mailto:test@example.com?subject=Test` (Win+R) → Outlook am PC des Benutzers. Danach ERP-Beleg per MAPI (32/64 Bit), Outlook-Versandbestätigung (110), ERP-Rückmeldung mit eigener SecuredApp.
   - Hinweis: Die neuen MAPI-DLLs werden nur ersetzt, wenn kein Prozess sie geladen hat (sonst beim Neustart).
   - **Erledigt 15.11.:** Standard-Apps → E-Mail zeigt jetzt „Belegpost“ mit Symbol (Versionsinfo + Icon in der DLL, ProgID-Unterschlüssel `Application`, b77e95f) – nach „Neu installieren“ prüfen. Der mailto-/MAPI-Weg funktioniert laut Benutzer.
0a. **Chats:** Ab 15.11.2026 gibt es zwei Chats im Projekt – einer für **Windows/Server** (Claude-App in der VM `martin-vm40`, Ordner `Y:\Dev\eRechnung`), einer für den **Mac** (`mdauw-app-air25-local`, Ordner `~/Claude/BelegpostMelderMac`). Ein Chat ist immer nur mit einem Rechner verbunden.
   - **Mac-Build:** `~/Claude/BelegpostMelderMac` ist eine Kopie von `src/BelegpostMelderMac` (kein Git). Geänderte Dateien dorthin kopieren und im Terminal `cd ~/Claude/BelegpostMelderMac && ./build.sh` ausführen (oder „Melder bauen.command“ doppelklicken → Protokoll in `build.log`). Claudes Shell am Mac ist Linux und kann nicht für macOS bauen; Terminal ist für die Bildschirmsteuerung nur „click“. Offen am Mac: Funktionstest des Melders (normale Mail aus der RDP-Sitzung muss Outlook am Mac öffnen; Computername = lokaler Hostname).
1. **Drucker „Belegpost“ (15.11.2026, Commit 988163b, am Server noch nicht getestet):**
   - Einstellungen → Drucker & Eingang → „Drucker einrichten“: PowerShell legt den lokalen Anschluss `\\.\pipe\belegpost-druck` an und den Drucker mit Treiber **Microsoft Print To PDF** (optional freigegeben). `PrinterIntake` lauscht auf der Pipe, liest beim Verbinden den laufenden Auftrag aus der Warteschlange (`EnumJobs`: Dokument, Benutzer, Rechner, Einreichzeit) und macht daraus einen Vorgang (Quelle „Drucker“). Kein PDF → `eingang\fehler`.
   - **ERP-Benutzer aus dem druckenden Prozess (WebWARE):** Die WebWARE startet je Benutzer `wwa.exe <Benutzernr> /M:… /S`, alle als SYSTEM. `PrintProcessTracker` schreibt per ETW (Microsoft-Windows-RPC, Ereignis 5, Filter auf Programmname) mit, wann `wwa.exe` die Druckwarteschlange (winspool) oder den 32-Bit-Druckhelfer (UmpdProxy/splwow64) aufruft, liest die Befehlszeile (Parameter Nr. 1 = Benutzernummer) und ordnet den Druck zeitlich zu (nächster Aufruf zur Einreichzeit; anderer Benutzer innerhalb 1 s → Warnung). Reihenfolge: `@@pb@@` → `@@pu@@` → Prozess → Benutzer des Druckauftrags. Einstellbar: Programme, Parameter-Nr., Filter nach Programmname. Diagnose (letzte Drucke, erkannte Aufrufe) in den Einstellungen.
   - Am Server testen: Drucker einrichten, aus der WebWARE drucken, prüfen ob Aufrufe von wwa.exe erscheinen. Falls nicht: „Nur in diesen Programmen mitschreiben“ aus.
1a. **Weiterleitung an gotomaxx PDFMAILER (15.11.2026, Commit 39c12cc, am Server noch nicht getestet):** `@@pg@@` (Standarddrucker), `@@pgDrucker@@`, `@@pg0@@` oder Einstellung „jeden Beleg“. Das PDF wird ohne Steuerzeichen per PDFium (bblanchon.PDFium.Win32) über GDI gedruckt; die Steuerzeichen werden je Seite als Text (weiß, 2 pt, unter dem Inhalt) neu gedruckt – ohne `pb pi pu pv pt pf pg` und `xi ERP_VOUCHER_INDEX`. Gedruckt wird in der Windows-Sitzung des Benutzers am Server (`Belegpost.exe --gotomaxx-druck <Ordner>` per CreateProcessAsUser), sonst vom Dienst. Status 140/840, Knopf im Vorgang. Belegpost-Prüfung entfällt bei Weiterleitung.
2. **Weitere Steuerzeichen:** `@@af@@` (PDF anhängen, `no_stationery`), `@@nd@@` (Druckauftrag teilen), `@@rpc@@` (Seite entfernen).
3. **Ende-zu-Ende-Test** mit echten Briefpapieren je Firma.

Danach kommt ZUGFeRD: `@@xt@@`/`@@xi@@`/`@@xr@@` werden zu CII/UBL, dazu **PDF/A-3** mit `factur-x.xml`. PDF/A-3 ist für ZUGFeRD Pflicht, und das Versand-PDF wird dafür entsprechend erzeugt.
- Hinweis: ZUGFeRD-csharp (Apache-2.0) wird laut NuGet ab 1.12.2026 nicht mehr gepflegt. Zur Wahl stehen: trotzdem einsetzen, ein eigener Generator oder ein Fork.

## Arbeitsweise

- **Test-PDFs:** Der Benutzer legt sie in `Y:\Dev\eRechnung` ab oder hängt sie im Chat an.
- **Steuerzeichen schnell prüfen:** Kleines Konsolenprogramm, das `PdfText.Extract` und `ControlCodeParser.Parse` aufruft. Alternativ `pdftotext -raw <pdf> - | grep @@`.
- **Bauen und testen:** `dotnet test` im Repo-Stamm. Release: `dotnet publish src/PdfRechnung/PdfRechnung.csproj -c Release -r win-x64` → `Belegpost.exe`.
- **Lokaler Ordner:** Nach jeder Änderung auf GitHub pushen; der Benutzer holt den Stand in `Y:\Dev\eRechnung` mit `git pull`. Fertige Programme nach `Y:\Dev\eRechnung\Ausgabe`. Aus Claude heraus kann nicht in `.git` geschrieben und nichts verschoben/gelöscht werden – solche Schritte gibt der Benutzer per PowerShell aus.
- **Bauen unter Linux (Claude):** .NET 9 SDK per `dotnet-install.sh`, MAPI-DLLs mit mingw-w64 (`gcc-mingw-w64`), Melder mit `-p:EnableWindowsTargeting=true`. Der Mac-Melder kann nur auf dem Mac gebaut werden.
- **Sprache:** Antworten auf Deutsch, Ansprache mit „du“.

## Mandanten (16.11.2026)

- Erkennung je Vorgang (`Vorgang.Mandant`, `OriginMandant`, `MandantReason`): `@@pm…@@` → `/M:` aus der Befehlszeile des druckenden Prozesses (`MandantOptions.FromCommandLine`, PrinterIntake) → Eingangsordner des Mandanten (InboxWatcher) → bei MAPI `@@pm…@@` im Mailtext.
- Einstellungen → Mandanten (`AppSettings.Mandanten.Items`): je Mandant ERP-Anbindung „wie allgemein / eigene SecuredApp / aus“ (`Mandant.ErpFor`: leere Felder = allgemein, Feldzuordnung und Statuscodes immer allgemein), Eingangsordner, gotomaxx-Standarddrucker, zusätzliche Pfadumsetzungen, Ablageordner. Kennwort verschlüsselt wie das allgemeine.
- WEBWARE: Ein Servicepass greift nur auf einen Mandanten zu → je weiterem Mandanten eine eigene SecuredApp im ERP anlegen. `ErpFeedbackService` hält je Verbindung einen eigenen `ErpServicePassManager`; `/api/admin/erp/pruefen?mandant=…`.
- Mail, Versand, Benutzer, Regeln, Vorlagen, MAPI: für alle Mandanten gleich.
- **Weitere Drucker (16.11.2026):** Einstellungen → Drucker & Eingang → „Weitere Drucker“ (`InboxOptions.Printers`, `ExtraPrinter`): je Drucker eigener Anschluss `\\.\pipe\belegpost-druck-<Id>` (feste Kennung, ändert sich beim Umbenennen nicht), optional Mandant und Briefpapier („-“ = ohne). `PrinterIntake` horcht auf alle Anschlüsse und übernimmt Änderungen laufend. Vorrang: `@@pm@@` → `/M:` → Mandant des Druckers; `@@bp@@` → Briefpapier des Druckers (`Vorgang.PrinterId`, gilt auch bei „Neu prüfen“). Einrichten/„Aus Windows entfernen“ je Drucker.
