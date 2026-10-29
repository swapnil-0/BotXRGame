# BotXRGame — notes for Claude

Mixed-reality game: a player in a **Samsung Galaxy XR** headset drives a physical
**mecanum robot** (Hiwonder JetRover, Qualcomm IQ9 board) around a real room, with
virtual game objects (tornadoes, cups, arena) composited into passthrough. Built
for a University Relations program.

- **Swapnil (swapnil-0)** is the XR / Unity developer and owns this repo.
- A separate **ROS developer** owns the robot code. Don't change robot-side
  behaviour without saying so; document interface changes in `docs/ros-interface/`.

## Repo layout

| Path | What |
|---|---|
| `unity/BotXRGame/` | Unity project (the headset app) |
| `unity/BotXRGame/Assets/Scripts/` | Runtime C# |
| `unity/BotXRGame/Assets/Editor/` | Editor tools — scene wiring is done by these, not by hand |
| `ros2_ws/` | Vendored `ros_tcp_endpoint` (patched, see `PATCHES.md`) |
| `tools/` | `check-scripts.py`, `ros_tcp_probe.py`, `export_cup_model.py`, `cup_pose_check.py` (desktop mirror of the cup colour/pose pass), `pull-cup-frames.sh`, adb helpers |
| `docs/` | Architecture, ROS interface, guides (`guides/cup-detect.md`) |
| `Qualcomm_IQ9_Jetrover_Integration_Guide.md` | Robot power-on, SSH, ROS launch order (from the ROS dev) |

## Stack

- Unity **6000.4.2f1**, URP, Vulkan, single-pass instanced
- OpenXR + `com.unity.xr.androidxr-openxr` **1.3.1**, `com.google.xr.extensions` **1.4.0** (git URL), AR Foundation
- `com.unity.ai.inference` **2.6.1** — shown as "Sentis" in Package Manager, but the C# namespace is `Unity.InferenceEngine`
- ROS-TCP-Connector ↔ `ros_tcp_endpoint` on ROS 2 **Jazzy**
- User's shell is **git-bash on Windows** — give commands in that form

## App flow and modes

Experience menu → ROS IP/port panel → HUD. Everything on the menu is an **experience** (the game modes and the tests). The menu is a scroll list built from `ExperienceCatalog` by Wire Session Flow; to add one: a `ShipSource` value, a catalog line, a mode script that polls `GameMode.Chosen`, `GameMode.NeedsNoRobot` if it skips the IP panel, then re-run the setup. `GameMode.Source` (static, set once):

| Mode | What it is |
|---|---|
| `VirtualBot` | Joystick flies a virtual ship (`GhostBot`). No robot needed. |
| `AprilTag` | Tag id 0 on the robot gives its pose; stick + tornado pull drive the real robot. Phases NoTag → Approach → Armed → Running (`BotCommandMixer`). |
| `LinkTest` | Bare ROS link: stick straight to `/cmd_vel`, everything else disabled. First thing to try when the robot won't move. |
| `CupDetect` | Headset camera → YOLO + red-blob colour pass → cups placed on the floor and tracked, with a pose arrow (upright / upside down / tipped). No robot, ROS or tags. Skips the IP panel. |
| `DistanceTest` | Aim the right controller, trigger drops a pin: distance from the headset via Android XR depth and plane ray casts (`ARRaycastManager`, `TrackableType.Depth` needs `SCENE_UNDERSTANDING_FINE`, requested on entry). Skips the IP panel. |
| `NpuTest` / `ControllerTest` | Self-installing test experiences (no scene object): SNPE probe; Bluetooth gamepad live readout. Skip the IP panel. |

**Controls (no XR controllers - the Oculus Touch profile is off for Android):** hands for the menu and pointing, the Bluetooth pad for everything else. Menu: point with the right hand (white line on the menu), pinch to click, pinch-drag to scroll. At runtime XRI swaps the EventSystem's module for `XRUIInputModule`, so clicks go through the Ray Interactor's UI press reader, which must be `XRI Right Interaction/UI Press` (has the pinch binding) - `Use Hand Pinch On Menu` sets it, BatchBuild runs it. It was `Activate` (controller trigger only), which made the ray hover but never click. The `InputSystem_Actions` UI map is not what clicks. `HandInputDiag` logs `[Hand]` lines (devices, aim pose, UI hit, press) every second - remove once hands are settled. `Bot/Place` = right-hand pinch (place arena, START button, Cup Detect floor, Distance Test pin), aimed by the Ray Interactor, which follows the right hand's aim pose via the XRI default actions. `Bot/Move|Turn|Swing|Kick|Menu` = pad left stick, right stick, A, B, View. Bindings live in the `.inputactions` files, so editor tools that wire references need no change.

**Bluetooth gamepad (`DriveInput`):** when a pad is connected (8BitDo Ultimate 2C, mode switch on Bluetooth, paired in headset Settings) it drives and works the arm in every mode. Standard twin-stick layout, by button position: left stick = forward/strafe, right stick X = turn, A = SWEEP, B = KICK, X (or Start) = start the run when ARMED - not B, which also fires KICK. Pad lost = zero input, so the robot stops. Read from `Gamepad.all` directly, not serialized actions, so no scene wiring. Same code on XREAL Aura (Android XR; a paired pad is plain Android input). The Controller Test experience shows raw pad values, all devices and Android BACK presses; `[Pad]` lines in logcat show connects.

