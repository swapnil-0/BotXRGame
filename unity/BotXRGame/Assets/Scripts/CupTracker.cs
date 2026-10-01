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
    public float markerHeight = 0.12f;

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
        public GameObject Marker;
        public Renderer MarkerRenderer;
        public TMPro.TextMeshPro Label;
    }

    public readonly List<Track> Tracks = new List<Track>();
    public int ConfirmedCount { get; private set; }

    /// <summary>Detections in the last result that could not be placed.</summary>
    public int UnplacedLast { get; private set; }
    public string Status { get; private set; } = "tracker: waiting";

    private int lastResultId = -1;
    private int nextId = 1;
    private readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
    private readonly List<Vector3> placed = new List<Vector3>();
    private readonly List<float> placedScore = new List<float>();

    // -------------------------------------------------------------- per frame

    void Update()
    {
        if (detector == null || cam == null) return;
        if (detector.ResultId == lastResultId) { RefreshMarkers(); return; }
        lastResultId = detector.ResultId;

        PlaceDetections();
        Associate();
        RefreshMarkers();

        Status = string.Format("tracker: {0} cups ({1} confirmed)  unplaced {2}  floor {3}{4:F2}  lat {5:F0}ms",
            Tracks.Count, ConfirmedCount, UnplacedLast,
            FloorCalibrated ? "" : "~", floorY, latencyMs);
    }

    /// <summary>Set the fallback floor height, e.g. from a trigger pull.</summary>
    public void SetFloor(float y)
    {
        floorY = y;
        FloorCalibrated = true;
        Debug.LogFormat("[Cups] floor set to {0:F3}", y);
    }

    /// <summary>Forget every cup - for re-laying the arena.</summary>
    public void ClearAll()
    {
        foreach (var t in Tracks) if (t.Marker != null) Destroy(t.Marker);
        Tracks.Clear();
        ConfirmedCount = 0;
    }

    // --------------------------------------------------------- localisation

    private void PlaceDetections()
    {
        placed.Clear();
        placedScore.Clear();
        UnplacedLast = 0;

        foreach (var det in detector.Latest)
        {
            // Bottom-centre of the box: where the cup stands on the floor.
            float u = det.box.center.x;
            float v = det.box.yMax;

            if (TryPixelToFloor(u, v, det.frameTime, out Vector3 p))
            {
                placed.Add(p);
                placedScore.Add(det.score);
            }
            else UnplacedLast++;
        }
    }

    /// <summary>
    /// Normalised image point (y down) to a world point on the floor.
    /// </summary>
    public bool TryPixelToFloor(float u, float v, float frameTime, out Vector3 world)
    {
        world = Vector3.zero;
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
                return true;
            }
        }

        // Plane y = floorY. Rays pointing up or nearly flat never land.
        if (ray.direction.y > -0.05f) return false;
        float t = (floorY - ray.origin.y) / ray.direction.y;
        if (t <= 0f || t > maxRange) return false;

        world = ray.origin + ray.direction * t;
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
                Debug.LogFormat("[Cups] new cup #{0} at {1:F2},{2:F2}", best.Id, p.x, p.z);
            }
            else
            {
                best.Position = Vector3.Lerp(best.Position, p, smoothing);
            }

            best.Hits++;
            best.InViewMisses = 0;
            best.LastSeen = now;
            best.Score = placedScore[i];
            if (!best.Confirmed && best.Hits >= confirmHits) best.Confirmed = true;
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
                Debug.LogFormat("[Cups] cup #{0} dropped after {1} in-view misses", t.Id, t.InViewMisses);
                if (t.Marker != null) Destroy(t.Marker);
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
                if (lose.Marker != null) Destroy(lose.Marker);
                Tracks.RemoveAt(j);
                merged = true;
            }
        }
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ---------------------------------------------------------------- markers

    private void RefreshMarkers()
    {
        foreach (var t in Tracks)
        {
            if (t.Marker == null) BuildMarker(t);

            // Root sits on the floor at the cup, unscaled; the cylinder child is
            // scaled, the label child is not.
            float y = FloorAt(t.Position);
            t.Marker.transform.position = new Vector3(t.Position.x, y, t.Position.z);

            float s = t.Confirmed ? 1f : 0.6f;
            var body = t.MarkerRenderer != null ? t.MarkerRenderer.transform : null;
            if (body != null)
            {
                body.localPosition = new Vector3(0f, markerHeight * 0.5f * s, 0f);
                body.localScale = new Vector3(markerRadius * 2f * s, markerHeight * 0.5f * s, markerRadius * 2f * s);
            }

            if (t.MarkerRenderer != null && t.MarkerRenderer.material != null)
                t.MarkerRenderer.material.color = t.Confirmed ? confirmedColour : tentativeColour;

            if (t.Label != null)
            {
                t.Label.text = t.Confirmed ? "#" + t.Id : "#" + t.Id + "?";
                t.Label.transform.localPosition = new Vector3(0f, markerHeight + 0.08f, 0f);
                var c = Camera.main;
                if (c != null)
                    t.Label.transform.rotation = Quaternion.LookRotation(
                        t.Label.transform.position - c.transform.position, Vector3.up);
            }
        }
    }

    private float FloorAt(Vector3 p) => FloorCalibrated || !useArPlanes ? floorY : p.y;

    private void BuildMarker(Track t)
    {
        // Unscaled root, so the label child is not stretched by the cylinder's
        // non-uniform scale. Destroying the root removes both.
        var root = new GameObject("CupMarker_" + t.Id);
        root.transform.SetParent(transform, false);

        var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        body.name = "Body";
        Destroy(body.GetComponent<Collider>());   // markers must not eat UI/controller rays
        body.transform.SetParent(root.transform, false);

        var r = body.GetComponent<Renderer>();
        if (markerMaterial != null) r.material = new Material(markerMaterial);
        t.Marker = root;
        t.MarkerRenderer = r;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        var label = labelGo.AddComponent<TMPro.TextMeshPro>();
        label.fontSize = 0.6f;
        label.alignment = TMPro.TextAlignmentOptions.Center;
        label.color = Color.white;
        var rt = label.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = new Vector2(0.4f, 0.12f);
        t.Label = label;
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
            sb.AppendFormat("  #{0,-2} {1,-9} at {2,5:F2},{3,5:F2}  hits {4,-3} seen {5:F1}s ago  conf {6:F2}\n",
                t.Id, t.Confirmed ? "TRACKED" : "tentative",
                t.Position.x, t.Position.z, t.Hits, now - t.LastSeen, t.Score);

        return sb.ToString();
    }
}
