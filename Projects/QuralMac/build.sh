#!/bin/bash
# Собрать Qural.app для Apple Silicon: build/Qural.app и build/Qural-mac.zip.
# Нужны Xcode или Command Line Tools (xcode-select --install). Подпись — ad-hoc (без аккаунта Apple Developer):
# после каждой новой сборки macOS может заново спросить разрешение на снимки экрана.
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release --arch arm64
BIN="$(swift build -c release --arch arm64 --show-bin-path)/Qural"

APP=build/Qural.app
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Qural"
cp Info.plist "$APP/Contents/Info.plist"
iconutil -c icns Icon.iconset -o "$APP/Contents/Resources/AppIcon.icns"   # иконка: tools/make-icon.ps1
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --verbose "$APP"

(cd build && ditto -c -k --keepParent Qural.app Qural-mac.zip)
echo "Готово: $(pwd)/$APP"
