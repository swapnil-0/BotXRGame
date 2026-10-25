using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Turns 2D cup detections into persistent, numbered cups on the floor.
///
/// Localisation: the bottom-centre of each box is where the cup meets the
/// floor. That pixel becomes a ray using the camera's intrinsics and the head
/// pose AT THE MOMENT THE FRAME WAS CAPTURED, and the ray is intersected with
/// the floor - a detected AR plane where available, otherwise a calibrated
/// floor height. One camera, no depth sensor needed, because cups stand on a
/// known surface.
///
/// Tracking: detections are matched to existing cups by distance on the floor.
/// A cup is only dropped when its spot is IN VIEW and it repeatedly is not
/// found there. Looking away does not delete anything - otherwise every head
/// turn would forget the arena, which is per-frame detection, not tracking.
/// </summary>
public class CupTracker : MonoBehaviour
{
    [Header("Wiring")]
    public CupDetector detector;
    public PassthroughCamera cam;

    [Tooltip("Optional. When present, rays hit real detected planes, which " +
             "copes with a sloping floor or cups on a table.")]
    public ARRaycastManager raycastManager;

    [Header("Camera relative to the head")]
    [Tooltip("Where the passthrough camera sits relative to the eye centre, " +
             "metres, head-local. Unknown for this headset - starts at zero " +
             "and is tuned in the headset if markers sit consistently off.")]
    public Vector3 cameraOffset = Vector3.zero;

    [Tooltip("Rotation of the camera relative to the head, degrees. Pitch " +
             "error shows as markers sliding toward/away from you; yaw as " +
             "markers shifted sideways.")]
    public float pitchOffsetDeg = 0f;
    public float yawOffsetDeg = 0f;
    public float rollOffsetDeg = 0f;

    [Tooltip("Milliseconds between the camera capturing a frame and Unity " +
             "receiving it. Tune by turning your head: if markers swing the " +
             "way you turn and settle back, this is too low.")]
    [Range(0f, 200f)] public float latencyMs = 40f;

    [Header("Floor")]
    [Tooltip("Floor height used when no AR plane is hit. Set by pointing at " +
             "the floor and pulling the trigger.")]
    public float floorY = 0f;
    public bool FloorCalibrated { get; private set; }

    public bool useArPlanes = true;

    [Tooltip("Ignore anything further than this. Long rays meet the floor at " +
             "a grazing angle, where a one-pixel error is tens of centimetres.")]
    public float maxRange = 5f;

    [Tooltip("Width a detection must have on the floor, metres, to be a cup. " +
             "A solo cup is about 0.1 m. Mostly for colour blobs: a red bin, a " +
             "dustpan or a red patch across the room is red but not cup-sized.")]
    public float minCupWidth = 0.05f;
    public float maxCupWidth = 0.18f;

    [Header("Tracking")]
    [Tooltip("A detection within this many metres of a cup is that cup.")]
    public float gateRadius = 0.30f;

    [Tooltip("How far each new detection pulls the cup's position, 0..1. " +
             "Low = steady, slow to follow a moved cup.")]
    [Range(0.05f, 1f)] public float smoothing = 0.35f;

    [Tooltip("Detections needed before a cup counts as real. Filters one-off " +
             "false positives.")]
    public int confirmHits = 3;

    [Tooltip("Consecutive in-view misses before a cup is dropped.")]
    public int dropAfterInViewMisses = 8;

    [Header("Markers")]
    [Tooltip("Assigned by the setup script as a material ASSET so the shader " +
             "is guaranteed to be in the build. A shader found at runtime with " +
             "Shader.Find can be stripped, and then the marker is invisible.")]
    public Material markerMaterial;
    public Color confirmedColour = new Color(0.2f, 1f, 0.4f, 0.75f);
    public Color tentativeColour = new Color(1f, 0.85f, 0.2f, 0.55f);
    public float markerRadius = 0.05f;

