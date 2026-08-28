using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// One-click wiring for the session flow: mode menu, head-locked HUD, arm
/// publisher and tag follower.
///
/// Separate file from BotXRGameSetup deliberately. That one owns the arena and
/// score board and is 600 lines; adding to it risks disturbing a scene builder
/// that already silently reset arenaSize once. This only ever ADDS components
/// and wires empty references - it does not overwrite values that are already
/// set, for the same reason.
/// </summary>
public static class SessionFlowSetup
{
    [MenuItem("Tools/BotXRGame/Wire Session Flow", false, 40)]
    public static void WireSessionFlow()
    {
        var problems = new List<string>();
        var done = new List<string>();

        var ipConfig = Object.FindAnyObjectByType<ROSIPConfig>();
        if (ipConfig == null)
        {
            EditorUtility.DisplayDialog("BotXRGame",
                "No ROSIPConfig found in the scene.\n\nThe mode menu attaches to " +
                "the same canvas as the IP panel, so that has to exist first.",
                "OK");
            return;
        }

        // --------------------------------------------------- find the canvas
        GameObject ipPanel = GetObjectField(ipConfig, "ipInputPanel");
        GameObject hudPanel = GetObjectField(ipConfig, "hudPanel");

        if (ipPanel == null)
        {
            EditorUtility.DisplayDialog("BotXRGame",
                "ROSIPConfig.ipInputPanel is not assigned. Assign it first - " +
                "the mode panel is built as its sibling.", "OK");
            return;
        }

        Transform canvasRoot = ipPanel.transform.parent != null
            ? ipPanel.transform.parent
            : ipPanel.transform;

        // ------------------------------------------------ build the mode panel
        GameObject modePanel = FindOrCreateUiChild(canvasRoot, "ModePanel");
        var modeRt = modePanel.GetComponent<RectTransform>();
        Stretch(modeRt);

        var bgGo = FindOrCreateUiChild(modePanel.transform, "Background");
        var bg = GetOrAdd<Image>(bgGo);
        bg.color = new Color(0.05f, 0.07f, 0.10f, 0.88f);
        Stretch(bgGo.GetComponent<RectTransform>());

        var title = MakeText(modePanel.transform, "Title", 56,
                             TMPro.TextAlignmentOptions.Center,
                             new Vector2(0f, 170f), new Vector2(700f, 90f));
        var help = MakeText(modePanel.transform, "Help", 26,
                            TMPro.TextAlignmentOptions.Center,
                            new Vector2(0f, 90f), new Vector2(760f, 90f));
        help.color = new Color(0.70f, 0.78f, 0.88f);

        Button virtualBtn = MakeButton(modePanel.transform, "VirtualBotButton",
                                       "Virtual Bot",
                                       new Vector2(0f, -10f),
                                       new Color(0.16f, 0.42f, 0.28f));
        Button tagBtn = MakeButton(modePanel.transform, "AprilTagButton",
                                   "AprilTag (real bot)",
                                   new Vector2(0f, -120f),
                                   new Color(0.18f, 0.32f, 0.52f));

        done.Add("ModePanel with two buttons");

        // --------------------------------------------------- ModeSelectMenu
        var menuHost = ipConfig.gameObject;
        var menu = GetOrAdd<ModeSelectMenu>(menuHost);
        Wire(menu, new Dictionary<string, Object>
        {
            { "modePanel",        modePanel },
            { "virtualBotButton", virtualBtn },
            { "aprilTagButton",   tagBtn },
            { "titleText",        title },
            { "helpText",         help },
            { "ipConfig",         ipConfig },
        });
        done.Add("ModeSelectMenu wired to ROSIPConfig");

        // ---------------------------------------------------- HeadLockedHUD
        if (hudPanel != null)
        {
            var hl = GetOrAdd<HeadLockedHUD>(hudPanel);
            Wire(hl, new Dictionary<string, Object> { { "panel", hudPanel.transform } });
            done.Add("HeadLockedHUD on " + hudPanel.name);

            var canvas = canvasRoot.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.WorldSpace)
            {
                problems.Add(
                    "Canvas '" + canvas.name + "' render mode is " + canvas.renderMode +
                    ", not WorldSpace. HeadLockedHUD moves a world transform, which " +
                    "does nothing on an overlay canvas.");
            }
        }
        else
        {
            problems.Add("ROSIPConfig.hudPanel is empty - HeadLockedHUD not added.");
        }

        // --------------------------------------------------- ArmRosPublisher
        var arm = Object.FindAnyObjectByType<ArmController>();
        GameObject armHost = arm != null ? arm.gameObject : menuHost;
        var armPub = GetOrAdd<ArmRosPublisher>(armHost);

