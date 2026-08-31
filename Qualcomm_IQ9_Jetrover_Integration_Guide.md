# Qualcomm IQ9 & Hiwonder Jetrover Integration Guide

### **1. Physical Operations & Host Setup**

**Charging the Robot**
- Connect the Hiwonder Charger barrel jack plug into the barrel jack charging port on the underside of the robot.

**Battery Care & Level Check**
- Query the battery level by echoing the topic (value returned is in mV):
  ```bash
  ros2 topic echo /ros_robot_controller/battery
  ```
- **Crucial Care**: Try to avoid letting the battery charge drop below **`10500` mV** (10.5V) to protect the battery cell health.

**Powering On**
- **Chassis/STM32**: Press the metallic button on the side of the robot.
- **Qualcomm IQ9 Board**: Power on the robot, then ensure the manual power switch on the IQ9 board is clicked to the **ON** position.

**Powering Off**
- Press and hold the metallic button on the side of the robot until the LED ring turns off. Switch the IQ9 board power switch to **OFF**.

**Connecting via SSH**
- Query the robot's IP address on the board if needed using:
  ```bash
  ip addr
  ```
- Connect from your workstation using the hostname or the IP address:
  ```bash
  ssh ubuntu@ur-iq9-3
  # OR
  ssh ubuntu@<robot_ip_address>
  ```
  * **Username**: `ubuntu`
  * **Hostname**: `ur-iq9-3`
  * **Password**: `dragonwing`

---

### **2. ROS 2 Workspace Configuration**

**Required Environment Sourcing**
You must source these setup files in **every new terminal session** before building or running commands:
```bash
source /opt/ros/jazzy/setup.bash
source ~/Documents/BotXRGame_ws/install/setup.bash
source ~/Documents/jetrover-from-github/jetrover/install/setup.bash
source ~/Documents/jetrover-bringup/ros2_ws/install/setup.bash
```

---

### **3. Execution Commands**

Run the following ROS 2 nodes in **exactly this order** (each in a separate, sourced terminal session):

1. **Start the low-level motor/servo controller**:
   ```bash
   ros2 run ros_robot_controller ros_robot_controller
   ```
2. **Start the chassis bring-up & base sensors**:
   ```bash
   ros2 run hiwonder jetrover
   ```
3. **Start the gesture arm teleop node**:
   ```bash
   ros2 run hiwonder gesture_arm_teleop
   ```
4. **Start the main headset communication bridge (Port 10000)**:
   ```bash
   ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=0.0.0.0 -p ROS_TCP_PORT:=10000
   ```

---

### **4. Driving the Base Without the Headset**

Drive the robot from the robot itself. This separates "the base does not move"
from "the headset is not reaching the base" - two faults that look identical
from inside the headset, and which cost us a demo when we could not tell them
apart.

Every terminal needs the sourcing from section 2 first:
```bash
source /opt/ros/jazzy/setup.bash
```

**Keyboard teleop** - the quickest proof the base drives at all:
```bash
ros2 run teleop_twist_keyboard teleop_twist_keyboard
```

**Which topic is the base actually listening on?** The headset publishes
`/cmd_vel`; if nothing subscribes to it, we are publishing into a void and
everything upstream looks healthy:
```bash
ros2 topic list
ros2 topic info /cmd_vel --verbose
ros2 topic info /arm_command --verbose
```

**One axis at a time.** The headset sends all three together, so a single wrong
sign is hard to spot in combination:
```bash
# forward
ros2 topic pub --once /cmd_vel geometry_msgs/msg/Twist \
  "{linear: {x: 0.1, y: 0.0, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.0}}"

# strafe - mecanum only. Positive linear.y should move LEFT.
ros2 topic pub --once /cmd_vel geometry_msgs/msg/Twist \
  "{linear: {x: 0.0, y: 0.1, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.0}}"

# spin - positive angular.z should turn COUNTER-clockwise
ros2 topic pub --once /cmd_vel geometry_msgs/msg/Twist \
  "{linear: {x: 0.0, y: 0.0, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.5}}"
```

Strafing the wrong way and not strafing at all need opposite fixes - a sign
flip in Unity versus a base that ignores `linear.y` - which is why they are
tested separately.

**Watch what the headset is sending**, with the endpoint running:
```bash
ros2 topic echo /cmd_vel
ros2 topic echo /arm_command
```
