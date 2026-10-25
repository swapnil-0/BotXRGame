#!/usr/bin/env bash
#
# Pull the camera frames Cup Detect saves for tuning (CupDetector
# "saveTuningFrames": one every few seconds, up to 40 per run) into
# captures/cupframes/, then check them on the PC:
#
#   ./tools/pull-cup-frames.sh
#   python tools/cup_pose_check.py --save captures/cupframes/*.jpg
#
# These are what the detector actually saw - the headset camera at its real
# resolution and exposure, without the passthrough tint or any overlays - so
# thresholds tuned on them carry over, unlike ones tuned on phone photos.
#
# The app overwrites frame_000... on each launch; pull before restarting it.
# Usage: ./tools/pull-cup-frames.sh [adb-serial]

set -euo pipefail
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

ADB="${ADB:-adb}"
SERIAL=()
[ -n "${1:-}" ] && SERIAL=(-s "$1")

SRC="/sdcard/Android/data/com.swapnil.BotXRGame/files/cupframes"
DEST="captures/cupframes"
mkdir -p "$DEST"

if ! "$ADB" "${SERIAL[@]}" shell "ls $SRC" >/dev/null 2>&1; then
    echo "No frames on the headset at $SRC." >&2
    echo "Run Cup Detect for a few seconds first. More than one device? Pass its serial." >&2
    exit 1
fi

"$ADB" "${SERIAL[@]}" pull "$SRC/." "$DEST/"
echo "Frames in $DEST:"
ls "$DEST"
