#!/bin/zsh
# Сборка и установка Сторожа синхронизации.
# Ставит ~/Applications/Сторож синхронизации.app и launchd-задачу (раз в 10 мин).
# После ПЕРВОЙ установки (и после каждой пересборки) включить приложение в
# Настройки → Конфиденциальность и безопасность → Универсальный доступ.
set -e
cd "${0:A:h}"
APP="$HOME/Applications/Сторож синхронизации.app"
LABEL=com.anton.storozh-sinhronizacii
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
export DEVELOPER_DIR=/Library/Developer/CommandLineTools   # без лицензии Xcode

mkdir -p build
swiftc -O -o build/storozh Sources/main.swift -framework AppKit -framework ApplicationServices

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp -R Resources/ru.lproj Resources/en.lproj "$APP/Contents/Resources/"
cp build/storozh "$APP/Contents/MacOS/storozh"
# Подпись: сертификат «Apple Development» (если есть) — тогда разрешение «Универсальный доступ»
# переживает пересборку. Без него — временная подпись, после каждой пересборки доступ включать заново.
SIGN=$(security find-identity -v -p codesigning | awk '/Apple Development/ {print $2; exit}')
codesign --force --deep --options runtime --timestamp=none --sign "${SIGN:--}" --identifier "$LABEL" "$APP"
codesign -dr - "$APP" 2>&1 | grep designated

cat > "$PLIST" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>Label</key><string>$LABEL</string>
	<key>ProgramArguments</key><array><string>$APP/Contents/MacOS/storozh</string></array>
	<key>RunAtLoad</key><true/>
	<key>StartInterval</key><integer>600</integer>
	<key>ProcessType</key><string>Background</string>
	<key>LowPriorityIO</key><true/>
</dict>
</plist>
PL
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"
# Добавить себя в список «Универсальный доступ» (macOS покажет окно с кнопкой «Открыть Системные настройки»)
[ "$1" = "--no-ask" ] || open "$APP" --args --ask
echo "Установлено: $APP"
