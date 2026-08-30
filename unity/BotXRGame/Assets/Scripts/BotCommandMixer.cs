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

    [Tooltip("LEFT stick. X spins the robot in place; Y is ignored.\n\n" +
             "Mecanum separates translation from rotation, so one stick cannot " +
             "express both - the right stick's X is already strafe. Splitting " +
             "them across two hands is the standard mecanum layout and means " +
             "you can aim the arm while the tornado drags you sideways.")]
    public InputActionReference turnAction;

    [Tooltip("Radians per second at full LEFT stick deflection.")]
    public float manualTurnRate = 1.2f;

    [Tooltip("Also starts the run, as well as the floating START button.\n\n" +
             "ARMED sends zero by design, so START is the only way out of it. " +
             "Gating that solely on a world-space button means anything wrong " +
             "with the button - unwired, mispositioned, unlit shader - traps " +
             "the session with a robot that will not move. A button press " +
             "cannot be mispositioned.\n\n" +
             "A is free in ARMED: the arm is only useful once running.")]
    public InputActionReference startAction;

    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;
    private bool startWasPressed;

    [Header("Approach")]
    [Tooltip("Metres from the start point that counts as arrived.")]
    public float arriveRadius = 0.15f;

    [Tooltip("Speed while driving itself to the start line, m/s.")]
    public float approachSpeed = 0.12f;

    [Tooltip("Degrees of heading error above which it turns in place. Turning " +
             "while driving traces an arc, which on a small arena can leave it.")]
    [Range(10f, 120f)]
    public float turnFirstAngle = 35f;

    public enum DriveKind { Differential, Mecanum }

    [Header("Drive")]
    [Tooltip("Mecanum can strafe, so the tornado's sideways pull is executed as " +
             "lateral motion instead of being converted into spin.\n\n" +
             "That is a much closer match to the virtual ship, which is pushed " +
             "bodily by the vortex. On a differential base the same pull can " +
             "only become a turn, which feels like being spun rather than " +
             "dragged.")]
    public DriveKind driveKind = DriveKind.Mecanum;

    [Tooltip("Keep the robot's heading fixed while translating. With mecanum " +
             "the stick moves the base without turning it, so the arm keeps " +
             "pointing the same way while the tornado drags it sideways.")]
    public bool holdHeadingWhenMecanum = true;

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
        if (turnAction != null && turnAction.action != null) turnAction.action.Enable();
        if (startAction != null && startAction.action != null) startAction.action.Enable();
    }

    private void ReadStartButton()
    {
        if (startAction == null || startAction.action == null) return;

        bool pressed = startAction.action.ReadValue<float>() > pressThreshold;
        if (pressed && !startWasPressed && AwaitingStart) PressStart();
        startWasPressed = pressed;
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
        // Polled before every early return below, so A still works in the
        // states that bail out - those are exactly the states you need a way
        // out of.
        ReadStartButton();

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

        // Robot CENTRE and forward, not the tag's. The robot turns about its
        // centre, so an off-centre tag sweeps an arc during a spin-in-place -
        // using it directly would make the tornado pull vary with heading while
        // the robot stood still, which reads as the vortex flickering.
        Vector3 here = tagTracker.BotCentre;
        Vector3 facing = Flat(tagTracker.BotForward);

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

        float forward = Vector3.Dot(CommandVector, facing);
        float lateral = Vector3.Dot(CommandVector, right);

        float lin = Mathf.Clamp(forward, -driveSpeed * 1.5f, driveSpeed * 1.5f);

        if (driveKind == DriveKind.Mecanum)
        {
            // Both components executed as motion. This is the whole advantage
            // of mecanum here: a sideways tornado pull DRAGS the robot, the way
            // the vortex drags the virtual ship, rather than spinning it.
            float lat = Mathf.Clamp(lateral, -driveSpeed * 1.5f, driveSpeed * 1.5f);

            // Yaw is its own axis on the LEFT stick, not something derived from
            // the pull. The tornado must never rotate the robot on mecanum: if
            // it did, the heading would drift while you were being dragged and
            // the arm would end up pointing somewhere you did not choose.
            float ang = ManualTurn();
            if (Mathf.Abs(ang) < 1e-4f && !holdHeadingWhenMecanum)
                ang = -TurnFromLateral(lateral);

            // ROS convention: linear.y is positive to the LEFT, and 'right' is
            // built as the robot's right, so the sign flips here.
            robot.SetExternalCommand(lin, -lat, ang);
        }
        else
        {
            // Differential: the sideways component cannot be executed, so it
            // becomes turn - which is why a strong side pull reads as being
            // spun rather than shoved.
            //
            // The left stick still adds yaw here, so the control layout is the
            // same in both modes. Added rather than overriding: on a
            // differential base the tornado's only way to affect you IS the
            // turn, and letting the stick cancel it would remove the pull.
            float ang = -TurnFromLateral(lateral) + ManualTurn();
            robot.SetExternalCommand(lin, ang);
        }

        Status = string.Format("running  stick {0:F2}  tornado {1:F2}",
            StickVector.magnitude, TornadoVector.magnitude);
    }

    /// <summary>Left-stick X, live, for the HUD.</summary>
    public float TurnStick { get; private set; }

    /// <summary>
    /// Yaw rate from the left stick, in ROS convention: angular.z is positive
    /// COUNTER-clockwise, and pushing the stick right should turn right, so the
    /// sign inverts here.
    /// </summary>
    private float ManualTurn()
    {
        if (turnAction == null || turnAction.action == null) { TurnStick = 0f; return 0f; }

        float x = turnAction.action.ReadValue<Vector2>().x;
        if (Mathf.Abs(x) < stickDeadzone) x = 0f;

        TurnStick = x;
        return -x * manualTurnRate;
    }

    private float TurnFromLateral(float lateral)
    {
        return Mathf.Clamp(lateral / Mathf.Max(driveSpeed, 1e-3f), -1.5f, 1.5f) * turnRate;
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
