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
    [Tooltip("One button per remembered address, most recent first, each " +
             "labelled with its ip:port. Tapping one fills both fields.\n\n" +
             "Replaced a single button that cycled through a hidden list: you " +
             "could not see what it would load, and with two stale entries it " +
             "looked dead. Typing an IP on an XR keyboard is slow, and one wrong " +
             "digit fails exactly like an unreachable robot.")]
    public UnityEngine.UI.Button[] recentButtons;

    [Tooltip("Legacy single cycle button. Unused when recentButtons is set.")]
    public UnityEngine.UI.Button recentButton;

    [Tooltip("Legacy label for the cycle button.")]
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
        "192.168.1.204",   // test RubikPi, wired - the link test passed here 2026-10-29
        "192.168.1.245",   // test RubikPi, wlan0
        "192.168.1.214",   // robot IQ9 (ur-iq9-3), last seen on home wifi; DHCP
    };

    /// <summary>
    /// Entries are "ip" or "ip:port" (older saves have no port). Stored
    /// newline separated in one pref, most recent first.
    /// </summary>
    private void LoadRecent()
    {
        recent.Clear();
        string raw = PlayerPrefs.GetString(PREF_RECENT, "");
        foreach (var s in raw.Split('\n'))
            AddUnique(recent, s.Trim());

        // Seeds after the real history, so an address you actually used comes first.
        if (seedAddresses != null)
            foreach (var s in seedAddresses)
                AddUnique(recent, s);
    }

    /// <summary>Adds unless the same host:port is already listed.</summary>
    private static void AddUnique(System.Collections.Generic.List<string> list, string entry)
    {
        if (string.IsNullOrEmpty(entry)) return;
        string key = WithPort(entry);
        foreach (var e in list)
            if (WithPort(e) == key) return;
        list.Add(entry);
    }

    private static string WithPort(string entry)
    {
        Split(entry, out string ip, out int port);
        return ip + ":" + port;
    }

    private static void Split(string entry, out string ip, out int port)
    {
        ip = entry;
        port = DEFAULT_PORT;
        int colon = entry.LastIndexOf(':');
        if (colon > 0 && int.TryParse(entry.Substring(colon + 1), out int p))
        {
            ip = entry.Substring(0, colon);
            port = p;
        }
    }

    private void RememberIP(string ip, int port)
    {
        if (string.IsNullOrEmpty(ip)) return;

        string entry = ip + ":" + port;
        recent.RemoveAll(e => WithPort(e) == entry);   // move to the front, no duplicate
        recent.Insert(0, entry);

        while (recent.Count > Mathf.Max(1, recentCount))
            recent.RemoveAt(recent.Count - 1);

        PlayerPrefs.SetString(PREF_RECENT, string.Join("\n", recent));
        PlayerPrefs.Save();
        RefreshRecentButtons();
    }

    /// <summary>Label each recent button with its address; hide the spares.</summary>
    private void RefreshRecentButtons()
    {
        if (recentButtons == null) return;
        for (int i = 0; i < recentButtons.Length; i++)
        {
            var b = recentButtons[i];
            if (b == null) continue;
            bool has = i < recent.Count;
            b.gameObject.SetActive(has);
            if (!has) continue;

            Split(recent[i], out string ip, out int port);
            var label = b.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
                label.text = port == DEFAULT_PORT ? ip : ip + ":" + port;
        }
    }

    /// <summary>Fill the IP and port fields from recent entry i.</summary>
    public void UseRecent(int i)
    {
        if (i < 0 || i >= recent.Count) return;
        Split(recent[i], out string ip, out int port);

        if (ipInputField != null) ipInputField.text = ip;
        if (portInputField != null) portInputField.text = port.ToString();

        if (ipStatusText != null)
        {
            ipStatusText.text = "Loaded " + ip + ":" + port + " - press Connect";
            ipStatusText.color = Color.white;
        }
        Debug.LogFormat("[IP] recent {0} -> {1}:{2}", i, ip, port);
    }

    /// <summary>Legacy cycle button: steps through remembered addresses.</summary>
    public void CycleRecent()
    {
        if (recent.Count == 0)
        {
            if (recentLabel != null) recentLabel.text = "no recent addresses";
            return;
        }

        recentIndex = (recentIndex + 1) % recent.Count;
        UseRecent(recentIndex);
        if (recentLabel != null)
            recentLabel.text = string.Format("{0}   ({1}/{2})",
                recent[recentIndex], recentIndex + 1, recent.Count);
    }

    private const string PREF_IP = "ROS_IP";
    private const string PREF_PORT = "ROS_PORT";
    private const string DEFAULT_IP = "192.168.1.100";
    private const int DEFAULT_PORT = 10000;   // ros_tcp_endpoint default

    [Tooltip("Leave ON when a ModeSelectMenu exists: the mode menu must be the " +
             "first screen, and this panel waits to be revealed. Turn OFF only " +
             "for a build with no menu, where this should appear immediately.")]
    public bool waitForModeSelection = true;

    private bool warnedBothPanels;

    /// <summary>
    /// The connect panel and the HUD are mutually exclusive - the HUD only ever
    /// appears after Connect or Skip, both of which hide the panel.
    ///
    /// Enforced every frame rather than trusted, because the panel was seen
    /// floating over a live run: both hide paths are correct on inspection, so
    /// something else re-enables it, and reading the code did not reveal what.
    /// Enforcing the invariant fixes the symptom whatever the cause; the log
    /// line then names the culprit if it happens again.
    /// </summary>
    void LateUpdate()
    {
        if (ipInputPanel == null || hudPanel == null) return;
        if (!hudPanel.activeSelf || !ipInputPanel.activeSelf) return;

        ipInputPanel.SetActive(false);

        if (!warnedBothPanels)
        {
            warnedBothPanels = true;
            Debug.LogWarning("[IP] connect panel was active while the HUD was up - " +
                             "re-hidden. Something re-enabled IPInputPanel after " +
                             "Connect/Skip.");
        }
    }

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

        if (recentButtons != null && recentButtons.Length > 0)
        {
            for (int i = 0; i < recentButtons.Length; i++)
            {
                if (recentButtons[i] == null) continue;
                int index = i;   // a copy for the closure, not the loop variable

                // On pointer DOWN, not click. A click needs press and release
                // on the same object, and closing a pinch drags the hand's aim
                // ray down a few cm - off these small buttons and onto the
                // panel (logged: press on RecentIP_0, release on IPInputPanel).
                // Safe on press: it only fills the fields; Connect is separate.
                var trigger = recentButtons[i].gameObject.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (trigger == null)
                    trigger = recentButtons[i].gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                var down = new UnityEngine.EventSystems.EventTrigger.Entry
                {
                    eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown
                };
                down.callback.AddListener(_ => UseRecent(index));
                trigger.triggers.Add(down);
            }
            RefreshRecentButtons();
        }
        else if (recentButton != null)
        {
            recentButton.onClick.AddListener(CycleRecent);
            if (recentLabel != null)
                recentLabel.text = recent.Count > 0
                    ? string.Format("{0} recent - tap to cycle", recent.Count)
                    : "no recent addresses";
        }

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
        RememberIP(ip, port);

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
