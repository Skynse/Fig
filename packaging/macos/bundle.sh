#!/usr/bin/env bash
# Packs a macOS release build into Fig.app, with the FFmpeg dylibs it links (and theirs) inside, zipped as
# Fig-<v>-macos-arm64.zip. The app is signed ad hoc, not notarized: the first launch needs right-click > Open.
# Run from the repo root after `cargo build --release -p fig-ui` (ci/ffmpeg.sh installs FFmpeg with Homebrew):
#
#   packaging/macos/bundle.sh 2.0.0 dist
set -euo pipefail

version=$1
out=${2:-dist}
arch=$(uname -m | sed 's/x86_64/x64/')
mkdir -p "$out"
out=$(realpath "$out")
work=$(mktemp -d)
app=$work/Fig.app
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$app/Contents/Frameworks"

cp "${CARGO_TARGET_DIR:-target}/release/fig" "$app/Contents/MacOS/fig"
cp LICENSE "$app/Contents/Resources/LICENSE"

# the icon, from the 512 px PNG
icons=$work/fig.iconset
mkdir -p "$icons"
png=packaging/linux/share/icons/hicolor/512x512/apps/io.github.skynse.Fig.png
for size in 16 32 128 256 512; do
    sips -z $size $size "$png" --out "$icons/icon_${size}x${size}.png" > /dev/null
    double=$((size * 2)); [ $double -le 512 ] && sips -z $double $double "$png" --out "$icons/icon_${size}x${size}@2x.png" > /dev/null
done
cp "$png" "$icons/icon_256x256@2x.png"
iconutil -c icns "$icons" -o "$app/Contents/Resources/fig.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Fig</string>
    <key>CFBundleDisplayName</key><string>Fig</string>
    <key>CFBundleIdentifier</key><string>io.github.skynse.Fig</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleExecutable</key><string>fig</string>
    <key>CFBundleIconFile</key><string>fig</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# FFmpeg and everything it pulls in from Homebrew, rewritten to load from Contents/Frameworks
brew list dylibbundler > /dev/null 2>&1 || brew install --quiet dylibbundler
dylibbundler -of -b -cd -x "$app/Contents/MacOS/fig" -d "$app/Contents/Frameworks" -p @executable_path/../Frameworks/ > /dev/null
cp "$(brew --prefix ffmpeg)/LICENSE.md" "$app/Contents/Resources/FFMPEG-LICENSE.md" 2>/dev/null || true

# arm64 refuses to run modified binaries unsigned: an ad hoc signature over the whole bundle
codesign --force --deep --sign - "$app"

zip="$out/Fig-$version-macos-$arch.zip"
rm -f "$zip"
ditto -c -k --keepParent "$app" "$zip"
ls -l "$zip"
