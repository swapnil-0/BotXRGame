using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

/// <summary>
/// Publishes an arm swing to /arm_command when the controller button is pressed.
///
/// Reuses the link that already works: the same ROSConnection on port 10000 that
/// carries /cmd_vel, and the exact payload ArmController.BuildRosCommand
/// produces, which bot_sim already parses. Nothing new on the robot side has to
/// exist for this to be testable - run bot_sim and watch /arm_state change.
///
/// Message type is std_msgs/String carrying JSON, matching bot_sim's
/// create_subscription(String, "/arm_command", ...).
/// </summary>
public class ArmRosPublisher : MonoBehaviour
{
    [Header("ROS")]
    public string topicName = "/arm_command";

    [Tooltip("Own port for arm traffic, separate from /cmd_vel on 10000.\n\n" +
             "Needs a SECOND ros_tcp_endpoint on the robot listening here. The " +
             "isolation is real - a flood or crash on one link cannot affect " +
             "the other - but it is two processes to start and two connections " +
             "that can be independently down, so the HUD reports this one's " +
             "state separately.")]
    public int armPort = 10001;

    [Tooltip("Robot IP. Left empty, it copies whatever the main connection is " +
             "using, so the connect screen configures both links.")]
    public string armIP = "";

    [Tooltip("Fall back to the main connection on port 10000 if the arm link " +
             "cannot be established.\n\n" +
             "ON by default: a demo where the arm silently does nothing is " +
             "worse than one where it works over the wrong port. The HUD says " +
             "which link is carrying it.")]
    public bool fallBackToMainConnection = true;

    [Tooltip("Publish even in Virtual Bot mode. Useful for testing the link " +
             "against bot_sim before the robot is on the bench.")]
    public bool publishInVirtualBotMode = true;

    [Header("Input - both on the right controller")]
    [Tooltip("A button. Publishes the primary action string.")]
    public InputActionReference swingAction;

    [Tooltip("B button. Publishes the secondary action string.\n\n" +
             "Field is still named kickAction: renaming a serialized field " +
             "silently drops whatever is bound to it, and that class of bug has " +
             "already cost this project several build cycles. The name is " +
             "cosmetic; the action it sends is set below.")]
    public InputActionReference kickAction;

    [Range(0.1f, 0.9f)]
    public float pressThreshold = 0.5f;

    [Header("ROS action names")]
    [Tooltip("Payload action for the A button.")]
    public string swingActionName = "SWING";

    [Tooltip("Payload action for the B button.\n\n" +
             "STOW, not KICK: the robot side implements SWING and STOW, and " +
             "inventing a command it does not understand only produces warnings " +
             "in its log. Track what exists rather than what we wish existed.\n\n" +
             "STOW aborts a swing in progress and returns the arm to stowed, " +
             "which is a genuinely useful second button - it is the recovery " +
             "control when the arm is mid-stroke and about to hit something.")]
    public string kickActionName = "STOW";

    [Tooltip("Actions treated as an abort: no cooldown, no local swing " +
             "animation. Comma separated.")]
    public string abortActions = "STOW";

    [Tooltip("Minimum seconds between swings. The robot arm takes over a second " +
             "to complete its arc, so a button held down would otherwise queue " +
             "commands faster than the hardware can act on them.")]
    public float cooldownSeconds = 1.5f;

    [Header("Local echo")]
    [Tooltip("Optional. Also plays the local arm animation so the headset shows " +
             "a swing even when no robot is listening.")]
    public ArmController localArm;

    // --- status, read by the HUD -----------------------------------------
    // Plain comment, not [Header]: HeaderAttribute is AttributeTargets.Field,
    // so putting it on a property is a compile error (CS0592).
    public string LastCommand { get; private set; } = "-";
    public int SwingsSent { get; private set; }
    public string Status { get; private set; } = "idle";

    private ROSConnection ros;
    private bool swingWasPressed;
    private bool kickWasPressed;
    private float lastSendTime = -999f;
    private bool registered;

    void Start()
    {
        Enable(swingAction);
        Enable(kickAction);
        TryRegister();
    }

    private static void Enable(InputActionReference r)
    {
        if (r != null && r.action != null) r.action.Enable();
    }

    private static bool Pressed(InputActionReference r, float threshold)
    {
        if (r == null || r.action == null) return false;

        // A/B are digital buttons, but the same code has to cope with a
        // trigger bound here, so read as float and threshold. ReadValue<float>
        // on a button control returns 0 or 1, which thresholds correctly.
        return r.action.ReadValue<float>() > threshold;
    }

    private ROSConnection mainRos;
    private bool usingFallback;

    /// <summary>Which link the arm is actually publishing on, for the HUD.</summary>
    public string LinkDescription { get; private set; } = "not connected";

