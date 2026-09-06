$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$generator = Join-Path $PSScriptRoot 'generate_icon.py'
$dst = Join-Path $root 'Assets/BLESerialTerminal.ico'
if (-not (Test-Path $generator)) { throw "Missing icon generator: $generator" }
python $generator $dst
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dst)) { throw "Application icon generation failed" }
Write-Host "Prepared application icon: $dst"
