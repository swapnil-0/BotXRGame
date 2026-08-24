# BotXRGame — ROS 2 / Unity Interface Specification

Defines every topic exchanged between the Unity app on the Samsung Galaxy XR headset
and ROS 2 on the robot's Qualcomm board.

This contract is deliberately stable. The transport underneath it is expected to
change; these names and message types are not.

---

## Connection

| Parameter | Value |
|---|---|
| Protocol | TCP |
| Port | 10000 (default; configurable at runtime in-headset) |
| ROS side | `ros_tcp_endpoint` running on the board, bound to `0.0.0.0` |
| Unity side | `ROSConnection` component |
| Network | Both devices on the same subnet |

Bind the endpoint to `0.0.0.0`, never to a specific address. Binding to one interface
works until the board has both Ethernet and Wi-Fi, then fails confusingly.

---

## Topic Reference

### Unity → ROS (headset commands the robot)

| Topic | Message Type | Rate | Description |
|---|---|---|---|
| `/cmd_vel` | `geometry_msgs/Twist` | 10–30 Hz | Drive command. `linear.x` forward/back, `angular.z` turn. All other fields zero. |
| `/arm_command` | `std_msgs/String` | On event | JSON. Knock-down arm control — see below. |
| `/goal_pose` | `geometry_msgs/PoseStamped` | On event | Drive-to-point target, for autonomous movement. |
| `/head_pose` | `geometry_msgs/PoseStamped` | 30 Hz | Headset pose in world space. |
| `/game_state` | `std_msgs/String` | On change | `SETUP`, `PHASE1_PLAYER`, `PHASE2_AUTO`, `GAME_OVER`. |
| `/game_object_map` | `geometry_msgs/PoseArray` | On change | Positions of virtual game objects, so the robot can reason about them. |
| `/game_object_types` | `std_msgs/String` | On change | JSON array, index-matched to `/game_object_map`. |
| `/play_area` | `geometry_msgs/Polygon` | On change | Arena boundary corners. |

### ROS → Unity (robot reports to the headset)

| Topic | Message Type | Rate | Description |
|---|---|---|---|
| `/odom` | `nav_msgs/Odometry` | 30 Hz | Robot pose and velocity. Drives the virtual overlay on the real robot. |
| `/scan` | `sensor_msgs/LaserScan` | 10 Hz | 2D lidar. See the note on occlusion below. |
| `/tf` | `tf2_msgs/TFMessage` | 30 Hz | `odom` → `base_link` → `laser`. |
| `/arm_state` | `std_msgs/String` | On change | `STOWED`, `READY`, `SWINGING`, `RETURNING`. |
| `/bot_state` | `std_msgs/String` | On change | `IDLE`, `MANUAL`, `RETURNING`. |
| `/score_event` | `std_msgs/String` | On event | JSON scoring events — see below. |
| `/detected_objects` | `geometry_msgs/PoseArray` | 2 Hz | Positions of recognised targets. |
| `/detected_object_types` | `std_msgs/String` | 2 Hz | JSON array, index-matched to `/detected_objects`. |
| `/plan` | `nav_msgs/Path` | On change | Planned route, for rendering in the headset before the robot moves. |
| `/camera/compressed` | `sensor_msgs/CompressedImage` | 15–30 Hz | Optional camera feed. |

---

## JSON Payloads

Several topics carry JSON inside a `std_msgs/String`. This keeps the message set
small and avoids custom message packages, at the cost of type safety.

### `/arm_command`

```json
{"action": "SWING", "yaw": 0.35}
{"action": "STOW"}
```

- `action` — `SWING` or `STOW`.
- `yaw` — optional, radians, relative to the robot's heading. `0` is straight ahead.
  Simple drivers may ignore it and always swing forward.

A `SWING` received while a swing is already in progress is ignored rather than
restarting the motion — a real arm cannot return to the ready position mid-stroke.

The arm sequence is `STOWED → READY → SWINGING → RETURNING → STOWED`, with impact
resolved at the end of `SWINGING`. The wheels are held still for the duration so the
strike lands where it was aimed.

### `/score_event`

```json
{"event": "STAR_COLLECTED", "kind": "star", "index": 2, "x": 1.2, "y": -0.8}
{"event": "PENALTY_HIT",    "kind": "pit",  "index": 4, "x": 2.2, "y":  0.4}
{"event": "SWING_MISSED"}
```

### `/game_object_types` and `/detected_object_types`

A JSON array of strings, index-matched to the corresponding `PoseArray`:

```json
["star", "pit", "star", "cyclone"]
```

---

## Coordinate Systems

