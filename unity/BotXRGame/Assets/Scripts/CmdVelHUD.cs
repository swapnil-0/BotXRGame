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
        topicText.text    = "Topic: /cmd_vel";
    }
}