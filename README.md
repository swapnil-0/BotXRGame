# BotXRGame

A mixed-reality game where a player wearing a **Samsung Galaxy XR** headset drives a
**physical robot** around a real room. Virtual game objects are composited into
passthrough, the robot knocks over real targets with its arm, and virtual weather
pushes the robot around while the player fights to stay on course.

The robot runs **ROS 2 Jazzy** on a Qualcomm board. The headset runs a **Unity**
app. They talk over Wi-Fi.

---

## Start here

**You do not need the robot to work on this project.** A simulator (`bot_sim`)
publishes everything the headset needs and consumes everything it sends, so the
entire game can be built and played on a desk. See
[docs/guides/simulator.md](docs/guides/simulator.md).

| If you are... | Read |
|---|---|
| New to the project | This file, then [system-overview](docs/architecture/system-overview.md) |
| Building the Unity app | [unity-app-architecture](docs/architecture/unity-app-architecture.md) |
| Working on the robot | [ros-unity-interface](docs/ros-interface/ros-unity-interface.md) |
| Trying to get the link working | [Link Test Guide (PDF)](docs/guides/BotXRGame_Link_Test_Guide.pdf) |
| Developing without hardware | [simulator](docs/guides/simulator.md) |

---

## Repository map

```
BotXRGame/
├── unity/BotXRGame/          Unity project (Android XR, URP, OpenXR)
│   └── Assets/Scripts/       Game logic - see unity-app-architecture.md
├── ros2_ws/src/
│   ├── xr_link_test/         Diagnostics + robot simulator
│   │   ├── link_monitor.py   Prints every ROS message, with rate and jitter
│   │   └── bot_sim.py        Fake robot: odom, lidar, detections, arm
│   └── ros_tcp_endpoint/     Unity<->ROS bridge (vendored and patched)
└── docs/
    ├── architecture/         System and app design
    ├── ros-interface/        Topic contract between headset and robot
    ├── guides/               Practical how-to documents
    └── updates/              Session progress logs
```

---

## Quick start: run the game with no robot

On the Qualcomm board (or any machine with ROS 2 Jazzy):

```bash
git clone https://github.com/swapnil-0/BotXRGame.git
mkdir -p ~/ros2_ws/src
cp -r BotXRGame/ros2_ws/src/* ~/ros2_ws/src/
cd ~/ros2_ws && colcon build --symlink-install
source install/setup.bash

ros2 launch xr_link_test xr_sim.launch.py
```

That starts the bridge, a simulated robot, and a message monitor. Then build the
Unity project to the headset, enter the board's IP address, and press Connect.

> **Important:** use the `ros_tcp_endpoint` copy in this repository. It is not
> available through `apt`, and the upstream version does not work with the current
> Unity connector - it drops every message silently. See
> [PATCHES.md](ros2_ws/src/ros_tcp_endpoint/PATCHES.md) for what was changed and why.

---

## How the game works

**Setup — the app plans the course.** The headset finds the floor, the player marks
out a play area, and the app *generates* a layout of target positions suited to that
specific room. It then guides the player through placing real cups at those marks,
one at a time, using a handheld laser pointer so a helper without a headset can see
where each cup goes. A Skip button plays the whole game with virtual targets instead.

This is the reverse of usual mixed reality. Rather than detecting whatever happens
to be on the floor and adapting to it, the app decides what the course *should* look
like and asks reality to match. It also makes recognition far easier later: the app
already knows where each cup should be, so checking is a spot test rather than an
open-ended search.

**Play — knock down the targets.** Green targets score, red targets penalise. The
player drives the robot into position and swings its arm to knock cups over. Virtual
covers sitting over the real cups shatter on impact, revealing the cup underneath.

**Later levels — virtual weather.** Tornadoes wander the arena and pull the robot off
course. They are virtual, but the robot physically fights them, because the force is
applied to the velocity commands the headset sends.

---

## Hardware

| Component | Detail |
|---|---|
| Headset | Samsung Galaxy XR (Android XR) |
| Robot compute | Qualcomm board running Ubuntu 24.04 + ROS 2 Jazzy |
| Robot | Hiwonder JetRover — 6-DOF arm, 2D lidar, encoder DC motors, onboard STM32 |
| Play area | 8 ft × 8 ft (2.44 m) marked on the floor |
| Targets | Red plastic cups |

---

## Constraints worth knowing early

**One ROS distro everywhere.** ROS 2 does not support traffic between distributions.
Every ROS machine on this project must run Jazzy; a Humble node and a Jazzy node
cannot share a DDS graph.

**Passthrough cannot be erased.** Android XR lets apps read camera frames and draw
on top of passthrough, but not rewrite the pixels the user sees. Real objects are
hidden by covering them with opaque virtual geometry, never by deleting them.

**Plane detection needs permission.** `ARPlaneManager` must stay disabled until
`android.permission.SCENE_UNDERSTANDING_COARSE` is granted, or the subsystem fails
with no planes and no error.

---

## License

See [LICENSE](LICENSE).
