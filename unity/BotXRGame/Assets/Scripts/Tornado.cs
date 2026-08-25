using UnityEngine;

/// <summary>
/// A whirlpool that pushes the ship around.
///
/// Two force components, because they feel completely different:
///   SWIRL - tangential, shoves the ship sideways. This is what reads as
///           "tornado". It is the component the player sees and fights.
///   SUCK  - radial, drags the ship inward. Slows progress but is nearly
///           invisible; too much of it just feels like sluggish controls.
///
/// Simulation of a 3 ft crossing at 0.2 m/s found swirl-dominant settings give
/// the strongest felt effect: swirl 0.8 / suck 0.3 produces about 15 cm of
/// lateral drift in a 91 cm arena and roughly a 1.5x slowdown. Equivalent
/// suck-dominant settings cost the same time but only push the ship 5 cm, which
/// players read as the ship being broken rather than as weather.
///
/// Strength breathes on a sine so there are windows to run for. The period must
/// be comparable to the crossing time or the variation is never experienced -
/// at 0.6 m/s the crossing takes 1.5 s and no plausible period matters, which is
/// why the ship is slowed down for this game mode.
/// </summary>
public class Tornado : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Left empty, the first GhostBot in the scene is found automatically.")]
    public GhostBot bot;

    [Header("Force - expressed as a multiple of ship speed")]
    // Absolute m/s values do not transfer between ship speeds. At 0.2 m/s a
    // 0.85 m/s swirl is a fun shove; at 0.10 m/s the same number is eight times
    // the player's authority and most starts become unwinnable. Scaling to ship
    // speed keeps the game playable whatever the ship is tuned to.
    [Tooltip("Tangential push at full strength and dead centre, as a multiple " +
             "of ship speed. Around 0.9 is a strong but fair shove; above ~1.5 " +
             "the player loses authority near the centre.")]
    public float swirlSpeedMultiple = 0.9f;
    [Tooltip("Inward pull, as a multiple of ship speed. Keep well below swirl - " +
             "suck slows the ship without visibly moving it, which reads as " +
             "broken controls rather than as weather.")]
    public float suckSpeedMultiple = 0.4f;
    [Tooltip("Fallback ship speed if the bot cannot be read.")]
    public float assumedShipSpeed = 0.10f;
    [Tooltip("No effect beyond this radius. About a third of a 3 ft arena. " +
             "Larger forces a wider detour and a longer crossing.")]
    public float influenceRadius = 0.32f;
    [Tooltip("Clockwise when true.")]
    public bool clockwise = true;

    [Header("Breathing")]
    [Tooltip("Seconds for a full weak-strong-weak cycle. Comparable to the " +
             "crossing time, or the variation is never experienced.")]
    public float period = 8f;
    [Range(0f, 1f)]
    [Tooltip("Strength floor. Above about 0.3 there is no free window, so " +
             "standing still stops being a viable strategy and the player has " +
             "to steer the whole way.")]
    public float minStrength = 0.35f;
    [Tooltip("Phase offset, so multiple tornadoes are not synchronised.")]
    public float phaseOffset = 0f;

    [Header("Visuals")]
    [Tooltip("Scaled and spun in proportion to current strength.")]
    public Transform funnel;
    [Tooltip("Ring on the floor showing the influence radius.")]
    public Transform radiusRing;
    public float minVisualScale = 0.35f;
    public float spinDegreesPerSecond = 220f;
    public ParticleSystem particles;

    /// <summary>Current strength, 0 to 1. Drives audio, visuals and HUD.</summary>
    public float Strength { get; private set; }

    void Start()
    {
        if (bot == null) bot = FindFirstObjectByType<GhostBot>();
        if (radiusRing != null)
            radiusRing.localScale = new Vector3(influenceRadius * 2f, 1f, influenceRadius * 2f);
    }

    void Update()
    {
        float wave = 0.5f + 0.5f * Mathf.Sin(
            (Time.time / Mathf.Max(period, 0.01f)) * 2f * Mathf.PI + phaseOffset);
        Strength = Mathf.Lerp(minStrength, 1f, wave);

        UpdateVisuals();
        ApplyForce();
    }

    private void ApplyForce()
    {
        if (bot == null) return;

        Vector3 delta = bot.transform.position - transform.position;
        delta.y = 0f;                                  // planar force only
        float d = delta.magnitude;

        if (d >= influenceRadius || d < 1e-4f) return;

        // Linear falloff: strongest at the centre, nothing at the edge.
        float falloff = 1f - (d / influenceRadius);
        float s = Strength * falloff;

        Vector3 inward = -delta / d;
        Vector3 tangent = Vector3.Cross(Vector3.up, inward) * (clockwise ? 1f : -1f);

        // Scale to whatever the ship is currently capable of, so retuning the
        // ship does not silently make the course unwinnable.
        float shipSpeed = (bot.linearSpeed > 0.01f) ? bot.linearSpeed : assumedShipSpeed;
        float swirl = swirlSpeedMultiple * shipSpeed;
        float suck = suckSpeedMultiple * shipSpeed;

        bot.AddExternalVelocity(inward * (suck * s) + tangent * (swirl * s));
    }

    private void UpdateVisuals()
    {
        if (funnel != null)
        {
            float scale = Mathf.Lerp(minVisualScale, 1f, Strength);
            funnel.localScale = new Vector3(scale, funnel.localScale.y, scale);
            funnel.Rotate(0f, spinDegreesPerSecond * Strength * Time.deltaTime, 0f, Space.Self);
        }

        if (particles != null)
        {
            var emission = particles.emission;
            emission.rateOverTimeMultiplier = Mathf.Lerp(10f, 90f, Strength);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, influenceRadius);
    }
}
