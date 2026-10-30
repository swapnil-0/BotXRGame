using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// "Back" and "Restart" buttons in every experience, pressed with a hand
/// pinch. Lazily follows the head, below eye level.
///
/// RESTART starts the current experience over in place: it calls every
/// active IExperienceRestart (the run, the robot's approach, Distance Test
/// pins, Cup Detect tracks...). Instant, nothing reloads.
///
/// BACK returns to the experience menu by relaunching the app. Not by
/// rebuilding the menu in place - every experience switches parts of the
/// scene off and would have to undo it exactly - and not by reloading the
/// scene: that destroys ARTrackedImageManager, which aborts the Galaxy XR
/// runtime (CLAUDE.md rule 10), the same abort as an ordinary app exit.
/// RestartActivity (Plugins/Android) runs in its own process, SIGKILLs this
/// one - so no XR shutdown code runs, so no abort - and starts the app
/// again. Costs one app start (~10 s). Robot addresses persist (PlayerPrefs).
///
/// Both need a second tap within a few seconds, so a stray pinch mid-run
/// cannot throw the game away. They act on pointer DOWN: a closing pinch
/// drags the hand ray off a small button before release (see ROSIPConfig).
///
/// Self-installing: no scene object or wiring. Hidden on the menu.
/// </summary>
public class ExperienceBar : MonoBehaviour
{
    private const float ConfirmSeconds = 3f;
    private const float Distance = 0.95f;      // metres in front of the head
    private const float Drop = 0.42f;          // metres below eye level
    private const float FollowAngle = 32f;     // degrees of head turn before it follows
    private const float FollowSpeed = 4f;

    private Canvas canvas;
    private TMPro.TextMeshProUGUI backLabel, restartLabel, statusLabel;
    private string pending;                    // "back" / "restart" awaiting the second tap
    private float pendingUntil;
    private float statusUntil;
    private Vector3 anchorForward = Vector3.forward;
    private bool leaving;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindAnyObjectByType<ExperienceBar>(FindObjectsInactive.Include) != null) return;
        new GameObject("ExperienceBar").AddComponent<ExperienceBar>();
    }

    void Update()
    {
        bool show = GameMode.Chosen && !leaving;
        if (show && canvas == null) Build();
        if (canvas != null && canvas.gameObject.activeSelf != show) canvas.gameObject.SetActive(show);
        if (!show) return;

        if (pending != null && Time.time > pendingUntil) ClearPending();
        if (statusLabel != null && statusLabel.text.Length > 0 && Time.time > statusUntil) statusLabel.text = "";
    }

    void LateUpdate()
    {
        if (canvas == null || !canvas.gameObject.activeSelf || Camera.main == null) return;

        var head = Camera.main.transform;
        Vector3 fwd = head.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) return;
        fwd.Normalize();

        // Follows only once you have turned well away, so it holds still
        // while you reach for it.
        if (Vector3.Angle(anchorForward, fwd) > FollowAngle)
            anchorForward = fwd;

        Vector3 target = head.position + anchorForward * Distance + Vector3.down * Drop;
        var t = canvas.transform;
        t.position = Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-FollowSpeed * Time.deltaTime));
        Vector3 look = t.position - head.position;
        look.y = 0f;
        if (look.sqrMagnitude > 1e-4f) t.rotation = Quaternion.LookRotation(look, Vector3.up);
    }

    // ------------------------------------------------------------- actions

    private void Tap(string action)
    {
        if (leaving) return;
        if (pending != action)
        {
            pending = action;
            pendingUntil = Time.time + ConfirmSeconds;
            if (action == "back") backLabel.text = "Tap again: menu";
            else restartLabel.text = "Tap again: restart";
            Debug.LogFormat("[Bar] {0} armed", action);
            return;
        }

        ClearPending();
        if (action == "back") StartCoroutine(Back());
        else Restart();
    }

    private void ClearPending()
    {
        pending = null;
        if (backLabel != null) backLabel.text = "<  Back";
        if (restartLabel != null) restartLabel.text = "Restart";
    }

    private void Restart()
    {
        var targets = new List<IExperienceRestart>();
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (mb is IExperienceRestart r && mb.isActiveAndEnabled) targets.Add(r);

        foreach (var r in targets)
        {
            try { r.RestartExperience(); }
            catch (System.Exception e) { Debug.LogException(e); }
        }

        Debug.LogFormat("[Bar] restart {0}: {1} part(s)", GameMode.Source, targets.Count);
        Status(targets.Count > 0 ? "Restarted" : "Nothing to restart yet");
    }

    private IEnumerator Back()
    {
        leaving = true;
        Debug.LogFormat("[Bar] back to menu from {0} - relaunching", GameMode.Source);

        // Stop the robot first. Killing the process drops the socket, and a
        // base that keeps its last /cmd_vel would carry on driving. Zero for
        // a few publish periods (10 Hz) so at least one reaches it.
        foreach (var rc in FindObjectsByType<RobotController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            rc.SetExternalCommand(0f, 0f, 0f);
        PlayerPrefs.Save();
        yield return new WaitForSecondsRealtime(0.35f);

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var restart = new AndroidJavaClass("com.swapnil.botxr.RestartActivity"))
            {
                restart.CallStatic("relaunch", activity);
            }
        }
        catch (System.Exception e)
        {
            // No way back without the helper; say so rather than freeze.
            Debug.LogException(e);
            leaving = false;
            Status("Back failed - restart the app");
        }
