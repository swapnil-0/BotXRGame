using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The app's backbone: scan the floor, define the arena, find the bot, invent
/// a layout, have a human build it, verify, play.
///
/// Every later feature hangs off one of these states, so it is worth having
/// the machine explicit rather than scattered across UI callbacks.
///
/// The ROS-facing /game_state strings from the interface spec are coarser than
/// these states, so RosGameState maps many-to-one. That keeps the robot's view
/// of the world stable while the app's setup flow evolves.
/// </summary>
public class GameFlow : MonoBehaviour
{
    public enum State
    {
        ScanFloor,       // waiting for plane detection to find enough floor
        DefineArea,      // player marks the play area corners
        LocateBot,       // find the bot (AprilTag later; manual placement now)
        GenerateLayout,  // pick target positions for this room
        PlaceCups,       // guided physical placement, or skip
        Verify,          // check reality against the layout
        Play,            // the actual game
        End,
    }

    [Header("References")]
    public Transform playAreaCenter;
    public GhostBot bot;
    public PlacementGuide placementGuide;
    public Transform goalMarker;
    [Tooltip("Prefab with a Target component. Spawned per layout entry.")]
    public GameObject targetPrefab;
    public TMPro.TextMeshProUGUI statusText;

    [Header("Layout")]
    public float areaSize = 2.44f;          // 8 ft
    public int greenCount = 4;
    public int redCount = 2;
    [Tooltip("6 is the reliable ceiling in an 8 ft square. Above that the " +
             "generator starts returning short layouts.")]
    public int maxTargets = 6;
    public int seed = 0;
    [Tooltip("Randomise the layout each run. Turn off for a repeatable demo.")]
    public bool randomiseSeed = true;

    [Header("Verification")]
    public VerifyMode verifyMode = VerifyMode.Adaptive;
    [Tooltip("How far a cup may be from its mark before it counts as misplaced.")]
    public float verifyTolerance = 0.15f;

    [Header("Scoring")]
    public int score;

    public State Current { get; private set; } = State.ScanFloor;
    public bool PhysicalCupsInPlay { get; private set; }

    public event Action<State> OnStateChanged;
    public event Action<int> OnScoreChanged;

    private readonly List<LayoutGenerator.TargetSpec> layout = new List<LayoutGenerator.TargetSpec>();
    private readonly List<Target> spawned = new List<Target>();
    private ICupVerifier verifier = new StubCupVerifier();

    /// <summary>Swap in real detection when it exists.</summary>
    public void SetVerifier(ICupVerifier v) { if (v != null) verifier = v; }

    /// <summary>Coarse state for the ROS /game_state topic.</summary>
    public string RosGameState
    {
        get
        {
            switch (Current)
            {
                case State.Play: return "PHASE1_PLAYER";
                case State.End: return "GAME_OVER";
                default: return "SETUP";
            }
        }
    }

    void OnEnable() { Target.OnAnyKnocked += HandleKnock; }
    void OnDisable() { Target.OnAnyKnocked -= HandleKnock; }

    void Start()
    {
        if (placementGuide != null)
        {
            placementGuide.OnAllPlaced += () => Advance(State.Verify);
            placementGuide.OnSkipped += HandleSkip;
        }
        SetState(State.ScanFloor);
    }

    // ------------------------------------------------------------- driving
    // These are wired to UI buttons; each one completes the current step.

    public void FloorFound() { if (Current == State.ScanFloor) Advance(State.DefineArea); }
    public void AreaDefined() { if (Current == State.DefineArea) Advance(State.LocateBot); }

    public void BotLocated()
    {
        if (Current != State.LocateBot) return;
        Advance(State.GenerateLayout);
        BuildLayout();
    }

    /// <summary>
    /// "Done" in the placement step. Also valid from Verify, so that a Strict
    /// failure can be corrected and re-checked without restarting setup.
    /// </summary>
    public void ConfirmPlacement()
    {
        if (Current != State.PlaceCups && Current != State.Verify) return;
        PhysicalCupsInPlay = true;

        if (Current == State.Verify) RunVerification();   // re-check in place
        else Advance(State.Verify);
    }

    public void StartPlay()
    {
        if (Current == State.Verify) Advance(State.Play);
    }

    public void EndGame() { Advance(State.End); }

    // -------------------------------------------------------------- layout

