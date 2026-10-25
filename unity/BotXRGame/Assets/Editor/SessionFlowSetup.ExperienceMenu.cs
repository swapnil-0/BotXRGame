using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The start menu's experience list: a vertical scroll view with one button
/// per ExperienceCatalog entry, built and re-laid-out by Wire Session Flow.
///
/// Rebuilt from the catalog on every run - entries are created, updated and put
/// in catalog order, and ones no longer in the catalog are removed - so adding
/// an experience is a catalog line plus one run, never hand-placing a button.
/// </summary>
public static partial class SessionFlowSetup
{
    // The fixed buttons the menu had before it became a list.
    private static readonly string[] LegacyModeButtons =
    {
        "VirtualBotButton", "AprilTagButton", "LinkTestButton", "CupDetectButton", "DistanceTestButton",
    };

    private static void RemoveLegacyModeButtons(Transform modePanel, List<string> done)
    {
        foreach (var n in LegacyModeButtons)
        {
            var t = modePanel.Find(n);
            if (t == null) continue;
            Undo.DestroyObjectImmediate(t.gameObject);
            done.Add("Removed old fixed button " + n + " (it is in the experience list now)");
        }
    }

    private static ScrollRect BuildExperienceList(Transform modePanel, List<string> done)
    {
        // ------------------------------------------------------------ frame
        // Below the title and help line: y from +190 down to -280 on the
        // 800x600 canvas. Five 80-unit rows show at once; more scroll.
        var listGo = FindOrCreateUiChild(modePanel, "ExperienceList");
        var listRt = listGo.GetComponent<RectTransform>();
        listRt.anchorMin = listRt.anchorMax = new Vector2(0.5f, 0.5f);
        listRt.pivot = new Vector2(0.5f, 0.5f);
        listRt.anchoredPosition = new Vector2(0f, -45f);
        listRt.sizeDelta = new Vector2(600f, 470f);
        var scroll = GetOrAdd<ScrollRect>(listGo);

        // --------------------------------------------------------- viewport
        var viewport = FindOrCreateUiChild(listGo.transform, "Viewport");
        var vpRt = viewport.GetComponent<RectTransform>();
        vpRt.anchorMin = Vector2.zero;
        vpRt.anchorMax = Vector2.one;
        vpRt.pivot = new Vector2(0f, 1f);
        vpRt.offsetMin = Vector2.zero;
        vpRt.offsetMax = new Vector2(-22f, 0f);   // room for the scrollbar
        GetOrAdd<RectMask2D>(viewport);
        // Near-invisible but ray-hittable, so a drag that starts in the gap
        // between two buttons still scrolls the list.
        var vpImg = GetOrAdd<Image>(viewport);
        vpImg.color = new Color(1f, 1f, 1f, 0.01f);

        // ---------------------------------------------------------- content
        var content = FindOrCreateUiChild(viewport.transform, "Content");
        var cRt = content.GetComponent<RectTransform>();
        cRt.anchorMin = new Vector2(0f, 1f);
        cRt.anchorMax = new Vector2(1f, 1f);
        cRt.pivot = new Vector2(0.5f, 1f);
        cRt.anchoredPosition = Vector2.zero;
        cRt.sizeDelta = Vector2.zero;

        var layout = GetOrAdd<VerticalLayoutGroup>(content);
        layout.spacing = 10f;
        layout.padding = new RectOffset(10, 10, 6, 6);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = GetOrAdd<ContentSizeFitter>(content);
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // ---------------------------------------------- one row per entry
        var keep = new HashSet<string>();
        int index = 0;
        foreach (var e in ExperienceCatalog.All)
        {
            string name = "Exp_" + e.Source;
            keep.Add(name);
            var row = MakeExperienceButton(content.transform, name, e);
            row.transform.SetSiblingIndex(index++);
        }
        for (int k = content.transform.childCount - 1; k >= 0; k--)
        {
            var child = content.transform.GetChild(k);
            if (keep.Contains(child.name)) continue;
            done.Add("Removed list entry " + child.name + " (not in ExperienceCatalog)");
            Undo.DestroyObjectImmediate(child.gameObject);
        }

        // -------------------------------------------------------- scrollbar
        // Slim, on the right: the cue that the list scrolls, and a second way
        // to scroll (point at it and drag).
        var sbGo = FindOrCreateUiChild(listGo.transform, "Scrollbar");
        var sbRt = sbGo.GetComponent<RectTransform>();
        sbRt.anchorMin = new Vector2(1f, 0f);
        sbRt.anchorMax = new Vector2(1f, 1f);
        sbRt.pivot = new Vector2(1f, 0.5f);
        sbRt.anchoredPosition = Vector2.zero;
        sbRt.sizeDelta = new Vector2(14f, 0f);
        var sbImg = GetOrAdd<Image>(sbGo);
        sbImg.color = new Color(1f, 1f, 1f, 0.12f);

        var sliding = FindOrCreateUiChild(sbGo.transform, "Sliding Area");
        Stretch(sliding.GetComponent<RectTransform>());
        var handle = FindOrCreateUiChild(sliding.transform, "Handle");
        var handleRt = handle.GetComponent<RectTransform>();
        Stretch(handleRt);
        var handleImg = GetOrAdd<Image>(handle);
        handleImg.color = new Color(0.70f, 0.80f, 0.90f, 0.75f);

        var sb = GetOrAdd<Scrollbar>(sbGo);
        sb.direction = Scrollbar.Direction.BottomToTop;
        sb.handleRect = handleRt;
        sb.targetGraphic = handleImg;

        // ------------------------------------------------------- scroll rect
        scroll.content = cRt;
        scroll.viewport = vpRt;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.verticalScrollbar = sb;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scroll.scrollSensitivity = 20f;
        EditorUtility.SetDirty(scroll);

        done.Add("Experience list: " + ExperienceCatalog.All.Length +
                 " entries from ExperienceCatalog (drag, scrollbar or right stick to scroll)");
        return scroll;
    }

