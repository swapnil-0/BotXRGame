#!/usr/bin/env python3
"""
Calibration inputs for snpe-dlc-quant: float32 .raw files of 320x320 RGB in
[0, 1], preprocessed the way the app feeds the model (CupDetector:
TextureConverter.ToTensor stretches the whole camera frame to the model size,
no letterbox), plus an input_list.txt naming them.

Sources: the cup photos at the repo root and any headset tuning frames in
captures/cupframes/ (tools/pull-cup-frames.sh). Headset frames are the better
calibration data - real exposure and lens - so pull some when you can. Each
image also yields a few crops and flips, so a handful of photos still gives the
quantizer a spread of scales.

Run inside the QAIRT venv (WSL), from anywhere:
    python tools/npu/make_calib.py [--layout NHWC|NCHW] [--out tools/npu/build/calib]
The layout must match the converted DLC's input (snpe-dlc-info shows it).
"""
import argparse
import glob
import os
import random

import numpy as np
from PIL import Image, ImageOps

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SIZE = 320


def sources():
    files = sorted(glob.glob(os.path.join(REPO, "cups*.jpg")))
    files = [f for f in files if not f.endswith(("_detected.jpg", "_pose.jpg"))]
    files += sorted(glob.glob(os.path.join(REPO, "captures", "cupframes", "*.jpg")))
    return files


def variants(im, rng, n):
    """The full frame, a mirror, and random crops (a cup further away / off-centre)."""
    yield im
    yield ImageOps.mirror(im)
    w, h = im.size
    for _ in range(n):
        s = rng.uniform(0.45, 0.85)
        cw, ch = int(w * s), int(h * s)
        x, y = rng.randint(0, w - cw), rng.randint(0, h - ch)
        yield im.crop((x, y, x + cw, y + ch))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--layout", default="NHWC", choices=["NHWC", "NCHW"])
    ap.add_argument("--out", default=os.path.join(REPO, "tools", "npu", "build", "calib"))
    ap.add_argument("--crops", type=int, default=6, help="random crops per image")
    args = ap.parse_args()

    files = sources()
    if not files:
        raise SystemExit("No calibration images: put cups*.jpg at the repo root or pull headset frames.")

    os.makedirs(args.out, exist_ok=True)
    rng = random.Random(0)
    paths = []
    for f in files:
        im = ImageOps.exif_transpose(Image.open(f)).convert("RGB")
        for v in variants(im, rng, args.crops):
            a = np.asarray(v.resize((SIZE, SIZE), Image.BILINEAR), dtype=np.float32) / 255.0  # HWC
            if args.layout == "NCHW":
                a = a.transpose(2, 0, 1)
            p = os.path.join(args.out, "calib_%03d.raw" % len(paths))
            a.astype(np.float32).tofile(p)
            paths.append(p)

    lst = os.path.join(args.out, "input_list.txt")
    with open(lst, "w") as fh:
        fh.write("\n".join(paths) + "\n")
    print("%d images -> %d calibration inputs (%s) in %s" % (len(files), len(paths), args.layout, args.out))
    print("input list:", lst)


if __name__ == "__main__":
    main()
