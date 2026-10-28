using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Moon Lord attack clock. Hands and head pick their sub-attack from
/// NPC.MoonLordAttacksArray[core.ai[2] (always 0), part, *, slot] by
/// accumulating durations against ai[1]; True Eyes use MoonLordAttacksArray2.
/// </summary>
public static class MoonClock
{
    public readonly record struct Slot(int Attack, int Elapsed, int Duration, int Index);

    /// <param name="part">0 = left hand (ai[2] == 0), 1 = right hand, 2 = head.</param>
    public static Slot Part(int part, float ai1)
    {
        int acc = 0;
        for (int k = 0; k < 5; k++)
        {
            int dur = NPC.MoonLordAttacksArray[0, part, 1, k];
            if (dur + acc > ai1) return new Slot(NPC.MoonLordAttacksArray[0, part, 0, k], (int)ai1 - acc, dur, k);
            acc += dur;
        }
        return new Slot(NPC.MoonLordAttacksArray[0, part, 0, 0], 0, NPC.MoonLordAttacksArray[0, part, 1, 0], 0);
    }

    public static Slot Eye(float ai1)
    {
        int acc = 0;
        for (int k = 0; k < 10; k++)
        {
            int dur = NPC.MoonLordAttacksArray2[1, k];
            if (dur + acc > ai1) return new Slot(NPC.MoonLordAttacksArray2[0, k], (int)ai1 - acc, dur, k);
            acc += dur;
        }
        return new Slot(NPC.MoonLordAttacksArray2[0, 0], 0, NPC.MoonLordAttacksArray2[1, 0], 0);
    }

    /// <summary>The slot <paramref name="ahead"/> ticks from now.</summary>
    public static Slot PartAt(int part, float ai1, int ahead)
    {
        int cycle = 0;
        for (int k = 0; k < 5; k++) cycle += NPC.MoonLordAttacksArray[0, part, 1, k];
        return Part(part, (ai1 + ahead) % cycle);
    }

    public static int EyeCycle
    {
        get
        {
            int c = 0;
            for (int k = 0; k < 10; k++) c += NPC.MoonLordAttacksArray2[1, k];
            return c;
        }
    }

    public static int PartIndex(NPC n) => n.type == NPCID.MoonLordHead ? 2 : (n.ai[2] == 0f ? 0 : 1);
}

/// <summary>
/// Moon Lord core chase (aiStyle 77, ai[0] 0/1): steers towards pilot + (0,130)
/// with desired Normalize(offset - v) * 8, SimpleFlyMovement 0.5 then halved
/// (lerp with the old velocity), idle inside 20 px. Hands and head ride on the
/// core, so every emitter is shifted by this predicted core displacement.
/// Reactive; it must be stepped before the hazards that read it.
/// </summary>
public sealed class MoonFrame : Hazard
{
    private readonly Vector2 c0, v0;
    private Vector2 c, v;

    public override bool Reactive => true;
    public Vector2 Shift => c - c0;

    public MoonFrame(NPC core) { c0 = core.Center; v0 = core.velocity; Rewind(); }

    public override void Rewind() { c = c0; v = v0; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        Vector2 off = pilot - c + new Vector2(0f, 130f);
        if (off.Length() > 20f)
        {
            Vector2 desired = Vector2.Normalize(off - v) * 8f;
            Vector2 old = v;
            const float a = 0.5f;
            if (v.X < desired.X) { v.X += a; if (v.X < 0 && desired.X > 0) v.X += a; }
            else if (v.X > desired.X) { v.X -= a; if (v.X > 0 && desired.X < 0) v.X -= a; }
            if (v.Y < desired.Y) { v.Y += a; if (v.Y < 0 && desired.Y > 0) v.Y += a; }
            else if (v.Y > desired.Y) { v.Y -= a; if (v.Y > 0 && desired.Y < 0) v.Y -= a; }
            v = Vector2.Lerp(v, old, 0.5f);
        }
        c += v;
    }

    public override void Emit(List<Shape> sink) { }
}

/// <summary>Contact box that rides on the Moon Lord core (spent hands and head deal 80).</summary>
public sealed class FramedBody(NPC n, MoonFrame frame) : Hazard
{
    private readonly Vector2 c0 = n.Center, half = new Vector2(n.width, n.height) * 0.5f + new Vector2(4f);
    public override bool Reactive => true;
    public override void Rewind() => Dead = false;
    public override void Step(Vector2 pilot) { }
    public override void Emit(List<Shape> sink) => sink.Add(Shape.Box(c0 + frame.Shift, half));
}

