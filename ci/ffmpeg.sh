#!/usr/bin/env bash
# Fetches FFmpeg 8.1 (BtbN's GPL shared build: libx264 is needed for export and proxies, and Fig is GPLv3) into
# ./ffmpeg and points the build at it. CI and releases build against this one, so what is tested is what ships.
#
#   source ci/ffmpeg.sh        sets FFMPEG_DIR and LD_LIBRARY_PATH, and puts its ffmpeg/ffprobe on PATH (the export
#                              tests check their output with ffprobe); on Actions it saves them for later steps
set -euo pipefail

name=ffmpeg-n8.1-latest-linux64-gpl-shared-8.1
url=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.tar.xz

if [ ! -d ffmpeg/lib ]; then
    rm -rf ffmpeg "$name"
    curl -fsSL "$url" | tar xJ
    mv "$name" ffmpeg
fi

export FFMPEG_DIR=$PWD/ffmpeg
export LD_LIBRARY_PATH=$FFMPEG_DIR/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
export PATH=$FFMPEG_DIR/bin:$PATH
if [ -n "${GITHUB_ENV:-}" ]; then
    echo "FFMPEG_DIR=$FFMPEG_DIR" >> "$GITHUB_ENV"
    echo "LD_LIBRARY_PATH=$LD_LIBRARY_PATH" >> "$GITHUB_ENV"
    echo "$FFMPEG_DIR/bin" >> "$GITHUB_PATH"
fi
