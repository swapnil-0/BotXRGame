using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Standalone vision test: the headset camera finds cups and tracks them.
///
/// Stands everything else down - ship, arena, tornado, tags, robot - so the
/// only thing being tested is camera -> detector -> floor position -> track.
///
/// Activates by POLLING for the mode choice rather than checking it in Start().
/// Start() runs at scene load, before the player has picked anything, so a
/// Start-time mode check always sees the default. Link Test only works today
/// because its HUD object happens to be hidden before its Start runs; this
/// mode does not rely on that ordering.
///
/// Controls (right controller):
///   trigger  set the floor height from where you point
///   A        forget all cups          B  show / hide the camera preview
///   grip     open the tuner: stick up/down picks, left/right changes,
///            A saves, B resets
/// </summary>
public class CupDetectMode : MonoBehaviour
{
    [Header("Pipeline")]
    public PassthroughCamera cam;
    public CupDetector detector;
    public CupTracker tracker;

    [Header("HUD")]
    public TMPro.TextMeshProUGUI display;

    [Tooltip("Live camera image with the detector's boxes drawn on it. The " +
             "single most useful instrument here: it separates 'the model " +
             "does not see the cup' from 'it sees it but places it wrong'.")]
    public RawImage preview;

    [Header("Input")]
    public InputActionReference moveAction;     // right stick
    public InputActionReference triggerAction;  // set floor
    public InputActionReference aAction;
    public InputActionReference bAction;
    public InputActionReference gripAction;     // tuner
    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    [Header("Camera")]
    [Tooltip("Camera size this mode asks for, overriding PassthroughCamera's " +
             "serialized 640x480. At 640 a cup 4 m away is ~6 px wide - too " +
             "small to find, let alone read its pose. The sensor is 3000x3000; " +
             "the device picks the nearest size it supports and the HUD shows it.")]
    public int cameraWidth = 1280;
    public int cameraHeight = 960;

    [Tooltip("Where the floor-setting ray comes from. Taken from ArenaPlacer " +
             "when empty.")]
    public Transform rayOrigin;
    public ARRaycastManager raycastManager;

    public bool Active { get; private set; }

    private ARPlaneManager planeManager;

    private readonly List<RectTransform> boxPool = new List<RectTransform>();
    private bool tunerOpen;
    private int tunerIndex;
    private float stickRepeatAt;
    private readonly Dictionary<InputActionReference, bool> was = new Dictionary<InputActionReference, bool>();
    private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();

    // ----------------------------------------------------------- activation

    void Update()
    {
        if (!Active)
        {
            if (GameMode.Chosen && GameMode.IsCupDetect) Activate();
            return;
        }

        HandleInput();
        DrawPreview();
        Render();
    }

    private void Activate()
    {
        Active = true;
        Debug.Log("[CupDetect] mode active - standing everything else down");

        if (cam == null) cam = FindAnyObjectByType<PassthroughCamera>(FindObjectsInactive.Include);
        if (detector == null) detector = FindAnyObjectByType<CupDetector>(FindObjectsInactive.Include);
        if (tracker == null) tracker = FindAnyObjectByType<CupTracker>(FindObjectsInactive.Include);
        if (raycastManager == null) raycastManager = FindAnyObjectByType<ARRaycastManager>();

        var placer = FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include);
        if (rayOrigin == null && placer != null) rayOrigin = placer.rayOrigin;

        planeManager = FindAnyObjectByType<ARPlaneManager>();

        ExperienceStandDown.HudFollowsGaze();
        ExperienceStandDown.Gameplay(placer);
        ExperienceStandDown.HideOtherHudText(display, preview != null ? preview.transform : null);

        foreach (var r in new[] { moveAction, triggerAction, aAction, bAction, gripAction })
            if (r != null && r.action != null) r.action.Enable();

        LoadTuning();

        if (cam != null)
        {
            // Set at runtime: the scene has 640x480 serialized on the camera.
            cam.requestedWidth = cameraWidth;
            cam.requestedHeight = cameraHeight;
            cam.enabled = true;
            cam.Begin();
        }
        if (detector != null) { detector.enabled = true; detector.cam = cam; detector.Begin(); }
        if (tracker != null)
        {
            tracker.enabled = true;
            tracker.cam = cam;
            tracker.detector = detector;
            if (tracker.raycastManager == null) tracker.raycastManager = raycastManager;
        }

