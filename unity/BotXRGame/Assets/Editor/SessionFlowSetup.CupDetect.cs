using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using Object = UnityEngine.Object;

/// <summary>
/// One-click setup for the Cup Detect test mode.
///
/// Same rules as the rest of this class: adds what is missing, fills only
/// EMPTY references, never overwrites a value already set - so it is safe to
/// re-run after dropping a new model in.
/// </summary>
public static partial class SessionFlowSetup
{
    private const string CupModelFolder = "Assets/Models/CupDetect";
    private const string CupMarkerMaterialPath = CupModelFolder + "/CupMarker.mat";

    [MenuItem("Tools/BotXRGame/Set Up Cup Detect", false, 45)]
    public static void SetUpCupDetect()
    {
        var done = new List<string>();
        var problems = new List<string>();

        // ------------------------------------------------------------- folder
        // AssetDatabase.CreateFolder, not Directory.CreateDirectory: a folder
        // made behind Unity's back is not registered, and CreateAsset into it
        // then fails - which bit this project once already.
        if (!AssetDatabase.IsValidFolder("Assets/Models"))
            AssetDatabase.CreateFolder("Assets", "Models");
        if (!AssetDatabase.IsValidFolder(CupModelFolder))
            AssetDatabase.CreateFolder("Assets/Models", "CupDetect");

        // -------------------------------------------------------- root object
        var scene = EditorSceneManager.GetActiveScene();
        GameObject root = null;
        foreach (var go in scene.GetRootGameObjects())
            if (go.name == "CupDetect") { root = go; break; }

        if (root == null)
        {
            root = new GameObject("CupDetect");
            Undo.RegisterCreatedObjectUndo(root, "Create CupDetect");
            done.Add("Created CupDetect object");
        }

        var cam = GetOrAdd<PassthroughCamera>(root);
        var detector = GetOrAdd<CupDetector>(root);
        var tracker = GetOrAdd<CupTracker>(root);
        var mode = GetOrAdd<CupDetectMode>(root);

        // Pipeline components start OFF and are switched on by the mode when
        // Cup Detect is chosen - so the other modes pay nothing for them and
        // the camera permission prompt only appears when it is needed.
        cam.enabled = false;
        detector.enabled = false;
        tracker.enabled = false;
        done.Add("PassthroughCamera, CupDetector, CupTracker (off until chosen), CupDetectMode");

        // -------------------------------------------------------------- model
        var model = FindCupModel();
        if (model != null)
        {
            Wire(detector, new Dictionary<string, Object> { { "modelAsset", model } });
            done.Add("Model: " + AssetDatabase.GetAssetPath(model));
        }
        else
        {
            problems.Add("NO MODEL in " + CupModelFolder + ". Run tools/export_cup_model.py, " +
                         "copy the .onnx there, then run this again.");
        }

        // ---------------------------------------------------- marker material
        // A material ASSET referenced from the scene forces its shader into the
        // build. A runtime Shader.Find can come back stripped, which is the
        // leading suspect for the invisible START button.
        var mat = AssetDatabase.LoadAssetAtPath<Material>(CupMarkerMaterialPath);
        if (mat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null)
            {
                mat = new Material(sh) { name = "CupMarker" };
                mat.SetColor("_BaseColor", new Color(0.2f, 1f, 0.4f, 1f));
                AssetDatabase.CreateAsset(mat, CupMarkerMaterialPath);
                done.Add("Created " + CupMarkerMaterialPath);
            }
            else problems.Add("URP Unlit shader not found - markers will use the default material");
        }

        // ----------------------------------------------------------------- HUD
        var ipConfig = Object.FindAnyObjectByType<ROSIPConfig>(FindObjectsInactive.Include);
        GameObject hudPanel = ipConfig != null ? GetObjectField(ipConfig, "hudPanel") : null;

        TMPro.TextMeshProUGUI text = null;
        RawImage preview = null;

