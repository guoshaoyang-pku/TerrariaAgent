using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Bosses.EvilDeer;
using BossPilot.Common;

namespace BossPilot.Bosses;

/// <summary>
/// Eater of Worlds (Classic: 65 segments x 150 HP, contact damage only; the
/// vile-spit spawns in AI_006 are expert-only).
///
/// Kernel facts used:
/// - A worm head steers only while overlapping tiles (platforms count) or when
///   farther than 1000 px from the player box; in open air it is ballistic with
///   gravity 0.11 and speed cap 10, so from the floor it peaks ~30 tiles up.
/// - The head dives and despawns when the target is in neither the corruption
///   nor the crimson (ZoneCorrupt is required for the head to stay).
/// Arena: the standard floor with its platform rows replaced by one sky bridge
/// 40 tiles up (lower rows would let the head re-steer mid-air), terrain beside
/// the arena levelled to the floor. The pilot stands on the bridge, out of
/// ballistic reach, lobs grenades onto the densest part of each arc (a point on
/// the chain stays occupied while the body flows through it) and fills the gaps
/// with the Minishark; the planner only has to dodge heads that come in from far.
/// </summary>
public sealed class EaterOfWorlds : BossPlan
{
    public override string Boss => "eater_of_worlds";

    private const int BridgeRise = 40;
    private const int BridgeHalf = 48;

    private readonly Planner planner = new();
    private readonly Gunner gunner = new();
    private FightContext ctxSeen;
    private float homeX, bridgeFeet;
    private readonly List<NPC> exposed = [];
    private int bowSlot, grenadeSlot;
    private float attendX;

