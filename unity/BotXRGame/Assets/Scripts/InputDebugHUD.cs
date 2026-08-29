using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Live controller state on the HUD: A, B, trigger and thumbstick.
///
/// Worth a permanent line rather than a one-off test. A button that does
/// nothing has two very different causes - the binding is dead, or the binding
/// is fine and the code behind it declined to act - and they are
/// indistinguishable from inside a headset. This separates them: if the digit
/// flips and nothing happens, the input layer is fine and the fault is
/// downstream.
/// </summary>
public class InputDebugHUD : MonoBehaviour
{
    [Header("Output")]
    public TMPro.TextMeshProUGUI text;

    [Header("Actions")]
    public InputActionReference swingAction;   // A
    public InputActionReference kickAction;    // B
    public InputActionReference placeAction;   // trigger
    public InputActionReference moveAction;    // thumbstick

    [Range(0.1f, 0.9f)]
    public float pressThreshold = 0.5f;

    [Tooltip("Also show what the arm publisher did with the last press, which " +
             "is the other half of 'I pressed it and nothing happened'.")]
    public ArmRosPublisher armPublisher;

    [Tooltip("Drive link telemetry. 'ROS did not work' has at least three " +
             "distinct causes that look identical from a headset: never " +
             "connected, connected but sending zeros, or connected and sending " +
             "values the robot ignores. This tells them apart.")]
    public RobotController robot;

    [Tooltip("Shows when the mixer has taken the stick away, which is a normal " +
             "state that looks exactly like a dead joystick.")]
    public BotCommandMixer mixer;

    void Start()
    {
        Enable(swingAction);
        Enable(kickAction);
        Enable(placeAction);
        Enable(moveAction);

        if (text == null)
            Debug.LogWarning("[InputHUD] no text assigned; nothing will be shown.");
    }

    private static void Enable(InputActionReference r)
    {
        if (r != null && r.action != null) r.action.Enable();
    }

    private int Digit(InputActionReference r)
    {
        if (r == null || r.action == null) return 0;
        return r.action.ReadValue<float>() > pressThreshold ? 1 : 0;
    }

    void Update()
    {
        if (text == null) return;

        Vector2 stick = (moveAction != null && moveAction.action != null)
            ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;

        string line = string.Format(
            "A:{0}  B:{1}  T:{2}   stick {3:F2},{4:F2}",
            Digit(swingAction), Digit(kickAction), Digit(placeAction),
            stick.x, stick.y);

        if (armPublisher != null)
        {
            // Link description as well as status: with the arm on its own port
            // there are now two ways to be "connected", and a demo where the
            // arm quietly ran over the fallback link would otherwise look
            // identical to one where it did not.
            line += "   arm: " + armPublisher.Status;
        }

        if (robot != null)
        {
            line += "\n";

            if (!string.IsNullOrEmpty(robot.PublishBlockedReason))
            {
                line += "ROS: " + robot.PublishBlockedReason;
            }
            else
            {
                // Count and age, not just "connected". A frozen count with a
                // healthy status is the failure that wasted the demo: the link
                // was up and nothing was going down it.
                float age = robot.LastPublishTime >= 0f
                    ? Time.time - robot.LastPublishTime : -1f;

                line += string.Format(
                    "ROS: {0}  sent {1}  last {2:F2}/{3:F2}  {4}",
                    robot.connectionStatus,
                    robot.PublishCount,
                    robot.LastPublishedLinear,
                    robot.LastPublishedAngular,
                    age < 0f ? "never" : string.Format("{0:F1}s ago", age));
            }

            if (mixer != null && GameMode.IsAprilTag)
                line += string.Format("   [{0} drives]", mixer.CurrentPhase);
        }

        text.text = line;
    }
}
