# BelegpostMapi.dll

Simple-MAPI-Mailclient „Belegpost“ für den ERP-/RDP-Server. Das ERP versendet über MAPI (`MAPISendMail`),
die DLL übergibt Empfänger, Betreff, Text und Anhänge über die Named Pipe `\\.\pipe\belegpost-mapi`
an den Dienst „Belegpost Portal“. Der Dienst öffnet die Mail je nach Einstellung über den Belegpost Melder
als Outlook-Entwurf auf dem PC des Benutzers, legt sie zur Prüfung ins Portal oder versendet sie direkt.

- Bauen (Linux/WSL mit mingw-w64): `./build.sh` → `out/BelegpostMapi-x64.dll`, `out/BelegpostMapi-x86.dll`
- Installieren: im Portal unter Einstellungen → MAPI-Mails (kopiert nach System32/SysWOW64 und registriert
  `HKLM\Software\Clients\Mail\Belegpost`). Für einzelne Benutzer: `reg add "HKCU\Software\Clients\Mail" /ve /d "Belegpost" /f`
- Ist der Dienst nicht erreichbar, wird die Mail in `%LOCALAPPDATA%\Belegpost\mapi-ausgang` abgelegt und beim nächsten Versand nachgeliefert.
