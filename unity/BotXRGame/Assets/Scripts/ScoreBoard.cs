using UnityEngine;

/// <summary>
/// A low board standing just past the finish line, facing back down the course.
///
/// Deliberately separate from the in-headset HUD. The HUD is transient status
/// that follows the player; this is a fixed object in the world that you look
/// AT - so it can hold score, timing and debug together without competing for
/// the same screen space.
///
/// Positioned at runtime, because the arena does not exist until the player
/// places it.
/// </summary>
public class ScoreBoard : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Root of the score canvas. Moved and rotated at runtime.")]
    public Transform boardRoot;
    [Tooltip("Large headline: running time, or the finish result.")]
    public TMPro.TextMeshProUGUI headlineText;
    [Tooltip("Smaller body: cups, captures, best time.")]
    public TMPro.TextMeshProUGUI bodyText;
    [Tooltip("Optional monospace block for live telemetry.")]
    public TMPro.TextMeshProUGUI debugText;

    [Header("Placement")]
    [Tooltip("Metres beyond the finish marker, along the course direction.")]
    public float distanceBehindFinish = 0.35f;
    [Tooltip("Height of the board's centre above the floor. Low, so it sits in " +
             "view while the player is looking down at the arena.")]
    public float heightAboveFloor = 0.28f;
    [Tooltip("Degrees tilted back from vertical. A little tilt makes a low " +
             "board far easier to read from standing height.")]
    [Range(0f, 60f)]
    public float tiltDegrees = 25f;

    [Header("Options")]
    public bool showDebug = true;
    [Tooltip("Hidden until the arena is placed.")]
    public bool hideUntilPlaced = true;

    private ArenaRun run;
    private Tornado tornado;
    private GhostBot ship;
    private TrackedImageTagSource tagSource;
    private ShipTagFollower follower;
    [Tooltip("Fallback only. The live value is read from an actual cup, so " +
             "this cannot drift out of sync with the real collect radius the " +
             "way a hardcoded 0.28 did.")]
    public float cupRadiusForDisplay = 0.13f;
    private bool placed;

    void Awake()
    {
        run = FindAnyObjectByType<ArenaRun>();
        if (hideUntilPlaced && boardRoot != null) boardRoot.gameObject.SetActive(false);
    }

    /// <summary>
    /// Called by ArenaRun once the course exists. Sits the board just past the
    /// finish, facing back toward the start so the player reads it head-on
    /// while driving toward the goal.
    /// </summary>
    public void Place(Vector3 finishPoint, Vector3 forward, float floorY)
    {
        if (boardRoot == null) return;

        Vector3 pos = finishPoint + forward.normalized * distanceBehindFinish;
        pos.y = floorY + heightAboveFloor;
        boardRoot.position = pos;

        // A world-space Canvas draws its content on the +Z face and does no
        // back-face culling, so getting this backwards does not hide the board
        // - it shows the reverse side and every label reads mirrored, which is
        // exactly what the first headset capture showed. LookRotation(forward)
        // is what actually puts the readable face toward the start line.
        //
        // Tilt sign follows: with this yaw, +tilt leans the top away from the
        // player, which is what makes a knee-high board readable standing up.
        boardRoot.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up)
                             * Quaternion.Euler(tiltDegrees, 0f, 0f);

        boardRoot.gameObject.SetActive(true);
        placed = true;
    }

    /// <summary>
    /// Called by ArenaPlacer with a tornado it actually spawned.
    ///
    /// Without this the board picks a tornado with FindAnyObjectByType, which
    /// returns whichever one Unity happens to hit first - including a template
    /// left in the scene by the Editor builder. That is almost certainly why
    /// the readout showed influenceRadius 0.10 (the prefab default) instead of
    /// the arena-derived value the placer assigns.
    /// </summary>
    public void WatchTornado(Tornado t)
    {
        if (t != null) tornado = t;
    }

    /// <summary>
    /// Told by ArenaPlacer which GhostBot is the player's ship.
    ///
    /// FindAnyObjectByType cannot be trusted for this: the real ship is
    /// deactivated during placement and gets skipped, so the board latched
    /// onto a different, stationary GhostBot and reported ship 0.25,0.85 for
    /// an entire run while the visible ship flew around the arena.
    /// </summary>
    public void WatchShip(GhostBot s)
    {
        if (s != null) ship = s;
    }

    private readonly System.Text.StringBuilder sb = new System.Text.StringBuilder(256);

    /// <summary>
    /// Absolute world XZ for the ship and every live cup, so a stale object
    /// left over from edit time is obvious: it will sit at a coordinate
    /// nowhere near the arena, often (0.00, 0.00).
    /// </summary>
    private string BuildDebug()
    {
        if (tornado == null) tornado = FindAnyObjectByType<Tornado>();
        if (ship == null) ship = CollectibleCup.Ship;

        sb.Length = 0;

        // Tornado count matters: more than one per expected spawn means there
        // is a spare in the scene and the numbers above may describe it.
        int tornadoCount = FindObjectsByType<Tornado>(FindObjectsInactive.Include).Length;

        if (tornado == null)
        {
            sb.Append("no tornado\n");
        }
        else
        {
            Vector3 tp = tornado.transform.position;
            sb.AppendFormat("{0} d {1:F2}/{2:F2} pull {3:F2}   torn x{4} @ {5:F2},{6:F2}\n",
                tornado.State, tornado.LastDistance, tornado.influenceRadius,
                tornado.LastPull, tornadoCount, tp.x, tp.z);
        }

        if (ship == null)
        {
            sb.Append("SHIP NOT FOUND");
            return sb.ToString();
        }

        // Name and scene count included deliberately: a GhostBot count above 1
        // is what let the board silently report a decoy ship for a whole run.
        int shipCount = FindObjectsByType<GhostBot>(FindObjectsInactive.Include).Length;
        Vector3 c = ship.Center;

        // ext = world-space velocity being applied by something OTHER than the
        // stick. Added because "the ship stopped going where I pointed it and
        // it did not feel like the tornado" is unanswerable by eye: if ext is
        // ~0 the vortex is not involved and the cause is steering, and if it
        // is large the pull is real but not being communicated.
        float ext = ship.ExternalVelocity.magnitude;

        // cmd vs applied yaw rate. With the stick centred both should read
        // 0.000; an applied rate that lingers while cmd is zero is heading
        // drift being integrated frame after frame, and hdg is the total
        // error it has accumulated so far.
        float hdg = Vector3.SignedAngle(
            Vector3.forward, ship.transform.forward, Vector3.up);

        sb.AppendFormat("ship {0:F2},{1:F2}  ext {2:F2}  [{3}] x{4}\n",
            c.x, c.z, ext, ship.gameObject.name, shipCount);
        sb.AppendFormat("hdg {0:F0}  cmd {1:F3}  applied {2:F3} rad/s\n",
            hdg, ship.CommandedAngularZ, ship.AppliedAngularRate);

        // Tag line, only in AprilTag mode - it is noise otherwise. Prints the
        // detected pose, which is what tells you whether the marker is actually
        // being seen rather than the stand-in being followed.
        if (GameMode.IsAprilTag)
        {
            if (tagSource == null) tagSource = FindAnyObjectByType<TrackedImageTagSource>();
            sb.AppendFormat("tag {0}\n",
                tagSource != null ? tagSource.Status : "no TrackedImageTagSource");

            // Offset from the tag to the ship's visible centre, in world axes.
            // "In front rather than above" is a judgement about depth made
            // through passthrough, which has already been wrong twice in this
            // project. This turns it into three numbers: up should be the
            // hover height and the other two should be ~0.
            if (follower == null) follower = FindAnyObjectByType<ShipTagFollower>();
            if (follower != null && follower.tagTransform != null)
            {
                Vector3 d = c - follower.tagTransform.position;
                sb.AppendFormat("tag->ship  right {0:F2}  up {1:F2}  fwd {2:F2}  (hover {3:F2})\n",
                    d.x, d.y, d.z, follower.hoverHeight);
            }
        }

        if (CollectibleCup.Active.Count == 0)
        {
            sb.Append("no live cups");
            return sb.ToString();
        }

        int i = 1;
        foreach (var cup in CollectibleCup.Active)
        {
            if (cup == null) continue;
            Vector3 p = cup.transform.position;
            Vector3 d = p - c;
            d.y = 0f;
            sb.AppendFormat("cup{0} {1:F2},{2:F2}  d {3:F2} (need <{4:F2})\n",
                i++, p.x, p.z, d.magnitude, cup.collectRadius);
        }

        return sb.ToString();
    }

    void Update()
    {
        if (run == null) run = FindAnyObjectByType<ArenaRun>();
        if (run == null || (hideUntilPlaced && !placed)) return;

        int collected = CollectibleCup.CollectedCount;
        int total = collected + CollectibleCup.Remaining;

        if (headlineText != null)
        {
            headlineText.text = run.IsRunning
                ? string.Format("{0:F1}s", run.ElapsedSeconds)
                : (run.ElapsedSeconds > 0f
                    ? string.Format("FINISHED  {0:F2}s", run.ElapsedSeconds)
                    : "READY");
        }

        if (bodyText != null)
        {
            string best = run.BestSeconds >= 0f
                ? string.Format("best {0:F2}s", run.BestSeconds) : "best --";
            bodyText.text = string.Format(
                "cups {0}/{1}     swallowed {2}\n{3}",
                collected, total, run.CaptureCount, best);
        }

        if (debugText != null)
        {
            if (!showDebug) { debugText.text = ""; return; }
            debugText.text = BuildDebug();
        }
    }
}
