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
    [Tooltip("Clean, undisturbed crossing time. Ship speed is derived from this " +
             "and the arena size, so a course tuned in a small room behaves the " +
             "same on a full-size field. 6 s across 3 ft gives about 0.15 m/s.")]
    public float targetCrossingSeconds = 6f;

    [Header("Finish")]
    [Tooltip("Clamp margin as a fraction of arena size. The finish sits on the " +
             "far edge, so a tight clamp fights the player at the goal.")]
    [Range(0f, 0.5f)]
    public float clampMargin = 0.15f;
    [Tooltip("Optional burst played at the finish line on arrival.")]
    public ParticleSystem finishEffect;
    [Tooltip("Optional. Pulsed and recoloured on arrival.")]
    public Transform finishMarkerVisual;
    [Tooltip("Stop the vortex once the run is over.")]
    public bool calmTornadoOnFinish = true;
    public AudioSource audioSource;
    public AudioClip finishClip;

    [Header("Debug")]
    [Tooltip("Show live tornado numbers on the HUD. Turn off for demos.")]
    public bool showTelemetry = true;
    [Tooltip("Found automatically if left empty.")]
    public Tornado tornado;
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
    [Tooltip("How long the ship is held at the eye of the storm before being " +
             "returned. An instant teleport is invisible; a visible hold reads " +
             "as being eaten.")]
    public float captureHoldSeconds = 3f;
    [Tooltip("Spin rate while held, degrees per second.")]
    public float captureSpinDegreesPerSecond = 540f;
    [Tooltip("Seconds added to the clock. Cosmetic - there is no time limit.")]
    public float capturePenaltySeconds = 3f;
    [Tooltip("Where to restart after capture. Off = leave it at the centre.")]
    public bool respawnAtStart = true;

    /// <summary>How many times the vortex has caught the ship this run.</summary>
    public int CaptureCount { get; private set; }
    /// <summary>True while the ship is held at the eye of the storm.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>
    /// Called by the tornado when the ship reaches the inescapable core.
    ///
    /// An instant teleport was invisible - the ship simply reappeared at the
    /// start with no explanation. Instead the ship is held at the eye of the
    /// storm, spinning, with a visible countdown, and only then returned.
    /// </summary>
    public void HandleCapture()
    {
        if (!IsRunning || ship == null || IsHeld) return;
        StartCoroutine(CaptureSequence());
    }

    private System.Collections.IEnumerator CaptureSequence()
    {
        IsHeld = true;
        CaptureCount++;
        ElapsedSeconds += capturePenaltySeconds;

        // Freeze both the player's input and the vortex force, or the hold
        // fights whatever is still pushing the ship around.
        ship.MotionLocked = true;
        ship.acceptExternalForces = false;

        Vector3 hold = tornado != null ? tornado.transform.position : ship.transform.position;
        hold.y = floorY + hoverHeight;

        for (float t = captureHoldSeconds; t > 0f; t -= Time.deltaTime)
        {
            ship.transform.position = hold;
            // Spin while held: makes "swallowed" legible without any new art.
            ship.transform.Rotate(0f, captureSpinDegreesPerSecond * Time.deltaTime, 0f);
            SetHud(string.Format("SWALLOWED\nreturning in {0:F1}s", t));
            yield return null;
        }

        if (respawnAtStart)
        {
            ship.transform.position = new Vector3(
                startPoint.x, floorY + hoverHeight, startPoint.z);
            ship.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        ship.MotionLocked = false;
        ship.acceptExternalForces = true;
        IsHeld = false;
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

                // The finish sits exactly ON the far edge of the arena, so an
                // exact clamp fights the player right where they are trying to
                // arrive - it reads as being shoved away from the goal. Give
                // the clamp some margin so the boundary is never the target.
                float clamped = size * (1f + clampMargin);
                ship.playAreaSize = new Vector2(clamped, clamped);
            }
        }

        ElapsedSeconds = 0f;
        IsRunning = true;
        SetHud("Go!");
    }

    void Update()
    {
        if (!IsRunning || ship == null) return;

        // The capture sequence owns the ship's position and the HUD while it
        // runs; letting Update continue would fight it every frame.
        if (IsHeld) { ElapsedSeconds += Time.deltaTime; return; }

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

        if (showTelemetry)
        {
            // Tuning force by feel through a six-minute build cycle is guesswork.
            // These are the numbers that actually decide whether the vortex can
            // out-muscle the ship.
            if (tornado == null) tornado = FindAnyObjectByType<Tornado>();

            string t;
            if (tornado == null)
            {
                t = "NO TORNADO IN SCENE";
            }
            else
            {
                // Distance to the tornado is measured here as well as inside
                // Tornado itself, so a stale or unassigned bot reference shows
                // up as a disagreement between the two numbers.
                Vector3 tp = tornado.transform.position;
                tp.y = p.y;
                float actualDist = Vector3.Distance(p, tp);

                t = string.Format(
                    "{0}  d {1:F2}/{2:F2}  real {3:F2}\npull {4:F2}  ship {5:F2}  x{6:F1}  str {7:F2}",
                    tornado.State, tornado.LastDistance, tornado.influenceRadius,
                    actualDist, tornado.LastPull, RunSpeed,
                    RunSpeed > 0.001f ? tornado.LastPull / RunSpeed : 0f,
                    tornado.Strength);
            }

            SetHud(string.Format("{0:F1}s  {1:F2}m to go\n{2}", ElapsedSeconds, remaining, t));
        }
        else
        {
            SetHud(string.Format("{0:F1}s   {1:F2} m to go", ElapsedSeconds, remaining));
        }
    }

    private void Finish()
    {
        IsRunning = false;

        bool best = BestSeconds < 0f || ElapsedSeconds < BestSeconds;
        if (best) BestSeconds = ElapsedSeconds;

        // Calm the vortex. Leaving it spinning next to a "finished" message
        // reads as though the run is still going.
        if (calmTornadoOnFinish)
        {
            if (tornado == null) tornado = FindAnyObjectByType<Tornado>();
            if (tornado != null) tornado.enabled = false;
        }

        if (ship != null) ship.acceptExternalForces = false;

        if (finishEffect != null) finishEffect.Play();
        if (audioSource != null && finishClip != null) audioSource.PlayOneShot(finishClip);

        string penalty = CaptureCount > 0
            ? string.Format("\nswallowed {0}x  (+{1:F0}s)",
                            CaptureCount, CaptureCount * capturePenaltySeconds)
            : "";

        SetHud(string.Format("FINISHED  {0:F2}s{1}{2}",
                             ElapsedSeconds, best ? "   NEW BEST" : "", penalty));

        if (finishMarkerVisual != null) StartCoroutine(CelebrateMarker());

        OnFinished?.Invoke(ElapsedSeconds);
    }

    /// <summary>Brief pulse on the finish marker so arrival is unmistakable.</summary>
    private System.Collections.IEnumerator CelebrateMarker()
    {
        Vector3 baseScale = finishMarkerVisual.localScale;
        var renderers = finishMarkerVisual.GetComponentsInChildren<Renderer>();

        for (float t = 0f; t < 2.5f; t += Time.deltaTime)
        {
            float pulse = 1f + 0.5f * Mathf.Abs(Mathf.Sin(t * 5f));
            finishMarkerVisual.localScale =
                new Vector3(baseScale.x * pulse, baseScale.y, baseScale.z * pulse);

            // Flash between green and white rather than fading out, so it stays
            // readable against a bright passthrough background.
            Color c = Color.Lerp(new Color(0.2f, 1f, 0.4f, 0.6f),
                                 Color.white, Mathf.Abs(Mathf.Sin(t * 5f)));
            foreach (var r in renderers)
                if (r.material != null) r.material.color = c;

            yield return null;
        }

        finishMarkerVisual.localScale = baseScale;
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
