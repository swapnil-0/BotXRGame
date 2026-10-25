#!/usr/bin/env bash
#
# YOLO .onnx -> SNPE .dlc (float) -> quantized .dlc for the Hexagon NPU.
# Run INSIDE WSL Ubuntu 22.04 (the QAIRT tools are Linux/Python 3.10):
#
#   wsl -d Ubuntu-22.04 -- bash /mnt/c/Users/swapn/Documents/BotXRGame/tools/npu/convert_snpe.sh
#
# Env overrides: QAIRT_ROOT, VENV, ONNX, OUT.
#
# Outputs are cut BEFORE YOLO's final Concat: boxes (/model.23/Mul_2, pixels
# 0..320) and class scores (/model.23/Sigmoid, 0..1) stay separate tensors.
# One concatenated 8-bit tensor would spend its range on the boxes and crush
# every score to a few levels (Qualcomm AI Developer Workflow, sec. 8.1).
# The app's decoder must read the two outputs instead of output0.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
QAIRT_ROOT="${QAIRT_ROOT:-/mnt/c/Users/swapn/Documents/qairt/2.32.6.250402}"
VENV="${VENV:-$HOME/qairt-venv}"
ONNX="${ONNX:-$REPO/unity/BotXRGame/Assets/Models/CupDetect/yolo11n_320.onnx}"
OUT="${OUT:-$REPO/tools/npu/build}"
NAME="$(basename "${ONNX%.onnx}")"

# Tensor names, not node names - --out_node matches tensors.
BOXES=/model.23/Mul_2_output_0
SCORES=/model.23/Sigmoid_output_0

# The venv and SDK setup scripts read unset variables; -u would abort them.
set +u
source "$VENV/bin/activate"
export QAIRT_ROOT
source "$QAIRT_ROOT/bin/envsetup.sh" >/dev/null
set -u
mkdir -p "$OUT"

echo "== 1. convert $ONNX"
snpe-onnx-to-dlc -i "$ONNX" --out_node "$BOXES" --out_node "$SCORES" -o "$OUT/$NAME.dlc"

echo "== 2. input layout of the DLC"
snpe-dlc-info -i "$OUT/$NAME.dlc" > "$OUT/$NAME.info.txt" 2>&1 || true
grep -i -E "input|output|images|Mul_2|Sigmoid" "$OUT/$NAME.info.txt" | head -20 || true
LAYOUT=NHWC
grep -q -E "images.*1,3,320,320|images.*\[1, 3, 320, 320\]" "$OUT/$NAME.info.txt" && LAYOUT=NCHW
echo "calibration layout: $LAYOUT"

echo "== 3. calibration inputs"
python "$REPO/tools/npu/make_calib.py" --layout "$LAYOUT" --out "$OUT/calib"

echo "== 4. quantize (8-bit weights and activations)"
snpe-dlc-quant --input_dlc "$OUT/$NAME.dlc" --input_list "$OUT/calib/input_list.txt" \
               --output_dlc "$OUT/${NAME}_q.dlc"

echo
echo "Done:"
ls -la "$OUT/$NAME.dlc" "$OUT/${NAME}_q.dlc"
echo "Next: tools/npu/run_on_headset.sh pushes it with snpe-net-run and times it on the NPU."
