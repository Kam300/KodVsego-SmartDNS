#!/usr/bin/env bash
# Сборка HB AI · Smart DNS для macOS: swift build → .app → zip. Запускать на Mac или в GitHub Actions.
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release 2>&1 | tail -5

APP="build/HB AI Smart DNS.app"
rm -rf build && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/HBAISmartDNS "$APP/Contents/MacOS/"
cp Info.plist "$APP/Contents/"
cp res/*.ttf res/OFL-*.txt ../LICENSE "$APP/Contents/Resources/"

# иконка из res/icon.png
ICONSET="build/app.iconset"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s res/icon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2)); sips -z $d $d res/icon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/app.icns"

# подпись ad hoc: без аккаунта разработчика Apple. При первом запуске: правый клик → Открыть.
codesign --force --deep -s - "$APP"
ditto -c -k --keepParent "$APP" "build/HB_AI_Smart_DNS_mac.zip"
ls -la build/HB_AI_Smart_DNS_mac.zip
shasum -a 256 build/HB_AI_Smart_DNS_mac.zip
