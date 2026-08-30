#!/usr/bin/env python3
"""
Stand-in for ros_tcp_endpoint that proves what the headset is actually sending.

Run this INSTEAD of ros_tcp_endpoint on port 10000. It speaks enough of the
ROS-TCP wire format to name the topic and decode the payload, and it needs no
ROS installation at all - plain Python 3.

Why this rather than `ros2 topic echo`: echo can only show messages that
survived the whole chain - network, endpoint, ROS graph, QoS. When nothing
arrives it cannot say which link broke. This sits at the first hop, so it
separates three failures that look identical from the headset:

  * nothing connects            -> network, IP, firewall
  * connects, no bytes          -> the app is not publishing
  * bytes arrive, wrong topic   -> the robot subscribes to a different name

Usage:
    python3 tools/ros_tcp_probe.py            # listen on 10000
    python3 tools/ros_tcp_probe.py 10001      # or any other port

Then press CONNECT in the headset with this machine's IP.
"""

import socket
import struct
import sys
import threading
import time

# ROS-TCP-Connector frames every message the same way:
#   <u32 topic-name length><topic name><u32 payload length><payload>
# both little-endian. Anything that does not parse under that shape is
# reported raw rather than guessed at.
HEADER = struct.Struct("<I")


def decode_twist(payload):
    """
    geometry_msgs/Twist: six float64 - linear xyz, angular xyz.

    ROS-TCP-Connector sends 48 raw bytes with NO CDR encapsulation header; the
    endpoint prepends the 4-byte header before handing it to ROS. The first
    version of this decoder demanded 52 bytes and so silently decoded nothing,
    printing empty strings over a stream that was in fact perfectly correct -
    which nearly cost another round of debugging the wrong half of the system.
    """
    if len(payload) == 48:
        body = payload
    elif len(payload) >= 52:
        body = payload[4:52]          # with header, e.g. replayed from a bag
    else:
        return None

    lx, ly, lz, ax, ay, az = struct.unpack("<6d", body)
    return lx, ly, lz, ax, ay, az


def decode_string(payload):
    """
    std_msgs/String: u32 length then bytes, again with no CDR header from
    Unity. Falls back to the header-prefixed layout.
    """
    for offset in (0, 4):
        if len(payload) < offset + 4:
            continue
        try:
            n = struct.unpack_from("<I", payload, offset)[0]
            if 0 < n <= len(payload) - offset - 4:
                return payload[offset + 4:offset + 4 + n] \
                    .rstrip(b"\x00").decode("utf-8", "replace")
        except Exception:
            pass
    return None


def stick_bar(linear, angular, width=9):
    """
    Crude ASCII gauge so stick direction is readable at a glance while driving.
    Watching six-decimal floats scroll past does not tell you whether forward
    means forward.
    """
    def cell(v, scale=0.3):
        n = int(round(max(-1.0, min(1.0, v / scale)) * (width // 2)))
        row = ["-"] * width
        row[width // 2] = "|"
        row[max(0, min(width - 1, width // 2 + n))] = "#"
        return "".join(row)

    return f"fwd[{cell(linear)}] turn[{cell(angular, 1.0)}]"


def read_exactly(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def handle_client(conn, addr):
    print(f"\n[CONNECTED] {addr[0]}:{addr[1]}\n")

    counts = {}
    last_report = time.time()
    first = True

    try:
        while True:
            raw_len = read_exactly(conn, 4)
            if raw_len is None:
                break

            topic_len = HEADER.unpack(raw_len)[0]

            # A wildly large length means this is not ROS-TCP framing - most
            # likely something else entirely is connected to this port.
            if topic_len > 4096:
                print(f"[!] topic length {topic_len} is implausible - "
                      f"this does not look like ROS-TCP framing")
                break

            topic = read_exactly(conn, topic_len)
            if topic is None:
                break
            topic = topic.decode("utf-8", "replace")

            raw_size = read_exactly(conn, 4)
            if raw_size is None:
                break
            size = HEADER.unpack(raw_size)[0]

            payload = read_exactly(conn, size) if size else b""
            if payload is None:
                break

            counts[topic] = counts.get(topic, 0) + 1

            if first:
                print(f"[FIRST MESSAGE] topic={topic!r} {size} bytes")
                first = False

            # Handshake and syscommand carry JSON; publishes carry CDR.
            if topic.startswith("__"):
                print(f"  {topic}: {payload.decode('utf-8', 'replace').strip()}")
                continue

            twist = decode_twist(payload)
            if twist:
                lx, ly, lz, ax, ay, az = twist
                moving = abs(lx) > 1e-6 or abs(az) > 1e-6

                # A 10 Hz stream of zeros scrolls everything useful off the
                # screen, so idle frames are counted rather than printed - but
                # the transition into and out of idle is shown, because "the
                # stick did something" is the fact under test.
                if moving:
                    bar = stick_bar(lx, az)
                    print(f"  DRIVE  linear.x={lx:+.3f}  angular.z={az:+.3f}  "
                          f"{bar}   (#{counts[topic]})")
                elif counts[topic] < 3 or counts.get("_was_moving"):
                    print(f"  DRIVE  idle (0.000, 0.000)   (#{counts[topic]})")

                counts["_was_moving"] = moving
                continue

            text = decode_string(payload)
            if text is not None:
                # Arm commands land here once both links share port 10000.
                print(f"  BUTTON {topic}  ->  {text}   (#{counts[topic]})")
            else:
                print(f"  {topic}  {size} bytes   (#{counts[topic]})")

            if time.time() - last_report > 5:
                summary = "  ".join(f"{t}={n}" for t, n in counts.items())
                print(f"[5s] {summary}")
                last_report = time.time()

    except ConnectionResetError:
        print("[DISCONNECTED] client reset the connection")
    except Exception as e:
        print(f"[ERROR] {e}")
    finally:
        conn.close()
        print(f"\n[CLOSED] {addr[0]}  totals: {counts or 'nothing received'}\n")


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 10000

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

    # 0.0.0.0, never 127.0.0.1. Binding loopback is the single most common
    # reason a headset "cannot reach" a robot that is plainly running.
    srv.bind(("0.0.0.0", port))
    srv.listen(5)

    print(f"ros_tcp_probe listening on 0.0.0.0:{port}")
    print("Press CONNECT in the headset pointed at this machine.")
    print("Ctrl-C to stop.\n")

    try:
        while True:
            conn, addr = srv.accept()
            threading.Thread(target=handle_client, args=(conn, addr),
                             daemon=True).start()
    except KeyboardInterrupt:
        print("\nstopped")
    finally:
        srv.close()


if __name__ == "__main__":
    main()