    private void TryRegister()
    {
        // Deferred and retried rather than done once in Start: the main
        // connection does not exist until the player has entered an IP and
        // pressed Connect, and the arm link copies its address from there.
        if (registered) return;

        mainRos = ROSConnection.GetOrCreateInstance();
        if (mainRos == null) return;

        string ip = !string.IsNullOrEmpty(armIP) ? armIP : mainRos.RosIPAddress;
        if (string.IsNullOrEmpty(ip)) return;      // no address yet; retry next frame

        if (ros == null) ros = CreateArmConnection(ip);

        if (ros == null)
        {
            if (!fallBackToMainConnection) return;

            ros = mainRos;
            usingFallback = true;
        }

        ros.RegisterPublisher<StringMsg>(topicName);
        registered = true;

        LinkDescription = usingFallback
            ? string.Format("{0}:{1} (FALLBACK - main link)", ip, mainRos.RosPort)
            : string.Format("{0}:{1}", ip, armPort);

        Status = "publisher on " + topicName + " via " + LinkDescription;
        Debug.LogFormat("[Arm] {0} -> {1}", topicName, LinkDescription);
    }

    /// <summary>
    /// Build a second ROSConnection for the arm.
    ///
    /// A second instance is safe: ROSConnection keeps its state per-object,
    /// with only its connection-error flag static - so an error on either link
    /// shows in the connector's own HUD for both, but publishing stays
    /// independent. GetOrCreateInstance is deliberately NOT used, since that
    /// returns the shared singleton already bound to port 10000.
    ///
    /// Built on an inactive object so ConnectOnStart cannot fire against the
    /// default port before the real one is set.
    /// </summary>
    private ROSConnection CreateArmConnection(string ip)
    {
        try
        {
            var go = new GameObject("ArmRosConnection");
            go.SetActive(false);

            var conn = go.AddComponent<ROSConnection>();
            conn.ConnectOnStart = false;
            conn.RosIPAddress = ip;
            conn.RosPort = armPort;
            conn.listenForTFMessages = false;   // arm link carries commands only

            go.SetActive(true);
            conn.Connect(ip, armPort);

            Debug.LogFormat("[Arm] opened arm link to {0}:{1}", ip, armPort);
            return conn;
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat(
                "[Arm] could not open arm link on port {0}: {1}", armPort, e.Message);
            return null;
        }
    }

    void Update()
    {
        if (!registered) TryRegister();

        bool swing = Pressed(swingAction, pressThreshold);
        bool kick = Pressed(kickAction, pressThreshold);

        // A and B double as the mode-select buttons, so the press that picks a
        // mode would otherwise also publish a SWING - firing a real arm command
        // at the robot before the run has even started. Swallow presses until a
        // mode is chosen, recording their state so the release does not then
        // register as a fresh edge.
        if (!GameMode.Chosen)
        {
            swingWasPressed = swing;
            kickWasPressed = kick;
            Status = "waiting for mode selection";
            return;
        }

        // Rising edge only. Reading the level would fire every frame the button
        // is held, flooding the topic.
        if (swing && !swingWasPressed) Send(swingActionName);
        swingWasPressed = swing;

        if (kick && !kickWasPressed) Send(kickActionName);
        kickWasPressed = kick;
    }

    private bool IsAbort(string actionName)
    {
        if (string.IsNullOrEmpty(abortActions)) return false;
        foreach (var part in abortActions.Split(','))
            if (part.Trim().Equals(actionName, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void Send(string actionName)
    {
        bool abort = IsAbort(actionName);

        // An abort must never be rate-limited. The cooldown exists because the
        // arm's arc outlasts a button press, but STOW is precisely what you
        // press DURING that arc - blocking it would disable the control exactly
        // when it is needed, with the arm mid-stroke and heading somewhere bad.
        if (!abort && Time.time - lastSendTime < cooldownSeconds)
        {
            Status = string.Format("cooldown {0:F1}s",
                cooldownSeconds - (Time.time - lastSendTime));
            return;
        }

        // Local animation for a swing only, and regardless of connection, so a
        // silent button is not indistinguishable from a broken one. An abort
        // plays no swing - it is the opposite of one.
        if (localArm != null && !abort) localArm.RequestSwing();

        if (!publishInVirtualBotMode && !GameMode.IsAprilTag)
        {
            Status = "local only (Virtual Bot)";
            lastSendTime = Time.time;
            return;
        }

        if (!registered || ros == null)
        {
            Status = "NOT CONNECTED - nothing sent";
            Debug.LogWarningFormat("[Arm] {0} pressed but no ROS connection", actionName);
            return;
        }

        // Built here rather than via ArmController.BuildRosCommand so the action
        // name is not hardcoded to SWING. Same shape, same parser on the robot.
        //
        // STOW carries no yaw: bot_sim reads only "action" for it, and sending
        // a meaningless field invites someone later to assume it means
        // something. Yaw stays 0 on a swing too - the stroke is straight ahead,
        // and aiming needs a cup-relative bearing that does not exist until cup
        // detection runs.
        string payload = abort
            ? string.Format("{{\"action\":\"{0}\"}}", actionName)
            : string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{{\"action\":\"{0}\",\"yaw\":{1:F3}}}", actionName, 0f);

        ros.Publish(topicName, new StringMsg(payload));

        SwingsSent++;
        LastCommand = payload;
        lastSendTime = Time.time;
        Status = string.Format("sent #{0} {1}", SwingsSent, actionName);

        Debug.LogFormat("[Arm] -> {0}  {1}", topicName, payload);
    }

    /// <summary>Wire to a UI button for testing without the controller.</summary>
    public void SendSwingNow() { Send(swingActionName); }

    /// <summary>Wire to a UI button for testing without the controller.</summary>
    public void SendKickNow() { Send(kickActionName); }
}
