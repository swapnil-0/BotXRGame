using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.ARFoundation;
using Object = UnityEngine.Object;

/// <summary>
/// Dumps every setting that matters into a text file so scene wiring can be
/// reviewed without screenshots.
///
/// Written because checking wiring by eye through Inspector screenshots is slow
/// and misses exactly the things that break builds: an unassigned reference, a
/// canvas in the wrong render mode, a serialized value that quietly disagrees
/// with its code default. Every bug of that kind in this project took at least
/// one build cycle to find.
///
/// Dumps ALL serialized properties generically rather than a hand-picked list,
/// because the fields worth checking are usually the ones nobody thought to
/// list.
/// </summary>
public static class SceneReport
{
    // Relative to Application.dataPath, which is <repo>/unity/BotXRGame/Assets.
    // Three levels up is the repo root; two lands in unity/ and puts the report
    // somewhere nobody looks.
    private const string OutPath = "../../../docs/reports/scene-report.md";

    [MenuItem("Tools/BotXRGame/Export Scene Report", false, 60)]
    public static void Export()
    {
        var sb = new StringBuilder();
        var scene = EditorSceneManager.GetActiveScene();

        sb.AppendLine("# BotXRGame scene report");
        sb.AppendLine();
        sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("Scene: " + scene.name + "  (" + scene.path + ")");
        sb.AppendLine("Unity: " + Application.unityVersion);
        sb.AppendLine();

        // ---------------------------------------------------------- overview
        sb.AppendLine("## Scene roots");
        sb.AppendLine("```");
        foreach (var root in scene.GetRootGameObjects())
            DumpHierarchy(root.transform, 0, sb);
        sb.AppendLine("```");
        sb.AppendLine();

        // ------------------------------------------------- UI health checks
        sb.AppendLine("## UI plumbing");
        sb.AppendLine("```");
        var eventSystems = Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include);
        sb.AppendLine("EventSystem count: " + eventSystems.Length +
                      (eventSystems.Length == 0
                          ? "   <-- UI buttons cannot be clicked at all"
                          : ""));
        foreach (var es in eventSystems)
        {
            sb.AppendLine("  " + HierarchyPath(es.transform));
            foreach (var c in es.GetComponents<Component>())
                if (c != null && !(c is Transform))
                    sb.AppendLine("     component: " + c.GetType().Name);
        }

        foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
        {
            sb.AppendLine("Canvas '" + HierarchyPath(canvas.transform) + "'");
            sb.AppendLine("   renderMode : " + canvas.renderMode +
                          (canvas.renderMode == RenderMode.WorldSpace
                              ? ""
                              : "   <-- HeadLockedHUD only works on WorldSpace"));
            sb.AppendLine("   worldCamera: " + (canvas.worldCamera == null
                              ? "NULL" : canvas.worldCamera.name));
            // Any BaseRaycaster, not GraphicRaycaster specifically. XR scenes
            // use TrackedDeviceGraphicRaycaster, which does NOT derive from
            // GraphicRaycaster - checking for the concrete type reported
            // "buttons will not receive clicks" on a canvas that was correctly
            // set up for XR. A check that cries wolf is worse than none.
            var rc = canvas.GetComponent<UnityEngine.EventSystems.BaseRaycaster>();
            sb.AppendLine("   raycaster  : " +
                          (rc != null ? rc.GetType().Name
                                      : "NONE  <-- buttons will not receive clicks"));
            sb.AppendLine("   scale      : " + canvas.transform.localScale.ToString("F4"));
        }
        sb.AppendLine("```");
        sb.AppendLine();

        // ------------------------------------------- AprilTag readiness
        sb.AppendLine("## AprilTag / marker tracking");
        sb.AppendLine("```");
        AppendMarkerReport(sb);
        sb.AppendLine("```");
        sb.AppendLine();

        // ------------------------------------------------------- components
        var types = new List<Type>
        {
            typeof(ModeSelectMenu), typeof(ROSIPConfig), typeof(HeadLockedHUD),
            typeof(ArmRosPublisher), typeof(ShipTagFollower),
            typeof(ArenaPlacer), typeof(ArenaRun), typeof(ScoreBoard),
            typeof(GhostBot), typeof(ShipVisualLock), typeof(CenterMarker),
            typeof(RobotController), typeof(ArmController),
            typeof(Tornado), typeof(CollectibleCup), typeof(FloorSetup),
        };

        sb.AppendLine("## Components");
        foreach (var t in types) DumpType(t, sb);

