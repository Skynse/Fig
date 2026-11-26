#!/bin/sh
# Adds Fig to your app menu, with its icon, for your user only (nothing outside ~/.local/share is touched).
#
#   ./install.sh                     the fig binary next to this script (an unpacked release)
#   ./install.sh path/to/fig         another build, such as target/release/fig
#   ./install.sh --uninstall         remove the launcher and icons again
#
# The launcher points at fig where it is now: run this again after moving it.
set -eu

id=io.github.skynse.Fig
here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}

refresh() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$data/applications" || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true
    command -v kbuildsycoca6 >/dev/null 2>&1 && kbuildsycoca6 >/dev/null 2>&1 || true
}

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$data/applications/$id.desktop"
    rm -f "$data"/icons/hicolor/*/apps/"$id".png
    refresh
    echo "Removed Fig from the app menu."
    exit 0
fi

exe=$(realpath "${1:-$here/fig}")
if [ ! -x "$exe" ]; then
    echo "No fig binary at $exe; pass the path to one." >&2
    exit 1
fi

mkdir -p "$data/applications"
sed "s|@EXEC@|$exe|g" "$here/share/applications/$id.desktop" > "$data/applications/$id.desktop"

for icon in "$here"/share/icons/hicolor/*/apps/"$id".png; do
    size=$(basename "$(dirname "$(dirname "$icon")")")
    mkdir -p "$data/icons/hicolor/$size/apps"
    cp "$icon" "$data/icons/hicolor/$size/apps/$id.png"
done

refresh
echo "Fig is in your app menu (launching $exe)."
