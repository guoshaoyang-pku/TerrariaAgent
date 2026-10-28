using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Base for the three mech plans: shared loadout, threat collection, the MPC planner,
/// patrol/arena goal terms and potion use. Subclasses add boss-specific predictors,
/// goal terms and targeting.
/// </summary>
public abstract class MechPlan : BossPlan
{
    protected readonly Planner planner = new();
    protected FightContext ctx;
    protected int patrolDir = 1;

    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        MechKit.Grant(p, s);
        // Ammo experiment switch: MECHS_AMMO=holy puts Holy Arrows first.
        if (Environment.GetEnvironmentVariable("MECHS_AMMO") == "holy" && s.Loadout != "min_preboss")
            MechKit.Put(p, 54, ItemID.HolyArrow, 999);
        planner.Reset();
        patrolDir = 1;
    }

    /// <summary>
    /// Surface arenas sit on the world spawn row. ArenaSites.Default uses SurfaceAt, which scans
    /// from the top of the world and, in a worker's reused world, finds the stone slab an
    /// earlier space arena left at worldSurface/4 (or an enclosed arena's ceiling), so the
    /// "surface" arena would be rebuilt in the sky among wyverns.
    /// </summary>
    public override Point ArenaSite(ScenarioConfig s) =>
        s.Biome == "space" ? ArenaSites.Default(s.Biome) : new Point(Main.spawnTileX, Main.spawnTileY);

    /// <summary>
    /// Undoes what earlier scenarios on this worker left behind: floating remnants of an
    /// enclosed arena's ceiling above surface arenas, another biome's painted floor slab
    /// under a forest arena, and a running sandstorm.
    /// </summary>
    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        // Above this arena's own structure only (the enclosed box's ceiling is at 50 rows).
        int top = s.Arena switch { "enclosed" => 51, "open_sky" => 81, _ => 41 };
        if (s.Biome != "space")
            for (int x = site.X - 85; x <= site.X + 85; x++)
            for (int y = site.Y - 95; y < site.Y - top; y++)
                if (WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile)
                    WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
        if (s.Biome == "forest")
            for (int x = site.X - 60; x < site.X + 60; x++)
            for (int y = site.Y; y < site.Y + 16; y++)
            {
                if (!WorldGen.InWorld(x, y, 2)) continue;
                Tile t = Main.tile[x, y];
                if (t.HasTile && t.TileType is TileID.Sandstone or TileID.Ebonstone or TileID.Crimstone or TileID.Pearlstone
                        or TileID.SnowBlock or TileID.JungleGrass or TileID.Ash)
                    t.ResetToType(TileID.Stone);
            }
        if (s.Weather != "sandstorm" && Terraria.GameContent.Events.Sandstorm.Happening)
            Terraria.GameContent.Events.Sandstorm.StopSandstorm();
        MechKit.PeaceCandles(site.X, site.Y);
    }

    /// <summary>
    /// A headless server only rescans SceneMetrics (biome tile counts, candles) when asked;
    /// the desert sandstorm and the Peace Candles depend on it.
    /// </summary>
    protected static void RefreshBiomes(Player p, FightContext ctx)
    {
        if (ctx.Tick % 30 == 1) p.ForceUpdateBiomes();
    }

    /// <summary>Adds hostile NPCs (except those the predicate claims) and projectiles near the pilot.</summary>
    protected void CollectThreats(Player p, Func<NPC, bool> handled = null, float npcGrow = 0.6f, float accCap = 0.4f)
    {
        Threats th = planner.Threats;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.damage <= 0 || n.lifeMax <= 5) continue;
            if (Vector2.DistanceSquared(n.Center, p.Center) > 1500f * 1500f) continue;
            if (handled != null && handled(n)) continue;
            th.AddNpc(n, grow: npcGrow, accCap: accCap);
        }
        foreach (Projectile pr in Main.projectile)
            if (pr.active && pr.hostile && Vector2.DistanceSquared(pr.Center, p.Center) < 1500f * 1500f)
                th.AddProjectile(pr);
    }

    /// <summary>Back-and-forth patrol target inside the arena; flips near either end.</summary>
    protected float PatrolTarget(Player p, float inset)
    {
        (float left, float right) = MechKit.ArenaSpan(ctx);
        if (p.Center.X > right - inset) patrolDir = -1;
        else if (p.Center.X < left + inset) patrolDir = 1;
        return patrolDir > 0 ? right - inset * 0.5f : left + inset * 0.5f;
    }

    /// <summary>Soft walls: cost for leaving [left+inset, right-inset] and for altitude outside [lo, hi] tiles.</summary>
    protected float ArenaCost(in PState s, float inset, float lo, float hi)
    {
        (float left, float right) = MechKit.ArenaSpan(ctx);
        float cost = 0f;
        if (s.X < left + inset) cost += (left + inset - s.X) / 3f;
        if (s.X > right - inset) cost += (s.X - right + inset) / 3f;
        float alt = (MechKit.FloorY(ctx) - s.Y) / 16f;
        if (alt < lo) cost += (lo - alt) * 2f;
        if (alt > hi) cost += (alt - hi) * 2f;
        if (!s.Ground && s.Wing < 30f) cost += (30f - s.Wing) * 0.04f * Math.Max(0f, alt - 3f);
        // The world's top edge (~41 tiles) pins the pilot: no room to dodge upward there.
        if (s.Y < 60 * 16f) cost += (60 * 16f - s.Y) / 4f;
        return cost;
    }

    private readonly Vector2[] aimDisp = new Vector2[Main.maxNPCs];
    private readonly uint[] aimSeen = new uint[Main.maxNPCs];

    /// <summary>Holds use and leads the Daedalus volley onto the target (velocity + one-tick acceleration).</summary>
    protected void Shoot(Player p, NPC target)
    {
        if (target == null) { p.controlUseItem = false; return; }
        Vector2 v = target.position - target.oldPosition;
        int i = target.whoAmI;
        Vector2 a = aimSeen[i] + 1 == Main.GameUpdateCount ? v - aimDisp[i] : Vector2.Zero;
        aimDisp[i] = v; aimSeen[i] = Main.GameUpdateCount;
        if (a.LengthSquared() > 0.25f) a = Vector2.Normalize(a) * 0.5f;
        FightContext.Aim(Aiming.DaedalusLead(p, target.Center, v, a, Aiming.ArrowSpeed(p)));
        p.controlUseItem = true;
    }

    /// <summary>A wyvern or harpy closing in on the pilot, shot before it reaches the arena.</summary>
    protected static NPC Intruder(Player p)
    {
        foreach (NPC n in Main.npc)
            if (n.active && (n.type == NPCID.WyvernHead || n.type == NPCID.Harpy) && n.Center.Y > p.Center.Y - 560f
                && Vector2.DistanceSquared(n.Center, p.Center) < 640f * 640f)
                return n;
        return null;
    }

    protected static NPC Find(int type)
    {
        foreach (NPC n in Main.npc)
            if (n.active && n.type == type) return n;
        return null;
    }

    protected void Finish(Player p) => MechKit.MaybeHeal(p);

    public float LastCost => planner.BestCost;
    public int LastFirstHit => planner.BestFirstHit;
    public string HitHistory => planner.HitHistory();
    public string FreeHistory => planner.FreeHistory();
    public string TagHistory => planner.TagHistory();
}
