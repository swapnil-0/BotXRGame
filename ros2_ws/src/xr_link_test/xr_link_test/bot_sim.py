#!/usr/bin/env python3
"""
bot_sim - a fake JetRover, so the XR app can be built without the robot.

Publishes everything the headset needs and consumes everything it sends, using
the exact topic names from docs/ros-interface/ros-unity-interface.md. When the
real chassis is ready, stop this node and start the real drivers: nothing in
Unity changes.

Subscribes
----------
/cmd_vel        geometry_msgs/Twist         drives the robot
/goal_pose      geometry_msgs/PoseStamped   drive-to-point (RViz "2D Goal Pose")

Publishes
---------
/odom                   nav_msgs/Odometry           pose + velocity
/scan                   sensor_msgs/LaserScan       raycast against a room model
/tf                     tf2_msgs/TFMessage          odom -> base_link -> laser
/plan                   nav_msgs/Path               straight-line path to goal
/detected_objects       geometry_msgs/PoseArray     fake red-cup detections
/detected_object_types  std_msgs/String             JSON list, index-matched
/bot_state              std_msgs/String             MANUAL | RETURNING | IDLE

Run
---
    ros2 run xr_link_test bot_sim
    python3 bot_sim.py --ros-args -p room_width:=8.0 -p occluded_deg:=120.0

The lidar models a 360-degree scanner with a wedge blocked by the chassis,
which is what the JetRover actually has. Blocked beams report +inf, exactly as
a real driver reports no-return, so the XR renderer must handle them - better
to find that out here than on hardware.
"""

import json
import math
import time

import rclpy
from rclpy.node import Node
from rclpy.qos import QoSProfile, QoSHistoryPolicy, QoSReliabilityPolicy

from geometry_msgs.msg import PoseArray, PoseStamped, Pose, Twist, TransformStamped
from nav_msgs.msg import Odometry, Path
from sensor_msgs.msg import LaserScan
from std_msgs.msg import String

try:
    from tf2_ros import TransformBroadcaster
    HAVE_TF2 = True
except ImportError:                                    # keep running without tf2
    HAVE_TF2 = False

WHEEL_SEPARATION = 0.30
WHEEL_RADIUS = 0.035


# ----------------------------------------------------------------- geometry

def ray_segment(ox, oy, dx, dy, x1, y1, x2, y2):
    """
    Distance from ray origin to its intersection with segment (x1,y1)-(x2,y2),
    or None. Ray direction (dx,dy) must be unit length.

    Solves  origin + t*dir = p1 + u*(p2-p1)  for t >= 0 and 0 <= u <= 1.
    """
    sx = x2 - x1
    sy = y2 - y1
    denom = dx * sy - dy * sx
    if abs(denom) < 1e-12:                             # parallel
        return None
    t = ((x1 - ox) * sy - (y1 - oy) * sx) / denom
    if t < 0.0:
        return None
    u = ((x1 - ox) * dy - (y1 - oy) * dx) / denom
    if u < 0.0 or u > 1.0:
        return None
    return t


def box_segments(cx, cy, w, h):
    """Four segments forming an axis-aligned box centred on (cx, cy)."""
    hw, hh = w / 2.0, h / 2.0
    a = (cx - hw, cy - hh)
    b = (cx + hw, cy - hh)
    c = (cx + hw, cy + hh)
    d = (cx - hw, cy + hh)
    return [(a[0], a[1], b[0], b[1]),
            (b[0], b[1], c[0], c[1]),
            (c[0], c[1], d[0], d[1]),
            (d[0], d[1], a[0], a[1])]


def yaw_to_quat(yaw):
    return 0.0, 0.0, math.sin(yaw * 0.5), math.cos(yaw * 0.5)


def wrap(a):
    return math.atan2(math.sin(a), math.cos(a))


# --------------------------------------------------------------------- node