    [Tooltip("Length of the orientation arrow, metres. It runs from the cup's " +
             "base toward its mouth: up for an upright cup, down for an " +
             "upside-down one, along the floor for one on its side.")]
    public float arrowLength = 0.32f;

    [Tooltip("Detections in a row that must agree before a cup's pose changes, " +
             "so one odd frame does not flip the arrow.")]
    public int poseConfirmFrames = 2;

    [Tooltip("Draw markers for unconfirmed cups too. Off: a one-frame false " +
             "detection never appears, which on the first headset run was " +
             "hundreds of flickering markers.")]
    public bool showTentative = false;

    [Tooltip("Label text height per metre of distance, so a label reads the " +
             "same size near and far instead of filling the view up close.")]
    public float labelSizePerMetre = 0.22f;

    // ------------------------------------------------------------------ tracks

    public class Track
    {
        public int Id;
        public Vector3 Position;
        public int Hits;
        public int InViewMisses;
        public float LastSeen;
        public float Score;
        public bool Confirmed;
        public CupDetector.CupPose Pose;
        public CupDetector.CupPose PendingPose;
        public int PendingCount;

        /// <summary>Tipped cups: unit direction on the floor from base to mouth.</summary>
        public Vector3 FloorDir;

        public GameObject Marker;
        public Renderer MarkerRenderer;
        public LineRenderer Arrow;
        public Material ArrowMat;
        public TMPro.TextMeshPro Label;
    }

    public readonly List<Track> Tracks = new List<Track>();
    public int ConfirmedCount { get; private set; }

    /// <summary>Detections in the last result that could not be placed.</summary>
    public int UnplacedLast { get; private set; }

    /// <summary>Detections in the last result rejected as the wrong size for a cup.</summary>
    public int WrongSizeLast { get; private set; }

    /// <summary>Where the floor height came from: "AR plane", "controller height", or "default".</summary>
    public string FloorSource { get; private set; } = "default";

    public string Status { get; private set; } = "tracker: waiting";

    private int lastResultId = -1;
    private int nextId = 1;
    private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
    private readonly List<Vector3> placed = new List<Vector3>();
    private readonly List<float> placedScore = new List<float>();
    private readonly List<CupDetector.CupPose> placedPose = new List<CupDetector.CupPose>();
    private readonly List<Vector3> placedDir = new List<Vector3>();

    // -------------------------------------------------------------- per frame

    void Update()
    {
        if (detector == null || cam == null) return;
        if (detector.ResultId == lastResultId) { RefreshMarkers(); return; }
        lastResultId = detector.ResultId;

        PlaceDetections();
        Associate();
        RefreshMarkers();

        Status = string.Format("tracker: {0} cups ({1} confirmed)  unplaced {2}  wrong-size {3}  floor {4:F2} ({5})  lat {6:F0}ms",
            Tracks.Count, ConfirmedCount, UnplacedLast, WrongSizeLast,
            floorY, FloorSource, latencyMs);
    }

    /// <summary>Set the fallback floor height, e.g. from a trigger pull.</summary>
    public void SetFloor(float y, string source)
    {
        floorY = y;
        FloorCalibrated = true;
        FloorSource = source;
        Debug.LogFormat("[Cups] floor set to {0:F3} from {1}", y, source);
    }

    /// <summary>Forget every cup - for re-laying the arena.</summary>
    public void ClearAll()
    {
        foreach (var t in Tracks) DestroyMarker(t);
        Tracks.Clear();
        ConfirmedCount = 0;
    }

    // --------------------------------------------------------- localisation

