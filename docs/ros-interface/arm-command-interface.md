# Arm command interface

How the headset tells the robot to swing or stow its arm.

This is a **second, independent link** from the one carrying `/cmd_vel`. You
need to run two `ros_tcp_endpoint` instances.

---

## Summary

| | Drive link | Arm link |
|---|---|---|
| Port | `10000` | **`10001`** |
| Topic | `/cmd_vel` | **`/arm_command`** |
| Type | `geometry_msgs/Twist` | **`std_msgs/String`** |
| Rate | 10 Hz continuous | on button press only |
| Direction | headset → robot | headset → robot |

The arm link carries **only** `/arm_command`. It does not subscribe to
anything and does not carry TF.

---

## Starting the endpoints

Two instances, different ports. Both must bind `0.0.0.0` — binding `127.0.0.1`
means the headset cannot reach them, which is the single most common cause of
"the app says connecting forever".

```bash
# Terminal 1 - drive commands, unchanged from before
ros2 run ros_tcp_endpoint default_server_endpoint \
  --ros-args -p ROS_IP:=0.0.0.0 -p ROS_TCP_PORT:=10000

# Terminal 2 - arm commands
ros2 run ros_tcp_endpoint default_server_endpoint \
  --ros-args -p ROS_IP:=0.0.0.0 -p ROS_TCP_PORT:=10001
```

Use the vendored copy in `ros2_ws/src/ros_tcp_endpoint` — it carries two
patches without which messages are silently dropped. See `PATCHES.md` there.

---

## Message format

`std_msgs/String` whose `data` is a JSON object.

### Swing

Sent when the player presses **A**.

```json
{"action": "SWING", "yaw": 0.000}
```

- `action` — always upper case
- `yaw` — radians, relative to the robot's heading, `0` = straight ahead.
  Currently always `0`; it exists so aiming can be added without changing the
  message shape. Ignore it if your arm cannot aim.

### Stow

Sent when the player presses **B**. Aborts a swing in progress and returns the
arm to its stowed position.

```json
{"action": "STOW"}
```

No `yaw` field — it would mean nothing here, and including a field that means
nothing invites a later reader to assume it does.

**Stow is not rate limited.** Swing has a 1.5 s cooldown because the arc
outlasts a button press, but stow is exactly what the player presses *during*
that arc. Expect it at any time, including immediately after a swing.

---

## Subscribing

```python
import json
import rclpy
from rclpy.node import Node
from std_msgs.msg import String


class ArmCommandNode(Node):
    def __init__(self):
        super().__init__("arm_command_node")
        self.create_subscription(String, "/arm_command", self.on_command, 10)

    def on_command(self, msg):
        try:
            cmd = json.loads(msg.data)
        except ValueError:
            self.get_logger().warn("bad /arm_command %r" % msg.data)
            return

        action = str(cmd.get("action", "")).upper()

        if action == "SWING":
            yaw = float(cmd.get("yaw", 0.0))
            self.swing(yaw)
        elif action == "STOW":
            self.stow()
        else:
            self.get_logger().warn("unknown arm action %r" % action)
```

Parse defensively and warn on anything unrecognised rather than raising. An
unknown action should not take the node down mid-demo — and a warning in your
log is how we find out the two sides have drifted.

---

## Testing without the headset

```bash
ros2 topic pub --once /arm_command std_msgs/String \
  '{data: "{\"action\": \"SWING\", \"yaw\": 0.0}"}'

ros2 topic pub --once /arm_command std_msgs/String \
  '{data: "{\"action\": \"STOW\"}"}'
```

Watch what the headset is actually sending:

```bash
ros2 topic echo /arm_command
```

`bot_sim` already implements both actions and publishes `/arm_state`
(`STOWED` / `READY` / `SWINGING` / `RETURNING`), so the whole path can be
exercised before the real arm is on the bench.

---

## Why a separate port

Requested for isolation: a flood or a crash on one link cannot disturb the
other, and arm traffic can be captured without `/cmd_vel` at 10 Hz drowning it.

The cost is honest and worth stating: two processes to start, two connections
that can be independently down, and a failure mode where driving works while
the arm silently does nothing. Because of that last one, the headset **falls
back to port 10000** if it cannot open 10001, and the HUD says which link is
carrying the arm:

```
arm: sent #3 SWING via 192.168.1.50:10001
arm: sent #3 SWING via 192.168.1.50:10000 (FALLBACK - main link)
```

If you see `FALLBACK`, the endpoint on 10001 is not running or not reachable —
the arm still works, but on the drive link. Subscribing to `/arm_command` works
identically either way, so nothing on your side needs to change.

---

## Headset side, for reference

| Setting | Where | Default |
|---|---|---|
| `armPort` | `ArmRosPublisher` | `10001` |
| `armIP` | `ArmRosPublisher` | empty = copy the drive link's IP |
| `topicName` | `ArmRosPublisher` | `/arm_command` |
| `swingActionName` | `ArmRosPublisher` | `SWING` |
| `kickActionName` | `ArmRosPublisher` | `STOW` |
| `cooldownSeconds` | `ArmRosPublisher` | `1.5` |

The action strings are plain text fields. If you rename a command on the robot,
it can be matched from the Inspector without a Unity rebuild.
