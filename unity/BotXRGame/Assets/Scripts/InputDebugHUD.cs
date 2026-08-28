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
            line += "   arm: " + armPublisher.Status;

        text.text = line;
    }
}