        var armWires = new Dictionary<string, Object>();
        if (arm != null)
        {
            armWires["localArm"] = arm;

            // Reuse whatever button the local arm already listens to, so the
            // real and virtual swings cannot end up on different buttons.
            var existingAction = GetObjectFieldGeneric(arm, "swingAction");
            if (existingAction != null) armWires["swingAction"] = existingAction;
            else problems.Add("ArmController.swingAction is empty - set the swing " +
                              "button there and re-run this, or assign " +
                              "ArmRosPublisher.swingAction by hand.");
        }
        else
        {
            problems.Add("No ArmController in the scene - ArmRosPublisher added to " +
                         ipConfig.name + " but swingAction must be assigned by hand.");
        }
        Wire(armPub, armWires);
        done.Add("ArmRosPublisher on " + armHost.name);

        // -------------------------------------------------- ShipTagFollower
        var placer = Object.FindAnyObjectByType<ArenaPlacer>();
        Transform shipT = placer != null ? GetTransformField(placer, "ship") : null;

        GameObject followHost = shipT != null ? shipT.gameObject : menuHost;
        var follower = GetOrAdd<ShipTagFollower>(followHost);

        var followWires = new Dictionary<string, Object>();
        if (shipT != null)
        {
            var bot = shipT.GetComponent<GhostBot>();
            if (bot != null) followWires["ship"] = bot;
        }
        Wire(follower, followWires);
        done.Add("ShipTagFollower on " + followHost.name);

        problems.Add("ShipTagFollower.tagTransform must be assigned by hand - it " +
                     "depends on which tracker you use. Any transform works for a " +
                     "bench test; AprilTag mode falls back to joystick without it.");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Selection.activeGameObject = modePanel;

        string msg = "Wired:\n  " + string.Join("\n  ", done);
        if (problems.Count > 0)
            msg += "\n\nStill needs you:\n  " + string.Join("\n  ", problems);

        Debug.Log("[BotXRGame] " + msg);
        EditorUtility.DisplayDialog("BotXRGame - Session Flow", msg, "OK");
    }

    // ============================================================== helpers

    /// <summary>
    /// Assigns only EMPTY reference fields. Never overwrites a value already
    /// set, so re-running is safe and hand-tuned wiring survives.
    /// </summary>
    private static void Wire(Object target, Dictionary<string, Object> values)
    {
        if (target == null || values == null || values.Count == 0) return;

        var so = new SerializedObject(target);
        foreach (var kv in values)
        {
            var p = so.FindProperty(kv.Key);
            if (p == null)
            {
                Debug.LogWarning("[BotXRGame] no field '" + kv.Key + "' on " +
                                 target.GetType().Name);
                continue;
            }
            if (p.objectReferenceValue == null) p.objectReferenceValue = kv.Value;
        }
        so.ApplyModifiedProperties();
    }

    private static GameObject GetObjectField(Object target, string field)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        return p != null ? p.objectReferenceValue as GameObject : null;
    }

    private static Object GetObjectFieldGeneric(Object target, string field)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        return p != null ? p.objectReferenceValue : null;
    }

    private static Transform GetTransformField(Object target, string field)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        return p != null ? p.objectReferenceValue as Transform : null;
    }

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null) c = Undo.AddComponent<T>(go);
        return c;
    }

    private static GameObject FindOrCreateUiChild(Transform parent, string name)
    {
        var existing = parent.Find(name);
        if (existing != null) return existing.gameObject;
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        return go;
    }

    private static void Stretch(RectTransform r)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = Vector2.zero;
        r.offsetMax = Vector2.zero;
    }

    private static TMPro.TextMeshProUGUI MakeText(
        Transform parent, string name, float size,
        TMPro.TextAlignmentOptions align, Vector2 pos, Vector2 sizeDelta)
    {
        var go = FindOrCreateUiChild(parent, name);
        var t = GetOrAdd<TMPro.TextMeshProUGUI>(go);
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.text = name;

        var r = go.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = pos;
        r.sizeDelta = sizeDelta;
        return t;
    }

    private static Button MakeButton(Transform parent, string name, string label,
                                     Vector2 pos, Color colour)
    {
        var go = FindOrCreateUiChild(parent, name);
        var img = GetOrAdd<Image>(go);
        img.color = colour;

        var btn = GetOrAdd<Button>(go);
        btn.targetGraphic = img;

        var r = go.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = pos;
        r.sizeDelta = new Vector2(520f, 90f);

        var t = MakeText(go.transform, "Label", 34,
                         TMPro.TextAlignmentOptions.Center,
                         Vector2.zero, new Vector2(500f, 80f));
        t.text = label;
        Stretch(t.GetComponent<RectTransform>());

        return btn;
    }
}
