# Installing and testing BotXRGame on XREAL Aura

A self-contained guide for installing the demo APK on **XREAL Project Aura**
(an Android XR device) from any PC, by a person or an AI agent with no other
context. Commands are given for **git-bash on Windows**; they are the same in
a Linux or macOS shell.

- App id: **`com.swapnil.BotXRGame`**
- Launch activity: **`com.unity3d.player.UnityPlayerGameActivity`**
- APK for Aura: **`DemoAPKs/BotXRGame-v2.1.1-demo-2026-10-30.apk`** (~265 MB)
- The same APK runs on Galaxy XR. `...-2026-10-29-GalaxyXR.apk` in the same
  folder is the previous build (no Back/Restart buttons), kept for comparison.
- Status: built and tested on Samsung Galaxy XR. **Not yet run on Aura.**

---

## 1. Set up the PC (once)

### Get the repo, including the APK

The APKs are stored with **Git LFS**. Without it, a clone contains only small
text pointer files instead of the APK, and `adb install` fails with an
"invalid APK" / "failed to parse" error.

```bash
git lfs install
git clone https://github.com/swapnil-0/BotXRGame.git
cd BotXRGame
git lfs pull
```

Check it is the real file, not a pointer (should be about 265 MB):

```bash
ls -la DemoAPKs/
```

If Git LFS is missing: Git for Windows includes it; otherwise install from
https://git-lfs.com.

### Install adb

Download **Android SDK Platform-Tools** from
https://developer.android.com/tools/releases/platform-tools, unzip it, and
either add the folder to `PATH` or run `adb` from inside it.

```bash
adb version
```

---

## 2. Prepare the Aura (once)

1. On the Aura, open **Settings → About** and tap the **build number** seven
   times to enable Developer options. (Menu names may differ slightly on Aura;
   look for "About device" and "Build number".)
