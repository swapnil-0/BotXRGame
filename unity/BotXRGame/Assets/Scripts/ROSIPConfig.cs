using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Robotics.ROSTCPConnector;

/// <summary>
/// Runtime connection settings screen. Lets the user point the app at any ROS
/// host and port from inside the headset, with no rebuild.
///
/// The port field is optional: if portInputField is not wired up in the
/// Inspector, the port falls back to RobotController.rosPort and everything
/// behaves exactly as before. That keeps existing scenes working.
/// </summary>
public class ROSIPConfig : MonoBehaviour
{
    [Header("UI Panels")]
    public GameObject ipInputPanel;
    public GameObject hudPanel;

    [Header("Connection Input")]
    public TMP_InputField ipInputField;
    [Tooltip("Optional. Leave unassigned to keep using RobotController.rosPort.")]
    public TMP_InputField portInputField;
    public TextMeshProUGUI ipStatusText;

    [Header("References")]
    public RobotController robotController;

    private ROSConnection ros;

    [Header("Recent addresses")]
    [Tooltip("Button that cycles through recently used IPs into the field.\n\n" +
             "A cycle button rather than a dropdown: typing an IP on an XR " +
             "keyboard is slow and error-prone, and a wrong digit produces the " +
             "same silent failure as an unreachable robot - which has already " +
             "cost a test session.")]
    public UnityEngine.UI.Button recentButton;

    [Tooltip("Optional label showing which recent entry is selected.")]
    public TextMeshProUGUI recentLabel;

    [Tooltip("How many addresses to remember.")]
    public int recentCount = 5;

    private const string PREF_RECENT = "ROS_IP_RECENT";
    private System.Collections.Generic.List<string> recent =
        new System.Collections.Generic.List<string>();
    private int recentIndex = -1;

    /// <summary>
    /// Most-recent-first list of addresses used successfully.
    /// Stored newline separated in one pref, which is enough for five entries
    /// and avoids inventing a key per slot.
    /// </summary>
    [Tooltip("Addresses available before anything has been connected to.\n\n" +
             "Seeded with the two machines actually in use, so the first run " +
             "after an install does not require typing an IP on an XR keyboard " +
             "to find out whether the link works.")]
    public string[] seedAddresses =
    {
        // wlan0 on ur-xr-robotics-rubikpi-1, the interface actually carrying
        // traffic. Its wired interface (192.168.1.204) reported RX 0 / TX 0,
        // so it is up but unused - connecting there would time out exactly
        // like an unreachable host.
        "192.168.1.245",
        "192.168.1.204",
        // Previously typed by hand and known not to reach this Pi. Kept last so
        // they are reachable in the cycle but never the first suggestion:
        // 192.168.2.216 is not even on the same subnet.
        "192.168.1.200",
        "192.168.2.216",
    };

    private void LoadRecent()
    {
        recent.Clear();
        string raw = PlayerPrefs.GetString(PREF_RECENT, "");
        foreach (var s in raw.Split('\n'))
            if (!string.IsNullOrEmpty(s.Trim())) recent.Add(s.Trim());

        // Append seeds that are not already remembered, after the real history
        // so a genuinely used address always comes first.
        if (seedAddresses != null)
            foreach (var s in seedAddresses)
                if (!string.IsNullOrEmpty(s) && !recent.Contains(s)) recent.Add(s);
    }

    private void RememberIP(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return;

        recent.Remove(ip);            // moving to the front, not duplicating
        recent.Insert(0, ip);

        while (recent.Count > Mathf.Max(1, recentCount))
            recent.RemoveAt(recent.Count - 1);

        PlayerPrefs.SetString(PREF_RECENT, string.Join("\n", recent));
        PlayerPrefs.Save();
    }

    /// <summary>Wire to the recent button. Steps through remembered addresses.</summary>
    public void CycleRecent()
    {
        if (recent.Count == 0)
        {
            if (recentLabel != null) recentLabel.text = "no recent addresses";
            return;
        }

        recentIndex = (recentIndex + 1) % recent.Count;
        if (ipInputField != null) ipInputField.text = recent[recentIndex];

        if (recentLabel != null)
            recentLabel.text = string.Format("recent {0}/{1}",
                recentIndex + 1, recent.Count);
    }

    private const string PREF_IP = "ROS_IP";
    private const string PREF_PORT = "ROS_PORT";
    private const string DEFAULT_IP = "192.168.1.100";
    private const int DEFAULT_PORT = 10000;   // ros_tcp_endpoint default

    [Tooltip("Leave ON when a ModeSelectMenu exists: the mode menu must be the " +
             "first screen, and this panel waits to be revealed. Turn OFF only " +
             "for a build with no menu, where this should appear immediately.")]
    public bool waitForModeSelection = true;

