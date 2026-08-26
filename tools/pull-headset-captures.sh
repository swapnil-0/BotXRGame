#!/usr/bin/env bash
#
# Pull the most recent screenshots / recordings off the XR headset.
#
# Capture folders differ between headsets and OS versions, so this does not
# hardcode one. It searches the usual media roots, sorts by modification time,
# and pulls the newest N files.
#
# Usage:
#   ./tools/pull-headset-captures.sh              # newest 1 -> ./captures
#   ./tools/pull-headset-captures.sh 5            # newest 5 -> ./captures
#   ./tools/pull-headset-captures.sh 5 docs/images
#
# Requires adb on PATH. Works in Git Bash, WSL, macOS and Linux.

set -euo pipefail

COUNT="${1:-1}"
DEST="${2:-captures}"

# Git Bash (MSYS) rewrites anything that looks like a Unix path into a Windows
# one before it reaches the program - so "/sdcard/DCIM" would arrive at adb as
# "C:/Program Files/Git/sdcard/DCIM" and find nothing. This disables that.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

ROOTS="/sdcard/DCIM /sdcard/Pictures /sdcard/Movies"

if ! command -v adb >/dev/null 2>&1; then
    echo "adb not found on PATH." >&2
    echo "Add it, or call it by full path, e.g. /c/platform-tools/adb" >&2
    exit 1
fi

if ! adb devices | tail -n +2 | grep -qw "device"; then
    echo "No device found." >&2
    echo "Put the headset on and approve the USB debugging prompt." >&2
    echo "For wireless: adb connect <headset-ip>:5555" >&2
    exit 1
fi

# List every file under the media roots, newest first, take the top N.
# 2>/dev/null per root so a missing folder does not abort the listing.
FILES=$(adb shell "find $ROOTS -type f 2>/dev/null | xargs -r ls -1t 2>/dev/null | head -n $COUNT" | tr -d '\r')

if [ -z "$FILES" ]; then
    echo "No capture files found under: $ROOTS" >&2
    echo "Take a screenshot in the headset first." >&2
    exit 1
fi

mkdir -p "$DEST"

n=0
while IFS= read -r file; do
    [ -z "$file" ] && continue
    name=$(basename "$file")
    echo "Pulling $name"
    adb pull "$file" "$DEST/$name" >/dev/null
    echo "  -> $DEST/$name"
    n=$((n + 1))
done <<< "$FILES"

echo
echo "Done. $n file(s) in $DEST"