        if (hudPanel == null)
        {
            problems.Add("No HUD panel (ROSIPConfig.hudPanel) - Cup Detect will have no readout");
        }
        else
        {
            var textGo = FindOrCreateUiChild(hudPanel.transform, "CupDetectText");
            text = GetOrAdd<TMPro.TextMeshProUGUI>(textGo);
            text.fontSize = 15;
            text.color = new Color(0.85f, 0.95f, 1f);
            text.alignment = TMPro.TextAlignmentOptions.TopLeft;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            text.text = "";

            var trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(10f, 10f);
            trt.offsetMax = new Vector2(-10f, -10f);

            // Bottom-right, 4:3, clear of the text that starts top-left.
            var prevGo = FindOrCreateUiChild(hudPanel.transform, "CupDetectPreview");
            preview = GetOrAdd<RawImage>(prevGo);
            preview.raycastTarget = false;
            preview.color = Color.white;

            var prt = prevGo.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = new Vector2(1f, 0f);
            prt.pivot = new Vector2(1f, 0f);
            prt.anchoredPosition = new Vector2(-10f, 10f);
            prt.sizeDelta = new Vector2(280f, 210f);
            prevGo.SetActive(false);

            done.Add("HUD: CupDetectText + CupDetectPreview under " + hudPanel.name);
        }

        // -------------------------------------------------------------- wiring
        var raycaster = Object.FindAnyObjectByType<ARRaycastManager>(FindObjectsInactive.Include);
        var placer = Object.FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include);
        Transform rayOrigin = placer != null ? GetTransformField(placer, "rayOrigin") : null;

        Wire(detector, new Dictionary<string, Object> { { "cam", cam } });

        var trackerWires = new Dictionary<string, Object> { { "detector", detector }, { "cam", cam } };
        if (raycaster != null) trackerWires["raycastManager"] = raycaster;
        if (mat != null) trackerWires["markerMaterial"] = mat;
        Wire(tracker, trackerWires);

        var modeWires = new Dictionary<string, Object>
        {
            { "cam", cam }, { "detector", detector }, { "tracker", tracker },
        };
        if (text != null) modeWires["display"] = text;
        if (preview != null) modeWires["preview"] = preview;
        if (raycaster != null) modeWires["raycastManager"] = raycaster;
        if (rayOrigin != null) modeWires["rayOrigin"] = rayOrigin;

        AddAction(modeWires, "moveAction", "Move", problems);
        AddAction(modeWires, "triggerAction", "Place", problems);
        AddAction(modeWires, "aAction", "Swing", problems);
        AddAction(modeWires, "bAction", "Kick", problems);
        AddAction(modeWires, "gripAction", "Menu", problems);
        Wire(mode, modeWires);
        done.Add("CupDetectMode wired (stick, trigger, A, B, grip)");

        if (raycaster == null) problems.Add("No ARRaycastManager - floor comes from trigger calibration only");

        // ------------------------------------------------------------- finish
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();

        var msg = "Done:\n- " + string.Join("\n- ", done);
        if (problems.Count > 0) msg += "\n\nCheck:\n- " + string.Join("\n- ", problems);
        msg += "\n\nAlso run Wire Session Flow once, for the fourth menu button. Then save the scene.";

        Debug.Log("[CupDetect setup] " + msg.Replace("\n", " "));
        EditorUtility.DisplayDialog("Set Up Cup Detect", msg, "OK");
    }

    private static void AddAction(Dictionary<string, Object> wires, string field,
                                  string action, List<string> problems)
    {
        var r = FindActionReference("Bot", action);
        if (r != null) wires[field] = r;
        else problems.Add("Input action Bot/" + action + " not found");
    }

    /// <summary>
    /// The model in the CupDetect folder; failing that, any imported model with
    /// 'yolo' in its name, so a model dropped elsewhere still gets found.
    /// </summary>
    private static Unity.InferenceEngine.ModelAsset FindCupModel()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:ModelAsset", new[] { CupModelFolder }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(AssetDatabase.GUIDToAssetPath(guid));
            if (m != null) return m;
        }

        foreach (var guid in AssetDatabase.FindAssets("yolo t:ModelAsset"))
        {
            var m = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(AssetDatabase.GUIDToAssetPath(guid));
            if (m != null) return m;
        }
        return null;
    }
}
