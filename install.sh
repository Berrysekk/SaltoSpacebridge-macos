#!/bin/sh
# Installs Spacebridge for the logged-in user, puts the `spacebridge` command on the PATH and starts the guided setup.
#   curl -fsSL https://raw.githubusercontent.com/Berrysekk/SaltoSpacebridge-macos/main/install.sh | sh
# For scripts, a SPACE address skips the questions: ... | sh -s -- https://space.example:8100
# Uninstall: spacebridge uninstall --all && rm "$(command -v spacebridge)"
set -eu

repo=Berrysekk/SaltoSpacebridge-macos
dir="$HOME/Library/Application Support/Spacebridge"
app="$dir/Spacebridge.app"
[ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || { echo "Spacebridge runs on Apple silicon Macs only." >&2; exit 1; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
# A local archive (SPACEBRIDGE_ARCHIVE=dist/spacebridge-1.1.0-macos-arm64.tar.gz) instead of the latest release.
if [ -n "${SPACEBRIDGE_ARCHIVE:-}" ]; then
  cp "$SPACEBRIDGE_ARCHIVE" "$tmp/spacebridge.tar.gz"
else
  url=$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" | sed -n 's/.*"browser_download_url": *"\(.*macos-arm64\.tar\.gz\)".*/\1/p' | head -1)
  [ -n "$url" ] || { echo "No release found at github.com/$repo." >&2; exit 1; }
  echo "Downloading $url"
  curl -fsSL -o "$tmp/spacebridge.tar.gz" "$url"
fi
tar -xzf "$tmp/spacebridge.tar.gz" -C "$tmp"

# The app goes where the service runs it from, so the command points at a program that stays.
# A running service keeps its old copy until `install` or the setup restarts it.
mkdir -p "$dir"
rm -rf "$app"
ditto "$tmp/Spacebridge.app" "$app"
xattr -dr com.apple.quarantine "$app" 2>/dev/null || true
program="$app/Contents/MacOS/spacebridge"

# `curl | sh` takes the script on standard input: questions are asked on the terminal itself.
terminal=""
( : </dev/tty ) 2>/dev/null && terminal=1
on_path() { case ":$PATH:" in *":$1:"*) return 0 ;; esac; return 1; }

# The command goes into a directory that is already on the PATH, so it works in this terminal straight away:
# one the user may write to, else /usr/local/bin (on every Mac's PATH, but on a new Mac missing or root's).
bin=""
for candidate in "$HOME/.local/bin" "$HOME/bin" /opt/homebrew/bin /usr/local/bin; do
  if on_path "$candidate" && [ -d "$candidate" ] && [ -w "$candidate" ]; then bin=$candidate; break; fi
done
if [ -n "$bin" ]; then
  ln -sf "$program" "$bin/spacebridge"
elif on_path /usr/local/bin && [ -n "$terminal" ]; then
  echo "Putting the spacebridge command in /usr/local/bin; macOS asks for your password."
  if sudo mkdir -p /usr/local/bin </dev/tty && sudo ln -sf "$program" /usr/local/bin/spacebridge </dev/tty; then bin=/usr/local/bin; fi
fi
added=""
if [ -z "$bin" ]; then
  # No password, or no terminal to ask for it: ~/.local/bin, on the PATH of new terminal windows.
  bin="$HOME/.local/bin"
  mkdir -p "$bin"
  ln -sf "$program" "$bin/spacebridge"
  if ! on_path "$bin"; then
    case "${SHELL##*/}" in bash) rc="$HOME/.bash_profile" ;; zsh) rc="${ZDOTDIR:-$HOME}/.zshrc" ;; *) rc="$HOME/.profile" ;; esac
    # Creates the file when there is none yet.
    grep -qs '\.local/bin' "$rc" || printf '\n# Added by the Spacebridge installer\nexport PATH="$HOME/.local/bin:$PATH"\n' >> "$rc"
    added=$rc
  fi
fi
echo "Installed the spacebridge command in $bin."

# An address sets up without questions; an upgrade only updates the service; otherwise the guided setup.
unfinished=""
if [ $# -gt 0 ]; then
  "$program" setup "$@" || unfinished=1
elif [ -f "$dir/sign.key" ] && [ -f "$HOME/Library/LaunchAgents/cz.totosoft.spacebridge.plist" ]; then
  "$program" install || unfinished=1
elif [ -n "$terminal" ]; then
  "$program" setup </dev/tty || unfinished=1
else
  unfinished=1
fi

if [ -n "$added" ]; then
  echo
  echo "New terminal windows find the command ($added puts $bin on the PATH). In this one, first run:"
  echo '  export PATH="$HOME/.local/bin:$PATH"'
fi
if [ -n "$unfinished" ]; then
  echo
  echo "Spacebridge is not set up yet. Run: spacebridge setup"
  # Scripts that gave an address learn that it did not work.
  [ $# -eq 0 ] || exit 1
fi