    private void PlaceDetections()
    {
        placed.Clear();
        placedScore.Clear();
        placedPose.Clear();
        placedDir.Clear();
        UnplacedLast = 0;
        WrongSizeLast = 0;

        cam.GetStreamIntrinsics(out float fx, out _, out _, out _);

        foreach (var det in detector.Latest)
        {
            // Bottom-centre of the box: where the cup stands on the floor.
            float u = det.box.center.x;
            float v = det.box.yMax;

            if (!TryPixelToFloor(u, v, det.frameTime, out Vector3 p, out float dist))
            {
                UnplacedLast++;
                continue;
            }

            // Physical width from pixel width and distance. Skipped for boxes
            // cut off by the image edge, whose width is not the cup's.
            bool clipped = det.box.xMin <= 0.01f || det.box.xMax >= 0.99f;
            float widthM = det.box.width * cam.Width / Mathf.Max(1f, fx) * dist;
            if (!clipped && (widthM < minCupWidth || widthM > maxCupWidth))
            {
                WrongSizeLast++;
                continue;
            }

            Vector3 dir = Vector3.zero;
            if (det.pose == CupDetector.CupPose.Tipped &&
                TryPixelToFloor(det.baseUv.x, det.baseUv.y, det.frameTime, out Vector3 b) &&
                TryPixelToFloor(det.mouthUv.x, det.mouthUv.y, det.frameTime, out Vector3 m))
            {
                dir = m - b;
                dir.y = 0f;
                dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.zero;
            }

            placed.Add(p);
            placedScore.Add(det.score);
            placedPose.Add(det.pose);
            placedDir.Add(dir);
        }
    }

    /// <summary>
    /// Normalised image point (y down) to a world point on the floor.
    /// </summary>
    public bool TryPixelToFloor(float u, float v, float frameTime, out Vector3 world) =>
        TryPixelToFloor(u, v, frameTime, out world, out _);

    private bool TryPixelToFloor(float u, float v, float frameTime, out Vector3 world, out float distance)
    {
        world = Vector3.zero;
        distance = 0f;
        if (!TryCameraPose(frameTime - latencyMs * 0.001f, out Vector3 camPos, out Quaternion camRot))
            return false;

        cam.GetStreamIntrinsics(out float fx, out float fy, out float cx, out float cy);
        float x = u * cam.Width;
        float y = v * cam.Height;

        // Camera frame, Unity convention: +x right, +y up, +z forward. Image y
        // runs down, hence the sign.
        var dirCam = new Vector3((x - cx) / fx, -(y - cy) / fy, 1f).normalized;
        var ray = new Ray(camPos, camRot * dirCam);

        if (useArPlanes && raycastManager != null &&
            raycastManager.Raycast(ray, hits, TrackableType.PlaneWithinPolygon))
        {
            foreach (var h in hits)
            {
                // Horizontal surfaces only: a wall hit is not where a cup stands.
                if (Vector3.Dot(h.pose.up, Vector3.up) < 0.85f) continue;
                if (h.distance > maxRange) break;
                world = h.pose.position;
                distance = h.distance;
                return true;
            }
        }

        // Plane y = floorY. Rays pointing up or nearly flat never land.
        if (ray.direction.y > -0.05f) return false;
        float t = (floorY - ray.origin.y) / ray.direction.y;
        if (t <= 0f || t > maxRange) return false;

        world = ray.origin + ray.direction * t;
        distance = t;
        return true;
    }

    /// <summary>Camera pose at a time: head pose from history, then extrinsics.</summary>
    private bool TryCameraPose(float time, out Vector3 pos, out Quaternion rot)
    {
        if (!cam.TryGetHeadPose(time, out Vector3 hp, out Quaternion hr))
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            return false;
        }

