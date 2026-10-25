using UnityEngine;

/// <summary>
/// Which source drives the ship's pose for this session.
/// </summary>
public enum ShipSource
{
    /// <summary>
    /// Joystick integrated locally by GhostBot. No robot required - this is
    /// the mode every playtest so far has used.
    /// </summary>
    VirtualBot,

    /// <summary>
    /// Pose comes from an AprilTag tracked on the physical robot. The ship
    /// hovers above the real bot and follows it; the joystick drives the robot
    /// over ROS rather than moving the ship directly.
    /// </summary>
    AprilTag,

    /// <summary>
    /// Nothing but the ROS link: connect, and the stick drives the robot.
    ///
    /// No arena, no placement, no tags, no tornado. Exists because when the
    /// link failed at the demo there was no way to tell whether the fault was
    /// in ROS or in one of the six things layered on top of it - the joystick
    /// only reaches /cmd_vel after a floor is found, an arena is placed, a tag
    /// resolves and a phase advances. Any of those failing looks exactly like
    /// a dead link.
    /// </summary>
    LinkTest,

    /// <summary>
    /// Standalone test of on-headset vision: the passthrough camera feeds a
    /// detector, and every cup it sees is placed on the floor and tracked.
    ///
    /// No robot, no ROS, no AprilTags, no arena, no ship. It answers one
    /// question - can the headset find and follow cups on its own - without
    /// anything else in the scene able to make the answer look better or worse
    /// than it is.
    /// </summary>
    CupDetect,

    /// <summary>
    /// Point the right controller, pull the trigger, and read how far that
    /// point is from the headset - measured by the platform's own depth and
    /// plane ray casts (Android XR XR_ANDROID_raycast), not by our camera code.
    ///
    /// Standalone like Cup Detect: no robot, no ROS, no tags. It is the ground
    /// truth to check our camera-based cup positions against, and it shows how
    /// far out the headset's depth reaches in a given room.
    /// </summary>
    DistanceTest,

    /// <summary>
    /// Runs the cup model through Qualcomm SNPE on the CPU, the Hexagon NPU
    /// and the GPU inside the app process, and shows what worked and how fast.
    /// The NPU can only be judged from the app's own process - adb shell and
    /// run-as are refused by SELinux on this headset (tools/npu/).
    /// </summary>
    NpuTest,

    /// <summary>
    /// Shows exactly what the Bluetooth gamepad sends - device names, sticks,
    /// triggers, buttons - and the drive command that would result. Pair the
    /// pad and check it here before blaming the robot (DriveInput).
    /// </summary>
    ControllerTest,
}

/// <summary>
/// Session-wide mode selection, set once at startup before anything else runs.
///
/// Deliberately a plain static rather than a MonoBehaviour singleton: the mode
/// is chosen before the scene's gameplay objects are active, and every previous
/// attempt in this project to have systems find each other at runtime
/// (FindAnyObjectByType) picked the wrong object and cost a build cycle. A
/// static that is written once and read everywhere cannot pick wrong.
/// </summary>
public static class GameMode
{
    /// <summary>Defaults to VirtualBot so a build with no menu still runs.</summary>
    public static ShipSource Source { get; private set; } = ShipSource.VirtualBot;

    /// <summary>True once the player has actually chosen, as opposed to defaulting.</summary>
    public static bool Chosen { get; private set; }

    public static void Select(ShipSource source)
    {
        Source = source;
        Chosen = true;
        Debug.LogFormat("[Mode] {0} selected", source);
    }

    /// <summary>Convenience for the many places that only care about one branch.</summary>
    public static bool IsAprilTag => Source == ShipSource.AprilTag;

    public static bool IsLinkTest => Source == ShipSource.LinkTest;

    public static bool IsCupDetect => Source == ShipSource.CupDetect;

    public static bool IsDistanceTest => Source == ShipSource.DistanceTest;

    /// <summary>Experiences that never talk to the robot, so skip the IP/port screen.</summary>
    public static bool NeedsNoRobot(ShipSource s) =>
        s == ShipSource.CupDetect || s == ShipSource.DistanceTest || s == ShipSource.NpuTest
        || s == ShipSource.ControllerTest;

    public static bool IsNpuTest => Source == ShipSource.NpuTest;

    public static bool IsControllerTest => Source == ShipSource.ControllerTest;

    /// <summary>True when the arena, tags and tornado should all stand down.</summary>
    public static bool IsBareLink => Source == ShipSource.LinkTest;

    /// <summary>
    /// Reset for a fresh session. Called when the flow returns to the menu, so
    /// a second run in the same process does not inherit the first choice.
    /// </summary>
    public static void Reset()
    {
        Source = ShipSource.VirtualBot;
        Chosen = false;
    }
}