## Robot interface (port 10000, one connection)

- `/cmd_vel` — `geometry_msgs/Twist`, 10 Hz (`RobotController.publishRate`, measured on the RubikPi 2026-10-29). `linear.y` = strafe, **left positive**. `angular.z` **CCW positive**.
- `/arm_command` — `std_msgs/String`, bare word (`SWEEP`, `KICK`).
- Wire format: message bodies **omit the 4-byte CDR header** — a Twist is **48 bytes** on the wire, not 52.
- `tools/ros_tcp_probe.py` replaces the endpoint on a Pi and prints what Unity sends.
- Robot: `ssh ubuntu@ur-iq9-3` (credentials in the integration guide). DHCP. On the test router **URCade-Mobile** (no internet, `192.168.2.x`) it was at **`192.168.2.114`** (2026-10-29); ssh alias `robot` uses the PC's key (`ssh-copy-id` done), so no password is needed. The PC reaches that network over Ethernet while internet stays on Wi-Fi. Launch order is in the integration guide; nodes run in tmux session `botxr` (`tmux attach -t botxr`), with `~/botxr_env.sh` sourcing all four workspaces. They do NOT survive a robot reboot - it rebooted once mid-session, so check `uptime` first when nodes are missing.
- Check the base without the headset: `ros2 run teleop_twist_keyboard teleop_twist_keyboard` (Shift+J/L = strafe).

## Editor tools (Tools > BotXRGame)

| Item | Behaviour |
|---|---|
| Wire Session Flow | Builds the mode menu and HUD and wires references. **Fills only EMPTY fields** — safe to re-run. |
| Bind All Controls | **Overwrites** input bindings on every consumer. Asks first. |
| Set Up Cup Detect | Creates the `CupDetect` object, HUD text and preview, marker material; wires the model. Fills empty only. |
| Set Up Distance Test | Creates the `DistanceTest` object, HUD text, marker material; wires ray, raycaster, trigger/A/B. Fills empty only. |
| (headless) `BatchBuild.SetUp` / `.Build` | With the Editor **closed**: `Unity.exe -batchmode -quit -projectPath unity/BotXRGame -executeMethod BatchBuild.SetUpAndBuild`. Runs the setups, saves, writes the scene report, builds `Builds/BotXRGame.apk`. Unity kills the adb server on quit ("Killing ADB server"), which drops a wireless headset. Run Unity with `ANDROID_ADB_SERVER_PORT=5038` so it uses its own server and the headset's (5037) survives. Never reconnect from the agent side - the user connects the headset. |
| Set Up AprilTag Tracking / Set All Marker Sizes To 100 mm | Marker database (AprilTag 36H11, 100 mm, id 0 = bot) |
| Export Scene Report | Writes `docs/reports/scene-report.md` — every serialized value. Use it to inspect the scene instead of guessing. |

## Hard-won rules — read before changing things

1. **Unity serialization trap.** Changing a field's default in code does NOT change a value already serialized in the scene. Either set it via an editor script with `Overwrite`, or tell the user to set it in the Inspector.
2. **`Start()` runs at scene load, before the user picks a mode.** A `Start()` mode check always sees the default `VirtualBot`. Use `IEnumerator Start()`: `enabled = false; yield return new WaitUntil(() => GameMode.Chosen);` then decide (see `FloatingStartButton`), or poll like `CupDetectMode`. This bug kept `BotCommandMixer`, the START button, `BotTagMarker`, `CupTagMarkers` and `ShipTagFollower` switched off in AprilTag mode until 2026-10-29 - AprilTag driving was plain stick through `RobotController`, with no phases, approach or tornado pull.
3. **`FindAnyObjectByType` skips inactive objects**, and the scene has decoy/stand-in objects. Use `FindObjectsInactive.Include` and prefer serialized references.
4. **Every `InputActionReference` must be `.Enable()`d.** A disabled action reads exactly 0, which looks identical to a centred stick or an unwired reference.
5. **`[Header]`/`[Tooltip]` on a property is CS0592.** Fields only.
6. **`Shader.Find` at runtime can return a stripped shader in builds** → invisible objects. Reference a material asset from the scene instead.
7. **Create asset folders with `AssetDatabase.CreateFolder`**, not `Directory.CreateDirectory`.
8. **`Assets/Settings/Build Profiles/Android XR.asset`:** Unity repeatedly drops `XRGeneralSettingsPerBuildTarget` and `OpenXR Package Settings` from `preloadedAssets`. Revert that diff — those make XR initialise. (Scripting-define changes from packages are fine.)
9. **Instrument rather than theorise.** Wrong diagnoses from screenshots cost real time here; the scene report, logcat timestamps and the TCP probe found the truth first try. And verify the instrument — the probe once expected 52-byte Twists and made a working stream look dead.
10. **Never disable `ARTrackedImageManager` at runtime.** Stopping the image-tracking subsystem aborts the Galaxy XR runtime (`oxr_xrDestroyTrackableTrackerANDROID`, SIGABRT). Disable its consumers instead.
11. **Before committing C#:** run `python tools/check-scripts.py` (brace balance, attribute-on-property, shadowed locals). It is not a compiler. After Unity recompiles, a successful build updates `Library/ScriptAssemblies/Assembly-CSharp.dll`.

