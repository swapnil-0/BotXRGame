using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Bare ROS link test: connect, push the stick, the robot moves.
///
/// Everything else stands down - no floor detection, no arena placement, no
/// tags, no tornado, no phases. Those exist in the game modes and each one sits
/// between the joystick and /cmd_vel, so when the link failed at the demo there
/// was no way to tell whether ROS was broken or whether a tag had simply not
/// resolved. This mode removes every layer that can silently withhold a
/// command.
///
/// It is a diagnostic, not a game. If the robot moves here and not in AprilTag
/// mode, the fault is in the game layer; if it does not move here either, the
/// fault is the link, and that is a completely different search.
/// </summary>
public class LinkTestMode : MonoBehaviour
{
    [Header("Wiring")]
    public RobotController robot;
    public ArmRosPublisher arm;
    public TMPro.TextMeshProUGUI display;

    [Tooltip("Stick, read directly. Deliberately not routed through anything " +
             "that could hold it back.")]
    public InputActionReference moveAction;

    [Header("Drive")]
    [Tooltip("Metres per second at full stick. Low on purpose - this is a " +
             "bench test, often with the robot on a table.")]
    public float linearSpeed = 0.15f;

    [Tooltip("Radians per second at full stick.")]
    public float angularSpeed = 0.8f;

    [Range(0f, 0.9f)] public float deadzone = 0.15f;

    [Header("Topic")]
    [Tooltip("Grip cycles through these. The robot's drive topic belongs to the " +
             "vendor stack and is not knowable from this repo - and a wrong " +
             "name fails identically to a dead link, because the headset " +
             "publishes happily into a topic nobody subscribes to.\n\n" +
             "Confirm the real one on the robot with: ros2 topic list")]
    public string[] candidateTopics =
    {
        "/cmd_vel",
        "/controller/cmd_vel",
        "/ros_robot_controller/cmd_vel",
        "/jetrover/cmd_vel",
    };

    public InputActionReference cycleTopicAction;   // grip
    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    private int topicIndex;
    private bool cycleWasPressed;

    [Header("Safety")]
    [Tooltip("Publish zero when the stick is centred rather than stopping.\n\n" +
             "A robot that keeps its last command when publishing stops is a " +
             "robot that drives off a table. Explicit zeros are cheap.")]
    public bool publishZeroWhenIdle = true;

    private float lastNonZeroTime = -1f;

    void Start()
    {
        if (!GameMode.IsLinkTest) { enabled = false; return; }

        if (robot == null) robot = FindAnyObjectByType<RobotController>();
        if (arm == null) arm = FindAnyObjectByType<ArmRosPublisher>();
        if (moveAction != null && moveAction.action != null) moveAction.action.Enable();
        if (cycleTopicAction != null && cycleTopicAction.action != null)
            cycleTopicAction.action.Enable();

        // Start on whichever topic the controller is already set to, so the
        // list reflects reality rather than resetting a working configuration.
        if (robot != null && candidateTopics != null)
            for (int i = 0; i < candidateTopics.Length; i++)
                if (candidateTopics[i] == robot.topicName) topicIndex = i;

        StandDownEverythingElse();
        HideOtherHudText();

        Debug.Log("[LinkTest] bare ROS link mode - arena, tags and tornado disabled");
    }

    /// <summary>
    /// Switch off every system that sits between the stick and /cmd_vel.
    ///
    /// Disabling components rather than checking a mode flag inside each one:
    /// the flag has to be remembered in every new system, and the one that
    /// forgets is the one that silently eats the command.
    /// </summary>
    private void StandDownEverythingElse()
    {
        // Re-activate the ship hierarchy first. RobotController sits on a child
        // of it, so anything that deactivated the ship also silenced the
        // publisher - and a publisher that never runs looks exactly like a
        // network fault.
        var placer = FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include);
        if (placer != null && placer.ship != null && !placer.ship.gameObject.activeSelf)
        {
            placer.ship.gameObject.SetActive(true);
            Debug.Log("[LinkTest] re-activated ship root so RobotController can publish");
        }

        if (robot != null && !robot.gameObject.activeInHierarchy)
        {
            Debug.LogWarning("[LinkTest] RobotController is on an inactive object - " +
                             "nothing will publish. Check the ship hierarchy.");
        }

