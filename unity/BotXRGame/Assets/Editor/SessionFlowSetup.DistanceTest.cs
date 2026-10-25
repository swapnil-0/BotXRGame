using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Tools > BotXRGame > Set Up Distance Test: the DistanceTest object, its HUD
/// text and marker material, wired to the ray caster, the right controller's
/// aim and the trigger/A/B actions. Fills EMPTY fields only - safe to re-run.
/// The menu row comes from ExperienceCatalog via Wire Session Flow.
/// </summary>
public static partial class SessionFlowSetup
{
    private const string DistanceFolder = "Assets/Models/DistanceTest";
    private const string DistanceMaterialPath = DistanceFolder + "/DistanceMarker.mat";

    [MenuItem("Tools/BotXRGame/Set Up Distance Test", false, 46)]
    public static void SetUpDistanceTest()
    {
        var done = new List<string>();
        var problems = new List<string>();

        if (!AssetDatabase.IsValidFolder("Assets/Models"))
            AssetDatabase.CreateFolder("Assets", "Models");
        if (!AssetDatabase.IsValidFolder(DistanceFolder))
            AssetDatabase.CreateFolder("Assets/Models", "DistanceTest");

        // -------------------------------------------------------- root object
        var scene = EditorSceneManager.GetActiveScene();
        GameObject root = null;
        foreach (var go in scene.GetRootGameObjects())
            if (go.name == "DistanceTest") { root = go; break; }

        if (root == null)
        {
            root = new GameObject("DistanceTest");
            Undo.RegisterCreatedObjectUndo(root, "Create DistanceTest");
            done.Add("Created DistanceTest object");
        }
        var mode = GetOrAdd<DistanceTestMode>(root);

        // ----------------------------------------------------------- material
        // An asset referenced from the scene, so the shader is in the build.
        var mat = AssetDatabase.LoadAssetAtPath<Material>(DistanceMaterialPath);
        if (mat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null)
            {
                mat = new Material(sh) { name = "DistanceMarker" };
                mat.SetColor("_BaseColor", new Color(0.25f, 0.85f, 1f, 1f));
                AssetDatabase.CreateAsset(mat, DistanceMaterialPath);
                done.Add("Created " + DistanceMaterialPath);
            }
            else problems.Add("URP Unlit shader not found - markers will use the default material");
        }

        // ---------------------------------------------------------------- HUD
        var ipConfig = Object.FindAnyObjectByType<ROSIPConfig>(FindObjectsInactive.Include);
        GameObject hudPanel = ipConfig != null ? GetObjectField(ipConfig, "hudPanel") : null;
        TMPro.TextMeshProUGUI text = null;

        if (hudPanel == null)
        {
            problems.Add("No HUD panel (ROSIPConfig.hudPanel) - Distance Test will have no readout");
        }
        else
        {
            var textGo = FindOrCreateUiChild(hudPanel.transform, "DistanceTestText");
            text = GetOrAdd<TMPro.TextMeshProUGUI>(textGo);
            text.fontSize = 15;
            text.color = new Color(0.85f, 0.95f, 1f);
            text.alignment = TMPro.TextAlignmentOptions.TopLeft;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            text.text = "";

            var trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(10f, 10f);
            trt.offsetMax = new Vector2(-10f, -10f);
            done.Add("HUD: DistanceTestText under " + hudPanel.name);
        }

        // -------------------------------------------------------------- wiring
        var raycaster = Object.FindAnyObjectByType<ARRaycastManager>(FindObjectsInactive.Include);
        var placer = Object.FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include);
        Transform rayOrigin = placer != null ? GetTransformField(placer, "rayOrigin") : null;

        var wires = new Dictionary<string, Object>();
        if (text != null) wires["display"] = text;
        if (raycaster != null) wires["raycastManager"] = raycaster;
        else problems.Add("No ARRaycastManager in the scene - nothing can be measured");
        if (rayOrigin != null) wires["rayOrigin"] = rayOrigin;
        else problems.Add("No ArenaPlacer.rayOrigin - set DistanceTestMode.rayOrigin to the right controller's aim");
        if (Camera.main != null) wires["head"] = Camera.main.transform;
        if (mat != null) wires["markerMaterial"] = mat;

        AddAction(wires, "triggerAction", "Place", problems);
        AddAction(wires, "aAction", "Swing", problems);
        AddAction(wires, "bAction", "Kick", problems);
        Wire(mode, wires);
        done.Add("DistanceTestMode wired (trigger, A, B, ray, HUD)");

        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();

        var msg = "Done:\n- " + string.Join("\n- ", done);
        if (problems.Count > 0) msg += "\n\nCheck:\n- " + string.Join("\n- ", problems);
        msg += "\n\nRun Wire Session Flow for the menu entry, then save the scene.";

        Debug.Log("[DistanceTest setup] " + msg.Replace("\n", " "));
        EditorUtility.DisplayDialog("Set Up Distance Test", msg, "OK");
    }
}
