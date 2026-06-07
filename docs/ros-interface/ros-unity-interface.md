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

### Install ROS 2 Humble
Follow official instructions at:
https://docs.ros.org/en/humble/Installation/Ubuntu-Install-Debians.html

### Install ros_tcp_endpoint
sudo apt install ros-humble-ros-tcp-endpoint

### Launch the endpoint
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=<rubikpi-ip-address>

### Verify connection
Once Unity app is running and connected, verify topics are flowing:
ros2 topic list
ros2 topic echo /cmd_vel
ros2 topic echo /game_state

---

## Notes for ROS Dev
- The RubikPi IP address must be entered in the Unity ROSConnection component before building
- For development/testing, ros_tcp_endpoint can run on any Ubuntu machine on the same network
- A laptop running ROS 2 Humble can substitute for the RubikPi during early testing
- All string message values are uppercase as listed above

