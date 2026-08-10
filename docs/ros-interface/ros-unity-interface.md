# BotXRGame — ROS 2 / Unity Interface Specification

## Overview
This document defines all ROS 2 topics, message types, and data flow between 
the Unity MR app (Samsung Galaxy XR) and the ROS 2 system (RubikPi / Qualcomm board).
Both systems communicate over WiFi via ROS TCP Connector on port 10000.

---

## Connection Details
| Parameter | Value |
|---|---|
| Protocol | TCP |
| Port | 10000 |
| ROS Side | ros_tcp_endpoint node running on RubikPi |
| Unity Side | ROSConnection component in Unity scene |
| Network | Both devices on same WiFi network |

---

## Topic Reference

### Unity --> ROS (Commands from headset to robot)

| Topic | Message Type | Rate | Description |
|---|---|---|---|
| /cmd_vel | geometry_msgs/Twist | 10-20 Hz | Bot movement commands from virtual joystick. linear.x = forward/back, angular.z = left/right |
| /head_pose | geometry_msgs/PoseStamped | 30 Hz | Headset position and orientation in world space |
| /game_object_map | geometry_msgs/PoseArray | On change | World positions of all virtual game objects (pits, stars, obstacles) detected at game start |
| /game_state | std_msgs/String | On change | Current game state. Values: SETUP, PHASE1_PLAYER, PHASE2_AUTO, GAME_OVER |

### ROS --> Unity (Data from robot to headset)

| Topic | Message Type | Rate | Description |
|---|---|---|---|
| /odom | nav_msgs/Odometry | 30 Hz | Bot real-world position and orientation. Used by Unity to render virtual overlay on real bot |
| /camera/compressed | sensor_msgs/CompressedImage | 15-30 Hz | Bot camera feed (optional, for monitoring) |
| /bot_state | std_msgs/String | On change | Current bot mode. Values: MANUAL, AUTONOMOUS, PICKUP, RETURNING |
| /score_event | std_msgs/String | On event | Triggered when bot collects a star or reaches finish. Values: STAR_COLLECTED, FINISH_REACHED, FLAG_PICKED |

---

## Game Object Map Format
The /game_object_map topic sends a PoseArray where each Pose represents 
one virtual game object detected by the headset camera at game start.

To distinguish object types (star, pit, obstacle), we use a companion topic:

| Topic | Message Type | Description |
|---|---|---|
| /game_object_types | std_msgs/String | JSON array matching /game_object_map order. Example: ["star","pit","star","obstacle"] |

Example JSON:
["star", "pit", "star", "obstacle", "star"]

The index of each type matches the index of the corresponding Pose in the PoseArray.

---

## Coordinate System
- All positions are in ROS world frame (REP-105 compliant)
- Origin is the bot starting position at game start
- Unity converts from ROS coordinate system (Z forward, Y up) automatically via ROS TCP Connector
- Play area boundary corners will be published separately as /play_area (geometry_msgs/Polygon)

---

## Bot Control Details

### Manual Mode (Phase 1)
Unity publishes /cmd_vel based on virtual joystick input:
- linear.x > 0  : move forward
- linear.x < 0  : move backward
- angular.z > 0 : turn left
- angular.z < 0 : turn right
- All other fields (linear.y, linear.z, angular.x, angular.y) = 0

### Autonomous Mode (Phase 2)
- Unity publishes /game_state = "PHASE2_AUTO" to trigger autonomous return
- ROS navigation stack takes over, Unity stops publishing /cmd_vel
- ROS uses /game_object_map to avoid pits and collect stars
- ROS publishes /bot_state = "RETURNING" during this phase

---

## Setup Sequence
1. RubikPi boots, ROS 2 starts, ros_tcp_endpoint node launches
2. Galaxy XR app launches, ROSConnection connects to RubikPi IP
3. Unity publishes /game_state = "SETUP"
4. Player scans play area, green cubes detected by headset camera
5. Unity publishes /game_object_map and /game_object_types
6. Unity publishes /game_state = "PHASE1_PLAYER"
7. Game begins

---

## ROS Dev Setup Instructions

### Install ROS 2 Jazzy
The RubikPi 3 runs Ubuntu 24.04, which pairs natively with ROS 2 Jazzy. No Docker required.
Follow official instructions at:
https://docs.ros.org/en/jazzy/Installation/Ubuntu-Install-Debs.html

