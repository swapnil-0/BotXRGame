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
    [Tooltip("Selectable sizes in FEET. Push the thumbstick left or right " +
             "while aiming to cycle; the rectangle resizes live so you choose " +
             "by seeing what actually fits the room.")]
    public float[] arenaSizeOptionsFeet = { 3f, 5f, 7f, 8f, 9f };
    [Tooltip("Which option to start on. 0 = the first entry above.")]
    public int defaultSizeIndex = 0;
    [Tooltip("Edge length in metres. Set from the selected option at runtime.")]
    public float arenaSize = 0.9144f;
    [Tooltip("How far the ship floats above the floor.")]
    public float hoverHeight = 0.04f;

    [Header("Tornado")]
    [Tooltip("Influence radius as a fraction of arena size. This is a RADIUS, " +
             "so 0.5 makes the vortex as wide as the whole arena and leaves " +
             "nowhere safe. 0.30 gives a vortex covering about 60% of the " +
             "width, with clear ground at the start and the finish.")]
    [Range(0.1f, 0.6f)]
    public float tornadoRadiusFraction = 0.30f;
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

    public enum Phase { Area, Ship, Done }
    /// <summary>Area first, then the ship separately inside it, then play.</summary>
    public Phase CurrentPhase { get; private set; } = Phase.Area;

    [Header("Course - spawned once the ship is placed")]
    [Tooltip("Cups to scatter. New field, so the code default applies.")]
    public int cupCount = 4;
    [Tooltip("Cup visual height, metres.")]
    public float cupHeight = 0.10f;
    [Tooltip("Number of patrolling tornadoes across the course.")]
    public int tornadoCount = 2;
    [Tooltip("Twin-tornado influence radius as a fraction of arena size. " +
             "Smaller than the single-tornado value: two of them plus movement " +
             "covers plenty of ground already.")]
    public float twinTornadoRadiusFraction = 0.16f;
    [Tooltip("Side-to-side travel as a fraction of arena size.")]
    public float tornadoPatrolFraction = 0.28f;
    [Tooltip("Material for spawned cups. MUST be an asset from the project - " +
             "runtime-created primitives fall back to a default material whose " +
             "shader can be stripped from URP builds, rendering magenta and, " +
             "under single-pass instanced XR, only in one eye.")]
    public Material cupMaterial;

    [Tooltip("Thin the course out on small arenas. Every fraction here scales " +
             "with arenaSize, but the SHIP does not - it is a fixed physical " +
             "size. So at 3 ft two tornado bands leave gaps the ship cannot " +
             "actually fit through, even though the proportions look identical " +
             "to 8 ft. Turn off to force the raw cupCount/tornadoCount.")]
    public bool scaleDensityWithArena = true;

    /// <summary>Tornadoes to actually spawn, after the small-arena rule.</summary>
    private int EffectiveTornadoCount =>
        (scaleDensityWithArena && arenaSize < 1.3f) ? Mathf.Min(1, tornadoCount) : tornadoCount;

    /// <summary>Cups to actually spawn, after the small-arena rule.</summary>
    private int EffectiveCupCount
    {
        get
        {
            if (!scaleDensityWithArena) return cupCount;
            if (arenaSize < 1.3f) return Mathf.Min(2, cupCount);   // under ~4 ft
            if (arenaSize < 1.8f) return Mathf.Min(3, cupCount);   // under ~6 ft
            return cupCount;
        }
    }

    public bool IsPlaced { get; private set; }
    public event Action<Vector3, Vector3> OnPlaced;   // origin, forward

    // Committed arena, captured at the end of the Area phase.
    private Vector3 arenaOrigin, arenaForward, arenaRight;
    private float arenaFloorY;

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

    private int sizeIndex;
    private bool sizeLatched;

    void Start()
    {
        // The ship only appears once the area exists - during area selection
        // the player should see nothing but the rectangle.
        if (ship != null) ship.gameObject.SetActive(false);

        // A component already serialized in the scene predates this field, and
        // Unity will not backfill an array default onto it - it deserializes
        // as empty. Rebuild it rather than silently shipping no options.
        if (arenaSizeOptionsFeet == null || arenaSizeOptionsFeet.Length == 0)
            arenaSizeOptionsFeet = new[] { 3f, 5f, 7f, 8f, 9f };

        sizeIndex = Mathf.Clamp(defaultSizeIndex, 0, arenaSizeOptionsFeet.Length - 1);
        ApplySelectedSize();
    }

    private void ApplySelectedSize()
    {
        arenaSize = arenaSizeOptionsFeet[sizeIndex] * 0.3048f;
    }

    /// <summary>
    /// Cycle the arena size with the thumbstick during aiming.
    ///
    /// Borrows GhostBot's move action rather than adding another Inspector
    /// field: the ship is parked during area selection, so the stick is free,
    /// and this needs no extra wiring.
    /// </summary>
    private void ReadSizeInput()
    {
        if (arenaSizeOptionsFeet == null || arenaSizeOptionsFeet.Length < 2) return;
        if (ship == null) return;

        var bot = ship.GetComponent<GhostBot>();
        if (bot == null || bot.moveAction == null || bot.moveAction.action == null) return;

        // GhostBot enables this in OnEnable, but the ship is deactivated for
        // the whole of area selection - so nothing has enabled it yet and
        // ReadValue would sit at zero forever.
        if (!bot.moveAction.action.enabled) bot.moveAction.action.Enable();

        float x = bot.moveAction.action.ReadValue<Vector2>().x;

        // Latch so one flick moves one step; release below 0.3 to re-arm.
        if (!sizeLatched && Mathf.Abs(x) > 0.6f)
        {
            sizeIndex += (x > 0f) ? 1 : -1;
            sizeIndex = Mathf.Clamp(sizeIndex, 0, arenaSizeOptionsFeet.Length - 1);
            ApplySelectedSize();
            sizeLatched = true;
        }
        else if (sizeLatched && Mathf.Abs(x) < 0.3f)
        {
            sizeLatched = false;
        }
    }

    private void ShowSizePrompt()
    {
        if (run == null) return;
        float ft = arenaSizeOptionsFeet[sizeIndex];
        run.ShowMessage(string.Format(
            "{0:0} x {1:0} ft   ({2}/{3})\nstick left/right to resize, trigger to place",
            ft, ft, sizeIndex + 1, arenaSizeOptionsFeet.Length));
    }

    void Update()
    {
        if (CurrentPhase == Phase.Done) return;

        if (CurrentPhase == Phase.Area)
        {
            // Size first: UpdateAim runs the free-space probe against
            // arenaSize, so changing it afterwards would validate one
            // footprint and draw another.
            ReadSizeInput();
            UpdateAim();
            ShowSizePrompt();
            ReadInput();
        }
        else
        {
            UpdateShipAim();
            ReadShipInput();
        }
    }

    // ------------------------------------------------- phase 2: place ship

    private bool shipAimValid;
    private Vector3 shipAimPoint;

    private void UpdateShipAim()
    {
        if (raycastManager == null || rayOrigin == null || ship == null) return;

        var ray = new Ray(rayOrigin.position, rayOrigin.forward);
        if (!raycastManager.Raycast(ray, hits, TrackableType.PlaneWithinPolygon))
        {
            shipAimValid = false;
            return;
        }

        Vector3 hit = hits[0].pose.position;

        // Inside the committed arena? Work in arena-local coordinates, with a
        // small inset so the ship cannot start half over the boundary.
        Vector3 rel = hit - arenaOrigin;
        float fwdDist = Vector3.Dot(rel, arenaForward);
        float sideDist = Vector3.Dot(rel, arenaRight);
        float half = arenaSize * 0.5f;
        float inset = 0.06f;

        shipAimValid = fwdDist >= inset && fwdDist <= arenaSize - inset
                       && Mathf.Abs(sideDist) <= half - inset;

        ship.gameObject.SetActive(true);
        ship.transform.position = new Vector3(hit.x, arenaFloorY + hoverHeight, hit.z);
        ship.transform.rotation = Quaternion.LookRotation(arenaForward, Vector3.up);
        shipAimPoint = ship.transform.position;
    }

    private bool shipWasPressed;

    private void ReadShipInput()
    {
        float v = (placeAction != null && placeAction.action != null)
            ? placeAction.action.ReadValue<float>() : 0f;
        bool pressed = v > pressThreshold;

        if (pressed && !shipWasPressed && shipAimValid) CommitShip();
        shipWasPressed = pressed;
    }

    private void CommitShip()
    {
        CurrentPhase = Phase.Done;
        IsPlaced = true;

        SpawnCups();
        SpawnTornadoes();

        if (run != null)
            run.BeginAt(shipAimPoint, arenaOrigin, arenaForward,
                        arenaSize, arenaFloorY, hoverHeight);

        OnPlaced?.Invoke(arenaOrigin, arenaForward);
    }

    // ------------------------------------------------------ course spawning

    private Vector3 ArenaPoint(float sideFrac, float fwdFrac)
    {
        // sideFrac in [-0.5, 0.5] across the width, fwdFrac in [0, 1] along it.
        return arenaOrigin
             + arenaRight * (sideFrac * arenaSize)
             + arenaForward * (fwdFrac * arenaSize);
    }

    private void SpawnCups()
    {
        CollectibleCup.ResetAll();

        // Fixed fractional layout, chosen so every cup sits in a gap between
        // the tornado sweep bands. The patrols run at 0.38 and 0.68 of the
        // course with a radius of 0.16 x arena, so their danger bands span
        // roughly 0.22-0.54 and 0.52-0.84 forward. Cups go before, between
        // and after those bands - the player still has to CROSS the bands to
        // reach them, but standing at a cup is safe.
        Vector2[] layout =
        {
            // The slot between the two bands is narrower than the vortex
            // radius, so no cup goes there - two before the first band, two
            // after the second. The crossing itself is the challenge.
            //
            // Ordered near/far/near/far rather than near/near/far/far so that
            // taking the first N on a small arena still puts cups on BOTH
            // sides of the tornadoes. Grouping them would have let a reduced
            // course be finished without ever crossing a band.
            new Vector2(-0.28f, 0.14f),
            new Vector2( 0.26f, 0.91f),
            new Vector2( 0.30f, 0.17f),
            new Vector2(-0.28f, 0.88f),
        };

        for (int i = 0; i < Mathf.Min(EffectiveCupCount, layout.Length); i++)
        {
            var cup = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cup.name = "Cup_" + (i + 1);
            var col = cup.GetComponent<Collider>();
            if (col != null) Destroy(col);

            Vector3 p = ArenaPoint(layout[i].x, layout[i].y);
            cup.transform.position = new Vector3(p.x, arenaFloorY + cupHeight * 0.5f, p.z);
            cup.transform.localScale = new Vector3(0.07f, cupHeight * 0.5f, 0.07f);

            var r = cup.GetComponent<Renderer>();
            if (r != null)
            {
                if (cupMaterial != null)
                {
                    r.sharedMaterial = cupMaterial;
                }
                else
                {
                    // Last-resort fallback; only works if URP Unlit survived
                    // shader stripping. The assigned asset is the real fix.
                    var sh = Shader.Find("Universal Render Pipeline/Unlit");
                    if (sh != null) r.material = new Material(sh);
                    r.material.color = new Color(0.15f, 0.9f, 0.35f);
                }
            }

            cup.AddComponent<CollectibleCup>();
        }
    }

    private void SpawnTornadoes()
    {
        if (tornadoPrefab == null) return;

        // Two patrol lines across the course. Different periods and phases so
        // the gaps between them keep shifting and there is no fixed safe lane.
        float[] fwdFracs = { 0.38f, 0.68f };
        float[] periods = { 6.5f, 9.0f };
        float[] phases = { 0f, Mathf.PI * 0.7f };

        int count = Mathf.Min(EffectiveTornadoCount, fwdFracs.Length);

        for (int i = 0; i < count; i++)
        {
            // A lone tornado sits mid-course instead of at 0.38, so the run
            // is not lopsided when the small-arena rule drops the second one.
            float fwdFrac = (count == 1) ? 0.5f : fwdFracs[i];
            Vector3 basePos = ArenaPoint(0f, fwdFrac);
            var t = Instantiate(tornadoPrefab,
                                new Vector3(basePos.x, arenaFloorY, basePos.z),
                                Quaternion.identity);
            var tornado = t.GetComponent<Tornado>();
            if (tornado == null) continue;

            if (ship != null) tornado.bot = ship.GetComponent<GhostBot>();
            tornado.influenceRadius = arenaSize * twinTornadoRadiusFraction;
            tornado.InitPatrol(arenaRight,
                               arenaSize * tornadoPatrolFraction,
                               periods[i], phases[i]);

            if (run != null) tornado.OnCaptured += run.HandleCapture;

            var ring = tornado.radiusRing;
            if (ring != null)
            {
                float d = tornado.influenceRadius * 2f;
                ring.localScale = new Vector3(d, ring.localScale.y, d);
            }

            // Scale the funnel with the influence radius. The prefab funnel is
            // a fixed size, so at small radii it towered over its actual
            // danger zone and read as far bigger than it was.
            if (tornado.funnel != null)
            {
                float w = tornado.influenceRadius;          // diameter = radius
                tornado.funnel.localScale = new Vector3(
                    w, tornado.funnel.localScale.y, w);
                tornado.RefreshFunnelBaseScale();
            }
        }
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

        // The ship stays hidden during area selection; it appears in phase 2.
    }

    private void ReadInput()
    {
        float v = (placeAction != null && placeAction.action != null)
            ? placeAction.action.ReadValue<float>() : 0f;
        bool pressed = v > pressThreshold;

        if (pressed && !wasPressed && aimValid) Place();
        wasPressed = pressed;
    }

    /// <summary>End of phase 1: commit the area, move on to placing the ship.</summary>
    private void Place()
    {
        arenaOrigin = aimOrigin;
        arenaForward = aimForward;
        arenaRight = Vector3.Cross(Vector3.up, aimForward).normalized;
        arenaFloorY = floorY;

        ShowPreview(placedColour);

        Vector3 finish = arenaOrigin + arenaForward * arenaSize;
        if (finishMarker != null)
        {
            finishMarker.gameObject.SetActive(true);
            finishMarker.position = new Vector3(finish.x, floorY + 0.005f, finish.z);
        }

        CurrentPhase = Phase.Ship;
        // Consume this press so the same trigger pull cannot immediately place
        // the ship as well.
        shipWasPressed = true;
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
