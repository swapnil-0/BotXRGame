using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// Distance Measurement experience: aim the right controller, pull the
/// trigger, read how far that point is from the headset.
///
/// The distances come from the platform, not from our camera code: Android
/// XR's ray cast (XR_ANDROID_raycast via AR Foundation's ARRaycastManager)
/// against two things -
///   DEPTH  the headset's depth map. Hits any surface: floor, wall, a cup, a
///          person. Needs android.permission.SCENE_UNDERSTANDING_FINE, which
///          this experience asks for when it starts.
///   PLANE  detected planes (floor, table). Only flat, already-detected
///          surfaces, but stable.
/// Both are shown side by side, live, so they can be compared - and both
/// are the ground truth to check Cup Detect's camera-based positions against.
///
/// Like Cup Detect, activates by POLLING for the choice (Start() runs before
/// the player picks anything) and stands the game down while it runs.
///
/// Controls (right controller):
///   trigger  drop a pin at the aimed point (depth if it hit, else plane)
///   A        clear all pins          B  undo the last pin
/// </summary>
public class DistanceTestMode : MonoBehaviour, IExperienceRestart
{
    [Header("HUD")]
    public TMPro.TextMeshProUGUI display;

    [Header("Input")]
    public InputActionReference triggerAction;
    public InputActionReference aAction;
    public InputActionReference bAction;
    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    [Header("Ray")]
    [Tooltip("Where the measuring ray comes from - the right controller's aim. " +
             "Taken from ArenaPlacer when empty.")]
    public Transform rayOrigin;
    public ARRaycastManager raycastManager;

    [Tooltip("Head to measure from. Camera.main when empty.")]
    public Transform head;

    [Header("Markers")]
    [Tooltip("A material ASSET, so its shader is in the build (Shader.Find at " +
             "runtime can come back stripped and the markers vanish).")]
    public Material markerMaterial;
    public Color depthColour = new Color(0.25f, 0.85f, 1f);
    public Color planeColour = new Color(1f, 0.85f, 0.25f);
    public int maxPins = 12;

    [Tooltip("Label text height per metre of distance - constant apparent size.")]
    public float labelSizePerMetre = 0.22f;

    public bool Active { get; private set; }

    private const string FinePermission = "android.permission.SCENE_UNDERSTANDING_FINE";

    private enum Source { Depth, Plane }

    private struct Hit
    {
        public bool valid;
        public Vector3 point;
        public float rayDistance;   // from the controller, along the ray
    }

    private class Pin
    {
        public int Id;
        public Source Source;
        public Vector3 Point;
        public Vector3 HeadAt;
        public float HeadDistance, RayDistance, FromPrevious;
        public GameObject Root;
        public TMPro.TextMeshPro Label;
        public Material Mat;
    }

    private readonly List<Pin> pins = new List<Pin>();
    private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
    private readonly Dictionary<InputActionReference, bool> was = new Dictionary<InputActionReference, bool>();
    private int nextPinId = 1;

    private Hit liveDepth, livePlane;
    private int depthHitFrames, planeHitFrames, castFrames;
    private string permissionState = "not asked";

    private Transform reticle;
    private Material reticleMat;
    private LineRenderer aimLine;

    // ----------------------------------------------------------- activation

    void Update()
    {
        if (!Active)
        {
            if (GameMode.Chosen && GameMode.IsDistanceTest) Activate();
            return;
        }

        CastLive();
        DrawLive();
        HandleInput();
        RefreshPinLabels();
        Render();
    }

    private void Activate()
    {
        Active = true;
        Debug.Log("[Distance] experience active - standing the game down");

        if (raycastManager == null) raycastManager = FindAnyObjectByType<ARRaycastManager>(FindObjectsInactive.Include);
        var placer = FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include);
        if (rayOrigin == null && placer != null) rayOrigin = placer.rayOrigin;
        if (head == null && Camera.main != null) head = Camera.main.transform;

        ExperienceStandDown.HudFollowsGaze();
        ExperienceStandDown.Gameplay(placer);
        ExperienceStandDown.HideOtherHudText(display);
        if (display != null) display.enabled = true;

        foreach (var r in new[] { triggerAction, aAction, bAction })
            if (r != null && r.action != null) r.action.Enable();

        RequestDepthPermission();
        BuildLiveVisuals();
    }

    /// <summary>
    /// Depth ray casts need SCENE_UNDERSTANDING_FINE. The manifest already
    /// declares it (the AR Raycast feature adds it); the user has to grant it.
    /// Plane casts work on COARSE, which FloorSetup asks for at startup.
    /// </summary>
    private void RequestDepthPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(FinePermission))
        {
            permissionState = "granted";
            return;
        }

        permissionState = "asking - accept the prompt in the headset";
        var cb = new PermissionCallbacks();
        cb.PermissionGranted += _ => { permissionState = "granted"; Debug.Log("[Distance] depth permission granted"); };
        cb.PermissionDenied += _ => { permissionState = "DENIED - depth hits will be empty, planes only"; Debug.LogWarning("[Distance] depth permission denied"); };
        Permission.RequestUserPermission(FinePermission, cb);
