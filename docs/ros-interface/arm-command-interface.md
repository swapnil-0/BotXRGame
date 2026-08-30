# Arm command interface

How the headset tells the robot to move its arm.

**Revised** after reading `gesture_arm_teleop.py` on `arm-control-debugging`.
The earlier version of this document described a `ros_tcp_endpoint` carrying a
`std_msgs/String` topic. That is not what the node does, and the two could
never have interoperated — they are different wire protocols on the same port.

---

## Summary

**Both links now share port 10000.** The headset opens one ROS-TCP connection
and publishes two topics on it.

| | Drive | Arm |
|---|---|---|
| Port | `10000` | `10000` (same connection) |
| Topic | `/cmd_vel` | `/arm_command` |
| Type | `geometry_msgs/Twist` | `std_msgs/String` |
| Rate | 10 Hz continuous | on button press |

One endpoint to run, one connection to debug, one thing that can be down. The
earlier two-port arrangement added a second endpoint and a failure mode where
driving worked while the arm silently did nothing.

```bash
ros2 run ros_tcp_endpoint default_server_endpoint \
  --ros-args -p ROS_IP:=0.0.0.0 -p ROS_TCP_PORT:=10000
```

Bind `0.0.0.0`, never `127.0.0.1` - a loopback bind is unreachable from the
headset and looks exactly like a robot that is switched off.

---

## Robot side: what needs to change

`gesture_arm_teleop.py` currently runs its own TCP server on 10001 and reads
newline-terminated lines. To use the shared link, subscribe instead:

```python
from std_msgs.msg import String

self.create_subscription(String, "/arm_command", self._on_arm_command, 10)

def _on_arm_command(self, msg):
    self._trigger_action(msg.data.strip().upper(), source="ROS /arm_command")
```

`_trigger_action` needs no changes - the payload is the same bare command word
the socket path already receives.

The socket server can stay for bench testing with `nc`; the two paths do not
conflict.

---

## Commands

| Command | Effect | Button |
|---|---|---|
| `SWEEP` | Home -> Left -> Right -> Home | **A** |
| `KICK` | Home -> Extend -> Home | **B** |
| `SET_HOME` | dynamic home calibration | not bound |

Sent as the bare word, uppercased by the node. **Not JSON** - the node matches
the whole string, so a JSON wrapper arrives as an unknown action and is
dropped.

The node refuses commands while a gesture plays and logs the rejection, so
there is no abort and none is sent.

---

## Testing

Watch what the headset actually sends, with no ROS installed at all:

```bash
# stop the endpoint first - it holds the port
python3 tools/ros_tcp_probe.py
```

```
[CONNECTED] 192.168.1.102:42526
  __publish: {"topic":"/cmd_vel","message_name":"geometry_msgs/Twist",...}
  DRIVE  linear.x=+0.150  angular.z=-0.268  fwd[----|-#--] turn[---#|----]
  BUTTON /arm_command  ->  SWEEP
```

With the endpoint running instead:

```bash
ros2 topic echo /cmd_vel
ros2 topic echo /arm_command
ros2 topic pub --once /arm_command std_msgs/String '{data: "SWEEP"}'
```

The legacy socket path still works if the robot has not moved to the topic yet -
set `useRawTcp` on `ArmRosPublisher` and run the node as before:

```bash
printf 'SWEEP\n' | nc <robot-ip> 10001
```

---

## Settings on ArmRosPublisher

| Setting | Default | Notes |
|---|---|---|
| `useMainConnection` | `true` | publish on the /cmd_vel link, port 10000 |
| `useRawTcp` | `false` | legacy socket on 10001 |
| `topicName` | `/arm_command` | |
| `swingActionName` | `SWEEP` | A button |
| `kickActionName` | `KICK` | B button |
| `cooldownSeconds` | `1.5` | the node also locks out on its own |

---

## Wire format note

ROS-TCP-Connector sends message bodies **without** the 4-byte CDR
encapsulation header; the endpoint prepends it. A `Twist` is 48 bytes on the
wire, not 52. This matters for anything parsing the stream directly - the first
version of `ros_tcp_probe.py` expected 52 and silently decoded nothing from a
stream that was entirely correct.
