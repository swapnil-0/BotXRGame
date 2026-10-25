#!/usr/bin/env bash
#
# Read-only probe: can an app on this headset reach the Hexagon NPU (cDSP/HTP)?
# Changes nothing on the device. Run with the headset awake and on adb:
#
#   ./tools/npu_probe.sh [adb-serial] | tee captures/npu_probe.txt
#
# What decides app-level NPU access (no root):
#   - libcdsprpc.so listed in /vendor/etc/public.libraries.txt, so an app can
#     load it via <uses-native-library> in its manifest (Android 12+)
#   - a FastRPC device node the untrusted_app SELinux domain may open
#     (/dev/fastrpc-cdsp, or the "-secure" variant), and unsigned PDs allowed
#     on the cDSP so an app can offload without a device-specific testsig
#   - the Hexagon arch (v69/v73/...), which picks the QNN HTP stub/skel pair

set -uo pipefail
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

ADB="${ADB:-adb}"
SERIAL="${1:-}"
# A wireless headset often shows twice (IP:port and its mDNS name) - same
# device, but adb then refuses every command. Take the first one.
if [ -z "$SERIAL" ]; then
    SERIAL=$("$ADB" devices | tr -d '\r' | awk 'NR>1 && $2=="device" {print $1; exit}')
fi
if [ -z "$SERIAL" ]; then
    echo "No device on adb. Wake the headset (Wireless debugging on) and retry." >&2
    exit 1
fi
echo "device: $SERIAL"
S=(-s "$SERIAL")
sh() { "$ADB" "${S[@]}" shell "$@" 2>&1 | tr -d '\r'; }
section() { echo; echo "=== $1"; }

section "device / build"
sh 'for p in ro.product.model ro.build.fingerprint ro.build.type ro.build.tags \
            ro.soc.manufacturer ro.soc.model ro.board.platform ro.hardware \
            ro.boot.verifiedbootstate ro.boot.flash.locked ro.oem_unlock_supported \
            sys.oem_unlock_allowed ro.debuggable ro.secure; do
          printf "%-32s %s\n" "$p" "$(getprop $p)"; done'
sh 'getenforce'

section "FastRPC device nodes (need: one an app may open)"
sh 'ls -lZ /dev/*fastrpc* /dev/adsprpc-smd* 2>&1'

section "libcdsprpc.so exposed to apps? (/vendor/etc/public.libraries*.txt)"
sh 'for f in /vendor/etc/public.libraries*.txt /system/etc/public.libraries*.txt; do
      [ -f "$f" ] && { echo "--- $f"; grep -i -E "cdsprpc|adsprpc|qnn|snpe|hta" "$f" || echo "(no DSP/QNN libs listed)"; }; done'

section "DSP-side libraries and Hexagon arch hints"
sh 'ls /vendor/lib64/ | grep -i -E "cdsprpc|adsprpc|qnn|snpe|hexagon|htp" '
sh 'ls /vendor/dsp/ /vendor/dsp/cdsp/ 2>&1 | head -40'
sh 'ls /vendor/lib/rfsa/adsp/ 2>&1 | grep -i -E "skel|qnn|htp|testsig" | head -40'
sh 'cat /sys/devices/soc0/soc_id /sys/devices/soc0/machine /sys/devices/soc0/family 2>&1'

section "unsigned PD / cDSP status hints"
sh 'getprop | grep -i -E "fastrpc|cdsp|unsigned|hexagon|dsp" | head -20'
sh 'ls -l /sys/kernel/boot_cdsp/ 2>&1; cat /sys/kernel/boot_cdsp/boot 2>&1'

section "SELinux: what the app domain may do with fastrpc (needs policy dump access; may be denied)"
sh 'ls -lZ /vendor/etc/selinux/ 2>&1 | head'

echo
echo "Done. Key lines: getenforce, the /dev/*fastrpc* SELinux labels, whether"
echo "libcdsprpc.so is in a public.libraries file, ro.boot.flash.locked /"
echo "ro.oem_unlock_supported, and any v6x/v7x skel names (Hexagon arch)."
