using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Feeds ShipTagFollower from a real tracked AprilTag.
///
/// Uses ARTrackedImageManager, which is provider-agnostic: Google's
/// XRMarkerTrackingFeature (package com.google.xr.extensions, feature
/// "Android XR (Extensions): Image Tracking (Marker)") supplies an
/// XRImageTrackingSubsystem behind it and supports AprilTag families directly.
/// So this file compiles and runs today with no tracking, and starts working
/// the moment that feature is installed and enabled - nothing here changes.
///
/// Drives the same Transform the stand-in cube used, so the whole follow path
/// downstream is already tested.
///
/// Setup, once the Google package is installed:
///   1. Project Settings > XR Plug-in Management > OpenXR > Android
///      enable "Android XR (Extensions): Image Tracking (Marker)"
///   2. Create an XRMarkerDatabase, add an entry for AprilTag 36h11, id 0,
///      physical size 0.1 m, and build it into an XRReferenceImageLibrary
///   3. Put an ARTrackedImageManager on XR Origin, assign that library
///   4. Assign this component's tagOutput to the same Transform
///      ShipTagFollower.tagTransform points at
/// </summary>
public class TrackedImageTagSource : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Found on XR Origin if left empty.")]
    public ARTrackedImageManager trackedImageManager;

    [Tooltip("Only accept this marker name / id. Empty accepts the first tag " +
             "seen, which is fine with a single tag in the room but wrong the " +
             "moment there are two.")]
    public string expectedName = "";

    [Header("Output")]
    [Tooltip("Transform moved to the tag pose. Point ShipTagFollower.tagTransform " +
             "at this same object - usually TagStandIn, so the stand-in becomes " +
             "the real thing rather than a parallel path.")]
    public Transform tagOutput;

    [Tooltip("Disable the controller-held stand-in once a real tag is seen, so " +
             "the two cannot fight over the same Transform.")]
    public ControllerHeldStandIn standInToDisable;

    // --- status, read by the HUD -----------------------------------------
    // Plain comment: [Header] is AttributeTargets.Field, so on a property it is
    // CS0592. Second time I have made this exact mistake in this project.
    public bool Tracking { get; private set; }
    public string Status { get; private set; } = "no manager";
    public Vector3 LastPosition { get; private set; }
    public float LastSeenAge { get; private set; }

    private float lastSeenTime = -999f;
    private bool warnedNoManager;

    void Start()
    {
        if (trackedImageManager == null)
            trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();

        if (trackedImageManager == null)
        {
            Status = "NO ARTrackedImageManager - marker tracking not installed";
            if (!warnedNoManager)
            {
                warnedNoManager = true;
                Debug.LogWarning(
                    "[Tag] No ARTrackedImageManager in the scene. AprilTag tracking " +
                    "needs Google's com.google.xr.extensions package with the " +
                    "'Android XR (Extensions): Image Tracking (Marker)' feature " +
                    "enabled. Until then the controller stand-in is the tag.");
            }
        }
    }

    void Update()
    {
        if (trackedImageManager == null || tagOutput == null) return;

        ARTrackedImage best = null;

        foreach (var img in trackedImageManager.trackables)
        {
            // Limited tracking means the pose is being extrapolated rather than
            // observed. Accepting it makes the ship drift smoothly to a wrong
            // place, which reads as a physics bug rather than lost tracking.
            if (img.trackingState != TrackingState.Tracking) continue;

            if (!string.IsNullOrEmpty(expectedName) &&
                img.referenceImage.name != expectedName) continue;

            best = img;
            break;
        }

        if (best != null)
        {
            tagOutput.SetPositionAndRotation(best.transform.position,
                                             best.transform.rotation);
            LastPosition = best.transform.position;
            lastSeenTime = Time.time;
            Tracking = true;

            if (standInToDisable != null && standInToDisable.enabled)
            {
                standInToDisable.enabled = false;
                Debug.Log("[Tag] real marker acquired - stand-in disabled");
            }

            Status = string.Format("{0} @ {1:F2},{2:F2},{3:F2}",
                best.referenceImage.name,
                LastPosition.x, LastPosition.y, LastPosition.z);
        }
        else
        {
            Tracking = false;
            LastSeenAge = Time.time - lastSeenTime;
            Status = lastSeenTime < 0f
                ? "searching - no marker seen yet"
                : string.Format("LOST {0:F1}s", LastSeenAge);
        }
    }
}
