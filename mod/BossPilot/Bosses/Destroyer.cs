using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.Mechs;

namespace BossPilot.Bosses;

/// <summary>
/// The Destroyer. Loadout: MechKit (Daedalus Stormbow); Jester's Arrows go first in the
/// ammo slots because they pierce every segment they fall through, and the 80 body
/// segments share the head's life (realLife).
///
/// Kernel facts (AI_037): the head steers (speed 16, per-axis accel 0.1/0.15) only while
/// its hitbox overlaps solid tiles, platform tops or liquid, or while it is below the pilot
/// and outside the pilot's ±1000 px box; otherwise it falls with vy += 0.15 (cap 16). So a
/// head leaving the ground can climb at most 16²/0.3 ≈ 850 px of open air. Body segments
/// fire 8 px/tick death lasers (x3 updates, ±20 px aim noise) at random while they have
/// line of sight; hit segments release Probes (1 in 25, once per segment).
///
/// Arena: the classic sky bridge. Surface arenas get one long platform row 80 tiles above
/// the floor (out of reach of a head that left the ground; the pilot is placed on it when
/// the Mechanical Worm is used), the space arena's 10-tile stone floor becomes a platform
/// row (stone would let the head steer right under the pilot), and the enclosed box gets
/// one platform row 6 tiles under its ceiling; in there the Adamantite Repeater fires
/// Jester's Arrows straight at the body, since the ceiling stops Daedalus arrows, and the
/// pilot holds the end of the row away from the head. Spawned 80 tiles below the pilot,
/// like an off-screen summon: below and outside the 1000 px box it homes upward.
///
/// Control: the WormPredictor rolls the head forward with AI_037's own rules and slides
/// the body along its trail; the MPC planner dodges that chain, Probes, their scheduled
/// lasers and death lasers. The goal keeps the pilot on the bridge, moving, and far from
/// the body; arrows are led onto the nearest segment, or onto a nearby Probe.
/// </summary>
public sealed class Destroyer : MechPlan
{
    public override string Boss => "destroyer";

