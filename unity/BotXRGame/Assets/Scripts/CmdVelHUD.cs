using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class CmdVelHUD : MonoBehaviour
{
    [Header("References")]
    public RobotController robotController;

    [Header("UI Elements")]
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI linearXText;
    public TextMeshProUGUI angularZText;
    public TextMeshProUGUI topicText;
    [Tooltip("Optional. Shows the host:port actually in use.")]
    public TextMeshProUGUI endpointText;

    void Update()
    {
        if (robotController == null) return;

        // Connection status
        statusText.text = robotController.connectionStatus;

        // Color status text based on connection
        if (robotController.connectionStatus.Contains("Connected"))
            statusText.color = Color.green;
        else
            statusText.color = Color.yellow;

        // Live values
        linearXText.text  = $"linear.x  : {robotController.linearX:F3}";
        angularZText.text = $"angular.z : {robotController.angularZ:F3}";
        topicText.text    = $"Topic: {robotController.topicName}";

        // Optional: which endpoint we are actually talking to. Useful when
        // debugging on a network with more than one board.
        if (endpointText != null)
            endpointText.text = $"ROS: {robotController.rosIP}:{robotController.rosPort}";
    }
}