#else
        permissionState = "n/a (editor)";
#endif
    }

    // ------------------------------------------------------------ ray casts

    private void CastLive()
    {
        liveDepth = default;
        livePlane = default;
        if (raycastManager == null || rayOrigin == null) return;

        var ray = new Ray(rayOrigin.position, rayOrigin.forward);
        castFrames++;

        liveDepth = Cast(ray, TrackableType.Depth);
        // Planes: any detected plane, inside its outline. Estimated planes are
        // left out - they extend detected ones and would blur the comparison.
        livePlane = Cast(ray, TrackableType.PlaneWithinPolygon);

        if (liveDepth.valid) depthHitFrames++;
        if (livePlane.valid) planeHitFrames++;
    }

    private Hit Cast(Ray ray, TrackableType type)
    {
        hits.Clear();
        if (!raycastManager.Raycast(ray, hits, type) || hits.Count == 0) return default;

        // Results come sorted nearest first.
        var h = hits[0];
        return new Hit { valid = true, point = h.pose.position, rayDistance = h.distance };
    }

    private Vector3 HeadPosition => head != null ? head.position : Vector3.zero;

    // ---------------------------------------------------------------- input

    private bool Pressed(InputActionReference r)
    {
        if (r == null || r.action == null) return false;
        bool now = r.action.ReadValue<float>() > pressThreshold;
        was.TryGetValue(r, out bool before);
        was[r] = now;
        return now && !before;
    }

    private void HandleInput()
    {
        if (Pressed(triggerAction)) DropPin();
        if (Pressed(aAction)) ClearPins();
        if (Pressed(bAction)) UndoPin();
    }

    private void DropPin()
    {
        // Depth first: it hits whatever is actually there, cups included.
        Hit h = liveDepth.valid ? liveDepth : livePlane;
        if (!h.valid)
        {
            Debug.Log("[Distance] trigger: no depth or plane hit along the ray");
            return;
        }

        var pin = new Pin
        {
            Id = nextPinId++,
            Source = liveDepth.valid ? Source.Depth : Source.Plane,
            Point = h.point,
            HeadAt = HeadPosition,
            RayDistance = h.rayDistance,
        };
        pin.HeadDistance = Vector3.Distance(pin.HeadAt, pin.Point);
        pin.FromPrevious = pins.Count > 0 ? Vector3.Distance(pins[pins.Count - 1].Point, pin.Point) : -1f;

        BuildPinMarker(pin);
        pins.Add(pin);
        while (pins.Count > maxPins) RemovePin(0);

        Debug.LogFormat("[Distance] pin #{0} {1}: head->point {2:F2} m, controller->point {3:F2} m, " +
                        "floor-distance {4:F2} m{5}  (depth {6}, plane {7})",
            pin.Id, pin.Source, pin.HeadDistance, pin.RayDistance, FlatDistance(pin.HeadAt, pin.Point),
            pin.FromPrevious >= 0f ? string.Format(", from previous pin {0:F2} m", pin.FromPrevious) : "",
            liveDepth.valid ? Vector3.Distance(HeadPosition, liveDepth.point).ToString("F2") : "-",
            livePlane.valid ? Vector3.Distance(HeadPosition, livePlane.point).ToString("F2") : "-");
    }

    private void UndoPin()
    {
        if (pins.Count > 0) RemovePin(pins.Count - 1);
    }

    private void ClearPins()
    {
        while (pins.Count > 0) RemovePin(0);
    }

    private void RemovePin(int index)
    {
        var p = pins[index];
        if (p.Root != null) Destroy(p.Root);
        if (p.Mat != null) Destroy(p.Mat);
        pins.RemoveAt(index);
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // -------------------------------------------------------------- visuals

    private void BuildLiveVisuals()
    {
        var r = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        r.name = "DistanceReticle";
        Destroy(r.GetComponent<Collider>());   // must not eat UI/controller rays
        r.transform.SetParent(transform, false);
        r.transform.localScale = Vector3.one * 0.03f;
        if (markerMaterial != null)
        {
            reticleMat = new Material(markerMaterial);
            r.GetComponent<Renderer>().sharedMaterial = reticleMat;
        }
        reticle = r.transform;

        var lineGo = new GameObject("DistanceAimLine");
        lineGo.transform.SetParent(transform, false);
        aimLine = lineGo.AddComponent<LineRenderer>();
        aimLine.useWorldSpace = true;
        aimLine.positionCount = 2;
        aimLine.startWidth = 0.004f;
        aimLine.endWidth = 0.004f;
        if (reticleMat != null) aimLine.sharedMaterial = reticleMat;
    }

    private void DrawLive()
    {
        Hit h = liveDepth.valid ? liveDepth : livePlane;
        bool on = h.valid && rayOrigin != null;

        if (reticle != null && reticle.gameObject.activeSelf != on) reticle.gameObject.SetActive(on);
        if (aimLine != null) aimLine.enabled = on;
        if (!on) return;

        reticle.position = h.point;
        // Constant apparent size, so it stays visible at 5 m without filling the view at 0.5 m.
        reticle.localScale = Vector3.one * Mathf.Clamp(0.012f * Vector3.Distance(HeadPosition, h.point), 0.01f, 0.08f);
        if (reticleMat != null) reticleMat.color = liveDepth.valid ? depthColour : planeColour;

        aimLine.SetPosition(0, rayOrigin.position);
        aimLine.SetPosition(1, h.point);
    }

    private void BuildPinMarker(Pin pin)
    {
        var root = new GameObject("DistancePin_" + pin.Id);
        root.transform.SetParent(transform, false);
        root.transform.position = pin.Point;
        pin.Root = root;

        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dot.name = "Dot";
        Destroy(dot.GetComponent<Collider>());
        dot.transform.SetParent(root.transform, false);
        float d = Mathf.Max(0.3f, pin.HeadDistance);
        dot.transform.localScale = Vector3.one * Mathf.Clamp(0.015f * d, 0.015f, 0.09f);
        if (markerMaterial != null)
        {
            pin.Mat = new Material(markerMaterial) { color = pin.Source == Source.Depth ? depthColour : planeColour };
            dot.GetComponent<Renderer>().sharedMaterial = pin.Mat;
        }

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        var label = labelGo.AddComponent<TMPro.TextMeshPro>();
        label.alignment = TMPro.TextAlignmentOptions.Center;
        label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        label.fontStyle = TMPro.FontStyles.Bold;
        label.color = pin.Source == Source.Depth ? depthColour : planeColour;
        label.text = string.Format("#{0}  {1:F2} m", pin.Id, pin.HeadDistance);
        var rt = label.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = new Vector2(1.5f, 0.3f);
        pin.Label = label;
    }

    private void RefreshPinLabels()
    {
        Vector3 hp = HeadPosition;
        foreach (var p in pins)
        {
            if (p.Label == null) continue;
            float d = Vector3.Distance(hp, p.Point);
            p.Label.transform.position = p.Point + Vector3.up * Mathf.Clamp(0.04f * d, 0.05f, 0.25f);
            Vector3 look = p.Label.transform.position - hp;
            if (look.sqrMagnitude > 1e-6f) p.Label.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
            p.Label.fontSize = Mathf.Clamp(labelSizePerMetre * d, 0.12f, 1.2f);
        }
    }

    // ------------------------------------------------------------------ HUD

    private void Render()
    {
        if (display == null) return;
        Vector3 hp = HeadPosition;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("DISTANCE TEST   pinch = pin   A = clear   B = undo  (pad)");
        sb.AppendLine("depth permission: " + permissionState);
        if (raycastManager == null) sb.AppendLine("NO ARRaycastManager in the scene - nothing can be measured");
        if (rayOrigin == null) sb.AppendLine("NO ray origin - set rayOrigin (right controller aim)");

        sb.AppendLine(LiveLine("DEPTH", liveDepth, hp, depthHitFrames));
        sb.AppendLine(LiveLine("PLANE", livePlane, hp, planeHitFrames));
        if (liveDepth.valid && livePlane.valid)
            sb.AppendFormat("depth vs plane: {0:+0.00;-0.00} m\n",
                Vector3.Distance(hp, liveDepth.point) - Vector3.Distance(hp, livePlane.point));

        if (pins.Count == 0) sb.AppendLine("no pins yet - aim your right hand at something and pinch");
        for (int i = pins.Count - 1; i >= 0 && i >= pins.Count - 6; i--)
        {
            var p = pins[i];
            sb.AppendFormat("#{0,-2} {1,-5} head {2,5:F2} m  ctrl {3,5:F2} m  floor {4,5:F2} m{5}\n",
                p.Id, p.Source == Source.Depth ? "depth" : "plane",
                p.HeadDistance, p.RayDistance, FlatDistance(p.HeadAt, p.Point),
                p.FromPrevious >= 0f ? string.Format("  prev {0:F2} m", p.FromPrevious) : "");
        }

        display.text = sb.ToString();
    }

    private string LiveLine(string name, Hit h, Vector3 hp, int hitFrames)
    {
        int pct = castFrames > 0 ? Mathf.RoundToInt(100f * hitFrames / castFrames) : 0;
        return h.valid
            ? string.Format("{0}: {1,5:F2} m from head  ({2:F2} m from controller)   hit {3}% of frames",
                name, Vector3.Distance(hp, h.point), h.rayDistance, pct)
            : string.Format("{0}:   --   no hit   (hit {1}% of frames)", name, pct);
    }

    void OnDestroy()
    {
        ClearPins();
        if (reticleMat != null) Destroy(reticleMat);
    }

    /// <summary>Experience bar Restart: clear every pin.</summary>
    public void RestartExperience() { ClearPins(); }
}
