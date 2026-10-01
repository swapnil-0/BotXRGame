#!/usr/bin/env python3
"""
Export a YOLO detector for the headset's Cup Detect mode, and (optionally)
check it on a photo of YOUR cups before building anything.

Why the photo check exists: the plan relies on COCO's pretrained 'cup' class
(id 41) firing on red solo cups. If it does not, no amount of Unity work will
fix that - so find out in ten seconds on the PC, not after a headset build.

Setup (once):
    pip install ultralytics onnx onnxslim

Typical use (from the repo root, in git-bash):
    python tools/export_cup_model.py --test-image path/to/cups.jpg
    python tools/export_cup_model.py

The .onnx lands in unity/BotXRGame/Assets/Models/CupDetect/. Then in Unity run
Tools > BotXRGame > Set Up Cup Detect.

LICENSE NOTE: Ultralytics YOLO models are AGPL-3.0. Fine for a research
prototype; check with your program before shipping it in anything public. A
permissively licensed alternative is YOLOv9 from MultimediaTechLab (MIT), which
is what Meta's Quest sample uses - the Unity decoder accepts either layout.
"""
import argparse
import shutil
import sys
from pathlib import Path

COCO_CUP = 41
CUP_LIKE = {39: "bottle", 40: "wine glass", 41: "cup", 45: "bowl"}

REPO = Path(__file__).resolve().parent.parent
DEST = REPO / "unity" / "BotXRGame" / "Assets" / "Models" / "CupDetect"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", default="yolo11n.pt",
                    help="Ultralytics weights to start from (downloaded on first use). "
                         "'n' = nano, the only size that suits a headset.")
    ap.add_argument("--imgsz", type=int, default=320,
                    help="Square input size. MUST match CupDetector.inputWidth/Height "
                         "(320 by default). 320 keeps inference cheap; 416 sees "
                         "smaller / further cups at roughly 1.7x the cost.")
    ap.add_argument("--opset", type=int, default=17,
                    help="ONNX opset. Sentis 2.6 accepts up to 25; 17 is the safe middle.")
    ap.add_argument("--test-image", type=Path,
                    help="Run the model on this photo and report cup-like detections, "
                         "then stop (no export).")
    ap.add_argument("--conf", type=float, default=0.25, help="Score threshold for --test-image.")
    args = ap.parse_args()

    try:
        from ultralytics import YOLO
    except ImportError:
        print("ultralytics not installed:  pip install ultralytics onnx onnxslim")
        return 1

    model = YOLO(args.model)

    if args.test_image:
        return test_on_photo(model, args)

    print(f"Exporting {args.model} -> ONNX, {args.imgsz}x{args.imgsz}, opset {args.opset}")
    out = model.export(format="onnx", imgsz=args.imgsz, opset=args.opset,
                       simplify=True, dynamic=False, half=False)
    out = Path(out)

    DEST.mkdir(parents=True, exist_ok=True)
    target = DEST / f"{out.stem}_{args.imgsz}.onnx"
    shutil.copy2(out, target)
    print(f"\nCopied to {target.relative_to(REPO)}")

    report_output_shape(target)

    print("\nNext: in Unity, Tools > BotXRGame > Set Up Cup Detect, then save and build.")
    return 0


def test_on_photo(model, args) -> int:
    """Desktop sanity check on a real photo of the real cups."""
    if not args.test_image.exists():
        print(f"No such file: {args.test_image}")
        return 1

    res = model.predict(str(args.test_image), imgsz=args.imgsz, conf=args.conf, verbose=False)[0]
    names = res.names

    cupish, other = [], []
    for b in res.boxes:
        cls = int(b.cls)
        score = float(b.conf)
        (cupish if cls in CUP_LIKE else other).append((names[cls], cls, score))

    print(f"\n{args.test_image.name} at {args.imgsz}px, conf >= {args.conf}:")
    if not cupish and not other:
        print("  nothing detected at all - try a closer photo, or --imgsz 416")
    for name, cls, score in sorted(cupish, key=lambda t: -t[2]):
        flag = "  <- counted by default" if cls == COCO_CUP else \
               f"  <- add {cls} to CupDetector.cupClassIds to count these"
        print(f"  {name:12} (id {cls:2})  {score:.2f}{flag}")
    for name, cls, score in sorted(other, key=lambda t: -t[2])[:8]:
        print(f"  {name:12} (id {cls:2})  {score:.2f}   (ignored)")

    annotated = args.test_image.with_name(args.test_image.stem + "_detected.jpg")
    res.save(filename=str(annotated))
    print(f"\nAnnotated image: {annotated}")

    n_cup = sum(1 for _, c, _ in cupish if c == COCO_CUP)
    if n_cup == 0 and cupish:
        print("\nVERDICT: cups are seen but under another label - adjust cupClassIds.")
    elif n_cup == 0:
        print("\nVERDICT: the pretrained model does not see these cups. Fine-tuning "
              "on ~100 labelled photos of your cups is the next step.")
    else:
        print(f"\nVERDICT: {n_cup} cup(s) found as 'cup' - the pretrained route works.")
    return 0


def report_output_shape(path: Path) -> None:
    """Print the output tensor shape - the Unity decoder picks its layout from it."""
    try:
        import onnx
        m = onnx.load(str(path))
        for o in m.graph.output:
            dims = [d.dim_value or d.dim_param for d in o.type.tensor_type.shape.dim]
            print(f"Output '{o.name}': {dims}   (expect [1, 84, 2100] at 320px)")
    except Exception as e:  # report, don't fail the export
        print(f"(could not read output shape: {e})")


if __name__ == "__main__":
    sys.exit(main())
