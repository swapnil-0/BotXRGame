using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Is a patch of floor clear enough to play on?
///
/// Two levels of confidence, depending on what the headset is configured to
/// provide:
///
/// 1. PLANE COVERAGE (always available). Samples a grid over the proposed
///    rectangle and checks each point lands on a horizontal plane at roughly
///    floor height. This reliably catches missing floor: unscanned regions,
///    the edge of the plane, a step down, a table surface at the wrong height.
///
/// 2. MESH OBSTACLES (only if AR Scene Meshing is enabled in OpenXR settings).
///    A physics raycast against the generated environment mesh, which catches
///    actual objects sitting on the floor.
///
/// Level 1 alone will NOT see a backpack on the carpet, because AR raycasts
/// test planes rather than real geometry and pass straight through to the floor
/// behind. If obstacle detection matters, enable scene meshing.
/// </summary>
public static class FreeSpaceProbe
{
    public struct Result
    {
        public bool IsClear;
        /// <summary>Sample points that failed, in world space. Useful to visualise.</summary>
        public List<Vector3> BadSamples;
        public int TotalSamples;
        public string Reason;
    }

    /// <summary>
    /// Test an axis-aligned-in-local-space rectangle for playability.
    /// </summary>
    /// <param name="raycaster">AR raycast manager, for plane tests.</param>
    /// <param name="origin">Centre of the near edge of the rectangle.</param>
    /// <param name="forward">Horizontal direction the rectangle extends.</param>
    /// <param name="width">Rectangle width, metres.</param>
    /// <param name="depth">Rectangle depth, metres.</param>
    /// <param name="floorY">Expected floor height, world Y.</param>
    /// <param name="samplesPerSide">Grid resolution. 5 gives 25 probes.</param>
    /// <param name="heightTolerance">Allowed deviation from floorY.</param>
    /// <param name="useMesh">Also physics-raycast against environment mesh.</param>
    /// <param name="obstacleMask">Layers counting as obstacles for the mesh test.</param>
    public static Result Test(
        ARRaycastManager raycaster,
        Vector3 origin, Vector3 forward,
        float width, float depth, float floorY,
        int samplesPerSide = 5,
        float heightTolerance = 0.08f,
        bool useMesh = true,
        int obstacleMask = ~0)
    {
        var result = new Result
        {
            IsClear = false,
            BadSamples = new List<Vector3>(),
            TotalSamples = 0,
            Reason = "",
        };

        if (raycaster == null)
        {
            result.Reason = "No ARRaycastManager";
            return result;
        }

        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f)
        {
            result.Reason = "Degenerate forward direction";
            return result;
        }
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        int n = Mathf.Max(2, samplesPerSide);
        var hits = new List<ARRaycastHit>();

        for (int i = 0; i < n; i++)
        {
            // Sample across the width, inset slightly from the edges so a
            // rectangle that merely touches the plane boundary is not rejected.
            float u = Mathf.Lerp(-0.45f, 0.45f, i / (float)(n - 1));

            for (int j = 0; j < n; j++)
            {
                float v = Mathf.Lerp(0.05f, 0.95f, j / (float)(n - 1));

                Vector3 p = origin + right * (u * width) + forward * (v * depth);
                p.y = floorY;
                result.TotalSamples++;

                // Probe straight down from above, so the test does not depend
                // on where the player happens to be standing.
                var ray = new Ray(p + Vector3.up * 0.6f, Vector3.down);

                bool onFloor = false;
                if (raycaster.Raycast(ray, hits, TrackableType.PlaneWithinPolygon))
                {
                    foreach (var h in hits)
                    {
                        if (Mathf.Abs(h.pose.position.y - floorY) <= heightTolerance)
                        {
                            onFloor = true;
                            break;
                        }
                    }
                }

                if (!onFloor)
                {
                    result.BadSamples.Add(p);
                    continue;
                }

                if (useMesh && Physics.Raycast(ray, out RaycastHit mh, 1.2f, obstacleMask))
                {
                    // Something solid noticeably above the floor is an obstacle.
                    if (mh.point.y > floorY + heightTolerance)
                        result.BadSamples.Add(p);
                }
            }
        }

        result.IsClear = result.BadSamples.Count == 0;
        result.Reason = result.IsClear
            ? "Clear"
            : string.Format("{0} of {1} sample points blocked",
                            result.BadSamples.Count, result.TotalSamples);
        return result;
    }
}
