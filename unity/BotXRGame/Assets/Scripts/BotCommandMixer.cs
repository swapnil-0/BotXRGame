using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Works out what to send the real robot, and in which phase.
///
/// Three phases, and the arrow on the tag shows the command in every one:
///
///   APPROACH  before the robot reaches the start line. Command points at the
///             start point; the stick is ignored, because mixing player input
///             with an automatic approach gives a robot that fights itself.
///   ARMED     it has arrived and is waiting for the player to press START.
///             Command is zero - the robot must hold still while the player
///             gets ready, not creep.
///   RUNNING   stick plus tornado pull.
///
/// The tornado acts on the ROBOT here, at the tag's position, using the same
/// Tornado maths the virtual ship feels. That is the whole point of the mixed
/// reality: the pull has to be real for the driver, not a visual effect.
/// </summary>
public class BotCommandMixer : MonoBehaviour
{
    public enum Phase { NoTag, Approach, Armed, Running }

    [Header("Sources")]
    public TagCupTracker tagTracker;
    public RobotController robot;
    public ArenaRun run;
    public ArenaPlacer placer;
    public InputActionReference moveAction;

    [Header("Approach")]
    [Tooltip("Metres from the start point that counts as arrived.")]
    public float arriveRadius = 0.15f;

    [Tooltip("Speed while driving itself to the start line, m/s.")]
    public float approachSpeed = 0.12f;

    [Tooltip("Degrees of heading error above which it turns in place. Turning " +
             "while driving traces an arc, which on a small arena can leave it.")]
    [Range(10f, 120f)]
    public float turnFirstAngle = 35f;

    [Header("Play")]
    [Tooltip("Metres per second at full stick.")]
    public float driveSpeed = 0.18f;

    [Tooltip("Radians per second at full stick.")]
    public float turnRate = 1.2f;

    [Tooltip("Scales tornado pull into the robot's command. 1 sends the pull " +
             "at full strength; the robot cannot be pushed sideways, so the " +
             "pull can only be expressed as forward and turn - some of it is " +
             "unavoidably lost.")]
    public float tornadoInfluence = 1f;

    [Range(0f, 0.9f)] public float stickDeadzone = 0.15f;

    [Header("Testing")]
    [Tooltip("Offer START during the approach as well as on arrival.\n\n" +
             "Without a robot nothing drives the tag to the start line, so the " +
             "phase never leaves APPROACH and everything after it - START, the " +
             "split arrows, the tornado pull - is unreachable. That made the " +
             "second half of the flow untestable without hardware, which is " +
             "most of the time.\n\n" +
             "Harmless with a robot present: it just means you may start early.")]
    public bool allowStartBeforeArrival = true;

    // --- read by the marker and the HUD ---------------------------------
    public Phase CurrentPhase { get; private set; } = Phase.NoTag;

    /// <summary>World-space direction and magnitude being commanded.</summary>
    public Vector3 CommandVector { get; private set; }

    public Vector3 StickVector { get; private set; }
    public Vector3 TornadoVector { get; private set; }

    /// <summary>
    /// Direction to the start line, kept live in every phase.
    ///
    /// Needed while ARMED, where the command is deliberately zero so the robot
    /// holds still - the marker still has to point somewhere, and "where you
    /// are about to start from" is the useful thing to show.
    /// </summary>
    public Vector3 ToStartDirection { get; private set; } = Vector3.forward;
    public float DistanceToStart { get; private set; }
    public string Status { get; private set; } = "no tag";

    private bool startPressed;

    void Start()
    {
        if (!GameMode.IsAprilTag) { enabled = false; return; }

        if (tagTracker == null) tagTracker = FindAnyObjectByType<TagCupTracker>();
        if (robot == null) robot = FindAnyObjectByType<RobotController>();
        if (run == null) run = FindAnyObjectByType<ArenaRun>();
        if (placer == null) placer = FindAnyObjectByType<ArenaPlacer>();

        if (moveAction != null && moveAction.action != null) moveAction.action.Enable();
    }

    /// <summary>Called by the floating START button.</summary>
    public void PressStart()
    {
        if (!AwaitingStart) return;
        startPressed = true;
        Debug.LogFormat("[Bot] START pressed in {0} - joystick live", CurrentPhase);
    }

