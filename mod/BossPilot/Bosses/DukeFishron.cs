using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.JungleTempleOcean;

namespace BossPilot.Bosses;

/// <summary>
/// Duke Fishron (Classic, 60000 HP, defense 50). The kernel enrages him (AI_069 flag6: hover 10
/// ticks, +6 dash speed, double damage/defense) unless the pilot is above the surface line and
/// within 6400 px (400 tiles) of a world edge, so every arena is on the left ocean: open_sky and
/// flat_platforms are slabs over the water, natural is the untouched beach. Duke is spawned
/// over the water as if fished up.
/// Control: MPC with an exact replay of AI_069's state machine (hover toward pilot +/-300,-200 at
/// 7.5-8 px/tick, accel 0.45-0.5; dash 16 px/tick for 30 ticks aimed at the pilot when the hover
/// timer hits 30/20; bubble and tornado pauses; phase 2 at 50%), homing bubbles (aiStyle 70:
/// ~11 px/tick pursuit, 116 px trigger box) and sharkron dives; bubbles that close in are shot.
/// </summary>
public sealed class DukeFishron : PilotPlan
{
    public override string Boss => "duke_fishron";

    private static readonly HashSet<int> Npcs = [NPCID.DukeFishron, NPCID.DetonatingBubble, NPCID.Sharkron, NPCID.Sharkron2];
    protected override ISet<int> ModelledNpcs => Npcs;
    private static readonly HashSet<int> Projs = [ProjectileID.Sharknado, ProjectileID.Cthulunado];
    protected override ISet<int> ModelledProjectiles => Projs;

