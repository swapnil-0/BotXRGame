using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Aim at the floor, see where the arena would go, press to commit.
///
/// No menus. The app opens straight into aiming, which is the whole
/// interaction: point, look at the colour, press.
///
///   BLUE   the patch of floor is clear and the arena would fit here
///   RED    something is in the way, or the floor is not mapped there
///   GREEN  placed
///
/// The arena runs FORWARD from where you point: the ship sits at the midpoint
/// of the near edge, and the finish is the midpoint of the far edge. Forward is
/// the horizontal direction you are pointing, so you naturally aim the course
/// in the direction you want to travel.
/// </summary>
[RequireComponent(typeof(ArenaRun))]
public class ArenaPlacer : MonoBehaviour
{
    [Header("AR")]
    public ARRaycastManager raycastManager;
    [Tooltip("Transform whose forward is the aim ray - controller or ray interactor.")]
    public Transform rayOrigin;

    [Header("Input")]
    public InputActionReference placeAction;
    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    [Header("Arena")]
    [Tooltip("Edge length in metres. 0.9144 = 3 ft (fits a small room), " +
             "2.4384 = 8 ft (the real field). Ship speed and tornado size are " +
             "derived from this, so a course tuned small behaves the same large.")]
    public float arenaSize = 0.9144f;
    [Tooltip("How far the ship floats above the floor.")]
    public float hoverHeight = 0.04f;

    [Header("Tornado")]
    [Tooltip("Influence radius as a fraction of arena size. 0.45 means the pull " +
             "is felt across most of the crossing; lower values confine it to " +
             "the middle and leave the approach uneventful.")]
    [Range(0.1f, 0.6f)]
    public float tornadoRadiusFraction = 0.45f;
    [Tooltip("Breathing period as a fraction of the target crossing time. " +
             "Near 1 means the player experiences roughly one gust per run.")]
    [Range(0.3f, 2f)]
    public float tornadoPeriodFraction = 0.9f;

    [Header("Validation")]
    [Tooltip("Grid resolution per side. 5 gives 25 probe points.")]
    public int samplesPerSide = 5;
    [Tooltip("Also physics-raycast for obstacles. Needs AR Scene Meshing enabled.")]
    public bool useMeshObstacles = true;
    public LayerMask obstacleMask = ~0;

    [Header("Scene References")]
    [Tooltip("The ship. Follows the aim point while placing, then is released.")]
    public Transform ship;
    [Tooltip("Flat quad covering the arena footprint. Tinted by validity.")]
    public Renderer previewSurface;
    [Tooltip("Optional outline. Four corners are set in order.")]
    public LineRenderer previewOutline;
    [Tooltip("Marker dropped at the finish line midpoint.")]
    public Transform finishMarker;
    [Tooltip("Spawned at the arena centre once placed.")]
    public GameObject tornadoPrefab;

    [Header("Colours")]
    public Color validColour = new Color(0.2f, 0.5f, 1f, 0.35f);
    public Color invalidColour = new Color(1f, 0.25f, 0.2f, 0.35f);
    public Color placedColour = new Color(0.2f, 0.9f, 0.35f, 0.30f);

    public bool IsPlaced { get; private set; }
    public event Action<Vector3, Vector3> OnPlaced;   // origin, forward

    private ArenaRun run;
    private bool wasPressed;
    private bool aimValid;
    private Vector3 aimOrigin, aimForward;
    private float floorY;
    private static readonly System.Collections.Generic.List<ARRaycastHit> hits =
        new System.Collections.Generic.List<ARRaycastHit>();

    void Awake() { run = GetComponent<ArenaRun>(); }

    void OnEnable()
    {
        if (placeAction != null && placeAction.action != null)
            placeAction.action.Enable();
    }

    void Update()
    {
        if (IsPlaced) return;
        UpdateAim();
        ReadInput();
    }

    private void UpdateAim()
    {
        if (raycastManager == null || rayOrigin == null) { Hide(); return; }

        var ray = new Ray(rayOrigin.position, rayOrigin.forward);
        if (!raycastManager.Raycast(ray, hits, TrackableType.PlaneWithinPolygon))
        {
            Hide();
            aimValid = false;
            return;
        }

        Vector3 hit = hits[0].pose.position;
        floorY = hit.y;

        // The course runs along where you are pointing, flattened to the floor.
        Vector3 fwd = rayOrigin.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
        fwd.Normalize();

        aimOrigin = hit;
        aimForward = fwd;

        var probe = FreeSpaceProbe.Test(
            raycastManager, aimOrigin, aimForward, arenaSize, arenaSize, floorY,
            samplesPerSide, 0.08f, useMeshObstacles, obstacleMask);

        aimValid = probe.IsClear;

        ShowPreview(aimValid ? validColour : invalidColour);

        if (ship != null)
        {
            ship.position = aimOrigin + Vector3.up * hoverHeight;
            ship.rotation = Quaternion.LookRotation(aimForward, Vector3.up);
        }
    }

