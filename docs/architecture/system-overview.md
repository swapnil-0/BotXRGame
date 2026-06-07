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
| Robot Controller | RubikPi (Qualcomm) or equivalent Qualcomm board |
| Robot | Ground bot with clamp/arm for object pickup |
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
| ROS Version | ROS 2 Humble (Ubuntu 22.04) |
| Unity to ROS Bridge | ROS TCP Connector 0.7.0 |
| Graphics API | Vulkan |
| Version Control | Git + GitHub (this repo) |

---

## Network Architecture

[Samsung Galaxy XR]  <---- WiFi/TCP (port 10000) ---->  [RubikPi / Qualcomm Board]
   Unity MR App                                              ROS 2 Humble

- Both devices must be on the same WiFi network
- ROS TCP Endpoint runs on the RubikPi (default port 10000)
- Unity ROSConnection component connects to RubikPi IP at runtime

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
- [ ] ROS 2 Humble installed on RubikPi
- [ ] ROS TCP Endpoint configured
- [ ] Unity to ROS connection tested
- [ ] Green cube detection implemented
- [ ] Virtual joystick implemented
- [ ] Bot control via Unity working
- [ ] Autonomous navigation working
- [ ] Full game loop implemented