        // ------------------------------------------------------ input assets
        sb.AppendLine("## Input action assets");
        sb.AppendLine("```");
        foreach (var guid in AssetDatabase.FindAssets("t:InputActionAsset"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            sb.AppendLine(path);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(path);
            if (asset == null) continue;
            foreach (var map in asset.actionMaps)
            {
                // Only our own map in full; XRI's is large and not the thing
                // under review.
                if (map.name != "Bot") continue;
                sb.AppendLine("  map: " + map.name);
                foreach (var a in map.actions)
                {
                    sb.AppendLine("    " + a.name + " (" + a.expectedControlType + ")");
                    foreach (var b in a.bindings) sb.AppendLine("       <- " + b.path);
                }
            }
        }
        sb.AppendLine("```");
        sb.AppendLine();

        // ----------------------------------------------------------- write
        string dir = Path.GetDirectoryName(Path.GetFullPath(
            Path.Combine(Application.dataPath, OutPath)));
        Directory.CreateDirectory(dir);

        string full = Path.GetFullPath(Path.Combine(Application.dataPath, OutPath));
        File.WriteAllText(full, sb.ToString());

        Debug.Log("[BotXRGame] Scene report written to " + full);
        EditorUtility.DisplayDialog("BotXRGame",
            "Scene report written to:\n\ndocs/reports/scene-report.md\n\n" +
            "Commit it, or just tell Claude it exists.", "OK");
    }

    /// <summary>
    /// Every link in the AprilTag chain, in the order it has to work.
    ///
    /// The chain is long - package, OpenXR feature, database, generated
    /// library, manager, manager enabled, source component - and a break
    /// anywhere shows up identically in the headset as "the tag does nothing".
    /// Listing them in order turns that into one glance.
    /// </summary>
    private static void AppendMarkerReport(StringBuilder sb)
    {
        // 1. Google's package
        bool pkg = FindType("Google.XR.Extensions.XRMarkerTrackingFeature") != null;
        sb.AppendLine("1 package com.google.xr.extensions : " +
                      (pkg ? "installed" : "MISSING"));

        // 2. OpenXR feature enabled for Android
        string featureState = "unknown";
        var settings = UnityEngine.XR.OpenXR.OpenXRSettings
            .GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
        {
            featureState = "no Android OpenXR settings";
        }
        else
        {
            featureState = "NOT FOUND";
            foreach (var f in settings.GetFeatures())
            {
                if (f == null) continue;
                string n = f.GetType().FullName;
                if (n == "Google.XR.Extensions.XRMarkerTrackingFeature")
                    featureState = f.enabled ? "enabled" : "DISABLED";
            }
        }
        sb.AppendLine("2 marker tracking feature (Android)  : " + featureState);

        // 3. the database asset and its entries
        string dbPath = null;
        foreach (var guid in AssetDatabase.FindAssets("MarkerDatabase"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".asset")) continue;
            var o = AssetDatabase.LoadAssetAtPath<ScriptableObject>(p);
            if (o != null && o.GetType().FullName == "Google.XR.Extensions.XRMarkerDatabase")
            {
                dbPath = p;
                break;
            }
        }

        if (dbPath == null)
        {
            sb.AppendLine("3 marker database                    : MISSING");
        }
        else
        {
            var db = AssetDatabase.LoadAssetAtPath<ScriptableObject>(dbPath);
            var so = new SerializedObject(db);
            var entries = so.FindProperty("_entries");
            sb.AppendLine("3 marker database                    : " + dbPath);
            sb.AppendLine("     entries: " + (entries != null ? entries.arraySize : 0));

            if (entries != null)
            {
                for (int i = 0; i < entries.arraySize; i++)
                {
                    var e = entries.GetArrayElementAtIndex(i);
                    sb.AppendFormat("       dict {0}  id {1}  edge {2:F3} m  all {3}\n",
                        Rel(e, "_dictionary"), Rel(e, "_markerId"),
                        RelF(e, "_physicalEdge"), Rel(e, "_allMarkers"));
                }
            }

            var lib = so.FindProperty("_imageLibrary");
            sb.AppendLine("4 reference library on database      : " +
                (lib != null && lib.objectReferenceValue != null
                    ? lib.objectReferenceValue.name
                    : "NOT CREATED  <-- press 'Create Reference Library'"));
        }

        // 5/6. the manager
        var tim = Object.FindAnyObjectByType<ARTrackedImageManager>(FindObjectsInactive.Include);
        if (tim == null)
        {
            sb.AppendLine("5 ARTrackedImageManager              : MISSING");
        }
        else
        {
            sb.AppendLine("5 ARTrackedImageManager              : on " +
                          HierarchyPath(tim.transform) +
                          (tim.enabled ? " (enabled)" : "  <-- DISABLED"));
            sb.AppendLine("6 manager referenceLibrary           : " +
                (tim.referenceLibrary != null
                    ? tim.referenceLibrary.name
                    : "NOT ASSIGNED  <-- assign the generated library"));
        }

        // 7. our adapter
        var src = Object.FindAnyObjectByType<TrackedImageTagSource>(FindObjectsInactive.Include);
        sb.AppendLine("7 TrackedImageTagSource              : " +
            (src == null ? "MISSING"
                         : HierarchyPath(src.transform) + "  status: " + src.Status));

