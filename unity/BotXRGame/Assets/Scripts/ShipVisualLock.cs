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

    [Tooltip("Report once CUMULATIVE drift exceeds this many metres.\n\n" +
             "Must be cumulative, not instantaneous: this component resets the " +
             "mesh every frame, so any single frame's drift is one frame's " +
             "worth and an instantaneous threshold can never trip. The first " +
             "version of this check tested instantaneous drift and therefore " +
             "reported nothing whether or not a second mover existed.")]
    public float reportDriftOver = 0.05f;

    private Vector3 basePos;
    private Quaternion baseRot;
    private bool captured;
    private bool reported;
    private float cumulativeDrift;
    private float peakFrameDrift;
    private int driftFrames;

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

        float drift = (visual.localPosition - basePos).magnitude;

        if (drift > 1e-5f)
        {
            cumulativeDrift += drift;
            peakFrameDrift = Mathf.Max(peakFrameDrift, drift);
            driftFrames++;

            if (!reported && cumulativeDrift > reportDriftOver)
            {
                reported = true;
                Debug.LogWarningFormat(
                    "[ShipVisualLock] '{0}' is being moved by something else. " +
                    "Cumulative {1:F3} m over {2} frames, peak {3:F4} m/frame " +
                    "(~{4:F2} m/s of unwanted motion). Find that mover and this " +
                    "component can be removed.",
                    visual.name, cumulativeDrift, driftFrames, peakFrameDrift,
                    peakFrameDrift / Mathf.Max(Time.deltaTime, 1e-4f));
            }
        }

        visual.localPosition = basePos;
        if (lockRotation) visual.localRotation = baseRot;
    }
}
