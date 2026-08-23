"""
Everything the XR app needs, with no robot attached.

    ros2 launch xr_link_test xr_sim.launch.py

Brings up:
  * ros_tcp_endpoint   - the Unity bridge, on 0.0.0.0:10000
  * bot_sim            - fake JetRover: odom, scan, tf, detections
  * link_monitor       - prints what the headset sends (optional)

Useful arguments:
    monitor:=false          quieter console
    occluded_deg:=0.0       full 360 lidar, no chassis blocking
    room_width:=8.0         bigger room
    port:=10000             bridge port
"""

from launch import LaunchDescription
from launch.actions import DeclareLaunchArgument
from launch.conditions import IfCondition
from launch.substitutions import LaunchConfiguration
from launch_ros.actions import Node


def generate_launch_description():
    port = LaunchConfiguration("port")
    monitor = LaunchConfiguration("monitor")
    occluded = LaunchConfiguration("occluded_deg")
    room_w = LaunchConfiguration("room_width")
    room_h = LaunchConfiguration("room_height")

    return LaunchDescription([
        DeclareLaunchArgument("port", default_value="10000"),
        DeclareLaunchArgument("monitor", default_value="true"),
        DeclareLaunchArgument("occluded_deg", default_value="120.0",
                              description="Wedge blocked by the chassis"),
        DeclareLaunchArgument("room_width", default_value="6.0"),
        DeclareLaunchArgument("room_height", default_value="4.0"),

        Node(
            package="ros_tcp_endpoint",
            executable="default_server_endpoint",
            name="UnityEndpoint",
            output="screen",
            # 0.0.0.0, never a specific address: binding to one interface
            # silently breaks the moment the board has both Ethernet and Wi-Fi.
            parameters=[{"ROS_IP": "0.0.0.0", "ROS_TCP_PORT": port}],
        ),

        Node(
            package="xr_link_test",
            executable="bot_sim",
            name="bot_sim",
            output="screen",
            parameters=[{
                "occluded_deg": occluded,
                "room_width": room_w,
                "room_height": room_h,
            }],
        ),

        Node(
            package="xr_link_test",
            executable="link_monitor",
            name="link_monitor",
            output="screen",
            emulate_tty=True,
            condition=IfCondition(monitor),
            parameters=[{"preset": "headset", "full_message": False}],
        ),
    ])
