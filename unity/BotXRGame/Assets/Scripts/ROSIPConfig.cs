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

    private const string PREF_IP = "ROS_IP";
    private const string PREF_PORT = "ROS_PORT";
    private const string DEFAULT_IP = "192.168.1.100";
    private const int DEFAULT_PORT = 10000;   // ros_tcp_endpoint default

    void Start()
    {
        // Show connection panel first, hide HUD
        ipInputPanel.SetActive(true);
        hudPanel.SetActive(false);

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
