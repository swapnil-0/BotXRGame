// Editor-only tooling. Lives in Assets/Editor/ so it is excluded from builds.
//
// Scene setup as code rather than as clicks:
//   * reproducible - a teammate gets an identical scene
//   * diffable - this file reviews sensibly, a .unity file does not
//   * re-runnable - safe to run again after breaking something
//
// Tools > BotXRGame > Build Tornado MVP Scene
// Tools > BotXRGame > Fit Ship To Length
// Tools > BotXRGame > Check Tornado MVP Wiring

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

public static class BotXRGameSetup
{
    private const float ArenaSize = 1.8288f;          // 6 ft
    private const string MatDir = "Assets/Materials/Generated";
    private const string PrefabDir = "Assets/Prefabs/Generated";

    // ===================================================== scene construction

    [MenuItem("Tools/BotXRGame/Build Tornado MVP Scene", false, 10)]
    public static void BuildScene()
    {
        var log = new StringBuilder();
        var todo = new List<string>();

        EnsureFolder(MatDir);
        EnsureFolder(PrefabDir);

        // --- AR managers ------------------------------------------------
        GameObject originGo = FindXrOrigin();
        if (originGo == null)
        {
            EditorUtility.DisplayDialog(
                "BotXRGame",
                "Could not find an XR Origin in the scene.\n\n" +
                "Looked for an object with an ARRaycastManager, an ARPlaneManager, " +
                "or a name containing \"XR Origin\". Add the XR Origin first, then " +
                "run this again.",
                "OK");
            return;
        }
        log.AppendLine("XR Origin: " + originGo.name);

        var planeManager = GetOrAdd<ARPlaneManager>(originGo);
        // Must start disabled: enabling it before SCENE_UNDERSTANDING_COARSE is
        // granted makes the subsystem fail silently with no planes at all.
        planeManager.enabled = false;
        log.AppendLine("ARPlaneManager added (left disabled - FloorSetup enables it)");

        var raycastManager = GetOrAdd<ARRaycastManager>(originGo);
        log.AppendLine("ARRaycastManager added");

        // --- manager object ---------------------------------------------
        GameObject manager = GameObject.Find("GameManager");
        if (manager == null)
        {
            manager = new GameObject("GameManager");
            Undo.RegisterCreatedObjectUndo(manager, "Create GameManager");
        }

        // ArenaPlacer requires ArenaRun, so Unity adds it automatically.
        var placer = GetOrAdd<ArenaPlacer>(manager);
        var run = GetOrAdd<ArenaRun>(manager);
        var floorSetup = GetOrAdd<FloorSetup>(manager);
        log.AppendLine("GameManager: ArenaPlacer + ArenaRun + FloorSetup");

        // --- visuals -----------------------------------------------------
        Material validMat = MakeTransparentMaterial("ArenaPreview", new Color(0.2f, 0.5f, 1f, 0.35f));

        GameObject preview = FindOrCreateChild(manager.transform, "ArenaPreview", PrimitiveType.Quad);
        StripCollider(preview);
        preview.transform.localScale = new Vector3(ArenaSize, ArenaSize, 1f);
        preview.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // lay flat
        var previewRenderer = preview.GetComponent<Renderer>();
        previewRenderer.sharedMaterial = validMat;
        preview.SetActive(false);

        GameObject outlineGo = FindOrCreateChild(manager.transform, "ArenaOutline", PrimitiveType.Quad, false);
        var outline = GetOrAdd<LineRenderer>(outlineGo);
        outline.useWorldSpace = true;
        outline.loop = true;
        outline.positionCount = 4;
        outline.widthMultiplier = 0.008f;
        outline.numCornerVertices = 2;
        outline.sharedMaterial = MakeTransparentMaterial("ArenaOutline", Color.white);
        outlineGo.SetActive(false);

        GameObject finish = FindOrCreateChild(manager.transform, "FinishMarker", PrimitiveType.Cylinder);
        StripCollider(finish);
        finish.transform.localScale = new Vector3(0.20f, 0.003f, 0.20f);
        finish.GetComponent<Renderer>().sharedMaterial =
            MakeTransparentMaterial("FinishMarker", new Color(0.2f, 1f, 0.4f, 0.55f));
        finish.SetActive(false);
        log.AppendLine("Preview quad, outline and finish marker created");

        // --- tornado prefab ----------------------------------------------
        GameObject tornadoPrefab = BuildTornadoPrefab();
        log.AppendLine("Tornado prefab: " + AssetDatabase.GetAssetPath(tornadoPrefab));

        // --- ship ---------------------------------------------------------
        GameObject shipRoot = GameObject.Find("ShipRoot");
        if (shipRoot == null)
        {
            shipRoot = new GameObject("ShipRoot");
            Undo.RegisterCreatedObjectUndo(shipRoot, "Create ShipRoot");
            todo.Add("Drag your spaceship model in as a CHILD of ShipRoot, then use " +
                     "Tools > BotXRGame > Fit Ship To Length. Do not scale ShipRoot itself.");
        }
        var ghost = GetOrAdd<GhostBot>(shipRoot);
        log.AppendLine("ShipRoot with GhostBot ready");

        // --- wire everything ----------------------------------------------
        Wire(placer, new Dictionary<string, Object>
        {
            { "raycastManager", raycastManager },
            { "ship", shipRoot.transform },
            { "previewSurface", previewRenderer },
            { "previewOutline", outline },
            { "finishMarker", finish.transform },
            { "tornadoPrefab", tornadoPrefab },
        });
        // Only seed the size on a fresh placer. Re-running the builder must
        // never overwrite a size the user has tuned by hand - it silently
        // shrank a 6 ft arena back to 3 ft once already.
        var soP = new SerializedObject(placer);
        var sizeProp = soP.FindProperty("arenaSize");
        if (sizeProp != null && sizeProp.floatValue < 0.01f)
        {
            sizeProp.floatValue = ArenaSize;
            soP.ApplyModifiedPropertiesWithoutUndo();
        }

        // Cups are spawned at runtime; the material must be a project asset or
        // its shader is stripped from the build and renders magenta (and only
        // in one eye under single-pass instanced XR).
        Wire(placer, new Dictionary<string, Object>
        {
            { "cupMaterial", MakeOpaqueMaterial("CupGreen", new Color(0.15f, 0.9f, 0.35f)) },
        });

        Wire(run, new Dictionary<string, Object> { { "ship", ghost } });
        Wire(floorSetup, new Dictionary<string, Object> { { "planeManager", planeManager } });

        // Ray origin: use an XR ray interactor if one exists, else leave for the user.
        Transform rayOrigin = FindRayOrigin();
        if (rayOrigin != null)
        {
            Wire(placer, new Dictionary<string, Object> { { "rayOrigin", rayOrigin } });
            log.AppendLine("Ray origin: " + rayOrigin.name);
        }
        else
        {
            todo.Add("Assign ArenaPlacer > Ray Origin to your controller or ray " +
                     "interactor transform.");
        }

        todo.Add("Assign ArenaPlacer > Place Action to your trigger InputActionReference " +
                 "(the same one RobotController uses).");
        todo.Add("Add <uses-permission android:name=\"android.permission." +
                 "SCENE_UNDERSTANDING_COARSE\" /> to Assets/Plugins/Android/AndroidManifest.xml.");
        todo.Add("Enable OpenXR Android features: AR Session, AR Plane, AR Raycast, AR Anchor, AR Camera.");
        todo.Add("If your ship has colliders, put it on its own layer and exclude that " +
                 "layer from ArenaPlacer > Obstacle Mask, or the ship detects itself " +
                 "as an obstacle and the square stays red.");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();

        log.AppendLine();
        log.AppendLine("STILL TO DO BY HAND:");
        foreach (var t in todo) log.AppendLine("  - " + t);

        Debug.Log("[BotXRGame] Scene build complete.\n\n" + log);
        EditorUtility.DisplayDialog("BotXRGame",
            "Scene built.\n\n" + todo.Count + " item(s) still need manual wiring - " +
            "see the Console for the list.", "OK");
    }

