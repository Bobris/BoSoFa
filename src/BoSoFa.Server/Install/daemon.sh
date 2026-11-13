#!/bin/sh
# Installs the __BRAND_NAME__ daemon from this server and makes this computer one of its machines.
#   curl -fsSL <server>/install/daemon.sh | sh
# Environment: BOSOFA_DATA_DIR      the daemon's data folder
#              BOSOFA_INSTALL_ARGS  more arguments for '__BRAND_DAEMON__ install', such as: --scope system --service-user build
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
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "Downloading the daemon for $rid from $server"
if ! curl -fsSL -D "$work/headers" -o "$work/__BRAND_DAEMON__" "$server/api/v1/downloads/daemon/$rid"; then
  echo "This server carries no daemon for $rid." >&2
  exit 1
fi
expected=$(tr -d '\r' <"$work/headers" | awk 'tolower($1) == "x-bosofa-sha256:" { print $2 }')
if command -v sha256sum >/dev/null 2>&1; then
  actual=$(sha256sum "$work/__BRAND_DAEMON__" | cut -d' ' -f1)
else
  actual=$(shasum -a 256 "$work/__BRAND_DAEMON__" | cut -d' ' -f1)
fi
if [ "$actual" != "$expected" ]; then
  echo "The download is damaged: its SHA-256 is $actual, expected $expected." >&2
  exit 1
fi
chmod +x "$work/__BRAND_DAEMON__"

if [ -n "${BOSOFA_DATA_DIR:-}" ]; then set -- --data-dir "$BOSOFA_DATA_DIR"; else set --; fi
"$work/__BRAND_DAEMON__" enroll --server "$server" "$@" </dev/null
# shellcheck disable=SC2086 # the extra arguments are meant to split
"$work/__BRAND_DAEMON__" install "$@" ${BOSOFA_INSTALL_ARGS:-} </dev/null
