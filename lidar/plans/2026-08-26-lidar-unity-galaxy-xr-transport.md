# LiDAR-to-Galaxy XR Unity Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transmit the JetRover's ROS 2 LiDAR scan to a new Unity Android XR application running on Samsung Galaxy XR and render the scan as a 2D point cloud.

**Architecture:** Keep ROS 2 and the LiDAR driver on the Rubik Pi. Implement two interchangeable headset transports: a rosbridge WebSocket path for the fastest prototype and a compact UDP bridge for lowest latency and bandwidth. Render the first version in the LiDAR-local frame; defer native ROS 2 inside Unity and world-frame TF integration.

**Tech Stack:** ROS 2 Jazzy, Python `rclpy`, `sensor_msgs/msg/LaserScan`, rosbridge WebSocket, UDP, Unity 6, Android XR/OpenXR, C# networking, dynamic Unity mesh rendering.

## Global Constraints

- The ROS input topic is `/scan` with type `sensor_msgs/msg/LaserScan`.
- The headset is Samsung Galaxy XR running the Unity application as an Android XR build.
- The headset and Rubik Pi communicate over the same local network.
- The low-latency path may drop an occasional scan and must always prefer the newest complete scan.
- The initial display uses the LiDAR-local coordinate frame; `/tf` and `/tf_static` are not required for the first visualizer.
- Do not add native ROS 2 libraries or long-term ROS integration to the Unity application in this phase.
- Do not create one Unity GameObject per LiDAR point; update one reusable render buffer.

---

## Current Repository Context

- `docs/Lidar_Bringup.md` documents the external `rplidar_ros` driver, `/scan`, and a static `map → laser` transform.
- `ros2_ws/src/my_robot_bringup/launch/jetrover.launch.py` does not currently launch a LiDAR driver or transport bridge.
- The Unity headset application is a new project for this plan; do not modify or depend on unrelated Unity files elsewhere in the workspace.

## Transport Interfaces

### ROS input

The bridge subscribes to:

```text
Topic: /scan
Type:  sensor_msgs/msg/LaserScan
QoS:   sensor-data compatible; use the publisher's offered QoS when available
```

### Prototype WebSocket interface

Run `rosbridge_websocket` on the Rubik Pi. Unity connects to:

```text
ws://<rubik-pi-ip>:9090
```

Unity sends a rosbridge subscription request for `/scan` and parses the `LaserScan` JSON payload. The Unity receive path uses a bounded queue of one item: a newly received scan replaces an older unrendered scan.

### Low-latency UDP interface

The custom ROS node sends one complete scan per packet when the encoded payload is at most 1200 bytes. The default port is `39001`; expose the port as a ROS parameter and Unity setting.

Packet layout, little-endian:

```text
u32 magic             # ASCII "LIDR" as 0x5244494c in little-endian
u8  version            # 1
u8  flags              # reserved; must be zero in version 1
u16 sample_count
u32 sequence
u64 stamp_ns
f32 angle_min
f32 angle_increment
f32 range_min
f32 range_max
u16 ranges_mm[sample_count]
```

Use `0xFFFF` for NaN, infinity, zero, or values outside `[range_min, range_max]`. Unity converts millimeters to meters. A 360-sample scan must fit in one datagram. If a future scanner does not fit, reject the scan with a diagnostic rather than silently fragmenting version-1 packets.

### Optional bag-playback source

Use a recorded bag as an optional replacement for `rplidar_ros` when a physical LiDAR is unavailable or deterministic/repeatable testing is needed. The bag must contain `/scan`; include `/tf` and `/tf_static` when testing a later world-frame integration.

Record a focused bag from a live LiDAR session:

```bash
timeout --signal=SIGINT --kill-after=10s 60s \
  ros2 bag record \
  -o lidar_capture_$(date +%Y%m%d_%H%M%S) \
  /scan \
  /tf \
  /tf_static
```

Replay the bag in a loop with simulated playback time:

```bash
ros2 bag play lidar_capture_YYYYMMDD_HHMMSS --loop --clock
```

Start the UDP bridge against the headset in a separate terminal while the bag is playing:

