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
/// The marker only moves when the tag moves. The arrows carry all the motion,
/// and their LENGTH is magnitude, not just direction:
///
///   green   total command actually sent to the robot
///   purple  the joystick's contribution
///   orange  the tornado's contribution
///
/// Green is the sum of the other two, so the angle between green and purple is
/// precisely how much the vortex is taking from the driver. One arrow could
/// only show that something was wrong; three show what.
///
/// Before START only green appears, pointing at the start line - the stick is
/// ignored during the approach and the tornado is not yet in play, so a split
/// there would be two arrows of zero length.
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

    [Header("Colours")]
    [Tooltip("Total command being sent - the sum of the other two.")]
    public Color totalColour = new Color(0.2f, 0.95f, 0.35f);

    [Tooltip("Joystick contribution.")]
    public Color stickColour = new Color(0.65f, 0.35f, 0.95f);

    [Tooltip("Tornado contribution.")]
    public Color tornadoColour = new Color(1f, 0.55f, 0.15f);

    [Tooltip("Before START, only the green arrow shows, pointing at the start " +
             "line. Splitting it into components there would be noise: the " +
             "stick is ignored during the approach and the tornado is not yet " +
             "in play.")]
    public bool splitOnlyWhileRunning = true;

    [Tooltip("Vertical spacing between the three arrows, metres. Without it " +
             "they z-fight when the tornado contribution is small and the " +
             "three nearly coincide.")]
    public float arrowSpacing = 0.012f;

    private Transform dot;
    private LineRenderer stem;
    private LineRenderer totalArrow, stickArrow, tornadoArrow;
    private Material totalMat, stickMat, tornadoMat;
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
            // totalColour: this material is the base for the dot and stem, and
            // green is the marker's identity colour. The three arrows each
            // tint their own copy of it.
            if (sh != null) mat = new Material(sh) { color = totalColour };
        }

        var r = sphere.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;

        stem = MakeLine("BotStem", mat, 0.004f, 0.004f);

        // One material instance per arrow. Sharing would make all three the
        // same colour, which defeats the entire point of splitting them.
        totalMat = Tint(mat, totalColour);
        stickMat = Tint(mat, stickColour);
        tornadoMat = Tint(mat, tornadoColour);

        // Total drawn thickest so it stays readable when the components
        // overlap it almost exactly, which is the common case.
        totalArrow = MakeLine("CmdTotal", totalMat, 0.014f, 0.001f);
        stickArrow = MakeLine("CmdStick", stickMat, 0.008f, 0.001f);
        tornadoArrow = MakeLine("CmdTornado", tornadoMat, 0.008f, 0.001f);
    }

    private static Material Tint(Material src, Color c)
    {
        if (src == null) return null;
        var m = new Material(src);
        m.color = c;
        return m;
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

        bool running = mixer != null &&
                       mixer.CurrentPhase == BotCommandMixer.Phase.Running;
        bool split = running || !splitOnlyWhileRunning;

        Vector3 basePos = p + Vector3.up * arrowSpacing;

        if (mixer == null)
        {
            DrawArrow(totalArrow, basePos, Flat(tag.forward), 0.15f);
            SetArrow(stickArrow, false);
            SetArrow(tornadoArrow, false);
            return;
        }

        if (!split)
        {
            // Before START: one green arrow at the start line. The stick is
            // ignored during the approach and the tornado is not in play, so
            // three arrows here would be two arrows of zero length.
            DrawArrow(totalArrow, basePos, mixer.ToStartDirection, 0.20f);
            SetArrow(stickArrow, false);
            SetArrow(tornadoArrow, false);
            return;
        }

        // All three from the same origin, stacked slightly so they do not
        // z-fight when the tornado contribution is small and they nearly
        // coincide. Green is the sum, so green disagreeing with purple is
        // exactly the amount the tornado is taking from you.
        DrawVector(totalArrow, basePos, mixer.CommandVector);
        DrawVector(stickArrow, basePos + Vector3.up * arrowSpacing, mixer.StickVector);
        DrawVector(tornadoArrow, basePos + Vector3.up * arrowSpacing * 2f, mixer.TornadoVector);
    }

    private void DrawVector(LineRenderer lr, Vector3 from, Vector3 v)
    {
        v.y = 0f;
        if (v.sqrMagnitude < 1e-6f) { SetArrow(lr, false); return; }

        DrawArrow(lr, from, v.normalized,
                  Mathf.Min(v.magnitude * metresPerUnitSpeed, maxArrowLength));
    }

    private void DrawArrow(LineRenderer lr, Vector3 from, Vector3 dir, float length)
    {
        if (lr == null) return;
        SetArrow(lr, true);
        lr.SetPosition(0, from);
        lr.SetPosition(1, from + dir * length);
    }

    private static void SetArrow(LineRenderer lr, bool on)
    {
        if (lr != null && lr.enabled != on) lr.enabled = on;
    }

    private void SetVisible(bool v)
    {
        if (dot != null && dot.gameObject.activeSelf != v) dot.gameObject.SetActive(v);
        if (stem != null && stem.enabled != v) stem.enabled = v;
        SetArrow(totalArrow, v);
        SetArrow(stickArrow, v);
        SetArrow(tornadoArrow, v);
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
    }
}
