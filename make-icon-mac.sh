#!/bin/zsh
# Значок для Мака из того же рисунка, что и на Windows (windows/icon/icon.svg) → Resources/AppIcon.icns.
# Mac icon from the same artwork as Windows. Rounded square takes 824 of 1024 px, as in macOS app icons.
# Запускать только при смене рисунка; готовый AppIcon.icns лежит в репозитории.
set -e
cd "${0:A:h}"
export DEVELOPER_DIR=/Library/Developer/CommandLineTools
WORK=$(mktemp -d "${TMPDIR:-/tmp}/syncwatchdog-icon.XXXXXX")
trap 'rm -rf "${WORK:?}"' EXIT
# Шире поле вокруг квадрата (8..248 из 256): 240 → 824/1024
sed 's/viewBox="0 0 256 256" width="256" height="256"/viewBox="-21.126 -21.126 298.252 298.252" width="1024" height="1024"/' \
  windows/icon/icon.svg > "$WORK/icon.svg"
cat > "$WORK/render.swift" <<'SW'
import AppKit
let a = CommandLine.arguments
guard let img = NSImage(contentsOfFile: a[1]) else { exit(1) }
let set = a[2]
for (px, name) in [(16,"16x16"),(32,"16x16@2x"),(32,"32x32"),(64,"32x32@2x"),(128,"128x128"),(256,"128x128@2x"),
                   (256,"256x256"),(512,"256x256@2x"),(512,"512x512"),(1024,"512x512@2x")] {
  let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                             hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
  NSGraphicsContext.saveGraphicsState()
  NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
  NSGraphicsContext.current?.imageInterpolation = .high
  img.draw(in: NSRect(x: 0, y: 0, width: px, height: px))
  NSGraphicsContext.restoreGraphicsState()
  try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(set)/icon_\(name).png"))
}
SW
mkdir -p "$WORK/AppIcon.iconset"
swift "$WORK/render.swift" "$WORK/icon.svg" "$WORK/AppIcon.iconset"
iconutil -c icns -o Resources/AppIcon.icns "$WORK/AppIcon.iconset"
echo "Готово / Done: Resources/AppIcon.icns"
