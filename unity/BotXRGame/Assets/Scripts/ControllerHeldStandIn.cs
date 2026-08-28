using UnityEngine;

/// <summary>
/// Makes the tag stand-in movable from inside the headset by holding it out in
/// front of the controller.
///
/// The stand-in cube was only movable in the Editor's Scene view, which makes it
/// useless in a build: you cannot drag a GameObject while wearing a headset, so
/// AprilTag mode looked frozen with the ship parked on a cube nobody could
/// reach. Now you point the controller and the "tag" goes there, which is
/// enough to verify the whole follow path - ship hover, lag, heading, cup
/// interaction - without any real tag tracking existing yet.
///
/// Delete this once genuine tag tracking is available; it is scaffolding.
/// </summary>
public class ControllerHeldStandIn : MonoBehaviour
{
    [Tooltip("Controller or ray interactor to hold the stand-in in front of.")]
    public Transform rayOrigin;

    [Tooltip("Metres in front of the controller.")]
    public float distance = 0.8f;

    [Tooltip("Drop the stand-in to this height above the detected floor, so it " +
             "sits where a tag on a robot would rather than floating at " +
             "whatever height your hand happens to be.")]
    public bool projectToFloor = true;

    public float floorY = 0f;
    public float heightAboveFloor = 0.02f;

    [Tooltip("Only move while this is held, so the tag can be parked and then " +
             "driven around. Leave empty to follow the controller constantly.")]
    public UnityEngine.InputSystem.InputActionReference holdAction;

    [Range(0.1f, 0.9f)]
    public float pressThreshold = 0.5f;

    [Tooltip("Only active in AprilTag mode; harmless otherwise.")]
    public bool onlyInAprilTagMode = true;

    private bool everPlaced;
    private TrackedImageTagSource tagSource;

    void Start()
    {
        if (holdAction != null && holdAction.action != null)
            holdAction.action.Enable();

        if (rayOrigin == null)
        {
            var placer = FindAnyObjectByType<ArenaPlacer>();
            if (placer != null) rayOrigin = placer.rayOrigin;
        }

        if (rayOrigin == null)
            Debug.LogWarning("[StandIn] no rayOrigin; stand-in cannot be moved.");
    }

    void Update()
    {
        if (onlyInAprilTagMode && !GameMode.IsAprilTag) return;
        if (rayOrigin == null) return;

        // Stand down whenever a real marker is being tracked.
        //
        // TrackedImageTagSource is supposed to disable this component via
        // standInToDisable, but that reference can be unset - it was, in the
        // scene report - and then BOTH write to the same Transform every frame
        // in undefined order. The tag would appear to jitter between the
        // printed marker and the controller, which looks like bad tracking
        // rather than two components fighting. Checking directly cannot be
        // left unwired.
        if (tagSource == null) tagSource = FindAnyObjectByType<TrackedImageTagSource>();
        if (tagSource != null && tagSource.Tracking) return;

        if (holdAction != null && holdAction.action != null && everPlaced)
        {
            if (holdAction.action.ReadValue<float>() <= pressThreshold) return;
        }

        Vector3 p = rayOrigin.position + rayOrigin.forward * distance;

        if (projectToFloor) p.y = floorY + heightAboveFloor;

        transform.position = p;

        // Face the way the controller points, flattened - the follower copies
        // this yaw onto the ship, so it needs to mean something.
        Vector3 f = rayOrigin.forward;
        f.y = 0f;
        if (f.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);

        everPlaced = true;
    }

    /// <summary>Called by ArenaPlacer once the floor height is known.</summary>
    public void SetFloor(float y)
    {
        floorY = y;
    }
}