ROS and Unity disagree, and this is the most common source of subtle bugs.

| | ROS | Unity |
|---|---|---|
| Handedness | Right | Left |
| Up axis | Z | Y |
| Forward axis | X | Z |

ROS TCP Connector provides conversion helpers. **Do the conversion in one place,
test it, and reuse it.** A sign error produces a robot that drives mirrored or a
lidar scan rotated 90°, both of which are painful to diagnose visually.

Get this right with a single pose from `/odom` before attempting it with 360 lidar
points.

Positions are in the ROS world frame with the origin at the robot's starting pose,
per REP-105.

---

## Lidar Occlusion

The lidar is a 360° scanner, but the chassis blocks roughly 120° behind the robot.
Blocked beams report **positive infinity**, which is the standard no-return value.

Any consumer of `/scan` must handle infinities. Rendering them naively produces
points at the far clip plane or NaN geometry. The simulator reproduces this exactly,
so the behaviour can be tested before hardware is involved.

---

## Setup Sequence

1. Board boots, ROS 2 starts, `ros_tcp_endpoint` launches.
2. Headset app launches and connects to the board's IP.
3. App publishes `/game_state` = `SETUP`.
4. Player scans the floor and marks the play area.
5. App publishes `/play_area`.
6. App generates a target layout and guides the player through placing cups.
7. App publishes `/game_object_map` and `/game_object_types`.
8. App publishes `/game_state` = `PHASE1_PLAYER`. Play begins.

---

## ROS Setup Instructions

### Install ROS 2 Jazzy

The board runs Ubuntu 24.04, which pairs natively with Jazzy. No container required.
Follow <https://docs.ros.org/en/jazzy/Installation/Ubuntu-Install-Debs.html>.

### Build the workspace

`ros_tcp_endpoint` is **not available through apt** — it has never been released as a
Debian package for any ROS 2 distribution. It is vendored in this repository, and the
vendored copy is patched: the upstream version does not work against the current Unity
connector and drops every published message silently. See
[`ros2_ws/src/ros_tcp_endpoint/PATCHES.md`](../../ros2_ws/src/ros_tcp_endpoint/PATCHES.md).

```bash
git clone https://github.com/swapnil-0/BotXRGame.git
mkdir -p ~/ros2_ws/src
cp -r BotXRGame/ros2_ws/src/* ~/ros2_ws/src/
cd ~/ros2_ws
colcon build --symlink-install
source install/setup.bash
```

### Run

Everything at once, with a simulated robot:

```bash
ros2 launch xr_link_test xr_sim.launch.py
```

Or the bridge alone, against a real robot:

```bash
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0
```

### Verify

```bash
ros2 topic list
ros2 topic echo /cmd_vel
```

Or use the monitor, which prints every inbound message with rate and jitter:

```bash
ros2 run xr_link_test link_monitor --ros-args -p preset:=headset -p full_message:=false
```

---

## Troubleshooting the Link

Work outward from ROS toward the headset — each row isolates one layer.

| Symptom | What to check |
|---|---|
| Nothing on the graph | `ros2 topic pub -r 10 /cmd_vel geometry_msgs/msg/Twist '{linear: {x: 0.25}}'` — proves ROS and the monitor work without the headset |
| Headset cannot connect | `ss -tlnp \| grep 10000` on the board, then test the port from a laptop on the same Wi-Fi |
| Connects then drops repeatedly | Bridge protocol mismatch. Confirm you are running the vendored copy, not a fresh upstream clone |
| Topic registers but no data arrives | Restart the bridge with `-p debug_frames:=true` to log every inbound frame |
| Connection drops after ~20 seconds | Expected if the headset was removed — the app pauses and closes the socket |
| Topic exists but nothing publishes | The bridge keeps its publisher alive after a disconnect. The bridge log, not `ros2 topic list`, is the authority on whether the headset is connected |

---

## Reference Measurements

Galaxy XR to a Qualcomm board over Wi-Fi:

| Scenario | Rate | Jitter |
|---|---|---|
| Local `ros2 topic pub`, no network | 10.0 Hz | 0.9 ms |
| Headset over Wi-Fi | 10.0 Hz | 3–8 ms |

Use the local figure as the noise floor. If wireless jitter is far above this, suspect
the radio — congestion, 2.4 GHz, or Wi-Fi power saving — before suspecting the code.

Some jitter is contributed by the app quantising its publish timer to the headset's
render loop, and is expected.

---

## Notes

- The board's IP and port are entered at runtime in the headset; changing them needs
  no rebuild.
- Any substitute machine used for development must also run **Jazzy**.
- All string enum values are uppercase as listed.