    // stage_default (after the Eye, before the evil boss): Grenades from the Demolitionist (a
    // 128 px blast takes several segments at once) and the Arms Dealer's Minishark with Meteor
    // Shot (orb-smash meteorite; pierces two segments) for the short windows when only a few
    // segments surface; platinum armor, the sky-island/surface mobility kit,
    // Swiftness/Ironskin/Archery, ~10 crystals. min_preboss: the bow with wooden arrows, boots
    // only, 200 HP. stage_plus: hellstone-era Molten Fury + Jester's and grenades, molten armor.
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
                Kit.Hotbar(p, 0, ItemID.Grenade, 999);
                Kit.Hotbar(p, 1, ItemID.MoltenFury);
                Kit.Ammo(p, ItemID.JestersArrow);
                Kit.Armor(p, ItemID.MoltenHelmet, ItemID.MoltenBreastplate, ItemID.MoltenGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.AnkletoftheWind);
                Kit.Buffs(p, s.MaxDurationSeconds, BuffID.Swiftness, BuffID.Ironskin, BuffID.Archery);
                LoadoutGiver.SetMaxStats(p, 400);
                break;
            default:
                Kit.Hotbar(p, 0, ItemID.Grenade, 999);
                Kit.Hotbar(p, 1, ItemID.Minishark);
                Kit.Ammo(p, ItemID.MeteorShot);
                Kit.Armor(p, ItemID.PlatinumHelmet, ItemID.PlatinumChainmail, ItemID.PlatinumGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.Aglet);
                Kit.Buffs(p, s.MaxDurationSeconds, BuffID.Swiftness, BuffID.Ironskin, BuffID.Archery);
                LoadoutGiver.SetMaxStats(p, 300);
                break;
        }
        // TrialRunner has just placed the pilot on the arena floor: start on the bridge instead.
        p.position.Y -= BridgeRise * 16;
        p.velocity = Vector2.Zero;
        p.fallStart = (int)(p.position.Y / 16f);
        // Headless servers never draw, and only drawing refreshes SceneMetrics; rescan here.
        p.ForceUpdateBiomes();
    }

    public override Point ArenaSite(ScenarioConfig s) => Arena.SpawnSite();

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        int halfArena = s.Arena == "enclosed" ? 50 : 60;
        // Default platform rows (12/24/36 up) would give the worm mid-air steering.
        for (int x = site.X - 61; x <= site.X + 61; x++)
            foreach (int rise in (int[])[12, 24, 36])
                Arena.ClearTile(x, site.Y - rise);
        // Level hills next to the arena so heads can only surface at floor height.
        Arena.ClearBox(site.X - 120, site.X - halfArena - 1, site.Y - 70, site.Y - 1);
        Arena.ClearBox(site.X + halfArena + 1, site.X + 120, site.Y - 70, site.Y - 1);
        Arena.ClearBox(site.X - BridgeHalf, site.X + BridgeHalf, site.Y - BridgeRise - 6, site.Y - BridgeRise - 1);
        Arena.PlatformRow(site.X - BridgeHalf, site.X + BridgeHalf, site.Y - BridgeRise);
    }

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        // Worm Food summons the head off-screen; bring it up from under the floor, off to one side.
        int floorY = (int)(p.position.Y + p.height) + BridgeRise * 16;
        int x = (int)p.Center.X + 30 * 16, y = floorY + 30 * 16;
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), x, y, NPCID.EaterofWorldsHead);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        if (ctx != ctxSeen) Begin(p, ctx);
        if (ctx.Tick % 30 == 0) p.ForceUpdateBiomes();

        HazardField f = planner.Field;
        f.Clear();
        exposed.Clear();
        foreach (NPC n in Main.npc)
        {
            if (!n.active) continue;
            if (n.type is NPCID.EaterofWorldsHead or NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail)
            {
                Box b = new(n.position.X, n.position.Y, n.width, n.height);
                f.Statics.Add(b);
                if (n.type == NPCID.EaterofWorldsHead && Math.Abs(n.Center.X - p.Center.X) < 1400 && Math.Abs(n.Center.Y - p.Center.Y) < 1400)
                    WormPredictor.Predict(n, p, planner.Grid, f.NewTrack(), HazardField.Horizon);
                if (!planner.Grid.WormInTiles(n.position.X, n.position.Y, n.width, n.height)) exposed.Add(n);
            }
            else if (!n.friendly && n.damage > 0 && !n.townNPC)
                f.AddLinear(new Box(n.position.X, n.position.Y, n.width, n.height), n.velocity);
        }
        Threats.AddProjectiles(f);
        Grenades.AddOwnBlasts(f, p);

        // Late in the fight few short worms surface far apart: drift along the bridge toward them.
        if (exposed.Count > 0)
        {
            float sum = 0f;
            foreach (NPC n in exposed) sum += n.Center.X;
            float want = homeX + MathHelper.Clamp((sum / exposed.Count - homeX) * 0.7f, -34 * 16, 34 * 16);
            attendX = MathHelper.Lerp(attendX, want, 0.02f);
        }
        planner.Plan(p, Objective);
        Shoot(p);
        if (ctx.Tick % 120 == 0) TraceState(p, ctx);
        return true;
    }

    private int lastCount;

    private void TraceState(Player p, FightContext ctx)
    {
        long life = 0;
        int segs = 0, heads = 0;
        foreach (NPC n in Main.npc)
            if (n.active && n.type is NPCID.EaterofWorldsHead or NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail)
            {
                segs++;
                life += n.life;
                if (n.type == NPCID.EaterofWorldsHead) heads++;
            }
        float near = float.MaxValue;
        foreach (NPC n in exposed) near = Math.Min(near, Vector2.Distance(n.Center, p.Center));
        string hs = "";
        foreach (NPC n in Main.npc)
            if (n.active && n.type == NPCID.EaterofWorldsHead)
                hs += $" ({(n.Center.X - p.Center.X) / 16f:F0},{(n.Center.Y - p.Center.Y) / 16f:F0})";
        Trace.Line($"[eow] t={ctx.Tick} life={life} segs={segs} exposed={exposed.Count} near={near / 16f:F0} pierce={lastCount} shots={gunner.Shots} hits={HitStats.Hits} p=({p.Center.X / 16f:F0},{p.Center.Y / 16f:F0}) hit={planner.PredictedHit} heads{hs}");
    }

    private void Begin(Player p, FightContext ctx)
    {
        ctxSeen = ctx;
        planner.Grid = ArenaGrid.Capture(ctx.Site, 125, 90, 40);
        homeX = attendX = ctx.Site.X * 16 + 8;
        bridgeFeet = (ctx.Site.Y - BridgeRise) * 16;
        bowSlot = grenadeSlot = -1;
        for (int i = 0; i < 10; i++)
        {
            if (p.inventory[i].type == ItemID.Grenade) grenadeSlot = i;
            else if (p.inventory[i].useAmmo == AmmoID.Arrow || p.inventory[i].useAmmo == AmmoID.Bullet) bowSlot = i;
        }
        p.selectedItem = Math.Max(bowSlot, 0);
        gunner.Configure(p);
        gunner.Shots = 0;
        HitStats.Hits = 0;
        Trace.Line($"[eow] {ctx.Scenario.Name} start corrupt={p.ZoneCorrupt} evil={Main.SceneMetrics.EvilTileCount} pos={p.position / 16f} speed={gunner.Speed:F1}");
    }

    private float Objective(in PilotState s, int t)
    {
        float dx = s.Pos.X + 10f - attendX;
        float c = 0.0015f * dx * dx;
        if (s.Pos.Y + 42f > bridgeFeet + 8f) c += 3000f;
        return c;
    }

    /// <summary>
    /// Throws a grenade where the most exposed segments will be inside its blast, else fires the
    /// bow along the line that pierces the most exposed segments.
    /// </summary>
    private void Shoot(Player p)
    {
        Vector2 c = p.Center;
        bool ready = p.itemAnimation == 0 && p.itemTime == 0;
        if (ready && grenadeSlot >= 0 && TryGrenade(p, out Vector2 throwAt))
        {
            p.selectedItem = grenadeSlot;
            gunner.Fire(p, throwAt, true);
            return;
        }
        if (ready && bowSlot >= 0) p.selectedItem = bowSlot;
        if (p.HeldItem.type == ItemID.Grenade) { gunner.Fire(p, c, false); return; }
        NPC bestT = null;
        int bestCount = 0;
        float bestDist = float.MaxValue;
        Vector2 bestAim = c;
        foreach (NPC n in exposed)
        {
            float d = Vector2.Distance(n.Center, c);
            if (d > (gunner.ArrowDrop ? 720f : 1000f)) continue;
            Vector2 aim = gunner.Lead(c, n.Center, n.position - n.oldPosition);
            Vector2 dir = aim - c;
            if (dir.LengthSquared() < 1f) continue;
            dir.Normalize();
            int count = 0;
            foreach (NPC k in exposed)
            {
                Vector2 r = k.Center - c;
                float along = Vector2.Dot(r, dir);
                if (along < 0f || along > 760f) continue;
                if (Math.Abs(r.X * dir.Y - r.Y * dir.X) < 24f) count++;
            }
            if (count > bestCount || (count == bestCount && d < bestDist))
            {
                if (!Collision.CanHitLine(c, 1, 1, n.Center, 1, 1)) continue;
                bestCount = count; bestDist = d; bestT = n; bestAim = aim;
            }
        }
        lastCount = bestCount;
        gunner.Fire(p, bestAim, bestT != null);
    }

    /// <summary>
    /// Grenade target: a body segment whose 128 px blast box holds the most exposed segments.
    /// Body segments trail the head's path, so a point on the chain stays occupied until the
    /// tail passes; it only needs enough segments behind it to cover the flight time.
    /// </summary>
    private bool TryGrenade(Player p, out Vector2 dirPoint)
    {
        dirPoint = p.Center;
        Vector2 c = p.Center;
        foreach (NPC n in Main.npc)
            if (n.active && n.type is NPCID.EaterofWorldsHead or NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail
                && Vector2.Distance(n.Center, c) < 170f) return false;
        int bestScore = 1;
        Vector2 bestDir = Vector2.Zero;
        foreach (NPC n in exposed)
        {
            if (n.type == NPCID.EaterofWorldsHead) continue;
            float d = Vector2.Distance(n.Center, c);
            if (d < 180f || d > 640f || n.Center.Y < c.Y + 60f) continue;
            int score = 0;
            foreach (NPC k in exposed)
                if (Math.Abs(k.Center.X - n.Center.X) < 60f && Math.Abs(k.Center.Y - n.Center.Y) < 60f) score++;
            if (score <= bestScore) continue;
            if (!Grenades.Solve(c, n.Center, p.HeldItem.type == ItemID.Grenade ? p.HeldItem.shootSpeed : 5.5f, 14f, out Vector2 dir, out int ticks)) continue;
            // Segments behind this one must still be flowing through the point on arrival.
            float speed = Math.Max(2f, (n.position - n.oldPosition).Length());
            int behind = 0;
            for (int k = (int)n.ai[0], guard = 0; k > 0 && k < Main.maxNPCs && guard < 80 && Main.npc[k].active; k = (int)Main.npc[k].ai[0], guard++)
            {
                behind++;
                if (Main.npc[k].type == NPCID.EaterofWorldsTail) break;
            }
            if (behind * 38f < speed * ticks + 40f) continue;
            if (!Collision.CanHitLine(c, 1, 1, n.Center, 1, 1)) continue;
            bestScore = score;
            bestDir = dir;
        }
        if (bestDir == Vector2.Zero) return false;
        dirPoint = c + bestDir * 160f;
        return true;
    }
}
