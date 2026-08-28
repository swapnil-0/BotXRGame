using UnityEngine;

/// <summary>
/// Drives the real robot to the middle of the starting line before handing
/// control to the player.
///
/// The arena is placed by pointing at the floor, so the robot is wherever it
/// happened to be - usually not on the start line. Without this the run begins
/// from an arbitrary spot and the course means nothing.
///
/// Holds the joystick off until it arrives. Mixing player input with an
/// automatic approach produces a robot that fights itself, and "why is it not
/// going where I point it" has already cost this project several sessions.
/// </summary>
[RequireComponent(typeof(RobotController))]
public class BotStartupDrive : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Tag on the robot. Position and facing come from here.")]
    public Transform botTag;

    public ArenaRun run;
    public ArenaPlacer placer;

    [Header("Approach")]
    [Tooltip("Metres from the start point that counts as arrived.")]
    public float arriveRadius = 0.12f;

    [Tooltip("Forward speed while approaching, m/s. Deliberately slower than " +
             "play speed: this happens while the player is watching rather " +
             "than driving, and a robot crossing a room unattended should be " +
             "calm.")]
    public float approachSpeed = 0.12f;

    [Tooltip("Turn rate while approaching, rad/s.")]
    public float approachTurnRate = 0.8f;

    [Tooltip("Degrees of heading error above which it turns in place rather " +
             "than driving. Turning while driving traces an arc, which on a " +
             "small arena can leave the course entirely.")]
    [Range(10f, 120f)]
    public float turnFirstAngle = 35f;

    [Tooltip("Give up after this long and hand over anyway, so a tag that " +
             "cannot be tracked does not leave the player permanently unable " +
             "to drive.")]
    public float timeoutSeconds = 30f;

    // --- state, read by the marker and the HUD --------------------------
    public bool HasControl { get; private set; }
    public Vector3 TargetDirection { get; private set; } = Vector3.forward;
    public string Status { get; private set; } = "idle";
    public float DistanceToStart { get; private set; }

    private RobotController robot;
    private bool finished;
    private float startedAt = -1f;

    void Awake()
    {
        robot = GetComponent<RobotController>();
    }

    void Start()
    {
        if (!GameMode.IsAprilTag)
        {
            Status = "inactive (Virtual Bot)";
            enabled = false;
            return;
        }

        if (run == null) run = FindAnyObjectByType<ArenaRun>();
        if (placer == null) placer = FindAnyObjectByType<ArenaPlacer>();
    }

    void Update()
    {
        if (finished) return;

        if (placer == null || !placer.IsPlaced)
        {
            Status = "waiting for arena";
            HasControl = false;
            return;
        }

        if (botTag == null)
        {
            Status = "no bot tag";
            HasControl = false;
            return;
        }

        if (startedAt < 0f) startedAt = Time.time;

        Vector3 start = StartPoint();
        Vector3 here = botTag.position;

        Vector3 delta = start - here;
        delta.y = 0f;
        DistanceToStart = delta.magnitude;

        if (DistanceToStart <= arriveRadius)
        {
            Hand0ver("arrived - joystick live");
            return;
        }

        if (Time.time - startedAt > timeoutSeconds)
        {
            Hand0ver("timeout - joystick live anyway");
            return;
        }

        HasControl = true;
        TargetDirection = delta.normalized;

        Vector3 facing = botTag.forward;
        facing.y = 0f;
        if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
        facing.Normalize();

        float angle = Vector3.SignedAngle(facing, TargetDirection, Vector3.up);

        // Turn in place first when badly misaligned. Driving and turning at
        // once traces an arc, and on a 3 ft arena that arc can leave the course.
        float lin = Mathf.Abs(angle) > turnFirstAngle ? 0f : approachSpeed;
        float ang = Mathf.Clamp(angle / 45f, -1f, 1f) * approachTurnRate;

        // ROS convention: positive angular z turns left, and the controller
        // negates it downstream, so the sign is flipped here to match.
        robot.SetExternalCommand(lin, -ang);

        Status = string.Format("driving to start  {0:F2} m  yaw err {1:F0} deg",
            DistanceToStart, angle);
    }

    private void Hand0ver(string why)
    {
        finished = true;
        HasControl = false;
        robot.ClearExternalCommand();
        Status = why;
        Debug.Log("[Startup] " + why);
    }

    /// <summary>
    /// Midpoint of the near edge - the same point the virtual ship starts from,
    /// so both modes run the identical course.
    /// </summary>
    private Vector3 StartPoint()
    {
        if (run != null) return run.StartPoint;
        return placer != null ? placer.transform.position : Vector3.zero;
    }

    /// <summary>Wire to a button to re-run the approach, e.g. after a capture.</summary>
    public void Restart()
    {
        finished = false;
        startedAt = -1f;
    }
}
