using UnityEngine;

/// <summary>
/// A small marker drawn at the position the GAME thinks the ship occupies.
///
/// Written after two wrong diagnoses made from headset screenshots. Reported
/// coordinates and cup distances have been internally consistent every time,
/// yet the ship looks like it is somewhere else - and judging "somewhere else"
/// from a photograph of a passthrough scene, at an angle, with perspective, is
/// guesswork.
///
/// This removes the guessing. If the marker sits on the ship, the logical
/// position is correct and the bug is in what the player is shown. If the
/// marker floats away from the ship, the logical position is wrong and the
/// ship's visible mesh is not where its transform is.
/// </summary>
public class CenterMarker : MonoBehaviour
{
    [Tooltip("Marker diameter in metres. Small enough not to hide the ship.")]
    public float size = 0.06f;

    [Tooltip("Metres above the tracked point, so the marker does not sit " +
             "inside the ship mesh and become invisible.")]
    public float heightOffset = 0.12f;

    [Tooltip("Length of the heading ray showing the direction the ship " +
             "actually travels when you push forward.")]
    public float headingLength = 0.25f;

    private GhostBot bot;
    private Transform dot;
    private LineRenderer stem;
    private LineRenderer heading;

    /// <summary>Spawn a marker that follows the given ship. Returns null if bot is null.</summary>
    public static CenterMarker Create(GhostBot bot, Material material)
    {
        if (bot == null) return null;

        var go = new GameObject("CenterMarker");
        var m = go.AddComponent<CenterMarker>();
        m.bot = bot;
        m.Build(material);
        return m;
    }

    private void Build(Material material)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Dot";
        var col = sphere.GetComponent<Collider>();
        if (col != null) Destroy(col);

        dot = sphere.transform;
        dot.SetParent(transform, false);
        dot.localScale = Vector3.one * size;

        var r = sphere.GetComponent<Renderer>();
        if (r != null)
        {
            if (material != null)
            {
                r.sharedMaterial = material;
            }
            else
            {
                // Runtime-created primitives get a default material whose
                // shader is stripped from URP builds - it renders magenta and,
                // under single-pass instanced XR, in one eye only.
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh != null) r.material = new Material(sh);
                r.material.color = new Color(1f, 0.2f, 0.9f);
            }
        }

        // A vertical stem down to the tracked point. Without it the floating
        // dot is impossible to localise in depth through passthrough.
        var stemGo = new GameObject("Stem");
        stemGo.transform.SetParent(transform, false);
        stem = stemGo.AddComponent<LineRenderer>();
        stem.useWorldSpace = true;
        stem.positionCount = 2;
        stem.startWidth = 0.004f;
        stem.endWidth = 0.004f;
        if (dot != null) stem.sharedMaterial = dot.GetComponent<Renderer>().sharedMaterial;

        // The direction the ship ACTUALLY moves on forward stick. The player
        // steers by the nose of the model, so if the nose and this ray point
        // different ways, "I pushed forward and it went right" is explained -
        // and the angle between them is the correction needed.
        var headGo = new GameObject("Heading");
        headGo.transform.SetParent(transform, false);
        heading = headGo.AddComponent<LineRenderer>();
        heading.useWorldSpace = true;
        heading.positionCount = 2;
        heading.startWidth = 0.008f;
        heading.endWidth = 0.001f;      // tapered, so it reads as an arrow
        if (dot != null) heading.sharedMaterial = dot.GetComponent<Renderer>().sharedMaterial;
    }

    void LateUpdate()
    {
        // LateUpdate, not Update: the ship moves in Update, and reading its
        // position beforehand would show the marker trailing by a frame and
        // invite a third wrong theory.
        if (bot == null) { gameObject.SetActive(false); return; }

        Vector3 c = bot.Center;
        if (dot != null) dot.position = c + Vector3.up * heightOffset;
        if (stem != null)
        {
            stem.SetPosition(0, c);
            stem.SetPosition(1, c + Vector3.up * heightOffset);
        }

        if (heading != null)
        {
            // Root forward, because that is what GhostBot drives along -
            // deliberately not the mesh's forward, which is the thing under
            // suspicion.
            Vector3 f = bot.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude > 1e-6f) f.Normalize();

            Vector3 baseP = c + Vector3.up * 0.01f;
            heading.SetPosition(0, baseP);
            heading.SetPosition(1, baseP + f * headingLength);
        }
    }
}
