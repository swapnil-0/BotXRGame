#!/usr/bin/env bash
#
# Build native/cupnpu -> libcupnpu.so (arm64-v8a) with Unity's bundled NDK,
# and stage the SNPE runtime libraries next to it so the APK carries them.
#
#   ./tools/npu/build_plugin.sh            (git-bash, repo root)
#
# Staged into unity/BotXRGame/Assets/Plugins/Android/libs/arm64-v8a/ (Unity
# packs that folder into the APK's lib/arm64-v8a). The SNPE libraries are the
# SDK's and are gitignored - re-run this after a fresh clone.
#
# The NPU-side skel (libSnpeHtpV69Skel.so) is NOT a CPU library: it goes in
# StreamingAssets, and the app copies it to a plain folder at runtime and
# points ADSP_LIBRARY_PATH there (FastRPC reads it as a file).
#
# Env overrides: QAIRT_WIN, NDK.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
QAIRT_WIN="${QAIRT_WIN:-/c/Users/swapn/Documents/qairt/2.32.6.250402}"
NDK="${NDK:-/c/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Data/PlaybackEngines/AndroidPlayer/NDK}"
API=29
CC="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android$API-clang.cmd"

PLUG="$REPO/unity/BotXRGame/Assets/Plugins/Android/libs/arm64-v8a"
STREAM="$REPO/unity/BotXRGame/Assets/StreamingAssets/CupNpu"
mkdir -p "$PLUG" "$STREAM"

[ -f "$CC" ] || { echo "NDK clang not found: $CC" >&2; exit 1; }

echo "== compile libcupnpu.so"
"$CC" -shared -fPIC -O2 -fvisibility=hidden -Wall \
    -I"$(cygpath -m "$QAIRT_WIN/include/SNPE")" \
    "$(cygpath -m "$REPO/native/cupnpu/cupnpu.c")" \
    -L"$(cygpath -m "$QAIRT_WIN/lib/aarch64-android")" -lSNPE -llog \
    -Wl,-soname,libcupnpu.so \
    -o "$(cygpath -m "$PLUG/libcupnpu.so")"
ls -la "$PLUG/libcupnpu.so"

echo "== stage SNPE runtime (CPU side) -> Plugins/Android/libs/arm64-v8a"
for f in libSNPE.so libSnpeHtpV69Stub.so libSnpeHtpPrepare.so; do
    cp "$QAIRT_WIN/lib/aarch64-android/$f" "$PLUG/"
done

echo "== stage NPU skel + model -> StreamingAssets/CupNpu"
cp "$QAIRT_WIN/lib/hexagon-v69/unsigned/libSnpeHtpV69Skel.so" "$STREAM/"
cp "$REPO/tools/npu/build/yolo11n_320_q.dlc" "$STREAM/"

ls -la "$PLUG" "$STREAM"
