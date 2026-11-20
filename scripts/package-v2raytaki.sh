#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."

case "${1:-$(uname -m)}" in
  x86_64|x64|linux-x64) rid=linux-x64 ;;
  aarch64|arm64|linux-arm64) rid=linux-arm64 ;;
  *) echo "Usage: $0 [linux-x64|linux-arm64]" >&2; exit 2 ;;
esac

stage="$(mktemp -d)"
trap 'rm -rf -- "$stage"' EXIT
name="KnightRay-$rid"
bundle="$stage/$name"
mkdir -p "$bundle/app" dist
"${DOTNET:-dotnet}" publish v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj \
  -c Release -r "$rid" --self-contained true -p:PublishSingleFile=false \
  -p:PublishTrimmed=false -o "$bundle/app"
python3 scripts/bundle-linux-cores.py "$rid" "$bundle/app/bin" "$stage/downloads"
install -m 755 scripts/install-v2raytaki.sh "$bundle/install.sh"
install -m 644 LICENSE "$bundle/LICENSE"
install -m 644 v2rayN/v2rayN.Desktop/Assets/v2rayTaki.png "$bundle/knightray.png"
tar -C "$stage" -czf "dist/$name.tar.gz" "$name"
printf 'Created %s\n' "dist/$name.tar.gz"
