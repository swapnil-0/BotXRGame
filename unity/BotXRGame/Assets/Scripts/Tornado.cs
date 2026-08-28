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
    public float suckMetersPerSecond = 1.2f;
    [Tooltip("Tangential push at dead centre, metres per second. Swirl and suck " +
             "balance into a stable orbit: tangential force sustains a circular " +
             "path while radial pull tries to close it. Keep it well under suck " +
             "or the ship circles indefinitely instead of spiralling in.")]
    public float swirlMetersPerSecond = 0.25f;

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
    [Tooltip("Ship is swallowed inside this fraction of the influence radius. " +
             "At 0.12 of a 0.5 m radius the core was a 6 cm target, small " +
             "enough to orbit straight past without ever triggering.")]
    [Range(0f, 0.5f)]
    public float captureRadiusFraction = 0.22f;
    [Tooltip("Off = the ship simply cannot escape the core, with no reset.")]
    public bool captureEnabled = true;

    /// <summary>Raised when the ship is swallowed. ArenaRun handles the reset.</summary>
    public event System.Action OnCaptured;

    [Header("Patrol")]
    [Tooltip("Metres of side-to-side travel from the base position. 0 = static.")]
    public float patrolAmplitude = 0f;
    [Tooltip("Seconds for one full there-and-back cycle.")]
    public float patrolPeriod = 7f;
    [Tooltip("World-space direction of travel. Set by the spawner.")]
    public Vector3 patrolDirection = Vector3.right;
    [Tooltip("Phase offset so two tornadoes are not synchronised.")]
    public float patrolPhase = 0f;

    private Vector3 patrolBase;
    private bool patrolBaseSet;

    /// <summary>Configure side-to-side movement. Call after positioning.</summary>
    public void InitPatrol(Vector3 direction, float amplitude, float period, float phase)
    {
        patrolDirection = direction.normalized;
        patrolAmplitude = amplitude;
        patrolPeriod = Mathf.Max(period, 0.5f);
        patrolPhase = phase;
        patrolBase = transform.position;
        patrolBaseSet = true;
    }
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
    public float minStrength = 0.6f;

    [Tooltip("Floor under the distance falloff, as a fraction of full pull.\n\n" +
             "Without it the force reaches zero at the rim - the exact place " +
             "the player meets the tornado - so it could be brushed past with " +
             "nothing felt. At 0.4 the boundary itself has weight, which is " +
             "what makes the pull constant rather than a surprise at the core.")]
    [Range(0f, 1f)]
    public float edgePullFraction = 0.4f;
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
        // Same decoy hazard as the cups: FindAnyObjectByType would skip the
        // real ship while it is deactivated for placement and return a
        // stationary GhostBot instead - a vortex that silently tracks the
        // wrong target. Use the ship the placer registered.
        if (bot == null) bot = CollectibleCup.Ship;
        if (radiusRing != null)
            radiusRing.localScale = new Vector3(influenceRadius * 2f, 1f, influenceRadius * 2f);
    }

    void Update()
    {
        float wave = 0.5f + 0.5f * Mathf.Sin(
            (Time.time / Mathf.Max(period, 0.01f)) * 2f * Mathf.PI + phaseOffset);
        Strength = Mathf.Lerp(minStrength, 1f, wave);

        if (patrolAmplitude > 0f)
        {
            if (!patrolBaseSet) { patrolBase = transform.position; patrolBaseSet = true; }
            float s = Mathf.Sin((Time.time / patrolPeriod) * 2f * Mathf.PI + patrolPhase);
            transform.position = patrolBase + patrolDirection * (s * patrolAmplitude);
        }

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

        // Center, not transform.position: the ship's pivot sits well outside
        // its own mesh, so the origin was being tested against the funnel
        // while the visible ship was somewhere else entirely.
        Vector3 delta = bot.Center - transform.position;
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

        // Strongest at the centre, weakest at the rim - but never zero inside
        // the radius. With a plain falloff the pull vanished exactly where the
        // player first meets it, so the tornado could be skirted without
        // feeling anything and only became real at the core. edgePullFraction
        // puts a floor under it: crossing the boundary is now always felt.
        float falloff = Mathf.Pow(1f - (d / influenceRadius), falloffExponent);
        falloff = Mathf.Max(falloff, edgePullFraction);
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

    private Vector3 funnelBaseScale;
    private bool funnelBaseSet;

    /// <summary>Re-read the funnel's scale as the new breathing baseline.</summary>
    // Width of the funnel mesh at its ORIGINAL scale, measured once. Needed
    // because the radius can now be changed repeatedly at runtime, and scaling
    // relative to the current size compounds rounding every time - the funnel
    // would creep away from the danger zone over a tuning session.
    private float funnelUnitWidth = -1f;

    /// <summary>
    /// Set the influence radius and resize the ring and funnel to match.
    ///
    /// Single place that knows how the visuals relate to the radius. The
    /// placer and the in-headset tuner both call this, so the drawn funnel
    /// cannot drift from the real danger zone - which it already did once,
    /// rendering at 0.18 m while the radius was 0.146.
    /// </summary>
    /// <summary>
    /// The world velocity this tornado would apply at an arbitrary point.
    ///
    /// Split out so the pull can act on the REAL robot, whose position comes
    /// from a tracked tag rather than from a GhostBot. Same maths the ship
    /// feels, so both modes are pulled identically rather than by two
    /// implementations that drift apart.
    /// </summary>
    public Vector3 VelocityAt(Vector3 worldPos)
    {
        Vector3 delta = worldPos - transform.position;
        delta.y = 0f;
        float d = delta.magnitude;

        if (d < 1e-4f || d >= influenceRadius) return Vector3.zero;

        float falloff = Mathf.Pow(1f - (d / influenceRadius), falloffExponent);
        falloff = Mathf.Max(falloff, edgePullFraction);
        float s = Strength * falloff;

        Vector3 inward = -delta / d;
        Vector3 tangent = Vector3.Cross(Vector3.up, inward) * (clockwise ? 1f : -1f);

        return inward * (suckMetersPerSecond * s) + tangent * (swirlMetersPerSecond * s);
    }

    /// <summary>Sum of every live tornado's pull at a point.</summary>
    public static Vector3 TotalVelocityAt(Vector3 worldPos)
    {
        Vector3 sum = Vector3.zero;
        foreach (var t in FindObjectsByType<Tornado>(FindObjectsInactive.Include))
            if (t != null && t.isActiveAndEnabled) sum += t.VelocityAt(worldPos);
        return sum;
    }

    public void ApplyRadius(float radius)
    {
        influenceRadius = Mathf.Max(0.01f, radius);

        if (radiusRing != null)
        {
            float d = influenceRadius * 2f;
            radiusRing.localScale = new Vector3(d, radiusRing.localScale.y, d);
        }

        if (funnel != null)
        {
            if (funnelUnitWidth < 0f)
            {
                var r = funnel.GetComponentInChildren<Renderer>();
                float w = (r != null) ? r.bounds.size.x : 0f;
                float sx = Mathf.Abs(funnel.localScale.x) > 1e-4f ? funnel.localScale.x : 1f;
                // Width the mesh would have at localScale.x == 1.
                funnelUnitWidth = (w > 1e-4f) ? w / sx : 1f;
            }

            float target = influenceRadius * 2f;              // a diameter
            float s = target / Mathf.Max(funnelUnitWidth, 1e-4f);
            funnel.localScale = new Vector3(s, funnel.localScale.y, s);
            RefreshFunnelBaseScale();
        }
    }

    public void RefreshFunnelBaseScale()
    {
        if (funnel != null) { funnelBaseScale = funnel.localScale; funnelBaseSet = true; }
    }

    private void UpdateVisuals()
    {
        if (funnel != null)
        {
            if (!funnelBaseSet) RefreshFunnelBaseScale();

            // Breathe RELATIVE to the base scale. Writing an absolute 0.35-1.0
            // here was the bug that made every funnel balloon to a metre wide,
            // regardless of what the prefab or the spawner had set.
            float scale = Mathf.Lerp(minVisualScale, 1f, Strength);
            funnel.localScale = new Vector3(
                funnelBaseScale.x * scale, funnelBaseScale.y, funnelBaseScale.z * scale);
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
