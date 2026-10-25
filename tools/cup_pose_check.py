#!/usr/bin/env python3
"""
Desktop mirror of CupDetector's colour pass and pose estimate: red blobs ->
UPRIGHT / UPSIDE DOWN / TIPPED, on a photo, in seconds instead of a headset
build.

Keep this in step with CupDetector.cs (ReadMask, FindBlobs, EstimatePose,
EstimateTipped, WhiteAboveRatio). Thresholds are the C# defaults.

Usage (repo root, git-bash):
    python tools/cup_pose_check.py cups.jpg cups1.jpg
    python tools/cup_pose_check.py --save cups.jpg     # writes cups_pose.jpg

Photos larger than 1280 px are resized to 1280 on the long side, like the
headset camera frame; headset tuning frames (tools/pull-cup-frames.sh) are used
as they are.
"""
import argparse
import math
import sys
from pathlib import Path

GRID_WIDTH = 640         # colourGridWidth (stride = frame width / this)
MIN_BLOB_CELLS = 6       # minBlobCells
MIN_CELLS_FOR_POSE = 15  # minCellsForPose
MAX_RING_RED = 0.10      # maxRingRed
MAX_RING_DARK = 0.25     # maxRingDark
TIPPED_ELONGATION = 1.35 # tippedElongation
WHITE_ABOVE_UPRIGHT = 0.20
UPSIDE_DOWN_TAPER = 1.05


STRIDE = 2  # set per image in analyse()


def classify(r, g, b):
    """ReadMask's integer HSV: (red, saturation, value)."""
    mx, mn = max(r, g, b), min(r, g, b)
    delta = mx - mn
    sat = delta / mx if mx > 0 else 0.0
    red = (mx == r and delta > 0 and mx > 46 and sat > 0.40
           and (g - b) < 0.33 * delta and (g - b) > -0.36 * delta)
    return red, sat, mx / 255