    private void ReadInput()
    {
        float v = (placeAction != null && placeAction.action != null)
            ? placeAction.action.ReadValue<float>() : 0f;
        bool pressed = v > pressThreshold;

        if (pressed && !wasPressed && aimValid) Place();
        wasPressed = pressed;
    }

    private void Place()
    {
        IsPlaced = true;
        ShowPreview(placedColour);

        Vector3 centre = aimOrigin + aimForward * (arenaSize * 0.5f);
        Vector3 finish = aimOrigin + aimForward * arenaSize;

        if (finishMarker != null)
        {
            finishMarker.gameObject.SetActive(true);
            finishMarker.position = new Vector3(finish.x, floorY + 0.005f, finish.z);
        }

        // Start the run first, so the ship's speed is set before the tornado
        // reads it - the vortex scales its force to whatever the ship can do.
        if (run != null)
            run.Begin(aimOrigin, aimForward, arenaSize, floorY, hoverHeight);

        if (tornadoPrefab != null)
        {
            var t = Instantiate(tornadoPrefab,
                                new Vector3(centre.x, floorY, centre.z),
                                Quaternion.identity);
            var tornado = t.GetComponent<Tornado>();
            if (tornado != null)
            {
                if (ship != null) tornado.bot = ship.GetComponent<GhostBot>();

                // Everything proportional to the arena, so the same feel
                // survives a change of field size.
                tornado.influenceRadius = arenaSize * tornadoRadiusFraction;
                if (run != null)
                    tornado.period = run.targetCrossingSeconds * tornadoPeriodFraction;

                // The core is inescapable by design, so it needs an exit:
                // ArenaRun resets the ship and adds a time penalty.
                if (run != null) tornado.OnCaptured += run.HandleCapture;

                var ring = tornado.radiusRing;
                if (ring != null)
                {
                    float d = tornado.influenceRadius * 2f;
                    ring.localScale = new Vector3(d, ring.localScale.y, d);
                }
            }
        }

        OnPlaced?.Invoke(aimOrigin, aimForward);
    }

    // ------------------------------------------------------------- visuals

    private void ShowPreview(Color c)
    {
        Vector3 centre = aimOrigin + aimForward * (arenaSize * 0.5f);
        Vector3 right = Vector3.Cross(Vector3.up, aimForward).normalized;
        float h = arenaSize * 0.5f;

        if (previewSurface != null)
        {
            previewSurface.gameObject.SetActive(true);
            var tr = previewSurface.transform;
            tr.position = new Vector3(centre.x, floorY + 0.003f, centre.z);

            // A Unity Quad's face lies in its local XY plane with the normal
            // along local +Z. LookRotation sets local +Z to its first argument,
            // so the normal must be given as Vector3.up - passing aimForward
            // there points the normal along the floor and the quad stands up
            // as a vertical wall.
            //
            // With +Z up, the quad's local X spans width and local Y spans
            // depth along aimForward.
            tr.rotation = Quaternion.LookRotation(Vector3.up, aimForward);
            tr.localScale = new Vector3(arenaSize, arenaSize, 1f);

            if (previewSurface.material != null)
                previewSurface.material.color = c;
        }

        if (previewOutline != null)
        {
            previewOutline.gameObject.SetActive(true);
            previewOutline.loop = true;
            previewOutline.positionCount = 4;
            float y = floorY + 0.006f;
            Vector3 nearL = aimOrigin - right * h;
            Vector3 nearR = aimOrigin + right * h;
            Vector3 farR = nearR + aimForward * arenaSize;
            Vector3 farL = nearL + aimForward * arenaSize;
            previewOutline.SetPosition(0, Flat(nearL, y));
            previewOutline.SetPosition(1, Flat(nearR, y));
            previewOutline.SetPosition(2, Flat(farR, y));
            previewOutline.SetPosition(3, Flat(farL, y));
            previewOutline.startColor = previewOutline.endColor = c;
        }
    }

    private static Vector3 Flat(Vector3 p, float y) { p.y = y; return p; }

    private void Hide()
    {
        if (previewSurface != null) previewSurface.gameObject.SetActive(false);
        if (previewOutline != null) previewOutline.gameObject.SetActive(false);
    }
}
