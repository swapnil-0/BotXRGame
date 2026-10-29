using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

/// <summary>
/// Logs what the hand input actually delivers, once a second, to logcat
/// ("[Hand]"): every XR device with its layout, usages, tracking state, aim
/// (pointer) pose and pinch; where the Ray Interactor is; and what the UI
/// event system thinks the pointer is over.
///
/// Exists because pinch placed the arena but did nothing on the menu and no
/// ray followed the hand - consistent with pinch arriving but the aim pose
/// not, and that is a question for a log, not for theory (CLAUDE.md rule 9).
/// Self-installing; remove once hand input is settled.
/// </summary>
public class HandInputDiag : MonoBehaviour
{
    private float next;
    private Transform ray;
    private readonly StringBuilder sb = new StringBuilder(1024);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindAnyObjectByType<HandInputDiag>(FindObjectsInactive.Include) != null) return;
        new GameObject("HandInputDiag").AddComponent<HandInputDiag>();
    }

    void Update()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 1f;

        if (ray == null)
        {
            var go = GameObject.Find("Ray Interactor");
            if (go != null) ray = go.transform;
        }

        foreach (var d in InputSystem.devices)
        {
            if (!(d is TrackedDevice) && !(d is XRController)) continue;
            if (d is XRHMD) continue;

            sb.Clear();
            sb.AppendFormat("[Hand] '{0}' layout {1} usages [{2}] enabled {3}",
                d.displayName, d.layout, string.Join(",", d.usages), d.enabled);
            Append(d, "isTracked");
            Append(d, "trackingState");
            Append(d, "pointerPosition");
            Append(d, "pointer/position");
            Append(d, "devicePosition");
            Append(d, "pinchValue");
            Append(d, "pointerActivated");
            Append(d, "pointerActivateValue");
            Debug.Log(sb.ToString());
        }

        // The ray's own view: does its UI raycast hit the menu, does its UI
        // press fire, and is its line being drawn at all.
        var interactor = ray != null
            ? ray.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor>() : null;
        if (interactor != null)
        {
            bool hit = interactor.TryGetCurrentUIRaycastResult(out var rr);
            var press = interactor.uiPressInput;
            var pressRef = press != null ? press.inputActionReferencePerformed : null;
            var pressAct = pressRef != null ? pressRef.action : null;
            var lr = ray.GetComponent<LineRenderer>();
            Debug.LogFormat("[Hand] ui hit {0} {1} at {2:F2} m  uiInteraction {3}  press src {4} performed {5} value {6:F2}  action {7} enabled {8}  line {9} pts {10} width {11:F3} alpha {12:F2}  interactor enabled {13}",
                hit ? "YES" : "no", hit && rr.gameObject != null ? rr.gameObject.name : "-", hit ? rr.distance : 0f,
                interactor.enableUIInteraction,
                press != null ? press.inputSourceMode.ToString() : "null",
                press != null && press.ReadIsPerformed(), press != null ? press.ReadValue() : 0f,
                pressAct != null ? pressAct.actionMap.name + "/" + pressAct.name : "none",
                pressAct != null && pressAct.enabled,
                lr != null && lr.enabled, lr != null ? lr.positionCount : 0,
                lr != null ? lr.widthMultiplier : 0f, lr != null ? lr.startColor.a : 0f,
                interactor.isActiveAndEnabled);
        }

        var es = UnityEngine.EventSystems.EventSystem.current;
        Debug.LogFormat("[Hand] ray {0}  fwd {1}  ui over {2}  module {3}",
            ray != null ? ray.position.ToString("F2") : "none",
            ray != null ? ray.forward.ToString("F2") : "-",
            es != null && es.IsPointerOverGameObject() ? "YES" : "no",
            es != null && es.currentInputModule != null ? es.currentInputModule.GetType().Name : "none");
    }

    private void Append(InputDevice d, string path)
    {
        var c = d.TryGetChildControl(path);
        if (c == null) return;
        object v = c.ReadValueAsObject();
        if (v is Vector3 p) v = p.ToString("F2");
        else if (v is float f) v = f.ToString("F2");
        sb.Append("  ").Append(path).Append('=').Append(v);
    }
}
