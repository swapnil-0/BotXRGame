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
            // Shader.Find in a BUILT player only sees shaders the build kept.
            // A stripped shader gives a null material, the cube falls back to
            // Unity's default - which URP does not render - and the button is
            // silently invisible while every other check says it is fine.
            // So try several, and borrow a working one off the scene last.
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Sprites/Default")
                     ?? Shader.Find("Unlit/Color");

            if (sh == null)
            {
                var donor = FindAnyObjectByType<MeshRenderer>();
                if (donor != null && donor.sharedMaterial != null)
                    sh = donor.sharedMaterial.shader;
            }

            if (sh != null) mat = new Material(sh);
            else Debug.LogError("[Start] no usable shader - button will be invisible.");
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

    /// <summary>Why the button is not on screen, for the HUD.</summary>
    public string Diagnosis { get; private set; } = "not started";

    void Update()
    {
        // Re-resolve, not just once in Start(). FindAnyObjectByType skips
        // INACTIVE objects, and the mixer rides the ship root, which AprilTag
        // mode suppresses. One frame of the ship being inactive at load leaves
        // this null forever, and a null mixer here is indistinguishable from
        // "not time to start yet" - the button simply never appears and the
        // session is stuck in ARMED with no way out.
        if (mixer == null) mixer = FindAnyObjectByType<BotCommandMixer>(FindObjectsInactive.Include);
        if (run == null) run = FindAnyObjectByType<ArenaRun>(FindObjectsInactive.Include);

        if (rayOrigin == null)
        {
            var p = FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include);
            if (p != null) rayOrigin = p.rayOrigin;
        }

        if (mixer == null) Diagnosis = "no mixer found";
        else if (!mixer.AwaitingStart) Diagnosis = "phase " + mixer.CurrentPhase;
        else if (run == null) Diagnosis = "no ArenaRun - cannot place";
        else if (faceMat == null) Diagnosis = "VISIBLE but unlit (no URP shader)";
        else Diagnosis = "VISIBLE at " + transform.position.ToString("F2");

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
        Vector3 p;

        if (run != null)
        {
            p = run.StartPoint;
            p.y += height;
        }
        else if (Camera.main != null)
        {
            // Rather than return and leave the button parked at the world
            // origin - which is usually behind or under the player, and looks
            // exactly like not existing - put it where it must be seen.
            Vector3 fwd = Camera.main.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            p = Camera.main.transform.position + fwd.normalized * 0.8f;
            p.y -= 0.2f;
        }
        else return;

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