/// <summary>
/// PhantasmalEye (452, aiStyle 82): 45 + 90 ticks drifting upwards (vx follows
/// the ai[1] spin, clamped to 6; vy -0.08/tick, capped at -7), then dives at
/// the pilot: steering 0.4/tick towards Lerp(v, 14*dir, 0.6) with vy >= 6, so it
/// can never climb back. Once its center is within 30 px of the pilot's (checked
/// before it moves) or its 600-tick life ends, Projectile.Kill blows it up as a
/// 144x144 hitbox: that explosion, not the 14x14 body, is what usually hits. Reactive.
/// </summary>
public sealed class PhantasmalEye : Hazard
{
    private readonly Vector2 c0, v0;
    private readonly float ai0_, ai1_, la0;
    private readonly int life0;
    private Vector2 c, v, prev;
    private float ai0, ai1, la;
    private int life;
    private bool blast;
    private int blastFor;   // kept for a few ticks so sparse samples late in the horizon still see it

    public override bool Reactive => true;

    public PhantasmalEye(Projectile pr) { c0 = pr.Center; v0 = pr.velocity; ai0_ = pr.ai[0]; ai1_ = pr.ai[1]; la0 = pr.localAI[0]; life0 = pr.timeLeft; Rewind(); }

    public override void Rewind() { c = prev = c0; v = v0; ai0 = ai0_; ai1 = ai1_; la = la0; life = life0; blast = false; blastFor = 3; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        if (blast) { if (--blastFor <= 0) Dead = true; return; }
        if (ai0 < 2)
        {
            la++;
            if (ai0 == 0 && la >= 45) { la = 0; ai0 = 1; ai1 = -ai1; }
            else if (ai0 == 1 && la >= 90) { la = 0; ai0 = 2; }
            v.X = MathHelper.Clamp(v.RotatedBy(ai1).X, -6f, 6f);
            v.Y -= 0.08f;
            if (v.Y > 0) v.Y -= 0.2f;
            if (v.Y < -7) v.Y = -7;
        }
        else
        {
            Vector2 d = pilot - c;
            if (d.Length() < 30f) { blast = true; return; }
            Vector2 want = Vector2.Lerp(v, Vector2.Normalize(d) * 14f, 0.6f);
            if (want.Y < 6) want.Y = 6;
            const float a = 0.4f;
            if (v.X < want.X) { v.X += a; if (v.X < 0 && want.X > 0) v.X += a; }
            else if (v.X > want.X) { v.X -= a; if (v.X > 0 && want.X < 0) v.X -= a; }
            if (v.Y < want.Y) { v.Y += a; if (v.Y < 0 && want.Y > 0) v.Y += a; }
            else if (v.Y > want.Y) { v.Y -= a; if (v.Y > 0 && want.Y < 0) v.Y -= a; }
        }
        c += v;
        if (--life <= 0) blast = true;
    }

    public override void Emit(List<Shape> sink)
    {
        if (Dead) return;
        if (blast) sink.Add(Shape.Box(c, new Vector2(72f)));
        else sink.Add(Vector2.DistanceSquared(c, prev) > 64f ? Shape.Segment(prev, c, 7f) : Shape.Box(c, new Vector2(7)));
        prev = c;
    }
}

/// <summary>
/// PhantasmalDeathray (455, aiStyle 84): a 2400 px line from its emitter that
/// turns by ai[0] rad/tick for 180 ticks, 36 * scale wide (scale 1 on the head,
/// 0.4 on a True Eye). Head rays ride on the predicted core; eye rays are
/// extrapolated with the eye's velocity.
/// </summary>
public sealed class Deathray : Hazard
{
    private readonly Vector2 o0, ov;
    private readonly float rot0, spin, la0, halfWidth, len0;
    private readonly MoonFrame frame;
    private Vector2 o;
    private float rot, la, len;

    /// <summary>A live ray; head rays ride on <paramref name="frame"/> (the head is re-centred on the core each tick).</summary>
    public Deathray(Projectile pr, MoonFrame frame)
    {
        NPC owner = Main.npc[(int)pr.ai[1]];
        this.frame = owner.type == NPCID.MoonLordHead ? frame : null;
        o0 = pr.Center; ov = owner.active && this.frame == null ? owner.velocity : Vector2.Zero;
        rot0 = pr.velocity.ToRotation(); spin = pr.ai[0]; la0 = pr.localAI[0]; len0 = pr.localAI[1];
        halfWidth = 18f * (owner.type == NPCID.MoonLordFreeEye ? 0.4f : 1f);
        Rewind();
    }

    public override bool Reactive => frame != null;

    public override void Rewind() { o = o0; rot = rot0; la = la0; len = len0; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        la++;
        if (la <= 0) return;
        o = frame != null ? o0 + frame.Shift : o + ov;
        rot += spin;
        len = MathHelper.Lerp(len, 2400f, 0.5f);
        if (la >= 180) Dead = true;
    }

