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
| `tools/` | `check-scripts.py`, `ros_tcp_probe.py`, `export_cup_model.py`, adb helpers |
| `docs/` | Architecture, ROS interface, guides (`guides/cup-detect.md`) |
| `Qualcomm_IQ9_Jetrover_Integration_Guide.md` | Robot power-on, SSH, ROS launch order (from the ROS dev) |

## Stack

- Unity **6000.4.2f1**, URP, Vulkan, single-pass instanced
- OpenXR + `com.unity.xr.androidxr-openxr` **1.3.1**, `com.google.xr.extensions` **1.4.0** (git URL), AR Foundation
- `com.unity.ai.inference` **2.6.1** — shown as "Sentis" in Package Manager, but the C# namespace is `Unity.InferenceEngine`
- ROS-TCP-Connector ↔ `ros_tcp_endpoint` on ROS 2 **Jazzy**
- User's shell is **git-bash on Windows** — give commands in that form

## App flow and modes

Mode menu → ROS IP/port panel → HUD. `GameMode.Source` (static, set once):

| Mode | What it is |
|---|---|
| `VirtualBot` | Joystick flies a virtual ship (`GhostBot`). No robot needed. |
| `AprilTag` | Tag id 0 on the robot gives its pose; stick + tornado pull drive the real robot. Phases NoTag → Approach → Armed → Running (`BotCommandMixer`). |
| `LinkTest` | Bare ROS link: stick straight to `/cmd_vel`, everything else disabled. First thing to try when the robot won't move. |
| `CupDetect` | Headset camera → YOLO → cups placed on the floor and tracked. No robot, ROS or tags. Skips the IP panel. |

**Controls:** the right stick translates (forward and strafe) and the left stick spins (X only). A = arm SWEEP, B = arm KICK. In AprilTag ARMED, B also starts the run. Trigger = place / select. Grip = in-headset tuner.

## Robot interface (port 10000, one connection)

- `/cmd_vel` — `geometry_msgs/Twist`, 30 Hz. `linear.y` = strafe, **left positive**. `angular.z` **CCW positive**.
- `/arm_command` — `std_msgs/String`, bare word (`SWEEP`, `KICK`).
- Wire format: message bodies **omit the 4-byte CDR header** — a Twist is **48 bytes** on the wire, not 52.
- `tools/ros_tcp_probe.py` replaces the endpoint on a Pi and prints what Unity sends.
- Robot: `ssh ubuntu@ur-iq9-3` (credentials in the integration guide). DHCP — last seen at `192.168.1.214` on home wifi, and it will differ in the office. Launch order is in the integration guide.
- Check the base without the headset: `ros2 run teleop_twist_keyboard teleop_twist_keyboard` (Shift+J/L = strafe).

## Editor tools (Tools > BotXRGame)

| Item | Behaviour |
|---|---|
| Wire Session Flow | Builds the mode menu and HUD and wires references. **Fills only EMPTY fields** — safe to re-run. |
| Bind All Controls | **Overwrites** input bindings on every consumer. Asks first. |
| Set Up Cup Detect | Creates the `CupDetect` object, HUD text and preview, marker material; wires the model. Fills empty only. |
| Set Up AprilTag Tracking / Set All Marker Sizes To 100 mm | Marker database (AprilTag 36H11, 100 mm, id 0 = bot) |
| Export Scene Report | Writes `docs/reports/scene-report.md` — every serialized value. Use it to inspect the scene instead of guessing. |

## Hard-won rules — read before changing things

1. **Unity serialization trap.** Changing a field's default in code does NOT change a value already serialized in the scene. Either set it via an editor script with `Overwrite`, or tell the user to set it in the Inspector.
2. **`Start()` runs at scene load, before the user picks a mode.** A `Start()` mode check always sees the default `VirtualBot`. Poll `GameMode.Chosen` instead (see `CupDetectMode`). `LinkTestMode` only works because of a lucky start order — fragile.
3. **`FindAnyObjectByType` skips inactive objects**, and the scene has decoy/stand-in objects. Use `FindObjectsInactive.Include` and prefer serialized references.
4. **Every `InputActionReference` must be `.Enable()`d.** A disabled action reads exactly 0, which looks identical to a centred stick or an unwired reference.
5. **`[Header]`/`[Tooltip]` on a property is CS0592.** Fields only.
6. **`Shader.Find` at runtime can return a stripped shader in builds** → invisible objects. Reference a material asset from the scene instead.
7. **Create asset folders with `AssetDatabase.CreateFolder`**, not `Directory.CreateDirectory`.
8. **`Assets/Settings/Build Profiles/Android XR.asset`:** Unity repeatedly drops `XRGeneralSettingsPerBuildTarget` and `OpenXR Package Settings` from `preloadedAssets`. Revert that diff — those make XR initialise. (Scripting-define changes from packages are fine.)
9. **Instrument rather than theorise.** Wrong diagnoses from screenshots cost real time here; the scene report, logcat timestamps and the TCP probe found the truth first try. And verify the instrument — the probe once expected 52-byte Twists and made a working stream look dead.
10. **Before committing C#:** run `python tools/check-scripts.py` (brace balance, attribute-on-property, shadowed locals). It is not a compiler. After Unity recompiles, a successful build updates `Library/ScriptAssemblies/Assembly-CSharp.dll`.

## Working rules

- **The user runs `git push`.** Never force-push. Commit as `swapnil-0`.
- Don't delete Unity's `Library/` folder.
- A Git LFS hook prints a warning on every commit and checkout (`git-lfs` isn't installed). It's noise, not an error.
- Headset screenshots: `tools/pull-headset-captures.sh` (into `captures/`, which is gitignored). Logs: `tools/watch-logs.sh`.
- Test photos, YOLO weights (`yolo*.pt`), `cups*.jpg` and `*_detected.jpg` at the repo root are gitignored.

## Current state (2026-09-30)

**Cup Detect** — built, committed, compiles in Unity. **Not yet run on the headset.**
Next steps:
1. `python tools/export_cup_model.py --test-image cups.jpg` — does pretrained COCO class 41 "cup" see the red solo cups? If not, fine-tune before anything else.
2. `python tools/export_cup_model.py` → `Assets/Models/CupDetect/yolo11n_320.onnx`, then Wire Session Flow + Set Up Cup Detect, save, build.
3. On the headset: read the HUD lines (camera fps, Camera2 intrinsics present or MISSING, ms/inference, red-check availability) and the camera preview. Calibrate per `docs/guides/cup-detect.md`.

Design facts: WebCamTexture is used because AR Foundation's CPU image is unsupported on Android XR. GPU, not NPU — XR2+ Gen 2 isn't on LiteRT's Qualcomm NPU list, and Sentis has no NPU backend. Ultralytics YOLO is AGPL-3.0.

**Open items**
- AprilTag mode: START button invisible — the board prints a `START:` line naming the cause (suspect: stripped shader). B works as a fallback.
- Mecanum strafe sign on the real base not explicitly verified (Shift+J/L in teleop).
- Cups read TOPPLED while lying flat → `TagCupTracker.normalAxis` probably needs `Forward`.
- Tag offset `(0, 0, 0.115)` unverified — spin the robot in place; the marker should stay put.
- LIDAR HUD: branch `lidar-initial-work` has a bag and a plan that conflicts with the single-app / port-10000 design. Needs a conversation with the ROS dev.
- Make `LinkTestMode` poll for its mode the way `CupDetectMode` does.
