#!/usr/bin/env python3
"""
link_monitor - print every ROS 2 message arriving on the board.

Purpose
-------
Answer one question fast: "is the Galaxy XR headset actually reaching ROS?"

This is a plain DDS subscriber, so it is deliberately bridge-agnostic. It sees
traffic whether it arrived via ros_tcp_endpoint, rosbridge, `ros2 topic pub`,
or a real robot node. That separates "did the message reach ROS" from "is the
bridge configured right" -- which are the two failures you cannot otherwise
tell apart.

Topics are discovered at runtime from the ROS graph, so no message types are
hardcoded and new topics are picked up as soon as the headset advertises them.

Run standalone (no build needed)
--------------------------------
    source /opt/ros/jazzy/setup.bash
    python3 link_monitor.py

Or as a package node
--------------------
    ros2 run xr_link_test link_monitor

Common variants
---------------
    # only what the headset sends us, one line per message
    python3 link_monitor.py --ros-args -p preset:=headset -p full_message:=false

    # everything except the high-rate streams
    python3 link_monitor.py --ros-args -p exclude:='/head_pose|/odom'

Parameters
----------
preset          'headset' | 'robot' | 'all'  convenience filters   (default all)
include         regex; topic must match to be subscribed           (default '.*')
exclude         regex; topics matching are skipped        (default noise topics)
full_message    print the full body, not just the summary line     (default True)
max_body_lines  truncate long message bodies to this many lines    (default 20)
discovery_hz    how often to rescan the graph for new topics       (default 1.0)
stats_period    seconds between the rate summary table, 0 = off    (default 5.0)
"""

import re
import time
from collections import deque

import rclpy
from rclpy.node import Node
from rclpy.qos import (
    QoSProfile,
    QoSDurabilityPolicy,
    QoSHistoryPolicy,
    QoSReliabilityPolicy,
)
from rosidl_runtime_py import message_to_yaml
from rosidl_runtime_py.utilities import get_message

# ---------------------------------------------------------------- appearance

DIM = "\033[2m"
BOLD = "\033[1m"
RED = "\033[31m"
GREEN = "\033[32m"
YELLOW = "\033[33m"
BLUE = "\033[34m"
CYAN = "\033[36m"
RESET = "\033[0m"

# Topics that are always noise for our purposes. /client_count and
# /connected_clients are published by rosbridge; the rest are ROS internals.
DEFAULT_EXCLUDE = (
    r"^/(rosout|parameter_events|client_count|connected_clients)$"
    r"|^/_"
    r"|/(get|set|list|describe)_parameters"
)

# Presets built from docs/ros-interface/ros-unity-interface.md.
PRESETS = {
    # What the headset sends to the robot. This is the Week 1 question.
    "headset": r"^/(cmd_vel|head_pose|game_object_map|game_object_types"
               r"|game_state|play_area)$",
    # What the robot sends back to the headset.
    "robot": r"^/(odom|bot_state|score_event|joint_states|scan)$"
             r"|^/camera/",
    "all": r".*",
}


# ------------------------------------------------------------- formatters
#
# Raw YAML for a Twist is eight lines of mostly zeros. At 10-20 Hz that is
# unreadable. These collapse the message types we actually care about down to
# one useful line; anything unrecognised falls back to YAML.

def _fmt_twist(m):
    return ("v=[%+.3f %+.3f %+.3f] m/s   w=[%+.3f %+.3f %+.3f] rad/s"
            % (m.linear.x, m.linear.y, m.linear.z,
               m.angular.x, m.angular.y, m.angular.z))


def _fmt_pose_stamped(m):
    p, o = m.pose.position, m.pose.orientation
    return ("frame=%-10s pos=[%+.3f %+.3f %+.3f]  quat=[%+.3f %+.3f %+.3f %+.3f]"
            % (m.header.frame_id or "-", p.x, p.y, p.z, o.x, o.y, o.z, o.w))


def _fmt_odometry(m):
    p = m.pose.pose.position
    t = m.twist.twist
    return ("pos=[%+.3f %+.3f]  v=%+.3f m/s  w=%+.3f rad/s  (%s -> %s)"
            % (p.x, p.y, t.linear.x, t.angular.z,
               m.header.frame_id or "-", m.child_frame_id or "-"))


def _fmt_string(m):
    d = m.data
    return ('"%s"' % d) if len(d) <= 160 else ('"%s..." (%d chars)' % (d[:157], len(d)))


def _fmt_pose_array(m):
    return "%d poses in frame '%s'" % (len(m.poses), m.header.frame_id or "-")


