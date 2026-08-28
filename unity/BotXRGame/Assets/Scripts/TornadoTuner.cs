using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Tune the tornado from inside the headset, with no PC.
///
/// Exists because tuning through the Inspector means taking the headset off,
/// editing, rebuilding and putting it back on - roughly six minutes per guess.
/// Most of the tornado's feel was arrived at that way, and several of those
/// build cycles were spent on values that turned out not to be the problem at
/// all. On a test day away from the PC, Inspector-only tuning is no tuning.
///
/// Controls, live only once the arena is placed:
///   trigger        toggle the tuner (the trigger is otherwise unused after
///                  placement, so it steals nothing)
///   stick up/down  select a parameter
///   stick L/R      change it
///   A              save to PlayerPrefs
///   B              reset the selected parameter to its build default
///
/// The ship is held still while the tuner is open, because the same stick
/// drives both and a ship wandering into a tornado mid-edit is not a useful
/// experiment.
/// </summary>
public class TornadoTuner : MonoBehaviour
{
    [Header("Output")]
    public TMPro.TextMeshProUGUI text;

    [Header("Input")]
    public InputActionReference toggleAction;   // trigger
    public InputActionReference moveAction;     // thumbstick
    public InputActionReference saveAction;     // A
    public InputActionReference resetAction;    // B

    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    [Tooltip("Stick deflection needed to count as a nudge.")]
    [Range(0.3f, 0.95f)] public float stickThreshold = 0.6f;

    [Tooltip("Seconds between repeats while the stick is held.")]
    public float repeatDelay = 0.18f;

    [Header("Scene")]
    public ArenaPlacer placer;
    public GhostBot ship;

    [Header("Overlap")]
    [Tooltip("Hide the HUD's other text while the tuner is open. The tuner " +
             "needs the whole panel to list four parameters, so without this " +
             "it draws straight over the status lines and both become " +
             "unreadable.")]
    public bool hideOtherHudText = true;

    [Tooltip("Text to keep visible even while tuning - the button readout is " +
             "worth keeping, since the tuner is driven entirely by buttons.")]
    public TMPro.TextMeshProUGUI[] keepVisible;

    private readonly List<TMPro.TextMeshProUGUI> hidden =
        new List<TMPro.TextMeshProUGUI>();

    private class Param
    {
        public string Name;
        public Func<float> Get;
        public Action<float> Set;
        public float Step, Min, Max, Default;
        public string Key;
    }

    private readonly List<Param> parameters = new List<Param>();
    private int index;
    private bool open;
    private bool toggleWasPressed, saveWasPressed, resetWasPressed;
    private float nextRepeat;
    private string flash = "";
    private float flashUntil;

    void Start()
    {
        Enable(toggleAction); Enable(moveAction);
        Enable(saveAction); Enable(resetAction);

        if (placer == null) placer = FindAnyObjectByType<ArenaPlacer>();
        if (ship == null) ship = CollectibleCup.Ship;

        BuildParameters();
        LoadSaved();
        if (text != null) text.text = "";
    }

    private static void Enable(InputActionReference r)
    {
        if (r != null && r.action != null) r.action.Enable();
    }

    private void BuildParameters()
    {
        parameters.Clear();
        if (placer == null) return;

        // Values live on the PLACER, which owns them, and are pushed to every
        // live tornado each frame. Editing the spawned instances directly would
        // be lost the moment a new arena was placed.
        parameters.Add(new Param
        {
            Name = "pull (suck)", Key = "tune_suck",
            Get = () => placer.tornadoSuck, Set = v => placer.tornadoSuck = v,
            Step = 0.05f, Min = 0f, Max = 3f, Default = placer.tornadoSuck,
        });
        parameters.Add(new Param
        {
            Name = "swirl", Key = "tune_swirl",
            Get = () => placer.tornadoSwirl, Set = v => placer.tornadoSwirl = v,
            Step = 0.05f, Min = 0f, Max = 2f, Default = placer.tornadoSwirl,
        });
        parameters.Add(new Param
        {
            Name = "size (radius frac)", Key = "tune_radiusfrac",
            Get = () => placer.twinTornadoRadiusFraction,
            Set = v => placer.twinTornadoRadiusFraction = v,
            Step = 0.01f, Min = 0.04f, Max = 0.45f,
            Default = placer.twinTornadoRadiusFraction,
        });
        parameters.Add(new Param
        {
            Name = "capture radius frac", Key = "tune_capfrac",
            Get = () => FirstTornado() != null ? FirstTornado().captureRadiusFraction : 0.22f,
            Set = v => { foreach (var t in Live()) t.captureRadiusFraction = v; },
            Step = 0.02f, Min = 0.05f, Max = 0.6f, Default = 0.22f,
        });
    }

    private static IEnumerable<Tornado> Live()
    {
        return FindObjectsByType<Tornado>(FindObjectsInactive.Include);
    }

    private static Tornado FirstTornado()
    {
        foreach (var t in Live()) return t;
        return null;
    }

    void Update()
    {
        if (parameters.Count == 0) { BuildParameters(); LoadSaved(); }
        if (placer == null || !placer.IsPlaced) return;

        bool toggle = Read(toggleAction) > pressThreshold;
        if (toggle && !toggleWasPressed) SetOpen(!open);
        toggleWasPressed = toggle;

        if (!open) return;

        HandleNavigation();
        HandleSaveReset();
        PushToTornadoes();
        Render();
    }

