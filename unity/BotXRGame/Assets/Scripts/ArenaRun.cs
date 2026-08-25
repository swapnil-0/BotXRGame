using System;
using UnityEngine;

/// <summary>
/// The run itself: cross the arena, fight the tornado, reach the finish line.
///
/// Scored as a TIME TRIAL rather than pass/fail, and that is a deliberate
/// consequence of how force fields behave. Simulation showed that a vortex with
/// no capture rule cannot actually stop a player from arriving - they get pushed
/// around, correct, and eventually get there. Success/failure would therefore
/// always read "success" and the tornado would feel pointless.
///
/// Time is the honest metric. The same simulation put a swirl-dominant tornado
/// at roughly a 1.5x slowdown over a clean crossing, which is very visible on a
/// clock and rewards waiting for a lull before committing.
/// </summary>
public class ArenaRun : MonoBehaviour
{
    [Header("References")]
    public GhostBot ship;
    public TMPro.TextMeshProUGUI hudText;

    [Header("Rules")]
    [Tooltip("How close to the finish midpoint counts as arriving.")]
    public float finishRadius = 0.10f;
    [Tooltip("Keep the ship inside the arena footprint.")]
    public bool clampToArena = true;

    [Header("Ship Tuning")]
    // Speed is DERIVED from arena size rather than fixed, so a course tuned in
    // a small room behaves identically on the full-size field. A fixed 0.10 m/s
    // gives a 9 s crossing at 3 ft but 24 s at 8 ft - the same numbers produce
    // completely different games.
    [Tooltip("How long a clean, undisturbed crossing should take. Steering " +
             "around the tornado adds roughly 10-20% on top. Ship speed is " +
             "computed from this and the arena size.")]
    public float targetCrossingSeconds = 9f;
    [Tooltip("Uncheck to drive the ship at whatever speed GhostBot is set to.")]
    public bool overrideShipSpeed = true;

    /// <summary>Speed actually applied on the last Begin, m/s.</summary>
    public float RunSpeed { get; private set; }

    public bool IsRunning { get; private set; }
    public float ElapsedSeconds { get; private set; }
    public float BestSeconds { get; private set; } = -1f;

    public event Action<float> OnFinished;    // elapsed seconds

    private Vector3 startPoint, forward, finishPoint;
    private float arenaSize, floorY, hoverHeight;
    private Transform playAreaAnchor;

    [Header("Tornado Capture")]
    [Tooltip("Seconds added to the clock when the vortex swallows the ship.")]
    public float capturePenaltySeconds = 3f;
    [Tooltip("Where to restart after capture. Off = back to the start line.")]
    public bool respawnAtStart = true;

    /// <summary>How many times the vortex has caught the ship this run.</summary>
    public int CaptureCount { get; private set; }

    /// <summary>
    /// Called by the tornado when the ship reaches the inescapable core.
    /// Without this the ship is simply stuck forever, since the pull there
    /// exceeds the ship's top speed by design.
    /// </summary>
    public void HandleCapture()
    {
        if (!IsRunning || ship == null) return;

        CaptureCount++;
        ElapsedSeconds += capturePenaltySeconds;

        if (respawnAtStart)
        {
            ship.transform.position = new Vector3(
                startPoint.x, floorY + hoverHeight, startPoint.z);
            ship.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        SetHud(string.Format("Swallowed!  +{0:F0}s penalty", capturePenaltySeconds));
    }

    /// <summary>Called by ArenaPlacer once the arena is committed.</summary>
    public void Begin(Vector3 origin, Vector3 fwd, float size, float y, float hover)
    {
        startPoint = origin;
        forward = fwd.normalized;
        arenaSize = size;
        floorY = y;
        hoverHeight = hover;
        finishPoint = origin + forward * size;

        // Scale speed to the arena so the crossing always takes about the same
        // time, whether this is a 3 ft test square or the full 8 ft field.
        RunSpeed = arenaSize / Mathf.Max(targetCrossingSeconds, 0.5f);

        if (ship != null)
        {
            ship.transform.position = new Vector3(origin.x, y + hover, origin.z);
            ship.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            ship.acceptExternalForces = true;

            if (overrideShipSpeed) ship.linearSpeed = RunSpeed;

            // ArenaPlacer already constrains placement to clear floor, so the
            // play area is exactly the validated rectangle.
            //
            // Use a dedicated anchor object, NOT this transform. The preview
            // quad, outline and finish marker are children of this object, so
            // moving it to the arena centre drags them off the positions
            // ShowPreview just set for them.
            if (clampToArena)
            {
                if (playAreaAnchor == null)
                    playAreaAnchor = new GameObject("PlayAreaCentre").transform;

                playAreaAnchor.position = origin + forward * (size * 0.5f);
                playAreaAnchor.rotation = Quaternion.LookRotation(forward, Vector3.up);

                ship.playAreaCenter = playAreaAnchor;
                ship.playAreaSize = new Vector2(size, size);
            }
        }

        ElapsedSeconds = 0f;
        IsRunning = true;
        SetHud("Go!");
    }

    void Update()
    {
        if (!IsRunning || ship == null) return;

        ElapsedSeconds += Time.deltaTime;

        // Hold the ship at hover height; the tornado only acts in the plane,
        // but clamping keeps it from creeping vertically over a long run.
        Vector3 p = ship.transform.position;
        p.y = floorY + hoverHeight;
        ship.transform.position = p;

        Vector3 toFinish = finishPoint - p;
        toFinish.y = 0f;
        float remaining = toFinish.magnitude;

        if (remaining <= finishRadius)
        {
            Finish();
            return;
        }

        SetHud(string.Format("{0:F1}s   {1:F2} m to go", ElapsedSeconds, remaining));
    }

    private void Finish()
    {
        IsRunning = false;

        bool best = BestSeconds < 0f || ElapsedSeconds < BestSeconds;
        if (best) BestSeconds = ElapsedSeconds;

        SetHud(string.Format("Finished in {0:F2}s{1}",
                             ElapsedSeconds, best ? "   NEW BEST" : ""));
        OnFinished?.Invoke(ElapsedSeconds);
    }

    /// <summary>Wire to a button to run the same course again.</summary>
    public void Restart()
    {
        if (arenaSize <= 0f) return;
        Begin(startPoint, forward, arenaSize, floorY, hoverHeight);
    }

    private void SetHud(string s)
    {
        if (hudText != null) hudText.text = s;
    }

    void OnDrawGizmosSelected()
    {
        if (arenaSize <= 0f) return;
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(finishPoint, finishRadius);
    }
}
