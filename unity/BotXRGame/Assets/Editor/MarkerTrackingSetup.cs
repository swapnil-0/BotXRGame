using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Object = UnityEngine.Object;

/// <summary>
/// Wires the AprilTag path as far as it can go without Google's package, and
/// reports precisely what is left.
///
/// Uses reflection to detect com.google.xr.extensions rather than referencing
/// its types. A direct reference would be a compile error on a project that has
/// not installed it yet - which is every project until the moment it is
/// installed, including this one right now. Scaffolding that breaks the build
/// before the thing it scaffolds exists is worse than none.
/// </summary>
public static class MarkerTrackingSetup
{
    private const string GoogleFeatureType =
        "Google.XR.Extensions.XRMarkerTrackingFeature";

    [MenuItem("Tools/BotXRGame/Set Up AprilTag Tracking", false, 42)]
    public static void SetUp()
    {
        var done = new List<string>();
        var todo = new List<string>();

        bool googleInstalled = FindType(GoogleFeatureType) != null;

        // ------------------------------------------------ ARTrackedImageManager
        var origin = Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (origin == null)
        {
            EditorUtility.DisplayDialog("BotXRGame",
                "No XROrigin in the scene - cannot add ARTrackedImageManager.", "OK");
            return;
        }

        var tim = origin.GetComponent<ARTrackedImageManager>();
        if (tim == null)
        {
            tim = Undo.AddComponent<ARTrackedImageManager>(origin.gameObject);
            done.Add("ARTrackedImageManager added to " + origin.name);
        }
        else
        {
            done.Add("ARTrackedImageManager already on " + origin.name);
        }

        // Disabled until a library exists. An enabled manager with no reference
        // library logs errors every frame on some providers, which buries the
        // real diagnostics we rely on.
        if (googleInstalled == false && tim.enabled)
        {
            tim.enabled = false;
            done.Add("ARTrackedImageManager disabled until a marker library exists");
        }

        // ---------------------------------------------------- the tag source
        var standIn = GameObject.Find("TagStandIn");
        var host = standIn != null ? standIn : origin.gameObject;

        var src = host.GetComponent<TrackedImageTagSource>();
        if (src == null) src = Undo.AddComponent<TrackedImageTagSource>(host);

        var wires = new Dictionary<string, Object> { { "trackedImageManager", tim } };
        if (standIn != null)
        {
            wires["tagOutput"] = standIn.transform;
            var held = standIn.GetComponent<ControllerHeldStandIn>();
            if (held != null) wires["standInToDisable"] = held;
        }
        WireEmpty(src, wires);
        done.Add("TrackedImageTagSource on " + host.name +
                 (standIn != null ? " (writes into TagStandIn)" : ""));

        // The follower already points at TagStandIn, so once the source writes
        // real poses into that same transform nothing downstream changes. That
        // is the whole reason the stand-in and the real tag share a transform.
        var follower = Object.FindAnyObjectByType<ShipTagFollower>();
        if (follower != null && standIn != null)
        {
            var so = new SerializedObject(follower);
            var p = so.FindProperty("tagTransform");
            if (p != null && p.objectReferenceValue == null)
            {
                p.objectReferenceValue = standIn.transform;
                so.ApplyModifiedProperties();
            }
            done.Add("ShipTagFollower still reads TagStandIn - no change needed later");
        }

        // ------------------------------------------------------- what is left
        if (!googleInstalled)
        {
            todo.Add("INSTALL Google's package - marker tracking is not in Unity's " +
                     "Android XR package at any version:");
            todo.Add("   Package Manager > + > Install package from git URL");
            todo.Add("   https://github.com/android/android-xr-unity-package.git");
            todo.Add("Then re-run this command.");
        }
        else
        {
            done.Add("com.google.xr.extensions detected");
            todo.Add("Project Settings > XR Plug-in Management > OpenXR > Android:");
            todo.Add("   enable 'Android XR (Extensions): Image Tracking (Marker)'");
            todo.Add("Create an XRMarkerDatabase: AprilTag 36h11, id 0, size 0.100 m");
            todo.Add("Build it into an XRReferenceImageLibrary and assign that to");
            todo.Add("   ARTrackedImageManager.referenceLibrary, then enable the manager");
        }

        todo.Add("Marker tracking needs the SceneUnderstandingCoarse permission - " +
                 "FloorSetup already requests it, so no extra work there.");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        string msg = "Wired:\n  " + string.Join("\n  ", done) +
                     "\n\nStill needed:\n  " + string.Join("\n  ", todo);
        Debug.Log("[BotXRGame] " + msg);
        EditorUtility.DisplayDialog("BotXRGame - AprilTag", msg, "OK");
    }

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, false);
            if (t != null) return t;
        }
        return null;
    }

    private static void WireEmpty(Object target, Dictionary<string, Object> values)
    {
        var so = new SerializedObject(target);
        foreach (var kv in values)
        {
            var p = so.FindProperty(kv.Key);
            if (p != null && p.objectReferenceValue == null)
                p.objectReferenceValue = kv.Value;
        }
        so.ApplyModifiedProperties();
    }
}