def analyse(path, save=False):
    from PIL import Image, ImageOps, ImageDraw

    global STRIDE
    im = ImageOps.exif_transpose(Image.open(path)).convert("RGB")
    w, h = im.size
    if max(w, h) > 1280:
        sc = 1280 / max(w, h)
        im = im.resize((int(w * sc), int(h * sc)))
    W, H = im.size
    STRIDE = max(1, W // GRID_WIDTH)
    px = im.load()
    gw, gh = W // STRIDE, H // STRIDE

    S = [[0.0] * gw for _ in range(gh)]
    V = [[0.0] * gw for _ in range(gh)]
    M = [[False] * gw for _ in range(gh)]
    for y in range(gh):
        for x in range(gw):
            M[y][x], S[y][x], V[y][x] = classify(*px[x * STRIDE, y * STRIDE])

    # FindBlobs: 8-connected red regions of cup-like shape.
    lab = [[0] * gw for _ in range(gh)]
    blobs, n = [], 0
    for y in range(gh):
        for x in range(gw):
            if not M[y][x] or lab[y][x]:
                continue
            n += 1
            stack, cells = [(x, y)], []
            lab[y][x] = n
            while stack:
                cx, cy = stack.pop()
                cells.append((cx, cy))
                for dy in (-1, 0, 1):
                    for dx in (-1, 0, 1):
                        nx, ny = cx + dx, cy + dy
                        if 0 <= nx < gw and 0 <= ny < gh and M[ny][nx] and not lab[ny][nx]:
                            lab[ny][nx] = n
                            stack.append((nx, ny))
            xs = [c[0] for c in cells]
            ys = [c[1] for c in cells]
            x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
            bw, bh = x1 - x0 + 1, y1 - y0 + 1
            fill, aspect = len(cells) / (bw * bh), bw / bh
            if len(cells) < MIN_BLOB_CELLS or len(cells) > gw * gh / 4:
                continue
            if fill < 0.30 or aspect < 0.3 or aspect > 3.5:
                continue
            if elongation(cells) > 3.0:
                continue
            if in_clutter(M, V, gw, gh, x0, x1, y0, y1):
                print(f"   (clutter) box px {x0*STRIDE},{y0*STRIDE}-{(x1+1)*STRIDE},{(y1+1)*STRIDE}")
                continue
            blobs.append((x0, y0, x1, y1))

    print(f"{path}: {len(blobs)} blob(s)  (tiny far-away blobs are dropped later by the tracker's size check)")
    results = []
    for (x0, y0, x1, y1) in blobs:
        pose, info = estimate_pose(M, S, V, gw, gh, x0, y0, x1, y1)
        results.append(((x0, y0, x1, y1), pose))
        print(f"   box px {x0*STRIDE},{y0*STRIDE}-{(x1+1)*STRIDE},{(y1+1)*STRIDE}  -> {pose:11}  {info}")

    if save:
        d = ImageDraw.Draw(im)
        for (x0, y0, x1, y1), pose in results:
            d.rectangle((x0 * STRIDE, y0 * STRIDE, (x1 + 1) * STRIDE, (y1 + 1) * STRIDE), outline=(0, 255, 120), width=2)
            d.text((x0 * STRIDE, max(0, y0 * STRIDE - 12)), pose, fill=(0, 255, 120))
        out = Path(path).with_name(Path(path).stem + "_pose.jpg")
        im.save(out)
        print(f"   saved {out}")


def in_clutter(M, V, gw, gh, x0, x1, y0, y1):
    bw, bh = x1 - x0 + 1, y1 - y0 + 1
    px_, py_ = max(2, bw // 2), max(2, bh // 2)
    total = red = dark = 0
    for y in range(max(0, y0 - py_), min(gh - 1, y1 + py_) + 1):
        for x in range(max(0, x0 - px_), min(gw - 1, x1 + px_) + 1):
            if x0 <= x <= x1 and y0 <= y <= y1:
                continue
            total += 1
            if M[y][x]:
                red += 1
            elif V[y][x] < 0.20:
                dark += 1
    return total > 0 and (red > MAX_RING_RED * total or dark > MAX_RING_DARK * total)


def elongation(cells):
    n = len(cells)
    mx = sum(c[0] for c in cells) / n
    my = sum(c[1] for c in cells) / n
    cxx = sum((c[0] - mx) ** 2 for c in cells) / n
    cyy = sum((c[1] - my) ** 2 for c in cells) / n
    cxy = sum((c[0] - mx) * (c[1] - my) for c in cells) / n
    half = (cxx + cyy) / 2
    disc = math.sqrt(max(0.0, half * half - (cxx * cyy - cxy * cxy)))
    return math.sqrt((half + disc) / max(1e-4, half - disc))


def estimate_pose(M, S, V, gw, gh, x0, y0, x1, y1):
    pts = [(x, y) for y in range(y0, y1 + 1) for x in range(x0, x1 + 1) if M[y][x]]
    if len(pts) < MIN_CELLS_FOR_POSE:
        return "pose ?", f"only {len(pts)} red cells - too small to read"
    N = len(pts)
    mx = sum(p[0] for p in pts) / N
    my = sum(p[1] for p in pts) / N
    cxx = sum((p[0] - mx) ** 2 for p in pts) / N
    cyy = sum((p[1] - my) ** 2 for p in pts) / N
    cxy = sum((p[0] - mx) * (p[1] - my) for p in pts) / N
    half = (cxx + cyy) / 2
    disc = math.sqrt(max(0.0, half * half - (cxx * cyy - cxy * cxy)))
    elong = math.sqrt((half + disc) / max(1e-4, half - disc))
    theta = 0.5 * math.atan2(2 * cxy, cxx - cyy)
    from_horizontal = abs(math.degrees(theta))
    shape = f"elong {elong:.2f}, axis {from_horizontal:.0f} deg from horizontal"

    if elong >= TIPPED_ELONGATION and from_horizontal < 40:
        return "TIPPED", shape + "; " + tipped_ends(pts, mx, my, theta)

    # WhiteAboveRatio
    bw, bh = x1 - x0 + 1, y1 - y0 + 1
    ring = []
    for y in range(max(0, y0 - bh // 2), min(gh - 1, y1 + bh // 2) + 1):
        for x in range(max(0, x0 - bw // 2), min(gw - 1, x1 + bw // 2) + 1):
            if x0 - 1 <= x <= x1 + 1 and y0 - 1 <= y <= y1 + 1:
                continue
            ring.append(V[y][x])
    if not ring:
        return "pose ?", "no floor ring"
    ring.sort()
    floor_v = ring[len(ring) // 2]
    white = 0
    for y in range(max(0, y0 - round(0.6 * bh)), y0 + bh // 2 + 1):
        for x in range(x0, x1 + 1):
            if not M[y][x] and S[y][x] < 0.25 and V[y][x] > floor_v + 0.10:
                white += 1
    white_above = white / max(1, N)
    if white_above >= WHITE_ABOVE_UPRIGHT:
        return "UPRIGHT", f"{shape}; white above {white_above:.2f}"

    mid = (y0 + y1) / 2
    top_max = bottom_max = rows = 0
    for y in range(y0, y1 + 1):
        xs = [x for x in range(x0, x1 + 1) if M[y][x]]
        if not xs:
            continue
        rows += 1
        wdt = xs[-1] - xs[0] + 1
        if y <= mid:
            top_max = max(top_max, wdt)
        else:
            bottom_max = max(bottom_max, wdt)
    if rows < 3 or bottom_max == 0:
        return "pose ?", shape
    taper = top_max / bottom_max
    info = f"{shape}; white above {white_above:.2f}; taper {taper:.2f}"
    if taper < UPSIDE_DOWN_TAPER:
        return "UPSIDE DOWN", info
    return "TIPPED", info + "; " + tipped_ends(pts, mx, my, theta)


def tipped_ends(pts, mx, my, theta):
    ax, ay = math.cos(theta), math.sin(theta)
    s = [(p[0] - mx) * ax + (p[1] - my) * ay for p in pts]
    smin, smax = min(s), max(s)
    cut = 0.3 * (smax - smin)
    a, b = [], []
    for p, si in zip(pts, s):
        pp = -(p[0] - mx) * ay + (p[1] - my) * ax
        if si <= smin + cut:
            a.append(pp)
        if si >= smax - cut:
            b.append(pp)
    wa = max(a) - min(a) + 1 if a else 0
    wb = max(b) - min(b) + 1 if b else 0
    mouth = smax if wb >= wa else smin
    base = smin if wb >= wa else smax
    m = ((mx + ax * mouth) * STRIDE, (my + ay * mouth) * STRIDE)
    bs = ((mx + ax * base) * STRIDE, (my + ay * base) * STRIDE)
    return f"mouth at px {m[0]:.0f},{m[1]:.0f}, base at {bs[0]:.0f},{bs[1]:.0f}"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("photos", nargs="+", type=Path)
    ap.add_argument("--save", action="store_true", help="write <photo>_pose.jpg with boxes and poses")
    args = ap.parse_args()
    try:
        import PIL  # noqa: F401
    except ImportError:
        print("Pillow not installed:  pip install pillow")
        return 1
    for p in args.photos:
        analyse(str(p), args.save)
    return 0


if __name__ == "__main__":
    sys.exit(main())