        if (preview != null) preview.gameObject.SetActive(true);
    }

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
        if (Pressed(gripAction)) tunerOpen = !tunerOpen;

        bool a = Pressed(aAction);
        bool b = Pressed(bAction);
        bool trig = Pressed(triggerAction);

        if (tunerOpen)
        {
            HandleTuner();
            if (a) SaveTuning();
            if (b) ResetTuning();
            return;
        }

        if (a && tracker != null) tracker.ClearAll();
        if (b && preview != null) preview.gameObject.SetActive(!preview.gameObject.activeSelf);
        if (trig) SetFloorFromPointer();
    }

    /// <summary>
    /// Floor height from where the controller points: an AR plane if one is
    /// hit, otherwise the controller's own height (rest it on the floor).
    /// </summary>
    private void SetFloorFromPointer()
    {
        if (tracker == null || rayOrigin == null) return;

        var ray = new Ray(rayOrigin.position, rayOrigin.forward);
        if (raycastManager != null &&
            raycastManager.Raycast(ray, hits, UnityEngine.XR.ARSubsystems.TrackableType.PlaneWithinPolygon))
        {
            tracker.SetFloor(hits[0].pose.position.y, "AR plane");
        }
        else
        {
            tracker.SetFloor(rayOrigin.position.y, "controller height");
        }
    }

    // ---------------------------------------------------------------- tuner

    private struct Param
    {
        public string name; public float step, min, max;
        public System.Func<float> get; public System.Action<float> set;
        public bool isToggle;
    }

    private Param[] parameters;

    private Param[] Params()
    {
        if (parameters != null) return parameters;
        parameters = new[]
        {
            new Param { name = "confidence",       step = 0.05f, min = 0.05f, max = 0.95f,
                        get = () => detector.scoreThreshold, set = v => detector.scoreThreshold = v },
            new Param { name = "require red",      isToggle = true,
                        get = () => detector.requireRed ? 1 : 0, set = v => detector.requireRed = v > 0.5f },
            new Param { name = "min red fraction", step = 0.05f, min = 0.05f, max = 0.9f,
                        get = () => detector.minRedFraction, set = v => detector.minRedFraction = v },
            new Param { name = "colour detect",    isToggle = true,
                        get = () => detector.colourDetect ? 1 : 0, set = v => detector.colourDetect = v > 0.5f },
            new Param { name = "flip image",       isToggle = true,
                        get = () => detector.invertFlip ? 1 : 0, set = v => detector.invertFlip = v > 0.5f },
            new Param { name = "layers / frame",   step = 5f, min = 5f, max = 200f,
                        get = () => detector.layersPerFrame, set = v => detector.layersPerFrame = Mathf.RoundToInt(v) },
            new Param { name = "latency (ms)",     step = 5f, min = 0f, max = 200f,
                        get = () => tracker.latencyMs, set = v => tracker.latencyMs = v },
            new Param { name = "camera pitch (deg)", step = 0.5f, min = -30f, max = 30f,
                        get = () => tracker.pitchOffsetDeg, set = v => tracker.pitchOffsetDeg = v },
            new Param { name = "camera yaw (deg)", step = 0.5f, min = -30f, max = 30f,
                        get = () => tracker.yawOffsetDeg, set = v => tracker.yawOffsetDeg = v },
            new Param { name = "fallback FOV (deg)", step = 1f, min = 40f, max = 130f,
                        get = () => cam.fallbackHorizontalFov, set = v => cam.fallbackHorizontalFov = v },
            new Param { name = "match radius (m)", step = 0.05f, min = 0.1f, max = 0.8f,
                        get = () => tracker.gateRadius, set = v => tracker.gateRadius = v },
        };
        return parameters;
    }

    private void HandleTuner()
    {
        if (detector == null || tracker == null || cam == null) return;
        var ps = Params();

        Vector2 s = (moveAction != null && moveAction.action != null)
            ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;
        if (s.magnitude < 0.5f) { stickRepeatAt = 0f; return; }
        if (Time.unscaledTime < stickRepeatAt) return;
        stickRepeatAt = Time.unscaledTime + 0.22f;   // auto-repeat while held

        if (Mathf.Abs(s.y) > Mathf.Abs(s.x))
        {
            tunerIndex = (tunerIndex + (s.y > 0 ? -1 : 1) + ps.Length) % ps.Length;
            return;
        }

        var p = ps[tunerIndex];
        if (p.isToggle) { p.set(p.get() > 0.5f ? 0 : 1); return; }
        p.set(Mathf.Clamp(p.get() + Mathf.Sign(s.x) * p.step, p.min, p.max));
    }

    private const string Prefix = "CupDetect.";

    private void SaveTuning()
    {
        foreach (var p in Params()) PlayerPrefs.SetFloat(Prefix + p.name, p.get());
        PlayerPrefs.Save();
        Debug.Log("[CupDetect] tuning saved");
    }

    private void LoadTuning()
    {
        if (detector == null || tracker == null || cam == null) return;
        foreach (var p in Params())
            if (PlayerPrefs.HasKey(Prefix + p.name)) p.set(PlayerPrefs.GetFloat(Prefix + p.name));
    }

    private void ResetTuning()
    {
        foreach (var p in Params()) PlayerPrefs.DeleteKey(Prefix + p.name);
        PlayerPrefs.Save();
        Debug.Log("[CupDetect] tuning reset - takes effect on next launch");
    }

    // -------------------------------------------------------------- preview

    private void DrawPreview()
    {
        if (preview == null || !preview.gameObject.activeSelf || cam == null || detector == null) return;

        if (cam.Texture != null && preview.texture != cam.Texture) preview.texture = cam.Texture;

        // Show exactly what the model saw, flip included - so an upside-down
        // preview means an upside-down model input, fixable from the tuner.
        preview.uvRect = detector.FlipApplied ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1);

        var dets = detector.Latest;
        while (boxPool.Count < dets.Count) boxPool.Add(MakeBox());

        for (int i = 0; i < boxPool.Count; i++)
        {
            bool on = i < dets.Count;
            boxPool[i].gameObject.SetActive(on);
            if (!on) continue;

            Rect b = dets[i].box;   // normalised, y down
            boxPool[i].anchorMin = new Vector2(Mathf.Clamp01(b.xMin), Mathf.Clamp01(1f - b.yMax));
            boxPool[i].anchorMax = new Vector2(Mathf.Clamp01(b.xMax), Mathf.Clamp01(1f - b.yMin));
            boxPool[i].offsetMin = Vector2.zero;
            boxPool[i].offsetMax = Vector2.zero;

            // Green = the model found it, cyan = only the colour pass did.
            var img = boxPool[i].GetComponent<Image>();
            if (img != null)
                img.color = dets[i].fromColour ? new Color(0.2f, 0.85f, 1f, 0.35f)
                                               : new Color(0.2f, 1f, 0.4f, 0.35f);
        }
    }

    private RectTransform MakeBox()
    {
        var go = new GameObject("Box", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(preview.transform, false);
        var img = go.GetComponent<Image>();
        img.color = new Color(0.2f, 1f, 0.4f, 0.35f);
        img.raycastTarget = false;
        return go.GetComponent<RectTransform>();
    }

    // ------------------------------------------------------------------ HUD

    private void Render()
    {
        if (display == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CUP DETECT   pinch = set floor   A = clear   B = preview   View = tune");
        sb.AppendLine(cam != null ? cam.Status : "camera: NOT WIRED");
        if (cam != null) sb.AppendLine(cam.Camera2Report);
        sb.AppendLine(detector != null ? detector.Status : "detector: NOT WIRED");
        if (detector != null && !string.IsNullOrEmpty(detector.ModelReport)) sb.AppendLine(detector.ModelReport);
        sb.AppendLine(tracker != null ? tracker.Status : "tracker: NOT WIRED");
        sb.AppendLine(planeManager != null
            ? "AR planes: " + planeManager.trackables.count + "  (0 = pinch falls back to hand height)"
            : "AR planes: no ARPlaneManager");
        if (tracker != null) sb.Append(tracker.BuildReport());

        if (tunerOpen && detector != null && tracker != null && cam != null)
        {
            sb.AppendLine("\nTUNER   stick up/down = pick   left/right = change   A = save   B = reset");
            var ps = Params();
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                string val = p.isToggle ? (p.get() > 0.5f ? "ON" : "off") : p.get().ToString("0.##");
                sb.AppendFormat("{0} {1,-20} {2}\n", i == tunerIndex ? ">" : " ", p.name, val);
            }
        }

        display.text = sb.ToString();
    }
}
