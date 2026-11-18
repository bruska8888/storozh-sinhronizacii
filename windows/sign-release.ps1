# Signs a release exe for the auto-updater: writes <exe>.sig (base64 RSA-SHA256, PKCS#1 v1.5).
# The private key is the "notes" of the Bitwarden item "Sync Watchdog — ключ подписи релизов" (RSA XML).
# Usage:
#   powershell -ExecutionPolicy Bypass -File windows\sign-release.ps1 -Exe SyncWatchdog-1.5.exe -KeyFile <private.xml or Bitwarden import .json>
#   powershell -ExecutionPolicy Bypass -File windows\sign-release.ps1 -Exe SyncWatchdog-1.5.exe -FromBitwarden   (needs unlocked bw CLI)
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$KeyFile,
    [switch]$FromBitwarden
)
$ErrorActionPreference = 'Stop'
if ($FromBitwarden) {
    # bw must be unlocked (BW_SESSION set). Some setups keep bw data in a separate folder; use it if present.
    $alt = Join-Path $env:USERPROFILE '.bitwarden-cli-mcp'
    if (-not $env:BITWARDENCLI_APPDATA_DIR -and (Test-Path $alt)) { $env:BITWARDENCLI_APPDATA_DIR = $alt }
    $items = bw list items --search 'Sync Watchdog' | ConvertFrom-Json
    $item = $items | Where-Object { $_.notes -like '<RSAKeyValue>*<D>*' } | Select-Object -First 1
    if (-not $item) { throw 'Bitwarden item with the signing key not found (is bw unlocked?)' }
    $xml = $item.notes
} elseif ($KeyFile) {
    $text = [IO.File]::ReadAllText($KeyFile)
    $xml = if ($text.TrimStart().StartsWith('{')) { (($text | ConvertFrom-Json).items | Select-Object -First 1).notes } else { $text }
} else { throw 'Pass -KeyFile or -FromBitwarden' }

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$rsa.FromXmlString($xml)
$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = $sha.ComputeHash([IO.File]::ReadAllBytes($Exe))
$sig = $rsa.SignHash($hash, [System.Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
[IO.File]::WriteAllText("$Exe.sig", [Convert]::ToBase64String($sig))
# Self-check with the public half.
$pub = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$pub.FromXmlString($rsa.ToXmlString($false))
if (-not $pub.VerifyHash($hash, [System.Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'), $sig)) { throw 'self-check failed' }
Write-Output "Signed: $Exe.sig"
