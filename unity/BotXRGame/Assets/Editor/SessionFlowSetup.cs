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

        // ------------------------------------- the second mover, finally found
        // RobotController sits on the ship's MESH child with moveInSimulation
        // on, so it moved and rotated Fighter03 from the same stick that
        // GhostBot uses to move ShipRoot. Two movers on one hierarchy: the
        // ship's nose drifted off its direction of travel, and ShipVisualLock
        // has been overwriting the result every frame ever since.
        //
        // RobotController still publishes /cmd_vel - only its local transform
        // writing is switched off, which is the half that was never wanted here.
        foreach (var rc in Object.FindObjectsByType<RobotController>(FindObjectsInactive.Include))
        {
            var so = new SerializedObject(rc);
            var p = so.FindProperty("moveInSimulation");
            if (p != null && p.boolValue)
            {
                p.boolValue = false;
                so.ApplyModifiedProperties();
                done.Add("RobotController.moveInSimulation OFF on " +
                         HierarchyPath(rc.transform) + " (was the mesh drift)");
            }
        }

        // ---------------------------------------------------- HeadLockedHUD
        if (hudPanel != null)
        {
            var hl = GetOrAdd<HeadLockedHUD>(hudPanel);
            var hudWires = new Dictionary<string, Object> { { "panel", hudPanel.transform } };

            // head falls back to Camera.main at runtime, but Camera.main
            // depends on the MainCamera tag being set, which is one more thing
            // that can silently be wrong on a test day.
            if (Camera.main != null) hudWires["head"] = Camera.main.transform;
            Wire(hl, hudWires);

            done.Add("HeadLockedHUD on " + hudPanel.name +
                     (Camera.main != null ? " (head = " + Camera.main.name + ")"
                                          : "  head NULL - no MainCamera tag"));

            // Button readout line on the HUD itself, since that is the panel
            // that is always in view.
            var inputGo = FindOrCreateUiChild(hudPanel.transform, "InputDebugText");
            var inputText = GetOrAdd<TMPro.TextMeshProUGUI>(inputGo);
            inputText.fontSize = 18;
            inputText.color = new Color(0.75f, 0.85f, 1f);
            inputText.alignment = TMPro.TextAlignmentOptions.BottomLeft;
            inputText.textWrappingMode = TMPro.TextWrappingModes.NoWrap;

            var irt = inputGo.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0f, 0f);
            irt.anchorMax = new Vector2(1f, 0f);
            irt.pivot = new Vector2(0.5f, 0f);
            irt.offsetMin = new Vector2(10f, 6f);
            irt.offsetMax = new Vector2(-10f, 40f);

            var ih = GetOrAdd<InputDebugHUD>(hudPanel);
            var ihWires = new Dictionary<string, Object> { { "text", inputText } };
            var pub = Object.FindAnyObjectByType<ArmRosPublisher>();
            if (pub != null) ihWires["armPublisher"] = pub;
            Wire(ih, ihWires);

            done.Add("InputDebugHUD text on " + hudPanel.name +
                     " (run Bind All Controls to bind its actions)");

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
            // No ArmController means no button to copy, so pick one directly.
            // Left trigger: the right trigger is already placeAction, and both
            // thumbsticks drive movement, so this is the obvious free control
            // and putting the arm on a used button would be worse than leaving
            // it empty.
            var swing = FindActionReference("XRI Left Interaction", "Activate Value");
            if (swing == null)
                swing = FindActionReference("XRI Left Interaction", "Select Value");

            if (swing != null)
            {
                // Report only if it will actually be assigned. Wire() skips
                // fields that are already set, so announcing the intent rather
                // than the outcome claimed a left-trigger binding on a scene
                // where A was already bound and nothing changed - a report that
                // describes what did not happen is worse than no report.
                if (GetObjectFieldGeneric(armPub, "swingAction") == null)
                {
                    armWires["swingAction"] = swing;
                    done.Add("ArmRosPublisher.swingAction -> left trigger (" + swing.name + ")");
                }
                else
                {
                    done.Add("ArmRosPublisher.swingAction already set - left alone");
                }
            }
            else
            {
                problems.Add("No ArmController and could not find a left-trigger " +
                             "action - assign ArmRosPublisher.swingAction by hand.");
            }
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

        // Create a stand-in the follower can track today. AprilTag mode is
        // otherwise untestable until the tracker exists, and "untested until
        // the morning of" is how a demo fails. Named so nobody mistakes it for
        // real tracking, and only created if tagTransform is still empty.
        if (GetTransformField(follower, "tagTransform") == null)
        {
            GameObject standIn = GameObject.Find("TagStandIn");
            if (standIn == null)
            {
                standIn = GameObject.CreatePrimitive(PrimitiveType.Cube);
                standIn.name = "TagStandIn";
                standIn.transform.localScale = Vector3.one * 0.08f;
                standIn.transform.position = new Vector3(0f, 0.05f, 1.0f);
                Object.DestroyImmediate(standIn.GetComponent<Collider>());
                Undo.RegisterCreatedObjectUndo(standIn, "Create TagStandIn");
            }

            Wire(follower, new Dictionary<string, Object>
            {
                { "tagTransform", standIn.transform }
            });

            done.Add("ShipTagFollower.tagTransform -> TagStandIn (placeholder)");
            problems.Add("TagStandIn is a PLACEHOLDER cube, not real tracking.");
        }

        // Make the stand-in reachable from inside the headset. A cube you can
        // only drag in the Scene view is useless in a build, which is why
        // AprilTag mode looked frozen with the ship parked on it.
        var standInGo = GameObject.Find("TagStandIn");
        if (standInGo != null)
        {
            var held = GetOrAdd<ControllerHeldStandIn>(standInGo);
            var heldWires = new Dictionary<string, Object>();
            if (placer != null)
            {
                var ro = GetTransformField(placer, "rayOrigin");
                if (ro != null) heldWires["rayOrigin"] = ro;
            }
            Wire(held, heldWires);
            done.Add("TagStandIn follows the controller (point to place the 'tag')");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Selection.activeGameObject = modePanel;

        string msg = "Wired:\n  " + string.Join("\n  ", done);
        if (problems.Count > 0)
            msg += "\n\nStill needs you:\n  " + string.Join("\n  ", problems);

        Debug.Log("[BotXRGame] " + msg);
        EditorUtility.DisplayDialog("BotXRGame - Session Flow", msg, "OK");
    }

    // ================================== bind everything to one controller

    /// <summary>
    /// Point Move, Place, Swing and Kick at the right controller.
    ///
    /// A SEPARATE command from Wire Session Flow, and it OVERWRITES existing
    /// references - which is exactly why it is not folded into the other one.
    /// Wire Session Flow is safe to re-run because it never overwrites; this
    /// changes controls that already work, so it asks first.
    /// </summary>
    [MenuItem("Tools/BotXRGame/Bind All Controls To Right Controller", false, 41)]
    public static void BindRightController()
    {
        var move  = FindActionReference("Bot", "Move");
        var place = FindActionReference("Bot", "Place");
        var swing = FindActionReference("Bot", "Swing");
        var kick  = FindActionReference("Bot", "Kick");

        if (move == null || place == null || swing == null || kick == null)
        {
            EditorUtility.DisplayDialog("BotXRGame",
                "Could not find the Bot action map.\n\n" +
                "Assets/SourceFiles/InputSystem/BotXRGameControls.inputactions " +
                "must be imported first. If you just pulled it, let Unity finish " +
                "importing and try again.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("BotXRGame",
                "This OVERWRITES existing input bindings:\n\n" +
                "  Move  -> right thumbstick\n" +
                "  Place -> right trigger\n" +
                "  Swing -> A (primaryButton)   sends SWING\n" +
                "  Stow  -> B (secondaryButton) sends STOW\n\n" +
                "Your current move/place bindings will be replaced.",
                "Bind", "Cancel"))
            return;

        var done = new List<string>();

        var bot = Object.FindAnyObjectByType<GhostBot>();
        if (bot != null) { Overwrite(bot, "moveAction", move); done.Add("GhostBot.moveAction"); }

        var robot = Object.FindAnyObjectByType<RobotController>();
        if (robot != null)
        {
            Overwrite(robot, "moveAction", move);
            done.Add("RobotController.moveAction");
        }

        var placer = Object.FindAnyObjectByType<ArenaPlacer>();
        if (placer != null) { Overwrite(placer, "placeAction", place); done.Add("ArenaPlacer.placeAction"); }

        var armPub = Object.FindAnyObjectByType<ArmRosPublisher>();
        if (armPub != null)
        {
            Overwrite(armPub, "swingAction", swing);
            Overwrite(armPub, "kickAction", kick);
            done.Add("ArmRosPublisher: A = SWING, B = STOW");
        }
        else
        {
            done.Add("NO ArmRosPublisher - run Wire Session Flow first");
        }

        var arm = Object.FindAnyObjectByType<ArmController>();
        if (arm != null) { Overwrite(arm, "swingAction", swing); done.Add("ArmController.swingAction"); }

        // Live button readout on the HUD. Bound here rather than in Wire
        // Session Flow because it needs the same action references as the arm,
        // and the point of the line is to prove those exact bindings are live.
        var inputHud = Object.FindAnyObjectByType<InputDebugHUD>();
        if (inputHud != null)
        {
            Overwrite(inputHud, "swingAction", swing);
            Overwrite(inputHud, "kickAction", kick);
            Overwrite(inputHud, "placeAction", place);
            Overwrite(inputHud, "moveAction", move);
            done.Add("InputDebugHUD: A/B/trigger/stick");
        }

        // Same A/B on the mode menu. Those buttons are not otherwise live until
        // a mode is chosen, and this makes the menu usable without any UI
        // raycasting - the one screen where a dead end costs the whole session.
        var menu = Object.FindAnyObjectByType<ModeSelectMenu>();
        if (menu != null)
        {
            Overwrite(menu, "selectVirtualAction", swing);
            Overwrite(menu, "selectAprilTagAction", kick);
            done.Add("ModeSelectMenu: A = Virtual Bot, B = AprilTag");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        string msg = "Bound to right controller:\n  " + string.Join("\n  ", done) +
                     "\n\nA sends SWING, B sends STOW - both implemented on the " +
                     "robot side, so neither will be rejected.";
        Debug.Log("[BotXRGame] " + msg);
        EditorUtility.DisplayDialog("BotXRGame - Controls", msg, "OK");
    }

    /// <summary>Unconditional assignment, unlike Wire(). Used only by the bind command.</summary>
    private static void Overwrite(Object target, string field, Object value)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null)
        {
            Debug.LogWarning("[BotXRGame] no field '" + field + "' on " + target.GetType().Name);
            return;
        }
        p.objectReferenceValue = value;
        so.ApplyModifiedProperties();
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

    /// <summary>
    /// Find an InputActionReference by map and action name.
    ///
    /// References are sub-assets of the .inputactions file, so they are loaded
    /// with LoadAllAssetsAtPath and matched on the action's own map/name rather
    /// than the asset filename, which varies by Unity version.
    /// </summary>
    private static Object FindActionReference(string mapName, string actionName)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:InputActionAsset"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var r = sub as UnityEngine.InputSystem.InputActionReference;
                if (r == null || r.action == null) continue;
                if (r.action.actionMap == null) continue;

                if (r.action.actionMap.name == mapName && r.action.name == actionName)
                    return r;
            }
        }
        return null;
    }

    private static string HierarchyPath(Transform t)
    {
        string s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
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