    /// <summary>True while the START button should be visible.</summary>
    public bool AwaitingStart =>
        CurrentPhase == Phase.Armed ||
        (allowStartBeforeArrival && CurrentPhase == Phase.Approach);

    void Update()
    {
        if (robot == null || tagTracker == null) return;

        if (placer == null || !placer.IsPlaced)
        {
            SetIdle("waiting for arena");
            return;
        }

        Transform tag = tagTracker.BotTag;
        if (tag == null || !tagTracker.BotTracked)
        {
            // Stop the robot when the tag is lost rather than continuing on the
            // last command. A robot driving on a stale command it can no longer
            // correct is the one genuinely unsafe state here.
            SetIdle("bot tag not visible");
            CurrentPhase = Phase.NoTag;
            return;
        }

        Vector3 here = tag.position;
        Vector3 facing = Flat(tag.forward);

        Vector3 start = run != null ? run.StartPoint : here;
        Vector3 toStart = start - here;
        toStart.y = 0f;
        DistanceToStart = toStart.magnitude;

        if (toStart.sqrMagnitude > 1e-6f) ToStartDirection = toStart.normalized;

        if (!startPressed && DistanceToStart > arriveRadius)
        {
            DoApproach(facing, toStart);
            return;
        }

        if (!startPressed)
        {
            CurrentPhase = Phase.Armed;
            CommandVector = Vector3.zero;
            StickVector = Vector3.zero;
            TornadoVector = Vector3.zero;
            robot.SetExternalCommand(0f, 0f);
            Status = "at start - press START";
            return;
        }

        DoRunning(here, facing);
    }

    private void DoApproach(Vector3 facing, Vector3 toStart)
    {
        CurrentPhase = Phase.Approach;

        Vector3 dir = toStart.normalized;
        CommandVector = dir * approachSpeed;
        StickVector = Vector3.zero;
        TornadoVector = Vector3.zero;

        float angle = Vector3.SignedAngle(facing, dir, Vector3.up);
        float lin = Mathf.Abs(angle) > turnFirstAngle ? 0f : approachSpeed;
        float ang = Mathf.Clamp(angle / 45f, -1f, 1f) * turnRate;

        robot.SetExternalCommand(lin, -ang);

        Status = string.Format("approaching start  {0:F2} m  err {1:F0} deg",
            DistanceToStart, angle);
    }

    private void DoRunning(Vector3 here, Vector3 facing)
    {
        CurrentPhase = Phase.Running;

        Vector2 stick = (moveAction != null && moveAction.action != null)
            ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;

        if (stick.magnitude < stickDeadzone) stick = Vector2.zero;

        // Stick is robot-relative: forward is where the robot points, which is
        // how it drove before the tag existed. Keeping that avoids relearning
        // the controls between modes.
        Vector3 right = Vector3.Cross(Vector3.up, facing) * -1f;
        StickVector = (facing * stick.y + right * stick.x) * driveSpeed;

        TornadoVector = Tornado.TotalVelocityAt(here) * tornadoInfluence;

        CommandVector = StickVector + TornadoVector;

        // Project the desired world velocity onto what a differential drive can
        // actually do: forward along its own heading, plus a turn toward the
        // rest. The sideways component cannot be executed, so it becomes turn -
        // which is why a strong sideways pull reads as being spun.
        float forward = Vector3.Dot(CommandVector, facing);
        float lateral = Vector3.Dot(CommandVector, right);

        float lin = Mathf.Clamp(forward, -driveSpeed * 1.5f, driveSpeed * 1.5f);
        float ang = Mathf.Clamp(lateral / Mathf.Max(driveSpeed, 1e-3f), -1.5f, 1.5f) * turnRate;

        robot.SetExternalCommand(lin, -ang);

        Status = string.Format("running  stick {0:F2}  tornado {1:F2}",
            StickVector.magnitude, TornadoVector.magnitude);
    }

    private void SetIdle(string why)
    {
        CommandVector = Vector3.zero;
        StickVector = Vector3.zero;
        TornadoVector = Vector3.zero;
        if (robot != null) robot.SetExternalCommand(0f, 0f);
        Status = why;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
    }

    /// <summary>Send the robot back to the start line, e.g. after a capture.</summary>
    public void ResetToApproach()
    {
        startPressed = false;
        CurrentPhase = Phase.Approach;
    }
}
