using UnityEngine;

/// <summary>
/// Keeps a world-space panel in front of the head so its contents are readable
/// wherever the player looks.
///
/// Not parented to the camera on purpose. A panel rigidly attached to the head
/// is genuinely unpleasant in a headset - it never settles, and reading small
/// debug numbers on it is worse than reading them off a fixed board. This
/// instead follows with a lag and a dead angle, so the panel sits still while
/// you look around normally and only catches up when you actually turn. That
/// keeps text stable enough to read during a live bot test.
/// </summary>
public class HeadLockedHUD : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Panel root to move. Defaults to this transform.")]
    public Transform panel;

    [Tooltip("Head to follow. Defaults to Camera.main.")]
    public Transform head;

    [Header("Placement")]
    [Tooltip("Metres in front of the head.")]
    public float distance = 1.2f;

    [Tooltip("Scale applied to the panel at startup. The HUD covered most of " +
             "the play area at 1.0 - it is a debug readout, not the game, and " +
             "it should not be the biggest thing you are looking at.\n\n" +
             "New field, so the code default applies rather than whatever the " +
             "scene has serialized.")]
    public float panelScale = 0.45f;

    [Tooltip("Metres below eye level, so the panel does not sit over whatever " +
             "you are actually trying to look at. The arena is on the floor, so " +
             "a HUD at eye height covers the room instead of the game.")]
    public float verticalOffset = 0.25f;

    [Header("Follow feel")]
    [Tooltip("Degrees the head can turn before the panel starts following. " +
             "Below this it stays put, which is what makes the text readable.")]
    [Range(0f, 45f)]
    public float deadAngle = 12f;

    [Tooltip("Seconds to catch up once moving. Higher is calmer.")]
    public float followTime = 0.35f;

    [Tooltip("Keep the panel upright rather than matching head roll/pitch. " +
             "Matching pitch makes it swing wildly when you look at the floor, " +
             "which is most of this game.")]
    public bool keepUpright = true;

    private Vector3 velocity;
    private Vector3 lockedForward;
    private bool initialised;

    void Start()
    {
        if (panel == null) panel = transform;
        if (head == null && Camera.main != null) head = Camera.main.transform;

        if (head == null)
        {
            Debug.LogWarning("[HUD] no head transform; HUD will not follow.");
            enabled = false;
            return;
        }

        if (panelScale > 0f && !Mathf.Approximately(panelScale, 1f))
            panel.localScale = panel.localScale * panelScale;

        lockedForward = FlatForward(head.forward);
        SnapTo(lockedForward);
        initialised = true;
    }

    void LateUpdate()
    {
        // LateUpdate so the head pose for this frame is final. Following a
        // stale pose shows up as the panel juddering against head motion.
        if (!initialised || head == null || panel == null) return;

        Vector3 current = FlatForward(head.forward);

        // Only re-aim once the head has turned past the dead angle. Inside it
        // the panel holds position, which is the whole point.
        if (Vector3.Angle(lockedForward, current) > deadAngle)
            lockedForward = current;

        Vector3 target = head.position
                         + lockedForward * distance
                         + Vector3.down * verticalOffset;

        panel.position = Vector3.SmoothDamp(
            panel.position, target, ref velocity, followTime);

        Vector3 look = panel.position - head.position;
        if (keepUpright) look.y = 0f;

        if (look.sqrMagnitude > 1e-6f)
            panel.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
    }

    private static Vector3 FlatForward(Vector3 f)
    {
        f.y = 0f;
        return f.sqrMagnitude < 1e-6f ? Vector3.forward : f.normalized;
    }

    private void SnapTo(Vector3 forward)
    {
        panel.position = head.position + forward * distance + Vector3.down * verticalOffset;
        Vector3 look = panel.position - head.position;
        if (keepUpright) look.y = 0f;
        if (look.sqrMagnitude > 1e-6f)
            panel.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        velocity = Vector3.zero;
    }

    /// <summary>Re-centre immediately, e.g. after the arena is placed.</summary>
    public void Recenter()
    {
        if (head == null || panel == null) return;
        lockedForward = FlatForward(head.forward);
        SnapTo(lockedForward);
    }
}
