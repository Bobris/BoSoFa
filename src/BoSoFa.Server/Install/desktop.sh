#!/bin/sh
# Installs the __BRAND_NAME__ desktop app from this server. It updates itself from the same server afterwards.
#   curl -fsSL <server>/install/desktop.sh | sh
# Environment: BOSOFA_APP_DIR    where the app goes (default: ~/Applications/__BRAND_NAME__.app, or the per-user data folder on Linux)
#              BOSOFA_NO_LAUNCH  set to anything to leave the app closed
set -eu
server='__BOSOFA_SERVER__'

case "$(uname -s)" in
  Darwin) os=osx ;;
  Linux) os=linux ;;
  *) echo "This script is for macOS and Linux." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  arm64 | aarch64) arch=arm64 ;;
  x86_64 | amd64) arch=x64 ;;
  *) echo "Unsupported processor: $(uname -m)." >&2; exit 1 ;;
esac
rid="$os-$arch"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
if [ "$os" = osx ]; then
  root=__BRAND_NAME__.app
  target="${BOSOFA_APP_DIR:-$HOME/Applications/__BRAND_NAME__.app}"
  executable="$target/Contents/MacOS/__BRAND_ID__-desktop"
else
  root=__BRAND_ID__-desktop
  target="${BOSOFA_APP_DIR:-$data_home/__BRAND_NAME__/app}"
  executable="$target/__BRAND_ID__-desktop"
fi
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "Downloading the desktop app for $rid from $server"
if ! curl -fsSL -D "$work/headers" -o "$work/app.tar.gz" "$server/api/v1/downloads/desktop/latest/$rid"; then
  echo "This server has no desktop release for $rid." >&2
  exit 1
fi
expected=$(tr -d '\r' <"$work/headers" | awk 'tolower($1) == "x-bosofa-sha256:" { print $2 }')
if command -v sha256sum >/dev/null 2>&1; then
  actual=$(sha256sum "$work/app.tar.gz" | cut -d' ' -f1)
else
  actual=$(shasum -a 256 "$work/app.tar.gz" | cut -d' ' -f1)
fi
if [ "$actual" != "$expected" ]; then
  echo "The download is damaged: its SHA-256 is $actual, expected $expected." >&2
  exit 1
fi
tar -xzf "$work/app.tar.gz" -C "$work"
mkdir -p "$(dirname "$target")"
rm -rf "$target"
mv "$work/$root" "$target"
echo "Installed in $target"

if [ "$os" = linux ] && [ -z "${BOSOFA_APP_DIR:-}" ]; then
  mkdir -p "$data_home/applications" "$HOME/.local/bin"
  cat >"$data_home/applications/__BRAND_ID__.desktop" <<ENTRY
[Desktop Entry]
Type=Application
Name=__BRAND_NAME__
Exec="$executable" %U
Terminal=false
Categories=Development;
ENTRY
  ln -sf "$executable" "$HOME/.local/bin/__BRAND_ID__-desktop"
fi

if [ -z "${BOSOFA_NO_LAUNCH:-}" ]; then
  if [ "$os" = osx ]; then
    open "$target" --args --server "$server"
  else
    nohup "$executable" --server "$server" >/dev/null 2>&1 &
  fi
fi
