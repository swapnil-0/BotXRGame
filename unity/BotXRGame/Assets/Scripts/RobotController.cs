using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;

public class RobotController : MonoBehaviour
{
    [Header("ROS Settings")]
    public string rosIP = "192.168.1.100";
    public int rosPort = 10000;
    public string topicName = "/cmd_vel";
    // 30 Hz gives responsive teleop without meaningful bandwidth cost
    // (48-byte Twist payload => ~2 KB/s).
    public float publishRate = 30f;

    [Header("Movement Settings")]
    public float linearSpeed = 1.0f;
    public float angularSpeed = 1.5f;

    [Header("Input Actions")]
    public InputActionReference moveAction;
    public InputActionReference triggerAction;

    [Header("Simulation")]
    public bool moveInSimulation = true;

    // ROS
    private ROSConnection ros;
    private float timeElapsed;

    // Public so HUD can read them
    [HideInInspector] public float linearX;
    [HideInInspector] public float angularZ;
    [HideInInspector] public string connectionStatus = "Waiting for ROS...";
    [HideInInspector] public bool connectionRequested = false;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RosIPAddress = rosIP;
        ros.RosPort = rosPort;
        ros.RegisterPublisher<TwistMsg>(topicName);
        InvokeRepeating("CheckConnection", 1f, 2f);

        // Enable input actions
        if (moveAction != null && moveAction.action != null)
            moveAction.action.Enable();
        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Enable();
    }

    void CheckConnection()
    {
        if (!connectionRequested)
            connectionStatus = "Not connected";
        else if (ros != null && !ros.HasConnectionError)
            connectionStatus = "ROS Connected | Publishing " + topicName;
        else
            connectionStatus = "Connecting / Retrying...";
    }

    /// <summary>
    /// Publish to a different topic at runtime.
    ///
    /// The robot's drive topic is not knowable from this repo - it belongs to
    /// the vendor stack - and a wrong topic name fails exactly like a dead
    /// link: the headset publishes happily into a topic nobody subscribes to.
    /// Being able to try names in the headset turns a rebuild per guess into a
    /// button press.
    /// </summary>
    public void SetTopic(string newTopic)
    {
        if (string.IsNullOrEmpty(newTopic) || newTopic == topicName) return;

        topicName = newTopic;

        // Re-register: the connector maps topic to type at registration, and
        // publishing to an unregistered topic is silently dropped.
        if (ros != null) ros.RegisterPublisher<TwistMsg>(topicName);

        Debug.LogFormat("[Robot] publishing to {0}", topicName);
    }

    // --- external command override ---------------------------------------
    private bool hasExternalCommand;
    private float externalLinear, externalAngular;

    /// <summary>True while something other than the stick is driving.</summary>
    public bool IsExternallyDriven => hasExternalCommand;

    /// <summary>
    /// Take over the robot. Values are in the same units the stick produces,
    /// so they flow through the existing publish path unchanged.
    /// </summary>
    public void SetExternalCommand(float linear, float angular)
    {
        hasExternalCommand = true;
        externalLinear = linear;
        externalAngular = angular;
    }

    /// <summary>Hand control back to the stick.</summary>
    public void ClearExternalCommand()
    {
        hasExternalCommand = false;
        externalLinear = 0f;
        externalAngular = 0f;
    }

    void Update()
    {
        // Read controller input
        Vector2 input = Vector2.zero;
        float triggerValue = 0f;

        if (moveAction != null && moveAction.action != null)
            input = moveAction.action.ReadValue<Vector2>();
        
        if (input.magnitude < 0.15f) input = Vector2.zero;

        if (triggerAction != null && triggerAction.action != null)
            triggerValue = triggerAction.action.ReadValue<float>();


        // Map to Twist values
        linearX = input.y * linearSpeed;
        angularZ = -input.x * angularSpeed;

        // An external driver (BotStartupDrive) overrides the stick entirely
        // while it has control. Overriding rather than blending on purpose:
        // mixing a player's input with an automatic approach gives a robot that
        // fights itself, and the resulting motion is impossible to attribute.
        if (hasExternalCommand)
        {
            linearX = externalLinear;
            angularZ = externalAngular;
        }

        // Move spaceship in simulation
        if (moveInSimulation)
        {
            transform.Translate(0, 0, linearX * Time.deltaTime);
            transform.Rotate(0, -angularZ * Time.deltaTime * Mathf.Rad2Deg, 0);
        }

        // Publish to ROS at set rate.
        // Subtract the interval rather than zeroing: resetting to 0 discards the
        // overshoot every cycle, rounding the period up to a whole number of frames.
        // At 72 Hz display and publishRate=10 that produced 8 frames * 13.89ms =
        // 111ms = 9.0 Hz instead of 10 Hz (measured 2026-08-10).
        float publishInterval = 1f / publishRate;
        timeElapsed += Time.deltaTime;
        if (timeElapsed >= publishInterval)
        {
            PublishTwist();
            timeElapsed -= publishInterval;
            // Guard against unbounded catch-up after a frame hitch or app pause.
            if (timeElapsed > publishInterval) timeElapsed = 0f;
        }
    }

    // --- publish telemetry, read by the HUD ------------------------------
    // Added after a demo where "ROS did not work" could not be distinguished
    // from "ROS worked and every value was zero" or "publishing never started".
    // Those three have completely different fixes and looked identical.
    public int PublishCount { get; private set; }
    public float LastPublishedLinear { get; private set; }
    public float LastPublishedAngular { get; private set; }
    public float LastPublishTime { get; private set; } = -1f;

    /// <summary>Why nothing is being sent, when nothing is being sent.</summary>
    public string PublishBlockedReason { get; private set; } = "";

    void PublishTwist()
    {
        if (!connectionRequested)
        {
            // Set by ROSIPConfig only when CONNECT is pressed. Skip reaches the
            // HUD without ever setting it, so the app looks fully alive while
            // publishing nothing at all - which is exactly what a broken link
            // looks like from the headset.
            PublishBlockedReason = "not connected - press CONNECT, not SKIP";
            return;
        }

        PublishBlockedReason = "";

        TwistMsg twist = new TwistMsg();
        twist.linear.x = linearX;
        twist.linear.y = 0;
        twist.linear.z = 0;
        twist.angular.x = 0;
        twist.angular.y = 0;
        twist.angular.z = angularZ;
        ros.Publish(topicName, twist);

        PublishCount++;
        LastPublishedLinear = linearX;
        LastPublishedAngular = angularZ;
        LastPublishTime = Time.time;
    }

    void OnDestroy()
    {
        SendZero();
        CancelInvoke("CheckConnection");
    }

    void SendZero()
    {
        if (!connectionRequested || ros == null) return;
        TwistMsg stop = new TwistMsg();
        ros.Publish(topicName, stop);
    }

    void OnApplicationPause(bool paused) { if (paused) SendZero(); }
    void OnApplicationFocus(bool focused) { if (!focused) SendZero();} 
}