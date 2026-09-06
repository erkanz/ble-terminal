#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GENERATOR="$ROOT/scripts/generate_icon.py"
DST="$ROOT/Assets/BLESerialTerminal.ico"
[ -f "$GENERATOR" ] || { echo "Missing icon generator: $GENERATOR" >&2; exit 1; }
python3 "$GENERATOR" "$DST"
[ -f "$DST" ] || { echo "Application icon generation failed" >&2; exit 1; }
echo "Prepared application icon: $DST"