    private static GameObject BuildTornadoPrefab()
    {
        string path = PrefabDir + "/Tornado.prefab";

        var root = new GameObject("Tornado");
        var tornado = root.AddComponent<Tornado>();

        // Placeholder funnel. Unity has no cone primitive, so a cylinder stands
        // in until real art exists - the mechanic is testable either way.
        var funnel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        funnel.name = "Funnel";
        StripCollider(funnel);
        funnel.transform.SetParent(root.transform, false);
        funnel.transform.localScale = new Vector3(0.18f, 0.30f, 0.18f);
        funnel.transform.localPosition = new Vector3(0f, 0.30f, 0f);
        funnel.GetComponent<Renderer>().sharedMaterial =
            MakeTransparentMaterial("TornadoFunnel", new Color(0.55f, 0.75f, 1f, 0.40f));

        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "RadiusRing";
        StripCollider(ring);
        ring.transform.SetParent(root.transform, false);
        ring.transform.localScale = new Vector3(0.64f, 0.002f, 0.64f);   // radius 0.32 -> diameter 0.64
        ring.GetComponent<Renderer>().sharedMaterial =
            MakeTransparentMaterial("TornadoRing", new Color(0.4f, 0.7f, 1f, 0.22f));

        var so = new SerializedObject(tornado);
        so.FindProperty("funnel").objectReferenceValue = funnel.transform;
        so.FindProperty("radiusRing").objectReferenceValue = ring.transform;
        so.ApplyModifiedPropertiesWithoutUndo();

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    // ======================================================= fit ship to size

    [MenuItem("Tools/BotXRGame/Fit Ship To Length", false, 20)]
    public static void OpenFitWindow() => FitShipWindow.Open();

    /// <summary>
    /// Uniformly scale <paramref name="go"/> so its longest dimension equals
    /// <paramref name="targetLength"/>, optionally seating its base on the origin.
    /// </summary>
    public static bool FitToLength(GameObject go, float targetLength, bool alignBottom, out string message)
    {
        message = "";
        if (go == null) { message = "Nothing selected."; return false; }

        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) { message = "Selection has no Renderers."; return false; }

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

        float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (longest < 1e-5f) { message = "Bounds are degenerate."; return false; }

        float factor = targetLength / longest;

        Undo.RecordObject(go.transform, "Fit Ship To Length");
        go.transform.localScale *= factor;

        if (alignBottom)
        {
            // Recompute after scaling, then lift so the lowest point sits on the
            // parent origin - otherwise a centre-pivot model floats half-sunk.
            renderers = go.GetComponentsInChildren<Renderer>();
            b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

            Vector3 parentOrigin = go.transform.parent != null
                ? go.transform.parent.position : Vector3.zero;
            float lift = parentOrigin.y - b.min.y;
            go.transform.position += new Vector3(0f, lift, 0f);
        }

        EditorUtility.SetDirty(go);
        message = string.Format(
            "Scaled by {0:F4} ({1:F3} m -> {2:F3} m){3}",
            factor, longest, targetLength,
            alignBottom ? ", base seated on parent origin" : "");
        return true;
    }