    private const int BridgeHeight = 80;
    // The enclosed box's stand row: 6 tiles under its ceiling (best of rows at 24/34/44 and
    // three rows at 12/24/36; standing on the floor was worst - the head homes underground).
    private const int EncHigh = 44;
    private readonly WormPredictor worm = new();
    private float standY;      // world Y of the surface the pilot fights on
    private float spanL, spanR;
    private int nearCount;
    private readonly Vector2[] near = new Vector2[256];

    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        base.GrantLoadout(p, s);
        if (s.Loadout != "min_preboss")
        {
            MechKit.Put(p, 54, ItemID.JestersArrow, 999);
            MechKit.Put(p, 55, ItemID.HolyArrow, 999);
            // Same Adamantite bars as the armor: direct fire for the enclosed box, whose ceiling
            // stops Daedalus arrows (they spawn 600 px above the pilot).
            MechKit.Put(p, 1, ItemID.AdamantiteRepeater);
        }
    }

    private static bool Bridge(ScenarioConfig s) => s.Arena == "flat_platforms" || s.Arena == "open_sky" && s.Biome != "space";

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        base.DecorateArena(site, s);
        if (Bridge(s))
            Platforms(site.X - 75, site.X + 75, site.Y - BridgeHeight);
        else if (s.Arena == "open_sky")
        {
            for (int x = site.X - 80; x < site.X + 80; x++)
            for (int y = site.Y; y <= site.Y + 10; y++)
                if (WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile)
                    WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
            Platforms(site.X - 80, site.X + 80, site.Y);
        }
        if (s.Arena == "enclosed") Platforms(site.X - 48, site.X + 48, site.Y - EncHigh);
        int stand = Bridge(s) ? site.Y - BridgeHeight : s.Arena == "enclosed" ? site.Y - EncHigh : site.Y;
        MechKit.PeaceCandles(site.X, stand);
    }

    private static void Platforms(int x0, int x1, int y)
    {
        for (int x = x0; x < x1; x++)
        {
            if (!WorldGen.InWorld(x, y, 2)) continue;
            WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
            WorldGen.PlaceTile(x, y, TileID.Platforms, mute: true, forced: true, style: 0);
        }
    }

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        Point site = TrialRunner.Instance.Context.Site;
        int half = s.Arena == "enclosed" ? 48 : 75;
        spanL = (site.X - half) * 16f; spanR = (site.X + half) * 16f;
        standY = site.Y * 16f;
        if (Bridge(s))
        {
            // Walk up before using the Mechanical Worm: stand on the bridge.
            standY = (site.Y - BridgeHeight) * 16f;
            p.position = new Vector2(site.X * 16f - p.width / 2f, standY - p.height);
            p.velocity = Vector2.Zero;
            p.fallStart = (int)(p.position.Y / 16f);
            p.wingTime = p.wingTimeMax;
        }
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), site.X * 16, site.Y * 16 + 80 * 16, NPCID.TheDestroyer);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        this.ctx = ctx;
        RefreshBiomes(p, ctx);
        Threats th = planner.Threats;
        th.Clear();
        nearCount = 0;
        if (worm.Build(p))
        {
            worm.AddThreats(th, p, 1400f);
            for (int j = 0; j <= worm.Segments && nearCount < near.Length; j += 2)
            {
                Vector2 q = worm.SegmentAt(j, 0);
                if (Vector2.DistanceSquared(q, p.Center) < 700f * 700f) near[nearCount++] = q;
            }
        }
        CollectThreats(p, n => n.type >= NPCID.TheDestroyer && n.type <= NPCID.TheDestroyerTail);
        foreach (NPC n in Main.npc)
            if (n.active && n.type == NPCID.Probe && Vector2.DistanceSquared(n.Center, p.Center) < 900f * 900f)
                ShotSchedule.Probe(n, th);
        float patrol = PatrolTarget(p, 320f);
        // In the box, hold the end of the high row away from the head: a head leaving the floor
        // cannot arc that far up and sideways, and fewer segments have line of sight there.
        if (ctx.Scenario.Arena == "enclosed" && worm.Head >= 0)
            patrol = Main.npc[worm.Head].Center.X > (spanL + spanR) / 2f ? spanL + 96f : spanR - 96f;
        planner.Goal = (s, t) => Goal(s, t, patrol);
        planner.Drive(p);
        NPC target = Intruder(p) ?? PickTarget(p);
        if (ctx.Scenario.Arena == "enclosed" && p.inventory[1].type == ItemID.AdamantiteRepeater)
        {
            p.selectedItem = 1;
            if (target == null) p.controlUseItem = false;
            else
            {
                FightContext.Aim(Aiming.DirectLead(p, target.Center, target.position - target.oldPosition, 27.7f));
                p.controlUseItem = true;
            }
        }
        else
        {
            p.selectedItem = 0;
            Shoot(p, target);
        }
        Finish(p);
        return true;
    }

    private float Goal(PState s, int t, float patrol)
    {
        float cost = 0f;
        // Stay over the bridge / box: soft walls 4 tiles in from the ends.
        if (s.X < spanL + 64f) cost += (spanL + 64f - s.X) / 6f;
        if (s.X > spanR - 64f) cost += (s.X - spanR + 64f) / 6f;
        float alt = (standY - s.Y - PilotModel.HalfH) / 16f;
        bool box = ctx.Scenario.Arena == "enclosed";
        float lo = box ? EncHigh - 0.5f : -0.5f, hi = box ? EncHigh + 4f : 8f;
        // Below the stand (bridge or the box's high row) is where the body and its lasers are.
        if (alt < lo) cost += (lo - alt) * (box ? 0.8f : 3f);
        if (alt > hi) cost += (alt - hi) * 0.8f;
        if (!s.Ground && s.Wing < 30f) cost += (30f - s.Wing) * 0.05f * Math.Max(0f, alt - lo);
        // Keep running along the bridge so aimed lasers trail behind.
        cost += Math.Abs(s.X - patrol) / 96f;
        // Away from the head (it only turns hard inside tiles) and from the body (lasers).
        if (worm.Head >= 0)
        {
            Vector2 me = new(s.X, s.Y);
            float d = Vector2.Distance(worm.HeadAt(t), me);
            if (d < 520f) cost += (520f - d) / 16f;
            if ((t & 7) == 0)
                for (int j = 0; j < nearCount; j++)
                {
                    float e = Vector2.Distance(near[j], me);
                    if (e < 320f) cost += (320f - e) / 160f;
                }
        }
        return cost;
    }

    private static NPC PickTarget(Player p)
    {
        NPC target = null;
        float best = float.MaxValue;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.dontTakeDamage) continue;
            bool part = n.type >= NPCID.TheDestroyer && n.type <= NPCID.TheDestroyerTail;
            bool probe = n.type == NPCID.Probe;
            if (!part && !probe) continue;
            float d = Vector2.Distance(n.Center, p.Center);
            if (probe) d -= 300f;                            // probes shoot; clear the close ones first
            if (n.Center.Y < p.Center.Y - 560f) d += 2000f;  // above the Daedalus spawn line
            if (d < best) { best = d; target = n; }
        }
        return target;
    }
}
