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

    [Header("Input")]
    [Tooltip("Controller button that triggers a swing.")]
    public InputActionReference swingAction;

    [Range(0.1f, 0.9f)]
    public float pressThreshold = 0.5f;

    [Tooltip("Minimum seconds between swings. The robot arm takes over a second " +
             "to complete its arc, so a button held down would otherwise queue " +
             "commands faster than the hardware can act on them.")]
    public float cooldownSeconds = 1.5f;

    [Header("Local echo")]
    [Tooltip("Optional. Also plays the local arm animation so the headset shows " +
             "a swing even when no robot is listening.")]
    public ArmController localArm;

    [Header("Status, read by the HUD")]
    public string LastCommand { get; private set; } = "-";
    public int SwingsSent { get; private set; }
    public string Status { get; private set; } = "idle";

    private ROSConnection ros;
    private bool wasPressed;
    private float lastSendTime = -999f;
    private bool registered;

    void Start()
    {
        if (swingAction != null && swingAction.action != null)
            swingAction.action.Enable();

        TryRegister();
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

        if (swingAction == null || swingAction.action == null) return;

        bool pressed = swingAction.action.ReadValue<float>() > pressThreshold;

        // Rising edge only. Reading the level would fire every frame the button
        // is held, flooding the topic.
        if (pressed && !wasPressed) OnSwingPressed();
        wasPressed = pressed;
    }

    private void OnSwingPressed()
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
            Debug.LogWarning("[Arm] swing pressed but no ROS connection");
            return;
        }

        // Yaw the arm should aim at. Zero for now: the swing is straight ahead
        // of the robot, and aiming needs the cup-relative bearing which only
        // exists once cup detection is running.
        string payload = localArm != null
            ? localArm.BuildRosCommand(0f)
            : "{\"action\":\"SWING\",\"yaw\":0.000}";

        ros.Publish(topicName, new StringMsg(payload));

        SwingsSent++;
        LastCommand = payload;
        lastSendTime = Time.time;
        Status = string.Format("sent #{0}", SwingsSent);

        Debug.LogFormat("[Arm] -> {0}  {1}", topicName, payload);
    }

    /// <summary>Wire to a UI button for testing without the controller.</summary>
    public void SendSwingNow()
    {
        OnSwingPressed();
    }
}
