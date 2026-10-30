# Demo APKs

Installable builds of the headset app, kept for demos and for trying other
Android XR devices. Stored with **Git LFS** (each is ~265 MB; GitHub refuses
files over 100 MB in plain git) - clone with `git lfs install` done first, or
run `git lfs pull` afterwards.

| File | Device | Built from |
|---|---|---|
| `BotXRGame-v2.1.1-demo-2026-10-29-GalaxyXR.apk` | Samsung Galaxy XR | `6f4e05ac` |
| `BotXRGame-v2.1.1-demo-2026-10-29-XREAL-Aura.apk` | XREAL Project Aura (untested) | `6f4e05ac` |

The two files are the **same build** (identical SHA-256), named per device so
each can be handed out on its own. App id `com.swapnil.BotXRGame`, version 2.1.1.

**In this build:** experience menu by hand pinch; Bluetooth gamepad drives the
ship and the robot (left stick move/strafe, right stick turn, A sweep, B kick,
X start); AprilTag mode with approach, START and tornado pull; Link Test;
Cup Detect; Distance Test; Controller Test; tap-to-fill recent robot addresses.

## Install

```bash
adb devices
adb install -r DemoAPKs/BotXRGame-v2.1.1-demo-2026-10-29-GalaxyXR.apk
adb shell am start -n com.swapnil.BotXRGame/com.unity3d.player.UnityPlayerGameActivity
```

With more than one device attached, add `-s <serial>` from `adb devices`.
