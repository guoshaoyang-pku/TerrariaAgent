using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Axis-aligned box in world pixels.</summary>
public struct Box
{
    public float L, T, R, B;

    public Box(float x, float y, float w, float h) { L = x; T = y; R = x + w; B = y + h; }

    public static Box Centered(Vector2 c, float w, float h) => new(c.X - w / 2f, c.Y - h / 2f, w, h);

    public readonly Vector2 Center => new((L + R) / 2f, (T + B) / 2f);

    /// <summary>Separation along the best axis; negative when overlapping.</summary>
    public readonly float Gap(in Box o) => Math.Max(Math.Max(o.L - R, L - o.R), Math.Max(o.T - B, T - o.B));

    public readonly Box Inflate(float m) => new() { L = L - m, T = T - m, R = R + m, B = B + m };

    public readonly Box Union(in Box o) => new() { L = Math.Min(L, o.L), T = Math.Min(T, o.T), R = Math.Max(R, o.R), B = Math.Max(B, o.B) };

    public readonly bool Intersects(in Box o) => Gap(o) < 0f;
}

/// <summary>A predicted hazard trajectory: one box per look-ahead tick (or none).</summary>
public sealed class Track
{
    public readonly Box[] At = new Box[HazardField.Horizon + 1];
    public readonly bool[] On = new bool[HazardField.Horizon + 1];
    public Box Bounds;
    public bool Any;

    public void Reset()
    {
        Array.Clear(On);
        Any = false;
    }

    public void Set(int t, in Box b)
    {
        At[t] = b;
        On[t] = true;
        Bounds = Any ? Bounds.Union(b) : b;
        Any = true;
    }
}

/// <summary>A thick line hazard (Deerclops ice spike) alive for ticks [T0, T1].</summary>
public struct LineHazard
{
    public int T0, T1;
    public Vector2 A, B;
    public float Width;
    public readonly Box Bounds => new()
    {
        L = Math.Min(A.X, B.X) - Width, R = Math.Max(A.X, B.X) + Width,
        T = Math.Min(A.Y, B.Y) - Width, B = Math.Max(A.Y, B.Y) + Width,
    };
}

/// <summary>A hazard whose motion reacts to where the pilot is (e.g. a homing boss).</summary>
public interface IReactiveHazard
{
    /// <summary>Rewinds to the current state before simulating one candidate.</summary>
    void Begin();
    /// <summary>Advances one tick toward the pilot center; returns false when harmless this tick.</summary>
    bool Step(int t, Vector2 pilotCenter, out Box box);
}

/// <summary>Everything that can hurt the pilot over the look-ahead horizon.</summary>
public sealed class HazardField
{
    public const int Horizon = 72;

    public readonly List<Box> Statics = [];
    public readonly List<Track> Tracks = [];
    public readonly List<LineHazard> Lines = [];
    public readonly List<IReactiveHazard> Reactive = [];
    private readonly List<Track> pool = [];
    private int used;

    public void Clear()
    {
        Statics.Clear();
        Tracks.Clear();
        Lines.Clear();
        Reactive.Clear();
        used = 0;
    }

    public Track NewTrack()
    {
        if (used == pool.Count) pool.Add(new Track());
        Track t = pool[used++];
        t.Reset();
        Tracks.Add(t);
        return t;
    }

    /// <summary>Adds a box moving at constant velocity for the whole horizon.</summary>
    public void AddLinear(in Box b, Vector2 v, int ticks = Horizon)
    {
        if (v.LengthSquared() < 0.01f)
        {
            Statics.Add(b);
            return;
        }
        Track tr = NewTrack();
        for (int t = 1; t <= Math.Min(ticks, Horizon); t++)
            tr.Set(t, new Box { L = b.L + v.X * t, R = b.R + v.X * t, T = b.T + v.Y * t, B = b.B + v.Y * t });
    }
}
