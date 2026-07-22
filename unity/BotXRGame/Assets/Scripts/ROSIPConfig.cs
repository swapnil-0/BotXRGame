using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Robotics.ROSTCPConnector;

public class ROSIPConfig : MonoBehaviour
{
    [Header("UI Panels")]
    public GameObject ipInputPanel;
    public GameObject hudPanel;

    [Header("IP Input")]
    public TMP_InputField ipInputField;
    public TextMeshProUGUI ipStatusText;

    [Header("References")]
    public RobotController robotController;

    private ROSConnection ros;

    void Start()
    {
        // Show IP input panel first, hide HUD
        ipInputPanel.SetActive(true);
        hudPanel.SetActive(false);

        // Load last used IP if available
        string savedIP = PlayerPrefs.GetString("ROS_IP", "192.168.1.100");
        ipInputField.text = savedIP;
    }

    public void OnConnectPressed()
    {
        string ip = ipInputField.text.Trim();

        if (string.IsNullOrEmpty(ip))
        {
            ipStatusText.text = "Please enter a valid IP address";
            ipStatusText.color = Color.red;
            return;
        }

        // Save IP for next time
        PlayerPrefs.SetString("ROS_IP", ip);
        PlayerPrefs.Save();

        // Update RobotController with new IP
        robotController.rosIP = ip;

        // Update ROSConnection
        ros = ROSConnection.GetOrCreateInstance();
        ros.RosIPAddress = ip;
        ros.RosPort = robotController.rosPort;

        ipStatusText.text = "Connecting to " + ip + "...";
        ipStatusText.color = Color.yellow;

        // Switch to HUD panel
        ipInputPanel.SetActive(false);
        hudPanel.SetActive(true);
    }

    public void OnSkipPressed()
    {
        // Skip ROS connection - simulation only mode
        ipInputPanel.SetActive(false);
        hudPanel.SetActive(true);
        robotController.connectionStatus = "Simulation Mode - No ROS";
    }
}