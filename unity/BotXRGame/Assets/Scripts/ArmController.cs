using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Knock-down arm: raise, swing, hit whatever is in the swept arc, return.
///
/// Deliberately not a grasping arm. Grasping needs object pose estimation,
/// approach planning, grip force and carry stability - four hard problems.
/// A swing needs none of them, keeps the manipulator visibly in the demo, and
/// a miss is funny rather than broken.
///
/// Timings and geometry mirror bot_sim's arm state machine so the ghost and
/// the real robot feel the same. The JSON this emits is exactly what
/// /arm_command expects, so wiring it to ROS later is a one-line publish.
/// </summary>
public class ArmController : MonoBehaviour
{
    public enum ArmState { Stowed, Ready, Swinging, Returning }

    [Header("References")]
    public GhostBot bot;
    [Tooltip("Optional visual arm. Rotated through the swing if assigned.")]
    public Transform armPivot;
    [Tooltip("Optional ring showing reach. Scaled to armReach on start.")]
    public Transform reachIndicator;

    [Header("Input")]
    public InputActionReference swingAction;
    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    [Header("Geometry - keep in sync with bot_sim")]
    [Tooltip("Metres from the bot centre. JetRover reaches roughly 0.3-0.4 m.")]
    public float armReach = 0.35f;
    [Tooltip("Full swept width in degrees, centred on the bot's forward.")]
    public float armArcDegrees = 70f;

    [Header("Timing - keep in sync with bot_sim")]
    public float raiseTime = 0.5f;
    public float swingTime = 0.6f;
    public float returnTime = 0.5f;

    [Header("Visuals")]
    public float stowedAngle = -10f;
    public float readyAngle = 55f;
    public float impactAngle = -20f;

    /// <summary>Fired at the moment of impact with everything hit (may be empty).</summary>
    public event Action<List<Target>> OnSwingResolved;
    /// <summary>Fired on every state change, for HUD and audio.</summary>
    public event Action<ArmState> OnStateChanged;

    public ArmState State { get; private set; } = ArmState.Stowed;

    private float timer;
    private bool wasPressed;
    private readonly List<Target> hitBuffer = new List<Target>();

    void OnEnable()
    {
        if (swingAction != null && swingAction.action != null)
            swingAction.action.Enable();
    }

    void Start()
    {
        if (reachIndicator != null)
            reachIndicator.localScale = new Vector3(armReach * 2f, 1f, armReach * 2f);
        ApplyArmAngle(stowedAngle);
    }

    void Update()
    {
        ReadInput();
        Tick(Time.deltaTime);
    }

    private void ReadInput()
    {
        float value = (swingAction != null && swingAction.action != null)
            ? swingAction.action.ReadValue<float>()
            : 0f;
        bool pressed = value > pressThreshold;

        // Rising edge only: one pull, one swing. Holding the button does not
        // machine-gun the arm.
        if (pressed && !wasPressed) RequestSwing();
        wasPressed = pressed;
    }

    /// <summary>Also callable from a UI button.</summary>
    public void RequestSwing()
    {
        // A real arm cannot teleport back to Ready mid-stroke, so an
        // in-progress swing absorbs the request rather than restarting.
        if (State != ArmState.Stowed) return;

        SetState(ArmState.Ready);
        timer = raiseTime;
        if (bot != null) bot.MotionLocked = true;
    }

    private void Tick(float dt)
    {
        if (State == ArmState.Stowed) return;

        timer -= dt;
        float t;

        switch (State)
        {
            case ArmState.Ready:
                t = 1f - Mathf.Clamp01(timer / Mathf.Max(raiseTime, 1e-4f));
                ApplyArmAngle(Mathf.Lerp(stowedAngle, readyAngle, t));
                if (timer <= 0f) { SetState(ArmState.Swinging); timer = swingTime; }
                break;

            case ArmState.Swinging:
                t = 1f - Mathf.Clamp01(timer / Mathf.Max(swingTime, 1e-4f));
                // Ease-in so the arm accelerates into the hit rather than
                // sliding at constant speed - reads as a strike, not a sweep.
                ApplyArmAngle(Mathf.Lerp(readyAngle, impactAngle, t * t));
                if (timer <= 0f)
                {
                    ResolveHit();
                    SetState(ArmState.Returning);
                    timer = returnTime;
                }
                break;

            case ArmState.Returning:
                t = 1f - Mathf.Clamp01(timer / Mathf.Max(returnTime, 1e-4f));
                ApplyArmAngle(Mathf.Lerp(impactAngle, stowedAngle, t));
                if (timer <= 0f)
                {
                    SetState(ArmState.Stowed);
                    if (bot != null) bot.MotionLocked = false;
                }
                break;
        }
    }

    private void ResolveHit()
    {
        hitBuffer.Clear();

        Vector3 origin = transform.position;
        Vector3 forward = transform.forward;
        float halfArc = armArcDegrees * 0.5f;

        foreach (var target in Target.Active)
        {
            if (target == null || target.IsKnocked) continue;

            Vector3 delta = target.transform.position - origin;
            delta.y = 0f;                                  // planar test

            if (delta.magnitude > armReach) continue;
            if (Vector3.Angle(forward, delta) > halfArc) continue;

            target.Knock(forward);
            hitBuffer.Add(target);
        }

        OnSwingResolved?.Invoke(hitBuffer);
    }

    private void ApplyArmAngle(float degrees)
    {
        if (armPivot != null)
            armPivot.localRotation = Quaternion.Euler(degrees, 0f, 0f);
    }

    private void SetState(ArmState next)
    {
        State = next;
        OnStateChanged?.Invoke(next);
    }

    /// <summary>
    /// The exact payload /arm_command expects. Publish this string when the
    /// real robot is connected; bot_sim already understands it.
    /// </summary>
    public string BuildRosCommand(float aimYawRadians = 0f)
    {
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{{\"action\":\"SWING\",\"yaw\":{0:F3}}}", aimYawRadians);
    }

    void OnDrawGizmosSelected()
    {
        // Reach and arc, visible in the Editor so you can tune without building.
        Gizmos.color = Color.yellow;
        Vector3 p = transform.position;
        Gizmos.DrawWireSphere(p, armReach);

        Gizmos.color = Color.red;
        float half = armArcDegrees * 0.5f;
        Vector3 a = Quaternion.Euler(0f, -half, 0f) * transform.forward * armReach;
        Vector3 b = Quaternion.Euler(0f, half, 0f) * transform.forward * armReach;
        Gizmos.DrawLine(p, p + a);
        Gizmos.DrawLine(p, p + b);
    }
}
