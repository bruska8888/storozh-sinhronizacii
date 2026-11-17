#!/bin/zsh
# Собирает build/Sync Watchdog.app и dist/SyncWatchdog-<версия>.dmg
# Builds the app and a .dmg for GitHub Releases.
set -e
cd "${0:A:h}"
export DEVELOPER_DIR=/Library/Developer/CommandLineTools   # без лицензии Xcode / no Xcode license needed
LABEL=com.anton.storozh-sinhronizacii
NAME="Sync Watchdog"
VER=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Resources/Info.plist)
APP="build/$NAME.app"

rm -rf build && mkdir -p build dist
swiftc -O -target arm64-apple-macos13 -o build/bin-arm64 Sources/main.swift
swiftc -O -target x86_64-apple-macos13 -o build/bin-x86_64 Sources/main.swift
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Library/LaunchAgents"
lipo -create -output "$APP/Contents/MacOS/SyncWatchdog" build/bin-arm64 build/bin-x86_64
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp -R Resources/ru.lproj Resources/en.lproj "$APP/Contents/Resources/"
cp "Resources/$LABEL.plist" "$APP/Contents/Library/LaunchAgents/"

# Подпись «Apple Development», если есть: разрешение переживает обновления. Иначе — временная.
SIGN=$(security find-identity -v -p codesigning | awk '/Apple Development/ {print $2; exit}')
codesign --force --deep --options runtime --timestamp=none --sign "${SIGN:--}" --identifier "$LABEL" "$APP"
codesign --verify --deep --strict "$APP"

# .dmg: приложение + ярлык на «Программы» — перетащить и готово
STAGE=build/dmg && mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
DMG="dist/SyncWatchdog-$VER.dmg"
rm -f "$DMG"
hdiutil create -quiet -volname "$NAME $VER" -srcfolder "$STAGE" -format UDZO "$DMG"
echo "Готово / Done: $APP  |  $DMG"
