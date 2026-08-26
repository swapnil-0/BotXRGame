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

    [Tooltip("Pin local rotation as well as position. ON by default.\n\n" +
             "It was off, and that was the bug: the mesh reports zero local " +
             "yaw at spawn, so the alignment path never enabled this, and the " +
             "mesh's rotation was left free while only its position was held. " +
             "Something then rotated it progressively - nose diverging from " +
             "the direction of travel the longer you played, while the heading " +
             "ray stayed correct.")]
    public bool lockRotation = true;

    [Tooltip("Zero out any local yaw on the mesh so its nose points along the " +
             "direction the ship actually travels. Only corrects yaw the mesh " +
             "carries as a local offset - if the MODEL itself was authored " +
             "facing sideways, that is a different fix and needs a manual " +
             "offset instead.")]
    public bool alignVisualYawToRoot = true;

    [Tooltip("Report once CUMULATIVE drift exceeds this many metres.\n\n" +
             "Must be cumulative, not instantaneous: this component resets the " +
             "mesh every frame, so any single frame's drift is one frame's " +
             "worth and an instantaneous threshold can never trip. The first " +
             "version of this check tested instantaneous drift and therefore " +
             "reported nothing whether or not a second mover existed.")]
    public float reportDriftOver = 0.05f;

    [Tooltip("Same, in degrees, for rotation drift.")]
    public float reportAngleDriftOver = 5f;

    private Vector3 basePos;
    private Quaternion baseRot;
    private bool captured;
    private bool reported;
    private float cumulativeDrift;
    private float peakFrameDrift;
    private int driftFrames;

    // Rotation drift was not measured at all, which is why nothing was
    // reported while the nose swung away from the heading. Position-only
    // instrumentation cannot see a rotation fault.
    private float cumulativeAngleDrift;
    private float peakAngleDrift;
    private bool reportedAngle;

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

        // Local rotation matters as much as position here. The ship drives
        // along the ROOT's forward, but the player steers by the nose of the
        // model. Any local yaw on the mesh makes those two disagree by a fixed
        // angle - push forward, travel sideways - and it would look like a
        // physics or input fault rather than a transform offset.
        Vector3 e = visual.localEulerAngles;
        float yaw = Mathf.DeltaAngle(0f, e.y);

        Debug.LogFormat("[ShipVisualLock] pinning '{0}' at local pos {1} rot {2} (yaw {3:F1} deg)",
            visual.name, basePos, e, yaw);

        if (Mathf.Abs(yaw) > 5f)
        {
            Debug.LogWarningFormat(
                "[ShipVisualLock] '{0}' is yawed {1:F1} deg from the root. The ship " +
                "moves along the ROOT's forward, so the nose points {1:F1} deg away " +
                "from the direction the stick actually drives. Set " +
                "alignVisualYawToRoot to correct it.",
                visual.name, yaw);
        }

        if (alignVisualYawToRoot && Mathf.Abs(yaw) > 0.5f)
        {
            baseRot = Quaternion.Euler(e.x, e.y - yaw, e.z);
            visual.localRotation = baseRot;
            lockRotation = true;
            Debug.LogFormat("[ShipVisualLock] corrected yaw by {0:F1} deg", -yaw);
        }
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

        float angle = Quaternion.Angle(visual.localRotation, baseRot);
        if (angle > 0.01f)
        {
            cumulativeAngleDrift += angle;
            peakAngleDrift = Mathf.Max(peakAngleDrift, angle);

            if (!reportedAngle && cumulativeAngleDrift > reportAngleDriftOver)
            {
                reportedAngle = true;
                Debug.LogWarningFormat(
                    "[ShipVisualLock] '{0}' is being ROTATED by something else. " +
                    "Cumulative {1:F1} deg, peak {2:F3} deg/frame (~{3:F1} deg/s). " +
                    "This is the nose drifting off the direction of travel.",
                    visual.name, cumulativeAngleDrift, peakAngleDrift,
                    peakAngleDrift / Mathf.Max(Time.deltaTime, 1e-4f));
            }
        }

        visual.localPosition = basePos;
        if (lockRotation) visual.localRotation = baseRot;
    }
}