    private void SetOpen(bool value)
    {
        open = value;

        // Freeze the ship: the stick is shared, and a ship drifting into a
        // vortex while you are editing the vortex is not a measurement.
        if (ship == null) ship = CollectibleCup.Ship;
        if (ship != null)
        {
            ship.MotionLocked = open;
            if (open) ship.ResetMotion();
        }

        SetOtherHudTextVisible(!open);

        if (!open && text != null) text.text = "";
        if (open) Flash("tuner open - trigger to close");
    }

    /// <summary>
    /// Hide or restore the HUD's other text.
    ///
    /// Restores only what THIS hid, rather than enabling everything: some HUD
    /// lines are legitimately off at other times, and blanket re-enabling would
    /// switch them on as a side effect of closing the tuner.
    /// </summary>
    private void SetOtherHudTextVisible(bool visible)
    {
        if (!hideOtherHudText || text == null) return;

        if (!visible)
        {
            hidden.Clear();
            var parent = text.transform.parent != null ? text.transform.parent : text.transform;

            foreach (var t in parent.GetComponentsInChildren<TMPro.TextMeshProUGUI>(false))
            {
                if (t == text || !t.enabled) continue;
                if (IsKept(t)) continue;
                t.enabled = false;
                hidden.Add(t);
            }
        }
        else
        {
            foreach (var t in hidden) if (t != null) t.enabled = true;
            hidden.Clear();
        }
    }

    private bool IsKept(TMPro.TextMeshProUGUI t)
    {
        if (keepVisible == null) return false;
        foreach (var k in keepVisible) if (k == t) return true;
        return false;
    }

    private void HandleNavigation()
    {
        Vector2 stick = (moveAction != null && moveAction.action != null)
            ? moveAction.action.ReadValue<Vector2>() : Vector2.zero;

        if (Mathf.Abs(stick.x) < stickThreshold && Mathf.Abs(stick.y) < stickThreshold)
        {
            nextRepeat = 0f;      // released: next nudge acts immediately
            return;
        }

        if (Time.unscaledTime < nextRepeat) return;
        nextRepeat = Time.unscaledTime + repeatDelay;

        // Vertical wins when both are deflected, so a diagonal push does not
        // change a value while you are only trying to move the selection.
        if (Mathf.Abs(stick.y) >= Mathf.Abs(stick.x))
        {
            index += stick.y > 0f ? -1 : 1;
            index = Mathf.Clamp(index, 0, parameters.Count - 1);
        }
        else
        {
            var p = parameters[index];
            float v = Mathf.Clamp(p.Get() + (stick.x > 0f ? p.Step : -p.Step), p.Min, p.Max);
            p.Set(v);
        }
    }

    private void HandleSaveReset()
    {
        bool save = Read(saveAction) > pressThreshold;
        if (save && !saveWasPressed) SaveAll();
        saveWasPressed = save;

        bool reset = Read(resetAction) > pressThreshold;
        if (reset && !resetWasPressed)
        {
            var p = parameters[index];
            p.Set(p.Default);
            Flash(p.Name + " reset to " + p.Default.ToString("F2"));
        }
        resetWasPressed = reset;
    }

    /// <summary>
    /// Push the placer's values onto every live tornado, every frame.
    ///
    /// Cheap, and it means a tornado spawned after a change still gets it -
    /// including the ones created when the arena is placed again.
    /// </summary>
    private void PushToTornadoes()
    {
        if (placer == null) return;

        float radius = placer.arenaSize * placer.twinTornadoRadiusFraction;

        foreach (var t in Live())
        {
            t.suckMetersPerSecond = placer.tornadoSuck;
            t.swirlMetersPerSecond = placer.tornadoSwirl;

            if (Mathf.Abs(t.influenceRadius - radius) > 1e-4f)
                t.ApplyRadius(radius);
        }
    }

    private void SaveAll()
    {
        foreach (var p in parameters) PlayerPrefs.SetFloat(p.Key, p.Get());
        PlayerPrefs.Save();
        Flash("saved");
        Debug.Log("[Tuner] saved tornado parameters");
    }

    private void LoadSaved()
    {
        foreach (var p in parameters)
            if (PlayerPrefs.HasKey(p.Key))
                p.Set(Mathf.Clamp(PlayerPrefs.GetFloat(p.Key), p.Min, p.Max));
    }

    private void Flash(string msg)
    {
        flash = msg;
        flashUntil = Time.unscaledTime + 1.5f;
    }

    private float Read(InputActionReference r)
    {
        return (r == null || r.action == null) ? 0f : r.action.ReadValue<float>();
    }

    private void Render()
    {
        if (text == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("TUNER   stick: pick/change   A save   B reset   trigger close");

        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            sb.AppendFormat("{0} {1,-20} {2:F3}\n",
                i == index ? ">" : " ", p.Name, p.Get());
        }

        // Actual radius in metres, not just the fraction. A fraction of an
        // arena size is not something anyone can picture standing in a room.
        if (placer != null)
            sb.AppendFormat("  radius {0:F3} m of arena {1:F2} m\n",
                placer.arenaSize * placer.twinTornadoRadiusFraction, placer.arenaSize);

        if (Time.unscaledTime < flashUntil) sb.AppendLine("  " + flash);

        text.text = sb.ToString();
    }
}
