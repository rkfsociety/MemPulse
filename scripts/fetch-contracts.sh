#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-1.0.0}"
TAG="${2:-contracts-v1.0.0}"

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
FEED_DIR="$ROOT_DIR/.nuget-local"
mkdir -p "$FEED_DIR"

PACKAGE_NAME="PCPulse.Contracts.${VERSION}.nupkg"
PACKAGE_PATH="$FEED_DIR/$PACKAGE_NAME"
SHA_PATH="$PACKAGE_PATH.sha256"
BASE_URL="https://github.com/rkfsociety/PCPulse/releases/download/${TAG}"

verify_checksum() {
  [[ -f "$PACKAGE_PATH" && -f "$SHA_PATH" ]] || return 1
  local expected actual
  expected="$(awk '{print tolower($1)}' "$SHA_PATH" | tr -d '\r\n')"
  [[ "$expected" =~ ^[a-f0-9]{64}$ ]] || return 1
  actual="$(sha256sum "$PACKAGE_PATH" | awk '{print tolower($1)}')"
  [[ "$actual" == "$expected" ]]
}

if verify_checksum; then
  echo "PCPulse.Contracts $VERSION already cached and verified in .nuget-local."
  exit 0
fi

echo "Downloading $BASE_URL/$PACKAGE_NAME ..."
curl -fsSL "$BASE_URL/$PACKAGE_NAME.sha256" -o "$SHA_PATH"
curl -fsSL "$BASE_URL/$PACKAGE_NAME" -o "$PACKAGE_PATH"

if ! verify_checksum; then
  rm -f "$PACKAGE_PATH"
  echo "SHA-256 verification failed for $PACKAGE_NAME" >&2
  exit 1
fi

echo "Verified $PACKAGE_NAME in $FEED_DIR."
