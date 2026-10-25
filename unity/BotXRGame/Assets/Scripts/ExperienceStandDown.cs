using UnityEngine;

/// <summary>
/// Switches off the game - ship, arena, tornado, tags, robot drive - for the
/// standalone experiences (Cup Detect, Distance Test) that test one thing alone.
///
/// Disabling beats a mode flag inside each system: the one system that forgets
/// the flag is the one that interferes. Shared so a system added to the game
/// later is stood down in every experience, not only the one someone remembered.
/// </summary>
public static class ExperienceStandDown
{
    public static void Gameplay(ArenaPlacer placer)
    {
        if (placer != null)
        {
            placer.enabled = false;
            if (placer.ship != null) placer.ship.gameObject.SetActive(false);

            // The placer has already been drawing its search rectangle since
            // scene load. Disabling the component freezes that rectangle in
            // the air rather than removing it.
            if (placer.previewSurface != null) placer.previewSurface.gameObject.SetActive(false);
            if (placer.previewOutline != null) placer.previewOutline.gameObject.SetActive(false);
            if (placer.finishMarker != null) placer.finishMarker.gameObject.SetActive(false);
        }

        Disable(Object.FindAnyObjectByType<ArenaRun>());
        Disable(Object.FindAnyObjectByType<BotCommandMixer>());
        Disable(Object.FindAnyObjectByType<ShipTagFollower>());
        Disable(Object.FindAnyObjectByType<BotTagMarker>());
        Disable(Object.FindAnyObjectByType<CupTagMarkers>());
        Disable(Object.FindAnyObjectByType<TagCupTracker>());
        Disable(Object.FindAnyObjectByType<FloatingStartButton>());
        Disable(Object.FindAnyObjectByType<TornadoTuner>());   // shares the grip button
        Disable(Object.FindAnyObjectByType<LinkTestMode>());

        // ARTrackedImageManager is deliberately LEFT RUNNING. Disabling it stops
        // the image-tracking subsystem, and on Galaxy XR the runtime then aborts
        // inside oxr_xrDestroyTrackableTrackerANDROID (SIGABRT, "pointer tag
        // truncated") - the app died the moment Cup Detect was chosen. Its
        // consumers are disabled above, so tags are seen but nothing acts on them.

        var board = Object.FindAnyObjectByType<ScoreBoard>(FindObjectsInactive.Include);
        if (board != null) board.gameObject.SetActive(false);

        foreach (var t in Object.FindObjectsByType<Tornado>(FindObjectsInactive.Include))
            if (t != null) t.gameObject.SetActive(false);
    }

    /// <summary>
    /// Blank every HUD text except the experience's own, so leftovers from the
    /// game modes do not overprint it.
    /// </summary>
    public static void HideOtherHudText(TMPro.TextMeshProUGUI keep, Transform alsoKeepUnder = null)
    {
        if (keep == null) return;
        var parent = keep.transform.parent != null ? keep.transform.parent : keep.transform;

        foreach (var t in parent.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
        {
            if (t == keep) continue;
            if (alsoKeepUnder != null && t.transform.IsChildOf(alsoKeepUnder)) continue;
            t.text = "";
            t.enabled = false;
        }
    }

    /// <summary>
    /// Make the HUD follow the gaze, sitting just above it. Experiences look at
    /// the floor; a HUD held at eye level means looking up and down between.
    /// </summary>
    public static void HudFollowsGaze(float lift = 0.30f)
    {
        var hud = Object.FindAnyObjectByType<HeadLockedHUD>(FindObjectsInactive.Include);
        if (hud != null) { hud.followGaze = true; hud.gazeLift = lift; }
    }

    /// <summary>
    /// Blank every text on the head-locked HUD, for experiences that show their
    /// own world-space panel - otherwise the HUD text shows through behind it.
    /// </summary>
    public static void HideHud()
    {
        foreach (var hud in Object.FindObjectsByType<HeadLockedHUD>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            foreach (var t in hud.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
            {
                t.text = "";
                t.enabled = false;
            }
    }

    private static void Disable(Behaviour c) { if (c != null) c.enabled = false; }
}