def _fmt_laser_scan(m):
    n = len(m.ranges)
    finite = [r for r in m.ranges if r not in (float("inf"), float("-inf")) and r == r]
    rng = ("min=%.2f max=%.2f" % (min(finite), max(finite))) if finite else "no returns"
    return "%d beams  %s m  frame=%s" % (n, rng, m.header.frame_id or "-")


def _fmt_joint_state(m):
    pairs = ", ".join("%s=%+.2f" % (n, p) for n, p in zip(m.name, m.position))
    return pairs if len(pairs) <= 160 else pairs[:157] + "..."


def _fmt_compressed_image(m):
    return "%s, %d bytes" % (m.format, len(m.data))


FORMATTERS = {
    "geometry_msgs/msg/Twist": _fmt_twist,
    "geometry_msgs/msg/TwistStamped": lambda m: _fmt_twist(m.twist),
    "geometry_msgs/msg/PoseStamped": _fmt_pose_stamped,
    "geometry_msgs/msg/PoseArray": _fmt_pose_array,
    "nav_msgs/msg/Odometry": _fmt_odometry,
    "std_msgs/msg/String": _fmt_string,
    "sensor_msgs/msg/LaserScan": _fmt_laser_scan,
    "sensor_msgs/msg/JointState": _fmt_joint_state,
    "sensor_msgs/msg/CompressedImage": _fmt_compressed_image,
}


# ------------------------------------------------------------------- stats

class TopicStat:
    """Rolling arrival statistics for a single topic."""

    def __init__(self, type_name):
        self.type_name = type_name
        self.count = 0
        self.last_rx = None
        self._gaps = deque(maxlen=50)

    def tick(self):
        now = time.monotonic()
        gap = (now - self.last_rx) if self.last_rx is not None else 0.0
        if self.last_rx is not None:
            self._gaps.append(gap)
        self.last_rx = now
        self.count += 1
        return gap

    @property
    def hz(self):
        if not self._gaps:
            return 0.0
        mean = sum(self._gaps) / len(self._gaps)
        return (1.0 / mean) if mean > 0 else 0.0

    @property
    def jitter_ms(self):
        if len(self._gaps) < 2:
            return 0.0
        mean = sum(self._gaps) / len(self._gaps)
        var = sum((g - mean) ** 2 for g in self._gaps) / len(self._gaps)
        return (var ** 0.5) * 1000.0

    @property
    def age(self):
        return (time.monotonic() - self.last_rx) if self.last_rx else float("inf")


# -------------------------------------------------------------------- node

