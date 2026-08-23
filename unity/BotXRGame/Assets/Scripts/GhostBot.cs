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

    void Update()
    {
        float dt = Time.deltaTime;

        Vector2 stick = Vector2.zero;
        if (moveAction != null && moveAction.action != null)
            stick = moveAction.action.ReadValue<Vector2>();
        if (stick.magnitude < deadzone) stick = Vector2.zero;

        // Same mapping RobotController uses, so the ghost and the real robot
        // respond identically to the same stick position.
        LinearX = stick.y * linearSpeed;
        AngularZ = -stick.x * angularSpeed;

        if (MotionLocked)
        {
            LinearX = 0f;
            AngularZ = 0f;
        }

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

        Vector3 local = playAreaCenter.InverseTransformPoint(transform.position);
        float hx = playAreaSize.x * 0.5f;
        float hz = playAreaSize.y * 0.5f;

        bool clamped = false;
        if (Mathf.Abs(local.x) > hx) { local.x = Mathf.Sign(local.x) * hx; clamped = true; }
        if (Mathf.Abs(local.z) > hz) { local.z = Mathf.Sign(local.z) * hz; clamped = true; }

        if (clamped)
            transform.position = playAreaCenter.TransformPoint(local);
    }
}
