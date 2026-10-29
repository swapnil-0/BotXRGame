using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Where the drive and arm inputs come from: a Bluetooth gamepad when one is
/// paired and connected, otherwise the XR controllers' actions as before.
///
/// The split is deliberate: the XR controllers keep the menu, trigger-place
/// and grip tuner; the gamepad owns the robot and the virtual ship. While a
/// pad is connected the XR sticks and A/B do NOT drive - two inputs fighting
/// over /cmd_vel would make it impossible to tell who moved the robot.
///
/// Standard twin-stick mecanum layout (same as ROS teleop_twist_joy and most
/// games), read by POSITION so Xbox/8BitDo/PlayStation pads all agree:
///   left stick       forward / strafe
///   right stick X    turn
///   South (A)        arm SWEEP
///   East  (B)        arm KICK
///   West  (X)        starts the run when ARMED (Start / Menu too)
///
/// Read straight from the Input System device rather than through serialized
/// InputActionReferences, so nothing in the scene needs rewiring (CLAUDE.md
/// rule 1) and an unwired or disabled action cannot make the pad look dead
/// (rule 4). Pad lost (out of range, battery) = this returns the XR values,
/// which are zero unless someone is touching the XR sticks, so the robot stops.
///
/// Works the same on any Android XR headset (Galaxy XR, XREAL Aura): a paired
/// gamepad is plain Android input, not part of OpenXR.
/// </summary>
public static class DriveInput
{
    private static Gamepad cached;
    private static int cachedFrame = -1;

    /// <summary>
    /// Keeps the pad off the menu. The EventSystem's UI module uses an action
    /// asset whose Navigate/Submit include gamepad bindings, so pad A would
    /// click whichever menu button had focus. The menu is driven by the XR
    /// ray (pointer events), which this does not touch.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void KeepPadOffTheMenu()
    {
        foreach (var es in Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            es.sendNavigationEvents = false;
    }

    /// <summary>The gamepad driving this frame, or null when none is connected.</summary>
    public static Gamepad Pad
    {
        get
        {
            if (cachedFrame == Time.frameCount) return cached;
            cachedFrame = Time.frameCount;

            // The most recently used pad wins, so a second pad (or a phantom
            // HID device) cannot silently take over from the one in your hands.
            cached = null;
            double latest = double.MinValue;
            foreach (var pad in Gamepad.all)
            {
                if (!pad.added || !pad.enabled) continue;
                if (pad.lastUpdateTime > latest) { latest = pad.lastUpdateTime; cached = pad; }
            }
            return cached;
        }
    }

    public static bool HasPad => Pad != null;

    public static string PadName => HasPad ? Pad.displayName : "none";

    /// <summary>Forward (y) and strafe (x). Pad: left stick. Else the XR move action.</summary>
    public static Vector2 Move(InputActionReference xr)
    {
        var pad = Pad;
        return pad != null ? pad.leftStick.ReadValue() : Read2(xr);
    }

    /// <summary>Turn on x (y unused). Pad: right stick. Else the XR turn action.</summary>
    public static Vector2 Turn(InputActionReference xr)
    {
        var pad = Pad;
        return pad != null ? pad.rightStick.ReadValue() : Read2(xr);
    }

    /// <summary>0..1. Pad: South (A). Else the XR swing action.</summary>
    public static float Sweep(InputActionReference xr)
    {
        var pad = Pad;
        return pad != null ? pad.buttonSouth.ReadValue() : Read1(xr);
    }

    /// <summary>0..1. Pad: East (B). Else the XR kick action.</summary>
    public static float Kick(InputActionReference xr)
    {
        var pad = Pad;
        return pad != null ? pad.buttonEast.ReadValue() : Read1(xr);
    }

    /// <summary>
    /// 0..1. Pad: West (X) or Start. Not B: B is KICK, so starting on B also
    /// fired the arm at the start line.
    /// </summary>
    public static float StartRun(InputActionReference xr)
    {
        var pad = Pad;
        return pad != null
            ? Mathf.Max(pad.buttonWest.ReadValue(), pad.startButton.ReadValue())
            : Read1(xr);
    }

    private static Vector2 Read2(InputActionReference r) =>
        r != null && r.action != null ? r.action.ReadValue<Vector2>() : Vector2.zero;

    private static float Read1(InputActionReference r) =>
        r != null && r.action != null ? r.action.ReadValue<float>() : 0f;
}
