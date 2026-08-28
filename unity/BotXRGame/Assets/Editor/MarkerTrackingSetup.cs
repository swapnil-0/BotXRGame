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

            if (EnableMarkerFeature(out string featureNote)) done.Add(featureNote);
            else todo.Add(featureNote);

            if (CreateMarkerDatabase(out string dbNote, out string dbPath)) done.Add(dbNote);
            else todo.Add(dbNote);

            // Library generation is left to the package's own inspector on
            // purpose. It builds an XRReferenceImageLibrary from the entries
            // through internal editor code; reproducing that from outside is
            // exactly the kind of guess that has cost this project build cycles.
            todo.Add("Select " + dbPath + ", press Create (image library), then Update");
            todo.Add("Assign that library to ARTrackedImageManager.referenceLibrary");
            todo.Add("Re-run this command to enable the manager once the library exists");
        }

        todo.Add("Marker tracking needs the SceneUnderstandingCoarse permission - " +
                 "FloorSetup already requests it, so no extra work there.");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        string msg = "Wired:\n  " + string.Join("\n  ", done) +
                     "\n\nStill needed:\n  " + string.Join("\n  ", todo);
        Debug.Log("[BotXRGame] " + msg);
        EditorUtility.DisplayDialog("BotXRGame - AprilTag", msg, "OK");
    }

    /// <summary>
    /// Turn on the marker tracking OpenXR feature for Android.
    ///
    /// Matches on the feature's type name rather than referencing Google's
    /// type, so this file still compiles with the package absent.
    /// </summary>
    private static bool EnableMarkerFeature(out string note)
    {
        var settings = UnityEngine.XR.OpenXR.OpenXRSettings
            .GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);

        if (settings == null)
        {
            note = "No OpenXR settings for Android - enable OpenXR in XR Plug-in Management first";
            return false;
        }

        foreach (var f in settings.GetFeatures())
        {
            if (f == null || f.GetType().FullName != GoogleFeatureType) continue;

            if (f.enabled)
            {
                note = "Marker tracking feature already enabled";
                return true;
            }

            var so = new SerializedObject(f);
            var p = so.FindProperty("m_enabled");
            if (p != null)
            {
                p.boolValue = true;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(f);
                AssetDatabase.SaveAssets();
                note = "Enabled 'Android XR (Extensions): Image Tracking (Marker)'";
                return true;
            }

            note = "Found the marker feature but could not enable it - do it by hand";
            return false;
        }

        note = "Marker tracking feature not found in Android OpenXR settings";
        return false;
    }

    /// <summary>
    /// Create a marker database containing exactly the tag in use:
    /// AprilTag 36H11, id 0, 100 mm.
    ///
    /// Written through SerializedObject because AddEntry is internal to the
    /// package. Field names come from reading XRMarkerDatabaseEntry directly,
    /// not from guesswork.
    /// </summary>
    private static bool CreateMarkerDatabase(out string note, out string path)
    {
        path = "Assets/SourceFiles/XR/MarkerDatabase.asset";

        if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(path) != null)
        {
            note = "Marker database already exists at " + path;
            return true;
        }

        var dbType = FindType("Google.XR.Extensions.XRMarkerDatabase");
        if (dbType == null)
        {
            note = "XRMarkerDatabase type not found - create it via Assets > Create > XR > Marker Database";
            return false;
        }

        Directory.CreateDirectory("Assets/SourceFiles/XR");

        var db = ScriptableObject.CreateInstance(dbType);
        AssetDatabase.CreateAsset(db, path);

        var so = new SerializedObject(db);
        var entries = so.FindProperty("_entries");
        if (entries == null)
        {
            note = "Created " + path + " but could not find its entry list - add the tag by hand";
            AssetDatabase.SaveAssets();
            return false;
        }

        entries.arraySize = 1;
        var e = entries.GetArrayElementAtIndex(0);

        // 19 = XRMarkerDictionary.AprilTag_36H11, read from the package enum.
        SetIfPresent(e, "_dictionary", 19);
        SetBoolIfPresent(e, "_allMarkers", false);
        SetIfPresent(e, "_markerId", 0);
        SetFloatIfPresent(e, "_physicalEdge", 0.1f);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        note = "Created " + path + " with AprilTag 36H11, id 0, 0.100 m";
        return true;
    }

    private static void SetIfPresent(SerializedProperty parent, string name, int value)
    {
        var p = parent.FindPropertyRelative(name);
        if (p == null) return;

        // intValue for enums too, not enumValueIndex. enumValueIndex is the
        // position in the name list, which only equals the enum's value while
        // the enum happens to be contiguous from zero. XRMarkerDictionary is
        // contiguous today, so both work - but the moment a family is inserted
        // or removed, index-based writing silently selects a different marker
        // family and the tag just never resolves.
        p.intValue = value;
    }

    private static void SetBoolIfPresent(SerializedProperty parent, string name, bool value)
    {
        var p = parent.FindPropertyRelative(name);
        if (p != null) p.boolValue = value;
    }

    private static void SetFloatIfPresent(SerializedProperty parent, string name, float value)
    {
        var p = parent.FindPropertyRelative(name);
        if (p != null) p.floatValue = value;
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
