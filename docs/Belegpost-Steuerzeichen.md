# Belegpost: eigene und erweiterte Steuerzeichen

Stand 16.11.2026. Alle Steuerzeichen werden wie bei gotomaxx als `@@xxWert@@` gedruckt und aus dem Versand-PDF entfernt. Groß-/Kleinschreibung des Kürzels ist egal. Die Position im Ausdruck spielt keine Rolle: Klein, gedreht oder auf jeder Seite gedruckt funktioniert alles.

## 1. Neue Steuerzeichen (gibt es in gotomaxx nicht)

| Steuerzeichen | Beispiel | Wirkung |
|---|---|---|
| `@@pb…@@` | `@@pb007@@`, `@@pb7@@` | **ERP-Bedienernummer.** Der Beleg gehört dem Portal-Benutzer mit dieser Nummer (Einstellungen → Benutzer → ERP-Nr.). Er erscheint dort unter „Meine Belege“ und öffnet sich über dessen **Melder** am PC. Vornullen werden ergänzt (`7` → `007`), erlaubt sind 0–999. Ist die Nummer unbekannt oder ungültig, erscheint eine Warnung im Vorgang. |
| `@@pi…@@` | `@@pi0NR41261010@@` | **ERP-Belegindex (BELNDX).** Nur Belege mit Belegindex bekommen eine **ERP-Rückmeldung**: Jeder Statuswechsel (3-stelliger Statuscode, z. B. 100 = versendet, 110 = über Outlook versendet) wird per WEBWARE-Webservice `BELEG.PUT` in die eingestellten `BEL_…`-Felder geschrieben (Einstellungen → ERP-Rückmeldung). Der Belegindex hat immer 11 Zeichen: Zeitraum (0–9, A–Z, a–z) + Archiv (A = Archiv, N = Normal) + Belegart (z. B. R = WA-Rechnung) + 8-stellige Belegnummer. Die Belegnummer steht rechtsbündig: Kürzere oder falsch ausgerichtete Nummern werden automatisch korrigiert (`0NR1234`, `0NR1234    ` → `0NR    1234`), Vornullen bleiben. Sonst wird der Index unverändert (inkl. Groß-/Kleinschreibung) ans ERP übergeben. Ein ungültiger Wert ergibt eine Warnung. Auch im MAPI-Mailtext möglich (wird vor dem Versand entfernt). Gleichwertig: `@@xi ERP_VOUCHER_INDEX=0NR41261010@@`. |
| `@@pu…@@` | `@@puFIRMA\mmuster@@`, `@@pum.muster@firma.at@@` | **Wer hat gedruckt** (Windows- oder ERP-Name). Wird mit „Windows-Benutzer“, Anmeldename und E-Mail verglichen, auch ohne Domäne. `@@pb@@` hat Vorrang. |
| `@@pvName=Wert@@` | `@@pvAnrede=Sehr geehrter Herr Maier@@` | **Eigener Platzhalter** für Betreff, Mailtext, Signatur, Fußzeile und Dateiname. Im Text als `{Anrede}` verwenden. Beliebig viele Namen möglich. |
| `@@pt…@@` | `@@ptFirma B@@` | **Mail-Vorlage** (HTML im CI) wählen. Ohne `@@pt@@` wird die Vorlage in dieser Reihenfolge gesucht: Briefpapier → Absender → Standardvorlage. |
| `@@pf…@@` | `@@pfservice@firma.at@@` | **Absenderadresse** dieses Belegs. Wählt auch die Mail-Vorlage dieses Absenders. Bei Microsoft 365 muss die App für diese Adresse senden dürfen. |
| `@@pg…@@` | `@@pg@@`, `@@pgPDFMAILER Rechnung@@`, `@@pg0@@` | **Weiterleitung an gotomaxx PDFMAILER.** Der Beleg wird als echter Druckauftrag an einen gotomaxx-Drucker geschickt (`@@pg@@` = Standarddrucker aus Einstellungen → Drucker & Eingang, `@@pgName@@` = dieser Drucker, `@@pg0@@` = nicht weiterleiten, auch wenn „jeden Beleg weiterleiten“ eingestellt ist). Die Steuerzeichen werden auf derselben Seite neu als Text gedruckt – ohne die Portal-Steuerzeichen `@@pb@@ @@pi@@ @@pu@@ @@pv@@ @@pt@@ @@pf@@ @@pg@@ @@pa@@ @@pp@@` und `@@xi ERP_VOUCHER_INDEX=…@@`, alle anderen unverändert. Das **Trennzeichen** ist einstellbar (Einstellungen → gotomaxx): `@@`, z. B. `$$`, oder „automatisch“ aus dem gotomaxx-Profil des Zieldruckers beim druckenden Benutzer (`commandDelimiter` in `%APPDATA%\gotomaxx\pdfmailer6\profiles\<ID>.cfg`; ohne Profil `@@`). Gedruckt wird in der Windows-Sitzung des Benutzers am Server (sonst vom Dienst). Status 140 = übergeben, 840 = fehlgeschlagen. |
| `@@pa…@@` | `@@pa@@`, `@@paRechnungen\{Jahr}@@`, `@@paK:\Belege@@`, `@@pa0@@` | **Speichern über den Melder.** Der Melder des Benutzers öffnet „Speichern unter“ mit dem fertigen PDF (Briefpapier, E-Rechnung) im Ablageordner aus Einstellungen → Mailclient/Melder → „Speichern und Drucken über den Melder“, bei einem relativen Wert in diesem Unterordner, bei `X:\…` oder `\\server\…` genau dort. Platzhalter `{Jahr} {Monat} {Kunde} {Kundennummer} {Belegart} {Mandant}`. Läuft zusätzlich zu Mail, Outlook oder gotomaxx. Ausgeführt vom Melder auf dem Computer, an dem gedruckt wurde (am RDS-Server in der Sitzung), sonst vom nächsten Melder des Benutzers. Ergebnis im Verlauf. |
| `@@pp…@@` | `@@pp@@`, `@@ppKyocera Büro@@`, `@@pp0@@` | **Zusätzlich drucken über den Melder** auf dem Standarddrucker des PCs bzw. auf diesem Drucker. Gedruckt wird das fertige PDF. Sonst wie `@@pa@@`. |
| `@@ps…@@` | `@@psMartin – Firma A@@`, `@@ps@@` | **Signatur** dieses Belegs (Name einer eigenen oder globalen Signatur). `@@ps@@` bzw. `@@ps0@@` = ohne Signatur. Ohne `@@ps@@` gilt: Regel → eigene Signatur für Briefpapier/Absender/Standard → globale → Signatur der Mail-Vorlage. |
| `@@pw…@@` | `@@pwdirekt@@`, `@@pwwennok@@`, `@@pwfreigabe@@`, `@@pwoutlook@@`, `@@pwgotomaxx@@`, `@@pwkeine@@` | **Versandweg** – übersteuert die Regeln: direkt (automatisch versenden), wennok (automatisch nur, wenn der Beleg ohne Warnungen und Fehler ist – sonst wartet er auf Freigabe), nach Freigabe im Portal, über Outlook des Benutzers (Melder), an gotomaxx, keine Mail. |
| `@@pm…@@` | `@@pm10000@@` | **Mandant.** Übersteuert den aus dem druckenden Programm erkannten Mandanten (WebWARE: Parameter `/M:` von `wwa.exe`). Bestimmt je Mandant die ERP-Rückmeldung (eigene SecuredApp), den gotomaxx-Standarddrucker, zusätzliche Pfadumsetzungen und den Ablageordner (Einstellungen → Mandanten). Auch im MAPI-Mailtext möglich. Wird bei der Weiterleitung an gotomaxx entfernt. |

