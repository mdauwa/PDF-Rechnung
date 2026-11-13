# Belegpost Melder für macOS

Diese Mac-App sitzt in der Menüleiste. Sie entspricht dem Windows-Melder (`src/BelegpostMelder`) und nutzt dieselbe Schnittstelle des Portals:

- **Koppeln:** über den Link aus dem Portal („Im Melder übernehmen“, Schema `belegpost-melder:`) oder per Code. Der Zugangsschlüssel liegt im Schlüsselbund.
- **Neue eigene Belege:** Je nach Einstellung öffnet die App das Portal im Browser, öffnet die Mail als Entwurf in Outlook oder zeigt nur eine Mitteilung mit „Öffnen“ und „In Outlook“.
- **Outlook:**
  - Das klassische Outlook für Mac wird per AppleScript gesteuert. Der Entwurf enthält Empfänger, Betreff, den HTML-Text der CI-Vorlage und alle Anhänge.
  - Beim ersten Mal fragt macOS einmal, ob der Melder Outlook steuern darf. Die Antwort lässt sich unter Systemeinstellungen → Datenschutz & Sicherheit → Automation ändern.
  - Im neuen Outlook für Mac öffnet die App die Mail als `.eml`-Entwurf.
  - Ist kein Outlook installiert, nimmt sie Apple Mail. Dort ist der Text schlicht, die Anhänge sind dabei.
- **Start bei der Anmeldung:** über SMAppService (macOS 13 oder neuer).

## Bauen (auf einem Mac)

```bash
xcode-select --install          # einmalig, falls noch keine Entwicklerwerkzeuge da sind
cd src/BelegpostMelderMac
./build.sh                      # → build/Belegpost Melder.app und build/Belegpost-Melder-macOS.zip
```

Die ZIP-Datei neben `Belegpost.exe` auf den Server legen. Das Portal bietet sie Mac-Benutzern unter „Melder & Hinweise“ zum Download an.

**Signatur:** Ohne Apple-Entwicklerkonto ist die App nur ad-hoc signiert. Beim ersten Start öffnest du sie deshalb mit Rechtsklick → Öffnen. Mit einem Konto geht es so:

```bash
SIGN_ID="Developer ID Application: Firma (TEAMID)" NOTARY_PROFILE=belegpost ./build.sh
```

Das Profil legst du vorher an mit `xcrun notarytool store-credentials belegpost …`.
