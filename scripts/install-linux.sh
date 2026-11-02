#!/usr/bin/env bash
# Install a portable Linux release for the current user, without root.
set -euo pipefail

SOURCE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if [[ ! -f "$SOURCE_DIR/MemPulse.Linux" || ! -f "$SOURCE_DIR/MemPulse.png" ]]; then
  echo "Run this script from the extracted MemPulse Linux release." >&2
  exit 1
fi

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$DATA_HOME/MemPulse"
DESKTOP_DIR="$DATA_HOME/applications"
mkdir -p "$APP_DIR" "$DESKTOP_DIR"
install -m 755 "$SOURCE_DIR/MemPulse.Linux" "$APP_DIR/MemPulse.Linux"
install -m 644 "$SOURCE_DIR/MemPulse.png" "$APP_DIR/MemPulse.png"

cat > "$DESKTOP_DIR/mempulse.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=MemPulse
Comment=Linux memory and process monitor
Exec="$APP_DIR/MemPulse.Linux"
Icon=$APP_DIR/MemPulse.png
Terminal=false
Categories=System;Monitor;
DESKTOP
chmod 644 "$DESKTOP_DIR/mempulse.desktop"
echo "MemPulse installed: $APP_DIR"
echo "Desktop entry: $DESKTOP_DIR/mempulse.desktop"