class BotSim(Node):

    def __init__(self):
        super().__init__("bot_sim")

        p = self.declare_parameter
        p("rate", 30.0)                 # physics / odom rate
        p("scan_rate", 10.0)            # lidar rate
        p("room_width", 6.0)            # metres, X extent
        p("room_height", 4.0)           # metres, Y extent
        p("num_beams", 360)
        p("range_min", 0.12)
        p("range_max", 8.0)
        p("range_noise", 0.01)          # metres, std dev
        # Chassis blocks a wedge centred behind the robot. The JetRover's
        # scanner is a full 360 unit but the body occludes the rear.
        p("occluded_deg", 120.0)
        p("occluded_centre_deg", 180.0)
        p("max_linear", 0.6)            # clamp, m/s
        p("max_angular", 2.0)           # clamp, rad/s
        p("cmd_timeout", 1.0)           # stop if no /cmd_vel for this long
        p("goal_tolerance", 0.12)
        p("publish_tf", True)
        p("odom_frame", "odom")
        p("base_frame", "base_link")
        p("laser_frame", "laser")
        p("detect_range", 3.0)          # cups closer than this are "detected"

        g = lambda k: self.get_parameter(k).value
        self.rate = float(g("rate"))
        self.num_beams = int(g("num_beams"))
        self.range_min = float(g("range_min"))
        self.range_max = float(g("range_max"))
        self.range_noise = float(g("range_noise"))
        self.max_lin = float(g("max_linear"))
        self.max_ang = float(g("max_angular"))
        self.cmd_timeout = float(g("cmd_timeout"))
        self.goal_tol = float(g("goal_tolerance"))
        self.publish_tf = bool(g("publish_tf"))
        self.odom_frame = g("odom_frame")
        self.base_frame = g("base_frame")
        self.laser_frame = g("laser_frame")
        self.detect_range = float(g("detect_range"))

        half = math.radians(float(g("occluded_deg"))) / 2.0
        self.occ_centre = math.radians(float(g("occluded_centre_deg")))
        self.occ_half = half

        # --- world -------------------------------------------------------
        W, H = float(g("room_width")), float(g("room_height"))
        self.segments = box_segments(0.0, 0.0, W, H)          # outer walls
        self.segments += box_segments(1.6, 1.0, 0.5, 0.5)     # obstacle A
        self.segments += box_segments(-1.8, -0.9, 0.6, 0.4)   # obstacle B

        # Red solo cups scattered on the floor, in the odom frame.
        self.cups = [(1.2, -0.8, "star"), (-1.0, 1.2, "star"),
                     (2.2, 0.4, "pit"), (-2.0, -0.2, "star"),
                     (0.4, 1.5, "cyclone")]

        # --- state -------------------------------------------------------
        self.x = self.y = self.yaw = 0.0
        self.vx = self.wz = 0.0
        self.cmd_vx = self.cmd_wz = 0.0
        self.last_cmd = None
        self.goal = None
        self.state = "IDLE"
        self._last_step = time.monotonic()

        qos = QoSProfile(history=QoSHistoryPolicy.KEEP_LAST, depth=10,
                         reliability=QoSReliabilityPolicy.RELIABLE)

        self.pub_odom = self.create_publisher(Odometry, "/odom", qos)
        self.pub_scan = self.create_publisher(LaserScan, "/scan", qos)
        self.pub_path = self.create_publisher(Path, "/plan", qos)
        self.pub_objs = self.create_publisher(PoseArray, "/detected_objects", qos)
        self.pub_types = self.create_publisher(String, "/detected_object_types", qos)
        self.pub_state = self.create_publisher(String, "/bot_state", qos)

        self.create_subscription(Twist, "/cmd_vel", self._on_cmd, qos)
        self.create_subscription(PoseStamped, "/goal_pose", self._on_goal, qos)

        self.tf_bc = TransformBroadcaster(self) if (HAVE_TF2 and self.publish_tf) else None

        self.create_timer(1.0 / self.rate, self._step)
        self.create_timer(1.0 / float(g("scan_rate")), self._publish_scan)
        self.create_timer(0.5, self._publish_objects)

        self.get_logger().info(
            "bot_sim up. room %.1fx%.1f m, %d beams, %.0f deg occluded at %.0f deg. "
            "drive with /cmd_vel or send /goal_pose."
            % (W, H, self.num_beams, float(g("occluded_deg")),
               float(g("occluded_centre_deg")))
        )

    # ---------------------------------------------------------- callbacks

    def _on_cmd(self, msg):
        self.cmd_vx = max(-self.max_lin, min(self.max_lin, msg.linear.x))
        self.cmd_wz = max(-self.max_ang, min(self.max_ang, msg.angular.z))
        self.last_cmd = time.monotonic()
        self.goal = None                      # manual input overrides autonomy
        self.state = "MANUAL"

    def _on_goal(self, msg):
        self.goal = (msg.pose.position.x, msg.pose.position.y)
        self.state = "RETURNING"
        self.get_logger().info("goal: (%.2f, %.2f)" % self.goal)
        self._publish_plan()

    # ------------------------------------------------------------ physics

    def _step(self):
        now = time.monotonic()
        dt = now - self._last_step
        self._last_step = now
        if dt <= 0.0 or dt > 0.5:             # ignore hitches and resumes
            return

        if self.goal is not None:
            self._drive_to_goal()
        else:
            stale = (self.last_cmd is None) or (now - self.last_cmd > self.cmd_timeout)
            if stale:
                self.vx = self.wz = 0.0
                if self.state == "MANUAL":
                    self.state = "IDLE"
            else:
                self.vx, self.wz = self.cmd_vx, self.cmd_wz

        # differential-drive integration
        self.yaw = wrap(self.yaw + self.wz * dt)
        self.x += self.vx * math.cos(self.yaw) * dt
        self.y += self.vx * math.sin(self.yaw) * dt

        self._publish_odom()
        self._publish_tf()

    def _drive_to_goal(self):
        gx, gy = self.goal
        dx, dy = gx - self.x, gy - self.y
        dist = math.hypot(dx, dy)

        if dist < self.goal_tol:
            self.vx = self.wz = 0.0
            self.goal = None
            self.state = "IDLE"
            self.get_logger().info("goal reached")
            return

        heading_err = wrap(math.atan2(dy, dx) - self.yaw)
        self.wz = max(-self.max_ang, min(self.max_ang, 2.0 * heading_err))
        # Only drive forward once roughly pointed the right way, so the robot
        # turns on the spot instead of arcing wildly toward a goal behind it.
        self.vx = min(self.max_lin, 0.8 * dist) if abs(heading_err) < 0.6 else 0.0

    # ----------------------------------------------------------- outputs

    def _stamp(self):
        return self.get_clock().now().to_msg()

    def _publish_odom(self):
        qx, qy, qz, qw = yaw_to_quat(self.yaw)
        m = Odometry()
        m.header.stamp = self._stamp()
        m.header.frame_id = self.odom_frame
        m.child_frame_id = self.base_frame
        m.pose.pose.position.x = self.x
        m.pose.pose.position.y = self.y
        m.pose.pose.orientation.x = qx
        m.pose.pose.orientation.y = qy
        m.pose.pose.orientation.z = qz
        m.pose.pose.orientation.w = qw
        m.twist.twist.linear.x = self.vx
        m.twist.twist.angular.z = self.wz
        self.pub_odom.publish(m)

        s = String()
        s.data = self.state
        self.pub_state.publish(s)

    def _publish_tf(self):
        if self.tf_bc is None:
            return
        qx, qy, qz, qw = yaw_to_quat(self.yaw)
        t = TransformStamped()
        t.header.stamp = self._stamp()
        t.header.frame_id = self.odom_frame
        t.child_frame_id = self.base_frame
        t.transform.translation.x = self.x
        t.transform.translation.y = self.y
        t.transform.rotation.x = qx
        t.transform.rotation.y = qy
        t.transform.rotation.z = qz
        t.transform.rotation.w = qw

        l = TransformStamped()
        l.header.stamp = t.header.stamp
        l.header.frame_id = self.base_frame
        l.child_frame_id = self.laser_frame
        l.transform.translation.z = 0.12
        l.transform.rotation.w = 1.0

        self.tf_bc.sendTransform([t, l])

    def raycast(self, angle_body):
        """Range along a beam at `angle_body` radians in the robot frame."""
        rel = wrap(angle_body - self.occ_centre)
        if abs(rel) <= self.occ_half:                 # blocked by the chassis
            return float("inf")

        a = self.yaw + angle_body
        dx, dy = math.cos(a), math.sin(a)
        best = None
        for (x1, y1, x2, y2) in self.segments:
            t = ray_segment(self.x, self.y, dx, dy, x1, y1, x2, y2)
            if t is not None and (best is None or t < best):
                best = t
        if best is None or best > self.range_max:
            return float("inf")
        return best if best >= self.range_min else float("inf")

    def _publish_scan(self):
        n = self.num_beams
        inc = 2.0 * math.pi / n

        m = LaserScan()
        m.header.stamp = self._stamp()
        m.header.frame_id = self.laser_frame
        m.angle_min = -math.pi
        m.angle_max = math.pi - inc
        m.angle_increment = inc
        m.time_increment = 0.0
        m.scan_time = 1.0 / max(self.rate, 1.0)
        m.range_min = self.range_min
        m.range_max = self.range_max

        ranges = []
        for i in range(n):
            ang = -math.pi + i * inc
            r = self.raycast(ang)
            if r != float("inf") and self.range_noise > 0.0:
                # deterministic-ish jitter; avoids importing random per beam
                r += self.range_noise * math.sin(i * 12.9898 + self.x * 78.233)
            ranges.append(float(r))
        m.ranges = ranges
        self.pub_scan.publish(m)

    def _publish_objects(self):
        arr = PoseArray()
        arr.header.stamp = self._stamp()
        arr.header.frame_id = self.odom_frame
        kinds = []
        for (cx, cy, kind) in self.cups:
            if math.hypot(cx - self.x, cy - self.y) > self.detect_range:
                continue                              # out of camera range
            pose = Pose()
            pose.position.x = cx
            pose.position.y = cy
            pose.orientation.w = 1.0
            arr.poses.append(pose)
            kinds.append(kind)
        self.pub_objs.publish(arr)

        s = String()
        s.data = json.dumps(kinds)
        self.pub_types.publish(s)

    def _publish_plan(self):
        if self.goal is None:
            return
        path = Path()
        path.header.stamp = self._stamp()
        path.header.frame_id = self.odom_frame
        gx, gy = self.goal
        steps = 20
        for i in range(steps + 1):
            f = i / float(steps)
            ps = PoseStamped()
            ps.header.stamp = path.header.stamp
            ps.header.frame_id = self.odom_frame
            ps.pose.position.x = self.x + (gx - self.x) * f
            ps.pose.position.y = self.y + (gy - self.y) * f
            ps.pose.orientation.w = 1.0
            path.poses.append(ps)
        self.pub_path.publish(path)


def main(args=None):
    rclpy.init(args=args)
    node = BotSim()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        pass
    finally:
        node.destroy_node()
        if rclpy.ok():
            rclpy.shutdown()


if __name__ == "__main__":
    main()
