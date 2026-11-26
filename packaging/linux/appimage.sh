#!/usr/bin/env bash
# Packs a release build into one file, Fig-<version>-linux-x64.AppImage, with the FFmpeg it was built against inside.
# Run it from the repo root after `cargo build --release -p fig-ui` with FFMPEG_DIR set (ci/ffmpeg.sh):
#
#   packaging/linux/appimage.sh 2.0.0 dist
#
# Needs patchelf. It fetches appimagetool on first use.
set -euo pipefail

version=$1
out=${2:-dist}
id=io.github.skynse.Fig
bin=${CARGO_TARGET_DIR:-target}/release/fig
: "${FFMPEG_DIR:?run ci/ffmpeg.sh first}"

app=$(mktemp -d)/Fig.AppDir
mkdir -p "$app/usr/bin" "$app/usr/lib" "$app/usr/share" "$out"

cp "$bin" "$app/usr/bin/fig"
# only the real FFmpeg libraries (libavcodec.so.62 and so on), not their symlinks or headers
for lib in "$FFMPEG_DIR"/lib/lib*.so.*; do
    [[ $(basename "$lib") =~ ^lib[a-z0-9_]+\.so\.[0-9]+$ ]] && cp -L "$lib" "$app/usr/lib/"
done
cp "$FFMPEG_DIR/LICENSE.txt" "$app/usr/share/FFMPEG-LICENSE.txt"
cp LICENSE "$app/usr/share/LICENSE"
# DT_RPATH (not RUNPATH), so the FFmpeg libraries find each other in the same folder too
patchelf --force-rpath --set-rpath '$ORIGIN/../lib' "$app/usr/bin/fig"
for lib in "$app"/usr/lib/*.so.*; do patchelf --force-rpath --set-rpath '$ORIGIN' "$lib"; done

sed 's|^Exec=.*|Exec=fig|' "packaging/linux/share/applications/$id.desktop" > "$app/$id.desktop"
cp "packaging/linux/share/icons/hicolor/256x256/apps/$id.png" "$app/$id.png"
cp -r packaging/linux/share/icons "$app/usr/share/"
cat > "$app/AppRun" <<'RUN'
#!/bin/sh
here=$(dirname "$(readlink -f "$0")")
exec "$here/usr/bin/fig" "$@"
RUN
chmod +x "$app/AppRun"

tool=${APPIMAGETOOL:-$PWD/target/appimagetool}
if [ ! -x "$tool" ]; then
    mkdir -p "$(dirname "$tool")"
    curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$tool"
fi
file="$out/Fig-$version-linux-x64.AppImage"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 VERSION=$version "$tool" --no-appstream "$app" "$file"
ls -l "$file"