### Platzhalter in Texten (zu `@@pv@@`)

- **Eingebaut:** `{Belegart}` `{Belegnummer}` `{Belegdatum}` `{Kunde}` `{Kundennummer}` `{Betrag}` `{Waehrung}` `{Dateiname}` `{Absender}` `{Datum}`.
- **Ersatztext**, wenn kein Wert übergeben wurde: `{Anrede|Sehr geehrte Damen und Herren}`.
- **Platzhalter ohne Wert und ohne Ersatztext** werden leer ersetzt. Der Vorgang zeigt dann eine Warnung.
- **Im Mailtext:**
  - `**fett**` und `*kursiv*`
  - Zeilen mit `- ` werden zur Aufzählung.
  - Adressen und Links werden klickbar.

### Mail-Regeln und Signaturen

Unter Einstellungen → **Regeln & Signaturen** (global) und je Benutzer (Persönlich → **Meine Mail-Regeln**, für Admins auch in der Benutzer-Maske) legen Regeln Vorlage, Absender, Absendername, Antwort-an, Signatur, Versandweg und CC/BCC fest – abhängig von Belegart, Briefpapier, Absender, Empfänger, Kunde, Quelle, E-Rechnungsformat und Text. Eigene Regeln gelten vor globalen; je Aktion gewinnt die erste passende Regel. **Steuerzeichen haben immer Vorrang** (`@@pt@@`, `@@pf@@`, `@@ps@@`, `@@pw@@`, `@@pg@@`, `@@cc@@`/`@@bc@@`, `@@ml0@@`).

## 2. gotomaxx-Steuerzeichen mit zusätzlicher Funktion

