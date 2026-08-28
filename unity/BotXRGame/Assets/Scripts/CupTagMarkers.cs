using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws a marker over every detected cup tag, labelled with its id and state.
///
/// The debug list already says cup 4 is upright, but not WHICH physical cup is
/// cup 4. With five near-identical cups on the floor that is the difference
/// between a number and a fact - and if one cup never resolves, the list simply
/// omits it, which is invisible unless you are counting rows.
///
/// A marker in the world answers both at once: every cup wearing a label is
/// tracked, and any cup without one is not.
/// </summary>
public class CupTagMarkers : MonoBehaviour
{
    [Header("Source")]
    public TagCupTracker tracker;

    [Header("Look")]
    public Material markerMaterial;

    [Tooltip("Height above the tag, metres. Above the cup rather than on it, so " +
             "the marker does not hide the tag from the tracker.")]
    public float height = 0.16f;

    public float dotSize = 0.045f;

    [Tooltip("World-space text size. The first value was too small to read " +
             "through passthrough at arm's length, which made the labels " +
             "useless for the one job they have - saying which cup is which.")]
    public float labelSize = 0.22f;

    [Header("Colours")]
    public Color uprightColour = new Color(0.25f, 0.9f, 0.4f);
    public Color toppledColour = new Color(1f, 0.35f, 0.25f);

    [Tooltip("A cup seen before but not right now. Distinct from toppled: a " +
             "toppled cup often hides its own tag, so the two are easy to " +
             "confuse - and confusing them would turn a tracking dropout into " +
             "a phantom score.")]
    public Color lostColour = new Color(0.55f, 0.55f, 0.6f);

    private class Marker
    {
        public GameObject Root;
        public Transform Dot;
        public TMPro.TextMeshPro Label;
        public Material Mat;
    }

    private readonly Dictionary<int, Marker> markers = new Dictionary<int, Marker>();

    void Start()
    {
        if (!GameMode.IsAprilTag) { enabled = false; return; }
        if (tracker == null) tracker = FindAnyObjectByType<TagCupTracker>();
    }

    [Tooltip("Also label the bot tag, in a distinct colour.\n\n" +
             "Without this there is no way to see WHICH physical tag the app " +
             "considers the robot - the bot marker sits on it, but if that tag " +
             "is not the one you expected, the marker just looks misplaced.")]
    public bool labelBotTag = true;

    public Color botColour = new Color(0.3f, 0.7f, 1f);

    void LateUpdate()
    {
        if (tracker == null) return;

        // Bot tag first, so it is labelled even though it is not a cup.
        if (labelBotTag && tracker.SeenTags.TryGetValue(tracker.botMarkerId, out var botPos))
        {
            var bm = GetOrCreate(tracker.botMarkerId);
            bm.Root.transform.position = botPos + Vector3.up * height;
            if (bm.Mat != null) bm.Mat.color = botColour;
            if (bm.Label != null)
            {
                bm.Label.color = botColour;
                bm.Label.text = string.Format("#{0}\nBOT", tracker.botMarkerId);
                FaceCamera(bm);
            }
        }

        foreach (var cup in tracker.Cups)
        {
            var m = GetOrCreate(cup.Id);

            m.Root.transform.position = cup.Position + Vector3.up * height;

            Color c = !cup.Visible ? lostColour
                    : cup.Toppled ? toppledColour
                    : uprightColour;

            if (m.Mat != null) m.Mat.color = c;
            if (m.Label != null)
            {
                m.Label.color = c;
                // Id first: that is what the debug list keys on, so the two
                // can be read together without translating between them.
                m.Label.text = string.Format("#{0}\n{1}",
                    cup.Id,
                    !cup.Visible ? "lost" : cup.Toppled ? "DOWN" : "up");
            }

            FaceCamera(m);
        }
    }

    private void FaceCamera(Marker m)
    {
        if (Camera.main == null || m.Label == null) return;

        Vector3 look = m.Root.transform.position - Camera.main.transform.position;
        look.y = 0f;
        if (look.sqrMagnitude > 1e-6f)
            m.Label.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
    }

    private Marker GetOrCreate(int id)
    {
        if (markers.TryGetValue(id, out var existing)) return existing;

        var root = new GameObject("CupMarker_" + id);
        root.transform.SetParent(transform, false);

        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Dot";
        var col = sphere.GetComponent<Collider>();
        if (col != null) Destroy(col);
        sphere.transform.SetParent(root.transform, false);
        sphere.transform.localScale = Vector3.one * dotSize;

        Material baseMat = markerMaterial;
        if (baseMat == null)
        {
            // Runtime primitives fall back to a material whose shader is
            // stripped from URP builds - magenta, and one eye only under
            // instanced stereo.
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null) baseMat = new Material(sh);
        }

        // Its own instance per cup, so cups can differ in colour. Sharing would
        // make every cup take the colour of whichever updated last.
        var mat = baseMat != null ? new Material(baseMat) : null;
        var r = sphere.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, dotSize * 1.6f, 0f);

        var label = labelGo.AddComponent<TMPro.TextMeshPro>();
        label.text = "#" + id;
        label.fontSize = labelSize * 20f;
        label.alignment = TMPro.TextAlignmentOptions.Center;

        // Wide enough for "#10 DOWN" without wrapping, and wrapping disabled:
        // a wrapped label at this size becomes a block of text hanging over the
        // arena rather than a tag.
        label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

        var rt = label.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = new Vector2(0.6f, 0.3f);

        var marker = new Marker { Root = root, Dot = sphere.transform, Label = label, Mat = mat };
        markers[id] = marker;

        Debug.LogFormat("[Cups] marker created for cup {0}", id);
        return marker;
    }
}
