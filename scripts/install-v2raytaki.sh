#!/usr/bin/env bash
set -euo pipefail

bundle="$(cd -- "$(dirname -- "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
destination="$data/v2rayTaki-app"
applications="$data/applications"
launcher="$HOME/.local/bin/knightray"
icon="$data/icons/hicolor/256x256/apps/knightray.png"

if [[ ! -x "$bundle/app/KnightRay" || ! -f "$bundle/knightray.png" ]]; then
  echo 'Run this installer from the extracted KnightRay archive.' >&2
  exit 1
fi

mkdir -p -- "$data" "$applications" "$(dirname -- "$launcher")" "$(dirname -- "$icon")"
staging="$(mktemp -d -- "$data/.v2rayTaki-app.XXXXXXXX")"
trap 'rm -rf -- "$staging"' EXIT
cp -a -- "$bundle/app/." "$staging/"
install -m 644 "$bundle/LICENSE" "$staging/LICENSE"
# Replace only directories previously installed by this script.
touch "$staging/.taki-install"
# Existing commands and autostart files must start the app with its new process name.
cat > "$staging/v2rayTaki" <<'LAUNCHER'
#!/bin/sh
exe="$(readlink -f -- "$0")" || exit 1
exec "${exe%/*}/KnightRay" "$@"
LAUNCHER
chmod 755 "$staging/v2rayTaki"
if [[ -e "$destination" ]]; then
  if [[ -L "$destination" || ! -f "$destination/.taki-install" ]]; then
    printf 'Refusing to replace an unrelated directory: %s\n' "$destination" >&2
    exit 1
  fi
  mv -T -- "$destination" "$staging.previous"
fi
if ! mv -T -- "$staging" "$destination"; then
  if [[ -d "$staging.previous" ]]; then mv -T -- "$staging.previous" "$destination"; fi
  exit 1
fi
rm -rf -- "$staging.previous"

install -m 644 "$bundle/knightray.png" "$icon"
ln -sfn -- "$destination/KnightRay" "$launcher"
# Escape strings for the Desktop Entry Exec field.
desktop_exec_path() {
  printf '%s' "$1" | sed 's/[\\"$\x60]/\\&/g; s/%/%%/g; s/\\/\\\\/g'
}
exec_path="$(desktop_exec_path "$launcher")"
cat > "$applications/knightray.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=KnightRay
Comment=Proxy client with a config fetcher and tester
Exec="$exec_path"
Icon=knightray
Terminal=false
Categories=Network;
StartupWMClass=KnightRay
DESKTOP
legacy_entry="$applications/v2raytaki.desktop"
legacy_exec_path="$(desktop_exec_path "${launcher%/*}/v2raytaki")"
if [[ -f "$legacy_entry" && ! -L "$legacy_entry" ]] &&
   grep -Fxq -e "Exec=\"$exec_path\"" -e "Exec=\"$legacy_exec_path\"" "$legacy_entry"; then
  rm -- "$legacy_entry"
fi
printf 'Installed KnightRay. Launch it from your application menu or run %s.\n' "$launcher"
