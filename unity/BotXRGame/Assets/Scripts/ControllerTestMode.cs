using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Controller Test experience: shows exactly what the Bluetooth gamepad sends,
/// and the drive command DriveInput would make of it. No robot, no ROS.
///
/// Android maps Bluetooth pads through generic layouts, and a pad that
/// reports its right stick or triggers on unexpected axes looks, from the
/// robot's side, exactly like a broken link. This answers "is it the pad?" in
/// ten seconds (CLAUDE.md rule 9: instrument rather than theorise).
///
/// Also lists every input device, so we can see whether the XR controllers
/// show up as gamepads too, and counts Android BACK (Input System: Escape) -
/// unhandled B presses can turn into BACK on Android.
///
/// Self-installing like NpuTestMode, so the only scene change is the menu row.
/// The installed object also logs gamepad connects/disconnects in every mode
/// (logcat tag Unity, "[Pad]").
/// </summary>
public class ControllerTestMode : MonoBehaviour, IExperienceRestart
{
    private bool active;
    private TMPro.TextMeshPro text;
    private int backCount;
    private string lastBackAt = "never";
    private readonly StringBuilder sb = new StringBuilder(2048);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindAnyObjectByType<ControllerTestMode>(FindObjectsInactive.Include) != null) return;
        new GameObject("ControllerTest").AddComponent<ControllerTestMode>();
    }

    void OnEnable()
    {
        InputSystem.onDeviceChange += OnDeviceChange;
        foreach (var pad in Gamepad.all) Debug.LogFormat("[Pad] present: {0}", Describe(pad));
    }

    void OnDisable() => InputSystem.onDeviceChange -= OnDeviceChange;

    private static void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (device is Gamepad)
            Debug.LogFormat("[Pad] {0}: {1}", change, Describe(device));
    }

    private static string Describe(InputDevice d) =>
        string.Format("'{0}' layout {1} id {2} ({3})", d.displayName, d.layout, d.deviceId,
                      string.IsNullOrEmpty(d.description.product) ? "?" : d.description.product);

    void Update()
    {
        if (!active)
        {
            if (GameMode.Chosen && GameMode.IsControllerTest) Activate();
            return;
        }

        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            backCount++;
            lastBackAt = Time.time.ToString("F1") + " s";
        }

        if (text != null) text.text = Render();
    }

    void LateUpdate()
    {
        if (!active || text == null || Camera.main == null) return;

        // Loosely follows the gaze, slightly below eye level - the pad is in
        // your hands, so you glance down at both.
        var head = Camera.main.transform;
        Vector3 target = head.position + head.forward * 1.3f + Vector3.down * 0.15f;
        text.transform.position = Vector3.Lerp(text.transform.position, target, 0.08f);
        text.transform.rotation = Quaternion.LookRotation(text.transform.position - head.position, Vector3.up);
    }

    private void Activate()
    {
        active = true;
        Debug.Log("[ControllerTest] experience active");
        ExperienceStandDown.Gameplay(FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include));
        ExperienceStandDown.HideHud();

        var go = new GameObject("ControllerTestText");
        go.transform.SetParent(transform, false);
        text = go.AddComponent<TMPro.TextMeshPro>();
        text.fontSize = 0.30f;
        text.alignment = TMPro.TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        text.color = new Color(0.88f, 1f, 0.85f);
        var rt = text.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = new Vector2(1.6f, 1.1f);
        if (Camera.main != null)
            go.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 1.3f;
    }

    private string Render()
    {
        sb.Clear();
        sb.AppendLine("CONTROLLER TEST - Bluetooth gamepad");
        sb.AppendLine();

        var pad = DriveInput.Pad;
        if (pad == null)
        {
            sb.AppendLine("<color=#ff8080>No gamepad connected.</color>");
            sb.AppendLine("Pair: mode switch to Bluetooth, hold pair ~3 s (LED blinks fast),");
            sb.AppendLine("then headset Settings > Connected devices > Pair new device.");
        }
        else
        {
            sb.AppendFormat("driving: <b>{0}</b>  ({1})\n", pad.displayName, pad.layout);
            Vector2 l = pad.leftStick.ReadValue(), r = pad.rightStick.ReadValue();
            sb.AppendFormat("L stick {0,5:F2} {1,5:F2}    R stick {2,5:F2} {3,5:F2}\n", l.x, l.y, r.x, r.y);
            sb.AppendFormat("LT {0:F2}  RT {1:F2}    dpad {2:F0},{3:F0}\n",
                pad.leftTrigger.ReadValue(), pad.rightTrigger.ReadValue(),
                pad.dpad.ReadValue().x, pad.dpad.ReadValue().y);
            sb.Append("pressed: ");
            int n = 0;
            foreach (var c in pad.allControls)
            {
                // Sticks and triggers are buttons too in the Input System;
                // list only true buttons so the line stays readable.
                if (c is ButtonControl b && !(c.parent is StickControl) && !(c.parent is DpadControl)
                    && c != pad.leftTrigger && c != pad.rightTrigger && b.isPressed)
                {
                    sb.Append(c.name).Append(' ');
                    n++;
                }
            }
            sb.AppendLine(n == 0 ? "-" : "");
            sb.AppendLine();

            // Same mapping and sign conventions as DriveInput's consumers:
            // ROS linear.y positive LEFT, angular.z positive CCW.
            Vector2 move = DriveInput.Move(null), turn = DriveInput.Turn(null);
            sb.AppendFormat("drive  fwd {0,5:F2}  strafe(L+) {1,5:F2}  yaw(CCW+) {2,5:F2}\n",
                move.y, -move.x, -turn.x);
            sb.AppendFormat("arm    SWEEP(A) {0}   KICK(B) {1}   START(X) {2}\n",
                DriveInput.Sweep(null) > 0.5f ? "<b>ON</b>" : "-",
                DriveInput.Kick(null) > 0.5f ? "<b>ON</b>" : "-",
                DriveInput.StartRun(null) > 0.5f ? "<b>ON</b>" : "-");
        }

        sb.AppendFormat("Android BACK seen: {0} (last {1})\n", backCount, lastBackAt);
        sb.AppendLine();

        sb.AppendLine("gamepads:");
        foreach (var g in Gamepad.all)
            sb.AppendFormat("  {0}{1}  upd {2:F1} s ago\n", g == pad ? "> " : "  ", Describe(g),
                            Time.realtimeSinceStartupAsDouble - g.lastUpdateTime);
        sb.AppendLine("all devices:");
        foreach (var d in InputSystem.devices)
            sb.AppendFormat("  {0} [{1}]\n", d.displayName, d.layout);

        return sb.ToString();
    }

    /// <summary>Experience bar Restart: reset the BACK counter.</summary>
    public void RestartExperience() { backCount = 0; lastBackAt = "never"; }
}
