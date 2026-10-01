# Cup Detect mode

A standalone test of on-headset vision: the Galaxy XR's passthrough camera feeds
a YOLO detector, and every cup it finds is placed on the floor and tracked with a
number. No robot, no ROS, no AprilTags, no arena.

It answers one question — *can the headset find and follow cups on its own?* —
with nothing else in the scene able to help or hurt the answer.

## How it works

```
passthrough camera (Camera2 id 0, via WebCamTexture)
  -> YOLO on Unity Inference Engine (GPU, a few layers per frame)
  -> boxes for COCO class 41 "cup", optional red-colour check
  -> bottom-centre of each box -> ray from the head pose at capture time
  -> intersect with the floor (AR plane, or calibrated height)
  -> match to existing cups by distance -> numbered, persistent markers
```

Why these choices:

| Decision | Reason |
|---|---|
| WebCamTexture, not AR Foundation | Android XR's provider reports `supportsCameraImage = false`; the camera is only reachable as a normal Camera2 device behind the `CAMERA` permission. |
| GPU, not NPU | The Galaxy XR's XR2+ Gen 2 is not on LiteRT's Qualcomm NPU list, and Inference Engine has no NPU backend. |
| Pretrained COCO YOLO | Works today with no dataset. Fine-tune only if your cups are missed. |
| Floor intersection, no depth | Cups stand on a known surface, so one camera is enough. |
| Tracks age only when in view | Looking away must not forget cups; a cup is dropped only after repeatedly *not* being seen where the camera is looking. |

## Setup (once)

1. **Check the model sees your cups — on the PC, before building anything.**
   Photograph the red cups on your floor from about where you'd stand, then:

   ```bash
   pip install ultralytics onnx onnxslim
   python tools/export_cup_model.py --test-image path/to/cups.jpg
   ```

   The verdict line tells you whether the pretrained `cup` class works, works
   under another label (add that id to `CupDetector.cupClassIds`), or doesn't
   (fine-tune — the next step, not covered here).

2. **Export the model:**

   ```bash
   python tools/export_cup_model.py
   ```

   It writes `unity/BotXRGame/Assets/Models/CupDetect/yolo11n_320.onnx`.

3. **In Unity** (the Sentis / Inference Engine package installs automatically
   from `Packages/manifest.json`):
   - `Tools > BotXRGame > Wire Session Flow` — adds the fourth menu button
   - `Tools > BotXRGame > Set Up Cup Detect` — creates the `CupDetect` object,
     HUD readout, camera preview, marker material, and wires the model
   - Save the scene, build.

4. **First launch:** choose **Cup Detect** and accept the camera permission prompt.

> License: Ultralytics YOLO is AGPL-3.0. Fine for a research prototype; check
> before shipping publicly. MultimediaTechLab's YOLOv9 (MIT) also works — the
> decoder reads either output layout.

## Controls (right controller)

| Input | Action |
|---|---|
| Trigger | Set the floor height from where you point |
| A | Forget all cups |
| B | Show / hide the camera preview |
| Grip | Open the tuner — stick up/down picks, left/right changes, A saves, B resets |

## Reading the HUD

Each line answers a different "why isn't it working":

| Line | Tells you |
|---|---|
| `camera: 'Camera 0' 640x480 @ 30 fps ...` | Frames are arriving. `PERMISSION DENIED` or `NO DEVICES` stops everything here. |
| `camera2 ids ... intrinsics [...]` | What lens calibration the headset reports. `MISSING` means the fallback FOV is in use. |
| `detector: 35 ms/inference 8.0/s raw 3 kept 2` | The model is running. `raw` > 0 but `kept` 0 means the red check or NMS removed them. |
| `tracker: 3 cups (2 confirmed) unplaced 0` | Boxes became floor positions. High `unplaced` means rays miss the floor — set it with the trigger. |
| `#2 TRACKED at 0.41, 1.83 hits 27 ...` | Per-cup state. |

**The camera preview is the main instrument.** Boxes on the cups but markers in
the wrong place → calibration. No boxes on visible cups → the model or the red
check. Boxes upside-down → toggle *flip image* in the tuner.

## Calibrating, in order

1. **Floor** — point at the floor, pull the trigger.
2. **FOV** (only if `intrinsics MISSING`) — put a cup off to one side. Marker too
   far out → FOV too high; too far in → too low.
3. **Camera pitch** — a centred cup's marker sits nearer/further than the cup.
4. **Camera yaw** — markers consistently shifted left or right.
5. **Latency** — turn your head steadily. Markers that swing with the turn and
   settle back mean latency is too low.
6. **A saves** all of it to the headset.

## Known unknowns (the first build will tell us)

- Whether `WebCamTexture` on Android XR delivers the world-facing camera.
- Whether Camera2 reports intrinsics and lens pose for it.
- Inference time on the XR2+ Gen 2 GPU with rendering running.
- Whether the red check can read pixels on this platform — if not, the HUD says
  `red-check N/A` and it stops filtering instead of rejecting every cup.
