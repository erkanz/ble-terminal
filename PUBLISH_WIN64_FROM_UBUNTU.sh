#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

chmod +x scripts/prepare-icon.sh
scripts/prepare-icon.sh

LOCAL_DOTNET_DIR="$(pwd)/.dotnet-ms"
LOCAL_DOTNET="$LOCAL_DOTNET_DIR/dotnet"

has_windowsdesktop_sdk() {
  local dn="$1"
  [ -x "$dn" ] || return 1
  local line version root sdkdir
  line="$($dn --list-sdks 2>/dev/null | tail -n 1 || true)"
  [ -n "$line" ] || return 1
  version="${line%% *}"
  root="${line#*[}"
  root="${root%]}"
  sdkdir="$root/$version/Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.targets"
  [ -f "$sdkdir" ]
}

install_microsoft_dotnet() {
  echo "===== MICROSOFT .NET 8 SDK BOOTSTRAP ====="
  mkdir -p "$LOCAL_DOTNET_DIR"
  local installer="$(pwd)/.dotnet-install.sh"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  elif command -v wget >/dev/null 2>&1; then
    wget -q https://dot.net/v1/dotnet-install.sh -O "$installer"
  else
    echo "ERROR: curl veya wget gerekli." >&2
    exit 1
  fi
  chmod +x "$installer"
  bash "$installer" --channel 8.0 --install-dir "$LOCAL_DOTNET_DIR" --no-path
}

DOTNET_EXE=""
if has_windowsdesktop_sdk "$LOCAL_DOTNET"; then
  DOTNET_EXE="$LOCAL_DOTNET"
elif command -v dotnet >/dev/null 2>&1 && has_windowsdesktop_sdk "$(command -v dotnet)"; then
  DOTNET_EXE="$(command -v dotnet)"
else
  install_microsoft_dotnet
  DOTNET_EXE="$LOCAL_DOTNET"
fi

export DOTNET_ROOT="$(dirname "$DOTNET_EXE")"
export PATH="$DOTNET_ROOT:$PATH"

OUT="$(pwd)/publish/win-x64"
rm -rf "$OUT" bin obj
mkdir -p "$OUT"

echo "===== STATIC REGRESSION CHECK ====="
python3 tests/STATIC_REGRESSION_CHECK.py

echo "===== RESTORE ====="
"$DOTNET_EXE" restore BLESerialTerminal.csproj -r win-x64 -p:EnableWindowsTargeting=true

echo "===== PUBLISH: SELF-CONTAINED SINGLE EXE ====="
"$DOTNET_EXE" publish BLESerialTerminal.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  --no-restore \
  -p:EnableWindowsTargeting=true \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$OUT"

EXE="$OUT/BLESerialTerminal.exe"
if [ ! -f "$EXE" ]; then
  echo "ERROR: EXE olusmadi: $EXE" >&2
  exit 1
fi

if command -v python3 >/dev/null 2>&1; then
python3 - "$EXE" <<'PY'
import struct, sys
p=sys.argv[1]
with open(p,'r+b') as f:
    if f.read(2) != b'MZ': raise SystemExit('Invalid PE')
    f.seek(0x3c); pe=struct.unpack('<I',f.read(4))[0]
    f.seek(pe)
    if f.read(4) != b'PE\0\0': raise SystemExit('Invalid PE signature')
    off=pe+4+20+0x44
    f.seek(off); old=struct.unpack('<H',f.read(2))[0]
    f.seek(off); f.write(struct.pack('<H',2))
print(f'PE subsystem: {old} -> 2 (Windows GUI)')
PY
fi

# Remove any nonessential publish artifacts if the SDK emitted them.
find "$OUT" -maxdepth 1 -type f ! -name 'BLESerialTerminal.exe' -delete

SIZE=$(du -h "$EXE" | awk '{print $1}')
echo
echo "===== DONE ====="
echo "SELF-CONTAINED SINGLE EXE: $EXE"
echo "SIZE: $SIZE"
echo "Windows 10/11 x64 üzerinde .NET kurulumu gerekmez."
