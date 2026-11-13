<img src="docs/branding/Belegpost-256.png" width="96" align="right" alt="">

# Belegpost – Druckdienst und Versand-Portal für Belege und E-Rechnungen

(Repository: pdf-rechnung; Programm, Dienst und Datenordner heißen **Belegpost**.)

Eigener Ersatz für gotomaxx PDFMAILER.

- Das ERP druckt Belege wie bisher mit den gotomaxx-Steuerzeichen (`@@…@@`) auf einen Drucker.
- Der Dienst am Server liest die Steuerzeichen, prüft den Beleg und bereitet den Versand vor.
- Versendet wird im Portal: manuell oder, wenn eingestellt, automatisch.

Technik und Aufbau sind wie im **Firmenportal**:
- .NET 9, eine selbstständige EXE
- Einrichtung im Browser, Windows-Dienst
- HTTPS mit eigenem oder selbst signiertem Zertifikat
- Anmeldung mit Microsoft Entra ID oder Administrator-Kennwort am Server

## Stand

| Teil | Stand |
|---|---|
| Portal: Vorgangsliste, Filter, PDF-Vorschau, Prüfung, Versanddaten bearbeiten, Senden, Massenversand, Verwerfen, Verlauf | ✅ |
| Portal nur an bestimmte IP-Adressen binden (Server & Zertifikat → „Gebunden an“; localhost immer) | ✅ |
| Autoversand korrekter Vorgänge (Einstellung, mit Wartezeit) | ✅ |
| E-Mail-Versand über Microsoft 365 (Graph-API, App-Registrierung mit Mail.Send, große Anlagen per Upload-Sitzung) oder SMTP, Testmail | ✅ |
| Eingangsordner (jede PDF-Datei → Vorgang), Upload im Portal | ✅ |
| Steuerzeichen lesen (gotomaxx-kompatibel, inkl. ERP-Vorlage SEBELK/SEBELP) | ✅ |
| Prüfung: Pflichtfelder EN 16931/XRechnung, Summen und Steuer nachgerechnet, Einheiten, Empfänger | ✅ |
| Drucker „Microsoft Print to PDF“ mit eigenem Anschluss automatisch einrichten | ⏳ nächster Schritt |
| Versand-PDF: Steuerzeichen aus dem PDF entfernt (Textbefehle gelöscht), Briefpapier je Firma per `@@bpName@@` oder Standard, erste Seite/Folgeseiten, Druckbereich (Ränder) mit Warnung oder automatischem Einpassen, Versatz/Skalierung, Vorschau in den Einstellungen | ✅ |
| HTML-Mails im CI: Vorlagen mit Logo, Farben, Schrift, Kopfzeile, Infokasten, Signatur, Fußzeile oder eigenem HTML; Auswahl per `@@pt@@`, Briefpapier, Absender oder Standard; Vorschau in Einstellungen und Vorgang | ✅ |
| Platzhalter `{Name}` / `{Name\|Ersatztext}` in Betreff, Text, Signatur, Dateiname; eigene Werte per `@@pvName=Wert@@`, Absender per `@@pf@@` | ✅ |
| Automatische Korrektur kaputter Umlaute/Sonderzeichen (UTF-8 als ANSI, Ersatzzeichen, Ligaturen), `@@mb@@`-Dateien in ANSI oder UTF-8 | ✅ |
| `@@af@@`, `@@nd@@`, `@@rpc@@` | ⏳ |
| PDF/A-3 (Pflicht für ZUGFeRD) | ⏳ mit ZUGFeRD |
| E-Rechnung erzeugen (ZUGFeRD/Factur-X PDF/A-3, XRechnung, Peppol) | ⏳ – bis dahin meldet ein angefordertes Format **Fehler** (kein Autoversand) |

## Bauen

```
dotnet test
dotnet publish src/PdfRechnung/PdfRechnung.csproj -c Release -o C:\Dienste\Belegpost
```

Das Ergebnis ist eine eigenständige `Belegpost.exe` mit dem Ordner `wwwroot`. Die .NET-Laufzeit ist enthalten.

## Einrichten