2. In **Settings → System → Developer options**, turn on **USB debugging**.
3. Connect the Aura's compute unit to the PC with a **USB-C data cable**.
4. On the Aura, accept the **"Allow USB debugging?"** prompt (tick "Always
   allow from this computer").

Optional, wireless instead of USB: in Developer options turn on **Wireless
debugging**, then on the PC run `adb pair <ip>:<pairing-port>` with the code
shown, then `adb connect <ip>:<port>` using the IP and port shown on the main
Wireless debugging screen (not the pairing port).

---

## 3. Install and launch

Find the Aura's serial:

```bash
adb devices
```

It must show `device` (not `unauthorized` - if so, accept the prompt on the
Aura). If more than one device is listed, put `-s <serial>` after `adb` in
every command below.

Install (or update - `-r` keeps the app's saved settings):

```bash
adb install -r DemoAPKs/BotXRGame-v2.1.1-demo-2026-10-30.apk
```

Launch:

```bash
adb shell am start -n com.swapnil.BotXRGame/com.unity3d.player.UnityPlayerGameActivity
```

Or open it from the Aura's app launcher ("BotXRGame").

Uninstall, if needed:

```bash
adb uninstall com.swapnil.BotXRGame
```

---

## 4. Logs (for diagnosing anything that does not work)

Clear the log, launch the app, try the feature, then save the app's log:

```bash
adb logcat -c
adb shell am start -n com.swapnil.BotXRGame/com.unity3d.player.UnityPlayerGameActivity
adb logcat -d -s Unity > aura-unity.log
```

Useful tags inside the Unity log:

| Prefix | Meaning |
|---|---|
| `[Mode]` | Menu loaded / experience chosen |
| `[Hand]` | Once a second: hand devices, aim pose, whether the ray hits UI, pinch state |
| `[Pad]` | Bluetooth gamepad connected / disconnected, with its name |
| `[Bot]` | AprilTag game phases, `START pressed` |
| `[Tag]` | AprilTag tracking |
| `[IP]` | Recent robot address chosen |
| `[Arm]` | Arm command link |

Full device log, if the app crashes or will not start:

```bash
adb logcat -d > aura-full.log
```

---

## 5. Using the app

**Controls.** No XR controllers are used.

- **Hands:** point with the right hand (a white ray appears), **pinch** to
  choose a menu entry, pinch and drag to scroll the list. A right-hand pinch
  also places the arena and presses on-screen buttons. Pinch with the palm
  facing away - palm towards you opens the system menu.
- **Bluetooth gamepad** (tested: 8BitDo Ultimate 2C, mode switch on
  **Bluetooth**): pair it in the Aura's Bluetooth settings (hold the pad's
  pair button ~3 s until the LED blinks fast). Left stick = move / strafe,
  right stick = turn, **A** = arm sweep, **B** = arm kick, **X** = start the
  run, **View** = tuner. The pad never drives the menu.

**Back / Restart.** Inside every experience a small bar floats just above eye
level: **< Back** returns to the menu (the app relaunches, ~10 s), **Restart**
starts the experience over. Pinch a button, then pinch again within 3 s to
confirm.

**Experiences** on the menu:

| Experience | Needs | Notes |
|---|---|---|
| Virtual Bot | pad | Fly a virtual ship; the run starts when the arena is placed |
| AprilTag | robot, AprilTag id 0 (36h11, 100 mm) on it | Robot drives itself to the start line, then press **X** |
| Link Test | robot | Sticks straight to the robot; first thing to try with a robot |
| Cup Detect | camera access | Finds red cups with the device camera |
| Distance Test | depth / planes | Aim the right hand, pinch to measure |
| NPU Test | - | Qualcomm SNPE probe; diagnostic only |
| Controller Test | pad | Live gamepad readout - use it to check pairing |

**Robot connection.** The robot runs a ROS TCP endpoint on port **10000**.
After choosing a robot experience, tap one of the recent-address buttons or
type the robot's IP, keep port 10000, and press Connect. The device and the
robot must be on the same network. Robot bring-up is in
`Qualcomm_IQ9_Jetrover_Integration_Guide.md` at the repo root.

---

## 6. What to test on Aura first, and what may not work

Suggested order: **Controller Test** (pair the pad) → **Virtual Bot** →
**Link Test** with the robot → the rest. Record what happens for each.

| Feature | Expectation on Aura (untested) |
|---|---|
| App starts, menu, hand pinch | Likely works (Android XR hand interaction) |
| Gamepad, Virtual Bot | Likely works (plain Android Bluetooth input) |
| Link Test, robot driving | Likely works (plain networking) |
| AprilTag mode | Uncertain - needs Android XR marker tracking; if unsupported the board stays on "NO TAG" |
| Distance Test | Uncertain - needs depth or plane detection |
| Cup Detect | Uncertain - needs app access to the camera |
| Visuals | Aura is optical see-through with a narrower field of view than Galaxy XR; virtual content looks fainter and the menu may need a step back |

---

## 7. Troubleshooting

| Symptom | Fix |
|---|---|
| `adb devices` empty | Different USB cable (must carry data) or port; USB debugging on; on Windows, try another port if "Device Descriptor Request Failed" appears |
| `unauthorized` | Accept the debugging prompt on the Aura; if none appears, revoke USB debugging authorisations in Developer options and reconnect |
| `more than one device/emulator` | Add `-s <serial>` from `adb devices` |
| Install fails: invalid / failed to parse APK | The file is an LFS pointer - run `git lfs pull` and check the size is ~265 MB |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | An older build with a different signature is installed - `adb uninstall com.swapnil.BotXRGame`, then install again |
| `INSTALL_FAILED_NO_MATCHING_ABIS` | The device is not arm64 - this build is arm64-v8a only |
| App opens to nothing / black | Wait 10 s (first launch loads ~265 MB); save `aura-full.log` as in section 4 |
| No ray from the hand | Put any XR controllers down - holding one can switch the device off hand tracking |
| Pinch does nothing on the menu | Save `aura-unity.log`; the `[Hand]` lines show whether the ray hits the menu (`ui hit YES`) and whether the pinch registers (`performed True`) |
| Robot will not connect | Same network? Robot endpoint running on port 10000? `ping <robot-ip>` from the PC on that network |
