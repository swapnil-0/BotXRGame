# BotXRGame — System Architecture Overview

## Project Summary

A mixed reality game in which a player wearing a Samsung Galaxy XR headset drives a
physical robot around a real play area. The headset composites virtual game objects
into passthrough, the robot knocks over real targets with its arm, and virtual
hazards push the robot around while the player fights to stay on course.

The distinguishing idea is that **the app directs the physical setup**. Instead of
detecting whatever happens to be on the floor and adapting, the app plans a course
suited to the room it finds itself in, then guides a human through building it.

---

## Hardware

| Component | Details |
|---|---|
| XR Headset | Samsung Galaxy XR (Android XR) |
| Robot Controller | Qualcomm board running Ubuntu 24.04 |
| Robot | Hiwonder JetRover — 6-DOF arm, 2D lidar, Hall-encoder DC motors, onboard STM32 |
| Play Area | 8 ft × 8 ft (2.44 m) rectangle marked on the floor |
| Targets | Red plastic cups, covered by virtual objects in the headset |

The lidar is a 360° unit, but the chassis occludes roughly 120° behind the robot.
Blocked beams report no return. Any code consuming `/scan` must handle infinities.

---

## Software Stack

| Layer | Technology |
|---|---|
| Game Engine | Unity 6000.4.2f1, Mixed Reality template |
| Render Pipeline | Universal Render Pipeline (URP) |
| XR Framework | OpenXR + Android XR (`com.unity.xr.androidxr-openxr`) |
| Spatial Features | AR Foundation plane detection, raycast, anchors |
| ROS Version | ROS 2 Jazzy on Ubuntu 24.04 |
| Unity ↔ ROS Bridge | ROS TCP Connector + vendored, patched `ros_tcp_endpoint` |
| Graphics API | Vulkan |

---

## Network Architecture

```
[Samsung Galaxy XR]  <---- Wi-Fi / TCP port 10000 ---->  [Qualcomm board]
   Unity MR App                                             ROS 2 Jazzy
```

- Both devices must be on the same subnet.
- `ros_tcp_endpoint` runs on the board bound to `0.0.0.0`. Binding to a specific
  address silently breaks as soon as the board has both Ethernet and Wi-Fi.
- The board's IP and port are entered at runtime inside the headset, so changing
  them does not require rebuilding the app.

### Constraint: one ROS distribution

ROS 2 does not support traffic between distributions. Every ROS machine on this
project must run Jazzy. A Humble node and a Jazzy node cannot share a DDS graph, and
the failure mode is memory exhaustion rather than a clear error.

### Planned: project-owned transport

The headset-to-board link is expected to move off ROS TCP Connector onto a
project-owned protocol, with a small gateway process on the board publishing into
ROS. That decouples the XR link from ROS entirely and makes a later change of
transport a swap rather than a rewrite. The topic names and semantics in the
interface specification are designed to survive that change.

---

## Development Without Hardware

**The robot is not required to build or play the game.** `bot_sim` publishes
odometry, lidar, detections and arm state, and consumes velocity, goal and arm
commands — using the same topic names the real robot uses. Swapping the simulator
for the real robot changes nothing in the Unity app.

On the Unity side, `GhostBot` is a virtual robot driven by the same thumbstick input
and the same velocity mapping as the real one. The full game loop — setup, placement,
scoring — runs with no ROS connection at all.

This matters for more than convenience: it means the game design can be iterated
quickly, and that hardware bring-up and gameplay work can proceed in parallel.

See [../guides/simulator.md](../guides/simulator.md).

---

## Game Flow

### Phase 0 — Setup

1. **Scan floor.** AR Foundation plane detection finds horizontal surfaces. Requires
   `SCENE_UNDERSTANDING_COARSE` permission, requested at runtime.
2. **Define play area.** The player marks the arena corners.
3. **Locate robot.** The robot's position within the arena is established. A fiducial
   marker is the planned approach, since it gives full pose and doubles as a shared
   coordinate reference between headset and robot.
4. **Generate layout.** The app picks target positions for this specific room, subject
   to constraints: clear of walls so the robot can approach, far enough apart that one
   swing cannot take two, clear of the robot's start, and shaped so green targets lie
   along the route and red ones just off it.
5. **Place cups.** The app guides the player through placing real cups one at a time.
   The player sees a numbered bullseye in the headset and aims a handheld laser pointer
   at it so a helper without a headset can see where the cup goes. The app never tracks
   the laser — the player aligns it by eye, so there is nothing to calibrate.
6. **Verify.** The app checks whether cups ended up where it asked. In *adaptive* mode
   it moves its targets to match reality; in *strict* mode it names the misplaced cup
   and waits. **Skip** at any point plays the round with virtual targets only.

### Phase 1 — Play

- The player drives the robot with a thumbstick or virtual joystick.
- Green targets score, red targets penalise.
- Green targets must be knocked down **with the arm**, which requires positioning and
  aiming. Red targets penalise **chassis contact**, so they act as obstacles to steer
  around. That asymmetry is what gives the two colours different gameplay meaning.
- Virtual covers over the real cups shatter on impact, deliberately revealing the cup
  underneath rather than trying to hide the aftermath.

### Phase 2 — Hazards (planned)

- Virtual tornadoes wander the arena, growing and fading in strength.
- They apply drift to the robot's velocity command, so a virtual hazard visibly moves
  a physical machine and the player has to fight it.
- Tornadoes may relocate target markers, reshuffling which cups are worth points.

### Design decisions worth understanding

**Knock down, do not pick up.** Grasping requires object pose estimation, approach
planning, grip force control and carry stability. Knocking requires none of them,
keeps the manipulator central to the demonstration, and a miss is entertaining rather
than broken.

**Cover, do not erase.** Android XR does not allow an app to remove objects from the
passthrough image. Real cups are hidden underneath opaque virtual geometry, and the
cover is deliberately destroyed on impact so the reveal reads as intentional.

**Hazards act on the robot, not on physical objects.** A virtual tornado cannot move a
real cup, and pretending otherwise breaks the illusion. It can move the robot, because
the robot's velocity commands are under software control.

---

## Repository Layout

```
BotXRGame/
├── unity/BotXRGame/          Unity project
│   └── Assets/Scripts/       Game logic (see unity-app-architecture.md)
├── ros2_ws/src/
│   ├── xr_link_test/         link_monitor diagnostics + bot_sim simulator
│   └── ros_tcp_endpoint/     Vendored, patched Unity bridge
└── docs/
    ├── architecture/         This file, plus the Unity app design
    ├── ros-interface/        Topic contract
    ├── guides/               Practical how-to documents
    └── updates/              Session progress logs
```

---

## Status

**Working**

- [x] Unity project configured for Android XR with passthrough
- [x] ROS 2 Jazzy on the board, native
- [x] Unity ↔ ROS link verified end to end over Wi-Fi
- [x] Thumbstick teleoperation moving the physical robot
- [x] `link_monitor` diagnostic node
- [x] `bot_sim` robot simulator: odometry, lidar, detections, arm
- [x] Floor detection and virtual object placement
- [x] Layout generation, guided cup placement, verification flow
- [x] Ghost robot and knock-down arm, playable without hardware

**In progress**

- [ ] Virtual tornado hazards
- [ ] Lidar visualisation in the headset
- [ ] Fiducial-marker robot localisation and shared coordinate frame
- [ ] Red cup recognition (verification against known positions)

**Planned**

- [ ] Real arm driver on the robot
- [ ] STM32 chassis driver on the Qualcomm board
- [ ] Project-owned transport replacing ROS TCP Connector
- [ ] Autonomous navigation
