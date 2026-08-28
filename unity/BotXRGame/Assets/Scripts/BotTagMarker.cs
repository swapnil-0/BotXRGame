using UnityEngine;

/// <summary>
/// Green marker and heading arrow drawn on the tracked robot.
///
/// Replaces both the placeholder cube and the hovering spaceship. The cube said
/// only "a tag is here"; the arrow says what direction is being COMMANDED,
/// which is the thing you actually need while driving a real robot - if the
/// robot goes somewhere else, the gap between the arrow and its motion is the
/// whole diagnosis.
/// </summary>
public class BotTagMarker : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Transform tracking the bot's tag. Usually TagStandIn.")]
    public Transform tagTransform;

    [Tooltip("Reads the command actually being sent to the robot.")]
    public RobotController robot;

    [Tooltip("Drive-to-start controller, when present, so its target heading " +
             "is shown instead of the stick's while it has control.")]
    public BotStartupDrive startupDrive;

    [Header("Look")]
    public Material markerMaterial;
    public float dotSize = 0.07f;
    public float dotHeight = 0.10f;
    public float arrowLength = 0.30f;

    private Transform dot;
    private LineRenderer stem;
    private LineRenderer arrow;
    private bool built;

    void Start()
    {
        if (!GameMode.IsAprilTag) { enabled = false; return; }
        Build();
    }

    private void Build()
    {
        if (built) return;
        built = true;

        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "BotDot";
        var col = sphere.GetComponent<Collider>();
        if (col != null) Destroy(col);

        dot = sphere.transform;
        dot.SetParent(transform, false);
        dot.localScale = Vector3.one * dotSize;

        var r = sphere.GetComponent<Renderer>();
        Material mat = markerMaterial;
        if (mat == null)
        {
            // Runtime primitives get a material whose shader is stripped from
            // URP builds - magenta, and single-eye under instanced stereo.
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null) mat = new Material(sh) { color = new Color(0.2f, 0.95f, 0.35f) };
        }
        if (r != null && mat != null) r.sharedMaterial = mat;

        stem = MakeLine("BotStem", mat, 0.004f, 0.004f);
        arrow = MakeLine("BotHeading", mat, 0.010f, 0.001f);
    }

    private LineRenderer MakeLine(string name, Material mat, float w0, float w1)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.startWidth = w0;
        lr.endWidth = w1;
        if (mat != null) lr.sharedMaterial = mat;
        return lr;
    }

    void LateUpdate()
    {
        if (tagTransform == null || dot == null) return;

        Vector3 p = tagTransform.position;

        dot.position = p + Vector3.up * dotHeight;
        stem.SetPosition(0, p);
        stem.SetPosition(1, p + Vector3.up * dotHeight);

        // Direction being commanded, not the tag's own facing. Those differ
        // whenever the robot is turning, and the commanded one is what explains
        // the robot's behaviour.
        Vector3 dir = CommandedDirection();
        Vector3 baseP = p + Vector3.up * 0.012f;
        arrow.SetPosition(0, baseP);
        arrow.SetPosition(1, baseP + dir * arrowLength);
    }

    private Vector3 CommandedDirection()
    {
        // While the startup drive owns the robot, show where IT is steering -
        // otherwise the arrow would show a stick that is being ignored.
        if (startupDrive != null && startupDrive.HasControl)
        {
            Vector3 d = startupDrive.TargetDirection;
            d.y = 0f;
            if (d.sqrMagnitude > 1e-6f) return d.normalized;
        }

        Vector3 fwd = tagTransform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();

        if (robot == null) return fwd;

        // Rotate the tag's forward by the commanded yaw so a turn command shows
        // as the arrow swinging before the robot follows.
        float yaw = -robot.angularZ * Mathf.Rad2Deg * 0.5f;
        return Quaternion.Euler(0f, yaw, 0f) * fwd;
    }
}