Die Einrichtung läuft genau wie beim Firmenportal:

1. `Belegpost.exe` doppelklicken. Der Browser öffnet `https://localhost:5444/#/einstellungen`.
2. Administrator-Kennwort festlegen.
3. Die Reiter einstellen:

   | Reiter | Inhalt |
   |---|---|
   | Server & Zertifikat | Ports (Standard 5444/5081), PFX-Zertifikat |
   | Drucker & Eingang | Eingangsordner, Druckername |
   | E-Mail & Versand | SMTP, Absender, Vorlagen, Autoversand |
   | E-Rechnung | Summentoleranz, Zuordnung ERP-Einheit → UN/ECE-Code |
   | Ablage | Aufbewahrung, Exportordner |
   | Benutzer | Rechte *Versenden* und *Admin* |
   | Anmeldung | Microsoft Entra ID |

4. Unter **Dienst** auf „Als Windows-Dienst installieren & starten“ klicken.

Updates: die neue EXE doppelklicken.

## Ablauf eines Vorgangs

1. **Eingang**: Ein PDF landet im Eingangsordner (später direkt vom Drucker) oder wird im Portal hochgeladen.
2. **Auslesen**: Text je Seite lesen (PdfPig), Steuerzeichen sammeln. Rechnungsdaten aus `@@xi@@`/`@@xr@@` aufbereiten, Summen und Steuer nachrechnen.
3. **Versanddaten**:
   - Empfänger, CC und BCC aus `@@to@@`/`@@cc@@`/`@@bc@@`
   - Betreff aus `@@ed@@`, Text aus `@@sa@@`/`@@mb@@`
   - Dateiname aus `@@fn@@`
   - Fehlt etwas, kommen die Vorlagen aus den Einstellungen zum Zug.
4. **Prüfung**: ergibt den Status.

   | Status | Bedeutung |
   |---|---|
   | Fehler | E-Rechnung oder Versand nicht möglich |
   | Prüfen | Warnungen |
   | Bereit | korrekt |
   | Ohne Versand | `@@ml0@@` |

5. **Versand**:
   - Manuell im Portal, auch mehrere auf einmal.
   - Oder per Autoversand: Vorgänge mit Status *Bereit* (optional auch *Prüfen*) nach der Wartezeit.
   - *Fehler* gehen nie automatisch hinaus. Manuell nur mit „Trotz Fehler senden“.

## Wo liegt was?

