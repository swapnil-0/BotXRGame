using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A virtual stand-in for the JetRover.
///
/// Point of this class: the whole game can be built and played before the real
/// robot exists. It consumes the same thumbstick input and produces the same
/// linear/angular velocity pair that RobotController publishes as a Twist, so
/// when the real chassis arrives you swap which object the game points at and
/// nothing else changes.
///
/// Deliberately NOT a Rigidbody. Differential-drive kinematics integrated by
/// hand matches how the real robot moves and how bot_sim models it; Unity
/// physics would introduce drift, bounce and slide that the real robot does
/// not have, and the two would stop agreeing.
/// </summary>
public class GhostBot : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("Same action RobotController uses. Vector2, thumbstick.")]
    public InputActionReference moveAction;

    [Header("Motion")]
    public float linearSpeed = 0.6f;      // m/s, matches bot_sim max_linear
    public float angularSpeed = 2.0f;     // rad/s, matches bot_sim max_angular
    [Tooltip("Stick magnitude below this is treated as zero.")]
    public float deadzone = 0.15f;

    [Header("Inertia")]
    // Without this the ship snaps to full velocity the instant the stick moves
    // and stops dead on release, which reads as twitchy regardless of how low
    // the top speed is. It also let the player counter the vortex instantly.
    [Tooltip("Seconds to reach full speed. 0 disables smoothing.")]
    public float accelerationTime = 0.4f;
    [Tooltip("Seconds to reach full turn rate.")]
    public float turnAccelerationTime = 0.25f;

    [Header("Play Area")]
    [Tooltip("Optional. If set, the bot cannot leave this rectangle.")]
    public Transform playAreaCenter;
    public Vector2 playAreaSize = new Vector2(2.44f, 2.44f);   // 8 ft x 8 ft

    [Header("External Forces")]
    [Tooltip("Tornadoes and anything else add world-space velocity here.")]
    public bool acceptExternalForces = true;

    // --- read by the HUD, the arm, and any ROS bridge -------------------
    /// <summary>Commanded forward velocity, m/s. Same value a Twist would carry.</summary>
    public float LinearX { get; private set; }
    /// <summary>Commanded yaw rate, rad/s. Positive = left, matching ROS.</summary>
    public float AngularZ { get; private set; }
    /// <summary>Set true by the arm during a swing to hold the chassis still.</summary>
    public bool MotionLocked { get; set; }
    /// <summary>World-space drift applied this frame, for HUD feedback.</summary>
    public Vector3 ExternalVelocity { get; private set; }

    private Vector3 externalAccum;
    private float linearVel, angularVel;   // SmoothDamp state

    // --- visual centre --------------------------------------------------
    // The ship model is an imported asset whose mesh pivot is nowhere near
    // its visual middle, and it is scaled down on top of that. Measured in
    // the headset the gap was 0.63 m - wider than a 3 ft arena. Anything that
    // asks "where is the ship" must therefore use Center, not
    // transform.position, or it will be testing a point in empty space.
    private bool centerCached;
    private Vector3 localCenter;

    /// <summary>
    /// World position of the middle of the ship's visible geometry.
    /// Falls back to transform.position if there is nothing to measure.
    /// </summary>
    public Vector3 Center
    {
        get
        {
            if (!centerCached) CacheCenter();
            return transform.TransformPoint(localCenter);
        }
    }

    /// <summary>Offset from the transform origin to the visual centre, world space.</summary>
    public Vector3 CenterOffset => Center - transform.position;

    /// <summary>
    /// Teleport so the ship's VISIBLE middle lands on <paramref name="worldPoint"/>.
    /// Use for respawns - setting transform.position directly puts the pivot
    /// there and leaves the ship itself off to one side.
    /// </summary>
    public void MoveCenterTo(Vector3 worldPoint)
    {
        transform.position = worldPoint - CenterOffset;
    }

    private void CacheCenter()
    {
        var rends = GetComponentsInChildren<Renderer>();
        if (rends == null || rends.Length == 0)
        {
            // Do NOT latch: renderers may simply not exist yet. Latching here
            // would freeze the offset at zero for the whole session.
            localCenter = Vector3.zero;
            return;
        }

        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

        localCenter = transform.InverseTransformPoint(b.center);
        centerCached = true;
    }

    void OnEnable()
    {
        if (moveAction != null && moveAction.action != null)
            moveAction.action.Enable();
    }

    /// <summary>Called by tornadoes each frame. World space, metres/second.</summary>
    public void AddExternalVelocity(Vector3 velocity)
    {
        if (acceptExternalForces) externalAccum += velocity;
    }

    /// <summary>
    /// Clear all motion state. Must be called after any teleport.
    ///
    /// SmoothDamp keeps its own velocity in linearVel/angularVel. Teleporting
    /// without clearing it means the ship resumes carrying whatever momentum it
    /// had before, and lurches away from wherever it was just placed.
    /// </summary>
    public void ResetMotion()
    {
        LinearX = 0f;
        AngularZ = 0f;
        linearVel = 0f;
        angularVel = 0f;
        externalAccum = Vector3.zero;
        ExternalVelocity = Vector3.zero;
    }

    void Update()
    {
        float dt = Time.deltaTime;

        Vector2 stick = Vector2.zero;
        if (moveAction != null && moveAction.action != null)
            stick = moveAction.action.ReadValue<Vector2>();
        if (stick.magnitude < deadzone) stick = Vector2.zero;

        // Same mapping RobotController uses, so the ghost and the real robot
        // respond identically to the same stick position.
        float targetLinear = stick.y * linearSpeed;
        float targetAngular = -stick.x * angularSpeed;

        if (MotionLocked)
        {
            targetLinear = 0f;
            targetAngular = 0f;
        }

        // Ramp toward the commanded velocity so the ship carries momentum.
        LinearX = accelerationTime > 0.001f
            ? Mathf.SmoothDamp(LinearX, targetLinear, ref linearVel, accelerationTime)
            : targetLinear;

        AngularZ = turnAccelerationTime > 0.001f
            ? Mathf.SmoothDamp(AngularZ, targetAngular, ref angularVel, turnAccelerationTime)
            : targetAngular;

        // Yaw first, then translate along the new heading - this is the same
        // integration order bot_sim uses, so the two stay in agreement.
        transform.Rotate(0f, -AngularZ * dt * Mathf.Rad2Deg, 0f);
        transform.Translate(0f, 0f, LinearX * dt, Space.Self);

        // External drift applies even while the arm has the wheels locked:
        // a tornado should still be able to shove you off your shot.
        ExternalVelocity = externalAccum;
        if (externalAccum != Vector3.zero)
        {
            transform.position += externalAccum * dt;
            externalAccum = Vector3.zero;
        }

        ClampToPlayArea();
    }

    private void ClampToPlayArea()
    {
        if (playAreaCenter == null) return;

        // Clamp the visible ship, not the pivot. Clamping the pivot let the
        // model sail well outside the rectangle while the origin sat neatly
        // on the boundary.
        Vector3 local = playAreaCenter.InverseTransformPoint(Center);
        float hx = playAreaSize.x * 0.5f;
        float hz = playAreaSize.y * 0.5f;

        bool clamped = false;
        if (Mathf.Abs(local.x) > hx) { local.x = Mathf.Sign(local.x) * hx; clamped = true; }
        if (Mathf.Abs(local.z) > hz) { local.z = Mathf.Sign(local.z) * hz; clamped = true; }

        if (clamped)
            MoveCenterTo(playAreaCenter.TransformPoint(local));
    }
}
