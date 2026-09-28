# DJI-RTMP-OBS-Windows installer (run as Administrator)
# Downloads pinned MediaMTX, verifies SHA-256 against the official release, adds firewall rule.
# If github.com download is unstable on your network, enable your proxy/accelerator first.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$ver  = 'v1.21.1'
$file = "mediamtx_${ver}_windows_amd64.zip"
$base = "https://github.com/bluenviron/mediamtx/releases/download/$ver"
$zip  = Join-Path $root $file
$dest = Join-Path $root 'mediamtx'
$exe  = Join-Path $dest 'mediamtx.exe'

if (Test-Path $exe) {
    Write-Host "mediamtx.exe already present, skip download."
} else {
    Write-Host "Downloading MediaMTX $ver ..."
    Invoke-WebRequest "$base/$file" -OutFile $zip
    Invoke-WebRequest "$base/checksums.sha256" -OutFile (Join-Path $root 'checksums.sha256')

    $expect = (Select-String -Path (Join-Path $root 'checksums.sha256') -SimpleMatch $file).Line.Split(' ')[0].ToLower()
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expect) {
        Remove-Item $zip -Force
        throw "SHA-256 mismatch! expected $expect but got $actual"
    }
    Write-Host "SHA-256 OK."

    Expand-Archive $zip -DestinationPath $dest -Force
    Remove-Item (Join-Path $root 'checksums.sha256') -Force
}

$rule = Get-NetFirewallRule -DisplayName 'DJI-RTMP-OBS MediaMTX' -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host "Firewall rule exists."
} else {
    New-NetFirewallRule -DisplayName 'DJI-RTMP-OBS MediaMTX' -Direction Inbound -Action Allow -Program $exe | Out-Null
    Write-Host "Firewall rule added."
}
Write-Host "Done. Double-click TrayApp.exe to start."
