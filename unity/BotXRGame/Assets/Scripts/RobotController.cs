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
    public float publishRate = 10f;

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
        if (ros != null && !ros.HasConnectionError)
        {
            connectionStatus = "ROS Connected | Publishing /cmd_vel";
        }
        else
        {
            connectionStatus = "Waiting for ROS...";
        }
    }

    void Update()
    {
        // Read controller input
        Vector2 input = Vector2.zero;
        float triggerValue = 0f;

        if (moveAction != null && moveAction.action != null)
            input = moveAction.action.ReadValue<Vector2>();

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

        // Publish to ROS at set rate
        timeElapsed += Time.deltaTime;
        if (timeElapsed >= 1f / publishRate)
        {
            PublishTwist();
            timeElapsed = 0f;
        }
    }

    void PublishTwist()
    {
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
        CancelInvoke("CheckConnection");
    }
}