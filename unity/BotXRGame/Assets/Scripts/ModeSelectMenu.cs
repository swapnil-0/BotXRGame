using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// First screen of the session: choose an experience - the game (Virtual Bot,
/// AprilTag) or one of the standalone tests - then hand over to the ROS IP/port
/// panel, or straight to the HUD for experiences that need no robot.
///
/// The experiences are a scroll list built by Wire Session Flow from
/// ExperienceCatalog. Every row carries an ExperienceButton saying what it
/// starts, and this hooks up whatever rows it finds - so adding an experience
/// adds no field here.
///
/// Flow is menu -> IP/port config -> placement -> run. This inserts itself
/// AHEAD of ROSIPConfig, which previously showed its panel in Start(); that call
/// is now deferred so the two panels cannot both appear at once.
///
/// Wiring (all optional - a missing reference degrades to a sensible default
/// rather than a null crash, because this runs first and a crash here means a
/// session with nothing on screen at all):
///   modePanel       - root of the menu; its ExperienceButtons are the choices
///   experienceList  - the scroll view, for scrolling with the stick
///   ipConfig        - the ROSIPConfig to reveal afterwards
/// </summary>
public class ModeSelectMenu : MonoBehaviour
{
    [Header("Panel")]
    public GameObject modePanel;

    [Tooltip("The experience scroll list. Scrolls by pinch-dragging with the " +
             "hand ray, or by its scrollbar.")]
    public ScrollRect experienceList;

    [Tooltip("Stick that scrolls the list while the menu is up (Bot/Move).\n\n" +
             "Only used when scrollWithStick is on. Bot/Move is the gamepad now, " +
             "and the menu is hands-only, so it is off by default.")]
    public InputActionReference scrollAction;

    [Tooltip("List heights per second at full stick.")]
    public float stickScrollSpeed = 1.2f;

    [Tooltip("Let scrollAction scroll the list. OFF: the menu is driven by hand " +
             "tracking alone and the gamepad only drives the robot.")]
    public bool scrollWithStick = false;

    [Header("Follow the head")]
    [Tooltip("Keep the menu (and the connect panel after it) in front of you " +
             "instead of fixed where the app started - it was easy to lose. " +
             "Lazy like the HUD: it holds still while you look around a little " +
             "and pointing at a button, and glides back once you turn away.")]
    public bool followHead = true;

    [Tooltip("Degrees you can turn before the menu starts following.")]
    [Range(0f, 60f)] public float followDeadAngle = 25f;

    [Tooltip("Seconds to glide back in front once following.")]
    public float followTime = 0.4f;

    [Tooltip("Metres below eye level.")]
    public float followDrop = 0.10f;

    [Header("Optional labels")]
    public TMPro.TextMeshProUGUI titleText;
    public TMPro.TextMeshProUGUI helpText;

    [Tooltip("Let A and B pick the mode as well as the on-screen buttons.\n\n" +
             "OFF by default now that pointer selection is confirmed working. " +
             "It was a safety net for the case where UI raycasting failed and " +
             "the menu became a dead end; that case did not happen, and the " +
             "buttons behave like Connect/Skip, which is what everything else " +
             "in this app uses.")]
    public bool allowButtonSelection = false;

    [Header("Button fallback")]
    [Tooltip("A button - selects Virtual Bot without needing a UI raycast.\n\n" +
             "World-space UI buttons only work if an EventSystem, a UI input " +
             "module and a ray interactor are all present and aimed correctly. " +
             "If any of that is missing the menu is a dead end with no way past " +
             "it, and the whole session is lost. A direct button press cannot " +
             "fail that way.")]
    public InputActionReference selectVirtualAction;

    [Tooltip("B button - selects AprilTag.")]
    public InputActionReference selectAprilTagAction;

    [Range(0.1f, 0.9f)]
    public float pressThreshold = 0.5f;

    [Header("Next step")]
    [Tooltip("Revealed once a mode is chosen. Its own panel stays hidden until then.")]
    public ROSIPConfig ipConfig;

    [Tooltip("Skip the ROS IP/port step in Virtual Bot mode. Virtual Bot needs " +
             "no robot, so the connect screen is pure friction during solo " +
             "playtesting - but leave it ON for a bot test day, where you may " +
             "want telemetry even while driving the virtual ship.")]
    public bool skipIpConfigForVirtualBot = false;

