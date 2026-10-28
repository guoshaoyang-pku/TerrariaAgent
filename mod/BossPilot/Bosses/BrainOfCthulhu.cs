using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Bosses.EvilDeer;
using BossPilot.Common;

namespace BossPilot.Bosses;

/// <summary>
/// Brain of Cthulhu (NPC.VanillaAI_Inner aiStyle 54 brain / 55 creepers).
///
/// Kernel facts used:
/// - Phase 1 (ai[0] >= 0): the brain is dontTakeDamage, drifts at 1 px/tick toward the
///   player and teleports (fade 51 ticks; destination tile already in ai[1], ai[2]).
///   20 creepers orbit it; each tick each has a 1/200 chance (Classic) to charge in a
///   straight line at 8 px/tick, and returns to orbit when hit or 700 px from the brain.
/// - Phase 2 (ai[0] < 0, all creepers dead): velocity = (50 v + 8 dir) / 51 toward the
///   player, teleport every 60-180 ticks to 10-12 tiles away (destination in ai[1], ai[2]
///   during the 10-tick fade of ai[0] == -2).
/// - Off the crimson the brain stops chasing and falls away (ZoneCrimson required).
/// Strategy: kite on the floor keeping ~20 tiles from the brain cluster, shoot piercing
/// arrows through creeper clusters (hits also cancel charges), then the brain; the planner
/// dodges charges and the phase-2 chase, which it predicts per candidate move.
/// </summary>
public sealed class BrainOfCthulhu : BossPlan
{
    public override string Boss => "brain_of_cthulhu";

    private readonly Planner planner = new();
    private readonly Gunner gunner = new();
    private readonly BrainHazard brain = new();
    private FightContext ctxSeen;
    private float homeX, floorY;
    private Vector2 brainCenter;
    private bool phase2;

