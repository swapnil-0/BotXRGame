# Developing Without the Robot

`bot_sim` is a fake robot. It publishes everything the headset needs and consumes
everything the headset sends, using the **same topic names the real robot uses**. When
the real robot is available, stop the simulator and start the real drivers — nothing in
the Unity app changes.

This exists so that gameplay work and hardware bring-up can happen in parallel, and so
that a broken robot never blocks app development.

---

## Running it

```bash
source /opt/ros/jazzy/setup.bash
source ~/ros2_ws/install/setup.bash

ros2 launch xr_link_test xr_sim.launch.py
```

That starts three things:

| Node | Purpose |
|---|---|
| `ros_tcp_endpoint` | The Unity bridge, on `0.0.0.0:10000` |
| `bot_sim` | The fake robot |
| `link_monitor` | Prints what the headset sends, with rate and jitter |

Then build the Unity app to the headset, enter the board's IP address, and connect.
Drive with the thumbstick — the simulated robot moves and publishes odometry and lidar
throughout.

Useful arguments:

```bash
ros2 launch xr_link_test xr_sim.launch.py monitor:=false        # quieter
ros2 launch xr_link_test xr_sim.launch.py occluded_deg:=0.0     # full 360 lidar
ros2 launch xr_link_test xr_sim.launch.py room_width:=8.0 room_height:=6.0
```

To run the simulator alone, without the bridge:

```bash
ros2 run xr_link_test bot_sim
```

---

## What it provides

| Topic | Direction | Content |
|---|---|---|
| `/odom` | out | Pose and velocity from differential-drive integration |
| `/scan` | out | Lidar raycast against a room model, with chassis occlusion |
| `/tf` | out | `odom` → `base_link` → `laser` |
| `/detected_objects` | out | `PoseArray` of cups within detection range |
| `/detected_object_types` | out | JSON array, index-matched |
| `/arm_state` | out | `STOWED`, `READY`, `SWINGING`, `RETURNING` |
| `/bot_state` | out | `IDLE`, `MANUAL`, `RETURNING` |
| `/score_event` | out | JSON on each knock, hit or miss |
| `/plan` | out | Straight-line path to the current goal |
| `/cmd_vel` | in | Drive command |
| `/goal_pose` | in | Drive-to-point target |
| `/arm_command` | in | Arm control |

---

## The world it simulates

A rectangular room with walls, plus two interior obstacles, plus five cups at fixed
positions. The lidar raycasts against all of it.

Cups are reported on `/detected_objects` only when within `detect_range` of the robot,
which approximates a camera's limited view. Knocked cups stop being reported.

The room can be resized with `room_width` and `room_height`.

---

## Lidar occlusion

The real robot's lidar is a 360° unit, but the chassis blocks roughly 120° behind it.
The simulator reproduces this: blocked beams report **positive infinity**, exactly as a
real driver reports a no-return.

This matters. Any renderer consuming `/scan` must handle infinities, and it is far
better to discover that against the simulator than on hardware. Set `occluded_deg:=0.0`
to compare against an unobstructed scan.

```bash
ros2 run xr_link_test bot_sim --ros-args -p occluded_deg:=90.0 -p num_beams:=720
```

---

## Driving it from the command line

Useful for testing without the headset.

```bash
# drive forward and turn
ros2 topic pub -r 10 /cmd_vel geometry_msgs/msg/Twist \
  '{linear: {x: 0.25}, angular: {z: -0.5}}'

# send it somewhere
ros2 topic pub --once /goal_pose geometry_msgs/msg/PoseStamped \
  '{header: {frame_id: "odom"}, pose: {position: {x: 1.5, y: 0.5}}}'

# swing the arm
ros2 topic pub --once /arm_command std_msgs/msg/String \
  '{data: "{\"action\":\"SWING\"}"}'

# watch the results
ros2 topic echo /score_event
ros2 topic echo /arm_state
```

---

## The arm

The simulator implements the same state machine and the same timings as the Unity
`ArmController`:

```
STOWED → READY (0.5 s) → SWINGING (0.6 s) → RETURNING (0.5 s) → STOWED
```

Impact resolves at the end of `SWINGING`. Any cup within 0.35 m and inside the 70°
swept arc is knocked over, producing a `/score_event`:

- `STAR_COLLECTED` — a scoring cup
- `PENALTY_HIT` — a penalty cup
- `SWING_MISSED` — nothing in range

The wheels are locked for the duration of a swing, so the strike lands where it was
aimed. Disable with `arm_locks_wheels:=false`.

A `SWING` arriving mid-swing is ignored rather than restarting the motion, matching
what a real arm can physically do.

---

## Parameters

| Parameter | Default | Meaning |
|---|---|---|
| `rate` | 30.0 | Physics and odometry rate, Hz |
| `scan_rate` | 10.0 | Lidar publish rate, Hz |
| `room_width` / `room_height` | 6.0 / 4.0 | Room size, metres |
| `num_beams` | 360 | Lidar beams per revolution |
| `range_max` | 8.0 | Lidar maximum range, metres |
| `occluded_deg` | 120.0 | Wedge blocked by the chassis |
| `occluded_centre_deg` | 180.0 | Centre of the blocked wedge; 180 is directly behind |
| `max_linear` / `max_angular` | 0.6 / 2.0 | Velocity clamps |
| `cmd_timeout` | 1.0 | Stop if no `/cmd_vel` for this long |
| `detect_range` | 3.0 | Cups closer than this are reported |
| `arm_reach` | 0.35 | Arm reach from the robot centre, metres |
| `arm_arc_deg` | 70.0 | Swept width of a swing |
| `publish_tf` | true | Publish transforms |

**Keep `arm_reach` and `arm_arc_deg` in sync with `ArmController` in Unity.** If they
diverge, the virtual and real robots will behave differently and the difference will be
hard to spot.

---

## Behaviour worth knowing

**Velocity clamping.** Commands above `max_linear` or `max_angular` are clamped, as a
real motor controller would.

**Watchdog.** If no `/cmd_vel` arrives for `cmd_timeout` seconds the robot stops. Real
robots do this so that a lost connection does not leave them driving into a wall.

**Manual overrides autonomy.** A `/cmd_vel` message cancels any active goal.

**Timestep guard.** Absurdly large timesteps — after a suspend, say — are discarded
rather than teleporting the robot.

---

## Limitations

- The robot cannot collide with walls or obstacles; it drives through them. Only the
  lidar is aware of geometry.
- Cups are at fixed positions rather than generated from the app's layout.
- Odometry is exact, with no drift or wheel slip. Real odometry will be worse, which is
  part of why marker-based localisation is planned.
- The path published on `/plan` is a straight line, not a planned route around
  obstacles.