    void Start()
    {
        GameMode.Reset();

        if (modePanel != null) modePanel.SetActive(true);
        if (ipConfig != null) ipConfig.HideUntilModeChosen();

        Enable(selectVirtualAction);
        Enable(selectAprilTagAction);
        Enable(scrollAction);

        if (titleText != null) titleText.text = "Choose an experience";
        if (helpText != null)
        {
            helpText.text = allowButtonSelection
                ? "A  Virtual Bot     B  AprilTag     or point and pinch"
                : "Point with your hand and pinch to choose  -  pinch and drag to scroll";
        }

        int hooked = 0;
        if (modePanel != null)
        {
            foreach (var eb in modePanel.GetComponentsInChildren<ExperienceButton>(true))
            {
                var btn = eb.GetComponent<Button>();
                if (btn == null) continue;
                ShipSource source = eb.source;   // a copy for the closure, not the field
                btn.onClick.AddListener(() => Choose(source));
                hooked++;
            }
        }

        // Start at the top of the list, not wherever the editor left it.
        if (experienceList != null) experienceList.verticalNormalizedPosition = 1f;

        if (hooked == 0)
        {
            Debug.LogWarning("[Mode] no experience buttons found; defaulting to VirtualBot " +
                             "and continuing so the session is not dead on arrival. " +
                             "Run Tools > BotXRGame > Wire Session Flow.");
            Choose(ShipSource.VirtualBot);
        }
        else Debug.LogFormat("[Mode] menu ready, {0} experiences", hooked);
    }

    private static void Enable(InputActionReference r)
    {
        if (r != null && r.action != null) r.action.Enable();
    }

    private bool virtualWasPressed, tagWasPressed;

    void Update()
    {
        // Only while the menu is up; afterwards the stick drives and A/B are the arm.
        if (GameMode.Chosen) return;
        if (modePanel != null && !modePanel.activeSelf) return;

        if (scrollWithStick) ScrollWithStick();

        if (!allowButtonSelection) return;

        bool v = Read(selectVirtualAction);
        if (v && !virtualWasPressed) Choose(ShipSource.VirtualBot);
        virtualWasPressed = v;

        bool t = Read(selectAprilTagAction);
        if (t && !tagWasPressed) Choose(ShipSource.AprilTag);
        tagWasPressed = t;
    }

    private void ScrollWithStick()
    {
        if (experienceList == null || scrollAction == null || scrollAction.action == null) return;
        if (experienceList.content == null || experienceList.viewport == null) return;

        float y = scrollAction.action.ReadValue<Vector2>().y;
        if (Mathf.Abs(y) < 0.2f) return;   // dead zone - a resting stick must not creep

        // Normalised position covers only the part that overflows the viewport,
        // so convert "list heights per second" through that overflow.
        float overflow = experienceList.content.rect.height - experienceList.viewport.rect.height;
        if (overflow <= 1f) return;   // everything already visible

        float step = y * stickScrollSpeed * experienceList.viewport.rect.height / overflow * Time.unscaledDeltaTime;
        experienceList.verticalNormalizedPosition =
            Mathf.Clamp01(experienceList.verticalNormalizedPosition + step);
    }

    // ----------------------------------------------------------- follow head

    private Transform followRoot;
    private float followDistance;
    private Vector3 followAim, followVelocity;
    private bool followStarted;

    private float nextDiag;

    /// <summary>
    /// Every 2 s while the menu is up: where the head and the menu canvas are.
    /// Added when the menu could not be found in the headset - says whether it
    /// is active, in front, at what distance, facing which way, and whether the
    /// camera even renders its layer.
    /// </summary>
    private void LogMenuDiagnostics()
    {
        if (Time.unscaledTime < nextDiag || GameMode.Chosen) return;
        nextDiag = Time.unscaledTime + 2f;

        var cam = Camera.main;
        var canvas = modePanel != null ? modePanel.GetComponentInParent<Canvas>(true) : null;
        if (cam == null || canvas == null)
        {
            Debug.LogFormat("[MenuDiag] cam {0}  canvas {1}  modePanel {2}",
                cam != null, canvas != null, modePanel != null);
            return;
        }
        var h = cam.transform;
        var c = modePanel.transform;   // the panel the follow moves
        Vector3 toMenu = c.position - h.position;
        Debug.LogFormat(
            "[MenuDiag] head {0} fwd {1} | canvas {2} fwd {3} scale {4} | dist {5:F2} m, angle off gaze {6:F0} deg, " +
            "menu faces {7} | modePanel active {8} | canvas enabled {9} mode {10} | camera renders UI layer {11} | layer {12}",
            h.position.ToString("F2"), h.forward.ToString("F2"),
            c.position.ToString("F2"), c.forward.ToString("F2"), c.lossyScale.ToString("F4"),
            toMenu.magnitude, Vector3.Angle(h.forward, toMenu),
            Vector3.Dot(c.forward, toMenu) > 0 ? "away (front visible)" : "TOWARD head (back side)",
            modePanel.activeInHierarchy, canvas.enabled, canvas.renderMode,
            (cam.cullingMask & (1 << canvas.gameObject.layer)) != 0, LayerMask.LayerToName(canvas.gameObject.layer));
    }

