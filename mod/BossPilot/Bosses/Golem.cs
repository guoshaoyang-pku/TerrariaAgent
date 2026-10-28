using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.JungleTempleOcean;

namespace BossPilot.Bosses;

/// <summary>
/// Golem (Classic: head 25000, body 15000 invulnerable while the head lives, fists 10000).
/// Arenas sit below the surface with Lihzahrd wall behind the pilot (ZoneLihzhardTemple), so
/// the AI's x2 "balance" enrage never triggers: enclosed = a 190x44 Lihzahrd hall (brick shell,
/// Lihzahrd wall, three platform rows, altar) dug into the real underground jungle; natural =
/// the real temple altar room (spikes, traps and all) with two platform rows, Golem at the altar.
/// Control: MPC with kernel models of the body jump (AI_045: charge 300 at 1-27/tick, jump
/// 4 px/s sideways, -12.1 up, 0.2 air steer, gravity 0.3), both fists (AI_047: 60-tick reload,
/// 30-tick wind-up at the shoulder, straight punch at 12-48 px/tick aimed at the pilot, recall
/// beyond 700 px) and head fireballs fired when ai[1] reaches 300. The route layer keeps the
/// pilot beyond the 700 px fist leash where the hall allows it and plans passes over the body.
/// </summary>
public sealed class Golem : PilotPlan
{
    public override string Boss => "golem";

    private static readonly HashSet<int> Npcs = [NPCID.Golem, NPCID.GolemHead, NPCID.GolemFistLeft, NPCID.GolemFistRight];
    protected override ISet<int> ModelledNpcs => Npcs;

