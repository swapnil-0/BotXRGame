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
    [Tooltip("Applied on Begin. At 0.6 m/s a 3 ft crossing takes 1.5 s, which " +
             "is too short for a breathing tornado to matter. 0.2 m/s gives " +
             "about 4.6 s and makes the lulls worth waiting for.")]
    public float runSpeed = 0.20f;
    public bool overrideShipSpeed = true;

    public bool IsRunning { get; private set; }
    public float ElapsedSeconds { get; private set; }
    public float BestSeconds { get; private set; } = -1f;

    public event Action<float> OnFinished;    // elapsed seconds

    private Vector3 startPoint, forward, finishPoint;
    private float arenaSize, floorY, hoverHeight;

    /// <summary>Called by ArenaPlacer once the arena is committed.</summary>
    public void Begin(Vector3 origin, Vector3 fwd, float size, float y, float hover)
    {
        startPoint = origin;
        forward = fwd.normalized;
        arenaSize = size;
        floorY = y;
        hoverHeight = hover;
        finishPoint = origin + forward * size;

        if (ship != null)
        {
            ship.transform.position = new Vector3(origin.x, y + hover, origin.z);
            ship.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            ship.acceptExternalForces = true;

            if (overrideShipSpeed) ship.linearSpeed = runSpeed;

            // ArenaPlacer already constrains placement to clear floor, so the
            // play area is exactly the validated rectangle.
            if (clampToArena)
            {
                ship.playAreaCenter = transform;
                transform.position = origin + forward * (size * 0.5f);
                transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
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
