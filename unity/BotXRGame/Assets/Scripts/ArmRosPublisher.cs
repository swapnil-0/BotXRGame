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

    [Header("Transport")]
    [Tooltip("Publish the arm command as a ROS topic on the SAME connection as " +
             "/cmd_vel, port 10000.\n\n" +
             "This is the agreed arrangement: one link, two topics. The probe " +
             "confirmed the headset's ROS-TCP link works, so putting the arm on " +
             "it removes a second endpoint, a second connection to debug, and a " +
             "second thing that can be independently down.\n\n" +
             "The robot side must subscribe to /arm_command instead of reading " +
             "its own socket - see docs/ros-interface/arm-command-interface.md")]
    public bool useMainConnection = true;

    [Tooltip("Legacy: raw newline-terminated text to the node's own socket on " +
             "10001. Kept for the case where the robot has not moved to the " +
             "topic yet, since it is what gesture_arm_teleop.py currently reads.")]
    public bool useRawTcp = false;

    [Header("ROS action names")]
    [Tooltip("Payload action for the A button.\n\n" +
             "SWEEP, not SWING: the robot node's command vocabulary is SWEEP, " +
             "KICK and SET_HOME, and anything else is logged as 'Unknown action " +
             "command' and dropped.")]
    public string swingActionName = "SWEEP";

    [Tooltip("Payload action for the B button. KICK is the node's second " +
             "gesture: Home -> Extend -> Home.")]
    public string kickActionName = "KICK";

    [Tooltip("Actions treated as an abort: no cooldown, no local animation.\n\n" +
             "Empty now. The node has no abort - it LOCKS OUT every command " +
             "while a gesture is playing and logs the rejection, so there is " +
             "nothing that can interrupt a stroke and pretending otherwise " +
             "would just send commands that are discarded.")]
    public string abortActions = "";

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
    private RobotController driveRobot;
    private string openedIP = "";
    private RawTcpCommandClient tcp;

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

        // Wait for the player to actually press Connect. Registering earlier
        // copied the ROSConnection prefab's DEFAULT address, so the arm link
        // opened against 192.168.1.100 while the drive link used the address
        // just typed in - two links to two different machines, and only one of
        // them a real robot.
        if (driveRobot == null) driveRobot = FindAnyObjectByType<RobotController>();
        if (driveRobot != null && !driveRobot.connectionRequested)
        {
            Status = "waiting for CONNECT";
            return;
        }

        // Prefer the address the connect screen wrote onto RobotController.
        // That is the one the user typed; ROSConnection.RosIPAddress is only
        // updated when it connects, so reading it can lag by a frame or a
        // whole session.
        string ip = !string.IsNullOrEmpty(armIP)
            ? armIP
            : (driveRobot != null && !string.IsNullOrEmpty(driveRobot.rosIP)
                ? driveRobot.rosIP
                : mainRos.RosIPAddress);

        if (string.IsNullOrEmpty(ip)) return;      // no address yet; retry next frame

        // Rebuild if the address changed since the arm link was opened - the
        // player can reconnect to a different robot without restarting.
        if (ros != null && !usingFallback && openedIP != ip)
        {
            Debug.LogFormat("[Arm] address changed {0} -> {1}, reopening arm link",
                openedIP, ip);
            if (ros != mainRos && ros != null) Destroy(ros.gameObject);
            ros = null;
            registered = false;
        }

        // Raw TCP path: no ROS publisher at all, just a socket.
        if (useRawTcp)
        {
            if (tcp == null) tcp = new RawTcpCommandClient();

            if (openedIP != ip)
            {
                tcp.Connect(ip, armPort);
                openedIP = ip;
            }

            registered = true;
            LinkDescription = string.Format("{0}:{1} raw TCP", ip, armPort);
            Status = tcp.Status;
            return;
        }

        if (useMainConnection)
        {
            // Same connection /cmd_vel already uses. No second socket, no
            // second endpoint on the robot, and it inherits the link the probe
            // just proved works.
            ros = mainRos;
            usingFallback = false;
        }
        else
        {
            if (ros == null) ros = CreateArmConnection(ip);

            if (ros == null)
            {
                if (!fallBackToMainConnection) return;

                ros = mainRos;
                usingFallback = true;
            }
        }

        ros.RegisterPublisher<StringMsg>(topicName);
        registered = true;

        LinkDescription = useMainConnection
            ? string.Format("{0}:{1} (with /cmd_vel)", ip, mainRos.RosPort)
            : usingFallback
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

            openedIP = ip;
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

    void OnDestroy()
    {
        // Background thread must be stopped explicitly, or it survives play
        // mode and keeps a socket open against the robot.
        tcp?.Disconnect();
    }

    void Update()
    {
        if (!registered) TryRegister();

        // Keep the HUD honest about the socket's live state - the raw client
        // reconnects on its own, so a status captured at registration would go
        // stale the moment the robot was power-cycled.
        if (useRawTcp && tcp != null && registered)
            Status = string.Format("{0}  sent {1}", tcp.Status, tcp.SentCount);

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

        if (useRawTcp)
        {
            if (tcp == null || !tcp.Connected)
            {
                Status = "TCP not connected: " + (tcp != null ? tcp.Status : "no client");
                Debug.LogWarningFormat("[Arm] {0} pressed but arm socket is not connected",
                    actionName);
                return;
            }

            // Bare command word, newline added by the client. No JSON: his
            // reader uppercases the line and matches it directly.
            tcp.Send(actionName);

            SwingsSent++;
            LastCommand = actionName;
            lastSendTime = Time.time;
            Status = string.Format("sent #{0} {1}", SwingsSent, actionName);

            Debug.LogFormat("[Arm] -> {0} '{1}'", LinkDescription, actionName);
            return;
        }

        if (!registered || ros == null)
        {
            Status = "NOT CONNECTED - nothing sent";
            Debug.LogWarningFormat("[Arm] {0} pressed but no ROS connection", actionName);
            return;
        }

        // Bare command word, not JSON. The robot node matches the whole line
        // against SWEEP / KICK / SET_HOME, so a JSON wrapper would arrive as an
        // unknown action and be dropped. Same vocabulary over the topic as over
        // the socket, which keeps one thing to agree on rather than two.
        string payload = actionName;

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
