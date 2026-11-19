#!/bin/zsh
# Установка из исходника (для тех, кто не качает .dmg): собрать, положить в «Программы», открыть.
# Install from source: build, copy to /Applications, open (the app installs itself on first run).
set -e
cd "${0:A:h}"
./build.sh
VER=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Resources/Info.plist)
APP="/Applications/Sync Watchdog.app"
rm -rf "$APP"
ditto -x -k "dist/SyncWatchdog-mac-$VER.zip" /Applications
open "$APP"
