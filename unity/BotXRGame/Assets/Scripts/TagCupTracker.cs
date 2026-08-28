using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Routes tracked AprilTags: id 0 is the robot, every other id is a cup, and
/// each cup reports upright or toppled.
///
/// Tags rather than colour because the question is not "where is a red thing"
/// but "which cup is it and is it still standing". A marker answers both with
/// one signal - a colour blob gives a centroid and nothing about orientation.
///
/// Toppling is read from the angle between the tag's normal and world up. A
/// tag on the top face of a standing cup points up; tip the cup and the normal
/// swings toward horizontal.
///
/// The normal axis is configurable because ARFoundation providers disagree
/// about which local axis comes out of a tracked image, and guessing wrong
/// would report every cup as permanently toppled. Both angles are printed so
/// the right one can be chosen by looking rather than by argument.
/// </summary>
public class TagCupTracker : MonoBehaviour
{
    public enum NormalAxis { Up, Forward }

    [Header("Source")]
    public ARTrackedImageManager trackedImageManager;

    [Header("Identity")]
    [Tooltip("Marker id that is the robot. Everything else is treated as a cup.")]
    public int botMarkerId = 0;

    [Tooltip("Substring identifying the family in the reference image name, " +
             "used to parse the trailing id. Google names entries " +
             "'<Dictionary>-<id>', e.g. AprilTag_36H11-3.")]
    public string namePrefix = "AprilTag_36H11-";

    [Header("Topple test")]
    [Tooltip("Which local axis points out of the tag face for this provider.")]
    public NormalAxis normalAxis = NormalAxis.Up;

    [Tooltip("Degrees from vertical beyond which a cup counts as toppled. " +
             "45 is deliberately generous: a cup resting against another cup " +
             "is knocked over for gameplay purposes even if not flat.")]
    [Range(15f, 80f)]
    public float toppleAngle = 45f;

    [Tooltip("Seconds a cup may go unseen before it is reported LOST. A " +
             "toppled cup often hides its own tag, so LOST after being upright " +
             "is itself evidence of a topple - reported, not silently ignored.")]
    public float lostAfterSeconds = 1.5f;

    public class CupState
    {
        public int Id;
        public Vector3 Position;
        public float TiltDegrees;
        public bool Toppled;
        public bool EverUpright;
        public float LastSeen;
        public bool Visible;
    }

    private readonly Dictionary<int, CupState> cups = new Dictionary<int, CupState>();

    /// <summary>Bot tag pose, or null when the bot tag is not currently tracked.</summary>
    public Transform BotTag { get; private set; }
    public bool BotTracked { get; private set; }

    /// <summary>
    /// Every id seen this frame and where it is, bot included.
    ///
    /// Needed because "which physical tag is id 0" is not answerable from a
    /// count. The bot marker sat on an id-0 tag that was tracked but off to one
    /// side, and from inside the headset that is indistinguishable from the
    /// marker being broken.
    /// </summary>
    public readonly Dictionary<int, Vector3> SeenTags = new Dictionary<int, Vector3>();

    /// <summary>
    /// Change which id is the robot at runtime.
    ///
    /// Clears the cup table, because the previous bot id must now be treated
    /// as a cup and the new one must stop being one - keeping stale entries
    /// would leave a phantom cup that never topples and a real cup that never
    /// appears.
    /// </summary>
    public void SetBotMarkerId(int id)
    {
        if (id == botMarkerId) return;

        botMarkerId = id;
        cups.Clear();
        BotTag = null;
        BotTracked = false;
        Debug.LogFormat("[Cups] bot marker id is now {0}", id);
    }

    public IEnumerable<CupState> Cups => cups.Values;
    public int CupCount => cups.Count;

    public int ToppledCount
    {
        get
        {
            int n = 0;
            foreach (var c in cups.Values) if (c.Toppled) n++;
            return n;
        }
    }

    void Start()
    {
        if (trackedImageManager == null)
            trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();

        if (trackedImageManager == null)
            Debug.LogWarning("[Cups] no ARTrackedImageManager - cup tags cannot be read.");
    }

    void Update()
    {
        if (trackedImageManager == null) return;

        BotTracked = false;
        SeenTags.Clear();

        foreach (var img in trackedImageManager.trackables)
        {
            int id = ParseId(img.referenceImage.name);
            if (id < 0) continue;

            bool tracking = img.trackingState == TrackingState.Tracking;

            if (tracking) SeenTags[id] = img.transform.position;

            if (id == botMarkerId)
            {
                if (tracking) { BotTag = img.transform; BotTracked = true; }
                continue;
            }

            if (!tracking) continue;

            if (!cups.TryGetValue(id, out var c))
            {
                c = new CupState { Id = id };
                cups[id] = c;
                Debug.LogFormat("[Cups] cup {0} first seen", id);
            }

            Vector3 normal = normalAxis == NormalAxis.Up
                ? img.transform.up : img.transform.forward;

            c.Position = img.transform.position;
            c.TiltDegrees = Vector3.Angle(normal, Vector3.up);
            c.LastSeen = Time.time;
            c.Visible = true;

            bool nowToppled = c.TiltDegrees > toppleAngle;

            if (!nowToppled) c.EverUpright = true;

            if (nowToppled != c.Toppled)
            {
                c.Toppled = nowToppled;
                Debug.LogFormat("[Cups] cup {0} {1} (tilt {2:F0} deg)",
                    id, nowToppled ? "TOPPLED" : "upright", c.TiltDegrees);
            }
        }

        // Age out cups we have stopped seeing.
        foreach (var c in cups.Values)
            if (Time.time - c.LastSeen > lostAfterSeconds) c.Visible = false;
    }

    /// <summary>
    /// Pull the trailing integer out of a reference image name.
    /// Returns -1 when the name does not match the expected family.
    /// </summary>
    private int ParseId(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;

        int dash = name.LastIndexOf('-');
        if (dash < 0 || dash == name.Length - 1) return -1;

        if (!string.IsNullOrEmpty(namePrefix) && !name.StartsWith(namePrefix))
        {
            // Still try the trailing number: the family string in the database
            // may not match namePrefix exactly, and refusing to parse would
            // silently drop every cup.
            if (!int.TryParse(name.Substring(dash + 1), out int loose)) return -1;
            return loose;
        }

        return int.TryParse(name.Substring(dash + 1), out int id) ? id : -1;
    }

    /// <summary>One compact line per cup for the debug panel.</summary>
    public string BuildReport()
    {
        if (trackedImageManager == null) return "cups: no image manager";
        if (cups.Count == 0) return "cups: none seen yet";

        var sb = new System.Text.StringBuilder();

        var ids = new List<int>(cups.Keys);
        ids.Sort();

        foreach (int id in ids)
        {
            var c = cups[id];
            string state = !c.Visible
                ? (c.EverUpright ? "LOST (was upright)" : "LOST")
                : (c.Toppled ? "TOPPLED" : "upright");

            sb.AppendFormat("  #{0}  {1,-18} tilt {2:F0}\n", id, state, c.TiltDegrees);
        }

        return sb.ToString();
    }
}
