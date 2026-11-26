#!/usr/bin/env bash
# Puts FFmpeg 8.1 where the build finds it, and its tools (ffprobe, which the export tests use) on PATH:
#   Linux, Windows   BtbN's GPL shared build (libx264 is needed for export and proxies, and Fig is GPLv3), in ./ffmpeg;
#                    CI and the releases build against the same one, so what is tested is what ships
#   macOS            Homebrew's
#
#   source ci/ffmpeg.sh        sets FFMPEG_DIR (and the library path); on Actions it saves them for later steps
set -euo pipefail

save() { # name value: for this shell and, on Actions, the steps after it
    export "$1=$2"
    if [ -n "${GITHUB_ENV:-}" ]; then echo "$1=$2" >> "$GITHUB_ENV"; fi
}
add_path() {
    export PATH="$1:$PATH"
    if [ -n "${GITHUB_PATH:-}" ]; then echo "$2" >> "$GITHUB_PATH"; fi
}

case "$(uname -s)" in
    Darwin)
        brew list ffmpeg > /dev/null 2>&1 || brew install --quiet ffmpeg pkg-config
        save FFMPEG_DIR "$(brew --prefix ffmpeg)"
        ;;
    MINGW* | MSYS* | CYGWIN*)
        name=ffmpeg-n8.1-latest-win64-gpl-shared-8.1
        if [ ! -d ffmpeg/lib ]; then
            rm -rf ffmpeg "$name"
            curl -fsSL -o ffmpeg.zip "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.zip"
            unzip -q ffmpeg.zip && rm ffmpeg.zip
            mv "$name" ffmpeg
        fi
        # build scripts are Windows programs: give them C:/... paths; the DLLs are found through PATH
        save FFMPEG_DIR "$(cygpath -m "$PWD/ffmpeg")"
        add_path "$PWD/ffmpeg/bin" "$(cygpath -w "$PWD/ffmpeg/bin")"
        ;;
    *)
        name=ffmpeg-n8.1-latest-linux64-gpl-shared-8.1
        if [ ! -d ffmpeg/lib ]; then
            rm -rf ffmpeg "$name"
            curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.tar.xz" | tar xJ
            mv "$name" ffmpeg
        fi
        save FFMPEG_DIR "$PWD/ffmpeg"
        save LD_LIBRARY_PATH "$PWD/ffmpeg/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
        add_path "$PWD/ffmpeg/bin" "$PWD/ffmpeg/bin"
        ;;
esac
