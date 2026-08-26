using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A cup the ship collects by driving over it. Spawned by ArenaPlacer.
/// Kept separate from Target: Target models knock-down with penalty rules for
/// the arm game; this is a plain collect-on-contact pickup.
/// </summary>
public class CollectibleCup : MonoBehaviour
{
    public static readonly List<CollectibleCup> Active = new List<CollectibleCup>();
    public static int CollectedCount { get; private set; }
    public static int Remaining => Active.Count;
    public static event System.Action<CollectibleCup> OnCollected;

    // --- diagnostics, read by the score board --------------------------
    /// <summary>False if no GhostBot could be found - collection can never fire.</summary>
    public static bool ShipFound { get; private set; }

    /// <summary>
    /// Planar distance from a point to the nearest uncollected cup, computed on
    /// demand. Done as one query rather than each cup writing to a shared
    /// static, which raced depending on script execution order.
    /// </summary>
    public static float NearestDistanceTo(Vector3 p)
    {
        float best = float.PositiveInfinity;
        foreach (var c in Active)
        {
            if (c == null) continue;
            Vector3 d = c.transform.position - p;
            d.y = 0f;
            best = Mathf.Min(best, d.magnitude);
        }
        return best;
    }

    [Tooltip("Ship centre within this distance collects the cup.")]
    public float collectRadius = 0.28f;
    // 0.15 was a near-miss machine: the cup is 7 cm wide and the test runs
    // against the ship's ORIGIN, so "visually through it" while turning could
    // still pass 20 cm from the centre. 0.28 forgives that.

    private GhostBot ship;
    private bool collected;

    public static void ResetAll()
    {
        CollectedCount = 0;
        foreach (var c in Active.ToArray())
            if (c != null) Destroy(c.gameObject);
        Active.Clear();
    }

    void OnEnable() { if (!collected) Active.Add(this); }
    void OnDisable() { Active.Remove(this); }

    void Update()
    {
        if (collected) return;

        // Re-acquire every frame until found. The ship is deactivated during
        // arena selection, and FindAnyObjectByType skips inactive objects - so
        // a single lookup in Start can silently come back null forever.
        if (ship == null)
        {
            ship = FindAnyObjectByType<GhostBot>();
            ShipFound = ship != null;
            if (ship == null) return;
        }

        // Center, not transform.position - the ship model's pivot is ~0.6 m
        // clear of its own geometry, which is why driving visually over a cup
        // reported "nearest 0.63" and never collected.
        Vector3 d = ship.Center - transform.position;
        d.y = 0f;
        float dist = d.magnitude;

        if (dist <= collectRadius) Collect();
    }

    private void Collect()
    {
        collected = true;
        CollectedCount++;
        Active.Remove(this);
        OnCollected?.Invoke(this);
        Debug.Log("[Cup] collected at " + transform.position +
                  "  total " + CollectedCount);
        StartCoroutine(PopAndVanish());
    }

    private System.Collections.IEnumerator PopAndVanish()
    {
        Vector3 start = transform.position;
        for (float t = 0f; t < 0.35f; t += Time.deltaTime)
        {
            transform.position = start + Vector3.up * (t * 1.2f);
            transform.Rotate(0f, 720f * Time.deltaTime, 0f);
            transform.localScale *= 0.94f;
            yield return null;
        }
        gameObject.SetActive(false);
    }
}