class LinkMonitor(Node):

    def __init__(self):
        super().__init__("link_monitor")

        self.declare_parameter("preset", "all")
        self.declare_parameter("include", "")
        self.declare_parameter("exclude", DEFAULT_EXCLUDE)
        self.declare_parameter("full_message", True)
        self.declare_parameter("max_body_lines", 20)
        self.declare_parameter("discovery_hz", 1.0)
        self.declare_parameter("stats_period", 5.0)

        g = self.get_parameter

        preset = str(g("preset").value).lower()
        explicit = str(g("include").value)
        if explicit:
            pattern = explicit          # explicit include always wins
        elif preset in PRESETS:
            pattern = PRESETS[preset]
        else:
            self.get_logger().warn(
                "unknown preset %r, falling back to 'all'. valid: %s"
                % (preset, ", ".join(sorted(PRESETS)))
            )
            pattern = PRESETS["all"]

        self._include = re.compile(pattern)
        self._exclude = re.compile(str(g("exclude").value))
        self._full = bool(g("full_message").value)
        self._max_lines = int(g("max_body_lines").value)

        self._subs = {}        # topic -> Subscription, or None if unresolvable
        self._stats = {}       # topic -> TopicStat
        self._t0 = time.monotonic()
        self._total = 0

        # Best-effort + volatile is the maximally permissive subscriber QoS:
        # it can receive from reliable and transient-local publishers alike.
        # Bridges and Nav2 do not agree on defaults, so this avoids silent
        # QoS-incompatibility, which looks exactly like "no data".
        self._qos = QoSProfile(
            history=QoSHistoryPolicy.KEEP_LAST,
            depth=10,
            reliability=QoSReliabilityPolicy.BEST_EFFORT,
            durability=QoSDurabilityPolicy.VOLATILE,
        )

        self.create_timer(1.0 / max(float(g("discovery_hz").value), 0.1),
                          self._discover)

        stats_period = float(g("stats_period").value)
        if stats_period > 0:
            self.create_timer(stats_period, self._print_stats)

        print("%s%s link_monitor %s  preset=%s  full=%s"
              % (BOLD, BLUE, RESET, preset, self._full), flush=True)
        print("%swatching topics matching:%s %s" % (DIM, RESET, pattern), flush=True)
        print("%swaiting for traffic... (Ctrl-C to stop)%s\n" % (DIM, RESET),
              flush=True)

    # --------------------------------------------------------------- graph

    def _discover(self):
        for topic, types in self.get_topic_names_and_types():
            if topic in self._subs or not types:
                continue
            if not self._include.search(topic) or self._exclude.search(topic):
                continue

            type_name = types[0]
            try:
                msg_cls = get_message(type_name)
            except (ImportError, AttributeError, ValueError, ModuleNotFoundError) as exc:
                self.get_logger().warn(
                    "cannot resolve %s on %s (%s) - is the message package "
                    "installed?" % (type_name, topic, exc)
                )
                self._subs[topic] = None      # remember, so we stop retrying
                continue

            self._stats[topic] = TopicStat(type_name)
            self._subs[topic] = self.create_subscription(
                msg_cls, topic, lambda msg, t=topic: self._on_msg(t, msg), self._qos
            )
            print("%s+ watching%s %s%-24s%s %s%s%s  (%d publisher(s))"
                  % (GREEN, RESET, BOLD, topic, RESET,
                     DIM, type_name, RESET, self.count_publishers(topic)),
                  flush=True)

    # ------------------------------------------------------------- receive

    def _on_msg(self, topic, msg):
        st = self._stats[topic]
        first = st.count == 0
        gap = st.tick()
        self._total += 1
        t = time.monotonic() - self._t0

        if first:
            # The moment that answers "did the headset connect".
            print("\n%s%s>>> FIRST MESSAGE on %s <<<%s"
                  % (BOLD, GREEN, topic, RESET), flush=True)

        summary = self._summarise(st.type_name, msg)
        print("%s[%9.3f]%s %s%-20s%s %s#%-5d %6.1fHz%s  %s"
              % (CYAN, t, RESET, BOLD, topic, RESET,
                 DIM, st.count, st.hz, RESET, summary),
              flush=True)

        if self._full and st.type_name not in FORMATTERS:
            self._print_body(msg)

    def _summarise(self, type_name, msg):
        fmt = FORMATTERS.get(type_name)
        if fmt is None:
            return "%s<%s>%s" % (DIM, type_name.rsplit("/", 1)[-1], RESET)
        try:
            return fmt(msg)
        except Exception as exc:                      # never let printing kill us
            return "%s<format error: %s>%s" % (RED, exc, RESET)

    def _print_body(self, msg):
        try:
            body = message_to_yaml(msg, truncate_length=200).rstrip()
        except Exception as exc:
            print("  %s| <cannot render: %s>%s" % (RED, exc, RESET), flush=True)
            return
        lines = body.splitlines()
        if len(lines) > self._max_lines:
            hidden = len(lines) - self._max_lines
            lines = lines[: self._max_lines] + ["... <%d more lines>" % hidden]
        for line in lines:
            print("  %s|%s %s" % (DIM, RESET, line), flush=True)

    # --------------------------------------------------------------- stats

    def _print_stats(self):
        if not self._stats:
            self.get_logger().warn(
                "no matching topics on the graph yet. checks: is "
                "ros_tcp_endpoint running? is the headset connected? "
                "does `ros2 topic list` show anything?"
            )
            return

        if self._total == 0:
            self.get_logger().warn(
                "%d topic(s) advertised but zero messages received - publisher "
                "exists but is not sending, or QoS mismatch" % len(self._stats)
            )

        print("\n%s--- link summary  (%.0fs, %d msgs) ---%s"
              % (BOLD, time.monotonic() - self._t0, self._total, RESET), flush=True)
        print("%-24s%10s%9s%9s%10s%9s"
              % ("topic", "type", "count", "hz", "jitter", "age"), flush=True)
        for topic in sorted(self._stats):
            st = self._stats[topic]
            short = st.type_name.rsplit("/", 1)[-1][:10]
            if st.count == 0:
                print("%-24s%10s%9d%9s%10s   %ssilent%s"
                      % (topic, short, 0, "-", "-", YELLOW, RESET), flush=True)
            else:
                stale = st.age > 2.0
                c = YELLOW if stale else ""
                r = RESET if stale else ""
                print("%-24s%10s%9d%9.1f%9.1fm%s%8.1fs%s"
                      % (topic, short, st.count, st.hz, st.jitter_ms,
                         c, st.age, r), flush=True)
        print(flush=True)


def main(args=None):
    rclpy.init(args=args)
    node = LinkMonitor()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        print("\n%sstopped.%s" % (DIM, RESET), flush=True)
    finally:
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == "__main__":
    main()
