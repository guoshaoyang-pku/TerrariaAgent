using System;
using Microsoft.Xna.Framework;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Damage shape of a hazard at one instant: an axis-aligned box or a thick
/// segment (lances, rays, swept fast projectiles, rainbow trails).
/// </summary>
public struct Shape
{
    public bool IsSegment;
    public Vector2 A;   // box: center; segment: start
    public Vector2 B;   // box: half extents; segment: end
    public float R;     // segment half thickness

    public static Shape Box(Vector2 center, Vector2 half) => new() { A = center, B = half };
    public static Shape Box(Rectangle r) => new() { A = new Vector2(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f), B = new Vector2(r.Width, r.Height) * 0.5f };
    public static Shape Segment(Vector2 a, Vector2 b, float halfThickness) => new() { IsSegment = true, A = a, B = b, R = halfThickness };

    /// <summary>
    /// Separation between this shape and a box (center c, half extents h):
    /// negative = overlap depth. For segments it uses the segment axis and its
    /// normal as separating axes, a lower bound of the true distance (safe side).
    /// </summary>
    public readonly float Clearance(Vector2 c, Vector2 h)
    {
        if (!IsSegment)
        {
            float dx = Math.Abs(c.X - A.X) - (h.X + B.X);
            float dy = Math.Abs(c.Y - A.Y) - (h.Y + B.Y);
            return Combine(dx, dy);
        }
        Vector2 d = B - A;
        float len = d.Length();
        if (len < 1e-3f)
        {
            float dx = Math.Abs(c.X - A.X) - (h.X + R);
            float dy = Math.Abs(c.Y - A.Y) - (h.Y + R);
            return Combine(dx, dy);
        }
        Vector2 u = d / len;
        Vector2 n = new(-u.Y, u.X);
        Vector2 rel = c - A;
        float alongExtent = Math.Abs(u.X) * h.X + Math.Abs(u.Y) * h.Y;
        float normalExtent = Math.Abs(n.X) * h.X + Math.Abs(n.Y) * h.Y;
        float s = Vector2.Dot(rel, u);
        float dn = Math.Abs(Vector2.Dot(rel, n)) - normalExtent - R;
        float da = Math.Max(-s, s - len) - alongExtent - R;
        return Combine(dn, da);
    }

    private static float Combine(float a, float b)
    {
        if (a < 0 && b < 0) return Math.Max(a, b);
        float pa = Math.Max(a, 0), pb = Math.Max(b, 0);
        return (float)Math.Sqrt(pa * pa + pb * pb);
    }

    /// <summary>Rough distance from a point to the shape, for cheap culling.</summary>
    public readonly float Reach(Vector2 p)
    {
        if (!IsSegment) return Math.Max(0, Vector2.Distance(p, A) - B.Length());
        Vector2 d = B - A;
        float t = d.LengthSquared() < 1e-3f ? 0 : MathHelper.Clamp(Vector2.Dot(p - A, d) / d.LengthSquared(), 0, 1);
        return Math.Max(0, Vector2.Distance(p, A + d * t) - R);
    }
}