    private void BuildLayout()
    {
        layout.Clear();
        ClearSpawned();

        if (playAreaCenter == null)
        {
            SetStatus("No play area defined.");
            return;
        }

        int green = greenCount;
        int red = redCount;
        // Respect the tested ceiling: above 6 the generator returns short
        // layouts, which reads as a bug rather than a design choice.
        while (green + red > maxTargets && green > 1) green--;
        while (green + red > maxTargets && red > 0) red--;

        var settings = new LayoutGenerator.Settings
        {
            AreaSize = areaSize,
            GreenCount = green,
            RedCount = red,
            Seed = randomiseSeed ? UnityEngine.Random.Range(0, 100000) : seed,
        };

        Vector3 start = bot != null ? bot.transform.position : playAreaCenter.position;
        Vector3? goal = goalMarker != null ? goalMarker.position : (Vector3?)null;

        layout.AddRange(LayoutGenerator.Generate(playAreaCenter, start, goal, settings));

        if (layout.Count < green + red)
        {
            SetStatus(string.Format(
                "Space is tight - placed {0} of {1} targets.", layout.Count, green + red));
        }

        Advance(State.PlaceCups);
        if (placementGuide != null) placementGuide.Begin(layout);
    }

    // ---------------------------------------------------------- verifying

    private void HandleSkip()
    {
        // No physical cups, so the stub verifier is telling the truth: the
        // virtual covers really are exactly where the layout put them.
        PhysicalCupsInPlay = false;
        SetVerifier(new StubCupVerifier());
        Advance(State.Verify);          // Advance runs verification itself
    }

    private void RunVerification()
    {
        var results = verifier.Verify(layout, verifyTolerance);

        var problems = new List<int>();
        for (int i = 0; i < results.Count && i < layout.Count; i++)
        {
            var r = results[i];
            if (!r.Found || r.ErrorMetres > verifyTolerance)
            {
                problems.Add(r.Index);
                continue;
            }
            if (verifyMode == VerifyMode.Adaptive && r.Found)
            {
                // Reality wins: move the target to where the cup actually is,
                // so a slightly-off placement does not block the game.
                var spec = layout[i];
                spec.Position = r.FoundAt;
                layout[i] = spec;
            }
        }

        if (problems.Count > 0 && verifyMode == VerifyMode.Strict)
        {
            SetStatus("Cup " + string.Join(", ", problems) +
                      " not in place. Fix and press Done, or Skip.");
            return;                                   // stay in Verify
        }

        SpawnTargets();
        Advance(State.Play);
    }

    private void SpawnTargets()
    {
        ClearSpawned();
        if (targetPrefab == null) return;

        foreach (var spec in layout)
        {
            var go = Instantiate(targetPrefab, spec.Position, Quaternion.identity);
            var t = go.GetComponent<Target>();
            if (t != null)
            {
                t.kind = spec.Kind;
                t.ApplyKindVisuals();
                spawned.Add(t);
            }
        }
    }

    private void ClearSpawned()
    {
        foreach (var t in spawned)
            if (t != null) Destroy(t.gameObject);
        spawned.Clear();
    }

    // ---------------------------------------------------------- gameplay

    private void HandleKnock(Target t)
    {
        if (Current != State.Play || t == null) return;

        score += t.Points;
        OnScoreChanged?.Invoke(score);

        bool anyLeft = false;
        foreach (var s in spawned)
            if (s != null && !s.IsKnocked && s.kind == Target.Kind.Green) { anyLeft = true; break; }

        if (!anyLeft) Advance(State.End);
    }

    // ------------------------------------------------------------ plumbing

    private void Advance(State next)
    {
        SetState(next);
        if (next == State.Verify) RunVerification();
    }

    private void SetState(State next)
    {
        Current = next;
        OnStateChanged?.Invoke(next);

        switch (next)
        {
            case State.ScanFloor: SetStatus("Look around slowly to find the floor."); break;
            case State.DefineArea: SetStatus("Mark the play area corners."); break;
            case State.LocateBot: SetStatus("Point at the robot to locate it."); break;
            case State.GenerateLayout: SetStatus("Planning the course..."); break;
            case State.PlaceCups: SetStatus("Place the cups, or press Skip."); break;
            case State.Verify: SetStatus("Checking the course..."); break;
            case State.Play: SetStatus("Go! Knock the green targets, avoid the red."); break;
            case State.End: SetStatus("Round over. Score: " + score); break;
        }
    }

    private void SetStatus(string s)
    {
        if (statusText != null) statusText.text = s;
        Debug.Log("[GameFlow] " + s);
    }
}
