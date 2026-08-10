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

    void PublishTwist()
    {
        if (!connectionRequested) return;

        TwistMsg twist = new TwistMsg();
        twist.linear.x = linearX;
        twist.linear.y = 0;
        twist.linear.z = 0;
        twist.angular.x = 0;
        twist.angular.y = 0;
        twist.angular.z = angularZ;
        ros.Publish(topicName, twist);
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