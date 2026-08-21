using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Point at a detected plane and pull the trigger to drop an object on it.
///
/// Raycasts against AR planes rather than physics colliders, so it lands on the
/// real floor surface reported by the headset rather than on anything virtual.
///
/// Everything is null-guarded, so a half-wired Inspector degrades quietly
/// instead of spamming exceptions in a headset you cannot read.
/// </summary>
public class ObjectPlacer : MonoBehaviour
{
    [Header("References")]
    public ARRaycastManager raycastManager;

    [Tooltip("Transform whose forward axis is the aim ray. Use the controller " +
             "or the Ray Interactor object.")]
    public Transform rayOrigin;

    [Tooltip("What to spawn. A scaled-down cube works fine to start with.")]
    public GameObject prefabToPlace;

    [Header("Input")]
    [Tooltip("Trigger action. Reuse the same reference RobotController uses.")]
    public InputActionReference placeAction;

    [Range(0.1f, 0.9f)]
    public float pressThreshold = 0.5f;

    [Header("Aim Feedback (optional)")]
    [Tooltip("Small object shown where the ray meets the floor.")]
    public GameObject reticle;

    [Header("Limits")]
    [Tooltip("0 = unlimited. Otherwise the oldest object is removed.")]
    public int maxObjects = 0;

    /// <summary>How many objects are currently placed. Handy for a HUD.</summary>
    public int PlacedCount => placed.Count;

    private static readonly List<ARRaycastHit> hits = new List<ARRaycastHit>();
    private readonly List<GameObject> placed = new List<GameObject>();
    private bool wasPressed;

    void OnEnable()
    {
        if (placeAction != null && placeAction.action != null)
            placeAction.action.Enable();
    }

    void Update()
    {
        if (raycastManager == null || rayOrigin == null) return;

        var ray = new Ray(rayOrigin.position, rayOrigin.forward);

        // PlaneWithinPolygon respects the plane's actual detected boundary,
        // not the infinite mathematical plane - so you cannot place objects
        // out past the edge of the floor the headset has actually seen.
        bool hit = raycastManager.Raycast(ray, hits, TrackableType.PlaneWithinPolygon);

        if (reticle != null)
        {
            reticle.SetActive(hit);
            if (hit)
            {
                reticle.transform.position = hits[0].pose.position;
                reticle.transform.rotation = hits[0].pose.rotation;
            }
        }

        // Rising-edge detection so one pull places one object.
        float value = (placeAction != null && placeAction.action != null)
            ? placeAction.action.ReadValue<float>()
            : 0f;
        bool pressed = value > pressThreshold;

        if (pressed && !wasPressed && hit)
            Place(hits[0].pose);

        wasPressed = pressed;
    }

    private void Place(Pose pose)
    {
        if (prefabToPlace == null)
        {
            Debug.LogWarning("[ObjectPlacer] prefabToPlace is not assigned.");
            return;
        }

        // Keep the object upright: take the surface position but discard the
        // plane's tilt, otherwise props lean on slightly-off floor estimates.
        var obj = Instantiate(prefabToPlace, pose.position, Quaternion.identity);
        placed.Add(obj);

        if (maxObjects > 0 && placed.Count > maxObjects)
        {
            Destroy(placed[0]);
            placed.RemoveAt(0);
        }

        Debug.Log("[ObjectPlacer] Placed at " + pose.position + " (total " + placed.Count + ")");
    }

    /// <summary>Wire to a UI button to clear the scene between runs.</summary>
    public void ClearAll()
    {
        foreach (var o in placed)
            if (o != null) Destroy(o);
        placed.Clear();
    }
}
