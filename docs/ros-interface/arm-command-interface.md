# Arm command interface

How the headset tells the robot to move its arm.

**Revised** after reading `gesture_arm_teleop.py` on `arm-control-debugging`.
The earlier version of this document described a `ros_tcp_endpoint` carrying a
`std_msgs/String` topic. That is not what the node does, and the two could
never have interoperated — they are different wire protocols on the same port.

---

## Summary

| | Drive link | Arm link |
|---|---|---|
| Port | `10000` | **`10001`** |
| Transport | `ros_tcp_endpoint` | **raw TCP socket in the node** |
| Payload | `geometry_msgs/Twist` on `/cmd_vel` | **newline-terminated text** |
| Commands | continuous at 10 Hz | `SWEEP` / `KICK` / `SET_HOME` |
| Direction | headset → robot | headset → robot |

The arm link is **not ROS-TCP**. `gesture_arm_teleop.py` binds its own socket:

```python
server_socket.bind(('0.0.0.0', self.tcp_port))   # tcp_port defaults to 10001
...
buffer += data.decode('utf-8')
while "\n" in buffer:
    line, buffer = buffer.split("\n", 1)
    self._trigger_action(line.strip().upper(), ...)
```

So the headset opens a plain TCP connection and writes `SWEEP\n`. No ROS
message framing, no topic, no JSON.

---

## Commands the node accepts

Read straight from `_trigger_action`:

| Command | Effect |
|---|---|
| `SWEEP` | Home -> Left -> Right -> Home |
| `KICK` | Home -> Extend -> Home |
| `SET_HOME` | dynamic home calibration |

Anything else is logged as `Unknown action command received` and dropped.
Commands are **uppercased** by the node, so case does not matter on the wire.

### Lockout

The node refuses every command while a gesture is playing:

```
[LOCKOUT WARNING] Incoming 'SWEEP' from TCP Client ... ignored.
Reason: Arm is currently busy in state: ...
```

So there is no abort. The headset used to send a `STOW` on the B button; that
command does not exist here and would only produce warnings, so B now sends
`KICK`.

---

## Headset side

| Button | Sends |
|---|---|
| **A** | `SWEEP` |
| **B** | `KICK` |

The connection is a raw `TcpClient` opened to `<robot-ip>:10001` once CONNECT is
pressed on the config screen. It reconnects on its own every two seconds if the
node is not listening, and connect/send run off Unity's main thread — a TCP
connect to an unreachable host blocks for the OS timeout, and doing that on the
main thread freezes the headset, which looks like a crash rather than a network
problem.

Settings on `ArmRosPublisher`:

| Setting | Default | Notes |
|---|---|---|
| `useRawTcp` | `true` | off falls back to the old ROS-topic path |
| `armPort` | `10001` | matches the node's `tcp_port` parameter |
| `armIP` | empty | copies the address entered on the connect screen |
| `swingActionName` | `SWEEP` | A button |
| `kickActionName` | `KICK` | B button |
| `cooldownSeconds` | `1.5` | the node also locks out on its own |

---

## Testing without the headset

The node is a plain socket server, so anything can drive it:

```bash
# one command
printf 'SWEEP\n' | nc <robot-ip> 10001

# interactive - type SWEEP or KICK and press enter
nc <robot-ip> 10001
```

Confirm it is listening:

```bash
ss -tlnp | grep 10001
```

The node logs every accepted command with its source, so a successful send from
the headset appears as `[INPUT TRIGGER] Source: TCP Client ('<ip>', <port>)`.

---

## Why the first version of this document was wrong

It specified `std_msgs/String` carrying JSON over a second `ros_tcp_endpoint`,
because that is what `bot_sim` implements and what the Unity side already
spoke. The robot node was written against a different design — a socket it owns
directly — and neither side was wrong in isolation. The mismatch was invisible
from both ends: the headset reported a healthy connection because a TCP socket
did open, and the node reported nothing because ROS-TCP framing never produced
a newline for it to parse.

Worth remembering as a class of bug: **a connection that opens is not a
connection that is understood.**