    private static Button MakeExperienceButton(Transform parent, string name, ExperienceCatalog.Entry e)
    {
        var go = FindOrCreateUiChild(parent, name);

        var img = GetOrAdd<Image>(go);
        img.color = e.Colour;
        var btn = GetOrAdd<Button>(go);
        btn.targetGraphic = img;

        var le = GetOrAdd<LayoutElement>(go);
        le.minHeight = 80f;
        le.preferredHeight = 80f;

        var eb = GetOrAdd<ExperienceButton>(go);
        eb.source = e.Source;
        EditorUtility.SetDirty(eb);

        var title = MakeRowText(go.transform, "Title", e.Title, 30, TMPro.FontStyles.Bold,
                                new Vector2(0f, 0.45f), new Vector2(1f, 1f),
                                TMPro.TextAlignmentOptions.BottomLeft);
        title.color = Color.white;

        var desc = MakeRowText(go.transform, "Description", e.Description, 18, TMPro.FontStyles.Normal,
                               new Vector2(0f, 0f), new Vector2(1f, 0.45f),
                               TMPro.TextAlignmentOptions.TopLeft);
        desc.color = new Color(0.85f, 0.90f, 0.95f);

        // The single label MakeButton used to add, if this row was converted.
        var old = go.transform.Find("Label");
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

        return btn;
    }

    private static TMPro.TextMeshProUGUI MakeRowText(Transform parent, string name, string text, float size,
                                                     TMPro.FontStyles style, Vector2 anchorMin, Vector2 anchorMax,
                                                     TMPro.TextAlignmentOptions align)
    {
        var go = FindOrCreateUiChild(parent, name);
        var t = GetOrAdd<TMPro.TextMeshProUGUI>(go);
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        t.raycastTarget = false;   // the row's Image takes the ray, so the whole row is the button

        var r = go.GetComponent<RectTransform>();
        r.anchorMin = anchorMin;
        r.anchorMax = anchorMax;
        r.offsetMin = new Vector2(18f, 2f);
        r.offsetMax = new Vector2(-18f, -2f);
        return t;
    }
}
