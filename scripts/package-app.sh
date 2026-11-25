#!/usr/bin/env bash
# Builds downloadable, self-contained apps (no .NET install needed) into dist/:
#
#   E2EE-Messenger-<version>-macOS-arm64.zip    E2EE Messenger.app for Apple Silicon
#   E2EE-Messenger-<version>-macOS-x64.zip      E2EE Messenger.app for Intel Macs
#   E2EE-Messenger-<version>-Windows-x64.zip    E2eeMessenger.exe
#   E2EE-Messenger-<version>-Linux-x64.zip      E2eeMessenger
#   E2EE-Relay-<version>.zip                    the relay (needs the .NET 8 runtime)
#
# Usage: scripts/package-app.sh [version]       (default 0.5.0)
# The builds are unsigned (macOS ones are ad-hoc signed when built on a Mac). See README.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${1:-0.5.0}"
OUT=dist
WORK="$OUT/work"
rm -rf "$WORK"
mkdir -p "$WORK"

publish() { # rid outdir
  # Single file: one executable plus a few native libraries, which macOS can code-sign.
  dotnet publish src/E2ee.App -c Release -r "$1" --self-contained true \
    -p:PublishSingleFile=true -p:Version="$VERSION" -p:DebugType=none -o "$2" -v quiet --nologo
}

zip_dir() { # dir zipfile (zip the directory itself, keeping its name)
  rm -f "$2"
  (cd "$(dirname "$1")" && zip -qry "$OLDPWD/$2" "$(basename "$1")")
}

for arch in arm64 x64; do
  echo "macOS $arch"
  app="$WORK/macos-$arch/E2EE Messenger.app"
  publish "osx-$arch" "$app/Contents/MacOS"
  mkdir -p "$app/Contents/Resources"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>E2EE Messenger</string>
  <key>CFBundleDisplayName</key><string>E2EE Messenger</string>
  <key>CFBundleIdentifier</key><string>edu.utk.cosc483.e2ee-messenger</string>
  <key>CFBundleExecutable</key><string>E2eeMessenger</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
  if command -v codesign >/dev/null; then
    # Ad-hoc signature: required to run on Apple Silicon. Not a Developer ID, so Gatekeeper
    # still asks the first time (see README).
    for lib in "$app/Contents/MacOS/"*.dylib; do codesign --force --sign - "$lib"; done
    codesign --force --sign - "$app"
    codesign --verify --strict "$app"
  fi
  zip="$OUT/E2EE-Messenger-$VERSION-macOS-$arch.zip"
  rm -f "$zip"
  if command -v ditto >/dev/null; then ditto -c -k --keepParent "$app" "$zip"; else zip_dir "$app" "$zip"; fi
done

echo "Windows x64"
publish win-x64 "$WORK/E2EE Messenger"
zip_dir "$WORK/E2EE Messenger" "$OUT/E2EE-Messenger-$VERSION-Windows-x64.zip"
rm -rf "$WORK/E2EE Messenger"

echo "Linux x64"
publish linux-x64 "$WORK/E2EE Messenger"
zip_dir "$WORK/E2EE Messenger" "$OUT/E2EE-Messenger-$VERSION-Linux-x64.zip"
rm -rf "$WORK/E2EE Messenger"

echo "Relay"
dotnet publish src/E2ee.Server -c Release -p:Version="$VERSION" -p:DebugType=none -o "$WORK/E2EE Relay" -v quiet --nologo
zip_dir "$WORK/E2EE Relay" "$OUT/E2EE-Relay-$VERSION.zip"

rm -rf "$WORK"
ls -lh "$OUT"/*.zip