## Working rules

- **The user runs `git push`.** Never force-push. Commit as `swapnil-0`.
- Don't delete Unity's `Library/` folder.
- A Git LFS hook prints a warning on every commit and checkout (`git-lfs` isn't installed). It's noise, not an error.
- App id on the headset is **`com.swapnil.BotXRGame`** (from the build profile; ProjectSettings still shows the template id). Launch: `adb shell am start -n com.swapnil.BotXRGame/com.unity3d.player.UnityPlayerGameActivity`.
- Headset screenshots: `tools/pull-headset-captures.sh` (into `captures/`, which is gitignored). Logs: `tools/watch-logs.sh`.
- Test photos, YOLO weights (`yolo*.pt`), `cups*.jpg` and `*_detected.jpg` at the repo root are gitignored.

## Current state (2026-09-30)

**Cup Detect** — built, committed, compiles in Unity. **Not yet run on the headset.**
Next steps:
1. `python tools/export_cup_model.py --test-image cups.jpg` — does pretrained COCO class 41 "cup" see the red solo cups? If not, fine-tune before anything else.
2. `python tools/export_cup_model.py` → `Assets/Models/CupDetect/yolo11n_320.onnx`, then Wire Session Flow + Set Up Cup Detect, save, build.
3. On the headset: read the HUD lines (camera fps, Camera2 intrinsics present or MISSING, ms/inference, red-check availability) and the camera preview. Calibrate per `docs/guides/cup-detect.md`.

Design facts: WebCamTexture is used because AR Foundation's CPU image is unsupported on Android XR. The model currently runs on the GPU (Sentis has no NPU backend; XR2+ Gen 2 isn't on LiteRT's NPU list). Ultralytics YOLO is AGPL-3.0.

**NPU findings (`tools/npu_probe.sh`, 2026-10-07):** SoC `SXR2230P` ("anorak"), Hexagon **V69**. `/vendor/etc/public.libraries.txt` exposes **`libSNPE.so`** and **`libcdsprpc.so`** to apps; vendor SNPE is **AISW 2.25.0** (QNN API 2.18.0) with signed `libSnpeHtpV69Skel.so` / `libQnnHtpV69Skel.so` in `/vendor/lib/rfsa/adsp`. Vendor `libQnn*.so` are NOT app-readable. Device node `/dev/adsprpc-smd` (`vendor_qdsp_device`). User build, SELinux enforcing, bootloader locked, no OEM unlock → no root on the retail unit.

**NPU verdict (2026-10-08, NPU Test experience, in the real app process): third-party apps CANNOT reach the Hexagon NPU on the retail Galaxy XR.** `open_device_node failed for domain 3 (Permission denied)` on `/dev/adsprpc-smd`, and the HAL fallback fails with `DspClient: unable to acquire dspservice instance`. Same for unsigned (our 2.32 skel) and signed (vendor skel) routes, so a 2.25 SDK would not help. Also denied from adb shell and run-as. No NNAPI vendor driver (no NN HAL service). Only system services (e.g. `android.hardware.perceptionhost`) use the cDSP. SNPE CPU runs the quantized model at ~180 ms; SNPE GPU reports unavailable. Way forward needs platform access (Samsung/Google build with dspservice open to apps, or a userdebug/dev unit) — otherwise stay on Sentis GPU + the colour pass. Pipeline (convert, plugin, probe) is in `tools/npu/`, `native/cupnpu/`, `NpuTestMode`; reusable on a device that allows it.

**Display wedge:** after many exit crashes (rule 10's abort also fires on every app EXIT, in XR deinit), the headset stopped showing app content (frames submitted at 72 FPS, nothing visible, even old APKs). A headset restart fixed it. Treat "nothing virtual visible, even old builds" as a headset restart, not a code bug.

**Open items**
- AprilTag mode: START button never appeared - cause was rule 2 (fixed 2026-10-29, untested on the robot). Now the mixer really runs: after the arena is placed and tag 0 is seen it DRIVES the robot to the start line at `approachSpeed`, offers START (pad X, or pinch it), then stick + tornado pull. Watch the first run closely.
- Mecanum strafe sign on the real base not explicitly verified (Shift+J/L in teleop).
- Cups read TOPPLED while lying flat → `TagCupTracker.normalAxis` probably needs `Forward`.
- Tag offset `(0, 0, 0.115)` unverified — spin the robot in place; the marker should stay put.
- LIDAR HUD: branch `lidar-initial-work` has a bag and a plan that conflicts with the single-app / port-10000 design. Needs a conversation with the ROS dev.
