#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/Assets/BLESerialTerminal.ico.b64"
DST="$ROOT/Assets/BLESerialTerminal.ico"
[ -f "$SRC" ] || { echo "Missing icon source: $SRC" >&2; exit 1; }
base64 -d "$SRC" > "$DST"
echo "Prepared application icon: $DST"
