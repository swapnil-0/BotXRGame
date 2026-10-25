using UnityEngine;

/// <summary>
/// Marks a start-menu button with the experience it starts. ModeSelectMenu
/// hooks up every one it finds under its panel, so a new experience needs a
/// catalog line, not a new serialized field. Built by Wire Session Flow from
/// ExperienceCatalog.
/// </summary>
[RequireComponent(typeof(UnityEngine.UI.Button))]
public class ExperienceButton : MonoBehaviour
{
    public ShipSource source;
}
