using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Walks the player through placing real cups, one at a time.
///
/// The player holds a physical laser pointer, sees a virtual bullseye on the
/// floor through the headset, and aims the laser dot to coincide with it. The
/// helper - who has no headset - just follows the red dot. The app never has
/// to know where the laser is: the PLAYER is the alignment mechanism, so there
/// is nothing to track or calibrate.
///
/// That workflow is exactly why this is sequential. Six discs on the floor at
/// once are ambiguous for both people; "cup 3, here" is not.
/// </summary>
public class PlacementGuide : MonoBehaviour
{
    [Header("Visuals")]
    [Tooltip("Bullseye marker moved to the active target. Concentric rings " +
             "help the player judge when the laser dot is centred.")]
    public GameObject activeMarker;
    [Tooltip("Optional faint marker prefab shown at already-placed positions.")]
    public GameObject placedMarkerPrefab;
    [Tooltip("Optional label showing the cup number and colour.")]
    public TMPro.TextMeshProUGUI instructionText;

    [Header("Behaviour")]
    [Tooltip("Lift markers slightly off the floor to avoid z-fighting with the plane.")]
    public float markerHeight = 0.01f;

    /// <summary>Fires when every cup has been confirmed placed.</summary>
    public event Action OnAllPlaced;
    /// <summary>Fires when the player skips physical placement entirely.</summary>
    public event Action OnSkipped;
    /// <summary>Fires whenever the active target changes. Argument is 1-based index.</summary>
    public event Action<int> OnActiveChanged;

    public bool IsRunning { get; private set; }
    public int ActiveIndex { get; private set; }        // 0-based into the layout
    public int Remaining => layout == null ? 0 : Mathf.Max(0, layout.Count - ActiveIndex);

    private List<LayoutGenerator.TargetSpec> layout;
    private readonly List<GameObject> placedMarkers = new List<GameObject>();

    public void Begin(List<LayoutGenerator.TargetSpec> targets)
    {
        layout = targets;
        ActiveIndex = 0;
        IsRunning = layout != null && layout.Count > 0;

        ClearPlacedMarkers();

        if (!IsRunning)
        {
            // Nothing to place - treat as immediately complete rather than
            // stranding the player on an empty step.
            if (activeMarker != null) activeMarker.SetActive(false);
            OnAllPlaced?.Invoke();
            return;
        }

        ShowActive();
    }

    /// <summary>Wire to a "Placed" button or a controller press.</summary>
    public void ConfirmPlaced()
    {
        if (!IsRunning || layout == null) return;

        // Leave a faint marker behind so the player can see the course taking
        // shape, and so a misplaced cup is obvious in context.
        if (placedMarkerPrefab != null && ActiveIndex < layout.Count)
        {
            var m = Instantiate(placedMarkerPrefab,
                                Raised(layout[ActiveIndex].Position),
                                Quaternion.identity);
            placedMarkers.Add(m);
        }

        ActiveIndex++;

        if (ActiveIndex >= layout.Count)
        {
            IsRunning = false;
            if (activeMarker != null) activeMarker.SetActive(false);
            SetText("All cups placed. Press Done to verify.");
            OnAllPlaced?.Invoke();
            return;
        }

        ShowActive();
    }

    /// <summary>Step back one cup, for when the helper mis-hears.</summary>
    public void GoBack()
    {
        if (!IsRunning || ActiveIndex == 0) return;
        ActiveIndex--;

        if (placedMarkers.Count > 0)
        {
            var last = placedMarkers[placedMarkers.Count - 1];
            placedMarkers.RemoveAt(placedMarkers.Count - 1);
            if (last != null) Destroy(last);
        }
        ShowActive();
    }

    /// <summary>Wire to the Skip button. Game proceeds fully virtual.</summary>
    public void Skip()
    {
        IsRunning = false;
        if (activeMarker != null) activeMarker.SetActive(false);
        ClearPlacedMarkers();
        SetText("Skipped - playing with virtual targets only.");
        OnSkipped?.Invoke();
    }

    private void ShowActive()
    {
        if (layout == null || ActiveIndex >= layout.Count) return;
        var spec = layout[ActiveIndex];

        if (activeMarker != null)
        {
            activeMarker.SetActive(true);
            activeMarker.transform.position = Raised(spec.Position);
        }

        SetText(string.Format("Place cup {0} of {1}  ({2})\nAim the laser at the target.",
                              spec.Index, layout.Count,
                              spec.Kind == Target.Kind.Green ? "GREEN +" : "RED -"));

        OnActiveChanged?.Invoke(spec.Index);
    }

    private Vector3 Raised(Vector3 p)
    {
        return new Vector3(p.x, p.y + markerHeight, p.z);
    }

    private void ClearPlacedMarkers()
    {
        foreach (var m in placedMarkers)
            if (m != null) Destroy(m);
        placedMarkers.Clear();
    }

    private void SetText(string s)
    {
        if (instructionText != null) instructionText.text = s;
    }
}
