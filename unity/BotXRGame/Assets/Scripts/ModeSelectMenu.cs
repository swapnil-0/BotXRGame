using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// First screen of the session: pick Virtual Bot or AprilTag, then hand over to
/// the existing ROS IP/port panel.
///
/// Flow is menu -> IP/port config -> placement -> run. This inserts itself
/// AHEAD of ROSIPConfig, which previously showed its panel in Start(); that call
/// is now deferred so the two panels cannot both appear at once.
///
/// Wiring (all optional - a missing reference degrades to a sensible default
/// rather than a null crash, because this runs first and a crash here means a
/// session with nothing on screen at all):
///   modePanel        - root of the two-button panel
///   virtualBotButton - selects ShipSource.VirtualBot
///   aprilTagButton   - selects ShipSource.AprilTag
///   ipConfig         - the ROSIPConfig to reveal afterwards
/// </summary>
public class ModeSelectMenu : MonoBehaviour
{
    [Header("Panel")]
    public GameObject modePanel;

    [Header("Buttons")]
    public Button virtualBotButton;
    public Button aprilTagButton;

    [Header("Optional labels")]
    public TMPro.TextMeshProUGUI titleText;
    public TMPro.TextMeshProUGUI helpText;

    [Header("Next step")]
    [Tooltip("Revealed once a mode is chosen. Its own panel stays hidden until then.")]
    public ROSIPConfig ipConfig;

    [Tooltip("Skip the ROS IP/port step in Virtual Bot mode. Virtual Bot needs " +
             "no robot, so the connect screen is pure friction during solo " +
             "playtesting - but leave it ON for a bot test day, where you may " +
             "want telemetry even while driving the virtual ship.")]
    public bool skipIpConfigForVirtualBot = false;

    void Start()
    {
        GameMode.Reset();

        if (modePanel != null) modePanel.SetActive(true);
        if (ipConfig != null) ipConfig.HideUntilModeChosen();

        if (titleText != null) titleText.text = "Select mode";
        if (helpText != null)
        {
            helpText.text =
                "Virtual Bot - fly the ship with the joystick, no robot needed\n" +
                "AprilTag - ship follows the tag on the real robot";
        }

        if (virtualBotButton != null)
            virtualBotButton.onClick.AddListener(() => Choose(ShipSource.VirtualBot));

        if (aprilTagButton != null)
            aprilTagButton.onClick.AddListener(() => Choose(ShipSource.AprilTag));

        if (virtualBotButton == null && aprilTagButton == null)
        {
            Debug.LogWarning("[Mode] no buttons wired; defaulting to VirtualBot " +
                             "and continuing so the session is not dead on arrival.");
            Choose(ShipSource.VirtualBot);
        }
    }

    public void Choose(ShipSource source)
    {
        GameMode.Select(source);

        if (modePanel != null) modePanel.SetActive(false);

        bool skip = source == ShipSource.VirtualBot && skipIpConfigForVirtualBot;

        if (ipConfig == null)
        {
            // Would otherwise hide the menu and show nothing at all - a black
            // screen with no way forward, which is the worst possible failure
            // on a test day.
            Debug.LogError("[Mode] ipConfig not wired. Menu hidden with no next " +
                           "screen. Assign ROSIPConfig on ModeSelectMenu.");
            if (modePanel != null) modePanel.SetActive(true);
            return;
        }

        if (skip) ipConfig.SkipStraightToHud();
        else ipConfig.ShowConfig();
    }

    /// <summary>Wire to a Back button if you add one later.</summary>
    public void ReturnToMenu()
    {
        GameMode.Reset();
        if (ipConfig != null) ipConfig.HideUntilModeChosen();
        if (modePanel != null) modePanel.SetActive(true);
    }
}