    // Same progression point as the Eater: ore bow + Jester's Arrows (pierce the creeper ring,
    // then the brain; grenades do not carry the ~20 tiles kept from the swarm), platinum armor,
    // boots/bottle/balloon/horseshoe/aglet, Swiftness/Ironskin/Archery, ~10 crystals.
    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        // Pre-hardmode boss; the world flag is not reset between scenarios by the harness.
        Main.hardMode = false;
        Kit.Reset(p);
        switch (s.Loadout)
        {
            case "min_preboss":
                Kit.Hotbar(p, 0, ItemID.PlatinumBow);
                Kit.Ammo(p, ItemID.WoodenArrow);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.LuckyHorseshoe);
                LoadoutGiver.SetMaxStats(p, 200);
                break;
            case "stage_plus":
                Kit.Hotbar(p, 0, ItemID.MoltenFury);
                Kit.Ammo(p, ItemID.JestersArrow);
                Kit.Armor(p, ItemID.MoltenHelmet, ItemID.MoltenBreastplate, ItemID.MoltenGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.AnkletoftheWind);
                Kit.Buffs(p, s.MaxDurationSeconds, BuffID.Swiftness, BuffID.Ironskin, BuffID.Archery);
                LoadoutGiver.SetMaxStats(p, 400);
                break;
            default:
                Kit.Hotbar(p, 0, ItemID.PlatinumBow);
                Kit.Ammo(p, ItemID.JestersArrow);
                Kit.Armor(p, ItemID.PlatinumHelmet, ItemID.PlatinumChainmail, ItemID.PlatinumGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.Aglet);
                Kit.Buffs(p, s.MaxDurationSeconds, BuffID.Swiftness, BuffID.Ironskin, BuffID.Archery);
                LoadoutGiver.SetMaxStats(p, 300);
                break;
        }
        p.ForceUpdateBiomes();
    }

    public override Point ArenaSite(ScenarioConfig s) => Arena.SpawnSite();

    /// <summary>
    /// Extends the crimstone slab under the arena well past its edges: the brain gives up the
    /// chase the moment ZoneCrimson drops, which a long dodge toward an arena edge can cause.
    /// </summary>
    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        int half = s.Arena == "enclosed" ? 50 : 60;
        Arena.Fill(site.X - half - 40, site.X - half - 1, site.Y, site.Y + 4, TileID.Crimstone);
        Arena.Fill(site.X + half, site.X + half + 40, site.Y, site.Y + 4, TileID.Crimstone);
    }

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        if (ctx != ctxSeen) Begin(p, ctx);
        if (ctx.Tick % 30 == 0) p.ForceUpdateBiomes();

        HazardField f = planner.Field;
        f.Clear();
        NPC b = null;
        foreach (NPC n in Main.npc)
            if (n.active && n.type == NPCID.BrainofCthulhu) { b = n; break; }
        phase2 = b != null && b.ai[0] < 0f;
        if (b != null)
        {
            // While fading out the destination tile is already known: keep away from where it will be.
            brainCenter = b.ai[0] is 1f or -2f ? new Vector2(b.ai[1] * 16f, b.ai[2] * 16f) : b.Center;
            brain.Capture(b);
            f.Reactive.Add(brain);
        }
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.type != NPCID.Creeper) continue;
            if (n.ai[0] != 0f || b == null) f.AddLinear(new Box(n.position.X, n.position.Y, n.width, n.height), n.velocity);
            else PredictOrbit(n, b, f.NewTrack());
        }
        Threats.AddStrays(f, n => n.type is NPCID.BrainofCthulhu or NPCID.Creeper);
        Threats.AddProjectiles(f);

        planner.Plan(p, Objective);
        Shoot(p, b);
        if (ctx.Tick % 120 == 0) TraceState(p, ctx, b);
        return true;
    }

    private void Begin(Player p, FightContext ctx)
    {
        ctxSeen = ctx;
        planner.Grid = ArenaGrid.Capture(ctx.Site, 125, 90, 30);
        homeX = ctx.Site.X * 16 + 8;
        floorY = ctx.Site.Y * 16;
        gunner.Configure(p);
        gunner.Shots = 0;
        HitStats.Hits = 0;
        Trace.Line($"[boc] {ctx.Scenario.Name} start crimson={p.ZoneCrimson} blood={Main.SceneMetrics.BloodTileCount} speed={gunner.Speed:F1}");
    }

    private void TraceState(Player p, FightContext ctx, NPC b)
    {
        int creepers = 0;
        foreach (NPC n in Main.npc) if (n.active && n.type == NPCID.Creeper) creepers++;
        Trace.Line($"[boc] t={ctx.Tick} brain={(b == null ? -1 : b.life)} ai0={(b == null ? 0 : b.ai[0])} creepers={creepers} shots={gunner.Shots} hits={HitStats.Hits} " +
                   $"p=({p.Center.X / 16f:F0},{p.Center.Y / 16f:F0}) d={(b == null ? 0 : Vector2.Distance(b.Center, p.Center) / 16f):F0} hit={planner.PredictedHit} crimson={p.ZoneCrimson}");
    }

    /// <summary>AI 55 orbit: chase the brain at 8 when farther than 90 px, else speed up 5%/tick to 8.</summary>
    private static void PredictOrbit(NPC n, NPC b, Track tr)
    {
        Vector2 c = n.Center, v = n.velocity, bc = b.Center, bv = b.velocity;
        for (int t = 1; t <= HazardField.Horizon; t++)
        {
            bc += bv;
            Vector2 d = bc - c;
            float len = d.Length();
            if (len > 90f) v = (v * 15f + d * (8f / len)) / 16f;
            else if (Math.Abs(v.X) + Math.Abs(v.Y) < 8f) v *= 1.05f;
            c += v;
            tr.Set(t, Box.Centered(c, n.width, n.height));
        }
    }

    private float Objective(in PilotState s, int t)
    {
        Vector2 me = s.Pos + new Vector2(10f, 21f);
        float c = 0f;
        float d = Vector2.Distance(me, brainCenter);
        const float want = 360f;
        if (d < want) c += 0.012f * (want - d) * (want - d);
        else if (d > 560f) c += 0.002f * (d - 560f) * (d - 560f);
        float dx = Math.Abs(me.X - homeX);
        if (dx > 36 * 16) c += 300f + 0.2f * (dx - 36 * 16) * (dx - 36 * 16);
        // Prefer the floor and the first platform row: room to jump either way.
        float rise = floorY - (s.Pos.Y + 42f);
        if (rise > 13 * 16) c += 0.01f * (rise - 13 * 16) * (rise - 13 * 16);
        return c;
    }

    private void Shoot(Player p, NPC b)
    {
        Vector2 c = p.Center;
        if (b == null) { gunner.Fire(p, c, false); return; }
        if (phase2)
        {
            Vector2 aim = gunner.Lead(c, b.Center, b.velocity);
            gunner.Fire(p, aim, true);
            return;
        }
        // Phase 1: charging creepers first, else the line through the most creepers.
        NPC best = null;
        float bestScore = float.MinValue;
        Vector2 bestAim = c;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.type != NPCID.Creeper) continue;
            float dist = Vector2.Distance(n.Center, c);
            if (dist > 800f) continue;
            Vector2 aim = gunner.Lead(c, n.Center, n.velocity);
            Vector2 dir = aim - c;
            if (dir.LengthSquared() < 1f) continue;
            dir.Normalize();
            float score = 0f;
            foreach (NPC k in Main.npc)
            {
                if (!k.active || k.type != NPCID.Creeper) continue;
                Vector2 r = k.Center - c;
                float along = Vector2.Dot(r, dir);
                if (along > 0f && along < 900f && Math.Abs(r.X * dir.Y - r.Y * dir.X) < 18f) score += 1f;
            }
            if (n.ai[0] == 1f && Vector2.Dot(n.velocity, c - n.Center) > 0f) score += 50f - dist / 20f;
            score -= dist / 400f;
            if (score > bestScore) { bestScore = score; best = n; bestAim = aim; }
        }
        gunner.Fire(p, bestAim, best != null);
    }

    /// <summary>The brain, simulated against each candidate pilot path (it homes on it).</summary>
    private sealed class BrainHazard : IReactiveHazard
    {
        private Vector2 c0, v0;
        private float ai0, ai1, ai2, ai3, alpha0;
        private int w, h;
        private Vector2 c, v;
        private float a0, a3, alpha;

        public void Capture(NPC n)
        {
            c0 = n.Center; v0 = n.velocity;
            ai0 = n.ai[0]; ai1 = n.ai[1]; ai2 = n.ai[2]; ai3 = n.ai[3];
            alpha0 = n.alpha;
            w = n.width; h = n.height;
        }

        public void Begin()
        {
            c = c0; v = v0; a0 = ai0; a3 = ai3; alpha = alpha0;
        }

        public bool Step(int t, Vector2 me, out Box box)
        {
            Vector2 d = me - c;
            float len = Math.Max(d.Length(), 0.001f);
            if (a0 < 0f)
            {
                v = (v * 50f + d * (8f / len)) / 51f;
                if (a0 == -2f)
                {
                    v *= 0.9f;
                    a3 += 25f;
                    if (a3 >= 255f) { a3 = 255f; c = new Vector2(ai1 * 16f, ai2 * 16f); a0 = -3f; }
                }
                else if (a0 == -3f)
                {
                    a3 -= 25f;
                    if (a3 <= 0f) { a3 = 0f; a0 = -1f; }
                }
            }
            else
            {
                v = len < 1f ? d : d / len;
                if (a0 == 1f)
                {
                    alpha += 5f;
                    if (alpha >= 255f) { alpha = 255f; c = new Vector2(ai1 * 16f, ai2 * 16f); a0 = 2f; }
                }
            }
            c += v;
            box = Box.Centered(c, w, h);
            return true;
        }
    }
}