```bash
ros2 run lidar_unity_bridge lidar_udp_bridge --ros-args \
  -p destination_ip:=<galaxy-xr-ip> \
  -p destination_port:=39001
```

For the WebSocket prototype, start `rosbridge_websocket` instead of the UDP bridge and connect Unity to `ws://<rubik-pi-ip>:9090`. Do not run `rplidar_ros` at the same time as bag playback; both would publish `/scan` and make the input nondeterministic.

## Implementation Tasks

### Task 1: Define the bridge package and shared scan conversion

**Files:**
- Create: `ros2_ws/src/lidar_unity_bridge/package.xml`
- Create: `ros2_ws/src/lidar_unity_bridge/setup.py`
- Create: `ros2_ws/src/lidar_unity_bridge/lidar_unity_bridge/scan_codec.py`
- Create: `ros2_ws/src/lidar_unity_bridge/test/test_scan_codec.py`

**Interfaces:**
- `encode_udp_scan(scan: LaserScan, sequence: int) -> bytes`
- `decode_udp_scan(packet: bytes) -> DecodedScan`
- `DecodedScan` fields: `sequence: int`, `stamp_ns: int`, `angle_min: float`, `angle_increment: float`, `range_min: float`, `range_max: float`, `ranges_m: list[float | None]`

- [ ] Add the ROS 2 Python package with `rclpy` and `sensor_msgs` runtime dependencies.
- [ ] Implement little-endian header packing using the packet layout above.
- [ ] Convert valid meter ranges to rounded millimeters and encode invalid values as `0xFFFF`.
- [ ] Reject malformed magic, unsupported version, nonzero reserved flags, truncated headers, truncated range arrays, and payloads over 1200 bytes.
- [ ] Write tests for valid scans, invalid ranges, sequence/timestamp preservation, malformed packets, and a 360-sample packet size.
- [ ] Run `colcon test --packages-select lidar_unity_bridge` and require all codec tests to pass.

### Task 2: Implement the ROS 2 UDP bridge node

**Files:**
- Create: `ros2_ws/src/lidar_unity_bridge/lidar_unity_bridge/udp_bridge.py`
- Modify: `ros2_ws/src/lidar_unity_bridge/setup.py`
- Test: `ros2_ws/src/lidar_unity_bridge/test/test_udp_bridge.py`

**Interfaces:**
- Executable: `lidar_udp_bridge`
- ROS parameters:
  - `destination_ip: string`, required at runtime
  - `destination_port: int`, default `39001`
  - `scan_topic: string`, default `/scan`
  - `max_packet_bytes: int`, default `1200`
- UDP output: one version-1 packet per accepted `/scan` message.

- [ ] Declare and validate the parameters before creating the socket.
- [ ] Subscribe to `/scan` with sensor-data-compatible QoS.
- [ ] Increment the sequence number for every received scan, including scans rejected for size.
- [ ] Send only valid-size packets and publish throttled ROS log diagnostics for rejected scans and socket errors.
- [ ] Close the socket during node shutdown.
- [ ] Test the node with a local UDP receiver and a synthetic `LaserScan`; verify payload contents and sequence increments.
- [ ] Add the executable entry point and run package tests.

Example launch command:

```bash
ros2 run lidar_unity_bridge lidar_udp_bridge --ros-args \
  -p destination_ip:=<galaxy-xr-ip> \
  -p destination_port:=39001
```

### Task 3: Document and validate the rosbridge prototype path

**Files:**
- Create: `docs/Lidar_Unity_Transport.md`
- Modify: `ros2_ws/src/lidar_unity_bridge/package.xml`

- [ ] Document installing and starting `rosbridge_websocket` on the Rubik Pi.
- [ ] Document the Unity WebSocket URL and rosbridge subscription request for `/scan`.
- [ ] Document that rosbridge JSON is the prototype baseline, not the bandwidth benchmark.
- [ ] Add a command sequence for recording/replaying a focused bag containing `/scan`, `/tf`, and `/tf_static`.
- [ ] Document bag playback with `ros2 bag play <bag> --loop --clock` as an optional replacement for the physical LiDAR driver.
- [ ] Add troubleshooting for wrong IP address, blocked TCP port 9090, missing `/scan`, and stale Unity queue data.
- [ ] Validate the path by subscribing with a minimal WebSocket test client before Unity integration.

