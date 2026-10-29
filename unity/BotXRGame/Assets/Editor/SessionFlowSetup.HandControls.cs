using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Tools > BotXRGame > Use Hand Pinch On Menu: points the Ray Interactor's UI
/// press at XRI "UI Press", whose bindings include the hand pinch
/// (&lt;HandInteraction&gt;{RightHand}/pointerActivated).
///
/// It was wired to XRI "Activate", bound only to the controller trigger. With
/// the controllers gone the ray hovered the menu (white line, UI hit logged)
/// but the press never fired. At runtime XRI swaps the EventSystem's module
/// for XRUIInputModule, which clicks through this reader - the UI action map
/// on InputSystemUIInputModule is not used at all.
///
/// OVERWRITES the reader (that is the point); everything else is untouched.
/// </summary>
public static partial class SessionFlowSetup
{
    [MenuItem("Tools/BotXRGame/Use Hand Pinch On Menu", false, 47)]
    public static void UseHandPinchOnMenu()
    {
        var press = FindActionReference("XRI Right Interaction", "UI Press");
        var pressValue = FindActionReference("XRI Right Interaction", "UI Press Value");
        if (press == null)
        {
            Debug.LogError("[HandControls] XRI Right Interaction/UI Press not found - is the XRI Starter Assets sample imported?");
            return;
        }

        int wired = 0;
        foreach (var ray in Object.FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var so = new SerializedObject(ray);
            so.FindProperty("m_UIPressInput.m_InputSourceMode").intValue = 2;   // InputActionReference
            so.FindProperty("m_UIPressInput.m_InputActionReferencePerformed").objectReferenceValue = press;
            so.FindProperty("m_UIPressInput.m_InputActionReferenceValue").objectReferenceValue = pressValue;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(ray);
            wired++;
            Debug.LogFormat("[HandControls] {0}: UI press -> XRI Right Interaction/UI Press", HierarchyPath(ray.transform));
        }

        if (wired > 0) EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        else Debug.LogWarning("[HandControls] no XRRayInteractor in the scene");
    }
}
