$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'Assets/BLESerialTerminal.ico.b64'
$dst = Join-Path $root 'Assets/BLESerialTerminal.ico'
if (-not (Test-Path $src)) { throw "Missing icon source: $src" }
$raw = (Get-Content -Raw $src).Trim()
[IO.File]::WriteAllBytes($dst, [Convert]::FromBase64String($raw))
Write-Host "Prepared application icon: $dst"
