#!/bin/zsh
# Собирает dist/SyncWatchdog-<версия>.dmg и .zip (временные файлы за собой удаляет)
# Builds the app and a .dmg for GitHub Releases.
set -e
cd "${0:A:h}"
export DEVELOPER_DIR=/Library/Developer/CommandLineTools   # без лицензии Xcode / no Xcode license needed
LABEL=com.anton.storozh-sinhronizacii
NAME="Sync Watchdog"
VER=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Resources/Info.plist)
# Промежуточные файлы — во временной папке, удаляется в конце (и при ошибке):
# в проекте не остаётся лишних копий приложения, которые Spotlight показывает как ещё одного Сторожа.
# Intermediate files go to a temp dir that is always removed — no stray app copies left behind.
rm -rf build   # остатки старых сборок / leftovers from older builds
WORK=$(mktemp -d "${TMPDIR:-/tmp}/syncwatchdog-build.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
APP="$WORK/$NAME.app"

mkdir -p dist
swiftc -O -target arm64-apple-macos13 -o "$WORK/bin-arm64" Sources/main.swift Sources/update.swift
# Intel: CLT печатает «libswiftCompatibilityPacks.a … missing arch x86_64» — безвредно,
# программа ни одного символа из этой библиотеки не использует (проверено nm, 17.11.2026).
swiftc -O -target x86_64-apple-macos13 -o "$WORK/bin-x86_64" Sources/main.swift Sources/update.swift 2> >(grep -v libswiftCompatibilityPacks >&2)
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Library/LaunchAgents"
lipo -create -output "$APP/Contents/MacOS/SyncWatchdog" "$WORK/bin-arm64" "$WORK/bin-x86_64"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/"
cp -R Resources/ru.lproj Resources/en.lproj "$APP/Contents/Resources/"
cp "Resources/$LABEL.plist" "$APP/Contents/Library/LaunchAgents/"

# Подпись своим сертификатом «Sync Watchdog» (самоподписанный, в связке ключей «Вход»):
# разрешение «Универсальный доступ» переживает обновления, а в подписи нет личных данных.
# Нет такого сертификата — временная подпись (доступ придётся включать после каждой пересборки).
# Не подписывать сертификатом «Apple Development»: в нём почта владельца.
SIGN=$(security find-identity -p codesigning | awk '/"Sync Watchdog"/ {print $2; exit}')
codesign --force --deep --options runtime --timestamp=none --sign "${SIGN:--}" --identifier "$LABEL" "$APP"
if codesign -dvv "$APP" 2>&1 | grep -qi "@"; then echo "СТОП: в подписи есть адрес почты" >&2; exit 1; fi
codesign --verify --deep --strict "$APP"

# .dmg: приложение + ярлык на «Программы» — перетащить и готово
STAGE="$WORK/dmg" && mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
DMG="dist/SyncWatchdog-$VER.dmg"
rm -f "$DMG"
hdiutil create -quiet -volname "$NAME $VER" -srcfolder "$STAGE" -format UDZO "$DMG"
# .zip для автообновления (подпись — sign-release-mac.py) / zip for the auto-updater
ZIP="dist/SyncWatchdog-mac-$VER.zip"
rm -f "$ZIP" "$ZIP.sig"
ditto -c -k --keepParent "$APP" "$ZIP"
echo "Готово / Done: $DMG  |  $ZIP"
