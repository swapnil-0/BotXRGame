#!/usr/bin/env bash
#
# Run the quantized cup model on the headset's Hexagon NPU with the SDK's
# snpe-net-run, from adb shell. No app involved: answers "does this model run
# on this NPU, how fast, and which route works" before any plugin is written.
#
#   ./tools/npu/run_on_headset.sh [adb-serial]        (git-bash, repo root)
#
# Routes tried, each reported OK/FAIL with its timing:
#   A  our SDK runtime (2.32) + our unsigned V69 skel, unsignedPD:ON
#      -> the route an app would ship (libs inside the APK)
#   B  the headset's own SNPE (2.25, /vendor) + its signed skel
#      -> the officially exposed route; may reject a DLC from a newer SDK
#   CPU  same binary on the CPU, as the baseline
#
# Env overrides: ADB, QAIRT_WIN (SDK path on Windows), DLC, RUNS.

set -uo pipefail
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
ADB="${ADB:-adb}"
QAIRT_WIN="${QAIRT_WIN:-/c/Users/swapn/Documents/qairt/2.32.6.250402}"
DLC="${DLC:-$REPO/tools/npu/build/yolo11n_320_q.dlc}"
RUNS="${RUNS:-30}"
NDK_LIBCXX="/c/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Data/PlaybackEngines/AndroidPlayer/NDK/toolchains/llvm/prebuilt/windows-x86_64/sysroot/usr/lib/aarch64-linux-android/libc++_shared.so"
DEV=/data/local/tmp/cupnpu

SERIAL="${1:-}"
[ -z "$SERIAL" ] && SERIAL=$("$ADB" devices | tr -d '\r' | awk 'NR>1 && $2=="device" {print $1; exit}')
[ -z "$SERIAL" ] && { echo "No device on adb." >&2; exit 1; }
A=("$ADB" -s "$SERIAL")
echo "device: $SERIAL"
[ -f "$DLC" ] || { echo "No DLC at $DLC - run tools/npu/convert_snpe.sh first." >&2; exit 1; }

echo "== push"
"${A[@]}" shell "rm -rf $DEV; mkdir -p $DEV/lib $DEV/dsp" >/dev/null
# adb.exe is a Windows program: with path conversion off (needed for the
# device-side paths), local /c/... paths must be handed over as C:\...
push() {
    local src="$1"
    command -v cygpath >/dev/null && src="$(cygpath -w "$1")"
    "${A[@]}" push "$src" "$2" >/dev/null || echo "push failed: $1"
}
push "$QAIRT_WIN/bin/aarch64-android/snpe-net-run" "$DEV/"
for f in libSNPE.so libSnpeHtpPrepare.so libSnpeHtpV69Stub.so; do
    push "$QAIRT_WIN/lib/aarch64-android/$f" "$DEV/lib/"
done
push "$QAIRT_WIN/lib/hexagon-v69/unsigned/libSnpeHtpV69Skel.so" "$DEV/dsp/"
[ -f "$NDK_LIBCXX" ] && push "$NDK_LIBCXX" "$DEV/lib/"
push "$DLC" "$DEV/model.dlc"

# One calibration input, repeated RUNS times so the timing averages over runs.
RAW=$(ls "$REPO"/tools/npu/build/calib/*.raw | head -1)
push "$RAW" "$DEV/input.raw"
"${A[@]}" shell "cd $DEV && rm -f list.txt && for i in \$(seq $RUNS); do echo $DEV/input.raw >> list.txt; done; chmod 755 snpe-net-run"

# Run one route; print the result line and the average inference time.
run() {
    local name="$1" libpath="$2" dsppath="$3" flags="$4"
    echo
    echo "== route $name"
    "${A[@]}" shell "cd $DEV && rm -rf out_$name && \
        export LD_LIBRARY_PATH=$libpath && export ADSP_LIBRARY_PATH='$dsppath' && \
        t0=\$(date +%s%N); \
        ./snpe-net-run --container model.dlc --input_list list.txt --output_dir out_$name $flags \
            > log_$name.txt 2>&1; rc=\$?; t1=\$(date +%s%N); \
        echo exit=\$rc  wall_ms=\$(( (t1 - t0) / 1000000 ))  runs=$RUNS; \
        tail -4 log_$name.txt; \
        ls out_$name 2>/dev/null | head -3" | tr -d '\r'
    local dst="$REPO/tools/npu/build/SNPEDiag_$name.log"
    command -v cygpath >/dev/null && dst="$(cygpath -w "$dst")"
    "${A[@]}" pull "$DEV/out_$name/SNPEDiag.log" "$dst" >/dev/null 2>&1 \
        && echo "(diag log -> tools/npu/build/SNPEDiag_$name.log)"
}

OURS="$DEV/lib"
VENDOR_DSP="/vendor/lib/rfsa/adsp;/vendor/dsp/cdsp;/system/lib/rfsa/adsp;/dsp"

run A   "$OURS"              "$DEV/dsp;$VENDOR_DSP" "--use_dsp --platform_options unsignedPD:ON --perf_profile burst --profiling_level basic"
run B   "/vendor/lib64:$OURS" "$VENDOR_DSP"         "--use_dsp --perf_profile burst --profiling_level basic"
run CPU "$OURS"              "$DEV/dsp;$VENDOR_DSP" "--profiling_level basic"

echo
echo "Per-inference timings: tools/npu/diag_summary.sh reads the pulled SNPEDiag logs."
