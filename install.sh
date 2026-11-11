#!/bin/sh
# Installs Spacebridge for the logged-in user, puts the `spacebridge` command on the PATH and starts the guided setup.
#   curl -fsSL https://raw.githubusercontent.com/Berrysekk/spacebridge/main/install.sh | sh
# For scripts, a SPACE address skips the questions: ... | sh -s -- https://space.example:8100
# Uninstall: spacebridge uninstall
set -eu

repo=Berrysekk/spacebridge
again="curl -fsSL https://raw.githubusercontent.com/$repo/main/install.sh | sh"
dir="$HOME/Library/Application Support/Spacebridge"
app="$dir/Spacebridge.app"

# What went wrong, and the command that fixes it.
red="" bold="" plain=""
[ -t 2 ] && red=$(printf '\033[31m') bold=$(printf '\033[1m') plain=$(printf '\033[0m')
fail() {
  printf '\n%s✗%s %s\n' "$red" "$plain" "$1" >&2
  [ -z "${2:-}" ] || printf '  Fix: %s%s%s\n' "$bold" "$2" "$plain" >&2
  exit 1
}

[ "$(uname -s)" = Darwin ] || fail "Spacebridge is for macOS; this is $(uname -s)."
[ "$(uname -m)" = arm64 ] || fail "Spacebridge runs on Apple silicon Macs; this one has an Intel processor."
macos=$(sw_vers -productVersion)
[ "${macos%%.*}" -ge 13 ] || fail "Spacebridge needs macOS 13 or later; this is macOS $macos." "softwareupdate --list"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
# A local archive (SPACEBRIDGE_ARCHIVE=dist/spacebridge-2.0.2-macos-arm64.tar.gz) instead of the latest release.
if [ -n "${SPACEBRIDGE_ARCHIVE:-}" ]; then
  cp "$SPACEBRIDGE_ARCHIVE" "$tmp/spacebridge.tar.gz" || fail "There is no archive at $SPACEBRIDGE_ARCHIVE." "./build.sh"
else
  # Where "latest" leads tells the version, without GitHub's API and its hourly limit.
  latest=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$repo/releases/latest") \
    || fail "github.com cannot be reached. Check the internet connection." "$again"
  version=${latest##*/v}
  case "$version" in [0-9]*.[0-9]*) ;; *) fail "No release found at github.com/$repo." "$again" ;; esac
  url="https://github.com/$repo/releases/download/v$version/spacebridge-$version-macos-arm64.tar.gz"
  echo "Downloading Spacebridge $version"
  curl -fsSL --retry 2 -o "$tmp/spacebridge.tar.gz" "$url" || fail "Downloading $url failed." "$again"
fi
tar -xzf "$tmp/spacebridge.tar.gz" -C "$tmp" 2>/dev/null && [ -x "$tmp/Spacebridge.app/Contents/MacOS/spacebridge" ] \
  || fail "The download is not a Spacebridge archive (cut short?)." "$again"

# The app goes where the service runs it from, so the command points at a program that stays.
# A running service keeps its old copy until `install` or the setup restarts it.
own="sudo chown -R \"\$(id -un)\" \"$dir\""
mkdir -p "$dir" 2>/dev/null && [ -w "$dir" ] || fail "No permission to write $dir." "$own && $again"
rm -rf "$app" 2>/dev/null && ditto "$tmp/Spacebridge.app" "$app" 2>/dev/null || fail "The app could not be copied to $app." "$own && $again"
xattr -dr com.apple.quarantine "$app" 2>/dev/null || true
program="$app/Contents/MacOS/spacebridge"
"$program" version >/dev/null 2>&1 || fail "The installed program does not start on this Mac (macOS $macos)." "\"$program\" version"

# `curl | sh` takes the script on standard input: questions are asked on the terminal itself.
terminal=""
( : </dev/tty ) 2>/dev/null && terminal=1
on_path() { case ":$PATH:" in *":$1:"*) return 0 ;; esac; return 1; }

# The command goes into a directory that is already on the PATH, so it works in this terminal straight away:
# one the user may write to (made if it is on the PATH but not there yet), else /usr/local/bin (on every Mac's PATH,
# but on a new Mac missing or root's).
bin=""
for candidate in "$HOME/.local/bin" "$HOME/bin" /opt/homebrew/bin /usr/local/bin; do
  on_path "$candidate" || continue
  [ -d "$candidate" ] || mkdir -p "$candidate" 2>/dev/null || continue
  if [ -w "$candidate" ] && ln -sf "$program" "$candidate/spacebridge" 2>/dev/null; then bin=$candidate; break; fi
done
if [ -z "$bin" ] && on_path /usr/local/bin && [ -n "$terminal" ]; then
  echo "Putting the spacebridge command in /usr/local/bin; macOS asks for your password."
  if sudo mkdir -p /usr/local/bin </dev/tty && sudo ln -sf "$program" /usr/local/bin/spacebridge </dev/tty; then bin=/usr/local/bin; fi
fi
added=""
if [ -z "$bin" ]; then
  # No password, or no terminal to ask for it: ~/.local/bin, on the PATH of new terminal windows.
  bin="$HOME/.local/bin"
  mkdir -p "$bin" && ln -sf "$program" "$bin/spacebridge" || fail "The spacebridge command could not be put in $bin." "sudo ln -sf \"$program\" /usr/local/bin/spacebridge"
  if ! on_path "$bin"; then
    case "${SHELL##*/}" in bash) rc="$HOME/.bash_profile" ;; zsh) rc="${ZDOTDIR:-$HOME}/.zshrc" ;; *) rc="$HOME/.profile" ;; esac
    # Creates the file, and the folder it is in, when there is none yet.
    mkdir -p "$(dirname "$rc")"
    grep -qs '\.local/bin' "$rc" || printf '\n# Added by the Spacebridge installer\nexport PATH="$HOME/.local/bin:$PATH"\n' >> "$rc"
    added=$rc
  fi
fi
echo "Installed the spacebridge command in $bin."

# An address sets up without questions; an upgrade only updates the service; otherwise the guided setup.
unfinished=""
if [ $# -gt 0 ]; then
  "$program" setup "$@" || unfinished="spacebridge setup $*"
elif [ -f "$dir/sign.key" ] && [ -f "$HOME/Library/LaunchAgents/cz.totosoft.spacebridge.plist" ]; then
  "$program" install || unfinished="spacebridge doctor"
elif [ -n "$terminal" ]; then
  "$program" setup </dev/tty || unfinished="spacebridge setup"
else
  unfinished="spacebridge setup"
fi

if [ -n "$added" ]; then
  echo
  echo "New terminal windows find the command ($added puts $bin on the PATH). In this one, first run:"
  echo '  export PATH="$HOME/.local/bin:$PATH"'
fi
if [ -n "$unfinished" ]; then
  printf '\nSpacebridge is installed but not set up yet.\n  Fix: %s%s%s\n' "$bold" "$unfinished" "$plain" >&2
  # Scripts that gave an address learn that it did not work.
  [ $# -eq 0 ] || exit 1
fi
