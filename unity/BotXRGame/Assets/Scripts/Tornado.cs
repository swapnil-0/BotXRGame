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

    [Header("Force - absolute")]
    // Originally these were multiples of ship speed. That was a mistake: a
    // faster ship got a proportionally stronger vortex, so the player's
    // authority never changed and the tornado could never overwhelm them no
    // matter how high the multiplier went. Absolute metres per second lets the
    // vortex genuinely out-muscle the ship near the centre.
    [Tooltip("Inward pull at dead centre, metres per second. This should be " +
             "several times the ship's speed or the core is not a threat.")]
    public float suckMetersPerSecond = 0.6f;
    [Tooltip("Tangential push at dead centre, metres per second. Provides the " +
             "sense of rotation; keep below suck or the ship just orbits.")]
    public float swirlMetersPerSecond = 0.3f;

    [Header("Force - legacy, relative to ship speed")]
    [Tooltip("Use the multiples below instead of the absolute values above.")]
    public bool useSpeedMultiples = false;
    // Absolute m/s values do not transfer between ship speeds. At 0.2 m/s a
    // 0.85 m/s swirl is a fun shove; at 0.10 m/s the same number is eight times
    // the player's authority and most starts become unwinnable. Scaling to ship
    // speed keeps the game playable whatever the ship is tuned to.
    [Tooltip("Tangential push, as a multiple of ship speed. Produces orbiting " +
             "and the visual sense of rotation, but on its own it never drags " +
             "the ship inward.")]
    public float swirlSpeedMultiple = 0.6f;
    [Tooltip("Inward pull, as a multiple of ship speed. This is what makes the " +
             "vortex feel like it is grabbing you. At 1.0 the pull equals the " +
             "ship's top speed at dead centre, so escape means driving straight " +
             "out; above ~1.2 the centre becomes a trap with no way out.")]
    public float suckSpeedMultiple = 2.5f;
    [Tooltip("Fallback ship speed if the bot cannot be read.")]
    public float assumedShipSpeed = 0.10f;
    [Tooltip("No effect beyond this radius. Set from arena size by ArenaPlacer.")]
    public float influenceRadius = 0.41f;
    [Range(0.2f, 4f)]
    [Tooltip("Shape of the falloff. 1 is linear; higher is gentler at the rim " +
             "and steeper toward the centre. At 1.5 with the default forces, " +
             "the pull overtakes ship speed at about 60% of the radius.")]
    public float falloffExponent = 1.5f;

    [Header("Capture")]
    // A purely radial force cannot stop a ship driving straight through the
    // middle: it accelerates it in and decelerates it out by the same amount,
    // for no net effect. Only a pull stronger than the ship's top speed makes
    // the centre a genuine trap - and once it is a trap, there has to be a way
    // out, or the run soft-locks.
    [Tooltip("Ship is swallowed inside this fraction of the influence radius.")]
    [Range(0f, 0.5f)]
    public float captureRadiusFraction = 0.12f;
    [Tooltip("Off = the ship simply cannot escape the core, with no reset.")]
    public bool captureEnabled = true;

    /// <summary>Raised when the ship is swallowed. ArenaRun handles the reset.</summary>
    public event System.Action OnCaptured;
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

    // --- telemetry, read by the HUD ------------------------------------
    /// <summary>Distance from the ship to the centre, metres. Infinity if out of range.</summary>
    public float LastDistance { get; private set; } = float.PositiveInfinity;
    /// <summary>Inward pull applied on the last frame, m/s.</summary>
    public float LastPull { get; private set; }
    /// <summary>Ship speed as the tornado sees it, m/s.</summary>
    public float LastShipSpeed { get; private set; }
    /// <summary>What the tornado did last frame: NO BOT, outside, pulling, CORE.</summary>
    public string State { get; private set; } = "init";

    private bool captureLatched;

    void Start()
    {
        if (bot == null) bot = FindAnyObjectByType<GhostBot>();
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
        // Telemetry is written FIRST, before any early return. Putting it after
        // the returns meant the one case worth diagnosing - the tornado
        // deciding to do nothing - reported nothing at all.
        if (bot == null)
        {
            State = "NO BOT";
            LastDistance = float.PositiveInfinity;
            LastPull = 0f;
            return;
        }

        Vector3 delta = bot.transform.position - transform.position;
        delta.y = 0f;                                  // planar force only
        float d = delta.magnitude;

        LastDistance = d;
        LastShipSpeed = bot.linearSpeed;
        LastPull = 0f;

        if (d >= influenceRadius)
        {
            State = "outside";
            captureLatched = false;
            return;
        }

        if (captureEnabled && d <= influenceRadius * captureRadiusFraction)
        {
            State = "CORE";
            // Latch, or capture fires every frame and stacks penalties.
            if (!captureLatched)
            {
                captureLatched = true;
                OnCaptured?.Invoke();
            }
            return;
        }

        captureLatched = false;
        State = "pulling";

        if (d < 1e-4f) return;

        // Strongest at the centre, nothing at the edge. The exponent shapes how
        // quickly it dies off: below 1 keeps meaningful force out near the rim,
        // which is what makes the vortex feel like it grabs you on approach
        // rather than only misbehaving at the very centre.
        float falloff = Mathf.Pow(1f - (d / influenceRadius), falloffExponent);
        float s = Strength * falloff;

        Vector3 inward = -delta / d;
        Vector3 tangent = Vector3.Cross(Vector3.up, inward) * (clockwise ? 1f : -1f);

        float suck, swirl;
        if (useSpeedMultiples)
        {
            float shipSpeed = (bot.linearSpeed > 0.01f) ? bot.linearSpeed : assumedShipSpeed;
            suck = suckSpeedMultiple * shipSpeed;
            swirl = swirlSpeedMultiple * shipSpeed;
        }
        else
        {
            suck = suckMetersPerSecond;
            swirl = swirlMetersPerSecond;
        }

        bot.AddExternalVelocity(inward * (suck * s) + tangent * (swirl * s));

        LastPull = suck * s;
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
