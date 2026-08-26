using UnityEngine;

/// <summary>
/// Pins the ship's visible mesh to its root transform.
///
/// The centre marker proved that ShipRoot moves correctly, with the intended
/// inertia, while the visible mesh drifts away from it over a run - so
/// something is moving the mesh in local space on top of GhostBot moving the
/// root. Two movers, one hierarchy.
///
/// This holds the mesh at its original local offset every LateUpdate, after
/// all Update-driven motion has run. The ship then inherits exactly the root's
/// motion, which is the weighted feel that was wanted.
///
/// It treats the symptom on purpose: the drift is logged with the name of the
/// transform that moved, so the real culprit can be removed and this component
/// dropped afterwards.
/// </summary>
public class ShipVisualLock : MonoBehaviour
{
    [Tooltip("The visible mesh under the ship root. Found automatically if unset.")]
    public Transform visual;

    [Tooltip("Also pin local rotation. Leave off if the model has an idle " +
             "animation you want to keep - the root already supplies heading.")]
    public bool lockRotation;

    [Tooltip("Log once when drift first exceeds this many metres, naming the " +
             "transform, so the second mover can be tracked down.")]
    public float reportDriftOver = 0.05f;

    private Vector3 basePos;
    private Quaternion baseRot;
    private bool captured;
    private bool reported;

    void Start()
    {
        if (visual == null)
        {
            // First renderer that is not on the root itself - that is the model.
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (r.transform != transform) { visual = r.transform; break; }
            }
        }

        if (visual == null)
        {
            Debug.LogWarning("[ShipVisualLock] no child renderer found; nothing to pin.");
            enabled = false;
            return;
        }

        basePos = visual.localPosition;
        baseRot = visual.localRotation;
        captured = true;

        Debug.LogFormat("[ShipVisualLock] pinning '{0}' at local {1}",
            visual.name, basePos);
    }

    void LateUpdate()
    {
        if (!captured || visual == null) return;

        if (!reported)
        {
            float drift = (visual.localPosition - basePos).magnitude;
            if (drift > reportDriftOver)
            {
                reported = true;
                Debug.LogWarningFormat(
                    "[ShipVisualLock] '{0}' drifted {1:F3} m in local space " +
                    "(now {2}, expected {3}). Something is moving the mesh as well " +
                    "as the root - find it and this component can be removed.",
                    visual.name, drift, visual.localPosition, basePos);
            }
        }

        visual.localPosition = basePos;
        if (lockRotation) visual.localRotation = baseRot;
    }
}
