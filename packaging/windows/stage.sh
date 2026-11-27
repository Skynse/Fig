#!/usr/bin/env bash
# Stages a Windows release build with the FFmpeg DLLs beside it, then makes the portable zip and the installer:
#   Fig-<v>-win-x64.zip          unzip anywhere and run fig.exe
#   Fig-<v>-win-x64-setup.exe    Start menu, uninstaller, no admin needed
# Run from the repo root in Git Bash after `cargo build --release -p fig-ui` with FFMPEG_DIR set (ci/ffmpeg.sh):
#
#   packaging/windows/stage.sh 2.0.0 dist
set -euo pipefail

version=$1
out=${2:-dist}
: "${FFMPEG_DIR:?run ci/ffmpeg.sh first}"
stage=$(mktemp -d)/Fig
mkdir -p "$stage" "$out"

cp "${CARGO_TARGET_DIR:-target}/release/fig.exe" "$stage/"
cp "$FFMPEG_DIR"/bin/*.dll "$stage/"
cp "$FFMPEG_DIR/LICENSE.txt" "$stage/FFMPEG-LICENSE.txt"
cp LICENSE "$stage/LICENSE.txt"
cp packaging/windows/fig.ico "$stage/"

out=$(realpath "$out")
zip="$out/Fig-$version-win-x64.zip"
rm -f "$zip"
(cd "$(dirname "$stage")" && 7z a -tzip -bso0 -bsp0 "$(cygpath -w "$zip")" Fig)

iscc="/c/Program Files (x86)/Inno Setup 6/ISCC.exe"
[ -f "$iscc" ] || choco install innosetup -y --no-progress > /dev/null
# no Git Bash path rewriting: ISCC's /D options must arrive as written
MSYS_NO_PATHCONV=1 "$iscc" /Q "/DVersion=$version" "/DSource=$(cygpath -w "$stage")" "/DOutput=$(cygpath -w "$out")" packaging/windows/Fig.iss
ls -l "$out"
