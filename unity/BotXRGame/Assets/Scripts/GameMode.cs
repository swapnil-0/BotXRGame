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
