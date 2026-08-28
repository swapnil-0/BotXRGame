using UnityEngine;

/// <summary>
/// In AprilTag mode, drives the ship's pose from a tag tracked on the robot so
/// the ship hovers above the physical bot and moves with it.
///
/// Takes a plain Transform as its pose source rather than binding directly to a
/// tracking API. Two reasons: the tracking backend for this headset is not
/// settled (Android XR exposes both marker and QR trackable extensions, and AR
/// Foundation's tracked-image path is a third option), and a hard reference to
/// a package that may not be installed is a compile error rather than a
/// degraded feature. Wire tagTransform to whatever the tracker produces - or
/// during a bench test, to a cube you move by hand.
///
/// Does nothing at all in Virtual Bot mode, so the same scene serves both.
/// </summary>
public class ShipTagFollower : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Transform updated by the tag tracker. Assign the tracked object; " +
             "during a bench test, any transform you can move works.")]
    public Transform tagTransform;

    [Header("Target")]
    [Tooltip("Ship root to drive. Found from the placer's ship if left empty.")]
    public GhostBot ship;

    [Header("Placement")]
    [Tooltip("Metres above the tag. The ship should read as hovering over the " +
             "robot, not sitting on it.")]
    public float hoverHeight = 0.35f;

    [Tooltip("Seconds to catch up to the tag. Tag tracking is jittery frame to " +
             "frame; following it rigidly makes the ship vibrate. This trades a " +
             "little lag for a ship that looks attached rather than nervous.")]
    public float smoothTime = 0.12f;

    [Tooltip("Also copy the tag's yaw, so the ship faces the way the robot does.")]
    public bool followYaw = true;

    [Tooltip("Seconds without a tracking update before the ship is treated as " +
             "lost. Tags drop out constantly under motion blur, and freezing in " +
             "place reads far better than snapping to a stale pose.")]
    public float trackingTimeout = 0.5f;

    // --- status, read by the HUD ---------------------------------------
    public bool Tracking { get; private set; }
    public float SecondsSinceUpdate { get; private set; }
    public string Status { get; private set; } = "inactive";

    private Vector3 velocity;
    private Vector3 lastTagPosition;
    private float lastMoveTime;
    private bool everTracked;

    void Start()
    {
        if (!GameMode.IsAprilTag)
        {
            // Virtual Bot: GhostBot keeps full control of its own transform.
            Status = "inactive (Virtual Bot mode)";
            enabled = false;
            return;
        }

        if (ship == null) ship = CollectibleCup.Ship;

        if (ship == null)
        {
            Debug.LogWarning("[Tag] no ship registered yet; will retry.");
        }

        if (tagTransform == null)
        {
            Debug.LogWarning(
                "[Tag] AprilTag mode selected but tagTransform is not wired. " +
                "The ship will stay under joystick control so the session is " +
                "still usable, but it will NOT follow the robot.");
            Status = "NO TAG SOURCE";
        }
    }

    void LateUpdate()
    {
        // LateUpdate so the tracker has written the tag pose for this frame,
        // and after GhostBot's Update - whose integration we are overriding.
        if (ship == null)
        {
            ship = CollectibleCup.Ship;
            if (ship == null) return;
        }

        if (tagTransform == null)
        {
            // Leave PoseDrivenExternally false: without a tag the joystick is
            // the only thing that can move the ship, and a frozen ship in a
            // live demo is worse than a virtual one.
            ship.PoseDrivenExternally = false;
            Status = "NO TAG SOURCE - joystick fallback";
            return;
        }

        Vector3 tagPos = tagTransform.position;

        // Treat "the pose stopped changing" as tracking lost. A tracker that
        // drops out usually leaves its last pose in place rather than
        // reporting anything, so a stale transform looks identical to a
        // stationary robot except that it never updates at all.
        if ((tagPos - lastTagPosition).sqrMagnitude > 1e-8f)
        {
            lastTagPosition = tagPos;
            lastMoveTime = Time.time;
            everTracked = true;
        }

        SecondsSinceUpdate = Time.time - lastMoveTime;
        Tracking = everTracked && SecondsSinceUpdate < trackingTimeout;

        ship.PoseDrivenExternally = true;

        Vector3 target = tagPos + Vector3.up * hoverHeight;

        // MoveCenterTo, not transform.position. Writing the transform put the
        // ship's PIVOT over the tag, and the model sits forward of its pivot -
        // so the ship appeared in front of the marker rather than above it.
        // Everything else in this project positions the ship by its visible
        // centre; this was the last place still using the raw transform.
        Vector3 smoothed = Vector3.SmoothDamp(
            ship.Center, target, ref velocity, smoothTime);
        ship.MoveCenterTo(smoothed);

        if (followYaw)
        {
            Vector3 f = tagTransform.forward;
            f.y = 0f;
            if (f.sqrMagnitude > 1e-6f)
            {
                Quaternion want = Quaternion.LookRotation(f.normalized, Vector3.up);
                ship.transform.rotation = Quaternion.Slerp(
                    ship.transform.rotation, want,
                    1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(smoothTime, 1e-3f)));
            }
        }

        Status = Tracking
            ? string.Format("tracking  hover {0:F2} m", hoverHeight)
            : string.Format("LOST {0:F1}s - holding", SecondsSinceUpdate);
    }
}
