#!/bin/sh
# Installs Spacebridge for the logged-in user and puts the `spacebridge` command on the PATH.
#   curl -fsSL https://raw.githubusercontent.com/Berrysekk/SaltoSpacebridge-macos/main/install.sh | sh -s -- https://space.example:8100
# With a SPACE address it also sets everything up (`spacebridge setup <address>`); without one, run that afterwards.
# Uninstall: spacebridge uninstall --all && rm "$(command -v spacebridge)"
set -eu

repo=Berrysekk/SaltoSpacebridge-macos
dir="$HOME/Library/Application Support/Spacebridge"
[ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || { echo "Spacebridge runs on Apple silicon Macs only." >&2; exit 1; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
# A local archive (SPACEBRIDGE_ARCHIVE=dist/spacebridge-1.0.0-macos-arm64.tar.gz) instead of the latest release.
if [ -n "${SPACEBRIDGE_ARCHIVE:-}" ]; then
  cp "$SPACEBRIDGE_ARCHIVE" "$tmp/spacebridge.tar.gz"
else
  url=$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" | sed -n 's/.*"browser_download_url": *"\(.*macos-arm64\.tar\.gz\)".*/\1/p' | head -1)
  [ -n "$url" ] || { echo "No release found at github.com/$repo." >&2; exit 1; }
  echo "Downloading $url"
  curl -fsSL -o "$tmp/spacebridge.tar.gz" "$url"
fi
tar -xzf "$tmp/spacebridge.tar.gz" -C "$tmp"

# The first writable directory that is on the PATH gets the command.
bin=""
for candidate in "$HOME/.local/bin" "$HOME/bin" /opt/homebrew/bin /usr/local/bin; do
  case ":$PATH:" in *":$candidate:"*) [ -d "$candidate" ] && [ -w "$candidate" ] && { bin=$candidate; break; } ;; esac
done

# `install` copies the app into place and (re)starts the service; `setup` first fetches SALTO's signing key from the SPACE.
# A setup that fails (a mistyped address) still leaves the app and the command behind, so it can be repeated.
unfinished=""
if [ $# -gt 0 ]; then
  "$tmp/Spacebridge.app/Contents/MacOS/spacebridge" setup "$@" || { unfinished=1; "$tmp/Spacebridge.app/Contents/MacOS/spacebridge" install; }
else "$tmp/Spacebridge.app/Contents/MacOS/spacebridge" install; fi

if [ -n "$bin" ]; then
  ln -sf "$dir/Spacebridge.app/Contents/MacOS/spacebridge" "$bin/spacebridge"
  echo "The command is $bin/spacebridge."
else
  echo "No writable directory on your PATH. Add this to ~/.zshrc:"
  echo "  alias spacebridge='\"$dir/Spacebridge.app/Contents/MacOS/spacebridge\"'"
fi
if [ -n "$unfinished" ]; then
  echo "Setup did not finish (see the error above). Repeat it with the address of your own SPACE: spacebridge setup <address>" >&2
  exit 1
fi
[ $# -gt 0 ] || echo "Next: spacebridge setup <the address of your SPACE, as in the browser>"