#else
        Debug.Log("[Bar] (editor) relaunch skipped");
        leaving = false;
#endif
    }

    private void Status(string s)
    {
        if (statusLabel == null) return;
        statusLabel.text = s;
        statusUntil = Time.time + 2.5f;
    }

    // ---------------------------------------------------------------- build

    private void Build()
    {
        var root = new GameObject("ExperienceBarCanvas", typeof(RectTransform));
        root.transform.SetParent(transform, false);
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.sortingOrder = 10;
        // The XRI ray clicks world-space UI through this raycaster.
        root.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>();

        var rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(460f, 130f);
        root.transform.localScale = Vector3.one * 0.001f;   // 1 unit = 1 mm

        backLabel = MakeButton(root.transform, "Back", new Vector2(-115f, 15f),
                               new Color(0.30f, 0.30f, 0.38f, 0.92f), () => Tap("back"));
        restartLabel = MakeButton(root.transform, "Restart", new Vector2(115f, 15f),
                                  new Color(0.18f, 0.38f, 0.30f, 0.92f), () => Tap("restart"));
        ClearPending();

        var s = new GameObject("Status", typeof(RectTransform));
        s.transform.SetParent(root.transform, false);
        statusLabel = s.AddComponent<TMPro.TextMeshProUGUI>();
        statusLabel.fontSize = 22;
        statusLabel.alignment = TMPro.TextAlignmentOptions.Center;
        statusLabel.color = new Color(0.85f, 0.92f, 1f);
        statusLabel.raycastTarget = false;
        statusLabel.text = "";
        var srt = s.GetComponent<RectTransform>();
        srt.anchoredPosition = new Vector2(0f, -50f);
        srt.sizeDelta = new Vector2(440f, 30f);

        if (Camera.main != null)
        {
            Vector3 f = Camera.main.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude > 1e-4f) anchorForward = f.normalized;
            root.transform.position = Camera.main.transform.position + anchorForward * Distance + Vector3.down * Drop;
        }
    }

    private static TMPro.TextMeshProUGUI MakeButton(Transform parent, string name, Vector2 pos,
                                                    Color colour, System.Action onDown)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = colour;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(210f, 72f);

        var trigger = go.AddComponent<EventTrigger>();
        var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
        down.callback.AddListener(_ => onDown());
        trigger.triggers.Add(down);

        var l = new GameObject("Label", typeof(RectTransform));
        l.transform.SetParent(go.transform, false);
        var label = l.AddComponent<TMPro.TextMeshProUGUI>();
        label.fontSize = 26;
        label.alignment = TMPro.TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
        label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        var lrt = l.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        return label;
    }
}
