#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
if [ "$(uname -sm)" != "Darwin arm64" ]; then
    echo "Dieses Demo-Paket benötigt macOS ARM64 (MXC 1.0.1 enthält kein osx-x64-Asset)." >&2
    exit 1
fi
app="$PWD/artifacts/MXC Sandbox Console.app"
dotnet publish src/MxcSandbox.Desktop -c Release --disable-build-servers -r osx-arm64 --self-contained true -p:NuGetLockFilePath=obj/publish-osx-arm64.lock.json -o "$app/Contents/MacOS"
dotnet publish src/MxcSandbox.Console -c Release --disable-build-servers -r osx-arm64 --self-contained true -p:NuGetLockFilePath=obj/publish-osx-arm64.lock.json -o "$app/Contents/MacOS/console"
mkdir -p "$app/Contents/Resources"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>MxcSandbox.Desktop</string>
<key>CFBundleIdentifier</key><string>local.mxc.sandboxdemo.console</string>
<key>CFBundleName</key><string>MXC Sandbox Console</string>
<key>CFBundleDisplayName</key><string>MXC Sandbox Console</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>0.2.1</string>
<key>CFBundleVersion</key><string>3</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
printf 'App: %s\n' "$app"