    // Loadouts. Golem follows Plantera: the post-Plantera ranger kit is Venus Magnum (Plantera
    // drop) with Chlorophyte bullets, Shroomite armor (Truffle's autohammer), Sniper Scope from
    // the dungeon and Leaf Wings from the Witch Doctor. stage_plus uses post-Golem gear (Chain
    // Gun, Beetle Wings, Destroyer Emblem); min_preboss is the pre-Plantera Megashark kit.
    private static Kit KitFor(string tier) => tier switch
    {
        "min_preboss" => new Kit(ItemID.Megashark, ItemID.ChlorophyteBullet,
            [ItemID.HallowedHelmet, ItemID.HallowedPlateMail, ItemID.HallowedGreaves],
            [ItemID.FrostsparkBoots, ItemID.FlameWings, ItemID.RangerEmblem],
            [BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration], 500),
        "stage_plus" => new Kit(ItemID.ChainGun, ItemID.ChlorophyteBullet,
            [ItemID.ShroomiteMask, ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings],
            [ItemID.FrostsparkBoots, ItemID.BeetleWings, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.AvengerEmblem],
            Kits.StandardBuffs, 600),
        _ => new Kit(ItemID.VenusMagnum, ItemID.ChlorophyteBullet,
            [ItemID.ShroomiteMask, ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings],
            [ItemID.FrostsparkBoots, ItemID.LeafWings, ItemID.SniperScope, ItemID.AvengerEmblem, ItemID.RangerEmblem],
            Kits.StandardBuffs, 540),
    };

    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Kits.Hardmode(plantDown: true, golemDown: false);
        Kits.Apply(p, KitFor(s.Loadout), s);
    }

    private const int HalfWidth = 95, Height = 44;
    private static readonly int[] PlatformRows = [11, 22, 33];
    private Point golemSpawn;

    public override Point ArenaSite(ScenarioConfig s)
    {
        if (s.Arena == "natural" && Arenas.TempleAltar() is Point a)
        {
            // Start 26 tiles left of the altar, on the room floor.
            int x = a.X - 26;
            return new Point(x, Arenas.GroundBelow(x, a.Y - 4));
        }
        return Arenas.JungleSites()[2];
    }

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        if (s.Arena == "natural" && Arenas.TempleAltar() is Point a)
        {
            // The altar room floor is lumpy (bricks up to 4 tiles high): measure walls 6 tiles up.
            int floor = a.Y + 2, probe = floor - 6;
            int left = a.X, right = a.X + 2, top = probe;
            while (left > a.X - 120 && !Arenas.Solid(left - 1, probe)) left--;
            while (right < a.X + 120 && !Arenas.Solid(right + 1, probe)) right++;
            while (top > floor - 120 && !Arenas.Solid(a.X + 1, top - 1)) top--;
            Arena = ArenaInfo.FromTiles(left, right, top, floor);
            // Two rows of platforms in the real altar room (what players place there), so the
            // pilot can rest above the body's jump height and refuel its wings.
            var natRows = new List<int> { floor - 12, floor - 23 };
            foreach (int r in natRows)
                for (int x = left + 3; x <= right - 3; x++)
                    if (!Main.tile[x, r].HasTile) WorldGen.PlaceTile(x, r, TileID.Platforms, mute: true, forced: true, style: 0);
            foreach (int r in natRows) Arena.Platforms.Add(r * 16f);
            Arena.ScanGround();
            golemSpawn = new Point(a.X * 16 + 24, floor * 16);
            PilotLog.Line($"temple room x {left}..{right} top {top} floor {floor} altar {a}");
            return;
        }
        int x0 = site.X - HalfWidth, x1 = site.X + HalfWidth;
        // A Lihzahrd hall: brick floor and shell, Lihzahrd wall behind everything.
        Arenas.Carve(site, 2 * HalfWidth + 1, Height, TileID.LihzahrdBrick, TileID.LihzahrdBrick, 4);
        for (int x = x0 - 1; x <= x1 + 1; x++)
        for (int y = site.Y - Height - 1; y <= site.Y + 10; y++)
        {
            bool edge = x == x0 - 1 || x == x1 + 1 || y == site.Y - Height - 1 || y >= site.Y + 4;
            if (edge && WorldGen.InWorld(x, y, 2)) Main.tile[x, y].ResetToType(TileID.LihzahrdBrick);
        }
        Arenas.Walls(x0 - 1, site.Y - Height - 1, x1 + 1, site.Y + 4, WallID.LihzahrdBrickUnsafe);
        var rows = new List<int>();
        foreach (int r in PlatformRows) rows.Add(site.Y - r);
        Arenas.PlatformRows(x0 + 2, x1 - 2, rows);
        Arena = ArenaInfo.FromTiles(x0, x1, site.Y - Height, site.Y);
        foreach (int r in rows) Arena.Platforms.Add(r * 16f);
        Arena.ScanGround();
        // Altar where Golem is summoned, 40 tiles right of the pilot.
        int ax = site.X + 40;
        WorldGen.PlaceTile(ax, site.Y - 1, TileID.LihzahrdAltar, mute: true, forced: true);
        golemSpawn = new Point(ax * 16 + 8, site.Y * 16);
    }

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        Biomes.Refresh(p);
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), golemSpawn.X, golemSpawn.Y, NPCID.Golem);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    private NPC body, head, freeHead;
    private readonly RoutePlanner routes = new();
    private Vector2 carrot;

    protected override void OnAttemptStart(Player p) => routes.Best = null;

    protected override void BuildThreats(Player p)
    {
        body = FindNpc(NPCID.Golem);
        head = FindNpc(NPCID.GolemHead);
        freeHead = FindNpc(NPCID.GolemHeadFree);
        if (body == null) return;
        bool enraged = (!p.ZoneLihzhardTemple && !p.ZoneJungle) || p.Center.Y < Main.worldSurface * 16.0;
        var sim = new GolemSim(body, Arena, enraged);
        FuncThreat bodyT = Func("golem", (pc, bx) => sim.Predict(pc, bx), 6f, 0.3f);
        Planner.Boss = bodyT;

        if (head != null)
        {
            Vector2 off = head.Center - body.Center;
            float hw = head.width / 2f, hh = head.height / 2f;
            NPC hd = head;
            Func("head", (pc, bx) =>
            {
                Box[] b = bodyT.Boxes;
                for (int k = 0; k < Planner.H; k++) bx[k] = Box.Around(b[k].Center + off, hw, hh);
            }, 6f, 0.3f);
            Func("headshot", (pc, bx) => HeadFireball(hd, bodyT.Boxes, off, pc, bx), 4f, 0.15f);
        }
        // Eye lasers (22 px/tick effective) fire at random times once the head is below half
        // (and always from the detached head): reward plans that keep crossing their line of fire.
        if (head != null && head.ai[0] == 1f)
            Virtual("laser?", head.Center - new Vector2(0f, 22f * head.scale), 22f, 6f, 0.05f, 0, 8, 16);
        if (freeHead != null)
        {
            Virtual("laser?", freeHead.Center - new Vector2(0f, 50f * freeHead.scale), 22.5f, 6f, 0.05f, 0, 8, 16);
            Virtual("fire?", freeHead.Center, 8f, 10f, 0.05f, 0, 12);
        }
        foreach (NPC n in Main.npc)
        {
            if (!n.active || (n.type != NPCID.GolemFistLeft && n.type != NPCID.GolemFistRight)) continue;
            NPC fist = n;
            Func(n.type == NPCID.GolemFistLeft ? "fistL" : "fistR", (pc, bx) => FistSim.Predict(fist, body, bodyT.Boxes, sim.Damaged, Arena, pc, bx), 5f, 0.3f);
        }

        if (Ctx.Tick % 12 == 1 || routes.Best == null)
        {
            var wps = new List<Vector2>();
            for (float x = Arena.Left + 96; x <= Arena.Right - 96; x += 160)
            {
                wps.Add(new Vector2(x, Arena.Floor - 22));
                foreach (float py in Arena.Platforms) wps.Add(new Vector2(x, py - 22));
            }
            routes.BuildRoutes(p.Center, wps, Math.Max(Arena.Top + 60, Arena.Floor - 520), Arena.Floor - 22);
            routes.Choose(p.Center, sim,
                (pc, bc) => Math.Max(Math.Abs(pc.X - bc.X) - 110f, Math.Abs(pc.Y - bc.Y) - 150f),
                (pc, bc) => 0.5f * Math.Min(Math.Abs(pc.X - bc.X), 850f) - 3f * BoundsCost(pc, 120f, 60f));
        }
        carrot = routes.Carrot(p.Center, 45);
        Planner.Objective = (k, st, boss) => Objective(st, boss);
    }

    private float Objective(PilotState s, Box boss)
    {
        Vector2 c = s.Center;
        float cost = BoundsCost(c, 40f, 48f) + Vector2.Distance(c, carrot) / 24f;
        if (!boss.Valid) return cost;
        // Fists recall beyond 700 px of their shoulder: stay out of that leash when room allows.
        float dx = Math.Abs(c.X - boss.Center.X);
        float want = Arena.Right - Arena.Left > 2400f ? 760f : 420f;
        if (dx < want) cost += (want - dx) / 160f;
        // Parts off "screen" (about 1000 px) for 750 ticks despawn (NPC.CheckActive): stay closer.
        if (dx > 900f) cost += (dx - 900f) / 20f;
        return cost;
    }

    /// <summary>Head fireball (AI 46): fired at the pilot when ai[1] reaches 300; 8 px/tick from the head.</summary>
    private static void HeadFireball(NPC hd, Box[] bodyBoxes, Vector2 off, Vector2[] pc, Box[] bx)
    {
        float rate = 1f;
        if (hd.ai[0] == 1f)
        {
            rate = 1f;
            if (hd.life < hd.lifeMax * 0.4f) rate += 1f;
            if (hd.life < hd.lifeMax * 0.2f) rate += 1f;
        }
        int fire = (int)Math.Ceiling((300f - hd.ai[1]) / rate) - 1;
        Vector2 pos = Vector2.Zero, vel = Vector2.Zero;
        bool live = false;
        for (int k = 0; k < Planner.H; k++)
        {
            if (k == fire)
            {
                pos = bodyBoxes[k].Center + off + new Vector2(0f, 10f);
                vel = Vector2.Normalize(pc[k] - pos) * 8f;
                live = true;
            }
            if (live) { pos += vel; bx[k] = Box.Around(pos, 8f, 8f); }
            else bx[k] = Box.None;
        }
    }

    protected override Vector2? AimTarget(Player p, out Vector2 v)
    {
        NPC t = head ?? body;
        v = t?.velocity ?? Vector2.Zero;
        return t?.Center;
    }

    /// <summary>Kernel model of the Golem body (NPC.cs AI_045_Golem + gravity 0.3, max fall 10).</summary>
    private sealed class GolemSim : IBossSim
    {
        private readonly NPC n;
        private readonly ArenaInfo arena;
        private readonly float rate, airMax, num;
        public readonly bool Damaged;
        private Vector2 pos, vel;
        private float ai0, ai1;

        public GolemSim(NPC golem, ArenaInfo arena, bool enraged)
        {
            n = golem;
            this.arena = arena;
            num = enraged ? 2f : 1f;
            bool hd = NPC.AnyNPCs(NPCID.GolemHead), fl = NPC.AnyNPCs(NPCID.GolemFistLeft), fr = NPC.AnyNPCs(NPCID.GolemFistRight);
            float r = 1f;
            if (!fl) r += 2f;
            if (!fr) r += 2f;
            if (!hd) r += 2f;
            if (n.life < n.lifeMax) r += 1f;
            if (n.life < n.lifeMax / 2) r += 4f;
            if (n.life < n.lifeMax / 3) r += 8f;
            rate = r * num;
            Damaged = n.life < n.lifeMax;
            float m = 3f;
            if (n.life < n.lifeMax) m += 1f;
            if (n.life < n.lifeMax / 2) m += 1f;
            if (n.life < n.lifeMax / 4) m += 1f;
            airMax = m * (num + 1f) / 2f;
            Reset();
        }

        public void Reset()
        {
            pos = n.position; vel = n.velocity; ai0 = n.ai[0]; ai1 = n.ai[1];
        }

        public Vector2 Step(Vector2 pilot)
        {
            float cx = pos.X + n.width / 2f;
            int dir = pilot.X < cx ? -1 : 1;
            if (ai0 == 0f)
            {
                if (vel.Y == 0f)
                {
                    vel.X *= 0.8f;
                    ai1 += ai1 > 0f ? rate : 1f;
                    if (ai1 >= 300f) ai1 = -20f;
                    else if (ai1 == -1f)
                    {
                        vel.X = 4 * dir;
                        vel.Y = Damaged ? Math.Max(-12.1f * (num + 9f) / 10f, -19.1f) : -12.1f;
                        ai0 = 1f;
                        ai1 = 0f;
                    }
                }
            }
            else if (vel.Y == 0f) ai0 = 0f;
            else
            {
                if (pos.X < pilot.X - 10f && pos.X + n.width > pilot.X + 10f)
                {
                    vel.X *= 0.9f;
                    if (pos.Y + n.height < pilot.Y - 21f) vel.Y += 0.2f * (num + 1f) / 2f;
                }
                else
                {
                    vel.X += 0.2f * dir;
                    vel.X = Math.Clamp(vel.X, -airMax, airMax);
                }
            }
            vel.Y = Math.Min(vel.Y + 0.3f, 10f);
            float bottom = pos.Y + n.height;
            float next = bottom + vel.Y;
            float land = arena?.GroundUnder(pos.X + 8f, pos.X + n.width - 8f) ?? float.MaxValue;
            if (vel.Y > 0f && arena != null && pilot.Y - 21f <= bottom)
                foreach (float py in arena.Platforms)
                    if (bottom <= py && next >= py && py < land) land = py;
            if (vel.Y > 0f && next >= land)
            {
                pos.Y = land - n.height;
                vel.Y = 0f;
            }
            else pos.Y += vel.Y;
            pos.X += vel.X;
            if (arena != null) pos.X = Math.Clamp(pos.X, arena.Left, arena.Right - n.width);
            return pos + new Vector2(n.width / 2f, n.height / 2f);
        }

        public void Predict(Vector2[] pc, Box[] bx)
        {
            Reset();
            for (int k = 0; k < Planner.H; k++)
            {
                Vector2 c = Step(pc[k]);
                bx[k] = Box.Around(c, n.width / 2f, n.height / 2f);
            }
        }
    }

    /// <summary>Kernel model of one Golem fist (NPC.cs AI_047_GolemFist).</summary>
    private static class FistSim
    {
        public static void Predict(NPC f, NPC golem, Box[] bodyBoxes, bool bodyDamaged, ArenaInfo arena, Vector2[] pc, Box[] bx)
        {
            bool left = f.type == NPCID.GolemFistLeft;
            float sc = golem.scale;
            Vector2 c = f.Center, v = f.velocity;
            float ai0 = f.ai[0], ai1 = f.ai[1];
            float retSpeed = 14f + (f.life < f.lifeMax / 2 ? 3f : 0f) + (f.life < f.lifeMax / 4 ? 3f : 0f) + (bodyDamaged ? 8f : 0f);
            retSpeed = Math.Min(retSpeed, 32f);
            float punch = 12f + (f.life < f.lifeMax / 2 ? 4f : 0f) + (f.life < f.lifeMax / 4 ? 4f : 0f) + (bodyDamaged ? 10f : 0f);
            punch = Math.Min(punch, 48f);
            float reload = 1f + (f.life < f.lifeMax / 2 ? 1f : 0f) + (f.life < f.lifeMax / 4 ? 1f : 0f) + (bodyDamaged ? 10f : 0f);
            Vector2 bodyVel = golem.velocity;
            for (int k = 0; k < Planner.H; k++)
            {
                Vector2 bc = k == 0 ? golem.Center : bodyBoxes[k - 1].Center;
                if (k > 0) bodyVel = bodyBoxes[k - 1].Center - (k > 1 ? bodyBoxes[k - 2].Center : golem.Center);
                Vector2 anchor = bc + bodyVel + new Vector2((left ? -84f : 78f) * sc, -9f * sc);
                Vector2 toA = anchor - c;
                float dist = toA.Length();
                if (ai0 == 0f)
                {
                    if (dist < 12f + retSpeed)
                    {
                        v = toA;
                        ai1 += reload;
                        if (ai1 >= 60f)
                        {
                            bool side = left ? c.X + 100f > pc[k].X : c.X - 100f < pc[k].X;
                            ai1 = 0f;
                            if (side) ai0 = 1f;
                        }
                    }
                    else v = toA * (retSpeed / dist);
                }
                else if (ai0 == 1f)
                {
                    ai1 += 1f;
                    c = anchor;
                    v = Vector2.Zero;
                    if (ai1 >= 30f)
                    {
                        ai0 = 2f;
                        ai1 = 0f;
                        Vector2 d = pc[k] - c;
                        v = d * (punch / Math.Max(d.Length(), 1f));
                    }
                }
                else if (ai0 == 2f)
                {
                    bool passed = Math.Abs(v.X) > Math.Abs(v.Y)
                        ? (v.X > 0f ? c.X > pc[k].X : c.X < pc[k].X)
                        : (v.Y > 0f ? c.Y > pc[k].Y : c.Y < pc[k].Y);
                    bool wall = arena != null && passed &&
                        (c.X - 20f < arena.Left || c.X + 20f > arena.Right || c.Y + 15f > arena.Floor || c.Y - 15f < arena.Top);
                    if (dist > 700f || wall) { ai0 = 0f; v = Vector2.Zero; }
                }
                c += v;
                bx[k] = Box.Around(c, f.width / 2f, f.height / 2f);
            }
        }
    }
}