| Pfad | Inhalt |
|---|---|
| `C:\ProgramData\Belegpost\settings.json` | Einstellungen, Passwörter verschlüsselt (`enc:…`) |
| `C:\ProgramData\Belegpost\users.json` | Benutzer, Hash des Administrator-Kennworts |
| `C:\ProgramData\Belegpost\eingang\` | Eingangsordner (Standard); nicht lesbare Dateien landen in `fehler\` |
| `C:\ProgramData\Belegpost\vorgaenge\JJJJ\MM\<Id>\` | `meta.json` (Daten, Prüfung, Verlauf), `beleg.pdf` (Ausdruck wie eingegangen), `versand.pdf` (ohne Steuerzeichen, mit Briefpapier – wird versendet), `text.txt` (ausgelesener Text) |
| `C:\ProgramData\Belegpost\briefpapier\` | hochgeladene Briefpapier-PDFs |
| `C:\ProgramData\Belegpost\mailvorlagen\` | Logos der Mail-Vorlagen |
| `keys\`, `cert\` | Schlüssel für die Verschlüsselung und Zertifikate. **Nicht löschen.** |

Für Entwicklung und Tests gibt es zwei Umgebungsvariablen:
- `BELEGPOST_DATA`: anderes Datenverzeichnis.
- `BELEGPOST_NO_BROWSER=1`: Browser beim Start nicht öffnen.

(Die alten Namen `PDFRECHNUNG_…` gehen weiterhin.)

## Steuerzeichen

Syntax wie bei gotomaxx: `@@` + 2 Zeichen Befehl + Wert + `@@`, eine Zeile, Courier ≥ 4 pt.

| Gruppe | ausgewertet |
|---|---|
| E-Rechnung | `xt` (Format), `xi FELD=Wert`, `xr table=…\|…`, `<<\|…` (Fortsetzung der vorigen Tabellenzeile wie in der ERP-Vorlage), `pe` |
| Mail | `ml`, `to`/`aa`, `cc`, `bc`, `ed`, `sa`, `mb`, `ma`, `mc`, `md`, `mf` (Dateiname aus der ERP-Vorlage) (`mo`/`ms`/`mp` werden ignoriert – der Server sendet) |
| Bearbeiten | `bp`, `fn`, `di`, `af`, `ef`, `rpc`, `nd`, `ca`, `cf` (teilweise erst mit dem Druckdienst wirksam) |
| Eigene (Belegpost) | `pvName=Wert` (Platzhalter), `ptVorlage` (Mail-Vorlage), `pfadresse@firma.at` (Absender) |

### Mail-Texte, Platzhalter und Vorlagen

- Platzhalter in Betreff, Mailtext, Kopfzeile, Signatur, Fußzeile und Dateiname: `{Belegart} {Belegnummer} {Belegdatum} {Kunde} {Kundennummer} {Betrag} {Waehrung} {Dateiname} {Absender} {Datum}` und alle mit `@@pvName=Wert@@` übergebenen, z. B. `@@pvAnrede=Sehr geehrter Herr Maier@@` → `{Anrede}`.
- Ersatztext: `{Anrede|Sehr geehrte Damen und Herren}`. Platzhalter ohne Wert und ohne Ersatztext → Warnung im Vorgang.
- Im Text: `**fett**`, `*kursiv*`, Zeilen mit `- ` als Aufzählung, Adressen/Links werden klickbar.
- Vorlagenwahl: `@@ptName@@` → Vorlage des Briefpapiers → Vorlage des Absenders (`@@pf@@` bzw. Einstellungen) → Standardvorlage → schlichte Mail.
- Texte aus dem ERP werden automatisch korrigiert (z. B. „Ã¼“ → „ü“); jede Korrektur steht als Hinweis im Vorgang.

Die Steuerzeichen dürfen überall auf der Seite stehen (auch winzig, neben anderem Text oder gedreht) und auf jeder Seite. Pfade wie `K:\WebWARE5\…` (@@mb@@, @@ma@@) werden über die Pfadzuordnung (Drucker & Eingang) auf eine Freigabe umgesetzt, weil der Dienst keine Netzlaufwerke sieht. Unbekannte Steuerzeichen erscheinen im Vorgang als Warnung.

Feldzuordnung zur ZUGFeRD-XML: [docs/Steuerzeichen zu ZUGFeRD-XML.md](docs/Steuerzeichen%20zu%20ZUGFeRD-XML.md).
Konzept: [docs/Konzept Druckdienst Phase 1.md](docs/Konzept%20Druckdienst%20Phase%201.md).

## Projektstruktur

```
src/PdfRechnung/
  Program.cs           Start: Dienst / Einrichtung / --install, Kestrel + HTTPS, API
  Settings/            Einstellungen, Verschlüsselung, Zertifikat, Dienst, Anmeldung, Admin-API (aus dem Firmenportal)
  Documents/           Steuerzeichen, Rechnungsdaten, Prüfung, Ablage, Versand, Eingang, Autoversand, Portal-API
  wwwroot/             Oberfläche (app.js Shell, vorgaenge.js Portal, settings.js Einstellungen)
tests/PdfRechnung.Tests/  Tests (Steuerzeichen, Summen, Prüfung, PDF → Vorgang)
docs/                  Konzept und Feldzuordnung
```

## Bibliotheken

| Paket | Lizenz | Zweck |
|---|---|---|
| PdfPig | Apache-2.0 | PDF lesen |
| PDFsharp | MIT | Versand-PDF: Steuerzeichen entfernen, Briefpapier unterlegen |
| MailKit | MIT | SMTP (HTML mit eingebettetem Logo) |
| Microsoft.Extensions.Hosting.WindowsServices, Microsoft.AspNetCore.Authentication.OpenIdConnect | MIT | Windows-Dienst, Microsoft-Anmeldung |
