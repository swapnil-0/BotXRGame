import json
import os
import sys
import socket
import threading
from enum import Enum
from dataclasses import dataclass
from pathlib import Path

try:
    import rclpy
    from rclpy.node import Node
    from sensor_msgs.msg import Joy
    from std_msgs.msg import String
    from ros_robot_controller_msgs.msg import BusServoState, SetBusServoState
    from ros_robot_controller_msgs.msg import GetBusServoCmd
    from ros_robot_controller_msgs.srv import GetBusServoState
except ImportError:
    rclpy = None
    Node = object
    Joy = None
    String = None
    BusServoState = None
    SetBusServoState = None
    GetBusServoCmd = None
    GetBusServoState = None

SERVO_ORDER = (1, 2, 3, 4, 5, 10)
DEFAULT_HOME = {
    1: 498,
    2: 185,
    3: 343,
    4: 330,
    5: 870,
    10: 543
}


class ArmState(Enum):
    READY = "READY"
    SWINGING = "SWINGING"
    RETURNING = "RETURNING"
    STOWED = "STOWED"


@dataclass(frozen=True)
class CalibrationLimits:
    home: int
    minimum: int
    maximum: int

    def clamp(self, value):
        return max(self.minimum, min(self.maximum, int(value)))


def load_calibration(path):
    path = Path(path).expanduser()
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise ValueError(f"unable to read calibration file {path}: {error}") from error
    except json.JSONDecodeError as error:
        raise ValueError(f"invalid calibration JSON in {path}: {error}") from error

    servos = data.get("servos")
    if not isinstance(servos, dict):
        raise ValueError("calibration file must contain a servos object")

    limits = {}
    for servo_id in SERVO_ORDER:
        record = servos.get(str(servo_id))
        if not isinstance(record, dict):
            raise ValueError(f"missing calibration for servo {servo_id}")
        try:
            home = int(record["home"])
            first_endpoint = int(record["min"])
            second_endpoint = int(record["max"])
        except (KeyError, TypeError, ValueError) as error:
            raise ValueError(f"invalid calibration for servo {servo_id}") from error

        numeric_min = min(first_endpoint, second_endpoint)
        numeric_max = max(first_endpoint, second_endpoint)
        if not 0 <= numeric_min <= numeric_max <= 1000:
            raise ValueError(f"calibration range out of bounds for servo {servo_id}")
        if not numeric_min <= home <= numeric_max:
            raise ValueError(f"home is outside calibration range for servo {servo_id}")
        limits[servo_id] = CalibrationLimits(home, numeric_min, numeric_max)
    return limits


