# BotXRGame — Unity App Architecture

How the headset application is put together, what each script is responsible for, and
where to add things.

All scripts live in `unity/BotXRGame/Assets/Scripts/`.

---

## Design Principles

**The game runs without hardware.** Every script here works against a virtual robot.
Nothing in the game logic knows whether it is driving a simulation or a real machine.
This is not only for convenience — it means gameplay iteration and robot bring-up can
happen at the same time.

**One place for each concern.** Coordinate conversion, scoring, and the state machine
each live in exactly one script. Duplicating them is how sign errors and inconsistent
rules creep in.

**Optional references degrade quietly.** Most public fields are null-checked. An
unwired Inspector slot disables a feature rather than throwing exceptions into a
headset where you cannot read them.

---

## Script Map

| Script | Responsibility |
|---|---|
| `GameFlow` | The state machine. Owns setup sequence, scoring, and round end. |
| `LayoutGenerator` | Chooses where targets go, given a room. Pure static logic, no scene dependencies. |
| `PlacementGuide` | Walks the player through placing real cups, one at a time. |
| `CupVerifier` | Interface + stub for checking whether cups ended up where they were asked. |
| `GhostBot` | Virtual robot. Same input mapping and velocity model as the real one. |
| `ArmController` | Knock-down arm: raise, swing, resolve hits, return. |
| `Target` | A scoring target — the virtual cover over a real cup. |
| `FloorSetup` | Requests scene permission, then enables plane detection. |
| `ObjectPlacer` | Ray-and-trigger placement of objects onto detected planes. |
| `RobotController` | Publishes `/cmd_vel` to ROS from thumbstick input. |
| `ROSIPConfig` | Runtime IP and port entry screen. |
| `CmdVelHUD` | Live telemetry display. |

---

## Game Flow

`GameFlow` owns this sequence. Each state is completed by a UI button or an event.

```
ScanFloor → DefineArea → LocateBot → GenerateLayout
    → PlaceCups ⇄ Verify → Play → End
              ↘  (Skip)  ↗
```

| State | Completed by | Notes |
|---|---|---|
| `ScanFloor` | `FloorFound()` | Waits for plane detection to find floor |
| `DefineArea` | `AreaDefined()` | Player marks the arena |
| `LocateBot` | `BotLocated()` | Establishes the robot's position |
| `GenerateLayout` | automatic | Runs `LayoutGenerator`, then moves on |
| `PlaceCups` | `ConfirmPlacement()` or `Skip()` | Guided physical placement |
| `Verify` | automatic | Checks reality; `ConfirmPlacement()` re-checks after a fix |
| `Play` | all green targets knocked | The game |
| `End` | — | Round summary |

`GameFlow.RosGameState` maps these many-to-one onto the coarser `/game_state` strings
in the interface specification, so the robot's view of the world stays stable while
the app's setup flow evolves.

---

## Layout Generation

`LayoutGenerator` picks target positions for whatever room it finds, subject to:

| Constraint | Default | Why |
|---|---|---|
| Wall clearance | 0.40 m | The robot must be able to get behind a target to swing at it |
| Target separation | 0.50 m | One swing must not take two targets, and the helper must be able to tell adjacent marks apart |
| Start clearance | 0.60 m | Nothing immediately in front of the robot at spawn |
| Route band | 0.45 m | Greens lie along the start-to-goal line; reds sit just off it |

Reds being *near* the route rather than scattered in corners is what makes avoiding
them cost something. A red target in a far corner is scenery.

**Capacity.** Simulating 200 random seeds in a 2.44 m square: 6 targets placed
successfully 200/200 times, 7 targets 92/100, 8 targets only 49/100. Separation is the
binding constraint and starts failing above 0.55 m; wall clearance never binds at these
sizes. `maxTargets` therefore defaults to 6.

If the space is tighter than expected, the generator relaxes separation in stages and
returns fewer targets rather than failing. Returning a short layout silently would look
like a bug, so `GameFlow` reports it.

---

## Cup Placement and the Laser

The player holds a physical laser pointer. They see a numbered bullseye on the floor
through the headset and aim the laser dot to coincide with it. A helper without a
headset follows the dot.

**The app never tracks the laser.** The player aligns it by eye, so there is nothing to
calibrate and no extra hardware integration. This is why placement is sequential — six
markers on the floor at once would be ambiguous for both people, whereas "cup 3, here"
is not.

`PlacementGuide` provides `ConfirmPlaced()`, `GoBack()` for when the helper mishears,
and `Skip()` to abandon physical placement entirely and play with virtual targets.

---

## Verification

`ICupVerifier` answers: is there actually a cup where we asked for one?

