#!/usr/bin/env bash
#
# Live Unity log stream from the headset. Leave this running in its own window
# and rebuild in another - the stream comes from the device, not the app, so it
# survives app restarts and reinstalls.
#
# Reconnects by itself. A plain "adb logcat" goes silent when the headset
# sleeps or the cable is nudged, which looks identical to "my app stopped
# logging" and wastes a debugging round.
#
# Usage:
#   ./tools/watch-logs.sh              # everything Unity prints
#   ./tools/watch-logs.sh game         # only our [Cup]/[Arena]/[Tornado] tags
#   ./tools/watch-logs.sh 'Cup'        # any custom grep pattern

set -uo pipefail

export MSYS_NO_PATHCONV=1

PATTERN="${1:-}"
if [ "$PATTERN" = "game" ]; then
    # Any [Tag] rather than a hand-listed set. The explicit list silently hid
    # every [ShipVisualLock] line - including the one naming the transform we
    # were trying to identify - and a filter that quietly drops the evidence
    # you are hunting for is worse than no filter.
    PATTERN='\[[A-Za-z]+\]|Exception|error CS'
fi

if ! command -v adb >/dev/null 2>&1; then
    echo "adb not found on PATH." >&2
    exit 1
fi

echo "Watching Unity logs. Ctrl-C to stop."
[ -n "$PATTERN" ] && echo "Filter: $PATTERN"
echo

while true; do
    adb wait-for-device

    # Clear on each (re)connect so a run is not buried under the old ring
    # buffer. Comment out if you want history preserved across reconnects.
    adb logcat -c 2>/dev/null || true

    echo "--- connected $(date +%H:%M:%S) ---"

    if [ -n "$PATTERN" ]; then
        adb logcat -s Unity:V | grep --line-buffered -E "$PATTERN"
    else
        adb logcat -s Unity:V
    fi

    echo "--- disconnected $(date +%H:%M:%S), waiting for device ---"
    sleep 2
done
