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
    [Tooltip("Shown in the debug line for comparison against nearest cup.")]
    public float cupRadiusForDisplay = 0.28f;
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

        // Face back down the course toward the player, then tilt the top away
        // so a low board angles up into their eyeline.
        boardRoot.rotation = Quaternion.LookRotation(-forward.normalized, Vector3.up)
                             * Quaternion.Euler(-tiltDegrees, 0f, 0f);

        boardRoot.gameObject.SetActive(true);
        placed = true;
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

            if (tornado == null) tornado = FindAnyObjectByType<Tornado>();
            if (ship == null) ship = FindAnyObjectByType<GhostBot>();

            string t = tornado == null
                ? "no tornado"
                : string.Format("{0} d {1:F2}/{2:F2} pull {3:F2}",
                    tornado.State, tornado.LastDistance, tornado.influenceRadius,
                    tornado.LastPull);

            // Cup diagnostics: distinguishes "never spawned" from "spawned in
            // the wrong place" from "ship reference is null", which all look
            // identical from inside the headset.
            string cups;
            if (ship == null)
            {
                cups = "cups: NO SHIP";
            }
            else
            {
                float near = CollectibleCup.NearestDistanceTo(ship.transform.position);
                cups = string.Format("cups active {0}  nearest {1}  need <{2:F2}",
                    CollectibleCup.Remaining,
                    float.IsInfinity(near) ? "--" : near.ToString("F2"),
                    cupRadiusForDisplay);
            }

            debugText.text = t + "\n" + cups;
        }
    }
}