This is a much easier question than general object recognition. The app already knows
where each cup should be, so it is a spot check within a tolerance rather than an
open-ended search of the scene.

Two modes:

- **Adaptive** — a cup found slightly off its mark moves the target to match. Reality
  wins. More robust, and the default.
- **Strict** — names the misplaced cup and waits for a fix. Better for demonstrating
  that the system is genuinely checking.

`StubCupVerifier` always reports success and is what the Skip path uses. It is not a
lie: with no physical cups, the virtual covers really are exactly where the layout put
them. Replacing it with real recognition touches one class.

---

## The Robot: Ghost and Real

`GhostBot` is a virtual robot using the **same thumbstick mapping and velocity model**
as `RobotController` uses for the real one. Same stick position, same resulting motion.

It is deliberately **not** a `Rigidbody`. Differential-drive kinematics are integrated
by hand, matching both how the real robot moves and how `bot_sim` models it. Unity
physics would add drift, bounce and sliding the real robot does not have, and the two
would quietly stop agreeing.

`GhostBot.AddExternalVelocity()` is the injection point for hazards. Anything that
pushes the robot — currently planned for tornadoes — adds world-space velocity here
rather than moving the transform directly.

---

## The Arm

`ArmController` implements a knock-down swing, not a grasp.

Grasping requires object pose estimation, approach planning, grip force control and
carry stability — four hard problems. A swing requires none of them, keeps the
manipulator visibly part of the game, and a miss is entertaining rather than broken.

States and timings mirror `bot_sim` exactly:

| Phase | Default | Behaviour |
|---|---|---|
| `Ready` | 0.5 s | Arm raises — telegraphs the swing |
| `Swinging` | 0.6 s | Eased strike; impact resolves at the end |
| `Returning` | 0.5 s | Back to stowed |

Reach is 0.35 m with a 70° swept arc. **These numbers must match `bot_sim`**, or the
virtual and real robots will feel different.

The chassis is locked for the duration of a swing so the strike lands where it was
aimed. Hazard drift still applies, so a tornado can shove the robot off its shot.

`BuildRosCommand()` emits exactly the `/arm_command` JSON the robot expects, so
connecting to real hardware is one publish call.

---

## Targets

A `Target` is an opaque virtual cover sitting over a real cup.

Android XR does not allow an app to remove objects from passthrough — it can read
camera frames and draw over them, but not rewrite what the user sees. So real cups are
hidden by covering them, which requires the cover to be opaque and generously larger
than the cup in every dimension.

**On impact the cover shatters and reveals the cup.** This is deliberate. A real cup
tumbling out from underneath a virtual object is the one unavoidable break in the
illusion, and it happens at the most-watched moment. Scripting it as an intentional
reveal turns that into the joke rather than a bug.

Green and red targets differ mechanically, not just in score:

- **Green** must be knocked with the arm — requires positioning and aiming.
- **Red** penalises chassis contact — an obstacle to steer around.

`SetKind()` allows a target to change type at runtime, for hazards that reshuffle the
course.

---

## Plane Detection

`FloorSetup` exists because of one platform detail: `ARPlaneManager` must remain
**disabled** until `android.permission.SCENE_UNDERSTANDING_COARSE` is granted. Enable
it earlier and the subsystem fails with no planes and no error message.

The permission must also be declared in `Assets/Plugins/Android/AndroidManifest.xml`:

```xml
<uses-permission android:name="android.permission.SCENE_UNDERSTANDING_COARSE" />
```

Required OpenXR features, under Project Settings → XR Plug-in Management → OpenXR →
Android: **AR Session**, **AR Plane**, **AR Raycast**, **AR Anchor**, **AR Camera**.

`FloorSetup.Status` reports which stage succeeded or failed, which is worth surfacing
on a HUD during bring-up.

---

## Extension Points

| To add... | Do this |
|---|---|
| A new hazard | Call `GhostBot.AddExternalVelocity()` each frame |
| A new target type | Extend `Target.Kind`, update `ApplyKindVisuals()` and `Points` |
| Real cup recognition | Implement `ICupVerifier`, pass to `GameFlow.SetVerifier()` |
| A new setup step | Add to `GameFlow.State` and its `SetState` switch |
| Scoring rules | `GameFlow.HandleKnock()` — the single place score changes |
| ROS subscription | One conversion helper, tested against `/odom` first |

---

## Known Gaps

- The app currently **publishes** to ROS but does not subscribe. Lidar rendering,
  odometry overlay and path display all depend on adding that, plus a tested
  coordinate conversion.
- Robot localisation is manual. A fiducial marker would give full pose and a shared
  coordinate frame between headset and robot.
- Hazards are designed but not implemented.
