# Vendored `ros_tcp_endpoint` — why, and what changed

## Why this is vendored

Upstream: <https://github.com/Unity-Technologies/ROS-TCP-Endpoint>, branch `main-ros2`, version 0.7.0.

Two reasons it lives in this repo rather than being cloned during setup:

1. **It is not installable.** `ros_tcp_endpoint` has never been released as a
   Debian package for any ROS 2 distro. `sudo apt install ros-jazzy-ros-tcp-endpoint`
   does not work and never has. Source is the only option.
2. **It does not work unmodified** against a current ROS-TCP-Connector. Two
   defects below silently break the Unity to ROS 2 data path. Both were found
   the hard way on 2026-08-10 while bringing up the RubikPi 3.

Vendoring also pins the version. An unpinned clone means whoever sets up a board
next month gets whatever upstream looks like that day, and rediscovers these bugs.

Verified working on: **RubikPi 3, Ubuntu 24.04, ROS 2 Jazzy, Python 3.12**,
against the Unity ROS-TCP-Connector pulled from the git default branch.

---

## Patch 1 — `server.py`, `handle_syscommand()`: JSON payload truncation

**Symptom.** Every Unity connection immediately produced
`json.decoder.JSONDecodeError`, the client thread died, and the connection
dropped. Repeatedly, forever. No topic was ever registered.

**Cause.** The original line:

```python
message_json = data.decode("utf-8")[:-1]
```

That `[:-1]` unconditionally discards the final character. It exists to strip a
trailing NUL byte that older ROS-TCP-Connector releases appended to syscommand
payloads. The current connector does not send that NUL, so the code was eating
the closing `}` of the JSON instead:

```
payload='{"topic":"/cmd_vel","message_name":"geometry_msgs/Twist","queue_size":10,"latch":false'
payload='{'                              <- this one was originally '{}'
```

**Fix.** Strip actual NUL bytes rather than blindly removing one character, and
log rather than die if a payload is still unparseable:

```python
message_json = data.decode("utf-8").rstrip("\x00").strip()
try:
    params = json.loads(message_json)
except json.JSONDecodeError as e:
    self.logerr("BAD SYSCOMMAND topic={!r} err={} payload={!r}".format(
        topic, e, message_json))
    return
function(**params)
```

This is correct for **both** protocol generations — connectors that send the
terminator and connectors that do not. Note that upstream `client.py` already
does exactly this (`destination.rstrip("\x00")`) for the destination string; the
syscommand path was simply inconsistent with it.

---

## Patch 2 — `publisher.py`, `RosPublisher.send()`: missing CDR header

**Symptom.** Harder to spot than patch 1, because **nothing errored**. After
patch 1, registration succeeded (`RegisterPublisher(/cmd_vel, ...) OK`) and
Unity streamed data frames at the expected rate — the endpoint logged
`FRAME dest='/cmd_vel' len=48` continuously. But `ros2 topic echo /cmd_vel`
returned nothing and no subscriber ever received a message. Silent data loss.

**Cause.** The original method, with its own deserialization commented out:

```python
def send(self, data):
    # message_type = type(self.msg)
    # message = deserialize_message(data, message_type)

    self.pub.publish(data)
```

Passing `bytes` to `rclpy`'s `publish()` makes it treat them as an
already-CDR-serialized message and push them to the wire unvalidated.
Unity sends the **bare message body with no CDR encapsulation header**, so what
went out was malformed and every subscriber dropped it during deserialization —
without logging anything.

The frame length is the proof. `geometry_msgs/Twist` is six `float64` values =
**48 bytes** of payload. A valid CDR message carries a 4-byte encapsulation
header, so a correct frame would be **52 bytes**. The logs showed 48.

**Fix.** Prepend the little-endian CDR header, deserialize properly, and publish
a real message object:

```python
def send(self, data):
    try:
        # Unity sends the raw message body with no CDR encapsulation header.
        # ROS 2 requires one: b"\x00\x01\x00\x00" = little-endian CDR.
        message = deserialize_message(b"\x00\x01\x00\x00" + data, type(self.msg))
        self.pub.publish(message)
    except Exception as e:
        self.get_logger().error(
            "publish failed on {}: {} (len={})".format(self.pub.topic_name, e, len(data)))
    return None
```

Deserializing explicitly rather than publishing raw bytes means a future format
mismatch fails **loudly**, with the topic name and payload length, instead of
disappearing.

---

## Patch 3 — `debug_frames` parameter (diagnostic aid)

`client.py` can log every inbound frame's destination and length. This is what
made patch 2 findable: it proved Unity was sending data that the endpoint was
receiving and then losing. Off by default because it logs at the full publish
rate.

```bash
ros2 run ros_tcp_endpoint default_server_endpoint \
  --ros-args -p ROS_IP:=0.0.0.0 -p debug_frames:=true
```

Reach for it whenever topics register but no data reaches the graph.

---

## Patch 4 — `setup.cfg` dash-separated options

Changed `script-dir` to `script_dir` and `install-scripts` to `install_scripts`.
Setuptools on Python 3.12 warns that the dashed forms will stop working; this
avoids a future hard build failure.

---

## Build

```bash
cd ~/ros2_ws
colcon build --symlink-install --packages-select ros_tcp_endpoint
source install/setup.bash
ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0
```

Bind to `0.0.0.0`, not the board's own address — otherwise the socket is
reachable on one interface only, which breaks as soon as the board has both
Ethernet and Wi-Fi.

## Verifying the link

```bash
# terminal 2
python3 ~/ros2_ws/src/xr_link_test/xr_link_test/link_monitor.py \
  --ros-args -p preset:=headset -p full_message:=false
```

Working output looks like:

```
>>> FIRST MESSAGE on /cmd_vel <<<
[  12.345] /cmd_vel  #1  9.0Hz  v=[+0.867 +0.000 +0.000] m/s  w=[+0.000 +0.000 +0.700] rad/s
```

Baseline measured 2026-08-10, Galaxy XR over Wi-Fi to RubikPi 3:
**9.0 Hz, 3.1 ms jitter** (versus 0.9 ms for a local `ros2 topic pub`, so the
wireless path costs roughly 2.2 ms of jitter).

---

## Note on the future

These defects are why BotXRGame is moving to its own XR-to-Linux transport.
Upstream `ros_tcp_endpoint` is effectively unmaintained, its wire protocol has
drifted from the Unity package it is meant to pair with, and the failure modes
are silent. This vendored copy is a working stopgap, not a long-term dependency.