### Build ros_tcp_endpoint from this repo
IMPORTANT: ros_tcp_endpoint is NOT available via apt. It has never been released as a
Debian package for any ROS 2 distro. `sudo apt install ros-jazzy-ros-tcp-endpoint` will fail.

A patched copy is vendored in this repository at `ros2_ws/src/ros_tcp_endpoint`. Upstream
v0.7.0 does NOT work against the current Unity ROS-TCP-Connector - it drops all published
data silently. See `ros2_ws/src/ros_tcp_endpoint/PATCHES.md` for details. Use the vendored
copy, not a fresh clone.

    mkdir -p ~/ros2_ws/src
    cp -r <repo>/ros2_ws/src/ros_tcp_endpoint ~/ros2_ws/src/
    cp -r <repo>/ros2_ws/src/xr_link_test     ~/ros2_ws/src/
    cd ~/ros2_ws
    colcon build --symlink-install
    source install/setup.bash

### Launch the endpoint
Bind to 0.0.0.0, not the board's own address. Binding to a specific address makes the
socket reachable on one interface only, which breaks as soon as the board has both
Ethernet and Wi-Fi.

    ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0

### Verify connection
Once the Unity app is running and connected, verify topics are flowing:

    ros2 topic list
    ros2 topic echo /cmd_vel
    ros2 topic echo /game_state

Or use the link monitor, which prints every inbound message with rate and jitter:

    python3 ~/ros2_ws/src/xr_link_test/xr_link_test/link_monitor.py \
      --ros-args -p preset:=headset -p full_message:=false

### Troubleshooting the link
Work outward from ROS toward the headset - each step isolates one layer:

| Symptom | Check |
|---|---|
| Nothing on the graph | `ros2 topic pub -r 10 /cmd_vel geometry_msgs/msg/Twist '{linear: {x: 0.25}}'` - proves ROS and the monitor work without the headset |
| Headset cannot connect | `ss -tlnp \| grep 10000` on the board; then `Test-NetConnection <ip> -Port 10000` from a PC on the same Wi-Fi |
| Connects then drops | Check the endpoint log for JSON errors - indicates a connector/endpoint protocol mismatch |
| Topic registers but no data | Run the endpoint with `-p debug_frames:=true` to log every inbound frame |
| Connection drops after ~20 s | Expected if the headset was removed - the app pauses and closes the socket |

---

## Notes for ROS Dev
- The RubikPi IP is entered at runtime on the in-headset IP screen; no rebuild needed to change it
- For development/testing, ros_tcp_endpoint can run on any Ubuntu machine on the same network
- A laptop running ROS 2 Jazzy can substitute for the RubikPi during early testing
- Substitute machines must also run **Jazzy**. ROS 2 does not support inter-distro traffic;
  a Humble node and a Jazzy node cannot share a DDS graph
- All string message values are uppercase as listed above

---

## Measured Baseline

Galaxy XR to RubikPi 3 over Wi-Fi, 2026-08-10:

| Metric | Value |
|---|---|
| `/cmd_vel` rate | 9.0 Hz (app requests 10 Hz - see note) |
| Jitter over Wi-Fi | 3.1 ms |
| Jitter, local `ros2 topic pub` | 0.9 ms |
| Wireless cost | ~2.2 ms |

Rate note: `RobotController.Update()` originally reset its accumulator to zero after
publishing, discarding the overshoot each cycle and rounding the period up to a whole
number of frames. At a 72 Hz display that yields 8 frames x 13.89 ms = 111 ms = 9.0 Hz.
Fixed by subtracting the interval rather than zeroing.

---

## Planned Change: Custom Transport

The ROS-TCP-Connector / ros_tcp_endpoint pair is a stopgap. Upstream is effectively
unmaintained and its wire protocol has drifted from the Unity package it pairs with,
with silent failure modes (see PATCHES.md).

The agreed direction is a BotXRGame-owned protocol between the headset and the Qualcomm
board, with a local gateway process publishing to ROS. That decouples the headset link
from ROS entirely and makes a later move to QUIC a transport swap rather than a rewrite.
Topic names and semantics in this document are expected to survive that change.

