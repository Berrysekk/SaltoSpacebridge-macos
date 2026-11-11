#!/bin/sh
# Installs the SALTO Local IO Bridge for the logged-in user: the program, autostart at login,
# and trust for its own https://localhost certificate (macOS asks for your password once).
# Run from the folder holding SaltoLocalIOBridge and libusb-1.0.0.dylib.
#
# Uninstall:
#   launchctl bootout gui/$(id -u)/cz.totosoft.salto-local-io-bridge
#   security delete-certificate -c localhost ~/Library/Keychains/login.keychain-db
#   rm -rf ~/Library/LaunchAgents/cz.totosoft.salto-local-io-bridge.plist ~/Library/Application\ Support/SaltoLocalIOBridge
set -eu

here=$(cd "$(dirname "$0")" && pwd)
dir="$HOME/Library/Application Support/SaltoLocalIOBridge"
label=cz.totosoft.salto-local-io-bridge
plist="$HOME/Library/LaunchAgents/$label.plist"
log="$HOME/Library/Logs/SaltoLocalIOBridge.log"

mkdir -p "$dir" "$HOME/Library/LaunchAgents" "$HOME/Library/Logs"
launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
cp -f "$here/SaltoLocalIOBridge" "$here/libusb-1.0.0.dylib" "$dir/"
# The build is not notarised: a copy that arrived by download or AirDrop would be blocked.
xattr -dr com.apple.quarantine "$dir" 2>/dev/null || true

cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>ProgramArguments</key><array><string>$dir/SaltoLocalIOBridge</string></array>
  <key>WorkingDirectory</key><string>$dir</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>$log</string>
  <key>StandardErrorPath</key><string>$log</string>
</dict>
</plist>
EOF
launchctl bootstrap "gui/$(id -u)" "$plist"

# The bridge writes its certificate on first start.
for _ in 1 2 3 4 5 6 7 8 9 10; do
  [ -f "$dir/localhost.crt" ] && break
  sleep 1
done
# Already trusted (an update): no password prompt.
security verify-cert -c "$dir/localhost.crt" -p ssl -s localhost >/dev/null 2>&1 ||
  security add-trusted-cert -r trustRoot -p ssl -k "$HOME/Library/Keychains/login.keychain-db" "$dir/localhost.crt"

sleep 1
tail -n 1 "$log"