| Steuerzeichen | wie gotomaxx | zusätzlich in Belegpost |
|---|---|---|
| `@@bp…@@` Briefpapier | Briefpapier hinterlegen | Mehrere Briefpapiere (je Firma) per Name. Eigene erste Seite und Folgeseiten. Ränder werden geprüft: Ragt der Inhalt hinein, gibt es eine Warnung, auf Wunsch wird er automatisch eingepasst. Versatz und Skalierung sind einstellbar. Das Briefpapier wählt auch die passende Mail-Vorlage. `@@bp@@` ohne Namen bedeutet ohne Briefpapier. |
| `@@ed…@@` Betreff | Betreff | Platzhalter `{…}` und automatische Korrektur kaputter Umlaute (z. B. „Ã¼“ → „ü“). |
| `@@sa…@@` Mailtext (Zeilen) | Text zeilenweise | Platzhalter, Umlaut-Korrektur, Ausgabe als HTML-Mail im CI (Logo, Farben, Signatur, Fußzeile). |
| `@@mb…@@` Mailtext aus Datei | Textdatei | Datei darf ANSI oder UTF-8 sein (wird erkannt). Dazu Platzhalter, Umlaut-Korrektur und HTML-Mail. Pfade wie `K:\…` werden über die **Pfadzuordnung** auf den Server umgesetzt. |
| `@@mf…@@` / `@@fn…@@` Dateiname | Dateiname des Anhangs | Platzhalter, Umlaut-Korrektur, unzulässige Zeichen werden ersetzt. |
| `@@to@@` / `@@aa@@` / `@@cc@@` / `@@bc@@` | Empfänger | Adressen werden geprüft. Ohne Empfänger wird, falls vorhanden, die Käufer-E-Mail aus `@@xi@@` genommen. |
| `@@sp…@@` SMTP-Profil | Versand über dieses SMTP-Profil | Ist der Wert eine Mailadresse, wird sie zum **Absender** (nach `@@pf@@`, vor Regeln und Benutzer-Einstellung). Bei der Weiterleitung an gotomaxx bleibt es unverändert. |
| `@@ab@@` `@@ac@@` `@@ad@@` `@@bn@@` | in der gotomaxx-Doku nicht beschrieben (druckt das WebWARE-Standardformular) | ohne Wirkung, keine Warnung; Werte stehen als Hinweis im Vorgang |
| `@@ma…@@` / `@@mc…@@` Anlagen | Datei / Ordner anhängen | Pfadzuordnung `K:\` → `\\server\…`. Fehlende Dateien erscheinen als Warnung. |
| `@@ml0@@` / `@@md@@` | ohne Mail / ohne Beleg-Anhang | Werden im Portal angezeigt und können dort geändert werden. |
| `@@di…@@` PDF-Eigenschaften | Titel, Autor, Thema, Stichwörter | Werden ins Versand-PDF geschrieben. |
| `@@xt…@@` E-Rechnungsformat | Format der E-Rechnung | Ein Format, das Belegpost (noch) nicht erzeugen kann, wird nicht stillschweigend ersetzt. Der Vorgang bekommt den Status **Fehler** mit Meldung im Portal und wird nicht automatisch versendet. |
| `@@xi…@@` / `@@xr…@@` / `@@<<\|…@@` | Rechnungsdaten | Prüfung im Portal: Pflichtfelder, Summen werden nachgerechnet, Einheiten werden geprüft (ERP-Text → UN/ECE). Kopf, Positionen und Summen sind im Portal sichtbar. |

## 3. Gelesen, aber noch ohne Wirkung

Diese Steuerzeichen werden schon erkannt und aus dem PDF entfernt. Ihre Wirkung kommt mit dem Druckdienst bzw. mit ZUGFeRD:

- `@@af@@`: PDF anhängen
- `@@ef@@`: Datei einbetten
- `@@rpc@@`: Seite entfernen
- `@@nd@@`: Druckauftrag teilen
- `@@cf@@`
- `@@ca@@`: PDF/A
- `@@ip@@`: Grafik/QR-Code

Ohne Bedeutung für Belegpost und deshalb ignoriert: `@@mo@@`, `@@ms@@`, `@@mp@@`, `@@od@@`, `@@of@@`, `@@jc@@`, `@@je@@`.

## 4. Beispiel: ERP-Druckblock

```
@@pb007@@
@@pi{Belegindex}@@
@@mfRechnung_{Belegnummer}@@
@@aakunde@firma.at@@
@@edRechnung {Belegnummer} vom {Belegdatum}@@
@@pvAnrede=Sehr geehrter Herr Maier@@
@@mbK:\WebWARE5\APP\PDFMailer\DAT00004\Rechnung.txt@@
@@bpFirma A@@
```

In `Rechnung.txt` kann dann `{Anrede|Sehr geehrte Damen und Herren},` stehen.