        rot = hr * Quaternion.Euler(pitchOffsetDeg, yawOffsetDeg, rollOffsetDeg);
        pos = hp + hr * cameraOffset;
        return true;
    }

    /// <summary>
    /// Is this floor point inside the camera's current picture?
    ///
    /// Decides whether NOT seeing a cup is evidence it is gone. Out of view,
    /// absence means nothing.
    /// </summary>
    private bool InCurrentView(Vector3 world)
    {
        if (!TryCameraPose(Time.realtimeSinceStartup, out Vector3 cp, out Quaternion cr)) return false;

        Vector3 local = Quaternion.Inverse(cr) * (world - cp);
        if (local.z < 0.2f || local.z > maxRange) return false;

        cam.GetStreamIntrinsics(out float fx, out float fy, out float cx, out float cy);
        float x = cx + fx * local.x / local.z;
        float y = cy - fy * local.y / local.z;

        // 8% margin: a cup at the very edge is often cut off and missed.
        float mx = cam.Width * 0.08f, my = cam.Height * 0.08f;
        return x > mx && x < cam.Width - mx && y > my && y < cam.Height - my;
    }

    // --------------------------------------------------------------- tracking

    private void Associate()
    {
        var matched = new HashSet<Track>();
        float now = Time.realtimeSinceStartup;

        // Highest-confidence detections claim their cup first.
        var order = new List<int>();
        for (int i = 0; i < placed.Count; i++) order.Add(i);
        order.Sort((a, b) => placedScore[b].CompareTo(placedScore[a]));

        foreach (int i in order)
        {
            Vector3 p = placed[i];
            Track best = null;
            float bestD = gateRadius;

            foreach (var t in Tracks)
            {
                if (matched.Contains(t)) continue;
                float d = FlatDistance(t.Position, p);
                if (d < bestD) { bestD = d; best = t; }
            }

            if (best == null)
            {
                best = new Track { Id = nextId++, Position = p };
                Tracks.Add(best);
            }
            else
            {
                best.Position = Vector3.Lerp(best.Position, p, smoothing);
            }

            best.Hits++;
            best.InViewMisses = 0;
            best.LastSeen = now;
            best.Score = placedScore[i];
            if (!best.Confirmed && best.Hits >= confirmHits)
            {
                // Logged at confirmation, not creation: one-frame false hits
                // would otherwise flood the log (hundreds on the first run).
                best.Confirmed = true;
                Debug.LogFormat("[Cups] cup #{0} confirmed at {1:F2},{2:F2}  {3}",
                    best.Id, best.Position.x, best.Position.z, PoseName(best.Pose));
            }
            UpdatePose(best, placedPose[i], placedDir[i]);
            matched.Add(best);
        }

        // Unmatched cups: only a miss if we were looking right at them.
        for (int i = Tracks.Count - 1; i >= 0; i--)
        {
            var t = Tracks[i];
            if (matched.Contains(t)) continue;
            if (!InCurrentView(t.Position)) continue;

            t.InViewMisses++;

            // Unconfirmed tracks die fast: they are mostly one-off false hits.
            int limit = t.Confirmed ? dropAfterInViewMisses : 2;
            if (t.InViewMisses >= limit)
            {
                if (t.Confirmed)
                    Debug.LogFormat("[Cups] cup #{0} dropped after {1} in-view misses", t.Id, t.InViewMisses);
                DestroyMarker(t);
                Tracks.RemoveAt(i);
            }
        }

        MergeDuplicates();

        int c = 0;
        foreach (var t in Tracks) if (t.Confirmed) c++;
        ConfirmedCount = c;
    }

    /// <summary>
    /// Two tracks drifting onto the same cup - typically created during a fast
    /// head turn before latency is tuned - are folded into the older one.
    /// </summary>
    private void MergeDuplicates()
    {
        // Restart after every merge: removing from the list shifts indices, and
        // a handful of cups makes the rescan free.
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < Tracks.Count && !merged; i++)
            for (int j = i + 1; j < Tracks.Count && !merged; j++)
            {
                if (FlatDistance(Tracks[i].Position, Tracks[j].Position) > gateRadius * 0.5f) continue;

                // Tracks are appended in creation order, so i is the older one.
                var keep = Tracks[i];
                var lose = Tracks[j];
                keep.Hits += lose.Hits;
                keep.Confirmed |= lose.Confirmed;
                if (keep.Pose == CupDetector.CupPose.Unknown) keep.Pose = lose.Pose;
                DestroyMarker(lose);
                Tracks.RemoveAt(j);
                merged = true;
            }
        }
    }

    /// <summary>
    /// Change a cup's pose only after a few detections agree. Unknown readings
    /// are ignored rather than counted: a cup seen at a bad angle keeps the last
    /// pose it was sure of.
    /// </summary>
    private void UpdatePose(Track t, CupDetector.CupPose pose, Vector3 dir)
    {
        if (pose == CupDetector.CupPose.Tipped && dir != Vector3.zero)
            t.FloorDir = t.FloorDir == Vector3.zero ? dir : Vector3.Slerp(t.FloorDir, dir, 0.4f).normalized;

        if (pose == CupDetector.CupPose.Unknown) return;

        if (pose == t.PendingPose) t.PendingCount++;
        else { t.PendingPose = pose; t.PendingCount = 1; }

        bool first = t.Pose == CupDetector.CupPose.Unknown;
        if (pose != t.Pose && (first || t.PendingCount >= poseConfirmFrames))
        {
            t.Pose = pose;
            if (t.Confirmed) Debug.LogFormat("[Cups] cup #{0} is {1}", t.Id, PoseName(pose));
        }
    }

    public static string PoseName(CupDetector.CupPose p)
    {
        switch (p)
        {
            case CupDetector.CupPose.Upright: return "UPRIGHT";
            case CupDetector.CupPose.UpsideDown: return "UPSIDE DOWN";
            case CupDetector.CupPose.Tipped: return "TIPPED";
            default: return "pose ?";
        }
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ---------------------------------------------------------------- markers

    private static readonly Color UprightText = new Color(0.3f, 1f, 0.45f);
    private static readonly Color UpsideDownText = new Color(1f, 0.65f, 0.15f);
    private static readonly Color TippedText = new Color(1f, 0.3f, 0.25f);

    private void RefreshMarkers()
    {
        var c = Camera.main;

        foreach (var t in Tracks)
        {
            if (t.Marker == null) BuildMarker(t);

            bool show = t.Confirmed || showTentative;
            if (t.Marker.activeSelf != show) t.Marker.SetActive(show);
            if (!show) continue;

            // Root on the floor at the cup, unscaled; the disc child is scaled,
            // the label is not.
            float y = FloorAt(t.Position);
            Vector3 foot = new Vector3(t.Position.x, y, t.Position.z);
            t.Marker.transform.position = foot;

            if (t.MarkerRenderer != null)
            {
                float s = t.Confirmed ? 1f : 0.6f;
                var disc = t.MarkerRenderer.transform;
                disc.localPosition = new Vector3(0f, 0.003f, 0f);
                disc.localScale = new Vector3(markerRadius * 2f * s, 0.002f, markerRadius * 2f * s);
                if (t.MarkerRenderer.material != null)
                    t.MarkerRenderer.material.color = t.Confirmed ? confirmedColour : tentativeColour;
            }

            float labelHeight = DrawArrow(t, foot, c);

            if (t.Label != null)
            {
                t.Label.text = "#" + t.Id + " " + PoseName(t.Pose) + (t.Confirmed ? "" : "?");
                t.Label.color = t.Pose == CupDetector.CupPose.Upright ? UprightText
                              : t.Pose == CupDetector.CupPose.UpsideDown ? UpsideDownText
                              : t.Pose == CupDetector.CupPose.Tipped ? TippedText
                              : Color.white;
                t.Label.transform.position = foot + Vector3.up * labelHeight;
                if (c != null)
                {
                    Vector3 toLabel = t.Label.transform.position - c.transform.position;
                    t.Label.transform.rotation = Quaternion.LookRotation(toLabel, Vector3.up);
                    // Constant apparent size. 3.5 fixed filled the view at 1 m.
                    t.Label.fontSize = Mathf.Clamp(labelSizePerMetre * toLabel.magnitude, 0.12f, 1.2f);
                }
            }
        }
    }

    /// <summary>
    /// The arrow runs from the cup's base toward its mouth, tapering like the
    /// ship's command arrow, so the wide end is the base. Returns the height to
    /// put the label at, clear of the arrow.
    /// </summary>
    private float DrawArrow(Track t, Vector3 foot, Camera c)
    {
        var lr = t.Arrow;
        if (lr == null) return 0.2f;

        Vector3 from, to;
        switch (t.Pose)
        {
            case CupDetector.CupPose.Upright:
                from = foot;
                to = foot + Vector3.up * arrowLength;
                break;

            case CupDetector.CupPose.UpsideDown:
                from = foot + Vector3.up * arrowLength;
                to = foot + Vector3.up * 0.02f;
                break;

            case CupDetector.CupPose.Tipped:
                Vector3 dir = t.FloorDir;
                if (dir == Vector3.zero && c != null)
                {
                    // No floor direction yet: lie across the view rather than not at all.
                    dir = c.transform.right;
                    dir.y = 0f;
                    dir.Normalize();
                }
                Vector3 axis = foot + Vector3.up * 0.05f;
                from = axis - dir * (arrowLength * 0.25f);
                to = axis + dir * (arrowLength * 0.75f);
                break;

            default:
                lr.enabled = false;
                return 0.15f;
        }

        lr.enabled = true;
        lr.SetPosition(0, from);
        lr.SetPosition(1, to);
        float w = t.Confirmed ? 1f : 0.6f;
        lr.startWidth = 0.035f * w;
        lr.endWidth = 0.003f;
        return Mathf.Max(from.y, to.y) - foot.y + 0.08f;
    }

    private float FloorAt(Vector3 p) => FloorCalibrated || !useArPlanes ? floorY : p.y;

    private void BuildMarker(Track t)
    {
        // Unscaled root, so the label child is not stretched by the disc's
        // non-uniform scale. Destroying the root removes every child.
        var root = new GameObject("CupMarker_" + t.Id);
        root.transform.SetParent(transform, false);

        var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.name = "Disc";
        Destroy(disc.GetComponent<Collider>());   // markers must not eat UI/controller rays
        disc.transform.SetParent(root.transform, false);

        var r = disc.GetComponent<Renderer>();
        if (markerMaterial != null) r.material = new Material(markerMaterial);
        t.Marker = root;
        t.MarkerRenderer = r;

        var arrowGo = new GameObject("Arrow");
        arrowGo.transform.SetParent(root.transform, false);
        var lr = arrowGo.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.numCapVertices = 2;
        if (markerMaterial != null)
        {
            t.ArrowMat = new Material(markerMaterial) { color = confirmedColour };
            lr.sharedMaterial = t.ArrowMat;
        }
        lr.enabled = false;
        t.Arrow = lr;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        var label = labelGo.AddComponent<TMPro.TextMeshPro>();
        label.fontSize = 0.4f;   // resized every frame by distance
        label.fontStyle = TMPro.FontStyles.Bold;
        label.alignment = TMPro.TextAlignmentOptions.Center;
        label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        label.color = Color.white;
        var rt = label.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = new Vector2(1.5f, 0.3f);
        t.Label = label;
    }

    private static void DestroyMarker(Track t)
    {
        if (t.Marker != null) Destroy(t.Marker);
        if (t.MarkerRenderer != null && t.MarkerRenderer.material != null) Destroy(t.MarkerRenderer.material);
        if (t.ArrowMat != null) Destroy(t.ArrowMat);
    }

    /// <summary>One line per cup, for the HUD.</summary>
    public string BuildReport()
    {
        if (Tracks.Count == 0) return "no cups yet - look at a cup 1-3 m away";

        var sb = new System.Text.StringBuilder();
        float now = Time.realtimeSinceStartup;
        var sorted = new List<Track>(Tracks);
        sorted.Sort((a, b) => a.Id.CompareTo(b.Id));

        foreach (var t in sorted)
            sb.AppendFormat("  #{0,-2} {1,-9} {2,-11} at {3,5:F2},{4,5:F2}  hits {5,-3} seen {6:F1}s ago\n",
                t.Id, t.Confirmed ? "TRACKED" : "tentative", PoseName(t.Pose),
                t.Position.x, t.Position.z, t.Hits, now - t.LastSeen);

        return sb.ToString();
    }
}