    public override void Emit(List<Shape> sink)
    {
        if (la > 0 && !Dead) sink.Add(Shape.Segment(o, o + rot.ToRotationVector2() * Math.Max(len, 200f), halfWidth));
    }
}

/// <summary>
/// A deathray about to be cast (head sub-attack 1 or True Eye sub-attack 4, at
/// elapsed 180): aimed at the pilot then, rotated 60 degrees away on the side
/// the pilot is on, and swept back through it at 2*pi/540 rad/tick. The head
/// rides on the core frame; a True Eye has braked to a stop by then.
/// Reactive on the pilot's position at the cast.
/// </summary>
public sealed class CastDeathray : Hazard
{
    private readonly Vector2 head0;
    private readonly MoonFrame frame;
    private readonly int castIn;
    private readonly float halfWidth;
    private int t;
    private Vector2 aim;
    private float spin;
    private bool cast;

    public override bool Reactive => true;
    private Vector2 Head => frame != null ? head0 + frame.Shift : head0;

    public CastDeathray(NPC emitter, MoonFrame frame, int castIn)
    {
        head0 = emitter.Center; this.frame = frame; this.castIn = castIn;
        halfWidth = emitter.type == NPCID.MoonLordFreeEye ? 7.2f : 18f;
        Rewind();
    }

    public override void Rewind() { t = 0; cast = false; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        t++;
        if (!cast && t >= castIn)
        {
            cast = true;
            Vector2 d = (pilot - Head).SafeNormalize(Vector2.UnitY);
            float side = d.X < 0f ? 1f : -1f;
            aim = d.RotatedBy(-side * MathHelper.TwoPi / 6f);
            spin = side * MathHelper.TwoPi / 540f;
        }
        else if (cast) aim = aim.RotatedBy(spin);
        if (cast && t - castIn >= 180) Dead = true;
    }

    public override void Emit(List<Shape> sink)
    {
        if (cast) sink.Add(Shape.Segment(Head + aim * 40f, Head + aim * 2400f, halfWidth));
    }
}

/// <summary>
/// Phantasmal bolts (462) a part will fire: hands/head sub-attack 3 and True
/// Eye sub-attack 1 fire at elapsed duration-14 and -7, aimed at the
/// pilot's center + 20 x its velocity, 8 px/tick x 4 updates. Reactive.
/// </summary>
public sealed class BoltVolley : Hazard
{
    private readonly Vector2 from;
    private readonly int[] fireAt;
    private readonly List<(Vector2 pos, Vector2 vel, Vector2 prev)> bolts = [];
    private readonly Vector2 pilot0;
    private Vector2 lastPilot;
    private int t;

    public override bool Reactive => true;

    private readonly MoonFrame frame;

    /// <param name="frame">Core frame for hand/head emitters, null for a True Eye.</param>
    public BoltVolley(Vector2 from, MoonFrame frame, Vector2 pilotNow, params int[] fireAt) { this.from = from; this.frame = frame; pilot0 = pilotNow; this.fireAt = fireAt; Rewind(); }

    public override void Rewind() { t = 0; bolts.Clear(); lastPilot = pilot0; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        t++;
        for (int i = 0; i < bolts.Count; i++) { var b = bolts[i]; b.prev = b.pos; b.pos += b.vel; bolts[i] = b; }
        Vector2 pv = pilot - lastPilot;
        lastPilot = pilot;
        foreach (int f in fireAt)
            if (f == t)
            {
                // Created during the NPC update, then moved by the projectile update of the same tick.
                Vector2 o = frame != null ? from + frame.Shift : from;
                Vector2 v = (pilot + pv * 20f - o).SafeNormalize(Vector2.UnitY) * 32f;
                bolts.Add((o + v, v, o));
            }
    }

    public override void Emit(List<Shape> sink)
    {
        foreach (var b in bolts) sink.Add(Shape.Segment(b.prev, b.pos, 5f));
    }
}

/// <summary>
/// Phantasmal spheres (454) parked around a hand or True Eye, plus their launch:
/// hands launch at elapsed 292 of sub-attack 2 towards the pilot as seen from
/// 350 px above the hand, eyes at elapsed 105 of their sub-attack 2 towards the
/// pilot; launched spheres fly 12 px/tick x 2 updates in parallel and blow up
/// as a 208x208 hitbox when their 600-tick life ends. Reactive.
/// </summary>
public sealed class SphereVolley : Hazard
{
    private readonly List<(Vector2 c, Vector2 v, bool launched, int life)> init = [];
    private readonly List<(Vector2 c, Vector2 v, bool launched, Vector2 prev, int life)> cur = [];
    private readonly Vector2 aimFrom;
    private readonly int launchIn;
    private readonly MoonFrame frame;