        // 8. duplicate XR settings folders. Unity numbers a folder when it
        //    cannot write the existing one, so duplicates usually mean an
        //    earlier settings write failed - which is how a stale loader
        //    reference survives and breaks an Android build.
        var dupes = new List<string>();
        foreach (var d in AssetDatabase.GetSubFolders("Assets/XR"))
            if (d.Contains(" 1") || d.Contains(" 2") || d.Contains(" 3")) dupes.Add(d);
        foreach (var d in AssetDatabase.GetSubFolders("Assets"))
            if (d.StartsWith("Assets/XR ")) dupes.Add(d);

        sb.AppendLine("8 duplicate XR settings folders      : " +
            (dupes.Count == 0 ? "none" : string.Join(", ", dupes) +
             "  <-- an earlier XR settings write failed"));
    }

    private static string Rel(SerializedProperty p, string name)
    {
        var r = p.FindPropertyRelative(name);
        if (r == null) return "?";
        if (r.propertyType == SerializedPropertyType.Enum)
            return r.enumValueIndex >= 0 && r.enumValueIndex < r.enumDisplayNames.Length
                ? r.enumDisplayNames[r.enumValueIndex] : r.intValue.ToString();
        if (r.propertyType == SerializedPropertyType.Boolean) return r.boolValue.ToString();
        return r.intValue.ToString();
    }

    private static float RelF(SerializedProperty p, string name)
    {
        var r = p.FindPropertyRelative(name);
        return r != null ? r.floatValue : 0f;
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

    private static void DumpHierarchy(Transform t, int depth, StringBuilder sb)
    {
        // Deep hierarchies are mostly model internals and add noise without
        // adding information.
        if (depth > 3) return;

        var comps = t.GetComponents<Component>();
        var names = new List<string>();
        foreach (var c in comps)
        {
            if (c == null) { names.Add("<MISSING SCRIPT>"); continue; }
            if (c is Transform) continue;
            names.Add(c.GetType().Name);
        }

        sb.AppendLine(new string(' ', depth * 2) + t.name +
                      (t.gameObject.activeSelf ? "" : "  [inactive]") +
                      (names.Count > 0 ? "   [" + string.Join(", ", names) + "]" : ""));

        for (int i = 0; i < t.childCount; i++)
            DumpHierarchy(t.GetChild(i), depth + 1, sb);
    }

    private static void DumpType(Type t, StringBuilder sb)
    {
        var found = Object.FindObjectsByType(t, FindObjectsInactive.Include);

        sb.AppendLine();
        sb.AppendLine("### " + t.Name + "  (" + found.Length + " in scene)");

        if (found.Length == 0)
        {
            sb.AppendLine("_none_");
            return;
        }

        sb.AppendLine("```");
        foreach (var obj in found)
        {
            var comp = obj as Component;
            sb.AppendLine("on: " + (comp != null ? HierarchyPath(comp.transform) : obj.name) +
                          (comp != null && !comp.gameObject.activeInHierarchy
                              ? "   [INACTIVE]" : ""));

            var so = new SerializedObject(obj);
            var p = so.GetIterator();
            bool enterChildren = true;
            while (p.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (p.propertyPath == "m_Script") continue;
                sb.AppendLine("   " + p.propertyPath.PadRight(28) + " = " + Value(p));
            }
            sb.AppendLine();
        }
        sb.AppendLine("```");
    }

    private static string Value(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.ObjectReference:
                // The single most common failure in this project is an empty
                // reference, so make it impossible to skim past.
                return p.objectReferenceValue == null
                    ? "NULL   <-- unassigned"
                    : p.objectReferenceValue.name +
                      " (" + p.objectReferenceValue.GetType().Name + ")";

            case SerializedPropertyType.Float:   return p.floatValue.ToString("F4");
            case SerializedPropertyType.Integer: return p.intValue.ToString();
            case SerializedPropertyType.Boolean: return p.boolValue.ToString();
            case SerializedPropertyType.String:  return "\"" + p.stringValue + "\"";
            case SerializedPropertyType.Enum:
                return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length
                    ? p.enumDisplayNames[p.enumValueIndex] : p.enumValueIndex.ToString();
            case SerializedPropertyType.Vector2: return p.vector2Value.ToString("F3");
            case SerializedPropertyType.Vector3: return p.vector3Value.ToString("F3");
            case SerializedPropertyType.Color:   return p.colorValue.ToString();
            case SerializedPropertyType.ArraySize: return p.intValue + " items";
            default:
                return p.isArray ? p.arraySize + " items" : "(" + p.propertyType + ")";
        }
    }

    private static string HierarchyPath(Transform t)
    {
        string s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }
}
