# BotXRGame — System Architecture Overview

## Project Summary
A mixed reality robot teleoperation game where a player wearing a Samsung Galaxy XR headset 
controls a physical robot in a real-world play area. Virtual game elements (obstacles, rewards) 
are overlaid on the real world using MR passthrough. The robot also navigates autonomously 
for parts of the game.

---

## Team
| Role | Responsibility |
|---|---|
| XR Dev | Unity MR app, Galaxy XR, hand tracking, virtual overlays, game logic |
| ROS Dev | RubikPi setup, ROS 2 nodes, robot navigation, autonomous behavior, sensor integration |

---

## Hardware
| Component | Details |
|---|---|
| XR Headset | Samsung Galaxy XR |
| Robot Controller | Qualcomm Dragonwing IQ-9075 (target); RubikPi 3 (current prototype) |
| Robot | Hiwonder JetRover - 6-DOF arm, lidar, Hall-encoder DC motors, onboard STM32 |
| Play Area | Physically marked rectangle (tape/mat) |
| Game Objects | Small green cubes (physical), detected and replaced with virtual overlays |
| Flags | Physical flags, seen as-is through passthrough |

---

## Software Stack
| Layer | Technology |
|---|---|
| Game Engine | Unity 6000.4.2f1 - Mixed Reality template |
| Render Pipeline | URP (Universal Render Pipeline) |
| XR Framework | OpenXR + Android XR package (com.unity.xr.androidxr-openxr 1.3.1) |
| XR Input | Hand Tracking only (Hand Interaction Profile) |
| ROS Version | ROS 2 Jazzy (Ubuntu 24.04) |
| Unity to ROS Bridge | ROS TCP Connector 0.7.0 + patched ros_tcp_endpoint (vendored) |
| Graphics API | Vulkan |
| Version Control | Git + GitHub (this repo) |

---

## Network Architecture

[Samsung Galaxy XR]  <---- WiFi/TCP (port 10000) ---->  [RubikPi 3 / IQ-9075]
   Unity MR App                                              ROS 2 Jazzy

- Both devices must be on the same WiFi network and subnet
- ROS TCP Endpoint runs on the board, bound to 0.0.0.0 (default port 10000)
- Unity ROSConnection connects to the board IP, entered at runtime in-headset
- Verified end to end 2026-08-10: 9.0 Hz, 3.1 ms jitter over Wi-Fi

### Planned: custom transport
The headset-to-board link will move off ROS-TCP-Connector to a BotXRGame-owned protocol
with a local gateway publishing into ROS. This decouples the XR link from ROS and makes a
later move to QUIC a transport swap rather than a rewrite.

### Constraint: single ROS distro
ROS 2 does not support traffic between distros. Every ROS machine on this project must run
Jazzy. This matters because Hiwonder ships JetRover with Humble on its own SBC - that stack
is not reusable as-is on the Qualcomm board.

---

## Game Flow

### Phase 0 - Setup (before game starts)
- Player looks around the play area wearing the headset
- Headset camera detects green cubes by color
- Each green cube gets a spatial anchor
- Unity renders virtual game object (star, hole, pit) over each cube
- Virtual object map is sent to ROS as /game_object_map

### Phase 1 - Player drives to finish
- Player uses virtual joystick (hand tracking, pinch gesture) to control bot
- Bot moves toward finish flag
- Bot autonomously avoids virtual obstacles (pits) sent from Unity
- Bot autonomously collects stars/rewards along the way
- Player reaches finish flag -> Phase 2 begins

### Phase 2 - Autonomous return
- Bot navigates back to start autonomously
- Continues avoiding pits, collecting stars
- Bot uses clamp to pick up physical flags along the way
- Game repeats with increasing difficulty

---

## Folder Structure

BotXRGame/
├── unity/                  <- Unity MR project (XR Dev)
│   └── BotXRGame/
│       ├── Assets/
│       ├── Packages/
│       └── ProjectSettings/
├── ros2_ws/                <- ROS 2 workspace (ROS Dev)
│   └── src/
│       ├── bot_control/
│       ├── game_bridge/
│       └── bot_navigation/
└── docs/                   <- Shared documentation
    ├── architecture/       <- System design docs
    ├── ros-interface/      <- ROS to Unity topic specs
    └── presentations/      <- Slides for presentations

---

## Current Status
- [x] GitHub repo created
- [x] Unity MR project created and configured
- [x] All Unity packages installed
- [x] Android XR build profile active
- [x] ROS 2 Jazzy installed on RubikPi 3 (Ubuntu 24.04, native)
- [x] ROS TCP Endpoint built and patched (vendored - see PATCHES.md)
- [x] Unity to ROS connection tested end to end (9.0 Hz, 3.1 ms jitter)
- [x] link_monitor diagnostic node (`ros2_ws/src/xr_link_test`)
- [ ] Custom XR-to-board transport (replaces ROS-TCP-Connector)
- [ ] STM32 chassis driver on the Qualcomm board
- [ ] Lidar publishing /scan and rendering in-headset
- [ ] Green cube detection implemented
- [ ] Virtual joystick implemented (currently controller thumbstick)
- [ ] Bot control via Unity working
- [ ] Autonomous navigation working
- [ ] Full game loop implemented