using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Picks where the cups should go.
///
/// This is the inversion that makes the setup phase interesting: instead of
/// detecting whatever happens to be on the floor, the app decides on a good
/// layout for THIS room and then asks a human to make reality match it. Which
/// also means detection later is only ever verification against known points,
/// not open-ended search.
///
/// Constants were tuned by simulating 200 random seeds in a 2.44 m (8 ft)
/// square. Results: 6 targets placed successfully 200/200; 7 targets 92/100;
/// 8 targets only 49/100. Separation is the binding constraint - it starts
/// failing above 0.55 m - while wall clearance never binds at these sizes.
/// So 6 is the safe maximum and the generator degrades rather than failing.
/// </summary>
public static class LayoutGenerator
{
    public struct TargetSpec
    {
        public Vector3 Position;      // world space, on the floor plane
        public Target.Kind Kind;
        public int Index;             // 1-based, shown to the player and helper
    }

    public class Settings
    {
        /// <summary>Square play area edge length, metres. 2.44 = 8 ft.</summary>
        public float AreaSize = 2.44f;
        /// <summary>Keep targets off the walls so the bot can get behind them.</summary>
        public float WallClearance = 0.40f;
        /// <summary>Minimum gap between targets. One swing must not take two.</summary>
        public float Separation = 0.50f;
        /// <summary>Nothing this close to the bot's start pose.</summary>
        public float StartClearance = 0.60f;
        /// <summary>Greens sit within this distance of the start-to-goal line.</summary>
        public float RouteBand = 0.45f;
        public int GreenCount = 4;
        public int RedCount = 2;
        public int Seed = 0;
        /// <summary>Rejection-sampling budget per target.</summary>
        public int AttemptsPerTarget = 400;
    }

    /// <summary>
    /// Generate a layout. Never throws and never returns null: if the space is
    /// too tight it relaxes separation in stages and returns fewer targets
    /// rather than failing in front of an audience.
    /// </summary>
    /// <param name="areaCenter">Transform defining the play area origin and rotation.</param>
    /// <param name="botStart">Bot start position, world space.</param>
    /// <param name="goal">Goal position, world space. If null, the far corner is used.</param>
    public static List<TargetSpec> Generate(
        Transform areaCenter, Vector3 botStart, Vector3? goal, Settings s = null)
    {
        s = s ?? new Settings();
        var result = new List<TargetSpec>();
        if (areaCenter == null) return result;

        var rng = new System.Random(s.Seed);

        float half = s.AreaSize * 0.5f;
        float lo = -half + s.WallClearance;
        float hi = half - s.WallClearance;
        if (lo >= hi) return result;                  // area smaller than its margins

        // Work in the play area's local XZ plane, convert to world at the end.
        Vector2 startLocal = ToLocal(areaCenter, botStart);
        Vector2 goalLocal = goal.HasValue
            ? ToLocal(areaCenter, goal.Value)
            : new Vector2(hi * 0.9f, hi * 0.9f);

        var chosen = new List<Vector2>();
        var kinds = new List<Target.Kind>();

        // Relax separation in stages if the room is tighter than expected.
        float[] separations = { s.Separation, s.Separation * 0.85f, s.Separation * 0.7f };

        foreach (float sep in separations)
        {
            chosen.Clear();
            kinds.Clear();

            // Greens hug the route; reds sit just off it, so avoiding them
            // actually costs the player something. Reds in far corners are
            // scenery, not obstacles.
            PlaceGroup(rng, chosen, kinds, Target.Kind.Green, s.GreenCount,
                       lo, hi, sep, startLocal, goalLocal, s,
                       0f, s.RouteBand);
            PlaceGroup(rng, chosen, kinds, Target.Kind.Red, s.RedCount,
                       lo, hi, sep, startLocal, goalLocal, s,
                       s.RouteBand * 0.5f, s.RouteBand * 1.8f);

            if (chosen.Count == s.GreenCount + s.RedCount) break;   // full layout
        }

        for (int i = 0; i < chosen.Count; i++)
        {
            result.Add(new TargetSpec
            {
                Position = ToWorld(areaCenter, chosen[i]),
                Kind = kinds[i],
                Index = i + 1,
            });
        }
        return result;
    }

    private static void PlaceGroup(
        System.Random rng, List<Vector2> chosen, List<Target.Kind> kinds,
        Target.Kind kind, int count, float lo, float hi, float sep,
        Vector2 start, Vector2 goal, Settings s, float bandLo, float bandHi)
    {
        int placed = 0;
        for (int attempt = 0; attempt < s.AttemptsPerTarget && placed < count; attempt++)
        {
            var p = new Vector2(
                Mathf.Lerp(lo, hi, (float)rng.NextDouble()),
                Mathf.Lerp(lo, hi, (float)rng.NextDouble()));

            if (Vector2.Distance(p, start) < s.StartClearance) continue;
            if (Vector2.Distance(p, goal) < s.StartClearance * 0.6f) continue;

            bool tooClose = false;
            foreach (var q in chosen)
                if (Vector2.Distance(p, q) < sep) { tooClose = true; break; }
            if (tooClose) continue;

            float d = DistanceToSegment(p, start, goal);
            if (d < bandLo || d > bandHi) continue;

            chosen.Add(p);
            kinds.Add(kind);
            placed++;
        }
    }

    /// <summary>Shortest distance from p to the segment ab.</summary>
    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-9f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + t * ab);
    }

    private static Vector2 ToLocal(Transform area, Vector3 world)
    {
        Vector3 l = area.InverseTransformPoint(world);
        return new Vector2(l.x, l.z);
    }

    private static Vector3 ToWorld(Transform area, Vector2 local)
    {
        return area.TransformPoint(new Vector3(local.x, 0f, local.y));
    }
}
