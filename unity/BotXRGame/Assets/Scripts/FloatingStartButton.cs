using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A START button floating above the start line, pressed by pointing and
/// pulling the trigger.
///
/// Exists so the player decides when the run begins. Starting automatically on
/// arrival means the clock is already running while the player is still
/// straightening up, looking at the arena, or waiting for someone to start
/// filming - and a demo where the timer began without you is a retake.
///
/// Raycast against its own collider rather than a world-space Canvas: the
/// button appears mid-run over the floor, and adding another canvas competing
/// for UI raycasts with the HUD is a good way to break both.
/// </summary>
public class FloatingStartButton : MonoBehaviour
{
    [Header("Wiring")]
    public BotCommandMixer mixer;

    [Tooltip("Where the pointing ray comes from - the ray interactor.")]
    public Transform rayOrigin;

    public InputActionReference pressAction;   // trigger
    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    [Header("Placement")]
    public ArenaRun run;

    [Tooltip("Metres above the start point.")]
    public float height = 0.35f;

    [Tooltip("Button width and height in metres.")]
    public Vector2 size = new Vector2(0.26f, 0.12f);

    [Header("Look")]
    public Material buttonMaterial;
    public Color idleColour = new Color(0.15f, 0.55f, 0.25f);
    public Color hoverColour = new Color(0.25f, 0.9f, 0.4f);

    private GameObject face;
    private TMPro.TextMeshPro label;
    private BoxCollider box;
    private Renderer faceRenderer;
    private Material faceMat;
    private bool wasPressed;
    private bool built;

    void Start()
    {
        if (!GameMode.IsAprilTag) { enabled = false; return; }

        if (mixer == null) mixer = FindAnyObjectByType<BotCommandMixer>();
        if (run == null) run = FindAnyObjectByType<ArenaRun>();

        if (rayOrigin == null)
        {
            var placer = FindAnyObjectByType<ArenaPlacer>();
            if (placer != null) rayOrigin = placer.rayOrigin;
        }

        if (pressAction != null && pressAction.action != null) pressAction.action.Enable();

        Build();
        SetVisible(false);
    }

    private void Build()
    {
        if (built) return;
        built = true;

        face = GameObject.CreatePrimitive(PrimitiveType.Cube);
        face.name = "StartFace";
        face.transform.SetParent(transform, false);
        face.transform.localScale = new Vector3(size.x, size.y, 0.02f);

        box = face.GetComponent<BoxCollider>();

        faceRenderer = face.GetComponent<Renderer>();
        Material mat = buttonMaterial;
        if (mat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null) mat = new Material(sh);
        }
        faceMat = mat != null ? new Material(mat) : null;
        if (faceRenderer != null && faceMat != null)
        {
            faceMat.color = idleColour;
            faceRenderer.sharedMaterial = faceMat;
        }

        var textGo = new GameObject("StartLabel");
        textGo.transform.SetParent(transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -0.012f);

        label = textGo.AddComponent<TMPro.TextMeshPro>();
        label.text = "START";
        label.fontSize = 1.2f;
        label.alignment = TMPro.TextAlignmentOptions.Center;
        label.color = Color.white;

        var rt = label.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = new Vector2(size.x, size.y);
    }

    void Update()
    {
        bool shouldShow = mixer != null && mixer.AwaitingStart;
        SetVisible(shouldShow);
        if (!shouldShow) return;

        PlaceSelf();

        bool hovering = IsPointedAt();

        if (faceMat != null)
            faceMat.color = hovering ? hoverColour : idleColour;

        bool pressed = pressAction != null && pressAction.action != null &&
                       pressAction.action.ReadValue<float>() > pressThreshold;

        // Rising edge, and only while pointed at it: a trigger pull aimed
        // elsewhere should not start the run.
        if (pressed && !wasPressed && hovering)
        {
            mixer.PressStart();
            SetVisible(false);
        }
        wasPressed = pressed;
    }

    private void PlaceSelf()
    {
        if (run == null) return;

        Vector3 p = run.StartPoint;
        p.y += height;
        transform.position = p;

        // Face the player, upright. A button that matches head pitch swings
        // wildly while you are looking down at the floor, which is where the
        // arena is.
        if (Camera.main != null)
        {
            Vector3 look = transform.position - Camera.main.transform.position;
            look.y = 0f;
            if (look.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }
    }

    private bool IsPointedAt()
    {
        if (rayOrigin == null || box == null) return false;

        var ray = new Ray(rayOrigin.position, rayOrigin.forward);
        return box.Raycast(ray, out _, 10f);
    }

    private void SetVisible(bool v)
    {
        if (face != null && face.activeSelf != v) face.SetActive(v);
        if (label != null && label.gameObject.activeSelf != v) label.gameObject.SetActive(v);
    }
}
