#!/bin/sh
# Builds dist/Spacebridge.app and the release archive. Needs the .NET 9 SDK and Xcode's command line tools.
set -eu
cd "$(dirname "$0")"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Spacebridge.csproj)
app=dist/Spacebridge.app

# The CoreBluetooth helper the .NET side calls. The header padding leaves Homebrew room to rewrite the library id.
clang -dynamiclib -fobjc-arc -arch arm64 -mmacosx-version-min=13.0 -framework Foundation -framework CoreBluetooth \
  -headerpad_max_install_names -install_name @loader_path/libspacebridge-ble.dylib -o lib/libspacebridge-ble.dylib ble/spacebridge_ble.m

rm -rf dist obj/publish
dotnet publish -c Release -o obj/publish --nologo -v quiet
mkdir -p "$app/Contents/MacOS"
cp obj/publish/spacebridge obj/publish/*.dylib "$app/Contents/MacOS/"
sed "s/VERSION/$version/g" packaging/Info.plist > "$app/Contents/Info.plist"
# Ad-hoc signature: it ties Info.plist (the Bluetooth permission text) to the program.
codesign --force --deep --sign - "$app" 2>/dev/null
tar -czf "dist/spacebridge-$version-macos-arm64.tar.gz" -C dist Spacebridge.app
echo "$app"
shasum -a 256 "dist/spacebridge-$version-macos-arm64.tar.gz"
