using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace BossPilot.Bosses.JungleTempleOcean;

/// <summary>Boss motion model the route planner can step against a point-mass pilot.</summary>
public interface IBossSim
{
    void Reset();
    /// <summary>Advances one tick toward/around the pilot center; returns the boss center.</summary>
    Vector2 Step(Vector2 pilot);
}

/// <summary>
/// Long-horizon route choice (seconds, not ticks): candidate routes through the arena are
/// followed by a point-mass pilot at cruise speed while the boss is stepped with its kernel
/// model; the route with the best clearance and end distance wins. The MPC then tracks a
/// carrot on that route while dodging, so the pilot circles past the boss instead of
/// getting pinned in a corner the 40-tick planner cannot see out of.
/// </summary>
public sealed class RoutePlanner
{
    public int Horizon = 150;
    public float Cruise = 6.5f;
    public readonly List<Vector2[]> Routes = [];
    public Vector2[] Best;
    public float BestScore;
    private int bestIdx = -1;

    /// <summary>Builds direct, via-top and via-bottom routes to every waypoint.</summary>
    public void BuildRoutes(Vector2 from, IEnumerable<Vector2> waypoints, float topLane, float bottomLane)
    {
        Routes.Clear();
        foreach (Vector2 w in waypoints)
        {
            Routes.Add([w]);
            Routes.Add([new Vector2(from.X, topLane), new Vector2(w.X, topLane), w]);
            Routes.Add([new Vector2(from.X, bottomLane), new Vector2(w.X, bottomLane), w]);
        }
    }

    /// <param name="score">(tick, pilot, boss) -&gt; per-tick clearance in px (smaller is worse).</param>
    /// <param name="terminal">(pilot, boss) -&gt; end-of-route bonus.</param>
    public void Choose(Vector2 from, IBossSim boss, Func<Vector2, Vector2, float> clearance, Func<Vector2, Vector2, float> terminal)
    {
        float best = float.MinValue;
        int bestI = -1;
        for (int r = 0; r < Routes.Count; r++)
        {
            Vector2[] route = Routes[r];
            boss.Reset();
            Vector2 p = from, b = Vector2.Zero;
            int seg = 0;
            float minClear = float.MaxValue;
            for (int t = 0; t < Horizon; t++)
            {
                p = Follow(route, ref seg, p);
                b = boss.Step(p);
                float c = clearance(p, b);
                // Near-term clearance weighs more; far-future boss positions are less certain.
                float w = t < 60 ? 1f : 1f + (t - 60) / 60f;
                minClear = Math.Min(minClear, c * w);
            }
            float s = 3f * Math.Min(minClear, 160f) + terminal(p, b);
            if (r == bestIdx) s += 25f; // stickiness
            if (s > best) { best = s; bestI = r; }
        }
        bestIdx = bestI;
        Best = bestI >= 0 ? Routes[bestI] : null;
        BestScore = best;
    }

    private Vector2 Follow(Vector2[] route, ref int seg, Vector2 p)
    {
        while (seg < route.Length)
        {
            Vector2 d = route[seg] - p;
            float len = d.Length();
            if (len <= Cruise) { p = route[seg]; seg++; if (seg < route.Length) continue; return p; }
            return p + d * (Cruise / len);
        }
        return p;
    }

    /// <summary>Point reached by following the chosen route for the given ticks from the pilot.</summary>
    public Vector2 Carrot(Vector2 from, int ticks)
    {
        if (Best == null) return from;
        int seg = 0;
        Vector2 p = from;
        // Skip vertices already passed (the pilot is past them along the route's first leg).
        for (int t = 0; t < ticks; t++) p = Follow(Best, ref seg, p);
        return p;
    }
}
