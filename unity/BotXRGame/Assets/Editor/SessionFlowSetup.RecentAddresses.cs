using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tools > BotXRGame > Build Recent Address Row: replaces the connect panel's
/// single "Recent IPs" cycle button with a row of address buttons, each
/// labelled with the ip:port it loads (ROSIPConfig.recentButtons).
///
/// The old button cycled through a list you could not see, and the scene's
/// serialized seeds were two stale demo addresses, so it looked dead. This
/// OVERWRITES the panel layout and ROSIPConfig.seedAddresses (rule 1: a code
/// default never reaches an already-serialized field). Safe to re-run.
/// </summary>
public static partial class SessionFlowSetup
{
    private const int RecentSlots = 3;

    private static readonly string[] RecentSeeds =
    {
        "192.168.1.204",   // test RubikPi, wired - link test passed 2026-10-29
        "192.168.1.245",   // test RubikPi, wlan0
        "192.168.1.214",   // robot IQ9, last seen; DHCP
    };

    [MenuItem("Tools/BotXRGame/Build Recent Address Row", false, 48)]
    public static void BuildRecentAddressRow()
    {
        var ipConfig = Object.FindAnyObjectByType<ROSIPConfig>(FindObjectsInactive.Include);
        if (ipConfig == null) { Debug.LogError("[Recent] no ROSIPConfig in the scene"); return; }
        var panel = GetObjectField(ipConfig, "ipInputPanel");
        if (panel == null) { Debug.LogError("[Recent] ROSIPConfig.ipInputPanel not set"); return; }

        // The old cycle button goes; its fields are cleared so nothing points
        // at a destroyed object.
        var old = panel.transform.Find("RecentIPButton");
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

        // Title up a little to free a row above the IP field (800x600 canvas,
        // IP field top edge at y=110).
        var title = panel.transform.Find("TitleText");
        if (title != null)
        {
            var tr = title.GetComponent<RectTransform>();
            tr.anchoredPosition = new Vector2(0f, 252f);
            tr.sizeDelta = new Vector2(600f, 60f);
        }

        var header = MakeText(panel.transform, "RecentHeader", 18,
                              TMPro.TextAlignmentOptions.Center,
                              new Vector2(0f, 204f), new Vector2(660f, 24f));
        header.text = "Recent - tap to fill in, then Connect";
        header.color = new Color(0.70f, 0.78f, 0.88f);
        header.raycastTarget = false;

        var buttons = new Button[RecentSlots];
        for (int i = 0; i < RecentSlots; i++)
        {
            var go = FindOrCreateUiChild(panel.transform, "RecentIP_" + i);
            var img = GetOrAdd<Image>(go);
            img.color = new Color(0.22f, 0.30f, 0.42f);
            var btn = GetOrAdd<Button>(go);
            btn.targetGraphic = img;

            var r = go.GetComponent<RectTransform>();
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = new Vector2((i - 1) * 245f, 150f);
            r.sizeDelta = new Vector2(230f, 64f);

            var label = MakeText(go.transform, "Label", 22,
                                 TMPro.TextAlignmentOptions.Center,
                                 Vector2.zero, new Vector2(220f, 60f));
            label.text = RecentSeeds[i];
            label.raycastTarget = false;   // the button's Image takes the ray
            Stretch(label.GetComponent<RectTransform>());
            buttons[i] = btn;
        }

        // ROSIPConfig was found DISABLED in the scene (unticked by hand at some
        // point): its Start() never ran, so the recent list, the button
        // listeners and the last-used IP/port restore were all dead, while
        // Connect still worked through its persistent onClick. That was the
        // "Recent IPs does nothing" bug all along.
        if (!ipConfig.enabled)
        {
            Undo.RecordObject(ipConfig, "Enable ROSIPConfig");
            ipConfig.enabled = true;
            Debug.Log("[Recent] ROSIPConfig was disabled - enabled it");
        }

        var so = new SerializedObject(ipConfig);
        var arr = so.FindProperty("recentButtons");
        arr.arraySize = RecentSlots;
        for (int i = 0; i < RecentSlots; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
        so.FindProperty("recentButton").objectReferenceValue = null;
        so.FindProperty("recentLabel").objectReferenceValue = null;
        var seeds = so.FindProperty("seedAddresses");
        seeds.arraySize = RecentSeeds.Length;
        for (int i = 0; i < RecentSeeds.Length; i++)
            seeds.GetArrayElementAtIndex(i).stringValue = RecentSeeds[i];
        so.ApplyModifiedProperties();

        // Virtual Bot never talks to the robot: skip the connect screen. Left
        // on, the panel floated over the game unless Skip was pressed.
        var menu = Object.FindAnyObjectByType<ModeSelectMenu>(FindObjectsInactive.Include);
        if (menu != null && !menu.skipIpConfigForVirtualBot)
        {
            Undo.RecordObject(menu, "Skip connect screen for Virtual Bot");
            menu.skipIpConfigForVirtualBot = true;
            Debug.Log("[Recent] Virtual Bot now skips the connect screen");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[Recent] address row built: " + RecentSlots + " buttons, seeds " +
                  string.Join(", ", RecentSeeds));
    }
}
