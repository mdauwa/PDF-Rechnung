#!/bin/sh
# Belegpost Melder für Linux installieren (ohne Administratorrechte, nur für diesen Benutzer)
set -e
here="$(cd "$(dirname "$0")" && pwd)"
bin="$HOME/.local/bin"
mkdir -p "$bin" "$HOME/.local/share/applications"
install -m 755 "$here/belegpost-melder" "$bin/belegpost-melder"
cat > "$HOME/.local/share/applications/belegpost-melder.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Belegpost Melder
Comment=Meldet neue Belege aus Belegpost
Exec=$bin/belegpost-melder
Icon=$HOME/.local/share/belegpost/melder.png
Terminal=false
Categories=Office;
DESKTOP
for t in xdg-open xdg-email notify-send lp; do
  command -v "$t" >/dev/null 2>&1 || echo "Hinweis: $t fehlt (Pakete xdg-utils, libnotify-bin, cups-client)."
done
echo "Installiert nach $bin/belegpost-melder – der Melder startet jetzt."
nohup "$bin/belegpost-melder" >/dev/null 2>&1 &
