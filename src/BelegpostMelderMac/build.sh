#!/bin/bash
# Baut „Belegpost Melder.app“ und ein ZIP zum Verteilen.
#   ./build.sh                → ad-hoc signiert (für eigene Macs; beim ersten Start Rechtsklick → Öffnen)
#   SIGN_ID="Developer ID Application: Firma (TEAMID)" ./build.sh   → mit Apple-Zertifikat signiert
#   NOTARY_PROFILE=belegpost ./build.sh  → zusätzlich bei Apple notarisieren (xcrun notarytool store-credentials …)
# Voraussetzung: Xcode oder die Command Line Tools (xcode-select --install), macOS 13 oder neuer.
set -euo pipefail
cd "$(dirname "$0")"

APP="build/Belegpost Melder.app"
rm -rf build && mkdir -p build

echo "▸ Kompiliere (Apple Silicon + Intel) …"
swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/BelegpostMelder"

echo "▸ App-Paket …"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/BelegpostMelder"
cp Info.plist "$APP/Contents/Info.plist"

# Symbol aus dem Belegpost-Logo
ICON_SRC="../../docs/branding/Belegpost-256.png"; [ -f "$ICON_SRC" ] || ICON_SRC="branding/Belegpost-256.png"
if [ -f "$ICON_SRC" ]; then
  SET="build/AppIcon.iconset"; mkdir -p "$SET"
  for s in 16 32 64 128 256; do
    sips -z $s $s "$ICON_SRC" --out "$SET/icon_${s}x${s}.png" >/dev/null
    d=$((s*2)); sips -z $d $d "$ICON_SRC" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$SET" -o "$APP/Contents/Resources/AppIcon.icns"
fi

echo "▸ Signieren …"
if [ -n "${SIGN_ID:-}" ]; then
  codesign --force --options runtime --timestamp --sign "$SIGN_ID" "$APP"
else
  codesign --force --sign - "$APP"
fi

ZIP="build/Belegpost-Melder-macOS.zip"
ditto -c -k --keepParent "$APP" "$ZIP"

if [ -n "${NOTARY_PROFILE:-}" ]; then
  echo "▸ Notarisieren …"
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm "$ZIP" && ditto -c -k --keepParent "$APP" "$ZIP"
fi

echo "✓ Fertig: $APP"
echo "✓ Zum Verteilen: $ZIP  (neben Belegpost.exe legen – das Portal bietet es Mac-Benutzern zum Download an)"