    public override bool Reactive => true;

    private readonly int kickIn;

    /// <param name="kickIn">Tick at which parked spheres get +(0,-7) (True Eye elapsed 75), or 0.</param>
    public SphereVolley(Vector2 aimFrom, MoonFrame frame, int launchIn, int kickIn = 0) { this.aimFrom = aimFrom; this.frame = frame; this.launchIn = launchIn; this.kickIn = kickIn; }

    public void Add(Projectile pr) => init.Add((pr.Center, pr.ai[0] == -1f ? pr.velocity * 2f : pr.velocity, pr.ai[0] == -1f, pr.timeLeft));
    public bool Any => init.Count > 0;
    private int t;

    public override void Rewind()
    {
        t = 0; Dead = false;
        cur.Clear();
        foreach (var s in init) cur.Add((s.c, s.v, s.launched, s.c, s.life));
    }

    public override void Step(Vector2 pilot)
    {
        t++;
        Vector2 dir = Vector2.Zero;
        if (t == launchIn) dir = (pilot - (frame != null ? aimFrom + frame.Shift : aimFrom)).SafeNormalize(Vector2.UnitY) * 24f;
        for (int i = 0; i < cur.Count; i++)
        {
            var s = cur[i];
            s.prev = s.c;
            if (!s.launched && t == kickIn) s.v += new Vector2(0f, -7f);
            if (!s.launched && t == launchIn) { s.v = dir; s.launched = true; }
            if (!s.launched) s.v *= 0.96f;
            s.c += s.v;
            s.life--;
            cur[i] = s;
        }
    }

    public override void Emit(List<Shape> sink)
    {
        foreach (var s in cur)
        {
            if (s.life < 0) continue;
            if (s.life == 0) sink.Add(Shape.Box(s.c, new Vector2(104f)));   // Kill(): 208x208 blast
            else sink.Add(Vector2.DistanceSquared(s.c, s.prev) > 64f ? Shape.Segment(s.prev, s.c, 20f) : Shape.Box(s.c, new Vector2(20)));
        }
    }
}

/// <summary>
/// True Eye of Cthulhu body (400, aiStyle 81, 60 contact damage): chases
/// pilot - (0,200) at 24 px/tick with 1/30 smoothing in sub-attack 0, charges
/// 24 px/tick at the pilot in elapsed 105-119 of sub-attack 2, loops in
/// elapsed 45-184 of sub-attack 3 (heading turns by ai[2] per tick, x0.95 every
/// 40 ticks, speed 8 to 20), otherwise slows.
/// </summary>
public sealed class TrueEyeBody : Hazard
{
    private readonly Vector2 c0, v0, half;
    private readonly float ai1_, ai2_, la0_;
    private Vector2 c, v;
    private float ai1, aimRot, spin, heading;

    public override bool Reactive => true;

    public TrueEyeBody(NPC n) { c0 = n.Center; v0 = n.velocity; ai1_ = n.ai[1]; ai2_ = n.ai[2]; la0_ = n.localAI[0]; half = new Vector2(n.width, n.height) * 0.5f; Rewind(); }

    public override void Rewind() { c = c0; v = v0; ai1 = ai1_; aimRot = ai2_; spin = ai2_; heading = la0_; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        ai1++;
        MoonClock.Slot s = MoonClock.Eye(ai1);
        switch (s.Attack)
        {
            case 0:
                Vector2 want = Vector2.Normalize(pilot - c - Vector2.UnitY * 200f) * 24f;
                v = (v * 29f + want) / 30f;
                break;
            case 2 when s.Elapsed >= 75 && s.Elapsed < 105:
                if (s.Elapsed == 75) v = Vector2.UnitY * -7f;
                v.Y *= 0.96f;
                aimRot = (pilot - c).ToRotation() + MathHelper.PiOver2;
                break;
            case 2 when s.Elapsed >= 105 && s.Elapsed < 120:
                v = (aimRot - MathHelper.PiOver2).ToRotationVector2() * 24f;
                break;
            case 3 when s.Elapsed >= 45 && s.Elapsed < 185:
                // Spin is re-rolled at elapsed 45 (random sign); keep the live one if we are past it.
                if ((s.Elapsed - 45) % 40 == 0 && s.Elapsed > 45) spin *= 0.95f;
                heading += spin;
                v = heading.ToRotationVector2() * MathHelper.Lerp(8f, 20f, (s.Elapsed - 45) / 140f);
                break;
            default:
                v *= 0.92f;
                break;
        }
        c += v;
    }

    public override void Emit(List<Shape> sink) => sink.Add(Shape.Box(c, half));
}