    // ============================================================ wiring check

    [MenuItem("Tools/BotXRGame/Check Tornado MVP Wiring", false, 30)]
    public static void CheckWiring()
    {
        var problems = new List<string>();

        var placer = Object.FindAnyObjectByType<ArenaPlacer>();
        if (placer == null) problems.Add("No ArenaPlacer in the scene - run Build Tornado MVP Scene.");
        else
        {
            RequireRef(placer, "raycastManager", problems);
            RequireRef(placer, "rayOrigin", problems);
            RequireRef(placer, "ship", problems);
            RequireRef(placer, "previewSurface", problems);
            RequireRef(placer, "tornadoPrefab", problems);
            RequireRef(placer, "placeAction", problems);
        }

        var run = Object.FindAnyObjectByType<ArenaRun>();
        if (run != null) RequireRef(run, "ship", problems);

        var floor = Object.FindAnyObjectByType<FloorSetup>();
        if (floor == null) problems.Add("No FloorSetup - plane detection will never start.");
        else RequireRef(floor, "planeManager", problems);

        var planeManager = Object.FindAnyObjectByType<ARPlaneManager>();
        if (planeManager != null && planeManager.enabled)
            problems.Add("ARPlaneManager is ENABLED. It must start disabled; FloorSetup " +
                         "enables it after the permission is granted.");

        var ghost = Object.FindAnyObjectByType<GhostBot>();
        if (ghost == null) problems.Add("No GhostBot in the scene.");
        else
        {
            if (ghost.moveAction == null)
                problems.Add("GhostBot > Move Action is unassigned - the ship will not move.");
            Vector3 s = ghost.transform.localScale;
            if (Mathf.Abs(s.x - 1f) > 0.01f || Mathf.Abs(s.y - 1f) > 0.01f || Mathf.Abs(s.z - 1f) > 0.01f)
                problems.Add(string.Format(
                    "GhostBot object is scaled to {0}. Keep it at 1,1,1 and scale the " +
                    "model child instead, so movement and hover height stay in real metres.", s));
        }

        if (problems.Count == 0)
        {
            Debug.Log("[BotXRGame] Wiring check passed.");
            EditorUtility.DisplayDialog("BotXRGame", "Wiring check passed.", "OK");
        }
        else
        {
            var sb = new StringBuilder("[BotXRGame] Wiring problems:\n");
            foreach (var p in problems) sb.AppendLine("  - " + p);
            Debug.LogWarning(sb.ToString());
            EditorUtility.DisplayDialog("BotXRGame",
                problems.Count + " problem(s) found - see the Console.", "OK");
        }
    }