    private bool renderCaptured;

    /// <summary>
    /// Once, 8 s in: render what Camera.main sees, from the same pose, into an
    /// opaque image at files/menu_render.png. Separates "the scene does not
    /// draw the menu" from "the headset does not composite what was drawn" -
    /// a headset screenshot shows only the latter.
    /// </summary>
    private void CaptureCameraView()
    {
        if (renderCaptured || Time.unscaledTime < 8f || Camera.main == null) return;
        renderCaptured = true;

        var src = Camera.main;
        var go = new GameObject("MenuDiagCamera");
        var cam = go.AddComponent<Camera>();
        cam.CopyFrom(src);
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.2f, 0f, 0.2f, 1f);   // opaque purple: anything drawn shows against it
        cam.fieldOfView = 90f;
        go.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);

        var rt = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        string path = System.IO.Path.Combine(Application.persistentDataPath, "menu_render.png");
        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
        Debug.Log("[MenuDiag] camera view saved to " + path);

        cam.targetTexture = null;
        Destroy(go);
        Destroy(rt);
        Destroy(tex);
    }

    void LateUpdate()
    {
        LogMenuDiagnostics();
        if (!GameMode.Chosen) CaptureCameraView();

        // LateUpdate so the head pose is this frame's final one.
        if (!followHead) return;

        // Only while the menu or the connect panel is up. Once the HUD shows,
        // its own HeadLockedHUD takes over and the canvas stays put.
        bool menuUp = modePanel != null && modePanel.activeInHierarchy;
        bool connectUp = ipConfig != null && ipConfig.ipInputPanel != null && ipConfig.ipInputPanel.activeInHierarchy;
        if (!menuUp && !connectUp) return;

        var cam = Camera.main;
        if (cam == null) return;
        var head = cam.transform;

        // Move the PANEL, not the root canvas - the same thing HeadLockedHUD
        // does with HUDPanel, which is proven on this headset. Moving the root
        // Canvas every frame left the menu drawn in the app's camera but never
        // seen in the headset.
        Transform panel = menuUp ? modePanel.transform : ipConfig.ipInputPanel.transform;
        if (panel != followRoot)
        {
            followRoot = panel;
            followStarted = false;   // a new panel starts from where it was laid out
        }

        Vector3 flat = Flat(head.forward);
        if (!followStarted)
        {
            // Keep the distance the scene was laid out at, so the menu reads
            // the same size it always has.
            followDistance = Mathf.Clamp(Vector3.Distance(head.position, followRoot.position), 0.8f, 3f);
            followAim = flat;
            followRoot.position = Target(head, followAim);
            Face(head);
            followStarted = true;
            return;
        }

        if (Vector3.Angle(followAim, flat) > followDeadAngle) followAim = flat;

        followRoot.position = Vector3.SmoothDamp(followRoot.position, Target(head, followAim),
                                                 ref followVelocity, followTime);
        Face(head);
    }

    private Vector3 Target(Transform head, Vector3 aim) =>
        head.position + aim * followDistance + Vector3.down * followDrop;

    private void Face(Transform head)
    {
        Vector3 look = followRoot.position - head.position;
        look.y = 0f;   // upright, like the HUD - no tilting with head pitch
        if (look.sqrMagnitude > 1e-6f)
            followRoot.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
    }

    private bool Read(InputActionReference r)
    {
        if (r == null || r.action == null) return false;
        return r.action.ReadValue<float>() > pressThreshold;
    }

    public void Choose(ShipSource source)
    {
        GameMode.Select(source);

        if (modePanel != null) modePanel.SetActive(false);

        // Cup Detect and Distance Test never talk to a robot, so the connect
        // screen would be a question with no right answer - and pressing
        // Connect by habit would start a ROS connection retry loop in the
        // background of a vision test.
        bool skip = (source == ShipSource.VirtualBot && skipIpConfigForVirtualBot)
                 || GameMode.NeedsNoRobot(source);

        if (ipConfig == null)
        {
            // Would otherwise hide the menu and show nothing at all - a black
            // screen with no way forward, which is the worst possible failure
            // on a test day.
            Debug.LogError("[Mode] ipConfig not wired. Menu hidden with no next " +
                           "screen. Assign ROSIPConfig on ModeSelectMenu.");
            if (modePanel != null) modePanel.SetActive(true);
            return;
        }

        if (skip) ipConfig.SkipStraightToHud();
        else ipConfig.ShowConfig();
    }

    /// <summary>Wire to a Back button if you add one later.</summary>
    public void ReturnToMenu()
    {
        GameMode.Reset();
        if (ipConfig != null) ipConfig.HideUntilModeChosen();
        if (modePanel != null) modePanel.SetActive(true);
    }
}
