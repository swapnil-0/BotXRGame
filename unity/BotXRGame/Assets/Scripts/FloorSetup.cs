using System.Collections;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Enables floor/plane detection on Android XR.
///
/// Plane detection needs a runtime permission that Unity does NOT request for
/// you. If ARPlaneManager is enabled before the grant, the subsystem errors out
/// and you get no planes with no obvious reason why. So the manager starts
/// disabled and is switched on only after the permission comes back.
///
/// Attach to any always-active GameObject (the XR Origin is a good home).
/// </summary>
public class FloorSetup : MonoBehaviour
{
    [Header("References")]
    [Tooltip("ARPlaneManager on the XR Origin. Leave it DISABLED in the Inspector.")]
    public ARPlaneManager planeManager;

    [Header("Options")]
    [Tooltip("Horizontal only is what you want for a floor. Everything costs more.")]
    public PlaneDetectionMode detectionMode = PlaneDetectionMode.Horizontal;

    [Tooltip("Seconds to wait for the user to answer the permission dialog.")]
    public float permissionTimeout = 20f;

    // Android XR gates plane data behind scene understanding. COARSE is enough
    // for floor-sized planes; FINE gives finer geometry at higher cost.
    private const string ScenePermission = "android.permission.SCENE_UNDERSTANDING_COARSE";

    /// <summary>True once planes are actually being tracked. Useful for HUD text.</summary>
    public bool Ready { get; private set; }

    /// <summary>Human-readable state, safe to show on a HUD.</summary>
    public string Status { get; private set; } = "Starting...";

    void Start()
    {
        if (planeManager == null)
        {
            Status = "ERROR: ARPlaneManager not assigned";
            Debug.LogError("[FloorSetup] planeManager is not assigned in the Inspector.");
            return;
        }

        // Must be off until the permission is granted.
        planeManager.enabled = false;
        StartCoroutine(RequestThenEnable());
    }

    private IEnumerator RequestThenEnable()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(ScenePermission))
        {
            Status = "Requesting scene permission...";
            Permission.RequestUserPermission(ScenePermission);

            float waited = 0f;
            while (!Permission.HasUserAuthorizedPermission(ScenePermission)
                   && waited < permissionTimeout)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (!Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                Status = "Scene permission DENIED - no floor detection";
                Debug.LogError("[FloorSetup] " + ScenePermission + " was not granted.");
                yield break;
            }
        }
#endif
        // One frame of slack so the subsystem sees the fresh grant.
        yield return null;

        planeManager.requestedDetectionMode = detectionMode;
        planeManager.enabled = true;

        Ready = true;
        Status = "Scanning for floor - look around slowly";
        Debug.Log("[FloorSetup] Plane detection enabled, mode=" + detectionMode);
    }

    void Update()
    {
        if (!Ready || planeManager == null) return;

        int count = 0;
        foreach (var _ in planeManager.trackables) count++;

        Status = count == 0
            ? "Scanning for floor - look around slowly"
            : count + " plane(s) detected";
    }
}