    // Loadouts. Duke comes after Golem: Venus Magnum with Ichor bullets (defense -15 against his
    // 50; no homing, so shots can also pop bubbles), Shroomite armor, Destroyer Emblem from
    // Golem and Beetle Wings from Golem's beetle husks. stage_plus adds post-Duke/event gear
    // (Chain Gun, Steampunk Wings); min_preboss is the pre-Golem Megashark kit.
    private static Kit KitFor(string tier) => tier switch
    {
        "min_preboss" => new Kit(ItemID.Megashark, ItemID.IchorBullet,
            [ItemID.HallowedHelmet, ItemID.HallowedPlateMail, ItemID.HallowedGreaves],
            [ItemID.FrostsparkBoots, ItemID.LeafWings, ItemID.RangerEmblem, ItemID.AvengerEmblem],
            [BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration], 500),
        "stage_plus" => new Kit(ItemID.ChainGun, ItemID.IchorBullet,
            [ItemID.ShroomiteMask, ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings],
            [ItemID.FrostsparkBoots, ItemID.SteampunkWings, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.AvengerEmblem],
            Kits.StandardBuffs, 600),
        _ => new Kit(ItemID.VenusMagnum, ItemID.IchorBullet,
            [ItemID.ShroomiteMask, ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings],
            [ItemID.FrostsparkBoots, ItemID.BeetleWings, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.AvengerEmblem],
            Kits.StandardBuffs, 600),
    };

    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        // Golem's flag stays off: after Golem, Martian probes roam the outer thirds of the world
        // (the ocean) and can start an invasion mid-trial; it does not change Duke's AI.
        Kits.Hardmode(plantDown: true, golemDown: false);
        Kits.Apply(p, KitFor(s.Loadout), s);
    }

    private static (int, int)? shore;
    private static int shoreWorld = -1;

    /// <summary>Shoreline of the left ocean (cached before any arena is built over the water).</summary>
    private static (int waterLevel, int beachStart) LeftShore()
    {
        if (shoreWorld != Main.worldID || shore == null)
        {
            shoreWorld = Main.worldID;
            shore = ScanShore();
        }
        return shore.Value;
    }

    /// <summary>First dry column after the water, scanning from the left world edge.</summary>
    private static (int waterLevel, int beachStart) ScanShore()
    {
        int level = -1;
        for (int x = 20; x < 380; x++)
        {
            // Platforms (a skybridge left by another scenario) count as air.
            int y = 60;
            while (y < Main.worldSurface && !Arenas.Solid(x, y) && Main.tile[x, y].LiquidAmount == 0) y++;
            if (Main.tile[x, y].LiquidAmount > 0) { level = y; continue; }
            if (level > 0) return (level, x);
        }
        return (level > 0 ? level : 206, 250);
    }

    public override Point ArenaSite(ScenarioConfig s)
    {
        (int level, int beach) = LeftShore();
        if (s.Arena == "natural")
        {
            int x = beach + 45;
            return new Point(x, Arenas.Surface(x));
        }
        // A slab 10 tiles above the waves, centered over the open water.
        return new Point(Math.Max(110, beach - 110), level - 10);
    }

    private float waterTop, groundL, groundR;

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        waterTop = LeftShore().waterLevel * 16f;
        if (s.Arena == "natural")
        {
            // Untouched shore: take down a skybridge another scenario may have left over the sea.
            int bridgeY = LeftShore().waterLevel - 10;
            for (int x = 50; x <= 388; x++)
                if (Main.tile[x, bridgeY].HasTile && Main.tile[x, bridgeY].TileType == TileID.Platforms)
                    WorldGen.KillTile(x, bridgeY, fail: false, effectOnly: false, noItem: true);
            // The flat dry beach around the start point (the sloping waterline, dunes and sand pits
            // beyond it are left out).
            (_, int beach) = LeftShore();
            int left = site.X, right = site.X;
            while (left > beach && Math.Abs(Arenas.Surface(left - 1) - site.Y) <= 4) left--;
            while (right < 392 && Math.Abs(Arenas.Surface(right + 1) - site.Y) <= 4) right++;
            Arena = ArenaInfo.FromTiles(left, right, site.Y - 70, site.Y);
            PilotLog.Line($"beach arena x {left}..{right} floor {site.Y}");
            groundL = Arena.Left; groundR = Arena.Right;
            standOff = 360f; fleeDiv = 200f;
            return;
        }
        // A skybridge over the whole Duke-legal stretch of sea (x below 400 tiles), level with the slab:
        // the usual Duke arena, long enough to kite dashes and outrun bubble streams.
        const int bridgeL = 50, bridgeR = 388;
        for (int x = bridgeL; x <= bridgeR; x++)
            if (!Main.tile[x, site.Y].HasTile)
            {
                WorldGen.PlaceTile(x, site.Y, TileID.Platforms, mute: true, forced: true, style: 0);
                Main.tile[x, site.Y].LiquidAmount = 0;
            }
        Arena = ArenaInfo.FromTiles(bridgeL, bridgeR, site.Y - 70, site.Y);
        groundL = Arena.Left; groundR = Arena.Right;
        standOff = 440f; fleeDiv = 110f;
        if (s.Arena == "flat_platforms")
            foreach (int r in new[] { 12, 24, 36 }) Arena.Platforms.Add((site.Y - r) * 16f);
    }

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        // Fished up from the water on the ocean side (left), 30 tiles out.
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), (int)p.Center.X - 30 * 16, (int)p.Center.Y - 6 * 16, NPCID.DukeFishron);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    private NPC duke;
    private NPC aimBubble;

    protected override void BuildThreats(Player p)
    {
        duke = FindNpc(NPCID.DukeFishron);
        if (duke == null) return;
        var sim = new DukeSim(duke);
        FuncThreat body = Func("duke", (pc, bx) => sim.Predict(pc, bx), 10f, 0.5f);
        Planner.Boss = body;

        aimBubble = null;
        float soonest = 70f;
        bool bubbles = false;
        foreach (NPC n in Main.npc)
        {
            if (!n.active) continue;
            if (n.type == NPCID.DetonatingBubble)
            {
                NPC b = n;
                bubbles = true;
                Func("bubble", (pc, bx) => PredictBubble(b, pc, bx), 3f, 0.3f);
                // Shoot the bubble that would arrive first (distance over closing speed).
                Vector2 rel = p.Center - n.Center;
                float d = rel.Length();
                float closing = Math.Max(2f, Vector2.Dot(n.velocity - p.velocity, rel / Math.Max(d, 1f)));
                float eta = (d - 70f) / closing;
                if (n.ai[0] == 0f && d < 700f && eta < soonest) { soonest = eta; aimBubble = n; }
            }
            else if (n.type is NPCID.Sharkron or NPCID.Sharkron2)
            {
                NPC sh = n;
                Func("sharkron", (pc, bx) => PredictSharkron(sh, pc, bx), 6f, 0.4f);
            }
        }
        // Tornado segments (aiStyle 64) sway sideways by up to width/5 (x2 for Cthulunado) and the
        // column keeps growing upward: widen each segment and stretch the top one.
        tornadoL = float.MaxValue; tornadoR = float.MinValue;
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || (pr.type != ProjectileID.Sharknado && pr.type != ProjectileID.Cthulunado)) continue;
            float sway = pr.width / 5f * (pr.type == ProjectileID.Cthulunado ? 2f : 1f) * 0.6f;
            Box b = Box.Of(pr);
            b.X0 -= sway; b.X1 += sway;
            if (pr.ai[1] > 0f) b.Y0 -= 2f * pr.height;
            Static("tornado", b, 4f);
            tornadoL = Math.Min(tornadoL, b.X0); tornadoR = Math.Max(tornadoR, b.X1);
        }
        // The sea is as bad as a hit: swimming halves speed and there is no way back up from under the slab.
        Planner.AllowDown = false;
        Static("sea", new Box(0f, waterTop + 6f, 400f * 16f, waterTop + 2000f), 0f).Weight = 0.6f;
        float a0 = duke.ai[0], a3 = duke.ai[3];
        flee = bubbles || a0 == 2f || a0 == 7f || (a0 == 0f && a3 == 10f) || (a0 == 5f && a3 == 6f);

        // Bubbles Duke will blow during the rest of a bubble pause or circle (one every 4 ticks).
        if (duke.ai[0] == 2f || duke.ai[0] == 7f)
            Func("bubble+", (pc, bx) => FutureBubbles(body.Boxes, pc, bx), 3f, 0.4f);

        Planner.Objective = (k, st, boss) => Objective(st, boss);
    }

    private bool flee;
    // A narrow beach leaves less room to back off than the long skybridge.
    private float standOff = 440f, fleeDiv = 110f;
    private float tornadoL, tornadoR;

    private float Objective(PilotState s, Box boss)
    {
        Vector2 c = s.Center;
        float cost = BoundsCost(c, 240f, 120f) + WingReserveCost(s);
        // Stay over solid ground: beyond the slab/beach there is only sea below.
        float off = Math.Max(groundL + 48f - c.X, c.X - (groundR - 48f));
        if (off > 0f) cost += 4f + (off / 16f) * (off / 16f);
        // Past 6400 px from the world edge Duke enrages (AI_069 flag6).
        if (c.X > 393f * 16f) cost += 40f + (c.X - 393f * 16f) / 4f;
        // Keep clear of tornado columns (they grow upward and spawn sharkrons).
        if (tornadoL < tornadoR && c.X > tornadoL - 80f && c.X < tornadoR + 80f) cost += 6f;
        // Never end up in the sea: swimming halves speed (and the planner does not model water).
        if (c.Y + 21f > waterTop - 8f) cost += 30f + (c.Y + 21f - waterTop) / 8f;
        // Keep the middle of the arena: room to run either way when bubbles come.
        float mid = (groundL + groundR) / 2f, halfSpan = (groundR - groundL) / 2f;
        float dm = (c.X - mid) / halfSpan;
        cost += 8f * dm * dm;
        // Mostly on the ground: wings recharge there and stay ready for the next dash.
        float alt = Arena != null ? Arena.Floor - (c.Y + 21f) : 0f;
        cost += Math.Max(0f, alt) / 160f;
        if (boss.Valid)
        {
            float d = Vector2.Distance(c, boss.Center);
            // Stand-off: a dash from far away takes longer to arrive, leaving time to side-step its band.
            if (d < standOff) cost += ((standOff - d) / 70f) * ((standOff - d) / 70f);
            // Bubble pauses: 20 bubbles homing at ~11 px/tick; a big head start plus shooting them is the counter.
            if (flee) cost += (1100f - Math.Min(d, 1100f)) / fleeDiv;

        }
        return cost;
    }

    /// <summary>Bubble pursuit (NPC.cs aiStyle 70): v = (40v + 20u)/41, then x/y damped by 50/51; trigger box 116 px.</summary>
    private static void PredictBubble(NPC n, Vector2[] pc, Box[] bx)
    {
        Vector2 c = n.Center, v = n.velocity;
        bool armed = n.ai[0] == 0f;
        float t = n.ai[1];
        for (int k = 0; k < Planner.H; k++)
        {
            Vector2 u = pc[k] - c;
            float len = u.Length();
            if (len > 1f) u /= len;
            v = (v * 40f + u * 20f) / 41f;
            v.X = v.X * 50f / 51f;
            v.Y = (v.Y * 50f - 0.25f) / 51f;
            if (v.Y > 0f) v.Y -= 0.04f;
            c += v;
            if (armed)
            {
                t++;
                if (t >= 150f) { armed = false; t = 4f; bx[k] = Box.Around(c, 50f, 50f); continue; }
                // Trigger rect: own 36x36 box grown 40 px each side, shifted up-left by half its size.
                bx[k] = new Box(c.X - 18f - 58f, c.Y - 18f - 58f, c.X - 18f + 58f, c.Y - 18f + 58f);
            }
            else
            {
                t--;
                bx[k] = t < 0f ? Box.None : Box.Around(c, 50f, 50f);
            }
        }
    }

    /// <summary>Bubbles still to be spawned this pause: every 4th tick at Duke + 85 px toward the pilot.</summary>
    private void FutureBubbles(Box[] dukeBoxes, Vector2[] pc, Box[] bx)
    {
        Span<Vector2> pos = stackalloc Vector2[12], vel = stackalloc Vector2[12];
        int count = 0;
        float ai2 = duke.ai[2];
        bool circle = duke.ai[0] == 7f;
        int dir = duke.direction;
        for (int k = 0; k < Planner.H; k++)
        {
            float tick = ai2 + k + 1;
            if (tick < (circle ? 120f : 80f) && (int)tick % 4 == 0 && count < 12)
            {
                Vector2 dc = dukeBoxes[k].Center;
                if (circle)
                {
                    // Circle dash (AI_069 state 7): spawned ahead of Duke, launched sideways at 6 px/tick.
                    Vector2 heading = Vector2.Normalize(dukeBoxes[k].Center - (k > 0 ? dukeBoxes[k - 1].Center : duke.Center));
                    pos[count] = dc + heading * 85f + new Vector2(0f, 45f);
                    vel[count] = heading.RotatedBy(MathHelper.PiOver2 * dir) * 6f;
                }
                else
                {
                    Vector2 d = Vector2.Normalize(pc[k] - dc);
                    pos[count] = dc + d * 85f + new Vector2(0f, 45f);
                    vel[count] = Vector2.Normalize(pc[k] - pos[count]) * 14.5f;
                }
                count++;
            }
            Box nearest = Box.None;
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Vector2 u = pc[k] - pos[i];
                float len = u.Length();
                if (len > 1f) u /= len;
                vel[i] = (vel[i] * 40f + u * 20f) / 41f;
                vel[i].X *= 50f / 51f;
                vel[i].Y = (vel[i].Y * 50f - 0.25f) / 51f;
                pos[i] += vel[i];
                if (len < best) { best = len; nearest = new Box(pos[i].X - 76f, pos[i].Y - 76f, pos[i].X + 40f, pos[i].Y + 40f); }
            }
            bx[k] = nearest;
        }
    }

    /// <summary>Sharkron (aiStyle 71): drifts vertically for 90 ticks, then dives at the pilot at 16 px/tick.</summary>
    private static void PredictSharkron(NPC n, Vector2[] pc, Box[] bx)
    {
        Vector2 c = n.Center, v = n.velocity;
        float ai0 = n.ai[0], ai1 = n.ai[1];
        float hw = n.width / 2f, hh = n.height / 2f;
        for (int k = 0; k < Planner.H; k++)
        {
            if (ai0 == 0f)
            {
                ai1++;
                v = new Vector2(0f, n.ai[3]);
                if (n.type == NPCID.Sharkron2)
                {
                    // Cthulunado sharkrons sway: x offset -dir * (cos(pi/30 t) - 0.5) * ai[2].
                    float w = MathF.PI / 30f, t0 = n.localAI[1] + k;
                    v.X = -n.direction * (MathF.Cos(w * (t0 + 1)) - MathF.Cos(w * t0)) * n.ai[2];
                }
                if (ai1 >= 90f)
                {
                    ai0 = 1f;
                    v = Vector2.Normalize(pc[k] - c) * 16f;
                }
            }
            c += v;
            bx[k] = Box.Around(c, hw, hh);
        }
    }

    protected override Vector2? AimTarget(Player p, out Vector2 v)
    {
        NPC t = aimBubble ?? duke;
        v = t?.velocity ?? Vector2.Zero;
        return t?.Center;
    }

    /// <summary>Replay of NPC.AI_069_DukeFishron (Classic, not enraged) for prediction.</summary>
    private sealed class DukeSim
    {
        private readonly NPC n;
        private Vector2 c, v;
        private float ai0, ai1, ai2, ai3;
        private int dir;

        public DukeSim(NPC duke) { n = duke; }

        private void Reset()
        {
            c = n.Center; v = n.velocity;
            ai0 = n.ai[0]; ai1 = n.ai[1]; ai2 = n.ai[2]; ai3 = n.ai[3];
            dir = n.direction;
        }

        public void Predict(Vector2[] pc, Box[] bx)
        {
            Reset();
            float hw = n.width / 2f, hh = n.height / 2f;
            for (int k = 0; k < Planner.H; k++)
            {
                Step(pc[k]);
                bx[k] = Box.Around(c, hw, hh);
            }
        }

        private void Step(Vector2 player)
        {
            bool half = n.life <= n.lifeMax * 0.5f;
            bool p2 = ai0 > 4f, fresh = ai3 < 10f;
            int hover = 60;
            float accel = 0.45f, speed = 7.5f;
            if (p2 && fresh) { accel = 0.5f; speed = 8f; hover = 20; }
            else if (fresh && !p2) hover = 30;
            const int dashTicks = 30;
            const float dashSpeed = 16f;
            switch (ai0)
            {
                case -1f:
                    v *= 0.98f;
                    if (ai2 > 20f) v.Y = -2f;
                    if (++ai2 >= 75f) { ai0 = 0f; ai1 = 0f; ai2 = 0f; }
                    break;
                case 0f:
                case 5f:
                {
                    Hover(player, speed, accel, 300f);
                    if (++ai2 >= hover)
                    {
                        int pick;
                        if (ai0 == 0f)
                        {
                            pick = ai3 < 10f ? 1 : ai3 == 10f ? 2 : 3;
                            if (ai3 == 10f) ai3 = 1f;
                            else if (ai3 == 11f) ai3 = 0f;
                            if (half) pick = 4;
                        }
                        else
                        {
                            pick = ai3 < 6f ? 1 : ai3 == 6f ? 2 : 3;
                            if (ai3 == 6f) ai3 = 1f;
                            else if (ai3 == 7f) ai3 = 0f;
                        }
                        bool phase2 = ai0 == 5f;
                        ai1 = 0f; ai2 = 0f;
                        dir = Math.Sign(player.X - c.X);
                        switch (pick)
                        {
                            case 1:
                                ai0 = phase2 ? 6f : 1f;
                                v = Vector2.Normalize(player - c) * dashSpeed;
                                break;
                            case 2:
                                if (phase2) { v = Vector2.Normalize(player - c) * 20f; ai0 = 7f; }
                                else ai0 = 2f;
                                break;
                            case 3: ai0 = phase2 ? 8f : 3f; break;
                            case 4: ai0 = 4f; break;
                        }
                    }
                    break;
                }
                case 1f:
                case 6f:
                    if (++ai2 >= dashTicks) { ai0 = ai0 == 1f ? 0f : 5f; ai1 = 0f; ai2 = 0f; ai3 += 2f; }
                    break;
                case 2f:
                    Hover(player, 5f, 0.3f, 300f);
                    if (++ai2 >= 80f) { ai0 = 0f; ai1 = 0f; ai2 = 0f; }
                    break;
                case 3f:
                case 8f:
                    v *= 0.98f;
                    v.Y = MathHelper.Lerp(v.Y, 0f, 0.02f);
                    if (++ai2 >= 90f) { ai0 = ai0 == 3f ? 0f : 5f; ai1 = 0f; ai2 = 0f; }
                    break;
                case 4f:
                    v *= 0.98f;
                    v.Y = MathHelper.Lerp(v.Y, 0f, 0.02f);
                    if (++ai2 >= 180f) { ai0 = 5f; ai1 = 0f; ai2 = 0f; ai3 = 0f; }
                    break;
                case 7f:
                    v = v.RotatedBy(-MathHelper.TwoPi / 60f * dir);
                    if (++ai2 >= 120f) { ai0 = 5f; ai1 = 0f; ai2 = 0f; }
                    break;
            }
            c += v;
        }

        private void Hover(Vector2 player, float speed, float accel, float side)
        {
            if (ai1 == 0f) ai1 = side * Math.Sign(c.X - player.X);
            Vector2 want = player + new Vector2(ai1, -200f) - c - v;
            float len = want.Length();
            want = len > 0.001f ? want * (speed / len) : Vector2.Zero;
            v.X = Approach(v.X, want.X, accel);
            v.Y = Approach(v.Y, want.Y, accel);
        }

        private static float Approach(float v, float want, float a)
        {
            if (v < want) { v += a; if (v < 0f && want > 0f) v += a; }
            else if (v > want) { v -= a; if (v > 0f && want < 0f) v -= a; }
            return v;
        }
    }
}