### Task 4: Create the new Unity transport and rendering components

**Files:**
- Create: `UnityProject/Assets/Scripts/Lidar/LidarScanData.cs`
- Create: `UnityProject/Assets/Scripts/Lidar/LidarUdpReceiver.cs`
- Create: `UnityProject/Assets/Scripts/Lidar/LidarWebSocketReceiver.cs`
- Create: `UnityProject/Assets/Scripts/Lidar/LidarScanRenderer.cs`
- Create: `UnityProject/Assets/Scripts/Lidar/LidarTransportSettings.cs`

**Interfaces:**
- `LidarScanData` contains sequence, timestamp, angular metadata, and a reusable range buffer.
- `ILidarScanReceiver` exposes `bool TryGetLatest(out LidarScanData scan)` and `void Start()`/`void Stop()`.
- `LidarScanRenderer` consumes the newest scan once per Unity frame.

- [ ] Implement UDP receive on a background thread or asynchronous socket without blocking the Unity main thread.
- [ ] Validate packet magic, version, flags, sample count, sequence, and packet length before publishing a scan.
- [ ] Use a single latest-scan slot protected by a lock or atomic exchange; discard older scans when a newer complete scan arrives.
- [ ] Track received packets, malformed packets, sequence gaps, and last receive time.
- [ ] Implement the WebSocket receiver behind the same `ILidarScanReceiver` interface.
- [ ] Decode each valid range into local coordinates using `cos`/`sin` and the scan’s angular metadata.
- [ ] Render with one dynamic mesh using `MeshTopology.Points`; reuse vertex storage and omit invalid samples.
- [ ] Expose IP, port, transport selection, point size, scale, and maximum range in the Inspector or runtime settings.
- [ ] Add a diagnostics panel for packet count, rendered scan count, dropped packets, sequence gaps, and receive-to-render latency.

### Task 5: Validate on Galaxy XR and compare transports

- [ ] Build the Unity project for the Galaxy XR Android XR target.
- [ ] Run a live LiDAR test over the shared local network using the WebSocket prototype.
- [ ] Run the same test using the UDP bridge.
- [ ] Replay a `/scan` bag into the bridge and verify deterministic Unity rendering.
- [ ] Replay the bag with `--loop --clock` for at least two complete cycles and verify the Unity receiver continues across the loop boundary.
- [ ] Verify that invalid ranges do not create origin points.
- [ ] Introduce packet loss or temporarily disable Wi-Fi and verify the UDP renderer resumes with the newest sequence.
- [ ] Confirm the Unity app does not freeze, buffer unboundedly, or display increasingly stale scans.
- [ ] Record receive-to-render latency and bandwidth for both paths over at least 60 seconds.
- [ ] Select UDP as the default transport if it meets the latency target and remains visually stable; keep WebSocket available for debugging.

## Acceptance Criteria

- A live `/scan` stream is visible on Galaxy XR without RViz or a desktop relay.
- The WebSocket path demonstrates the fastest end-to-end prototype.
- The UDP path uses compact binary packets, latest-only behavior, and no per-point GameObjects.
- A dropped UDP packet does not block later scans or cause the display to rewind.
- A recorded bag can feed the ROS bridge and reproduce the scan visualization.
- The first release displays the scan in LiDAR-local coordinates; no TF dependency is required.
- The transport and renderer expose enough diagnostics to distinguish ROS, network, decoding, and rendering failures.

## Explicit Non-Goals

- Native ROS 2 middleware or ROS 2 message generation inside Unity.
- Unity ROS TCP Connector integration unless a later compatibility test proves it is simpler than rosbridge for this ROS 2 Jazzy setup.
- World-frame alignment using `/tf` and `/tf_static`.
- Mapping, SLAM, navigation, obstacle avoidance, or persistent point-cloud accumulation.
- Reliable delivery of every historical scan; the viewer always prefers current data over completeness.