    // ================================================================ helpers

    private static void RequireRef(Object target, string field, List<string> problems)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { problems.Add(target.GetType().Name + "." + field + " not found."); return; }
        if (p.objectReferenceValue == null)
            problems.Add(target.GetType().Name + " > " + ObjectNames.NicifyVariableName(field) + " is unassigned.");
    }

    private static void Wire(Object target, Dictionary<string, Object> values)
    {
        var so = new SerializedObject(target);
        foreach (var kv in values)
        {
            var p = so.FindProperty(kv.Key);
            if (p == null)
            {
                Debug.LogWarning("[BotXRGame] No serialized field '" + kv.Key +
                                 "' on " + target.GetType().Name);
                continue;
            }
            p.objectReferenceValue = kv.Value;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(target);
    }

    private static void SetFloat(Object target, string field, float value)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p != null) { p.floatValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
    }

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null) c = Undo.AddComponent<T>(go);
        return c;
    }

    private static GameObject FindXrOrigin()
    {
        var rc = Object.FindAnyObjectByType<ARRaycastManager>();
        if (rc != null) return rc.gameObject;
        var pm = Object.FindAnyObjectByType<ARPlaneManager>();
        if (pm != null) return pm.gameObject;

        foreach (var t in Object.FindObjectsByType<Transform>())
            if (t.name.Contains("XR Origin")) return t.gameObject;
        return null;
    }

    private static Transform FindRayOrigin()
    {
        // Prefer an explicit ray interactor; fall back to anything named like a
        // right-hand controller. Nothing here is guaranteed, hence the fallback
        // to a manual to-do item.
        foreach (var t in Object.FindObjectsByType<Transform>())
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("ray interactor")) return t;
        }
        foreach (var t in Object.FindObjectsByType<Transform>())
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("right") && (n.Contains("controller") || n.Contains("hand"))) return t;
        }
        return null;
    }

    private static GameObject FindOrCreateChild(Transform parent, string name,
                                                PrimitiveType type, bool asPrimitive = true)
    {
        var existing = parent.Find(name);
        if (existing != null) return existing.gameObject;

        GameObject go = asPrimitive ? GameObject.CreatePrimitive(type) : new GameObject(name);
        go.name = name;
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        return go;
    }

    private static void StripCollider(GameObject go)
    {
        var c = go.GetComponent<Collider>();
        if (c != null) Object.DestroyImmediate(c);
    }

    private static Material MakeOpaqueMaterial(string name, Color colour)
    {
        string path = MatDir + "/" + name + ".mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) { existing.color = colour; return existing; }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = name, color = colour };
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    private static Material MakeTransparentMaterial(string name, Color colour)
    {
        string path = MatDir + "/" + name + ".mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) { existing.color = colour; return existing; }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Unlit/Color")
                        ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader) { name = name, color = colour };

        // URP transparency has to be configured explicitly; setting only the
        // alpha on an opaque material renders it fully solid.
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parts = path.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}

/// <summary>Small window for the fit-to-length command.</summary>
public class FitShipWindow : EditorWindow
{
    private float targetLength = 0.15f;
    private bool alignBottom = true;

    public static void Open()
    {
        var w = GetWindow<FitShipWindow>(true, "Fit Ship To Length");
        w.minSize = new Vector2(340f, 190f);
        w.Show();
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Select the SHIP MODEL (the child), not ShipRoot.\n\n" +
            "The arena is 0.914 m across and the tornado's influence radius is " +
            "0.32 m, so 0.12-0.18 m reads as a vehicle inside a storm.",
            MessageType.Info);

        targetLength = EditorGUILayout.Slider("Target length (m)", targetLength, 0.05f, 0.5f);
        alignBottom = EditorGUILayout.Toggle("Seat base on origin", alignBottom);

        var go = Selection.activeGameObject;
        EditorGUILayout.LabelField("Selected", go != null ? go.name : "(nothing)");

        EditorGUI.BeginDisabledGroup(go == null);
        if (GUILayout.Button("Fit", GUILayout.Height(28f)))
        {
            if (BotXRGameSetup.FitToLength(go, targetLength, alignBottom, out string msg))
                Debug.Log("[BotXRGame] " + msg);
            else
                Debug.LogWarning("[BotXRGame] " + msg);
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(
            "Uses world-space renderer bounds, so a rotated model may measure " +
            "slightly large. Fit before rotating for the tightest result.",
            EditorStyles.wordWrappedMiniLabel);
    }
}
