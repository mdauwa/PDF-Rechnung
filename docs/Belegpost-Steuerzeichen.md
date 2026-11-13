# Belegpost: eigene und erweiterte Steuerzeichen

Stand 13.11.2026. Alle Steuerzeichen werden wie bei gotomaxx als `@@xxWert@@` gedruckt und aus dem Versand-PDF entfernt. Groß-/Kleinschreibung des Kürzels ist egal. Die Position im Ausdruck spielt keine Rolle: Klein, gedreht oder auf jeder Seite gedruckt funktioniert alles.

## 1. Neue Steuerzeichen (gibt es in gotomaxx nicht)

| Steuerzeichen | Beispiel | Wirkung |
|---|---|---|
| `@@pb…@@` | `@@pb007@@`, `@@pb7@@` | **ERP-Bedienernummer.** Der Beleg gehört dem Portal-Benutzer mit dieser Nummer (Einstellungen → Benutzer → ERP-Nr.). Er erscheint dort unter „Meine Belege“ und öffnet sich über dessen **Melder** am PC. Vornullen werden ergänzt (`7` → `007`), erlaubt sind 0–999. Ist die Nummer unbekannt oder ungültig, erscheint eine Warnung im Vorgang. |
| `@@pu…@@` | `@@puFIRMA\mmuster@@`, `@@pum.muster@firma.at@@` | **Wer hat gedruckt** (Windows- oder ERP-Name). Wird mit „Windows-Benutzer“, Anmeldename und E-Mail verglichen, auch ohne Domäne. `@@pb@@` hat Vorrang. |
| `@@pvName=Wert@@` | `@@pvAnrede=Sehr geehrter Herr Maier@@` | **Eigener Platzhalter** für Betreff, Mailtext, Signatur, Fußzeile und Dateiname. Im Text als `{Anrede}` verwenden. Beliebig viele Namen möglich. |
| `@@pt…@@` | `@@ptFirma B@@` | **Mail-Vorlage** (HTML im CI) wählen. Ohne `@@pt@@` wird die Vorlage in dieser Reihenfolge gesucht: Briefpapier → Absender → Standardvorlage. |
| `@@pf…@@` | `@@pfservice@firma.at@@` | **Absenderadresse** dieses Belegs. Wählt auch die Mail-Vorlage dieses Absenders. Bei Microsoft 365 muss die App für diese Adresse senden dürfen. |

### Platzhalter in Texten (zu `@@pv@@`)

- **Eingebaut:** `{Belegart}` `{Belegnummer}` `{Belegdatum}` `{Kunde}` `{Kundennummer}` `{Betrag}` `{Waehrung}` `{Dateiname}` `{Absender}` `{Datum}`.
- **Ersatztext**, wenn kein Wert übergeben wurde: `{Anrede|Sehr geehrte Damen und Herren}`.
- **Platzhalter ohne Wert und ohne Ersatztext** werden leer ersetzt. Der Vorgang zeigt dann eine Warnung.
- **Im Mailtext:**
  - `**fett**` und `*kursiv*`
  - Zeilen mit `- ` werden zur Aufzählung.
  - Adressen und Links werden klickbar.

## 2. gotomaxx-Steuerzeichen mit zusätzlicher Funktion

| Steuerzeichen | wie gotomaxx | zusätzlich in Belegpost |
|---|---|---|
| `@@bp…@@` Briefpapier | Briefpapier hinterlegen | Mehrere Briefpapiere (je Firma) per Name. Eigene erste Seite und Folgeseiten. Ränder werden geprüft: Ragt der Inhalt hinein, gibt es eine Warnung, auf Wunsch wird er automatisch eingepasst. Versatz und Skalierung sind einstellbar. Das Briefpapier wählt auch die passende Mail-Vorlage. `@@bp@@` ohne Namen bedeutet ohne Briefpapier. |
| `@@ed…@@` Betreff | Betreff | Platzhalter `{…}` und automatische Korrektur kaputter Umlaute (z. B. „Ã¼“ → „ü“). |
| `@@sa…@@` Mailtext (Zeilen) | Text zeilenweise | Platzhalter, Umlaut-Korrektur, Ausgabe als HTML-Mail im CI (Logo, Farben, Signatur, Fußzeile). |
| `@@mb…@@` Mailtext aus Datei | Textdatei | Datei darf ANSI oder UTF-8 sein (wird erkannt). Dazu Platzhalter, Umlaut-Korrektur und HTML-Mail. Pfade wie `K:\…` werden über die **Pfadzuordnung** auf den Server umgesetzt. |
| `@@mf…@@` / `@@fn…@@` Dateiname | Dateiname des Anhangs | Platzhalter, Umlaut-Korrektur, unzulässige Zeichen werden ersetzt. |
| `@@to@@` / `@@aa@@` / `@@cc@@` / `@@bc@@` | Empfänger | Adressen werden geprüft. Ohne Empfänger wird, falls vorhanden, die Käufer-E-Mail aus `@@xi@@` genommen. |
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
@@mfRechnung_{Belegnummer}@@
@@aakunde@firma.at@@
@@edRechnung {Belegnummer} vom {Belegdatum}@@
@@pvAnrede=Sehr geehrter Herr Maier@@
@@mbK:\WebWARE5\APP\PDFMailer\DAT00004\Rechnung.txt@@
@@bpFirma A@@
```

In `Rechnung.txt` kann dann `{Anrede|Sehr geehrte Damen und Herren},` stehen.
