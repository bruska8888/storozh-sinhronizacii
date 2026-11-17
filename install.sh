#!/bin/zsh
# Установка из исходника (для тех, кто не качает .dmg): собрать, положить в «Программы», открыть.
# Install from source: build, copy to /Applications, open (the app installs itself on first run).
set -e
cd "${0:A:h}"
./build.sh
APP="/Applications/Sync Watchdog.app"
rm -rf "$APP"
cp -R "build/Sync Watchdog.app" "$APP"
open "$APP"
