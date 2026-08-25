using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A scoring target: a virtual cover sitting over a real red solo cup.
///
/// The cover is opaque and oversized so the cup underneath is not visible from
/// normal viewing angles - Android XR cannot erase pixels from passthrough, so
/// hiding by covering is the only option available.
///
/// When knocked, the cover deliberately SHATTERS and reveals the cup. That
/// turns the one unavoidable failure of the illusion - a real cup tumbling out
/// from under a virtual object - into the punchline instead of a bug.
/// </summary>
public class Target : MonoBehaviour
{
    public enum Kind { Green, Red }

    /// <summary>All live targets. ArmController iterates this instead of using physics.</summary>
    public static readonly List<Target> Active = new List<Target>();

    [Header("Type")]
    public Kind kind = Kind.Green;
    public int greenPoints = 100;
    public int redPoints = -150;

    [Header("Visuals")]
    [Tooltip("The opaque cover hiding the real cup. Hidden on knock.")]
    public GameObject cover;
    [Tooltip("Optional shatter particle burst, played at the moment of impact.")]
    public ParticleSystem shatterEffect;
    [Tooltip("Optional floating +/- marker. Hidden on knock.")]
    public GameObject marker;
    [Tooltip("Contact shadow. Also hides the seam where tracking drift shows first.")]
    public GameObject blobShadow;

    [Header("Chassis Penalty")]
    [Tooltip("Red targets punish driving into them, not just being hit by the arm.")]
    public bool penaliseChassisContact = true;
    public float chassisContactRadius = 0.18f;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip knockClip;

    public bool IsKnocked { get; private set; }
    public int Points => kind == Kind.Green ? greenPoints : redPoints;

    /// <summary>Raised on any knock. Listen for scoring and HUD.</summary>
    public static event System.Action<Target> OnAnyKnocked;

    private GhostBot trackedBot;

    void OnEnable() { Active.Add(this); }
    void OnDisable() { Active.Remove(this); }

    void Start()
    {
        // Only used for the chassis-contact rule; the arm finds targets itself.
        trackedBot = FindFirstObjectByType<GhostBot>();
        ApplyKindVisuals();
    }

    void Update()
    {
        if (IsKnocked || !penaliseChassisContact || kind != Kind.Red) return;
        if (trackedBot == null) return;

        Vector3 delta = trackedBot.transform.position - transform.position;
        delta.y = 0f;
        if (delta.magnitude <= chassisContactRadius)
            Knock(delta.normalized);
    }

    /// <summary>Knock this target over. Direction is the impact direction, world space.</summary>
    public void Knock(Vector3 impactDirection)
    {
        if (IsKnocked) return;
        IsKnocked = true;

        // Reveal: the cover breaks, exposing whatever is underneath. If a real
        // cup is there, it is now visible - which is the intended gag.
        if (cover != null) cover.SetActive(false);
        if (marker != null) marker.SetActive(false);
        if (blobShadow != null) blobShadow.SetActive(false);

        if (shatterEffect != null)
        {
            shatterEffect.transform.rotation =
                Quaternion.LookRotation(impactDirection.sqrMagnitude > 1e-6f
                    ? impactDirection : Vector3.forward);
            shatterEffect.Play();
        }

        if (audioSource != null && knockClip != null)
            audioSource.PlayOneShot(knockClip);

        OnAnyKnocked?.Invoke(this);
        StartCoroutine(DisableAfterEffect());
    }

    private IEnumerator DisableAfterEffect()
    {
        // Stay alive long enough for particles and audio to finish, then drop
        // out of Active so the arm stops considering it.
        float wait = 2f;
        if (shatterEffect != null) wait = Mathf.Max(wait, shatterEffect.main.duration + 0.5f);
        yield return new WaitForSeconds(wait);
        gameObject.SetActive(false);
    }

    /// <summary>Re-run when kind changes at runtime (tornado marker-stealing).</summary>
    public void ApplyKindVisuals()
    {
        if (marker == null) return;
        var renderers = marker.GetComponentsInChildren<Renderer>();
        Color c = kind == Kind.Green ? new Color(0.2f, 0.9f, 0.3f)
                                     : new Color(0.95f, 0.25f, 0.2f);
        foreach (var r in renderers)
            if (r.material != null) r.material.color = c;
    }

    /// <summary>Swap type at runtime - used by the tornado marker-steal mechanic.</summary>
    public void SetKind(Kind newKind)
    {
        kind = newKind;
        ApplyKindVisuals();
    }

    void OnDrawGizmosSelected()
    {
        if (!penaliseChassisContact || kind != Kind.Red) return;
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, chassisContactRadius);
    }
}
