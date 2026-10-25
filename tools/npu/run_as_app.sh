#!/usr/bin/env bash
#
# Same NPU test as run_on_headset.sh, but executed AS THE APP via run-as, so
# it runs with the app's uid and an app SELinux domain instead of adb shell's.
#
# Why: on this headset the adb shell may not read /vendor/dsp, where FastRPC
# keeps the NPU-side process shell - so every NPU route fails from a plain
# adb shell ("open_shell failed ... Permission denied") regardless of the
# model. Apps are offered libcdsprpc.so, so they should be allowed.
#
# Needs a DEBUGGABLE install (BatchBuild -development) and the files that
# run_on_headset.sh pushed to /data/local/tmp/cupnpu.
#
#   ./tools/npu/run_as_app.sh [adb-serial]

set -uo pipefail
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

ADB="${ADB:-adb}"
PKG=com.swapnil.BotXRGame
SRC=/data/local/tmp/cupnpu
RUNS="${RUNS:-30}"

SERIAL="${1:-}"
[ -z "$SERIAL" ] && SERIAL=$("$ADB" devices | tr -d '\r' | awk 'NR>1 && $2=="device" {print $1; exit}')
[ -z "$SERIAL" ] && { echo "No device on adb." >&2; exit 1; }
A=("$ADB" -s "$SERIAL")
echo "device: $SERIAL"

if ! "${A[@]}" shell "run-as $PKG id" | grep -q uid; then
    echo "run-as refused: install a debuggable build (BatchBuild -development) first." >&2
    exit 1
fi
echo "app identity: $("${A[@]}" shell "run-as $PKG id" | tr -d '\r')"

# Copy into the app's private dir: run-as can read /data/local/tmp, and the
# app dir is where an app's own files would live.
"${A[@]}" shell "run-as $PKG sh -c 'rm -rf files/cupnpu && mkdir -p files/cupnpu && cp -r $SRC/. files/cupnpu/ && chmod 755 files/cupnpu/snpe-net-run && \
    cd files/cupnpu && rm -f list.txt && for i in \$(seq $RUNS); do echo \$PWD/input.raw >> list.txt; done && pwd'" | tr -d '\r'

run() {
    local name="$1" flags="$2"
    echo
    echo "== as app: $name"
    "${A[@]}" logcat -c
    "${A[@]}" shell "run-as $PKG sh -c 'cd files/cupnpu && export LD_LIBRARY_PATH=\$PWD/lib && \
        export ADSP_LIBRARY_PATH=\"\$PWD/dsp;/vendor/lib/rfsa/adsp;/vendor/dsp/cdsp;/system/lib/rfsa/adsp;/dsp\" && \
        rm -rf out_$name && t0=\$(date +%s%N) && \
        ./snpe-net-run --container model.dlc --input_list list.txt --output_dir out_$name $flags > log_$name.txt 2>&1; \
        rc=\$?; t1=\$(date +%s%N); echo exit=\$rc wall_ms=\$(( (t1 - t0) / 1000000 )) runs=$RUNS; tail -3 log_$name.txt'" | tr -d '\r'
    echo "-- fastrpc / runtime errors:"
    "${A[@]}" logcat -d | grep -E "open_shell|apps_dev_init|remote_handle64_open|<E>|avc:.*(dsp|rpc)" | head -6
}

run dsp_unsigned "--use_dsp --platform_options=unsignedPD:ON --perf_profile burst"
run cpu          ""
