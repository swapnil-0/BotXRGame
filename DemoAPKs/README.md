# Demo APKs

Installable builds of the headset app, kept for demos and for trying other
Android XR devices. Stored with **Git LFS** (each is ~265 MB; GitHub refuses
files over 100 MB in plain git) - clone with `git lfs install` done first, or
run `git lfs pull` afterwards.

| File | What | Built from |
|---|---|---|
| `BotXRGame-v2.1.1-demo-2026-10-30.apk` | **Latest** - use this | `4f153b18` |
| `BotXRGame-v2.1.1-demo-2026-10-29-GalaxyXR.apk` | Previous build, kept for comparison | `6f4e05ac` |

One APK runs on both Samsung Galaxy XR and XREAL Aura (Aura untested). App id
`com.swapnil.BotXRGame`, version 2.1.1 - both builds carry the same version
number, so the file date tells them apart.

**In the latest build:** experience menu by hand pinch; Bluetooth gamepad drives the
ship and the robot (left stick move/strafe, right stick turn, A sweep, B kick,
X start); AprilTag mode with approach, START and tornado pull; Link Test;
Cup Detect; Distance Test; Controller Test; tap-to-fill recent robot addresses;
**Back** (to the menu) and **Restart** buttons in every experience; Virtual Bot
skips the connection screen. The 2026-10-29 build has none of the last three.

**Installing on XREAL Aura (or from any other PC):** see
[`XREAL-Aura-Guide.md`](XREAL-Aura-Guide.md) - PC setup, Git LFS, adb,
device prep, install, logs, controls, what to test, troubleshooting.

## Install (quick)

```bash
adb devices
adb install -r DemoAPKs/BotXRGame-v2.1.1-demo-2026-10-30.apk
adb shell am start -n com.swapnil.BotXRGame/com.unity3d.player.UnityPlayerGameActivity
```

With more than one device attached, add `-s <serial>` from `adb devices`.
