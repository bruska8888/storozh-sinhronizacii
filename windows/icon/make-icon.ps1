# icon.svg -> SyncWatchdog.ico (16..256 px, PNG frames) + preview.png. Uses Edge headless to rasterize the SVG.
# Run: powershell -ExecutionPolicy Bypass -File windows\icon\make-icon.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$work = Join-Path $env:TEMP 'syncwatchdog-icon'
New-Item -ItemType Directory -Force $work | Out-Null
$svg = [IO.File]::ReadAllText((Join-Path $here 'icon.svg'))
$html = "<!doctype html><html><head><style>html,body{margin:0;background:transparent}svg{display:block;width:1024px;height:1024px}</style></head><body>$svg</body></html>"
[IO.File]::WriteAllText((Join-Path $work 'icon.html'), $html)
$big = Join-Path $work 'icon-1024.png'
if (Test-Path $big) { Remove-Item $big }
& $edge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 --user-data-dir="$work\profile" `
    --window-size=1024,1024 --screenshot="$big" ([Uri](Join-Path $work 'icon.html')).AbsoluteUri | Out-Null
for ($i = 0; $i -lt 30 -and -not (Test-Path $big); $i++) { Start-Sleep -Milliseconds 300 }

Add-Type -AssemblyName System.Drawing
$src = [Drawing.Image]::FromFile($big)
$sizes = 256, 128, 64, 48, 32, 24, 16
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent); $g.DrawImage($src, 0, 0, $s, $s); $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@($s, $ms.ToArray())
    if ($s -eq 256) { $bmp.Save((Join-Path $here 'icon-256.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}
# ICO container with PNG-compressed frames (supported by Windows Vista and newer).
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]; $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $w.Write($f[1]) }
[IO.File]::WriteAllBytes((Join-Path $here 'SyncWatchdog.ico'), $out.ToArray())

# Preview: the icon on light and dark backgrounds at 256 / 48 / 32 / 24 / 16 px.
$pv = New-Object Drawing.Bitmap 760, 300
$g = [Drawing.Graphics]::FromImage($pv); $g.InterpolationMode = 'NearestNeighbor'
$g.FillRectangle([Drawing.Brushes]::White, 0, 0, 760, 150); $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(32,32,32))), 0, 150, 760, 150)
foreach ($row in 0, 1) {
    $x = 16; $y = 10 + $row * 150
    foreach ($f in $frames) {
        $s = $f[0]; if ($s -eq 128 -or $s -eq 64) { continue }
        $img = [Drawing.Image]::FromStream((New-Object IO.MemoryStream (,$f[1])))
        $draw = if ($s -eq 256) { 128 } else { $s }
        $g.DrawImage($img, $x, $y + (128 - $draw) / 2, $draw, $draw); $x += $draw + 40
    }
}
$g.Dispose(); $pv.Save((Join-Path $here 'preview.png'), [Drawing.Imaging.ImageFormat]::Png)
$src.Dispose()
Write-Output "ICO: $((Get-Item (Join-Path $here 'SyncWatchdog.ico')).Length) bytes"