    /// <summary>Hide everything; the mode menu owns the screen until it calls back.</summary>
    public void HideUntilModeChosen()
    {
        if (ipInputPanel != null) ipInputPanel.SetActive(false);
        if (hudPanel != null) hudPanel.SetActive(false);
    }

    /// <summary>Show the IP/port panel. Called by ModeSelectMenu once a mode is picked.</summary>
    public void ShowConfig()
    {
        if (ipInputPanel != null) ipInputPanel.SetActive(true);
        if (hudPanel != null) hudPanel.SetActive(false);

        // Spell out the exit. With no robot on the network, Connect fails and
        // leaves you on this panel with the HUD still hidden - which reads as
        // "the HUD is broken" rather than "you never got past the connect
        // screen".
        if (ipStatusText != null)
            ipStatusText.text = "Connect to a robot, or press Skip to play without one.";
    }

    /// <summary>
    /// Bypass the connect screen entirely - Virtual Bot needs no robot.
    /// Reuses OnSkipPressed so there is one code path into the HUD rather than
    /// two that can drift apart.
    /// </summary>
    public void SkipStraightToHud()
    {
        OnSkipPressed();
    }

    void Start()
    {
        // The mode menu is the first screen, so this panel does not show itself
        // any more. Without this both panels appeared at once, stacked.
        if (waitForModeSelection)
        {
            HideUntilModeChosen();
        }
        else
        {
            ipInputPanel.SetActive(true);
            hudPanel.SetActive(false);
        }

        LoadRecent();

        if (recentButton != null)
            recentButton.onClick.AddListener(CycleRecent);

        if (recentLabel != null)
            recentLabel.text = recent.Count > 0
                ? string.Format("{0} recent - tap to cycle", recent.Count)
                : "no recent addresses";

        // Restore last used IP
        ipInputField.text = PlayerPrefs.GetString(PREF_IP, DEFAULT_IP);

        // Restore last used port, if the field exists
        if (portInputField != null)
        {
            int savedPort = PlayerPrefs.GetInt(
                PREF_PORT,
                robotController != null ? robotController.rosPort : DEFAULT_PORT);

            portInputField.text = savedPort.ToString();
            // Numeric keypad + digits only, so the user cannot type nonsense.
            portInputField.contentType = TMP_InputField.ContentType.IntegerNumber;
            portInputField.characterLimit = 5;
        }
    }

    public void OnConnectPressed()
    {
        string ip = ipInputField.text.Trim();

        if (string.IsNullOrEmpty(ip))
        {
            ShowError("Please enter a valid IP address");
            return;
        }

        // Resolve the port: use the field if present, else keep the existing value.
        int port = robotController != null ? robotController.rosPort : DEFAULT_PORT;

        if (portInputField != null)
        {
            string portText = portInputField.text.Trim();

            if (string.IsNullOrEmpty(portText))
            {
                ShowError("Please enter a port (default " + DEFAULT_PORT + ")");
                return;
            }

            if (!int.TryParse(portText, out port))
            {
                ShowError("Port must be a number");
                return;
            }

            // 0 is reserved; 1-1023 are privileged and would need root on the
            // board, which ros_tcp_endpoint does not run as.
            if (port < 1024 || port > 65535)
            {
                ShowError("Port must be between 1024 and 65535");
                return;
            }
        }

        // Persist for next launch
        PlayerPrefs.SetString(PREF_IP, ip);
        PlayerPrefs.SetInt(PREF_PORT, port);
        PlayerPrefs.Save();

        // Push into the controller so the HUD and publisher agree
        if (robotController != null)
        {
            robotController.rosIP = ip;
            robotController.rosPort = port;
        }

        // Apply to the connection. Changing host or port requires a reconnect -
        // ROSConnection reads these at connect time, not per message.
        ros = ROSConnection.GetOrCreateInstance();
        ros.RosIPAddress = ip;
        ros.RosPort = port;
        ros.Disconnect();
        ros.Connect();

        if (robotController != null)
            robotController.connectionRequested = true;

        // Remembered on ATTEMPT, not on success. A typo would otherwise never
        // be saved, but neither would a correct address whose robot happened to
        // be off - and that is the address you most want back next time.
        RememberIP(ip);

        ipStatusText.text = "Connecting to " + ip + ":" + port + "...";
        ipStatusText.color = Color.yellow;

        // Switch to HUD panel
        ipInputPanel.SetActive(false);
        hudPanel.SetActive(true);
    }

    public void OnSkipPressed()
    {
        if (robotController != null)
        {
            robotController.connectionStatus = "Simulation Mode - No ROS";
            robotController.connectionRequested = false;
        }

        // Skip ROS connection - simulation only mode
        ipInputPanel.SetActive(false);
        hudPanel.SetActive(true);
    }

    private void ShowError(string message)
    {
        ipStatusText.text = message;
        ipStatusText.color = Color.red;
    }
}