        Disable(placer);
        Disable(FindAnyObjectByType<ArenaRun>());
        Disable(FindAnyObjectByType<BotCommandMixer>());
        Disable(FindAnyObjectByType<ShipTagFollower>());
        Disable(FindAnyObjectByType<BotTagMarker>());
        Disable(FindAnyObjectByType<CupTagMarkers>());
        Disable(FindAnyObjectByType<TagCupTracker>());
        Disable(FindAnyObjectByType<FloatingStartButton>());
        Disable(FindAnyObjectByType<TornadoTuner>());

        // Any external command left set by the mixer would override the stick
        // for the whole session, which is precisely the failure this mode
        // exists to rule out.
        if (robot != null) robot.ClearExternalCommand();

        foreach (var t in FindObjectsByType<Tornado>(FindObjectsInactive.Include))
            if (t != null) t.gameObject.SetActive(false);
    }

    private static void Disable(MonoBehaviour c)
    {
        if (c != null) c.enabled = false;
    }

    /// <summary>
    /// Blank the HUD's other lines.
    ///
    /// The arena placer's size prompt was still written into the HUD text and
    /// drew straight through this readout, so the two overlapped into
    /// unreadable soup - and the placer is disabled here, meaning that prompt
    /// was stale text about a step that will never happen.
    /// </summary>
    private void HideOtherHudText()
    {
        if (display == null) return;

        var parent = display.transform.parent != null
            ? display.transform.parent : display.transform;

        foreach (var t in parent.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
        {
            if (t == display) continue;
            t.text = "";
            t.enabled = false;
        }
    }

    void Update()
    {
        if (robot == null) return;

        Vector2 stick = (moveAction != null && moveAction.action != null)
            ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;

        if (stick.magnitude < deadzone) stick = Vector2.zero;
        else lastNonZeroTime = Time.time;

        HandleTopicCycle();

        // Straight to the controller's own fields, published at its publishRate.
        // No smoothing, no inertia: this is measuring the link, and anything
        // that shapes the value makes a wrong value harder to recognise.
        if (stick != Vector2.zero || publishZeroWhenIdle)
            robot.SetExternalCommand(stick.y * linearSpeed, -stick.x * angularSpeed);

        Render(stick);
    }

    private void HandleTopicCycle()
    {
        if (cycleTopicAction == null || cycleTopicAction.action == null) return;
        if (candidateTopics == null || candidateTopics.Length == 0) return;

        bool pressed = cycleTopicAction.action.ReadValue<float>() > pressThreshold;

        if (pressed && !cycleWasPressed)
        {
            topicIndex = (topicIndex + 1) % candidateTopics.Length;
            if (robot != null) robot.SetTopic(candidateTopics[topicIndex]);
        }
        cycleWasPressed = pressed;
    }

    private void Render(Vector2 stick)
    {
        if (display == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("LINK TEST - stick drives, A SWEEP, B KICK, grip = topic");

        // Connection first: everything below is meaningless if this is wrong,
        // and it is the single most common cause of "nothing happens".
        if (!string.IsNullOrEmpty(robot.PublishBlockedReason))
            sb.AppendFormat("*** {0} ***\n", robot.PublishBlockedReason);
        else
            sb.AppendFormat("{0}\n", robot.connectionStatus);

        sb.AppendFormat("{0}:{1}   {2}  [{3}/{4}]\n",
            robot.rosIP, robot.rosPort, robot.topicName,
            topicIndex + 1,
            candidateTopics != null ? candidateTopics.Length : 1);

        float age = robot.LastPublishTime >= 0f ? Time.time - robot.LastPublishTime : -1f;
        sb.AppendFormat("sent {0}   last {1:F3} / {2:F3}   {3}\n",
            robot.PublishCount,
            robot.LastPublishedLinear, robot.LastPublishedAngular,
            age < 0f ? "never" : string.Format("{0:F2}s ago", age));

        sb.AppendFormat("stick {0:F2},{1:F2}\n", stick.x, stick.y);

        if (arm != null)
        {
            sb.AppendFormat("arm  {0}\n", arm.Status);
            sb.AppendFormat("arm link {0}   sent {1}\n",
                arm.LinkDescription, arm.SwingsSent);
            sb.AppendFormat("last {0}\n", arm.LastCommand);
        }
        else
        {
            sb.AppendLine("no ArmRosPublisher");
        }

        display.text = sb.ToString();
    }
}
