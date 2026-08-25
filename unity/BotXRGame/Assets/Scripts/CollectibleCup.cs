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
        // Destroy leftovers from a previous round.
        foreach (var c in Active.ToArray())
            if (c != null) Destroy(c.gameObject);
        Active.Clear();
    }

    void OnEnable() { if (!collected) Active.Add(this); }
    void OnDisable() { Active.Remove(this); }

    void Start() { ship = FindAnyObjectByType<GhostBot>(); }

    void Update()
    {
        if (collected || ship == null) return;

        Vector3 d = ship.transform.position - transform.position;
        d.y = 0f;
        if (d.magnitude <= collectRadius) Collect();
    }

    private void Collect()
    {
        collected = true;
        CollectedCount++;
        Active.Remove(this);
        OnCollected?.Invoke(this);
        // No particle asset exists yet, so the pickup feedback is the cup
        // popping upward briefly before vanishing - visible and free.
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
