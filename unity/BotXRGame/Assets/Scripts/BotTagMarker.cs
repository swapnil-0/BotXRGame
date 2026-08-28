using UnityEngine;

/// <summary>
/// Green marker sitting on AprilTag id 0, with an arrow showing the command
/// being sent to the robot.
///
/// Bound to the tag id, not to "the first tag seen". With cup tags in the room
/// the first-seen rule picked a cup, so the bot marker sat on the wrong object
/// - and it looked like tag detection had failed when in fact identity
/// resolution had.
///
/// The marker only moves when the tag moves. The arrow carries all the motion:
/// its direction and LENGTH are the commanded velocity, so a strong tornado
/// pull is visibly a long arrow swinging away from where the stick is pointing.
/// That difference between what you asked for and what is being sent is the
/// thing worth seeing.
/// </summary>
public class BotTagMarker : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Supplies the id-0 tag. Identity comes from here, not from " +
             "whichever marker happened to resolve first.")]
    public TagCupTracker tagTracker;

    [Tooltip("Supplies the command being sent. Without it the arrow falls back " +
             "to the tag's own facing, which shows where the robot points " +
             "rather than where it is being told to go.")]
    public BotCommandMixer mixer;

    [Header("Look")]
    public Material markerMaterial;
    public float dotSize = 0.07f;
    public float dotHeight = 0.10f;

    [Tooltip("Metres of arrow per metre-per-second of command. The arrow shows " +
             "magnitude, not just direction, so the tornado's contribution is " +
             "legible rather than inferred.")]
    public float metresPerUnitSpeed = 1.2f;

    [Tooltip("Longest the arrow may draw, so a big pull cannot span the room.")]
    public float maxArrowLength = 0.6f;

    [Header("Colour")]
    public Color idleColour = new Color(0.2f, 0.95f, 0.35f);
    public Color pulledColour = new Color(1f, 0.55f, 0.15f);

    [Tooltip("Tornado share of the command above which the arrow turns orange - " +
             "the point where the robot is doing more of what the vortex wants " +
             "than what you asked for.")]
    [Range(0.1f, 1f)]
    public float pulledFraction = 0.4f;

    private Transform dot;
    private LineRenderer stem;
    private LineRenderer arrow;
    private Material arrowMat;
    private bool built;

    void Start()
    {
        if (!GameMode.IsAprilTag) { enabled = false; return; }

        if (tagTracker == null) tagTracker = FindAnyObjectByType<TagCupTracker>();
        if (mixer == null) mixer = FindAnyObjectByType<BotCommandMixer>();

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

        Material mat = markerMaterial;
        if (mat == null)
        {
            // Runtime primitives get a material whose shader is stripped from
            // URP builds - magenta, and one eye only under instanced stereo.
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null) mat = new Material(sh) { color = idleColour };
        }

        var r = sphere.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;

        stem = MakeLine("BotStem", mat, 0.004f, 0.004f);

        // Its own material instance so the arrow can recolour without turning
        // the dot and every other object sharing that material orange too.
        arrowMat = mat != null ? new Material(mat) : null;
        arrow = MakeLine("BotHeading", arrowMat, 0.012f, 0.001f);
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
        if (dot == null || tagTracker == null) return;

        Transform tag = tagTracker.BotTag;
        bool visible = tag != null && tagTracker.BotTracked;

        SetVisible(visible);
        if (!visible) return;

        // Straight to the tag pose, no smoothing. Smoothing here would make the
        // marker drift after the robot stops, which reads as tracking error.
        Vector3 p = tag.position;

        dot.position = p + Vector3.up * dotHeight;
        stem.SetPosition(0, p);
        stem.SetPosition(1, p + Vector3.up * dotHeight);

        Vector3 cmd = mixer != null ? mixer.CommandVector : Vector3.zero;
        cmd.y = 0f;

        float length = Mathf.Min(cmd.magnitude * metresPerUnitSpeed, maxArrowLength);
        Vector3 dir = cmd.sqrMagnitude > 1e-6f ? cmd.normalized : Flat(tag.forward);

        Vector3 baseP = p + Vector3.up * 0.012f;
        arrow.SetPosition(0, baseP);
        arrow.SetPosition(1, baseP + dir * length);

        if (arrowMat != null && mixer != null)
        {
            float total = mixer.CommandVector.magnitude;
            float share = total > 1e-4f ? mixer.TornadoVector.magnitude / total : 0f;
            arrowMat.color = share >= pulledFraction ? pulledColour : idleColour;
        }
    }

    private void SetVisible(bool v)
    {
        if (dot != null && dot.gameObject.activeSelf != v) dot.gameObject.SetActive(v);
        if (stem != null && stem.enabled != v) stem.enabled = v;
        if (arrow != null && arrow.enabled != v) arrow.enabled = v;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
    }
}
