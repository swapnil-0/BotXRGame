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

    [Tooltip("Publish even in Virtual Bot mode. Useful for testing the link " +
             "against bot_sim before the robot is on the bench.")]
    public bool publishInVirtualBotMode = true;

    [Header("Input - both on the right controller")]
    [Tooltip("A button. Publishes the swing action string.")]
    public InputActionReference swingAction;

    [Tooltip("B button. Publishes the kick action string.")]
    public InputActionReference kickAction;

    [Range(0.1f, 0.9f)]
    public float pressThreshold = 0.5f;

    [Header("ROS action names")]
    [Tooltip("Payload action for the A button. bot_sim accepts SWING and STOW.")]
    public string swingActionName = "SWING";

    [Tooltip("Payload action for the B button.\n\n" +
             "bot_sim currently REJECTS this - it accepts only SWING and STOW " +
             "and logs 'unknown arm action' for anything else. The robot side " +
             "has to add it. Left as a field so it can be renamed to match " +
             "whatever the ROS dev implements without another Unity build.")]
    public string kickActionName = "KICK";

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

    private void TryRegister()
    {
        // Registration is deferred and retried rather than done once in Start:
        // the ROS connection does not exist until the player has entered an IP
        // and pressed Connect, which happens after this component wakes up.
        if (registered) return;

        ros = ROSConnection.GetOrCreateInstance();
        if (ros == null) return;

        ros.RegisterPublisher<StringMsg>(topicName);
        registered = true;
        Status = "publisher registered on " + topicName;
        Debug.LogFormat("[Arm] registered publisher {0}", topicName);
    }

    void Update()
    {
        if (!registered) TryRegister();

        // Rising edge only. Reading the level would fire every frame the button
        // is held, flooding the topic.
        bool swing = Pressed(swingAction, pressThreshold);
        if (swing && !swingWasPressed) Send(swingActionName);
        swingWasPressed = swing;

        bool kick = Pressed(kickAction, pressThreshold);
        if (kick && !kickWasPressed) Send(kickActionName);
        kickWasPressed = kick;
    }

    private void Send(string actionName)
    {
        if (Time.time - lastSendTime < cooldownSeconds)
        {
            Status = string.Format("cooldown {0:F1}s",
                cooldownSeconds - (Time.time - lastSendTime));
            return;
        }

        // Local animation regardless, so the headset gives feedback even with
        // no robot connected - a silent button is indistinguishable from a
        // broken one.
        if (localArm != null) localArm.RequestSwing();

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
        // Yaw stays 0: the stroke is straight ahead of the robot, and aiming
        // needs a cup-relative bearing that does not exist until cup detection
        // is running.
        string payload = string.Format(
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