class GestureArmTeleop(Node):
    def __init__(self):
        super().__init__("gesture_arm_teleop")

        # Configuration Parameters
        calibration_path = self.declare_parameter(
            "calibration_path", "~/.ros/jetrover_servo_calibration.json"
        ).value
        self.calibration_path = Path(os.path.expanduser(calibration_path))
        self.limits = load_calibration(self.calibration_path)

        self.home_persistence_path = self.declare_parameter(
            "home_persistence_path", "~/.ros/gesture_arm_home.json"
        ).value

        self.sweep_button = int(self.declare_parameter("sweep_button", 3).value)  # Default: X button
        self.kick_button = int(self.declare_parameter("kick_button", 4).value)    # Default: Y button
        self.set_home_button = int(self.declare_parameter("set_home_button", 10).value)  # Default: BACK/SELECT

        self.publish_rate = float(self.declare_parameter("publish_rate", 20.0).value)
        self.sweep_speed = float(self.declare_parameter("sweep_speed", 40.0).value)  # pulses per tick

        self.tcp_port = int(self.declare_parameter("tcp_port", 10001).value)
        self.joy_topic = self.declare_parameter("joy_topic", "/ros_robot_controller/joy").value

        # Initialize State and Home Positions
        self.home_positions = self.load_persistent_home()
        self.state = ArmState.READY
        self.last_buttons = []

        # ROS Communication Interfaces
        self.state_client = self.create_client(
            GetBusServoState, "/ros_robot_controller/bus_servo/get_state"
        )
        self.command_pub = self.create_publisher(
            SetBusServoState, "/ros_robot_controller/bus_servo/set_state", 10
        )
        self.state_pub = self.create_publisher(
            String, "/arm_state", 10
        )
        self.joy_sub = self.create_subscription(Joy, self.joy_topic, self._joy_callback, 10)

        # Timer-based Trajectory Player Variables
        self.active_trajectory = []
        self.current_step_idx = 0
        self.step_timer = None

        # Threaded TCP Server Setup
        self.server_running = True
        self.server_thread = threading.Thread(target=self._tcp_server_thread, daemon=True)
        self.server_thread.start()

        self.get_logger().info("Gesture Arm Teleop Node initialized successfully.")
        self.publish_arm_state()

    def load_persistent_home(self):
        path = Path(self.home_persistence_path).expanduser()
        if path.exists():
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                home_positions = {}
                for k, v in data.items():
                    home_positions[int(k)] = int(v)
                if all(s in home_positions for s in SERVO_ORDER):
                    self.get_logger().info(f"Loaded persistent home position from {path}")
                    return home_positions
                else:
                    self.get_logger().warn(f"Persistence file {path} incomplete. Falling back to defaults.")
            except Exception as e:
                self.get_logger().error(f"Failed to read persistent home from {path}: {e}")
        
        self.get_logger().info("Using default hardcoded home positions.")
        return dict(DEFAULT_HOME)

    def save_persistent_home(self, home_positions):
        path = Path(self.home_persistence_path).expanduser()
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            temp_path = path.with_suffix(".tmp")
            temp_path.write_text(json.dumps(home_positions, indent=4), encoding="utf-8")
            temp_path.rename(path)
            self.get_logger().info(f"Persistent home positions saved successfully to {path}")
        except Exception as e:
            self.get_logger().error(f"Failed to save persistent home positions to {path}: {e}")

    def publish_arm_state(self):
        msg = String()
        msg.data = self.state.value
        self.state_pub.publish(msg)

    # -------------------------------------------------- Duration calculator

    def calculate_duration(self, start_positions, target_positions):
        # Travel speed: pulses/tick * ticks/sec = pulses/second
        speed_pulses_per_sec = self.sweep_speed * self.publish_rate
        if speed_pulses_per_sec <= 0:
            return 1.0  # Safe fallback if speed is zero

        max_duration = 0.05  # minimum duration to avoid instant snaps
        for servo_id in SERVO_ORDER:
            start = start_positions.get(servo_id, 500)
            target = target_positions.get(servo_id, 500)
            distance = abs(target - start)
            duration = distance / speed_pulses_per_sec
            if duration > max_duration:
                max_duration = duration
        return max_duration

    # -------------------------------------------------- TCP Command Server

    def _tcp_server_thread(self):
        server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            server_socket.bind(('0.0.0.0', self.tcp_port))
            server_socket.listen(5)
            self.get_logger().info(f"Direct TCP control server listening on 0.0.0.0:{self.tcp_port}")
        except Exception as e:
            self.get_logger().error(f"TCP server failed to bind/listen on port {self.tcp_port}: {e}")
            return

        while rclpy.ok() and self.server_running:
            try:
                server_socket.settimeout(1.0)
                try:
                    client_sock, client_addr = server_socket.accept()
                except socket.timeout:
                    continue

                t = threading.Thread(target=self._handle_client, args=(client_sock, client_addr), daemon=True)
                t.start()
            except Exception as e:
                if self.server_running:
                    self.get_logger().error(f"TCP connection accept error: {e}")

    def _handle_client(self, client_sock, client_addr):
        client_sock.settimeout(5.0)
        buffer = ""
        try:
            while rclpy.ok() and self.server_running:
                data = client_sock.recv(1024)
                if not data:
                    break
                buffer += data.decode('utf-8')
                while "\n" in buffer:
                    line, buffer = buffer.split("\n", 1)
                    cmd = line.strip().upper()
                    if cmd:
                        self._trigger_action(cmd, source=f"TCP Client {client_addr}")
        except Exception as e:
            pass
        finally:
            # Handle any residual buffer if the client closed connection
            residual = buffer.strip().upper()
            if residual:
                self._trigger_action(residual, source=f"TCP Client {client_addr}")
            client_sock.close()

    # -------------------------------------------------- Gamepad joy trigger

    def _joy_callback(self, msg):
        buttons = list(msg.buttons)
        if not self.last_buttons:
            self.last_buttons = [0] * len(buttons)

        # Detect rising edge (button press)
        for idx in range(min(len(buttons), len(self.last_buttons))):
            if buttons[idx] and not self.last_buttons[idx]:
                if idx == self.sweep_button:
                    self._trigger_action("SWEEP", source="Gamepad Button (X)")
                elif idx == self.kick_button:
                    self._trigger_action("KICK", source="Gamepad Button (Y)")
                elif idx == self.set_home_button:
                    self._trigger_action("SET_HOME", source="Gamepad Button (BACK)")

        self.last_buttons = buttons

    # -------------------------------------------------- Action Router

    def _trigger_action(self, action_name, source="Unknown"):
        # Lockout Guard: if we are already playing a trajectory, ignore triggers!
        if self.state != ArmState.READY:
            print("\n" + "=" * 60)
            print(f"[LOCKOUT WARNING] Incoming '{action_name}' from {source} ignored.")
            print(f"Reason: Arm is currently busy in state: {self.state.value}")
            print("=" * 60 + "\n")
            return

        if action_name == "SWEEP":
            print("\n" + "=" * 60)
            print(f"[INPUT TRIGGER] Source: {source} | Command: SWEEP")
            print("[ACTION] Initiating atomic SWEEP sequence (Home -> Left -> Right -> Home)")
            print("=" * 60)
            self._start_sweep()

        elif action_name == "KICK":
            print("\n" + "=" * 60)
            print(f"[INPUT TRIGGER] Source: {source} | Command: KICK")
            print("[ACTION] Initiating atomic KICK sequence (Home -> Extend -> Home)")
            print("=" * 60)
            self._start_kick()

        elif action_name == "SET_HOME":
            print("\n" + "=" * 60)
            print(f"[INPUT TRIGGER] Source: {source} | Command: SET_HOME")
            print("[ACTION] Executing Dynamic Home Calibration...")
            print("=" * 60)
            self._execute_set_home()

        else:
            self.get_logger().warn(f"Unknown action command received: {action_name}")

    # -------------------------------------------------- Dynamic Calibration

    def _execute_set_home(self):
        # Service call to read all 6 current positions
        self.get_logger().info("Calling bus servo state service to read current position...")
        positions = {}
        success = True

        for servo_id in SERVO_ORDER:
            pos = self._read_position(servo_id)
            if pos is None:
                success = False
                break
            positions[servo_id] = pos

        if success:
            self.home_positions = positions
            self.save_persistent_home(positions)
            print("-" * 60)
            print("[SUCCESS] New Home positions calibrated and saved:")
            for servo_id in SERVO_ORDER:
                print(f"  Servo {servo_id:2d}: {positions[servo_id]:4d} pulses")
            print("-" * 60 + "\n")
        else:
            print("-" * 60)
            print("[ERROR] Failed to query current physical positions. Calibration aborted.")
            print("-" * 60 + "\n")

    def _read_position(self, servo_id):
        if not self.state_client.wait_for_service(timeout_sec=0.5):
            self.get_logger().error("Bus servo state service not available.")
            return None

        request = GetBusServoState.Request()
        command = GetBusServoCmd()
        command.id = servo_id
        command.get_position = 1
        request.cmd = [command]
        
        future = self.state_client.call_async(request)
        rclpy.spin_until_future_complete(self, future, timeout_sec=1.0)
        
        if not future.done() or future.result() is None:
            return None
        
        response = future.result()
        if not response.success or not response.state:
            return None
        
        position = response.state[0].position
        if not position:
            return None
        
        return int(position[-1])

    # -------------------------------------------------- Trajectory Definitions

    def _start_sweep(self):
        start_positions = dict(self.home_positions)

        # 1. Sweep limit 1: Servo 1 moves to absolute 50 position
        target_1 = dict(self.home_positions)
        target_1[1] = self.limits[1].clamp(50)
        dur_1 = self.calculate_duration(start_positions, target_1)

        # 2. Sweep limit 2: Servo 1 moves to absolute 950 position
        target_2 = dict(self.home_positions)
        target_2[1] = self.limits[1].clamp(950)
        dur_2 = self.calculate_duration(target_1, target_2)

        # 3. Final return to home position for Servo 1
        target_3 = dict(self.home_positions)
        target_3[1] = self.limits[1].clamp(self.home_positions[1])
        dur_3 = self.calculate_duration(target_2, target_3)

        self.active_trajectory = [
            (target_1, dur_1, ArmState.SWINGING),   # Move to 50
            (target_2, dur_2, ArmState.SWINGING),   # Sweep from 50 to 950
            (target_3, dur_3, ArmState.RETURNING)   # Return to home
        ]
        self._play_trajectory()

    def _start_kick(self):
        start_positions = dict(self.home_positions)

        # 1. Kick Outward: Servo 3 (Elbow) moves to absolute 500 position, others remain home
        target_1 = dict(self.home_positions)
        target_1[3] = self.limits[3].clamp(500)
        dur_1 = self.calculate_duration(start_positions, target_1)

        # 2. Return Home: All servos return to exact home positions
        target_2 = dict(self.home_positions)
        dur_2 = self.calculate_duration(target_1, target_2)

        self.active_trajectory = [
            (target_1, dur_1, ArmState.SWINGING),   # Extend elbow to 500
            (target_2, dur_2, ArmState.RETURNING)   # Return home
        ]
        self._play_trajectory()

    # -------------------------------------------------- Keyframe Trajectory Engine

    def _play_trajectory(self):
        self.current_step_idx = 0
        self._execute_next_keyframe()

    def _execute_next_keyframe(self):
        if self.current_step_idx >= len(self.active_trajectory):
            # Trajectory completed! Reset to READY and idle.
            self.state = ArmState.READY
            self.publish_arm_state()
            print("-" * 60)
            print("[STATUS] Trajectory execution completed successfully. Arm is now READY.")
            print("-" * 60 + "\n")
            return

        targets, duration, step_state = self.active_trajectory[self.current_step_idx]
        self.state = step_state
        self.publish_arm_state()

        # Publish multi-joint commands at once with duration
        self._publish_multi_servo_positions(targets, duration)

        # Print screen update of command sent to robot
        print("-" * 60)
        print(f"[ROBOT COMMAND] Step {self.current_step_idx + 1}/{len(self.active_trajectory)} ({self.state.value})")
        print(f"  Duration: {duration:.4f}s")
        print("  Servos Dispatched:")
        for servo_id in SERVO_ORDER:
            target_val = targets[servo_id]
            print(f"    Servo {servo_id:2d} -> Target Pulse: {target_val:4d}")
        print("-" * 60)

        self.current_step_idx += 1
        
        # Schedule next step timer
        if self.step_timer is not None:
            self.destroy_timer(self.step_timer)
        self.step_timer = self.create_timer(duration, self._execute_next_keyframe)

    def _publish_multi_servo_positions(self, targets, duration):
        message = SetBusServoState()
        message.state = []
        for servo_id, target in targets.items():
            state = BusServoState()
            state.present_id = [1, servo_id]
            state.position = [1, int(target)]
            message.state.append(state)
        # Set command travel duration
        message.duration = duration
        self.command_pub.publish(message)

    def destroy_node(self):
        self.server_running = False
        super().destroy_node()


def main(args=None):
    rclpy.init(args=args)
    node = None
    try:
        node = GestureArmTeleop()
        rclpy.spin(node)
    except (KeyboardInterrupt, RuntimeError, ValueError) as error:
        print(f"gesture_arm_teleop failed: {error}", file=sys.stderr)
    finally:
        if node is not None:
            node.destroy_node()
        rclpy.shutdown()


if __name__ == "__main__":
    main()
