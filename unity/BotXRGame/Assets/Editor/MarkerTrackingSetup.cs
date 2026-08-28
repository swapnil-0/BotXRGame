using System;
using System.Collections.Generic;
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

    // 19 = XRMarkerDictionary.AprilTag_36H11, read from the package enum.
    private const int AprilTag36H11 = 19;

    // Explicit ids with explicit sizes, rather than "All Markers".
    //
    // All Markers omits the physical edge, so the runtime has to estimate size
    // at return - and the package's own warning says not every runtime supports
    // estimation. An unsupported one gives no tracking, or poses at the wrong
    // scale, which looks like bad tracking rather than missing configuration.
    // Explicit entries also let the bot tag and the cup tags be different
    // sizes, which they will be: a cup top cannot carry a 100 mm tag.
    private const int BotMarkerId = 0;
    private const float BotEdgeMetres = 0.100f;

    // ids 1..10. More than the four cups in play on purpose: an entry costs
    // nothing at runtime if its tag is never shown, whereas a tag whose id has
    // no entry simply does not resolve - and diagnosing that mid-test, with a
    // printed tag in hand that the headset ignores, is exactly the kind of
    // dead end worth pre-empting.
    private const int CupCount = 10;             // ids 1..10

    // Same 100 mm as the bot tag - every marker is printed at one size.
    // Kept as a separate constant rather than reusing BotEdgeMetres so the two
    // can diverge later without hunting through the code: a cup top is small,
    // and 100 mm tags on cups may yet prove impractical.
    private const float CupEdgeMetres = 0.100f;  // MUST match what you print

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

        var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        if (existing != null)
        {
            // Top up rather than skip: the database was created with only the
            // bot id, and the cups need adding without losing hand edits.
            int added = EnsureEntries(existing);
            note = added > 0
                ? "Added " + added + " missing cup entries to " + path
                : "Marker database already complete at " + path;
            return true;
        }

        var dbType = FindType("Google.XR.Extensions.XRMarkerDatabase");
        if (dbType == null)
        {
            note = "XRMarkerDatabase type not found - create it via Assets > Create > XR > Marker Database";
            return false;
        }

        // AssetDatabase.CreateFolder, not Directory.CreateDirectory. The latter
        // makes the folder on disk without registering it, and CreateAsset then
        // fails on a path the AssetDatabase does not know about - producing no
        // asset and no obvious reason why.
        if (!EnsureFolder("Assets/SourceFiles/XR"))
        {
            note = "Could not create Assets/SourceFiles/XR";
            return false;
        }

        var db = ScriptableObject.CreateInstance(dbType);
        AssetDatabase.CreateAsset(db, path);

        EnsureEntries(db);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        note = string.Format(
            "Created {0}: bot id {1} at {2:F3} m, cups id 1-{3} at {4:F3} m",
            path, BotMarkerId, BotEdgeMetres, CupCount, CupEdgeMetres);
        return true;
    }

    /// <summary>Create a folder path segment by segment, registering each with the AssetDatabase.</summary>
    private static bool EnsureFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath)) return true;

        var parts = assetPath.Split('/');
        string running = parts[0];                 // "Assets"

        for (int i = 1; i < parts.Length; i++)
        {
            string next = running + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(running, parts[i]);
            running = next;
        }

        AssetDatabase.Refresh();
        return AssetDatabase.IsValidFolder(assetPath);
    }

    /// <summary>
    /// Make sure the database holds one explicit entry per id we use.
    ///
    /// Adds only what is missing and never edits an existing row, so sizes
    /// tuned by hand in the Inspector survive a re-run. Returns how many were
    /// added.
    /// </summary>
    private static int EnsureEntries(Object db)
    {
        var so = new SerializedObject(db);
        var entries = so.FindProperty("_entries");
        if (entries == null) return 0;

        var present = new HashSet<int>();
        for (int i = 0; i < entries.arraySize; i++)
        {
            var e = entries.GetArrayElementAtIndex(i);
            var idProp = e.FindPropertyRelative("_markerId");
            var allProp = e.FindPropertyRelative("_allMarkers");

            // An All Markers row covers every id, so leaving it alongside
            // explicit ones would make which entry wins ambiguous. Clear the
            // flag and let the explicit rows carry their real sizes.
            if (allProp != null && allProp.boolValue) allProp.boolValue = false;

            if (idProp != null) present.Add(idProp.intValue);
        }

        int added = 0;

        for (int id = 0; id <= CupCount; id++)
        {
            if (present.Contains(id)) continue;

            entries.arraySize++;
            var e = entries.GetArrayElementAtIndex(entries.arraySize - 1);

            SetIfPresent(e, "_dictionary", AprilTag36H11);
            SetBoolIfPresent(e, "_allMarkers", false);
            SetIfPresent(e, "_markerId", id);
            SetFloatIfPresent(e, "_physicalEdge",
                id == BotMarkerId ? BotEdgeMetres : CupEdgeMetres);

            added++;
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(db);
        return added;
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

    /// <summary>
    /// Force every entry to one physical size.
    ///
    /// Separate from the setup command, and it asks first, because it
    /// OVERWRITES values - including any deliberately set by hand. Setup only
    /// ever adds rows; mixing the two would make a safe-to-re-run command
    /// quietly destructive, which is the same trap the scene builder fell into
    /// when it reset arenaSize on every run.
    /// </summary>
    [MenuItem("Tools/BotXRGame/Set All Marker Sizes To 100 mm", false, 43)]
    public static void NormaliseMarkerSizes()
    {
        const float edge = 0.100f;

        string path = "Assets/SourceFiles/XR/MarkerDatabase.asset";
        var db = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        if (db == null)
        {
            EditorUtility.DisplayDialog("BotXRGame",
                "No marker database at " + path + ".\nRun Set Up AprilTag Tracking first.",
                "OK");
            return;
        }

        var so = new SerializedObject(db);
        var entries = so.FindProperty("_entries");
        if (entries == null || entries.arraySize == 0)
        {
            EditorUtility.DisplayDialog("BotXRGame", "Database has no entries.", "OK");
            return;
        }

        int changed = 0;
        var before = new List<string>();

        for (int i = 0; i < entries.arraySize; i++)
        {
            var e = entries.GetArrayElementAtIndex(i);
            var idP = e.FindPropertyRelative("_markerId");
            var edgeP = e.FindPropertyRelative("_physicalEdge");
            if (edgeP == null) continue;

            if (Mathf.Abs(edgeP.floatValue - edge) > 1e-4f)
            {
                before.Add(string.Format("id {0}: {1:F3} -> {2:F3}",
                    idP != null ? idP.intValue : -1, edgeP.floatValue, edge));
                changed++;
            }
        }

        if (changed == 0)
        {
            EditorUtility.DisplayDialog("BotXRGame",
                "All " + entries.arraySize + " entries are already 0.100 m.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("BotXRGame",
                "Overwrite the physical edge of " + changed + " entries:\n\n  " +
                string.Join("\n  ", before) +
                "\n\nThis replaces hand-set values.", "Set to 100 mm", "Cancel"))
            return;

        for (int i = 0; i < entries.arraySize; i++)
        {
            var e = entries.GetArrayElementAtIndex(i);
            var edgeP = e.FindPropertyRelative("_physicalEdge");
            if (edgeP != null) edgeP.floatValue = edge;
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();

        Debug.LogFormat("[Marker] set {0} entries to {1:F3} m", changed, edge);
        EditorUtility.DisplayDialog("BotXRGame",
            changed + " entries set to 0.100 m.\n\n" +
            "Now press Create/Update on the database so the reference library " +
            "picks up the new sizes - the library is what the runtime reads.",
            "OK");
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
