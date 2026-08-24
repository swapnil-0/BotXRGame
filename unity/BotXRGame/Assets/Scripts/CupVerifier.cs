using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Was a real cup actually placed where the app asked?
///
/// Deliberately an interface with a stub: real detection needs either headset
/// camera CV or the robot's camera, and neither exists yet. Building the flow
/// against this contract means the whole setup sequence is playable today and
/// swapping in real detection later touches exactly one class.
///
/// Note how much easier this is than open-ended cup detection: the app already
/// knows where each cup SHOULD be, so the question is only "is there something
/// red within tolerance of this known point?" - a template check, not a search.
/// </summary>
public interface ICupVerifier
{
    /// <summary>
    /// Check every expected position. Returns one result per input, in order.
    /// Implementations must not block; call it from a coroutine if slow.
    /// </summary>
    List<CupCheck> Verify(IList<LayoutGenerator.TargetSpec> expected, float toleranceMetres);
}

public struct CupCheck
{
    public int Index;              // matches TargetSpec.Index
    public bool Found;
    public Vector3 FoundAt;        // meaningful only when Found
    public float ErrorMetres;      // distance from the requested position
}

/// <summary>
/// Always reports success at the exact requested position.
///
/// This is what the Skip path uses, and what runs until real detection exists.
/// It is NOT a lie: in Skip mode there are no physical cups, and the virtual
/// covers genuinely are exactly where the layout said.
/// </summary>
public class StubCupVerifier : ICupVerifier
{
    public List<CupCheck> Verify(IList<LayoutGenerator.TargetSpec> expected, float tolerance)
    {
        var results = new List<CupCheck>();
        if (expected == null) return results;

        foreach (var spec in expected)
        {
            results.Add(new CupCheck
            {
                Index = spec.Index,
                Found = true,
                FoundAt = spec.Position,
                ErrorMetres = 0f,
            });
        }
        return results;
    }
}

/// <summary>
/// How the app reacts when a cup is not quite where it was asked to be.
/// </summary>
public enum VerifyMode
{
    /// <summary>Move the target to wherever the cup actually is. Reality wins.</summary>
    Adaptive,
    /// <summary>Refuse to start and name the offending cup. Better demo theatre.</summary>
    Strict,
}
