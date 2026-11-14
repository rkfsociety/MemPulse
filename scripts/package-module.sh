#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <win-x64|linux-x64|linux-arm64> [output-dir]" >&2
  exit 1
fi

RID="$1"
ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${2:-$ROOT_DIR/release-artifacts}"

VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT_DIR/src/Directory.Build.props" | head -n1 | tr -d '[:space:]')"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Invalid version in Directory.Build.props: '$VERSION'" >&2
  exit 1
fi

MANIFEST_SRC="$ROOT_DIR/src/MemPulse.PulseModule/module.json"
MANIFEST_VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$MANIFEST_SRC" | head -n1)"
if [[ "$MANIFEST_VERSION" != "$VERSION" ]]; then
  echo "module.json version ($MANIFEST_VERSION) != Directory.Build.props ($VERSION)" >&2
  exit 1
fi

mkdir -p "$OUT_DIR"
STAGE_MOD="$ROOT_DIR/obj/stage/module-$RID"
STAGE_HLP="$ROOT_DIR/obj/stage/helper-$RID"
STAGE_PKG="$ROOT_DIR/obj/stage/pack-$RID"
rm -rf "$STAGE_MOD" "$STAGE_HLP" "$STAGE_PKG"
mkdir -p "$STAGE_MOD" "$STAGE_HLP" "$STAGE_PKG"

dotnet build "$ROOT_DIR/src/MemPulse.PulseModule/MemPulse.PulseModule.csproj" -c Release -o "$STAGE_MOD"
dotnet publish "$ROOT_DIR/src/MemPulse.Helper/MemPulse.Helper.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false \
  -o "$STAGE_HLP"

cp "$STAGE_MOD/module.json" "$STAGE_PKG/module.json"
cp "$STAGE_MOD/MemPulse.PulseModule.dll" "$STAGE_PKG/MemPulse.PulseModule.dll"
cp "$STAGE_MOD/MemPulse.PulseModule.deps.json" "$STAGE_PKG/MemPulse.PulseModule.deps.json"
cp "$STAGE_MOD/MemPulse.Core.dll" "$STAGE_PKG/MemPulse.Core.dll"

if [[ "$RID" == "win-x64" ]]; then
  HELPER_BIN="MemPulse.Helper.exe"
else
  HELPER_BIN="MemPulse.Helper"
fi

if [[ ! -f "$STAGE_HLP/$HELPER_BIN" ]]; then
  echo "Missing published helper: $STAGE_HLP/$HELPER_BIN" >&2
  exit 1
fi
cp "$STAGE_HLP/$HELPER_BIN" "$STAGE_PKG/$HELPER_BIN"
chmod +x "$STAGE_PKG/$HELPER_BIN" || true

cp "$MANIFEST_SRC" "$OUT_DIR/mempulse-module-$VERSION.manifest.json"

ZIP_NAME="mempulse-module-$VERSION-$RID.zip"
ZIP_PATH="$OUT_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH" "$ZIP_PATH.sha256"

(
  cd "$STAGE_PKG"
  zip -q -r "$ZIP_PATH" .
)

(
  cd "$OUT_DIR"
  sha256sum "$ZIP_NAME" > "$ZIP_NAME.sha256"
)

rm -rf "$STAGE_MOD" "$STAGE_HLP" "$STAGE_PKG"
echo "Packaged module: $ZIP_PATH"
cat "$ZIP_PATH.sha256"
